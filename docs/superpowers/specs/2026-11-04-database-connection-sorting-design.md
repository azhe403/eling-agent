# Sorting for the dashboard database-connection list

Design for adding a sort control to `GET /api/databases` and the *Connections*
panel that consumes it.

**Standard:** `docs/sorting-standard.md` §1–§4 — `sort` + `order=asc|desc`, server sorts, restore chain URL → localStorage → `name`/`asc`. This spec is the first adopter; it adds nothing the standard does not already decide.

## Intent

The connections panel today renders `conns.map(...)` in whatever order the JSON file
hands back, so the tree order is registry order — the order connections happened to be
registered in. A reader gets no answer out of that. Sorting answers "where is the
connection I am looking for" and lets the hotspot (the one they use every day, or the
newest one they just added) sit at the top.

## Goals

- The Connections panel can be ordered by Name, Provider, Created, or Last used, in
  either direction, from a small fixed set of keys.
- The order is produced by the server: `GET /api/databases` accepts `?sort=` and
  `?order=` and returns the rows already ordered.
- The choice persists in the standard web way: URL first, then localStorage, then
  Name A→Z. A `?sort=` in the URL always happens to localStorage; when the URL
  carries none, the remembered value from localStorage is used; when neither
  carries one, the order is Name A→Z.
- Neither `sort` nor `order` is written to the URL when it equals the default — a
  shared link stays clean, exactly as workspaces does today.
- Sorting never reorders the tables or databases inside a connection.

## Non-goals

- No search or filter on the connection list.
- No grouping (by provider, by environment) and no tree layout change.
- No change to `db_list_connections` or to the MCP tools — they read the store
  directly and keep their own order.

## Constraints

- The existing segmented-control convention (`role="group"`, `aria-label`,
  `aria-pressed`, `flex rounded-lg border p-1`): panel position fixed by the 2026-10-21
  rule — arrangement switch directly under the panel header and above the list, never
  buried under the rows it rearranges.
- shadcn-style `Button` for every interactive control outside the tree rows; tree rows
  stay plain `<button>`s for the fixed-height tree.
- One top-level type per file, records chopped one parameter per line, no tuples, in
  `Eling.Core` and `Eling.Backend`; camelCase JSON.
- Conventional commits, one logical change per commit.
- Backend dev port 4417; frontend 4427. Do not run `pnpm build` while the frontend
  dev server is up.

## Approaches

**A. Server sorts, client remembers** (chosen). `GET /api/databases?sort=name&order=asc`
orders rows before mapping to `ToView`. The client reads the choice from the URL
first, then localStorage (the same `db.explorer.state` blob as the explorer state),
then the Name A→Z default, and fetches with it. Cost: two query params, one parse
helper with tests, and a backend endpoint test.

**B. Client sorts only.** Pure comparator in a new `conn-sorting.ts`, mirroring
`workspaces/sorting.ts`. Cheapest; rejected because the order rule would live in the
client while the list itself is server-shaped, and the same connections are read by
other surfaces (the MCP tool reads the store directly, so ordering there would need a
second implementation anyway).

**C. Server returns all orders.** One payload carrying the names, providers, created and
last-used orders, so toggling never re-requests. Rejected: the connection list is
small (`databases.json`) and a fetch is cheap, so a round trip per toggle is not a
problem worth complicating the contract for.

## Parameter naming standard

`sort` for the column, `order` for the direction — the universal pair (`ORDER BY name
ASC`). The workspaces page uses `sort` + `dir` today, and "dir" is ambiguous (readers
either say "directory" or stumble). This feature standardises on `sort` + `order`
with `asc` / `desc`; the workspaces page is ported to the same pair in a separate
change so they stop diverging.

## Decisions

- **Keys**: `name`, `provider`, `created`, `lastUsed` — verified against the
  `DatabaseConnection` record: `Name`, `Provider`, `CreatedAt`, `LastConnectedAt`
  (`DateTimeOffset?`). Environment was asked about and left out.
- **Natural order per key**, direction mirrors the whole list (the `asc` / `desc`
  pair from the parameter-naming standard, replacing the `natural` / `flipped`
  vocabulary used by workspaces, so both pages end up naming direction the same
  way). "Mirrors" means the mirror of the **column**, not of the whole list: the
  name tie-break stays A→Z in both directions, so `desc` is not the reverse of
  `asc`:
  - name — A→Z, case-insensitive, numbers read as numbers (`Alpha2` before `Alpha10`)
  - provider — the enum's own order (`Sqlite`, `MySql`, `Redis`), then name
  - created — newest first
  - lastUsed — most recently connected first
- **Missing readings stay last in both directions.** A connection that has never been
  connected (`LastConnectedAt` null) never claims the top of either direction —
  "no reading" is unknown, not zero. Never-connected rows order among themselves by
  the name tiebreak.
- **Tiebreak is always name A→Z**, whatever the direction, so equal keys never depend
  on registry order and the reversed list is the mirror of the forward one only
  within one direction.
- **Sorting happens in Core.** One comparator (`DatabaseConnectionComparer` /
  `DatabaseConnectionSort`) in `Eling.Core.Database`, used by the endpoint; this is
  where a future surface (MCP, desktop settings) would share it rather than duplicate
  it. The dotted-path lesson test target is `Eling.Core.Tests`.
- **Invalid `sort`/`order` values fall back to the default** (`name`, `asc`) rather
  than answering 400, matching `parseSort`/`parseDirection` on the workspaces page
  (which fall back to `name` / `natural` for `dir` today, and to the same pair once
  it is ported to `order=asc`).
- **The control sits under the "Connections" header and above the list rows**. The
  panel's header row carries the connection count on the right, next to the
  "Connections" label, so the count is what the panel is about.
- **Client state**: `sort` and `order` live in `db.explorer.state` next to
  `expandedConns`/`openTabs`. Priority on restore: `?sort=` / `?order=` in the URL,
  then the remembered pair in localStorage, then `name` A→Z. `load()` appends them
  to the fetch, and the URL is updated (via `router.replace`) only when the pair
  differs from the default, so shared links stay clean.
- **No tests for `Databases.tsx` itself.** No component test exists for this page;
  behaviour is verified in the browser panel (user rule), not by a harness.

## Implementation notes for the builder

- `DatabaseEndpoints.cs` line 17: `group.MapGet("/")` — add `string? sort, string? order`
  parameters, parse via the Core helper, order `rows` before `ToView`. Audit event
  unchanged (same `Action`, `ResultCount` from `rows.Count`); the query string is not
  recorded — no caller input is reflected.
- `ToView` already exposes `provider`, `createdAt`, `lastConnectedAt` as camelCase, so
  the client needs no new field.
- `Databases.tsx` line 274: `conns` state; `load()` around lines 614–626 (fetch
  `API` plain today — append `?sort=&order=` here from the resolved pair);
  explorer-state restore at lines 642–730 (reads `URLSearchParams` at 648, then
  `db.explorer.state` at 653 — the `sort`/`order` resolution follows the same
  URL-then-localStorage shape, after the existing `connectionId`/`tab` reads);
  explorer-state persist at lines 757–775 (adds `sort`/`order` to the blob and to
  the URL params, mirroring the existing `connectionId`/`table` conditional writes);
  list render at 1804–1895 (segmented control under the header, then the rows).
- Frontend test infra: vitest under `src/frontend/Eling.Dashboard`, run with
  `pnpm --prefix src/frontend/Eling.Dashboard test`; type-check with
  `pnpm --filter eling-dashboard exec tsc --noEmit`.
- Backend test harness to copy: `tests/Eling.Backend.Tests/DatabaseConnectionErrorTests.cs`
  (`WebApplication.CreateSlimBuilder` + `UseTestServer` + `JsonDatabaseConnectionStore`
  on a temp file).
- Core test style to follow: `src/frontend/Eling.Dashboard/src/app/dashboard/workspaces/sorting.test.ts`
  for the two-direction/missing-reading/tiebreak cases; on the Core side use the
  existing `Eling.Core.Tests` naming convention for the database area.
- Verified live afterwards with the OpenChamber browser panel: open
  `http://localhost:4427/dashboard/databases`, pick each key, flip direction, reload
  (order survives from localStorage), revisit the shared URL (the `?sort=` in the URL
  wins over localStorage), and clear both URL param and localStorage (Name A→Z
  returns).
