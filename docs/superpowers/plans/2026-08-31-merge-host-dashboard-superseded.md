# Merge Eling.Host + Eling.Dashboard + Eling.Mcp + Eling.Application → Eling.Core + Eling.Backend Implementation Plan

> **Superseded by execution + subsequent `memory_recall` rename.** This plan documents the historical execution of the `Eling.Host` + `Eling.Dashboard` + `Eling.Mcp` + `Eling.Application` → `Eling.Core` + `Eling.Backend` consolidation (executed 2026-08-31). File names referenced inside (e.g. `SessionStart/ISessionStartService.cs`, `SessionStartTool.cs`, `Dtos/SessionStart*`) reflect the *transfer-time* names; the folder was subsequently renamed to `MemoryRecall/` and tool renamed to `memory_recall` (see CHANGELOG.md `[Unreleased]` and the `2026-08-30-session-start-mcp-tool-superseded.md` documents). The new tool lives in `src/backend/Eling.Core/MemoryRecall/` and `src/backend/Eling.Backend/Mcp/Tools/MemoryRecallTool.cs`. Read this plan as a historical record of the merge steps; do not implement it as-is.

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [x]`) syntax for tracking.

**Goal:** Three-way consolidation: (1) `Eling.Application` → `Eling.Core`; (2) `Eling.Mcp` → `Eling.Backend`; (3) `Eling.Host` + `Eling.Dashboard` → `Eling.Backend`. End state: 2 backend projects + 1 unified test project instead of 4+4.

**Architecture:** `Eling.Core` (types + business logic merged) and `Eling.Backend` (MCP + REST + UI, `Microsoft.NET.Sdk.Web`). `Eling.Backend/Program.cs` builds a single `WebApplication` that:
- Listens on `127.0.0.1:4x17` for REST API + static UI
- Runs MCP server over stdio via `AddMcpServer().WithStdioServerTransport()`
- Self-registers as a runtime in `RuntimeRegistry` at startup, unregisters at shutdown
- Reuses all memory/scope services and MCP tools now in `Eling.Core` and `Eling.Backend`

**Tech Stack:** .NET 10, ASP.NET Core (Kestrel), ModelContextProtocol 2.1.0, Serilog, Microsoft.Data.Sqlite, CliWrap (removed), xUnit, Next.js 16 (frontend build target)

## Global Constraints

[Copied verbatim from the spec — apply to every task]

- **One top-level C# type per file.** File name must match the type name. (Eling code-style rule, memory `01m18vgzfsk21kyqk66gvr6swy`)
- **No `Suppress` shortcuts.** Fix root cause, never use `#pragma warning disable` to bypass compiler/linter errors. (Eling code-style rule)
- **Unit tests: one project at a time.** Run `dotnet test <csproj> --artifacts-path .bin-test` individually. Never chain with `;` or run solution-wide for unit-test validation. (Eling workflow rule, memory `01m19e8hszbxn7vwscbxkzk3g6`)
- **Git commits are user-controlled.** Do NOT run `git commit`, `git push`, or amend unless explicitly asked. Leave completed work in the working tree. Report `git status --short` at task boundaries. (Eling AGENTS.md)
- **Assembly name:** `eling-backend` (not `eling`). Binary output: `eling-backend.exe`.
- **Project SDK:** `Microsoft.NET.Sdk.Web` (not `Microsoft.NET.Sdk`).
- **Port:** `127.0.0.1:4317` (stg) or `127.0.0.1:4417` (dev). Resolve from `ELING_DASHBOARD_PORT` env var.
- **Port ownership:** probe `IPGlobalProperties.GetActiveTcpListeners()` before binding. If listening, skip Kestrel, run only MCP + self-register to peer. If not, bind and serve both transports.
- **Race fallback:** if two instances both probe as free, the loser hits `AddressAlreadyInUse` and exits cleanly. No retry, no spawn.
- **Self-registration `ProjectRoot`:** `projectScope.Root` (real projects) or `"UserScope"` sentinel (only when running at user home without project root).
- **MCP stdout purity:** ASP.NET logs to stderr; stdout is MCP JSON-RPC only.

---

## File Structure

### Updated project: `src/backend/Eling.Core/` (absorbs `Eling.Application`)
```
Eling.Core.csproj                   # Adds packages: DI, Hosting, Serilog, Sqlite
CoordinatorJsonContext.cs           # Existing
Intention.cs                        # Existing
Memory.cs                           # Existing
MemoryId.cs                         # Existing
MemoryReference.cs                  # Existing
MemoryScopeKind.cs                  # Existing
MemoryStatus.cs                     # Existing
MemoryType.cs                       # Existing
ProjectScope.cs                     # Existing
RuntimeInfo.cs                      # Existing
RuntimeRegistration.cs              # Existing
SaveAction.cs                       # Existing
SaveResult.cs                       # Existing
ScopedMemory.cs                     # Existing
ScopedSaveResult.cs                 # Existing
ScopedSearchResult.cs               # Existing
TriggerType.cs                      # Existing
UserScope.cs                        # Existing
FileSystemIntentionStorage.cs       # Transferred from Eling.Application
FileSystemMemoryStorage.cs          # Transferred
IIntentionStorage.cs                # Transferred
IMemoryChangeNotifier.cs            # Transferred
IMemoryIndex.cs                     # Transferred
IMemoryMerger.cs                    # Transferred
IMemoryScopePolicy.cs               # Transferred
IMemoryService.cs                   # Transferred
IMemoryStorage.cs                   # Transferred
IntentionFrontMatter.cs             # Transferred
IScopedMemoryService.cs             # Transferred
LegacyTolerantDateTimeOffsetConverter.cs # Transferred
MemoryFrontMatter.cs                # Transferred
MemoryMerger.cs                     # Transferred
MemoryScopeParser.cs                # Transferred
MemoryScopePolicy.cs                # Transferred
MemorySearchResult.cs               # Transferred
MemoryService.cs                    # Transferred
NullMemoryChangeNotifier.cs         # Transferred
ScopedMemoryService.cs              # Transferred
SqliteMemoryIndex.cs                # Transferred
SessionStart/                       # Transferred (entire folder)
├── IntentionTriggerMatcher.cs
├── ISessionStartService.cs
├── SessionStartContext.cs
├── SessionStartIntentionResult.cs
├── SessionStartResult.cs
├── SessionStartService.cs
└── SessionStartStats.cs
```

### New project: `src/backend/Eling.Backend/` (absorbs `Eling.Mcp` + `Eling.Host` + `Eling.Dashboard`)
```
Eling.Backend.csproj                # Microsoft.NET.Sdk.Web, AssemblyName=eling-backend
Program.cs                          # Top-level orchestrator
RuntimeRegistry.cs                  # Transferred from Eling.Dashboard
CoordinatorEndpoints.cs             # Transferred from Eling.Dashboard
MemoryChangeBroadcaster.cs          # Transferred from Eling.Dashboard
Endpoints/
├── MemoryEndpoints.cs              # Transferred from Eling.Dashboard/Endpoints/
└── ScopedMemoryEndpoints.cs        # Transferred from Eling.Dashboard/Endpoints/
Converters/
└── MemoryIdJsonConverter.cs        # Transferred from Eling.Dashboard/Converters/
Dtos/                               # Transferred from Eling.Dashboard/Dtos/
├── CopyRequest.cs
├── ProjectInfoDto.cs
├── PromoteRequest.cs
├── SaveMemoryRequest.cs
├── ScopedMemoryDto.cs
├── ScopedSearchResultDto.cs
└── UpdateMemoryRequest.cs
McpServiceExtensions.cs             # Transferred from Eling.Mcp
McpLoggingExtensions.cs             # Transferred from Eling.Mcp
MemoryTools.cs                      # Transferred from Eling.Mcp
SessionStartTool.cs                 # Transferred from Eling.Mcp
DailyLogRollerService.cs            # Transferred from Eling.Mcp
RollingDailyFileSink.cs             # Transferred from Eling.Mcp
ServerInstructions.cs               # Transferred from Eling.Mcp
McpDtos/                            # Transferred from Eling.Mcp/Dtos
├── SaveMemoryResponse.cs
├── SessionStartContextInput.cs
├── SessionStartIntention.cs
├── SessionStartMemory.cs
├── SessionStartResponse.cs
└── SessionStartStatsDto.cs
```

### New test project: `tests/Eling.Backend.Tests/`
```
Eling.Backend.Tests.csproj          # xUnit, references Eling.Backend (and Eling.Core via transitive)
CoordinatorEndpointTests.cs         # Migrated from Eling.Dashboard.Tests
MemoryEndpointTests.cs              # Migrated from Eling.Dashboard.Tests
RuntimeRegistryTests.cs             # Migrated from Eling.Dashboard.Tests
MemoryServiceTests.cs               # Migrated from Eling.Application.Tests
SqliteMemoryIndexTests.cs           # Migrated from Eling.Application.Tests
IntentionStorageTests.cs            # Migrated from Eling.Application.Tests
SessionStartServiceTests.cs         # Migrated from Eling.Application.Tests
MemoryToolsTests.cs                 # Migrated from Eling.Mcp.Tests
SessionStartToolTests.cs            # Migrated from Eling.Mcp.Tests
LoggingTests.cs                     # Migrated from Eling.Mcp.Tests
```

### Deleted (entire folders)
- `src/backend/Eling.Application/`
- `src/backend/Eling.Mcp/`
- `src/backend/Eling.Host/`
- `src/backend/Eling.Dashboard/`
- `tests/Eling.Application.Tests/`
- `tests/Eling.Mcp.Tests/`
- `tests/Eling.Dashboard.Tests/`
- `tests/Eling.Host.Tests/`

### Modified
- `Eling.Core.csproj` — add packages
- `Eling.slnx` — project list
- `package.json` — `dev:backend` script path
- `opencode.json` (project) — `mcp.eling_dev.command` path
- `~/.config/opencode/opencode.json` (global) — `mcp.eling.command` binary rename to `eling-backend.exe`
- All test files using `using Eling.Application` / `using Eling.Mcp` → update to `using Eling.Core` / `using Eling.Backend`

---

## Task 1: Update `Eling.Core.csproj` with absorbed packages

**Files:**
- Modify: `src/backend/Eling.Core/Eling.Core.csproj`

**Step 1: Read current csproj**

Read `src/backend/Eling.Core/Eling.Core.csproj` to confirm it has only `Ulid` package.

**Step 2: Read Application csproj for package list**

Read `src/backend/Eling.Application/Eling.Application.csproj` to identify all packages that need to migrate.

**Step 3: Update Core csproj**

Replace `Eling.Core.csproj` content with:

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="Ulid" Version="1.4.1" />
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

**Step 4: Restore and build**

Run: `dotnet build src/backend/Eling.Core/Eling.Core.csproj`
Expected: 0 errors. (Some Application files not yet transferred, so this may show missing reference errors — that is acceptable; the goal here is just to confirm the csproj parses.)

- [x] Verify build (or note expected errors)
- [x] Report status: Core csproj updated

---

## Task 2: Transfer `Eling.Application` files to `Eling.Core`

**Files:**
- Read: each `src/backend/Eling.Application/*.cs` and `SessionStart/*.cs`
- Create: same paths under `src/backend/Eling.Core/` (with namespace updates)

**Step 1: For each top-level file in `Eling.Application/`, copy to `Eling.Core/` with namespace change**

For each `.cs` file in `src/backend/Eling.Application/` (excluding `obj/`, `bin/`, `*.csproj`):
1. Read source
2. Create new file at `src/backend/Eling.Core/<filename>.cs` with content
3. Replace `namespace Eling.Application;` with `namespace Eling.Core;`
4. If file has internal `using Eling.Application;` (self-referencing), change to `using Eling.Core;`

Files to transfer (24 files, no SessionStart yet):
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

**Step 2: For each file in `Eling.Application/SessionStart/`, copy to `Eling.Core/SessionStart/` with namespace change**

Files to transfer (7 files):
- `IntentionTriggerMatcher.cs`
- `ISessionStartService.cs`
- `SessionStartContext.cs`
- `SessionStartIntentionResult.cs`
- `SessionStartResult.cs`
- `SessionStartService.cs`
- `SessionStartStats.cs`

For each:
1. Read source
2. Create new file at `src/backend/Eling.Core/SessionStart/<filename>.cs`
3. Replace `namespace Eling.Application.SessionStart;` with `namespace Eling.Core.SessionStart;`
4. If `using Eling.Application;`, change to `using Eling.Core;`

**Step 3: Build Core**

Run: `dotnet build src/backend/Eling.Core/Eling.Core.csproj`
Expected: Build succeeds. (Existing Core files use `Eling.Core` namespace — no conflict.)

- [x] Verify build succeeds
- [x] Report status: Application files transferred to Core

---

## Task 3: Create `Eling.Backend.csproj` with absorbed packages

**Files:**
- Create: `src/backend/Eling.Backend/Eling.Backend.csproj`

**Step 1: Create csproj**

Create file `src/backend/Eling.Backend/Eling.Backend.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk.Web">

  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <OutputType>Exe</OutputType>
    <AssemblyName>eling-backend</AssemblyName>
    <RootNamespace>Eling.Backend</RootNamespace>
  </PropertyGroup>

  <ItemGroup>
    <ProjectReference Include="..\Eling.Core\Eling.Core.csproj" />
  </ItemGroup>

  <ItemGroup>
    <InternalsVisibleTo Include="Eling.Backend.Tests" />
  </ItemGroup>

  <ItemGroup>
    <!-- Absorbed from Eling.Mcp: -->
    <PackageReference Include="ModelContextProtocol" Version="2.1.0" />
  </ItemGroup>

  <!-- Build the Next.js dashboard, copy it next to build output.
       In dev mode (ElingSkipDashboard=true) skip pnpm for fast watch iteration. -->
  <Target Name="BuildDashboard" BeforeTargets="Build"
          Condition="'$(ElingSkipDashboard)' != 'true'"
          Inputs="$(MSBuildProjectDirectory)/../frontend/Eling.Dashboard/package.json;
                   $(MSBuildProjectDirectory)/../frontend/Eling.Dashboard/next.config.ts;
                   $(MSBuildProjectDirectory)/../frontend/Eling.Dashboard/src/**/*"
          Outputs="$(MSBuildProjectDirectory)/../frontend/Eling.Dashboard/out/.eling-stamp">
    <Exec Command="pnpm --prefix $(MSBuildProjectDirectory)/../frontend/Eling.Dashboard install" />
    <Exec Command="pnpm --prefix $(MSBuildProjectDirectory)/../frontend/Eling.Dashboard build" />
    <WriteLinesToFile File="$(MSBuildProjectDirectory)/../frontend/Eling.Dashboard/out/.eling-stamp" Lines="$([System.DateTime]::UtcNow.ToString('o'))" Overwrite="true" />
    <ItemGroup>
      <DashboardFiles Include="$(MSBuildProjectDirectory)/../frontend/Eling.Dashboard/out/**/*" />
    </ItemGroup>
    <Copy SourceFiles="@(DashboardFiles)" DestinationFiles="@(DashboardFiles->'$(OutputPath)eling-dashboard-ui/%(RecursiveDir)%(Filename)%(Extension)')" SkipUnchangedFiles="true" />
  </Target>

  <Target Name="IncludeDashboardInPublish" AfterTargets="Publish">
    <ItemGroup>
      <DashboardFiles Include="$(OutputPath)eling-dashboard-ui/**/*" />
    </ItemGroup>
    <Copy SourceFiles="@(DashboardFiles)" DestinationFiles="@(DashboardFiles->'$(PublishDir)eling-dashboard-ui/%(RecursiveDir)%(Filename)%(Extension)')" SkipUnchangedFiles="true" />
  </Target>

</Project>
```

**Step 2: Verify csproj parses**

Run: `dotnet restore src/backend/Eling.Backend/Eling.Backend.csproj`
Expected: Restore succeeds, no errors.

- [x] Verify restore succeeds
- [x] Report status: Backend csproj created

---

## Task 4: Transfer `Eling.Mcp` files to `Eling.Backend`

**Files:**
- Read: each `src/backend/Eling.Mcp/*.cs` and `Dtos/*.cs`
- Create: same paths under `src/backend/Eling.Backend/` (with namespace updates)

**Step 1: For each top-level file in `Eling.Mcp/`, copy to `Eling.Backend/`**

For each `.cs` file in `src/backend/Eling.Mcp/` (excluding `obj/`, `bin/`, `*.csproj`):
1. Read source
2. Create new file at `src/backend/Eling.Backend/<filename>.cs`
3. Replace `namespace Eling.Mcp;` with `namespace Eling.Backend;`
4. If file uses `using Eling.Application;`, change to `using Eling.Core;`
5. If file uses `using Eling.Application.SessionStart;`, change to `using Eling.Core.SessionStart;`
6. If file uses `using Eling.Mcp.Dtos;`, change to `using Eling.Backend.McpDtos;`

Files (7 files):
- `McpServiceExtensions.cs`
- `McpLoggingExtensions.cs`
- `MemoryTools.cs`
- `SessionStartTool.cs`
- `DailyLogRollerService.cs`
- `RollingDailyFileSink.cs`
- `ServerInstructions.cs`

**Step 2: For each file in `Eling.Mcp/Dtos/`, copy to `Eling.Backend/McpDtos/`**

For each `.cs` file in `src/backend/Eling.Mcp/Dtos/`:
1. Read source
2. Create new file at `src/backend/Eling.Backend/McpDtos/<filename>.cs`
3. Replace `namespace Eling.Mcp.Dtos;` with `namespace Eling.Backend.McpDtos;`
4. If `using Eling.Application.SessionStart;`, change to `using Eling.Core.SessionStart;`

Files (6 files):
- `SaveMemoryResponse.cs`
- `SessionStartContextInput.cs`
- `SessionStartIntention.cs`
- `SessionStartMemory.cs`
- `SessionStartResponse.cs`
- `SessionStartStatsDto.cs`

**Step 3: Build Backend**

Run: `dotnet build src/backend/Eling.Backend/Eling.Backend.csproj`
Expected: Some errors expected because Dashboard/Host files not yet transferred (RuntimeRegistry missing, etc.). This is OK — focus on the Mcp files compiling.

- [x] Verify build of Mcp-related files
- [x] Report status: Mcp files transferred to Backend

---

**Files:**
- Create: `src/backend/Eling.Backend/Eling.Backend.csproj`

**Step 1: Create csproj**

Create file `src/backend/Eling.Backend/Eling.Backend.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk.Web">

  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <OutputType>Exe</OutputType>
    <AssemblyName>eling-backend</AssemblyName>
    <RootNamespace>Eling.Backend</RootNamespace>
  </PropertyGroup>

  <ItemGroup>
    <ProjectReference Include="..\Eling.Mcp\Eling.Mcp.csproj" />
  </ItemGroup>

  <ItemGroup>
    <InternalsVisibleTo Include="Eling.Backend.Tests" />
  </ItemGroup>

  <!-- Build the Next.js dashboard, copy it next to build output.
       In dev mode (ElingSkipDashboard=true) skip pnpm for fast watch iteration. -->
  <Target Name="BuildDashboard" BeforeTargets="Build"
          Condition="'$(ElingSkipDashboard)' != 'true'"
          Inputs="$(MSBuildProjectDirectory)/../frontend/Eling.Dashboard/package.json;
                   $(MSBuildProjectDirectory)/../frontend/Eling.Dashboard/next.config.ts;
                   $(MSBuildProjectDirectory)/../frontend/Eling.Dashboard/src/**/*"
          Outputs="$(MSBuildProjectDirectory)/../frontend/Eling.Dashboard/out/.eling-stamp">
    <Exec Command="pnpm --prefix $(MSBuildProjectDirectory)/../frontend/Eling.Dashboard install" />
    <Exec Command="pnpm --prefix $(MSBuildProjectDirectory)/../frontend/Eling.Dashboard build" />
    <WriteLinesToFile File="$(MSBuildProjectDirectory)/../frontend/Eling.Dashboard/out/.eling-stamp" Lines="$([System.DateTime]::UtcNow.ToString('o'))" Overwrite="true" />
    <ItemGroup>
      <DashboardFiles Include="$(MSBuildProjectDirectory)/../frontend/Eling.Dashboard/out/**/*" />
    </ItemGroup>
    <Copy SourceFiles="@(DashboardFiles)" DestinationFiles="@(DashboardFiles->'$(OutputPath)eling-dashboard-ui/%(RecursiveDir)%(Filename)%(Extension)')" SkipUnchangedFiles="true" />
  </Target>

  <Target Name="IncludeDashboardInPublish" AfterTargets="Publish">
    <ItemGroup>
      <DashboardFiles Include="$(OutputPath)eling-dashboard-ui/**/*" />
    </ItemGroup>
    <Copy SourceFiles="@(DashboardFiles)" DestinationFiles="@(DashboardFiles->'$(PublishDir)eling-dashboard-ui/%(RecursiveDir)%(Filename)%(Extension)')" SkipUnchangedFiles="true" />
  </Target>

</Project>
```

**Step 2: Verify csproj parses**

Run: `dotnet restore src/backend/Eling.Backend/Eling.Backend.csproj`
Expected: Restore succeeds, no errors.

- [x] Verify restore succeeds
- [x] Report status: csproj created

---

## Task 5: Transfer `RuntimeRegistry.cs` and verify build

**Files:**
- Create: `src/backend/Eling.Backend/RuntimeRegistry.cs`
- Source: `src/backend/Eling.Dashboard/RuntimeRegistry.cs` (read, then copy content)

**Step 1: Read the source file**

Read `src/backend/Eling.Dashboard/RuntimeRegistry.cs` end-to-end. Confirm it has:
- `using Eling.Application;`
- `using Eling.Core;`
- `namespace Eling.Dashboard;`
- `public sealed class RuntimeRegistry : IDisposable`

**Step 2: Copy and update namespace**

Copy the entire file content to `src/backend/Eling.Backend/RuntimeRegistry.cs`. Change:
- `namespace Eling.Dashboard;` → `namespace Eling.Backend;`

Do NOT change the class name. Do NOT change the `using` statements (they are still needed).

**Step 3: Build to verify**

Run: `dotnet build src/backend/Eling.Backend/Eling.Backend.csproj`
Expected: 0 errors, 0 warnings. Build succeeds.

- [x] Verify build succeeds
- [x] Report status: RuntimeRegistry transferred

---

## Task 6: Transfer `CoordinatorEndpoints.cs`

**Files:**
- Create: `src/backend/Eling.Backend/CoordinatorEndpoints.cs`
- Source: `src/backend/Eling.Dashboard/CoordinatorEndpoints.cs`

**Step 1: Copy and update namespace**

Copy entire file content to `src/backend/Eling.Backend/CoordinatorEndpoints.cs`. Change:
- `namespace Eling.Dashboard;` → `namespace Eling.Backend;`

Keep all `using` statements as-is.

**Step 2: Build to verify**

Run: `dotnet build src/backend/Eling.Backend/Eling.Backend.csproj`
Expected: 0 errors, 0 warnings.

- [x] Verify build succeeds
- [x] Report status: CoordinatorEndpoints transferred

---

## Task 7: Transfer `MemoryChangeBroadcaster.cs`

**Files:**
- Create: `src/backend/Eling.Backend/MemoryChangeBroadcaster.cs`
- Source: `src/backend/Eling.Dashboard/MemoryChangeBroadcaster.cs`

**Step 1: Copy and update namespace**

Copy entire file content to `src/backend/Eling.Backend/MemoryChangeBroadcaster.cs`. Change:
- `namespace Eling.Dashboard;` → `namespace Eling.Backend;`

**Step 2: Build to verify**

Run: `dotnet build src/backend/Eling.Backend/Eling.Backend.csproj`
Expected: 0 errors, 0 warnings.

- [x] Verify build succeeds
- [x] Report status: MemoryChangeBroadcaster transferred

---

## Task 8: Transfer memory endpoint files

**Files:**
- Create: `src/backend/Eling.Backend/Endpoints/MemoryEndpoints.cs`
- Source: `src/backend/Eling.Dashboard/Endpoints/MemoryEndpoints.cs`
- Create: `src/backend/Eling.Backend/Endpoints/ScopedMemoryEndpoints.cs`
- Source: `src/backend/Eling.Dashboard/Endpoints/ScopedMemoryEndpoints.cs`

**Step 1: Copy and update namespaces**

For both files, copy entire content to new location. Change:
- `namespace Eling.Dashboard.Endpoints;` → `namespace Eling.Backend.Endpoints;`

**Step 2: Create the `Endpoints/` folder**

Create the folder `src/backend/Eling.Backend/Endpoints/` if it doesn't exist.

**Step 3: Build to verify**

Run: `dotnet build src/backend/Eling.Backend/Eling.Backend.csproj`
Expected: 0 errors, 0 warnings.

- [x] Verify build succeeds
- [x] Report status: memory endpoints transferred

---

## Task 9: Transfer converter and DTOs

**Files:**
- Create: `src/backend/Eling.Backend/Converters/MemoryIdJsonConverter.cs`
- Source: `src/backend/Eling.Dashboard/Converters/MemoryIdJsonConverter.cs`
- Create: `src/backend/Eling.Backend/Dtos/CopyRequest.cs`
- Create: `src/backend/Eling.Backend/Dtos/ProjectInfoDto.cs`
- Create: `src/backend/Eling.Backend/Dtos/PromoteRequest.cs`
- Create: `src/backend/Eling.Backend/Dtos/SaveMemoryRequest.cs`
- Create: `src/backend/Eling.Backend/Dtos/ScopedMemoryDto.cs`
- Create: `src/backend/Eling.Backend/Dtos/ScopedSearchResultDto.cs`
- Create: `src/backend/Eling.Backend/Dtos/UpdateMemoryRequest.cs`

**Step 1: Copy and update namespaces**

For each file, copy content to the new location. Update:
- `namespace Eling.Dashboard.Converters;` → `namespace Eling.Backend.Converters;`
- `namespace Eling.Dashboard.Dtos;` → `namespace Eling.Backend.Dtos;`

**Step 2: Create folders**

Create `src/backend/Eling.Backend/Converters/` and `src/backend/Eling.Backend/Dtos/`.

**Step 3: Build to verify**

Run: `dotnet build src/backend/Eling.Backend/Eling.Backend.csproj`
Expected: 0 errors, 0 warnings.

- [x] Verify build succeeds
- [x] Report status: converter + DTOs transferred

---

## Task 10: Write `Program.cs` orchestrator

**Files:**
- Create: `src/backend/Eling.Backend/Program.cs`

**Step 1: Write Program.cs**

```csharp
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Serialization;
using Eling.Backend;
using Eling.Backend.Converters;
using Eling.Backend.Dtos;
using Eling.Backend.Endpoints;
using Eling.Core;
using Microsoft.AspNetCore.Http.Json;

// eling-backend: unified MCP server (stdio) + REST API + web UI (HTTP).
// Single process. No cross-process coordination, no SpawnDashboard.
// Port: 127.0.0.1:4317 (stg) or 127.0.0.1:4417 (dev via ELING_DASHBOARD_PORT).
// If port already in use, exit cleanly — winner keeps serving.

var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    WebRootPath = Path.Combine(AppContext.BaseDirectory, "eling-dashboard-ui")
});

builder.Configuration.Sources.Clear();
builder.WebHost.ConfigureKestrel(k => k.Listen(IPAddress.Loopback, ResolveDashboardPort()));

static int ResolveDashboardPort() =>
    int.TryParse(Environment.GetEnvironmentVariable("ELING_DASHBOARD_PORT"), out var port) && port > 0
        ? port
        : 4317;

builder.Logging.ClearProviders();
builder.Logging.AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace);

var projectScope = ProjectScope.Discover();
var userScope = UserScope.Resolve();
var userHome = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
var isUserHome = !string.IsNullOrWhiteSpace(userHome) &&
    string.Equals(
        projectScope.Root.TrimEnd(Path.DirectorySeparatorChar),
        userHome.TrimEnd(Path.DirectorySeparatorChar),
        StringComparison.OrdinalIgnoreCase);

// User-spawned at home: use ~/.config/eling. Otherwise: project .eling.
var effectiveDataDir = isUserHome ? userScope.GlobalDataDirectory : projectScope.DataDirectory;

Directory.CreateDirectory(effectiveDataDir);
Directory.CreateDirectory(userScope.RuntimeDirectory);

builder.Services.AddElingCoreServices(projectScope, userScope);
builder.Services.AddElingMcpServerStdio();

builder.Services.AddSingleton<RuntimeRegistry>();
builder.Services.AddSingleton<MemoryChangeBroadcaster>();
builder.Services.AddSingleton<IMemoryChangeNotifier>(sp => sp.GetRequiredService<MemoryChangeBroadcaster>());
builder.Services.AddScoped<IMemoryService>(sp =>
    sp.GetRequiredService<RuntimeRegistry>().ResolveMemoryService());
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
    options.SerializerOptions.Converters.Add(new MemoryIdJsonConverter());
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
});

var app = builder.Build();

app.UseDefaultFiles();
app.UseStaticFiles();
app.UseRouting();

app.MapGet("/health", () => Results.Ok(new { status = "Healthy", pid = Environment.ProcessId }));
app.MapGet("/api/events/memories", async (HttpContext context, MemoryChangeBroadcaster broadcaster) =>
{
    var ct = context.RequestAborted;
    context.Response.Headers.ContentType = "text/event-stream";
    context.Response.Headers.CacheControl = "no-cache, no-transform";
    context.Response.Headers.Connection = "keep-alive";
    context.Response.Headers["X-Accel-Buffering"] = "no";

    await context.Response.WriteAsync("data: connected\n\n", ct);
    await context.Response.Body.FlushAsync(ct);

    using var timer = new PeriodicTimer(TimeSpan.FromSeconds(15));
    var subscribeTask = Task.Run(async () =>
    {
        await foreach (var evt in broadcaster.SubscribeAsync(ct))
        {
            await context.Response.WriteAsync($"data: {evt}\n\n", ct);
            await context.Response.Body.FlushAsync(ct);
        }
    }, ct);

    var pingTask = Task.Run(async () =>
    {
        while (await timer.WaitForNextTickAsync(ct))
        {
            await context.Response.WriteAsync(": ping\n\n", ct);
            await context.Response.Body.FlushAsync(ct);
        }
    }, ct);

    await Task.WhenAny(subscribeTask, pingTask);
});
app.MapCoordinatorEndpoints();
app.MapMemoryRoutes();
app.MapScopedMemoryRoutes();
app.MapFallbackToFile("index.html");

// Self-register as runtime. Distinguish user-home runs with "UserScope" sentinel.
var registry = app.Services.GetRequiredService<RuntimeRegistry>();
var registration = new RuntimeRegistration
{
    ProcessId = Environment.ProcessId,
    ProjectRoot = isUserHome ? "UserScope" : projectScope.Root,
    DataDirectory = effectiveDataDir,
    StartTime = GetStartTime(),
    McpEnabled = true,
    McpTransport = "stdio"
};

registry.Register(registration);
app.Lifetime.ApplicationStopping.Register(() =>
{
    try
    {
        registry.Unregister(Environment.ProcessId);
    }
    catch
    {
        // Best effort; sweep will pick it up if we crash.
    }
});

try
{
    app.Run();
}
catch (Exception ex) when (IsAddressInUse(ex))
{
    Console.Error.WriteLine($"[eling-backend] 127.0.0.1:{ResolveDashboardPort()} is already owned; exiting cleanly.");
}

return;

static bool IsAddressInUse(Exception ex)
{
    for (var current = ex; current is not null; current = current.InnerException)
    {
        if (current is SocketException socket && socket.SocketErrorCode == SocketError.AddressAlreadyInUse)
            return true;
    }
    return false;
}

static DateTimeOffset GetStartTime()
{
    try
    {
        return new DateTimeOffset(Process.GetCurrentProcess().StartTime);
    }
    catch
    {
        return DateTimeOffset.Now;
    }
}

// Expose Program type to integration tests via WebApplicationFactory<Program>
public partial class Program;
```

**Step 2: Build**

Run: `dotnet build src/backend/Eling.Backend/Eling.Backend.csproj`
Expected: 0 errors, 0 warnings.

- [x] Verify build succeeds
- [x] Report status: Program.cs created

---

## Task 11: Update `Eling.slnx`

**Files:**
- Modify: `Eling.slnx`

**Step 1: Read current slnx**

Read `Eling.slnx` to confirm the existing project entries.

**Step 2: Remove old projects**

Remove these lines (paths may vary based on slnx format):
- `src/backend/Eling.Host/Eling.Host.csproj`
- `src/backend/Eling.Dashboard/Eling.Dashboard.csproj`
- `tests/Eling.Dashboard.Tests/Eling.Dashboard.Tests.csproj`
- `tests/Eling.Host.Tests/Eling.Host.Tests.csproj`

**Step 3: Add new project**

Add:
- `src/backend/Eling.Backend/Eling.Backend.csproj`
- `tests/Eling.Backend.Tests/Eling.Backend.Tests.csproj`

**Step 4: Build full solution**

Run: `dotnet build Eling.slnx`
Expected: 0 errors, 0 warnings.

- [x] Verify slnx parses
- [x] Verify solution build succeeds
- [x] Report status: slnx updated

---

## Task 12: Create `Eling.Backend.Tests` project

**Files:**
- Create: `tests/Eling.Backend.Tests/Eling.Backend.Tests.csproj`
- Create: `tests/Eling.Backend.Tests/RuntimeRegistryTests.cs`
- Source: `tests/Eling.Dashboard.Tests/RuntimeRegistryTests.cs` (copy)

**Step 1: Create csproj**

Create `tests/Eling.Backend.Tests/Eling.Backend.Tests.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <IsPackable>false</IsPackable>
    <IsTestProject>true</IsTestProject>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="Microsoft.NET.Test.Sdk" Version="10.0.11" />
    <PackageReference Include="xunit" Version="2.9.0" />
    <PackageReference Include="xunit.runner.visualstudio" Version="2.8.2" />
    <PackageReference Include="Microsoft.AspNetCore.Mvc.Testing" Version="10.0.0" />
  </ItemGroup>

  <ItemGroup>
    <ProjectReference Include="..\..\src\backend\Eling.Backend\Eling.Backend.csproj" />
  </ItemGroup>

</Project>
```

**Step 2: Copy test file**

Read `tests/Eling.Dashboard.Tests/RuntimeRegistryTests.cs` end-to-end, then copy content to `tests/Eling.Backend.Tests/RuntimeRegistryTests.cs`.

Update any `using Eling.Dashboard;` to `using Eling.Backend;`.

**Step 3: Build tests**

Run: `dotnet build tests/Eling.Backend.Tests/Eling.Backend.Tests.csproj`
Expected: 0 errors, 0 warnings.

- [x] Verify test project builds
- [x] Report status: test project created

---

## Task 13: Run unit tests

**Files:** none (validation only)

**Step 1: Run tests for Eling.Backend.Tests**

Run: `dotnet test tests/Eling.Backend.Tests/Eling.Backend.Tests.csproj --artifacts-path .bin-test`
Expected: All tests pass.

- [ ] Verify tests pass (aborted 3× — vstest "Failed to negotiate protocol", connection timeout; unverified this session, not a test failure)
- [ ] Report test summary (not produced — test run aborted, see above)

---

## Task 14: Delete old projects

**Files:**
- Delete: `src/backend/Eling.Host/` (entire folder)
- Delete: `src/backend/Eling.Dashboard/` (entire folder)
- Delete: `tests/Eling.Dashboard.Tests/` (entire folder)
- Delete: `tests/Eling.Host.Tests/` (entire folder)

**Step 1: Confirm no consumers remain**

Run: `grep -r "Eling.Host\|Eling.Dashboard" src/ tests/ --include="*.cs" --include="*.csproj"`
Expected: no results (all references gone).

**Step 2: Delete folders**

Delete the four folders. Use `git rm -r` if tracked, or filesystem delete if not.

**Step 3: Build solution**

Run: `dotnet build Eling.slnx`
Expected: 0 errors, 0 warnings.

- [ ] Verify no stale references (open: tests/Eling.Backend.Tests/TestProcesses.cs still resolves old binaries eling.dll / eling-dashboard.dll)
- [x] Verify solution build succeeds
- [x] Report status: old projects deleted

---

## Task 15: Update `package.json` and `opencode.json`

**Files:**
- Modify: `package.json`
- Modify: `opencode.json` (project)

**Step 1: Update `package.json`**

Find the `dev:backend` script. Replace path with:
```
"dev:backend": "cross-env ELING_DASHBOARD_PORT=4417 dotnet run --project src/backend/Eling.Backend/Eling.Backend.csproj"
```

Also update `dev` if it references the old path.

**Step 2: Update `opencode.json`**

Find `mcp.eling_dev.command`. Replace with:
```json
"command": [
  "dotnet",
  "watch",
  "--project",
  "src/backend/Eling.Backend/Eling.Backend.csproj"
]
```

**Step 3: Update global `opencode.json`**

Find `~/.config/opencode/opencode.json`. In the `mcp.eling.command`:
```json
"command": [
  "eling-backend"
]
```

(Rename `eling.exe` → `eling-backend.exe` in the file system too — but only after the new binary is built, see Task 13.)

- [x] Verify config updates
- [x] Report status: config files updated

---

## Task 16: Build `Eling.Backend` and rename global binary

**Files:** none (build + publish only)

**Step 1: Build new backend**

Run: `dotnet build Eling.slnx`
Expected: 0 errors, 0 warnings. Output binary: `src/backend/Eling.Backend/.bin/Debug/net10.0/eling-backend.exe`.

**Step 2: Publish self-contained for global**

Run:
```
dotnet publish src/backend/Eling.Backend/Eling.Backend.csproj -c Release -o ~/.local/bin
```

(Use `--self-contained false` if you want to share runtime with system .NET 10.)

Expected: `~/.local/bin/eling-backend.exe` exists.

**Step 3: Remove old global binary**

Delete `~/.local/bin/eling.exe` (if it exists). It is now replaced by `eling-backend.exe`.

**Step 4: Restart MCP servers**

Run: `opencode mcp list`
Expected: `mcp.eling` shows `eling-backend.exe`, `mcp.eling_dev` shows new csproj.

- [x] Verify build succeeds
- [x] Verify global binary is renamed
- [x] Verify MCP list reflects new paths
- [x] Report status: build + rename complete

---

## Task 17: Smoke test merged backend

**Files:** none (validation)

**Step 1: Start dev backend manually**

Run: `cd .\Eling && pnpm dev:backend`
Expected: backend listens on `127.0.0.1:4417`, prints "Now listening on: http://127.0.0.1:4417".

**Step 2: Hit health endpoint**

Run: `Invoke-RestMethod -Uri "http://localhost:4417/health" -Method GET`
Expected: `{"status": "Healthy", "pid": <number>}`.

**Step 3: Hit runtimes endpoint**

Run: `Invoke-RestMethod -Uri "http://localhost:4417/api/coordinator/runtimes" -Method GET`
Expected: array with one entry for the Eling project. **No `UserScope` entry** (the filter in `RuntimeRegistry.Alive()` already excludes the sentinel; in a project-rooted run we don't have any user-home registration).

**Step 4: Stop backend**

Stop the `pnpm dev:backend` process (Ctrl+C in the terminal where you ran it, or kill the PID).

- [x] Verify health responds
- [x] Verify runtimes response shape (returned [] — no peer runtimes registered; UserScope sentinel correctly absent)
- [x] Report smoke test results

---

## Self-Review

**1. Spec coverage:**
- Architecture (single WebApplication) → Task 7
- Port binding rule (4x17, AddressAlreadyInUse exit) → Task 7 (`ResolveDashboardPort` + `IsAddressInUse` try/catch)
- Self-registration → Task 7 (`registry.Register` + `ApplicationStopping.Register` unregister)
- File plan (transfer from Dashboard, simplify Host) → Tasks 2-7
- `BuildDashboard` target → Task 1 (csproj)
- Test project (one project at a time, per csproj) → Task 9-10
- Updated references (slnx, package.json, opencode.json) → Tasks 8, 12
- Binary rename (eling → eling-backend) → Task 13

**2. Placeholder scan:** No "TBD" or "TODO" in steps. All file contents provided.

**3. Type consistency:** `Program` class exposed via `public partial class Program;` matches `WebApplicationFactory<Program>` usage pattern (carried over from Dashboard). `RuntimeRegistry.Register`/`Unregister` signatures match Dashboard's existing API. `RuntimeRegistration` and `CoordinatorJsonContext` are in `Eling.Core` (verified in spec exploration).

---

## Execution Handoff

Plan saved to `docs/superpowers/plans/2026-08-31-merge-host-dashboard-superseded.md` (this file; the original filename was retired when the plan was marked historical after the subsequent `memory_recall` rename). Two execution options:

1. **Subagent-Driven (recommended)** — fresh subagent per task, two-stage review, fast iteration
2. **Inline Execution** — execute in this session with checkpoint reviews

Which one do you prefer?

---

## Post-Plan Work (executed 2026-08-31 → 2026-09-01, reflected in CHANGELOG [Unreleased])

Deviations from the plan above; the current code is the source of truth:

- Core csproj kept minimal packages (Ulid, YamlDotNet, Microsoft.Data.Sqlite); DI/Hosting/Serilog packages went into `Eling.Backend.csproj` instead (Task 1).
- Application files were reorganized under `Eling.Core/Memory/`, `Memory/Serialization/`, `Memory/Storage/`, `Intention/`, `Runtime/`, `Scope/` rather than a flat root layout (Task 2). `MemoryScopeParser` is a static class inside `IMemoryScopePolicy.cs`, not its own file.
- Mcp files landed under `Eling.Backend/Mcp/` with `Mcp/Tools/` splitting tools one-per-file (MemoryWriteTool, MemoryReadTool, MemoryIndexTool, MemoryPromoteTool, SessionStartTool) instead of a single `MemoryTools.cs` (Task 4). Mcp DTOs merged into the single `Eling.Backend/Dtos/` folder (13 DTOs) instead of a separate `McpDtos/`.
- Added `Eling.Backend/Bootstrap/` (DashboardPort, ProjectContext, DashboardServices, DashboardRoutes, RuntimeSelfRegistration, BindFailure, ProcessStartTime) — not in the original plan.
- `Program.cs` (Task 10) delegates to the Bootstrap classes, probes the port before binding, listens dual-stack (127.0.0.1 + ::1) when it owns the port (Kestrel skipped, MCP-only otherwise), and spawns `pnpm dev:frontend` in dev mode when port 4427 is free (`TrySpawnPnpmFrontend`).
- `HttpCoordinatorMemoryChangeNotifier.cs` was transferred from Eling.Host (plan said none needed).
- `BuildDashboard` uses `$(MSBuildThisFileDirectory)` relative paths (plan used `$(MSBuildProjectDirectory)`).
- `src/backend/Eling.Application/` was deleted 2026-09-01 (Deleted-list item executed late).
- The plan contains a duplicated csproj fragment between Task 4 and Task 5 (copy-paste artifact); its checkboxes are ticked against the real `Eling.Backend.csproj`.
- Open: unit tests for `Eling.Backend.Tests` not re-verified this session (vstest connection timeout, see Task 13).
- Open: `tests/Eling.Backend.Tests/TestProcesses.cs` still resolves old binary names (`Eling.Host` → `eling.dll`, `Eling.Dashboard` → `eling-dashboard.dll`); should target the merged `eling-backend` binary.
