namespace Eling.Core.CommitLease;

/// <summary>
/// Machine-global, JSON-backed lease store (<c>commit-leases.json</c> under the global agent
/// directory, never per-project). See <see cref="ICommitLeaseChangeNotifier"/>
/// for the change-notification contract.
/// </summary>
public interface ICommitLeaseStore
{
    /// <summary>
    /// Live leases for <paramref name="checkout"/> (and <paramref name="branch"/> when given). Same live rules as <see cref="ListActiveAcrossCheckoutsAsync"/> (stale leases are swept into recently-released as part of the read and never appear).
    /// </summary>
    Task<IReadOnlyList<CommitLeaseRecord>> ListActiveAsync(string checkout, string? branch, CancellationToken ct = default);

    /// <summary>
    /// Live leases across every checkout (and <paramref name="branch"/> when given).
    /// Same live rules as <see cref="ListActiveAsync"/> (stale leases are moved to the
    /// recently-released list with reason <c>stale</c> as part of the read and never
    /// appear) but with no checkout scoping, so a single read spans every checkout
    /// that shares the machine-global store. Returned in the same stored order
    /// <see cref="ListActiveAsync"/> produces today.
    /// </summary>
    Task<IReadOnlyList<CommitLeaseRecord>> ListActiveAcrossCheckoutsAsync(string? branch, CancellationToken ct = default);

    /// <summary>
    /// First-write-wins insert. Returns the existing live conflicting lease
    /// when one exists; otherwise stores <paramref name="candidate"/> and
    /// returns null. Never throws for a conflict.
    /// </summary>
    Task<CommitLeaseRecord?> TryLeaseAsync(CommitLeaseRecord candidate, CancellationToken ct = default);

    /// <summary>
    /// Removes every lease owned by <paramref name="callerId"/> (optionally
    /// filtered by <paramref name="kind"/>), moving them to the
    /// recently-released list with reason <c>explicit</c>. Returns true when
    /// at least one lease was removed.
    /// <para>
    /// If any of the owned leases carries a lease token issued at claim time,
    /// <paramref name="leaseToken"/> must match ALL such tokens (one shared
    /// token is issued per claim in this codebase; owned leases either all
    /// carry it or none do). A missing or mismatched token leaves those
    /// leases untouched and returns false.
    /// </para>
    /// </summary>
    Task<bool> ReleaseAsync(string callerId, CommitLeaseKind? kind, string? leaseToken = null, CancellationToken ct = default);

    /// <summary>
    /// Removes the one live lease identified by <paramref name="leaseId"/>,
    /// regardless of which caller owns it, moving it to the recently-released
    /// list with reason <c>forced</c>. Returns false when no live lease with
    /// that id exists (unknown id, already released — including an earlier
    /// <c>stale</c> sweep, which moves the lease out of the active list as
    /// part of any read/mutation). Unlike <see cref="HeartbeatAsync"/> this is
    /// not owner-scoped: it exists so the dashboard can break a commit slot
    /// open without asking the holding caller.
    /// </summary>
    Task<bool> ForceReleaseAsync(string leaseId, CancellationToken ct = default);

    /// <summary>
    /// Extends <see cref="CommitLeaseRecord.LastHeartbeat"/> to now, but only when
    /// the lease identified by <paramref name="leaseId"/> is owned by
    /// <paramref name="callerId"/> and, if the stored lease carries a lease
    /// token, <paramref name="leaseToken"/> matches it. When
    /// <paramref name="ttlSeconds"/> is supplied, the lease's
    /// <see cref="CommitLeaseRecord.TtlSeconds"/> is recalculated to that value
    /// in the same write, so a valid re-claim can give the agent more time.
    /// Returns false otherwise (and never revives a stale lease that has
    /// already been swept).
    /// </summary>
    Task<bool> HeartbeatAsync(string leaseId, string callerId, string? leaseToken = null, int? ttlSeconds = null, CancellationToken ct = default);

    /// <summary>
    /// Released leases for <paramref name="checkout"/>, newest first, capped
    /// at <paramref name="limit"/>.
    /// </summary>
    Task<IReadOnlyList<CommitLeaseRecord>> ListRecentlyReleasedAsync(string checkout, int limit = 20, CancellationToken ct = default);

    /// <summary>
    /// Released leases across every checkout, newest first, capped at
    /// <paramref name="limit"/>. The cap applies AFTER the global ordering —
    /// a single global window of <paramref name="limit"/>, not
    /// <paramref name="limit"/> per checkout.
    /// </summary>
    Task<IReadOnlyList<CommitLeaseRecord>> ListRecentlyReleasedAcrossCheckoutsAsync(int limit, CancellationToken ct = default);

    /// <summary>
    /// Raw dump of both collections (active then recently-released), as they
    /// are on disk — intended for tests and diagnostics. Does not sweep
    /// stale leases and does not notify.
    /// </summary>
    Task<IReadOnlyList<CommitLeaseRecord>> ListAllAsync(CancellationToken ct = default);
}
