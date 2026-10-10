using CliWrap;
using CliWrap.Buffered;

namespace Eling.Core.Scope;

/// <summary>
/// Canonical project identity for central (machine-local) stores.
/// Git repos resolve to the main worktree toplevel so every worktree and
/// subfolder shares one shard. Non-git folders fall back to their own path.
/// The git probe runs through CliWrap (repo rule: no raw Process), strictly
/// bounded, once per call; DI registration calls this once at startup.
/// </summary>
public static class CanonicalProjectRoot
{
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(5);

    public static string Resolve(string? startDirectory = null, Func<string, Task<string?>>? gitToplevelProbe = null)
    {
        var start = string.IsNullOrWhiteSpace(startDirectory)
            ? Directory.GetCurrentDirectory()
            : Path.GetFullPath(startDirectory);
        var probe = gitToplevelProbe ?? DefaultGitToplevelProbeAsync;
        try
        {
            // Hop to the pool: registration may run on a thread with a
            // synchronization context (desktop host), where a direct
            // GetAwaiter().GetResult() could deadlock.
            var top = Task.Run(() => probe(start)).GetAwaiter().GetResult();
            if (!string.IsNullOrWhiteSpace(top)) return Path.GetFullPath(top!);
        }
        catch
        {
            // Fall through to path fallback below.
        }
        return Path.GetFullPath(start);
    }

    private static async Task<string?> DefaultGitToplevelProbeAsync(string start)
    {
        var executable = ResolveGitExecutable();
        try
        {
            using var timeout = new CancellationTokenSource(ProbeTimeout);
            var result = await Cli.Wrap(executable)
                .WithArguments(["rev-parse", "--show-toplevel"])
                .WithWorkingDirectory(start)
                .ExecuteBufferedAsync(timeout.Token);
            if (result.ExitCode != 0) return null;
            var trimmed = result.StandardOutput.Trim();
            return string.IsNullOrWhiteSpace(trimmed) ? null : trimmed;
        }
        catch
        {
            // No git, not a repo, timeout, start denied. Caller falls back.
            return null;
        }
    }

    private static string ResolveGitExecutable()
    {
        foreach (var candidate in WellKnownLocations())
        {
            if (File.Exists(candidate))
                return candidate;
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
