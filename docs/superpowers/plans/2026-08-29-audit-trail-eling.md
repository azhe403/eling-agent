# Audit Trail untuk Eling (Plan)

> Tracker index: `01m1729ysxn8gn0jjtk9pp79zx` (Eling project scope).
> Locked spec: `docs/superpowers/specs/2026-08-29-audit-trail-spec.md`.

## Visi

Setiap aksi penting di Eling (memory mutations, dashboard lifecycle, runtime sweep) dicatat
secara immutable ke audit log yang dapat di-query dan diaudit manual. Tujuannya adalah
governance, compliance, dan kemampuan forensik saat dibutuhkan.

## Scope Audit (apa saja yang di-track)

- **Memory mutations**: Save, Update, Delete, Promote-to-global, Copy-to-project.
- **Dashboard lifecycle**: Start, Stop, Restart, Auto-shutdown.
- **Runtime registration**: Register, Unregister, Heartbeat-fail, Stale-sweep.
- **Cross-cutting**: Coordinator notify-change (broadcast event dari MCP ke dashboard).

## Field Audit Entry (JSON Lines)

```json
{
  "timestamp": "2026-08-29T15:30:00+00:00",
  "actor": "mcp:eling_dev" | "mcp:eling" | "dashboard" | "system",
  "action": "memory_save" | "memory_update" | "memory_delete" | "promote" | "copy" | "start" | "stop" | "sweep" | "notify",
  "scope": "project" | "global",
  "memoryId": "01m..." | null,
  "previousContent": "..." | null,
  "newContent": "..." | null,
  "tags": ["...", "..."],
  "source": "user_chat_session_2026-08-29"
}
```

## Storage Strategy

1. **Internal Append-Only Log**: `.eling/audit/audit.log.jsonl` (JSONL, human-readable).
2. **File Rotation**: Monthly rotation agar ukuran file tetap manageable.
3. **Vestige Mirror**: Auto-duplicate setiap entry ke Vestige untuk long-term durable audit.
4. **Memory Snapshot** (opsional): On Delete, simpan versi sebelumnya sebagai `Archived` memory.

## UI

Dashboard menampilkan tab baru **"Activity Log"** dengan:
- Filter by actor, action, scope, time range.
- Per-entry detail: klik untuk melihat diff before-after.
- Pagination infinite scroll + virtual list untuk performa.

## Compliance & Retention

- **Append-only**: file log tidak pernah di-overwrite, hanya appended.
- **TTL**: file di-rotate per bulan (Januari 2027 → `audit-2027-01.log.jsonl`).
- **Off-system Backup**: Mirror ke Vestige sebagai immutable durable storage.

## Tasks (akan dipecah saat eksekusi)

- [ ] T1. Backend: Definisikan `AuditEvent` DTO dan `IAuditLogger` interface.
- [ ] T2. Backend: Implement `JsonlAuditLogger` (file-backed append-only).
- [ ] T3. Backend: Hook `IAuditLogger` ke `MemoryService.SaveAsync` / `UpdateAsync` / `DeleteAsync`.
- [ ] T4. Backend: Hook ke `RuntimeRegistry.Register` / `Unregister` / `Sweep` / `Shutdown`.
- [ ] T5. Backend: Vestige mirror (best-effort, async fire-and-forget).
- [ ] T6. Backend: Monthly rotation helper.
- [ ] T7. Frontend: `ActivityLog` page dengan table + filter.
- [ ] T8. Frontend: Per-entry detail modal dengan diff.
- [ ] T9. Tests: Backend unit + integration test untuk `IAuditLogger`.
- [ ] T10. Tests: Frontend E2E untuk tab Activity Log.
- [ ] T11. Docs: Update `AGENTS.md` dengan aturan audit trail.

## Non-Goals (di luar scope)

- Real-time streaming audit ke external SIEM (cukup batch mirror ke Vestige).
- Cryptographic chain-of-custody (opsional Fase 3+).
- User authentication / RBAC (perlu diskusi lebih lanjut).

## Status

Draft (2026-08-29). Spec locked. Plan tasks belum dieksekusi.
