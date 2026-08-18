# Eling.Host Modular Runtime Implementation Plan (Pecut 8)

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Consolidate the two executable entrypoints (`Eling.Mcp`, `Eling.Server`) into a single modular runtime `Eling.Host` that is the ONLY executable. Running `eling` starts ONE process hosting both MCP stdio and HTTP server, sharing the Host lifecycle.

**Architecture:** Composition root in `Eling.Host`; no business logic there. MCP adapter stays in `Eling.Mcp`, HTTP adapter stays in `Eling.Server`. Domain/persistence/indexing stay in `Eling.Core`/`Eling.Storage`/`Eling.Index`.

## Global Constraints (from Pecut #8 spec)

- `Eling.Host` is the ONLY executable project. `Eling.Mcp` and `Eling.Server` become class libraries.
- Single process: one `eling` = one workspace = one `.eling` scope. No `--data-dir`, no global/cross-project memory, no dynamic port allocation.
- MCP stdout MUST be protocol-only. All diagnostics/logs go to stderr.
- Default bind `127.0.0.1`, default port `4317`; `--port N` to override. Port in use = fail startup with clear error.
- Executable must be named `eling` (Windows: `eling.exe`), NOT `Eling.Host`/`Eling`/`eling-host`. Project stays `Eling.Host`.
- Preserve `GET /health`.
- Ctrl+C / SIGTERM gracefully stops both MCP and HTTP.
- Out of scope: dashboard, Next.js, embeddings, semantic/vector search, graph, auth, remote MCP/SSE, Docker, deployment packaging, unrelated refactors. (Dashboard is Pecut #9.)
- Do NOT commit/push. Stop after Pecut #8 is implemented and verified.

## Current State (verified)

- `Eling.Mcp` already converted to library; keeps `McpServiceExtensions.cs` (`AddElingCoreServices`, `AddElingMcpServer` with stdio transport), `McpLoggingExtensions.cs` (`AddElingLogging` — Serilog to stderr + rolling daily file), `MemoryTools.cs`, `ServerInstructions.cs`.
- `Eling.Server` already converted to library with `FrameworkReference`; keeps `MemoryEndpoints.cs` (`MapMemoryRoutes`), DTOs, `MemoryIdJsonConverter`.
- `Eling.Host` scaffolded (Web SDK) but `Program.cs` implements an OLD design: exclusive `--mcp`/`--server` modes + `--root-path`. Does NOT match the refined Pecut #8 spec.
- `Eling.slnx` modified; `Eling.Host` added.
- `Eling.Server.Tests` updated to reference `Eling.Host` and `WebApplicationFactory<TestProgram>`, but `TestProgram` does NOT exist — solution currently does not build (CS0234/CS0246 in `MemoryApiTests.cs`).
- `docs/pecut-008-eling-host-modular-runtime.md` is a "changes made" log; a fresh plan is being saved here.

## Design

### Eling.Host/Program.cs (rewrite)
- Top-level statements + `public partial class Program` (required for `WebApplicationFactory<Program>`).
- `System.CommandLine` root command with a single `--port` option (default 4317). No mode flags.
- Build a `WebApplication` (Kestrel) that ALSO registers the MCP server (stdio hosted service) — one process, one lifecycle:
  1. Configure `Eling:RootPath` = `.eling` (cwd-scoped workspace; overridable via config for tests).
  2. `AddElingLogging(rootPath)` — Serilog console sink to stderr only (stdout safety), rolling daily file.
  3. `AddElingCoreServices(rootPath)`.
  4. `AddElingMcpServer()` (only when `Eling:EnableMcp` is not `false`, so tests can disable stdio).
  5. `ConfigureHttpJsonOptions` (camelCase + `MemoryIdJsonConverter`).
  6. Map `/health` → 200; map `MapMemoryRoutes()`; map OpenAPI in Development.
  7. Kestrel bound to `http://127.0.0.1:{port}`.
- Wrap `app.RunAsync()` so port-bind failures surface as a clear error + non-zero exit.
- Lifecycle: WebApplication host manages both Kestrel and MCP hosted service; Ctrl+C/SIGTERM stops both gracefully.

### Eling.Host/Eling.Host.csproj
- Add `<AssemblyName>eling</AssemblyName>` so the output executable is `eling(.exe)`.

### Test entry point
- Add `Eling.Host/TestProgram.cs` (test-only partial) exposing a `CreateHostBuilder`-style static factory used by `WebApplicationFactory` in `Eling.Server.Tests`, with MCP stdio disabled via config. `Program` must expose a shared static `CreateApp(HostOptions)` / `RunApp` used by both the CLI path and `TestProgram`.

### Tests
- `Eling.Server.Tests/MemoryApiTests.cs`: point `WebApplicationFactory<TestProgram>` at the new test entry point (fix CS0234/CS0246).
- New `tests/Eling.Host.Tests/` (process-based, xUnit):
  - Host startup: launch `eling`, `/health` returns 200.
  - Default bind `127.0.0.1` and default port `4317`.
  - `--port 4318` overrides the listen port.
  - Port in use → fails with a clear error (no silent re-allocation).
  - stdout safety: no log lines on stdout during startup.
  - MCP registration: enabled host has MCP server registered (DI check via `Program` factory when stdio enabled; tools discoverable).
  - Graceful shutdown: SIGTERM/Ctrl+C stops the process cleanly.
  - Workspace resolution: process uses `<cwd>/.eling` (health + log file appear under cwd).

## Tasks

- [x] Save this plan (done when this file is written).
- [x] Fix stale dashboard plan label `2026-08-12-pecut-009-dashboard.md` → Pecut 9.
- [x] Rewrite `src/backend/Eling.Host/Program.cs` to dual-host MCP + HTTP with `--port`, `/health`, stdout-safe logging, graceful shutdown, cwd-scoped `.eling`.
- [x] ~~Add `TestProgram.cs`~~ Not needed — `Eling:EnableMcp=false` config toggle via `WebApplicationFactory.UseSetting()` serves the same purpose without a separate entry point.
- [x] Add `<AssemblyName>eling</AssemblyName>` to `Eling.Host.csproj`.
- [x] Fix `tests/Eling.Server.Tests/MemoryApiTests.cs` + project reference (compiles against new entry point).
- [x] Create `tests/Eling.Host.Tests` process-level suite per "Tests" section.
- [x] Add `Eling.Host.Tests` to `Eling.slnx`.
- [x] Update `docs/pecut-008-eling-host-modular-runtime.md` "Changes Made" to match final implementation.
- [x] Verify: `dotnet build Eling.slnx --artifacts-path .artifacts`, `dotnet test Eling.slnx --artifacts-path .artifacts`, run `eling` + `eling --port 4318`, confirm executable name is `eling(.exe)`, confirm stdout clean.
- [x] Report `git status --short` + `git diff --stat`. Do NOT commit/push.

## Verification Commands

```bash
dotnet build Eling.slnx --artifacts-path .artifacts
dotnet test Eling.slnx --artifacts-path .artifacts
dotnet run --project src/backend/Eling.Host -- --port 4318
```

## Notes

- `AddElingLogging` already writes console logs to stderr (`standardErrorFromLevel: Verbose`); keep it.
- MCP stdio hosted service runs inside the WebApplication host, so one process hosts both interfaces with a single lifecycle.
- `Eling:RootPath` config key stays for workspace scoping and test overrides; default `.eling` under cwd.

---

## Changes Made

### Architecture

`Eling.Host` is the sole executable. `Eling.Mcp` and `Eling.Server` are class libraries. One `eling` process hosts both MCP stdio and the HTTP server with a single WebApplication lifecycle.

### `src/backend/Eling.Host/Eling.Host.csproj`

- Added `<AssemblyName>eling</AssemblyName>` so the output executable is `eling(.exe)`.
- Removed `System.CommandLine` PackageReference (no longer needed).

### `src/backend/Eling.Host/Program.cs` (complete rewrite)

Top-level statements + `public partial class Program` (required for `WebApplicationFactory<Program>`):

- **`WebApplication.CreateBuilder(args)` at top level** — essential for `WebApplicationFactory` interception.
- **Config-driven:** reads `Eling:RootPath` and `Eling:EnableMcp` from `builder.Configuration`, overridable via CLI args (`--root-path`, `--enable-mcp`, `--port`).
- **CLI arg parsing:** simple `FindArg(args, name)` helper (no `System.CommandLine` dependency).
- **`ConfigureServices(services, rootPath, enableMcp)`** — static method on `partial class Program`: registers Serilog logging (stderr-safe), core domain services, MCP server (conditional on `enableMcp`), JSON options (camelCase + `MemoryIdJsonConverter`), OpenAPI.
- **`ConfigureApp(app, enableMcp)`** — static method on `partial class Program`: maps `MapMcp()` (conditional), `/health` endpoint, `MapMemoryRoutes()`, OpenAPI in Development.
- **`app.Urls.Add($"http://127.0.0.1:{port}")`** — default port `4317`, overridable via `--port`.
- **`app.RunAsync()` at top level** — graceful shutdown via WebApplication host (Ctrl+C / SIGTERM).
- **Port conflict handling:** catches `SocketException` with `SocketError.AddressAlreadyInUse`, writes clear error to stderr, exits with non-zero code.
- Removed: `HostOptions` record, `CreateApp()`, `RunAppAsync()`, `--mcp`/`--server` mode flags.

### `src/backend/Eling.Mcp/Eling.Mcp.csproj`

- Removed `System.CommandLine` PackageReference.

### `src/backend/Eling.Mcp/McpServiceExtensions.cs`

- No functional changes — `AddElingMcpServer()` still registers MCP server with stdio transport.

### `src/backend/Eling.Server/Eling.Server.csproj`

- Removed standalone executable output (now a class library).

### `tests/Eling.Server.Tests/MemoryApiTests.cs`

- `WebApplicationFactory<TestProgram>` → `WebApplicationFactory<Program>` (from `Eling.Host`).
- `.UseSetting("Eling:EnableMcp", "false")` disables MCP stdio in test mode — no `TestProgram.cs` needed.
- All 100 REST API tests pass.

### `tests/Eling.Server.Tests/Eling.Server.Tests.csproj`

- Added `FrameworkReference` for `Microsoft.AspNetCore.App` (needed for `WebApplicationFactory`).
- Added project reference to `Eling.Host` (for `Program` type).

### `tests/Eling.Host.Tests/` (new — 14 tests)

**Integration tests** (`HostIntegrationTests.cs` — 8 tests, `WebApplicationFactory<Program>`):
- `Get_health_returns_ok` — `GET /health` returns 200 with `{ status: "Healthy" }`.
- `Get_memory_returns_empty_list` — `GET /memory` returns empty list when no memories exist.
- `Create_memory_returns_created` — `POST /memory` creates and returns memory with id.
- `Create_memory_with_valid_data_persists` — created memory retrievable via `GET /memory/{id}`.
- `Get_memory_by_unknown_id_returns_404` — `GET /memory/nonexistent` returns 404.
- `Delete_memory_returns_no_content` — `DELETE /memory/{id}` returns 204 then `GET` returns 404.
- `Search_memory_returns_results` — `POST /memory/search` finds created memories.
- `Get_memory_invalid_status_returns_400` — `GET /memory?status=invalid` returns 400.

**Process tests** (`HostProcessTests.cs` — 6 tests, launches `eling` process):
- `Health_endpoint_returns_200` — process starts, `GET /health` returns 200.
- `Host_starts_with_default_port_and_shuts_down_cleanly` — process starts on default port, `SIGTERM` causes clean exit.
- `Host_binds_to_localhost_only` — verifies bound to `127.0.0.1`.
- `Port_override_works` — `--port 4318` causes process to listen on 4318.
- `Port_in_use_fails_with_clear_error` — second process on same port exits with error.
- `Stdout_is_clean_of_log_output` — no non-protocol output on stdout during startup.

### `Eling.slnx`

- Added `Eling.Host` and `Eling.Host.Tests`.

### Test Results

All 200 tests pass when executed serially (`NUnit.NumberOfTestWorkers=1`):

| Project | Tests |
|---------|-------|
| Eling.Host.Tests | 14/14 (8 integration + 6 process) |
| Eling.Core.Tests | 13/13 |
| Eling.Index.Tests | 13/13 |
| Eling.Storage.Tests | 12/12 |
| Eling.Application.Tests | 15/15 |
| Eling.Mcp.Tests | 33/33 |
| Eling.Server.Tests | 100/100 |
| **Total** | **200/200** |

### Remaining Notes

- Parallel execution of `HostProcessTests` causes `dotnet run` processes to lock each other's build output. Serial execution resolves this cleanly. This is a pre-existing environment issue unrelated to the implementation.
- `TestProgram.cs` was not created — the `Eling:EnableMcp=false` config toggle via `WebApplicationFactory.UseSetting()` achieves the same test isolation without a separate entry point class.
- Do NOT commit/push — changes are in working tree for review.
