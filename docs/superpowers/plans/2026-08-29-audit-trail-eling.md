# Audit Trail for Eling (Plan)

> Tracker index: `01m1729ysxn8gn0jjtk9pp79zx` (Eling project scope).
> Locked spec: `docs/superpowers/specs/2026-08-29-audit-trail-spec.md`.

## Vision

Every significant action in Eling (memory mutations, dashboard lifecycle, runtime sweep) is recorded
immutably to an audit log that can be queried and manually audited. The goal is
governance, compliance, and forensic capability when needed.

## Audit Scope (what is tracked)

- **Memory mutations**: Save, Delete, Promote-to-global, Copy-to-project.
- **Dashboard lifecycle**: Start, Stop, Restart, Auto-shutdown.
- **Runtime registration**: Register, Unregister, Heartbeat-fail, Stale-sweep.
- **Cross-cutting**: Coordinator notify-change (broadcast event from MCP to dashboard).

## Audit Entry Fields (JSON Lines)

```json
{
  "timestamp": "2026-08-29T15:30:00+00:00",
  "actor": "mcp:eling_dev" | "mcp:eling" | "dashboard" | "system",
  "action": "memory_save" | "memory_delete" | "promote" | "copy" | "start" | "stop" | "sweep" | "notify",
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
2. **File Rotation**: Monthly rotation to keep file size manageable.
3. **Vestige Mirror**: Auto-duplicate every entry to Vestige for long-term durable audit.
4. **Memory Snapshot** (optional): On Delete, store the previous version as an `Archived` memory.

## UI

The dashboard shows a new **"Activity Log"** tab with:
- Filter by actor, action, scope, time range.
- Per-entry detail: click to see a before-after diff.
- Infinite scroll pagination + virtual list for performance.

## Compliance & Retention

- **Append-only**: log files are never overwritten, only appended.
- **TTL**: files are rotated monthly (January 2027 → `audit-2027-01.log.jsonl`).
- **Off-system Backup**: Mirror to Vestige as immutable durable storage.

## Tasks (to be split during execution)

- [ ] T1. Backend: Define the `AuditEvent` DTO and `IAuditLogger` interface.
- [ ] T2. Backend: Implement `JsonlAuditLogger` (file-backed append-only).
- [ ] T3. Backend: Hook `IAuditLogger` into `MemoryService.SaveAsync` / `UpdateAsync` / `DeleteAsync`.
- [ ] T4. Backend: Hook into `RuntimeRegistry.Register` / `Unregister` / `Sweep` / `Shutdown`.
- [ ] T5. Backend: Vestige mirror (best-effort, async fire-and-forget).
- [ ] T6. Backend: Monthly rotation helper.
- [ ] T7. Frontend: `ActivityLog` page with table + filter.
- [ ] T8. Frontend: Per-entry detail modal with diff.
- [ ] T9. Tests: Backend unit + integration tests for `IAuditLogger`.
- [ ] T10. Tests: Frontend E2E for the Activity Log tab.
- [ ] T11. Docs: Update `AGENTS.md` with audit trail rules.

## Non-Goals (out of scope)

- Real-time streaming of audit events to an external SIEM (batch mirror to Vestige is sufficient).
- Cryptographic chain-of-custody (optional Phase 3+).
- User authentication / RBAC (needs further discussion).

## Status

Draft (2026-08-29). Spec locked. Plan tasks not yet executed.
