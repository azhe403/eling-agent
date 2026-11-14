using CliWrap;
using CliWrap.Buffered;

using Microsoft.Extensions.Logging;

namespace Eling.Backend.Mcp.Tools;

/// <summary>
/// Shells out to git for the state the commit-lease tools need, with a 5s
/// timeout per command. Any failure short of caller cancellation is treated
/// as "no answer" (null for that command, or null overall when nothing
/// answered); caller cancellation propagates.
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
            // if the timeout trips (same idiom as the other git helpers).
            var result = await Cli.Wrap(ResolvedGitExecutable.Value)
                .WithArguments(args)
                .WithWorkingDirectory(checkout)
                .ExecuteBufferedAsync(timeout.Token);

            if (result.ExitCode != 0)
            {
                // Not a repository, or a command git cannot answer there:
                // no answer for this command, and the caller degrades
                // gracefully instead of failing the whole read.
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
        // MCP hosts are often launched with a reduced environment, so standard
        // install locations are checked before falling back to a bare name for
        // the OS PATH lookup.
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
}
