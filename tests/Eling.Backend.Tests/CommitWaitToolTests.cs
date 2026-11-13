using System.Diagnostics;
using System.Text.Json;
using Eling.Backend.Mcp.Tools;
using Eling.Core.CommitLease;

namespace Eling.Backend.Tests;

public sealed class CommitWaitToolTests : IDisposable
{
    private readonly string _root;
    private readonly ManualTimeProvider _clock;

    public CommitWaitToolTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "eling-commit-wait-tool-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_root);
        _clock = new ManualTimeProvider(new DateTimeOffset(2026, 11, 12, 12, 0, 0, TimeSpan.Zero));
    }

    public void Dispose()
    {
        if (!Directory.Exists(_root)) return;
        // git object files are created read-only; Directory.Delete(recursive)
        // cannot remove them on Windows, so clear the attribute first.
        foreach (var file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
        {
            try
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Debug.WriteLine($"Could not clear read-only on {file}: {ex.Message}");
            }
        }
        Directory.Delete(_root, recursive: true);
    }

    private string Checkout(string name)
    {
        var dir = Path.Combine(_root, name);
        Directory.CreateDirectory(dir);
        return dir;
    }

    private JsonCommitLeaseStore NewStore()
        => new(Path.Combine(_root, "commit-leases.json"), notifier: null, timeProvider: _clock);

    private CommitWaitTool NewTool(JsonCommitLeaseStore store, string cwd)
        => new(store, _clock, cwd);

    private Task SeedCommitLeaseAsync(
        JsonCommitLeaseStore store,
        string checkout,
        string sessionId,
        string branch,
        string? intent = null,
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
            Paths =  null,
            Intent =  intent});

    [Fact]
    public async Task NoForeignLease_ReturnsGreenImmediately()
    {
        using var store = NewStore();
        var cwd = Checkout("a");
        var tool = NewTool(store, cwd);

        var result = await tool.WaitAsync("ses_test_1_000000000000000000000", total_timeout_seconds: 5, initial_interval_seconds: 0.05, branch: "main");

        using var doc = JsonDocument.Parse(result);
        Assert.Equal("green", doc.RootElement.GetProperty("verdict").GetString());
        var waited = doc.RootElement.GetProperty("waited_seconds").GetDouble();
        Assert.InRange(waited, 0.0, 1.0);
        Assert.False(doc.RootElement.TryGetProperty("holder", out _));
        Assert.Empty(await store.ListAllAsync());
    }

    [Fact]
    public async Task ForeignLeaseReleasedMidWait_ReturnsGreenAfterRelease()
    {
        using var store = NewStore();
        var cwd = Checkout("a");
        await SeedCommitLeaseAsync(store, cwd, "ses_test_2_000000000000000000000", "main", intent: "holds the slot");
        // Handshake: the signal trips only after the waiter's first store read
        // has completed (with the foreign lease still visible), so the release
        // below can never land before that read. The first poll must then see
        // the lease, and green can only arrive on a later poll.
        using var firstPoll = new ManualResetEventSlim();
        var signalingStore = new FirstPollSignalingLeaseStore(store, firstPoll);
        var tool = new CommitWaitTool(signalingStore, _clock, cwd);

        var wait = Task.Run(() => tool.WaitAsync("ses_test_1_000000000000000000000", total_timeout_seconds: 5, initial_interval_seconds: 0.05, branch: "main"));
        Assert.True(firstPoll.Wait(TimeSpan.FromSeconds(5)), "Waiter never polled the store.");
        await store.ReleaseAsync("ses_test_2_000000000000000000000", CommitLeaseKind.Commit);
        var result = await wait;

        using var doc = JsonDocument.Parse(result);
        Assert.Equal("green", doc.RootElement.GetProperty("verdict").GetString());
        var waited = doc.RootElement.GetProperty("waited_seconds").GetDouble();
        Assert.InRange(waited, 0.01, 5.0);
        Assert.False(doc.RootElement.TryGetProperty("holder", out _));
    }

    [Fact]
    public async Task ForeignLeaseHeld_TimesOut_ReturnsRedWithHolderInfo()
    {
        using var store = NewStore();
        var cwd = Checkout("a");
        await SeedCommitLeaseAsync(store, cwd, "ses_test_2_000000000000000000000", "main", intent: "land the feature");
        var tool = NewTool(store, cwd);

        var result = await tool.WaitAsync("ses_test_1_000000000000000000000", total_timeout_seconds: 0.3, initial_interval_seconds: 0.05, branch: "main");

        using var doc = JsonDocument.Parse(result);
        Assert.Equal("red", doc.RootElement.GetProperty("verdict").GetString());
        var waited = doc.RootElement.GetProperty("waited_seconds").GetDouble();
        Assert.InRange(waited, 0.25, 5.0);
        var holder = doc.RootElement.GetProperty("holder");
        Assert.Equal("ses_test_2_000000000000000000000", holder.GetProperty("holder_session_id").GetString());
        Assert.Equal("land the feature", holder.GetProperty("intent").GetString());
    }

    [Fact]
    public async Task OwnLease_DoesNotBlock()
    {
        using var store = NewStore();
        var cwd = Checkout("a");
        await SeedCommitLeaseAsync(store, cwd, "ses_test_1_000000000000000000000", "main");
        var tool = NewTool(store, cwd);

        var result = await tool.WaitAsync("ses_test_1_000000000000000000000", total_timeout_seconds: 5, initial_interval_seconds: 0.05, branch: "main");

        using var doc = JsonDocument.Parse(result);
        Assert.Equal("green", doc.RootElement.GetProperty("verdict").GetString());
    }

    [Fact]
    public async Task BlankSessionId_ReturnsError()
    {
        using var store = NewStore();
        var cwd = Checkout("a");
        var tool = NewTool(store, cwd);

        var result = await tool.WaitAsync("   ", total_timeout_seconds: 1, initial_interval_seconds: 0.05, branch: "main");

        using var doc = JsonDocument.Parse(result);
        Assert.False(string.IsNullOrWhiteSpace(doc.RootElement.GetProperty("error").GetString()));
    }

    [Fact]
    public async Task NonGitDirectory_NoBranchPassed_FailSafeTreatsAnyBranchAsBlocking()
    {
        using var store = NewStore();
        var cwd = Checkout("a"); // no git init: branch is undiscoverable, so a foreign lease on any branch blocks
        await SeedCommitLeaseAsync(store, cwd, "ses_test_2_000000000000000000000", "some-other-branch", intent: "holds the slot");
        var tool = NewTool(store, cwd);

        var result = await tool.WaitAsync("ses_test_1_000000000000000000000", total_timeout_seconds: 0.3, initial_interval_seconds: 0.05);

        using var doc = JsonDocument.Parse(result);
        Assert.Equal("red", doc.RootElement.GetProperty("verdict").GetString());
        Assert.Equal("ses_test_2_000000000000000000000", doc.RootElement.GetProperty("holder").GetProperty("holder_session_id").GetString());
    }

    private sealed class FirstPollSignalingLeaseStore(ICommitLeaseStore inner, ManualResetEventSlim firstPoll) : ICommitLeaseStore
    {
        public async Task<IReadOnlyList<CommitLeaseRecord>> ListActiveAsync(string checkout, string? branch, CancellationToken ct = default)
        {
            var result = await inner.ListActiveAsync(checkout, branch, ct);
            firstPoll.Set(); // trip only after the read completed: the caller has observed the store's state for this poll
            return result;
        }

        public Task<CommitLeaseRecord?> TryLeaseAsync(CommitLeaseRecord candidate, CancellationToken ct = default)
            => inner.TryLeaseAsync(candidate, ct);

        public Task<bool> ReleaseAsync(string sessionId, CommitLeaseKind? kind, CancellationToken ct = default)
            => inner.ReleaseAsync(sessionId, kind, ct);

        public Task<bool> ForceReleaseAsync(string leaseId, CancellationToken ct = default)
            => inner.ForceReleaseAsync(leaseId, ct);

        public Task<bool> HeartbeatAsync(string leaseId, string sessionId, CancellationToken ct = default)
            => inner.HeartbeatAsync(leaseId, sessionId, ct);

        public Task<IReadOnlyList<CommitLeaseRecord>> ListActiveAcrossCheckoutsAsync(string? branch, CancellationToken ct = default)
            => inner.ListActiveAcrossCheckoutsAsync(branch, ct);

        public Task<IReadOnlyList<CommitLeaseRecord>> ListRecentlyReleasedAsync(string checkout, int limit = 20, CancellationToken ct = default)
            => inner.ListRecentlyReleasedAsync(checkout, limit, ct);

        public Task<IReadOnlyList<CommitLeaseRecord>> ListRecentlyReleasedAcrossCheckoutsAsync(int limit, CancellationToken ct = default)
            => inner.ListRecentlyReleasedAcrossCheckoutsAsync(limit, ct);

        public Task<IReadOnlyList<CommitLeaseRecord>> ListAllAsync(CancellationToken ct = default)
            => inner.ListAllAsync(ct);
    }

    private sealed class ManualTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now = _now.Add(by);
    }
}
