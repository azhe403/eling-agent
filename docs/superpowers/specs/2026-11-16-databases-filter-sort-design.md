# Databases Data tab: filter & sort (condition builder)

**Date:** 2026-11-16
**Status:** proposed, pending user approval
**Area:** `src/backend/Eling.Core` (new browse query layer), `src/backend/Eling.Backend` (endpoint), `src/frontend/Eling.Dashboard` (`/dashboard/databases` Data tab)

## Intent

The Data tab of `/dashboard/databases` shows a table's rows with pagination and inline
editing, but has no way to filter or sort them. The teams that maintain mst_* reference
tables (e.g. `mst_rate`) filter them constantly in external tools (the reference is
DbGate's filter panel). This adds the same idea to the Data tab: a collapsible
condition builder - enabled/disabled condition rows over a column, an operator and a
value, joined with AND/OR, plus sort clauses and a raw "manual condition" escape
hatch - with the SQL assembled and executed **server-side**.

## Goals

- Data tab gets a collapsible filter panel, toggled by a funnel button in the grid's
  toolbar row, with an active-filter count badge on that button.
- Two kinds of rows:
  - **Condition rows**: enable checkbox, logic dropdown (`First` on the first row;
    `And (AND)` / `Or (OR)` afterwards), column dropdown, operator dropdown (filtered
    by the column's type), value input, remove button.
  - **Sort rows**: column dropdown + order dropdown (asc/desc), remove button.
- Toolbar buttons: **Apply** (commit drafts to the tab and refetch), plus
  **Enable all / Disable all / Clear all** operating on the drafts; **Add condition** /
  **Add sort** append rows.
- **Manual query condition** text input, ANDed verbatim when non-blank, with its own
  clear affordance inside the panel. Raw SQL by design (the same trust level as the
  SQL tab and `/query` - the operator owns the connection).
- Server-side SQL assembly in a new endpoint
  `POST /api/databases/{id}/browse` taking structured `filters`/`sort`/`page`/`limit`,
  validating identifiers against the table's schema, binding values as parameters,
  returning rows **and** total in one response.
- The grid header cycles sort (asc → desc → none) on click (single-column via header;
  multi-column via the panel). Columns carrying active filters show a funnel marker.
- Applying a filter resets the tab to page 1. Enabling/disabling rows or editing
  values in the panel does not refetch until Apply (no double fetches while typing).
- The offline cached-read behavior of today's browse is preserved (see Caching).

## Non-goals

- None of the other DbGate toolbar icons (pin, upload/download, trash, expand, …).
  Only the filter panel and grid-header sort.
- No changes to `/query`, MCP `db_query`, or any other raw-SQL surface.
- No BETWEEN / IN operators in v1 (the manual condition covers them). No nested
  condition groups or a parenthesis builder - only flat rows with per-row AND/OR.
- No persistence across reload: committed filter/sort state is session view state on
  the tab, like `browseLimit`/`browsePage`. It never reaches localStorage (a stale
  filter must not resurrect on every open).
- No per-column dropdown editors in the header - the header shows sort/filter *state*
  only; editing stays in the panel.
- No MCP tool for browse in v1 (the endpoint shape must not preclude one later).
- No fallback to the old client-built raw SQL against an older backend: the feature
  surfaces a toast error instead. Both halves ship in this repo.

## Approaches considered

| | A. Client-side assembly | B. Server-side structured endpoint |
|---|---|---|
| Where SQL is built | new pure TS helper, sent to `/query` | new `/browse` endpoint; builder in C# |
| Binding | escaped literals by hand | real parameter binding (per-provider) |
| Column validation | none (typo → DB error / silent cached miss) | validated against schema; 400 naming the column |
| Work | ~1 file + tests | Core builder + endpoint + adapter/read-service parameter support + UI |
| Offline cache | unchanged | preserved, now including params in the cache key |
| Reuse by MCP/other tools | no | yes |

**Chosen: B** (user decision, 2026-11-16). A was the cheaper path and consistent with
today's client-built LIMIT/OFFSET, but B gets parameter binding and schema validation,
which is what "filter server side" was asked for.

Supporting pieces of B that are unavoidable: `IDatabaseAdapter.QueryAsync` and
`IDatabaseReadService.QueryAsync` gain an optional parameters argument (default
`null`, so existing call sites compile unchanged), and the read cache folds parameter
values into its query key when present.

## Relation to `docs/superpowers/standards/sorting-standard.md`

The 2026-11-04 sorting standard applies to **list panels** (`GET
/api/databases?sort=&order=`, dashboard workspaces): server sorts, `sort`/`order`
query param names (`direction` forbidden), one sort key, unknown values fall back to
default, persistence URL → localStorage → default, and new endpoints follow it from
birth. The Data tab grid is a **paged data view**, not a list panel - it already
keeps `browseLimit`/`browsePage` as per-tab session state for the same reason, and a
filter carries raw literal values (ids, names) that must not persist in a URL or
localStorage. The standard is therefore **scoped to list panels** by this spec, and
`/browse` is a documented exception to it (user decision, 2026-11-16).

What `/browse` takes from the standard anyway: server-side sort, the property name
`order` (never `direction`), values `asc`/`desc`.

What it deliberately does not take: GET + query params (the multi-column panel sort
and the filter payload do not belong in a URL), the URL → storage → default
persistence chain (per-tab session state like limit/page), single sort only (the
panel sorts by any number of columns, header cycle stays single-column), and
fallback-not-400 for unknown values (a wrong column/op is a builder/client bug, not a
user preference; it answers 400 naming the offender).

Implementation includes adding a one-line exception entry to the standard's adoption
table (endpoint `POST /api/databases/{id}/browse`, scope "paged grid browse", shape
"POST body, multi-column, per-tab session state"). The standard's list-panel rules
are otherwise untouched.

## Design

### Backend

**New records** in `Eling.Core/Database` (e.g. `Browse/BrowseModels.cs`):
`BrowseFilter { string Column; string Op; string? Value; bool Enabled; string Logic }`,
`BrowseSort { string Column; string Order }`,
`SqlParam(string Name, object? Value)`. `Op` and `Order` are validated against
fixed whitelists, never passed through.

**New pure builder** `BrowseQueryBuilder` (no I/O): given the table name, the
provider (`Sqlite`|`MySql`), the resolved column types (or none, see Validation), the
request, and the page/limit, it produces:

- `rows` SQL: `SELECT * FROM <q table> [WHERE <fold> [AND (<manual>)]] [ORDER BY <q col> <order>, ...] LIMIT <n> OFFSET <n>` - limit/offset inlined (see the LIMIT/OFFSET bullet under Semantics for why).
- `count` SQL: `SELECT COUNT(*) FROM <q table> [WHERE same fold]` with the **same**
  parameters.

Semantics:

- Only `Enabled` filters participate. `First`/`And`/`Or` is the row's `Logic`; the
  first enabled row's logic is ignored. **Left-associative folding**: the WHERE is
  `(((a <op1> b) <op2> c) ...)`. No SQL precedence surprises; documented in a tooltip
  on the logic dropdown.
- `IsNull`/`IsNotNull` take no value and emit `col IS NULL` / `col IS NOT NULL`.
- String-typed columns get: `eq, neq, contains, notContains, startsWith, endsWith`
  (+ isNull/isNotNull). Numeric/bool/datetime get: `eq (=), neq, gt, gte, lt, lte`
  (+ isNull/isNotNull). The UI only offers ops valid for the column's declared type,
  and the server re-checks the op whitelist but not the op↔type pairing (a text
  column with `gt` is passed through - it is valid SQL, just unusual).
- `contains`/`startsWith`/`endsWith`/`notContains` emit `col LIKE @pN ESCAPE '!'` with
  `!` escaped first, then `%` and `_`. `!` is used as the escape character because it
  is a legal single-character ESCAPE operand for both MySQL (including
  `NO_BACKSLASH_ESCAPES`) and SQLite, with no backslash parsing anywhere.
- Bool-ish columns are `tinyint` only, via the bool-first rule in the numeric
  bullet above (one coercion rule per column type, no second pathway).
- Numeric columns (`int|bigint|smallint|tinyint|decimal|float|double|numeric|real`):
  the value must parse as an invariant-culture decimal, else **400**
  (`column 'x' is numeric; 'abc' is not a number`). Bound as that decimal.
  **Exception, `tinyint` only, bool-first:** `'true'/'false'/'1'/'0'`
  (case-insensitive) map to 1/0 *before* the decimal parse, so
  `is_active = 'true'` does the obvious thing on the mst_* flag tables this
  feature targets. Anything else falls through to the decimal parse.
- Datetime columns: value bound as string (MySQL coerces `datetime` comparisons
  against ISO strings including fractional seconds and `'YYYY-MM-DD HH:MM:SS'`;
  SQLite compares ISO text lexicographically, which is correct for ISO-8601).
- All other columns: value bound as string. Values are **not** trimmed on the server
  (a value with meaningful spaces is legal); the client trims drafts for display
  purposes only... no - nothing is trimmed anywhere except numeric parsing. Empty
  string is a legal equality/LIKE operand.
- Identifiers are always quoted: MySQL backticks with `` ` `` doubled; SQLite double
  quotes with `"` doubled. No caller input reaches the SQL text in any other form.
- `ManualCondition`, when non-blank, is appended as `AND (<raw>)`. Audit logs it
  verbatim, like `/query` does today.
- `LIMIT`/`OFFSET` are **inlined** integers (clamped), never parameters —
  identifiers and limit/offset carry no caller text, so there is nothing to bind,
  and today's cached snapshots (keyed on the SQL string) stay valid, including
  for unfiltered browse.
- Page ≥ 1, limit clamped to 1..1000. `total` from the count statement.
- The built rows SQL is run through `SqlSafety.Classify` as defense in depth;
  anything not `ReadOnly` is a 500 (it cannot happen, and its occurrence means a bug,
  not user input).

**Validation against schema.** The endpoint calls
`readService.GetSchemaAsync(row, database, ct, preferCache: true)` and requires the
table to exist (400 `table 'x' not found` otherwise) and each filtered/sorted column
to exist (400 `column 'x' not found`). **If the schema read throws**
(`DatabaseUnavailableException` - DB down and no cached schema), column validation is
skipped and only identifier quoting protects the statement. This keeps the offline
cached-browse path alive: a stale snapshot must still be viewable with a filter
applied. identifiers remain injection-safe (quoted with doubling); a typo'd column
just yields the same 502-with-empty-cache as today.

**Caching.** `DatabaseReadService.QueryAsync` gains
`IReadOnlyList<SqlParam>? parameters = null` (optional, existing call sites compile
unchanged). When parameters are present the cache key becomes
`sql + '\n--' + params-serialized` (`n:value` with type tag + invariant-culture
formatting, unit-separator-joined) instead of `sql` alone; without parameters the
key is unchanged. The browse endpoint issues rows + count statements through this
path with `preferCache: !req.Refresh` exactly like `/query` does today.
`fromCache` in the response is `rowsResult.FromCache || countResult.FromCache`,
`cachedAt` the later of the two stamps. Both statements stay keyed separately; when
offline they are either both cached or the endpoint 502s, same as today.

**Response**: `{ columns: string[], rows: unknown[][], truncated: bool,
total: number | null, fromCache: bool, cachedAt: string | null }`. HTTP 200 even
when `truncated` (the client already shows "Truncated"). `total` is always a number
unless the count statement fails; in that case the count failure downgrades the whole
call to 502 (failing half a response is worse than none).

**Endpoint**: `POST /api/databases/{id}/browse` in `DatabaseEndpoints.cs`, request DTO
in `DatabaseDtos.cs`:

```json
{
  "table": "mst_rate",
  "database": "dev_reference",           // optional; null for the connection's own db
  "page": 1,
  "limit": 100,
  "filters": [{ "column": "name", "op": "contains", "value": "single", "enabled": true, "logic": "first" }],
  "sort": [{ "column": "updated_at", "order": "desc" }],
  "manualCondition": null                 // or "is_active = 1" - ANDed verbatim
}
```

`400` on: missing table; unknown op/sort-order value; unknown table or column (when schema
readable); unparseable numeric/bool value. `page` and `limit` are **clamped**, never 400 -
`page` >= 1 and `limit` 1..1000; an out-of-range page falls back to the same clamp the
grid already performs.
`502` on: DB unreachable **and** no cached snapshot for either statement. Redis
connections get the same `RedisRequirement.NotSql` 400 as `/query`. The `/browse`
route is a read - allowed on read-only connections, like `/query`.

**Audit**: new const `DatabaseAuditActions.Browse = "db.browse"` (same spelling style as
the existing `db.mutate` compatibility entry). Recorded with `Trail.Access`,
`Category.Database`, `Target = id`, `Query` = table name plus the folded WHERE text
(built by the serializer, not reparsed), `ResultCount = total`, `DurationMs`,
`FromCache`, plus `Outcome` on every path (success / denied / error), exactly like
`/query` does now.

**Adapters**: `IDatabaseAdapter.QueryAsync` gains
`IReadOnlyList<SqlParam>? parameters = null`. MySql maps to `MySqlParameter`, SQLite
to `SqliteParameter` (`@p0..` placeholders - the builder names them so). Placeholders
in the SQL text are `@pN` for both providers; quoting style is provider-specific.

### Frontend

**State.** `DbTab` gains three session-state fields: `browseFilters?: BrowseFilter[]`,
`browseSort?: BrowseSort[]`, `manualCondition?: string` - session view state exactly
like `browseLimit`/`browsePage`, stripped by `persistableTab`, never persisted.
`browseData` in `useTableData` reads them from the tab at request time (same ref
pattern as limit/page) and sends them to `/{id}/browse`. Applying a filter sets
`browsePage` to 1. The two-fetch (`SELECT * ...` + `COUNT(*)`) pattern in
`useTableData` disappears - one call answers both - and the page-clamp logic consumes
the server's `total` unchanged in shape (it already takes a `countedTotal`).

**`filter-model.ts`** (new, pure, unit-tested): the TS mirrors of `BrowseFilter`/
`BrowseSort`; operator metadata per column kind (`text` / `numeric` / `datetime` /
`boolean`, derived from `ColumnSchema.dataType`); `opsFor(kind)`; draft validation
(value required? - no; empty string is legal; numeric columns must parse); sort
cycle helper (none → asc → desc → none, header clicks clear other columns to a
single-column sort; the panel is the only multi-column path); active-filter badge
count (enabled conditions + non-blank manual condition); wire serialization; and the
left-associative fold for the "where preview" shown in the panel footer (see UI).

**`FilterPanel.tsx`** (new component): collapsible block inside the Data tab's card,
between the toolbar row and the grid. Contents, matching the reference screenshot
but in the dashboard's look (shadcn `Select`, `Checkbox`, `Button`, `Input`):

```
Manual query condition  [ ________________________________ ]  (cleared by its own Clear link; ANDed verbatim, raw SQL; error from DB surfaces as the grid's error toast as today)

[✓] [First   ▾] [name        ▾] [Contains ▾] [single       ] [×]
[✓] [And (AND)▾] [updated_at  ▾] [=         ▾] [2025-02-19  ] [×]
[✓] [Ascending▾] [created_at  ▾]                             [×]
[+ Add condition] [+ Add sort]

                                  [Enable all] [Disable all] [Clear all] [Apply]
```

- Disabled rows render dimmed. The value input spans full width; the row wraps on
  narrow widths. The logic dropdown on row 1 is pinned to `First` and disabled.
- **Apply** commits the draft (drops nothing - empty string is a value; blank rows
  stay, they are legal) to the tab and refetches; `Ctrl+Enter` inside any input does
  the same. **Clear all** empties drafts and commits the empty state. `Enable all` /
  `Disable all` flip every checkbox. `Add sort` on a sorted column is a no-op (the
  column is already listed).
- Draft state is local to the panel, initialised from the tab's committed state every
  time the panel opens; **unsaved edits are lost when the panel closes** (close =
  discard, not apply). Applying also resets `browsePage` to 1.
- Funnel button in the DataGrid toolbar row (after Refresh): toggles the panel,
  shows a badge with the committed active-condition count (0 = no badge), goes
  solid/`secondary` styling while conditions are active.

**Grid header**: shows the sort arrow (▲ asc / ▼ desc) after the column name when
sorted; click cycles none → asc → desc → none as a **single-column** sort (it
replaces the panel's sort list, or is replaced by it when the panel applies multiple
sorts - one pipeline, last Apply wins). Filtered columns get a small funnel glyph
after the name. No per-header editors in v1.

**Wire**: `apiFetch<BrowseResponse>(API + "/" + id + "/browse", { method: "POST", ...body: { table, database, page, limit, filters, sort, manualCondition } })`.
A 404 here means the backend predates the endpoint - the error toast says exactly
that (`Backend does not support /browse; rebuild and restart it.`).

**DataGrid props**: gains `browseFilters`, `browseSort`, `manualCondition`,
`onApplyBrowse(filters, sort, manual)`, `onSortColumn(column, order | null)`
(header-cycle callback; replaces the panel's sort list with the single column, null
clears it). All interactive controls get `cursor-pointer` (repo convention).

**`Databases.tsx` / `ExplorerPanels`**: wire the three new tab fields and the two
callbacks through to `DataGrid`; `persistableTab` strips them; the
`useExplorerTabs`/`db-tabs.ts` open-table path sets them to undefined (default)
for new tabs. No URL sync (session state), no localStorage.

### Semantics worth restating

- Disabled rows never participate. Left-associative fold for mixed AND/OR.
- Manual condition ANDs last: `WHERE (<fold>) AND (<manual>)`.
- Apply resets to page 1. Count and clamp see the filtered total.
- Inline cell editing and row insert are unaffected: they locate rows by PK and do
  not depend on filters; edits made while a filter hides the row simply refetch into
  the filtered view as today.

## Testing

**Core.Tests (`BrowseQueryBuilderTests`)**: every operator on string/numeric/datetime/
bool columns; folding with mixed AND/OR over 3 rows (`((a AND b) OR c)` and
`((a OR b) AND c)`); LIKE escaping of `%`, `_`, `!`; isNull/isNotNull emit no param;
numeric value validation accept/reject; bool mapping; multi-column sort emission and
sort-order whitelist rejection; identifier quoting with embedded backtick/quote
(doubling); page/offset math; rows-WHERE and count-WHERE are identical strings;
manual condition parenthesised and ANDed; SqlSafety classifies the builder output as
ReadOnly.

**Backend.Tests**: endpoint with stubbed read service - Redis connection → 400;
unknown table → 400 (list the schema tables in nothing); unknown column → 400; bad
numeric → 400; page/limit clamps; happy path returns rows+total+fromCache=false;
offline fake raising `DatabaseUnavailableException` with a primed cache → 200 with
`fromCache: true`; audit action is `db.browse`.

**Frontend vitest (`filter-model.test.ts`)**: `opsFor` per type; value validation per
type; sort cycle; badge count (enabled-only + manual); wire serialization; empty
filters array round-trips; persistableTab strips the three new fields.

**Manual/e2e (repo rule)**: open the dev dev-server 4427 against the local backend
(4417 under `dotnet watch`). Pick the `mst_rate` table in `dev_reference` if a
registered dev connection exposes it (it is the table from the reference
screenshot); otherwise pick any comparable mst_* table the registered dev
connection exposes. Apply `name contains 'Standard'`, verify grid + `Total rows`
+ pagination clamp, toggle a condition off without Apply (no refetch), Apply,
sort by `updated_at` desc via header and via panel, and Clear all.
`pnpm --filter eling-dashboard exec tsc --noEmit` for the
frontend; `dotnet build` + targeted `dotnet test --filter` for the backend.
`pnpm build` is not run while a dev server is on 4427 (AGENTS.md).

## Phasing

1. Core builder + records + read-service/adapter parameter plumbing + unit tests.
2. Endpoint + DTO + audit const + endpoint tests + the one-line exception entry in
   `docs/superpowers/standards/sorting-standard.md`'s adoption table.
3. Frontend: model → panel → grid header + toolbar wiring → Databases/ExplorerPanels
   plumbing; vitest green; `tsc --noEmit`.
4. Browser-panel verification on the dev connection's mst_* table (see Manual/e2e);
   fix what shows.
Each phase is its own commit(s) per the repo's one-context-per-commit convention.

## Calls I made (check these)

- **One endpoint returning rows+total** instead of two `/browse` calls — halves
  round-trips and gives one consistent cache snapshot when offline.
- **Left-associative fold** for AND/OR (no SQL precedence surprises, no nested groups).
- **`!` as the LIKE escape character** — one operand, legal on both providers, no
  backslash anywhere (including `NO_BACKSLASH_ESCAPES` mode).
- **Empty string is a legal filter value** (UI trims only for display of drafts,
  server binds as-is; `''` equality stays possible).
- **Column/table validation is skipped when the schema itself is unreadable** — the
  offline cached path must survive a dead database; quoting still makes identifiers
  injection-free.
- **Header sort is single-column and replaces the panel's sort list**; multi-column
  sorting exists only via the panel.
- **No BETWEEN/IN in v1** - manual condition covers them; operator list stays small.
- **`/browse` is a documented carve-out from `docs/superpowers/standards/sorting-standard.md`** (user
  decision) - that standard scopes to list panels; a paged grid view keeps
  `browseLimit`/`browsePage`-style per-tab session state. `/browse` still honours
  the standard's property name (`order`, never `direction`) and asc/desc values, and
  edits the standard's adoption table to say so.
- **Unknown column/op/direction value answers 400, not the standard's fallback** -
  a silently-swapped sort key hides builder bugs; 400 names the offender.
- **`tinyint` coerces bool-first** - `true`/`false`/`1`/`0` map to 1/0 before the
  decimal parse, so `is_active = 'true'` works on the flag tables this targets; all
  other numeric types are decimal-only.
- **LIMIT/OFFSET are inlined, never parameters** - they carry no caller text, and
  today's cached snapshots (keyed on the SQL string) stay valid for unfiltered
  browse.
- **No localStorage/URL persistence** of filters - session state per tab, like
  `browseLimit`/`browsePage` (see the carve-out above for why the standard's
  persistence chain does not apply).
- **No old-backend fallback** — a 404 surfaces as a clear toast instead of a hidden
  second code path.
- **`db.browse` audit action**, Access trail, same conventions as `db_query`.
