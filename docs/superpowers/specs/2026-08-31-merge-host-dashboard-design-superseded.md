# Merge Eling.Host + Eling.Dashboard + Eling.Mcp + Eling.Application → Eling.Core + Eling.Backend

> **Superseded by execution + subsequent `memory_recall` rename.** This design spec was executed 2026-08-31 (see `2026-08-31-merge-host-dashboard-superseded.md` plan for the implementation record). File names referenced inside (e.g. `SessionStart/ISessionStartService.cs`, `SessionStartTool.cs`, `Dtos/SessionStart*`) reflect the *transfer-time* names; the folder was subsequently renamed to `MemoryRecall/` and tool renamed to `memory_recall` (see CHANGELOG.md `[Unreleased]`). For the current structure, see `Eling.Core/MemoryRecall/` and `Eling.Backend/Mcp/Tools/MemoryRecallTool.cs`.

> Design spec — 2026-08-31

## Overview

Three-way consolidation:

1. `Eling.Application` (memory business logic) merges into `Eling.Core` (types/scopes).
2. `Eling.Mcp` (MCP server setup) merges into `Eling.Backend` (new unified project).
3. `Eling.Host` + `Eling.Dashboard` merge into `Eling.Backend` (same as before).

Final layout: **2 backend projects** (`Eling.Core` + `Eling.Backend`) instead of the current 4 (`Core`, `Application`, `Mcp`, `Host`, `Dashboard`).

## Motivation

- **Dev mode is fragile.** `dotnet watch` on Host → `SpawnDashboard` → `dotnet run --project Dashboard`. Layout mismatch (`.bin/net10.0/` vs `.bin/Debug/net10.0/`) breaks sibling binary detection. csproj fallback works but adds complexity.
- **UserScope sentinel.** Host registers with `ProjectRoot = "UserScope"` to distinguish global-only runs. After merge, self-registration is straightforward and the sentinel logic can be cleaned up.
- **Future agentic agent.** A single-process engine is easier to extend (e.g. running sub-agents, coordinating port allocation, adding lifecycle hooks).
- **Project count.** 4 backend projects + 5 test projects is too many for a single-process engine. Consolidating to 2 backend + 1 unified test project reduces ceremony.

## Architecture

```
Eling.Backend (new)
├── Program.cs (single orchestrator)
│   ├── Resolve ProjectScope + UserScope
│   ├── Build WebApplication (Microsoft.NET.Sdk.Web)
│   ├── Configure DI:
│   │   ├── AddElingCoreServices(dataDir) — shared memory/scope services
│   │   ├── AddMcpServer().WithStdioServerTransport().WithToolsFromAssembly() — MCP over stdio
│   │   └── AddSingleton<RuntimeRegistry>, AddSingleton<MemoryChangeBroadcaster> — REST deps
│   ├── Kestrel: Listen 127.0.0.1:4317 (stg) or 4417 (dev)
│   ├── REST endpoints:
│   │   ├── /api/coordinator/runtimes
│   │   ├── /api/coordinator/register
│   │   ├── /api/coordinator/heartbeat/{pid}
│   │   ├── /api/coordinator/unregister/{pid}
│   │   ├── /api/memories (project + global)
│   │   ├── /api/project/memories
│   │   ├── /api/global/memories
│   │   ├── /api/events/memories (SSE)
│   │   └── /health
│   ├── Static files: serve eling-dashboard-ui (Next.js build output)
│   ├── Self-register as runtime (ProjectRoot = projectScope.Root)
│   └── On stop: self-unregister
```

**Single process, two transports:**
- **stdio** (stdin/stdout) → MCP protocol (ModelContextProtocol)
- **HTTP** (127.0.0.1:4x17) → REST API + static web UI

Stdout stays clean for MCP: ASP.NET Core logging goes to stderr by default. McpServiceExtensions already sets up stderr logging.

## File Plan

### Updated project: `Eling.Core` (absorbs `Eling.Application`)

**csproj changes** (add packages from `Eling.Application.csproj`):
```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Ulid" Version="1.4.1" />
    <!-- Absorbed from Eling.Application: -->
    <PackageReference Include="Microsoft.Extensions.DependencyInjection" Version="10.0.11" />
    <PackageReference Include="Microsoft.Extensions.Hosting" Version="10.0.11" />
    <PackageReference Include="Serilog" Version="4.4.0" />
    <PackageReference Include="Serilog.Extensions.Hosting" Version="10.0.0" />
    <PackageReference Include="Serilog.Sinks.Console" Version="6.1.1" />
    <PackageReference Include="Serilog.Sinks.File" Version="7.0.0" />
    <PackageReference Include="Microsoft.Data.Sqlite" Version="10.0.11" />
  </ItemGroup>
</Project>
```

**Files transferred from `Eling.Application/`:**
- `FileSystemIntentionStorage.cs`
- `FileSystemMemoryStorage.cs`
- `IIntentionStorage.cs`
- `IMemoryChangeNotifier.cs`
- `IMemoryIndex.cs`
- `IMemoryMerger.cs`
- `IMemoryScopePolicy.cs`
- `IMemoryService.cs`
- `IMemoryStorage.cs`
- `IntentionFrontMatter.cs`
- `IScopedMemoryService.cs`
- `LegacyTolerantDateTimeOffsetConverter.cs`
- `MemoryFrontMatter.cs`
- `MemoryMerger.cs`
- `MemoryScopeParser.cs`
- `MemoryScopePolicy.cs`
- `MemorySearchResult.cs`
- `MemoryService.cs`
- `NullMemoryChangeNotifier.cs`
- `ScopedMemoryService.cs`
- `SqliteMemoryIndex.cs`
- `SessionStart/IntentionTriggerMatcher.cs`
- `SessionStart/ISessionStartService.cs`
- `SessionStart/SessionStartContext.cs`
- `SessionStart/SessionStartIntentionResult.cs`
- `SessionStart/SessionStartResult.cs`
- `SessionStart/SessionStartService.cs`
- `SessionStart/SessionStartStats.cs`

For each file, change `namespace Eling.Application;` → `namespace Eling.Core;` and `namespace Eling.Application.SessionStart;` → `namespace Eling.Core.SessionStart;`. Also change `using Eling.Application;` (in some files) → `using Eling.Core;`.

### New project: `Eling.Backend` (absorbs `Eling.Mcp` + `Eling.Host` + `Eling.Dashboard`)

**csproj:**
```xml
<Project Sdk="Microsoft.NET.Sdk.Web">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <OutputType>Exe</OutputType>
    <AssemblyName>eling-backend</AssemblyName>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="..\Eling.Core\Eling.Core.csproj" />
  </ItemGroup>
  <ItemGroup>
    <InternalsVisibleTo Include="Eling.Backend.Tests" />
  </ItemGroup>
  <!-- Absorbed from Eling.Mcp: -->
  <ItemGroup>
    <PackageReference Include="ModelContextProtocol" Version="2.1.0" />
  </ItemGroup>
  <!-- Inherit ElingSkipDashboard for dev mode (skip slow pnpm build) -->
  <!-- Keep BuildDashboard target from Eling.Dashboard.csproj -->
</Project>
```

**Program.cs** — single orchestrator (replaces `Eling.Host/Program.cs` + `Eling.Dashboard/Program.cs` + absorbs `McpServiceExtensions` callsite):
```
1. Resolve dashboardPort from ELING_DASHBOARD_PORT (default 4317)
2. Discover ProjectScope + UserScope
3. Check if AddressAlreadyInUse on port → if yes, exit cleanly (let winner keep serving)
4. Build WebApplication:
   a. WebHost: Kestrel on 127.0.0.1:dashboardPort
   b. DI: AddElingCoreServices + AddMcpServer + RuntimeRegistry + Broadcaster
   c. Middleware: UseDefaultFiles → UseStaticFiles → UseRouting → endpoints → MapFallbackToFile
   d. Self-register as runtime (ProjectRoot = projectScope.Root)
   e. On stop: self-unregister
5. app.Run()
```

**Files transferred from `Eling.Mcp/`:**
- `McpServiceExtensions.cs`
- `McpLoggingExtensions.cs`
- `MemoryTools.cs`
- `SessionStartTool.cs`
- `DailyLogRollerService.cs`
- `RollingDailyFileSink.cs`
- `ServerInstructions.cs`
- `Dtos/SaveMemoryResponse.cs`
- `Dtos/SessionStartContextInput.cs`
- `Dtos/SessionStartIntention.cs`
- `Dtos/SessionStartMemory.cs`
- `Dtos/SessionStartResponse.cs`
- `Dtos/SessionStartStatsDto.cs`

For each file, change `namespace Eling.Mcp;` → `namespace Eling.Backend;` and `namespace Eling.Mcp.Dtos;` → `namespace Eling.Backend.Dtos;`. Replace `using Eling.Application;` with `using Eling.Core;` and `using Eling.Application.SessionStart;` with `using Eling.Core.SessionStart;`.

**Files transferred from `Eling.Dashboard`:**
- `RuntimeRegistry.cs`
- `CoordinatorEndpoints.cs`
- `MemoryChangeBroadcaster.cs`
- `Endpoints/MemoryEndpoints.cs`
- `Endpoints/ScopedMemoryEndpoints.cs`
- `Converters/MemoryIdJsonConverter.cs`
- `Dtos/*.cs` (all DTOs)

For each file, change `namespace Eling.Dashboard;` → `namespace Eling.Backend;` (and `Eling.Dashboard.Endpoints` → `Eling.Backend.Endpoints`, etc.). Replace `using Eling.Application;` with `using Eling.Core;`.

**Files transferred from `Eling.Host` (none needed):**
- In the merged model, self-registration is a one-time startup action in `Program.cs`. The heartbeat loop that existed in the old `DashboardCoordinator` is no longer needed because the host and dashboard are the same process — there is no cross-process liveness to track. Use `IHostApplicationLifetime.ApplicationStopped` for the unregister hook.

**Files removed:**
- `DashboardPaths.cs` — not needed (no sibling binary detection)
- `DashboardLauncher.cs` — not needed (no CliWrap spawn)
- `DashboardCoordinator.cs` — not needed (no cross-process coordination)

### Delete (entire folders)

- `Eling.Application/` — merged into `Eling.Core`
- `Eling.Mcp/` — merged into `Eling.Backend`
- `Eling.Host/` — replaced by `Eling.Backend`
- `Eling.Dashboard/` — replaced by `Eling.Backend`
- `tests/Eling.Application.Tests/` — merged into `Eling.Backend.Tests`
- `tests/Eling.Mcp.Tests/` — merged into `Eling.Backend.Tests`
- `tests/Eling.Dashboard.Tests/` — replaced by `Eling.Backend.Tests`
- `tests/Eling.Host.Tests/` — replaced by `Eling.Backend.Tests`

### Updated references

- `Eling.slnx` — remove old projects, add `Eling.Backend`
- `package.json` — `dev:backend`: `cross-env ELING_DASHBOARD_PORT=4417 dotnet run --project src/backend/Eling.Backend/Eling.Backend.csproj`
- `opencode.json` (project) — `mcp.eling_dev.command`: `dotnet run --project src/backend/Eling.Backend/Eling.Backend.csproj`
- `global ~/.config/opencode/opencode.json` — `mcp.eling.command`: `~/.local/bin/eling-backend.exe` (binary renamed from `eling.exe` to `eling-backend.exe`; rebuild + republish the global binary)
- `Directory.Build.props` — no change (OutputPath rules still apply)

## Port Binding Rule

`Eling.Backend` does not assume it owns the dashboard port. In a multi-project workspace, several `Eling.Backend` instances may be spawned — only one binds, the rest assume a peer already serves.

**Two-phase startup:**

1. **Probe:** Before binding, check whether `127.0.0.1:{ELING_DASHBOARD_PORT}` is already listening (via `IPGlobalProperties.GetActiveTcpListeners()`). 
   - If **listening**: assume another `Eling.Backend` is already serving this port. Skip Kestrel entirely — only run MCP over stdio and self-register in that peer's `RuntimeRegistry` (via HTTP register to the existing port).
   - If **not listening**: this instance binds the port and serves both REST + MCP.

2. **Race fallback:** If two instances both probe as free and one wins the bind, the loser still hits `AddressAlreadyInUse` at Kestrel startup and exits cleanly. No retry, no spawn.

Default ports:
- **Staging (global)**: 4317
- **Dev (project MCP)**: 4417

**Why this matters:** When a developer opens two projects that both spawn `Eling.Backend`, the second one should not fail with "address already in use" — it should silently fall into the first peer's registry. The runtime registry is shared across all `Eling.Backend` instances on the same loopback port.

## Self-Registration

On startup, `Eling.Backend` registers itself in `RuntimeRegistry`:

```csharp
new RuntimeRegistration {
    ProcessId = Environment.ProcessId,
    ProjectRoot = projectScope.Root,  // no more "UserScope" sentinel
    DataDirectory = effectiveDataDir,
    McpEnabled = true,
    McpTransport = "stdio"
}
```

Note: when `Eling.Backend` runs at user home (global scope), `ProjectRoot` is still `"UserScope"` sentinel — this is needed for the FE to distinguish global-only runs. The `RuntimeRegistry.Alive()` filter we already added handles this.

## BuildDashboard Target

Carried over from `Eling.Dashboard.csproj`:
- Runs `pnpm install` + `pnpm build` for the Next.js frontend
- Copies output to `$(OutputPath)eling-dashboard-ui/`
- Respects `ElingSkipDashboard=true` for dev mode (`dotnet watch`)

## What Gets Removed

**Code eliminated:**
- `SpawnDashboard()` logic (no sibling binary detection)
- `ElingOutputRoot` layout workaround (single process, no path mismatch)
- Cross-project `MSBuild` target in Host csproj (was building Dashboard as sibling)
- All `CliWrap` references in Host (was only used for spawn)
- `DashboardLoopAsync` (Host) + `DashboardCoordinator` (new, simplified)

**Complexity eliminated:**
- Heartbeat/register flow between two processes (no longer needed)
- Stale sweep for zombie processes
- Port 4317/4417 confusion (one port, one process)
- `dotnet watch` + `pnpm dev` dual orchestration
- `DashboardCoordinator` (entire file deleted — no cross-process loop needed)

## Testing

### New test project: `Eling.Backend.Tests`
- Replaces `Eling.Dashboard.Tests` + `Eling.Host.Tests` + `Eling.Application.Tests` + `Eling.Mcp.Tests`
- One project per Eling rules
- Runs with `--artifacts-path .bin-test`
- Tests: coordinator endpoints, memory API, runtime self-registration, memory service, scope policy, MCP tools, MCP logging

### Migration approach
1. Update `Eling.Core.csproj` (add Application packages) + transfer Application files
2. Create `Eling.Backend.csproj` (with ModelContextProtocol package) + transfer files from Mcp + Dashboard + simplified Host
3. Update `Eling.slnx` (remove old, add new)
4. Update test project references + namespace in test files
5. Build + verify compilation
6. Run tests
7. Update references (opencode.json, package.json)
8. Delete old projects
9. Verify endpoint runtimes (UserScope filter confirmed)

## Files Modified (existing)

- `RuntimeRegistry.cs` — already fixed `UserScope` filter in `Alive()`. After transfer to `Eling.Backend/`, namespace becomes `Eling.Backend`.
- `Eling.Core.csproj` — add packages absorbed from `Eling.Application.csproj`
- `Eling.slnx` — project list updated

## Files NOT Modified

- Frontend `src/frontend/Eling.Dashboard/` — untouched
- All `.cs` files in `Eling.Core/` original (types/scopes) — only namespace unification for the merged Application files
- Test files in `Eling.Core.Tests/` (they already use `Eling.Core` namespace)
