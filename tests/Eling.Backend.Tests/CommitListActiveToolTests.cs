using System.Text.Json;
using Eling.Backend.Mcp.Tools;
using Eling.Core.CommitLease;

namespace Eling.Backend.Tests;

public sealed class CommitListActiveToolTests : IDisposable
{
    private readonly string _root;
    private readonly ManualTimeProvider _clock;

    public CommitListActiveToolTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "eling-commit-list-active-tool-" + Guid.NewGuid().ToString("N")[..8]);
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

    private CommitListActiveTool NewTool(JsonCommitLeaseStore store, string cwd)
        => new(store, _clock, cwd);

    private Task SeedCommitLeaseAsync(
        JsonCommitLeaseStore store,
        string checkout,
        string sessionId,
        string branch,
        string? intent = null,
        string[]? paths = null,
        int ttlSeconds = 300)
        => store.TryLeaseAsync(new CommitLeaseRecord {
            LeaseId =  Ulid.NewUlid().ToString(),
            SessionId =  sessionId,
            Kind =  CommitLeaseKind.Commit,
            Checkout =  checkout,
            Branch =  branch,
            Since =  _clock.GetUtcNow(),
            LastHeartbeat =  _clock.GetUtcNow(),
            TtlSeconds =  ttlSeconds,
            Paths =  paths,
            Intent =  intent});

    private Task SeedFileLeaseAsync(JsonCommitLeaseStore store, string checkout, string sessionId, string branch)
        => store.TryLeaseAsync(new CommitLeaseRecord {
            LeaseId =  Ulid.NewUlid().ToString(),
            SessionId =  sessionId,
            Kind =  CommitLeaseKind.File,
            Checkout =  checkout,
            Branch =  branch,
            Since =  _clock.GetUtcNow(),
            LastHeartbeat =  _clock.GetUtcNow(),
            TtlSeconds =  300,
            Paths =  ["src/a.cs"],
            Intent =  null});

    [Fact]
    public async Task EmptyStore_ReturnsEmptyList()
    {
        using var store = NewStore();
        var tool = NewTool(store, Checkout("a"));

        var result = await tool.ListActiveAsync();

        using var doc = JsonDocument.Parse(result);
        Assert.Equal(JsonValueKind.Array, doc.RootElement.GetProperty("leases").ValueKind);
        Assert.Equal(0, doc.RootElement.GetProperty("leases").GetArrayLength());
    }

    [Fact]
    public async Task ActiveLease_OnThisCheckout_Appears()
    {
        using var store = NewStore();
        var cwd = Checkout("a");
        await SeedCommitLeaseAsync(store, cwd, "ses_test_2_000000000000000000000", "main", intent: "land the feature", paths: ["src/a.cs", "src/b.cs"]);
        _clock.Advance(TimeSpan.FromSeconds(30));
        var tool = NewTool(store, cwd);

        var result = await tool.ListActiveAsync();

        using var doc = JsonDocument.Parse(result);
        var lease = Assert.Single(doc.RootElement.GetProperty("leases").EnumerateArray());
        Assert.Equal("ses_test_2_000000000000000000000", lease.GetProperty("holder_session_id").GetString());
        Assert.Equal(_clock.GetUtcNow() - TimeSpan.FromSeconds(30), lease.GetProperty("since").GetDateTimeOffset());
        Assert.Equal(30, lease.GetProperty("lease_age_seconds").GetInt64());
        Assert.Equal("land the feature", lease.GetProperty("intent").GetString());
        Assert.Equal(new[] { "src/a.cs", "src/b.cs" }, lease.GetProperty("paths").EnumerateArray().Select(e => e.GetString()));
    }

    [Fact]
    public async Task StaleLease_DoesNotAppear()
    {
        using var store = NewStore();
        var cwd = Checkout("a");
        await SeedCommitLeaseAsync(store, cwd, "ses_test_2_000000000000000000000", "main", ttlSeconds: 300);
        _clock.Advance(TimeSpan.FromSeconds(300));
        var tool = NewTool(store, cwd);

        var result = await tool.ListActiveAsync();

        using var doc = JsonDocument.Parse(result);
        Assert.Equal(0, doc.RootElement.GetProperty("leases").GetArrayLength());
    }

    [Fact]
    public async Task Lease_OnDifferentCheckout_DoesNotAppear()
    {
        using var store = NewStore();
        var elsewhere = Checkout("b");
        await SeedCommitLeaseAsync(store, elsewhere, "ses_test_2_000000000000000000000", "main");
        var tool = NewTool(store, Checkout("a"));

        var result = await tool.ListActiveAsync();

        using var doc = JsonDocument.Parse(result);
        Assert.Equal(0, doc.RootElement.GetProperty("leases").GetArrayLength());
    }

    [Fact]
    public async Task FileLease_OnThisCheckout_DoesNotAppear()
    {
        using var store = NewStore();
        var cwd = Checkout("a");
        await SeedFileLeaseAsync(store, cwd, "ses_test_2_000000000000000000000", "main");
        var tool = NewTool(store, cwd);

        var result = await tool.ListActiveAsync();

        using var doc = JsonDocument.Parse(result);
        Assert.Equal(0, doc.RootElement.GetProperty("leases").GetArrayLength());
    }

    [Fact]
    public async Task BranchFilter_ReturnsOnlyMatchingBranch()
    {
        using var store = NewStore();
        var cwd = Checkout("a");
        await SeedCommitLeaseAsync(store, cwd, "ses_test_2_000000000000000000000", "main");
        await SeedCommitLeaseAsync(store, cwd, "ses_test_2_000000000000000000000", "feature");
        var tool = NewTool(store, cwd);

        var result = await tool.ListActiveAsync("main");

        using var doc = JsonDocument.Parse(result);
        var lease = Assert.Single(doc.RootElement.GetProperty("leases").EnumerateArray());
        Assert.Equal("ses_test_2_000000000000000000000", lease.GetProperty("holder_session_id").GetString());
    }

    [Fact]
    public async Task WhitespaceBranch_BehavesAsNoFilter()
    {
        using var store = NewStore();
        var cwd = Checkout("a");
        await SeedCommitLeaseAsync(store, cwd, "ses_test_2_000000000000000000000", "main");
        await SeedCommitLeaseAsync(store, cwd, "ses_test_2_000000000000000000000", "feature");
        var tool = NewTool(store, cwd);

        var result = await tool.ListActiveAsync("   ");

        using var doc = JsonDocument.Parse(result);
        Assert.Equal(2, doc.RootElement.GetProperty("leases").GetArrayLength());
    }

    [Fact]
    public async Task StoreThrows_ReturnsErrorShape()
    {
        var tool = new CommitListActiveTool(new ThrowingLeaseStore(), _clock, Checkout("a"));

        var result = await tool.ListActiveAsync();

        using var doc = JsonDocument.Parse(result);
        Assert.False(string.IsNullOrWhiteSpace(doc.RootElement.GetProperty("error").GetString()));
    }

    private sealed class ThrowingLeaseStore : ICommitLeaseStore
    {
        public Task<IReadOnlyList<CommitLeaseRecord>> ListActiveAsync(string checkout, string? branch, CancellationToken ct = default)
            => throw new InvalidOperationException("store is broken");

        public Task<CommitLeaseRecord?> TryLeaseAsync(CommitLeaseRecord candidate, CancellationToken ct = default)
            => throw new InvalidOperationException("store is broken");

        public Task<bool> ReleaseAsync(string sessionId, CommitLeaseKind? kind, CancellationToken ct = default)
            => throw new InvalidOperationException("store is broken");

        public Task<bool> ForceReleaseAsync(string leaseId, CancellationToken ct = default)
            => throw new InvalidOperationException("store is broken");

        public Task<bool> HeartbeatAsync(string leaseId, string sessionId, CancellationToken ct = default)
            => throw new InvalidOperationException("store is broken");

        public Task<IReadOnlyList<CommitLeaseRecord>> ListActiveAcrossCheckoutsAsync(string? branch, CancellationToken ct = default)
            => throw new InvalidOperationException("store is broken");

        public Task<IReadOnlyList<CommitLeaseRecord>> ListRecentlyReleasedAsync(string checkout, int limit = 20, CancellationToken ct = default)
            => throw new InvalidOperationException("store is broken");

        public Task<IReadOnlyList<CommitLeaseRecord>> ListRecentlyReleasedAcrossCheckoutsAsync(int limit, CancellationToken ct = default)
            => throw new InvalidOperationException("store is broken");

        public Task<IReadOnlyList<CommitLeaseRecord>> ListAllAsync(CancellationToken ct = default)
            => throw new InvalidOperationException("store is broken");
    }

    private sealed class ManualTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now = _now.Add(by);
    }
}
