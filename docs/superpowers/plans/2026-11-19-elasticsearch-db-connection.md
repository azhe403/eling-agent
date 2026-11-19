# Elasticsearch Read-Only Connection Support Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Register an Elasticsearch cluster as a connection provider in Eling's connection registry and browse it read-only — indices, mappings, paged documents (mini-Discover with a Filter builder and histogram), mapping/stats tab, DSL console — from dashboard, REST, and MCP, with zero mutation surface.

**Architecture:** A fifth `DatabaseProvider` (`Elasticsearch = 4`, appended), sharing the existing `databases.json` registry. `IDatabaseAdapter` stays SQL-only; Elasticsearch gets its own read path `IElasticsearchReadService` built on `Elastic.Clients.Elasticsearch` 8.19.x — the only code that talks to a cluster, with no method that can write. REST routes under the existing `/api/databases/{id}/es/...` group; four MCP tools (`es_indices`, `es_mapping`, `es_search`, `es_count`); dashboard gets an `Elasticsearch` provider option and a per-index panel.

**Tech Stack:** .NET 10, ASP.NET Core minimal API, `Elastic.Clients.Elasticsearch` 8.19.x (latest 8.x patch at implementation time), xUnit, SQLite-backed registry. Dashboard: Next.js 16, React 19, TypeScript, Tailwind, shadcn-style `@/components/ui`, vitest.

**Spec:** `docs/superpowers/specs/2026-11-19-db-connection-elasticsearch-design.md` — read it first; this plan executes it.

## Global Constraints

- **Read-only, standing contract** (spec §1, §9, §10): no write route, tool, or service method — and none is planned in the foreseeable future. `POST /{id}/mutate` rejects ES with 400. `/es/` maps GET routes only plus POST on exactly the two DSL endpoints.
- Enum order is **append-only**: `Elasticsearch = 4` (pinned by test).
- Latest 8.x stable at implementation time; ES 7 refused at connection-test time with a clear message; `NEST` never used; ES 9 = package bump later.
- `from + size ≤ 10,000`; `documents`/`search` size clamp **1–100** (`es_search` MCP: **1–50**); `histogram` `buckets` clamp **1–100** (default 60).
- `index` always travels in the **query string** (never path segments). Wildcards and `_all` are allowed — read-only is the contract.
- `includeSystem` defaults **false**; indices whose name starts with `.` are excluded unless requested.
- `field` (default `@timestamp`) must map to a `date`: **validated on-demand only** in `/es/documents` (only when `start`/`end` or an explicit `field` is supplied); **always** in `/es/histogram`.
- camelCase JSON everywhere; one top-level type per file; records chopped one parameter per line; no tuples (`CodeOrganizationConventionTests`).
- Conventional commits.
- **No network in tests.** The live-cluster smoke checklist (spec §7) is a manual checklist, post-implementation, with the user's basic-auth credentials via a secret card — never into the repo.
- AppSettings scan for ES keys is **not part of this phase** — ES connections are registered manually.

## Review Focus

- **Browsing a dateless index** (no `date`-typed field at all): today it must render documents unchanged as long as no `start`/`end`/`field` is passed; a regression here bricks every non-time-series index. → pinned in Task 6 (backend endpoint tests) and Task 9 (integration-ish read-service handler test).
- **Filters that don't compile to a clean 400** (bad `op`, bad `field` charset, `exists` with a `value`, non-numeric `value` on a range op): must surface 400 with a reason, never 500. → pinned in Task 6.
- **`from + size ≤ 10,000` boundary**: 10,000 passes, 10,001 clamps/rejects exactly as the spec defines. → pinned in Task 6.
- **Percent-encoded password round trip** (`user:pa@ss:w` and `p@ss:w` and `a=b,c:d`): build → parse must round-trip or the form silently drops credentials on reopen. → pinned in Task 4.
- **Two tabs of one cluster, two indices** (`connId + index` as the tab key): closing one must not close the other; an index with `*` in the name when hit from a wildcard search must not corrupt the tab comparison. → pinned in Task 11 (frontend unit tests), mirroring the Redis-tree plan's review focus.

Each line's test is added in the owning task, in that task's step style.

---

## File structure (locked by the spec §2.1)

```
Eling.Core/Database/
  DatabaseProvider.cs                  (edit) append Elasticsearch = 4
  DatabaseConnectionStringFactory.cs   (edit) Build/Parse/Update for the URI format
  ElasticsearchRequirement.cs          (new)  refusal messages, mirrors RedisRequirement
  IElasticsearchReadService.cs         (new)  interface: 6 read ops (CountAsync separate from SearchAsync)
  ElasticsearchReadService.cs          (new)  Elastic.Clients.Elasticsearch calls only
  ElasticsearchClientCache.cs          (new)  bounded singleton pool (8 entries, 5-min idle eviction)
  IElasticsearchClientCache.cs         (new)
  ElasticsearchFilterCompiler.cs       (new)  pure: FilterRow list → bool DSL JsonElement
  ElasticsearchFilterRow.cs            (new)  record: Field, Op, Value?
  ElasticsearchIndexInfo.cs            (new)  record: name, health, status, docsCount, storeSizeBytes, primaryShards, replicas
  ElasticsearchIndexDetail.cs          (new)  record: name, mappings (JsonElement), settings (JsonElement), docsCount, storeSizeBytes
  ElasticsearchHit.cs                  (new)  record: index, id, score, source (JsonElement)
  ElasticsearchSearchResult.cs         (new)  record: total, tookMs, hits, aggregations (JsonElement?)
  ElasticsearchDocumentPage.cs         (new)  record: total, from, size, tookMs, hits
  ElasticsearchHistogramBucket.cs      (new)  record: keyMs, docCount
  DatabaseAuditActions.cs              (edit) es_indices, es_mapping, es_search, es_count

Eling.Backend/
  Dtos/ElasticsearchDtos.cs            (new)  request/view DTOs for the es routes
  Endpoints/DatabaseElasticsearchEndpoints.cs  (new)  6 routes under MapGroup("/api/databases")
  Endpoints/DatabaseEndpoints.cs       (edit) ES refusals (schema/query/browse/mutate/keys) + ES test branch before adapter seam (Redis untouched — follow-up)
  Endpoints/DatabaseKeyEndpoints.cs    (edit) ES refusal on /{id}/keys* routes
  Mcp/Tools/EsIndicesTool.cs           (new)
  Mcp/Tools/EsMappingTool.cs           (new)
  Mcp/Tools/EsSearchTool.cs            (new)
  Mcp/Tools/EsCountTool.cs             (new)
  Mcp/ServerInstructions.cs            (edit) describe the four es tools
  Bootstrap/DashboardServices.cs       (edit) DI: client cache + read service
  Bootstrap/McpHostBuilder.cs          (edit) DI: client cache + read service (MCP host needs it too)
  Bootstrap/DashboardRoutes.cs         (edit) app.MapDatabaseElasticsearchEndpoints();
  Endpoints/DatabaseEndpoints.cs       (edit) ES test branch (both test routes, before adapter creation)

Eling.Dashboard (src/frontend/Eling.Dashboard/src/app/dashboard/databases/)
  types.ts                             (edit) DbConn provider union + ElasticsearchIndexInfo/Detail types, DbTab kind
  db-tabs.ts                           (edit) elasticsearch-index tab kind + isElasticsearchIndexTab
  es-browser.ts                        (new)  formatters, hidden-system-indices rule, tab dedup (mirror redis-browser.ts)
  es-client.ts                         (new)  typed fetchers: indices, index detail, documents, histogram, search, count
  components/connection-form/ConnectionFormFields.tsx  (edit) Elasticsearch option; hide Database/FilePath; helper text
  components/connection-tree/ConnectionTree.tsx        (edit) index rows for ES connections ("N docs"), toggle, onOpenEsIndex
  components/elasticsearch/ElasticsearchIndexPanel.tsx (new) Discover (Filter|Lucene) / Mapping / Query tabs
  Databases.tsx                        (edit) wire the panel, the tab open path, and ES tree rows

Tests
  tests/Eling.Core.Tests/DatabaseProviderTests.cs                  (edit) Elasticsearch_IsFour
  tests/Eling.Core.Tests/ElasticsearchConnectionStringTests.cs     (new)
  tests/Eling.Core.Tests/ElasticsearchRequirementTests.cs          (new)
  tests/Eling.Core.Tests/ElasticsearchFilterCompilerTests.cs       (new)
  tests/Eling.Core.Tests/ElasticsearchReadServiceTests.cs          (new)  stub HttpMessageHandler with canned ES JSON
  tests/Eling.Core.Tests/ElasticsearchReadOnlyConventionTests.cs   (new)  §8-mirror: no write-shaped members on the interface
  tests/Eling.Core.Tests/ElasticsearchPingTests.cs                 (new)  ES7 → version refusal
  tests/Eling.Backend.Tests/ElasticsearchEndpointTests.cs          (new)  TestServer + stub IElasticsearchReadService
  tests/Eling.Backend.Tests/DatabaseRefusalElasticsearchTests.cs   (new)  SQL routes refuse ES; ES routes refuse non-ES
  tests/Eling.Backend.Tests/ElasticsearchWriteVerbSweepTests.cs    (new)  PUT/DELETE/PATCH on each /es route → 405
  tests/Eling.Backend.Tests/DatabaseManualConnectionTests.cs       (edit?) ES manual connection round-trip if the shape fits; else new file

Smoke checklist (manual, post-implementation; spec §7)
  1. Register the cluster via the dashboard (basic auth, secret card — never the repo)
  2. Test connection → expect version string
  3. List indices → doc counts shown
  4. Open the target index → Discover page renders (query, histogram, grid)
  5. One scoped DSL query in Query tab; one es_search via MCP; es_count via MCP
  6. Wrong password → 401 surfaced as auth failure, not "connection refused"
```

---

## Phase 1 — Core

### Task 1: Provider enum + audit actions

**Files:**
- Modify: `src/backend/Eling.Core/Database/DatabaseProvider.cs`
- Modify: `src/backend/Eling.Core/Database/DatabaseAuditActions.cs`
- Test: `tests/Eling.Core.Tests/DatabaseProviderTests.cs` (edit, add pins)

**Interfaces:**
- Produces: `DatabaseProvider.Elasticsearch == 4`; `DatabaseAuditActions.EsIndices = "es_indices"`, `EsMapping = "es_mapping"`, `EsSearch = "es_search"`, `EsCount = "es_count"`.

- [ ] **Step 1: Write the failing test** — add to `DatabaseProviderTests`:
  - `Elasticsearch_IsFour` → `Assert.Equal(4, (int)DatabaseProvider.Elasticsearch);`
  - `Existing_Values_Unchanged` → Sqlite 0, MySql 1, Redis 2, PostgreSql 3.
- [ ] **Step 2: Run** `dotnet test tests/Eling.Core.Tests --filter FullyQualifiedName~DatabaseProviderTests` → fails to compile.
- [ ] **Step 3:** Append `Elasticsearch` after `PostgreSql` in the enum; add the four audit-action constants to `DatabaseAuditActions` next to the Redis ones.
- [ ] **Step 4:** Same command → PASS.
- [ ] **Step 5: Commit** `feat(core): append elasticsearch provider and audit actions`

### Task 2: Zero-mutation interface + records

**Files:**
- Create: `src/backend/Eling.Core/Database/IElasticsearchReadService.cs`
- Create: `src/backend/Eling.Core/Database/ElasticsearchIndexInfo.cs`, `ElasticsearchIndexDetail.cs`, `ElasticsearchHit.cs`, `ElasticsearchSearchResult.cs`, `ElasticsearchDocumentPage.cs`, `ElasticsearchHistogramBucket.cs`, `ElasticsearchFilterRow.cs`
- Test: `tests/Eling.Core.Tests/ElasticsearchReadOnlyConventionTests.cs` (new)

**Interfaces:**
- Produces:

```csharp
public interface IElasticsearchReadService
{
    Task<ElasticsearchPingResult> PingAsync(DatabaseConnection connection, CancellationToken ct);
    Task<IReadOnlyList<ElasticsearchIndexInfo>> ListIndicesAsync(DatabaseConnection connection, bool includeSystem, CancellationToken ct);
    Task<ElasticsearchIndexDetail> GetIndexDetailAsync(DatabaseConnection connection, string index, CancellationToken ct);
    Task<ElasticsearchDocumentPage> DocumentsAsync(DatabaseConnection connection, string index, int from, int size,
        string? q, DateTimeOffset? start, DateTimeOffset? end, string? field, IReadOnlyList<ElasticsearchFilterRow>? filters, string? logic, CancellationToken ct);
    Task<IReadOnlyList<ElasticsearchHistogramBucket>> HistogramAsync(DatabaseConnection connection, string index,
        string? q, DateTimeOffset? start, DateTimeOffset? end, string? field, int buckets,
        IReadOnlyList<ElasticsearchFilterRow>? filters, string? logic, CancellationToken ct);
    Task<ElasticsearchSearchResult> SearchAsync(DatabaseConnection connection, string index,
        JsonElement? query, JsonElement? sort, JsonElement? aggs, int size, int from,
        IReadOnlyList<string>? sourceIncludes, CancellationToken ct);
    Task<long> CountAsync(DatabaseConnection connection, string index, JsonElement? query, CancellationToken ct);
}
```

Records exactly as spec §3.3 (chopped one parameter per line): `ElasticsearchIndexInfo(string Name, string Health, string Status, long DocsCount, long StoreSizeBytes, int PrimaryShards, int Replicas)`; `ElasticsearchIndexDetail(string Name, JsonElement Mappings, JsonElement Settings, long DocsCount, long StoreSizeBytes)`; `ElasticsearchHit(string Index, string Id, double? Score, JsonElement Source)`; `ElasticsearchSearchResult(long Total, long TookMs, IReadOnlyList<ElasticsearchHit> Hits, JsonElement? Aggregations)`; `ElasticsearchDocumentPage(long Total, int From, int Size, long TookMs, IReadOnlyList<ElasticsearchHit> Hits)`; `ElasticsearchHistogramBucket(long KeyMs, long DocCount)`; `ElasticsearchFilterRow(string Field, string Op, string? Value)`. `ElasticsearchPingResult(string Version, long LatencyMs)` is required by Task 6 — define it in this task.

- [ ] **Step 1: Write the failing convention test** `ElasticsearchReadOnlyConventionTests.cs`: enumerate `typeof(IElasticsearchReadService).GetMethods()` — assert none match (case-insensitive) `Index`, `Update`, `Delete`, `Bulk`, `Reindex`, `Put`, `Create`, `Write`, `Ingest`, `Open`, `Close`, `PutMapping` (spec §9 wording: no write-shaped member names). One `[Theory]` per banned verb, or a single fact with the offender list in the failure message — either is fine.
- [ ] **Step 2:** Run `dotnet test tests/Eling.Core.Tests --filter ElasticsearchReadOnlyConventionTests` → fails to compile.
- [ ] **Step 3:** Create the interface and the eight records (seven + ping result).
- [ ] **Step 4:** Run → PASS. Also `dotnet test tests/Eling.Core.Tests --filter CodeOrganizationConventionTests` → green.
- [ ] **Step 5: Commit** `feat(core): es read-only contract and wire records`

### Task 3: Package pin

**Files:**
- Modify: `src/backend/Eling.Core/Eling.Core.csproj`

- [ ] **Step 1:** Add `<PackageReference Include="Elastic.Clients.Elasticsearch" Version="8.19.27" />` (or the newest 8.x patch available at implementation time — check `dotnet package search` or nuget.org; never 9.x).
- [ ] **Step 2:** `dotnet build src/backend/Eling.Core/Eling.Core.csproj` → success.
- [ ] **Step 3:** Commit `chore(core): add elastic.clients.elasticsearch 8.x`

### Task 4: Connection string Build/Parse/Update + percent-encoding

**Files:**
- Modify: `src/backend/Eling.Core/Database/DatabaseConnectionStringFactory.cs`
- Test: `tests/Eling.Core.Tests/ElasticsearchConnectionStringTests.cs` (new)

**Interfaces:**
- Consumes: `ManualConnectionFields` (unchanged record).
- Produces: `Build(Elasticsearch)` → absolute `http(s)://[user[:pass]@]host[:port]/` (default port 9200, `http` scheme in manual mode; `https` only from a stored/raw string); `Parse(Elasticsearch)` → `ManualConnectionFields` (`Database` and `FilePath` always null); `Update(Elasticsearch, stored, manual)` → rebuild from manual, preserving the stored URI's scheme (`https` stays `https`), empty/blank fields keep the stored value's field (a blank password keeps the stored password — same rule as SQL).

Rules to implement (spec §3.2, exact):
- Build assembles a `System.Uri`; userinfo components are **percent-encoded** on build (`Username`, `Password` — `@`, `:`, `%`, `/`, `=`, `&` must survive) and percent-**decoded** on parse.
- No userinfo → anonymous URI (`http://host:9200/`). Username without password (or vice versa) round-trips.
- A stored string that cannot be parsed (bad scheme, bad port, malformed URI) → `Parse` returns `null` (the form shows empty fields — same as SQL).
- Manual `Database`/`FilePath` are ignored for ES; the dashboard hides those fields (Task 11).

Tests (all in `ElasticsearchConnectionStringTests.cs`):
- `Build_NoAuth_RendersHostAndDefaultPort` → `http://127.0.0.1:9200/`
- `Build_WithCredentials_RendersUserInfo` → `http://u:p@127.0.0.1:9200/`
- `Build_EncodesPasswordSpecials` — password `pa:ss@wo:rd=a&b` → URI contains percent-encoded userinfo; `Parse` round-trips to the exact password.
- `Parse_RoundTrips_Build` (theory over several user/password/host/port combos) → fields come back identical (`Host`, `Port`, `Username`, `Password`); `Database`/`FilePath` null.
- `Parse_Unparseable_ReturnsNull` → `not a uri` and `http://host:notaport/` → null.
- `Parse_Anonymous_ReturnsNoUserNoPass` → `http://127.0.0.1:9200/` → `Username == null`, `Password == null`.
- `Update_KeepsStoredHttpsScheme` → stored `https://h:9200/`, manual with new host → `https://new:9200/`.
- `Update_BlankPassword_KeepsStored` → stored `http://u:secret@h:9200/`, manual username `u` password null → password is `secret`.

- [ ] **Step 1:** Write the failing tests. **Step 2:** Run → fail (compile). **Step 3:** Implement the three methods + `DefaultElasticsearchPort = 9200` const; wire ES into the existing `Build`/`Parse`/`Update` switch statements. **Step 4:** Tests pass; then `dotnet test tests/Eling.Core.Tests --filter FullyQualifiedName~DatabaseConnectionStringFactoryTests` → no regression. **Step 5: Commit** `feat(core): es connection string build/parse/update`

### Task 5: Filter compiler

**Files:**
- Create: `src/backend/Eling.Core/Database/ElasticsearchFilterCompiler.cs`
- Test: `tests/Eling.Core.Tests/ElasticsearchFilterCompilerTests.cs`

**Interfaces:**
- Produces: `public static JsonElement Compile(IReadOnlyList<ElasticsearchFilterRow> rows, string? logic)` — `logic == "or"` → `bool.should` with `minimum_should_match: 1`; anything else (default `and`) → `bool.must`. Rows compile per the spec §4.1 mapping: `is`→`term`, `contains`→`match` (`operator: and`), `gt|gte|lt|lte`→`range`, `exists`→`exists`.
- Throws `ElasticsearchFilterException : Exception` (with `Message` safe for 400s) on: `field` failing `^[A-Za-z0-9_.@-]{1,255}$`; `op` outside the enum; `exists` with non-null `value`; range op with a non-numeric `value`.

Tests:
- `Is_Term`, `Contains_MatchAnd`, `Range_Number` (gt/gte/lt/lte), `Exists_BoolField`
- `And_CombinesBoolMust`, `Or_CombinesBoolShouldMinMatch1`
- `BadFieldCharset_Throws` (`msg or`, `` empty, 300 chars, `/`)
- `BadOp_Throws`, `ExistsWithValue_Throws`, `RangeWithNonNumeric_Throws` (`value: "abc"`), `RangeWithEpochMs_Accepts` (numeric string accepted)
- `Compiled_Json_RoundTrips` — deserialize the result and assert the exact DSL shape (`{"bool":{"must":[{"term":{...}}, ...]}}`).

- [ ] **Steps:** failing tests → implement → green → commit `feat(core): compile filter rows to bool dsl`

### Task 6: Client cache + read service + handler-stub tests

**Files:**
- Create: `src/backend/Eling.Core/Database/IElasticsearchClientCache.cs`, `ElasticsearchClientCache.cs`, `ElasticsearchReadService.cs`
- Test: `tests/Eling.Core.Tests/ElasticsearchReadServiceTests.cs`

**Interfaces:**
- `IElasticsearchClientCache { ElasticsearchClient GetOrCreate(DatabaseConnection connection); void Invalidate(string connectionId); }` — keyed by `connection.Id`, stores the connection string per entry; rebuild on change; bounded to **8** entries (LRU eviction); 5-minute idle eviction via one `System.Threading.Timer`; invalidated from PUT/DELETE of the connection (Tasks 8).
- `ElasticsearchReadService` uses `indices.stats` (indices list + per-index detail), `indices.get_mapping`/`get_settings` (detail), `search` with `track_total_hits: true` (documents: `size`/`from`, `_doc` order; optional `query_string` + `bool.filter` from `filters` via the compiler + optional time-range), `search` with `size: 0` + `auto_date_histogram` (histogram; target bucket count, `interval` passthrough from ES), `search` passthrough (search: query/sort/aggs raw; `_source` includes), `count`.
- `PingAsync` → `GET /` (`ElasticsearchClient.InfoAsync()`), returns `ElasticsearchPingResult(version, latencyMs)`; refuses version `\b7\.` with `ElasticsearchRequirement.UnsupportedVersion` ("ES 7 is not supported; use an ES 8 cluster").
- Error mapping for the service to raise typed exceptions the endpoints translate (Task 7): `ElasticsearchApiException(int Status, string Reason)`, `ElasticsearchUnavailableException(string Reason)` (network/timeout), `ElasticsearchUnauthenticatedException` (401/403).

Handler-stub tests (`HttpMessageHandler` subclass returning canned JSON per path — no network):
- `Ping_ReturnsVersion` (canned `{"version":{"number":"8.19.2"}}`)
- `Ping_Es7_ThrowsUnsupportedVersion` (canned `7.17.0`)
- `ListIndices_HidesDotIndicesByDefault` (handler returns 3 indices incl. `.kibana`; `includeSystem: false` → 2 back)
- `GetIndexDetail_Missing_Throws404Shaped` (handler returns 404 → exception with `Status == 404`)
- `Documents_NoRange_DoesNotValidateField` (handler asserts the request JSON had **no** `range` clause when `start/end/field` absent — this is the on-demand rule; the handler records the sent body)
- `Documents_WithRange_OnDatelessField_Throws400Shaped`
- `Histogram_NonDateField_Throws400Shaped` (filter compilation throws before the HTTP call; assert the handler was **not** called)
- `Search_WithAggs_ReturnsAggregations` (canned aggregations block passes through verbatim)
- `Count_ReturnsLong`
- `Unauthorized_SurfacesAs401ShapedException` (handler returns 401)
- `WildcardIndex_IsSentVerbatim` (index `logs-*` reaches the handler untransformed)

- [ ] **Steps:** failing tests → implement → green → `dotnet test tests/Eling.Core.Tests` (full suite green) → commit `feat(core): elasticsearch read service and client cache`

### Task 7: Endpoint tests for the routes not yet mapped (red) — actually merge into Task 8

*(Task removed — endpoints are covered by Task 8; nothing to red-test before the files exist.)*

### Task 8: Backend — DTO + 6 routes + refusals + both test routes

**Files:**
- Create: `src/backend/Eling.Backend/Dtos/ElasticsearchDtos.cs` (request/view records: `ElasticsearchIndicesDto`? no — reuse Core records; only request DTOs: `ElasticsearchSearchRequest { string? Index; JsonElement? Query; JsonElement? Sort; JsonElement? Aggs; int Size = 25; int From; IReadOnlyList<string>? SourceIncludes }`, `ElasticsearchCountRequest { string? Index; JsonElement? Query }`)
- Create: `src/backend/Eling.Backend/Endpoints/DatabaseElasticsearchEndpoints.cs` (maps `/{id}/es/indices`, `/{id}/es/index`, `/{id}/es/documents`, `/{id}/es/histogram`, `/{id}/es/search`, `/{id}/es/count`)
- Create: `src/backend/Eling.Backend/Bootstrap/DashboardRoutes.cs` edit (analysis comment + `app.MapDatabaseElasticsearchEndpoints();`), `DashboardServices.cs`, `McpHostBuilder.cs` (DI: `AddSingleton<IElasticsearchClientCache>`, `IElasticsearchReadService`)
- Modify: `DatabaseEndpoints.cs` — (a) add `Elasticsearch` branches to the refusal sites (`/schema`, `/query`, `/browse`, `/{id}/mutate`, `/list-databases`) with `ElasticsearchRequirement.NotSql`, mirroring the Redis checks; (b) add an `Elasticsearch` provider branch in both test routes, placed **above** `TryCreateAdapter`, mirroring the Redis branch shape (test via `IElasticsearchReadService.PingAsync`). The pre-existing latent Redis bug (Redis branch unreachable because the adapter factory throws first) is **deliberately left alone** — the user opted to split that fix out and ask about it separately; do not touch the Redis branches or the Redis ordering in this task.
- Modify: `DatabaseKeyEndpoints.cs` — ES provider → 400 on `/{id}/keys*`.
- **Note — Redis branch deliberately untouched:** there is a latent dead-code bug in both test routes (`TryCreateAdapter` is called before the Redis branch, and the factory throws for Redis). The user decided to split that fix out from this work; the ES branch goes above `TryCreateAdapter`, the Redis branches and their ordering stay as-is, and nobody "helps" by fixing Redis in this task.
- Tests: `tests/Eling.Backend.Tests/ElasticsearchEndpointTests.cs`, `DatabaseRefusalElasticsearchTests.cs`, `ElasticsearchWriteVerbSweepTests.cs`

Route behavior (spec §4, exact):
- Every route: 404 unknown id; 400 non-ES provider (`ElasticsearchRequirement.NotElasticsearch(row, "...")`); `includeSystem` default false; `index` required where applicable.
- `/es/search`: `size` clamp 1–100, `from ≥ 0`, `from + size ≤ 10,000`; missing `query` → match_all; `aggs` passthrough returns `aggregations`; ES 4xx → 400 with ES reason; 401/403 → 502/"authentication failed"; network → 503 "elasticsearch unavailable".
- `/es/documents`: same envelope + `q` → `query_string` merged into `bool.filter`; `filters` param decoded and compiled via `ElasticsearchFilterCompiler` (400 on `ElasticsearchFilterException`); `start`/`end`/`field` → range clause; **field validated as date only when `start`/`end`/explicit `field` supplied** (on-demand rule, Task 6 test pin); `_doc` order; `track_total_hits: true`.
- `/es/histogram`: `buckets` clamp 1–100 (default 60); **field validated as date always**; `auto_date_histogram`; returns `{ field, interval, buckets: [{ keyMs, docCount }] }` ascending.
- `/es/index`: composes `_mapping` + `_settings` + `_stats` (Task 6); missing index → 404.
- Audit: every route records with `Action = DatabaseAuditActions.EsSearch|EsMapping|EsIndices|EsCount`, `Target = connectionId`, `ResultCount` (indices / hits / count / buckets), `Outcome`; `es/documents` and `es/histogram` audit as `es_search` (spec §6).

Tests (endpoint layer, stubbed `IElasticsearchReadService`, TestServer):
- 404 unknown id; 400 SQLite connection ("is a Sqlite connection"); for `/{id}/es/search` and `/{id}/es/count`: 400 body still points at `es_search`/`es_mapping` as advice in the refusal message.
- `/es/documents` dateless index, no `start`/`end`/`field` → stub called with `field == null` (assert captured args) → 200.
- `/es/documents` with `start` and a dateless stub → stub throws `ElasticsearchApiException(400, ...)` → 400.
- `/es/histogram` dateless stub → 400 always.
- `filters` bad JSON blob → 400; `filters` with bad op → 400 (via real compiler, not the stub).
- `from=10000,size=1` → OK captured; `from=10000,size=101` → clamped or 400 per clamp rule (pick the clamp: `size` clamped to 100 first, then `from + size ≤ 10,000` else 400).
- `/es/indices?includeSystem=false` with a stub list → 200, `includeSystem` captured false.
- `/es/search` with `aggs` → response contains `aggregations` verbatim from stub.
- Stub throws `ElasticsearchUnavailableException` → 503; stub throws `ElasticsearchUnauthenticatedException` → 502; stub throws `ElasticsearchApiException(404)` on `/es/index` → 404.
- SQL routes refuse ES: `POST /{id}/schema|browse|mutate|query...` (as those routes exist) → 400; `GET /{id}/keys` for ES → 400; and symmetric: ES connection refused with a message naming the provider.
- **Write-verb sweep:** for each of the six `/es` paths — `PUT`, `PATCH`, `DELETE`, `POST` (where not mapped) → 405 MethodNotAllowed. No write verb exists anywhere under `/es/`.

- [ ] **Steps:** failing endpoint tests (stub service registered in test DI) → implement DTO + endpoints + refusal edits + DI → green → `dotnet test tests/Eling.Backend.Tests` full suite → commit `feat(backend): es routes, refusals and test-branch ordering`

**Note for the reviewer:** Task 8 places the ES branch above `TryCreateAdapter` (required, mirrors the Redis shape). The latent Redis ordering bug is intentionally NOT fixed here — user decision, split to a follow-up.

### Task 9: Smoke-check that pins the Review Focus lines

*(Merge into Tasks 4/6/8 — their tests already pin the five Review Focus items. This task is the tracking note only; no code.)*

- [ ] **Step 1:** Confirm each Review Focus item has a named test: dateless browse (Task 6/8), filter-400 matrix (Task 5/6/8), `from+size` boundary (Task 8), password round-trip (Task 4), tab-key collision (Task 11).

---

## Phase 2 — MCP

### Task 10: Four MCP tools + ServerInstructions + DI

**Files:**
- Create: `src/backend/Eling.Backend/Mcp/Tools/EsIndicesTool.cs`, `EsMappingTool.cs`, `EsSearchTool.cs`, `EsCountTool.cs`
- Modify: `src/backend/Eling.Backend/Mcp/ServerInstructions.cs` (describe the tools; keep the Redis ones untouched)

Tool shapes (mirror `RedisScanTool` exactly — constructor `(IDatabaseConnectionStore, IElasticsearchReadService, ILogger<TTool>, IAuditLogger)`, `[McpServerToolType]`):

- `es_indices { string connectionId, bool includeSystem = false }` → `ElasticsearchIndexInfo[]` — audit `es_indices`, target = connectionId.
- `es_mapping { string connectionId, string index }` → anonymous record `new { name, mappings, settings }` (stats **dropped**, spec §4.8) — audit `es_mapping`.
- `es_search { string connectionId, string index, JsonElement? query = null, JsonElement? sort = null, JsonElement? aggs = null, int size = 25, int from = 0, string[]? sourceIncludes = null }` — `size` clamp **1–50**; `from` clamp so `from+size ≤ 10,000`; audit `es_search`; returns `ElasticsearchSearchResult`.
- `es_count { string connectionId, string index, JsonElement? query = null }` → `{ count, tookMs }` — audit `es_count`.

Refusal behavior identical to the Redis tools: unknown id → `KeyNotFoundException`; non-ES → `InvalidOperationException(ElasticsearchRequirement.NotElasticsearch(row, "..."))`; ES failures → `InvalidOperationException("es_search failed: " + reason)` with the inner exception swallowed to a category (log the exception server-side; never leak host/user). `ElasticsearchUnauthenticatedException` → message says authentication failed (401) — that guidance is valuable to the agent and leaks nothing.

- [ ] **Steps:** implement tools + ServerInstructions paragraph → `dotnet build` → `dotnet test tests/Eling.Backend.Tests` green (convention tests cover tool naming) → commit `feat(mcp): es_indices, es_mapping, es_search, es_count`

### Task 11: Dashboard — provider, form, tree rows

**Files:**
- Modify: `src/frontend/Eling.Dashboard/src/app/dashboard/databases/types.ts`, `db-tabs.ts`, `components/connection-form/ConnectionFormFields.tsx`, `components/connection-tree/ConnectionTree.tsx`, `Databases.tsx`
- Create: `src/frontend/Eling.Dashboard/src/app/dashboard/databases/es-browser.ts`
- Test: `src/frontend/Eling.Dashboard/src/app/dashboard/databases/es-browser.test.ts` (new)

Work:
- `DbConn["provider"]` gains `"Elasticsearch"`; `ConnectionFormFields` adds the `<SelectItem value="Elasticsearch">Elasticsearch</SelectItem>` option, hides the `Database` and `File path` fields for it, and shows the helper text: "No TLS in manual mode — use raw connection string mode for `https://`."
- `db-tabs.ts`: new tab kind — `DbTab = … | { kind: "elasticsearch-index"; connId: string; index: string }` with `isElasticsearchIndexTab()` mirroring `isRedisTab`; **every** tab-key comparison (active check, close-tab, keep-others, de-dup) includes `index` — copy the Redis comparison structure, not the field names.
- `es-browser.ts`: `esIndexLabel`, `esIndexDocs(n)` ("N docs", spelled out), `visibleIndices(indices, includeSystem)` (leading `.` hidden by default), `esTabKey(connId, index) → ${connId}::${index}`; unit tests: hidden system filter, tab dedup (two tabs one cluster two indices — closing one keeps the other), wildcard-hit index name in grid context doesn't split the tab key.
- `es-client.ts`: `fetchEsIndices(connId)`, `fetchEsIndex(connId, index)`, `fetchEsDocuments(connId, index, {from,size,q,start,end,field,filters,logic})`, `fetchEsHistogram`, `fetchEsSearch`, `fetchEsCount` — all GET/POST to `/api/databases/${connId}/es/...` with the envelope; non-2xx → throw `{status, message}` (match the existing `apiFetch` pattern in the file's neighborhood).
- `ConnectionTree`: ES connections render children like Redis connections render db rows — one row per open index (expand-on-click), label `index` + `N docs` subtitle, a ".system indices" toggle in the tree header for ES connections; `onOpenEsIndex(connId, index)` opens the index tab.
- `Databases.tsx`: wire index rows → tab open; a `kind === "elasticsearch-index"` tab renders the new panel (Task 12); **do not** run `pnpm build` — verify with `pnpm --filter eling-dashboard exec tsc --noEmit` (repo rule).

- [ ] **Steps:** es-browser.ts + tests (red→green) → types/db-tabs/Connections tree wiring → `pnpm --filter eling-dashboard exec tsc --noEmit` → commit(s): `feat(dashboard): elasticsearch provider and index tree rows`

### Task 12: Dashboard — Discover panel (Filter|Lucene, histogram, grid) + Mapping + Query

**Files:**
- Create: `src/frontend/Eling.Dashboard/src/app/dashboard/databases/components/elasticsearch/ElasticsearchIndexPanel.tsx`
- Test: `src/frontend/Eling.Dashboard/src/app/dashboard/databases/elasticsearch-panel.test.tsx` (new; vitest + @testing-library/react, mirroring `redis-tree.test.tsx` harness)

Panel (one tab kind, three tabs):
1. **Discover** — top row: mode toggle **Filter | Lucene**. Filter mode: rows `[field dropdown | operator dropdown | value input]` (operators: `is`, `contains`, `>`, `>=`, `<`, `<=`, `exists`; `.keyword` hint appended to the field label for text fields with a `.keyword` sub-field; date-aware placeholder for range ops on date fields), add/remove row, AND|OR toggle above the rows. Lucene mode: query bar (Lucene syntax; helper text says so). Both compile to the same `filters` (or `q`) + time-range envelope and drive grid **and** histogram identically. Below: time-range presets (15m/1h/24h/7d/all time) + date-field selector shown only when >1 `date` field in mapping (default `@timestamp`). Below: inline-SVG `auto_date_histogram` bar chart (~60 buckets, click a bucket → narrow time range to it; no chart-library dependency). Below: grid — 25/page, Prev/Next + "page N of M"; `_id` pinned first; `index` column shown only for wildcard/multi-index searches; selected-fields column picker (multi-select from mapping's top-level `properties` incl. dotted paths), persisted per `connId + index` in localStorage; row click → full `_source` pretty JSON in a side drawer. Documents grid shows **empty cell** (not misaligned column) when a source lacks a selected field.
2. **Mapping** — collapsible `mappings` and `settings` pretty-JSON blocks + header line `N docs · S B · shards P/R`.
3. **Query** — DSL textarea, optional `size`/`aggs`, Run; result pretty-JSON (hits + aggregations); JSON parse errors inline; server errors render the ES reason inline.

Persist per `connId + index` in localStorage: mode, filter rows, AND/OR, time range, selected fields (pattern matches existing `localStorage` usage in the databases page; **no** URL state).

Tests (component level):
- `FilterMode_CompilesRowsToFiltersParam` — build the fetch args, assert the exact `filters` JSON the client sends (one `is` row, AND join).
- `LuceneMode_SendsQ` — typing `mutation AND error` sends `q=...` untouched.
- `DatelessIndex_HidesTimeControls` — mapping without date fields → time presets and date selector absent.
- `SelectedFields_PersistAcrossMount` — unmount/remount → same columns.
- `EmptyCell_ForMissingField` — a hit missing a selected field renders an empty `<td>`, not "undefined".
- `EsTabKey_TwoIndices_DoNotMerge` — covered again at component level: two tabs, close one, other survives.

- [ ] **Steps:** panel + tests (red→green) → wire into `Databases.tsx` → `pnpm --filter eling-dashboard exec tsc --noEmit` → **manual render check on :4427** (verify the panel against the running frontend; if the dev server is running, never `pnpm build`) → commit `feat(dashboard): discover panel for elasticsearch indices`

### Task 13: Docs + final gate

- [ ] **Step 1:** Add the smoke checklist (top of this plan) to the PR/commit narrative — run it with the user's credentials via a secret card once implementation lands; record results.
- [ ] **Step 2:** `git log --oneline main..HEAD` review; `git status --short` clean of stray files.
- [ ] **Step 3:** Final `dotnet test` (backend + core suites) + `pnpm --filter eling-dashboard exec tsc --noEmit` — both green.
