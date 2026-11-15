using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using Eling.Core.CommitLease;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

namespace Eling.Backend.Mcp.Tools;

/// <summary>
/// Releases every commit-kind lease owned by the calling session, across all
/// checkouts: moves them to the recently-released list with reason
/// <c>explicit</c>. Called once the commit lands (or the session stops trying
/// to commit). Release is session-scoped; there is no checkout filter.
/// </summary>
[McpServerToolType]
public sealed class CommitReleaseTool
{
    private readonly ICommitLeaseStore _leaseStore;
    private readonly ILogger<CommitReleaseTool>? _logger;

    public CommitReleaseTool(
        ICommitLeaseStore leaseStore,
        ILogger<CommitReleaseTool>? logger = null)
    {
        _leaseStore = leaseStore;
        _logger = logger;
    }

    // Parameter names below use snake_case deliberately: the MCP host passes
    // C# parameter names through to the wire schema unchanged, and the
    // commit_* tool surface is snake_case end to end.
    [McpServerTool(Name = "commit_release"), Description("Release all commit leases owned by this session, across checkouts. Call this once the commit lands (or when abandoning the attempt) so sibling chats can take the slot. Returns { released: true } when a lease was held and removed, { released: false } when the session holds no commit lease, or { error } when a lease is held but the supplied lease_token does not match it. lease_token is required whenever the session got one back from commit_lease (new leases always carry one). Before releasing, unstage YOUR OWN staged changes (git restore --staged <paths> or git reset -- <paths>): another chat may be waiting on this lease and would sweep your half-staged work into its commit - the index is shared, so staged files ride the next commit whoever makes it. Never unstage another session's files; if another agent's work is staged, leave it alone and wait.")]
    public async Task<string> ReleaseAsync(
        [Description("The caller's stable identifier. Any non-blank string of at least 24 characters you choose - a slug, a UUID, or your host's session id; pass the same value on every call. If your identifier carries a date, put the date FIRST (e.g. 2026-11-15-...), never last, so plain lexical sorting matches chronological order. The format is not validated.")] string caller_id,
        [Description("Token issued by commit_lease at claim time, required to release a lease that carries one (new leases always do). Legacy leases without one release without this parameter.")] string? lease_token = null,
        CancellationToken cancellationToken = default)
    {
        if (!CommitLeaseToolHelpers.IsCallerIdPlausible(caller_id))
        {
            return Serialize(new CommitReleaseError(CommitLeaseToolHelpers.CallerIdTooShortMessage(caller_id)));
        }

        try
        {
            var released = await _leaseStore.ReleaseAsync(caller_id.Trim(), CommitLeaseKind.Commit, lease_token, cancellationToken);
            if (!released)
            {
                // Distinguish "nothing to release" from "caller holds token-carrying leases that were not released"
                var mine = (await _leaseStore.ListActiveAcrossCheckoutsAsync(null, cancellationToken))
                    .Where(l => string.Equals(l.CallerId, caller_id.Trim(), StringComparison.Ordinal)
                        && l.Kind == CommitLeaseKind.Commit
                        && !string.IsNullOrWhiteSpace(l.LeaseToken))
                    .ToList();
                if (mine.Count > 0)
                {
                    return Serialize(new CommitReleaseError(
                        string.IsNullOrWhiteSpace(lease_token)
                            ? "this session holds leases with a lease_token issued at claim time - pass it to commit_release (or re-take the lease via commit_lease to get it again)."
                            : "lease_token mismatch: this session's lease(s) were not released."));
                }
            }
            return Serialize(new CommitReleaseSuccess(released));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The caller cancelled: propagate so the turn aborts normally.
            _logger?.LogDebug("commit_release was cancelled by the caller (session {CallerId}).", caller_id);
            throw;
        }
        catch (Exception ex)
        {
            // The store's gate wait and its file I/O both throw; the caller gets
            // the repo's compact error shape, not an opaque host-level failure.
            _logger?.LogWarning(ex, "commit_release failed for session {CallerId}.", caller_id);
            return Serialize(new CommitReleaseError($"Could not release the commit lease: {ex.Message}"));
        }
    }

    private static string Serialize<T>(T value)
        => JsonSerializer.Serialize(value);

    private sealed record CommitReleaseSuccess(
        [property: JsonPropertyName("released")]
        bool Released);

    private sealed record CommitReleaseError(
        [property: JsonPropertyName("error")]
        string Error);
}
