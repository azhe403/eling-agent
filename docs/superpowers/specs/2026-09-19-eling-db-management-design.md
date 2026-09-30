# Design Specification: Eling Database Management & Agent Query Hub

**Date:** 2026-09-19  
**Status:** Proposed  
**Target Projects:** `Eling.Core`, `Eling.Backend`, `Eling.Mcp`, `Eling.Dashboard`, `Eling.Desktop`

---

## 1. Overview & Objectives

Eling Database Management memperluas kapabilitas Eling menjadi hub manajemen database multi-engine terpusat di backend host (`Eling.Backend`). Fitur ini menyediakan dua interface:
1. **Interactive GUI (Web Dashboard & Desktop Client)**: Menyediakan kemampuan penjelajahan skema (schema explorer), manajemen koneksi dengan preset environment (e.g. `development`, `staging`, `production`), query editor interaktif, dan kapabilitas **Full CRUD** (Create, Read, Update, Delete) sesuai preset environment koneksi.
2. **AI Agent / MCP Query Hub (`Eling.Mcp`)**: Memungkinkan AI Agent menjalankan query analitik (`SELECT`) secara instan dan aman, serta mewajibkan approval eksplisit untuk operasi mutasi/perubahan data (`INSERT`, `UPDATE`, `DELETE`, `DDL`).

---

## 2. Environment Presets & Permission Matrix

Saat koneksi database didaftarkan di backend melalui GUI, pengguna memilih **Environment Preset**:

| Environment Preset | Default GUI Access | Default Agent / MCP Access | Mutation Behavior (MCP) |
| :--- | :--- | :--- | :--- |
| **Development** (`dev`) | Full CRUD (Read + Write + DDL) | Read Allowed, Write with Approval / Auto-approve toggle | Prompt Approval / Configurable Auto-approve |
| **Staging** (`staging`) | Full CRUD (Read + Write) | Read Allowed, Write requires User Approval | Always Require Approval |
| **Production** (`prod`) | Restricted / Confirm on Write | Read Only (Writes strictly blocked or Dual-Key Approval) | Require Multi-step / GUI Approval |
| **Read-Only** (`readonly`) | Read Only (`SELECT` only) | Read Only (`SELECT` only) | Mutasi ditolak (Blocked) |

---

## 3. Architecture & Data Storage

### 3.1 Backend Host Storage
Konfigurasi koneksi disimpan di server host `Eling.Backend` pada path runtime:
`~/.eling/databases.json` (atau `.eling/databases.json` jika diatur per workspace project scope).

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
   - Form pendaftaran koneksi (Host, Port, DB Name, User, Password, atau Raw Connection String).
   - Selector **Environment Preset** (`Development`, `Staging`, `Production`, `Read-Only`).
   - Tombol **Test Connection**.
2. **Schema & Table Explorer**:
   - Sidebar pohon hirarki database: Tables, Views, Columns, Data Types, Constraints (PK/FK/Indexes).
3. **Data Viewer & Full CRUD Table View**:
   - Grid tabel interaktif untuk melihat isi data dengan sorting, filtering, pagination.
   - Fitur **Insert Row**, **Edit Inline**, dan **Delete Row** langsung dari GUI (aktif pada preset `Development`/`Staging`).
4. **Interactive SQL Editor**:
   - Editor SQL dengan syntax highlighting, eksekusi query, history query, dan panel hasil (JSON/Table Grid + Export).
5. **Agent Mutation Approval Panel**:
   - Real-time notification/toast dan queue list saat Agent meminta eksekusi query mutasi.
   - Tampilan detail: Nama Agent, Target Database, SQL Statement, Reason.
   - Tombol **Approve & Execute** atau **Reject**.

---

## 5. Agent & MCP Tools Specification (`Eling.Mcp`)

Agent berkomunikasi dengan database melalui 4 MCP Tools:

### 5.1 `db_list_connections`
* **Deskripsi**: Menampilkan daftar database yang terdaftar di Eling.
* **Output**: Array `{ id, name, provider, environment, isReadOnly }`.

### 5.2 `db_get_schema`
* **Deskripsi**: Mengambil struktur tabel dan metadata kolom.
* **Parameters**:
  - `connectionId` (string, required)
  - `tableName` (string, optional)
* **Output**: Metadata skema, tipe kolom, foreign key relations.

### 5.3 `db_query` (Selalu Diizinkan - Read Only)
* **Deskripsi**: Menjalankan query analitik/pembacaan data (`SELECT`, `EXPLAIN`, `PRAGMA`).
* **Parameters**:
  - `connectionId` (string, required)
  - `sql` (string, required)
  - `maxRows` (number, default: 100)
* **Guardrail**: Backend memvalidasi token SQL; jika terdeteksi mutasi (`INSERT`, `UPDATE`, `DELETE`, `DROP`), query dialihkan atau ditolak dengan petunjuk menggunakan `db_execute_mutation`.

### 5.4 `db_execute_mutation` (Memerlukan Persetujuan - Approval Gate)
* **Deskripsi**: Menjalankan query mutasi (`INSERT`, `UPDATE`, `DELETE`, `CREATE TABLE`, `ALTER`).
* **Parameters**:
  - `connectionId` (string, required)
  - `sql` (string, required)
  - `reason` (string, required)
* **Alur Eksekusi**:
  1. Backend membuat `PendingMutationAction` dengan ID unik.
  2. Backend memancarkan SSE event ke GUI Dashboard.
  3. MCP Tool mengembalikan status `PENDING_USER_APPROVAL` beserta action ID dan preview perubahan.
  4. Begitu disetujui di GUI atau melalui token konfirmasi, hasil eksekusi (rows affected) diselesaikan.

---

## 6. Security & Guardrails

1. **Credential Secrecy**: Connection string asli dan password tidak pernah dikembalikan ke Agent MCP maupun respons API publik.
2. **Transaction Isolation & Timeout**: Semua query query dibatasi execution timeout default (30s) untuk menghindari lock berkepanjangan.
3. **Audit Logging**: Setiap query yang dijalankan (baik via GUI maupun MCP) dicatat ke dalam audit trail Eling (`audit_trail`) lengkap dengan timestamp, user/agent initiator, dan affected row count.

---

## 7. Next Steps & Implementation Phasing

- **Phase 1 (Core & Backend)**: Implementasi model `DatabaseConnection`, ADO.NET provider adapters, database registry, dan CRUD API endpoints di `Eling.Backend`.
- **Phase 2 (MCP Tooling)**: Penambahan tools `db_list_connections`, `db_get_schema`, `db_query`, dan `db_execute_mutation` di `Eling.Mcp`.
- **Phase 3 (Dashboard GUI)**: Implementasi UI `/dashboard/databases` di `Eling.Dashboard` (Next.js) dan Desktop (Avalonia) mencakup Connection Manager, Schema Explorer, Query Runner, Data Grid CRUD, dan Approval Banner.
