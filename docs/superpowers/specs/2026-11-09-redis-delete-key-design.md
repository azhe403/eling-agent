# Redis single-key and subtree delete from the Databases page (dashboard only)

Short design. 2026-11-09. Builds on `2026-10-26-db-connection-redis-design.md` and `2026-10-27-redis-db-tree-design.md`.

Status: Proposed (awaiting user review)

## Intent

A Redis key today can only be read: browse the tree, click a key to inspect it, and stop. The person using the dashboard wants to remove keys from the same screen, confirmed by a Yes/Cancel dialog, without touching the read-only agent surface.

## Goals

- Each key row shows a small delete button on its right edge.
- Each prefix (folder) row in the tree shows a small delete button on its right edge.
- Clicking a delete asks Yes/Cancel naming the target. Yes deletes; Cancel closes. Clicking delete never selects the row first.
- The tree-level dialog lists the affected keys (capped) before the user confirms, so they see what will go away.
- After a successful delete the tree and the inspector refresh and the database key count updates.

## Non-goals

- No MCP tool (`redis_delete`), no agent path. The user has no spec or plan for Redis/DB mutation tools yet.
- No approval flow (`allowAgentMutations` / `requireApprovalForMutations`). This is a human, dashboard-only write.
- No whole-database delete. The root (empty prefix) has no delete button — deleting it would be a `FLUSHDB` equivalent.
- No `DEL` whitelisting in `POST /keys/command`; the command console stays read-only.

## Anti-goals

- Turning Redis into a general write surface in one step. One key or one subtree at a time.
- A single toolbar-wide delete button above the list. The buttons live per row.
- Putting `key` or `prefix` in a path segment; keys may contain `/`, `:` or `%` and would split the route.
- Deleting with a stale or unknown count. A whole subtree is deleted only after the dialog shows which keys it covers.

## Constraints

- `key`, `prefix`, and `database` travel in the query string, never as path segments (same rule as `keys` and `keys/inspect`).
- Database index 0–15 via `RedisKeyRoute.RefuseIndex`; standalone only; `AllowAdmin = false` unchanged.
- Cluster and Sentinel stay rejected with the existing message.
- Tree stays lazy; inspect TTL must not use `PEXPIRETIME` (dev server rejects it).
- Subtree delete and its preview respect the scan budgets of the read path (2000 keys scanned, 3000 ms): a prefix bigger than the budget answers truncated, never a silent partial delete.
- The affected-key list is capped in the dialog so a busy subtree does not blow up the confirm.

## Approaches

**A. Dedicated dashboard-only endpoints** (chosen). One endpoint per shape: single-key delete and prefix (subtree) delete, each via the existing `RedisKeyRoute` gate, audited distinctly, evicting the local read cache. The subtree shape gets a preview read so the dialog can list what will be deleted.

**B. Allow `DEL` in the command whitelist and call it from the dashboard** (rejected). Smaller backend delta, but generic: no distinct validation, generic error text, generic audit, and `DEL` becomes available to anyone who can call the console. Subtree delete would need repeated `DEL` calls from the browser and still needs a preview scan for the list.

**C. Full mutation parity — write service + approval + MCP tool** (deferred). Gives agents a `redis_delete` with the SQL approval UX, but needs its own spec, docs, and approval design. The user explicitly has neither yet.

## Decisions

**Placement** — a small trash icon button on the right edge of `RedisKeyRow` (next to `type · ttl`) and on the right edge of `RedisNodeRow`. Both rows today are one `Button`; they become container `div`s with the select/expand action left and the delete action right, and the delete click stops propagation so it never opens the inspector or expands the node.

**Single-key backend** — `DELETE /api/databases/{id}/keys/delete?database=&key=` (exact wording left to builder) reusing `RedisKeyRoute.OpenAsync`, `RefuseIndex`, and `RunAsync` so 404/400/502/503 mapping stays identical. `KeyDeleteAsync` through the existing `IRedisConnectionPool` on the chosen logical database, audit action `redis_delete` with the key as `Query`, and `IRedisReadCache.RemoveKey` so an offline read cannot resurrect it. Missing key answers 404 `key not found`. Response is a small `deleted: true` payload.

**Subtree backend** — `DELETE /api/databases/{id}/keys/prefix?database=&prefix=` (wording left to builder). The service enumerates `prefix*` with `KeysAsync` under the same budgets as `ScanAsync`, deletes in batches via `KeyDeleteAsync`, returns `deleted` count and `truncated: true` when the budget stops it early. Audit action `redis_prefix_delete` with the prefix as `Query` and the count as result. Empty prefix is refused (400) — the root never deletes.

**Preview for the list** — before the subtree dialog confirms, the dashboard reads the affected keys through a preview scan using the same `KeysAsync` pattern and budgets: one flat list of keys under the prefix, capped (100 keys is the natural ceiling, mirroring the inspect preview), plus the total count and a `truncated` flag. The builder reuses the existing `GET keys` machinery where it fits and adds a small dedicated preview route only if the tree-shaped `GET keys` cannot return the flat list. No new MCP surface; this is a read, same as browse.

**Naming tension** — the methods land on `IRedisKeyReadService` today, which would lie about its name. The builder either renames the surface or adds a sibling `IRedisKeyWriteService` with DashboardServices DI; the one-type-per-file rule still holds.

**Frontend** — `components/dialogs/RedisDeleteConfirmDialog.tsx` for one key (mono key name) and `components/dialogs/RedisPrefixDeleteConfirmDialog.tsx` for a subtree (mono prefix, total count, a capped scrollable list of the affected keys, and a `truncated` warning when the preview was capped). Both mirror `DeleteRowConfirmDialog` (`AlertDialog`, `AlertDialogCancel` "Cancel", `AlertDialogAction` destructive with an in-flight label). The list is mono, truncated with ellipsis, in a `max-h-60` scroll area like `ImportConfirmDialog` so the dialog keeps a fixed height. `useRedisBrowser` gains `deleteRedisKey` and `deleteRedisPrefix` that call the endpoints, then on success clear the inspector when it names a deleted key, drop affected entries from `redisPages`, and reload the root page and the database list. On failure they raise the existing `notifyDb("error", ...)` toast — no optimistic removal. `RedisPrefixTree` threads `onDeleteKey` and `onDeletePrefix` down to both row types.

**Counts** — the subtree dialog's count and list come from the preview scan, not from what the tree already drew; the toast after delete reports what was actually deleted, so a truncated run never reads as a full one.

**Audit actions** — `DatabaseAuditActions.RedisDelete = "redis_delete"` and `RedisPrefixDelete = "redis_prefix_delete"` next to `RedisDatabases`.

## Settled during planning

- Both buttons delete their own row's target only; there is no free-text key box and no "delete the filter" behavior.
- A delete click never selects or expands the row.
- Dialogs name their target in mono; the subtree dialog lists the affected keys (capped) rather than a bare count; Cancel wins on overlay close, matching the SQL delete dialog.
- No change to `RedisCommandSafety`; `DEL` via the console keeps answering 400.

## Verification

- Core: mocked `IConnectionMultiplexer`/`IDatabase` proves single `KeyDeleteAsync` fires on the right database, the cache entry drops, and the subtree path deletes every key under the prefix within budget, reporting `truncated` truthfully. Preview returns the capped flat list with total and flag.
- Backend: test-server harness like `RedisDatabaseListEndpointTests` — unknown id 404, non-Redis 400 `key browsing requires Redis`, out-of-range database 400, empty prefix 400, single-key happy-path 200 with `deleted: true`, subtree happy-path 200 with count, each with an audit row.
- Dashboard: `tsc --noEmit`, eslint on `databases/`, vitest for both rows' delete callbacks, stopPropagation, the capped list rendering, and the hooks' clear+reload on success.
- Browser against `dev-ibl-redis` on 4417/4427: right-side delete on a key row and on a `📁` row each open the dialog without selecting/expanding, Cancel leaves everything, Yes removes just that key or just that subtree, the affected-key list matches what disappeared, the tree reloads without them, and the sidebar count drops to match.
