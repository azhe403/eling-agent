using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using CliWrap;
using CliWrap.Buffered;
using Eling.Core.Git;
using Eling.Core.CommitLease;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

namespace Eling.Backend.Mcp.Tools;

/// <summary>
/// Answers "is it safe to stage and commit right now?" for the calling session
/// on this checkout. Folds three signals into one stage / don't-stage verdict:
/// a live foreign commit lease on the same checkout and branch, live file
/// leases held by another session on any of the requested paths, and whether
/// files are staged while the caller holds no commit lease of its own.
/// </summary>
/// <remarks>
/// Checkout is the working-copy root resolved via
/// <see cref="GitRepositoryResolver.ResolveWorkingCopyRoot"/> from the injected
/// working directory (default <see cref="Directory.GetCurrentDirectory"/>), so
/// leases taken from any subdirectory of a working copy key to that
/// working copy's root. A linked worktree keys by the worktree path,
/// distinct from the main checkout; each working copy sees only its own
/// leases — the store's checkout scoping is the isolation boundary. A
/// path outside every git repository falls back to the working directory
/// itself, keyed by that directory.
/// <para>
/// The store read path swallows I/O failures as "empty" (same contract as
/// the sibling tools), so a corrupt or unreadable commit-leases.json can
/// present as "free slot" here with no signal in the tool output; the
/// store logs a warning.
/// </para>
/// <para>
/// The branch is resolved from the optional <c>branch</c> argument, falling
/// back to discovery via <c>git branch --show-current</c>. When neither is
/// available (git missing, detached HEAD, or a non-git directory) the fail-safe
/// scope is every branch of the checkout: a foreign live commit lease on any
/// branch marks the verdict red. git state itself is read with a 5s timeout
/// per command; when git is unusable the tool still evaluates the leases and
/// simply reports <c>staged_without_lease</c> as false.
/// </para>
/// </remarks>
[McpServerToolType]
public sealed class CommitCheckConflictTool
{
    /// <summary>Path comparisons follow the host file system's case rules (mirrors JsonCommitLeaseStore).</summary>
    private static readonly StringComparison PathComparison
        = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private static readonly StringComparer PathComparer
        = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private readonly ICommitLeaseStore _leaseStore;
    private readonly TimeProvider _timeProvider;
    private readonly string _cwd;
    private readonly ILogger<CommitCheckConflictTool>? _logger;

    public CommitCheckConflictTool(
        ICommitLeaseStore leaseStore,
        TimeProvider? timeProvider = null,
        string? cwd = null,
        ILogger<CommitCheckConflictTool>? logger = null)
    {
        _leaseStore = leaseStore;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _cwd = Path.GetFullPath(string.IsNullOrWhiteSpace(cwd) ? Directory.GetCurrentDirectory() : cwd);
        _logger = logger;
    }

    // Parameter names below use snake_case deliberately: the MCP host passes
    // C# parameter names through to the wire schema unchanged, and the
    // commit_* tool surface is snake_case end to end.
    [McpServerTool(Name = "commit_check_conflict"), Description("Check whether it is safe to stage and commit on this checkout: combines a live foreign commit lease on the same branch, live file leases held by another session on any of the requested paths, and whether files are staged while you hold no commit lease of your own. Returns { verdict: 'green' | 'red', holder?: {...}, staged_without_lease: bool, conflicting_file_leases?: [...] }. On green, call commit_lease before the first staging; on red, stop and report the holder (or wait with commit_wait). branch is optional and is discovered via git when omitted.")]
    public async Task<string> CheckConflictAsync(
        [Description("The calling session's id. Must be at least 32 characters.")] string session_id,
        [Description("Paths the commit is expected to touch.")] string[] paths,
        [Description("Current branch of the checkout. Optional; discovered via git when omitted. Pass it to skip discovery (e.g. when git is unavailable).")] string? branch = null,
        CancellationToken cancellationToken = default)
    {
        if (!CommitLeaseToolHelpers.IsSessionIdPlausible(session_id))
        {
            return CommitLeaseToolHelpers.Serialize(new ConflictCheckError(CommitLeaseToolHelpers.SessionIdTooShortMessage(session_id)));
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
            // ListActiveAsync. The branch comparison happens in memory — file
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
            var holderInfo = holder is null
                ? null
                : new HolderInfo
                {
                    HolderSessionId = holder.SessionId,
                    Since = holder.Since,
                    LeaseAgeSeconds = CommitLeaseToolHelpers.AgeSeconds(now, holder.Since),
                    Intent = CommitLeaseToolHelpers.NormalizeIntent(holder.Intent),
                };
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
            return CommitLeaseToolHelpers.Serialize(new ConflictCheckResult
            {
                Verdict = verdict,
                Holder = holderInfo,
                StagedWithoutLease = stagedWithoutLease,
                ConflictingFileLeases = conflictItems,
            });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The caller cancelled: propagate so the turn aborts normally.
            _logger?.LogDebug("commit_check_conflict was cancelled by the caller (session {SessionId}).", session_id);
            throw;
        }
        catch (Exception ex)
        {
            // The store's gate wait and its file I/O both throw; the caller gets
            // the repo's compact error shape, not an opaque host-level failure.
            _logger?.LogWarning(ex, "commit_check_conflict failed for session {SessionId}.", session_id);
            return CommitLeaseToolHelpers.Serialize(new ConflictCheckError($"Could not check the commit conflict: {ex.Message}"));
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

    private sealed record ConflictCheckResult
    {
        [JsonPropertyName("verdict")]
        public required string Verdict { get; init; }

        [JsonPropertyName("holder")]
        public required HolderInfo? Holder { get; init; }

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

    private sealed record ConflictCheckError(
        [property: JsonPropertyName("error")]
        string Error);

    /// <summary>
    /// A snapshot of the checkout's git state. Null when git is unusable;
    /// callers then still evaluate the leases and treat git facts as unknown.
    /// </summary>
    internal sealed class GitState
    {
        public GitState(
            bool hasStagedChanges,
            string[] stagedPaths,
            string? currentBranch)
        {
            HasStagedChanges = hasStagedChanges;
            StagedPaths = stagedPaths;
            CurrentBranch = currentBranch;
        }

        public bool HasStagedChanges { get; }
        public string[] StagedPaths { get; }
        public string? CurrentBranch { get; }
    }

    internal sealed class GitPorcelainState
    {
        public GitPorcelainState(IReadOnlyList<PorcelainEntry> entries)
        {
            Entries = entries;
            var staged = entries.Where(e => IsStaged(e.Index)).Select(e => e.Path).ToArray();
            StagedPaths = staged;
            HasStagedChanges = staged.Length > 0;
        }

        public IReadOnlyList<PorcelainEntry> Entries { get; }
        public string[] StagedPaths { get; }
        public bool HasStagedChanges { get; }

        private static bool IsStaged(char index) => index != ' ' && index != '?' && index != '!';
    }

    internal readonly record struct PorcelainEntry(
        char Index,
        char WorkTree,
        string Path);

    /// <summary>
    /// Shells out to git for the state this tool needs, with a 5s timeout per
    /// command. Any failure short of caller cancellation is treated as "no
    /// answer" (null for that command, or null overall when nothing answered);
    /// caller cancellation propagates.
    /// </summary>
    internal static class GitStatusReader
    {
        private static readonly TimeSpan GitReadTimeout = TimeSpan.FromSeconds(5);

        public static async Task<GitState?> TryRead(
            string checkout,
            ILogger? logger,
            CancellationToken cancellationToken)
        {
            var porcelain = await RunGit(checkout, ["status", "--porcelain"], logger, cancellationToken);
            var staged = await RunGit(checkout, ["diff", "--cached", "--name-only"], logger, cancellationToken);
            var branch = await RunGit(checkout, ["branch", "--show-current"], logger, cancellationToken);
            if (porcelain is null && staged is null && branch is null) return null;

            var parsed = porcelain is null ? null : ParsePorcelain(porcelain);
            var stagedPaths = staged is not null
                ? SplitLines(staged)
                : parsed?.StagedPaths ?? [];
            var hasStaged = stagedPaths.Length > 0 || (parsed?.HasStagedChanges ?? false);
            var currentBranch = string.IsNullOrWhiteSpace(branch) ? null : branch.Trim();
            return new GitState(hasStaged, stagedPaths, currentBranch);
        }

        /// <summary>
        /// Parses <c>git status --porcelain</c> (v1) output: <c>XY path</c>,
        /// <c>XY orig -&gt; new</c> for renames, quoted paths for special
        /// characters. Staged means the index column is set (anything other
        /// than space, ? or !). Quoted paths are unquoted minimally
        /// (backslash-escapes removed; octal escapes are left untouched).
        /// </summary>
        internal static GitPorcelainState ParsePorcelain(string? output)
        {
            var entries = new List<PorcelainEntry>();
            if (output is not null)
            {
                using var reader = new StringReader(output);
                string? raw;
                while ((raw = reader.ReadLine()) is not null)
                {
                    if (raw.Length < 4) continue;
                    var path = raw[3..].Trim();
                    if (path.Length == 0) continue;
                    var renameMarker = path.LastIndexOf(" -> ", StringComparison.Ordinal);
                    if (renameMarker >= 0)
                    {
                        path = path[(renameMarker + 4)..].Trim();
                    }
                    path = Unquote(path);
                    entries.Add(new PorcelainEntry(raw[0], raw[1], path));
                }
            }
            return new GitPorcelainState(entries);
        }

        private static string Unquote(string path)
        {
            if (path.Length < 2 || path[0] != '"' || path[^1] != '"') return path;
            var inner = path[1..^1];
            return inner.Replace("\\\"", "\"").Replace("\\\\", "\\");
        }

        private static string[] SplitLines(string output)
            => output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        private static async Task<string?> RunGit(
            string checkout,
            string[] args,
            ILogger? logger,
            CancellationToken cancellationToken)
        {
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(GitReadTimeout);

                // Buffered execution captures both streams and kills the process
                // if the timeout trips (same idiom as GitConfigReader).
                var result = await Cli.Wrap(ResolvedGitExecutable.Value)
                    .WithArguments(args)
                    .WithWorkingDirectory(checkout)
                    .ExecuteBufferedAsync(timeout.Token);

                if (result.ExitCode != 0)
                {
                    // Not a repository, or a command git cannot answer there:
                    // no answer for this command, and the caller degrades
                    // gracefully instead of failing the whole check.
                    logger?.LogDebug("git {Args} exited with code {ExitCode} for {Checkout}; treating its output as unavailable.", string.Join(' ', args), result.ExitCode, checkout);
                    return null;
                }

                return result.StandardOutput;
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                // Missing git, start denied, killed by the 5s timeout. The caller
                // falls back to "no git state"; a cancellation the caller
                // actually asked for is left to propagate.
                logger?.LogDebug(ex, "git {Args} failed for {Checkout}; treating its output as unavailable.", string.Join(' ', args), checkout);
                return null;
            }
        }

        private static readonly Lazy<string> ResolvedGitExecutable = new(
            ResolveGitExecutable,
            LazyThreadSafetyMode.ExecutionAndPublication);

        private static string ResolveGitExecutable()
        {
            // Same probe order as GitConfigReader: MCP hosts are often launched
            // with a reduced environment, so standard install locations are
            // checked before falling back to a bare name for the OS PATH lookup.
            foreach (var candidate in WellKnownLocations())
            {
                if (File.Exists(candidate)) return candidate;
            }

            return "git";
        }

        private static IEnumerable<string> WellKnownLocations()
        {
            if (OperatingSystem.IsWindows())
            {
                var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
                var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
                var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

                if (!string.IsNullOrEmpty(programFiles))
                    yield return Path.Combine(programFiles, "Git", "cmd", "git.exe");
                if (!string.IsNullOrEmpty(programFilesX86))
                    yield return Path.Combine(programFilesX86, "Git", "cmd", "git.exe");
                if (!string.IsNullOrEmpty(localAppData))
                    yield return Path.Combine(localAppData, "Programs", "Git", "cmd", "git.exe");

                yield break;
            }

            yield return "/usr/bin/git";
            yield return "/usr/local/bin/git";
            yield return "/opt/homebrew/bin/git";
        }
    }
}
