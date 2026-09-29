using CliWrap;
using CliWrap.Buffered;
using Microsoft.Extensions.Logging;

namespace Eling.Backend.Identity;

/// <summary>
/// Reads global git config by shelling out to <c>git config --global</c>.
/// </summary>
/// <remarks>
/// Parsing <c>~/.gitconfig</c> in-process is avoided deliberately: the format
/// supports includes and conditional includes, and locating the file differs
/// on Windows, so a hand-rolled INI reader gets subtly wrong in ways that are
/// painful to debug. Asking git is always correct.
/// <para>
/// The executable is resolved to an absolute path before use. CliWrap — like
/// <c>Process.Start</c> — hands name resolution to the OS, so a bare
/// <c>Cli.Wrap("git")</c> fails whenever the host process was launched with a
/// reduced environment (IDE, MCP server, service host) and git is missing from
/// its PATH. Standard install locations are probed with a cheap
/// <see cref="File.Exists"/> first, with the PATH lookup kept as a last resort
/// so a non-standard install still resolves. Resolution is memoized per process.
/// </para>
/// </remarks>
public sealed class GitConfigReader(ILogger<GitConfigReader> logger) : IGitConfigReader
{
    private static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(5);

    private static readonly Lazy<string?> ResolvedGit = new(
        ResolveGitExecutable,
        LazyThreadSafetyMode.ExecutionAndPublication);

    public async Task<string?> ReadGlobalAsync(string key, CancellationToken cancellationToken)
    {
        var executable = ResolvedGit.Value;
        if (executable is null)
        {
            logger.LogDebug("No git executable found; {Key} reads will fall back", key);
            return null;
        }

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(ReadTimeout);

            // Buffered execution captures both streams and kills the process
            // tree if the timeout trips, so a hung git cannot wedge the request
            // and neither pipe can fill while the other is being read.
            var result = await Cli.Wrap(executable)
                .WithArguments(["config", "--global", key])
                .ExecuteBufferedAsync(timeout.Token);

            if (result.ExitCode != 0)
            {
                // git exits non-zero for "key not set" and for a malformed
                // config alike; both mean "no value" to the caller.
                return null;
            }

            var value = result.StandardOutput.Trim();
            return value.Length == 0 ? null : value;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            // Timeout, start denied, a bad key argument. None of these should
            // fail the request — the caller has a fallback. A cancellation the
            // caller actually asked for is left to propagate.
            logger.LogDebug(ex, "Reading global git config key {Key} failed", key);
            return null;
        }
    }

    private static string? ResolveGitExecutable()
    {
        foreach (var candidate in WellKnownLocations())
        {
            if (File.Exists(candidate))
                return candidate;
        }

        // Nothing at a standard location: fall back to a bare name so the OS
        // tries PATH and an unusual install still resolves.
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
