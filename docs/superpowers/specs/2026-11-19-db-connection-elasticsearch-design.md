# DB Connection — Elasticsearch (Read-Only Browsing & DSL) Design Specification

- **Date:** 2026-11-19
- **Status:** Proposed (awaiting user review)
- **Target Projects:** `Eling.Core`, `Eling.Backend`, `tests/Eling.Core.Tests`, `tests/Eling.Backend.Tests`, `src/frontend/Eling.Dashboard`
- **Extends:** `docs/superpowers/specs/2026-10-13-db-connection-design.md` and mirrors the provider-agnostic decisions of `docs/superpowers/specs/2026-10-26-db-connection-redis-design.md`. The SQL contract, the shared registry, the credential rules, and the audit trail stay as written. This spec adds a fifth provider and a **non-SQL, read-only read path**.

---

## 1. Overview & Scope

Elasticsearch becomes a registered connection in the existing `/api/databases` registry, browsed as **indices and documents** instead of tables and rows. An index maps to a table-like row in the dashboard tree; a document maps to a row in the grid. Queries are the native JSON DSL (`_search`, `_count`), never SQL.

**In scope (this phase):**

- `DatabaseProvider.Elasticsearch` — a fifth provider in the enum, registry, dashboard form, and connection tree (appended as `4`, never reordered)
- Connection string build/parse in `DatabaseConnectionStringFactory` — one opaque URI with optional basic-auth userinfo
- Connection test via `GET /` (root info) — reports the server version on success, a clear auth/unreachable message on failure
- Indices list with doc count and store size: `GET /api/databases/{id}/es/indices`
- Per-index detail: mapping + settings + stats: `GET /api/databases/{id}/es/index?index=`
- Document browsing, paged, read-only, with time-range filter: `GET /api/databases/{id}/es/documents?index=&from=&size=&q=&start=&end=&field=`
- Hit histogram over a date field (feeds the dashboard's Discover-style bar chart): `GET /api/databases/{id}/es/histogram?index=&q=&start=&end=&field=`
- Query DSL console, read-only: `POST /api/databases/{id}/es/search` and `POST /api/databases/{id}/es/count`
- Structured filter builder (**Filter mode**): rows of field/operator/value composed with AND/OR, compiled server-side to a `bool` filter — `/es/documents` and `/es/histogram` accept an optional `filters` parameter alongside `q`
- Read-only service `IElasticsearchReadService` (built on `Elastic.Clients.Elasticsearch`) — the only code path that talks to a cluster; it has no method that can mutate
- MCP tools: `es_indices`, `es_mapping`, `es_search`, `es_count` — all read-only
- `db_list_connections` picks the provider up for free (shared registry); `db_get_schema`, `db_query`, and `browse` reject it with a clear 400
- Dashboard: provider option, index tree rows, and a Discover-style mini panel per index — query bar with time-range presets, hit histogram bar chart, selected-fields column picker, Documents grid, Mapping/stats tab, DSL console tab
- Audit entries for every call: `es_indices`, `es_mapping`, `es_search`, `es_count`

**Out of scope (explicitly):**

- **Any mutation — zero mutation surface, and none is planned in the foreseeable future** — no `_update`, `_bulk`, `_delete_by_query`, `_update_by_query`, `_reindex`, `_doc` writes, index create/delete, mapping put. Read-only is the standing contract, not a staging posture: adding mutation later would be a new design discussion, not a step in this work. The `POST /{id}/mutate` route stays SQL/Redis-only and there is no ES equivalent; no ES route, MCP tool, or service method can write to a cluster. A test in §7 pins this: no member of `IElasticsearchReadService` may be named like a write, and the mapped ES route table contains no write verbs.
- API-key authentication (basic auth and no-auth only; see §3.2)
- TLS toggle on the manual form — `https://` URIs are accepted in raw connection-string mode only
- Elasticsearch SQL (`_sql`) — native DSL only; ES SQL is a lossy subset and a second query surface to guard
- ES 7 servers (EOL; the 8.x client does not support them) and the `NEST` client (deprecated)
- Cross-cluster search, CCS, index templates, snapshots, Kibana integration, ingest pipelines
- Deep paging beyond ES's `max_result_window` (default 10,000) — no PIT/`search_after` this phase
- KQL as a query language in the dashboard query bar — the bar speaks Lucene `query_string` syntax (the same syntax Kibana uses in Lucene mode); a KQL parser is a dependency this phase does not take on
- URL state / deep-linking for the mini-Discover panel (query, range, fields) — in-memory + localStorage only
- Result caching — documents and counts are never cached; mapping/stats are not cached in v1 (revisit only if a real hot path shows up)

### 1.1 Why Elasticsearch is not an `IDatabaseAdapter`

Same structural reason as Redis, plus one more:

1. **A query is not a string of SQL.** `IDatabaseAdapter.QueryAsync` receives SQL. ES queries are JSON DSL objects with nesting, aggregations and scripts; serialising them into a SQL string and re-parsing is not a bridge, it is a lossy costume.
2. **The schema is per-index, heterogeneous, and lossy to flatten.** ES mappings are dynamic JSON (`_source`, multi-fields, `.keyword` sub-fields, runtime fields). `TableSchema`/`ColumnSchema` has no honest representation for most of that, and the dashboard grid needs `_source` verbatim, not projected columns.
3. **Read-only must be enforceable by construction.** With ES, the read/write split is enforced by *which client methods exist* and *which routes are mapped*, not by parsing a query body. An adapter implementing `ExecuteAsync` would re-open a mutation surface the brief forbids.

So `IDatabaseAdapter` stays SQL-only. The ES read path is its own service, the SQL endpoints reject ES by provider, and the ES endpoints reject non-ES connections by provider.

---

## 2. Architecture & Components

### 2.1 New and edited files

```
Eling.Core/Database/
  DatabaseProvider.cs                  (edit) append Elasticsearch = 4
  DatabaseConnectionStringFactory.cs   (edit) Build/Parse for the URI format
  ElasticsearchRequirement.cs          (new)  refusal messages, mirrors RedisRequirement
  IElasticsearchReadService.cs         (new)  indices, mapping, documents, histogram, search, count
  ElasticsearchReadService.cs          (new)  Elastic.Clients.Elasticsearch calls only
  ElasticsearchIndexInfo.cs            (new)  record: name, health, status, docsCount, storeSizeBytes, primary, replicas
  ElasticsearchIndexDetail.cs          (new)  record: name, mappings (JsonElement), settings (JsonElement), docsCount, storeSizeBytes
  ElasticsearchDocumentPage.cs         (new)  record: total, from, size, tookMs, hits (JsonElement[])
  ElasticsearchSearchResult.cs         (new)  record: total, tookMs, hits (JsonElement[]), aggregations (JsonElement?) — shared by search and browse
  ElasticsearchHistogramBucket.cs      (new)  record: keyMs, docCount
  DatabaseAuditActions.cs              (edit) es_* action names (§6)

Eling.Backend/
  Dtos/ElasticsearchDtos.cs            (new)  request/view DTOs for the es routes
  Endpoints/DatabaseElasticsearchEndpoints.cs  (new)  maps onto the existing /api/databases group
  Mcp/Tools/EsIndicesTool.cs           (new)  MCP es_indices
  Mcp/Tools/EsMappingTool.cs           (new)  MCP es_mapping
  Mcp/Tools/EsSearchTool.cs            (new)  MCP es_search
  Mcp/Tools/EsCountTool.cs             (new)  MCP es_count
  Mcp/ServerInstructions.cs            (edit) describe the four es tools
  Bootstrap/DashboardServices.cs       (edit) DI: ElasticsearchClient factory + IElasticsearchReadService
  Bootstrap/DashboardRoutes.cs         (edit) app.MapDatabaseElasticsearchEndpoints();

src/frontend/Eling.Dashboard/src/app/dashboard/databases/
  Databases.tsx                        (edit) provider option, ES branch in the right panel
  ElasticsearchIndexPanel.tsx          (new)  Discover-style panel: query bar + time range, histogram bar chart, selected-fields grid / Mapping+stats / Query DSL tabs
  es-client.ts                         (new)  typed fetchers for the /es routes
```

`Elastic.Clients.Elasticsearch` **8.19.x** (latest 8.x stable; 8.19.27 at time of writing) is added to `Eling.Core.csproj`. It targets `net8.0`+ and works on `net10.0`. The 9.x line exists but is for ES 9 servers; the target cluster reports itself over `GET /` and the version is shown by the connection test — if it turns out to be ES 9, bump the package to the latest 9.x patch in the same shape. ES 7 is refused at connection-test time with a clear message. `NEST` is deprecated and is not used.

### 2.2 Layers

| Layer | Knows about | Does not know |
|---|---|---|
| `DatabaseEndpoints` (existing) | SQL providers | Elasticsearch |
| `DatabaseElasticsearchEndpoints` (new) | provider check → `IElasticsearchReadService` | SQL, adapters |
| `ElasticsearchReadService` | `ElasticsearchClient` | HTTP, MCP, DTOs |
| `IDatabaseAdapter` | SQLite, MySQL, PostgreSQL | Elasticsearch |

Both endpoint files map routes onto the **same** `MapGroup("/api/databases")`; the split is file size, not URL shape.

### 2.3 Connection lifetime

`ElasticsearchClient` is thread-safe and intended to be shared, like the Redis multiplexer, but ES has no expensive handshake: an HTTP client with a 5 s `RequestTimeout` is enough. A bounded singleton cache keyed by `connection.Id` (max 8 entries, 5-minute idle eviction, invalidated on PUT/DELETE of the connection — same shape as `RedisConnectionPool`) avoids paying DNS/TLS per click and matches the existing pattern. Invalidation on connection edit is mandatory: a rotated password must not keep working against the cached client.

### 2.4 Read-only by construction

The service exposes exactly seven operations: `PingAsync` (test), `ListIndicesAsync`, `GetIndexDetailAsync`, `DocumentsAsync`, `HistogramAsync`, `SearchAsync`, `CountAsync`. Every one of them maps to a read-only ES API (`GET /`, `_stats`, `_mapping`/`_settings`, `_search`, `_count`). **There is no method that can write.** The two POST routes (`/es/search`, `/es/count`) are both inherently read-only APIs; the guard is the route map, not the body parser. `update_by_query`, `delete_by_query`, `_bulk`, and `_reindex` are not mapped anywhere, and `POST /{id}/mutate` continues to refuse ES connections with 400.

---

## 3. Data Model & Storage

### 3.1 Provider

```csharp
public enum DatabaseProvider
{
    Sqlite,         // 0
    MySql,          // 1
    Redis,          // 2
    PostgreSql,     // 3
    Elasticsearch   // 4  -- append only; existing rows keep 0-3
}
```

`JsonDatabaseConnectionStore` writes enums as numbers, so **Elasticsearch must be appended and never reordered**. No migration; old files keep loading.

### 3.2 Connection string

Stored like the others: one opaque provider connection string in `databases.json`, plaintext at rest, never returned by the API (parent spec §3.3).

Format: **an absolute URI with optional basic-auth userinfo** —

```
http://127.0.0.1:9200/                     (no auth)
http://user:pass@127.0.0.1:9200/           (basic auth)
https://user:pass@es.example.com:9200/        (TLS — raw string only)
```

- **Manual fields** (`ManualConnectionFields`): `Host` + `Port` (default **9200**) → authority; `Username` + `Password` → percent-encoded userinfo; `Database` and `FilePath` are not meaningful for ES and are ignored by the form (the form hides them). Credentials are percent-encoded on build (`user@host` with a `:` or `@` in the password must survive the round trip) and decoded on parse — a stored string that cannot be parsed returns `null`, exactly as SQL does.
- **Build** constructs a `System.Uri` (scheme `http` for manual mode, `https` allowed only from raw strings) and renders it; **Parse** reads `Uri.UserInfo` back into `Username`/`Password`.
- A `username` without `password` (or vice versa) is valid — it round-trips to `user@host` and the cluster answers 401 on test with a clear message.
- **API key auth is out of scope** for this phase: an API key has no place in a URI's userinfo without lying about the scheme. Follow-up (§10): an `ApiKey` property on the raw connection string, e.g. `...?api_key=...`, handled in `Build`/`Parse` only, never shown in the form.
- Verified live behavior: the target cluster answers **401** with `WWW-Authenticate: Basic realm="security", charset="UTF-8"` and `ApiKey`, so it requires credentials. Basic-auth credentials are known to the user (never written to this document, the repo, or any memory); the connection test must surface 401 as a distinct, actionable failure ("authentication failed (401): the cluster requires credentials — check username/password"), not as "connection refused". A real round-trip is verified **after implementation** via the §7 checklist, using the user's credentials through a secret card; CI and the test suite stub the transport and never touch the network.

### 3.3 Response models (wire, not storage)

Nothing about the cluster is persisted. These shapes exist only in responses:

```csharp
public sealed record ElasticsearchIndexInfo(
    string Name, string Health, string Status,
    long DocsCount, long StoreSizeBytes, int PrimaryShards, int Replicas);

public sealed record ElasticsearchIndexDetail(
    string Name, JsonElement Mappings, JsonElement Settings,
    long DocsCount, long StoreSizeBytes);

public sealed record ElasticsearchHit(
    string Index, string Id, double? Score, JsonElement Source);

public sealed record ElasticsearchSearchResult(
    long Total, long TookMs, IReadOnlyList<ElasticsearchHit> Hits,
    JsonElement? Aggregations);

public sealed record ElasticsearchHistogramBucket(long KeyMs, long DocCount);

public sealed record ElasticsearchDocumentPage(
    long Total, int From, int Size, long TookMs,
    IReadOnlyList<ElasticsearchHit> Hits);
```

`Mappings`, `Settings`, and hit `Source` are raw `JsonElement` — the UI renders them verbatim and does not project columns. Hits also carry `Index` (always, so wildcard `logs-*` searches stay attributable) and `Score` exactly as ES returns it (0 or 1.0 for non-scoring queries); the UI ignores it except when a DSL query sorts by relevance. `Total` is `track_total_hits=true` (exact count, capped reporting at 10,000 by ES with `relation: gte` handled by reading `TotalHits.Value` as a lower bound when `gte`; the wire reports the number ES returned).

---

## 4. API Surface

### 4.1 Routes

| Method | Route | Purpose |
|---|---|---|
| `GET` | `/api/databases/{id}/es/indices?includeSystem=false` | list indices with stats |
| `GET` | `/api/databases/{id}/es/index?index=name` | mapping + settings + stats for one index |
| `GET` | `/api/databases/{id}/es/documents?index=X&from=0&size=25&q=&start=&end=&field=&filters=[...]&logic=and` | paged `_search` `match_all` (+ optional `query_string` filter + time range + structured filters) |
| `GET` | `/api/databases/{id}/es/histogram?index=X&q=&start=&end=&field=@timestamp&buckets=60&filters=[...]&logic=and` | hit count per time bucket (`auto_date_histogram`) |
| `POST` | `/api/databases/{id}/es/search` | read-only DSL console: `{ index, query?, sort?, size?, from?, sourceIncludes?, aggs? }` |
| `POST` | `/api/databases/{id}/es/count` | read-only DSL console: `{ index, query? }` |

Notes that are not negotiable:

- **`index` travels in the query string, never as a path segment.** Index names can contain `*` (wildcards) and `,` (multi-index) — legal in query strings, and the same reasoning as the Redis key rule.
- `size` is clamped to **1–100** (default 25); `documents`' `from` is clamped so `from + size ≤ 10,000`. `q` (when present) becomes a `query_string` query ANDed with `match_all`; when absent, pure `match_all`. `start`/`end` (ISO-8601 or epoch ms) become a `range` filter on `field` (default **`@timestamp`**) and apply to both `documents` and `histogram`; paging stays `from`/`size`.
- **`filters`** (optional, JSON-encoded array, URL-encoded) is accepted by both `/es/documents` and `/es/histogram` and merges with `q`/`start`/`end` as one `bool.filter` — e.g. `filters=[{"field":"kubernetes.container_name","op":"is","value":"api"},{"field":"level","op":"is","value":"error"}]`. Format: `[{ "field": string, "op": "is"|"contains"|"gt"|"gte"|"lt"|"lte"|"exists", "value"?: string }]`. Rules: `field` must match `^[A-Za-z0-9_.@-]{1,255}$`; `op` outside the enum is 400; `exists` takes no `value`; range ops with a non-numeric `value` on a numeric target are 400 (ES surfaces type errors as 400 with its reason). Composition: default **AND** (`bool.must` of the rows); pass `logic=or` to switch the rows to `bool.should` with `minimum_should_match: 1`. The builder UI (§5) emits exactly this shape — the server never sees a "builder", only rows.
- Operator → DSL mapping: `is` → `term` (exact; `.keyword` sub-fields work — the builder reads top-level mapping properties so it can offer the `.keyword` variant explicitly when a text field has one); `contains` → `match` with `operator: and`; `gt`/`gte`/`lt`/`lte` → `range`; `exists` → `exists`. Lucene mode stays for everything this cannot express — the toggle is a choice of interface, not a second query language.
- Every route rejects a non-ES connection with **400** and a message naming the provider (`ElasticsearchRequirement.NotElasticsearch`). `/schema`, `/query`, `/browse`, and `/{id}/keys` reject ES with 400 and advice ("use `es_mapping` / `es_search`"). `POST /{id}/mutate` rejects ES with 400 ("ES connections are read-only").
- Wildcards and `_all` are **allowed** for `index` on all read routes: read-only is the contract, and an agent asking for `logs-*` is a legitimate question. The response says which indices were hit (ES reports `_shards`; the UI notes it).
- A cluster that answers 401/403/5xx surfaces as **502/503 with the ES reason** (status from the 7xx–5xx family maps to 502; network/timeout maps to 503 "elasticsearch unavailable"), never as an empty 200 and never as a 500 with a stack trace.
- `includeSystem` defaults to **false**: indices whose name starts with `.` are excluded unless explicitly requested.

### 4.2 `GET /{id}/es/indices`

```json
[
  { "name": "logs-app-2026", "health": "green", "status": "open",
    "docsCount": 124312, "storeSizeBytes": 48211320,
    "primaryShards": 1, "replicas": 1 }
]
```

One `_stats` call (`indices.stats`), no per-index fan-out. Sorted by name.

### 4.3 `GET /{id}/es/index?index=name`

```json
{
  "name": "logs-app-2026",
  "mappings": { "properties": { "message": { "type": "text" }, "@timestamp": { "type": "date" } } },
  "settings": { "index": { "number_of_shards": "1", "number_of_replicas": "1" } },
  "docsCount": 124312,
  "storeSizeBytes": 48211320
}
```

Three cluster calls (`_mapping`, `_settings`, `_stats` for that index) composed into one response. A missing index answers **404** with the ES reason, not an empty 200.

### 4.4 `GET /{id}/es/documents?index=X&from=0&size=25&q=&start=&end=&field=`

```json
{ "total": 124312, "from": 0, "size": 25, "tookMs": 12,
  "hits": [ { "index": "logs-app-2026", "id": "abc123", "score": 1.0, "source": { "message": "...", "@timestamp": "..." } } ] }
```

- `_search` with `match_all` (optionally `bool.filter: [{ query_string: { query: q } }, { range: { [field]: { gte: start, lte: end } } }]`), `track_total_hits: true`, no sort (`_doc` order — cheapest stable-ish order for browsing).
- `q` is verbatim `query_string` (Lucene syntax); ES's own parse errors surface as 400 with the ES reason.
- `filters`, when present, is decoded and compiled to `bool` clauses and merged into the same `filter` array as `q` and the time range — the request never carries both a Lucene string and structured rows without both being applied.
- `start`/`end` are optional; when both are absent **and no `field` is passed**, the query runs over all time and **no mapping validation is performed** — browsing an index with no date-typed field keeps working. When `start`/`end` (or an explicit `field`) is supplied, `field` must map to a `date` in the index mapping, otherwise 400 with a message naming the field and the mapping type it was expected to be (the friendly message may be produced by reading `_mapping` on that field or by translating ES's parse exception — builder's choice).
- `_source` is returned verbatim; the UI truncates display at 64 KB per cell with a "truncated" marker (display-only; the wire is complete unless ES itself truncates).

### 4.5 `GET /{id}/es/histogram?index=X&q=&start=&end=&field=@timestamp&buckets=60`

```json
{ "field": "@timestamp", "interval": "day",
  "buckets": [ { "keyMs": 1760000000000, "docCount": 1204 }, { "keyMs": 1760086400000, "docCount": 3855 } ] }
```

- One `_search` with `size: 0` and an `auto_date_histogram` aggregation on `field` (target bucket count = the `buckets` query param, clamped to 1–100, default 60). The bucket width (`interval`) is whatever ES chose (`minute`, `day`, …); the wire returns the buckets, ascending by `keyMs`.
- Same filter envelope as `documents` (`q` + optional `start`/`end` range + `filters`). Unlike `documents`, the histogram **always** validates `field` (default `@timestamp`) as a `date` — `auto_date_histogram` cannot run on a non-date field, so 400 with a message naming the field is unconditional here.
- `buckets` is empty when no document matches the filter.
- This route exists for the dashboard's mini-Discover chart. Agents get the same data with `es_search` + `aggs` — no MCP twin.

### 4.6 `POST /{id}/es/search`

```json
{ "index": "logs-app-2026",
  "query": { "match": { "level": "error" } },
  "sort": [ { "@timestamp": "desc" } ],
  "size": 25, "from": 0,
  "sourceIncludes": ["message", "level"] }
```

Response: `{ "total": 812, "tookMs": 9, "hits": [ { "index": "logs-app-2026", "id": "...", "score": 0.87, "source": { ... } } ], "aggregations": { ... } }` (`aggregations` present only when `aggs` was sent).

- `query`, `sort`, and `sourceIncludes` are raw `JsonElement` passed through to `_search` — **except** ES control keys (`size`, `from`, `index`, `request_cache`) are taken from the envelope, never from inside `query`/`sort` (they are siblings of `query` in the ES body; the envelope fields win and are clamped).
- Missing `query` = `match_all`. No `index` = 400.
- **Aggregations are supported** as a top-level `aggs` object (raw passthrough, sibling of `query`), returned as `aggregations` (raw `JsonElement`) alongside `hits`. Aggregations are the most valuable read-only ES feature for an agent and cannot mutate anything.
- Scripts inside queries (`script` aggs, Painless field scripts) execute server-side but cannot write to the index; they are allowed. The audit log records the full request body (truncated to 4 KB) for every `es/search` call.

### 4.7 `POST /{id}/es/count`

```json
{ "index": "logs-*", "query": { "range": { "@timestamp": { "gte": "now-1d" } } } }
→ { "count": 18233, "tookMs": 4 }
```

Missing `query` = `match_all`.

### 4.8 MCP ↔ endpoint mapping

The MCP surface and the HTTP surface are the same read-only capabilities, two shapes. Four tools, all thin wrappers over the same `IElasticsearchReadService`:

| MCP tool | HTTP endpoint | Notes |
|---|---|---|
| `es_indices` | `GET /es/indices` | same handler shape; `{ id, includeSystem? }` → `ElasticsearchIndexInfo[]` |
| `es_mapping` | `GET /es/index?index=` | same call, but projects to `{ name, mappings, settings }` — drops stats, because mapping is the question |
| `es_search` | `POST /es/search` | same envelope (`query?`, `sort?`, `aggs?`, `size?`, `from?`, `sourceIncludes?`); `size` clamped to **1–50** (vs 1–100 for the dashboard) so an agent can't dump a page into its context |
| `es_count` | `POST /es/count` | same body, `{ count, tookMs }` |

Routes deliberately **without** an MCP twin:

| HTTP route | Why no tool | Reachable by an agent as |
|---|---|---|
| `GET /es/documents` (paged grid) | UI affordance; an agent wants one page, which is what `es_search` returns | `es_search` with the same `q`/`start`/`end` filters folded into the envelope (`filter` array in `query` or a bool query) |
| `GET /es/histogram` | UI chart data; not a question an agent asks directly | `es_search` with `size: 0` + an `aggs: auto_date_histogram` block |
| `POST /{id}/mutate` / any write | none exist, by construction (§2.4) | — |

The audit actions are shared by construction: both surfaces call the same service, and `DatabaseAuditActions` names the **MCP tool** for both — so `/es/documents`, `/es/histogram`, and an agent calling `es_search` all read back as `es_search` in the trail (§6), and the route is recoverable from the connection id + index recorded alongside.

`db_list_connections` — unchanged; ES rows appear automatically. `db_get_schema` and `db_query` — reject ES: "connection 'X' is an Elasticsearch cluster; use es_mapping / es_search". `db_browse`-backed grid — rejects with 400. AppSettings-detection for ES keys is **not part of this phase** (deferred; see §10). No `es_command`-style escape hatch: the DSL surface is exactly the four tools above.

---

## 5. Dashboard

Provider dropdown gains **Elasticsearch**. Manual form: host, port (default 9200), username, password; the `database` and `file path` fields are hidden for this provider; helper text: "No TLS in manual mode — use raw connection string mode for `https://`."

Tree: SQL connections show table rows (unchanged). An ES connection shows **one row per index** — name with `N docs` as the subtitle (spelled out, like `3 keys`), system indices hidden behind a toggle in the tree header (".system indices"). An index row opens a tab of kind `elasticsearch-index` keyed by `connId + index` (two indices of one cluster side by side; the tab-key comparison rules from the Redis tree plan apply verbatim).

Right panel, three tabs per index:

1. **Discover** (the log-search tab, modelled on Kibana Discover but read-only and minimal) — top row: a mode toggle **Filter | Lucene**. In **Lucene mode**: a query bar taking Lucene `query_string` syntax (the helper text says so, because KQL is not accepted). In **Filter mode**: the structured builder — rows of `[field dropdown | operator dropdown | value input]`, one row per condition with a delete button, an "+ add filter" button, an AND|OR toggle above the rows, and a `.keyword` hint appended to the operator tooltip for text fields that have a `.keyword` sub-field; a `date`-field-aware value input (ISO-8601 or epoch ms placeholder) for range operators. Field dropdown options come from the mapping's top-level properties (dotted paths included, e.g. `kubernetes.container_name`); the mode, rows, AND/OR choice, selected fields, and time range are all persisted per `connId + index` in localStorage like the rest of the panel state. Both modes compile to the same `filters`/`q` envelope (§4.1) — the grid, histogram and selected-fields behaviour are identical either way. After the row: a time-range preset selector (**15m / 1h / 24h / 7d / all time**), plus a date-field selector shown only when the mapping has more than one `date` field (default `@timestamp`). Below the bar: a **histogram bar chart** of hits per bucket (§4.5, ~60 buckets, inline SVG bars — no chart library dependency; clicking a bucket narrows the time range to that bucket). Below the chart: the results grid — paged (25/page, Prev/Next + "page N of M"), `_id` pinned as the first column, an `index` column shown automatically when the search targets a wildcard/multi-index (hidden for a concrete index), then the **selected fields** as columns (multi-select picker fed from the mapping's top-level `properties`, persisted per `connId + index` in localStorage; defaults to the first 6 top-level scalar fields). The picker is the Discover equivalent of Kibana's "selected fields"; documents whose source lacks a selected field render an empty cell. Row click expands the full `_source` as pretty JSON in a side drawer.
2. **Mapping** — pretty-printed `mappings` JSON and `settings` JSON in two collapsible sections, plus a header line `N docs · S B · shards P/R`.
3. **Query** — DSL textarea, `size` and `aggs` optional, Run button, result as pretty JSON (hits + aggregations). Client-side only validation: must parse as JSON object; server errors render inline with the ES reason.

Empty/error states follow the SQL/Redis conventions (a cluster that is down shows the error with the rows it already had, never a blank panel).

---

## 6. Audit

New actions in `DatabaseAuditActions`, spelled as the MCP tool names: `es_indices`, `es_mapping`, `es_search`, `es_count`. `/es/documents` and `/es/histogram` audit as `es_search` (same operation, browsing/chart mode). Every entry records provider, connection id, index (never the full query body at info level — `es/search` records a truncated 4 KB body copy at debug level only), outcome, and `ResultCount` (number of indices / hits / the count). Failure paths record the ES status code. No document `_source` is ever written to the audit trail at info level.

---

## 7. Test Strategy

- **Core unit tests** (pure): URI build/parse round trips including percent-encoded passwords; no-auth, user-only, user+pass; enum pin (`Elasticsearch == 4`, existing values unchanged); `ElasticsearchRequirement` messages.
- **Backend endpoint tests** with `WebApplication.CreateSlimBuilder` + `UseTestServer` and a stubbed `IElasticsearchReadService` (pattern from `RedisDatabaseListEndpointTests`): 404 unknown id, 400 non-ES provider, 502 on ES failure, 404 on missing index, envelope shapes for all six routes, `includeSystem` filter, `from+size` clamp, `start`/`end` → `range` filter on the right field, documents with no `start`/`end` and no `field` → 200 on a dateless index, documents with `start` supplied → 400 on a non-date field, histogram on a non-date field → 400 always, `filters`: valid AND/OR compile to the right `bool` shape, bad `op` → 400, bad `field` charset → 400, `exists` without `value` → 400, non-numeric `value` on a range op → 400.
- **MCP tool tests** with the same harness: each tool rejects non-ES connections and returns the documented shape.
- **Convention tests**: one type per file, no tuples, records chopped one parameter per line — plus a **zero-mutation pin**: a test asserts `IElasticsearchReadService` has no write-shaped member names (`Index*Async`, `Update*Async`, `Delete*Async`, `Bulk*Async`, `Reindex*Async`, `Put*Async`, `Create*Async`) and that every route mapped under `/api/databases/{id}/es/` is GET or a POST to the two read-only DSL endpoints. If a future maintainer adds a write, this test fails before review does.
- **No network in tests.** One **manual smoke checklist** ships in the plan for after implementation: register the cluster with the user's basic-auth credentials (entered via a secret card — never into the repo or a memory), test connection (expect version), list indices, open the target index, run a `match_all` browse page, one scoped DSL query, one histogram render, verify 401 failure messaging with a deliberately wrong password. This checklist is the only verification of the live cluster; it is explicitly **not** automatable in CI.

---

## 8. Approaches Considered

- **A — ES-as-SQL via the `_sql` endpoint** (implement `IDatabaseAdapter`): maximum reuse (grid, caching, `db_query` all free), but ES SQL is a lossy read-only subset with `.keyword`/text quirks, no real DSL, no aggregation ergonomics, and a second query surface to guard. **Rejected.**
- **B — Redis-style dedicated read path (chosen):** native DSL, read-only enforceable by construction (no write method exists), consistent with the one non-SQL precedent in this repo, dashboard tree needs new work but matches the Redis tree plan. Costs: one new service + six routes + four MCP tools + a new panel component.
- **C — Hybrid (SQL adapter for the grid + DSL console):** two query surfaces, two guards, two audit stories, and SQL on ES would silently mislead users about what ES can express. **Rejected.**

---

## 9. Constraints & Non-Goals Recap

- Read-only everywhere; zero mutation surface, and no mutation work is planned in the foreseeable future. No route, tool, or method in this spec can mutate a cluster, and `/es/` contains no write verb at all. `POST /{id}/mutate` rejects ES connections with 400.
- `from + size ≤ 10,000` (ES `max_result_window`) — deeper paging is a follow-up with PIT/`search_after`.
- Basic auth and no-auth only; TLS only via raw `https://` strings; API keys later.
- ES 8 only. ES 9 is a package bump, not a redesign.
- No caching of cluster data in v1.
- The structured filter builder (Filter mode) is a UI affordance over one wire contract (`filters` + `logic`, §4.1) — no separate query language, no duplicate endpoint; Lucene mode remains for everything the builder cannot express.

---

## 10. What's Left to the Builder

- Exact `8.19.x` patch pin to use at implementation time (whatever is newest then).
- Client pool sizing/idle eviction constants if a real hot path shows up; the 5-minute idle rule from §2.3 unless measurements say otherwise.
- The histogram's click-to-zoom interaction (click a bar → time range narrows to that bucket) and the exact inline-SVG bar rendering are left to the builder; keep them dependency-free.
- Filter-builder operator details (match `operator: and` for `contains`, the `.keyword`-sub-field hint in field dropdowns, the localStorage shape for persisted rows) are builder-level — the wire contract (`filters` / `logic`) is fixed in §4.1.
- The follow-up for **API-key auth** (§3.2) and **TLS manual-mode toggle** (§1 out-of-scope) — both are additive to Build/Parse and the form, no endpoint changes.
- Verification against the live cluster runs **after implementation** (§7 checklist) with the user's basic-auth credentials, which are known but never recorded here or in the repo; until the checklist runs, live behavior is unverified.
- **AppSettings scan for ES keys is not part of this phase** — deferred until the user asks for it; the scanner stays as-is, ES connections are registered manually.
- **No mutation work is planned.** The read-only contract is standing, not transitional; a future mutation capability is out of the roadmap for this work and would start from a new design discussion.
