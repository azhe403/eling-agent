using Eling.Core.CommitLease;

namespace Eling.Backend.Tests;

public sealed class JsonCommitLeaseStoreTests : IDisposable
{
    private readonly string _root;
    private readonly ManualTimeProvider _clock;

    private string StorePath => Path.Combine(_root, "commit-leases.json");

    public JsonCommitLeaseStoreTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "eling-lease-store-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_root);
        _clock = new ManualTimeProvider(new DateTimeOffset(2026, 11, 12, 12, 0, 0, TimeSpan.Zero));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private string Checkout(string name) => Path.Combine(_root, name);

    private JsonCommitLeaseStore NewStore(ICommitLeaseChangeNotifier? notifier = null)
        => new(StorePath, notifier, _clock);

    private CommitLeaseRecord Candidate(
        string sessionId,
        string checkout,
        string? branch = "main",
        CommitLeaseKind kind = CommitLeaseKind.Commit,
        string[]? paths = null,
        int ttlSeconds = 60,
        DateTimeOffset? lastHeartbeat = null)
    {
        var now = _clock.GetUtcNow();
        return new CommitLeaseRecord {
            LeaseId =  Ulid.NewUlid().ToString(),
            SessionId =  sessionId,
            Kind =  kind,
            Checkout =  checkout,
            Branch =  branch,
            Since =  now,
            LastHeartbeat =  lastHeartbeat ?? now,
            TtlSeconds =  ttlSeconds,
            Paths =  paths,
            Intent =  "test lease"};
    }

    [Fact]
    public async Task TryLease_ThenListActive_ReturnsTheLease()
    {
        var store = NewStore();
        var lease = Candidate("s1", Checkout("a"));

        var result = await store.TryLeaseAsync(lease);

        Assert.Null(result);
        var active = await store.ListActiveAsync(Checkout("a"), "main");
        var only = Assert.Single(active);
        Assert.Equal(lease.LeaseId, only.LeaseId);
        Assert.Equal(lease.SessionId, only.SessionId);
    }

    [Fact]
    public async Task TryLease_SecondCommitLeaseSameCheckoutBranch_ReturnsWinnerAndDoesNotOverwrite()
    {
        var store = NewStore();
        var first = Candidate("s1", Checkout("a"));
        await store.TryLeaseAsync(first);

        var second = Candidate("s2", Checkout("a"));
        var conflict = await store.TryLeaseAsync(second);

        Assert.NotNull(conflict);
        Assert.Equal(first.LeaseId, conflict.LeaseId);
        Assert.Single(await store.ListAllAsync());
        var active = await store.ListActiveAsync(Checkout("a"), "main");
        Assert.Equal(first.LeaseId, Assert.Single(active).LeaseId);
    }

    [Fact]
    public async Task TryLease_DifferentCheckout_DoesNotConflict()
    {
        var store = NewStore();
        await store.TryLeaseAsync(Candidate("s1", Checkout("a")));

        var result = await store.TryLeaseAsync(Candidate("s2", Checkout("b")));

        Assert.Null(result);
        Assert.Equal(2, (await store.ListAllAsync()).Count);
    }

    [Fact]
    public async Task TryLease_CheckoutStoredWithForwardSlashes_ConflictsWithBackslashCheckout()
    {
        var store = NewStore();
        var checkout = Checkout("a");
        var forward = checkout.Replace('\\', '/');
        var first = Candidate("s1", forward);
        await store.TryLeaseAsync(first);

        var conflict = await store.TryLeaseAsync(Candidate("s2", checkout));

        Assert.NotNull(conflict);
        Assert.Equal(first.LeaseId, conflict.LeaseId);
        Assert.Single(await store.ListActiveAsync(checkout, "main"));
        Assert.Single(await store.ListActiveAsync(forward, "main"));
    }

    [Fact]
    public async Task ListActiveAsync_CheckoutStoredWithBackslashes_FoundViaForwardSlashes()
    {
        var store = NewStore();
        var checkout = Checkout("a");
        var forward = checkout.Replace('\\', '/');
        await store.TryLeaseAsync(Candidate("s1", checkout));

        var active = await store.ListActiveAsync(forward, "main");

        Assert.Single(active);
    }

    [Fact]
    public async Task TryLease_SameCheckoutDifferentBranch_DoesNotConflict()
    {
        var store = NewStore();
        await store.TryLeaseAsync(Candidate("s1", Checkout("a"), branch: "main"));

        var result = await store.TryLeaseAsync(Candidate("s2", Checkout("a"), branch: "feature/x"));

        Assert.Null(result);
        Assert.Equal(2, (await store.ListAllAsync()).Count);
    }

    [Fact]
    public async Task ReleaseAsync_RemovesLeaseAndRecordsItWithExplicitReason()
    {
        var store = NewStore();
        var lease = Candidate("s1", Checkout("a"));
        await store.TryLeaseAsync(lease);

        var released = await store.ReleaseAsync("s1", kind: null);

        Assert.True(released);
        Assert.Empty(await store.ListActiveAsync(Checkout("a"), "main"));
        var recent = await store.ListRecentlyReleasedAsync(Checkout("a"));
        var only = Assert.Single(recent);
        Assert.Equal(lease.LeaseId, only.LeaseId);
        Assert.Equal("explicit", only.ReleaseReason);
        Assert.Equal(_clock.GetUtcNow(), only.ReleasedAt);

        Assert.False(await store.ReleaseAsync("s1", kind: null));
    }

    [Fact]
    public async Task ReleaseAsync_WithKindFilter_ReleasesOnlyThatKind()
    {
        var store = NewStore();
        var checkout = Checkout("a");
        await store.TryLeaseAsync(Candidate("s1", checkout, kind: CommitLeaseKind.Commit));
        await store.TryLeaseAsync(Candidate("s1", checkout, kind: CommitLeaseKind.File, paths: ["src/a.cs"]));

        var released = await store.ReleaseAsync("s1", CommitLeaseKind.File);

        Assert.True(released);
        var remaining = await store.ListActiveAsync(checkout, "main");
        Assert.Equal(CommitLeaseKind.Commit, Assert.Single(remaining).Kind);
    }

    [Fact]
    public async Task ListAllAsync_ReturnsCollectionsAsIs_WithoutSweepingStale()
    {
        var store = NewStore();
        var staleLease = Candidate("s1", Checkout("a"), lastHeartbeat: _clock.GetUtcNow().AddSeconds(-61));
        await store.TryLeaseAsync(staleLease);

        var all = await store.ListAllAsync();

        Assert.Equal(staleLease.LeaseId, Assert.Single(all).LeaseId);
        Assert.Empty(await store.ListRecentlyReleasedAsync(Checkout("a")));
    }

    [Fact]
    public async Task ListActiveAsync_DropsRecordWhoseHeartbeatIsBeyondTtl()
    {
        var store = NewStore();
        var staleAt = _clock.GetUtcNow().AddSeconds(-61);
        await store.TryLeaseAsync(Candidate("s1", Checkout("a"), lastHeartbeat: staleAt));

        Assert.Empty(await store.ListActiveAsync(Checkout("a"), "main"));
    }

    [Fact]
    public async Task ListActiveAsync_TreatsExactlyTtlBoundaryAsStale()
    {
        var store = NewStore();
        var boundary = _clock.GetUtcNow().AddSeconds(-60);
        await store.TryLeaseAsync(Candidate("s1", Checkout("a"), lastHeartbeat: boundary));

        Assert.Empty(await store.ListActiveAsync(Checkout("a"), "main"));
    }

    [Fact]
    public async Task ListActiveAsync_KeepsRecordJustBelowBoundary()
    {
        var store = NewStore();
        var justLive = _clock.GetUtcNow().AddSeconds(-59);
        await store.TryLeaseAsync(Candidate("s1", Checkout("a"), lastHeartbeat: justLive));

        Assert.Single(await store.ListActiveAsync(Checkout("a"), "main"));
    }

    [Fact]
    public async Task ListActiveAsync_SweepsStaleRecordIntoRecentlyReleased()
    {
        var store = NewStore();
        var lease = Candidate("s1", Checkout("a"), lastHeartbeat: _clock.GetUtcNow().AddSeconds(-61));
        await store.TryLeaseAsync(lease);

        _ = await store.ListActiveAsync(Checkout("a"), "main");

        var recent = await store.ListRecentlyReleasedAsync(Checkout("a"));
        var only = Assert.Single(recent);
        Assert.Equal(lease.LeaseId, only.LeaseId);
        Assert.Equal("stale", only.ReleaseReason);
    }

    [Fact]
    public async Task HeartbeatAsync_WithWrongSession_ReturnsFalseAndDoesNotExtend()
    {
        var store = NewStore();
        var lease = Candidate("s1", Checkout("a"));
        await store.TryLeaseAsync(lease);
        var before = (await store.ListAllAsync()).Single().LastHeartbeat;

        _clock.Advance(TimeSpan.FromSeconds(30));
        var ok = await store.HeartbeatAsync(lease.LeaseId, "s2");

        Assert.False(ok);
        Assert.Equal(before, (await store.ListAllAsync()).Single().LastHeartbeat);
    }

    [Fact]
    public async Task HeartbeatAsync_WithOwnerSession_ExtendsLastHeartbeat()
    {
        var store = NewStore();
        var lease = Candidate("s1", Checkout("a"));
        await store.TryLeaseAsync(lease);

        _clock.Advance(TimeSpan.FromSeconds(30));
        var ok = await store.HeartbeatAsync(lease.LeaseId, "s1");

        Assert.True(ok);
        Assert.Equal(_clock.GetUtcNow(), (await store.ListAllAsync()).Single().LastHeartbeat);
    }

    [Fact]
    public async Task TryLease_FileLeaseWithIntersectingPaths_ReturnsWinner()
    {
        var store = NewStore();
        var checkout = Checkout("a");
        var first = Candidate("s1", checkout, kind: CommitLeaseKind.File, paths: ["src/feature.cs"]);
        await store.TryLeaseAsync(first);

        var conflict = await store.TryLeaseAsync(
            Candidate("s2", checkout, kind: CommitLeaseKind.File, paths: ["src/feature.cs"]));

        Assert.NotNull(conflict);
        Assert.Equal(first.LeaseId, conflict.LeaseId);
    }

    [Fact]
    public async Task TryLease_FileLeaseWithDirectoryPathCoveringCandidate_ReturnsWinner()
    {
        var store = NewStore();
        var checkout = Checkout("a");
        var first = Candidate("s1", checkout, kind: CommitLeaseKind.File, paths: ["src"]);
        await store.TryLeaseAsync(first);

        var conflict = await store.TryLeaseAsync(
            Candidate("s2", checkout, kind: CommitLeaseKind.File, paths: ["src/nested/leaf.cs"]));

        Assert.NotNull(conflict);
        Assert.Equal(first.LeaseId, conflict.LeaseId);
    }

    [Fact]
    public async Task TryLease_FileLeaseWithDisjointPaths_DoesNotConflict()
    {
        var store = NewStore();
        var checkout = Checkout("a");
        await store.TryLeaseAsync(Candidate("s1", checkout, kind: CommitLeaseKind.File, paths: ["src/a.cs"]));

        var result = await store.TryLeaseAsync(
            Candidate("s2", checkout, kind: CommitLeaseKind.File, paths: ["docs/b.md"]));

        Assert.Null(result);
        Assert.Equal(2, (await store.ListAllAsync()).Count);
    }

    [Fact]
    public async Task TryLease_DifferentKindsDoNotConflict()
    {
        var store = NewStore();
        var checkout = Checkout("a");
        await store.TryLeaseAsync(Candidate("s1", checkout, kind: CommitLeaseKind.Commit, branch: "main"));

        var result = await store.TryLeaseAsync(
            Candidate("s2", checkout, kind: CommitLeaseKind.File, paths: ["src/a.cs"]));

        Assert.Null(result);
    }

    [Fact]
    public async Task ListRecentlyReleasedAsync_ReturnsNewestFirstAndHonoursLimit()
    {
        var store = NewStore();
        var checkout = Checkout("a");
        var ids = new List<string>();
        for (var i = 0; i < 3; i++)
        {
            var lease = Candidate($"s{i}", checkout);
            await store.TryLeaseAsync(lease);
            ids.Add(lease.LeaseId);
            _clock.Advance(TimeSpan.FromMinutes(1));
            await store.ReleaseAsync($"s{i}", kind: null);
            _clock.Advance(TimeSpan.FromMinutes(1));
        }

        var all = await store.ListRecentlyReleasedAsync(checkout, limit: 10);
        Assert.Equal(new[] { ids[2], ids[1], ids[0] }, all.Select(l => l.LeaseId).ToArray());

        var topTwo = await store.ListRecentlyReleasedAsync(checkout, limit: 2);
        Assert.Equal(new[] { ids[2], ids[1] }, topTwo.Select(l => l.LeaseId).ToArray());
    }

    [Fact]
    public async Task ListRecentlyReleasedAsync_CapsHistoryAtFiftyPerCheckout()
    {
        var store = NewStore();
        var checkout = Checkout("a");
        for (var i = 0; i < 55; i++)
        {
            var lease = Candidate($"s{i}", checkout);
            await store.TryLeaseAsync(lease);
            _clock.Advance(TimeSpan.FromSeconds(1));
            await store.ReleaseAsync($"s{i}", kind: null);
        }

        var recent = await store.ListRecentlyReleasedAsync(checkout, limit: 100);
        Assert.Equal(50, recent.Count);
    }

    [Fact]
    public async Task ForceReleaseAsync_ReleasesLiveLeaseRegardlessOfOwner_WithForcedReason()
    {
        var store = NewStore();
        var lease = Candidate("other-session", Checkout("a"));
        await store.TryLeaseAsync(lease);

        var released = await store.ForceReleaseAsync(lease.LeaseId);

        Assert.True(released);
        Assert.Empty(await store.ListActiveAsync(Checkout("a"), "main"));
        var recent = Assert.Single(await store.ListRecentlyReleasedAsync(Checkout("a")));
        Assert.Equal(lease.LeaseId, recent.LeaseId);
        Assert.Equal("other-session", recent.SessionId);
        Assert.Equal("forced", recent.ReleaseReason);
        Assert.Equal(_clock.GetUtcNow(), recent.ReleasedAt);
    }

    [Fact]
    public async Task ForceReleaseAsync_ReleasesLeaseOwnedByAnotherSession_LeavesOwnLeasesAlone()
    {
        var store = NewStore();
        var checkout = Checkout("a");
        var foreign = Candidate("ses_foreign_0000000000000000000000000", checkout);
        var own = new CommitLeaseRecord {
            LeaseId =  Ulid.NewUlid().ToString(),
            SessionId =  "caller-session",
            Kind =  CommitLeaseKind.File,
            Checkout =  checkout,
            Branch =  "main",
            Since =  _clock.GetUtcNow(),
            LastHeartbeat =  _clock.GetUtcNow(),
            TtlSeconds =  60,
            Paths =  ["docs/b.md"],
            Intent =  "own lease"};
        await store.TryLeaseAsync(foreign);
        await store.TryLeaseAsync(own);

        var released = await store.ForceReleaseAsync(foreign.LeaseId);

        Assert.True(released);
        var remaining = await store.ListActiveAsync(checkout, "main");
        Assert.Equal(own.LeaseId, Assert.Single(remaining).LeaseId);
    }

    [Fact]
    public async Task ForceReleaseAsync_UnknownLeaseId_ReturnsFalse()
    {
        var store = NewStore();
        await store.TryLeaseAsync(Candidate("s1", Checkout("a")));

        var released = await store.ForceReleaseAsync("no-such-lease-id");

        Assert.False(released);
        Assert.Single(await store.ListActiveAsync(Checkout("a"), "main"));
        Assert.Empty(await store.ListRecentlyReleasedAsync(Checkout("a")));
    }

    [Fact]
    public async Task ForceReleaseAsync_AlreadyReleasedLease_ReturnsFalse()
    {
        var store = NewStore();
        var lease = Candidate("s1", Checkout("a"));
        await store.TryLeaseAsync(lease);
        Assert.True(await store.ReleaseAsync("s1", kind: null));

        var released = await store.ForceReleaseAsync(lease.LeaseId);

        Assert.False(released);
        var recent = Assert.Single(await store.ListRecentlyReleasedAsync(Checkout("a")));
        Assert.Equal("explicit", recent.ReleaseReason);
    }

    [Fact]
    public async Task ForceReleaseAsync_StaleLeaseId_ReturnsFalseAndKeepsStaleReason()
    {
        var store = NewStore();
        var staleAt = _clock.GetUtcNow().AddSeconds(-61);
        var stale = Candidate("s1", Checkout("a"), lastHeartbeat: staleAt);
        await store.TryLeaseAsync(stale);

        var released = await store.ForceReleaseAsync(stale.LeaseId);

        Assert.False(released);
        var recent = Assert.Single(await store.ListRecentlyReleasedAsync(Checkout("a")));
        Assert.Equal(stale.LeaseId, recent.LeaseId);
        Assert.Equal("stale", recent.ReleaseReason);
    }

    [Fact]
    public async Task ForceReleaseAsync_NotifiesTheChangeNotifier()
    {
        var notifier = new CountingNotifier();
        var store = NewStore(notifier);
        var lease = Candidate("s1", Checkout("a"));
        await store.TryLeaseAsync(lease);
        var before = notifier.Count;

        var released = await store.ForceReleaseAsync(lease.LeaseId);

        Assert.True(released);
        Assert.True(notifier.Count > before);
    }

    [Fact]
    public async Task Mutations_NotifyTheChangeNotifier()
    {
        var notifier = new CountingNotifier();
        var store = NewStore(notifier);
        var lease = Candidate("s1", Checkout("a"));

        await store.TryLeaseAsync(lease);
        Assert.Equal(1, notifier.Count);

        await store.HeartbeatAsync(lease.LeaseId, "s1");
        Assert.Equal(2, notifier.Count);

        await store.ReleaseAsync("s1", kind: null);
        Assert.Equal(3, notifier.Count);
    }

    private sealed class ManualTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now = _now.Add(by);
    }

    private sealed class CountingNotifier : ICommitLeaseChangeNotifier
    {
        public int Count { get; private set; }

        public Task NotifyAsync(CancellationToken ct = default)
        {
            Count++;
            return Task.CompletedTask;
        }
    }
}
