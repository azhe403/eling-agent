# Databases Data tab: filter & sort condition builder

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add a DbGate-style filter/sort condition builder panel to the Data tab of `/dashboard/databases`, with the SQL assembled and executed server-side at `POST /api/databases/{id}/browse`.

**Architecture:** The backend gains a pure `BrowseQueryBuilder` (identifier quoting, parameter binding, left-associative AND/OR fold, `!` LIKE-escape) plus a new endpoint that validates table/columns against the schema (skipped with a log line when the schema is unreadable) and returns rows + `total` in one response. The read-service/adapter gain an optional `parameters` argument; the cache key folds values in only when parameters are present, so today's snapshots stay valid. The frontend keeps committed filter/sort/manualCondition on the tab as session view state (like `browseLimit`/`browsePage`), renders a collapsible shadcn panel behind a funnel toggle in the grid toolbar, and cycles sort via header click.

**Tech Stack:** .NET 10 (`Eling.Core`, `Eling.Backend`, xUnit), MySqlConnector + Microsoft.Data.Sqlite, Next.js 16 dashboard (React 19, Tailwind, shadcn/ui, lucide-react, vitest).

**Spec:** `docs/superpowers/specs/2026-11-16-databases-filter-sort-design.md` (approved 2026-11-16, commit facbf1d). The plan argues from the spec; where the spec says "see " the referenced section is the authority.

## Global Constraints

- Wire property for sort direction is `order` with values `asc`/`desc` — the name `direction` is **forbidden** in this feature (`docs/superpowers/standards/sorting-standard.md`).
- `/browse` is a documented carve-out from `docs/superpowers/standards/sorting-standard.md` (list-panel rules do not apply to the paged grid). Unknown column/op/order value answers **400 naming the offender**, not fallback.
- LIKE escape character is `!`; `!` is doubled first, then `%` and `_` are escaped; every LIKE carries `ESCAPE '!'`. Never `\`.
- AND/OR fold is left-associative: `(((a <logic2> b) <logic3> c) …)`. The first enabled row's `logic` is ignored. `First`/`And`/`Or` map to the SQL-side keyword set `and`/`or` (lowercase on the wire, upper-cased server-side).
- `tinyint` columns coerce bool-first: `true`/`false`/`1`/`0` (case-insensitive) → 1/0 before the decimal parse; every other numeric type parses as invariant-culture decimal else 400 `column '<col>' is numeric; '<value>' is not a number`.
- `LIMIT`/`OFFSET` are inlined clamped integers, never parameters — today's cached snapshots (keyed on the SQL string) stay valid for unfiltered browse.
- Empty string is a legal filter value everywhere; nothing is trimmed before binding (UI trims drafts only for display, and only for numeric parsing feedback).
- Committed filter/sort/manualCondition live on `DbTab` as session state, are stripped by `persistableTab`, never persisted to localStorage/URL; a saved filter must not resurrect.
- No BETWEEN/IN operators in v1. No old-backend fallback: a 404 from `/browse` surfaces as a toast ("Backend does not support /browse; rebuild and restart it.").
- All interactive controls get `cursor-pointer` (repo convention, 2026-11-16). The frontend Next.js is not the Next.js in training data — consult `node_modules/next/dist/docs/` before using any Next-specific API. No `pnpm build` / `next build` while the dev server runs on 4427 (AGENTS.md); type-check with `pnpm --filter eling-dashboard exec tsc --noEmit`.
- Git index ops are leased: `eling_commit_lease` before any `git add`/commit, `eling_commit_release` after it lands. One logical change per commit; foreign files in this checkout are never staged.
- Every audit-path for browse records `Action = "db.browse"`, `Trail = Access`, `Category = Database`, `Outcome` on success/denied/error — the same conventions as `db_query`.

## Review Focus

The five input classes the spec implies but does not spell out, most likely first — each is pinned to the task that owns the code:

1. **`tinyint` filter value `'true'`** — a person typing `true` on `is_active` expects 1, not a spurious 400 (the spec's two whitelists collided before the bool-first amendment). Pinned in Task 1 (`Build_BoolishTinyInt_Mapping`) and Task 4 (passes through untouched).
2. **AND/OR fold with a disabled row in the middle** — a person disables one condition expecting it to vanish from the fold entirely, not to act as a neutral has's there. Pinned in Task 1 (`Build_DisabledRow_IsIgnored`).
3. **Same placeholder SQL, two filter values** — cache-key collision would serve one value's snapshot for the other's query. Pinned in Task 2 (`QueryCacheKey_DifferentValues_DifferentKeys`).
4. **Schema unreadable but snapshot cached** — a person browsing an offline table with a filter expects the cached rows, not 502; and a *live* DB with a typo'd table must 400. Pinned in Task 3 (`Browse_OfflineWithPrimedCache_FromCacheTrue`, `Browse_UnknownTable_Answers400`).
5. **Empty string in a filter** — a person filtering `code = ''` expects rows with empty `code`, not a dropped condition. Pinned in Task 1 (`Build_EmptyStringValue_BindsEmptyString`) and Task 4 (`filter-model` keeps the value as-is).

---

## File Structure

**Backend**
- Create `src/backend/Eling.Core/Database/Browse/BrowseModels.cs` — `BrowseFilter`, `BrowseSort`, `BrowsePlan`, `BrowseQuery` records.
- Create `src/backend/Eling.Core/Database/Browse/BrowseQueryBuilder.cs` — pure, no I/O. Builds rows + count SQL, parameters, and the audit `WhereText`.
- Create `src/backend/Eling.Core/Database/Browse/BrowseQueryException.cs` — one string message, always user-safe (no connection details).
- Create `src/backend/Eling.Core/Database/SqlParam.cs` — `SqlParam(string Name, object? Value)` shared by adapters, read service and cache key.
- Create `src/backend/Eling.Core/Database/QueryCacheKey.cs` — pure `QueryCacheKey.For(string sql, IReadOnlyList<SqlParam>? parameters)`.
- Create `src/backend/Eling.Core/Database/Browse/SqlIdentifiers.cs` — provider-aware identifier quoting (backtick/double-quote doubling) shared by the builder.
- Modify `src/backend/Eling.Core/Database/IDatabaseAdapter.cs` — `QueryAsync` gains trailing `IReadOnlyList<SqlParam>? parameters = null`.
- Modify `src/backend/Eling.Core/Database/Adapters/MySqlDatabaseAdapter.cs`, `SqliteDatabaseAdapter.cs` — bind parameters when present.
- Modify `src/backend/Eling.Core/Database/IDatabaseReadService.cs`, `DatabaseReadService.cs` — `QueryAsync` gains trailing `IReadOnlyList<SqlParam>? parameters = null`; cache key via `QueryCacheKey.For`.
- Modify `src/backend/Eling.Core/Database/DatabaseAuditActions.cs` — add `Browse = "db.browse"`.
- Modify `src/backend/Eling.Backend/Dtos/DatabaseDtos.cs` — `BrowseFilterDto`, `BrowseSortDto`, `BrowseRequest`, `BrowseResponse`.
- Modify `src/backend/Eling.Backend/Endpoints/DatabaseEndpoints.cs` — new `POST /{id}/browse` route (validation, schema check, two read-service calls, one response, audit).
- Tests: `tests/Eling.Core.Tests/BrowseQueryBuilderTests.cs` (new, Task 1), `tests/Eling.Core.Tests/QueryCacheKeyTests.cs` (new, Task 2), `tests/Eling.Backend.Tests/DatabaseBrowseEndpointTests.cs` (new, Task 3).

**Frontend** (`src/frontend/Eling.Dashboard/src/app/dashboard/databases/`)
- Create `filter-model.ts` + `filter-model.test.ts` — wire types, `columnKind`, `opsFor`, `cycleSort`, `activeCount`, serialization.
- Create `components/data-grid/FilterPanel.tsx` — the collapsible panel (manual condition, condition rows, sort rows, enable/disable/clear/apply).
- Modify `components/data-grid/DataGrid.tsx` — funnel toggle with badge, header sort arrows/cycle, per-column filter marker, panel instance.
- Modify `db-tabs.ts` — `DbTab` gains `browseFilters?`, `browseSort?`, `manualCondition?`; `persistableTab` strips them. (+ `db-tabs.test.ts` case.)
- Modify `hooks/useTableData.ts` — browse path becomes one `/browse` call; page clamps on server `total` (shape unchanged: `countedTotal`).
- Modify `components/ExplorerPanels.tsx`, `Databases.tsx` — plumb callbacks/state; 404 toast text.
- Docs: `docs/superpowers/standards/sorting-standard.md` — one exception row in the adoption table (Task 7).

---

### Task 1: Core records, identifier quoting, `BrowseQueryBuilder`

**Interfaces:**
- Produces (consumed by Tasks 2–3):
  - `Eling.Core.Database.SqlParam { string Name; object? Value }` (own file `SqlParam.cs`)
  - `Eling.Core.Database.Browse.BrowseFilter { string Column; string Op; string? Value; bool Enabled; string Logic }`
  - `Eling.Core.Database.Browse.BrowseSort { string Column; string Order }`
  - `Eling.Core.Database.Browse.BrowseQuery { string Sql; IReadOnlyList<SqlParam> Parameters }`
  - `Eling.Core.Database.Browse.BrowsePlan { BrowseQuery Rows; BrowseQuery Count; string WhereText }`
  - `Eling.Core.Database.Browse.BrowseQueryException { string Message }`
  - `BrowseQueryBuilder.Build(DatabaseProvider provider, string table, IReadOnlyList<BrowseFilter> filters, IReadOnlyList<BrowseSort> sort, string? manualCondition, IReadOnlyDictionary<string, string> columnTypes, bool schemaAvailable, int page, int limit) : BrowsePlan` — throws `BrowseQueryException` with a user-safe message on validation failure.

- [ ] **Step 1: Write the failing tests** in `tests/Eling.Core.Tests/BrowseQueryBuilderTests.cs`. Class name `BrowseQueryBuilderTests`. Use `DatabaseProvider.Sqlite` for quoting-independent assertions, `DatabaseProvider.MySql` for backtick cases. Helper: `static BrowseFilter F(string col, string op, string? value = null, bool enabled = true, string logic = "first") => new(col, op, value, enabled, logic);` and `BuiltPlan` returns `(RowsSql, CountSql, Params, WhereText)` flattened for assertions. Tests (exact names):

  - `Build_StringContains_BindsEscapedLikeWithEscapeBang` — `name contains '50%'` → `RowsSql` contains `name LIKE @p0 ESCAPE '!'`, `Params[0].Value` is `"%50!%%"` (the `%` escaped), `_`-and-`!` escaping also covered in two more asserts or a second test (`Build_StringContains_EscapesUnderscoreAndBang` — input `_!` → `!_!!`).
  - `Build_EqualityOnStringColumn_BindsStringParam` — `updated_at eq '2025-02-19'` → param value `"2025-02-19"`.
  - `Build_BoolishTinyIntIsActiveTrue_BindsOne` — `is_active eq 'true'` on `tinyint` → param `1` (int). Also `'FALSE'` → `0`, `'1'` → `1`.
  - `Build_NumericColumn_BadValue_Throws` — `decimal_col eq 'abc'` → `BrowseQueryException` whose message contains `column 'decimal_col' is numeric`; same for `bigint` with `'12.5.7'`.
  - `Build_NumericColumn_GoodValue_BindsDecimal` — `decimal_col eq '12.5'` → param parses as `12.5m`.
  - `Build_EnabledFiltersLeftAssociativeFold_ProducesParenthesisedNest` — filters: `a eq 1` (first), `b eq 2` logic `and`, `c eq 3` logic `or` → `RowsSql` contains `(((id = @p0 AND b = @p1) OR c = @p2))`-shaped text with the identifiers quoted (assert exact string for Sqlite: `(((` + `"a" = @p0 AND "b" = @p1` + `) OR "c" = @p2` + `))`); the `OR` case mirrored (`a or b and c`).
  - `Build_DisabledRow_IsIgnored` — three rows, middle disabled → WHERE contains only the two enabled clauses in left-associative order, and the disabled column name does not appear anywhere in `RowsSql`/`Param` values.
  - `Build_FirstLogicOnNonFirstRow_IsTreatedAsAnd` — `logic: "first"` on a non-first row folds as `AND`.
  - `Build_IsNull_And_IsNotNull_BindNoParameters` — `isNull` → `x IS NULL`, no params consumed by it; fold with a second enabled row still works.
  - `Build_AllDisabledAndNoManual_OmitsWhere` — `RowsSql` has no `WHERE`; `CountSql` is `SELECT COUNT(*) FROM …` without WHERE.
  - `Build_ManualCondition_AppendsParenthesisedAnd` — filters `a eq 1` + manual `is_active = 1` → `(WHERE ("a" = @p0)) AND (is_active = 1)`; whitespace-only manual is treated as absent (`Build_ManualWhitespaceOnly_IsIgnored`).
  - `Build_MultiColumnSort_EmitsBothOrderClauses` — sort `updated_at desc`, then `created_at asc` → `ORDER BY "updated_at" DESC, "created_at" ASC`.
  - `Build_BadOrderValue_Throws` — `BrowseSort("a", "sideways")` → `BrowseQueryException`.
  - `Build_UnknownOp_Throws` — `op: "like"` → `BrowseQueryException`.
  - `Build_UnknownColumnWhenSchemaAvailable_Throws` — `schemaAvailable: true`, column not in `columnTypes` → `BrowseQueryException` naming the column; `Build_UnknownColumnWhenSchemaUnavailable_Succeeds` — same call with `schemaAvailable: false` succeeds and quotes the identifier.
  - `Build_IdentifierQuoting_DoublesQuoteChar` — MySql table/column with a backtick in the name (`a``b`) is quoted as `` `a``b` `` per doubling rule; Sqlite same with `"`.
  - `Build_PageAndLimit_InlineClamped` — `page 2`, `limit 250` → `LIMIT 250 OFFSET 250`; `page 0` → treated as 1; `limit` 1001 → clamped to 1000; `limit` 0 → clamped to 1.
  - `Build_RowsAndCountQueries_ShareWhereTextAndParamOrder` — `CountSql` contains the identical parenthesised WHERE string as `RowsSql` and parameter names appear in the same order.
  - `Build_EmptyStringValue_BindsEmptyString` — `code eq ''` → param value `""` (not dropped, not null).
  - `Build_SqlSafetyVerdictForBuilderOutput_IsReadOnly` — `SqlSafety.Classify(plan.Rows.Sql)` verdict is `ReadOnly` (Sqlite and MySql plans).

- [ ] **Step 2: Run tests to verify they fail** — `dotnet test tests/Eling.Core.Tests --filter "FullyQualifiedName~BrowseQueryBuilder"` → FAIL (types don't exist).
- [ ] **Step 3: Implement.** Files:
  - `SqlParam.cs`: `public sealed record SqlParam(string Name, object? Value);` in `Eling.Core.Database`.
  - `Browse/BrowseModels.cs`: `BrowseFilter`, `BrowseSort` (`Order` whitelisted to `asc`/`desc` case-insensitive at build time), `BrowseQuery` (`Sql`, `Parameters`), `BrowsePlan` (`Rows`, `Count`, `WhereText`), all in `namespace Eling.Core.Database.Browse`.
  - `Browse/BrowseQueryException.cs`: `public sealed class BrowseQueryException(string message) : Exception(message);`
  - `Browse/SqlIdentifiers.cs`: `internal static string Quote(DatabaseProvider provider, string name)` — MySql: `` ` `` + `name.Replace("`", "``")` + `` ` ``; Sqlite: `"` + `name.Replace("\"", "\"\"")` + `"`.
  - `Browse/BrowseQueryBuilder.cs`: static class implementing `Build` exactly as the tests describe; private helpers `BuildWhere`, `ClauseFor(filter)` (operator map, LIKE pattern + `ESCAPE '!'`, isNull/isNotNull no param), `CoerceValue(columnType, raw)` (bool-first tinyint → int, else invariant-culture `decimal.TryParse`, datetime/text/bool pass-through as string), `SerializeLike`. Parameter names `@p0..` in order of appearance; the same `WHERE` string and parameter list feed both Rows and Count. `WhereText` is the same fold with values inlined as quoted literals, truncated to 600 chars for the audit log.
  - Column-type map: `columnTypes` keys matched case-insensitively (OrdinalIgnoreCase); bool-first when the type string lowercases to `tinyint`.
- [ ] **Step 4: Run tests** — same command → PASS. Also `dotnet test tests/Eling.Core.Tests` (no regressions).
- [ ] **Step 5: Commit** — `git add src/backend/Eling.Core/Database/SqlParam.cs src/backend/Eling.Core/Database/Browse tests/Eling.Core.Tests/BrowseQueryBuilderTests.cs && git commit -m "feat(db): browse query builder for filter/sort"` (lease first, release after).

---

### Task 2: Parameter plumbing through adapters, read service, cache key

**Interfaces:**
- Consumes: `SqlParam` (Task 1).
- Produces: `QueryCacheKey.For(string sql, IReadOnlyList<SqlParam>? parameters) : string` in `Eling.Core.Database`; `IDatabaseAdapter.QueryAsync` and `IDatabaseReadService.QueryAsync` accept `IReadOnlyList<SqlParam>? parameters = null` as the trailing argument (existing call sites compile unchanged).

- [ ] **Step 1: Write failing tests** in `tests/Eling.Core.Tests/QueryCacheKeyTests.cs`:
  - `QueryCacheKey_NoParameters_IsTheSqlUnchanged` — `For("SELECT 1", null)` == `"SELECT 1"`; also for empty list.
  - `QueryCacheKey_DifferentValues_DifferentKeys` — same `sql`, params `[{p0, "a"}]` vs `[{p0, "b"}]` → different keys.
  - `QueryCacheKey_TypeTagsDistinguishStringFromInt` — value `"1"` vs `1` → different keys (guards SQLite's `'1' <> 1` trap).
  - `QueryCacheKey_IsStableForSameInput` — identical calls → identical key (cache hit path).
  - In the same file or in a small extension of `DatabaseReadServiceCacheTests` style (new file `DatabaseReadServiceParameterCacheTests.cs`): `QueryAsync_BrokenConnection_ParametersInCacheKeyServeFromCache` — create service + `JsonDatabaseReadCache`; use `DatabaseConnection.Create("broken@local", MySql, "Server=127.0.0.1;Port=1;Database=broken;User Id=u;Password=p;ConnectionTimeout=1", ReadOnly)`; `SetQuery(id, null, 1, QueryCacheKey.For(sql, p1), resultA)` and with `p2` the resultB; then `QueryAsync(conn, null, sql, 1, ct, preferCache: true, parameters: p1)` returns resultA with `FromCache == true` and **never** resultB.
- [ ] **Step 2: Run tests to verify they fail** — `dotnet test tests/Eling.Core.Tests --filter "FullyQualifiedName~QueryCacheKey|FullyQualifiedName~DatabaseReadServiceParameterCache"` → FAIL.
- [ ] **Step 3: Implement.**
  - `QueryCacheKey.cs`: `public static class QueryCacheKey { public static string For(string sql, IReadOnlyList<SqlParam>? parameters) }` — no params/empty → `sql` unchanged; else `sql + "\n--p:" + string.Join("\u001f", parameters.Select(p => p.Name + "=" + Tag(p.Value) + ":" + Convert.ToString(p.Value, CultureInfo.InvariantCulture)))` with `Tag` = `n` (null), `b` (bool), `s` (string), `d` (decimal/double/float), `i` (integral), else `x`; `Convert.ToString(..., InvariantCulture)` guards culture drift.
  - `IDatabaseReadService.QueryAsync`: append `IReadOnlyList<SqlParam>? parameters = null` after `preferCache`. `DatabaseReadService.QueryAsync`: compute `var key = QueryCacheKey.For(sql, parameters);` and pass `key` to `TryGetQuery`/`SetQuery` in place of `sql`; pass `parameters` to `adapter.QueryAsync(sql, maxRows, cancellationToken, parameters)`. Update the `ArgumentNullException`/SQL guard untouched.
  - `IDatabaseAdapter.QueryAsync`: signature becomes `Task<QueryResult> QueryAsync(string sql, int maxRows, CancellationToken ct = default, IReadOnlyList<SqlParam>? parameters = null);`
  - `MySqlDatabaseAdapter.QueryAsync`: after `cmd.CommandText = sql;` add `foreach (var p in parameters ?? []) cmd.Parameters.AddWithValue(p.Name, p.Value ?? DBNull.Value);` — use `MySqlParameter` with `MySqlDbType` inference via `AddWithValue`; placeholders in builder SQL are `@pN`, matching.
  - `SqliteDatabaseAdapter.QueryAsync`: same with `Microsoft.Data.Sqlite.SqliteParameter`.
  - `ExecuteAsync` is **not** touched (mutations stay raw, as today).
- [ ] **Step 4: Run tests** — the new tests PASS; `dotnet test` full suite PASS (no call-site breakage).
- [ ] **Step 5: Commit** — `feat(db): parameter binding for reads and parameter-aware cache key` (lease first).

---

### Task 3: `POST /api/databases/{id}/browse` endpoint + DTOs + audit + endpoint tests

**Interfaces:**
- Consumes: `BrowseQueryBuilder.Build`, `SqlParam`, `BrowseFilter`, `BrowseSort` (Task 1); `QueryAsync(..., parameters)` (Task 2); `DatabaseAuditActions`; `ConnectionNotFoundResult` (existing, `Eling.Backend`); `RedisRequirement.NotSql`.
- Produces (consumed by Task 6): route `POST /api/databases/{id}/browse`, request/response JSON below (camelCase by the existing `ConfigureHttpJsonOptions` policy).

Request:
```json
{ "table": "mst_rate", "database": "dev_reference", "page": 1, "limit": 100,
  "filters": [ { "column": "name", "op": "contains", "value": "single", "enabled": true, "logic": "first" } ],
  "sort": [ { "column": "updated_at", "order": "desc" } ],
  "manualCondition": "is_active = 1", "refresh": false }
```
Response: `{ "columns": [...], "rows": [...], "truncated": false, "total": 118, "fromCache": false, "cachedAt": null }`. `filters`/`sort` may be omitted/empty; `logic` values `first|and|or` (case-insensitive, server-side mapping `first`→ ignored); `refresh: true` bypasses cache (same semantics as `/query?refresh=true`).

- [ ] **Step 1: Write failing endpoint tests** in `tests/Eling.Backend.Tests/DatabaseBrowseEndpointTests.cs`, following `DatabaseConnectionSortEndpointTests` (TestHost, `CreateSlimBuilder`, temp dir, `JsonDatabaseConnectionStore`, `NullAuditLogger`, real `DatabaseReadService` + `JsonDatabaseReadCache`):

  Helper `SeedSqliteAsync(string name, string table, params string[] insertRows)` — creates a SQLite file in `_root`, table `mst_test (id INTEGER PRIMARY KEY, name TEXT, is_active INTEGER DEFAULT 1, updated_at TEXT)`, inserts the given rows, registers a connection (`DatabaseProvider.Sqlite`, `$"Data Source={db}"`, `ReadOnly`), returns `(id, dbPath)`. Tests:

  - `Browse_HappyPath_ReturnsRowsAndTotal` — 4 rows, 2 matching `name contains 'a'`; request `filters` + `sort name asc` → 200; `total == 2`; `columns == [id, name, is_active, updated_at]`; `rows` ordered asc by name; `fromCache == false`.
  - `Browse_Unfiltered_ReturnsAllRowsAndCorrectTotal` — no filters → `total == 4`.
  - `Browse_SortDesc_ReturnsDescendingOrder` — `order: "desc"` on `name`.
  - `Browse_UnknownTable_Answers400WithTableName` — `table: "nope"` against a SQLite connection (schema IS available) → 400, body contains `nope`.
  - `Browse_UnknownColumn_Answers400WithColumnName` — `filters: [ { column: "nope", op: "eq", value: "x" } ]` → 400 naming `nope`.
  - `Browse_InvalidNumericValue_Answers400` — `is_active eq 'banana'` (tinyint) → wait: bool-first means `'banana'` falls through to decimal parse → 400 naming `is_active`. Also `updated_at eq` on datetime with any string value binds fine (datetime accepts strings) — keep that as a passing case in the happy-path test instead.
  - `Browse_InvalidOp_Answers400` — `op: "like"` → 400 naming `like`.
  - `Browse_InvalidOrderValue_Answers400` — `order: "sideways"` → 400 naming `sideways`.
  - `Browse_RedisConnection_Answers400` — register a Redis row → 400 (NotSql), same as `/query`.
  - `Browse_OfflineWithPrimedCache_AnswersFromCache` — broken-MySQL connection (`Server=127.0.0.1;Port=1;...`); prime `JsonDatabaseReadCache` via `SetSchema` with a schema containing `mst_test`/(`id`,`name`,`is_active`,`updated_at`) and `SetQuery` for the exact `Sql` the builder will emit for request `filters:[name eq 'a'], sort:[updated_at asc], page:1, limit:50` with `QueryCacheKey.For(sql, [p0='a'])` → POST `/browse` → 200, `fromCache == true`. (If assembling `sql` by hand is brittle, the test may instead prime the cache by calling `QueryAsync` first against a real SQLite? No — SQLite bypasses cache. Hand-assembly is the point: compute `expectedSql` from `BrowseQueryBuilder.Build(...)` directly in the test — it's public.)
  - `Browse_CountAndRowsShareCacheKeys_OfflineRowsCachedCountMissing_Answers502` — prime rows but not count → 502 (whole request fails rather than half a response).
  - `Browse_AuditActionIsDbBrowse` — pass a recording `IAuditLogger` stub (pattern from existing audit tests; if none exists, capture into a `List<AuditEvent>`) and assert one event with `Action == "db.browse"` and `Trail == Access`.
  - `Browse_ReadOnlyEnvironment_IsAllowed` — SQLite connection registered as `ReadOnly` succeeds (it is a read) — covered implicitly by the happy-path helper using `ReadOnly`; assert 200 explicitly.
- [ ] **Step 2: Run tests to verify they fail** — `dotnet test tests/Eling.Backend.Tests --filter "FullyQualifiedName~DatabaseBrowseEndpoint"` → FAIL (route missing).
- [ ] **Step 3: Implement.** In `DatabaseDtos.cs` (append, positional records, `System.Text.Json` camelCase by existing options):

  ```csharp
  public sealed record BrowseFilterDto(string? Column, string? Op, string? Value, bool Enabled = true, string? Logic = "first");
  public sealed record BrowseSortDto(string? Column, string? Order = "asc");
  public sealed record BrowseRequest(string? Table, string? Database = null, int Page = 1, int Limit = 100, IReadOnlyList<BrowseFilterDto>? Filters = null, IReadOnlyList<BrowseSortDto>? Sort = null, string? ManualCondition = null, bool Refresh = false);
  public sealed record BrowseResponse(IReadOnlyList<string> Columns, IReadOnlyList<IReadOnlyList<object?>> Rows, bool Truncated, long Total, bool FromCache, DateTimeOffset? CachedAt);
  ```
  Add `Browse = "db.browse"` to `DatabaseAuditActions` (with a comment matching the class's doc convention).

  In `DatabaseEndpoints.cs`, next to `/{id}/query` (before it, to keep read routes adjacent):
  1. Find row; 404 via `ConnectionNotFoundResult`; Redis → `RedisRequirement.NotSql(row, "use /keys")` 400.
  2. Clamp `Page = Math.Max(1, req.Page)`, `Limit = Math.Clamp(req.Limit, 1, 1000)`.
  3. `preferCache = !req.Refresh`.
  4. Schema read: `try { var schema = await readService.GetSchemaAsync(row, req.Database, ct, preferCache: preferCache); tableExists / columnTypes / schemaAvailable from it } catch (Exception ex) { logger.LogWarning(ex, "schema read for browse validation skipped for {Id}", id); schemaAvailable = false; tableExists = true; columnTypes = empty; }`. If `schemaAvailable && !tableExists` → 400 `table '<table>' not found`. `columnTypes`: `OrdinalIgnoreCase` dict from the table's columns; columns referenced in filters/sort must exist else 400 `column '<col>' not found` (when `schemaAvailable`).
  5. Map DTOs → `BrowseFilter`/`BrowseSort` (null-check column/op/order; `Logic` normalized `first|and|or` via ToLowerInvariant, unknown → 400 naming it; default `"first"`). `manualCondition` trimmed for blankness check only.
  6. `var plan = BrowseQueryBuilder.Build(row.Provider, req.Table!, filters, sorts, req.ManualCondition, columnTypes, schemaAvailable, page, limit);` catch `BrowseQueryException ex` → `Results.BadRequest(ex.Message)` + audit `Outcome = Denied`, `Query = req.Table + summary`.
  7. `var rows = await readService.QueryAsync(row, req.Database, plan.Rows.Sql, limit, ct, preferCache: preferCache, parameters: plan.Rows.Parameters);` then `var count = await readService.QueryAsync(row, req.Database, plan.Count.Sql, 1, ct, preferCache: preferCache, parameters: plan.Count.Parameters);` — count failure → **502** (`Results.Problem(statusCode: 502, title: "browse failed", detail: ex.Message)`), audit `Outcome = Error`.
  8. `total`: `count.Rows.FirstOrDefault()?[0]` → `long` via `Convert.ToInt64(..., CultureInfo.InvariantCulture)`; if unparsable → 502 like a count failure.
  9. `fromCache = rows.FromCache || count.FromCache`, `cachedAt = max of the two stamps` (ignore nulls). `truncated: false` — the inline `LIMIT` caps by construction.
  10. Audit success event (`Action = DatabaseAuditActions.Browse`, `Query = plan.WhereText`,`ResultCount = total`, `FromCache`, `DurationMs`), then `Results.Ok(new BrowseResponse(rows.Columns, rows.Rows, false, total, fromCache, cachedAt))`.
  - [ ] **Step 4: Run tests** — new suite PASS; full `dotnet test tests/Eling.Backend.Tests` PASS.
- [ ] **Step 5: Commit** — `feat(db): /browse endpoint with schema validation and cached reads` (lease first).

---

### Task 4: Frontend `filter-model.ts` (pure, vitest)

**Interfaces:**
- Produces (imported by Tasks 5–6): `BrowseFilter`, `BrowseSort`, `BrowseResponse` types; `columnKind(dataType)`, `opsFor(kind)`, `cycleSort(current: BrowseSort | null): BrowseSort | null`, `activeCount(filters, manualCondition): number`, `serializeBrowse(table, database, page, limit, filters, sort, manualCondition)` (wire body), `trimDraft(value: string): string` (display-only).

- [ ] **Step 1: Write failing tests** in `filter-model.test.ts`:
  - `columnKind maps varchar/text/char/enum to text`; `int/bigint/smallint/tinyint/decimal/float/double/numeric/real to numeric`; `date/datetime/timestamp to datetime`.
  - `opsFor('text') contains contains/notContains/startsWith/endsWith/eq/neq/isNull/isNotNull and NOT gt/gte`; `opsFor('numeric') contains eq/neq/gt/gte/lt/lte/isNull/isNotNull and NOT contains`; `opsFor('datetime')` same list as numeric; every list ends with isNull/isNotNull. Labels: numeric `eq` → `=`, `neq` → `≠`; text `eq` → `Equals`, `contains` → `Contains`. (Label map is part of the return of `opsFor`.)
  - `cycleSort` — `null → {column, order:'asc'} → desc → null`; and `serializeBrowse` produces exactly `{table, database, page, limit, filters, sort, manualCondition}` with keys in that order.
  - `activeCount` — 2 enabled + 1 disabled + blank manual → `2`; manual `'  '` → ignored; manual `'x'` → `+1`.
  - `trimDraft` — `'  single  ' → 'single'`; keeps interior spaces.
  - `persistableTab strips the three new fields` — **edit** `db-tabs.test.ts`: extend the existing "drops browseLimit and browsePage" test (or add a sibling) with a tab carrying `browseFilters`, `browseSort`, `manualCondition` and assert all three are absent from `persistableTab`'s output. (This is the persistence test the spec requires.)
- [ ] **Step 2: Run tests to verify they fail** — `pnpm --filter eling-dashboard exec vitest run src/app/dashboard/databases/filter-model.test.ts` → FAIL.
- [ ] **Step 3: Implement `filter-model.ts`.** Pure module, no imports beyond types. Wire types:

  ```ts
  export type BrowseOp = "eq" | "neq" | "contains" | "notContains" | "startsWith" | "endsWith" | "gt" | "gte" | "lt" | "lte" | "isNull" | "isNotNull"
  export type BrowseLogic = "first" | "and" | "or"
  export interface BrowseFilter { column: string; op: BrowseOp; value: string; enabled: boolean; logic: BrowseLogic }
  export interface BrowseSort { column: string; order: "asc" | "desc" }
  export interface BrowseResponse { columns: string[]; rows: unknown[][]; truncated: boolean; total: number | null; fromCache?: boolean; cachedAt?: string | null }
  ```
  `columnKind` lowercases `dataType`, matches the families above with a regex/regex-union; unknown types fall back to `text`. `opsFor` returns `{ value, label }[]` per kind with the label map above; the `datetime` list mirrors `numeric` (`gt` etc. labelled `After`/`On or after`/`Before`/`On or before`, `eq` → `On`). `activeCount` counts `enabled` filters plus non-blank manual (`.trim() !== ""`). `serializeBrowse` returns the plain object. Sorting helpers `toggleSortColumn(sort, column)`: replaces the list with `[{column, order}]` cycling asc→desc→remove.
- [ ] **Step 4: Run tests** — PASS; `pnpm --filter eling-dashboard exec tsc --noEmit` clean.
- [ ] **Step 5: Commit** — `feat(databases): browse filter wire model` (lease first).

---

### Task 5: `FilterPanel.tsx` + DataGrid header cycle + funnel toggle

**Interfaces:**
- Consumes: `BrowseFilter`, `BrowseSort`, `opsFor`, `columnKind`, `activeCount`, `trimDraft`, `BrowseResponse`-adjacent types (Task 4); existing `ColumnSchema`, `DataGridProps` shape; shadcn `Select`/`Input`/`Button`/`Badge`/`Card` pattern already in the grid.
- Produces (consumed by Task 6): `FilterPanel` (props: `columns: string[]`, `columnTypes: Record<string, string>`, `filters: BrowseFilter[]`, `sort: BrowseSort[]`, `manualCondition: string`, `schemaKnown: boolean`, `onPatch(next: {filters: BrowseFilter[]; sort: BrowseSort[]; manualCondition: string}): void`, `onApply(): void`, `onClose(): void`). New `DataGridProps` fields: `browseFilters?`, `browseSort?`, `manualCondition?`, `onApplyBrowse?(next): void`, `onCycleSortColumn?(column: string): void`.

- [ ] **Step 1: (No unit test first — this is presentational; the behavior is pinned by Task 4 units + Task 6 wiring + Task 8 e2e.)** Build `FilterPanel.tsx` matching the spec's UI sketch, in the dashboard's look:
  - Layout: panel above the grid, `<div className="rounded-md border bg-muted/30 p-2 space-y-2">`. First row: `Manual query condition` label + full-width `<Input className="h-8 font-mono text-xs" spellCheck={false} placeholder="status = 1 AND name LIKE 'A%'">` + inline `Clear` text button (clears the field only). Help text under it (`text-[11px] text-muted-foreground`): `ANDed verbatim, raw SQL. Leave empty to filter only by the rows below.`
  - Condition rows (`filters.map`): `<input type="checkbox" checked={f.enabled}>` + `<Select>` logic (`first` → option `First`, disabled for row 0; `and` → `And (AND)`; `or` → `Or (OR)`) + column `<Select>` (all `columns`) + operator `<Select>` (`opsFor(columnKind(type))`, `Is null`/`Is not null` hide `value`) + value `<Input className="h-8 flex-1 font-mono text-xs">` (hidden for isNull/isNotNull) + remove `<button aria-label="Remove condition" className="cursor-pointer">×</button>`. Disabled rows get `opacity-50`.
  - Sort rows (`sort.map`): column `<Select>` + order `<Select>` (`ascending`/`descending`) + remove button.
  - Footer row: `<Button variant="outline" size="sm" onClick={addCondition}>+ Add condition</Button>` · `+ Add sort` (hidden when every schema column is already sorted) · spacer · `Enable all` · `Disable all` · `Clear all` · `Apply` (primary). `Clear all` clears drafts AND calls `onApply()` with the empty state. Every control `cursor-pointer`.
  - Row logic mapping: row 0 → `"first"` always and its select is disabled; rows > 0 carry `and|or`. `Add condition` appends `{ column: defaultColumn, op: "eq", value: "", enabled: true, logic: "and" }` where `defaultColumn` = second non-pk column if any else `columns[0]` (computed once from `columns` order).
  - Local draft state is owned by the panel: parent passes `initial` via props; panel does `useState` seeded from props **and re-seeds on open** via an `open` boolean prop — or simpler: parent mounts the panel only when open (`{open && <FilterPanel …/>}`), so unmount = discard. Choose mount/unmount; document in the component doc-comment.
  - `Ctrl+Enter` inside any input fires `onApply()`.
- [ ] **Step 2: DataGrid changes** (`data-grid/DataGrid.tsx`):
  - Toolbar row: after the Add row group, a funnel `<Button size="sm" variant={activeCount(committed) > 0 ? "secondary" : "outline"} onClick={togglePanel} aria-label="Filter">` — lucide `ListFilter` icon (or `Funnel`; use `ListFilter`, consistent with the rest of the lucide usage) + `<span className="ml-1">Filter</span>` + count badge (`<Badge variant="secondary">{n}</Badge>` only when > 0).
  - Panel renders between the toolbar row and the table when open (`{filterOpen && <FilterPanel columns=… columnTypes={…} filters={draft…} … onApply={handleApply} />}`). `columnTypes` derived from `schemaInfo` (same lookup `columnSchema` uses).
  - Header: append sort glyph to `TableHead` content (`▲`/`▼` or lucide `ArrowUp`/`ArrowDown`, size `size-3`, inline-flex with `ml-1`) when `browseSort` contains the column; clicking the header cycles via `onCycleSortColumn(col)` (button wrapping text, `cursor-pointer`, `title="Cycle sort: none → asc → desc"`). Filtered columns (in `browseFilters`, enabled or not) get lucide `ListFilter` size `size-3` `text-emerald-600`.
  - New props threaded: `browseFilters: BrowseFilter[]`, `browseSort: BrowseSort[]`, `manualCondition: string`, `onApplyBrowse(next: { filters, sort, manualCondition }): void`, `onCycleSortColumn(column: string): void`. Local `filterOpen` state lives in DataGrid.
- [ ] **Step 3: Verify** — `pnpm --filter eling-dashboard exec tsc --noEmit` → clean; `pnpm --filter eling-dashboard exec vitest run` → all existing suites + filter-model PASS.
- [ ] **Step 4: Commit** — `feat(databases): filter panel and header sort cycling` (lease first).

---

### Task 6: Wire the Data tab to `/browse` (state, hook, plumbing, persistence strip)

**Interfaces:**
- Consumes: everything in Tasks 2, 4, 5.
- Produces: a working Data tab. Route returns 404 on an un-rebuilt backend → toast `Backend does not support /browse; rebuild and restart it.`

- [ ] **Step 1: `db-tabs.ts`** — `DbTab` gains `browseFilters?: BrowseFilter[]`, `browseSort?: BrowseSort[]`, `manualCondition?: string` (import types from `filter-model`). `persistableTab` destructures them out exactly like `browseLimit`/`browsePage`. Update/add the `db-tabs.test.ts` case if Task 4 didn't already.
- [ ] **Step 2: `useTableData.ts`** — in `browseData`, replace the `Promise.all([…query…, …query COUNT…])` block with a single call:

  ```ts
  const current = activeTableRef.current   // already read
  const effectiveFilters = current?.browseFilters ?? []
  const effectiveSort = current?.browseSort ?? []
  const manualCondition = current?.manualCondition?.trim() ? current.manualCondition : null
  const res = await apiFetch<BrowseResponse>(API + "/" + id + "/browse", {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify(serializeBrowse(curTable, database ?? null, page, limit, effectiveFilters, effectiveSort, manualCondition)),
  })
  ```
  Set `browseResult = res` (it already satisfies `QueryResult`'s shape + `total`), `browseTotal = typeof res.total === "number" ? res.total : res.rows.length`. Keep the clamp branch but feed it `countedTotal`; keep the 5s re-poll when `fromCache`; keep `browseInFlightRef` semantics untouched. Import `serializeBrowse`, `BrowseResponse` from `../filter-model`. Delete the two raw-SQL branches (including the count query and `buildWherePk`-free inline SQL string construction — the `quoteIdent`/`quote` locals for this path go too; `quoteIdent` is still used by `useTableMutations` for its UPDATE statements).
  - [ ] **Step 3: `Databases.tsx` + `ExplorerPanels.tsx`** — plumb `browseFilters`/`browseSort`/`manualCondition` into `DataGrid` via the same prop chain as `browseLimit`/`browsePage` (ExplorerPanels gains three fields + two callbacks). Add to `Databases.tsx`:
    ```ts
    const applyBrowse = useCallback((next: { filters; sort; manualCondition }) => {
      setActiveTable(t => t ? { ...t, browseFilters: next.filters, browseSort: next.sort, manualCondition: next.manualCondition, browsePage: 1 } : t)
      setOpenTabs(tabs => tabs.map(t => sameTabIdentity(t) ? { ...t, browseFilters…, browseSort…, manualCondition…, browsePage: 1 } : t))
    }, [])
    ```
    (identity = existing `tabKey` helper in `db-tabs.ts`; reuse it, do not compare objects). `cycleSortColumn`: maps current committed sort to the next state via `cycleSort`, writes to tab, refetches. Refetch trigger: `applyBrowse`/`cycleSortColumn` must call the same `refreshData()` the Refresh button uses — find it in `Databases.tsx` (it wraps `browseData(..., refresh=true)`); extraction if it's inline. Also update the open-table tab initializer (`useExplorerTabs` `newActive`) — no change: new tabs default `undefined` filters, which is what we want.
  - [ ] **Step 4: 404 handling** — in the same `catch` block, when `e instanceof Error && /browse/.test(e.message) && /404/.test(e.message)`, `notifyDb("error", "Browse unavailable", "Backend does not support /browse; rebuild and restart it.")` once per tab, else today's behavior. Keep it a small branch, not a helper.
- [ ] **Step 5: Verify** — `pnpm --filter eling-dashboard exec tsc --noEmit`; `pnpm --filter eling-dashboard exec vitest run` (all green incl. `db-tabs`); commit `feat(databases): /browse-backed data tab with committed filter state` (lease first).

---

### Task 7: `docs/superpowers/standards/sorting-standard.md` exception row

- [ ] Add one row to the adoption table (§4):

  ```markdown
  | `POST /api/databases/{id}/browse` (paged grid: arbitrary columns, multi-column sort, filter payload, per-tab session state) | **Exception** — scoped to paged grids, not list panels. Server sorts; JSON body with `order` (never `direction`); no URL/localStorage persistence; unknown value → 400, not fallback. See `docs/superpowers/specs/2026-11-16-databases-filter-sort-design.md` § Relation to `docs/superpowers/standards/sorting-standard.md`. |
  ```

  And adjust the lead-in of §4 "Applies to every list endpoint" → "Applies to every **list panel** endpoint". No other rule changes.
- [ ] Commit `docs(sorting-standard): scope to list panels, note /browse exception` (lease first).

---

### Task 8: End-to-end verification on the dev servers

- [ ] Ensure backend runs under `dotnet watch` on 4417 (do not kill an already-running watcher to rebuild — build to the separate output dir or let the watcher rebuild; if `dotnet build` warns about the same bin being in use, stop and report).
- [ ] Ensure frontend dev server on 4427; **do not run `pnpm build`**. Type-check first: `pnpm --filter eling-dashboard exec tsc --noEmit`.
- [ ] Open `http://localhost:4427/dashboard/databases` in the OpenChamber browser panel. Pick a table the registered dev connection exposes (`mst_rate` in `dev_reference` per the reference screenshot; else any registered `mst_*` table; if none is registered, register/pick a SQLite file with a known table via the existing form, noting to the user what was used).
- [ ] Exercise: (1) open Filter panel; confirm rows are dimmed-disabled on first add? (no — enabled); (2) `name contains 'Standard'` → **Apply** → grid + `Total rows: N` + clamped page; (3) toggle the condition off **without** Apply → grid unchanged; (4) Apply again → unfiltered again; (5) header click on `updated_at` cycles asc → desc → none (arrow shows each); (6) panel `Add sort` for a second column → both apply via Apply (multi-column); (7) `is_active = 'true'` on tinyint → filtered, no 400; (8) manual condition `updated_at >= '2025-02-19'` AND a row condition → both apply (manual ANDs last); (9) Clear all → back to full table; (10) Ctrl+Enter applies; (11) funnel badge shows committed count, panel badge disappears on Clear all; (12) no console errors; (13) switch tabs and back — each tab keeps its own filters.
- [ ] If any step fails, fix in the relevant task's files, re-verify, and commit the fix separately.
- [ ] Report results to the user with what passed and what needed fixing; update this plan's checkboxes accordingly.

---

## Self-Review

**Spec coverage:** Intent/Goals — Tasks 5–6 (panel, funnel, badge, enable/disable/clear/apply, Ctrl+Enter, manual condition). Non-goals — honored (only filter/sort UI in Task 5; no `/query` change in Task 6 except call sites; no BETWEEN/IN in Task 1; no persistence in Tasks 4/6; no fallback in Task 6 Step 4). Approaches — B implemented. Backend section — Tasks 1–3 (records, builder, identifiers, LIKE `!`, bool-first tinyint, decimal validation, datetime-as-string binding, left-associative fold, manual `AND (...)`, inline LIMIT/OFFSET, SqlSafety defense-in-depth check, schema validation with unavailable→skip, `QueryCacheKey.For` parameter-aware cache key preserving today's snapshots, rows+total single response, 400/502/Redis/read-only-allowed behaviour, `db.browse` audit). Frontend section — Tasks 4–6 (DbTab state stripped by persistableTab, filter-model ops metadata/validation/cycle/badge, panel layout, header cycle single-column vs panel multi-column, wire body, no-URL persistence, 404 toast). Testing sections — Tasks 1–4 unit/integration tests + Task 8 e2e checklist (incl. `tsc --noEmit`, full dotnet/vitest suites). Phasing — tasks map 1:1 to spec phases (1+2=spec phases 1–2 Core/endpoint; 4–6=phase 3; 8=phase 4; spec phase 2's sorting-standard exception entry = Task 7). Calls I made — encoded as Global Constraints (order-not-direction, carve-out, 400-not-fallback, bool-first, inline LIMIT/OFFSET, empty-string legal, schema-unavailable skip, single-column header sort, no BETWEEN/IN, no persistence, no fallback, db.browse).

**Step scan:** every code step carries the exact signature/file; test steps name exact test methods and commands. Two steps intentionally carry no test first (Task 5 Step 1 — presentational; Task 8 — verification checklist) and say why.

**Type consistency:** `BrowseFilter`/`BrowseSort` (Task 1 .NET records) ↔ `BrowseFilter`/`BrowseSort` (Task 4 TS interfaces, same field names) ↔ wire DTO `BrowseFilterDto`/`BrowseSortDto` (column/op/value/enabled/logic, column/order) ↔ `serializeBrowse` output. `BrowseResponse.total` is `number | null` TS ↔ `long Total` C#. `QueryCacheKey.For` used identically in Tasks 2 and 3. `DbTab.browseFilters/browseSort/manualCondition` match the panels' props and the DTO fields.

**Review Focus:** all five items are pinned to tasks with tests named above.

**Proportion:** the plan is roughly the size of the spec, not a transcript of the code — bodies appear only in tiny, format-pinning DTOs and the cache-key serialization rule, both of which the answer genuinely depends on.
