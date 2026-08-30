# Audit Trail Specification (Locked)

> Tracker: `01m1729ysxn8gn0jjtk9pp79zx` (Eling project scope).
> Plan: `docs/superpowers/plans/2026-08-29-audit-trail-eling.md`.

## 1. Purpose

Mendefinisikan kontrak canonical untuk **Audit Trail** di Eling: mekanisme pencatatan
immutable untuk setiap aksi mutasi memori, lifecycle dashboard, dan runtime sweep
sehingga governance, compliance, dan forensik dapat dilakukan dengan mudah.

## 2. Scope

### 2.1 In-Scope (WAJIB diaudit)

- **Memory mutations**: Save, Update, Delete, Promote-to-global, Copy-to-project.
- **Dashboard lifecycle**: Start, Stop, Restart, Auto-shutdown.
- **Runtime registration**: Register, Unregister, Heartbeat-fail, Stale-sweep.
- **Cross-cutting**: Coordinator `notify-change` (broadcast event dari MCP ke dashboard).

### 2.2 Out-of-Scope

- Real-time streaming audit ke external SIEM (cukup batch mirror ke Vestige).
- Cryptographic chain-of-custody (Fase 3+).
- User authentication / RBAC (perlu diskusi lanjut).

## 3. Audit Entry Schema

Setiap audit entry adalah **satu baris JSON** (JSONL) dengan field berikut:

```json
{
  "timestamp": "2026-08-29T15:30:00+00:00",
  "actor": "mcp:eling_dev" | "mcp:eling" | "dashboard" | "system",
  "action": "memory_save" | "memory_update" | "memory_delete" | "promote" | "copy" | "start" | "stop" | "sweep" | "notify",
  "scope": "project" | "global" | "all",
  "memoryId": "01m..." | null,
  "previousContent": "..." | null,
  "newContent": "..." | null,
  "tags": ["...", "..."],
  "source": "user_chat_session_2026-08-29"
}
```

### Field Constraint

| Field | Tipe | Keterangan |
|---|---|---|
| `timestamp` | ISO-8601 string (UTC) | Wajib. Otomatis diisi saat entry dicatat. |
| `actor` | enum string | Wajib. Identitas pelaku aksi. |
| `action` | enum string | Wajib. Jenis aksi. |
| `scope` | enum string | Wajib. Scope memory yang terkait. |
| `memoryId` | ULID string \| null | Opsional. `null` untuk aksi yang tidak terkait memory. |
| `previousContent` | string \| null | Wajib untuk `update` / `delete`. `null` untuk `save`. |
| `newContent` | string \| null | Wajib untuk `save` / `update`. `null` untuk `delete`. |
| `tags` | string array | Wajib. Tag yang terkait aksi. |
| `source` | string | Wajib. Source context (user_chat_session, mcp_stdio, dashboard_web, dll). |

## 4. Storage Architecture

### 4.1 Primary Storage (Internal)

- **Path**: `<dataDir>/audit/audit.log.jsonl`
- **Format**: JSONL (satu entry per baris).
- **Access mode**: Append-only. File tidak boleh di-overwrite, truncate, atau dihapus saat runtime.
- **Rotation**: Monthly rotation → `audit-2027-01.log.jsonl`, dst.
- **Locking**: Append operation atomic via file lock OS untuk mencegah corruption.

### 4.2 Secondary Storage (Vestige Mirror)

- **Backend**: Vestige MCP `smart_ingest` dengan `node_type: "audit_log"`.
- **Cadence**: Best-effort, async fire-and-forget (tidak boleh memblokir primary operation).
- **Retention**: Immutable. Tidak ada auto-delete di Vestige.

### 4.3 Memory Snapshot (Opsional)

- Untuk `Delete` memory, simpan versi sebelumnya sebagai memory baru berstatus `Archived`.
- Ini di luar JSONL log; berupa entry di `.eling/memories/`.

## 5. API Contract

### 5.1 Internal Interface

```csharp
public interface IAuditLogger
{
    Task LogAsync(AuditEvent entry, CancellationToken cancellationToken = default);
}
```

Implementasi:
- `JsonlAuditLogger`: Primary file-backed.
- `VestigeAuditMirror`: Secondary best-effort async.

### 5.2 Dashboard API

- `GET /api/audit/events?actor=...&action=...&scope=...&from=...&to=...&limit=...`
- Response: `{ "events": [...], "total": N, "nextCursor": "..." }`
- Read-only; no write endpoint from dashboard (write only via internal hooks).

## 6. UI Requirements

### 6.1 Activity Log Tab di Dashboard

- **Path**: `/dashboard/activity-log/`
- **Table columns**: Timestamp, Actor, Action, Scope, MemoryId (short), Tags.
- **Filter bar**: Actor, Action, Scope, Time range.
- **Per-entry detail**: Klik untuk modal dengan diff before-after (untuk update/delete).
- **Performance**: Pagination + virtual list untuk dataset besar.

## 7. Retention & Compliance

- **TTL per file**: Monthly rotation, file lama diarsipkan (tidak dihapus).
- **Audit log tampering detection**: Hash chain opsional (Fase 3+).
- **Compliance scope**: Internal governance; belum ada external compliance standard yang harus dipenuhi.

## 8. Acceptance Criteria

Spesifikasi dianggap terpenuhi ketika:

1. Semua mutation memory hooks menulis entry audit ke JSONL.
2. Dashboard dapat membaca dan menampilkan audit log.
3. Vestige mirror best-effort aktif.
4. Monthly rotation berjalan otomatis.
5. Test unit + integration mencakup minimal 90% path audit.

## 9. Status

LOCKED (2026-08-29). Tidak boleh diedit sembarangan; gunakan plan untuk update progress.
