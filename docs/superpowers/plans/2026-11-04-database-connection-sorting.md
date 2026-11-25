# Database Connection List Sorting — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** The dashboard's *Connections* panel can be ordered by Name, Provider, Created, or Last used, in either direction, with the order produced by the server and the choice remembered by the client.

**Architecture:** One sort vocabulary in `Eling.Core` (`DatabaseConnectionSort` + `SortOrder`) parses the wire values and orders the rows; `GET /api/databases?sort=&order=` applies it before mapping to the view, so the client never re-sorts. The dashboard resolves the pair from URL → localStorage → default, renders a segmented control under the *Connections* header, and sends the pair with every fetch.

**Tech Stack:** .NET 10 (ASP.NET Core minimal API), `Eling.Core`, xUnit. Dashboard: Next.js 16, React 19, TypeScript, Tailwind, shadcn-style components from `@/components/ui`, vitest.

**Spec:** `docs/superpowers/specs/2026-11-04-database-connection-sorting-design.md`
**Standard:** `docs/sorting-standard.md` (§1–§4 are binding: `sort` + `order=asc|desc`, server sorts, restore chain URL → localStorage → `name`/`asc`)

## Global Constraints

- Read-only. The sort only reorders rows; it writes nothing and changes no connection.
- Query params are exactly `sort` and `order`. `order` is `asc` or `desc`. Never `dir`/`direction`.
- Default pair: `sort=name`, `order=asc` → Name A→Z.
- An unknown `sort` or `order` value falls back to the default. It never answers 400.
- A row with no reading for the chosen column (`LastConnectedAt` null) stays last in **both** directions.
- The tie-break is always Name A→Z, whatever the direction, so the order never depends on registry/file order.
- `asc` = the column's natural order (name A→Z; `created`/`lastUsed` newest first); `desc` = the mirror of it.
- One top-level type per file; records chopped one parameter per line; no tuples (`CodeOrganizationConventionTests`).
- JSON is camelCase (`sort`, `order` are query params, not JSON).
- Conventional commits, one logical change per commit.
- Never run `pnpm build` / `next build` while the frontend dev server is up on 4427 — it overwrites `.next/` and deadlocks Turbopack on Windows. Type-check with `pnpm typecheck:frontend` instead.
- Backend dev server runs on 4417; when it runs under `dotnet watch`, do not kill it to verify — build to a separate output directory.
- Backend test layers: `pwsh scripts/test-backend.ps1 -Layer core` (Core), `-Layer api` (Backend), `-Layer all` (all three). Each run uses a disposable output root the script deletes when the run passes, so it never collides with the dev server's output.
- Frontend: `pnpm --prefix src/frontend/Eling.Dashboard test` (vitest), `pnpm typecheck:frontend`, `pnpm lint:frontend`.
- The pre-commit hook runs the layer a change touches: a Core change runs the Core tests, a Backend change runs Core + API, a dashboard change runs lint + typecheck only, a docs-only change runs nothing.

## Review Focus

- **A connection that has never been connected** (`lastConnectedAt: null`). It must not climb to the top under `order=desc` either — "no reading" is unknown, not zero.
- **Two connections with the same name.** The tie-break must be deterministic, and `desc` must not be a plain reverse of the `asc` list (the name tie-break stays A→Z in both directions, so `desc` is the mirror of the *column*, not of the whole list).
- **`?sort=bogus` / `?order=sideways`.** The list must still answer 200, in the default order — not 400, and not an empty list.
- **`sort=provider`.** The provider order is the enum's own order (`Sqlite`, `MySql`, `Redis`), and two connections of the same provider must fall back to the name tie-break, not to registry order.
- **A shared URL with `?sort=created&order=desc`.** It must beat the localStorage value, not merge with it.
- **A reload with no `?sort=` in the URL.** The remembered localStorage pair must come back; clearing both must return Name A→Z.
- **The `created`/`lastUsed` columns have "newest first" as their natural order.** `asc` therefore means *newest first* for those columns — the same `asc` word reading as "A→Z" for names and "newest first" for dates is the intended semantics of the standard, not a bug.

---

### Task 1: The sort vocabulary in Core

**Files:**
- Create: `src/backend/Eling.Core/Database/SortOrder.cs`
- Create: `src/backend/Eling.Core/Database/DatabaseConnectionSort.cs`
- Test: `tests/Eling.Core.Tests/DatabaseConnectionSortTests.cs`

**Interfaces:**
- Consumes: `Eling.Core.Database.DatabaseConnection` (`Id`, `Name`, `Provider`, `CreatedAt`, `LastConnectedAt`), `Eling.Core.Database.DatabaseProvider`.
- Produces:
  - `public enum SortOrder { Ascending = 0, Descending = 1 }` in `src/backend/Eling.Core/Database/SortOrder.cs`.
  - `public static class DatabaseConnectionSort` in `src/backend/Eling.Core/Database/DatabaseConnectionSort.cs` with:
    - `public const string DefaultKey = "name";`
    - `public const string NameKey = "name";`
    - `public const string ProviderKey = "provider";`
    - `public const string CreatedKey = "created";`
    - `public const string LastUsedKey = "lastUsed";`
    - `public static bool TryParseKey(string? value, out string key)` — case-insensitive; true only for the four known keys; on false, `key` is `DefaultKey`.
    - `public static bool TryParseOrder(string? value, out SortOrder order)` — case-insensitive; `asc`/`ascending` → `Ascending`, `desc`/`descending` → `Descending`; on false, `order` is `Ascending`.
    - `public static IReadOnlyList<DatabaseConnection> Apply(IReadOnlyList<DatabaseConnection> rows, string key, SortOrder order)` — returns a new sorted list; the input is never mutated; an unknown `key` sorts by name.
  - Key comparison rules: `name` — case-insensitive, numeric-aware (`Alpha2` before `Alpha10`), A→Z; `provider` — the enum's own order (`Sqlite`, `MySql`, `Redis`); `created` — newest first; `lastUsed` — most recent first, nulls last in both directions.

- [ ] **Step 1: Write the failing test** in `tests/Eling.Core.Tests/DatabaseConnectionSortTests.cs` (xUnit, `namespace Eling.Core.Tests;`, `using Eling.Core.Database;`):

  A helper that builds a connection:

  ```csharp
  private static DatabaseConnection Conn(
      string name,
      DatabaseProvider provider = DatabaseProvider.MySql,
      DateTimeOffset? createdAt = null,
      DateTimeOffset? lastConnectedAt = null) => new(
      "id-" + name,
      name,
      provider,
      "Server=localhost;Database=db;Uid=root;Pwd=pw",
      DatabaseEnvironment.ReadOnly,
      false,
      true,
      createdAt ?? new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
      lastConnectedAt);
  ```

  and a helper that reads the names back:

  ```csharp
  private static List<string> Names(IReadOnlyList<DatabaseConnection> rows) =>
      rows.Select(r => r.Name).ToList();
  ```

  Cases:
  - `TryParseKey_Null_IsRefused` — `TryParseKey(null, out var key)` is false and `key == "name"`.
  - `TryParseKey_Unknown_IsRefused` — `"bogus"` is false, `key == "name"`.
  - `TryParseKey_Known_IsAccepted` — `"name"`, `"provider"`, `"created"`, `"lastUsed"` are true; the key is returned as the canonical spelling (`"lastUsed"` for `"lastused"` too, since the comparison is case-insensitive but the returned key is the canonical one).
  - `TryParseOrder_Null_IsRefused` — false, `order == SortOrder.Ascending`.
  - `TryParseOrder_Unknown_IsRefused` — `"sideways"` is false, `order == SortOrder.Ascending`.
  - `TryParseOrder_Accepted` — `"asc"`, `"ascending"`, `"desc"`, `"descending"` parse to the right values.
  - `Apply_ByName_OrdersCaseInsensitivelyAndReadsNumbersAsNumbers` — input `beta`, `alpha`, `Alpha2`, `Alpha10` → `alpha`, `Alpha2`, `Alpha10`, `beta`.
  - `Apply_ByName_Descending_MirrorsTheColumn` — the same input with `Descending` → `beta`, `Alpha10`, `Alpha2`, `alpha`.
  - `Apply_ByProvider_UsesTheEnumOrder_ThenTheName` — input `z-mysql` (MySql), `a-sqlite` (Sqlite), `b-redis` (Redis) → `a-sqlite`, `z-mysql`, `b-redis` (the enum's own order is `Sqlite`, `MySql`, `Redis`); a second connection named `a-mysql` (MySql) sorts before `z-mysql` by the name tie-break.
  - `Apply_ByProvider_Descending_MirrorsTheColumn` — the same rows with `Descending` → `b-redis`, `z-mysql`, `a-mysql`, `a-sqlite` (the provider order mirrors; within one provider the name tie-break still runs A→Z).
  - `Apply_ByCreated_PutsTheNewestFirst` — three rows with `createdAt` 2026-01-01, 2026-03-01, 2026-02-01 → newest first (`2026-03-01`, `2026-02-01`, `2026-01-01`).
  - `Apply_ByLastUsed_PutsTheMostRecentFirst` — three rows with `lastConnectedAt` 2026-01-01, 2026-03-01, 2026-02-01 → `2026-03-01`, `2026-02-01`, `2026-01-01`.
  - `Apply_ByLastUsed_KeepsNeverConnectedRowsLast_InBothDirections` — one row with `lastConnectedAt: null` and two with readings: `Ascending` → the two readings first (newest first), the null row last; `Descending` → the two readings first (oldest first), the null row still last.
  - `Apply_EqualKeys_FallBackToTheName_InBothDirections` — three rows sharing `createdAt`, named `charlie`, `bravo`, `alpha` → `alpha`, `bravo`, `charlie` under both `Ascending` and `Descending` (the tie-break does not flip with the direction).
  - `Apply_NeverMutatesTheInput` — call `Apply` on a two-row list, then assert the original list is unchanged.
  - `Apply_UnknownKey_SortsByName` — `Apply(rows, "bogus", Ascending)` returns the name order.

  Run: `pwsh scripts/test-backend.ps1 -Layer core` → the Core layer fails to compile (the types do not exist). That is the red step.

- [ ] **Step 2: Implement `SortOrder`** in `src/backend/Eling.Core/Database/SortOrder.cs`: a file-scoped namespace `namespace Eling.Core.Database;`, the two-member enum, with an XML doc comment stating that `Ascending` is the column's natural order and `Descending` mirrors it.

- [ ] **Step 3: Implement `DatabaseConnectionSort`** in `src/backend/Eling.Core/Database/DatabaseConnectionSort.cs`:

  - `TryParseKey`: `value?.Trim()` lower-cased, compared against `name`/`provider`/`created`/`lastused`; on a match, set `key` to the canonical spelling (`NameKey`, `ProviderKey`, `CreatedKey`, `LastUsedKey`) and return true; otherwise set `key = DefaultKey` and return false.
  - `TryParseOrder`: `value?.Trim()` lower-cased; `asc`/`ascending` → `SortOrder.Ascending`; `desc`/`descending` → `SortOrder.Descending`; otherwise `order = SortOrder.Ascending` and false.
  - `Apply`: copy the input into a `List<DatabaseConnection>`, then `Sort` with a comparison that (a) resolves the key's column value and whether the row has one, (b) puts a row without a reading after every row that has one, in both directions, (c) compares the two readings in the column's natural direction — newest-first for `created`/`lastUsed`, so `Ascending` puts the larger timestamp first and `Descending` the smaller — and (d) falls back to the name comparison when the column readings are equal. The name comparison is case-insensitive and numeric-aware: compare with `string.Compare(a, b, StringComparison.OrdinalIgnoreCase)` first and, when that says the strings are equal apart from case, compare with `StringComparer.OrdinalIgnoreCase` numeric-aware ordering so `Alpha2` precedes `Alpha10`. Never mutate the caller's list.

- [ ] **Step 4: Run the tests green.** `pwsh scripts/test-backend.ps1 -Layer core` → the Core layer passes.

  The script owns a disposable output root (`.bin-test-<pid>-<id>`) and deletes it when the run passes, so it never collides with the dev server's output. To run only the new tests, `dotnet test tests/Eling.Core.Tests/Eling.Core.Tests.csproj --filter FullyQualifiedName~DatabaseConnectionSortTests` works too — but then the output root is the shared one, so prefer the script.

- [ ] **Step 5: Commit.** `feat(core): add the database connection sort vocabulary`

---

### Task 2: The endpoint serves the sorted rows

**Files:**
- Modify: `src/backend/Eling.Backend/Endpoints/DatabaseEndpoints.cs` (the `group.MapGet("/")` handler, lines 17–34)
- Test: `tests/Eling.Backend.Tests/DatabaseConnectionSortEndpointTests.cs`

**Interfaces:**
- Consumes: `DatabaseConnectionSort.TryParseKey`, `DatabaseConnectionSort.TryParseOrder`, `DatabaseConnectionSort.Apply` (Task 1), `IDatabaseConnectionStore.ListAsync`, `IAuditLogger.RecordAsync`.
- Produces: `GET /api/databases?sort=&order=` → `200` with the connections **already ordered**. Absent, unknown, or blank `sort`/`order` → the default pair (`name`, `Ascending`). The response body is unchanged — the same `DatabaseConnectionView` array as today.

- [ ] **Step 1: Write the failing test** in `tests/Eling.Backend.Tests/DatabaseConnectionSortEndpointTests.cs`, using the same harness as `DatabaseConnectionErrorTests` (`WebApplication.CreateSlimBuilder`, `UseTestServer`, `JsonDatabaseConnectionStore` on a temp directory, `NullAuditLogger.Instance`, `builder.WebHost.UseTestServer()`, `_app.MapDatabaseEndpoints()`), with camelCase JSON and `JsonStringEnumConverter` configured the same way.

  A helper that registers a connection through the store and returns its id:

  ```csharp
  private async Task<string> Register(string name, DatabaseProvider provider, DateTimeOffset createdAt)
  {
      var connection = DatabaseConnection.Create(name, provider, "Server=localhost;Database=db;Uid=root;Pwd=pw", DatabaseEnvironment.ReadOnly, createdAt: createdAt);
      await _store.UpsertAsync(connection);
      return connection.Id;
  }
  ```

  A helper that reads the names out of the response:

  ```csharp
  private async Task<List<string>> NamesAsync(string query)
  {
      var response = await _client.GetAsync("/api/databases" + query);
      response.EnsureSuccessStatusCode();
      var body = await response.Content.ReadAsStringAsync();
      using var document = JsonDocument.Parse(body);
      return document.RootElement.EnumerateArray()
          .Select(e => e.GetProperty("name").GetString()!)
          .ToList();
  }
  ```

  Cases:
  - `List_WithoutParameters_AnswersTheDefaultNameOrder` — register `zebra` and `alpha` (different `createdAt`s) → `alpha`, `zebra`.
  - `List_WithSortNameAndAscending_AnswersTheNameOrder` — `?sort=name&order=asc` → `alpha`, `zebra`.
  - `List_WithSortCreated_AnswersTheNewestFirst` — `?sort=created` → the row created later first.
  - `List_WithSortLastUsed_AnswersTheMostRecentFirst` — `?sort=lastUsed` → the row connected most recently first.
  - `List_WithAnUnknownSort_FallsBackToTheDefault` — `?sort=bogus` → `200` and the name order (not 400, not empty).
  - `List_WithAnUnknownOrder_FallsBackToTheDefault` — `?order=sideways` → `200` and the name order.
  - `List_WithABlankSort_FallsBackToTheDefault` — `?sort=&order=` → `200` and the name order.
  - `List_WithoutParameters_KeepsTheExistingBodyShape` — the response is a JSON array and each element carries `name`, `provider`, `createdAt` and `lastConnectedAt`.

- [ ] **Step 2: Run the tests to see them fail.** `pwsh scripts/test-backend.ps1 -Layer api` → the ordering assertions fail (the endpoint still answers in registry order).

- [ ] **Step 3: Wire the parameters into the handler.** In `DatabaseEndpoints.cs`, change the list handler's signature to accept `string? sort` and `string? order`, parse them with `DatabaseConnectionSort.TryParseKey` / `TryParseOrder` (ignoring the boolean result, because an unknown value must fall back rather than fail), and apply `DatabaseConnectionSort.Apply(rows, key, direction)` before `rows.Select(ToView).ToList()`. The audit event is unchanged — same `Action`, same `ResultCount` — and the query string is not recorded, so no caller input is reflected into the audit trail.

- [ ] **Step 4: Run the tests green.** `pwsh scripts/test-backend.ps1 -Layer api` → the API layer passes.

- [ ] **Step 5: Run the whole backend suite.** `pwsh scripts/test-backend.ps1 -Layer all` → green across core, api and desktop, including `CodeOrganizationConventionTests`.

- [ ] **Step 6: Commit.** `feat(backend): serve the database connections in the requested order`

---

### Task 3: The dashboard reads the pair and remembers it

**Files:**
- Create: `src/frontend/Eling.Dashboard/src/app/dashboard/databases/conn-sorting.ts`
- Test: `src/frontend/Eling.Dashboard/src/app/dashboard/databases/conn-sorting.test.ts`
- Modify: `src/frontend/Eling.Dashboard/src/app/dashboard/databases/Databases.tsx` (state near line 274, `load()` at lines 614–626, the restore effect at 642–730, the persist effect at 821–846, the list render at 1803–1895)

**Interfaces:**
- Consumes: `GET /api/databases?sort=&order=` (Task 2).
- Produces — `conn-sorting.ts`:
  - `export const CONN_SORT_KEYS = ["name", "provider", "created", "lastUsed"] as const`
  - `export type ConnSortKey = (typeof CONN_SORT_KEYS)[number]`
  - `export const DEFAULT_CONN_SORT: ConnSortKey = "name"`
  - `export type ConnSortOrder = "asc" | "desc"`
  - `export const DEFAULT_CONN_ORDER: ConnSortOrder = "asc"`
  - `export const CONN_SORT_COPY: Record<ConnSortKey, { label: string; title: string }>` — labels `Name`, `Type`, `Created`, `Last used`; the titles explain each order.
  - `export const parseConnSort = (value: string | null | undefined): ConnSortKey` — one of the four keys, else `DEFAULT_CONN_SORT`.
  - `export const parseConnOrder = (value: string | null | undefined): ConnSortOrder` — `"desc"` → `"desc"`, anything else → `"asc"`.
  - `export const resolveConnSort = (opts: { urlSort?: string | null; urlOrder?: string | null; savedSort?: string | null; savedOrder?: string | null }): { sort: ConnSortKey; order: ConnSortOrder }` — the URL wins, then the saved pair, then the default. A URL that carries `sort` but no `order` uses `asc`; a saved pair with an unreadable sort falls back to the default sort **and** the saved order.
  - `export const connSortQuery = (sort: ConnSortKey, order: ConnSortOrder): string` — `?sort=&order=` when the pair differs from the default, `""` when it is the default (so a shared link stays clean).

- [ ] **Step 1: Write the failing tests** in `src/frontend/Eling.Dashboard/src/app/dashboard/databases/conn-sorting.test.ts` (vitest: `describe`/`it`/`expect`, imported from `"vitest"`):

  - `parseConnSort` returns `"name"` for `null`, `""` and `"bogus"`, and the key itself for `"provider"`, `"created"`, `"lastUsed"`.
  - `parseConnOrder` returns `"asc"` for `null`, `""` and `"sideways"`, and `"desc"` for `"desc"`.
  - `resolveConnSort` with a URL pair returns the URL pair.
  - `resolveConnSort` with no URL pair returns the saved pair.
  - `resolveConnSort` with a URL sort but no URL order returns that sort with `"asc"`.
  - `resolveConnSort` with a saved pair whose sort is unreadable returns the default sort and the saved order.
  - `resolveConnSort` with neither returns `"name"` / `"asc"`.
  - `connSortQuery("name", "asc")` is `""`.
  - `connSortQuery("created", "asc")` is `"?sort=created&order=asc"`.
  - `connSortQuery("name", "desc")` is `"?sort=name&order=desc"`.

  Run: `pnpm --prefix src/frontend/Eling.Dashboard test` → the module does not resolve. That is the red step.

- [ ] **Step 2: Implement `conn-sorting.ts`** with exactly the exports above and no others. `resolveConnSort` reads the URL sort first: if it parses to a known key, use it and take the order from the URL (defaulting to `"asc"`); otherwise fall back to the saved sort (parsed the same way, defaulting to `"name"`) with the saved order (`"desc"` only when it parses to it).

- [ ] **Step 3: Run the tests green.** `pnpm --prefix src/frontend/Eling.Dashboard test` → all pass.

- [ ] **Step 4: Resolve the pair in the dashboard.** In `Databases.tsx`:

  - Add the state next to the other explorer state: `const [connSort, setConnSort] = useState<ConnSortKey>(DEFAULT_CONN_SORT)` and `const [connOrder, setConnOrder] = useState<ConnSortOrder>(DEFAULT_CONN_ORDER)`.
  - **Resolve the pair in a `useState` lazy initializer, not in the restore effect.** The restore effect runs after the first `load()`, so resolving there would make the list fetch in the default order and then reorder itself — a visible glitch on every reload. The initializer reads `window.location.search` and `localStorage.getItem("db.explorer.state")` and calls `resolveConnSort({ urlSort, urlOrder, savedSort, savedOrder })`, so the very first fetch already carries the right pair. This is the React 19 lesson the workspace already learned: a `useState` lazy initializer is safe here because the page's client component renders inside a Suspense boundary.
    - The initializer must not read a state declared later in the component (the `react-hooks/immutability` lint rule fails it): declare `connSort`/`connOrder` **after** any state it reads, or read the URL and the localStorage blob directly inside the initializer rather than through another state variable.
  - In `load()`, append the pair to the request: `fetch(API + connSortQuery(connSort, connOrder), { cache: "no-store" })`. `load` is a `useCallback` — add `connSort` and `connOrder` to its dependency list, or the fetch keeps using the first pair.
  - In the persist effect (line 821), add `sort: connSort` and `order: connOrder` to the `db.explorer.state` blob, and add `sort`/`order` to the URL params only when the pair differs from the default — mirroring the existing conditional writes for `connectionId`/`table`. Add `connSort` and `connOrder` to that effect's dependency list.

- [ ] **Step 5: Draw the control.** In the *Connections* panel, between the `CardHeader` (line 1806) and the list (line 1813), render a segmented control:

  - The panel's header row shows the count on the right, next to the "Connections" label: `<CardHeader>` with a row holding the title and `<span className="text-xs text-muted-foreground">{conns.length}</span>`.
  - A `div` with `role="group"`, `aria-label="Sort connections"`, and the segmented shape `flex items-center gap-1 rounded-lg border bg-muted/40 p-1`.
  - One `Button` per key from `CONN_SORT_KEYS`, with `aria-pressed={connSort === key}`, `title={CONN_SORT_COPY[key].title}`, and the label from `CONN_SORT_COPY[key].label` — the chosen one uses the pressed style `rounded-md bg-background px-2.5 py-1 text-xs font-medium text-foreground shadow-sm`, the others `rounded-md px-2.5 py-1 text-xs font-medium text-muted-foreground hover:text-foreground`.
  - One direction button next to them: `aria-pressed={connOrder === "desc"}`, `aria-label` describing the flip, and the glyph `↓` when the order is `"desc"` and `↑` otherwise. Clicking it flips the order.
  - The control sits **directly under the panel header and above the list rows** — the position convention of 2026-10-21. It stays visible while the tree scrolls.
  - Each click sets the state, which re-runs `load()` and re-fetches in the new order.

- [ ] **Step 6: Type-check and lint.** `pnpm typecheck:frontend` → clean. `pnpm lint:frontend` → no new errors.

- [ ] **Step 7: Commit.** `feat(dashboard): order the connections panel by the chosen key`

---

### Task 4: Verify the behaviour in the browser

**Files:** none — verification only.

**Interfaces:**
- Consumes: the running dashboard (frontend 4427, backend 4417).
- Produces: nothing committed. A live confirmation that the feature behaves as the spec says.

- [ ] **Step 1: Start the backend and the frontend** if they are not already running: backend on 4417, frontend on 4427 (the frontend proxies `/api/*` to 4417). Do not run `pnpm build` to check anything.

- [ ] **Step 2: Open the page** at `http://localhost:4427/dashboard/databases` in the OpenChamber browser panel and confirm the panel hydrated (the list shows connections, not a permanent "Loading.").

- [ ] **Step 3: Exercise every key.** Click Name, Type, Created and Last used; after each click the list must re-order and the pressed segment must show. Confirm the arrow flips the direction.

- [ ] **Step 4: Exercise the restore chain.**
  - Reload the page → the order comes back (from localStorage).
  - Open `http://localhost:4427/dashboard/databases?sort=provider&order=desc` → the URL wins over localStorage.
  - Clear the URL params and clear the `db.explorer.state` key in localStorage, then reload → Name A→Z returns.

- [ ] **Step 5: Confirm the never-connected row** stays at the bottom under both directions of Last used.

- [ ] **Step 6: Report** what the panel showed, per the user rule that a frontend change is not done until the panel demonstrates it.
