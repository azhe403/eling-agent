# Memory Maintenance Tool Design

> Status: Draft (pending user review).
> Supersedes: none.
> Plan: to be created via writing-plans after approval.
> Tracker: created 2026-09-01.

## 1. Purpose

Define an on-demand **memory maintenance** capability for Eling: a single pipeline
that detects and (with explicit approval) resolves duplicate, near-duplicate, stale,
and index-drifted memories. It is exposed three ways from one service:

1. **MCP tool** `memory_maintenance` — for agents.
2. **REST endpoint** `POST /api/memory/maintenance` — for the dashboard and scripts.
3. **Dashboard page** `/maintenance` — for humans.

The tool is **non-destructive by default** (dry-run), deterministic and offline
(no embeddings), and addresses the manual dedup pain of 2026-08-28, where duplicate
memories had to be removed by hand via permanently-destructive `memory_delete`.

## 2. Scope

### 2.1 In-Scope

- `IMemoryMaintenanceService` / `MemoryMaintenanceService` in `Eling.Core/Memory/`.
- Deterministic similarity (`MemorySimilarity`: normalization + Jaccard token overlap)
  as pure static functions.
- Pipeline phases: `detect`, `dedup`, `merge`, `cleanup`, `reconcile`.
- MCP tool `memory_maintenance` (`Eling.Backend/Mcp/Tools/MemoryMaintenanceTool.cs`).
- REST endpoint `POST /api/memory/maintenance`
  (`Eling.Backend/Endpoints/MemoryMaintenanceEndpoints.cs`).
- Dashboard page `/maintenance` (`Eling.Dashboard`).
- DI wiring (`Eling.Backend/Mcp/McpServiceExtensions.cs`, `Bootstrap/DashboardServices.cs`).
- Tests: core service, MCP tool, endpoint, similarity.

### 2.2 Out-of-Scope

- Embeddings / semantic similarity (Eling remains keyword/FTS5 based and offline).
- Cross-scope merging (dedup/merge are always intra-scope).
- Intention cleanup / expiry (separate concern; memories only in v1).
- Automatic or scheduled runs (on-demand only, per user request).
- Tombstone/undo for deletions — deletions stay explicitly gated; superseded files
  remain on disk as history (see Merge Semantics).

## 3. Tool Contract (MCP)

### 3.1 Name

`memory_maintenance`

### 3.2 Input Schema

```json
{
  "type": "object",
  "properties": {
    "scope": { "type": "string", "description": "project | global | merged (default merged)" },
    "dryRun": { "type": "boolean", "description": "Detect only, apply nothing (default true)" },
    "operations": { "type": "array", "items": { "type": "string" }, "description": "dedup | merge | cleanup | reconcile (default: all)" },
    "approveFindingIds": { "type": "array", "items": { "type": "string" }, "description": "Finding keys to actually apply for risky operations (merge, cleanup). Ignored when dryRun=true." },
    "similarityThreshold": { "type": "number", "description": "Jaccard threshold 0..1 for fuzzy merge (default 0.8)" },
    "staleDays": { "type": "integer", "description": "Age cutoff for archived/superseded cleanup (default 90)" }
  }
}
```

### 3.3 Output Schema

```json
{
  "dryRun": true,
  "findings": [
    {
      "key": "merge:project:01M111...,01M222...",
      "operation": "dedup | merge | cleanup | reconcile",
      "scope": "project | global",
      "memoryIds": ["01M111...", "01M222..."],
      "proposedAction": "merge | delete | index | removeFromIndex",
      "reason": "identical normalized content | jaccard 0.83 | empty content | archived 120d | file not in index | index entry without file",
      "applied": false,
      "result": null
    }
  ],
  "stats": {
    "memoriesScanned": 0,
    "duplicateGroups": 0,
    "mergeGroups": 0,
    "cleanupCandidates": 0,
    "orphanFiles": 0,
    "danglingEntries": 0,
    "merged": 0,
    "superseded": 0,
    "deleted": 0,
    "indexed": 0,
    "removedFromIndex": 0,
    "skipped": 0,
    "failed": 0
  }
}
```

## 4. Behavior

### 4.1 Scope resolution

`scope` resolves like `memory_list`/`memory_search` (`project` | `global` | `merged`,
default `merged`). Merged runs the pipeline for **each** scope separately; dedup and
merge never cross scopes. Invalid scope → `ArgumentException`.

### 4.2 Pipeline phases

1. **detect** (always first): collects findings for every requested `operation`,
   with **zero mutations**. If `dryRun: true`, the run stops here and returns findings.
2. **dedup**: among `Active` memories, group by normalized content (identical logic
   to save-time dedup: trim + case-insensitive). Groups with >1 member → one finding
   per group with `proposedAction: "merge"`.
3. **merge** (fuzzy): among remaining (non-dedup'd) **`Active`** memories of the
   **same `type`**, pairs with Jaccard similarity ≥ `similarityThreshold` are
   grouped by **transitive closure** (a pair (A,B) and (B,C) merge into one group
   {A,B,C}) → one finding per maximal group. Memories already in a dedup finding
   are excluded. Empty/tokenless memories are excluded (handled by cleanup instead).
4. **cleanup**: memories with empty/whitespace content → `proposedAction: "delete"`;
   memories with status `Archived`/`Superseded` and `updatedAt` older than
   `staleDays` → `proposedAction: "delete"`.
5. **reconcile**: files on disk with no index entry → `proposedAction: "index"`;
   index entries with no backing file → `proposedAction: "removeFromIndex"`.

### 4.3 Apply mode (`dryRun: false`)

- **dedup** and **reconcile** findings always apply (safe: mirror save-time behavior;
  reconcile is non-destructive since disk is the source of truth).
- **merge (fuzzy)** and **cleanup** findings apply **only** when their finding `key`
  is present in `approveFindingIds`. `approveFindingIds` is ignored in dry-run.
- After applying, detection re-runs so the returned report reflects the
  post-maintenance state (remaining findings, if any).
- Typical MCP flow: call with `dryRun: true` → inspect findings → call again with
  `dryRun: false` and the `approveFindingIds` of the merge/delete items to execute.

## 5. Merge Semantics

Identical for dedup and fuzzy merge:

- **Survivor**: the memory with the smallest `MemoryId` (ULID sorts by creation
  time → oldest wins). Its `content` is kept **unchanged** (for dedup the contents
  are identical; for fuzzy merge the survivor content stands as-is and the value of
  the merge is tag/source consolidation — the finding shows both contents for review).
- **Tags**: union of all group members (distinct).
- **Source**: most recent non-empty `source` among members.
- **Survivor status**: unchanged (stays `Active`).
- **Absorbed memories**: status → `Superseded`, `updatedAt` = now. Files remain on
  disk (non-destructive; git-tracked history preserved; excluded from recall).
- **Survivor `updatedAt`**: now.

## 6. Similarity (`MemorySimilarity`, pure static)

- `Normalize(string)`: trim, lowercase, split on whitespace/punctuation → token set.
- `Jaccard(a, b)`: `|A ∩ B| / |A ∪ B|` over normalized token sets.
- Exact-normalized dedup is the special case handled by the string-compare path
  (identical to save-time dedup), not by Jaccard.
- Deterministic, offline, unit-testable without any service.

## 7. Components

| Component | Location | Responsibility |
|---|---|---|
| `IMemoryMaintenanceService` | `Eling.Core/Memory/IMemoryMaintenanceService.cs` | Interface + request/result records |
| `MemoryMaintenanceService` | `Eling.Core/Memory/MemoryMaintenanceService.cs` | Pipeline orchestration (detect/apply, per scope) |
| `MemorySimilarity` | `Eling.Core/Memory/MemorySimilarity.cs` | Normalization + Jaccard (pure static) |
| `MaintenanceModels` | `Eling.Core/Memory/MaintenanceModels.cs` | `MaintenanceRequest`, `MaintenanceReport`, `MaintenanceFinding`, `MaintenanceStats` |
| `MemoryMaintenanceTool` | `Eling.Backend/Mcp/Tools/MemoryMaintenanceTool.cs` | MCP tool `memory_maintenance` (thin mapper) |
| `MemoryMaintenanceEndpoints` | `Eling.Backend/Endpoints/MemoryMaintenanceEndpoints.cs` | `POST /api/memory/maintenance` |
| `MemoryMaintenanceDtos` | `Eling.Backend/Dtos/MemoryMaintenanceDtos.cs` | HTTP request/response DTOs |
| DI wiring | `Eling.Backend/Mcp/McpServiceExtensions.cs`, `Bootstrap/DashboardServices.cs` | Register service + tool |
| Dashboard page | `src/frontend/Eling.Dashboard/app/maintenance/` | Maintenance UI |

Service keeps all pipeline logic (Core), so the MCP tool and endpoint stay thin
mappers — consistent with the existing service/tool split.

## 8. REST Contract

`POST /api/memory/maintenance`

```json
// Request
{ "scope": "merged", "dryRun": true, "operations": ["dedup","merge","cleanup","reconcile"],
  "approveFindingIds": [], "similarityThreshold": 0.8, "staleDays": 90 }
// Response 200
{ "dryRun": true, "findings": [ /* MaintenanceFinding[] */ ], "stats": { /* MaintenanceStats */ } }
```

Errors: invalid `scope`/`operation`/`threshold`/`staleDays` → `400`; host without
scoped services → `500`. Follows the existing `MemoryEndpoints`/`ScopedMemoryEndpoints`
minimal-API patterns.

## 9. Dashboard

New page `/maintenance` in the Next.js dashboard (routed through the existing
`/api/*` proxy to the backend):

- **"Run report"** button → `POST /api/memory/maintenance` with `dryRun: true`
  (defaults: merged scope, all operations) → findings grouped by operation.
- Risky findings (fuzzy merge, delete) render with a **checkbox per finding**
  (checked key = finding `key`).
- **"Apply"** button → posts with `dryRun: false` and `approveFindingIds` =
  checked keys (dedup + reconcile auto-apply). Result view shows applied actions
  and remaining findings.

The UI mirrors the MCP flow: report first, decisions second, per-item approval.

## 10. Error Handling

- Invalid `scope` / unknown `operation` / `similarityThreshold` outside 0..1 /
  `staleDays` < 0 → `ArgumentException` (MCP) / `400` (HTTP).
- **Concurrency**: a save occurring mid-run may invalidate a finding; actions are
  idempotent — conflicts are skipped with `skipped` status and reported, never
  aborted. Re-running the report confirms the end state.
- **Per-action failures**: try/catch per finding; one failure does not stop the
  pipeline; failures are reported per finding (`result` + `stats.failed`).
- Missing scoped services in host → `InvalidOperationException` (consistent with
  other memory tools).

## 11. Testing

- `tests/Eling.Core.Tests/MemoryMaintenanceServiceTests.cs`:
  dedup group detection; merge semantics (oldest ULID survives, tags union, newer
  source, absorbed → `Superseded`); only `Active` memories dedup'd; fuzzy pairs
  same-type only; transitive closure grouping (A~B, B~C → one group {A,B,C});
  threshold boundary (0.79 vs 0.80 with default); cleanup detection
  (empty content, stale archived/superseded); reconcile (orphan file → index,
  dangling entry → remove); **invariant: `dryRun: true` mutates nothing**; fuzzy
  merge and delete apply only when `approveFindingIds` contains the key; idempotency
  (apply twice → second run finds nothing); per-action failure continues pipeline.
- `tests/Eling.Core.Tests/MemorySimilarityTests.cs`: normalization, Jaccard values,
  threshold edges, empty/tokenless inputs.
- `tests/Eling.Backend.Tests/MemoryMaintenanceToolTests.cs`: tool delegates to
  service, maps findings/stats, rejects invalid scope.
- `tests/Eling.Backend.Tests/MemoryMaintenanceApiTests.cs`: endpoint returns 200
  report shape; 400 on invalid input; dry-run default via API.
- Test infra: fake `IMemoryStorage`/`IMemoryIndex` plus temp-dir
  `FileSystemMemoryStorage` (existing pattern). Run `dotnet test` per csproj
  (`tests/Eling.Core.Tests`, `tests/Eling.Backend.Tests`), never solution-wide.
