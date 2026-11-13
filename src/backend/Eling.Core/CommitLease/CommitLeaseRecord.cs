namespace Eling.Core.CommitLease;

/// <summary>
/// A single lease on shared work. A lease is live while
/// <c>now - LastHeartbeat &lt; TtlSeconds</c>; at or beyond the TTL boundary it
/// is stale and drops from the active list into the recently-released list
/// with reason <c>stale</c>. <see cref="ReleasedAt"/> and
/// <see cref="ReleaseReason"/> are set only once the lease leaves the active
/// list (<c>explicit</c>, <c>stale</c>, <c>post-commit</c>, <c>forced</c>).
/// </summary>
public sealed record CommitLeaseRecord
{
    public required string LeaseId { get; init; }

    public required string SessionId { get; init; }

    public required CommitLeaseKind Kind { get; init; }

    public required string Checkout { get; init; }

    public required string? Branch { get; init; }

    public required DateTimeOffset Since { get; init; }

    public required DateTimeOffset LastHeartbeat { get; init; }

    public required int TtlSeconds { get; init; }

    public required string[]? Paths { get; init; }

    public required string? Intent { get; init; }

    public DateTimeOffset? ReleasedAt { get; init; }

    public string? ReleaseReason { get; init; }
}
