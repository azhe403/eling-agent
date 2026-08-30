---
id: 01m1729ysxn8gn0jjtk9pp79zx
type: decision
status: active
tags:
- audit_trail
- compliance
- future_plan
- activity_log
- governance
created_at: 2026-08-29T15:30:41.5977816+00:00
updated_at: 2026-08-29T15:30:41.5977816+00:00
source:
---
[Tracker] Audit Trail untuk semua operasi Eling. Semua aksi (memory mutations, dashboard lifecycle, runtime sweep) akan dicatat ke .eling/audit/audit.log.jsonl (append-only JSONL) + auto-mirror ke Vestige untuk long-term immutable storage. UI: tab baru Activity Log di dashboard dengan filter by actor, action, scope, time range. Compliance-ready: append-only, monthly rotation, off-system backup. Lihat plan: docs/superpowers/plans/2026-08-29-audit-trail-eling.md (akan dibuat).