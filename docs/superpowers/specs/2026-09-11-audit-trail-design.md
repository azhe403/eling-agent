# Audit Trail v1 — Change + Access Trails (Design)

> **Status:** LOCKED v1 — 2026-09-11.
> **Supersedes:** `docs/superpowers/specs/2026-08-29-audit-trail-spec.md` (superseded). The previous
> design assumed a Vestige mirror that has no client in the source tree and is not
> implementable from the backend; it also covered only mutations and did not anticipate the
> access (read/query) trail or the filesystem-tool surface.
> **Tracker memory:** `01m28c7eavp62hyqj3n090m55h` (Eling project scope).
> **Phase 1 plan:** `docs/superpowers/plans/2026-09-11-audit-trail-phase1-core-engine.md`.
> **Related designs:** `2026-09-04-eling-mcp-filesystem-tools-design.md`,
> `2026-09-10-eling-desktop-agentic-design.md`.

## 1. Purpose

Record every Eling operation — both state changes and reads/queries — into a durable,
queryable audit system that supports governance, compliance, forensics, and a live view in
the dashboard and desktop clients. The system must not degrade query latency and must remain
readable as plain text on disk.

## 2. Two Trails

The volume and value profile of a mutation and a read are fundamentally different, so they are
recorded as two independent trails with independent storage, retention, and durability.

| Trail | Contains | Volume | Raw retention | Durability |
|---|---|---|---|---|
| **Change trail** | State-changing operations | Low | Long (default 365 days) | Must not silently lose entries |
| **Access trail** | Read/query operations | High (1000×+) | Short (default 14 days) | Best-effort; losing the tail is acceptable |

Both trails live side by side under the same machine-global audit directory and are separate files.

## 3. Domains and Actions

Three domains are in scope. Every audited operation belongs to exactly one domain.

### 3.1 `memory`
Change: `memory_save` (create), `memory_update` (explicit update or smart-save merge),
`memory_delete`, `memory_copy`, `memory_move`, `memory_promote`, `memory_init`,
`memory_policy_change`, `memory_maintenance`.
Access: `memory_recall`, `memory_read`, `memory_search`, `memory_list`, `memory_project_status`.

### 3.2 `runtime`
Change: `runtime_register`, `runtime_unregister`, `runtime_stale_sweep` (only when at least one
runtime is actually pruned), `dashboard_autoshutdown`.

### 3.3 `filesystem`
Change: `fs_write`, `fs_append`, `fs_move`, `fs_copy`, `fs_delete`, `fs_directory_create`,
`fs_directory_move`, `fs_directory_copy`, `fs_directory_delete`.
Access: `fs_read`, `fs_list`, `fs_glob`, `fs_grep`, `fs_path_test`, `fs_directory_list`.

Filesystem **access** entries record metadata only (path, bytes, match count, kind). File
contents are never recorded.

## 4. Entry Schema

Each entry is one JSON line (JSONL). Field names are camelCase and serialized with the shared
`System.Text.Json` source-generated context.

```json
{
  "schemaVersion": 1,
  "timestamp": "2026-09-11T09:30:00.000+00:00",
  "trail": "change",
  "category": "memory",
  "action": "memory_update",
  "actor": "mcp:eling_dev",
  "source": "mcp_stdio",
  "scope": "project",
  "projectRoot": ".",
  "target": "01m...",
  "outcome": "success",
  "durationMs": 12,
  "metadata": {
    "content": "the remembered text after the change",
    "tags": ["audit"],
    "status": "active",
    "memorySource": "user_chat"
  },
  "previousMetadata": {
    "content": "the remembered text before the change",
    "tags": [],
    "status": "active"
  }
}
```

An access entry carries only event detail, with no state snapshot:

```json
{
  "schemaVersion": 1,
  "timestamp": "2026-09-11T09:31:00.000+00:00",
  "trail": "access",
  "category": "filesystem",
  "action": "fs_read",
  "actor": "dashboard",
  "source": "dashboard_web",
  "target": "src/a.cs",
  "outcome": "success",
  "durationMs": 3,
  "path": "src/a.cs",
  "byteSize": 128
}
```

Null-valued fields are omitted on disk (the JSON context uses `WhenWritingNull`), keeping lines
small.

### 4.1 Field rules

| Field | Type | Rules |
|---|---|---|
| `schemaVersion` | int | Always `1`. Bump only on a breaking shape change. |
| `timestamp` | ISO-8601 UTC | Set at record time by the logger, not the caller. |
| `trail` | enum | `change` or `access`. |
| `category` | enum | `memory`, `runtime`, `filesystem`. |
| `action` | enum string | From §3. |
| `actor` | string | Ambient actor (`mcp:<name>`, `dashboard`, `system`). |
| `source` | string | Transport (`mcp_stdio`, `dashboard_web`, `system`). |
| `scope` | enum | `project`, `global`, or `null`. |
| `projectRoot` | string \| null | Path of the originating project, relative to the user home or repo root where possible; never a machine-specific absolute path. |
| `target` | string \| null | Memory ULID for `memory`, PID for `runtime`, relative path for `filesystem`. |
| `outcome` | enum | `success`, `error`, `denied`. |
| `durationMs` | int \| null | Operation duration when measurable. |
| `byteSize` | int \| null | Access only. Payload size (file bytes) when relevant. |
| `hash` / `prevHash` | string \| null | Reserved for the hash chain (§13). Always `null` in v1. |
| `query` | string \| null | Access only. Query text for memory search/recall. |
| `resultCount` | int \| null | Access only. Number of results returned. |
| `path` | string \| null | Access only. Relative path for filesystem actions. |
| `matches` | int \| null | Access only. Match count for `fs_grep` / `fs_glob`. |
| `pid` | int \| null | Runtime domain only. Process id. |
| `metadata` | object \| null | State at record time (the "after" side) for changes. |
| `previousMetadata` | object \| null | Prior state (the "before" side); `null` for create and access. |

Content is inlined only in the **change** trail, and only as the `metadata` (after) and
`previousMetadata` (before) state snapshots for `memory_*` changes. Access entries never inline
content.

### 4.2 State snapshots

`metadata` is the state of the data at record time — the "after" side. `previousMetadata` is the
"before" side and is present only when a prior state exists:

| Action | `metadata` | `previousMetadata` |
|---|---|---|
| `memory_save` (create) | created state | `null` |
| `memory_update` (explicit or smart-save merge) | updated state | prior state |
| `memory_delete` | `null` | removed state |
| `memory_copy` | target state | `null` |
| `memory_move` / `memory_promote` | target state | source state |
| any access action | `null` | `null` |

The state snapshot holds only `content`, `tags`, `status`, and `memorySource`. Everything else —
`query`, `resultCount`, `path`, `byteSize`, `matches`, `pid` — is event detail and lives as
top-level optional fields on the entry, never inside the snapshot. Null-valued fields are omitted
on disk (the JSON context uses `WhenWritingNull`), keeping lines small.

## 5. Actor and Scope Propagation

Call sites must not change signatures. An `AsyncLocal<AuditActor>` holds `Actor` and `Source`
for the duration of a request/tool call:

- MCP tools set `mcp:<ELING_MCP_SERVER_NAME>` (fallback `mcp`), source `mcp_stdio`.
- HTTP endpoints set `dashboard`, source `dashboard_web`.
- Runtime registry and hosted services set `system`, source `system`.

The audit logger resolves actor/source from the ambient context at record time, defaulting to
`unknown`. Logical scope and project root are supplied by the owner of the file, not the
ambient context: `MemoryService` carries an audit scope descriptor injected at construction,
`RuntimeRegistry` uses the runtime's own scope, and filesystem tools use the active project
scope.

## 6. Storage Architecture

### 6.1 Layout

A single **machine-global** store under the Eling data root. The root is resolved with the same
XDG-style rule as `CentralLogDirectory`: `$XDG_DATA_HOME/eling` when set, otherwise
`<userHome>/.local/share/eling` on every platform (Windows included; `%LOCALAPPDATA%` is not
used). Audit lives beside `logs/`:

```
<elingDataRoot>/audit/
  changes/2026-09-11.jsonl         # current day, uncompressed, appended
  changes/2026-09-10.jsonl.gz      # closed day, compressed
  access/2026-09-11.jsonl
  access/2026-09-10.jsonl.gz
  rollups/2026-09-10.jsonl         # daily aggregate, kept long-term
  audit-index.db                   # SQLite index (rebuildable)
```

Because the store is central, the originating project is carried in each entry's `scope` and
`projectRoot` fields, not by the file path.

### 6.2 Rotation and compression

Rotation is **lazy and daily**: before the first append whose date differs from the current
file's date, the writer closes and compresses the old file (`.jsonl.gz`) and starts a new one.
Rotation happens inside the write lock. The clock is injected so rotation is deterministically
testable.

### 6.3 Index

`audit-index.db` (SQLite) is a pure, rebuildable cache over the JSONL files: if it is deleted or
corrupt it is rebuilt by scanning the JSONL. It is **normalized into relational tables — there is
no JSON payload column**:

```
audit_events(id, timestamp, trail, category, action, actor, source, scope, project_root,
             target, outcome, duration_ms, byte_size, hash, prev_hash,
             query_text, result_count, path, matches, pid, file, line)
audit_state(id, event_id -> audit_events.id, side, content, status, memory_source)
audit_tags(state_id -> audit_state.id, ordinal, tag)
```

`audit_events` holds the scalar event fields plus the access-only detail columns (`query_text`,
`result_count`, `path`, `matches`, `pid`). `audit_state` holds zero to two state snapshots per
change event — `side = 'after'` for `metadata`, `side = 'previous'` for `previousMetadata` — with
plain scalar columns. `audit_tags` holds each snapshot's tags, one row per tag in `ordinal` order,
because a list cannot be a single scalar column. Indexes cover `timestamp`, `action`, and `actor`.
Every column is a plain scalar and the whole database rebuilds from the JSONL. If content search
is needed later, an FTS5 table can be layered over `audit_state.content` without touching the
canonical JSONL or the API contract.

### 6.4 Rollups

A daily job aggregates the previous day's entries into `rollups/<date>.jsonl` — counts grouped
by `category`, `action`, `actor`, `outcome`, plus min/median/max `durationMs`. Rollups are small
and retained indefinitely; raw access files are pruned after the retention window.

### 6.5 Repo hygiene and backup

The store lives outside any repository, under the user data root, so there is nothing to add to
`.gitignore`. Because git is not the durability mechanism here, off-system backup of
`<elingDataRoot>/audit/` is a real requirement (see §13).

## 7. Concurrency and Throughput

### 7.1 In-process

Each trail has a single writer guarded by a `SemaphoreSlim(1,1)` and fed by a bounded
`Channel<AuditEvent>`. A background flusher drains the channel and writes batched lines. This
keeps hot read paths off the lock and avoids one fsync per entry, which would otherwise
serialize queries.

### 7.2 Cross-process

Multiple processes on the machine share this single store (the owner HTTP host plus MCP-only
peers, across every project). The writer opens the file for append with an exclusive write share
(`FileMode.Append, FileAccess.Write, FileShare.Read`) and retries with bounded backoff on a
sharing violation. The OS releases the handle if a process dies, so there is no stale lock.
This is cooperative among .NET processes; external append tools bypass it, which is acceptable
because only Eling writes these files.

### 7.3 Durability per trail

The **change** trail flushes to disk before the operation returns once the entry is enqueued
(batched, bounded wait). The **access** trail is fire-and-forget; losing the tail on an abrupt
exit is accepted.

## 8. Hook Points

| Domain | Change | Access |
|---|---|---|
| memory | `MemoryService.SaveAsync` / `UpdateAsync` / `DeleteAsync`; `ScopedMemoryService` copy/move/promote; `MemoryInit`/policy | read paths resolved through `IMemoryService`, `IScopedMemoryService`, and recall |
| runtime | `RuntimeRegistry.Register` / `Unregister` / prune branch of `Sweep` / `ScheduleEmptyShutdown` | `Alive()` listing |
| filesystem | write/move/copy/delete directory ops in `FileSystemTools` | read/list/glob/grep/path-test ops in `FileSystemTools` |
| HTTP | mutation endpoints (via middleware + ambient actor) | GET endpoints (via middleware) |

`MemoryService` is the lowest common mutation choke point: both MCP (through
`ScopedMemoryService`) and dashboard endpoints (through `RuntimeRegistry`) funnel through it.
Access hooks are placed on the read services and on an HTTP middleware that audits GETs.

## 9. Read API

Read-only; there is no write endpoint.

- `GET /api/audit/changes?actor=&action=&scope=&projectRoot=&from=&to=&limit=&cursor=`
- `GET /api/audit/access?actor=&action=&scope=&projectRoot=&from=&to=&limit=&cursor=`
- `GET /api/audit/rollups?from=&to=&category=&actor=`

The endpoint reads the single central store; `scope` and `projectRoot` are filters over entry
fields. Requests are answered from `audit-index.db` when present, falling back to a windowed
JSONL scan. A request MUST be bounded by a time window; unbounded scans are rejected.

## 10. Realtime

Phase 4 reuses the existing SSE infrastructure (`MemoryChangeBroadcaster` +
`/api/events/memories` + `useMemoriesSse`). Because mutations and lifecycle changes already
broadcast, and peer processes relay through `HttpCoordinatorMemoryChangeNotifier` to the
owner's `/api/coordinator/notify-change`, the Activity Log can refetch on those events with no
new backend plumbing. A later enhancement may publish structured audit payloads on a dedicated
topic for incremental (no-refetch) streaming. Events are broadcast **after** a successful
append so the live view matches the durable log.

## 11. UI

A new `/dashboard/activity-log` route shows the historical view (table with timestamp, actor,
category, action, scope, target; filters; cursor pagination; virtualized list; detail modal with
before/after diff for change entries) and the live view (refetch on SSE). The activity-log read
API is itself excluded from auditing. Desktop clients consume the same API and SSE; no backend
change is required for desktop.

## 12. Exclusions and Recursion Guards

Never audited: audit's own writes, the audit read API, SSE subscribe/ping, runtime heartbeat,
sweeps that change nothing, `/health`, static assets, and index rebuild bookkeeping. Without
these exclusions the access trail is dominated by system self-noise and the dashboard's
refetch-on-event cascade.

## 13. Retention, Privacy, Security

- Raw change: 365 days (configurable). Raw access: 14 days (configurable). Rollups: indefinite.
- Closed days are compressed; a total-size cap triggers oldest-first pruning after rollups are
  written.
- Query text is recorded as-is by default; a `hash-query` policy option stores only a digest.
  Query text may contain sensitive user content and the audit directory is plaintext.
- The hash chain (`hash`/`prevHash`) is reserved but not active in v1; the fields exist so it
  can be enabled forward without rewriting history.
- The read API is loopback-only, same as the rest of the dashboard API. RBAC is future work.

## 14. Failure Policy

Audit writes are fail-open: if the writer cannot record (disk full, lock timeout), the user
operation still completes and the failure is logged and counted, surfaced as an audit-degraded
signal. A fail-closed mode for the change trail may be added later as configuration.

## 15. Non-Goals

- Vestige (or any agent-side MCP) mirroring. A generic `IAuditSink` seam is retained for a
  future external sink.
- Real-time SIEM streaming.
- Cryptographic chain-of-custody activation (reserved fields only).
- User authentication / RBAC.
- Recording file **contents** in the filesystem access trail.

## 16. Phasing

Each phase is its own plan and review cycle.

1. **Phase 1 — Core engine:** schema, JSONL writer, async batched flusher, daily rotation +
   compression, SQLite index, retention/rollup job, concurrency, tests. No hooks.
2. **Phase 2 — Change trail:** memory + runtime + filesystem-write hooks; `GET /api/audit/changes`.
3. **Phase 3 — Access trail:** memory read + HTTP GET middleware + filesystem-read hooks;
   rollups; `GET /api/audit/access` and `/api/audit/rollups`.
4. **Phase 4 — Activity Log UI:** historical + realtime, diff modal.
5. **Phase 5 — Hardening:** size caps, backup sink, hash-chain activation, query-hash policy.

## 17. Acceptance Criteria

1. Every operation in §3 produces exactly one entry in the correct trail, with no self-noise.
2. Change trail entries survive an abrupt process exit for all completed operations.
3. Concurrent writes from two processes to one data directory produce no interleaved or
   corrupted lines.
4. Daily rotation and compression run without a background timer and are deterministically
   testable with an injected clock.
5. `audit-index.db` can be deleted and rebuilt from the JSONL files alone.
6. A bounded query returns in well under a second on the central store holding hundreds of MB of
   raw audit data.
7. The Activity Log view is live (refetch on SSE) and can filter by actor/category/action/scope/
   time.
8. Phase 1 unit tests cover the writer, rotation, index, and concurrency paths at ≥90%.
