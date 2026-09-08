# Audit Trail Specification (Locked)

> Tracker: `01m1729ysxn8gn0jjtk9pp79zx` (Eling project scope).
> Plan: `docs/superpowers/plans/2026-08-29-audit-trail-eling.md`.

## 1. Purpose

Defines the canonical contract for the **Audit Trail** in Eling: an immutable
recording mechanism for every memory mutation action, dashboard lifecycle event,
and runtime sweep, so that governance, compliance, and forensics can be performed
with ease.

## 2. Scope

### 2.1 In-Scope (MUST be audited)

- **Memory mutations**: Save, Update, Delete, Promote-to-global, Copy-to-project.
- **Dashboard lifecycle**: Start, Stop, Restart, Auto-shutdown.
- **Runtime registration**: Register, Unregister, Heartbeat-fail, Stale-sweep.
- **Cross-cutting**: Coordinator `notify-change` (broadcast event from MCP to dashboard).

### 2.2 Out-of-Scope

- Real-time streaming of audit events to an external SIEM (batch mirror to Vestige is sufficient).
- Cryptographic chain-of-custody (Phase 3+).
- User authentication / RBAC (needs further discussion).

## 3. Audit Entry Schema

Each audit entry is a **single JSON line** (JSONL) with the following fields:

```json
{
  "timestamp": "2026-08-29T15:30:00+00:00",
  "actor": "mcp:eling_dev" | "mcp:eling" | "dashboard" | "system",
  "action": "memory_save" | "memory_delete" | "promote" | "copy" | "start" | "stop" | "sweep" | "notify",
  "scope": "project" | "global" | "all",
  "memoryId": "01m..." | null,
  "previousContent": "..." | null,
  "newContent": "..." | null,
  "tags": ["...", "..."],
  "source": "user_chat_session_2026-08-29"
}
```

### Field Constraints

| Field | Type | Description |
|---|---|---|
| `timestamp` | ISO-8601 string (UTC) | Required. Auto-filled when the entry is recorded. |
| `actor` | enum string | Required. Identity of the action's performer. |
| `action` | enum string | Required. Type of action. |
| `scope` | enum string | Required. Related memory scope. |
| `memoryId` | ULID string \| null | Optional. `null` for actions not related to a memory. |
| `previousContent` | string \| null | Required for `delete`. `null` for `save`. |
| `newContent` | string \| null | Required for `save`. `null` for `delete`. |
| `tags` | string array | Required. Tags related to the action. |
| `source` | string | Required. Source context (user_chat_session, mcp_stdio, dashboard_web, etc.). |

## 4. Storage Architecture

### 4.1 Primary Storage (Internal)

- **Path**: `<dataDir>/audit/audit.log.jsonl`
- **Format**: JSONL (one entry per line).
- **Access mode**: Append-only. The file must not be overwritten, truncated, or deleted at runtime.
- **Rotation**: Monthly rotation → `audit-2027-01.log.jsonl`, etc.
- **Locking**: Append operation is atomic via OS file lock to prevent corruption.

### 4.2 Secondary Storage (Vestige Mirror)

- **Backend**: Vestige MCP `smart_ingest` with `node_type: "audit_log"`.
- **Cadence**: Best-effort, async fire-and-forget (must not block the primary operation).
- **Retention**: Immutable. No auto-delete in Vestige.

### 4.3 Memory Snapshot (Optional)

- For `Delete` actions, store the previous version as a new memory with `Archived` status.
- This lives outside the JSONL log; it is an entry in `.eling/memories/`.

## 5. API Contract

### 5.1 Internal Interface

```csharp
public interface IAuditLogger
{
    Task LogAsync(AuditEvent entry, CancellationToken cancellationToken = default);
}
```

Implementations:
- `JsonlAuditLogger`: Primary file-backed.
- `VestigeAuditMirror`: Secondary best-effort async.

### 5.2 Dashboard API

- `GET /api/audit/events?actor=...&action=...&scope=...&from=...&to=...&limit=...`
- Response: `{ "events": [...], "total": N, "nextCursor": "..." }`
- Read-only; no write endpoint from the dashboard (write only via internal hooks).

## 6. UI Requirements

### 6.1 Activity Log Tab in Dashboard

- **Path**: `/dashboard/activity-log/`
- **Table columns**: Timestamp, Actor, Action, Scope, MemoryId (short), Tags.
- **Filter bar**: Actor, Action, Scope, Time range.
- **Per-entry detail**: Click to open a modal with a before-after diff (for update/delete).
- **Performance**: Pagination + virtual list for large datasets.

## 7. Retention & Compliance

- **TTL per file**: Monthly rotation; old files are archived (not deleted).
- **Audit log tampering detection**: Optional hash chain (Phase 3+).
- **Compliance scope**: Internal governance; no external compliance standard must be met yet.

## 8. Acceptance Criteria

The specification is considered fulfilled when:

1. All memory mutation hooks write an audit entry to JSONL.
2. The dashboard can read and display the audit log.
3. The best-effort Vestige mirror is active.
4. Monthly rotation runs automatically.
5. Unit + integration tests cover at least 90% of the audit paths.

## 9. Status

LOCKED (2026-08-29). Must not be edited casually; use the plan to track progress updates.
