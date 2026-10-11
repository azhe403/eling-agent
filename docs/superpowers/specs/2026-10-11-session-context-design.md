# Session Context Replacement — Design

Date: 2026-10-11
Status: draft (phase 2, after file claims win)
Goal: replace `.tmp/sessions/*/context.md` with live progress in Eling.

## Scope
- Source of truth: project-local (machine-only, never committed, shared across worktrees of one repo).
- Large blobs (> ~8KB, long logs): `.eling/runtime/sessions/<session-id>/`, gitignored.
- Only durable outcomes (decisions, lessons): promoted to project memory via plain `memory_save`.
- During transition: `.tmp/sessions/` stays as a read-only mirror, not the source of truth.

## Model
- `session_id`: slug `YYYY-MM-DD-<topic>`, unique per chat.
- `branch`, `working_files[]`, `current_step`, `next_intent`, `updated_at`, `ttl_seconds`.
- Heartbeat: refresh `updated_at` on every step change; treated as stale when `now - updated_at > ttl`.

## Tools (new, minimal)
- `session_register(session_id, branch, ttl)` → create or reactivate.
- `session_heartbeat(session_id, current_step, next_intent, working_files)` → lightweight overwrite.
- `session_get(session_id)` / `session_list(active_only=true)` → read + list.
- `session_close(session_id)` → mark done, releases all claims.

## Agent guidance
- Via ServerInstructions + tool descriptions only, no AGENTS.md change.
- Rules: recall on every step change, heartbeat on every step change, check conflicts before writing files owned by another session.

## Phase 1 validation
- Two simulated chats on main: no silent overwrites, stale sessions drop from the list, recall stays clean.
- Pass criteria: no unannounced cross-chat overwrite in the test.
