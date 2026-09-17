using System.Collections.Concurrent;
using System.Net.Http.Json;
using Eling.Backend.Agent.Ports;
using Eling.Backend.Agent.Services;
using Eling.Backend.Bootstrap;
using Eling.Backend.FileSystem;
using Eling.Core.FileSystem;
using Eling.Core.Memory;
using Eling.Core.Memory.Serialization;
using Eling.Core.MemoryRecall;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Eling.Backend.Tests;

public class AgentChatTests : IAsyncLifetime, IDisposable
{
    private readonly string _tempDir;
    private readonly string _workspace;
    private readonly List<WebApplication> _stubs = [];

    public AgentChatTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "eling-chat-" + Guid.NewGuid().ToString("N")[..8]);
        _workspace = Path.Combine(_tempDir, "ws");
    }

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(_workspace);
        return Task.CompletedTask;
    }

    [Fact]
    public async Task TurnWithReadFile_ReturnsFileContentAndPersists()
    {
        File.WriteAllText(Path.Combine(_workspace, "hello.txt"), "hello-agent");
        var harness = CreateHarness();
        harness.Registry.Add(_workspace);

        harness.Gateway.Enqueue(new SingleShotResult(null, [new ToolCallRequest("t1", "file_read", """{"path":"hello.txt"}""")]));
        harness.Gateway.Enqueue(new SingleShotResult("file says hello-agent", []));

        var first = await harness.Turns.SendAsync(_workspace, null, "read hello", CancellationToken.None);
        Assert.Contains("hello-agent", first.Assistant);

        var second = await harness.Turns.SendAsync(_workspace, first.ChatId, "again", CancellationToken.None);
        var history = harness.Chats.Get(second.ChatId);
        Assert.NotNull(history);
        Assert.True(history.Messages.Count >= 6);
    }

    [Fact]
    public async Task HopCap_StopsAfterThreeExecutions()
    {
        var harness = CreateHarness();
        harness.Registry.Add(_workspace);
        for (var i = 0; i < 5; i++)
        {
            harness.Gateway.Enqueue(new SingleShotResult(null, [new ToolCallRequest($"t{i}", "directory_list", """{"path":""}""")]));
        }

        var turn = await harness.Turns.SendAsync(_workspace, null, "list", CancellationToken.None);
        Assert.Equal(3, turn.ToolCalls.Count);
        Assert.Contains("3 tool hops", turn.Assistant);
    }

    [Fact]
    public async Task Http_ChatRoundtrip_PersistsHistory()
    {
        var stubPort = await StartStubAsync("""{"choices":[{"message":{"role":"assistant","content":"stub-hi"}}]}""");

        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();

        using var app = TestAppBuilder.CreateSelfContained(dashboardPort: port);
        await app.StartAsync();
        using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };

        var attach = await client.PostAsJsonAsync("/api/agent/workspaces/", new { path = _workspace });
        attach.EnsureSuccessStatusCode();
        var provider = await client.PutAsJsonAsync("/api/agent/provider/", new
        {
            baseUrl = $"http://127.0.0.1:{stubPort}",
            model = "stub-model",
            apiKey = ""
        });
        provider.EnsureSuccessStatusCode();

        var send = await client.PostAsJsonAsync("/api/agent/chats", new
        {
            workspace = _workspace,
            message = "hello",
            chatId = (string?)null
        });
        send.EnsureSuccessStatusCode();
        var body = await send.Content.ReadAsStringAsync();
        Assert.Contains("stub-hi", body);

        var list = await client.GetAsync($"/api/agent/chats/?workspace={Uri.EscapeDataString(_workspace)}");
        list.EnsureSuccessStatusCode();
        var listBody = await list.Content.ReadAsStringAsync();
        Assert.Contains("hello", listBody);
    }

    private Harness CreateHarness()
    {
        var dataDir = Path.Combine(_tempDir, "data-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dataDir);

        var registry = new WorkspaceRegistry(Path.Combine(dataDir, "ws.json"), NullLogger<WorkspaceRegistry>.Instance);
        var files = new BackendFileTools(NullLogger<BackendFileTools>.Instance);
        var memory = new StubMemoryService();
        var provider = new ProviderStore(dataDir, NullLogger<ProviderStore>.Instance);
        var chats = new BackendChatStore(Path.Combine(dataDir, "chats"), NullLogger<BackendChatStore>.Instance);
        var gateway = new FakeGateway();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IFileSystemService>(new FileSystemService(_workspace));
        services.AddScoped<IMemoryService>(_ => memory);
        services.AddScoped<IScopedMemoryService>(_ => new ScopedMemoryService(memory, memory, new MemoryScopePolicy(), new MemoryMerger(), _workspace));
        services.AddScoped<IIntentionStorage>(_ => new FileSystemIntentionStorage(_workspace));
        services.AddScoped<IMemoryRecallService, MemoryRecallService>();
        var sp = services.BuildServiceProvider();

        var tools = Array.Empty<IAgentTool>();
        var turns = new AgentTurnService(gateway, tools, chats, registry, provider, NullLogger<AgentTurnService>.Instance);
        return new Harness(registry, chats, gateway, turns);
    }

    private async Task<int> StartStubAsync(string responseJson)
    {
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls($"http://127.0.0.1:{port}");
        var stub = builder.Build();
        stub.MapPost("/chat/completions", () => Results.Content(responseJson, "application/json"));
        await stub.StartAsync();
        _stubs.Add(stub);
        return port;
    }

    public Task DisposeAsync() => Task.CompletedTask;

    public void Dispose()
    {
        foreach (var stub in _stubs)
        {
            try
            {
                stub.StopAsync().GetAwaiter().GetResult();
            }
            catch
            {
            }
        }

        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, recursive: true);
            }
        }
        catch
        {
        }
    }

    private sealed record Harness(WorkspaceRegistry Registry, BackendChatStore Chats, FakeGateway Gateway, AgentTurnService Turns);

    private sealed class FakeGateway : IChatGateway
    {
        private readonly Queue<SingleShotResult> _script = [];

        public void Enqueue(SingleShotResult result) => _script.Enqueue(result);

        public Task<SingleShotResult> CompleteAsync(SingleShotRequest request, CancellationToken ct)
        {
            if (_script.Count == 0)
            {
                return Task.FromResult(new SingleShotResult("done", []));
            }

            return Task.FromResult(_script.Dequeue());
        }

        public IAsyncEnumerable<ChatStreamEvent> CompleteStreamingAsync(SingleShotRequest request, CancellationToken ct) =>
            throw new NotSupportedException("Use FakeStreamingGateway for streaming tests.");
    }

    private sealed class StubMemoryService : IMemoryService
    {
        private readonly ConcurrentDictionary<string, Memory> _items = new();

        public Task<SaveResult> SaveAsync(Memory memory)
        {
            _items[memory.Id.Value] = memory;
            return Task.FromResult(new SaveResult(memory, SaveAction.Created));
        }

        public Task<Memory?> GetByIdAsync(MemoryId id) =>
            Task.FromResult(_items.TryGetValue(id.Value, out var memory) ? memory : null);

        public Task<Memory?> UpdateAsync(MemoryId id, string? content = null, MemoryType? type = null, string[]? tags = null, string? source = null, MemoryStatus? status = null) =>
            Task.FromResult<Memory?>(null);

        public Task<bool> DeleteAsync(MemoryId id) => Task.FromResult(_items.TryRemove(id.Value, out _));

        public Task<IReadOnlyCollection<Memory>> ListAllAsync() =>
            Task.FromResult<IReadOnlyCollection<Memory>>(_items.Values.ToList());

        public Task<IReadOnlyCollection<MemorySearchResult>> SearchAsync(string query) =>
            Task.FromResult<IReadOnlyCollection<MemorySearchResult>>(
                _items.Values.Select(m => new MemorySearchResult(m.Id, 1.0)).ToList());

        public Task RebuildIndexAsync() => Task.CompletedTask;
    }
}
