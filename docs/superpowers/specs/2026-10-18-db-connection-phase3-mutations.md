# DB Connection Management — Phase 3: Dashboard Mutations (Design Specification)

- **Date:** 2026-10-18
- **Status:** Proposed (awaiting user review)
- **Target Projects:** `Eling.Core`, `Eling.Backend`, `Eling.Dashboard`
- **Supersedes/Extends:** `docs/superpowers/specs/2026-10-13-db-connection-design.md` §8 (deferred mutations) — this spec scopes Phase 3 to **Dashboard-only** mutations. MCP mutation tool (`db_execute_mutation`) and the approval gate (`PendingMutationAction` + SSE + approval panel) remain **deferred** (see §7).

---

## 1. Overview & Scope

Phase 1 shipped a read-only registry (SQLite + MySQL) with REST + MCP read tools. Phase 2 shipped the Dashboard GUI (connection manager, schema tree, data viewer, SQL editor). Phase 3 adds **data mutation** from the Dashboard: edit cell, insert row, delete row — executed against the target database, guarded by `SqlSafety`, enforced by environment preset, and recorded in the Audit Trail.

**In scope (this phase):**
- `SqlSafety` extension: classify a statement as read-only vs mutation; expose the mutation kind.
- `POST /api/databases/{id}/mutate` endpoint: accepts a SQL statement (or a structured mutation descriptor), classifies it, enforces preset, executes, audits.
- Environment-preset enforcement: `ReadOnly` connections reject all mutations (403); other presets allow mutations.
- Audit logging: every executed mutation recorded via `IAuditLogger` (change trail), including failed attempts.
- Dashboard: CRUD data grid on the Data tab — inline cell edit, add row, delete row — wired to the mutate endpoint, with optimistic UI + error rollback.

**Out of scope (deferred):**
- MCP mutation tool `db_execute_mutation` (deferred — no agent-facing mutation in this phase).
- Approval gate (`PendingMutationAction` + SSE + approval panel) — deferred together with MCP mutation.
- DDL helpers / schema migrations.
- Batch/transactional multi-statement mutations (single statement per request in v1).
- Row-level permission / RBAC.

---

## 2. Mutation Classification (`SqlSafety`)

Extend `SqlSafety` (in `Eling.Core`) with a classification result:

```csharp
public enum SqlStatementKind { ReadOnly, Mutation, Unknown }

public sealed record SqlClassification(
    SqlStatementKind Kind,
    string? MutationVerb,   // "INSERT" | "UPDATE" | "DELETE" | "CREATE" | "ALTER" | "DROP" | "TRUNCATE" | null
    string? RejectionReason);
```

- Reuse the existing normalization (strip comments, mask string literals).
- First keyword determines the class:
  - Allowlist (read-only): `SELECT`, `EXPLAIN`, `PRAGMA` → `ReadOnly`.
  - Mutation verbs: `INSERT`, `UPDATE`, `DELETE`, `CREATE`, `ALTER`, `DROP`, `TRUNCATE`, `REPLACE` → `Mutation`.
  - Anything else → `Unknown` (rejected).
- Secondary scan (same as v1) rejects mixed statements: a leading `SELECT` followed by a mutation keyword outside a literal → rejected (prevents `SELECT 1; DROP TABLE t`).
- `SqlSafety.Classify(sql)` returns `SqlClassification`; the existing `Classify` (bool verdict) becomes a thin wrapper for backward compat.

---

## 3. Mutate Endpoint

`POST /api/databases/{id}/mutate`

Request body:
```json
{ "sql": "UPDATE orders SET status='shipped' WHERE id=42", "maxAffectedRows": 1000 }
```

Response:
- `200 { affectedRows, truncated }` on success.
- `400` if classification is not `Mutation` (read-only statements go through `/query`, not `/mutate`) or rejected by secondary scan.
- `403` if the connection's environment is `ReadOnly` (or `AllowAgentMutations == false` for agent callers — N/A this phase, but the check stays).
- `404` unknown connection.
- `502` provider error (sanitized message).

Behavior:
1. Load connection; 404 if missing.
2. Enforce environment: if `Environment == ReadOnly` → 403 `{ error: "mutations not allowed on read-only connection" }`.
3. Classify SQL; if not `Mutation` → 400 with reason.
4. Execute via adapter (new `ExecuteAsync`/`MutateAsync` method on `IDatabaseAdapter` returning affected rows).
5. Bounded by `CancellationToken` (30s default), `maxAffectedRows` cap (default 1000, hard cap 10000).
6. Audit log the attempt (success or failure) via `IAuditLogger`.
7. Return `{ affectedRows, truncated }`.

**Adapter change:** add `Task<int> ExecuteAsync(string sql, CancellationToken ct)` to `IDatabaseAdapter` and both adapters (SQLite: `ExecuteNonQueryAsync`; MySQL: `ExecuteNonQueryAsync`). Read-only guard is enforced at the endpoint, not the adapter.

---

## 4. Audit Logging

Hook `IAuditLogger` into the mutate endpoint:

- On attempt: record a change-trail entry with:
  - `actor`: `"dashboard"` (or the authenticated user id when auth lands)
  - `action`: `"db.mutate"`
  - `target`: `{ connectionId, database }`
  - `metadata`: `{ verb, affectedRows, truncated, durationMs }`
  - `outcome`: `"success"` or `"error"` (with sanitized reason)
- Failed classification (400) and forbidden (403) are also recorded as `denied`/`error` outcomes for traceability.
- Fire-and-forget for the access trail is acceptable; change trail uses the existing durability confirmation.

The audit entry never includes the raw connection string or credentials.

---

## 5. Environment-Preset Enforcement

- `ReadOnly` → all mutations rejected (403). Reads unaffected.
- `Development` / `Staging` / `Production` → mutations allowed (no additional gate in this phase; preset-based restrictions deferred to a later phase).
- The check lives in the mutate endpoint (and a future MCP tool), not in the adapter.

---

## 6. Dashboard: CRUD Data Grid

On the Data tab, when a table is selected, replace the read-only table with an **editable grid**:

- **Inline cell edit:** click a cell → input → Enter/blur saves (issues `UPDATE ... SET col='val' WHERE pk=...`).
- **Delete row:** row action button → confirm → `DELETE FROM ... WHERE pk=...`.
- **Add row:** "Add row" button → empty row → fill → `INSERT INTO ... (...) VALUES (...)`.
- **Primary key detection:** use schema PK info to build WHERE clauses; if no PK, disable edit/delete with a tooltip ("no primary key — read only").
- **Optimistic UI:** apply change locally, revert on error, show toast.
- **Read-only fallback:** if mutation fails (403/400), show error banner (auto-clear on next action, per prior fix).

SQL generation is client-side from schema + edited values; values are parameterized via the mutate endpoint (endpoint accepts raw SQL in v1, but the client builds parameterized-style statements with quoted literals — a follow-up will move to parameterized queries).

---

## 7. Deferred Items

| Deferred item | Planned phase |
|---|---|
| MCP mutation tool `db_execute_mutation` | Later (with approval gate) |
| Approval gate (`PendingMutationAction` + SSE + panel) | Later (with MCP mutation) |
| Environment-preset fine-grained restrictions | Later |
| Parameterized mutation queries (bind values, not string concat) | Follow-up hardening |
| Batch/transactional mutations | Follow-up |
| Audit logging of reads (access trail) | Follow-up |

---

## 8. Testing Strategy

- `SqlSafetyClassifierTests`: add mutation classification cases (INSERT/UPDATE/DELETE/CREATE/ALTER/DROP/TRUNCATE allowed as Mutation; mixed statement rejected; unknown verb rejected).
- `DatabaseEndpointsTests`: mutate happy path (UPDATE on temp SQLite → affectedRows=1); read-only SQL → 400; ReadOnly connection → 403; unknown id → 404; audit entry recorded.
- Adapter tests: `ExecuteAsync` on temp SQLite.
- Dashboard: manual/E2E for edit cell → mutate → refresh.

---

## 9. Decision Log

| Decision | Choice | Rationale |
|---|---|---|
| Mutation execution model | Direct execute (no approval gate) | MCP mutation deferred; approval gate deferred with it; keeps Phase 3 small |
| Approval gate | Deferred | Depends on MCP mutation + SSE; not needed for Dashboard-only edits |
| SQL guard | Reuse `SqlSafety`, classify as Mutation | One classifier, two callers (future MCP reuses) |
| Environment enforcement | `ReadOnly` → 403 | Presets already stored; enforcement is the new bit |
| Audit | Hook `IAuditLogger` in mutate endpoint | Core engine exists; just wire it |
| PK handling | Disable edits when no PK | Avoid unsafe full-table updates |
| Parameterization | Client builds SQL literals in v1 | Simpler; hardening follow-up |

---

## 10. Open Follow-Ups

- Move to parameterized mutations (bind values) to prevent injection from crafted cell values.
- Re-introduce approval gate when MCP mutation ships.
- Add row-level / column-level permissions per environment preset.
- Undo/redo on the Dashboard grid.
