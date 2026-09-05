# Changelog

All notable changes to Eling are documented here. Follows [Keep a Changelog](https://keepachangelog.com/) and [Conventional Commits](https://www.conventionalcommits.org/).

## [Unreleased] - 2026-09-01

### Changed
- **Single binary `eling-backend`**: Merge `Eling.Host` + `Eling.Dashboard` + `Eling.Mcp` + `Eling.Application` → `Eling.Core` + `Eling.Backend` (`Microsoft.NET.Sdk.Web`). One process serves MCP (stdio) + REST (4317/4417) + UI. Removes `SpawnDashboard`, `DashboardCoordinator`, `DashboardLauncher`, `DashboardPaths` cross-process coordination.
- **Backend layout**: `Eling.Core` now owns Memory, Storage, Serialization, Intention, Scope, Runtime, MemoryRecall (flat `Eling.Core` namespace). `Eling.Backend` owns `Bootstrap/`, `Mcp/Tools/`, `Endpoints/`, `Dtos/` (single `Dtos/` folder, 13 DTOs).
- **DTO consolidation**: `Eling.Backend/Dtos/` + `Eling.Backend/Mcp/Dtos/` → single `Eling.Backend/Dtos/` (namespace `Eling.Backend.Dtos`). `Mcp/Tools` split: `MemoryWriteTool`, `MemoryReadTool`, `MemoryIndexTool`, `MemoryPromoteTool`, `MemoryRecallTool` (one logical tool per file).
- **Port ownership**: `RuntimeRegistry.Alive()` now filters `UserScope` sentinel. Frontend `memories/page.tsx` shows full ULID (`break-all`) instead of `6…4` truncation.
- **Dev servers**: Kestrel listens on `127.0.0.1` + `::1` (dual stack) so `localhost:4427` and `127.0.0.1:4427` both work. Next.js dev: `next dev -H 0.0.0.0 -p 4427`. `Eling.Backend` probes port before bind — if peer owns 4317/4417, skips Kestrel and runs MCP-only.
- **Scripts**: `package.json` `dev:backend` → `Eling.Backend.csproj`, `opencode.json` `eling_dev` → `watch --project Eling.Backend`, `scripts/validate-eling.ps1` + `test-single-mode.ps1` updated to `eling-backend.exe`, workflows `release.yml`/`pre-release.yml` publish single `Eling.Backend`.
- **MCP tool `session_start` → `memory_recall`**: renamed the on-demand context-hydration tool so the name reflects what it actually does (recall memories) rather than when it was historically called. The MCP `Name` is now `memory_recall`. Folder `Eling.Core/SessionStart/` is renamed to `Eling.Core/MemoryRecall/`; `ISessionStartService` → `IMemoryRecallService`; DTOs `SessionStart*` → `MemoryRecall*`. Bug fix: the previous implementation dropped the `topics` input and always returned `recallMemories: []`, and `recentLimit` was hard-coded to `Take(5)` regardless of the parameter; the new service actually performs `ScopedMemoryService.SearchAsync(topics, scope, recallLimit)` and respects `recentLimit`. New stats fields `recallCount` / `recentCount` surface the actual returned counts.

### Fixed
- Frontend ID truncation and scope selector showing `UserScope` as project.
- `pnpm build` path for `BuildDashboard` msbuild target (relative `MSBuildThisFileDirectory`).
- `Eling.Core` `Intention` namespace collision (`Intention` class vs namespace) by keeping flat `Eling.Core` namespace.

## [0.1.0-pre.2] - 2026-08-30

### Fixed
- `ci(pre-release)`: env-resolution error in `softprops` `tag_name` and `name`.

## [0.1.0-pre.1] - 2026-08-30

### Added
- `ci`: pre-release workflow, installer repo name update.
- `feat(pecut-10)`: verification tests, CI pipeline & cleanup; dashboard control plane API & scope selector UI; scope params through MCP; scope-aware memory application layer; scope-aware domain model & spec.
- `refactor`: consolidate HTTP APIs into `Eling.Dashboard`; consolidate libraries into `Core/Application`.
- `feat`: single-binary runtime (PECUT 9) with web dashboard; intention MCP tools (CRUD).

### Changed
- `chore(build)`: isolate per-configuration `obj/` and `.bin/` output; keep runtime `.bin` separate from `.bin-test`.
- `chore(scope)`: scope-aware memory + dedup + UI polish.

## [0.1.0] - 2026-08-12 to 2026-08-26

- Initial memory domain model & storage design, intention domain model, MCP tools (`memory_save`, `memory_get`, etc.), file storage + FTS5 index, Git-native `.eling/memories/*.md` canonical format.
