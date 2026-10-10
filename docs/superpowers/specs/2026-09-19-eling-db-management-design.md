# Design Specification: Eling Database Management & Agent Query Hub

**Date:** 2026-09-19  
**Status:** Proposed  
**Target Projects:** `Eling.Core`, `Eling.Backend`, `Eling.Mcp`, `Eling.Dashboard`, `Eling.Desktop`

---

## 1. Overview & Objectives

Eling Database Management expands Eling's capabilities into a centralized multi-engine database management hub hosted within the backend service (`Eling.Backend`). This feature exposes two primary interfaces:
1. **Interactive GUI (Web Dashboard & Desktop Client)**: Provides schema exploration (tables, views, columns, foreign keys), connection management with environment presets (e.g. `development`, `staging`, `production`, `readonly`), an interactive query editor, and **Full CRUD** (Create, Read, Update, Delete) data operations governed by environment presets.
2. **AI Agent / MCP Query Hub (`Eling.Mcp`)**: Enables AI coding agents to execute analytical queries (`SELECT`) safely and instantly, while enforcing explicit human approval gates for data mutation operations (`INSERT`, `UPDATE`, `DELETE`, `DDL`).

---

## 2. Environment Presets & Permission Matrix

When database connections are registered through the GUI, users assign an **Environment Preset**:

| Environment Preset | Default GUI Access | Default Agent / MCP Access | Mutation Behavior (MCP) |
| :--- | :--- | :--- | :--- |
| **Development** (`dev`) | Full CRUD (Read + Write + DDL) | Read Allowed, Write with Approval / Auto-approve toggle | Prompt Approval / Configurable Auto-approve |
| **Staging** (`staging`) | Full CRUD (Read + Write) | Read Allowed, Write requires User Approval | Always Require Approval |
| **Production** (`prod`) | Restricted / Confirm on Write | Read Only (Writes strictly blocked or Dual-Key Approval) | Require Multi-step / GUI Approval |
| **Read-Only** (`readonly`) | Read Only (`SELECT` only) | Read Only (`SELECT` only) | Mutations Blocked |

---

## 3. Architecture & Data Storage

### 3.1 Backend Host Storage
Connection configurations are stored securely on the `Eling.Backend` host at the runtime path:
`~/.eling/databases.json` (or `.eling/databases.json` when configured for workspace-scoped project connections).

**Model Schema (`DatabaseConnection`):**
```csharp
public class DatabaseConnection
{
    public string Id { get; set; } = string.Empty; // ULID
    public string Name { get; set; } = string.Empty;
    public string Provider { get; set; } = "postgresql"; // sqlite, postgresql, mysql, sqlserver
    public string ConnectionString { get; set; } = string.Empty; // Encrypted or masked at rest
    public string Environment { get; set; } = "development"; // dev, staging, prod, readonly
    public bool AllowAgentMutations { get; set; } = true;
    public bool RequireApprovalForMutations { get; set; } = true;
    public DateTime CreatedAt { get; set; }
    public DateTime? LastConnectedAt { get; set; }
}
```

### 3.2 Supported ADO.NET Providers
* **SQLite**: `Microsoft.Data.Sqlite`
* **PostgreSQL**: `Npgsql`
* **MySQL / MariaDB**: `MySqlConnector`
* **SQL Server**: `Microsoft.Data.SqlClient`

---

## 4. GUI & Frontend Capabilities (`Eling.Dashboard` / `Eling.Desktop`)

1. **Connection Manager (`/dashboard/databases`)**:
   - Connection registration form (Host, Port, Database Name, User, Password, or raw connection string).
   - **Environment Preset** selector (`Development`, `Staging`, `Production`, `Read-Only`).
   - **Test Connection** button with latency metrics.
2. **Schema & Table Explorer**:
   - Tree navigation sidebar: Tables, Views, Columns, Data Types, and Constraints (Primary Keys, Foreign Keys, Indexes).
3. **Data Viewer & Full CRUD Table View**:
   - Interactive data grid supporting sorting, filtering, and pagination.
   - **Insert Row**, **Inline Edit**, and **Delete Row** actions available directly in the GUI (enabled on `Development`/`Staging` presets).
4. **Interactive SQL Editor**:
   - SQL editor featuring syntax highlighting, query execution, history, and results panel (JSON view, table grid, and CSV export).
5. **Agent Mutation Approval Panel**:
   - Real-time toast notifications and pending approval queue when an AI Agent requests a data mutation query.
   - Detailed inspection: Requesting Agent Name, Target Database, SQL Statement, and Stated Rationale.
   - **Approve & Execute** or **Reject** action buttons.

---

## 5. Agent & MCP Tools Specification (`Eling.Mcp`)

AI Agents interact with registered databases through 4 specialized MCP tools:

### 5.1 `db_list_connections`
* **Description**: Lists all registered database connections available in Eling.
* **Output**: Array of `{ id, name, provider, environment, isReadOnly }`.

### 5.2 `db_get_schema`
* **Description**: Retrieves table structures and column metadata.
* **Parameters**:
  - `connectionId` (string, required)
  - `tableName` (string, optional)
* **Output**: Schema metadata, column types, primary keys, and foreign key relationships.

### 5.3 `db_query` (Always Allowed — Read Only)
* **Description**: Executes analytical and read-only queries (`SELECT`, `EXPLAIN`, `PRAGMA`).
* **Parameters**:
  - `connectionId` (string, required)
  - `sql` (string, required)
  - `maxRows` (number, default: 100)
* **Guardrail**: Backend tokenizes and validates SQL statements; if mutation statements (`INSERT`, `UPDATE`, `DELETE`, `DROP`, `ALTER`) are detected, the query is rejected with guidance to use `db_execute_mutation`.

### 5.4 `db_execute_mutation` (Enforces Human Approval Gate)
* **Description**: Submits a data or schema mutation query (`INSERT`, `UPDATE`, `DELETE`, `CREATE TABLE`, `ALTER`).
* **Parameters**:
  - `connectionId` (string, required)
  - `sql` (string, required)
  - `reason` (string, required)
* **Execution Flow**:
  1. Backend creates a `PendingMutationAction` assigned a unique ULID.
  2. Backend broadcasts an SSE event to the GUI Dashboard and Desktop Client.
  3. The MCP tool returns `PENDING_USER_APPROVAL` status accompanied by the action ID and SQL preview.
  4. Once approved in the GUI or via confirmation token, the backend executes the mutation and returns affected row counts.

---

## 6. Security & Guardrails

1. **Credential Secrecy**: Raw connection strings and plaintext passwords are never returned to MCP agents or unauthenticated API endpoints.
2. **Transaction Isolation & Timeout**: All queries run with bounded execution timeouts (default: 30s) to prevent lock starvation.
3. **Audit Logging**: Every executed query (via GUI or MCP) is recorded in Eling's audit trail (`audit_trail`) with timestamp, initiator identity, and affected row counts.

---

## 7. Next Steps & Implementation Phasing

- **Phase 1 (Core & Backend)**: Implement `DatabaseConnection` models, ADO.NET provider adapters, connection registry, and CRUD API endpoints in `Eling.Backend`.
- **Phase 2 (MCP Tooling)**: Introduce `db_list_connections`, `db_get_schema`, `db_query`, and `db_execute_mutation` MCP tools in `Eling.Mcp`.
- **Phase 3 (Dashboard & Desktop GUI)**: Build the `/dashboard/databases` interface in `Eling.Dashboard` (Next.js) and `Eling.Desktop` (Avalonia), featuring Connection Manager, Schema Explorer, Query Runner, Data Grid CRUD, and Mutation Approval Banners.
