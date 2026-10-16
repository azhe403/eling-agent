# DB Connection Management — Design Specification (SQLite + MySQL)

- **Date:** 2026-10-13
- **Status:** Proposed (awaiting user review)
- **Target Projects:** `Eling.Core`, `Eling.Backend`, `tests/Eling.Core.Tests`, `tests/Eling.Backend.Tests`
- **Supersedes:** the DB-connection scope of `docs/superpowers/specs/2026-09-19-eling-db-management-design.md` — this spec narrows Phase 1 to SQLite + MySQL with read-only query execution. Mutation capabilities from that spec are **deferred** (see §8).

---

## 1. Overview & Scope

Eling Backend gains a database connection registry: users register named connections (SQLite or MySQL), test them, browse their schema, and run read-only queries against them through the REST API. Connection configs persist as JSON on the backend host; credentials never leave the host.

**In scope (this phase):**

- `DatabaseConnection` domain model + validation (`Eling.Core`)
- `IDatabaseAdapter` with SQLite and MySQL implementations: connect/test, query execution, schema introspection (`Eling.Core`)
- `DatabaseAdapterFactory` (provider → adapter; unsupported providers fail with a clear error)
- Persistent connection registry (`JsonDatabaseConnectionStore`) with cross-process-safe writes
- REST endpoints under `/api/databases` (CRUD, test, schema, read-only query)
- Read-only SQL classification (`SqlSafety`) — the shared guard that a later mutation/MCP phase reuses
- **Auto-detect + confirm-gated import** of connections from the backend's `appsettings.json` (`ConnectionStrings` section): detect returns a masked preview only; import takes the approved `settingsKeys` and the backend reads its own file — no credential ever crosses the API/MCP boundary. Nothing is persisted without an explicit import call. Manual add/edit/delete remains fully available.
- **MCP read-only tools** (`Eling.Backend/Mcp/Tools/`): `db_list_connections`, `db_get_schema`, `db_query` (read-only via the same `SqlSafety` guard as the REST query endpoint). **The data-changing MCP tool (`db_execute_mutation`) is deferred** (§8).
- **MCP auto-detect + import tools**: `db_detect_connections` (read-only preview) and `db_import_connections` (takes approved `settingsKeys` only; writes the **local registry**, a config write, not a target-data mutation). Conversational approval is the gate: the agent detects, asks, and imports exactly the keys the user approved (§4.1 step 5). **The MCP surface is credential-free — agents see identity (id, `db@host` name) but never username, password, or connection strings (§4.2).**

**Out of scope (deferred, flagged — see §8):** mutations (INSERT/UPDATE/DELETE/DDL) via API or MCP, the MCP mutation tool `db_execute_mutation`, human approval gate for mutations, Dashboard/Desktop GUI, PostgreSQL/SQL Server adapters, credential encryption at rest.

---

## 2. Architecture & Components

Follows the existing repo split: domain + adapters in `Eling.Core`; store wiring + HTTP in `Eling.Backend`.

```
Eling.Core/Database/
  DatabaseConnection.cs          record + validation (name, provider, connection string, environment preset)
  DatabaseProvider.cs            enum: Sqlite, MySql
  DatabaseEnvironment.cs         enum: Development, Staging, Production, ReadOnly
  IDatabaseAdapter.cs            TestAsync / QueryAsync / GetSchemaAsync
  QueryResult.cs                 columns + rows + truncated flag
  DatabaseSchemaInfo.cs          tables → columns, PK, FK, indexes
  SqlSafety.cs                   read-only classifier (static)
  AppSettingsConnectionScanner.cs  parse ConnectionStrings from a JSON file → detected candidates (no persistence)
  DetectedConnection.cs          record(SettingsKey, Name, ConnectionString, MaskedConnectionString, Provider?, RejectionReason?) — Name derived as {db}@{host}
  Adapters/SqliteDatabaseAdapter.cs    Microsoft.Data.Sqlite (already referenced)
  Adapters/MySqlDatabaseAdapter.cs     MySqlConnector (new package)
  Adapters/DatabaseAdapterFactory.cs   provider → adapter; unknown → NotSupportedException
  IDatabaseConnectionStore.cs          registry interface (Core-side port, same pattern as IWorkspacesRegistry)
  JsonDatabaseConnectionStore.cs       registry store (pattern: JsonWorkspaceRegistry)

Eling.Backend/
  Endpoints/DatabaseEndpoints.cs            minimal-API group /api/databases
  Database/DatabaseConnectionImporter.cs    shared import logic (validate → Create → upsert; per-candidate results)
  Dtos/DatabaseDtos.cs                      request/view DTOs (masked views; REST surface — reused by MCP where they hold no credentials)
  Dtos/DatabaseMcpDtos.cs                   agent-safe detect DTO (index + settingsKey + name + provider; no strings)
  Mcp/Tools/DatabaseListConnectionsTool.cs  MCP tool db_list_connections (masked output — no connection strings)
  Mcp/Tools/DatabaseGetSchemaTool.cs        MCP tool db_get_schema
  Mcp/Tools/DatabaseQueryTool.cs            MCP tool db_query (read-only, same SqlSafety guard as REST)
  Mcp/Tools/DatabaseDetectConnectionsTool.cs  MCP tool db_detect_connections (read-only preview, masked output)
  Mcp/Tools/DatabaseImportConnectionsTool.cs  MCP tool db_import_connections (settingsKeys only — registry write for user-approved keys)
  Mcp/ServerInstructions.cs                 one section entry describing the db tools (edit)
  Bootstrap/DashboardServices.cs            DI registration (edit)
  Bootstrap/DashboardRoutes.cs              app.MapDatabaseEndpoints(); (edit)
```

**Registration vs. runtime:** the registry is the source of truth for what connections exist. Adapters are created per operation from the stored connection (no connection pooling phase in v1; each operation opens, uses, and closes its connection).

---

## 3. Data Model & Storage

### 3.1 Model

```csharp
public sealed record DatabaseConnection(
    string Id,                 // ULID
    string Name,
    DatabaseProvider Provider, // Sqlite | MySql
    string ConnectionString,   // plaintext at rest; NEVER returned by the API
    DatabaseEnvironment Environment,
    bool AllowAgentMutations,
    bool RequireApprovalForMutations,
    DateTimeOffset CreatedAt,
    DateTimeOffset? LastConnectedAt);
```

Validation (in a static `DatabaseConnection.Create(...)` factory that throws `ArgumentException` — the record itself stays a dumb data holder): `Name` non-empty; `ConnectionString` non-empty; provider and environment must be defined enum values. `AllowAgentMutations`/`RequireApprovalForMutations` are stored for the deferred mutation phase; **this phase performs no enforcement based on them** (they are recorded config, not gates).

### 3.2 Storage

- File: `databases.json` under `CentralAgentDirectory.Resolve()` — the same central location as `agent-workspaces.json` and `agent-provider.json` (NOT `~/.eling/` as the 2026-09-19 spec said; the repo moved host-wide JSON state to the central dir).
- Store follows `JsonWorkspaceRegistry` exactly:
  - Named cross-process mutex `Global\eling-databases-registry`, bounded wait (5s), `AbandonedMutexException` = ownership acquired.
  - Atomic writes: temp file + `File.Move(overwrite: true)`.
  - Every mutation re-reads under the gate; corrupt/wrong-shape file = empty registry + warning log, rewritten on next write.
  - Logging via injected `ILogger`; no silent catches.
- Test isolation: tests point the store at a temp dir (`Path.GetTempPath()` + GUID). Backend endpoint tests use the existing `ELING_AGENT_DATA_DIR` seam.

### 3.3 Credential masking

API responses use view DTOs that carry `hasConnectionString: true` (never the string). The raw connection string is accepted only on create (POST) and update (PUT); on PUT an empty/omitted connection string means "keep stored value" (same semantics as `ProviderStore.Update`'s apiKey handling). Logs and error messages never include the connection string.

**Masking everywhere, including detect:** no API or MCP response ever carries a password-bearing connection string. Detect output masks the password (`Password=***`; a string no provider builder recognizes is masked wholesale to `"***"`). Import never takes a connection string at all — it takes the `settingsKeys` the user approved and the backend re-reads its own `appsettings.json` under the hood, so no credential crosses the API/MCP boundary in either direction. The only path that accepts a raw connection string is manual create/update (`POST`/`PUT`), where the user typed it themselves.

**Agent visibility boundary (MCP — see §4.2):** the REST API serves the same-host Dashboard and may show `{db}@{host}` names plus password-masked strings. The MCP surface serves the agent (possibly a cloud LLM) and may show the same identity — `connectionId`, name (`{db}@{host}`, host included), `databaseName`, `provider`, `environment` — because the host of an internal/LAN database is not a secret and the name is the user's own readability convention. It may **never** show credentials: no username, no password, no connection string in any form (raw or masked — even a masked string carries the user id), in outputs *or* error messages.

---

## 4. API Surface

`Endpoints/DatabaseEndpoints.cs`, minimal APIs, one `MapGroup("/api/databases")`, mapped in `DashboardRoutes.Map`. Wire JSON is camelCase (global option). Enum values serialize as lowercase strings via the registered `JsonStringEnumConverter`.

| Method | Route | Request | Response |
|---|---|---|---|
| GET | `/api/databases` | — | `200` array of views (masked) |
| POST | `/api/databases` | create DTO (incl. connection string) | `201` view; `400` validation failure |
| GET | `/api/databases/{id}` | — | `200` view / `404` |
| PUT | `/api/databases/{id}` | update DTO (empty connection string = keep) | `200` view / `404` / `400` |
| DELETE | `/api/databases/{id}` | — | `204` / `404` |
| POST | `/api/databases/{id}/test` | — | `200` `{ ok, message, latencyMs }` — probe failures are 200 with `ok:false`, not 5xx |
| GET | `/api/databases/{id}/schema` | — | `200` schema info / `404` / `502` on provider error |
| POST | `/api/databases/{id}/query` | `{ sql, maxRows? }` | `200` `QueryResult`; `400` rejected by `SqlSafety`; `502` provider error |
| POST | `/api/databases/detect` | — | `200` candidates array with **masked** connection strings — **preview only, nothing persisted**; reads `{content root}/appsettings.json` only (local file, no network, no arbitrary paths) |
| POST | `/api/databases/import` | `{ settingsKeys: [...] }` — the approved candidate keys | `200` per-candidate result `{ settingsKey, name?, imported, reason? }` |

`maxRows` default 100, hard cap 1000 (response carries `truncated: true` when the cap applies).

### 4.2 Agent visibility boundary (MCP)

The five MCP tools are the only surface an agent touches. They may carry a connection's **identity** — `connectionId`, `name` (the `{db}@{host}` convention, host included), `databaseName`, `provider`, `environment` — so the user can read and reference connections naturally ("query orders@10.0.0.5"). They may **never** carry **credentials**: no username, no password, no connection string in any form (raw or masked — even a masked string carries the user id), in parameters, outputs, or error messages. Errors are category strings ("query failed", "connection failed"); details live in backend logs only.

| Tool | Agent sees | Agent never sees |
|---|---|---|
| `db_list_connections` | `connectionId`, `name` (`db@host`), `provider`, `environment` (reuses the REST view DTO — it holds no credential material) | username, password, any connection string |
| `db_get_schema` | schema metadata for the given `connectionId` | everything above |
| `db_query` | columns + rows of the executed read-only query | everything above (results are the user's own queried data) |
| `db_detect_connections` | `index`, `settingsKey`, `name` (`db@host`), `provider` — handles for approval | username, password, connection strings (raw or masked) |
| `db_import_connections` | in: approved `settingsKeys`; out: `connectionId` + `name` per result | username, password, any connection string |

Candidates are referenced by `index`/`settingsKey` during approval, so two same-named databases on different hosts stay distinguishable (`orders@10.0.0.5` vs `orders@10.0.0.9`). `provider` and `environment` are non-credential metadata the agent needs to give useful advice.

### 4.1 Auto-detect & import flow

> **HARD RULE: nothing is ever imported without explicit user approval.** Detect never writes. There is no startup job, no watcher, no background service that imports on its own. The only write path is `POST /api/databases/import` carrying the candidates the user explicitly approved (later: ticked in the Dashboard import dialog). "Asal main import" is impossible by construction.

0. **Source is fixed and local**: the scanner's file path is injected at DI time — default `{content root}/appsettings.json` (backend host's own content root; `IHostEnvironment.ContentRootPath`, so it resolves in both HTTP and MCP-only hosts), overridable for tests/edge deployments by the `ELING_DATABASE_DETECT_SOURCE` environment variable. The detect endpoint takes no path parameter — no arbitrary local reads, no network, no remote config.
1. **Detect**: scanner reads the JSON file, takes the `ConnectionStrings` section (case-insensitive), and classifies each entry by parsing with `SqliteConnectionStringBuilder` / `MySqlConnectionStringBuilder` (marker keys: `DataSource` for SQLite, `Server`+`Database` for MySQL). Unrecognized entries come back as candidates with `provider: null` + reason — visible but not importable. Missing/invalid file → `200` empty array (not an error).
2. **Naming (import-only convention)**: each candidate's `name` is derived as **`{db}@{host}`** at detect time — MySQL: `{Database}@{Server}` (non-default port appended as `:{port}`; e.g. `orders@10.0.0.5:3307`); SQLite: `{fileName-without-extension}@local` (e.g. `eling-index@local`). If derivation fails (missing db/host), fall back to the appsettings key as the name. The original key stays visible as `settingsKey` on the candidate. Manual create/update is **not** bound by this convention — free-form names remain allowed. Candidates carry a **masked** connection string (`Password=***`; unparseable → `"***"`); the raw string never leaves the backend.
3. **Confirm → import**: the user approves by naming candidates — by `settingsKey` (e.g. "import Orders"). The caller sends `{ settingsKeys: [...] }`; the backend re-scans the source file itself, picks those keys, re-validates each through `DatabaseConnection.Create`, and writes with `Environment = ReadOnly`, `AllowAgentMutations = false`, `RequireApprovalForMutations = true`. **Import carries keys, not connection strings** — the backend reads its own file, so no credential crosses the API/MCP boundary in either direction (safe even when the MCP client is a cloud-hosted LLM). **No auto-persist without an explicit import call** — that call is the confirmation gate.
4. **Per-candidate result**: unknown key → `{ imported: false, reason: "not found in source" }`; unrecognized connection string → `{ imported: false, reason: "unrecognized connection string" }`; derived name already in the registry → `{ imported: false, reason: "already exists" }` — imports never silently overwrite. A successful row carries the registered `name`. Environment-specific files (`appsettings.Development.json` etc.) are **not** scanned in v1 (follow-up noted in §10).
5. **Conversation flow (agent-as-UI, while the GUI is deferred):** the phase-1 MCP tools make chat a first-class consumer of this feature. `db_detect_connections` is read-only and safe to run whenever asked; its output carries `index`, `settingsKey`, `name` (`db@host`), and `provider` — identity only, never credentials. `db_import_connections` runs **only** after the user explicitly names the keys to register — the agent detects, shows the candidate list ("#1 — mysql, orders@10.0.0.5:3307; #2 — sqlite, eling-index@local — import which?"), and imports exactly the approved `settingsKeys`. A vague instruction ("set up my database") means detect + ask, never import. The deferred mutation tool (Phase 3) will carry its own explicit approval gate on top of this.

---

## 5. Read-Only Query Guard (`SqlSafety`)

Mutation support is **deferred** (§8), so the query endpoint is read-only in this phase.

- Input SQL is normalized first: strip `--` line comments and `/* */` block comments, and mask single-quoted string literals (so `SELECT 'delete'` is not rejected for containing the word).
- The first remaining keyword must be in the allowlist: **`SELECT`, `EXPLAIN`, `PRAGMA`**. Everything else — including `WITH` (which can wrap INSERT/UPDATE/DELETE) — is rejected in v1.
- A secondary scan rejects any mutation keyword token (`INSERT`, `UPDATE`, `DELETE`, `DROP`, `ALTER`, `CREATE`, `TRUNCATE`, `REPLACE`, `GRANT`, `REVOKE`, `ATTACH`, `DETACH`, `VACUUM`) appearing outside a masked literal.
- **PRAGMA is allowlisted wholesale (accepted risk, user decision):** most PRAGMAs are read-only, but a few (`journal_mode`, `user_version`, `writable_schema`, `optimize`, `shrink`, `incremental_vacuum`) can mutate database state. The user accepted this trade-off for v1; Phase 3 (mutations) revisits it with a writable-PRAGMA blocklist. `ATTACH` is already rejected by the keyword scan, which is the dangerous one (it attaches another database file).
- `SqlSafety` returns a verdict with a rejection reason; the endpoint maps rejection to `400` with a message stating that mutations are not yet supported.
- The classifier lives in `Eling.Core` (not in the adapters) so the deferred mutation/MCP phase reuses it without touching adapter code.
- One guard, two callers: the REST `POST /api/databases/{id}/query` and the MCP `db_query` tool both run `SqlSafety` before executing. Neither can execute a mutation; the MCP tool returns an error result (never executes), the REST endpoint returns `400`.

---

## 6. Error Handling & Guardrails

| Failure | Behavior |
|---|---|
| Connection test fails (bad host/credentials/file) | `200 { ok: false, message }` — the message names the failure category only; connection string never included |
| Query rejected by `SqlSafety` | `400` with the classifier's reason |
| Query execution fails (syntax error, timeout) | `502` with a sanitized provider message |
| MCP tool failure (query/schema/test) | category-only error to the agent ("query failed"); full detail logged server-side, never surfaced |
| Registry file corrupt | Empty registry + warning log; next write rewrites |
| Unknown connection id | `404` |
| Invalid model on create/update | `400` with field-level message |
| Unsupported provider in factory | `NotSupportedException` → `400` (e.g. a stored row claiming postgresql) |
| Detect source file missing/corrupt | `200` empty candidates + warning log |
| Import key not found in source | per-candidate `{ imported: false, reason: "not found in source" }`, rest still imported |
| Import key with unrecognized connection string | per-candidate skip with the scanner's reason |
| Import derived name already in registry | per-candidate skip with reason — never overwrites |

- Every query/test runs with a bounded `CancellationToken` (default 30s), linked to the request abort.
- Every `catch` logs via `ILogger` (repo convention).
- SQLite relative paths are resolved to absolute before connecting (avoids the classic "unable to open database file" class of failures).
- MySQL connections use `MySqlConnector` defaults (no pooling config in v1).

---

## 7. Testing Strategy

**`tests/Eling.Core.Tests`** (per-csproj runs, `--artifacts-path .bin-test`):

- `DatabaseConnectionTests` — validation: blank name/connection string rejected; unknown enum values rejected; env presets accepted.
- `JsonDatabaseConnectionStoreTests` — CRUD round-trip; persists across instances; corrupt file → empty; masking view has no connection string; two instances in one process keep every row (mutex).
- `SqlSafetyClassifierTests` — allowed: `SELECT ...`, `EXPLAIN ...`, `PRAGMA ...`; rejected: `INSERT`, `UPDATE`, `DELETE`, `DROP`, `ALTER`, `CREATE`, `WITH ... SELECT`, `select` with mutation keyword later in the statement; `SELECT 'delete'` literal not rejected.
- `SqliteDatabaseAdapterTests` — temp-file SQLite: create table, test connection, query rows, schema introspection (tables/columns/PK).
- `AppSettingsConnectionScannerTests` — temp `appsettings.json` with one SQLite + one MySQL + one unrecognized entry; `ConnectionStrings` section missing → empty; corrupt JSON → empty + log; entry returned with `settingsKey`, derived `{db}@{host}` name, parsed provider, and a **masked** string (`Password=***`, never the raw password); MySQL with non-default port names as `{db}@{server}:{port}`.

**`tests/Eling.Backend.Tests`** (WebApplicationFactory + `ELING_AGENT_DATA_DIR` → temp dir):

- `DatabaseEndpointsTests` — full CRUD round-trip; **assert no response body contains the stored connection string**; test/schema/query happy path against a temp SQLite file; rejected query (`DELETE ...`) → `400`; unknown id → `404`; unknown provider row → `400`.
- `DatabaseDetectImportTests` — detect against a temp `appsettings.json` (sqlite + mysql + garbage) returns masked candidates; **no response body contains any password**; import by `settingsKeys` registers only those, as ReadOnly; unknown key → `"not found in source"`; re-import of the same name → skipped with reason; detect persists nothing (registry still empty).
- MySQL adapter: unit-level only (connection-string handling via `MySqlConnectionStringBuilder`); live MySQL testing is manual (noted in the implementation plan).

**`tests/Eling.Backend.Tests` MCP tool tests** (same fixture + temp SQLite file):

- `DatabaseToolsTests` — `db_list_connections` output shows ids + `db@host` names but contains **no username, password, or connection string**; `db_get_schema` returns the temp table's columns/PK; `db_query` executes a `SELECT`; `db_query` with `DELETE ...` returns an error result and executes nothing; a failing `db_query` returns a sanitized category message ("query failed"), never the provider exception text (which can embed user@host).
- `DatabaseDetectImportToolTests` — `db_detect_connections` against a temp `appsettings.json` (sqlite + mysql + garbage) returns candidates with `{db}@{host}` names + provider and **no username, password, or connection string (raw or masked)**, and **writes nothing to the registry**; `db_import_connections` with a subset of keys registers only that subset, as ReadOnly, and reports `connectionId` + `name` per result; re-import of the same name is skipped with reason; unknown key is skipped with `"not found in source"`.

---

## 8. Deferred Items (flagged, not removed)

These come from the 2026-09-19 spec but are **deferred — out of scope for this phase**:

| Deferred item | Source | Planned phase |
|---|---|---|
| Mutation endpoint (`POST /api/databases/{id}/mutate`) | 2026-09-19 §7 Phase 1 | **Phase 3** (with `SqlSafety` mutation classification) |
| MCP mutation tool `db_execute_mutation` | 2026-09-19 §5.4 | **Phase 3** — the read-only MCP tools ship in Phase 1; only the data-changing tool is deferred to the mutation phase |
| Human approval gate for agent mutations (`PendingMutationAction` + SSE + GUI approve/reject) | 2026-09-19 §5.4, §4.5 | **Phase 3** (ships together with the mutation tool) |
| Dashboard UI (`/dashboard/databases`: connection manager, import dialog, schema tree, SQL editor, data viewer) | 2026-09-19 §4 | **Phase 2** — read-only surfaces; the CRUD data grid + approval banner land in Phase 3 with mutations |
| Desktop UI parity | 2026-09-19 §4 | **Phase 2** (same read-only-first scope) |
| PostgreSQL (`Npgsql`) and SQL Server (`Microsoft.Data.SqlClient`) adapters | 2026-09-19 §3.2 | Later — factory already fails cleanly for them |
| Environment-preset enforcement (GUI write restrictions, MCP read-only per preset) | 2026-09-19 §2 | **Phase 3** — presets are stored and validated only in this phase; enforcement pairs with mutations |
| Credential encryption at rest (DPAPI/AES) | 2026-09-19 §3.1 | Follow-up hardening |
| Audit logging of executed queries | 2026-09-19 §6.3 | Follow-up |
| Audit logging of imports (who/when/which candidate) | New (this spec) | Follow-up — registry readback (`GET /api/databases`) is the interim verification |

---

## 9. Decision Log

| Decision | Choice | Rationale |
|---|---|---|
| Providers | SQLite + MySQL only | User scope; factory stays open for postgres/sqlserver |
| Query execution | Read-only (SELECT/EXPLAIN/PRAGMA) | User decision — mutations deferred |
| PRAGMA allowlist | All PRAGMAs allowed in v1; writable ones (journal_mode, user_version, …) accepted as a v1 risk, revisited in Phase 3 | User decision — pragma reads dominate; blocklist deferred to the mutation phase |
| Credential storage | Plaintext at rest, masked in API | Matches existing `ProviderStore`/`JsonWorkspaceRegistry` repo pattern; encryption is follow-up |
| Storage location | `CentralAgentDirectory/databases.json` | Consistent with `agent-workspaces.json` / `agent-provider.json` |
| Environment presets | In model, stored + validated, no enforcement | Keep model stable for Phase 2/3 without building gates now |
| Endpoint style | Minimal APIs under `Endpoints/` | Matches newest backend convention (Agent endpoints) |
| Auto-detect source | Backend `appsettings.json` `ConnectionStrings` section | Standard ASP.NET Core convention; zero extra config |
| Auto-detect never auto-persists | detect = preview; explicit `POST /import` = confirmation gate | User requirement — no silent writes |
| Imported default preset | `ReadOnly` + mutation flags conservative | User decision; user can upgrade via PUT |
| MCP surface | Read-only tools (`db_list_connections`, `db_get_schema`, `db_query`) in Phase 1; mutation tool deferred | User correction — phase 1 is read-only mode, so only data-changing tools are deferred |
| MCP detect/import tools | `db_detect_connections` + `db_import_connections` in Phase 1; import writes the local registry only, conversational approval is the gate | User decision — gives the feature a real consumer now (chat) without touching target-data read-only mode |
| Import duplicate names | skip with reason, no overwrite | Prevent clobbering manually-curated entries |
| Import payload | approved `settingsKeys` only; backend re-reads its own source file | Credentials stay at the backend/MCP layer — nothing password-bearing crosses to clients or agents (cloud-LLM safe) |
| Agent visibility boundary | MCP may show identity (id, `{db}@{host}` name, database name, provider, environment) but never username, password, or any connection string (raw or masked); errors are category-only | User decision — host of internal/LAN DBs is not a secret and the name is the readability convention; credentials are the actual risk |
| Detect output | passwords masked (`Password=***`); unparseable → `"***"` | Same reason — tool results pass through the agent's context |
| MCP tool registration | no edit needed — `WithToolsFromAssembly()` auto-discovers `Mcp/Tools/` classes | Current repo state; only `ServerInstructions` gains an entry |
| Detect source path | fixed `{contentRoot}/appsettings.json`, DI-injected, no request param | Local-only by design; endpoint must not read arbitrary host paths |
| Import naming | derived `{db}@{host}` (MySQL: `{db}@{server}[:{port}]`; SQLite: `{file}@local`), fallback to settings key | User requirement; readable identity across environments. Manual entries keep free-form names |

---

## 10. Open Follow-Ups

- MySqlConnector version pinned to latest stable at implementation time (record resolved version in the plan/report).
- Environment-specific config files (`appsettings.Development.json` chain) not scanned in v1 — single file only.
- Roadmap for later phases (each gets its own spec → plan cycle):
  - **Phase 2 (GUI):** `/dashboard/databases` in `Eling.Dashboard` (Next.js) — connection manager, import dialog, schema tree, read-only data viewer, SQL editor — plus Desktop parity.
  - **Phase 3 (Mutations):** mutation classification (`SqlSafety` extended), `POST /{id}/mutate`, MCP `db_execute_mutation`, approval gate (`PendingMutationAction` + SSE + GUI approve/reject), environment-preset enforcement, audit logging. The GUI's CRUD grid and approval panel land here.
