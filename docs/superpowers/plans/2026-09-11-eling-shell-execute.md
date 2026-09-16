# Shell Execute MCP Tool Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add one opt-in, agent-facing MCP tool `shell_execute` that runs a command through the platform shell and returns its exit code, stdout, and stderr.

**Architecture:** `IShellService` lives in `Eling.Core.Shell`. Two Backend implementations exist: `CliWrapShellService` (CLI via CliWrap) and `DisabledShellService` (inert). `ShellServiceFactory` picks between them on `ELING_ENABLE_SHELL=1`. `ShellTools` is the thin MCP wrapper. The working directory is validated with the existing `IFileSystemService.TestPath`, reusing the project-root sandbox rule. `shell_execute` never uses `Process.Start`.

**Tech Stack:** .NET 10 (C# 14), ModelContextProtocol.Server, CliWrap 3.10.5, xUnit 2.9, Microsoft.Extensions.DependencyInjection.

## Global Constraints

- Target framework `net10.0`; `ImplicitUsings` and `Nullable` are enabled repo-wide.
- One top-level type per file; filename equals the type name; namespace follows the folder (`Eling.Core.Shell`, `Eling.Backend.Shell`, `Eling.Backend.Mcp.Tools`).
- Every `catch` must write a log (`ILogger` in backend code).
- CliWrap is the only command-invocation library for this tool. Never use `Process.Start` in the shell service or its tests.
- Tests run per csproj with `--artifacts-path .bin-test`. Never run solution-wide, never chain test commands with `;` or `&&`.
- Run each shell step separately. Do not chain commands with `&&`.
- **Commits are user-controlled (project rule).** Do NOT run `git commit`. Each task ends with a report checkpoint (`git status --short`) and stops for the user.
- Feature flag: exact string `ELING_ENABLE_SHELL=1` enables the tool. Any other value (including unset) leaves it disabled.
- The tool's working directory is resolved through `IFileSystemService.TestPath`, so it obeys the same sandbox root as the filesystem tools.

---

### Task 1: Inert tool surface (contract, disabled service, MCP wrapper)

Delivers the full MCP surface with zero execution: calling `shell_execute` returns `shell_disabled`.

**Files:**
- Create: `src/backend/Eling.Core/Shell/IShellService.cs`
- Create: `src/backend/Eling.Core/Shell/ShellRequest.cs`
- Create: `src/backend/Eling.Core/Shell/ShellResult.cs`
- Create: `src/backend/Eling.Core/Exceptions/ShellDisabledException.cs`
- Create: `src/backend/Eling.Backend/Shell/DisabledShellService.cs`
- Create: `src/backend/Eling.Backend/Mcp/Tools/ShellTools.cs`
- Test: `tests/Eling.Backend.Tests/Shell/FakeShellService.cs`
- Test: `tests/Eling.Backend.Tests/Shell/ShellToolsTests.cs`

**Interfaces:**
- Consumes: `Eling.Core.Exceptions.PathSandboxException` (existing); `ModelContextProtocol.Server` attributes.
- Produces: `IShellService.ExecuteAsync(ShellRequest, CancellationToken = default) -> Task<ShellResult>`; `ShellRequest(string Command, string WorkingDirectory = ".", int TimeoutMs = 30000, int MaxOutputBytes = 65536)`; `ShellResult(int ExitCode, string StandardOutput, string StandardError, bool Truncated, bool TimedOut, long DurationMs, string ResolvedWorkingDirectory)`; `ShellDisabledException`; `DisabledShellService`; `ShellTools(IShellService, ILogger<ShellTools>? = null)`.

- [ ] **Step 1: Create the Core contract types**

`src/backend/Eling.Core/Shell/IShellService.cs`
```csharp
namespace Eling.Core.Shell;

public interface IShellService
{
    Task<ShellResult> ExecuteAsync(ShellRequest request, CancellationToken cancellationToken = default);
}
```

`src/backend/Eling.Core/Shell/ShellRequest.cs`
```csharp
namespace Eling.Core.Shell;

public sealed record ShellRequest(
    string Command,
    string WorkingDirectory = ".",
    int TimeoutMs = 30000,
    int MaxOutputBytes = 65536);
```

`src/backend/Eling.Core/Shell/ShellResult.cs`
```csharp
namespace Eling.Core.Shell;

public sealed record ShellResult(
    int ExitCode,
    string StandardOutput,
    string StandardError,
    bool Truncated,
    bool TimedOut,
    long DurationMs,
    string ResolvedWorkingDirectory);
```

`src/backend/Eling.Core/Exceptions/ShellDisabledException.cs`
```csharp
namespace Eling.Core.Exceptions;

/// <summary><c>shell_execute</c> when ELING_ENABLE_SHELL is not 1.</summary>
public sealed class ShellDisabledException()
    : InvalidOperationException("shell_execute is disabled. Set ELING_ENABLE_SHELL=1 to enable it.");
```

- [ ] **Step 2: Write the fake and the failing wrapper tests**

`tests/Eling.Backend.Tests/Shell/FakeShellService.cs`
```csharp
using Eling.Core.Shell;

namespace Eling.Backend.Tests.Shell;

internal sealed class FakeShellService : IShellService
{
    public ShellRequest? LastRequest;
    public Func<ShellRequest, ShellResult>? Handler;

    public Task<ShellResult> ExecuteAsync(ShellRequest request, CancellationToken cancellationToken = default)
    {
        LastRequest = request;
        var result = Handler is null
            ? new ShellResult(0, "ok", "", false, false, 1, "/fake")
            : Handler(request);
        return Task.FromResult(result);
    }
}
```

`tests/Eling.Backend.Tests/Shell/ShellToolsTests.cs`
```csharp
using System.Text.Json;
using Eling.Backend.Mcp.Tools;
using Eling.Core.Exceptions;
using Eling.Core.Shell;

namespace Eling.Backend.Tests.Shell;

public class ShellToolsTests
{
    private static ShellTools CreateTool(FakeShellService? service = null)
        => new(service ?? new FakeShellService());

    private static JsonDocument Parse(string json) => JsonDocument.Parse(json);

    private static string CodeOf(string errorJson)
        => Parse(errorJson).RootElement.GetProperty("code").GetString()!;

    [Fact]
    public async Task ExecuteAsync_Success_ReturnsResultJson()
    {
        var service = new FakeShellService
        {
            Handler = _ => new ShellResult(0, "hello", "", false, false, 12, "/root")
        };
        var tool = CreateTool(service);

        var root = Parse(await tool.ExecuteAsync("echo hello")).RootElement;

        Assert.Equal(0, root.GetProperty("exitCode").GetInt32());
        Assert.Equal("hello", root.GetProperty("standardOutput").GetString());
        Assert.False(root.GetProperty("timedOut").GetBoolean());
        Assert.Equal("/root", root.GetProperty("resolvedWorkingDirectory").GetString());
    }

    [Fact]
    public async Task ExecuteAsync_NonZeroExit_IsDataNotError()
    {
        var service = new FakeShellService
        {
            Handler = _ => new ShellResult(2, "", "boom", false, false, 5, "/root")
        };
        var tool = CreateTool(service);

        var root = Parse(await tool.ExecuteAsync("false")).RootElement;

        Assert.Equal(2, root.GetProperty("exitCode").GetInt32());
        Assert.False(root.TryGetProperty("ok", out _));
    }

    [Fact]
    public async Task ExecuteAsync_Disabled_ReturnsShellDisabled()
    {
        var service = new FakeShellService { Handler = _ => throw new ShellDisabledException() };
        var tool = CreateTool(service);

        Assert.Equal("shell_disabled", CodeOf(await tool.ExecuteAsync("echo hi")));
    }

    [Fact]
    public async Task ExecuteAsync_SandboxViolation_ReturnsSandboxError()
    {
        var service = new FakeShellService
        {
            Handler = _ => throw new PathSandboxException("/outside", "/root")
        };
        var tool = CreateTool(service);

        Assert.Equal("sandbox_violation", CodeOf(await tool.ExecuteAsync("echo hi", workingDirectory: "../x")));
    }

    [Fact]
    public async Task ExecuteAsync_MissingWorkingDirectory_ReturnsNotFound()
    {
        var service = new FakeShellService { Handler = _ => throw new DirectoryNotFoundException("nope") };
        var tool = CreateTool(service);

        Assert.Equal("not_found", CodeOf(await tool.ExecuteAsync("echo hi", workingDirectory: "missing")));
    }

    [Fact]
    public async Task ExecuteAsync_EmptyCommand_ReturnsInvalidArgument()
    {
        var tool = CreateTool();

        Assert.Equal("invalid_argument", CodeOf(await tool.ExecuteAsync("   ")));
    }

    [Theory]
    [InlineData(0, 1000)]
    [InlineData(10, 1000)]
    [InlineData(999999, 300000)]
    public async Task ExecuteAsync_ClampsTimeout(int input, int expected)
    {
        var service = new FakeShellService();
        var tool = CreateTool(service);

        await tool.ExecuteAsync("echo hi", timeoutMs: input);

        Assert.Equal(expected, service.LastRequest!.TimeoutMs);
    }

    [Theory]
    [InlineData(1, 1024)]
    [InlineData(99999999, 1048576)]
    public async Task ExecuteAsync_ClampsOutputCap(int input, int expected)
    {
        var service = new FakeShellService();
        var tool = CreateTool(service);

        await tool.ExecuteAsync("echo hi", maxOutputBytes: input);

        Assert.Equal(expected, service.LastRequest!.MaxOutputBytes);
    }

    [Fact]
    public async Task ExecuteAsync_PassesCommandAndWorkingDirectory()
    {
        var service = new FakeShellService();
        var tool = CreateTool(service);

        await tool.ExecuteAsync("git status", workingDirectory: "src");

        Assert.Equal("git status", service.LastRequest!.Command);
        Assert.Equal("src", service.LastRequest!.WorkingDirectory);
    }
}
```

- [ ] **Step 3: Run the tests to verify they fail**

Run: `dotnet test tests/Eling.Backend.Tests/Eling.Backend.Tests.csproj --artifacts-path .bin-test --filter "FullyQualifiedName~Eling.Backend.Tests.Shell"`
Expected: build FAILURE — `ShellTools` and `DisabledShellService` do not exist yet.

- [ ] **Step 4: Implement the disabled service and the wrapper**

`src/backend/Eling.Backend/Shell/DisabledShellService.cs`
```csharp
using Eling.Core.Exceptions;
using Eling.Core.Shell;

namespace Eling.Backend.Shell;

public sealed class DisabledShellService : IShellService
{
    public Task<ShellResult> ExecuteAsync(ShellRequest request, CancellationToken cancellationToken = default)
        => Task.FromException<ShellResult>(new ShellDisabledException());
}
```

`src/backend/Eling.Backend/Mcp/Tools/ShellTools.cs`
```csharp
using System.ComponentModel;
using System.Text.Json;
using Eling.Core.Exceptions;
using Eling.Core.Shell;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

namespace Eling.Backend.Mcp.Tools;

[McpServerToolType]
public sealed class ShellTools
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly IShellService _shell;
    private readonly ILogger<ShellTools>? _logger;

    public ShellTools(IShellService shell, ILogger<ShellTools>? logger = null)
    {
        _shell = shell;
        _logger = logger;
    }

    [McpServerTool(Name = "shell_execute"), Description("Run a shell command in the project workspace and return its exit code, stdout, and stderr. Disabled unless ELING_ENABLE_SHELL=1. RCE-equivalent: enable only in a workspace the user trusts.")]
    public async Task<string> ExecuteAsync(
        [Description("Shell command line to run.")] string command,
        [Description("Absolute or project-relative working directory. Defaults to the project root.")] string workingDirectory = ".",
        [Description("Hard timeout in milliseconds, clamped to [1000, 300000].")] int timeoutMs = 30000,
        [Description("Per-stream output cap in bytes, clamped to [1024, 1048576].")] int maxOutputBytes = 65536)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(command))
            {
                throw new ArgumentException("Command cannot be empty.", nameof(command));
            }

            var request = new ShellRequest(
                command,
                workingDirectory,
                Clamp(timeoutMs, 1000, 300000),
                Clamp(maxOutputBytes, 1024, 1048576));
            var result = await _shell.ExecuteAsync(request);
            return JsonSerializer.Serialize(result, JsonOptions);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "shell_execute failed");
            return ErrorJson(ex);
        }
    }

    private static string ErrorJson(Exception ex)
    {
        var code = ex switch
        {
            ShellDisabledException => "shell_disabled",
            PathSandboxException => "sandbox_violation",
            FileNotFoundException or DirectoryNotFoundException => "not_found",
            ArgumentException => "invalid_argument",
            _ => "internal_error"
        };
        return JsonSerializer.Serialize(new { ok = false, error = ex.Message, code }, JsonOptions);
    }

    private static int Clamp(int value, int min, int max)
        => Math.Min(Math.Max(value, min), max);
}
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test tests/Eling.Backend.Tests/Eling.Backend.Tests.csproj --artifacts-path .bin-test --filter "FullyQualifiedName~Eling.Backend.Tests.Shell"`
Expected: PASS (12 tests).

- [ ] **Step 6: Report checkpoint**

Run: `git status --short`
Then stop and report. Do not commit.

---

### Task 2: Bounded text buffer

Caps captured output per stream so a command cannot flood the backend or the agent context.

**Files:**
- Create: `src/backend/Eling.Backend/Shell/BoundedTextBuffer.cs`
- Test: `tests/Eling.Backend.Tests/Shell/BoundedTextBufferTests.cs`

**Interfaces:**
- Consumes: nothing.
- Produces: `BoundedTextBuffer(int maxBytes)` with `bool Truncated { get; }`, `void Append(string text)`, `override string ToString()`.

- [ ] **Step 1: Write the failing test**

`tests/Eling.Backend.Tests/Shell/BoundedTextBufferTests.cs`
```csharp
using Eling.Backend.Shell;

namespace Eling.Backend.Tests.Shell;

public class BoundedTextBufferTests
{
    [Fact]
    public void Append_WithinCap_KeepsTextAndNotTruncated()
    {
        var buffer = new BoundedTextBuffer(64);

        buffer.Append("hello ");
        buffer.Append("world");

        Assert.Equal("hello world", buffer.ToString());
        Assert.False(buffer.Truncated);
    }

    [Fact]
    public void Append_ExceedsCap_DropsChunkAndFlagsTruncated()
    {
        var buffer = new BoundedTextBuffer(8);

        buffer.Append("12345");
        buffer.Append("67890");

        Assert.Equal("12345", buffer.ToString());
        Assert.True(buffer.Truncated);
    }

    [Fact]
    public void Append_AfterTruncated_IsIgnored()
    {
        var buffer = new BoundedTextBuffer(4);
        buffer.Append("abcd");
        buffer.Append("x");

        buffer.Append("more");

        Assert.Equal("abcd", buffer.ToString());
        Assert.True(buffer.Truncated);
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test tests/Eling.Backend.Tests/Eling.Backend.Tests.csproj --artifacts-path .bin-test --filter "FullyQualifiedName~BoundedTextBufferTests"`
Expected: build FAILURE — `BoundedTextBuffer` does not exist.

- [ ] **Step 3: Write the minimal implementation**

`src/backend/Eling.Backend/Shell/BoundedTextBuffer.cs`
```csharp
using System.Text;

namespace Eling.Backend.Shell;

internal sealed class BoundedTextBuffer
{
    private readonly int _maxBytes;
    private readonly StringBuilder _builder = new();
    private int _bytes;

    public BoundedTextBuffer(int maxBytes) => _maxBytes = Math.Max(0, maxBytes);

    public bool Truncated { get; private set; }

    public void Append(string text)
    {
        if (Truncated || string.IsNullOrEmpty(text))
        {
            return;
        }

        var incoming = Encoding.UTF8.GetByteCount(text);
        if (_bytes + incoming > _maxBytes)
        {
            Truncated = true;
            return;
        }

        _builder.Append(text);
        _bytes += incoming;
    }

    public override string ToString() => _builder.ToString();
}
```

- [ ] **Step 4: Run the test to verify it passes**

Run: `dotnet test tests/Eling.Backend.Tests/Eling.Backend.Tests.csproj --artifacts-path .bin-test --filter "FullyQualifiedName~BoundedTextBufferTests"`
Expected: PASS (3 tests).

- [ ] **Step 5: Report checkpoint**

Run: `git status --short`
Then stop and report. Do not commit.

---

### Task 3: CliWrap shell service

Runs the command through the platform shell with CliWrap, resolves the working directory through the sandbox, and reports exit code plus capped output.

**Files:**
- Create: `src/backend/Eling.Backend/Shell/CliWrapShellService.cs`
- Test: `tests/Eling.Backend.Tests/Shell/CliWrapShellServiceTests.cs`

**Interfaces:**
- Consumes: `IShellService`, `ShellRequest`, `ShellResult`, `BoundedTextBuffer` (Task 2), `IFileSystemService.TestPath` and `PathInfo`/`PathKind` (existing), `PathSandboxException` (existing).
- Produces: `CliWrapShellService(IFileSystemService fileSystem, ILogger<CliWrapShellService>? logger = null)`.

- [ ] **Step 1: Write the failing test**

`tests/Eling.Backend.Tests/Shell/CliWrapShellServiceTests.cs`
```csharp
using Eling.Backend.FileSystem;
using Eling.Backend.Shell;
using Eling.Core;
using Eling.Core.Exceptions;
using Eling.Core.FileSystem;
using Eling.Core.Shell;

namespace Eling.Backend.Tests.Shell;

public sealed class CliWrapShellServiceTests : IDisposable
{
    private readonly string _root;
    private readonly CliWrapShellService _service;

    public CliWrapShellServiceTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "eling-shell-" + Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(_root);
        _service = new CliWrapShellService(new FileSystemService(_root));
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best-effort temp cleanup.
        }
    }

    private static ShellRequest Request(string command, int timeoutMs = 30000, int maxOutputBytes = 65536)
        => new(command, ".", timeoutMs, maxOutputBytes);

    [Fact]
    public async Task ExecuteAsync_SimpleCommand_ReturnsStdoutAndZeroExit()
    {
        var result = await _service.ExecuteAsync(Request("echo hello"));

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("hello", result.StandardOutput);
        Assert.False(result.TimedOut);
    }

    [Fact]
    public async Task ExecuteAsync_FailingCommand_ReturnsNonZeroExit()
    {
        var result = await _service.ExecuteAsync(Request("exit 3"));

        Assert.Equal(3, result.ExitCode);
        Assert.False(result.TimedOut);
    }

    [Fact]
    public async Task ExecuteAsync_Stderr_IsCapturedSeparately()
    {
        var result = await _service.ExecuteAsync(Request("echo problem 1>&2"));

        Assert.Contains("problem", result.StandardError);
    }

    [Fact]
    public async Task ExecuteAsync_Timeout_SetsTimedOutAndNegativeExit()
    {
        var slow = OperatingSystem.IsWindows() ? "ping -n 6 127.0.0.1 > nul" : "sleep 5";

        var result = await _service.ExecuteAsync(Request(slow, timeoutMs: 1000));

        Assert.True(result.TimedOut);
        Assert.Equal(-1, result.ExitCode);
    }

    [Fact]
    public async Task ExecuteAsync_OutputOverCap_SetsTruncated()
    {
        var noisy = OperatingSystem.IsWindows()
            ? "for /L %i in (1,1,500) do @echo 0123456789"
            : "for i in $(seq 1 500); do echo 0123456789; done";

        var result = await _service.ExecuteAsync(Request(noisy, maxOutputBytes: 1024));

        Assert.True(result.Truncated);
    }

    [Fact]
    public async Task ExecuteAsync_WorkingDirectoryEscape_ThrowsPathSandboxException()
    {
        await Assert.ThrowsAsync<PathSandboxException>(
            () => _service.ExecuteAsync(new ShellRequest("echo hi", "../outside", 30000, 65536)));
    }

    [Fact]
    public async Task ExecuteAsync_MissingWorkingDirectory_ThrowsDirectoryNotFound()
    {
        await Assert.ThrowsAsync<DirectoryNotFoundException>(
            () => _service.ExecuteAsync(new ShellRequest("echo hi", "missing", 30000, 65536)));
    }

    [Fact]
    public async Task ExecuteAsync_ResolvedWorkingDirectory_IsUnderRoot()
    {
        var result = await _service.ExecuteAsync(Request("echo hi"));

        Assert.StartsWith(_root, result.ResolvedWorkingDirectory, StringComparison.OrdinalIgnoreCase);
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test tests/Eling.Backend.Tests/Eling.Backend.Tests.csproj --artifacts-path .bin-test --filter "FullyQualifiedName~CliWrapShellServiceTests"`
Expected: build FAILURE — `CliWrapShellService` does not exist.

- [ ] **Step 3: Write the implementation**

`src/backend/Eling.Backend/Shell/CliWrapShellService.cs`
```csharp
using System.Diagnostics;
using CliWrap;
using Eling.Core;
using Eling.Core.FileSystem;
using Eling.Core.Shell;
using Microsoft.Extensions.Logging;

namespace Eling.Backend.Shell;

public sealed class CliWrapShellService : IShellService
{
    private readonly IFileSystemService _fileSystem;
    private readonly ILogger<CliWrapShellService>? _logger;

    public CliWrapShellService(IFileSystemService fileSystem, ILogger<CliWrapShellService>? logger = null)
    {
        _fileSystem = fileSystem;
        _logger = logger;
    }

    public async Task<ShellResult> ExecuteAsync(ShellRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Command);
        var cwd = ResolveWorkingDirectory(request.WorkingDirectory);
        var stdout = new BoundedTextBuffer(request.MaxOutputBytes);
        var stderr = new BoundedTextBuffer(request.MaxOutputBytes);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(request.TimeoutMs);
        var stopwatch = Stopwatch.StartNew();

        var shell = OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/sh";
        var shellArguments = OperatingSystem.IsWindows()
            ? new[] { "/c", request.Command }
            : new[] { "-c", request.Command };

        try
        {
            var result = await Cli.Wrap(shell)
                .WithArguments(shellArguments)
                .WithWorkingDirectory(cwd)
                .WithStandardOutputPipe(PipeTarget.ToDelegate(line => stdout.Append(line + "\n")))
                .WithStandardErrorPipe(PipeTarget.ToDelegate(line => stderr.Append(line + "\n")))
                .WithValidation(CommandResultValidation.None)
                .ExecuteAsync(timeout.Token);
            stopwatch.Stop();
            _logger?.LogInformation(
                "shell_execute '{Command}' in {Cwd} exited {ExitCode} in {DurationMs}ms",
                request.Command, cwd, result.ExitCode, stopwatch.ElapsedMilliseconds);
            return new ShellResult(
                result.ExitCode, stdout.ToString(), stderr.ToString(),
                stdout.Truncated || stderr.Truncated, false, stopwatch.ElapsedMilliseconds, cwd);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            stopwatch.Stop();
            _logger?.LogWarning(
                "shell_execute timed out after {TimeoutMs}ms: '{Command}'",
                request.TimeoutMs, request.Command);
            return new ShellResult(
                -1, stdout.ToString(), stderr.ToString(),
                stdout.Truncated || stderr.Truncated, true, stopwatch.ElapsedMilliseconds, cwd);
        }
    }

    private string ResolveWorkingDirectory(string workingDirectory)
    {
        var info = _fileSystem.TestPath(string.IsNullOrWhiteSpace(workingDirectory) ? "." : workingDirectory);
        if (!info.Exists)
        {
            throw new DirectoryNotFoundException($"Working directory '{info.ResolvedPath}' does not exist.");
        }

        if (info.Kind != PathKind.Directory)
        {
            throw new ArgumentException("Working directory is not a directory.", nameof(workingDirectory));
        }

        return info.ResolvedPath;
    }
}
```

- [ ] **Step 4: Run the test to verify it passes**

Run: `dotnet test tests/Eling.Backend.Tests/Eling.Backend.Tests.csproj --artifacts-path .bin-test --filter "FullyQualifiedName~CliWrapShellServiceTests"`
Expected: PASS (8 tests).

- [ ] **Step 5: Report checkpoint**

Run: `git status --short`
Then stop and report. Do not commit.

---

### Task 4: Factory, DI wiring, and server instructions

Makes the tool selectable by `ELING_ENABLE_SHELL=1`, wires it into both `AddElingCoreServices` overloads, and tells MCP clients the tool exists.

**Files:**
- Create: `src/backend/Eling.Backend/Shell/ShellServiceFactory.cs`
- Modify: `src/backend/Eling.Backend/Mcp/McpServiceExtensions.cs` (both overloads)
- Modify: `src/backend/Eling.Backend/Mcp/ServerInstructions.cs` (`Sections` array)
- Test: `tests/Eling.Backend.Tests/Shell/ShellServiceFactoryTests.cs`

**Interfaces:**
- Consumes: `CliWrapShellService`, `DisabledShellService`, `IFileSystemService`, `ILogger<CliWrapShellService>`.
- Produces: `ShellServiceFactory.Create(string? enableFlag, IFileSystemService fileSystem, ILogger<CliWrapShellService>? logger = null) -> IShellService`.

- [ ] **Step 1: Write the failing test**

`tests/Eling.Backend.Tests/Shell/ShellServiceFactoryTests.cs`
```csharp
using Eling.Backend.FileSystem;
using Eling.Backend.Shell;
using Eling.Core.FileSystem;

namespace Eling.Backend.Tests.Shell;

public sealed class ShellServiceFactoryTests : IDisposable
{
    private readonly string _root;
    private readonly FileSystemService _fileSystem;

    public ShellServiceFactoryTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "eling-shell-factory-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_root);
        _fileSystem = new FileSystemService(_root);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best-effort temp cleanup.
        }
    }

    [Fact]
    public void Create_Enabled_ReturnsCliWrapService()
    {
        var service = ShellServiceFactory.Create("1", _fileSystem);

        Assert.IsType<CliWrapShellService>(service);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("0")]
    [InlineData("true")]
    public void Create_NotEnabled_ReturnsDisabledService(string? flag)
    {
        var service = ShellServiceFactory.Create(flag, _fileSystem);

        Assert.IsType<DisabledShellService>(service);
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test tests/Eling.Backend.Tests/Eling.Backend.Tests.csproj --artifacts-path .bin-test --filter "FullyQualifiedName~ShellServiceFactoryTests"`
Expected: build FAILURE — `ShellServiceFactory` does not exist.

- [ ] **Step 3: Write the factory**

`src/backend/Eling.Backend/Shell/ShellServiceFactory.cs`
```csharp
using Eling.Core.FileSystem;
using Eling.Core.Shell;
using Microsoft.Extensions.Logging;

namespace Eling.Backend.Shell;

public static class ShellServiceFactory
{
    public static IShellService Create(
        string? enableFlag,
        IFileSystemService fileSystem,
        ILogger<CliWrapShellService>? logger = null)
        => enableFlag == "1"
            ? new CliWrapShellService(fileSystem, logger)
            : new DisabledShellService();
}
```

- [ ] **Step 4: Wire the factory into both DI overloads**

In `src/backend/Eling.Backend/Mcp/McpServiceExtensions.cs`, add the usings:
```csharp
using Eling.Backend.Shell;
using Eling.Core.Shell;
using Microsoft.Extensions.Logging;
```

In the path-based `AddElingCoreServices` overload, immediately after the `IFileSystemService` registration, add:
```csharp
services.TryAddSingleton<IShellService>(sp =>
    ShellServiceFactory.Create(
        Environment.GetEnvironmentVariable("ELING_ENABLE_SHELL"),
        sp.GetRequiredService<IFileSystemService>(),
        sp.GetService<ILogger<CliWrapShellService>>()));
```

In the scope-chain `AddElingCoreServices` overload, immediately after the `IFileSystemService` registration (`new FileSystemService(chain.Head?.Root ?? chain.Cwd)`), add the same block.

- [ ] **Step 5: Document the tool in server instructions**

In `src/backend/Eling.Backend/Mcp/ServerInstructions.cs`, append one entry to the `Sections` array (after the provenance entry at the end of the list):
```csharp
"`shell_execute` runs a shell command in the project workspace and is RCE-equivalent. It is disabled unless the environment variable `ELING_ENABLE_SHELL=1`; when disabled it returns `{ ok: false, code: \"shell_disabled\" }`. Enable it only in a workspace the user trusts, never on a shared or remote backend."
```

Add a test in `tests/Eling.Backend.Tests/Shell/ShellServiceFactoryTests.cs`:
```csharp
    [Fact]
    public void ServerInstructions_MentionShellExecute()
    {
        Assert.Contains(Eling.Backend.Mcp.ServerInstructions.Sections, s => s.Contains("shell_execute"));
    }
```

- [ ] **Step 6: Run the tests to verify they pass**

Run: `dotnet test tests/Eling.Backend.Tests/Eling.Backend.Tests.csproj --artifacts-path .bin-test --filter "FullyQualifiedName~Eling.Backend.Tests.Shell"`
Expected: PASS (all shell tests, including the factory and instructions tests).

- [ ] **Step 7: Verify no regressions in the full backend suite**

Run: `dotnet test tests/Eling.Backend.Tests/Eling.Backend.Tests.csproj --artifacts-path .bin-test`
Expected: PASS (previously 246 tests plus the new shell tests; failures 0).

- [ ] **Step 8: Report checkpoint**

Run: `git status --short`
Then stop and report. Do not commit.

---

## Self-Review

**1. Spec coverage**
- §1 Goal / §2 Design summary → Tasks 1, 3, 4.
- §3 Threat model + enablement (flag, cwd sandbox, timeout, output caps, audit log) → Task 4 (flag/factory), Task 3 (cwd, timeout, caps, logging).
- §4 Components (`IShellService`, `CliWrapShellService`, `DisabledShellService`, `ShellTools`, DI) → Tasks 1, 3, 4.
- §5 Tool contract + error codes → Task 1 (wrapper, mapping, clamping) and Task 3 (not_found, sandbox_violation).
- §6 Execution semantics (shell selection, timeout, output, environment inheritance, concurrency) → Task 3.
- §7 Process.Start carve-outs → documentation only; no task (nothing to implement).
- §8 Defaults and limits → Task 1 clamping + Task 3 defaults.
- §9 Tests → Tasks 1–4 tests.
- §10 Deferred → no task (explicitly out of scope).
- §11 Files added/changed → matches task file lists.

**2. Placeholder scan:** every step carries real code, real commands, and expected outcomes; no TBD/TODO.

**3. Type consistency:** `ShellRequest` positional order `(Command, WorkingDirectory, TimeoutMs, MaxOutputBytes)` is used consistently in Task 1 (wrapper), Task 3 (tests and service), and Task 4. `ShellResult` positional order `(ExitCode, StandardOutput, StandardError, Truncated, TimedOut, DurationMs, ResolvedWorkingDirectory)` is consistent across Tasks 1 and 3. `CliWrapShellService(IFileSystemService, ILogger<CliWrapShellService>?)` matches its use in `ShellServiceFactory` and Task 4 DI.
