# DB Connection Management (Phase 1) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Give Eling Backend a persistent, user-confirmed database connection registry (SQLite + MySQL) with read-only query/schema access over REST and MCP, plus local auto-detect from the backend's own `appsettings.json`.

**Architecture:** Domain, adapters, safety classifier, and the JSON registry live in `Eling.Core/Database/` (pattern: `JsonWorkspaceRegistry` — named mutex, atomic write, corrupt-tolerant). `Eling.Backend` adds ten minimal-API routes under `/api/databases`, five MCP tools under `Mcp/Tools/`, and two DI lines. Fase 1 is read-only against target databases: every executed statement passes `SqlSafety`, and mutations are rejected everywhere.

**Tech Stack:** .NET 10, C# 14, `Microsoft.Data.Sqlite` (already referenced), `MySqlConnector` (new, pinned at add time), ASP.NET Core minimal APIs, ModelContextProtocol.Server, xUnit.

**Spec:** `docs/superpowers/specs/2026-10-13-db-connection-design.md`

## Global Constraints

- All C# projects target `net10.0` with `Nullable=enable` and `ImplicitUsings=enable` (root `Directory.Build.props`); no CPM — versions live per csproj.
- NEVER commit, push, amend, or reset. Leave work in the working tree; each task ends with a `git status --short` report (repo convention).
- Tests run per csproj only, never solution-wide: `dotnet test tests/Eling.Core.Tests/Eling.Core.Tests.csproj --artifacts-path .bin-test` (same shape for `Eling.Backend.Tests`). Run shell commands as separate steps, no `&&` chains.
- No credential ever crosses a boundary: stored connection strings are never returned by any API/MCP response; `detect` output is masked (`Password=***`, unparseable → `"***"`); `import` carries approved `settingsKeys` only and the backend re-reads its own `appsettings.json`. Raw strings are accepted only where the user typed them (manual create/update). Logs and error messages never include the connection string. Sanitized failure categories only.
- **Agent visibility boundary (MCP):** MCP outputs, parameters, and errors may carry connection *identity* — `connectionId`, the `{db}@{host}` name, `databaseName`, `provider`, `environment` — but never credentials: no username, no password, no connection string in any form (raw or masked), and never provider exception text (it can embed `user@host`). MCP failures surface category strings only ("query failed"); detail goes to logs. REST additionally serves password-masked strings to the same-host Dashboard.
- Every executed statement passes `SqlSafety.Classify` first; mutation keywords are rejected with the classifier's reason.
- Registry persistence uses `Eling.Core.Serialization.JsonDefaults.Shared` (camelCase, case-insensitive, indented), the same options `JsonWorkspaceRegistry` uses. Store file: `databases.json` under `CentralAgentDirectory.Resolve()`; tests isolate it via the `ELING_AGENT_DATA_DIR` seam that `TestAppBuilder.CreateSelfContained` already sets.
- Store internals mirror `JsonWorkspaceRegistry`: named mutex `Global\eling-databases-registry`, 5s bounded gate, `AbandonedMutexException` = acquired, temp file + `File.Move(overwrite: true)`, corrupt/wrong-shape file = empty + warning log, synchronous file IO inside the gate.
- One top-level type per file; records with >1 parameter are written one-param-per-line; no `ValueTuple` in public signatures.
- Every `catch` block logs via injected `ILogger`. No silent catches.
- No machine-specific absolute paths or usernames in tracked files; tests use `Path.GetTempPath()` + GUID.
- All adapter operations accept `CancellationToken` and default to a 30s command timeout, linked to the caller's token.
- Wire JSON is camelCase (global options); enums serialize as lowercase strings via the registered `JsonStringEnumConverter`.
- New MCP tool classes under `Mcp/Tools/` are auto-discovered by the existing `WithToolsFromAssembly()`; add one plain-language entry to `ServerInstructions.Sections`.

## Review Focus

Failure modes the spec implies but no single task's happy-path tests exercise — each is pinned in the task that owns the code:

1. **Multi-statement bypass** — `SELECT 1; DROP TABLE t` must be rejected, not executed as a leading-`SELECT` statement. Pinned in Task 2 (`Classify_SelectFollowedByDrop_Rejects`).
2. **Credential leakage in responses** — no list/get/registry body ever contains a stored connection string, and no detect body contains a password: detect output is masked and import is key-based, so nothing credential-bearing reaches clients or the agent's context. Pinned in Task 6 (`Scan_MasksPasswords_InMaskedField`) and Task 7 (`Detect_NeverLeaksPassword`, `GetList_NeverContainsConnectionString`).
3. **Agent sees credentials** — no MCP output, parameter, or error may contain a username, password, connection string (raw or masked), or provider exception text (which can embed `user@host`). Identity (`connectionId`, `db@host` name) is allowed. Pinned in Task 8 (`List_NeverContainsCredentials`, `Detect_NeverContainsCredentials`, `Import_NeverContainsCredentials`, `Query_Failure_SanitizedMessage`).
4. **Hand-edited / corrupt `databases.json`** — wrong shape, invalid JSON, or an unknown provider value (e.g. `postgresql`) must degrade to empty + warning log, and an unknown-provider row must produce a clean 400, not a crash. Pinned in Task 3 (`Load_WhenFileIsCorrupt_ReturnsEmpty`) and Task 7 (`Query_WhenStoredProviderUnknown_Returns400`).
5. **Import idempotency** — importing the same key twice must skip the second with reason and leave exactly one registry entry. Pinned in Task 7 (`Import_DuplicateName_SkipsSecondWithReason`).
6. **Relative SQLite DataSource** — a connection string written on another machine (`Data Source=eling.db`) must resolve to an absolute path (process cwd) before connecting, and the adapter must not silently create parent directories. Pinned in Task 4 (`Test_RelativeDataSource_ResolvesAgainstCwd`).

---

### Task 1: `DatabaseConnection` model, enums, validation

**Files:**
- Create: `src/backend/Eling.Core/Database/DatabaseProvider.cs`
- Create: `src/backend/Eling.Core/Database/DatabaseEnvironment.cs`
- Create: `src/backend/Eling.Core/Database/DatabaseConnection.cs`
- Test: `tests/Eling.Core.Tests/Database/DatabaseConnectionTests.cs`

**Interfaces:**
- Produces:
  - `Eling.Core.Database.DatabaseProvider` — enum `Sqlite, MySql`
  - `Eling.Core.Database.DatabaseEnvironment` — enum `Development, Staging, Production, ReadOnly`
  - `Eling.Core.Database.DatabaseConnection` — `sealed record` with positional members `Id, Name, Provider, ConnectionString, Environment, AllowAgentMutations, RequireApprovalForMutations, CreatedAt, LastConnectedAt` and a static factory:
    `DatabaseConnection Create(string name, DatabaseProvider provider, string connectionString, DatabaseEnvironment environment, bool allowAgentMutations = false, bool requireApprovalForMutations = true, DateTimeOffset? createdAt = null)`
  - Factory mints `Id` as `Ulid.NewUlid().ToString().ToLowerInvariant()` (the `Ulid` package is already referenced), sets `CreatedAt = createdAt ?? DateTimeOffset.UtcNow`, `LastConnectedAt = null`, and throws `ArgumentException` with a field-naming message when: `name` is null/whitespace, `connectionString` is null/whitespace, or provider/environment are not defined enum values (`Enum.IsDefined`).

- [ ] **Step 1: Write the failing test**

`tests/Eling.Core.Tests/Database/DatabaseConnectionTests.cs`
```csharp
using Eling.Core.Database;

namespace Eling.Core.Tests.Database;

public sealed class DatabaseConnectionTests
{
    [Fact]
    public void Create_ValidInput_MintsIdAndKeepsValues()
    {
        var at = new DateTimeOffset(2026, 10, 13, 12, 0, 0, TimeSpan.Zero);

        var connection = DatabaseConnection.Create(
            "orders@10.0.0.5",
            DatabaseProvider.MySql,
            "Server=10.0.0.5;Database=orders;User ID=u;Password=p",
            DatabaseEnvironment.Development,
            createdAt: at);

        Assert.False(string.IsNullOrWhiteSpace(connection.Id));
        Assert.Equal("orders@10.0.0.5", connection.Name);
        Assert.Equal(DatabaseProvider.MySql, connection.Provider);
        Assert.Equal(DatabaseEnvironment.Development, connection.Environment);
        Assert.False(connection.AllowAgentMutations);
        Assert.True(connection.RequireApprovalForMutations);
        Assert.Equal(at, connection.CreatedAt);
        Assert.Null(connection.LastConnectedAt);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Create_BlankName_Throws(string name)
        => Assert.Throws<ArgumentException>(() => DatabaseConnection.Create(
            name, DatabaseProvider.Sqlite, "Data Source=x.db", DatabaseEnvironment.ReadOnly));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Create_BlankConnectionString_Throws(string connectionString)
        => Assert.Throws<ArgumentException>(() => DatabaseConnection.Create(
            "db@host", DatabaseProvider.Sqlite, connectionString, DatabaseEnvironment.ReadOnly));

    [Fact]
    public void Create_UndefinedEnumValues_Throw()
    {
        Assert.Throws<ArgumentException>(() => DatabaseConnection.Create(
            "db@host", (DatabaseProvider)99, "Data Source=x.db", DatabaseEnvironment.ReadOnly));
        Assert.Throws<ArgumentException>(() => DatabaseConnection.Create(
            "db@host", DatabaseProvider.Sqlite, "Data Source=x.db", (DatabaseEnvironment)99));
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Eling.Core.Tests/Eling.Core.Tests.csproj --artifacts-path .bin-test --filter "FullyQualifiedName~DatabaseConnectionTests"`
Expected: build FAILURE — `Eling.Core.Database` types do not exist.

- [ ] **Step 3: Implement the three files**

`DatabaseProvider.cs` / `DatabaseEnvironment.cs`: plain enums (one file each, namespace `Eling.Core.Database`). `DatabaseConnection.cs`: the record per the Produces block — positional members one-per-line, public `Create` factory containing the validation; record itself carries no behavior.

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/Eling.Core.Tests/Eling.Core.Tests.csproj --artifacts-path .bin-test --filter "FullyQualifiedName~DatabaseConnectionTests"`
Expected: PASS.

- [ ] **Step 5: Report checkpoint**

Run: `git status --short` — report the new files. Do not commit.

---

### Task 2: `SqlSafety` read-only classifier

**Files:**
- Create: `src/backend/Eling.Core/Database/SqlSafety.cs`
- Test: `tests/Eling.Core.Tests/Database/SqlSafetyClassifierTests.cs`

**Interfaces:**
- Produces:
  - `Eling.Core.Database.SqlVerdict` — enum `ReadOnly, Rejected`
  - `Eling.Core.Database.SqlClassification` — `sealed record(SqlVerdict Verdict, string? Reason)`
  - `Eling.Core.Database.SqlSafety` — static class with `SqlClassification Classify(string? sql)`

**Algorithm (the signature and tests do not determine it):**
1. Null/whitespace → `Rejected("empty statement")`.
2. Single-pass strip: walk the string tracking single-quoted literals (`''` escape); outside literals, replace `--`-to-end-of-line and `/* */` ranges with spaces; replace literal contents with spaces (so `'delete'` is invisible to the scan).
3. First remaining token (whitespace-split, leading punctuation like `(` trimmed) must case-insensitively equal `select`, `explain`, or `pragma` — everything else, including `with`, is `Rejected("only SELECT/EXPLAIN/PRAGMA statements are allowed")`.
4. Scan all tokens for the mutation keywords `insert, update, delete, drop, alter, create, truncate, replace, grant, revoke, attach, detach, vacuum` — a hit is `Rejected("statement contains mutation keyword '<kw>'")` (catches `SELECT 1; DROP TABLE t`).
5. Otherwise `ReadOnly` with `Reason = null`.

- [ ] **Step 1: Write the failing test**

`tests/Eling.Core.Tests/Database/SqlSafetyClassifierTests.cs`
```csharp
using Eling.Core.Database;

namespace Eling.Core.Tests.Database;

public sealed class SqlSafetyClassifierTests
{
    [Theory]
    [InlineData("SELECT * FROM t")]
    [InlineData("select id from t where name = 'delete'")]
    [InlineData("  EXPLAIN  SELECT 1")]
    [InlineData("PRAGMA table_info(t)")]
    [InlineData("-- drop table t\nSELECT 1")]
    [InlineData("/* update x */ SELECT 1")]
    public void Classify_ReadOnlyStatements_Allow(string sql)
        => Assert.Equal(SqlVerdict.ReadOnly, SqlSafety.Classify(sql).Verdict);

    [Theory]
    [InlineData("INSERT INTO t VALUES (1)")]
    [InlineData("UPDATE t SET a = 1")]
    [InlineData("DELETE FROM t")]
    [InlineData("DROP TABLE t")]
    [InlineData("ALTER TABLE t ADD c INT")]
    [InlineData("CREATE TABLE t (a INT)")]
    [InlineData("TRUNCATE t")]
    [InlineData("GRANT ALL ON t TO u")]
    [InlineData("ATTACH DATABASE 'x.db' AS x")]
    [InlineData("WITH x AS (SELECT 1) SELECT * FROM x")]
    [InlineData("SELECT 1; DROP TABLE t")]
    [InlineData("")]
    [InlineData("   ")]
    public void Classify_MutationsAndGaps_Reject(string sql)
    {
        var verdict = SqlSafety.Classify(sql);
        Assert.Equal(SqlVerdict.Rejected, verdict.Verdict);
        Assert.False(string.IsNullOrWhiteSpace(verdict.Reason));
    }

    [Fact]
    public void Classify_NullInput_Rejects()
        => Assert.Equal(SqlVerdict.Rejected, SqlSafety.Classify(null).Verdict);

    [Fact]
    public void Classify_LiteralContainingMutationKeyword_Allows()
        => Assert.Equal(
            SqlVerdict.ReadOnly,
            SqlSafety.Classify("SELECT 'please delete this' AS note").Verdict);
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Eling.Core.Tests/Eling.Core.Tests.csproj --artifacts-path .bin-test --filter "FullyQualifiedName~SqlSafetyClassifierTests"`
Expected: build FAILURE — `SqlSafety` does not exist.

- [ ] **Step 3: Implement `SqlSafety.cs`** per the algorithm above. Static class, no state, no logging.

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/Eling.Core.Tests/Eling.Core.Tests.csproj --artifacts-path .bin-test --filter "FullyQualifiedName~SqlSafetyClassifierTests"`
Expected: PASS.

- [ ] **Step 5: Report checkpoint**

Run: `git status --short`. Do not commit.

---

### Task 3: `IDatabaseConnectionStore` + `JsonDatabaseConnectionStore`

**Files:**
- Create: `src/backend/Eling.Core/Database/IDatabaseConnectionStore.cs`
- Create: `src/backend/Eling.Core/Database/JsonDatabaseConnectionStore.cs`
- Test: `tests/Eling.Core.Tests/Database/JsonDatabaseConnectionStoreTests.cs`

**Interfaces:**
- Consumes: `DatabaseConnection` (Task 1).
- Produces:
  - `Eling.Core.Database.IDatabaseConnectionStore` with:
    - `Task<IReadOnlyList<DatabaseConnection>> ListAsync(CancellationToken ct = default)`
    - `Task<DatabaseConnection?> FindAsync(string id, CancellationToken ct = default)`
    - `Task UpsertAsync(DatabaseConnection connection, CancellationToken ct = default)` — blank `Id` is minted (`Ulid`, lowercased); existing `Id` replaces in place; new rows append (list order preserved).
    - `Task<bool> RemoveAsync(string id, CancellationToken ct = default)`
    - `Task MarkConnectedAsync(string id, DateTimeOffset at, CancellationToken ct = default)` — re-reads under the gate, sets `LastConnectedAt`, saves; no-op returning `Task` when id unknown.
  - `Eling.Core.Database.JsonDatabaseConnectionStore : IDatabaseConnectionStore` — ctor `(string filePath, ILogger<JsonDatabaseConnectionStore>? logger = null)`; serialization via `JsonDefaults.Shared`; all behavior per the Global Constraints (mutex/gate/atomic/corrupt-tolerant, sync IO inside gate).

- [ ] **Step 1: Write the failing test**

`tests/Eling.Core.Tests/Database/JsonDatabaseConnectionStoreTests.cs` — mirror `JsonWorkspaceRegistryTests` structure: `IDisposable`, temp dir via `Path.GetTempPath()` + GUID, `RegistryPath => Path.Combine(_dir, "databases.json")`. Facts:
```csharp
Upsert_WhenIdBlank_MintsIdAndPersists                    // FindAsync returns row with non-blank Id
Upsert_WhenIdExists_ReplacesInPlace                      // same Id, updated Name, list stays 1 row
List_WhenFileMissing_ReturnsEmpty
List_WhenFileIsCorrupt_ReturnsEmpty                      // write "{not-json" first; then upsert rewrites cleanly
Remove_UnknownId_ReturnsFalse
Persists_AcrossStoreInstances                            // two stores on same path
TwoInstancesInSameProcess_KeepEveryRow                   // interleaved upserts; final list has all rows
```
Helper: `var row = DatabaseConnection.Create("db@host", DatabaseProvider.Sqlite, "Data Source=" + Path.Combine(_dir, "x.db"), DatabaseEnvironment.Development);`

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Eling.Core.Tests/Eling.Core.Tests.csproj --artifacts-path .bin-test --filter "FullyQualifiedName~JsonDatabaseConnectionStoreTests"`
Expected: build FAILURE — store types do not exist.

- [ ] **Step 3: Implement the interface and store**

`JsonDatabaseConnectionStore.cs`: copy `JsonWorkspaceRegistry`'s gate skeleton verbatim (mutex name `Global\eling-databases-registry`, `GateTimeout` 5s, `EnterGate` swallowing `AbandonedMutexException`, `WithGate` overloads, `LoadCore`/`SaveCore` with `.tmp` + `File.Move(overwrite: true)`, corrupt → `[]` + `LogWarning`). `IDisposable.Dispose` stays empty with the same comment as the registry (process-shared mutex). Methods are `async Task` shapes wrapping the sync gate work in `Task.FromResult` — same rationale as the registry's remarks.

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/Eling.Core.Tests/Eling.Core.Tests.csproj --artifacts-path .bin-test --filter "FullyQualifiedName~JsonDatabaseConnectionStoreTests"`
Expected: PASS.

- [ ] **Step 5: Report checkpoint**

Run: `git status --short`. Do not commit.

---

### Task 4: Adapter types, factory, `SqliteDatabaseAdapter`

**Files:**
- Create: `src/backend/Eling.Core/Database/ConnectionTestResult.cs`
- Create: `src/backend/Eling.Core/Database/QueryResult.cs`
- Create: `src/backend/Eling.Core/Database/DatabaseSchemaInfo.cs`
- Create: `src/backend/Eling.Core/Database/IDatabaseAdapter.cs`
- Create: `src/backend/Eling.Core/Database/Adapters/DatabaseAdapterFactory.cs`
- Create: `src/backend/Eling.Core/Database/Adapters/SqliteDatabaseAdapter.cs`
- Test: `tests/Eling.Core.Tests/Database/Adapters/SqliteDatabaseAdapterTests.cs`

**Interfaces:**
- Produces:
  - `ConnectionTestResult(bool Ok, string Message, long LatencyMs)`
  - `QueryResult(IReadOnlyList<string> Columns, IReadOnlyList<IReadOnlyList<object?>> Rows, bool Truncated)`
  - `ColumnSchema(string Name, string DataType, bool IsNullable, bool IsPrimaryKey)`
  - `ForeignKeySchema(string Name, string Column, string ReferencesTable, string ReferencesColumn)`
  - `IndexSchema(string Name, bool IsUnique, IReadOnlyList<string> Columns)`
  - `TableSchema(string Name, string Kind, IReadOnlyList<ColumnSchema> Columns, IReadOnlyList<ForeignKeySchema> ForeignKeys, IReadOnlyList<IndexSchema> Indexes)`
  - `DatabaseSchemaInfo(IReadOnlyList<TableSchema> Tables)`
  - `IDatabaseAdapter` with `Task<ConnectionTestResult> TestAsync(CancellationToken ct = default)`, `Task<QueryResult> QueryAsync(string sql, int maxRows, CancellationToken ct = default)`, `Task<DatabaseSchemaInfo> GetSchemaAsync(CancellationToken ct = default)`
  - `DatabaseAdapterFactory.CreateAdapter(DatabaseConnection connection)` — `Sqlite` → `new SqliteDatabaseAdapter(connection)`; `MySql` → throws `NotSupportedException("MySql adapter is not implemented yet.")` until Task 5 replaces it; any other value → `NotSupportedException($"Provider '{connection.Provider}' is not supported.")`
  - `SqliteDatabaseAdapter` — ctor `(DatabaseConnection connection, ILogger<SqliteDatabaseAdapter>? logger = null)`

**SqliteDatabaseAdapter behavior (tests do not determine it):**
- Parse the incoming connection string with `SqliteConnectionStringBuilder`; take `DataSource`, resolve to absolute with `Path.GetFullPath` (relative resolves against process cwd — never create parent directories); rebuild the builder with `DataSource = absolute`, `Pooling = false`; every `SqliteCommand.CommandTimeout = 30`.
- `TestAsync`: stopwatch around `OpenAsync` + `SELECT 1;` → `Ok = true`. On exception: log the detail via `ILogger`, return `Ok = false, Message = "connection failed"` (never the exception text, which can contain the path).
- `QueryAsync`: execute `sql` as given; read up to `maxRows + 1` rows — more than `maxRows` present → `Truncated = true` and only `maxRows` returned. `Columns` from `reader.GetName(i)`; cell values via `reader.GetValue(i)`, `DBNull` → `null`.
- `GetSchemaAsync`: `sqlite_master` for `type IN ('table','view')` excluding `name LIKE 'sqlite_%'` (`Kind` = `"table"`/`"view"`); `PRAGMA table_info("<name>")` → columns (`pk > 0` → `IsPrimaryKey`); `PRAGMA foreign_key_list("<name>")` → FKs; `PRAGMA index_list` + `PRAGMA index_info` → indexes.

- [ ] **Step 1: Write the failing test**

`tests/Eling.Core.Tests/Database/Adapters/SqliteDatabaseAdapterTests.cs`
```csharp
using Microsoft.Data.Sqlite;
using Eling.Core.Database;

namespace Eling.Core.Tests.Database.Adapters;

public sealed class SqliteDatabaseAdapterTests : IDisposable
{
    private readonly string _dir;
    public SqliteDatabaseAdapterTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "eling-sqlite-adapter-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    private string DbPath => Path.Combine(_dir, "adapter.db");

    private SqliteDatabaseAdapter NewAdapter() => new(
        DatabaseConnection.Create("adapter@local", DatabaseProvider.Sqlite,
            "Data Source=" + DbPath, DatabaseEnvironment.Development));

    [Fact]
    public async Task Test_ThenQueryAndSchema_RoundTrips()
    {
        await using (var setup = new SqliteConnection("Data Source=" + DbPath))
        {
            await setup.OpenAsync();
            await using var cmd = setup.CreateCommand();
            cmd.CommandText = "CREATE TABLE t (id INTEGER PRIMARY KEY, name TEXT NOT NULL);"
                + "INSERT INTO t (id, name) VALUES (1, 'a'), (2, 'b');";
            await cmd.ExecuteNonQueryAsync();
        }

        var adapter = NewAdapter();

        var test = await adapter.TestAsync();
        Assert.True(test.Ok);
        Assert.True(test.LatencyMs >= 0);

        var query = await adapter.QueryAsync("SELECT id, name FROM t ORDER BY id", maxRows: 10);
        Assert.Equal(["id", "name"], query.Columns);
        Assert.Equal(2, query.Rows.Count);
        Assert.False(query.Truncated);

        var capped = await adapter.QueryAsync("SELECT id FROM t", maxRows: 1);
        Assert.True(capped.Truncated);
        Assert.Single(capped.Rows);

        var schema = await adapter.GetSchemaAsync();
        var table = Assert.Single(schema.Tables);
        Assert.Equal("t", table.Name);
        Assert.Contains(table.Columns, c => c.Name == "id" && c.IsPrimaryKey);
        Assert.Contains(table.Columns, c => c.Name == "name" && !c.IsNullable);
    }

    [Fact]
    public async Task Test_UnopenablePath_ReturnsOkFalseWithoutLeakingPath()
    {
        var adapter = new SqliteDatabaseAdapter(DatabaseConnection.Create(
            "bad@local", DatabaseProvider.Sqlite, "Data Source=" + _dir, DatabaseEnvironment.Development));

        var result = await adapter.TestAsync();

        Assert.False(result.Ok);
        Assert.DoesNotContain(_dir, result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Test_RelativeDataSource_ResolvesAgainstCwd()
    {
        var relative = "adapter-relative-" + Guid.NewGuid().ToString("N") + ".db";
        try
        {
            var adapter = new SqliteDatabaseAdapter(DatabaseConnection.Create(
                "rel@local", DatabaseProvider.Sqlite, "Data Source=" + relative, DatabaseEnvironment.Development));

            var result = await adapter.TestAsync();

            Assert.True(result.Ok);
            Assert.True(File.Exists(Path.GetFullPath(relative)));
        }
        finally
        {
            File.Delete(Path.GetFullPath(relative));
        }
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); } catch { }
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Eling.Core.Tests/Eling.Core.Tests.csproj --artifacts-path .bin-test --filter "FullyQualifiedName~SqliteDatabaseAdapterTests"`
Expected: build FAILURE — adapter types do not exist.

- [ ] **Step 3: Implement the record files, `IDatabaseAdapter`, factory (SQLite arm only), and `SqliteDatabaseAdapter`** per the behavior block.

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/Eling.Core.Tests/Eling.Core.Tests.csproj --artifacts-path .bin-test --filter "FullyQualifiedName~SqliteDatabaseAdapterTests"`
Expected: PASS.

- [ ] **Step 5: Report checkpoint**

Run: `git status --short`. Do not commit.

---

### Task 5: `MySqlDatabaseAdapter` + `MySqlConnector` package

**Files:**
- Modify: `src/backend/Eling.Core/Eling.Core.csproj` (one `PackageReference`, added via `dotnet add`)
- Create: `src/backend/Eling.Core/Database/Adapters/MySqlDatabaseAdapter.cs`
- Modify: `src/backend/Eling.Core/Database/Adapters/DatabaseAdapterFactory.cs` (replace the `MySql` placeholder arm)
- Test: `tests/Eling.Core.Tests/Database/Adapters/MySqlDatabaseAdapterTests.cs`

**Interfaces:**
- Consumes: `IDatabaseAdapter`, `DatabaseConnection` (Tasks 1, 4).
- Produces: `MySqlDatabaseAdapter : IDatabaseAdapter` — ctor `(DatabaseConnection connection, ILogger<MySqlDatabaseAdapter>? logger = null)`; `DatabaseAdapterFactory.CreateAdapter` now routes `MySql` to it.

**MySqlDatabaseAdapter behavior:**
- `new MySqlConnection(connection.ConnectionString)`; `CommandTimeout = 30` on every command; same sanitized-failure contract as the SQLite adapter (`Ok = false`, category message only, detail logged).
- `GetSchemaAsync` — four `information_schema` queries against `table_schema = DATABASE()`: `tables` (table_name, table_type), `columns` (ordered by `ordinal_position`; `column_key = 'PRI'` → PK; `is_nullable = 'YES'` → nullable), `key_column_usage` where `referenced_table_name IS NOT NULL` (FKs), `statistics` grouped per index (unique flag + ordered columns).

- [ ] **Step 1: Write the failing test**

`tests/Eling.Core.Tests/Database/Adapters/MySqlDatabaseAdapterTests.cs` (no live server in CI):
```csharp
using Eling.Core.Database;
using Eling.Core.Database.Adapters;

namespace Eling.Core.Tests.Database.Adapters;

public sealed class MySqlDatabaseAdapterTests
{
    [Fact]
    public void Factory_MySqlProvider_ReturnsMySqlAdapter()
        => Assert.IsType<MySqlDatabaseAdapter>(DatabaseAdapterFactory.CreateAdapter(
            DatabaseConnection.Create("orders@10.0.0.5", DatabaseProvider.MySql,
                "Server=10.0.0.5;Database=orders;User ID=u;Password=p",
                DatabaseEnvironment.Development)));

    [Fact]
    public void Factory_UnknownProvider_ThrowsNotSupported()
        => Assert.Throws<NotSupportedException>(() => DatabaseAdapterFactory.CreateAdapter(
            DatabaseConnection.Create("x@y", DatabaseProvider.Sqlite, "Data Source=x.db",
                DatabaseEnvironment.Development) with { Provider = (DatabaseProvider)99 }));

    [Fact]
    public async Task Test_UnreachableServer_ReturnsOkFalse()
    {
        var adapter = new MySqlDatabaseAdapter(DatabaseConnection.Create(
            "down@127.0.0.1", DatabaseProvider.MySql,
            "Server=127.0.0.1;Port=1;Database=eling;User ID=u;Password=p;Connection Timeout=2",
            DatabaseEnvironment.Development));

        var result = await adapter.TestAsync();

        Assert.False(result.Ok);
        Assert.DoesNotContain("Password", result.Message, StringComparison.OrdinalIgnoreCase);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Eling.Core.Tests/Eling.Core.Tests.csproj --artifacts-path .bin-test --filter "FullyQualifiedName~MySqlDatabaseAdapterTests"`
Expected: build FAILURE — `MySqlDatabaseAdapter` does not exist.

- [ ] **Step 3: Add the package, then implement the adapter and swap the factory arm**

Run: `dotnet add src/backend/Eling.Core/Eling.Core.csproj package MySqlConnector`
Record the resolved version (expected 2.6.x) in the report. Implement the adapter per the behavior block; replace the placeholder `MySql` arm in `DatabaseAdapterFactory` with `new MySqlDatabaseAdapter(connection)`.

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/Eling.Core.Tests/Eling.Core.Tests.csproj --artifacts-path .bin-test --filter "FullyQualifiedName~MySqlDatabaseAdapterTests"`
Expected: PASS. Then the full Core suite:

Run: `dotnet test tests/Eling.Core.Tests/Eling.Core.Tests.csproj --artifacts-path .bin-test`
Expected: PASS, no regressions.

- [ ] **Step 5: Report checkpoint**

Run: `git status --short` — include the resolved `MySqlConnector` version. Do not commit.

---

### Task 6: `AppSettingsConnectionScanner` (local-only, `{db}@{host}` names)

**Files:**
- Create: `src/backend/Eling.Core/Database/DetectedConnection.cs`
- Create: `src/backend/Eling.Core/Database/AppSettingsConnectionScanner.cs`
- Test: `tests/Eling.Core.Tests/Database/AppSettingsConnectionScannerTests.cs`

**Interfaces:**
- Produces:
  - `DetectedConnection(string SettingsKey, string Name, string ConnectionString, string MaskedConnectionString, DatabaseProvider? Provider, string? RejectionReason)`
  - `AppSettingsConnectionScanner` — ctor `(string filePath, ILogger<AppSettingsConnectionScanner>? logger = null)`; `IReadOnlyList<DetectedConnection> Scan()`

**Classification algorithm:**
- Missing file → `[]`. Invalid JSON / no `ConnectionStrings` object → `[]` + warning log. Property-name match is case-insensitive; non-string values → candidate with `RejectionReason = "value is not a string"`.
- Per string value: try `MySqlConnectionStringBuilder(value)` — `Server` (or `Host`) **and** `Database` non-empty → MySQL; else try `SqliteConnectionStringBuilder(value)` — `DataSource` non-empty → SQLite; else `Provider = null`, `RejectionReason = "unrecognized connection string"`, `Name = SettingsKey` (fallback).
- Name derivation: MySQL → `{Database}@{Server}` plus `:{Port}` when `Port != 3306`; SQLite → `{Path.GetFileNameWithoutExtension(DataSource)}@local`, with `:memory:` or empty falling back to `SettingsKey`.
- **Masking:** `MaskedConnectionString` is produced at scan time — recognized MySQL: builder round-trip with `Password = "***"`; recognized SQLite: builder round-trip (no secret component); unrecognized: `"***"`. The raw `ConnectionString` stays on the record for backend import use and never appears in any API/MCP payload.

- [ ] **Step 1: Write the failing test**

`tests/Eling.Core.Tests/Database/AppSettingsConnectionScannerTests.cs`
```csharp
using Eling.Core.Database;

namespace Eling.Core.Tests.Database;

public sealed class AppSettingsConnectionScannerTests : IDisposable
{
    private readonly string _dir;
    public AppSettingsConnectionScannerTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "eling-scanner-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    private string WriteSettings(string json) 
    {
        var path = Path.Combine(_dir, "appsettings.json");
        File.WriteAllText(path, json);
        return path;
    }

    [Fact]
    public void Scan_MixedEntries_ClassifiesAndDerivesNames()
    {
        var path = WriteSettings("""
        {
          "Logging": { "LogLevel": { "Default": "Information" } },
          "ConnectionStrings": {
            "Default": "Data Source=eling.db",
            "Orders": "Server=10.0.0.5;Port=3307;Database=orders;User ID=u;Password=p",
            "Weird": "foo=bar"
          }
        }
        """);

        var results = new AppSettingsConnectionScanner(path).Scan();

        Assert.Equal(3, results.Count);
        var sqlite = results.Single(r => r.SettingsKey == "Default");
        Assert.Equal(DatabaseProvider.Sqlite, sqlite.Provider);
        Assert.Equal("eling@local", sqlite.Name);
        var mysql = results.Single(r => r.SettingsKey == "Orders");
        Assert.Equal(DatabaseProvider.MySql, mysql.Provider);
        Assert.Equal("orders@10.0.0.5:3307", mysql.Name);
        var weird = results.Single(r => r.SettingsKey == "Weird");
        Assert.Null(weird.Provider);
        Assert.False(string.IsNullOrWhiteSpace(weird.RejectionReason));
        Assert.Equal("Weird", weird.Name);
        Assert.Equal("***", weird.MaskedConnectionString);
    }

    [Fact]
    public void Scan_MasksPasswords_InMaskedField()
    {
        var path = WriteSettings("""
        {
          "ConnectionStrings": {
            "Orders": "Server=10.0.0.5;Port=3307;Database=orders;User ID=u;Password=sup3r-secret"
          }
        }
        """);

        var result = Assert.Single(new AppSettingsConnectionScanner(path).Scan());

        Assert.DoesNotContain("sup3r-secret", result.MaskedConnectionString);
        Assert.Contains("***", result.MaskedConnectionString);
        Assert.Contains("sup3r-secret", result.ConnectionString); // raw stays internal to Core
    }

    [Fact]
    public void Scan_MissingSection_ReturnsEmpty()
        => Assert.Empty(new AppSettingsConnectionScanner(WriteSettings("""{"Logging":{}}""")).Scan());

    [Fact]
    public void Scan_CorruptFile_ReturnsEmpty()
        => Assert.Empty(new AppSettingsConnectionScanner(WriteSettings("{not-json")).Scan());

    [Fact]
    public void Scan_MissingFile_ReturnsEmpty()
        => Assert.Empty(new AppSettingsConnectionScanner(Path.Combine(_dir, "nope.json")).Scan());

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); } catch { }
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Eling.Core.Tests/Eling.Core.Tests.csproj --artifacts-path .bin-test --filter "FullyQualifiedName~AppSettingsConnectionScannerTests"`
Expected: build FAILURE — scanner types do not exist.

- [ ] **Step 3: Implement `DetectedConnection` and the scanner** per the algorithm. No persistence anywhere in this task.

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/Eling.Core.Tests/Eling.Core.Tests.csproj --artifacts-path .bin-test --filter "FullyQualifiedName~AppSettingsConnectionScannerTests"`
Expected: PASS.

- [ ] **Step 5: Report checkpoint**

Run: `git status --short`. Do not commit.

---

### Task 7: REST endpoints (`/api/databases`) + DTOs + importer + DI wiring

**Files:**
- Create: `src/backend/Eling.Backend/Database/DatabaseConnectionImporter.cs`
- Create: `src/backend/Eling.Backend/Dtos/DatabaseDtos.cs`
- Create: `src/backend/Eling.Backend/Endpoints/DatabaseEndpoints.cs`
- Modify: `src/backend/Eling.Backend/Bootstrap/DashboardServices.cs` (two DI registrations + one using)
- Modify: `src/backend/Eling.Backend/Bootstrap/DashboardRoutes.cs` (one `Map` call)
- Test: `tests/Eling.Backend.Tests/DatabaseEndpointsTests.cs`
- Test: `tests/Eling.Backend.Tests/DatabaseDetectImportTests.cs`

**Interfaces:**
- Consumes: `DatabaseConnection`, `IDatabaseConnectionStore` (Tasks 1, 3), `SqlSafety` (Task 2), `DatabaseAdapterFactory` (Tasks 4, 5), `AppSettingsConnectionScanner` (Task 6).
- Produces (namespace `Eling.Backend.Dtos`):
  - `DatabaseConnectionView(string Id, string Name, DatabaseProvider Provider, DatabaseEnvironment Environment, bool HasConnectionString, bool AllowAgentMutations, bool RequireApprovalForMutations, DateTimeOffset CreatedAt, DateTimeOffset? LastConnectedAt)`
  - `CreateDatabaseConnectionRequest(string Name, DatabaseProvider Provider, string ConnectionString, DatabaseEnvironment Environment, bool AllowAgentMutations, bool RequireApprovalForMutations)`
  - `UpdateDatabaseConnectionRequest(string? Name, DatabaseEnvironment? Environment, string? ConnectionString, bool? AllowAgentMutations, bool? RequireApprovalForMutations)`
  - `DatabaseTestResponse(bool Ok, string Message, long LatencyMs)`
  - `DatabaseQueryRequest(string Sql, int MaxRows = 100)`
  - `DetectedConnectionDto(string SettingsKey, string Name, string MaskedConnectionString, DatabaseProvider? Provider, string? RejectionReason)`
  - `ImportRequest(string[] SettingsKeys)`
  - `ImportResultDto(string SettingsKey, string? ConnectionId, string? Name, bool Imported, string? Reason)`
- Produces (namespace `Eling.Backend.Database`):
  - `DatabaseConnectionImporter` — ctor `(AppSettingsConnectionScanner scanner, IDatabaseConnectionStore store, ILogger<DatabaseConnectionImporter>? logger = null)`; `Task<IReadOnlyList<ImportResultDto>> ImportAsync(IReadOnlyList<string> settingsKeys, CancellationToken ct = default)`. One `Scan()` per call, then per key (ordinal match): not found → skip `"not found in source"`; `Provider` null → skip with the scanner's `RejectionReason`; `DatabaseConnection.Create` exception → skip with the exception's own field-naming message (safe text, no connection string); derived name already in the registry (ordinal-ignore-case) → skip `"already exists"`; else `Create(name, provider, rawConnectionString, DatabaseEnvironment.ReadOnly, allowAgentMutations: false, requireApprovalForMutations: true)` + `UpsertAsync` → imported, result carries the new `ConnectionId` and the registered `Name` (REST-facing; the MCP layer maps it away — Task 8). Every step's exception is caught, logged, and turned into a skip row.
- Produces (namespace `Eling.Backend.Endpoints`):
  - `DatabaseEndpoints.MapDatabaseEndpoints(this WebApplication app)` — `app.MapGroup("/api/databases")` with the ten routes from spec §4. Semantics: `POST` → `201` + `Location`, `ArgumentException` → `400 ex.Message`; unknown id → `404`; `PUT` with blank/omitted `ConnectionString` keeps the stored value; `/test` → `200 { ok, message, latencyMs }` (probe failures are 200, never 5xx) and `MarkConnectedAsync` on success; `/query` → `SqlSafety` rejection → `400 verdict.Reason`, else `QueryAsync`, `MaxRows` clamped to `[1, 1000]`, adapter exception → `502`; `/detect` → `200` scanner results as `DetectedConnectionDto`s with masked strings; `/import` → `200` per-candidate `ImportResultDto`s, body is `ImportRequest { settingsKeys }` handled by the importer.

**Wiring:**
- `DashboardServices.Register`, after the `ProviderStore` block (`globalAgentDir` already in scope): add usings `Microsoft.AspNetCore.Hosting` and `Microsoft.Extensions.Hosting`, then:
```csharp
services.AddSingleton<IDatabaseConnectionStore>(sp => new JsonDatabaseConnectionStore(
    Path.Combine(globalAgentDir, "databases.json"),
    sp.GetRequiredService<ILogger<JsonDatabaseConnectionStore>>()));

services.AddSingleton(sp => new AppSettingsConnectionScanner(
    Environment.GetEnvironmentVariable("ELING_DATABASE_DETECT_SOURCE")
        ?? Path.Combine(sp.GetRequiredService<IHostEnvironment>().ContentRootPath, "appsettings.json"),
    sp.GetRequiredService<ILogger<AppSettingsConnectionScanner>>()));

services.AddSingleton<DatabaseConnectionImporter>();
```
- `DashboardRoutes.Map`: add `app.MapDatabaseEndpoints();` after `app.MapCodebaseRoutes();`.

- [ ] **Step 1: Write the failing tests**

`tests/Eling.Backend.Tests/DatabaseEndpointsTests.cs` — fixture pattern from `ToolsEndpointsTests`: `[Collection(ElingDataDirCollection.Name)]`, `IAsyncLifetime`, random loopback port via `TcpListener`, `TestAppBuilder.CreateSelfContained(dashboardPort: port)`, `HttpClient` with `BaseAddress` (this isolates `databases.json` under the temp `ELING_AGENT_DATA_DIR`). Helper creates one SQLite connection pointing at a temp `.db` file in the test's temp dir. Facts:
```csharp
Post_ThenGetList_RoundTrips_AndListResponseNeverContainsConnectionString
GetById_UnknownId_Returns404
Put_BlankConnectionString_KeepsStoredValue   // update name only; re-query still ok
Delete_ThenGet_Returns404
Test_ValidSqliteFile_ReturnsOkTrue           // POST /{id}/test → body ok true, latencyMs >= 0
Schema_ReturnsColumnsAndPk
Query_Select_ReturnsRows                      // create table first via direct SqliteConnection
Query_WithMutationSql_Returns400              // POST /{id}/query { sql = "DELETE FROM t" }
Query_WhenStoredProviderUnknown_Returns400    // hand-edit the store file to provider 99 via the store singleton… 
```
For the last one: resolve `IDatabaseConnectionStore` from the running app's services, upsert a row `with { Provider = (DatabaseProvider)99 }`, then GET `/api/databases/{id}/schema` → `400` (factory's `NotSupportedException` mapped to 400) and `GET /api/databases` still lists it masked.

`tests/Eling.Backend.Tests/DatabaseDetectImportTests.cs` (same fixture, plus `ELING_DATABASE_DETECT_SOURCE` set to a temp `appsettings.json` written in `InitializeAsync`, restored in `DisposeAsync`):
```csharp
Detect_ReturnsCandidates_AndRegistryStaysEmpty
Detect_NeverLeaksPassword                            // temp appsettings has Password=sup3r-secret; body must not contain it
Import_SubsetByKey_RegistersOnlyThose_AsReadOnly     // POST { settingsKeys = ["Orders"] }; list has orders@10.0.0.5, ReadOnly, no "eling-index@local"
Import_UnknownKey_ReturnsNotFoundInSource            // settingsKeys = ["Nope"] → imported false, reason "not found in source"
Import_DuplicateName_SkipsSecondWithReason           // import same key twice → second row reason "already exists"; list has 1 entry
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/Eling.Backend.Tests/Eling.Backend.Tests.csproj --artifacts-path .bin-test --filter "FullyQualifiedName~DatabaseEndpointsTests"`
Expected: FAIL (routes 404). Same for `DatabaseDetectImportTests`.

- [ ] **Step 3: Implement DTOs, importer, endpoints, and the two wiring edits**

Importer and endpoints per the Interfaces block. Handler errors: `ArgumentException` → `400`, `KeyNotFoundException`/store miss → `404`, provider/execution failure → `502`; every catch logs via injected `ILogger` (use `ILoggerFactory` + `CreateLogger("Database")` in static handlers, matching the Agent endpoints' approach).

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/Eling.Backend.Tests/Eling.Backend.Tests.csproj --artifacts-path .bin-test --filter "FullyQualifiedName~DatabaseEndpointsTests|FullyQualifiedName~DatabaseDetectImportTests"`
Expected: PASS. Then the full backend suite:

Run: `dotnet test tests/Eling.Backend.Tests/Eling.Backend.Tests.csproj --artifacts-path .bin-test`
Expected: PASS, no regressions.

- [ ] **Step 5: Report checkpoint**

Run: `git status --short`. Do not commit.

---

### Task 8: MCP read-only + detect/import tools (`Eling.Backend/Mcp/Tools/`)

**Files:**
- Create: `src/backend/Eling.Backend/Dtos/DatabaseMcpDtos.cs`
- Create: `src/backend/Eling.Backend/Mcp/Tools/DatabaseListConnectionsTool.cs`
- Create: `src/backend/Eling.Backend/Mcp/Tools/DatabaseGetSchemaTool.cs`
- Create: `src/backend/Eling.Backend/Mcp/Tools/DatabaseQueryTool.cs`
- Create: `src/backend/Eling.Backend/Mcp/Tools/DatabaseDetectConnectionsTool.cs`
- Create: `src/backend/Eling.Backend/Mcp/Tools/DatabaseImportConnectionsTool.cs`
- Modify: `src/backend/Eling.Backend/Mcp/ServerInstructions.cs` (`Sections` array — one entry)
- Test: `tests/Eling.Backend.Tests/DatabaseToolsTests.cs`
- Test: `tests/Eling.Backend.Tests/DatabaseDetectImportToolTests.cs`

**Interfaces:**
- Consumes: `IDatabaseConnectionStore`, `AppSettingsConnectionScanner`, `DatabaseConnectionImporter` (Tasks 3, 6, 7), `DatabaseAdapterFactory` (Tasks 4, 5), `SqlSafety` (Task 2).
- Produces (namespace `Eling.Backend.Dtos`):
  - `McpDetectedCandidate(int Index, string SettingsKey, string Name, DatabaseProvider? Provider, string? RejectionReason)` — the only MCP-specific DTO: `DetectedConnection` minus both connection-string fields, plus a 1-based `Index` for approval handles. Identity (`name` `db@host`, provider) stays; credentials never appear.
- Produces (all in `Eling.Backend.Mcp.Tools`, ctor pattern `(deps…, ILogger<TTool>? logger = null)` so tests can construct directly):
  - `DatabaseListConnectionsTool` — `[McpServerTool(Name = "db_list_connections")]`, returns `Task<IReadOnlyList<DatabaseConnectionView>>` — the REST view DTO holds no credential material (`hasConnectionString` bool only), so it is reused as-is.
  - `DatabaseGetSchemaTool` — `[McpServerTool(Name = "db_get_schema")]`, params `string connectionId, string? tableName = null`; returns `Task<DatabaseSchemaInfo>` filtered to `tableName` when given; unknown id → throws `KeyNotFoundException`.
  - `DatabaseQueryTool` — `[McpServerTool(Name = "db_query")]`, params `string connectionId, string sql, int maxRows = 100`; runs `SqlSafety.Classify` first and on rejection throws `InvalidOperationException` carrying the classifier's reason (MCP surfaces it as a tool error — the statement never executes); runs `QueryAsync` via the factory, and on provider failure throws `InvalidOperationException("query failed")` with the detail logged (never the raw provider exception text, which can embed host/user).
  - `DatabaseDetectConnectionsTool` — `[McpServerTool(Name = "db_detect_connections")]`, returns `Task<IReadOnlyList<McpDetectedCandidate>>` from the scanner — `Index` = position in scan order (1-based), plus `settingsKey`, `name` (`db@host`), `provider`. **No connection strings of any kind.** Pure preview; writes nothing.
  - `DatabaseImportConnectionsTool` — `[McpServerTool(Name = "db_import_connections")]`, param `string[] settingsKeys`, delegates to `DatabaseConnectionImporter.ImportAsync` and returns its `ImportResultDto`s unchanged (they carry `ConnectionId` + `Name` — identity, no credentials). **Tool description must state:** the user must explicitly approve which keys to import; detect first, ask, then import exactly the approved keys.
- `ServerInstructions.Sections` gains one entry naming the five tools, their read-only nature, and the confirm-before-import rule.

- [ ] **Step 1: Write the failing tests**

`tests/Eling.Backend.Tests/DatabaseToolsTests.cs` — construct tools directly against a temp `JsonDatabaseConnectionStore` and a temp SQLite file (no HTTP host needed):
```csharp
List_NeverContainsCredentials                       // store a MySQL conn "orders@10.0.0.5:3307" with
                                                    // Server=…;User ID=appuser;Password=…; serialize the
                                                    // tool result; assert it contains none of:
                                                    // "appuser", the password, "Password", "hasConnectionString"
                                                    // — host/name ("10.0.0.5", "db@host") is allowed identity
List_ExposesIdAndName                               // result row has Id + Name "orders@10.0.0.5:3307"
GetSchema_TempTable_ReturnsColumnsAndPk
Query_Select_ReturnsRows
Query_WithDelete_ThrowsAndExecutesNothing           // create table + 1 row; DELETE via tool throws
                                                    // InvalidOperationException; SELECT COUNT(*) still returns 1
Query_Failure_SanitizedMessage                      // query against a dropped/invalid object → throws with
                                                    // message "query failed"; no user@host/exception text
GetSchema_UnknownId_ThrowsKeyNotFound
```
`tests/Eling.Backend.Tests/DatabaseDetectImportToolTests.cs` (scanner pointed at a temp `appsettings.json` via the tool's scanner dependency):
```csharp
Detect_NeverContainsCredentials                     // sqlite + mysql candidates; serialized output contains
                                                    // neither "appuser" nor the password nor "Password=";
                                                    // rows expose Index + SettingsKey + Name (db@host) + Provider
Detect_ReturnsCandidates_StoreStaysEmpty
Import_SubsetByKey_OnlyApprovedKeysRegistered       // candidates A+B; import ["A"]; store has exactly A, ReadOnly
Import_NeverContainsCredentials                     // results expose ConnectionId + Name; no username,
                                                    // password, or connection string anywhere
Import_DuplicateName_SkippedWithReason
```
Plus one guard on the docs contract:
```csharp
[Fact] public void ServerInstructions_MentionDbTools()
    => Assert.Contains(Eling.Backend.Mcp.ServerInstructions.Sections, s => s.Contains("db_query"));
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/Eling.Backend.Tests/Eling.Backend.Tests.csproj --artifacts-path .bin-test --filter "FullyQualifiedName~DatabaseToolsTests|FullyQualifiedName~DatabaseDetectImportToolTests"`
Expected: build FAILURE — tool types do not exist.

- [ ] **Step 3: Implement the five tool files and the `ServerInstructions` entry**

One tool per file, `[McpServerTool]` + `[Description]` attributes on methods, `[Description]` on parameters (pattern: `CodebaseIndexTool`). Registration needs no DI edit — `WithToolsFromAssembly()` discovers them. Tool return types serialize through the global camelCase options.

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/Eling.Backend.Tests/Eling.Backend.Tests.csproj --artifacts-path .bin-test --filter "FullyQualifiedName~DatabaseToolsTests|FullyQualifiedName~DatabaseDetectImportToolTests"`
Expected: PASS. Then the full backend suite:

Run: `dotnet test tests/Eling.Backend.Tests/Eling.Backend.Tests.csproj --artifacts-path .bin-test`
Expected: PASS, no regressions.

- [ ] **Step 5: Report checkpoint**

Run: `git status --short`. Do not commit.

---

### Task 9: CHANGELOG, MCP smoke check, final report

**Files:**
- Modify: `CHANGELOG.md` (one `[Unreleased]` entry)

- [ ] **Step 1: Add the CHANGELOG entry**

Under `[Unreleased] / Added`: one paragraph — DB connection registry (`/api/databases` CRUD + test + schema + read-only query), local auto-detect + confirm-gated import from the backend's `appsettings.json` (masked preview, key-based import, SQLite + MySQL, `{db}@{host}` names), and MCP tools `db_list_connections`, `db_get_schema`, `db_query`, `db_detect_connections`, `db_import_connections` (mutations deferred to Phase 3). Note the `MySqlConnector` version added.

- [ ] **Step 2: Verify tool discovery end-to-end**

Run: `pwsh scripts/sim-mcp-stdio.ps1`
Expected: the five `db_*` tools appear in the tools list alongside the existing ones.

- [ ] **Step 3: Full per-project green run**

Run: `dotnet test tests/Eling.Core.Tests/Eling.Core.Tests.csproj --artifacts-path .bin-test`
Expected: PASS.
Run: `dotnet test tests/Eling.Backend.Tests/Eling.Backend.Tests.csproj --artifacts-path .bin-test`
Expected: PASS.

- [ ] **Step 4: Final report**

Run: `git status --short` and summarize: all changed files, the resolved `MySqlConnector` version, test counts per project, and the manual MySQL verification that remains (live server test). Do not commit.

---

## Self-Review

**1. Spec coverage:** §1 scope → Tasks 1–8. §2 tree → one task per file group (Tasks 1–8; `DatabaseConnectionImporter` added in Task 7 and reflected in the spec tree). §3 model/storage/masking → Tasks 1, 3, 7 (masking asserted in tests). §4 API surface + §4.1 detect/import flow + naming → Tasks 6, 7. §5 SqlSafety guard (one guard, two callers) → Task 2, consumed in Tasks 7 (REST 400) and 8 (MCP throw). §6 error rows (sanitized messages, 30s timeout, absolute SQLite path, MySQL defaults) → Tasks 4, 5, 7. §7 tests → per-task test files. §8 deferred items → no tasks by design. §9/§10 decisions → baked into Global Constraints and Interfaces.

**2. Step scan:** every step names exact files, exact test names with assertions, exact commands, and expected outcomes; implementation steps carry signatures and the two algorithms the signatures don't determine (SqlSafety stripping/scanning; scanner classification/naming). No TBD/TODO.

**3. Type consistency:** `DatabaseConnection` positional order (Task 1) is used unchanged in Tasks 3, 4, 5; `IDatabaseAdapter` members (Task 4) are what both adapters (4, 5) and the factory implement; `DetectedConnection` (Task 6) carries the raw string for backend import use plus `MaskedConnectionString` for REST payloads; `ImportRequest { settingsKeys }` (Task 7) is what the MCP import tool (Task 8) forwards to `DatabaseConnectionImporter.ImportAsync` unchanged, and `ImportResultDto` (with `ConnectionId`) is returned by both the REST import route and the MCP import tool; `DatabaseConnectionView` is reused by both REST and the MCP list tool (it holds no credentials); `McpDetectedCandidate` (Task 8) is the only MCP-only DTO; store method names (`ListAsync`/`FindAsync`/`UpsertAsync`/`RemoveAsync`/`MarkConnectedAsync`) are identical in Tasks 3, 7, 8.

**4. Review Focus:** all five pinned — multi-statement bypass (Task 2), credential leakage (Tasks 4, 7), corrupt/hand-edited store + unknown provider (Tasks 3, 7), import idempotency (Task 7), relative SQLite DataSource (Task 4).

**5. Proportion:** the plan fixes names, signatures, test contracts, and two algorithms; adapter bodies, endpoint bodies, and tool bodies are left to the implementer — consistent with how the repo's other plans are written.
