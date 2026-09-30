# Inactive Scoped Projects Tracking & Last Active Status — Design

Date: 2026-09-18
Status: Draft for review
Scope: Eling.Core (Runtime/Projects), Eling.Backend (Coordinator/Registry/Endpoints), Eling.Dashboard (UI/Sidebar)

> All example project names and paths in this document are anonymized placeholders
> (e.g. `C:\work\acme\project-alpha`). No real usernames or machine paths are used,
> per the project hygiene rule.

## Context

Currently, the Eling Dashboard populates the scoped project list in the sidebar and memory filters solely from active process registrations held in `RuntimeRegistry.Alive()`. When an agent session or workspace in an IDE is closed, the process either unregisters or is swept by the liveness sweeper (`Sweep()`). This completely removes the entry from memory and disk (`.runtimeDir`), causing the project to immediately vanish from the dashboard's scoped list.

However, the actual project memories in `.eling/memories/` and the local SQLite index remain intact on disk. Users frequently need to browse, search, or review memories from projects that are not currently running in an active editor session without having to manually restart an IDE process just to make the project visible in Eling.

## Goals

1. **Persistent Project Registry**: Maintain a persistent history of discovered and registered workspace projects in user scope (`~/.config/eling/projects.json`).
2. **Liveness & Last Active Tracking**: Distinguish between active sessions (`isAlive: true`) and inactive sessions (`isAlive: false`), recording the exact `lastActive` timestamp when the project was last seen or communicated with.
3. **Physical Existence Verification (`isExists`)**: Actively verify whether the physical directory (`projectRoot` and `dataDirectory`) still exists on disk (`isExists: true/false`), allowing the UI to flag missing/moved folders and provide clean removal options.
4. **Seamless Offline Memory Access**: Allow the dashboard to open, read, search, and update memories from existing inactive projects directly from their on-disk `.eling` storage without requiring a live process.
5. **Clear Visual Posture in UI**: Provide unambiguous indicators in the dashboard sidebar:
   - Live project: Active indicator (e.g., green dot / live badge).
   - Inactive project: Muted styling with humanized last active time (e.g., "Offline • 2h ago").
   - Missing project: Warning indicator (e.g., "Folder not found") with a "Forget" action.

## Non-Goals (YAGNI)

- No remote network sync for projects across different machines.
- No auto-spawning or background launching of editor processes from the dashboard.
- No deep continuous filesystem watching for deleted project directories; `isExists` is verified on disk sync / query time.

## Architecture & Data Flow

```
+-------------------------------------------------------------+
|                     Eling Backend                           |
|                                                             |
|  [Agent / IDE Process]                                      |
|          | (register / heartbeat)                           |
|          v                                                  |
|  +---------------------+        +------------------------+  |
|  |   RuntimeRegistry   | -----> |   KnownProjectsStore   |  |
|  +---------------------+        +------------------------+  |
|          |                                   |              |
|          | (heartbeat & liveness)            v (persisted)  |
|          v                       ~/.config/eling/           |
|  +---------------------+            projects.json           |
|  | Liveness Sweeper    |                                    |
|  | (marks isAlive=false|                                    |
|  |  updates lastActive)|                                    |
|  +---------------------+                                    |
+-------------------------------------------------------------+
                           |
                           v (GET /api/coordinator/runtimes or /api/projects)
+-------------------------------------------------------------+
|                     Eling Dashboard                         |
|                                                             |
|  App Sidebar / Memory Scopes:                               |
|  - 🟢 project-alpha (Live)                                  |
|  - 📁 project-beta  (Offline • 3h ago)                      |
|  - ⚠️ project-gamma (Missing folder • [Forget])             |
+-------------------------------------------------------------+
```

## Detailed Design

### 1. Data Model (`ProjectRecord` & `ScopedProjectDto`)

A persistent project record stored in `~/.config/eling/projects.json`:

```csharp
public sealed class ProjectRecord
{
    public required string ProjectRoot { get; set; }
    public required string ProjectName { get; set; }
    public required string DataDirectory { get; set; }
    public DateTimeOffset FirstSeen { get; set; }
    public DateTimeOffset LastActive { get; set; }
}
```

The DTO returned to the frontend via API:

```csharp
public sealed class ScopedProjectDto
{
    public required string ProjectRoot { get; init; }
    public required string ProjectName { get; init; }
    public required string DataDirectory { get; init; }
    public required bool IsAlive { get; init; }
    public required bool IsExists { get; init; }
    public required DateTimeOffset LastActive { get; init; }
    public int? ProcessId { get; init; }
}
```

### 2. KnownProjectsStore (User-Level Persistence)

A dedicated store located at `UserScope.ConfigDirectory/projects.json`:
- **Load**: On startup, loads existing known project records.
- **Upsert**: When `RuntimeRegistry.Register()` or `Heartbeat()` is called, updates `LastActive = DateTimeOffset.UtcNow`.
- **Verify Existence (`IsExists`)**: Checks `Directory.Exists(projectRoot)` when constructing project DTOs.
- **Remove / Forget**: Allows removing a project from the registry via `DELETE /api/projects?projectRoot={path}` if a user wants to clear a stale or deleted project.

### 3. RuntimeRegistry Integration & Memory Resolution

- When a process exits (`Unregister`) or goes stale (`Sweep`), the runtime is marked `isAlive = false` in the project store instead of wiping out the project reference.
- `TryResolveMemoryServiceByProjectRoot(projectRoot)`:
  - If a live runtime exists, uses the runtime's memory service.
  - If no live runtime exists but `Directory.Exists(dataDirectory)` is `true`, resolves and caches a `MemoryService` using the on-disk `.eling` folder and `index.db`.
  - If the folder does not exist (`isExists == false`), returns `null` (NotFound).

### 4. Frontend Dashboard UI (`app-sidebar.tsx`)

- The sidebar requests the list of projects and renders each scope with status indicators:
  - **Live (`isAlive == true`)**: Full brightness text, green status dot / badge.
  - **Offline (`isAlive == false && isExists == true`)**: Slightly muted text, subtitle with relative time (e.g. `Last active 2h ago`), normal navigation to `/dashboard/memories?scope=...`.
  - **Missing (`isExists == false`)**: Grayed out, warning badge (e.g. `Folder not found`), with an inline action menu to "Remove from list".

## Validation & Testing Plan

1. **Unit Tests (`Eling.Core.Tests` / `Eling.Backend.Tests`)**:
   - `KnownProjectsStoreTests`: Verify creation, JSON serialization, updating last active timestamp, and removing entries.
   - `RuntimeRegistryTests`: Verify that unregistering a PID updates `isAlive = false` and preserves the project in the returned list.
   - `ProjectExistenceTests`: Verify `isExists` accurately reflects directory presence on disk.
   - `OfflineMemoryResolutionTests`: Verify reading/searching memories from an offline project with existing disk files succeeds.
2. **Frontend Component Tests & Verification**:
   - Verify sidebar updates via SSE when runtimes change status.
   - Verify rendering of live, offline, and missing states.
