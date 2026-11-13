using System.Text.Json;
using Eling.Backend.Mcp.Tools;
using Eling.Core.CommitLease;

namespace Eling.Backend.Tests;

public sealed class CommitReleaseToolTests : IDisposable
{
    private readonly string _root;
    private readonly ManualTimeProvider _clock;

    public CommitReleaseToolTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "eling-commit-release-tool-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_root);
        _clock = new ManualTimeProvider(new DateTimeOffset(2026, 11, 12, 12, 0, 0, TimeSpan.Zero));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private string Checkout(string name)
    {
        var dir = Path.Combine(_root, name);
        Directory.CreateDirectory(dir);
        return dir;
    }

    private JsonCommitLeaseStore NewStore()
        => new(Path.Combine(_root, "commit-leases.json"), notifier: null, timeProvider: _clock);

    private CommitLeaseTool NewLeaseTool(JsonCommitLeaseStore store, string cwd)
        => new(store, _clock, cwd);

    private CommitReleaseTool NewTool(JsonCommitLeaseStore store)
        => new(store);

    [Fact]
    public async Task Release_OwnLease_ReturnsTrue_AndClearsActiveList()
    {
        using var store = NewStore();
        var cwd = Checkout("a");
        var lease = NewLeaseTool(store, cwd);
        await lease.LeaseAsync("ses_test_1_000000000000000000000", "main");
        var tool = NewTool(store);

        var result = await tool.ReleaseAsync("ses_test_1_000000000000000000000");

        using var doc = JsonDocument.Parse(result);
        Assert.True(doc.RootElement.GetProperty("released").GetBoolean());
        Assert.Empty(await store.ListActiveAsync(cwd, null));
    }

    [Fact]
    public async Task Release_WithNoLease_ReturnsFalse()
    {
        using var store = NewStore();
        var tool = NewTool(store);

        var result = await tool.ReleaseAsync("ses_test_1_000000000000000000000");

        using var doc = JsonDocument.Parse(result);
        Assert.False(doc.RootElement.GetProperty("released").GetBoolean());
    }

    [Fact]
    public async Task Release_OtherSessionsLease_ReturnsFalse_AndLeavesLease()
    {
        using var store = NewStore();
        var cwd = Checkout("a");
        var lease = NewLeaseTool(store, cwd);
        await lease.LeaseAsync("ses_test_2_000000000000000000000", "main");
        var tool = NewTool(store);

        var result = await tool.ReleaseAsync("ses_test_1_000000000000000000000");

        using var doc = JsonDocument.Parse(result);
        Assert.False(doc.RootElement.GetProperty("released").GetBoolean());
        var remaining = Assert.Single(await store.ListActiveAsync(cwd, null));
        Assert.Equal("ses_test_2_000000000000000000000", remaining.SessionId);
    }

    [Fact]
    public async Task Release_BlankSessionId_ReturnsError_AndTouchesNothing()
    {
        using var store = NewStore();
        var cwd = Checkout("a");
        var lease = NewLeaseTool(store, cwd);
        await lease.LeaseAsync("ses_test_1_000000000000000000000", "main");
        var tool = NewTool(store);

        var result = await tool.ReleaseAsync("   ");

        using var doc = JsonDocument.Parse(result);
        Assert.False(string.IsNullOrWhiteSpace(doc.RootElement.GetProperty("error").GetString()));
        Assert.Single(await store.ListActiveAsync(cwd, null));
    }

    [Fact]
    public async Task Release_StoreThrows_ReturnsCompactError()
    {
        var tool = new CommitReleaseTool(new ThrowingLeaseStore());

        var result = await tool.ReleaseAsync("ses_test_1_000000000000000000000");

        using var doc = JsonDocument.Parse(result);
        Assert.False(string.IsNullOrWhiteSpace(doc.RootElement.GetProperty("error").GetString()));
    }

    private sealed class ManualTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;

        public override DateTimeOffset GetUtcNow() => _now;
    }

    private sealed class ThrowingLeaseStore : ICommitLeaseStore
    {
        public Task<IReadOnlyList<CommitLeaseRecord>> ListActiveAsync(string checkout, string? branch, CancellationToken ct = default)
            => throw new InvalidOperationException("store is unavailable");

        public Task<CommitLeaseRecord?> TryLeaseAsync(CommitLeaseRecord candidate, CancellationToken ct = default)
            => throw new InvalidOperationException("store is unavailable");

        public Task<bool> ReleaseAsync(string sessionId, CommitLeaseKind? kind, CancellationToken ct = default)
            => throw new InvalidOperationException("store is unavailable");

        public Task<bool> ForceReleaseAsync(string leaseId, CancellationToken ct = default)
            => throw new InvalidOperationException("store is unavailable");

        public Task<bool> HeartbeatAsync(string leaseId, string sessionId, CancellationToken ct = default)
            => throw new InvalidOperationException("store is unavailable");

        public Task<IReadOnlyList<CommitLeaseRecord>> ListActiveAcrossCheckoutsAsync(string? branch, CancellationToken ct = default)
            => throw new InvalidOperationException("store is unavailable");

        public Task<IReadOnlyList<CommitLeaseRecord>> ListRecentlyReleasedAsync(string checkout, int limit = 20, CancellationToken ct = default)
            => throw new InvalidOperationException("store is unavailable");

        public Task<IReadOnlyList<CommitLeaseRecord>> ListRecentlyReleasedAcrossCheckoutsAsync(int limit, CancellationToken ct = default)
            => throw new InvalidOperationException("store is unavailable");

        public Task<IReadOnlyList<CommitLeaseRecord>> ListAllAsync(CancellationToken ct = default)
            => throw new InvalidOperationException("store is unavailable");
    }
}
