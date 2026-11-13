using System.Text.Json;
using System.Text.Json.Serialization;
using Eling.Backend.Bootstrap;
using Eling.Backend.Dtos;
using Eling.Backend.Services;
using Eling.Core.Audit;
using Eling.Core.Git;
using Eling.Core.CommitLease;
using Microsoft.AspNetCore.Mvc;

namespace Eling.Backend.Controllers;

/// <summary>
/// The commit-lease panel's control surface (owner hosts only — the routes
/// are mapped exclusively when this instance owns the dashboard port).
/// <para>
/// Force-release exists for exactly two situations no session can resolve
/// itself: a wedged lease whose owner died mid-commit, and a user who wants
/// the slot open now. It is deliberately NOT an MCP tool — the force path is
/// a human-dashboard privilege, not something agent sessions may invoke on
/// each other.
/// </para>
/// </summary>
[ApiController]
[Route("api/commit-lease")]
public sealed class CommitLeaseController(
    ICommitLeaseStore leaseStore,
    ProjectContext context,
    ILogger<CommitLeaseController> logger,
    IServiceProvider services) : ControllerBase
{
    private const string ForceReleaseAction = "commit_lease_force_release";

    /// <summary>
    /// The lease snapshot. <paramref name="branch"/> filters both the live
    /// leases and the recently-released history to that branch; omitting it
    /// yields every branch. Stale leases are absent (the store sweeps them
    /// on read). <paramref name="scope"/> narrows the snapshot: the default
    /// (and any value other than <c>all</c>) returns this checkout only;
    /// <c>scope=all</c> returns every live lease and the global
    /// recently-released window across every checkout of the machine-global
    /// store, with <c>checkout</c> = null — the mode the dashboard consumes.
    /// </summary>
    [HttpGet("active")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult<CommitLeaseSnapshotDto>> GetActiveAsync(
        [FromQuery] string? branch,
        [FromQuery] string? scope,
        CancellationToken cancellationToken)
    {
        var branchFilter = string.IsNullOrWhiteSpace(branch) ? null : branch.Trim();
        var snapshot = string.Equals(scope, "all", StringComparison.Ordinal)
            ? await CommitLeaseSnapshotDto.BuildAcrossCheckoutsAsync(
                leaseStore, branchFilter, TimeProvider.System, cancellationToken)
            : await CommitLeaseSnapshotDto.BuildAsync(
                leaseStore, Checkout(), branchFilter, TimeProvider.System, cancellationToken);
        return Ok(snapshot);
    }

    /// <summary>
    /// Force-releases that one live lease regardless of which session owns
    /// it, moving it to the recently-released list with reason
    /// <c>forced</c>. An id with no live lease on the target checkout —
    /// unknown, already released, or stale (swept on read) — answers 404 and
    /// releases nothing. <paramref name="checkout"/> selects the checkout the
    /// lease must belong to; omitted, this host's checkout applies and a
    /// lease taken in another checkout answers 404 and survives untouched.
    /// Audit and the response/SSE snapshot carry the machine-global view.
    /// </summary>
    [HttpPost("{leaseId}/release")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<CommitLeaseSnapshotDto>> ForceReleaseAsync(
        string leaseId,
        [FromQuery] string? checkout,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(leaseId))
        {
            return NotFound();
        }

        var targetCheckout = string.IsNullOrWhiteSpace(checkout) ? Checkout() : checkout.Trim();

        // Scope to the target checkout BEFORE the release: ForceReleaseAsync
        // is id-global, and a lease taken in another checkout must survive a
        // call that names a different one untouched. The active list for the
        // target checkout is swept of staleness, so a miss here means "not a
        // live lease of that checkout" exactly — no further probing needed
        // for the 404 contract.
        var activeLeases = await leaseStore.ListActiveAsync(targetCheckout, branch: null, cancellationToken);
        var targetLease = activeLeases.FirstOrDefault(l => string.Equals(l.LeaseId, leaseId, StringComparison.Ordinal));
        if (targetLease is null)
        {
            return NotFound();
        }

        var released = await leaseStore.ForceReleaseAsync(targetLease.LeaseId, cancellationToken);
        if (!released)
        {
            // Lost a race with another releaser between the two calls; the
            // 404 contract holds either way.
            return NotFound();
        }

        await WriteAuditAsync(targetLease, targetLease.Checkout, cancellationToken);

        var snapshot = await CommitLeaseSnapshotDto.BuildAcrossCheckoutsAsync(
            leaseStore, branch: null, TimeProvider.System, cancellationToken);

        // In-proc push for this host's SSE subscribers. Every browser renders
        // the machine-global frame, so this is the same payload the relayed
        // peer notifications carry. The shared store's loopback relay may
        // deliver a duplicate frame a few milliseconds later; both are full
        // snapshots, so last-write-wins converges.
        services.GetService<CommitLeaseBroadcaster>()?.Publish(snapshot);
        return Ok(snapshot);
    }

    private string Checkout()
        => GitRepositoryResolver.ResolveWorkingCopyRoot(context.Chain.Cwd) ?? context.Chain.Cwd;

    /// <summary>
    /// Change-trail entry for the force release: the lease's full identity
    /// plus an explicit <c>forced</c> marker, so filtering the trail for
    /// forced releases reads back as exactly what happened. Actor is the
    /// dashboard surface itself. A host built without audit wiring records
    /// nothing instead of failing the request (mirrors the codebase routes).
    /// </summary>
    private async Task WriteAuditAsync(CommitLeaseRecord lease, string checkout, CancellationToken ct)
    {
        var audit = services.GetService<IAuditLogger>();
        if (audit is null)
        {
            logger.LogDebug("No audit logger registered; skipping lease force-release audit entry.");
            return;
        }

        try
        {
            using var actor = AuditActorContext.Begin(new AuditActor("dashboard", "dashboard_web"));
            var payload = JsonSerializer.Serialize(new ForceReleaseAuditPayload
            {
                LeaseId = lease.LeaseId,
                SessionId = lease.SessionId,
                Checkout = checkout,
                Branch = lease.Branch,
                Paths = lease.Paths,
                Intent = lease.Intent,
                ReleaseReason = "forced",
            });

            await audit.RecordAsync(new AuditEvent
            {
                Trail = AuditTrail.Change,
                Category = AuditCategory.Runtime,
                Action = ForceReleaseAction,
                Target = lease.LeaseId,
                ProjectRoot = checkout,
                Outcome = AuditOutcome.Success,
                Metadata = new AuditMetadata
                {
                    Content = payload,
                    Tags = ["commit-lease", "forced"],
                    Status = "forced",
                },
            }, ct);
        }
        catch (Exception ex)
        {
            // The lease is already released; failing the request over its
            // audit line would lie about the state change. Warn and move on.
            logger.LogWarning(ex, "Failed to write the audit entry for the forced release of lease {LeaseId}.", lease.LeaseId);
        }
    }

    private sealed record ForceReleaseAuditPayload
    {
        [JsonPropertyName("lease_id")]
        public required string LeaseId { get; init; }

        [JsonPropertyName("session_id")]
        public required string SessionId { get; init; }

        [JsonPropertyName("checkout")]
        public required string Checkout { get; init; }

        [JsonPropertyName("branch")]
        public required string? Branch { get; init; }

        [JsonPropertyName("paths")]
        public required string[]? Paths { get; init; }

        [JsonPropertyName("intent")]
        public required string? Intent { get; init; }

        [JsonPropertyName("release_reason")]
        public required string ReleaseReason { get; init; }
    }
}
