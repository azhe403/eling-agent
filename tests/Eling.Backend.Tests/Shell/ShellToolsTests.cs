using System.Text.Json;
using Eling.Backend.Mcp.Tools;
using Eling.Backend.Shell;
using Eling.Core.Scope;
using Eling.Core.Shell;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Eling.Backend.Tests.Shell;

/// <summary>
/// Behavioural tests for the <c>shell_execute</c> MCP tool. These really do
/// spawn processes — the point of the tool is that it runs a command line, so a
/// mocked process would assert nothing about exit codes, stream separation or
/// the output cap.
/// </summary>
/// <remarks>
/// <para>
/// The configuration store is the real one, on a temp user scope: the gates are
/// only meaningful if they are read from the file the dashboard writes. The
/// session root is a temp directory standing in for the launched workspace.
/// </para>
/// <para>
/// Every command here behaves the same under cmd.exe and sh where it can, so
/// the suite does not fork per OS; the cases that cannot be shared (stream
/// separation, "sleep", and printing the current directory) branch explicitly.
/// </para>
/// </remarks>
public sealed class ShellToolsTests : IDisposable
{
    /// <summary>
    /// Prints the shell's working directory: cmd.exe has no <c>pwd</c>, and
    /// <c>cd</c> with no argument is not the same thing under POSIX sh.
    /// </summary>
    private static readonly string PrintWorkingDirectory = OperatingSystem.IsWindows() ? "cd" : "pwd";

    private readonly string _root = Path.Combine(Path.GetTempPath(), $"eling-shell-execute-{Guid.NewGuid():N}");
    private readonly string _workspace;
    private readonly string _session;
    private readonly string _outside;
    private readonly ShellConfigStore _store;
    private readonly ShellTools _tools;

    public ShellToolsTests()
    {
        _workspace = Path.Combine(_root, "workspace");
        _session = Path.Combine(_root, "session");
        _outside = Path.Combine(_root, "outside");
        Directory.CreateDirectory(_workspace);
        Directory.CreateDirectory(_session);
        Directory.CreateDirectory(_outside);

        _store = new ShellConfigStore(new UserScope(Path.Combine(_root, "user")), NullLogger<ShellConfigStore>.Instance);
        _tools = new ShellTools(_store, NullLogger<ShellTools>.Instance, new ShellSessionRoot(_workspace));
    }

    /// <summary>Writes the configuration the way the dashboard would, through the store.</summary>
    private void Configure(Func<ShellConfig, ShellConfig> mutate) => _store.Update(mutate);

    private async Task<JsonElement> RunAsync(
        string command,
        string workingDirectory = ".",
        int timeoutMs = 120_000,
        int maxOutputBytes = 65_536,
        CancellationToken ct = default)
    {
        var json = await _tools.ExecuteAsync(command, workingDirectory, timeoutMs, maxOutputBytes, ct);
        return JsonDocument.Parse(json).RootElement.Clone();
    }

    private static string Out(JsonElement result) => result.GetProperty("standardOutput").GetString() ?? "";

    private static string Err(JsonElement result) => result.GetProperty("standardError").GetString() ?? "";

    private static string Code(JsonElement result) => result.GetProperty("code").GetString() ?? "";

    // ---- Gate 1: the argument -------------------------------------------------

    [Fact]
    public async Task Execute_BlankCommand_ReturnsInvalidArgumentBeforeTheEnabledGate()
    {
        // The store is still on its template, so "disabled" would also apply:
        // the blank-command check must answer first, because the check order is
        // part of the contract.
        var result = await RunAsync("   ");

        Assert.Equal("invalid_argument", Code(result));
    }

    // ---- Gate 2: the master switch --------------------------------------------

    [Fact]
    public async Task Execute_WhenDisabledByDefault_ReturnsShellDisabled()
    {
        var result = await RunAsync("echo hello");

        Assert.Equal("shell_disabled", Code(result));
        Assert.False(result.GetProperty("ok").GetBoolean());
    }

    // ---- Execution ------------------------------------------------------------

    [Fact]
    public async Task Execute_EchoCommand_ReturnsExitZeroWithStdoutAndDuration()
    {
        Configure(c => c with { Enabled = true });

        var result = await RunAsync("echo hello-shell");

        Assert.Equal(0, result.GetProperty("exitCode").GetInt32());
        Assert.Contains("hello-shell", Out(result));
        Assert.Empty(Err(result));
        Assert.False(result.GetProperty("timedOut").GetBoolean());
        Assert.False(result.GetProperty("truncated").GetBoolean());
        Assert.True(result.GetProperty("durationMs").GetInt64() > 0);
    }

    [Fact]
    public async Task Execute_NonZeroExit_IsAResultNotAnError()
    {
        Configure(c => c with { Enabled = true });

        var result = await RunAsync("exit 3");

        // `exit 3` works in both cmd.exe and sh, so this one does not fork.
        Assert.Equal(3, result.GetProperty("exitCode").GetInt32());
        Assert.False(result.TryGetProperty("code", out _));
    }

    [Fact]
    public async Task Execute_WritesStdoutAndStderrToSeparateStreams()
    {
        Configure(c => c with { Enabled = true });

        var command = OperatingSystem.IsWindows()
            ? "echo out-line & echo err-line 1>&2"
            : "echo out-line; echo err-line 1>&2";

        var result = await RunAsync(command);

        Assert.Contains("out-line", Out(result));
        Assert.DoesNotContain("err-line", Out(result));
        Assert.Contains("err-line", Err(result));
        Assert.DoesNotContain("out-line", Err(result));
    }

    // ---- Gate 3: the root-protection guardrail --------------------------------

    [Fact]
    public async Task Execute_GuardrailMatch_ReturnsGuardrailViolation()
    {
        Configure(c => c with { Enabled = true });

        var result = await RunAsync("rm -rf /");

        Assert.Equal("guardrail_violation", Code(result));
    }

    [Fact]
    public async Task Execute_GuardrailRunsAgainstTheRawCommandRegardlessOfWorkingDirectory()
    {
        // The working directory is outside the sandbox too. The guardrail is the
        // stronger gate, so it must answer: the caller must not learn the
        // boundary verdict for a command that would never have run.
        Configure(c => c with { Enabled = true });

        var result = await RunAsync("rm -rf /", workingDirectory: "../outside");

        Assert.Equal("guardrail_violation", Code(result));
    }

    // ---- Gate 4: user-blocked commands ----------------------------------------

    [Fact]
    public async Task Execute_ActiveBlockedRule_ReturnsCommandBlocked()
    {
        Configure(c => c with
        {
            Enabled = true,
            BlockedCommands = [new BlockedCommandRule { Pattern = "git checkout" }]
        });

        var result = await RunAsync("echo git checkout main");

        Assert.Equal("command_blocked", Code(result));
    }

    [Fact]
    public async Task Execute_DisabledBlockedRule_IsIgnored()
    {
        Configure(c => c with
        {
            Enabled = true,
            BlockedCommands = [new BlockedCommandRule { Pattern = "git checkout", Enabled = false }]
        });

        // The command contains the pattern but runs, which is the only proof
        // that the rule was skipped rather than the command having failed.
        var result = await RunAsync("echo git checkout main");

        Assert.Equal(0, result.GetProperty("exitCode").GetInt32());
        Assert.Contains("git checkout", Out(result));
    }

    // ---- Gate 5: the directory boundary ---------------------------------------

    [Fact]
    public async Task Execute_RelativePathEscapingTheWorkspace_ReturnsSandboxViolation()
    {
        Configure(c => c with { Enabled = true });

        // Directory.Exists is true here — the escape must be caught by the
        // normalized containment check, not by an accident of the path not
        // existing.
        var result = await RunAsync(PrintWorkingDirectory, workingDirectory: "../outside");

        Assert.Equal("sandbox_violation", Code(result));
    }

    [Fact]
    public async Task Execute_AbsolutePathOutsideTheWorkspace_ReturnsSandboxViolation()
    {
        Configure(c => c with { Enabled = true });

        var result = await RunAsync(PrintWorkingDirectory, workingDirectory: _outside);

        Assert.Equal("sandbox_violation", Code(result));
    }

    [Fact]
    public async Task Execute_DisabledAllowedDirectoryRule_ReturnsSandboxViolation()
    {
        Configure(c => c with
        {
            Enabled = true,
            AllowedDirectories = [new AllowedDirectoryRule { Path = _outside, Enabled = false }]
        });

        var result = await RunAsync(PrintWorkingDirectory, workingDirectory: _outside);

        Assert.Equal("sandbox_violation", Code(result));
    }

    [Fact]
    public async Task Execute_EnabledAllowedDirectoryRule_RunsThere()
    {
        Configure(c => c with
        {
            Enabled = true,
            AllowedDirectories = [new AllowedDirectoryRule { Path = _outside }]
        });

        var result = await RunAsync(PrintWorkingDirectory, workingDirectory: _outside);

        // The current-directory command prints the shell's working directory
        // under both shells, so this also proves the directory was actually
        // used, not merely accepted.
        Assert.Equal(0, result.GetProperty("exitCode").GetInt32());
        Assert.Contains("outside", Out(result), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Execute_UnrestrictedMode_AllowsAnyExistingDirectory()
    {
        Configure(c => c with { Enabled = true, RestrictToAllowedDirectories = false });

        var result = await RunAsync(PrintWorkingDirectory, workingDirectory: _outside);

        Assert.Equal(0, result.GetProperty("exitCode").GetInt32());
        Assert.Contains("outside", Out(result), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Execute_MissingDirectory_ReturnsNotFound()
    {
        Configure(c => c with { Enabled = true });

        // Inside the sandbox but absent: not_found is reported before the
        // restriction check, which is what distinguishes "wrong place" from
        // "nothing there".
        var result = await RunAsync("echo hi", workingDirectory: "no-such-dir");

        Assert.Equal("not_found", Code(result));
    }

    [Fact]
    public async Task Execute_WithoutWorkingDirectory_UsesTheSessionRootNotTheFileSystemRoot()
    {
        Configure(c => c with { Enabled = true });

        // The filesystem sandbox tracks the memory scope; a shell is about the
        // workspace the backend process was launched in, so the tool must be
        // able to run there even when the sandbox root is another folder.
        var tools = new ShellTools(_store, NullLogger<ShellTools>.Instance, new ShellSessionRoot(_session));

        var json = await tools.ExecuteAsync(PrintWorkingDirectory);
        var result = JsonDocument.Parse(json).RootElement.Clone();

        Assert.Equal(0, result.GetProperty("exitCode").GetInt32());
        Assert.Equal(Path.GetFullPath(_session), result.GetProperty("resolvedWorkingDirectory").GetString());
    }

    // ---- Bounds ---------------------------------------------------------------

    [Fact]
    public async Task Execute_Timeout_ReportsTimedOutAndMinusOneExitCode()
    {
        Configure(c => c with { Enabled = true });

        // The two shells share no "sleep", so this one forks. The command is
        // long enough that the 1 s floor fires first on both.
        var command = OperatingSystem.IsWindows()
            ? "ping -n 6 127.0.0.1 > nul"
            : "sleep 5";

        var result = await RunAsync(command, timeoutMs: 1000);

        Assert.True(result.GetProperty("timedOut").GetBoolean());
        Assert.Equal(-1, result.GetProperty("exitCode").GetInt32());
    }

    [Fact]
    public async Task Execute_DefaultTimeout_UsesStoreConfiguredDefault()
    {
        // When timeoutMs is omitted, ExecuteAsync must honor DefaultTimeoutSeconds
        // from the store instead of the compiled-in 120s fallback.
        Configure(c => c with { Enabled = true, DefaultTimeoutSeconds = 1 });

        var command = OperatingSystem.IsWindows()
            ? "ping -n 3 127.0.0.1 > nul"
            : "sleep 3";

        // Call ExecuteAsync directly with timeoutMs omitted so it passes the parameter default.
        var json = await _tools.ExecuteAsync(command);
        var result = JsonDocument.Parse(json).RootElement.Clone();

        Assert.True(result.GetProperty("timedOut").GetBoolean());
        Assert.Equal(-1, result.GetProperty("exitCode").GetInt32());
    }

    [Fact]
    public async Task Execute_CallerCancellation_PropagatesOperationCanceledException()
    {
        // The tool is enabled so the call reaches execution: a disabled tool
        // would return shell_disabled and the cancellation would never be seen.
        Configure(c => c with { Enabled = true });

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        // A pre-cancelled caller token must abort the turn, not be reported as
        // a timedOut envelope — those two are the same OperationCanceledException
        // internally, and only the caller's token tells them apart.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => RunAsync("echo hi", ct: cts.Token));
    }

    [Fact]
    public async Task Execute_NoisyOutput_TruncatesAtTheByteCap()
    {
        Configure(c => c with { Enabled = true });

        var result = await RunAsync($"echo {new string('x', 5000)}", maxOutputBytes: 1024);

        Assert.True(result.GetProperty("truncated").GetBoolean());
        var captured = Out(result);
        Assert.NotEmpty(captured);
        Assert.True(captured.Length <= 1024, $"captured {captured.Length} chars, cap was 1024 bytes");
    }

    // ---- The cap itself -------------------------------------------------------

    [Fact]
    public void CappedTextBuffer_CapsByUtf8BytesAndFlagsTruncation()
    {
        var buffer = new CappedTextBuffer(4);

        // Three two-byte characters in a four-byte budget: the third cannot fit
        // whole, so it is dropped rather than split.
        buffer.Append("ééé");

        Assert.True(buffer.Truncated);
        Assert.Equal("éé", buffer.ToString());
    }

    [Fact]
    public void CappedTextBuffer_ExactFitIsNotTruncation()
    {
        var buffer = new CappedTextBuffer(4);

        buffer.Append("abcd");

        Assert.False(buffer.Truncated);
        Assert.Equal("abcd", buffer.ToString());
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch
        {
            // Best effort cleanup.
        }
    }
}
