# Eling Database Management & Agent Query Hub Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Implement Eling Database Management allowing Eling Backend to store database connection configurations, provide full CRUD GUI in Dashboard/Desktop per environment preset, and expose MCP tools for Agent read queries and approval-gated mutations.

**Architecture:** A domain model & provider adapter abstraction in `Eling.Core`, SQLite/JSON persistent storage for database connections and pending mutation approvals in `Eling.Backend`, 4 new MCP tools in `Eling.Mcp`, and a complete `/dashboard/databases` web UI in `Eling.Dashboard`.

**Tech Stack:** .NET 10, C# 13/14, ADO.NET (`Microsoft.Data.Sqlite`, `Npgsql`, `MySqlConnector`, `Microsoft.Data.SqlClient`), Next.js 15 / React 19, TypeScript, Tailwind CSS, Lucide React.

## Global Constraints
- Target Frameworks: `net10.0` across all C# projects.
- Credentials and connection strings must be stored securely at rest on the host backend and never leaked to MCP tool outputs or public client payloads.
- MCP queries default to Read-Only (`SELECT`, `EXPLAIN`, `PRAGMA`), while mutations (`INSERT`, `UPDATE`, `DELETE`, `DDL`) require approval gate.
- GUI allows full CRUD matching the database's environment preset (`Development`, `Staging`, `Production`, `Read-Only`).

---

### Task 1: Domain Models & ADO.NET Database Adapter Interface in `Eling.Core`

**Files:**
- Create: `src/backend/Eling.Core/Database/DatabaseConnection.cs`
- Create: `src/backend/Eling.Core/Database/IDatabaseAdapter.cs`
- Create: `src/backend/Eling.Core/Database/QueryResult.cs`
- Create: `src/backend/Eling.Core/Database/DatabaseSchemaInfo.cs`
- Test: `tests/Eling.Core.Tests/DatabaseConnectionModelTests.cs`

**Interfaces:**
- Produces: `DatabaseConnection`, `IDatabaseAdapter`, `QueryResult`, `DatabaseSchemaInfo`, `TableSchemaInfo`, `ColumnSchemaInfo`

- [ ] **Step 1: Write failing unit test for DatabaseConnection and QueryResult validation**
- [ ] **Step 2: Run test and verify it fails**
- [ ] **Step 3: Implement DatabaseConnection domain model and interfaces**
- [ ] **Step 4: Run test and verify it passes**
- [ ] **Step 5: Commit changes**

---

### Task 2: Provider Adapters Implementation in `Eling.Core` (SQLite, PostgreSQL, MySQL, SQL Server)

**Files:**
- Create: `src/backend/Eling.Core/Database/Adapters/SqliteDatabaseAdapter.cs`
- Create: `src/backend/Eling.Core/Database/Adapters/PostgresDatabaseAdapter.cs`
- Create: `src/backend/Eling.Core/Database/Adapters/MySqlDatabaseAdapter.cs`
- Create: `src/backend/Eling.Core/Database/Adapters/SqlServerDatabaseAdapter.cs`
- Create: `src/backend/Eling.Core/Database/DatabaseAdapterFactory.cs`
- Test: `tests/Eling.Core.Tests/SqliteDatabaseAdapterTests.cs`

**Interfaces:**
- Consumes: `IDatabaseAdapter`, `DatabaseConnection`
- Produces: `DatabaseAdapterFactory.CreateAdapter(DatabaseConnection connection)`

- [ ] **Step 1: Write failing test for SqliteDatabaseAdapter (testing query execution & schema extraction)**
- [ ] **Step 2: Run test and verify it fails**
- [ ] **Step 3: Implement DatabaseAdapterFactory and SqliteDatabaseAdapter**
- [ ] **Step 4: Implement Postgres, MySql, and SqlServer adapters**
- [ ] **Step 5: Run tests and verify they pass**
- [ ] **Step 6: Commit changes**

---

### Task 3: Database Connection Store & Backend Endpoints in `Eling.Backend`

**Files:**
- Create: `src/backend/Eling.Backend/Database/IDatabaseConnectionStore.cs`
- Create: `src/backend/Eling.Backend/Database/JsonDatabaseConnectionStore.cs`
- Create: `src/backend/Eling.Backend/Database/PendingMutationStore.cs`
- Create: `src/backend/Eling.Backend/DatabaseEndpoints.cs`
- Modify: `src/backend/Eling.Backend/Program.cs`
- Test: `tests/Eling.Application.Tests/DatabaseEndpointsTests.cs`

**Interfaces:**
- Consumes: `DatabaseConnection`, `DatabaseAdapterFactory`
- Produces: REST Endpoints `/api/databases` (GET, POST, PUT, DELETE), `/api/databases/{id}/test`, `/api/databases/{id}/schema`, `/api/databases/{id}/query`, `/api/databases/{id}/mutate`, `/api/databases/mutations/pending`

- [ ] **Step 1: Write integration test for Database Endpoints (Add connection, Test, Schema, Query)**
- [ ] **Step 2: Run test and verify it fails**
- [ ] **Step 3: Implement JsonDatabaseConnectionStore and PendingMutationStore**
- [ ] **Step 4: Implement DatabaseEndpoints & map routes in Program.cs**
- [ ] **Step 5: Run tests and verify they pass**
- [ ] **Step 6: Commit changes**

---

### Task 4: MCP Database Tools in `Eling.Mcp`

**Files:**
- Create: `src/backend/Eling.Mcp/Tools/DatabaseTools.cs`
- Modify: `src/backend/Eling.Mcp/McpServerBuilder.cs`
- Test: `tests/Eling.Mcp.Tests/DatabaseToolsTests.cs`

**Interfaces:**
- Consumes: `IDatabaseConnectionStore`, `DatabaseAdapterFactory`, `PendingMutationStore`
- Produces: MCP Tools `db_list_connections`, `db_get_schema`, `db_query`, `db_execute_mutation`

- [ ] **Step 1: Write failing test for db_query read-only safety guard and db_execute_mutation approval flow**
- [ ] **Step 2: Run test and verify it fails**
- [ ] **Step 3: Implement DatabaseTools with SQL safety classification & pending action creation**
- [ ] **Step 4: Register DatabaseTools into MCP tool definitions**
- [ ] **Step 5: Run tests and verify they pass**
- [ ] **Step 6: Commit changes**

---

### Task 5: Database Management UI in `Eling.Dashboard`

**Files:**
- Create: `src/frontend/Eling.Dashboard/src/app/dashboard/databases/page.tsx`
- Create: `src/frontend/Eling.Dashboard/src/components/database/ConnectionList.tsx`
- Create: `src/frontend/Eling.Dashboard/src/components/database/ConnectionModal.tsx`
- Create: `src/frontend/Eling.Dashboard/src/components/database/SchemaTree.tsx`
- Create: `src/frontend/Eling.Dashboard/src/components/database/SqlEditor.tsx`
- Create: `src/frontend/Eling.Dashboard/src/components/database/DataTableCrud.tsx`
- Create: `src/frontend/Eling.Dashboard/src/components/database/PendingApprovalsBanner.tsx`
- Modify: `src/frontend/Eling.Dashboard/src/components/app-sidebar.tsx`

**Interfaces:**
- Consumes: Backend `/api/databases/*` REST & SSE endpoints

- [ ] **Step 1: Add Database menu item to `app-sidebar.tsx`**
- [ ] **Step 2: Build Connection Manager & Connection Modal with Environment Preset selector**
- [ ] **Step 3: Build Schema Tree Explorer and Table Grid Data Viewer with Full CRUD actions**
- [ ] **Step 4: Build Interactive SQL Editor with query results table**
- [ ] **Step 5: Build Pending Approvals Banner/Modal for Agent MCP mutation requests**
- [ ] **Step 6: Verify frontend build (`pnpm build` / `npm run build`)**
- [ ] **Step 7: Commit changes**
