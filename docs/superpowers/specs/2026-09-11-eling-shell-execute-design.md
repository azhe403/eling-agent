# Shell Execute MCP Tool — Design (Updated)

**Date:** 2026-11-09  
**Status:** Approved by user (draft ready for implementation plan)  
**Scope:** Eling backend (`Eling.Backend`, `Eling.Core`), tests in `Eling.Backend.Tests`, and dashboard UI

---

## 1. Goal

Add one explicit, opt-in MCP tool, `shell_execute`, so an agent can run a shell command line (with pipes and redirections) and read its exit code, stdout, stderr, and duration.

This tool is deliberately **RCE-equivalent**: a single call runs an arbitrary command with the privileges of the `eling-backend` process. Security controls combine configuration gates, working directory boundary controls, custom command block rules, and an active safety guardrail:
1. **Disabled by default** via dedicated global configuration file: `<user-scope>/config/shell-config.json`.
2. **Directory boundary toggle (default: restricted to cwd + whitelist)**:
   - When **enabled (`restrictToAllowedDirectories: true`)**: `workingDirectory` must be under `cwd` or in an active whitelisted directory.
   - When **disabled (`restrictToAllowedDirectories: false`)**: allow any valid existing directory on disk (`allow any`).
3. **Whitelist directories with per-path toggles & audit timestamps**: User-defined directory paths inheriting from `ShellRuleBase` (`Enabled`, `CreatedAt`, `UpdatedAt`).
4. **Custom Blocked Commands with per-rule toggles & audit timestamps**: User-defined command patterns inheriting from `ShellRuleBase` (`Enabled`, `CreatedAt`, `UpdatedAt`).
5. **Root-Protection Guardrail (ENABLED by default)**: blocks catastrophic destructive commands targeting system root drives or disk formatting (e.g. `rm -rf /`, `rmdir /s C:\`, `format C:`).
6. **Hard timeout killing the entire process tree** via `CliWrap`.
7. **Output stream capping** to protect memory and agent context.
8. **Dashboard Management UI**: full management of shell configuration, directory whitelists (with toggles), custom blocked command rules (with toggles), and security switches.

Non-goals:
- Interactive or persistent PTY sessions.
- Background jobs and daemons.

---

## 2. Global Configuration: `shell-config.json`

Location: `<user-scope>/config/shell-config.json` (resolved via `UserScope.ConfigDirectory`).

### 2.1 Models (`Eling.Core.Shell`)

```csharp
namespace Eling.Core.Shell;

public abstract record ShellRuleBase
{
    public bool Enabled { get; init; } = true;
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? UpdatedAt { get; init; }
}

/// A directory entry in the whitelist. `Path` is optional at the JSON
/// boundary: a rule without it deserializes inertly and the store drops it
/// before anyone consumes it.
public sealed record AllowedDirectoryRule : ShellRuleBase
{
    public string Path { get; init; }
}

/// A blocked-command fragment. `Pattern` is optional at the JSON boundary
/// for the same reason; the store drops empty or whitespace-only patterns.
public sealed record BlockedCommandRule : ShellRuleBase
{
    public string Pattern { get; init; }
}

public sealed record ShellConfig
{
    public bool Enabled { get; init; } = false;
    public bool RestrictToAllowedDirectories { get; init; } = true;
    public List<AllowedDirectoryRule> AllowedDirectories { get; init; } = [];
    public List<BlockedCommandRule> BlockedCommands { get; init; } = [];
    public bool RootGuardrail { get; init; } = true;
    public int DefaultTimeoutSeconds { get; init; } = 120;
    public int MaxOutputBytes { get; init; } = 65536;
    public DateTimeOffset UpdatedAt { get; init; } = DateTimeOffset.UtcNow;
}
```

### 2.2 File and Wire Formats

- **On disk** (`<user-scope>/config/shell-config.json`): snake_case-lower via `ShellJsonContext` (same `JsonSourceGenerationOptions` as `ToolPolicyJsonContext`), so a hand-editable file matches the rest of Eling's config style.
- **On the wire** (`/api/shell/config`): camelCase via the minimal-API host defaults, matching the rest of the dashboard API. The mapping between the two is mechanical (`PackWithStamp` / direct property copy), but nothing in the HTTP path uses snake_case.

```json
{
  "enabled": false,
  "restrict_to_allowed_directories": true,
  "allowed_directories": [
    {
      "path": "C:\\Projexts\\Shared",
      "enabled": true,
      "created_at": "2026-11-09T14:00:00Z",
      "updated_at": null
    }
  ],
  "blocked_commands": [
    {
      "pattern": "git checkout",
      "enabled": true,
      "created_at": "2026-11-09T14:00:00Z",
      "updated_at": null
    }
  ],
  "root_guardrail": true,
  "default_timeout_seconds": 120,
  "max_output_bytes": 65536,
  "updated_at": "2026-11-09T14:00:00Z"
}
```

*The JSON example above shows the file format (snake_case). The HTTP wire format is the same shape with camelCase keys instead: `restrictToAllowedDirectories`, `allowedDirectories`, `blockedCommands`, `rootGuardrail`, `defaultTimeoutSeconds`, `maxOutputBytes`, `createdAt`, `updatedAt`.*

- `enabled` (bool, default `false`): Master switch. When `false`, calling `shell_execute` immediately returns `{ ok: false, error: "shell_execute is disabled. Enable it in shell-config.json.", code: "shell_disabled" }`.
- `restrictToAllowedDirectories` (bool, default `true`):
  - `true`: enforces boundary. `workingDirectory` must be within `cwd` or an active whitelist entry.
  - `false`: allows any existing directory on the host (`allow any`).
- `allowedDirectories` (array of `AllowedDirectoryRule`, default `[]`):
  - Only rules with `enabled: true` are permitted outside `cwd`.
- `blockedCommands` (array of `BlockedCommandRule`, default `[]`):
  - When an active rule (`enabled: true`) matches a substring of the command (case-insensitive), execution is rejected.
- `rootGuardrail` (bool, default `true`): Guardrail against catastrophic root destruction. Enabled by default.
- `defaultTimeoutSeconds` (int, default `120`): Fallback timeout if not specified in tool call.
- `maxOutputBytes` (int, default `65536`): Fallback per-stream output cap.

### 2.3 Lifecycle & Reloading
- Managed by `ShellConfigStore`, thread-safe, reload-on-stale file stamp.
- Creates template with default values on first run if file does not exist.

---

## 3. Security Boundaries & Guardrails

### 3.1 Working Directory Validation
For any requested `workingDirectory`:
1. Resolve relative path against the launched workspace (`ShellSessionRoot`), not the filesystem/memory sandbox root.
2. Candidate path must exist on disk (`Directory.Exists`), otherwise error `not_found`.
3. If `restrictToAllowedDirectories` is `false`:
   - Any existing path is **ALLOWED**.
4. If `restrictToAllowedDirectories` is `true`:
   - Check if candidate path is under the launched workspace (`ShellSessionRoot`).
     - If YES → ALLOWED.
   - If candidate path is outside `ShellSessionRoot`:
     - Check if candidate path is under any **active** entry (`rule.Enabled == true`) in `allowedDirectories`.
     - If YES → ALLOWED.
     - If NO → REJECT with error `sandbox_violation`.

**Known limitation — symlink/junction resolution.** Containment is validated by path normalization
(`Path.GetFullPath`) followed by a case-insensitive prefix comparison. A symbolic link,
junction or reparse point inside `ShellSessionRoot` that points outside it still passes this
check when the backend process resolves it to the target. Resolving reparse points would add
I/O for every gate and is deliberately out of scope; deployments that need a hard filesystem
boundary should run Eling in a sandbox/container.

### 3.2 Root-Protection Guardrail (Enabled by Default)
Even when `workingDirectory` is valid, commands containing destructive root-level targets are intercepted before execution:
- **POSIX / Linux / macOS targets:**
  - Recursive deletion of filesystem root or root wildcard: matched with a two-lookahead pattern (recursive flag anywhere, root operand anywhere, e.g. `rm -rf /`, `rm / -rf`, `rm /\*`, `rm /.*`).
- **Windows targets:**
  - Recursive directory removal of drive root: `rd`/`rmdir` with `/s` targeting `[A-Za-z]:\` or `%SystemDrive%`/`%SystemRoot%`/`%WinDir%` (incl. quoted, no-space-switch and `C:\*` glob forms).
  - Recursive file deletion of drive root: `del` with `/s` targeting the same.
  - Disk formatting commands: `format [a-zA-Z]:`.
- When triggered, execution is aborted immediately and returns:
  `{ ok: false, error: "Command blocked by root-protection guardrail: destructive command targeting system root or disk volume.", code: "guardrail_violation" }`.

### 3.3 Custom Blocked Commands Check
- Before execution, check active rules in `blockedCommands` (`rule.Enabled == true`).
- If `command.Contains(rule.Pattern, StringComparison.OrdinalIgnoreCase)` is true for any active rule:
  - Abort execution immediately and return:
    `{ ok: false, error: "Command blocked by user policy: matches pattern '" + rule.Pattern + "'.", code: "command_blocked" }`.
- Rules with `rule.Enabled == false` are ignored, allowing temporary unblocking without deleting the rule.

---

## 4. Execution Engine

### 4.1 Platform Shell via CliWrap
CliWrap handles cross-platform process execution, streaming pipes, and process-tree termination:
- **Windows:** `Cli.Wrap(ShellExecutable).WithArguments(["/c", command])` where `ShellExecutable` resolves `%ComSpec%` (fallback `cmd.exe`).
- **Linux / macOS:** `Cli.Wrap("/bin/sh").WithArguments(["-c", command])`.

### 4.2 Timeout & Cancellation
- Linked `CancellationTokenSource` with `timeoutMs` (clamped to `[1000, 600000]`, default `120000`).
- CliWrap kills the whole process tree when the token fires.

### 4.3 Output Buffering
- Separate thread-safe `CappedTextBuffer` for stdout and stderr.
- Stream cap in bytes (clamped to `[1024, 1048576]`, default `65536`).
- Once reached, subsequent chunks are discarded and `truncated: true` is reported.

---

## 5. MCP Tool Contract — `shell_execute`

### 5.1 Parameters

| Parameter | Type | Required | Default | Description |
|---|---|---|---|---|
| `command` | string | yes | — | Shell command line to run (e.g. `git status`, `dotnet build`). |
| `workingDirectory` | string | no | `.` | Directory to run in. Relative to cwd or absolute; must be under cwd or an active whitelist path (unless boundary restriction is disabled). |
| `timeoutMs` | int | no | `120000` | Timeout in milliseconds, clamped to `[1000, 600000]`. |
| `maxOutputBytes` | int | no | `65536` | Per-stream output cap in bytes, clamped to `[1024, 1048576]`. |

### 5.2 Output JSON (Success / Data)

```json
{
  "command": "git status",
  "workingDirectory": "C:\\Projexts\\AZ\\eling-agent",
  "exitCode": 0,
  "standardOutput": "On branch main\nnothing to commit...",
  "standardError": "",
  "truncated": false,
  "timedOut": false,
  "durationMs": 95,
  "resolvedWorkingDirectory": "C:\\Projexts\\AZ\\eling-agent"
}
```

### 5.3 Error Codes (`{ ok: false, error, code }`)

Decision order (the tool's actual gate order — it is documented here so a caller
never sees an error for a weaker gate when a stronger one would fire first):

`invalid_argument` → `shell_disabled` → `guardrail_violation` → `command_blocked` → `not_found` / `invalid_argument` / `sandbox_violation` → execute.

| Code | When |
|---|---|
| `shell_disabled` | `enabled` is `false` in `shell-config.json`. |
| `guardrail_violation` | Command blocked by root-protection guardrail. |
| `command_blocked` | Command matches an active pattern in `blockedCommands`. |
| `sandbox_violation` | `workingDirectory` is outside `cwd` and not in an active `allowedDirectories` entry while boundary restriction is active. |
| `not_found` | `workingDirectory` does not exist on disk. |
| `invalid_argument` | `command` is empty/whitespace, or `workingDirectory` cannot be resolved. |
| `internal_error` | System failure during process spawning. |

Wire contract of the two code letters: `invalid_argument` is evaluated before `shell_disabled`, so a blank command returns `invalid_argument` even when the tool is off — a caller that shows this as a precondition error stays correct for an admin-only feature. The earlier spec text that claimed `shell_disabled` was always the first refusal was inaccurate.

---

## 6. Dashboard Management & API

### 6.1 Backend API Endpoints
- `GET /api/shell/config`: returns the current `ShellConfig` as camelCase JSON.
- `PUT /api/shell/config`: full replacement of the config — camelCase body; rules with blank `path`/`pattern` are dropped server-side before persisting; a rule whose `enabled` changed is stamped with the current time (`UpdatedAt`); top-level `UpdatedAt` is re-stamped. Values outside range (`defaultTimeoutSeconds < 1`, `maxOutputBytes < 1`) are rejected with **HTTP 400**. An I/O error during persistence is surfaced as **HTTP 500**, never as a stale "200 OK".
- The whitelist path manager has a **Browse…** button next to the add-field that opens the shared host-path picker (`src/components/host-path-picker.tsx`, mode `"directory"`) so a path never has to be typed by hand. The Databases SQLite-file picker was migrated onto the same shared component (mode `"file"`), deleting `SqliteFilePicker.tsx`.
- Optimistic UI: the dashboard applies the change to the UI immediately; on a server failure it refreshes state from the server (or reverts to the pre-save snapshot if the refresh itself fails) and shows the error inline — no toast.

### 6.2 Dashboard UI
- **Dedicated page** at `/dashboard/shell` (sidebar item "Shell", `SquareTerminal` icon, placed after Tools), because the shell configuration is its own subsystem with several independent controls — not a page section inside another page's card.
  - Intro card: what `shell_execute` is, that it is RCE-equivalent and disabled by default, and that the configuration file is `<user-scope>/config/shell-config.json`.
  - Master toggle switch for `shell_execute` enablement.
  - Boundary mode switch: "Restricted (cwd + whitelist)" vs "Allow any directory".
  - Whitelist Directory Manager:
    - Rows, one per path, with an individual toggle switch per path (`Enabled: true/false`), created timestamp (formatted `yyyy-mm-dd`, full datetime on hover), a delete button, and a Browse… button that opens the shared host-path picker.
    - Input box to add a new directory path.
  - Custom Blocked Commands Manager:
    - Rows, one per pattern, with an individual toggle switch per pattern (`Enabled: true/false`), created timestamp (formatted `yyyy-mm-dd`, full datetime on hover), and a delete button.
    - Input box to add a new pattern rule.
  - Root guardrail status toggle.
  - Default timeout and output cap inputs.

---

## 7. Architectural Components

1. **`Eling.Core.Shell`**:
   - `ShellRuleBase.cs`: `public abstract record ShellRuleBase { bool Enabled, DateTimeOffset CreatedAt, DateTimeOffset? UpdatedAt }`.
   - `AllowedDirectoryRule.cs`: `public sealed record AllowedDirectoryRule : ShellRuleBase { string Path }`.
   - `BlockedCommandRule.cs`: `public sealed record BlockedCommandRule : ShellRuleBase { string Pattern }`.
   - `ShellConfig.cs`: POCO record for `shell-config.json`.
2. **`Eling.Backend.Shell`**:
   - `ShellConfigStore.cs`: Loads, validates, writes template, and reloads config.
   - `ShellSessionRoot.cs`: Carries the launched workspace for `shell_execute`; this can differ from the filesystem/memory sandbox root, which still follows the nearest `.eling` scope.
   - `RootProtectionGuardrail.cs`: Pure pattern matcher checking commands against destructive root patterns.
   - `CappedTextBuffer.cs`: Thread-safe buffer capturing streams up to cap.
3. **`Eling.Backend.Mcp.Tools`**:
   - `ShellTools.cs`: `[McpServerToolType]` exposing `shell_execute`.
4. **`Eling.Backend.Endpoints`**:
   - `ShellEndpoints.cs`: Maps `GET` and `PUT /api/shell/config`.
5. **`Eling.Backend.Tools.ToolCatalog`**:
   - Registered under `DiagnosticsGroup`.
6. **`Eling.Backend.Mcp.ServerInstructions`**:
   - Documents `shell_execute` availability, disabled state, and security boundaries.

---

## 8. Verification & Test Suite

Tests in `tests/Eling.Backend.Tests/Shell/`:
- `RootProtectionGuardrailTests`:
  - Blocks `rm -rf /`, `rm -r /`, `rm -rf /*`.
  - Blocks `rd /s /q C:\`, `rmdir /s C:`, `del /s C:\`.
  - Blocks `format D:`.
  - Permits normal safe commands: `rm -rf bin/`, `git clean -fdx`, `del temp.txt`.
- `BlockedCommandsTests`:
  - Active rule `git checkout` blocks `git checkout main` with `command_blocked`.
  - Inactive rule (`enabled: false`) allows `git checkout main`.
  - Unmatched commands pass.
- `AllowedDirectoriesTests`:
  - Active path rule allows execution inside that path.
  - Inactive path rule (`enabled: false`) rejects outside execution with `sandbox_violation`.
  - *(Implementation note: these cases are covered in `ShellToolsTests` as `Execute_ActiveAllowedDirectoryRule_Succeeds` and `Execute_DisabledAllowedDirectoryRule_ReturnsSandboxViolation`; a dedicated suite file would be noise.)*
- `ShellConfigStoreTests`:
  - Template generation with defaults (`CreatedAt` set).
  - Mutation and reload on file stamp change (updating `UpdatedAt`).
- `ShellToolsTests`:
  - Disabled by default → returns `shell_disabled`.
  - Guardrail triggered → returns `guardrail_violation`.
  - Blocked command triggered → returns `command_blocked`.
  - Normal execution → exitCode 0, stdout, durationMs.
  - Boundary restriction `true` vs `false` with active/inactive whitelist rules.
  - Missing directory → `not_found`.
  - Timeout → kills process tree and sets `timedOut: true`, `exitCode: -1`.
  - Output truncation at cap.
