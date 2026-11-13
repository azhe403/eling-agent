using System.Text.Json;
using Microsoft.Extensions.Logging;

using Eling.Core.Serialization;

namespace Eling.Core.CommitLease;

/// <summary>
/// JSON-backed lease store, mirroring
/// <c>Eling.Core.Database.JsonDatabaseConnectionStore</c>: a cross-process
/// mutex named <c>Global\eling-commit-leases</c> guards an atomic <c>.tmp</c> write,
/// and unreadable data is swallowed (logged) in favour of an empty store.
/// The file holds two collections: active leases and a recently-released
/// history capped at 50 entries per checkout. A lease is live while
/// <c>now - LastHeartbeat &lt; TtlSeconds</c>; at the TTL boundary it is
/// stale. Reads and mutations sweep stale leases into the recently-released
/// list with reason <c>stale</c>; there is no background sweeper.
/// </summary>
public sealed class JsonCommitLeaseStore : ICommitLeaseStore, IDisposable
{
    private const string MutexName = @"Global\eling-commit-leases";
    private const string TempSuffix = ".tmp";

    /// <summary>Recently-released history is kept per checkout.</summary>
    private const int RecentlyReleasedCapPerCheckout = 50;

    private static readonly TimeSpan GateTimeout = TimeSpan.FromSeconds(5);

    /// <summary>Path comparisons follow the host file system's case rules.</summary>
    private static readonly StringComparison PathComparison
        = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private static readonly StringComparison BranchComparison
        = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private static readonly Mutex Gate = new(initiallyOwned: false, MutexName);

    private readonly string _filePath;
    private readonly ICommitLeaseChangeNotifier? _notifier;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<JsonCommitLeaseStore>? _logger;

    public JsonCommitLeaseStore(
        string filePath,
        ICommitLeaseChangeNotifier? notifier = null,
        TimeProvider? timeProvider = null,
        ILogger<JsonCommitLeaseStore>? logger = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        _filePath = Path.GetFullPath(filePath);
        _notifier = notifier;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _logger = logger;
    }

    public async Task<IReadOnlyList<CommitLeaseRecord>> ListActiveAsync(string checkout, string? branch, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(checkout)) return [];
        return await ListActiveCoreAsync(checkout, branch, ct);
    }

    public async Task<IReadOnlyList<CommitLeaseRecord>> ListActiveAcrossCheckoutsAsync(string? branch, CancellationToken ct = default)
        => await ListActiveCoreAsync(checkout: null, branch, ct);

    /// <summary>
    /// The shared read path behind <see cref="ListActiveAsync"/> and
    /// <see cref="ListActiveAcrossCheckoutsAsync"/>: sweep stale leases, then
    /// filter by branch and (when <paramref name="checkout"/> is given) by
    /// checkout. A null checkout yields every live lease across the store.
    /// </summary>
    private async Task<IReadOnlyList<CommitLeaseRecord>> ListActiveCoreAsync(string? checkout, string? branch, CancellationToken ct)
    {
        List<CommitLeaseRecord>? result = null;
        var changed = false;
        try
        {
            WithGate(() =>
            {
                var data = LoadCore();
                var now = _timeProvider.GetUtcNow();
                changed = SweepStaleLocked(data, now);
                if (changed) SaveCore(data);
                result = FilterActiveLocked(data, checkout, branch);
            });
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to list active leases from {Path}.", _filePath);
            return [];
        }

        if (changed) await NotifyChangedAsync(ct);
        return result ?? [];
    }

    private static List<CommitLeaseRecord> FilterActiveLocked(LeaseStoreData data, string? checkout, string? branch)
        => data.Active
            .Where(l => checkout is null || CheckoutEquals(l.Checkout, checkout))
            .Where(l => string.IsNullOrWhiteSpace(branch)
                || string.Equals(l.Branch, branch, BranchComparison))
            .ToList();

    public async Task<CommitLeaseRecord?> TryLeaseAsync(CommitLeaseRecord candidate, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        CommitLeaseRecord? conflict = null;
        var changed = false;
        WithGate(() =>
        {
            var data = LoadCore();
            var now = _timeProvider.GetUtcNow();
            changed = SweepStaleLocked(data, now);
            var existing = data.Active.FirstOrDefault(l => ConflictsWith(l, candidate));
            if (existing is not null)
            {
                if (changed) SaveCore(data);
                conflict = existing;
                return;
            }

            data.Active.Add(candidate);
            SaveCore(data);
            changed = true;
        });

        if (changed) await NotifyChangedAsync(ct);
        return conflict;
    }

    public async Task<bool> ReleaseAsync(string sessionId, CommitLeaseKind? kind, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(sessionId)) return false;
        var releasedAny = false;
        var changed = false;
        WithGate(() =>
        {
            var data = LoadCore();
            var now = _timeProvider.GetUtcNow();
            changed = SweepStaleLocked(data, now);
            var owned = data.Active
                .Where(l => string.Equals(l.SessionId, sessionId, StringComparison.Ordinal))
                .Where(l => kind is null || l.Kind == kind)
                .ToList();
            if (owned.Count > 0)
            {
                foreach (var lease in owned)
                {
                    data.RecentlyReleased.Add(lease with { ReleasedAt = now, ReleaseReason = "explicit" });
                    data.Active.Remove(lease);
                }

                TrimRecentlyReleasedLocked(data);
                changed = true;
            }

            if (changed) SaveCore(data);
            releasedAny = owned.Count > 0;
        });

        if (changed) await NotifyChangedAsync(ct);
        return releasedAny;
    }

    public async Task<bool> ForceReleaseAsync(string leaseId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(leaseId)) return false;
        var releasedTarget = false;
        var changed = false;
        WithGate(() =>
        {
            var data = LoadCore();
            var now = _timeProvider.GetUtcNow();
            changed = SweepStaleLocked(data, now);
            var idx = data.Active.FindIndex(l => string.Equals(l.LeaseId, leaseId, StringComparison.Ordinal));
            if (idx >= 0)
            {
                data.RecentlyReleased.Add(data.Active[idx] with { ReleasedAt = now, ReleaseReason = "forced" });
                data.Active.RemoveAt(idx);
                TrimRecentlyReleasedLocked(data);
                changed = true;
                releasedTarget = true;
            }

            if (changed) SaveCore(data);
        });

        if (changed) await NotifyChangedAsync(ct);
        return releasedTarget;
    }

    public async Task<bool> HeartbeatAsync(string leaseId, string sessionId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(leaseId) || string.IsNullOrWhiteSpace(sessionId)) return false;
        var extended = false;
        var changed = false;
        WithGate(() =>
        {
            var data = LoadCore();
            var now = _timeProvider.GetUtcNow();
            changed = SweepStaleLocked(data, now);
            var idx = data.Active.FindIndex(l => string.Equals(l.LeaseId, leaseId, StringComparison.Ordinal));
            if (idx >= 0 && string.Equals(data.Active[idx].SessionId, sessionId, StringComparison.Ordinal))
            {
                data.Active[idx] = data.Active[idx] with { LastHeartbeat = now };
                extended = true;
                changed = true;
            }

            if (changed) SaveCore(data);
        });

        if (changed) await NotifyChangedAsync(ct);
        return extended;
    }

    public Task<IReadOnlyList<CommitLeaseRecord>> ListRecentlyReleasedAsync(string checkout, int limit = 20, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(checkout) || limit <= 0)
        {
            return Task.FromResult<IReadOnlyList<CommitLeaseRecord>>([]);
        }

        return ListRecentlyReleasedCore(checkout, limit);
    }

    public Task<IReadOnlyList<CommitLeaseRecord>> ListRecentlyReleasedAcrossCheckoutsAsync(int limit, CancellationToken ct = default)
    {
        if (limit <= 0)
        {
            return Task.FromResult<IReadOnlyList<CommitLeaseRecord>>([]);
        }

        return ListRecentlyReleasedCore(checkout: null, limit);
    }

    /// <summary>
    /// The shared read path behind <see cref="ListRecentlyReleasedAsync"/> and
    /// <see cref="ListRecentlyReleasedAcrossCheckoutsAsync"/>: newest first by
    /// release timestamp, capped at <paramref name="limit"/> AFTER the global
    /// ordering. A null checkout yields the single global window across the
    /// store rather than limit entries per checkout.
    /// </summary>
    private Task<IReadOnlyList<CommitLeaseRecord>> ListRecentlyReleasedCore(string? checkout, int limit)
    {
        try
        {
            return Task.FromResult<IReadOnlyList<CommitLeaseRecord>>(
                WithGate(() => LoadCore().RecentlyReleased
                    .Where(l => checkout is null || CheckoutEquals(l.Checkout, checkout))
                    .OrderByDescending(l => l.ReleasedAt ?? DateTimeOffset.MinValue)
                    .Take(limit)
                    .ToList()));
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to list recently-released leases from {Path}.", _filePath);
            return Task.FromResult<IReadOnlyList<CommitLeaseRecord>>([]);
        }
    }

    public Task<IReadOnlyList<CommitLeaseRecord>> ListAllAsync(CancellationToken ct = default)
    {
        try
        {
            return Task.FromResult<IReadOnlyList<CommitLeaseRecord>>(
                WithGate(() =>
                {
                    var data = LoadCore();
                    return data.Active.Concat(data.RecentlyReleased).ToList();
                }));
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to list all leases from {Path}.", _filePath);
            return Task.FromResult<IReadOnlyList<CommitLeaseRecord>>([]);
        }
    }

    private async Task NotifyChangedAsync(CancellationToken ct)
    {
        if (_notifier is null) return;
        try
        {
            await _notifier.NotifyAsync(ct);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Lease change notifier failed; continuing.");
        }
    }

    private void WithGate(Action action)
    {
        EnterGate();
        try
        {
            action();
        }
        finally
        {
            Gate.ReleaseMutex();
        }
    }

    private T WithGate<T>(Func<T> action)
    {
        EnterGate();
        try
        {
            return action();
        }
        finally
        {
            Gate.ReleaseMutex();
        }
    }

    private void EnterGate()
    {
        try
        {
            if (!Gate.WaitOne(GateTimeout))
            {
                throw new TimeoutException(
                    $"Timed out after {GateTimeout} waiting for the lease store gate.");
            }
        }
        catch (AbandonedMutexException ex)
        {
            _logger?.LogWarning(ex, "Acquired the lease store gate after it was abandoned; continuing.");
        }
    }

    private LeaseStoreData LoadCore()
    {
        if (!File.Exists(_filePath)) return new LeaseStoreData();
        try
        {
            var json = File.ReadAllText(_filePath);
            return JsonSerializer.Deserialize<LeaseStoreData>(json, JsonDefaults.Shared) ?? new LeaseStoreData();
        }
        catch (JsonException ex)
        {
            _logger?.LogWarning(ex, "Ignoring unreadable lease store at {Path}.", _filePath);
            return new LeaseStoreData();
        }
    }

    private void SaveCore(LeaseStoreData data)
    {
        var dir = Path.GetDirectoryName(_filePath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        var tempPath = _filePath + TempSuffix;
        var json = JsonSerializer.Serialize(data, JsonDefaults.Shared);
        File.WriteAllText(tempPath, json);
        File.Move(tempPath, _filePath, overwrite: true);
    }

    /// <summary>
    /// Moves stale leases from the active collection into recently-released
    /// with reason <c>stale</c>. Returns true when anything moved.
    /// </summary>
    private bool SweepStaleLocked(LeaseStoreData data, DateTimeOffset now)
    {
        var stale = data.Active.Where(l => !IsLive(l, now)).ToList();
        if (stale.Count == 0) return false;
        foreach (var lease in stale)
        {
            data.RecentlyReleased.Add(lease with { ReleasedAt = now, ReleaseReason = "stale" });
        }

        data.Active.RemoveAll(l => !IsLive(l, now));
        TrimRecentlyReleasedLocked(data);
        return true;
    }

    private static void TrimRecentlyReleasedLocked(LeaseStoreData data)
    {
        data.RecentlyReleased = data.RecentlyReleased
            .GroupBy(l => TrimSeparators(l.Checkout), OperatingSystem.IsWindows()
                ? StringComparer.OrdinalIgnoreCase
                : StringComparer.Ordinal)
            .SelectMany(g => g
                .OrderByDescending(l => l.ReleasedAt ?? DateTimeOffset.MinValue)
                .Take(RecentlyReleasedCapPerCheckout))
            .ToList();
    }

    private static bool IsLive(CommitLeaseRecord lease, DateTimeOffset now)
        => now - lease.LastHeartbeat < TimeSpan.FromSeconds(lease.TtlSeconds);

    private static bool ConflictsWith(CommitLeaseRecord existing, CommitLeaseRecord candidate)
    {
        if (!CheckoutEquals(existing.Checkout, candidate.Checkout)) return false;
        if (existing.Kind != candidate.Kind) return false;
        return candidate.Kind switch
        {
            CommitLeaseKind.Commit => string.Equals(existing.Branch, candidate.Branch, BranchComparison),
            CommitLeaseKind.File => PathsIntersect(existing.Paths, candidate.Paths),
            _ => false,
        };
    }

    private static bool PathsIntersect(string[]? existing, string[]? candidate)
    {
        if (existing is null || candidate is null) return false;
        var left = existing.Select(NormalizePath).Where(p => p.Length > 0).ToList();
        var right = candidate.Select(NormalizePath).Where(p => p.Length > 0).ToList();
        return left.Any(l => right.Any(r => PathContains(l, r) || PathContains(r, l)));
    }

    /// <summary>True when <paramref name="container"/> is equal to or a parent directory of <paramref name="path"/>.</summary>
    private static bool PathContains(string container, string path)
        => string.Equals(container, path, PathComparison)
            || path.StartsWith(container + "/", PathComparison);

    /// <summary>
    /// Normalises a possibly-relative path for comparison: forward slashes,
    /// no leading "./", no empty or dot segments, no trailing slash. Case
    /// comparison happens at the <see cref="PathContains"/> site.
    /// </summary>
    private static string NormalizePath(string path)
    {
        var parts = path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var kept = parts.Where(p => p != ".").ToArray();
        return string.Join("/", kept);
    }

    /// <summary>Normalises path separators to '/' so mixed-style checkouts (\ vs /) compare equal; case rules follow the host OS.</summary>
    private static bool CheckoutEquals(string? a, string? b)
        => string.Equals(TrimSeparators(a), TrimSeparators(b), PathComparison);

    /// <summary>Null-tolerant: a hand-edited file could hold a record with no checkout; it simply compares unequal.</summary>
    private static string TrimSeparators(string? path)
        => path?.Replace('\\', '/').TrimEnd('/', '\\') ?? "";

    public void Dispose()
    {
    }

    private sealed class LeaseStoreData
    {
        public List<CommitLeaseRecord> Active { get; set; } = [];

        public List<CommitLeaseRecord> RecentlyReleased { get; set; } = [];
    }
}
