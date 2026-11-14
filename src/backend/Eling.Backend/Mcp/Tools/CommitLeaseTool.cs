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
/// sweep this session's uncommitted work into a commit. The preflight conflict
/// check is folded into the claim itself, so one call answers both "is it safe"
/// and "may I stage". First-write-wins; a second lessee soft-fails and receives
/// the holder's session, lease age and intent instead of an error.
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
/// <c>branch</c> is optional: it is discovered via <c>git branch --show-current</c>
/// when omitted or blank. When the branch cannot be discovered (git missing,
/// detached HEAD, or a non-git directory) the fail-safe scope is every branch
/// of the checkout - a foreign live commit lease anywhere in the checkout marks
/// the verdict red and blocks the claim, exactly as for a discovered branch.
/// </para>
/// <para>
/// The <c>verdict</c> is <c>green</c> when no live foreign commit lease exists
/// on the scope and no foreign file lease intersects the requested paths;
/// otherwise <c>red</c>. Acquiring the slot does not itself stage anything.
/// A <c>verdict</c> of <c>red</c> on an acquired lease means foreign file
/// leases collide with the paths of this commit: you hold the slot, but stage
/// nothing until they clear (or stop and report). A <c>verdict</c> of
/// <c>red</c> with <c>acquired: false</c> means a foreign live commit lease
/// holds the slot: do not stage; stop and report the holder (or wait with
/// <c>commit_wait</c>, then call this tool again).
/// </para>
/// <para>
/// Re-claiming your own live lease is idempotent: it succeeds with the same
/// lease_id rather than reporting a conflict, so a retried commit attempt is not
/// blocked by its own slot. <c>staged_without_lease</c> is a warning, not a hard
/// error: it is true when files are staged while the caller holds no live commit
/// lease of its own.
/// </para>
/// </remarks>
[McpServerToolType]
public sealed class CommitLeaseTool
{
    /// <summary>Liveness window for a commit lease; callers heartbeat to keep it alive.</summary>
    private const int DefaultTtlSeconds = 300;

    /// <summary>Path comparisons follow the host file system's case rules (mirrors JsonCommitLeaseStore).</summary>
    private static readonly StringComparison PathComparison
        = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private static readonly StringComparer PathComparer
        = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

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
    [McpServerTool(Name = "commit_lease"), Description("Take the commit lease on this checkout and branch before staging or committing. The conflict check is folded in: verdict 'green' and acquired: true - stage. verdict 'red' and acquired: false - do not stage; a foreign live commit lease holds the slot. verdict 'red' on an acquired lease means foreign file leases collide with your paths - stage nothing until they clear. Returns { acquired, lease_id?, since?, verdict, staged_without_lease, conflicting_file_leases? }; when acquired: false, holder info is in { holder_session_id, since, lease_age_seconds, intent }. Re-claiming your own live lease is idempotent (succeeds with the same lease_id). branch is optional and is discovered via git when omitted. Call commit_release once the commit lands - and just the same when the attempt fails.")]
    public async Task<string> LeaseAsync(
        [Description("The calling session's id. Must be at least 32 characters.")] string session_id,
        [Description("Current branch of the checkout. Optional; discovered via git when omitted. Pass it to skip discovery (e.g. when git is unavailable).")] string? branch = null,
        [Description("One-line summary of the planned commit. Optional.")] string? intent = null,
        [Description("Paths the commit is expected to touch. Optional.")] string[]? paths = null,
        CancellationToken cancellationToken = default)
    {
        if (!CommitLeaseToolHelpers.IsSessionIdPlausible(session_id))
        {
            return CommitLeaseToolHelpers.Serialize(new CommitLeaseError { Acquired = false, Error = CommitLeaseToolHelpers.SessionIdTooShortMessage(session_id) });
        }

        try
        {
            var caller = session_id.Trim();
            var checkout = GitRepositoryResolver.ResolveWorkingCopyRoot(_cwd) ?? _cwd;
            var git = await GitStatusReader.TryRead(checkout, _logger, cancellationToken);
            var branchKey = string.IsNullOrWhiteSpace(branch) ? git?.CurrentBranch : branch.Trim();
            branchKey = string.IsNullOrWhiteSpace(branchKey) ? null : branchKey.Trim();

            var now = _timeProvider.GetUtcNow();
            // One read serves every leg: the store sweeps stale leases as part of
            // ListActiveAsync. The branch comparison happens in memory - file
            // leases are not meaningfully branch-scoped, and the commit-lease
            // filter needs the discovered branch anyway.
            var activeLeases = await _leaseStore.ListActiveAsync(checkout, null, cancellationToken);
            var requestedPaths = NormalizeRequestedPaths(paths, checkout);

            CommitLeaseRecord? ownCommitLease = null;
            var foreignCommitLeases = new List<CommitLeaseRecord>();
            var fileConflicts = new List<CommitLeaseRecord>();
            foreach (var lease in activeLeases)
            {
                if (lease.Kind == CommitLeaseKind.Commit)
                {
                    if (!CommitLeaseToolHelpers.BranchMatches(lease.Branch, branchKey)) continue;
                    if (string.Equals(lease.SessionId, caller, StringComparison.Ordinal))
                    {
                        ownCommitLease ??= lease;
                        continue;
                    }
                    foreignCommitLeases.Add(lease);
                }
                else if (lease.Kind == CommitLeaseKind.File)
                {
                    if (string.Equals(lease.SessionId, caller, StringComparison.Ordinal)) continue;
                    if (requestedPaths.Length > 0 && FileLeaseIntersects(lease.Paths, requestedPaths, checkout))
                    {
                        fileConflicts.Add(lease);
                    }
                }
            }

            var holder = foreignCommitLeases.OrderBy(l => l.Since).FirstOrDefault();
            var conflictItems = fileConflicts.Count == 0
                ? null
                : fileConflicts
                    .OrderBy(l => l.Since)
                    .Select(l => new ConflictingFileLease
                    {
                        LeaseId = l.LeaseId,
                        HolderSessionId = l.SessionId,
                        Since = l.Since,
                        LeaseAgeSeconds = CommitLeaseToolHelpers.AgeSeconds(now, l.Since),
                        Paths = l.Paths,
                        Intent = CommitLeaseToolHelpers.NormalizeIntent(l.Intent),
                    })
                    .ToArray();

            var stagedWithoutLease = (git?.HasStagedChanges ?? false) && ownCommitLease is null;
            var verdict = (holder is not null || conflictItems is not null) ? "red" : "green";

            var candidate = new CommitLeaseRecord
            {
                LeaseId = Ulid.NewUlid().ToString(),
                SessionId = caller,
                Kind = CommitLeaseKind.Commit,
                Checkout = checkout,
                Branch = branchKey,
                Since = now,
                LastHeartbeat = now,
                TtlSeconds = DefaultTtlSeconds,
                Paths = NormalizePaths(paths),
                Intent = CommitLeaseToolHelpers.NormalizeIntent(intent)
            };

            // Idempotent re-entry, decided before the store gate: a live commit
            // lease of our own anywhere in the scope is the slot; return it
            // rather than double-claim. The store's conflict check cannot see
            // this scope (it is branch-scoped), and no caller ever wants two
            // records for one session.
            if (ownCommitLease is not null)
            {
                return CommitLeaseToolHelpers.Serialize(new CommitLeaseSuccess
                {
                    Acquired = true,
                    LeaseId = ownCommitLease.LeaseId,
                    Since = ownCommitLease.Since,
                    Verdict = verdict,
                    StagedWithoutLease = stagedWithoutLease,
                    ConflictingFileLeases = conflictItems,
                });
            }

            // Foreign seat conflict — the fail-safe scope is every branch when
            // the branch is undiscoverable; the store's gate cannot express
            // that, so it is enforced here. When a branch key exists the store
            // re-checks under its gate anyway.
            if (holder is not null)
            {
                return CommitLeaseToolHelpers.Serialize(new CommitLeaseHeld
                {
                    Acquired = false,
                    Verdict = "red",
                    HolderSessionId = holder.SessionId,
                    Since = holder.Since,
                    LeaseAgeSeconds = CommitLeaseToolHelpers.AgeSeconds(now, holder.Since),
                    Intent = CommitLeaseToolHelpers.NormalizeIntent(holder.Intent),
                    StagedWithoutLease = stagedWithoutLease,
                    ConflictingFileLeases = conflictItems,
                });
            }

            var existingLease = await _leaseStore.TryLeaseAsync(candidate, cancellationToken);
            if (existingLease is null)
            {
                return CommitLeaseToolHelpers.Serialize(new CommitLeaseSuccess
                {
                    Acquired = true,
                    LeaseId = candidate.LeaseId,
                    Since = now,
                    Verdict = verdict,
                    StagedWithoutLease = stagedWithoutLease,
                    ConflictingFileLeases = conflictItems,
                });
            }

            // Race with the store's gate: a foreign lease appeared between the
            // read above and the claim. Report it the same way.
            var ageSeconds = CommitLeaseToolHelpers.AgeSeconds(now, existingLease.Since);
            return CommitLeaseToolHelpers.Serialize(new CommitLeaseHeld
            {
                Acquired = false,
                Verdict = "red",
                HolderSessionId = existingLease.SessionId,
                Since = existingLease.Since,
                LeaseAgeSeconds = ageSeconds,
                Intent = CommitLeaseToolHelpers.NormalizeIntent(existingLease.Intent),
                StagedWithoutLease = stagedWithoutLease,
                ConflictingFileLeases = conflictItems,
            });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The caller cancelled: propagate so the turn aborts normally.
            _logger?.LogDebug("commit_lease was cancelled by the caller (session {SessionId}).", session_id);
            throw;
        }
        catch (Exception ex)
        {
            // The store's gate wait and its file I/O both throw; the caller gets
            // the repo's compact error shape, not an opaque host-level failure.
            _logger?.LogWarning(ex, "commit_lease failed for session {SessionId}.", session_id);
            return CommitLeaseToolHelpers.Serialize(new CommitLeaseError { Acquired = false, Error = $"Could not take the commit lease: {ex.Message}" });
        }
    }

    private string[] NormalizeRequestedPaths(string[]? paths, string checkout)
    {
        if (paths is null) return [];
        return paths
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => NormalizeComparablePath(p, checkout))
            .Where(p => p.Length > 0)
            .Distinct(PathComparer)
            .ToArray();
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

    private bool FileLeaseIntersects(string[]? leasePaths, string[] requestedPaths, string checkout)
    {
        if (leasePaths is null) return false;
        foreach (var leasePath in leasePaths)
        {
            if (string.IsNullOrWhiteSpace(leasePath)) continue;
            var normalized = NormalizeComparablePath(leasePath, checkout);
            if (normalized.Length == 0) continue;
            foreach (var requestedPath in requestedPaths)
            {
                if (PathsIntersect(normalized, requestedPath)) return true;
            }
        }
        return false;
    }

    private static bool PathsIntersect(string normalizedA, string normalizedB)
    {
        if (string.Equals(normalizedA, normalizedB, PathComparison)) return true;
        return normalizedA.StartsWith(normalizedB + "/", PathComparison)
            || normalizedB.StartsWith(normalizedA + "/", PathComparison);
    }

    private string NormalizeComparablePath(string path, string checkout)
    {
        var p = path.Trim().Replace('\\', '/');
        if (Path.IsPathRooted(p))
        {
            try
            {
                p = Path.GetRelativePath(checkout, p).Replace('\\', '/');
            }
            catch (ArgumentException ex)
            {
                _logger?.LogDebug(ex, "Could not relativize path {Path} against {Checkout}; comparing it as rooted.", path, checkout);
            }
            catch (NotSupportedException ex)
            {
                _logger?.LogDebug(ex, "Could not relativize path {Path} against {Checkout}; comparing it as rooted.", path, checkout);
            }
        }
        while (p.StartsWith("./", StringComparison.Ordinal))
        {
            p = p[2..];
        }
        p = p.Trim('/');
        return p;
    }

    private sealed record CommitLeaseSuccess
    {
        [JsonPropertyName("acquired")]
        public required bool Acquired { get; init; }

        [JsonPropertyName("lease_id")]
        public required string LeaseId { get; init; }

        [JsonPropertyName("since")]
        public required DateTimeOffset Since { get; init; }

        [JsonPropertyName("verdict")]
        public required string Verdict { get; init; }

        [JsonPropertyName("staged_without_lease")]
        public required bool StagedWithoutLease { get; init; }

        [JsonPropertyName("conflicting_file_leases")]
        public required ConflictingFileLease[]? ConflictingFileLeases { get; init; }
    }

    private sealed record CommitLeaseHeld
    {
        [JsonPropertyName("acquired")]
        public required bool Acquired { get; init; }

        [JsonPropertyName("verdict")]
        public required string Verdict { get; init; }

        [JsonPropertyName("holder_session_id")]
        public required string HolderSessionId { get; init; }

        [JsonPropertyName("since")]
        public required DateTimeOffset Since { get; init; }

        [JsonPropertyName("lease_age_seconds")]
        public required long LeaseAgeSeconds { get; init; }

        [JsonPropertyName("intent")]
        public required string? Intent { get; init; }

        [JsonPropertyName("staged_without_lease")]
        public required bool StagedWithoutLease { get; init; }

        [JsonPropertyName("conflicting_file_leases")]
        public required ConflictingFileLease[]? ConflictingFileLeases { get; init; }
    }

    private sealed record ConflictingFileLease
    {
        [JsonPropertyName("lease_id")]
        public required string LeaseId { get; init; }

        [JsonPropertyName("holder_session_id")]
        public required string HolderSessionId { get; init; }

        [JsonPropertyName("since")]
        public required DateTimeOffset Since { get; init; }

        [JsonPropertyName("lease_age_seconds")]
        public required long LeaseAgeSeconds { get; init; }

        [JsonPropertyName("paths")]
        public required string[]? Paths { get; init; }

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
