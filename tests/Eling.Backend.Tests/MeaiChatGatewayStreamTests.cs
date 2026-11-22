using Eling.Backend.Agent.Infrastructure.Ai;
using Eling.Backend.Agent.Ports;
using Eling.Backend.Agent.Services;
using Eling.Backend.FileSystem;
using Eling.Core.Codebase;
using Eling.Core.FileSystem;
using Eling.Core.Scope;
using Eling.Core.Memory;
using Eling.Core.Memory.Serialization;
using Eling.Core.Recall;
using Eling.Core.Projects;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Eling.Backend.Tests;

/// <summary>
/// Guards the reasoning-model trap: the provider thinks for longer than the
/// streaming first-byte budget, so the first streaming attempt stalls. The turn
/// must fall back to single-shot for that turn alone, and the next turn must still
/// attempt streaming — an earlier process-wide kill-switch meant one slow first
/// byte disabled streaming for the backend's entire lifetime.
/// </summary>
public class MeaiChatGatewayStreamTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _workspace;
    private readonly List<WebApplication> _stubs = [];

    public MeaiChatGatewayStreamTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "eling-gwstream-" + Guid.NewGuid().ToString("N")[..8]);
        _workspace = Path.Combine(_tempDir, "ws");
        Directory.CreateDirectory(_workspace);
    }

    [Fact]
    public async Task FirstByteTimeout_FallsBackForThatTurn_AndNextTurnStillStreams()
    {
        var (port, state) = await StartStubProviderAsync();

        var dataDir = Path.Combine(_tempDir, "data");
        Directory.CreateDirectory(dataDir);
        var provider = new ProviderStore(dataDir, NullLogger<ProviderStore>.Instance);
        provider.Update($"http://127.0.0.1:{port}", "m1", "test-key");
        var chats = new BackendChatStore(Path.Combine(dataDir, "chats"), NullLogger<BackendChatStore>.Instance);

        var registry = new JsonWorkspaceRegistry(Path.Combine(dataDir, "ws.json"), NullLogger<JsonWorkspaceRegistry>.Instance);
        Attach(registry, _workspace);

        var files = new BackendFileTools(NullLogger<BackendFileTools>.Instance);
        var memory = new StubMemoryService();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IFileSystemService>(new FileSystemService(_workspace));
        services.AddScoped<IMemoryService>(_ => memory);
        services.AddScoped<IScopedMemoryService>(_ => new ScopedMemoryService(memory, memory, new MemoryScopePolicy(), new MemoryMerger(), _workspace));
        services.AddScoped<IIntentionStorage>(_ => new FileSystemIntentionStorage(_workspace));
        services.AddScoped<IRecallService, RecallService>();
        using var sp = services.BuildServiceProvider();
        var recall = sp.GetRequiredService<IRecallService>();
        var codebase = new CodebaseIndexService(_workspace, new SqliteCodebaseIndex(Eling.Core.Scope.ElingPaths.ResolveCodebaseDbPath(_workspace)));

        var gateway = new MeaiChatGateway(provider, NullLogger<MeaiChatGateway>.Instance)
        {
            // Short budget so the stalled first streaming call times out fast.
            StreamFirstByteTimeout = TimeSpan.FromMilliseconds(1500)
        };
        var turns = new AgentTurnService(gateway, Array.Empty<IAgentTool>(), chats, provider, recall, codebase, NullLogger<AgentTurnService>.Instance);

        // Turn 1: the stub stalls the first streaming call, the gateway times out,
        // and the turn must complete through the single-shot fallback.
        var first = await turns.RunTurnAsync(_workspace, null, "hello", CancellationToken.None, _ => Task.CompletedTask);
        Assert.Equal("fallback-ok", first.Assistant);

        // Turn 2: the stub answers SSE promptly. Streaming must be attempted again,
        // not skipped by a process-wide marker from turn 1.
        var events2 = new List<TurnStreamEvent>();
        var second = await turns.RunTurnAsync(_workspace, null, "hello again", CancellationToken.None, ev =>
        {
            events2.Add(ev);
            return Task.CompletedTask;
        });

        Assert.Equal("live-ok", second.Assistant);
        Assert.Contains(events2, e => e is TurnTextDelta d && d.Delta.Contains("live-ok"));
        Assert.Equal(2, state.StreamRequests);
    }

    private async Task<(int Port, StubState State)> StartStubProviderAsync()
    {
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls($"http://127.0.0.1:{port}");
        var stub = builder.Build();
        var state = new StubState();

        stub.MapPost("/chat/completions", async (HttpContext ctx) =>
        {
            var body = await new StreamReader(ctx.Request.Body).ReadToEndAsync(ctx.RequestAborted);
            var wantsStream = body.Contains("\"stream\":true") || body.Contains("\"stream\": true");
            if (wantsStream)
            {
                var n = Interlocked.Increment(ref state.StreamRequests);
                if (n == 1)
                {
                    // Stall the first streaming call well past the gateway's
                    // first-byte timeout; the aborted request releases this via ct.
                    await Task.Delay(TimeSpan.FromSeconds(30), ctx.RequestAborted);
                    return;
                }

                ctx.Response.ContentType = "text/event-stream";
                await ctx.Response.WriteAsync("data: {\"id\":\"chatcmpl-test\",\"object\":\"chat.completion.chunk\",\"created\":1795100312,\"model\":\"m1\",\"choices\":[{\"index\":0,\"delta\":{\"role\":\"assistant\",\"content\":\"live-ok\"},\"finish_reason\":null}]}\n\n", ctx.RequestAborted);
                await ctx.Response.WriteAsync("data: {\"id\":\"chatcmpl-test\",\"object\":\"chat.completion.chunk\",\"created\":1795100312,\"model\":\"m1\",\"choices\":[{\"index\":0,\"delta\":{},\"finish_reason\":\"stop\"}]}\n\n", ctx.RequestAborted);
                await ctx.Response.WriteAsync("data: [DONE]\n\n", ctx.RequestAborted);
                return;
            }

            ctx.Response.ContentType = "application/json";
            await ctx.Response.WriteAsJsonAsync(new
            {
                choices = new[]
                {
                    new { index = 0, message = new { role = "assistant", content = "fallback-ok" }, finish_reason = "stop" }
                }
            }, ctx.RequestAborted);
        });

        await stub.StartAsync();
        _stubs.Add(stub);
        return (port, state);
    }

    private sealed class StubState
    {
        public int StreamRequests;
    }

    private static void Attach(IWorkspacesRegistry registry, string root)
    {
        var now = DateTimeOffset.UtcNow;
        registry.Record(new RegisteredWorkspace(
            WorkspaceId: string.Empty,
            WorkspaceRoot: root,
            HeadScopeRoot: root,
            CodebaseEnabled: false,
            FirstSeenAt: now,
            LastSeenAt: now));
    }

    private sealed class StubMemoryService : IMemoryService
    {
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, Memory> _items = new();

        public Task<MemorySaveResult> SaveAsync(Memory memory)
        {
            _items[memory.Id.Value] = memory;
            return Task.FromResult(new MemorySaveResult(memory, SaveAction.Created));
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

    public void Dispose()
    {
        foreach (var stub in _stubs)
        {
            stub.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }

        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, recursive: true);
            }
        }
        catch (Exception ex)
        {
            TestLogs.Log("test cleanup: delete temp dir failed", ex);
        }
    }
}
