using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using Eling.Core.Git;
using Eling.Core.CommitLease;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

namespace Eling.Backend.Mcp.Tools;

/// <summary>
/// Lists the live commit leases on the current checkout: the slot another
/// sibling chat holds when it is about to commit. Read-only; stale leases are
/// swept by the store as part of the read. Optionally filtered by branch.
/// </summary>
/// <remarks>
/// Checkout is the working-copy root resolved via
/// <see cref="GitRepositoryResolver.ResolveWorkingCopyRoot"/> from the injected
/// working directory (default <see cref="Directory.GetCurrentDirectory"/>), so
/// the list is scoped to the working copy the caller runs in. A linked
/// worktree keys by the worktree path, distinct from the main checkout. A
/// path outside every git repository falls back to the working directory
/// itself, keyed by that directory. Cross-worktree leases stay invisible
/// unless this caller is inside that worktree; the store's checkout
/// scoping is the isolation boundary.
/// </remarks>
[McpServerToolType]
public sealed class CommitListActiveTool
{
    private readonly ICommitLeaseStore _leaseStore;
    private readonly TimeProvider _timeProvider;
    private readonly string _cwd;
    private readonly ILogger<CommitListActiveTool>? _logger;

    public CommitListActiveTool(
        ICommitLeaseStore leaseStore,
        TimeProvider? timeProvider = null,
        string? cwd = null,
        ILogger<CommitListActiveTool>? logger = null)
    {
        _leaseStore = leaseStore;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _cwd = Path.GetFullPath(string.IsNullOrWhiteSpace(cwd) ? Directory.GetCurrentDirectory() : cwd);
        _logger = logger;
    }

    // Parameter names below use snake_case deliberately: the MCP host passes
    // C# parameter names through to the wire schema unchanged, and the
    // commit_* tool surface is snake_case end to end.
    [McpServerTool(Name = "commit_list_active"), Description("List the live commit leases on this checkout, optionally filtered by branch. Returns an object with a `leases` array of { holder_session_id, since, lease_age_seconds, intent, paths }; an empty `leases` array means no session holds a live lease. Call this before staging to see whether a sibling chat owns the commit slot. commit_lease to take it, commit_release once the commit lands.")]
    public async Task<string> ListActiveAsync(
        [Description("Only list leases on this branch. Optional; omit to list every branch of this checkout.")] string? branch = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var checkout = GitRepositoryResolver.ResolveWorkingCopyRoot(_cwd) ?? _cwd;
            var branchFilter = string.IsNullOrWhiteSpace(branch) ? null : branch.Trim();
            var now = _timeProvider.GetUtcNow();
            var leases = await _leaseStore.ListActiveAsync(checkout, branchFilter, cancellationToken);
            var slotRows = leases
                .Where(l => l.Kind == CommitLeaseKind.Commit)
                .Select(l => new CommitListActiveItem
                {
                    HolderSessionId = l.SessionId,
                    Since = l.Since,
                    LeaseAgeSeconds = (long)Math.Max(0, (now - l.Since).TotalSeconds),
                    Intent = l.Intent,
                    Paths = l.Paths,
                })
                .ToArray();
            return JsonSerializer.Serialize(new CommitListActiveResult { Leases = slotRows });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The caller cancelled: propagate so the turn aborts normally.
            _logger?.LogDebug("commit_list_active was cancelled by the caller (branch {Branch}).", branch);
            throw;
        }
        catch (Exception ex)
        {
            // The store's gate wait and its file I/O both throw; the caller gets
            // the repo's compact error shape, not an opaque host-level failure.
            _logger?.LogWarning(ex, "commit_list_active failed (branch {Branch}).", branch);
            return JsonSerializer.Serialize(new CommitListActiveError($"Could not list the commit leases: {ex.Message}"));
        }
    }

    private sealed record CommitListActiveResult
    {
        [JsonPropertyName("leases")]
        public required CommitListActiveItem[] Leases { get; init; }
    }

    private sealed record CommitListActiveItem
    {
        [JsonPropertyName("holder_session_id")]
        public required string HolderSessionId { get; init; }

        [JsonPropertyName("since")]
        public required DateTimeOffset Since { get; init; }

        [JsonPropertyName("lease_age_seconds")]
        public required long LeaseAgeSeconds { get; init; }

        [JsonPropertyName("intent")]
        public required string? Intent { get; init; }

        [JsonPropertyName("paths")]
        public required string[]? Paths { get; init; }
    }

    private sealed record CommitListActiveError(
        [property: JsonPropertyName("error")]
        string Error);
}
