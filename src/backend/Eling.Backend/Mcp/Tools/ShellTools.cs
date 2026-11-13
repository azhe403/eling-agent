using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using CliWrap;
using Eling.Backend.Shell;
using Eling.Core.Shell;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

namespace Eling.Backend.Mcp.Tools;

/// <summary>
/// Runs a command line through the platform shell and returns its exit code,
/// captured output and duration.
/// </summary>
/// <remarks>
/// <para>
/// This tool is RCE-equivalent by design: one call runs an arbitrary command
/// with the privileges of the backend. Five gates stand in front of the shell,
/// and they run in this order, because a weaker gate must never answer for a
/// stronger one — an operator who disabled the tool must not learn anything
/// about a command from its error, and a root-targeting command must be refused
/// as such even when its working directory is also illegal:
/// </para>
/// <list type="number">
/// <item><c>invalid_argument</c> — an empty command.</item>
/// <item><c>shell_disabled</c> — <see cref="ShellConfig.Enabled"/> is false.</item>
/// <item><c>guardrail_violation</c> — <see cref="RootProtectionGuardrail"/> matches the raw command line, whatever the working directory.</item>
/// <item><c>command_blocked</c> — an active <see cref="BlockedCommandRule"/> matches the raw command line.</item>
/// <item><c>not_found</c> / <c>sandbox_violation</c> — the working directory does not exist, or it escapes the sandbox.</item>
/// </list>
/// <para>
/// Only the last two gates look at the working directory, and the path is
/// normalized with <see cref="Path.GetFullPath(string)"/> before it is compared,
/// so <c>..</c> cannot be used to walk out of the sandbox. A non-zero exit code
/// is a result, not an error: it is the caller's information.
/// </para>
/// </remarks>
[McpServerToolType]
public sealed class ShellTools
{
    private const int MinTimeoutMs = 1_000;
    private const int MaxTimeoutMs = 600_000;
    private const int MinMaxOutputBytes = 1_024;
    private const int MaxMaxOutputBytes = 1_048_576;
    private const string DefaultWorkingDirectory = ".";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    /// <summary>
    /// Absolute path to the shell. Resolved rather than left as a bare name:
    /// the process can be launched with a reduced environment (IDE, service
    /// host) where the shell is not on <c>PATH</c>.
    /// </summary>
    private static readonly string ShellExecutable = OperatingSystem.IsWindows()
        ? Environment.GetEnvironmentVariable("ComSpec") ?? @"C:\Windows\System32\cmd.exe"
        : "/bin/sh";

    private readonly ShellConfigStore _store;
    private readonly ILogger<ShellTools>? _logger;
    private readonly ShellSessionRoot _sessionRoot;

    public ShellTools(ShellConfigStore store, ILogger<ShellTools>? logger, ShellSessionRoot sessionRoot)
    {
        _store = store;
        _logger = logger;
        _sessionRoot = sessionRoot;
    }

    [McpServerTool(Name = "shell_execute"), Description("Run a shell command line (cmd.exe /c on Windows, /bin/sh -c elsewhere) and return exitCode, standardOutput, standardError, truncated, timedOut and durationMs. Pipes and redirection work. Disabled by default; the user enables it in shell-config.json. Refused with a code before anything runs when: the command is empty (invalid_argument), the tool is disabled (shell_disabled), the command targets a system root or disk volume (guardrail_violation), a user-blocked pattern matches (command_blocked), or the working directory is missing (not_found) or outside the sandbox (sandbox_violation). A non-zero exitCode is a normal result, not an error. Use the dedicated file tools for reading and writing files.")]
    public async Task<string> ExecuteAsync(
        [Description("Command line to run, e.g. 'git status'. Passing the whole line to the shell means pipes and redirection behave as the user expects.")] string command,
        [Description("Directory to run in. A relative path resolves against the launched workspace (the same directory the hosting agent session opened), not necessarily the filesystem memory-scope root. Absolute paths are used as given. Defaults to '.'.")] string workingDirectory = DefaultWorkingDirectory,
        [Description("Kill the entire process tree after this many milliseconds, clamped to [1000, 600000]. Omit to use the default timeout from the user's configuration.")] int? timeoutMs = null,
        [Description("Per-stream output cap in bytes, clamped to [1024, 1048576]. Omit to use the default from the user's configuration.")] int? maxOutputBytes = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            return Error("command is required and must not be blank.", "invalid_argument");
        }

        var config = _store.GetConfig();

        // Unspecified parameters take the compiled-in fallback, overridden by the
        // user's configuration when present. The seconds-to-milliseconds product
        // is done in long: the config file is user-editable, and a large value
        // would overflow int before the clamp.
        var effectiveTimeoutMs = timeoutMs ?? (int)Math.Clamp((long)config.DefaultTimeoutSeconds * 1_000, MinTimeoutMs, MaxTimeoutMs);
        var effectiveMaxBytes = maxOutputBytes ?? Math.Clamp(config.MaxOutputBytes, MinMaxOutputBytes, MaxMaxOutputBytes);

        if (!config.Enabled)
        {
            return Error("shell_execute is disabled. The user can enable it on the dashboard Shell page (shell-config.json).", "shell_disabled");
        }

        if (config.RootGuardrail)
        {
            var reason = RootProtectionGuardrail.BlockedReason(command);
            if (reason is not null)
            {
                _logger?.LogWarning("shell_execute refused a guardrail match: {Reason} {Command}", reason, command);
                return Error("Command blocked by root-protection guardrail: destructive command targeting system root or disk volume.", "guardrail_violation");
            }
        }

        var blockedBy = FindBlockedPattern(config, command);
        if (blockedBy is not null)
        {
            _logger?.LogWarning("shell_execute refused a user-blocked pattern {Pattern}: {Command}", blockedBy, command);
            return Error($"Command blocked by user policy: matches pattern '{blockedBy}'.", "command_blocked");
        }

        // The session root is where the shell belongs: the launched workspace,
        // not the filesystem/memory-scope sandbox. It is required, so there is
        // no fallback into the sandbox root.
        var workspaceRoot = Path.GetFullPath(_sessionRoot.Root);
        if (!TryResolveDirectory(workspaceRoot, workingDirectory, out var directory, out var resolveError))
        {
            return Error(resolveError, "invalid_argument");
        }

        if (!Directory.Exists(directory))
        {
            return Error($"workingDirectory does not exist: {directory}", "not_found");
        }

        if (config.RestrictToAllowedDirectories && !IsAllowedDirectory(config, workspaceRoot, directory))
        {
            _logger?.LogWarning("shell_execute refused a sandbox escape: {Directory}", directory);
            return Error($"workingDirectory is outside the workspace root ({workspaceRoot}) and not in an allowed directory: {directory}", "sandbox_violation");
        }

        return await RunAsync(command, directory, effectiveTimeoutMs, effectiveMaxBytes, cancellationToken);
    }

    /// <summary>
    /// Runs the command and shapes the result envelope. The timeout is a linked
    /// token so the two cancellations stay distinguishable: when the caller
    /// cancels, the token it owns is set and the exception propagates to abort
    /// the turn; when the timeout fires, only the linked token is set, and the
    /// timeout is reported as a result with whatever output was captured.
    /// </summary>
    private async Task<string> RunAsync(
        string command,
        string directory,
        int timeoutMs,
        int maxOutputBytes,
        CancellationToken cancellationToken)
    {
        var timeout = Math.Clamp(timeoutMs, MinTimeoutMs, MaxTimeoutMs);
        var cap = Math.Clamp(maxOutputBytes, MinMaxOutputBytes, MaxMaxOutputBytes);
        var standardOutput = new CappedTextBuffer(cap);
        var standardError = new CappedTextBuffer(cap);

        var exitCode = -1;
        var timedOut = false;
        var stopwatch = Stopwatch.StartNew();

        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(TimeSpan.FromMilliseconds(timeout));

        try
        {
            // CliWrap kills the whole process tree when the token fires, so a
            // shell that spawned a child cannot survive its own timeout.
            var result = await Cli.Wrap(ShellExecutable)
                .WithArguments([ShellCommandSwitch, command])
                .WithWorkingDirectory(directory)
                .WithStandardOutputPipe(PipeTarget.ToDelegate(line => AppendLine(standardOutput, line)))
                .WithStandardErrorPipe(PipeTarget.ToDelegate(line => AppendLine(standardError, line)))
                .WithValidation(CommandResultValidation.None)
                .ExecuteAsync(timeoutSource.Token);

            exitCode = result.ExitCode;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The caller cancelled: propagate so the turn aborts normally.
            _logger?.LogDebug("shell_execute was cancelled by the caller: {Command}", command);
            throw;
        }
        catch (OperationCanceledException)
        {
            // Only the linked timeout token is set: report the timeout as a
            // result, keeping whatever output was captured before the kill.
            timedOut = true;
            _logger?.LogWarning("shell_execute timed out after {TimeoutMs} ms: {Command}", timeout, command);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "shell_execute failed to run {Command} in {Directory}", command, directory);
            return Error($"Could not run the command: {ex.Message}", "internal_error");
        }
        finally
        {
            stopwatch.Stop();
        }

        _logger?.LogInformation(
            "shell_execute ran {Command} in {Directory} (exit {ExitCode}, {DurationMs} ms, timedOut: {TimedOut})",
            command, directory, exitCode, stopwatch.ElapsedMilliseconds, timedOut);

        return JsonSerializer.Serialize(new
        {
            command,
            workingDirectory = directory,
            exitCode,
            standardOutput = standardOutput.ToString(),
            standardError = standardError.ToString(),
            truncated = standardOutput.Truncated || standardError.Truncated,
            timedOut,
            durationMs = stopwatch.ElapsedMilliseconds,
            resolvedWorkingDirectory = directory
        }, JsonOptions);
    }

    /// <summary>The argument the resolved shell needs in front of the command line.</summary>
    private static string ShellCommandSwitch => OperatingSystem.IsWindows() ? "/c" : "-c";

    /// <summary>
    /// Pipes deliver one line at a time without its terminator; put a newline
    /// back so the captured text reads like the command's real output.
    /// </summary>
    private static void AppendLine(CappedTextBuffer buffer, string line)
    {
        buffer.Append(line);
        buffer.Append("\n");
    }

    /// <summary>
    /// The first active pattern that the command line contains, or null. An
    /// empty pattern is inert: it would match every command.
    /// </summary>
    private static string? FindBlockedPattern(ShellConfig config, string command)
    {
        foreach (var rule in config.BlockedCommands)
        {
            if (!rule.Enabled)
            {
                continue;
            }

            var pattern = rule.Pattern?.Trim();
            if (string.IsNullOrEmpty(pattern))
            {
                continue;
            }

            // A fragment, not a word: 'rm -rf' must match inside a longer line.
            if (command.Contains(pattern, StringComparison.OrdinalIgnoreCase))
            {
                return pattern;
            }
        }

        return null;
    }

    /// <summary>
    /// Resolves the caller's directory. Relative paths resolve against the
    /// workspace root; absolute paths are kept. <see cref="Path.GetFullPath(string)"/>
    /// collapses <c>.</c>, <c>..</c> and separators, which is what makes the
    /// boundary check below impossible to walk past.
    /// </summary>
    private bool TryResolveDirectory(string workspaceRoot, string workingDirectory, out string directory, out string error)
    {
        var requested = string.IsNullOrWhiteSpace(workingDirectory) ? DefaultWorkingDirectory : workingDirectory;
        try
        {
            directory = Path.GetFullPath(Path.IsPathRooted(requested)
                ? requested
                : Path.Combine(workspaceRoot, requested));
            error = "";
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            _logger?.LogWarning(ex, "shell_execute received an invalid workingDirectory: {Directory}", requested);
            directory = "";
            error = $"workingDirectory is not a valid path: {requested}";
            return false;
        }
    }

    /// <summary>
    /// Whether the directory is the workspace root or below it, or is in an
    /// active allowed directory. Both sides are compared normalized and
    /// case-insensitively, as the store compares them when the dashboard toggles
    /// a rule.
    /// </summary>
    private bool IsAllowedDirectory(ShellConfig config, string workspaceRoot, string directory)
    {
        if (IsUnder(directory, workspaceRoot))
        {
            return true;
        }

        foreach (var rule in config.AllowedDirectories)
        {
            if (!rule.Enabled)
            {
                continue;
            }

            var path = rule.Path?.Trim();
            if (string.IsNullOrEmpty(path))
            {
                continue;
            }

            string allowed;
            try
            {
                allowed = Path.GetFullPath(path);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                _logger?.LogWarning(ex, "Shell allowed-directory rule is not a valid path and was skipped: {Path}", path);
                continue;
            }

            if (IsUnder(directory, allowed))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Path containment on normalized paths. The trailing separator matters:
    /// without it, <c>/work/allowed-evil</c> would pass as a child of
    /// <c>/work/allowed</c>.
    /// </summary>
    private static bool IsUnder(string directory, string root)
    {
        var rootWithSeparator = Normalize(root);
        return Normalize(directory).StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>A normalized path that always ends in exactly one separator.</summary>
    private static string Normalize(string path)
    {
        var full = Path.GetFullPath(path)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        // A drive root ("C:\") trims down to "C:", which would then match
        // "C:\anything" without the separator — and also "C:foo". Put it back.
        return full + Path.DirectorySeparatorChar;
    }

    private static string Error(string message, string code)
        => JsonSerializer.Serialize(new { ok = false, error = message, code }, JsonOptions);
}
