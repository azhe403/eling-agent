# Shell Execute MCP Tool — Design

**Date:** 2026-09-11
**Status:** Draft (pending user review)
**Scope:** Eling backend (`Eling.Backend`, `Eling.Core`) + tests in `Eling.Backend.Tests`

---

## 1. Goal

Add one explicit, opt-in MCP tool, `shell_execute`, so an agent can run a shell command inside the project workspace and read its exit code, stdout, and stderr.

This tool is deliberately **RCE-equivalent**: a single call runs an arbitrary command with the same privileges as the `eling-backend` process. It can read or delete anything the process user can, reach the network, and spawn further processes. The design therefore optimizes for *being explicit about that risk* rather than pretending the tool can be sandboxed into safety: it is disabled by default, its limits are documented, and it never uses `Process.Start` (see §7).

The non-goals are:

- Interactive or persistent shells (PTY, `stdin` streaming, session state).
- Background jobs, scheduling, or job control.
- A command allowlist or policy engine. A partial allowlist creates false confidence while missing trivial bypasses (`cmd /c`, `powershell -c`, `bash -c`, base64 payloads), so v1 relies on opt-in plus audit rather than a blocklist (see §3).
- Any change to the desktop agent, which stays shell-free by design.

---

## 2. Design summary

`IShellService` lives in `Eling.Core.Shell` with a single `ExecuteAsync` method. Two implementations exist in `Eling.Backend.Shell`: `CliWrapShellService` (the real one, built on CliWrap) and `DisabledShellService` (returns `shell_disabled` without running anything). A `ShellTools` class in `Eling.Backend/Mcp/Tools/` is the thin MCP wrapper and is auto-discovered by `WithToolsFromAssembly()`.

The service is registered by `AddElingCoreServices`. When the `ELING_ENABLE_SHELL` environment variable is not exactly `1`, the DI container resolves the disabled implementation, so the tool is visible to an agent but inert — calling it returns `{ "ok": false, "code": "shell_disabled" }` and nothing executes. When enabled, the real CliWrap implementation is wired. This keeps zero execution surface by default while giving a clear, actionable signal instead of a missing-tool surprise.

Every command runs with its working directory resolved under `ProjectScope.Root`, a hard timeout that kills the process tree, and byte caps on stdout and stderr. Every invocation is logged (command, resolved working directory, exit code, duration, truncation) so a human can audit what the agent ran.

The command string is executed through the platform shell (`cmd.exe /c` on Windows, `/bin/sh -c` elsewhere) so pipes and redirection work. That shell interpretation is exactly the RCE surface described in §1; it is intentional and gated by the opt-in flag.

---

## 3. Threat model and enablement

| Control | Behavior | Why |
|---|---|---|
| Default state | Disabled. `DisabledShellService` is registered unless `ELING_ENABLE_SHELL=1`. | Zero execution surface out of the box. |
| Enable flag | `ELING_ENABLE_SHELL=1` (exact match, matching the repo's `ELING_*` env convention). | One explicit, greppable switch. |
| Working directory | Resolved with the existing sandbox rule (under `ProjectScope.Root`); any escape throws `sandbox_violation`. | Prevents an accidental `cwd` outside the repo. |
| Filesystem boundary | **Not a security boundary.** A command may still reference absolute paths outside the root, e.g. `type C:\Users\...`. | The sandbox is a convenience, not a jail; claiming otherwise would be false confidence. The real gate is the opt-in flag. |
| Timeout | Hard cap, process tree killed on expiry. | Prevents hangs and orphaned children. |
| Output caps | stdout and stderr each capped; excess is dropped and flagged `truncated`. | Protects the agent context window and backend memory. |
| Audit | Every call logged at Information (success) or Warning (timeout, truncation, non-zero exit). | Makes agent behavior reviewable after the fact. |
| Allowlist | None. | A blocklist/allowlist would be trivially bypassed and would imply safety that does not exist. |

The honest framing for users: enabling this tool is equivalent to giving the agent a terminal on their machine. It is appropriate for a local, single-user dev workspace the user trusts, and inappropriate for shared or remote backends.

---

## 4. Components

### 4.1 `IShellService` (Core)

Location: `src/backend/Eling.Core/Shell/IShellService.cs`

```csharp
namespace Eling.Core.Shell;

public interface IShellService
{
    Task<ShellResult> ExecuteAsync(ShellRequest request, CancellationToken cancellationToken = default);
}
```

Result and request records live one-per-file in `Eling.Core.Shell`:

```csharp
public sealed record ShellRequest(
    string Command,
    string WorkingDirectory,
    int TimeoutMs,
    int MaxOutputBytes);

public sealed record ShellResult(
    int ExitCode,
    string StandardOutput,
    string StandardError,
    bool Truncated,
    bool TimedOut,
    long DurationMs,
    string ResolvedWorkingDirectory);
```

### 4.2 `CliWrapShellService` (Backend)

Location: `src/backend/Eling.Backend/Shell/CliWrapShellService.cs`

Uses `CliWrap.Cli.Wrap(shell)` with the command as an argument, `WithWorkingDirectory`, a linked `CancellationTokenSource` for the timeout, and `PipeTarget.ToDelegate` writers that append to bounded `StringBuilder`s. On timeout it cancels and kills the process tree. It takes the project root (for cwd resolution) and an `ILogger` through its constructor, mirroring `FileSystemService`.

### 4.3 `DisabledShellService` (Backend)

Location: `src/backend/Eling.Backend/Shell/DisabledShellService.cs`

Implements `IShellService` by returning a `ShellResult` that the tool maps to `shell_disabled`. No process is involved.

### 4.4 `ShellTools` (Backend MCP wrapper)

Location: `src/backend/Eling.Backend/Mcp/Tools/ShellTools.cs`

`[McpServerToolType]` with one `[McpServerTool]` method, `shell_execute`. It clamps caller-controlled limits, calls the service, and serialises JSON with the same camelCase options as `FileSystemTools`. Auto-discovered by `WithToolsFromAssembly()`.

### 4.5 DI wiring

`AddElingCoreServices` registers `IShellService` in both overloads. The path-based overload uses `projectScope.Root`; the scope-chain overload uses `chain.Head?.Root ?? chain.Cwd`, matching how `IFileSystemService` is wired:

```csharp
services.TryAddSingleton<IShellService>(sp =>
    Environment.GetEnvironmentVariable("ELING_ENABLE_SHELL") == "1"
        ? new CliWrapShellService(root, sp.GetRequiredService<ILogger<CliWrapShellService>>())
        : new DisabledShellService());
```

---

## 5. Tool contract — `shell_execute`

| Parameter | Type | Required | Default | Description |
|---|---|---|---|---|
| `command` | string | yes | — | Shell command line. |
| `workingDirectory` | string | no | `.` | Absolute or project-relative directory; resolved under the sandbox root. |
| `timeoutMs` | int | no | `30000` | Hard timeout; wrapper clamps to `[1000, 300000]`. |
| `maxOutputBytes` | int | no | `65536` | Per-stream cap; wrapper clamps to `[1024, 1048576]`. |

Returns JSON:

```json
{
  "exitCode": 0,
  "standardOutput": "total 12\n...",
  "standardError": "",
  "truncated": false,
  "timedOut": false,
  "durationMs": 84,
  "resolvedWorkingDirectory": "C:\\some-folder\\Eling"
}
```

Error codes reuse the existing convention (`{ ok: false, error, code }`):

| Code | When |
|---|---|
| `shell_disabled` | The tool was called while `ELING_ENABLE_SHELL` is not `1`. |
| `sandbox_violation` | `workingDirectory` resolves outside the project root. |
| `not_found` | `workingDirectory` does not exist. |
| `invalid_argument` | Empty `command`, or a `workingDirectory` that is empty. |
| `internal_error` | Any other failure (process could not start, I/O). |

A non-zero `exitCode` is **not** an error: it is reported as data so the agent can branch on it. A timeout is reported as data (`timedOut: true`) with whatever output was captured.

---

## 6. Execution semantics

- **Shell selection:** `cmd.exe /c <command>` on Windows, `/bin/sh -c <command>` otherwise. `Cli.Wrap` is given the shell executable and the command as arguments; CliWrap handles escaping.
- **Timeout:** a `CancellationTokenSource` linked with the caller token, cancelled after `timeoutMs`. On cancellation CliWrap kills the shell process it started; `TimedOut` is set and the exit code is reported as `-1`. Process-tree kill (grandchildren spawned by the shell) is a known limitation and is deferred (see §10).
- **Output:** stdout and stderr are captured separately through `PipeTarget.ToDelegate` into bounded buffers. Once a stream reaches `maxOutputBytes`, further chunks are dropped and `Truncated` is set. Buffers are never allowed to grow past the cap.
- **Environment:** the child inherits the backend process environment. v1 adds no per-call environment overrides, to keep the contract small; this is recorded as a deferred item (§10).
- **Concurrency:** each call is independent; there is no shared shell state. The service is registered as a singleton but holds no mutable per-call state.

---

## 7. Process.Start carve-outs (explicit)

The rule for this feature: **`shell_execute` never uses `Process.Start`; it goes through CliWrap.** CliWrap is the repo default for command invocation (see `FrontendDevSpawner`). The following `Process.Start` usages remain, intentionally, and are *not* migrated by this work:

| Location | Purpose | Why `Process.Start` stays |
|---|---|---|
| `src/desktop/Eling.Desktop/Services/BackendSupervisor.cs` | Spawns and supervises the backend process; holds the handle to stop it later. | Long-lived child supervision, not a run-to-completion command. |
| `src/backend/Eling.Backend/Bootstrap/FrontendDevSpawner.cs` (`GetPidsListeningOnPort`) | Runs `netstat.exe -ano -p TCP` to find port owners. | Trivial synchronous probe with a hard `WaitForExit(2000)`; async CliWrap is unnecessary churn here. |
| `tests/Eling.Backend.Tests/TestProcesses.cs` | Test harness that starts the real backend binary. | Needs a live process handle and controlled teardown. |
| `tests/Eling.Backend.Tests/MemoryProjectToolsTests.cs` | `git init` in a scratch dir. | One-shot setup helper; behavior-neutral to leave as-is. |
| `scripts/*.ps1` (`validate-eling.ps1`, `test-single-mode.ps1`, `sim-mcp-stdio.ps1`, `publish-global.ps1`) | PowerShell harnesses. | Outside the .NET app; not part of this tool. |

If any of these later needs output piping, cancellation, or exit-code handling, migrate it to CliWrap then. Until then they are documented carve-outs, not oversights.

---

## 8. Defaults and limits

| Setting | Default | Where set | Notes |
|---|---|---|---|
| Enabled | `false` | `ELING_ENABLE_SHELL` env | Exact `1` to enable. |
| Timeout | 30000 ms | per call | Wrapper clamps to `[1000, 300000]`. |
| Output cap | 65536 bytes per stream | per call | Wrapper clamps to `[1024, 1048576]`. |
| Working directory | project root | per call | Resolved under the sandbox root. |

---

## 9. Tests

- **`ShellToolsTests`** (fake `IShellService`): success shape, `shell_disabled` mapping, `sandbox_violation`, `invalid_argument` for empty command, clamping of `timeoutMs`/`maxOutputBytes`, camelCase serialization.
- **`CliWrapShellServiceTests`** (real service, temp cwd): exit code captured for a failing command, stdout/stderr separated, timeout sets `timedOut` and kills a long-running command, output truncation flag, cwd escape throws `sandbox_violation`.
- **`DisabledShellServiceTests`**: `ExecuteAsync` returns a `shell_disabled` result without spawning anything.
- Existing 246-test backend suite must stay green.

---

## 10. Out of scope (deferred)

- Per-call environment variables and secret redaction.
- Streaming/partial output while the command runs, and background job control.
- A persisted shell audit log (the filesystem tools defer durable destructive audit separately; shell audit is v1 log-only).
- Cross-platform PTY support.
- Command allowlist/policy enforcement.
- Process-tree kill on timeout (grandchildren started by the shell may survive cancellation).

---

## 11. Files added / changed

**Added:**

- `src/backend/Eling.Core/Shell/IShellService.cs`
- `src/backend/Eling.Core/Shell/ShellRequest.cs`
- `src/backend/Eling.Core/Shell/ShellResult.cs`
- `src/backend/Eling.Backend/Shell/CliWrapShellService.cs`
- `src/backend/Eling.Backend/Shell/DisabledShellService.cs`
- `src/backend/Eling.Backend/Mcp/Tools/ShellTools.cs`
- `tests/Eling.Backend.Tests/Shell/FakeShellService.cs`
- `tests/Eling.Backend.Tests/Shell/ShellToolsTests.cs`
- `tests/Eling.Backend.Tests/Shell/CliWrapShellServiceTests.cs`
- `tests/Eling.Backend.Tests/Shell/DisabledShellServiceTests.cs`

**Changed:**

- `src/backend/Eling.Backend/Mcp/McpServiceExtensions.cs` — register `IShellService` in both `AddElingCoreServices` overloads.
- `src/backend/Eling.Backend/Mcp/ServerInstructions.cs` — document that `shell_execute` exists, is opt-in, and is RCE-equivalent.

---

## 12. Open questions

- Whether to hide the tool entirely when disabled (via an MCP tool filter) instead of registering an inert `DisabledShellService`. The inert approach is simpler and gives a clear `shell_disabled` signal; hiding it avoids a tool the agent may keep retrying. Needs an SDK check during planning.
- Whether `ServerInstructions` should warn on every session or only when the flag is on.
