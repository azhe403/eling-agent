using Eling.Backend.Agent.Ports;
using Eling.Backend.Agent.Services;
using Eling.Backend.FileSystem;
using Eling.Core.FileSystem;
using Eling.Core.Memory;
using Eling.Core.Memory.Serialization;
using Eling.Core.MemoryRecall;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Eling.Backend.Tests;

public class AgentChatStreamTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _workspace;

    public AgentChatStreamTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "eling-stream-" + Guid.NewGuid().ToString("N")[..8]);
        _workspace = Path.Combine(_tempDir, "ws");
        Directory.CreateDirectory(_workspace);
    }

    [Fact]
    public async Task StreamedTurn_EmitsStartedDeltaToolDone_InOrder()
    {
        File.WriteAllText(Path.Combine(_workspace, "a.txt"), "abc");
        var harness = CreateHarness();
        harness.Registry.Add(_workspace);
        harness.Gateway.Enqueue(
            [new ChatTextDelta("he"), new ChatTextDelta("llo"), new ChatToolRequest(new ToolCallRequest("t1", "file_read", """{"path":"a.txt"}"""))]);
        harness.Gateway.Enqueue([new ChatTextDelta("says abc")]);

        var events = new List<TurnStreamEvent>();
        var response = await harness.Turns.RunTurnAsync(_workspace, null, "read a", CancellationToken.None, ev =>
        {
            events.Add(ev);
            return Task.CompletedTask;
        });

        Assert.Equal("says abc", response.Assistant);
        var kinds = events.Select(e => e switch
        {
            TurnStarted => "started",
            TurnTextDelta => "delta",
            TurnToolCall => "tool",
            TurnDone => "done",
            _ => "other"
        }).ToList();
        Assert.Equal(["started", "delta", "delta", "tool", "delta", "done"], kinds);
        var tool = events.OfType<TurnToolCall>().Single();
        Assert.Equal("file_read", tool.Call.Name);
    }

    [Fact]
    public async Task StreamedTurn_StopsAfterThreeHops()
    {
        var harness = CreateHarness();
        harness.Registry.Add(_workspace);
        for (var i = 0; i < 5; i++)
        {
            harness.Gateway.Enqueue([new ChatToolRequest(new ToolCallRequest($"t{i}", "directory_list", """{"path":""}"""))]);
        }

        var toolEvents = 0;
        var response = await harness.Turns.RunTurnAsync(_workspace, null, "list", CancellationToken.None, ev =>
        {
            if (ev is TurnToolCall) toolEvents++;
            return Task.CompletedTask;
        });

        Assert.Equal(3, toolEvents);
        Assert.Equal(3, response.ToolCalls.Count);
        Assert.Contains("3 tool hops", response.Assistant);
    }

    [Fact]
    public async Task StreamedTurn_FallsBackToSingleShot_WhenStreamingThrows()
    {
        var harness = CreateHarness();
        harness.Registry.Add(_workspace);
        harness.Gateway.ThrowOnStream = true;
        harness.Gateway.NonStreamResult = new SingleShotResult("fallback-hi", []);

        var events = new List<TurnStreamEvent>();
        var response = await harness.Turns.RunTurnAsync(_workspace, null, "hello", CancellationToken.None, ev =>
        {
            events.Add(ev);
            return Task.CompletedTask;
        });

        Assert.Equal("fallback-hi", response.Assistant);
        Assert.Contains(events, e => e is TurnTextDelta d && d.Delta == "fallback-hi");
        Assert.Contains(events, e => e is TurnDone);
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
        var gateway = new FakeStreamingGateway();
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
        return new Harness(registry, turns, gateway);
    }

    public void Dispose()
    {
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

    private sealed record Harness(WorkspaceRegistry Registry, AgentTurnService Turns, FakeStreamingGateway Gateway);

    private sealed class FakeStreamingGateway : IChatGateway
    {
        private readonly Queue<IReadOnlyList<ChatStreamEvent>> _script = [];

        public bool ThrowOnStream { get; set; }

        public SingleShotResult NonStreamResult { get; set; } = new SingleShotResult("done", []);

        public void Enqueue(IReadOnlyList<ChatStreamEvent> events) => _script.Enqueue(events);

        public Task<SingleShotResult> CompleteAsync(SingleShotRequest request, CancellationToken ct) =>
            Task.FromResult(NonStreamResult);

        public async IAsyncEnumerable<ChatStreamEvent> CompleteStreamingAsync(
            SingleShotRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            if (ThrowOnStream)
            {
                await Task.Yield();
                throw new System.IO.IOException("simulated broken stream");
            }

            var events = _script.Count == 0
                ? (IReadOnlyList<ChatStreamEvent>)[new ChatTextDelta("done")]
                : _script.Dequeue();
            foreach (var ev in events)
            {
                yield return ev;
                await Task.Yield();
            }
        }
    }

    private sealed class StubMemoryService : IMemoryService
    {
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, Memory> _items = new();

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
