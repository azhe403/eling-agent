# Redis logical databases in the connection tree

Short design. 2026-10-27. Builds on `2026-10-26-db-connection-redis-design.md`.

## Intent

A Redis connection today shows a dead hint in the tree ("Redis key browsing. Use
the Redis panel to inspect keys.") and its logical databases only as a chip row
`db0 … db15` above the key panel. The person using it wants the databases where
every other connection keeps them: as rows under the connection, and each one a
thing they can open. Keys stay on the right; the panel is where they are read.

## Goals

- Expanding a Redis connection lists its logical databases as tree rows.
- A database row opens its own tab, the way a table row does for SQL, so db3 and
  db5 can be open side by side.
- Selecting a database in the tree is the only way to pick one: the chip row is
  removed and the panel header says which database it is showing.
- Size fits content: rows are one line, count right-aligned, no chevrons.

## Non-goals

- Keys or prefixes in the sidebar. The tree stops at the database level.
- Writes. The store stays read-only; no `SET`, no `DEL`.
- An MCP tool for the database list. `redis_scan` already takes `database`.
- Cluster and Sentinel. Still rejected, unchanged.

## Anti-goals

- Listing all 16 logical databases. 16 rows of `db0 … db15`, thirteen of them
  empty, is the chip row again with a chevron.
- A database row that expands into prefixes. That is keys in the sidebar.
- Losing the prefix filter or the inspector from the panel.

## Constraints

- `DBSIZE` per logical database (0–15), never `KEYS` or `INFO keyspace` (which requires admin mode, and every multiplexer runs with `AllowAdmin = false`). per-request budgets unchanged.
- The connection stays non-server-level; this is a browse affordance, not a
  provider change.
- shadcn components where one exists; compact labels (`db3`, not `db 3`).

## Approaches

**A. Database as a selector inside one Redis tab.** Fewest changes: new endpoint,
tree rows, chips removed. Rejected by the person using it: clicking another
database overwrites the open tab, so two databases cannot be compared.

**B. Database as a tab** (chosen). Mirrors SQL, where each table is its own tab.
More code — tab identity, dedupe, restore, URL — but the behaviour is the one
already learned in this screen.

## Decisions

**Backend.** `GET /api/databases/{id}/keys/databases` returns
`[{ "index": 3, "keys": 3, "expires": 0 }]` from the existing
`IRedisKeyReadService.ListDatabasesAsync`, which now counts each logical database
with `DBSIZE` (0–15) instead of `INFO keyspace`, because `INFO` requires admin
mode and every multiplexer runs with `AllowAdmin = false`. 404 unknown id, 400
for a non-Redis connection ("key browsing requires Redis"), 503 when the server
is unreachable. Audited as `redis_databases` (new `DatabaseAuditActions` constant)
with the row count. The `INFO keyspace` parser written first was removed once the
service moved to `DBSIZE`: nothing in production read it, and a class kept alive
only by its own tests is a liability rather than coverage.

**Tree.** Expanding a Redis connection fetches the list once and renders a row per
database that holds keys, plus the active database when it is empty, so a link to
`redisDb=7` still shows where it is. Row: `db3` and, right-aligned `3 keys`
(`🗄` icon, no chevron). Clicking opens or activates that database's tab. The
connection row itself only expands, like SQL; it no longer opens a tab. When the
server has no keys at all, the tree says so in one muted line. The count is
spelled out with its unit — `3 keys`, not a bare `3`, which is ambiguous next to
a database name.

**Tabs.** `DbTab` gains `redisDb?: number`; a Redis tab is identified by
`connId + redisDb`, deduped, labelled `dev-ibl-redis · db3`. `closeTab`'s
active-tab comparison includes the database. `isRedisTab` keeps treating the
`Redis keys` table name as Redis, for restored state.

**Panel.** Header `Redis · dev-ibl-redis · db3`; prefix input and Browse root
stay; the chip row is deleted. The database shown is the active tab's.

**Caches.** `redisPages` and `redisExpanded` are keyed `db:prefix` so each open
database keeps its loaded pages; switching tabs does not refetch, and a tab that
was never loaded fetches its root on activation. `redisInspect` is reset when the
active database changes.

**Restore and URL.** `redisDb` stays the URL parameter and now names the tab. The
saved `activeTable` carries `redisDb`; restore prefers the URL, then the saved
tab, then the first database with keys. No more `redisDb` in the localStorage
payload as a free-floating value — it lives on the tab it belongs to.

## Settled during planning

- The row count reads `3 keys`, spelled out with its unit; a bare `3` is
  ambiguous next to a database name.
- The database row uses `🗄`, next to `📁` for a prefix and `🔑` for a key.
- A database that vanished between two fetches: the tree draws the row it
  knows about; the scan answers 400 for an index outside 0–15, and the
  dashboard falls back to the databases the list does report.
- The tree shows the databases that hold keys, plus the active one even when
  it is empty, so a link to an empty database still shows where it is.

## Verification

- `RedisDatabaseIndex` unit tests: 0 and 15 accepted, -1 / 16 / "abc" refused.
- Endpoint against `dev-ibl-redis` (db3) with curl, plus a non-Redis connection
  for the 400 and an out-of-range index for the 400.
- Browser: db3 and db5 open together, reload restores both tabs and the active
  one's keys, URL carries `redisDb`, chips gone, panel still fits.
- `dotnet build`, full backend suite, `tsc --noEmit`, eslint.
