# Shell Execute MCP Tool Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add an opt-in MCP tool `shell_execute` that runs a command line through the platform shell, disabled by default via a global `shell-config.json`, manageable from the dashboard.

**Architecture:** Dedicated global config file (`<user-scope>/config/shell-config.json`) managed by `ShellConfigStore` (thread-safe, reload-on-stale, template-on-first-run). The MCP wrapper `ShellTools` validates `workingDirectory` against the cwd + active whitelist (or any existing dir when restriction is off), intercepts destructive root-targeting commands via `RootProtectionGuardrail` and active `blockedCommands` rules, then executes through `CliWrap` with a linked `CancellationTokenSource` timeout and per-stream capped buffers. The dashboard gets `GET/PUT /api/shell/config` plus a config card on the Tools page with per-rule toggles.

**Tech Stack:** .NET 10 (C# 14, Native AOT publishing for the backend), ModelContextProtocol.Server, CliWrap, xUnit 2.9, Next.js dashboard (React 19, shadcn-style components).

**Spec:** `docs/superpowers/specs/2026-09-11-eling-shell-execute-design.md` — read it first; this plan argues from it.

## Global Constraints

- Target framework `net10.0`; `ImplicitUsings` and `Nullable` enabled repo-wide.
- One top-level type per file; filename equals the type name; namespace follows the folder.
- Backend publishes with Native AOT: **every type serialized to JSON must be registered in a `JsonSerializerContext`** (mirror `Tools/ToolPolicyJsonContext.cs` — snake_case on the wire).
- CliWrap is the only command-invocation library for this tool. Never use `Process.Start` in the shell service or its tests.
- Tests run per csproj with `--artifacts-path .bin-test`. Never run solution-wide, never chain test commands with `;` or `&&`. Run each shell step separately.
- Every `catch` must write a log (`ILogger` in backend code).
- **Commits are user-controlled (project rule).** Do NOT run `git commit`. Each task ends with a report checkpoint (`git status --short`) and stops for the user.
- Feature gate: `enabled` in `shell-config.json`, default `false`. `rootGuardrail` default `true`. `restrictToAllowedDirectories` default `true`.
- Non-zero `exitCode` is a **normal data result**, never an error envelope. A timeout reports `timedOut: true` and `exitCode: -1`.

## Review Focus

Failure modes the spec implies but does not test explicitly — most likely first. Each is pinned to an owning task below.

1. **`workingDirectory = "../.."` escapes the workspace through `Path.GetFullPath`** — boundary check must run on the resolved absolute path. → Task 4 (ShellTools tests: `ExecuteAsync_RelativePathEscape_ReturnsSandboxViolation`).
2. **Inactive whitelist entry (`enabled: false`) still lets an outside path through** — only `Enabled == true` rules permit paths outside cwd. → Task 4 (`ExecuteAsync_WhitelistRuleDisabled_ReturnsSandboxViolation`).
3. **Blocked command rule with empty pattern** matches everything (`"".Contains("")`) — store must sanitize/skip empty patterns. → Task 3 (store test `ReadConfig_IgnoresEmptyPatterns`, `ReadConfig_IgnoresEmptyPaths`).
4. **Empty `command` reaching `cmd /c ""`** — must return `invalid_argument` before touching the OS. → Task 4 (`ExecuteAsync_WhitespaceCommand_ReturnsInvalidArgument`).
5. **Guardrail false positive on ordinary commands** — `rm -rf bin/`, `rd /s bin`, `git clean -fdx` must pass. → Task 2 (`Guardrail_AllowsOrdinaryDestructiveCommands`).
6. **Non-zero exit interpreted as failure by an over-eager error path** — pinned by Task 4 (`ExecuteAsync_FailingCommand_ReturnsDataNotError`).

---

### Task 1: Core config models and JSON context

**Files:**
- Create: `src/backend/Eling.Core/Shell/ShellRuleBase.cs`
- Create: `src/backend/Eling.Core/Shell/AllowedDirectoryRule.cs`
- Create: `src/backend/Eling.Core/Shell/BlockedCommandRule.cs`
- Create: `src/backend/Eling.Core/Shell/ShellConfig.cs`
- Create: `src/backend/Eling.Backend/Shell/ShellJsonContext.cs`
- Test: `tests/Eling.Backend.Tests/Shell/ShellConfigSerializationTests.cs`

**Interfaces:**
- Produces: `ShellRuleBase` (abstract record: `Enabled { get; init; } = true`, `CreatedAt { get; init; } = DateTimeOffset.UtcNow`, `UpdatedAt { get; init; }`), `AllowedDirectoryRule : ShellRuleBase { required string Path }`, `BlockedCommandRule : ShellRuleBase { required string Pattern }`, `ShellConfig { bool Enabled = false; bool RestrictToAllowedDirectories = true; List<AllowedDirectoryRule> AllowedDirectories = []; List<BlockedCommandRule> BlockedCommands = []; bool RootGuardrail = true; int DefaultTimeoutSeconds = 120; int MaxOutputBytes = 65536; DateTimeOffset UpdatedAt }`. `ShellJsonContext` with snake_case-lower naming, `PropertyNameCaseInsensitive = true`, `WriteIndented = true`, `DefaultIgnoreCondition = WhenWritingNull`, `[JsonSerializable]` for `ShellConfig` and `List<BlockedCommandRule>`/`List<AllowedDirectoryRule>` handling (register `ShellConfig` only — nested lists are covered by the source generator once the root is registered).

- [ ] **Step 1: Write the failing test**

`ShellConfigSerializationTests.cs`: deserialize the spec §2.2 JSON example into `ShellConfig` and assert `Enabled == false`, `RestrictToAllowedDirectories == true`, the one allowed-directory rule has `Path == "C:\\Projexts\\Shared"` (or the unix form), `Enabled == true`, `CreatedAt` set; round-trip serialize produces `enabled`, `restrict_to_allowed_directories`, `created_at`, `updated_at` snake_case keys; an absent `blockedCommands` key deserializes to an empty list.

- [ ] **Step 2: Run it to verify it fails**

Run: `dotnet test tests/Eling.Backend.Tests/Eling.Backend.Tests.csproj --artifacts-path .bin-test --filter "FullyQualifiedName~ShellConfigSerializationTests"`
Expected: build FAILURE — the types do not exist.

- [ ] **Step 3: Implement the four record files and `ShellJsonContext`** (one type per file, per Global Constraints). Use `required` + `init` only; do not add behavior beyond records.
- [ ] **Step 4: Run the test to verify it passes** — same command, expected PASS.
- [ ] **Step 5: Report checkpoint** — `git status --short`, then stop and report. Do not commit.

---

### Task 2: RootProtectionGuardrail

**Files:**
- Create: `src/backend/Eling.Backend/Shell/RootProtectionGuardrail.cs`
- Test: `tests/Eling.Backend.Tests/Shell/RootProtectionGuardrailTests.cs`

**Interfaces:**
- Consumes: nothing.
- Produces: `public static class RootProtectionGuardrail` with `public static bool IsBlocked(string command)` and `public static string? BlockedReason(string command)` returning a short human-readable reason when blocked, `null` otherwise. Behavior per spec §3.2: block recursive-delete / recursive-remove / `del /s` / `format` targeting `/`, `/*`, `C:\` (any drive letter), `%SystemDrive%`, `%SystemRoot%`, `%WinDir%`.

- [ ] **Step 1: Write the failing tests** covering the spec's blocked set (`rm -rf /`, `rm -rf /*`, `rm -r /`, `rd /s /q C:\`, `rmdir /s C:`, `del /s C:\`, `del /s /q D:`, `format D:`) and the traversal-irrelevant leniency set (`rm -rf bin/`, `rm -rf ./obj`, `rd /s bin`, `git clean -fdx`, `del temp.txt`) asserting the first group returns true from `IsBlocked` and the second returns false (Review Focus 5). Use `[Theory]` with `[InlineData]`.
- [ ] **Step 2: Run it to verify it fails** — filter `FullyQualifiedName~RootProtectionGuardrailTests`; expected build FAILURE.
- [ ] **Step 3: Implement `IsBlocked`/`BlockedReason`** with compiled regexes (`RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant`) covering POSIX root variants and Windows drive/env-var variants. Match, never execute the pattern matching with untrusted input beyond the string itself.
- [ ] **Step 4: Run the test to verify it passes.**
- [ ] **Step 5: Report checkpoint.**

---

### Task 3: ShellConfigStore

**Files:**
- Create: `src/backend/Eling.Backend/Shell/ShellConfigStore.cs`
- Test: `tests/Eling.Backend.Tests/Shell/ShellConfigStoreTests.cs`

**Interfaces:**
- Consumes: `ShellConfig`, `AllowedDirectoryRule`, `BlockedCommandRule`, `ShellJsonContext` (Task 1), `UserScope` (Eling.Core.Scope), `ILogger<ShellConfigStore>`.
- Produces: `ShellConfigStore(UserScope userScope, ILogger<ShellConfigStore> logger)`, `GetConfig() -> ShellConfig`, `Update(Func<ShellConfig, ShellConfig> mutate) -> ShellConfig` (creates `UpdatedAt = DateTimeOffset.UtcNow`, persists atomically temp-file-then-move, swaps the snapshot only after the write succeeds), `ToggleAllowedDirectory(string path, bool enabled)`, `ToggleBlockedCommand(string pattern, bool enabled)` (flips `Enabled`, sets `UpdatedAt`). File: `<userScope.ConfigDirectory>/shell-config.json`. Sanitization on read (like `ToolPolicyStore.ReadConfig`): trim and drop rules whose `Path`/`Pattern` is null or whitespace after trim (Review Focus 3). Corrupt file: keep last good snapshot, log Warning, adopt the stamp like `ToolPolicyStore`. Template on first run: `new ShellConfig()` (defaults from Task 1).

Mirror `ToolPolicyStore`'s gate/RefreshIfStale/ReadStamp/Write structure exactly — same idiom, smaller surface.

- [ ] **Step 1: Write the failing tests** — template creation defaults (`Enabled == false`, `RootGuardrail == true`, `RestrictToAllowedDirectories == true`), round-trip of `Update` mutation persisted to disk, externally-written file observed after stamp change, corrupt file keeps last good snapshot, `ReadConfig` ignores empty `Path` and empty `Pattern` entries, `ToggleBlockedCommand` flips only the matching rule and stamps `UpdatedAt`.
- [ ] **Step 2: Run to verify failure.**
- [ ] **Step 3: Implement `ShellConfigStore`.** Use `ShellJsonContext.Default.ShellConfig` for (de)serialization.
- [ ] **Step 4: Run to verify pass.**
- [ ] **Step 5: Report checkpoint.**

---

### Task 4: `shell_execute` MCP tool

**Files:**
- Create: `src/backend/Eling.Backend/Mcp/Tools/ShellTools.cs`
- Create: `src/backend/Eling.Backend/Shell/CappedTextBuffer.cs`
- Test: `tests/Eling.Backend.Tests/Shell/ShellToolsTests.cs`

**Interfaces:**
- Consumes: `IFileSystemService` (`WorkspaceRoot`), `ShellConfigStore`, `RootProtectionGuardrail`, `CappedTextBuffer`, `ILogger<ShellTools>`, CliWrap.
- Produces: `[McpServerToolType] public sealed class ShellTools`, ctor `(IFileSystemService fs, ShellConfigStore store, ILogger<ShellTools>? logger = null)`; one `[McpServerTool(Name = "shell_execute"), Description(...)]` method `ExecuteAsync(string command, string workingDirectory = ".", int timeoutMs = 120000, int maxOutputBytes = 65536)`.

Decision order inside `ExecuteAsync` (each returns a `{ ok: false, error, code }` envelope on the matching path):
1. Empty/whitespace command → `invalid_argument` (Review Focus 4).
2. `!GetConfig().Enabled` → `shell_disabled`.
3. `RootGuardrail.IsBlocked(command)` when `GetConfig().RootGuardrail == true` → `guardrail_violation`.
4. First active `blockedCommands` rule whose pattern is non-empty and is a case-insensitive substring of `command` → `command_blocked`.
5. Resolve `workingDirectory`: relative → against `WorkspaceRoot`, absolute → as-is; `Path.GetFullPath` normalized. `!Directory.Exists` → `not_found`. If `RestrictToAllowedDirectories`: candidate must be under `WorkspaceRoot` or under an **active** `AllowedDirectoryRule.Path` (case-insensitive, ordinal), else `sandbox_violation`.
6. Execute: `Cli.Wrap(Windows ? %ComSpec% ?? "cmd.exe" : "/bin/sh").WithArguments([shellSwitch, command]).WithWorkingDirectory(cwd).WithStandardOutputPipe(ToDelegate(line => outBuffer.Append(line).Append('\n'))).WithStandardErrorPipe(...).WithValidation(CommandResultValidation.None).ExecuteAsync(linkedCts.Token)`; `timeoutMs` linked to `CancellationTokenSource` with `CancelAfter`. On `OperationCanceledException` where the linked source fired and the caller token did not → `timedOut: true`, `exitCode: -1`. Stopwatch measures `DurationMs`. `CappedTextBuffer` drops bytes past `maxOutputBytes` and sets `Truncated`.
7. Success envelope (camelCase serialization with the same `JsonSerializerOptions` pattern as `FileSystemTools`): `command`, `workingDirectory` (the resolved absolute path), `exitCode`, `standardOutput`, `standardError`, `truncated`, `timedOut`, `durationMs`, `resolvedWorkingDirectory`. **Non-zero exit is data, not an error.**

`CappedTextBuffer`: thread-safe via `lock`, `Append(string)`, `Truncated { get; }`, `ToString()`, drops chunks that would exceed the cap and flags `Truncated`.

- [ ] **Step 1: Write the failing tests** — all spec §8 ShellToolsTests cases (disabled default → `shell_disabled`; enabled echo → exit 0 + stdout + `durationMs > 0`; failing command → exitCode 2 as data; stdout/stderr separated; `../outside` escape → `sandbox_violation` (Review Focus 1); whitelist rule disabled → `sandbox_violation`, whitelist rule enabled → succeeds (Review Focus 2); unrestricted mode (`RestrictToAllowedDirectories = false`) allows outside path; missing dir → `not_found`; guardrail → `guardrail_violation`; blocked rule enabled → `command_blocked`, disabled → passes; whitespace command → `invalid_argument` (Review Focus 4); timeout on `ping -n 6 127.0.0.1 > nul` (Windows) / `sleep 5` (unix) → `timedOut: true`, `exitCode: -1`; noisy loop output at `maxOutputBytes: 1024` → `truncated: true`). Tests toggle config through a real `ShellConfigStore` on a temp `UserScope` — no mocks of the store.
- [ ] **Step 2: Run to verify failure.**
- [ ] **Step 3: Implement `CappedTextBuffer`, then `ShellTools.ExecuteAsync`** following the decision order exactly.
- [ ] **Step 4: Run to verify pass.**
- [ ] **Step 5: Report checkpoint.**

---

### Task 5: DI wiring, ToolCatalog, ServerInstructions

**Files:**
- Modify: `src/backend/Eling.Backend/Mcp/McpServiceExtensions.cs` (both `AddElingCoreServices` overloads, immediately after the `ToolPolicyStore` registration in each)
- Modify: `src/backend/Eling.Backend/Tools/ToolCatalog.cs` (add one line to `All`, group `DiagnosticsGroup`)
- Modify: `src/backend/Eling.Backend/Mcp/ServerInstructions.cs` (append one entry after the diagnostics entry)
- Test: covered by existing `ToolCatalogTests` (must stay green) plus `tests/Eling.Backend.Tests/Shell/ShellToolsRegistrationTests.cs` asserting `ShellTools` is auto-discovered: `typeof(ShellTools).GetCustomAttribute<McpServerToolTypeAttribute>()` is non-null and the tool's `Name == "shell_execute"`.

**Interfaces:**
- Consumes: `ShellConfigStore` (Task 3),`ShellTools` (Task 4).
- Produces: DI registration pattern identical to `ToolPolicyStore` but for `ShellConfigStore`:
  `services.AddSingleton(sp => new ShellConfigStore(userScope, sp.GetService<ILoggerFactory>()?.CreateLogger<ShellConfigStore>() ?? NullLogger<ShellConfigStore>.Instance));`
  ServerInstructions entry text (one sentence): “`shell_execute` runs a shell command in the workspace. It is disabled unless enabled in `<user-scope>/config/shell-config.json` (or the dashboard Tools page); when disabled it returns `{ ok: false, code: "shell_disabled" }`. Enabling it is RCE-equivalent: restrict paths and rules deliberately.”

- [ ] **Step 1: Write the failing registration test.**
- [ ] **Step 2: Run to verify failure.**
- [ ] **Step 3: Apply the three modifications.**
- [ ] **Step 4: Run `dotnet test tests/Eling.Backend.Tests/Eling.Backend.Tests.csproj --artifacts-path .bin-test`** — full suite green, including `ToolCatalogTests.Catalog_ContainsEveryRegisteredMcpServerTool` (catches a forgotten catalog entry or typo).
- [ ] **Step 5: Report checkpoint.**

---

### Task 6: Dashboard API endpoints

**Files:**
- Create: `src/backend/Eling.Backend/Endpoints/ShellEndpoints.cs`
- Modify: `src/backend/Eling.Backend/Bootstrap/DashboardRoutes.cs` (add `app.MapShellEndpoints();` next to `app.MapToolsEndpoints();`)
- Create: `src/backend/Eling.Backend/Endpoints/Dtos/ShellConfigDto.cs` only if the existing DTO convention lives in an Endpoints/Dtos folder — check `Dtos` folder convention first; if DTOs are inline records beside the endpoints, keep them in `ShellEndpoints.cs` (one file, existing pattern wins).
- Test: `tests/Eling.Backend.Tests/Shell/ShellEndpointsTests.cs` following the test style of `LogsEndpoints`/`ToolsEndpoints` tests if any exist; otherwise unit-test the store path the endpoints call and assert the handlers' status codes via a minimal `WebApplication` harness only if the repo already does that — otherwise rely on Task 3 store tests and do a manual smoke check.

**Interfaces:**
- Consumes: `ShellConfigStore`, `ShellConfig`.
- Produces: `GET /api/shell/config` → 200 with the full config JSON (camelCase, enums not involved); `PUT /api/shell/config` → full-replacement semantics: body is the complete `ShellConfig`; validate non-empty patterns trimmed, drop empty rules, stamp `UpdatedAt` on every rule whose `Enabled` changed plus top-level `UpdatedAt`. Reject `defaultTimeoutSeconds < 1` or `maxOutputBytes < 1` with 400. Response is the persisted config.

- [ ] **Step 1: Write the failing endpoint tests** — GET on empty state returns the default template; PUT with toggled `enabled: true` persists and round-trips; PUT with out-of-range values returns 400; PUT drops rules with empty `path`/`pattern`.
- [ ] **Step 2: Run to verify failure.**
- [ ] **Step 3: Implement the endpoints** mirroring `ToolsEndpoints` (TypedResults, `Results.BadRequest` with the same message style).
- [ ] **Step 4: Run to verify pass.**
- [ ] **Step 5: Report checkpoint.**

---

### Task 7: Dashboard Tools page config card

**Files:**
- Create: `src/frontend/Eling.Dashboard/src/app/dashboard/tools/ShellConfigCard.tsx`
- Modify: `src/frontend/Eling.Dashboard/src/app/dashboard/tools/Tools.tsx` (import and render `<ShellConfigCard onNotify={notify} />` above the tool groups list; expose `notify` as a prop)
- Next.js note: this repo pins a custom Next.js — before writing the component, read the relevant guide under `node_modules/next/dist/docs/` for `use client`, fetch patterns and any breaking changes (AGENTS.md in the dashboard folder). Use existing components only (`Card`, `CardHeader`, `CardTitle`, `CardDescription`, `Switch`, `Input`, `Button`, `Badge`, `Trash2` icon from lucide-react). No new dependencies.

**Slice to build:**
- On mount, `fetch("/api/shell/config")` into state; render:
  - Master switch "Enable shell_execute".
  - Switch "Restrict to workspace & whitelist" vs "Allow any directory" (bound to `restrictToAllowedDirectories`, inverse semantics, obvious labels).
  - Switch "Root protection guardrail" (`rootGuardrail`).
  - Two manager lists sharing one small local component inside the file: each row shows the rule's main text, `enabled` toggle, `createdAt` date (`toLocaleDateString()`), delete button. Input + "Add" button appends `{ path/pattern, enabled: true, createdAt: now ISO, updatedAt: null }`. Remove deletes the entry.
  - Inputs for `defaultTimeoutSeconds` and `maxOutputBytes`.
  - Every mutation sends the **whole updated config** to `PUT /api/shell/config` and replaces local state with the response. On 4xx, surface the error text through `notify("error", …)` and revert via re-`GET`.

- [ ] **Step 1: Implement the card and wire it into Tools.tsx.** Keep the file under ~250 lines; if the two manager lists duplicate more than a few lines, extract a local `RuleList` helper component in the same file.
- [ ] **Step 2: Type-check only** — `pnpm --filter eling-dashboard exec tsc --noEmit`. Do **not** run `pnpm build` or `next build` while the 4427 dev server may be running (project AGENTS.md).
- [ ] **Step 3: Manual smoke** — start backend (`dotnet run` on the backend project with `ELING_*` defaults), load `/dashboard/tools`, toggle enable off→on, add a blocked command rule, toggle it, delete it, confirm persistence across a browser refresh and by reading `<user-scope>/config/shell-config.json`.
- [ ] **Step 4: Report checkpoint** — `git status --short`, then stop and report. Do not commit.

---

### Task 8: Whole-branch verification

- [ ] Run the per-csproj backend suite: `dotnet test tests/Eling.Backend.Tests/Eling.Backend.Tests.csproj --artifacts-path .bin-test` — all tests green, zero regressions.
- [ ] Frontend type-check: `pnpm --filter eling-dashboard exec tsc --noEmit` — clean.
- [ ] Docs consistency: re-read the spec and check no section is now stale (e.g., if NestJS or the toggle naming drifted). Adjust the spec in the same branch only if needed.
- [ ] Report the complete change list (`git status --short` + one-paragraph summary) and stop for commit instructions. Do not commit.
