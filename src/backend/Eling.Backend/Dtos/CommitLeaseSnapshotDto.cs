using System.Text.Json.Serialization;

using Eling.Core.CommitLease;

namespace Eling.Backend.Dtos;

/// <summary>
/// Wire shape of the commit-lease snapshot: the same payload
/// <c>GET /api/commit-lease/active</c> answers with and every
/// <c>/api/events/commit-lease</c> SSE frame carries. Property names are
/// snake_case end to end (like the lease tool surface), so every name is
/// pinned with <see cref="JsonPropertyNameAttribute"/>. A full state
/// transfer, not a delta: a subscriber renders frames as-is. <see cref="Branch"/>
/// is null on the unfiltered snapshot ("all branches of this checkout"); the
/// recently-released history is filtered to the same branch when one is given.
/// Stale leases never appear — the store sweeps them into recently-released
/// on read. <see cref="Checkout"/> is null when the snapshot spans checkouts.
/// </summary>
public sealed record CommitLeaseSnapshotDto
{
    private static readonly StringComparison BranchComparison
        = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    public const int RecentlyReleasedLimit = 20;

    [JsonPropertyName("checkout")]
    public required string? Checkout { get; init; }

    [JsonPropertyName("branch")]
    public required string? Branch { get; init; }

    [JsonPropertyName("commit_leases")]
    public required IReadOnlyList<CommitLeaseSummaryDto> CommitLeases { get; init; }

    [JsonPropertyName("recently_released")]
    public required IReadOnlyList<CommitLeaseReleasedDto> RecentlyReleased { get; init; }

    public static CommitLeaseSnapshotDto From(
        string? checkout,
        string? branch,
        IReadOnlyList<CommitLeaseRecord> active,
        IReadOnlyList<CommitLeaseRecord> recentlyReleased,
        DateTimeOffset now)
    {
        var leases = active
            .Select(l => new CommitLeaseSummaryDto
            {
                LeaseId = l.LeaseId,
                SessionId = l.SessionId,
                Branch = l.Branch,
                Since = l.Since,
                LastHeartbeat = l.LastHeartbeat,
                LeaseAgeSeconds = (now - l.Since).TotalSeconds,
                TtlSeconds = l.TtlSeconds,
                Paths = l.Paths,
                Intent = l.Intent,
                Checkout = l.Checkout,
            })
            .ToList();

        var released = recentlyReleased
            .Where(l => string.IsNullOrWhiteSpace(branch)
                || string.Equals(l.Branch, branch, BranchComparison))
            .Select(l => new CommitLeaseReleasedDto
            {
                LeaseId = l.LeaseId,
                SessionId = l.SessionId,
                Since = l.Since,
                ReleasedAt = l.ReleasedAt,
                ReleaseReason = l.ReleaseReason,
                Intent = l.Intent,
                Paths = l.Paths,
                Checkout = l.Checkout,
            })
            .ToList();

        return new CommitLeaseSnapshotDto
        {
            Checkout = checkout,
            Branch = branch,
            CommitLeases = leases,
            RecentlyReleased = released,
        };
    }

    public static async Task<CommitLeaseSnapshotDto> BuildAsync(
        ICommitLeaseStore leaseStore,
        string? checkout,
        string? branch,
        TimeProvider clock,
        CancellationToken cancellationToken)
    {
        if (checkout is null)
        {
            return await BuildAcrossCheckoutsAsync(leaseStore, branch, clock, cancellationToken).ConfigureAwait(false);
        }

        var active = await leaseStore.ListActiveAsync(checkout, branch, cancellationToken).ConfigureAwait(false);
        var released = await leaseStore.ListRecentlyReleasedAsync(checkout, RecentlyReleasedLimit, cancellationToken).ConfigureAwait(false);
        return From(checkout, branch, active, released, clock.GetUtcNow());
    }

    public static async Task<CommitLeaseSnapshotDto> BuildAcrossCheckoutsAsync(
        ICommitLeaseStore leaseStore,
        string? branch,
        TimeProvider clock,
        CancellationToken cancellationToken)
    {
        var active = await leaseStore.ListActiveAcrossCheckoutsAsync(branch, cancellationToken).ConfigureAwait(false);
        var released = await leaseStore.ListRecentlyReleasedAcrossCheckoutsAsync(RecentlyReleasedLimit, cancellationToken).ConfigureAwait(false);
        return From(checkout: null, branch, active, released, clock.GetUtcNow());
    }

}
