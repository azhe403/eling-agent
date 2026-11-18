# PostgreSQL Support Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add first-class PostgreSQL support at exact parity with MySQL — the registry, connection-string handling, adapter, appsettings detect/import, and the dashboard create/edit flow all accept it.

**Architecture:** Per-provider sibling, no shared base class (approach A of the spec). New `PostgreSqlDatabaseAdapter` mirrors `MySqlDatabaseAdapter`; the enum, connection-string factory, adapter factory, and appsettings scanner gain PostgreSql branches; the frontend gains the provider in its unions, helpers, and select.

**Tech Stack:** .NET 10 (net10.0), Npgsql, xUnit, Next.js + React 19, vitest.

**Spec:** `docs/superpowers/specs/2026-11-12-postgres-support-design.md` — executors read both; the spec argues the decisions, the plan names the files.

## Global Constraints

- Work directly in the main checkout on `main` — never create a git worktree (user ruling 2026-10-15).
- One logical change per commit; never mix unrelated contexts in one commit (AZ workspace rule).
- Never run `pnpm build` / `next build` for the frontend while the dev server runs on 4427; type-check with `pnpm --prefix src/frontend/Eling.Dashboard exec tsc --noEmit`.
- Never try to start the dev backend from the agent shell (stdin EOF kills it); port 4417 is run by the user / publish scripts. The 4417 served build may diverge from the checkout — backend changes are verified by building and running the test layers, not by clicking the dev dashboard.
- Every response from the REST/MCP boundary carries categories only, never provider messages (spec §4/§8).
- Enum wire value is the member name verbatim: `PostgreSql`. No `JsonStringEnumConverter` naming policy anywhere (spec §1).
- Frontend changes get exercised in the actual browser on 4427 before reporting done (user preference 2026-11-16).
- Commits follow the repo's conventional style and MUST pass the pre-commit hook; never bypass it.
- Default ports: MySQL 3306, PostgreSQL 5432, Redis 6379.

## Review Focus

Five input classes the spec implies but no single task's happy path exercises — each is pinned to its owning task's tests:

1. **Password with `;`, `=` or quotes round-trips** through Build/Update untouched — Task 2 (`Build_PostgreSql_WithSeparatorsInPassword_RoundTripsThePassword`).
2. **A server-level connection names no database** — the adapter must substitute the `postgres` maintenance database on its private copy (the stored string is never mutated); a dropped `postgres` database must surface the `database not found` category, not a crash. Not unit-testable — pinned to the Task 8 live gate (server-level registration + `db_get_schema`).
3. **Appsettings ambiguity matrix** — `Server=` → MySQL; `Host=` only → PostgreSQL; `Host=…;Server=…` → MySQL; `Host=` without a database → unimportable — Task 4 (four scanner facts).
4. **Edit semantics of blank fields** — blank password keeps the stored one; empty database box clears the stored database (server-level) — Task 2 (`Update_PostgreSql_*`).
5. **Npgsql-only keywords survive an edit** — `SSL Mode=Require` round-trips through `Update` — Task 2 (`Update_PostgreSql_PreservesUnknownKeywords`).

---

## Task 1: Commit the design spec + core enum and package reference

**Files:**
- Commit: `docs/superpowers/specs/2026-11-12-postgres-support-design.md` (untracked)
- Modify: `src/backend/Eling.Core/Database/DatabaseProvider.cs`
- Modify: `src/backend/Eling.Core/Eling.Core.csproj`

**Interfaces:**
- Consumes: nothing
- Produces: `DatabaseProvider.PostgreSql` enum member; Npgsql reference available to Tasks 2–4

- [ ] **Step 1: Commit the spec as its own logical change**

```bash
git add docs/superpowers/specs/2026-11-12-postgres-support-design.md
git commit -m "docs: add PostgreSQL support design spec"
```

Expected: one commit, only the spec file in it.

- [ ] **Step 2: Add the enum member**

```csharp
public enum DatabaseProvider
{
    Sqlite,
    MySql,
    Redis,
    PostgreSql
}
```

- [ ] **Step 3: Add Npgsql at the latest stable version**

```bash
dotnet add src/backend/Eling.Core/Eling.Core.csproj package Npgsql
```

Record the resolved version — it goes in Task 8's report (spec §12). Do NOT bump anything else; the weekly NuGet check ran 2026-11-16 and is not due.

- [ ] **Step 4: Build**

Run: `dotnet build Eling.slnx`
Expected: build succeeds; the only new package is Npgsql.

- [ ] **Step 5: Commit**

```bash
git add src/backend/Eling.Core/Database/DatabaseProvider.cs src/backend/Eling.Core/Eling.Core.csproj
git commit -m "feat(core): add PostgreSql provider enum and Npgsql reference"
```

---

## Task 2: Connection-string factory PostgreSql branches

**Files:**
- Modify: `src/backend/Eling.Core/Database/DatabaseConnectionStringFactory.cs`
- Test: `tests/Eling.Core.Tests/DatabaseConnectionStringFactoryTests.cs`

**Interfaces:**
- Consumes: `DatabaseProvider.PostgreSql` (Task 1)
- Produces: `DatabaseConnectionStringFactory.DefaultPostgreSqlPort` (const int = 5432) and PostgreSql behavior of `Parse` / `Build` / `Update` / `WithDatabase` / `IsServerLevel` — exact for Tasks 3, 4 and any endpoint test

Behavior contract (spec §3.2):
- Build: `Host` required (blank → `ArgumentException`); `Port` optional, default 5432; `Database` optional (blank → server-level); `Username`/`Password` optional (blank → omitted).
- Parse: Npgsql keyword names (`Host`, `Port`, `Database`, `Username`, `Password`); `null` for absent; `null` when `Host` blank/unparseable.
- Update: host layered over stored (blank entry keeps stored); blank password keeps stored; **empty database clears the stored database** — set `builder.Database = null`, never `""` (Npgsql emits a dangling `Database=;` otherwise); unknown Npgsql keywords (`SSL Mode` etc.) survive untouched.
- WithDatabase: sets `Database`; SQLite stays a no-op.
- IsServerLevel: true for `PostgreSql` with blank `Database`. The existing XML doc comment ("Only MySQL has that notion") is updated to "MySQL and PostgreSQL".

- [ ] **Step 1: Write the failing tests** (mirror the `Build_MySql_*` / `Update_MySql_*` facts already in the file; name every fact `Build_PostgreSql_*` / `Parse_PostgreSql_*` / `Update_PostgreSql_*` / `WithDatabase_PostgreSql_*`):

```csharp
[Fact]
public void Build_PostgreSql_WritesEveryField() { /* 5432 explicit, round-trip via Parse asserts host/port/database/username/password */ }

[Fact]
public void Build_PostgreSql_WithSeparatorsInPassword_RoundTripsThePassword() { var password = "p@ss;word=1'x"; /* … */ }

[Fact]
public void Build_PostgreSql_WithoutPort_UsesTheDefaultPort() { /* Assert.Equal(DatabaseConnectionStringFactory.DefaultPostgreSqlPort, parsed.Port); */ }

[Fact]
public void Build_PostgreSql_WithoutHost_Throws() { Assert.Throws<ArgumentException>(act); }

[Fact]
public void Build_PostgreSql_WithoutDatabase_BuildsAServerLevelConnection() { /* IsServerLevel(PostgreSql, cs) true; parsed.Database null */ }

[Fact]
public void Build_PostgreSql_NonDefaultPort_RoundTrips() { /* 5433 survives Build→Parse */ }

[Fact]
public void Update_PostgreSql_BlankPasswordKeepsStored() { /* stored "…Password=s3cret…"; entered blank password; result parses to Password "s3cret" */ }

[Fact]
public void Update_PostgreSql_BlankDatabaseClearsStored() { /* stored has Database=orders; entered Database ""; IsServerLevel true; Parse(...).Database null */ }

[Fact]
public void Update_PostgreSql_PreservesUnknownKeywords() { /* stored "Host=h;Database=d;SSL Mode=Require"; blank-password update; rebuilt string still contains "SSL Mode=Require" */ }

[Fact]
public void Parse_PostgreSql_Garbage_ReturnsNull() { Assert.Null(DatabaseConnectionStringFactory.Parse(DatabaseProvider.PostgreSql, "::::")); }

[Fact]
public void WithDatabase_PostgreSql_SetsTheDatabase() { /* WithDatabase(PostgreSql, serverLevelCs, "orders") → Parse → Database "orders"; IsServerLevel false */ }

[Fact]
public void IsServerLevel_PostgreSql_TrueWithoutDatabase_FalseWithOne() { /* both branches */ }
```

- [ ] **Step 2: Run the tests, verify they fail**

Run: `dotnet test tests/Eling.Core.Tests --filter "FullyQualifiedName~PostgreSql" --artifacts-path .bin-test`
Expected: FAIL (PostgreSql branch not implemented).

- [ ] **Step 3: Implement**

Private helpers `ParsePostgreSql`, `BuildPostgreSql`, `UpdatePostgreSql`, `WithPostgreSqlDatabase` next to the MySQL siblings (same file, same order). The provider switch expressions in `Parse`/`Build`/`Update`/`WithDatabase` gain `DatabaseProvider.PostgreSql => …` arms; `IsServerLevel` gains the PostgreSql arm. Build from fields passes `Port = (uint)port` and omits Database/Username/Password when blank, exactly like `BuildMySql`. Update clears the database with `builder.Database = null`.

- [ ] **Step 4: Run the tests, verify they pass**

Run: `dotnet test tests/Eling.Core.Tests --filter "FullyQualifiedName~PostgreSql|FullyQualifiedName~MySql|FullyQualifiedName~Sqlite" --artifacts-path .bin-test`
Expected: PASS, MySQL/SQLite still green.

- [ ] **Step 5: Commit**

```bash
git add src/backend/Eling.Core/Database/DatabaseConnectionStringFactory.cs tests/Eling.Core.Tests/DatabaseConnectionStringFactoryTests.cs
git commit -m "feat(core): PostgreSql connection-string factory support"
```

---

## Task 3: PostgreSqlDatabaseAdapter + factory branch

**Files:**
- Create: `src/backend/Eling.Core/Database/Adapters/PostgreSqlDatabaseAdapter.cs`
- Modify: `src/backend/Eling.Core/Database/Adapters/DatabaseAdapterFactory.cs`
- Test: `tests/Eling.Core.Tests/DatabaseAdapterFactoryTests.cs` (create if absent)

**Interfaces:**
- Consumes: `DatabaseProvider.PostgreSql` (Task 1); shared `IDatabaseAdapter`, `QueryResult`, `ConnectionTestResult`, `DatabaseSchemaInfo`, `TableSchema`, `ColumnSchema`, `ForeignKeySchema`, `IndexSchema`, `DatabaseUnavailableException` — all already in `Eling.Core.Database`
- Produces: `DatabaseAdapterFactory.CreateAdapter(PostgreSqlConnection)` → `PostgreSqlDatabaseAdapter` (public sealed)

Behavior contract (spec §5):
- Private `ConnectionString` property: `new NpgsqlConnectionStringBuilder(_connection.ConnectionString)`; if `Database` blank → `"postgres"`; if the stored string lacks a `Timeout=` key (case-insensitive `Contains`) → `Timeout = 3`. On a `PostgreSqlConnection`-shaped misconfiguration the builder may throw `ArgumentException` — let it surface as `DatabaseUnavailableException("database unavailable", ex)` at the open site exactly like the MySQL adapter wraps `OpenAsync`.
- `TestAsync`: open → `SELECT 1;` (`CommandTimeout = 30`) → stopwatch; failures logged + `ConnectionTestResult(false, "connection failed", …)`.
- `Categorize`: `NpgsqlException.SqlState` `28P01`/`28000` → `authentication failed`; `3D000` → `database not found`; `TimeoutException` → `connection timed out`; `SocketException` → `host unreachable`; else `database unavailable`.
- `QueryAsync` / `ExecuteAsync`: the same reader loop as MySQL (columns from reader, `rows` capped at `maxRows`, `truncated` flag, `DBNull → null`).
- `ListDatabasesAsync`: `SELECT datname FROM pg_database WHERE datistemplate = false ORDER BY datname;` — `postgres` IS listed (spec §5.4).
- `GetSchemaAsync`: all catalog queries filter `table_schema = current_schema()` — never a parameter, it is a literal (only `@t` binds the table name). Columns PK flag via `information_schema.table_constraints` (`constraint_type='PRIMARY KEY'`) joined to `key_column_usage`. FKs via `key_column_usage` joined to `constraint_column_usage` (`position_in_unique_constraint IS NOT NULL` on the referenced side pattern from spec §5.5) mapped to `ForeignKeySchema(constraint_name, column_name, references_table, references_column)`. Indexes via `pg_index` ⋈ `pg_class` ⋈ `pg_attribute` scoped to current-schema tables, grouped by index name with `indisunique` and `indkey` ordinality — same `IndexGroup(bool Unique, List<string> Cols)` record shape as the MySQL adapter. `@t` parameters use `NpgsqlParameter`.
- `DatabaseAdapterFactory.CreateAdapter`: `DatabaseProvider.PostgreSql => new PostgreSqlDatabaseAdapter(connection),` added before the `_` arm.

- [ ] **Step 1: Write the failing factory test**

```csharp
[Fact]
public void CreateAdapter_PostgreSql_ReturnsPostgreSqlDatabaseAdapter()
{
    var connection = DatabaseConnection.Create("pg", DatabaseProvider.PostgreSql,
        "Host=localhost;Database=orders;", DatabaseEnvironment.ReadOnly);

    Assert.IsType<PostgreSqlDatabaseAdapter>(DatabaseAdapterFactory.CreateAdapter(connection));
}
```

- [ ] **Step 2: Run it, verify it fails**

Run: `dotnet test tests/Eling.Core.Tests --filter "FullyQualifiedName~AdapterFactory" --artifacts-path .bin-test`
Expected: FAIL (no PostgreSql arm / adapter type missing).

- [ ] **Step 3: Implement `PostgreSqlDatabaseAdapter.cs`** following the structure, record shapes, `await using` discipline, and logging conventions of `MySqlDatabaseAdapter.cs` exactly (private `TableInfo`/`IndexGroup` records, comment density, XML doc comments). The catalog SQL lives in fixed `CommandText` literals with `@t` the only bound parameter. The PG-specific catalog queries to start from:

```sql
-- tables (current schema only)
SELECT table_name, table_type FROM information_schema.tables
 WHERE table_schema = current_schema() ORDER BY table_name;

-- columns + PK flag (join table_constraints for the PK set; left join, or a
-- second per-table query like the MySQL adapter's information_schema pass)
SELECT c.column_name, c.data_type, c.is_nullable
  FROM information_schema.columns c
 WHERE c.table_schema = current_schema() AND c.table_name = @t
 ORDER BY c.ordinal_position;
SELECT kcu.column_name
  FROM information_schema.table_constraints tc
  JOIN information_schema.key_column_usage kcu
    ON tc.constraint_name = kcu.constraint_name AND tc.table_schema = kcu.table_schema
   AND tc.table_name = kcu.table_name
 WHERE tc.constraint_type = 'PRIMARY KEY' AND tc.table_schema = current_schema()
   AND tc.table_name = @t;

-- foreign keys
SELECT kcu.constraint_name, kcu.column_name,
       ccu.table_name AS referenced_table_name, ccu.column_name AS referenced_column_name
  FROM information_schema.key_column_usage kcu
  JOIN information_schema.constraint_column_usage ccu
    ON kcu.constraint_name = ccu.constraint_name AND kcu.table_schema = ccu.table_schema
 WHERE kcu.table_schema = current_schema() AND kcu.table_name = @t
   AND kcu.position_in_unique_constraint IS NOT NULL;

-- indexes (pg_catalog)
SELECT i.relname AS index_name, ix.indisunique, a.attname AS column_name
  FROM pg_index ix
  JOIN pg_class t  ON t.oid = ix.indrelid
  JOIN pg_namespace n ON n.oid = t.relnamespace
  JOIN pg_class i  ON i.oid = ix.indexrelid
  JOIN pg_attribute a ON a.attrelid = ix.indrelid AND a.attnum = ANY(ix.indkey)
 WHERE n.nspname = current_schema() AND t.relname = @t
 ORDER BY i.relname, array_position(ix.indkey, a.attnum);
```

If a join above proves wrong against the live server at Task 8, fix the query — not the spec; record the delta in the report.

- [ ] **Step 4: Add the factory arm and run the test**

Run: `dotnet test tests/Eling.Core.Tests --filter "FullyQualifiedName~AdapterFactory" --artifacts-path .bin-test`
Expected: PASS. (No live-server unit tests — adapter SQL is verified at the Task 8 live gate, per spec §9.)

- [ ] **Step 5: Commit**

```bash
git add src/backend/Eling.Core/Database/Adapters/PostgreSqlDatabaseAdapter.cs src/backend/Eling.Core/Database/Adapters/DatabaseAdapterFactory.cs tests/Eling.Core.Tests/DatabaseAdapterFactoryTests.cs
git commit -m "feat(core): PostgreSql database adapter"
```

Commit both files plus the test even if Step 3/4 flowed straight from Failure — one logical change.

---

## Task 4: Appsettings scanner — raw-key pre-scan + PostgreSql block

**Files:**
- Modify: `src/backend/Eling.Core/Database/AppSettingsConnectionScanner.cs`
- Test: `tests/Eling.Core.Tests/AppSettingsConnectionScannerTests.cs`

**Interfaces:**
- Consumes: Npgsql types (Task 1); `DatabaseProvider.PostgreSql`; existing `Classify(string key, string value)` structure
- Produces: `Scan()` output where PostgreSQL candidates appear with name `{db}@{host}[:{port}]`, masked string, `provider = PostgreSql`

Algorithm (spec §6, in match order):
1. `Server=` present (case-insensitive) → fall through to the existing MySQL-first chain untouched.
2. Otherwise `Host=` present → try the PostgreSQL block FIRST: parse with `NpgsqlConnectionStringBuilder`; require `Host` non-blank **and** `Database` non-blank. If it classifies → return it. If it does not (unparseable, or missing Database) → fall through to the existing remaining chain (SQLite/Redis — a `Host=` string never classifies as MySQL anyway).
3. Neither `Server=` nor `Host=` → existing chain unchanged; when that chain ends in `provider: null`, check for an Npgsql-only keyword (`SSL Mode`, `Search Path`, `Application Name`, `Include Error Details`, case-insensitive); if present, try the PostgreSQL block; else stay unrecognized.

Name: `{Database}@{Host}`, `:{port}` appended when port ≠ 5432. Masked: builder copy with `Password = "***"`.

- [ ] **Step 1: Write the failing tests** (four scanner facts, following the file's `Write(...)` + `ConnectionStrings(key, value)` helpers)

```csharp
[Fact]
public void Scan_ClassifiesPostgreSql_AndMasksThePassword()
{
    Write("appsettings.json", ConnectionStrings("Default",
        "Host=pg.internal;Port=5432;Database=orders;Username=app;Password=pg-secret;"));

    var detected = new AppSettingsConnectionScanner(_dir).Scan();

    Assert.Equal(DatabaseProvider.PostgreSql, detected[0].Provider);
    Assert.Equal("orders@pg.internal", detected[0].Name);
    Assert.Contains("Password=***", detected[0].MaskedConnectionString);
    Assert.DoesNotContain("pg-secret", detected[0].MaskedConnectionString);
}

[Fact]
public void Scan_PostgreSql_NonDefaultPort_SuffixedInTheName()
{
    Write("appsettings.json", ConnectionStrings("Default",
        "Host=pg.internal;Port=5433;Database=orders;Username=app;Password=pg-secret;"));

    var detected = new AppSettingsConnectionScanner(_dir).Scan();

    Assert.Equal("orders@pg.internal:5433", detected[0].Name);
}

[Fact]
public void Scan_HostKeyedStringWithServerKey_ClassifiesAsMySql()
{
    Write("appsettings.json", ConnectionStrings("Default",
        "Host=pg.internal;Server=mysql.internal;Database=orders;"));

    var detected = new AppSettingsConnectionScanner(_dir).Scan();

    Assert.Equal(DatabaseProvider.MySql, detected[0].Provider);
}

[Fact]
public void Scan_HostKeyedStringWithoutDatabase_IsNotImportable()
{
    Write("appsettings.json", ConnectionStrings("Default",
        "Host=pg.internal;Username=app;Password=pg-secret;"));

    var detected = new AppSettingsConnectionScanner(_dir).Scan();

    Assert.Null(detected[0].Provider);
    Assert.Equal("unrecognized connection string", detected[0].RejectionReason);
}
```

- [ ] **Step 2: Run the tests, verify they fail**

Run: `dotnet test tests/Eling.Core.Tests --filter "FullyQualifiedName~AppSettingsConnectionScanner" --artifacts-path .bin-test`
Expected: the PostgreSql facts FAIL; the MySQL/SQLite/Redis facts still pass.

- [ ] **Step 3: Implement** — extract `private static DetectedConnection? TryClassifyPostgreSql(string key, string value, bool npgOnlyKeywordRequired)`; wire it into `Classify` per the three-step algorithm; every `catch` stays logged-or-empty exactly as the existing provider blocks are (the existing blocks swallow silently by design — match that shape, no new logging in scanner).

- [ ] **Step 4: Run the tests, verify they pass**

Run: `dotnet test tests/Eling.Core.Tests --filter "FullyQualifiedName~AppSettingsConnectionScanner" --artifacts-path .bin-test`
Expected: PASS, all existing scanner facts green.

- [ ] **Step 5: Commit**

```bash
git add src/backend/Eling.Core/Database/AppSettingsConnectionScanner.cs tests/Eling.Core.Tests/AppSettingsConnectionScannerTests.cs
git commit -m "feat(core): detect PostgreSql connections in appsettings scan"
```

---

## Task 5: Frontend — provider union, helpers, select, placeholder

**Files:**
- Modify: `src/frontend/Eling.Dashboard/src/app/dashboard/databases/types.ts`
- Modify: `src/frontend/Eling.Dashboard/src/app/dashboard/databases/utils/connection-helpers.ts`
- Modify: `src/frontend/Eling.Dashboard/src/app/dashboard/databases/utils/index.ts`
- Modify: `src/frontend/Eling.Dashboard/src/app/dashboard/databases/components/connection-form/ConnectionFormFields.tsx`
- Create: `src/frontend/Eling.Dashboard/src/app/dashboard/databases/utils/connection-helpers.test.ts`

**Interfaces:**
- Consumes: `Provider` union, `deriveConnectionName`, `providerLabel`, `isMySql`, `isRedis` (existing)
- Produces: `Provider` including `"PostgreSql"`; `isPostgreSql(conn)` helper re-exported from `utils/index.ts`

Details (spec §7):
- `types.ts`: `export type Provider = "Sqlite" | "MySql" | "Redis" | "PostgreSql"`.
- `connection-helpers.ts`: per-provider default-port map — `{ MySql: 3306, PostgreSql: 5432, Redis: 6379 }` — used by `deriveConnectionName` (the `:{port}` suffix appears when the typed port differs from the provider's default; the literal `3306` at the suffix line goes away; Redis behavior improves to 6379 as a deliberate side effect — recorded here as the implementer's call, matching the scanner's Redis default). `providerLabel`: `PostgreSql → "PostgreSQL"`. New `isPostgreSql(conn)` mirroring `isMySql`.
- `utils/index.ts`: re-export `isPostgreSql` next to `isMySql`.
- `ConnectionFormFields.tsx`: add `<SelectItem value="PostgreSql">PostgreSql</SelectItem>` after the MySql item (keep value/label pairing identical to the others: value `PostgreSql`); port placeholder becomes provider-aware — `port (3306)` for MySql, `port (5432)` for PostgreSql, generic `port` otherwise.
- No changes to `useConnectionForm.ts`, `useTableData.ts`, `useTableMutations.ts`, `Databases.tsx` (the existing `provider !== "Sqlite"` and non-MySql-quoting branches are already correct; `quoteChar` yields `"` for PostgreSql).

- [ ] **Step 1: Write the failing test file `connection-helpers.test.ts`** (vitest, mirroring `DatabaseCombobox.test.tsx` style)

```ts
import { describe, it, expect } from "vitest"
import { deriveConnectionName, providerLabel, isPostgreSql } from "./connection-helpers"

describe("deriveConnectionName PostgreSql", () => {
  it("omits the port suffix on 5432", () => {
    expect(deriveConnectionName("PostgreSql", "pg.internal", "5432", "orders", "")).toBe("orders@pg.internal")
  })
  it("appends a non-default port", () => {
    expect(deriveConnectionName("PostgreSql", "pg.internal", "5433", "orders", "")).toBe("orders@pg.internal:5433")
  })
  it("keeps the server-level form without a database", () => {
    expect(deriveConnectionName("PostgreSql", "pg.internal", "5432", "", "")).toBe("pg.internal")
  })
})

describe("providerLabel", () => {
  it("labels PostgreSql as PostgreSQL", () => {
    expect(providerLabel("PostgreSql")).toBe("PostgreSQL")
  })
})

describe("isPostgreSql", () => {
  it("is true for PostgreSql and false for others", () => {
    expect(isPostgreSql({ provider: "PostgreSql" } as never)).toBe(true)
    expect(isPostgreSql({ provider: "MySql" } as never)).toBe(false)
    expect(isPostgreSql(null)).toBe(false)
  })
})
```

- [ ] **Step 2: Run it, verify it fails**

Run: `pnpm --dir src/frontend/Eling.Dashboard test -- connection-helpers.test.ts`
Expected: FAIL (`PostgreSql` not in the union / no `isPostgreSql`).

- [ ] **Step 3: Implement** the five file-level changes above.

- [ ] **Step 4: Verify**

Run: `pnpm --dir src/frontend/Eling.Dashboard test` then `pnpm --dir src/frontend/Eling.Dashboard exec tsc --noEmit`
Expected: vitest green (all files), no type errors. Do NOT run `pnpm build`.
- [ ] **Step 5: Commit**

```bash
git add src/frontend/Eling.Dashboard/src/app/dashboard/databases/types.ts src/frontend/Eling.Dashboard/src/app/dashboard/databases/utils/connection-helpers.ts src/frontend/Eling.Dashboard/src/app/dashboard/databases/utils/index.ts src/frontend/Eling.Dashboard/src/app/dashboard/databases/utils/connection-helpers.test.ts src/frontend/Eling.Dashboard/src/app/dashboard/databases/components/connection-form/ConnectionFormFields.tsx
git commit -m "feat(dashboard): PostgreSql provider in connection form"
```

---

## Task 6: Provider enumerations sweep (README, MCP text, SQL comments)

**Files:**
- Modify: `README.md` (line 26: "over MySQL/SQLite" → "over MySQL/PostgreSQL/SQLite")
- Modify: any `src/backend/**` file whose SQL comment or description enumerates providers, adding PostgreSql where a list genuinely exists (grep `MySql`, `mysql`, `3306`)
- Do NOT touch `useTableData.ts`/`Databases.tsx`/`useTableMutations.ts` (already correct)
- Do NOT rewrite the historical spec `2026-10-13-db-connection-design.md` (superseded; history stays)

- [ ] **Step 1: Produce the hit list**

Run: `git grep -n -i -E "MySql|3306" -- src/backend README.md | rg -v "test"`
Review each hit: provider *enumeration* → add PostgreSql; incidental mention in an example or unrelated identifier → leave.

- [ ] **Step 2: Edit the enumerated spots** (README line 26 at minimum; e.g. `Mcp/Tools/*` descriptions or `ServerInstructions.cs` only if they actually list providers — verify with the grep output, most are provider-agnostic).

- [ ] **Step 3: Verify nothing is missed and everything still builds/tests**

Run: `git grep -n -i -E "MySql|3306" -- src/backend README.md | rg -v "test"` (review remaining hits), then `pwsh scripts/test-backend.ps1 -Layer core`.

- [ ] **Step 4: Commit (only if Step 1 found enumerated spots)**

```bash
git add README.md <each enumerated file>
git commit -m "docs: PostgreSQL in provider lists"
```

---

## Task 7: Backend endpoint sanity for PostgreSql + test layers

**Files:**
- Modify: `tests/Eling.Backend.Tests/DatabaseManualConnectionTests.cs` (add facts)
- Verification only otherwise

**Interfaces:**
- Consumes: the same DI fixture as the MySql manual-connection tests above (no new host); endpoint behaviour from Tasks 2–4
- Produces: pinned sanity facts proving the wire value `PostgreSql` round-trips through create/get

- [ ] **Step 1: Add the sanity facts** (mirroring `Create_WithManualFields_StoresWhatWasTypedAndAnswersWithoutThePassword`)

```csharp
[Fact]
public async Task Create_WithPostgreSqlManualFields_StoresTheProviderAndAnswersWithoutThePassword()
{
    var res = await _client.PostAsync("/api/databases", Json($$"""
        {
          "name": "orders@10.0.0.5",
          "provider": "PostgreSql",
          "environment": "ReadOnly",
          "allowAgentMutations": false,
          "requireApprovalForMutations": true,
          "manual": {
            "host": "10.0.0.5",
            "port": 5432,
            "database": "orders",
            "username": "{{Username}}",
            "password": "{{Password}}"
          }
        }
        """));

    Assert.Equal(HttpStatusCode.Created, res.StatusCode);
    var body = await res.Content.ReadAsStringAsync();
    Assert.DoesNotContain(Password, body);
    using var doc = JsonDocument.Parse(body);
    Assert.Equal("PostgreSql", doc.RootElement.GetProperty("provider").GetString());
}

[Fact]
public async Task Create_WithUnknownProviderString_IsRejected()
{
    var res = await _client.PostAsync("/api/databases", Json("""
        {
          "name": "x",
          "provider": "NotAProvider",
          "environment": "ReadOnly",
          "allowAgentMutations": false,
          "requireApprovalForMutations": true,
          "connectionString": "Host=h;Database=d;"
        }
        """));

    Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
}
```

(The exact rejection shape follows however `Create` maps `Enum.IsDefined` failures today — mirror whatever a `"provider": "Redis"`-style unknown string yields; if it is not `400`, pin the actual observed status and say so in the report. This fact documents behavior; it must not change it.)

- [ ] **Step 2: Run the two facts**

Run: `dotnet test tests/Eling.Backend.Tests --filter "FullyQualifiedName~PostgreSql|FullyQualifiedName~UnknownProvider" --artifacts-path .bin-test`
Expected: PASS.

- [ ] **Step 3: Full core + api layers**

Run: `pwsh scripts/test-backend.ps1 -Layer all`
Expected: passes. If the pre-commit-style full suite hits the known Windows temp-file flake (unrelated `*MoveFile*`/`access denied` in `JsonCommitLeaseStoreTests` and friends), note it, re-run the failed class once, and report — never delete the failure root (`scripts/test-backend.ps1` keeps it on failure for inspection).

- [ ] **Step 4: Confirm the build one last time**

Run: `dotnet build Eling.slnx`
Expected: 0 errors.

- [ ] **Step 5: Commit**

```bash
git add tests/Eling.Backend.Tests/DatabaseManualConnectionTests.cs
git commit -m "test(backend): PostgreSql manual-connection sanity facts"
```

---

## Task 8: Live gate — dashboard registration by the user, `db_*` verification by the agent

**Files:** none

- [ ] **Step 1: Ask the user to register the Postgres connection** on `http://localhost:4427/dashboard/databases` → New connection → Provider PostgreSql → typed fields → Test → Save. Ask for pass/fail of the three clicks and the environment (which server). The form must show the 5432 placeholder and the PostgreSQL select item.
- [ ] **Step 2: The agent verifies the read path over the eling `db_*` tools:**
  - `db_list_connections` — the new row appears with provider `PostgreSql`.
  - `db_get_schema` (connectionId given, no `database`) — server-level: returns the database list; otherwise tables of the connected schema with columns and PKs.
  - `db_query` with `SELECT 1` and with a `SELECT * FROM <table> LIMIT 1` against a real table — rows return.
  - A disallowed mutation (e.g. `DELETE FROM …`) — expect the read-only error, nothing executed.
- [ ] **Step 3: Record the results** (pass/fail per step, which server, Npgsql version from Task 1 Step 3) in the completion report. If any step fails, report the category and keep the connection for debugging — do not work around it.
- [ ] **Step 4: Update the spec's status line** to shipped only after the whole gate is green, and commit it as a docs commit (`"docs: postgres support spec shipped"`).

---

## Spec coverage check (self-review)

| Spec § | Task |
|---|---|
| §3.2 connection-string semantics | Task 2 |
| §4 API & MCP surface (no new routes; adapter via factory) | Tasks 3, 4, 6, 7 |
| §5 adapter details (test/query/execute/list/schema/categorize) | Task 3 |
| §6 appsettings detect & import | Task 4 |
| §7 frontend | Task 5 (+ browser check in Task 8 Step 1) |
| §8 guardrails (categories only; maintenance-DB rule) | Task 3 (adapter), Task 8 (gate) |
| §9 testing strategy | Tasks 2–5 (unit), Task 7 (layers), Task 8 (gate) |
| §10/§11/§12 (approaches, decisions, follow-ups) | honoured; Npgsql version recorded in Task 8 Step 3 |
