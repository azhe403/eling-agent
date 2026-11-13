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
    [McpServerTool(Name = "commit_release"), Description("Release all commit leases owned by this session, across checkouts. Call this once the commit lands (or when abandoning the attempt) so sibling chats can take the slot. Returns { released: true } when a lease was held and removed, { released: false } when the session holds no commit lease.")]
    public async Task<string> ReleaseAsync(
        [Description("The calling session's id. Must be at least 32 characters.")] string session_id,
        CancellationToken cancellationToken = default)
    {
        if (!CommitLeaseToolHelpers.IsSessionIdPlausible(session_id))
        {
            return Serialize(new CommitReleaseError(CommitLeaseToolHelpers.SessionIdTooShortMessage(session_id)));
        }

        try
        {
            var released = await _leaseStore.ReleaseAsync(session_id.Trim(), CommitLeaseKind.Commit, cancellationToken);
            return Serialize(new CommitReleaseSuccess(released));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The caller cancelled: propagate so the turn aborts normally.
            _logger?.LogDebug("commit_release was cancelled by the caller (session {SessionId}).", session_id);
            throw;
        }
        catch (Exception ex)
        {
            // The store's gate wait and its file I/O both throw; the caller gets
            // the repo's compact error shape, not an opaque host-level failure.
            _logger?.LogWarning(ex, "commit_release failed for session {SessionId}.", session_id);
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
