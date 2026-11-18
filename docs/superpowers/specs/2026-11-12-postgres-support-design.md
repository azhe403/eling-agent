# PostgreSQL Support — Design Specification (parity with MySQL)

- **Date:** 2026-11-12
- **Status:** Proposed (awaiting user review)
- **Target Projects:** `Eling.Core`, `Eling.Backend`, `Eling.Dashboard`, `tests/Eling.Core.Tests`, `tests/Eling.Backend.Tests`, `Eling.Dashboard` unit tests
- **Supersedes:** the PostgreSQL deferral in `2026-10-13-db-connection-design.md` §8 ("PostgreSQL (Npgsql) and SQL Server adapters — Later — factory already fails cleanly for them"). SQL Server stays deferred.

---

## 1. Overview & Scope

The dashboard's connection create/edit flow, the backend connection registry, the read-only data path, the appsettings import, and the MCP `db_*` tools all gain first-class PostgreSQL support, at exact parity with MySQL. The user asked for this because create-from-dashboard is the entry point they actually use; shipping Postgres create-only and leaving schema/query/import behind would mean the provider works in the form and then fails one click later, so the parity is deliberate.

**In scope:**

- `DatabaseProvider.PostgreSql` enum member (backend) and `"PostgreSql"` (frontend) — wire value is the member name verbatim; the backend registers `JsonStringEnumConverter` without a naming policy, same as `MySql`/`Sqlite`.
- `NpgsqlConnectionStringBuilder`-backed connection-string handling: `Build` / `Parse` / `Update` / `WithDatabase` / `IsServerLevel` in `DatabaseConnectionStringFactory`.
- `PostgreSqlDatabaseAdapter` (`IDatabaseAdapter`): test, read-only query, schema introspection (tables/columns/PKs/FKs/indexes), `ListDatabasesAsync`.
- `DatabaseAdapterFactory` branch; adapter fails cleanly for other providers as today.
- Appsettings auto-detect: PostgreSQL candidates classified, masked, and named per the existing import convention.
- Dashboard: provider select option, port placeholder, name derivation with a per-provider default port, provider label. Everything else (form layout, database combobox, server-level behavior, edit dialog, raw-string mode) is provider-agnostic and stays untouched.
- Caching (`JsonDatabaseReadCache`) behaves like MySQL — only SQLite is live-first.
- Read-only query guard (`SqlSafety`) is provider-agnostic; no change.
- Mutation endpoint (`POST /api/databases/{id}/mutate`) needs no provider-specific code; Postgres connections flow through the same `ExecuteAsync` path.
- MCP tools: no code change; the Redis-only special-cases already let a Postgres connection through. Comments/ServerInstructions text that enumerates providers gets updated if it names them (implementation checks).
- Unit tests for string factory round-trip + scanner classification; adapter SQL verified live after implementation — the user registers the connection from the dashboard, the agent verifies read/query paths via eling `db_*` (§9).

**Out of scope (deferred):**

- SQL Server (`Microsoft.Data.SqlClient`) — still deferred, unchanged.
- SSL/TLS UI — no new field. Npgsql keywords (`SSL Mode=Require`, `Timeout`, …) round-trip through the stored string and the raw-string mode, exactly like MySQL's options; the edit form layers over them without dropping them.
- Connection pooling — adapters stay connect-per-operation, like MySQL.
- Npgsql version pin — latest stable at implementation time; the resolved version is recorded in the implementation plan/report (same convention as the MySqlConnector note).
- Encryption at rest — unchanged, follow-up.

---

## 2. Architecture & Components

Follows the existing per-provider idiom: no shared relational base class (approach B was considered and rejected — it would put the working MySQL path under review for a new feature). The Postgres adapter is a sibling of the MySQL adapter, not a generalization of it.

```
Eling.Core/Database/
  DatabaseProvider.cs                 enum gains PostgreSql (after MySql)
  DatabaseConnectionStringFactory.cs  PostgreSql branches: Parse/Build/Update/WithDatabase/IsServerLevel
                                      + DefaultPostgreSqlPort = 5432
  Adapters/PostgreSqlDatabaseAdapter.cs  new (test/query/execute/schema/list databases)
  Adapters/DatabaseAdapterFactory.cs     PostgreSql branch
  AppSettingsConnectionScanner.cs        raw-key pre-scan + PostgreSql block (before SQLite/Redis)

Eling.Backend/
  (no structural changes; DashboardServices already wires adapters through the factory)

Eling.Dashboard (frontend)
  app/dashboard/databases/types.ts           Provider | "PostgreSql"
  utils/connection-helpers.ts                provider-aware default port in deriveConnectionName;
                                             providerLabel case; isPostgreSql helper
  utils/index.ts                             re-export isPostgreSql
  components/connection-form/ConnectionFormFields.tsx
                                             SelectItem PostgreSql; port placeholder 5432
  Databases.tsx / useTableData.ts            no change ("non-MySql quotes with \"" is already correct)
```

**Registration vs. runtime:** unchanged — registry is source of truth; the factory creates per operation; `DatabaseReadService` caches Postgres like MySQL (it excludes only SQLite).

---

## 3. Data Model & Storage

### 3.1 Model

`DatabaseConnection` gains nothing — `Provider` already carries the enum. Validation is unchanged. Existing stored rows are untouched; a row claiming `PostgreSql` stops failing with `NotSupportedException` and starts working.

### 3.2 Connection string semantics

Npgsql keyword names are used in stored strings (`Host`, `Port`, `Database`, `Username`, `Password`, …). `NpgsqlConnectionStringBuilder` round-trips all Npgsql keywords, so options no field covers survive the edit flow intact (same contract as MySQL).

- **Build** (fresh from fields): `Host` required — missing/blank host is a `400`. `Port` optional, defaults to 5432 when blank. `Database` optional — blank means **server-level** (like MySQL): reaches every database on the server. `Username`/`Password` optional at build level; a bad credential surfaces at test time, as it does for MySQL.
- **Parse** (stored string → fields for the edit form): same five fields; `null` fields for anything absent.
- **Update** (layer over stored): host layered as in MySQL (blank entry keeps stored); blank password keeps stored password; **empty database box clears the stored database** — server-level is the explicit state, consistent with MySQL.
- **WithDatabase**: sets `Database` on the builder. SQLite remains a no-op.
- **IsServerLevel**: true for `PostgreSql` with a blank `Database` — the connection tree grows the `{connection} > {database} > {table}` level and queries/schema carry `database`, same as MySQL server-level.
- **Server-level opens against the maintenance database.** Postgres refuses a connection that names no database (Npgsql would resolve its own default, and would fail on a server where the login role has no same-named database). So when an adapter opens a PostgreSQL connection whose string names no database, the adapter points that connection at `postgres` (the maintenance database) for the duration of the operation. `ListDatabasesAsync` and schema reads work identically from there. The stored string is never mutated — the substitution happens on the adapter's private copy, exactly like the MySQL adapter's `ConnectionString` property. A raw-string user can pin any other database; a server where the `postgres` database was dropped surfaces "database not found" categorically at test time (see §5).
- **Timeouts:** if the stored string carries no `Timeout`, the adapter sets `Timeout=3`, mirroring the MySQL adapter's `ConnectionTimeout=3`.

### 3.3 Credential masking

Unchanged boundary. Detect masks with `Password = "***"`; import carries keys, not strings; the MCP surface never sees credentials. The Postgres masked string is produced by the same builder-with-masked-password pattern as MySQL/SQLite/Redis.

---

## 4. API & MCP Surface

No new routes. No request/response shape changes. The enum value `PostgreSql` flows through the existing DTOs and the existing `JsonStringEnumConverter` registration.

| Surface | Change |
|---|---|
| `POST /api/databases`, `PUT /{id}` | accepts `provider: "PostgreSql"` + `manual` fields or a raw Npgsql string |
| `POST /api/databases/{id}/test`, `POST /api/databases/test-connection`, `POST /api/databases/list-databases` | work for Postgres via the factory |
| `GET /api/databases/{id}/schema`, `POST /{id}/query`, `POST /{id}/mutate` | work for Postgres; `database` argument honored for server-level connections |
| `POST /api/databases/detect`, `POST /api/databases/import` | PostgreSQL candidates appear, masked, named `{db}@{host}[:{port}]` |
| MCP `db_list_connections` / `db_get_schema` / `db_query` / detect / import | no code change; descriptions already provider-agnostic. Any provider enumeration in `ServerInstructions` or tool descriptions is updated to include PostgreSql (implementation verifies with a grep for "MySql"/"mysql" in `Mcp/`) |

**Validation rule for `provider` on create:** `DatabaseConnection.Create` already rejects undefined enum values — `PostgreSql` is defined, so no change.

**Error translation:** `NotSupportedException` from the factory → `400` (existing `TryCreateAdapter` path). A stored row cannot claim an unregistered provider.

---

## 5. PostgreSQL Adapter Details

`PostgreSqlDatabaseAdapter` mirrors `MySqlDatabaseAdapter` operation-for-operation. Differences are confined to catalog access and error classification.

### 5.1 Connecting

```csharp
// private copy of the stored string; Database substituted with "postgres" when absent
var builder = new NpgsqlConnectionStringBuilder(_connection.ConnectionString);
if (string.IsNullOrWhiteSpace(builder.Database)) builder.Database = "postgres";
if (/* stored string lacks Timeout */) builder.Timeout = 3;
```

### 5.2 Test

Open + `SELECT 1;` + stopwatch, same shape as MySQL. Npgsql-level failures categorize as:

| Condition | Category |
|---|---|
| `PostgresException` SQLSTATE `28P01` / `28000` (bad password / authorization) | `authentication failed` |
| `PostgresException` SQLSTATE `3D000` (maintenance database missing) | `database not found` |
| `SocketException` / `IOException` / connection timeout | `host unreachable` / `connection timed out` |
| anything else | `database unavailable` |

The exact SQLSTATE→category table is finalized in implementation with tests for the two `28*` codes; the table above is the contract. MCP/REST error text stays category-only — never the provider's message.

### 5.3 Query / Execute

Identical row-mapping loop to MySQL (columns from the reader, `rows` capped at `maxRows` with `truncated`). No provider-specific SQL. Boolean/null handling: `DBNull → null` as today.

### 5.4 ListDatabasesAsync

```sql
SELECT datname FROM pg_database WHERE datistemplate = false ORDER BY datname;
```

`datistemplate = false` hides `template0`/`template1` permanently, including when an account can see them. **`postgres` is listed deliberately** — it is real, browseable data, unlike the template databases. Decision recorded here; if the user wants it hidden, that is a one-line change, not a design change.

### 5.5 GetSchemaAsync

The `database` argument behaves exactly as for MySQL (the connection string is already pointed at that database by the read path before the adapter sees it; the schema is whatever the connection is pointed at).

Non-system schemas are **not** listed. Introspection is restricted to `current_schema()` — the schema the connection is pointed at (`public` by default). This is the exact Postgres analog of MySQL's one-namespace-per-connection rule, keeps table naming unambiguous (a bare table name always resolves, no `schema.table` split anywhere in the frontend), and means `DatabaseSchemaInfo`/`TableSchema` wire shape is untouched. A table that lives only in a non-`current_schema()` schema is invisible to the tree until the user points the connection's `Search Path` (or the raw string) at it. Decision recorded in §11 (supersedes the earlier multi-schema idea). Tables/columns/PKs/FKs/indexes all filter on that one schema:

| Object | Source |
|---|---|
| Tables/views | `information_schema.tables` — `table_schema = current_schema()`; `table_type` maps `BASE TABLE`→`table`, `VIEW`→`view` |
| Columns | `information_schema.columns` — same schema filter — `column_name`, `data_type`, `is_nullable` (`YES`→nullable)
| Primary keys | `information_schema.table_constraints` (`constraint_type='PRIMARY KEY'`) joined to `information_schema.key_column_usage`, same schema filter |
| Foreign keys | `information_schema.key_column_usage` joined to `information_schema.constraint_column_usage`, mapped to the existing `ForeignKeySchema(constraint_name, column_name, references_table, references_column)` shape, same schema filter. Cross-schema references appear only when both ends live in the current schema |
| Indexes | pg_catalog: `pg_index` ⋈ `pg_class` ⋈ `pg_attribute` (PG has no `information_schema.statistics`), scoped to the current schema's tables (`indrelid` join); unique flag from `indisunique`; column order from `indkey` ordinality — grouped per index name like the MySQL implementation |

Parameterization: `@t` binds the table name only (the schema filter is the fixed expression `current_schema()` — never caller input); every `CommandText` is a fixed string, same discipline as the MySQL adapter.

Column order, per-table fan-out (one query per table for columns/FKs/indexes), and the returned `DatabaseSchemaInfo` are unchanged in shape.

---

## 6. Appsettings Detect & Import

The scanner decides the provider from **raw keys**, case-insensitively, before any builder votes — because the MySQL and Npgsql builders both accept `Host=` and both accept `Username`/`Password`, so "did the MySQL builder parse it" cannot separate a canonical Npgsql string from a MySQL one (a bare `Host=…;Port=5432;Database=d;Username=u;Password=p` parses fine as MySQL).

Rule (in match order):

1. `Server=` present anywhere → **MySQL** (today's behavior, unchanged — `Server=` is MySQL's documented key and the owner of the ambiguity wins).
2. Otherwise `Host=` present → **PostgreSQL** block: parse with `NpgsqlConnectionStringBuilder`; require `Host` non-blank **and** `Database` non-blank. A `Host=…;Username=…` string with no database is deliberately *not* importable — the import convention requires `{db}@{host}` names, and a host-only candidate would be server-level noise (the dashboard can register those manually).
3. Neither `Server=` nor `Host=` (exotic keying) → **MySQL** attempt as today; if it does not classify, and the Npgsql builder classifies with an Npgsql-only keyword present (`SSL Mode`, `Search Path`, `Application Name`, `Include Error Details`) → **PostgreSQL**; else unrecognized.

The PostgreSQL block therefore runs inside steps 2–3 — structurally **before** the SQLite/Redis blocks, and before the value can be claimed by MySQL. Recorded consequences:

- `Host=h;Port=5432;Database=d;Username=u;Password=p` → **PostgreSQL** (step 2).
- `Server=h;Database=d` → **MySQL** (step 1).
- `Host=…;Server=…` (mixed) → **MySQL** (step 1).
- Npgsql-only keyings with neither `Host=` nor `Server=` → step 3.

- Name: `{Database}@{Host}`, non-5432 port appended (`:{port}`).
- Masked string: builder copy with `Password = "***"`.
- SQLite/Redis classification unchanged; unrecognized values still come back with `provider: null` + reason.

---

## 7. Frontend Details

1. `types.ts`: `Provider = "Sqlite" | "MySql" | "Redis" | "PostgreSql"`.
2. `deriveConnectionName`: default port map — `MySql → 3306`, `PostgreSql → 5432`; the `:{port}` suffix appears when the typed port differs from the provider's default (existing rule, now provider-aware instead of hardcoded 3306).
3. `providerLabel`: `PostgreSql → "PostgreSQL"`.
4. New `isPostgreSql(conn)` helper, re-exported from `utils/index.ts` alongside `isMySql`.
5. `ConnectionFormFields.tsx`: `<SelectItem value="PostgreSql">PostgreSQL</SelectItem>`; port placeholder becomes provider-aware — `port (3306)` for MySQL, `port (5432)` for Postgres, generic `port` otherwise.
6. `useConnectionForm.ts`, `useTableData.ts`, `useTableMutations.ts`, `Databases.tsx`: no changes. The existing `provider !== "Sqlite"` branches naturally include Postgres; identifier quoting defaults to `"`, which is Postgres's quote character; server-level detection (`database.trim() === ""`) is provider-agnostic.
7. No change to stored `isServerLevel` semantics, the combobox, or the raw-string mode.

---

## 8. Error Handling & Guardrails

- Every adapter operation opens, uses, closes; bounded `CommandTimeout = 30`; cancellation linked as today.
- Every `catch` logs via `ILogger` (repo convention); MCP/REST see categories only.
- A Postgres row stored before this ships works after upgrade with no migration.
- `SqlSafety` unchanged; `WITH` stays rejected in v1 (wraps mutations in Postgres exactly as in MySQL).
- Read path serves cached snapshots on `DatabaseUnavailableException` — `postgres` maintenance-DB open failure yields the offline/cached path, never a crash.

---

## 9. Testing Strategy

**Automated (CI-safe, no live server):**

- `Eling.Core.Tests`
  - Connection-string factory round trips: build → parse → build stability for Postgres; blank host → `ArgumentException`; blank password keeps stored (Update); empty database clears stored database (Update); `WithDatabase` sets/replaces/clears; `IsServerLevel` true/false/other providers; non-5432 port round-trips; unknown Npgsql keywords (e.g. `SSL Mode`) survive Update.
  - Scanner, after the raw-key rule in §6: `Server=…` string → MySQL; `Host=h;Port=5432;Database=d` (no `Server=`) → PostgreSQL with `{db}@{host}` name, masked, no password; `Host=…` with no database → not importable; `Host=…;Server=…` → MySQL.
  - `DatabaseAdapterFactory`: PostgreSql returns `PostgreSqlDatabaseAdapter`; unknown string still `NotSupportedException`.
- `Eling.Core.Tests` / `Eling.Backend.Tests` endpoint sanity: create → 201 with `provider: "PostgreSql"`; test-connection route does not persist; invalid provider string still 400.
- Frontend (`vitest`): `deriveConnectionName` for PostgreSql (5432 → no suffix, other → suffix); `providerLabel("PostgreSql") === "PostgreSQL"`; `isPostgreSql` true/false cases.

**Manual gate, split by who can run what:**

1. **User (from the dashboard):** register the Postgres connection from the dashboard against their server (typed fields → Test → Save); confirm the create/test clicks behave; confirm detect/import of a PG appsettings entry if they have one.
2. **Agent (via eling `db_*`, after the connection is registered):** `db_list_connections` shows the new row; `db_get_schema` returns the tables of the connected schema with columns/PKs; a `SELECT` against a known table returns rows; a disallowed mutation attempt is refused. A server-level registration verified with `db_get_schema` and no database returns the database list.

Results (pass/fail per step) recorded in the implementation report. If the agent cannot see the registered connection (it lives on another host), the gate is handed to the user with the exact checklist, and the report says so — it is never silently skipped.

MySQL remains unit-level-only and manual; Postgres gets the same treatment, with the live checks above explicitly in scope because a server is available.

---

## 10. Approaches Considered

| Approach | Verdict |
|---|---|
| **A. Per-provider mirror (chosen)** — `PostgreSqlDatabaseAdapter` as a sibling of `MySqlDatabaseAdapter`; factory/enum/string-factory/scanner gain PostgreSql branches; frontend adds the provider | Chosen. Smallest change surface, matches the repo's explicit per-provider idiom, keeps the reviewed MySQL path untouched, and the ~300 lines of near-duplicate catalog SQL is the price of that separation. A shared base class can be extracted later, deliberately, when a third concrete adapter exists to prove the abstraction |
| **B. Extract a shared relational base for MySQL+Postgres** | Rejected. Reduces duplication but rewrites the working, reviewed MySQL adapter and puts both providers behind one diff; the catalog differences (PK via `information_schema` vs `column_key`, indexes via `pg_index` vs `statistics`) would be shoehorned behind template methods before a third case justified them |
| **C. Create-flow only (no schema/query/import/MCP)** | Rejected at scope selection (2026-11-12). Shipping a provider that fails one click after Save is worse than one that fails loudly up front |

---

## 11. Decision Log

| Decision | Choice | Rationale |
|---|---|---|
| Enum member name | `PostgreSql` (wire: `PostgreSql`) | Matches `MySql`/`Sqlite` member-name wire values; "PostgreSQL" as a C# identifier violates naming style |
| Default port | 5432 | Npgsql default; suffix logic ports to provider-aware map |
| Server-level open | adapter substitutes `postgres` maintenance DB on its private copy when no database is set | PG refuses database-less connections; the stored string is never mutated |
| `postgres` in list | listed | Real data; only `datistemplate = false` hides templates. One-line change if unwanted |
| Introspection schema scope | `current_schema()` only (`public` by default) | 2026-11-12 user decision — exact analog of MySQL's one-namespace-per-connection; no `schema.table` split anywhere in the frontend; keeps `DatabaseSchemaInfo`/`TableSchema` shape, FK and index semantics unambiguous. Supersedes the earlier "surface all user schemas" idea. Tables outside the current schema are invisible until the user points `Search Path` at them |
| Appsettings disambiguation | raw-key rules before builder votes: `Server=` → MySQL; otherwise `Host=` → PostgreSQL; neither → MySQL-as-today with an Npgsql-only-keyword fallback | The MySQL builder accepts `Host=` (alias of `Server=`) so a canonical Npgsql string parses as MySQL; port 5432 alone is not proof. `Server=` present means MySQL's keyword owner wins; mixed `Host=…;Server=…` → MySQL |
| Live verification | user registers the connection from the dashboard; agent verifies the data path via eling `db_*` afterward | 2026-11-12 user decision — the agent has a live read path once the connection is registered; the dashboard create/test clicks are the user's to confirm |
| SSL UI | none | Options round-trip; raw-string mode is the escape hatch (same as MySQL) |
| Caching | Postgres cached like MySQL | Only SQLite is live-first; PG over a LAN is not "local" |
| Adapter style | per-provider sibling, no base class | See §10 |
| Pool | none (connect-per-operation) | Same as MySQL; revisit if profiling shows pain |

---

## 12. Open Follow-Ups

- SQL Server adapter (same recipe as this one) — still deferred.
- Shared relational base extraction — only after a third concrete adapter.
- SSL/TLS field in the manual form (beyond round-trip) — separate feature, separate decision.
- `postgres` hiding / schema filter tightening in the browse tree — one-line follow-ups if the manual gate shows they are wanted.
- Version of Npgsql resolved at implementation time recorded in the plan report.
