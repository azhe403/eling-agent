# Redis Logical Databases in the Connection Tree Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A Redis connection lists its logical databases in the sidebar, one row per database with its key count, and each database opens as its own tab — the way tables work for SQL. Keys stay in the right-hand panel.

**Architecture:** One new read endpoint (`GET /api/databases/{id}/keys/databases`) returns the logical databases the server reports in `INFO keyspace`. The dashboard draws one tree row per database, and a Redis tab is identified by `connId + redisDb` instead of by a table name, so two databases of one connection can be open side by side. The key tree in the panel is per-database: its pages and expansion state are cached under `db:prefix`, so switching tabs costs no request.

**Tech Stack:** .NET 10 (ASP.NET Core minimal API), `Eling.Core`, StackExchange.Redis, xUnit. Dashboard: Next.js 16, React 19, TypeScript, Tailwind, shadcn-style components from `@/components/ui`, vitest.

**Spec:** `docs/superpowers/specs/2026-10-27-redis-db-tree-design.md`

> **Status — where this plan was overtaken.** The plan is kept as written, so
> what was planned can be compared with what shipped. Two of its tasks did not
> survive contact with the code:
>
> - **Task 1 built `RedisKeyspaceParser`; that class was later deleted.**
>   `INFO keyspace` needs admin mode, and every multiplexer runs with
>   `AllowAdmin = false`, so `ListDatabasesAsync` counts each database with
>   `DBSIZE` instead. The parser had no production reader once that changed and
>   was removed with its tests. Task 1's place in the code is taken by
>   `RedisDatabaseIndex` (see below).
> - **The cache key is `connId:db:prefix`, not `db:prefix`.** `db:prefix`
>   collides across connections — two Redis connections both have a `db0` — so
>   the second connection reused the first one's page.
>
> A third type the plan did not call for, `RedisDatabaseIndex`, now carries the
> 0–15 rule so the endpoints and MCP tools refuse an out-of-range index with a
> 400 instead of passing it to the server.

## Global Constraints

- Read-only. The new endpoint reads `INFO keyspace` and writes nothing. No `SET`, no `DEL`, no `FLUSH`.
- `INFO keyspace` only — never `KEYS`. The scan endpoint keeps its budgets (2000 keys scanned, 3000 ms, page size ≤ 500).
- Cluster and Sentinel stay rejected, with the same message. `AllowAdmin = false` on every multiplexer.
- A Redis connection stays **not** server-level: it must not appear as a set of logical databases in the SQL tree, and the SQL endpoints keep answering 400 for it.
- The tree lists the databases that hold keys, plus the active one when it is empty. Never all sixteen.
- The key count is spelled out with its unit: `3 keys`, not a bare `3`.
- Compact labels: `db3`, not `db 3`.
- One top-level type per file; records chopped one parameter per line; no tuples (`CodeOrganizationConventionTests`).
- JSON is camelCase (`index`, `keys`, `expires`).
- Conventional commits.

## Review Focus

- **A database row with no keys.** `INFO keyspace` omits empty databases, so the active database's row exists only because the panel adds it. It must read `0 keys`, and the database index that reaches the backend must always be in 0–15.
- **Two tabs of one connection, different databases.** The tab key, the "is this tab active" comparison, the close-tab comparison, the "keep the other tabs" comparison and the de-duplication must all include the database index. Missing one makes two tabs look like one, and closing one closes the other.
- **A database index that is not a usable number.** `redisDb=abc`, `redisDb=-1` and `redisDb=99` must fall back to the first database that holds keys — not open a tab for db 99, and not crash.
- **Two browser tabs on two different Redis connections.** The explorer state is one localStorage key for the whole application. A saved database index must only be used for the connection it was saved with.
- **A Redis server that is down.** The database list fails; the tree must show the failure and keep the rows it already had, not blank them.

---

### Task 1: Parse `INFO keyspace` in a pure, testable class

**Files:**
- Create: `src/backend/Eling.Core/Database/RedisKeyspaceParser.cs`
- Modify: `src/backend/Eling.Core/Database/RedisKeyReadService.cs` (`ListDatabasesAsync`, lines 18–51)
- Test: `tests/Eling.Core.Tests/RedisKeyspaceParserTests.cs`

**Interfaces:**
- Consumes: nothing.
- Produces:
  - `public static class RedisKeyspaceParser` with
    `public static IReadOnlyList<RedisDatabaseInfo> Parse(string? infoRaw)` —
    one entry per `db<N>:` line, in ascending index order. A line that is not a
    `db<N>` line, or whose index does not parse, is skipped. A missing `keys=`
    counts as 0. `expires=` is read when present, otherwise 0. Empty or null
    input returns an empty list.
  - `RedisDatabaseInfo(Index, Keys, Expires)` — unchanged, already in
    `src/backend/Eling.Core/Database/RedisDatabaseInfo.cs`.

- [ ] **Step 1: Write the failing test** in `tests/Eling.Core.Tests/RedisKeyspaceParserTests.cs`:

  - `Parse_Null_ReturnsNothing` → `Assert.Empty(RedisKeyspaceParser.Parse(null))`
  - `Parse_EmptyString_ReturnsNothing` → empty input → empty result
  - `Parse_OneDatabase_ReturnsItsIndexAndCount` — input `"# Keyspace\r\ndb3:keys=3,expires=0,avg_ttl=0\r\n"` → one entry, `Index == 3`, `Keys == 3`, `Expires == 0`
  - `Parse_SeveralDatabases_ReturnsThemInIndexOrder` — input with `db1`, `db3`, `db5` in that order → indexes `1, 3, 5`
  - `Parse_LineWithoutKeys_CountsZero` — `"db2:expires=4,avg_ttl=0"` → `Keys == 0`, `Expires == 4`
  - `Parse_LineWithAnUnreadableIndex_IsSkipped` — `"dbX:keys=1"` and `"db:keys=1"` are skipped, `"db7:keys=1"` survives
  - `Parse_OtherSections_AreIgnored` — `"used_memory:1234"` and `"db0:keys=1"` together → only `db0`

  Run: `dotnet test tests/Eling.Core.Tests --filter FullyQualifiedName~RedisKeyspaceParserTests` → fails to compile (class absent). That is the red step.

- [ ] **Step 2: Implement `RedisKeyspaceParser`** in `src/backend/Eling.Core/Database/RedisKeyspaceParser.cs`: a static class with the one public method above. Split the raw text on newlines, trim each line, keep a line that starts with `db` and has a colon, parse the index after `db` with `int.TryParse` (reject on failure), then read the comma-separated `key=value` pairs for `keys` and `expires` with `long.TryParse`. Order the result by index.

- [ ] **Step 3: Use the parser in the service.** In `RedisKeyReadService.ListDatabasesAsync`, replace the inline parsing loop with `RedisKeyspaceParser.Parse(info)`. **Keep** the existing behaviour that an empty result becomes a single `db0` entry with zero counts: Redis reports no database for an empty one, and without this fallback a server that holds nothing anywhere would show no row at all.

  Note for the reader: `ScanAsync` has a second caller of this list — a negative `database` argument turns the scan into a list of database nodes. The parser change must leave that path working unchanged.

- [ ] **Step 4: Run the tests green.** `dotnet test tests/Eling.Core.Tests --filter FullyQualifiedName~RedisKeyspaceParserTests` → all pass. Then `dotnet test tests/Eling.Core.Tests` → no regression.

- [ ] **Step 5: Commit.** `feat(core): parse redis keyspace info in a testable class`

---

### Task 2: Serve the database list

**Files:**
- Create: `src/backend/Eling.Backend/Dtos/RedisDatabaseInfoDto.cs`
- Modify: `src/backend/Eling.Core/Database/DatabaseAuditActions.cs` (add one constant)
- Modify: `src/backend/Eling.Backend/Endpoints/DatabaseKeyEndpoints.cs` (add one endpoint)
- Test: `tests/Eling.Backend.Tests/RedisDatabaseListEndpointTests.cs`

**Interfaces:**
- Consumes: `IRedisKeyReadService.ListDatabasesAsync(DatabaseConnection, CancellationToken)` (already exists), `IDatabaseConnectionStore.FindAsync`, `IAuditLogger.RecordAsync`.
- Produces:
  - `public sealed record RedisDatabaseInfoDto(int Index, long Keys, long Expires);` in
    `src/backend/Eling.Backend/Dtos/RedisDatabaseInfoDto.cs` — camelCase JSON
    `{"index":3,"keys":3,"expires":0}`.
  - `public const string RedisDatabases = "redis_databases";` in `DatabaseAuditActions`.
  - `GET /api/databases/{id}/keys/databases` → `200` with `RedisDatabaseInfoDto[]`;
    `404` unknown id; `400` when the connection is not Redis (body
    `"connection '{id}' is a {Provider} connection; key browsing requires Redis"`);
    `503` with title `redis unavailable` when the server cannot be reached.

- [ ] **Step 1: Write the failing test** in `tests/Eling.Backend.Tests/RedisDatabaseListEndpointTests.cs`, using the same `WebApplication.CreateSlimBuilder` + `UseTestServer` + `JsonDatabaseConnectionStore` harness as `DatabaseConnectionErrorTests`, and calling `app.MapDatabaseKeyEndpoints()`:

  - `ListDatabases_ForAnUnknownId_AnswersNotFound`
  - `ListDatabases_ForANonRedisConnection_AnswersBadRequest` — register a SQLite connection, expect 400 and a body containing `key browsing requires Redis`
  - `ListDatabases_ForARedisConnection_AnswersTheDatabaseList` — register a Redis connection, expect 200 and a JSON array with the `index`, `keys` and `expires` fields

  The test needs a stub `IRedisKeyReadService` in the test host; keep it a private nested class in the test file so the one-type-per-file rule holds.

  Run: `dotnet test tests/Eling.Backend.Tests --filter FullyQualifiedName~RedisDatabaseListEndpointTests` → 404s / compile errors. That is the red step.

- [ ] **Step 2: Add the DTO and the audit constant.** `RedisDatabaseInfoDto` with the three chopped parameters; `RedisDatabases = "redis_databases"` next to `RedisCommand` in `DatabaseAuditActions`.

- [ ] **Step 3: Add the endpoint** in `DatabaseKeyEndpoints.cs`, modelled on the existing `/{id}/keys` endpoint: find the row, `404` when absent, `400` when `row.Provider != DatabaseProvider.Redis`, then call `readService.ListDatabasesAsync(row, ct)`, map each entry to `RedisDatabaseInfoDto`, record an audit event with `Action = DatabaseAuditActions.RedisDatabases`, `Outcome = AuditOutcome.Success`, `ResultCount = <number of databases>`, and answer `Results.Ok(list)`. Catch `Exception` and answer `Results.Problem(statusCode: 503, title: "redis unavailable", detail: ex.Message)`.

- [ ] **Step 4: Run the tests green.** `dotnet test tests/Eling.Backend.Tests --filter FullyQualifiedName~RedisDatabaseListEndpointTests`

- [ ] **Step 5: Run the whole backend suite.** `dotnet test tests/Eling.Backend.Tests` → green, including `CodeOrganizationConventionTests`.

- [ ] **Step 6: Commit.** `feat(backend): serve the redis logical database list`

---

### Task 3: The names and the decisions the dashboard makes

**Files:**
- Create: `src/frontend/Eling.Dashboard/src/app/dashboard/databases/db-tabs.ts`
- Create: `src/frontend/Eling.Dashboard/src/app/dashboard/databases/redis-browser.ts`
- Test: `src/frontend/Eling.Dashboard/src/app/dashboard/databases/db-tabs.test.ts`
- Test: `src/frontend/Eling.Dashboard/src/app/dashboard/databases/redis-browser.test.ts`

**Interfaces:**
- Consumes: nothing.
- Produces — `db-tabs.ts`:
  - `export type DbTab = { connId: string; table: string; database?: string | null; kind?: "redis"; redisDb?: number }`
  - `export const REDIS_TAB_TABLE = "Redis keys"`
  - `export const isRedisTab = (tab?: { table?: string; kind?: string } | null): boolean` — true when `kind === "redis"` **or** `table === REDIS_TAB_TABLE` (a restored tab can carry only the table name).
  - `export const redisTabKey = (tab: DbTab): string` — `` `${tab.connId}:${tab.redisDb ?? 0}` `` — a Redis tab is a connection **and** a database.
- Produces — `redis-browser.ts`:
  - `export type RedisDbInfo = { index: number; keys: number; expires: number }`
  - `export const dbRowLabel = (db: RedisDbInfo): string` — `` `db${db.index}` ``
  - `export const dbRowCount = (db: RedisDbInfo): string` — `` `${db.keys} keys` ``
  - `export const parseRedisDb = (raw: string | null | undefined): number | null` — the URL's `redisDb` as a number, or `null` when it is absent, not an integer, or outside 0–15.
  - `export const pickRedisDb = (opts: { urlDb: string | null; savedTab?: { connId: string; redisDb?: number } | null; connId: string; available: RedisDbInfo[] }): number` — the URL's index first, then the saved tab's index **when that tab belongs to the same connection**, then the first available database's index, then `0`.
  - `export const fetchRedisDatabases = async (connId: string): Promise<RedisDbInfo[]>` — `GET /api/databases/${connId}/keys/databases`, parsed; rejects on a non-OK response.

- [ ] **Step 1: Write the failing tests**, in the two `*.test.ts` files, with vitest (`describe`/`it`/`expect`, `vi` for the fetch stub, as in `DatabaseCombobox.test.tsx`):

  `db-tabs.test.ts`:
  - `isRedisTab` is true for `{ kind: "redis" }`, true for `{ table: "Redis keys" }`, false for `{ table: "orders" }`, false for `null`
  - `redisTabKey` differs for two databases of one connection, and differs across connections
  - `redisTabKey` for `{ connId: "c1" }` without `redisDb` is `c1:0`

  `redis-browser.test.ts`:
  - `dbRowLabel({ index: 3, keys: 3, expires: 0 })` is `db3`
  - `dbRowCount({ index: 3, keys: 3, expires: 0 })` is `3 keys`; `dbRowCount` of a database with no keys is `0 keys`
  - `parseRedisDb("3")` is `3`; `parseRedisDb("abc")`, `parseRedisDb("-1")`, `parseRedisDb("99")` and `parseRedisDb(null)` are all `null`
  - `pickRedisDb` with a URL index returns the URL index
  - `pickRedisDb` with no URL index and a saved tab for the same connection returns the saved index
  - `pickRedisDb` with a saved tab for **another** connection falls through to the first available database
  - `pickRedisDb` with nothing available returns `0`
  - `fetchRedisDatabases` rejects when the server answers a non-OK status, and resolves with the parsed list when it answers 200 (stub `globalThis.fetch` with `vi.stubGlobal`)

  Run: `pnpm --prefix src/frontend/Eling.Dashboard test` → the files fail to resolve the modules. That is the red step.

- [ ] **Step 2: Implement `db-tabs.ts`** with exactly the four exports above and no others.

- [ ] **Step 3: Implement `redis-browser.ts`** with exactly the five exports above and no others. `pickRedisDb` reads the URL index with `parseRedisDb`, then the saved index when `savedTab?.connId === connId` and it is a number in 0–15, then `available[0]?.index ?? 0`.

- [ ] **Step 4: Run the tests green.** `pnpm --prefix src/frontend/Eling.Dashboard test` → all pass.

- [ ] **Step 5: Commit.** `feat(dashboard): name the redis tab and database row`

---

### Task 4: The database becomes a tab

One change, because its parts depend on each other: the tree rows, the tab identity, the per-database key tree and the panel header all describe the same thing — a database. Splitting it would leave the tree with rows that cannot be opened, and two tabs sharing one key tree.

**Files:**
- Modify: `src/frontend/Eling.Dashboard/src/app/dashboard/databases/Databases.tsx` — the sidebar block (lines ~1742–1806), the connection-row click, the tab bar (around line 1815), `closeTab` (around line 1193), the "keep the other tabs" comparison (around line 1216), `activateTabEntry` (around line 1174), the Redis panel (lines ~1913–2079), `fetchRedisPrefix` (around line 479), `toggleRedisPrefix` (around line 499), `selectRedisKey` (around line 513)

**Interfaces:**
- Consumes: `GET /api/databases/{id}/keys/databases` (Task 2), `db-tabs.ts` and `redis-browser.ts` (Task 3).
- Produces:
  - The local `DbTab` type and `REDIS_TAB_TABLE` in `Databases.tsx` are **deleted**; both come from `db-tabs.ts`. The local `isRedisTab` helper is replaced by the imported one — one definition only. `DbTab` gains the optional `redisDb?: number`.
  - `const [redisDbs, setRedisDbs] = useState<Record<string, RedisDbInfo[]>>({})` — the database list per connection id.
  - `const fetchRedisDbs = async (connId: string) => void` — calls `fetchRedisDatabases(connId)`, stores the list under that id, and on failure raises `notifyDb("error", "Redis databases failed", message)` **without** clearing the list it already had.
  - The sidebar renders, under an expanded Redis connection, one row per database: `🗄`, the label `db3`, and `3 keys` right-aligned and muted. No chevron.
  - A Redis tab is `{ connId, table: REDIS_TAB_TABLE, database: null, kind: "redis", redisDb: db.index }`, labelled `dev-ibl-redis · db3`.
  - The `redisDb` state variable is **removed**. The database of the panel is read from the active tab: `const activeRedisDb = isRedisTab(activeTable) ? (activeTable.redisDb ?? 0) : 0`.
  - The panel's page cache and expansion set are keyed `` `${db}:${prefix || "(root)"}` `` instead of by prefix alone, so two open databases keep separate trees.
  - The panel's header reads `Redis · {connection name} · db{activeRedisDb}`; the chip row `db0 … db15` is deleted.

- [ ] **Step 1: Move the tab type and the helpers.** Delete the local `DbTab` type, `REDIS_TAB_TABLE` and `isRedisTab` from `Databases.tsx`; import `DbTab`, `REDIS_TAB_TABLE`, `isRedisTab`, `redisTabKey` from `db-tabs.ts` and `RedisDbInfo`, `dbRowLabel`, `dbRowCount`, `fetchRedisDatabases` from `redis-browser.ts`.

- [ ] **Step 2: Give every tab comparison the database index.** A Redis tab is a connection **and** a database, so every place that compares two tabs must include the index. Replace the comparisons that today use `connId` + `table` + `database` with one that also covers `redisDb` — the tab bar's `key` and `isActive` (the `openTabs.map` around line 1815), `closeTab`'s `wasActive` (around line 1193), the "keep the other tabs" comparison (around line 1216), and the de-duplication inside `setOpenTabs` so opening db3 twice does not open two tabs. Use `redisTabKey(tab)` for a Redis tab and the existing `connId`/`table`/`database` triple for a SQL tab.

- [ ] **Step 3: Add the state and the fetch.** Next to the other Redis state in `Databases.tsx`:

  ```ts
  const [redisDbs, setRedisDbs] = useState<Record<string, RedisDbInfo[]>>({})
  ```

  and a `fetchRedisDbs(connId)` that awaits `fetchRedisDatabases(connId)` and stores the result with `setRedisDbs(prev => ({ ...prev, [connId]: list }))`, and on a rejected promise calls `notifyDb("error", "Redis databases failed", message)` and returns without touching the stored list.

- [ ] **Step 4: Load the list when the connection is expanded.** In `toggleRedisConn` (the Redis branch of the connection-row click), call `void fetchRedisDbs(id)` when the connection is being expanded. Fetching on expand, not on mount, keeps the dashboard from asking every Redis server for its keyspace on every page load.

- [ ] **Step 5: Render the rows.** Inside the `expandedConns.has(c.id)` block, the Redis branch currently prints the sentence "Redis key browsing. Use the Redis panel to inspect keys." Replace that sentence with the database rows, each a `button` shaped like the table rows below it:

  - the click target is the whole row: `flex w-full items-center gap-1 rounded px-1 py-0.5 text-left text-xs hover:bg-accent`
  - content: `🗄`, the label `dbRowLabel(db)` (truncate), and `dbRowCount(db)` right-aligned with `ml-auto shrink-0 tabular-nums text-muted-foreground`
  - the row is highlighted when it is the open Redis tab
  - a database that holds no keys is not drawn from the list; the active database is always drawn, with `0 keys`, so a link to an empty database still shows where it is
  - when the connection reports no databases at all, one muted line says so

- [ ] **Step 6: Open a database from the tree.** The database row's click opens (or activates) a Redis tab for that database: build `{ connId, table: REDIS_TAB_TABLE, database: null, kind: "redis", redisDb: db.index }`, add it to `openTabs` when no tab with the same `redisTabKey` exists, make it the active tab (`setActiveTable` and `activeTableRef.current`), select the connection, and load that database's root tree with `fetchRedisPrefix(connId, db.index, "")`.

  The connection row itself only expands, like a SQL connection; it no longer opens a tab.

- [ ] **Step 7: Label the tab.** The tab row's name for a Redis tab becomes `{connection name} · db{index}`.

- [ ] **Step 8: Point the panel at the active tab.** Delete the `redisDb` state and the `db0 … db15` chip row, and replace every use of `redisDb` with `activeRedisDb`:

  - `fetchRedisPrefix` and `selectRedisKey` take the database from the active tab
  - the panel's page cache and expansion set are keyed `db:prefix` — the root page's key is `` `${db}:(root)` ``, a child's is `` `${db}:${prefix}` ``
  - the "Collapse all" button and the "Browse root" button clear only the **active** database's entries, so the other open tab keeps its tree
  - the header reads `Redis · {connection name} · db{activeRedisDb}`

- [ ] **Step 9: Reset the inspector when the database changes.** The inspected key belongs to one database. When the active tab's database changes, clear `redisInspect` and `redisInspectKey`, so a stale value is not shown next to another database's key.

- [ ] **Step 10: Type-check and lint.** `pnpm --prefix src/frontend/Eling.Dashboard exec tsc --noEmit` → clean; `pnpm --prefix src/frontend/Eling.Dashboard exec eslint src/app/dashboard/databases` → no new errors.

- [ ] **Step 11: Commit.** `feat(dashboard): list redis databases in the connection tree`

---

### Task 5: Restore and the URL

**Files:**
- Modify: `src/frontend/Eling.Dashboard/src/app/dashboard/databases/Databases.tsx` (the restore effect around lines 614–711, the persist effect around lines 754–809)

**Interfaces:**
- Consumes: `parseRedisDb` and `pickRedisDb` (Task 3).
- Produces:
  - The URL keeps `redisDb` as a query parameter, and it now names the database of the **active tab**: `?connectionId=…&table=Redis+keys&tab=data&redisDb=3`.
  - The saved explorer state keeps the whole `activeTable` (which carries `redisDb`) and the `openTabs` array; the free-standing `redisDb` field is removed from the saved payload — the index belongs to the tab, and the tab is saved.
  - The restored database is the one `pickRedisDb` chooses.

- [ ] **Step 1: Write the URL.** In the persist effect, write the active tab's database: when the active tab is a Redis tab, `params.set("redisDb", String(activeTable.redisDb ?? 0))`. Remove `redisDb` from the saved localStorage payload.

- [ ] **Step 2: Restore the database.** In the restore effect, choose the database with `pickRedisDb({ urlDb: params.get("redisDb"), savedTab: st?.activeTable, connId, available })`, where `available` is the list the new endpoint reports. The saved index is used only when the saved tab belongs to the same connection — the explorer state is one localStorage key for the whole application, so an index saved for another connection must not leak in.

- [ ] **Step 3: Restore the tab, not just the number.** The restored tab carries `redisDb`, and the panel loads that database's root tree after the restore, so a reload shows the same keys the URL named.

- [ ] **Step 4: Type-check and lint.** `pnpm --prefix src/frontend/Eling.Dashboard exec tsc --noEmit` → clean; eslint → no new errors.

- [ ] **Step 5: Commit.** `feat(dashboard): restore the open redis database`

---

### Task 6: Verify the whole thing

**Files:**
- none

- [ ] **Step 1: Build and test the backend.** `dotnet build`, then `dotnet test tests/Eling.Backend.Tests` and `dotnet test tests/Eling.Core.Tests` → green, including `CodeOrganizationConventionTests` and `CliWrapConventionTests`. Use `$env:ELING_OUTPUT_ROOT=".bin-verify"` so the build does not fight the running dev server over `.bin`.

- [ ] **Step 2: Check the frontend.** `pnpm --prefix src/frontend/Eling.Dashboard exec tsc --noEmit` → clean; `pnpm --prefix src/frontend/Eling.Dashboard test` → green; `pnpm --prefix src/frontend/Eling.Dashboard exec eslint src/app/dashboard/databases` → no errors.

- [ ] **Step 3: Check it in the browser**, against the dev backend on 4417 and the dashboard on 4427, with the Redis connection `dev-ibl-redis`:

  - expanding `dev-ibl-redis` lists `db3` with its key count and `db5`
  - clicking `db3` opens a tab `dev-ibl-redis · db3` and the panel shows db3's keys
  - clicking `db5` opens a **second** tab; both stay open, and each keeps its own tree
  - the URL carries `redisDb=3` while the db3 tab is active
  - reloading that URL restores the same tab and the same keys
  - a `redisDb=99` URL falls back to the first database that holds keys
  - the key count reads `3 keys`, not `3`

- [ ] **Step 4: Commit.** none — verification only.
