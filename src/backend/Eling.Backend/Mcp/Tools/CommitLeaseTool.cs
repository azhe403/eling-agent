using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using Eling.Core.Git;
using Eling.Core.CommitLease;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

namespace Eling.Backend.Mcp.Tools;

/// <summary>
/// Takes the commit lease for the calling session on the current checkout and
/// branch: the slot that tells sibling chats on the same working tree not to
/// sweep this session's uncommitted work into a commit. First-write-wins; a
/// second lessee soft-fails and receives the holder's session, lease age and
/// intent instead of an error.
/// </summary>
/// <remarks>
/// Checkout is the working-copy root resolved via
/// <see cref="GitRepositoryResolver.ResolveWorkingCopyRoot"/> from the injected
/// working directory (default <see cref="Directory.GetCurrentDirectory"/>), so
/// leases taken from any subdirectory of a working copy key to that
/// working copy's root. A linked worktree keys by the worktree path,
/// distinct from the main checkout. A path outside every git repository
/// falls back to the working directory itself, keyed by that directory.
/// <para>
/// <c>branch</c> is required (non-blank): callers pass the current branch
/// (e.g. from <c>git branch --show-current</c>). The store keys commit leases
/// by checkout + branch, so a blank branch would collapse every branch into
/// one slot.
/// </para>
/// </remarks>
[McpServerToolType]
public sealed class CommitLeaseTool
{
    /// <summary>Liveness window for a commit lease; callers heartbeat to keep it alive.</summary>
    private const int DefaultTtlSeconds = 300;

    private readonly ICommitLeaseStore _leaseStore;
    private readonly TimeProvider _timeProvider;
    private readonly string _cwd;
    private readonly ILogger<CommitLeaseTool>? _logger;

    public CommitLeaseTool(
        ICommitLeaseStore leaseStore,
        TimeProvider? timeProvider = null,
        string? cwd = null,
        ILogger<CommitLeaseTool>? logger = null)
    {
        _leaseStore = leaseStore;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _cwd = Path.GetFullPath(string.IsNullOrWhiteSpace(cwd) ? Directory.GetCurrentDirectory() : cwd);
        _logger = logger;
    }

    // Parameter names below use snake_case deliberately: the MCP host passes
    // C# parameter names through to the wire schema unchanged, and the
    // commit_* tool surface is snake_case end to end.
    [McpServerTool(Name = "commit_lease"), Description("Take the commit lease on this checkout and branch before staging or committing. branch is required (pass the current branch, e.g. from 'git branch --show-current'). Returns { acquired: true, lease_id, since } on success, or { acquired: false, holder_session_id, lease_age_seconds, intent } when another session holds a live lease - a soft-fail, not an error: do not stage; report the holder and stop (or wait). Call commit_release once the commit lands.")]
    public async Task<string> LeaseAsync(
        [Description("The calling session's id. Must be at least 32 characters.")] string session_id,
        [Description("Current branch of the checkout (required, non-blank), e.g. 'main'.")] string branch,
        [Description("One-line summary of the planned commit. Optional.")] string? intent = null,
        [Description("Paths the commit is expected to touch. Optional.")] string[]? paths = null,
        CancellationToken cancellationToken = default)
    {
        if (!CommitLeaseToolHelpers.IsSessionIdPlausible(session_id))
        {
            return Serialize(new CommitLeaseError { Acquired = false, Error = CommitLeaseToolHelpers.SessionIdTooShortMessage(session_id) });
        }

        if (string.IsNullOrWhiteSpace(branch))
        {
            return Serialize(new CommitLeaseError { Acquired = false, Error = "branch is required and must be non-blank; pass the current branch (e.g. 'git branch --show-current')." });
        }

        try
        {
            var checkout = GitRepositoryResolver.ResolveWorkingCopyRoot(_cwd) ?? _cwd;
            var now = _timeProvider.GetUtcNow();
            var leaseCandidate = new CommitLeaseRecord {
                LeaseId =  Ulid.NewUlid().ToString(),
                SessionId =  session_id.Trim(),
                Kind =  CommitLeaseKind.Commit,
                Checkout =  checkout,
                Branch =  branch.Trim(),
                Since =  now,
                LastHeartbeat =  now,
                TtlSeconds =  DefaultTtlSeconds,
                Paths =  NormalizePaths(paths),
                Intent =  NormalizeIntent(intent)};

            var existingLease = await _leaseStore.TryLeaseAsync(leaseCandidate, cancellationToken);
            if (existingLease is null)
            {
                return Serialize(new CommitLeaseSuccess
                {
                    Acquired = true,
                    LeaseId = leaseCandidate.LeaseId,
                    Since = now,
                });
            }

            var ageSeconds = (long)Math.Max(0, (now - existingLease.Since).TotalSeconds);
            return Serialize(new CommitLeaseHeld
            {
                Acquired = false,
                HolderSessionId = existingLease.SessionId,
                LeaseAgeSeconds = ageSeconds,
                Intent = existingLease.Intent,
            });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The caller cancelled: propagate so the turn aborts normally.
            _logger?.LogDebug("commit_lease was cancelled by the caller (session {SessionId}, branch {Branch}).", session_id, branch);
            throw;
        }
        catch (Exception ex)
        {
            // The store's gate wait and its file I/O both throw; the caller gets
            // the repo's compact error shape, not an opaque host-level failure.
            _logger?.LogWarning(ex, "commit_lease failed for session {SessionId} on branch {Branch}.", session_id, branch);
            return Serialize(new CommitLeaseError { Acquired = false, Error = $"Could not take the commit lease: {ex.Message}" });
        }
    }

    private static string[]? NormalizePaths(string[]? paths)
    {
        if (paths is null) return null;
        var nonBlankPaths = paths
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => p.Trim())
            .ToArray();
        return nonBlankPaths.Length == 0 ? null : nonBlankPaths;
    }

    private static string? NormalizeIntent(string? intent)
        => string.IsNullOrWhiteSpace(intent) ? null : intent.Trim();

    private static string Serialize<T>(T value)
        => JsonSerializer.Serialize(value);

    private sealed record CommitLeaseSuccess
    {
        [JsonPropertyName("acquired")]
        public required bool Acquired { get; init; }

        [JsonPropertyName("lease_id")]
        public required string LeaseId { get; init; }

        [JsonPropertyName("since")]
        public required DateTimeOffset Since { get; init; }
    }

    private sealed record CommitLeaseHeld
    {
        [JsonPropertyName("acquired")]
        public required bool Acquired { get; init; }

        [JsonPropertyName("holder_session_id")]
        public required string HolderSessionId { get; init; }

        [JsonPropertyName("lease_age_seconds")]
        public required long LeaseAgeSeconds { get; init; }

        [JsonPropertyName("intent")]
        public required string? Intent { get; init; }
    }

    private sealed record CommitLeaseError
    {
        [JsonPropertyName("acquired")]
        public required bool Acquired { get; init; }

        [JsonPropertyName("error")]
        public required string Error { get; init; }
    }
}
