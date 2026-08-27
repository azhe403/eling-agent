# Pecut 10 — Scope-Aware Memory Management + Dashboard Control Plane

> **Status**: LOCKED — 2026-08-26
> **Memory ID**: 01m0z95pffr2s2kyg6p73e0gy4 (Eling Decision)
> **Context**: Pecut 9 established project-scoped MCP runtime + shared dashboard coordinator. Pecut 10 introduces two real memory scopes.

## Goal

Implement scope-aware memory management across Eling.Application, MCP memory tools, project memory runtime, global memory, Dashboard control plane, and Dashboard UI/API — preserving storage ownership, project isolation, and scope identity.

## Target Architecture

```
                Dashboard Control Plane
                        │
        ┌───────────────┼────────────────┐
        ▼               ▼                ▼
    Global Memory    Project A        Project B
      UserScope       Runtime          Runtime
        │               │                │
        ▼               ▼                ▼
    Global Store      .eling          .eling
```

Dashboard aggregation is a VIEW — ProjectScope and GlobalScope remain independent ownership boundaries.

## 1. Memory Scope Model

```csharp
enum MemoryScopeKind { Project, Global }

record MemoryReference
{
    MemoryId Id { get; }
    MemoryScopeKind Scope { get; }
    string? ProjectRoot { get; } // null for Global
}

record ScopedMemory
{
    Memory Memory { get; }
    MemoryScopeKind Scope { get; }
    string? ProjectRoot { get; }
    string? ProjectId { get; }
}
```

- Same `MemoryId` may exist independently in Global vs Project — operations must be scope-qualified.
- Every result returned outside its native store carries scope identity.
- Invalid contract: `DELETE /memory/{id}` alone. Valid: `Global + MemoryId` or `ProjectIdentity + MemoryId`.

## 2. Storage Topology

- **Project A**: `/projects/a/.eling/` → project memories + project index
- **Project B**: `/projects/b/.eling/` → project memories + project index
- **UserScope**: `~/.config/eling/` → global memories + global index (`memories/` + `index.db` under UserScope root)

Do NOT merge into one DB. No global row inside project DB. No project namespace inside global DB.

## 3. Application Layer Owns Scope Decisions

Scope logic in `Eling.Application`. NOT in `Eling.Mcp`, Dashboard UI, or SQLite implementation.

Abstractions:

- `IMemoryScopePolicy` — resolves `scope=project|global|auto` → concrete scope
- `IMemoryScopeRouter` / `IScopedMemoryService` — routes to correct `IMemoryService` instance
- `IMemoryMerger` — merges Project+Global results, preserves Rank + Scope, Project has priority
- `IGlobalMemoryService` / `IProjectMemoryService` resolved via factory keyed by scope

MCP and Dashboard are adapters. Storage persists scoped data. Application coordinates policy.

## 4. Write Behavior

```
scope = project → Project
scope = global  → Global
scope = auto    → Project (Pecut 10: auto resolves to Project, no LLM classifier)
scope omitted   → Project (default)
```

Explicit scope always wins. Application layer resolves destination. MCP adapter must not choose storage by filesystem paths.

## 5. Global Memory

Real global memory under `UserScope` (`UserScope.Resolve()` → `~/.config/eling`).

Must support: create, get, search, update, delete, rebuild-index — without any active project runtime.

`FileSystemMemoryStorage` + `SqliteMemoryIndex` instantiated with `UserScope.Root` paths.

## 6. Project Memory

Owned by project runtime (`ProjectScope.Discover()` → nearest `.eling`).

Flow:

```
Dashboard → Dashboard Control Plane → Project Runtime → Application Memory Service → Project .eling storage
```

Dashboard must NOT directly open arbitrary `<project>/.eling/` behind runtime's back. Access via registry lookup of alive runtime.

If runtime inactive → not in "Open Projects".

## 7. Runtime Control-Plane Protocol

Extend Dashboard ↔ Runtime `RuntimeRegistry` connection to support:

- list/query project memories
- search project memories
- get/create/update/delete project memory

Minimal local protocol only (Dashboard ↔ local active runtimes). No gRPC, bus, or cloud RPC.

## 8. Dashboard Control Plane API

Global:

- `GET /api/global/memories`, `POST`, `GET /{id}`, `PATCH /{id}`, `DELETE /{id}`, `GET /search?q=`

Project (routed via registry):

- `GET /api/projects/{projectId}/memories`, `POST`, `GET /{id}`, `PATCH /{id}`, `DELETE /{id}`, `GET /search?q=`

Aggregated (virtual):

- `GET /api/memories/aggregated?q=&scope=all` — fans out to Global + each alive project, preserves scope

Response preserves source scope:

```json
{ "id": "...", "scope": "global", "content": "..." }
{ "id": "...", "scope": "project", "project": { "id": "...", "root": "..." }, "content": "..." }
```

## 9. Dashboard Scope Selector

```
MEMORY SCOPE
[ Global ] [ Project A ] [ Project B ] [ All Open Projects ]
```

Active projects from `RuntimeRegistry.Alive()`. Do NOT scan filesystem. Must distinguish Global / Project / Aggregated badges.

## 10. Views

- **Global**: only Global memories; Create/Edit/Delete/Search → Global.
- **Project A**: only Project A memory; operations route through Project A runtime; UI shows project identity.
- **All Open Projects** (virtual, NOT a scope): `Global + active Project A + active Project B`. Every result shows `🌐 Global` or `📁 Project X`. Search aggregates per-scope then merges. `+ Add Memory` requires destination selection (Global / Project A / Project B). Never save to "All".

## 11. Editing / Delete / Copy

- Edit keeps original scope — no re-prompt.
- Delete always targets original scope (`Global+Id` or `Project+Id`).
- Copy/Promote explicit only:

  - `Promote to Global`: Project → copy → Global (source unchanged)
  - `Copy to Project`: Global → copy → Project (source unchanged)
  - No implicit move, no auto-delete source.

## 12. Agent Memory Search

MCP inside Project A, default search = `Project A + Global` (merged, Project priority).

Flow:

```
Agent → Eling.Mcp → Eling.Application → Project Search + Global Search → Merge → Agent Result
Final Rank = relevance + scope preference (Project > Global)
```

Dedup: same scoped identity + optional exact normalized content. No embeddings/vector.

MCP contracts:

```
remember: scope = project|global|auto (default project)
search:   scope = project|global|merged (default merged)
```

`merged` = current Project + Global only, never every project on machine. Project A MCP must NOT access Project B.

## 13. Security / Isolation

- Project A MCP may access Project A + Global only.
- Dashboard sees only active runtimes via registry; no filesystem/git scanning.
- Dashboard aggregation does not change MCP authority.

## 14. Frontend UX (Minimal)

Memory page: Scope selector, Search, Memory list with scope badges, Create, Edit, Delete, Copy/Promote actions.

Architecture correctness > visual polish. Power management / Keep Awake out of scope.

## 15. Tests Required

- Scope identity distinct, same MemoryId independently addressable, destructive ops scope-qualified
- Project isolation: A cannot read/write/delete B, A can read Global
- Write policy: default Project, explicit Global/Project, auto→Project
- Global persists in UserScope without project runtime
- Project remains in .eling owned by runtime
- Agent search: default Project+Global, isolated scopes, ranking, no cross-project leak
- Dashboard: Global view only Global, Project view correct runtime, registry selector, aggregated includes Global+active projects preserving identity, no storage merge, add from aggregate requires target, edit/delete stay in origin, copy/promote semantics, inactive not shown, no direct storage open, Global available with zero runtimes

## 16. Build / Validation

```
dotnet build Eling.slnx --artifacts-path .artifacts
dotnet test Eling.slnx --artifacts-path .artifacts
```

No regression to Pecut 9 runtime lifecycle, MCP stdio, ProjectScope/UserScope resolution.

## Acceptance (Pecut 10 Complete)

Given Global="User prefers concise answers", Project A="Use C#", Project B="Use Python":

- Project A search sees A+Global, not B; Project B sees B+Global, not A
- Dashboard Global→Global, Project A→A, Project B→B, All Open Projects→Global+A+B with scope badges
- No shared project-memory DB, no cross-project MCP, no filesystem scanning, no scope loss, no implicit move
