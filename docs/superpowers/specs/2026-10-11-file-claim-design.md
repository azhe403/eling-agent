# File Claim Replacement — Design (Phase 1, win this first)

Date: 2026-10-11
Status: active draft
Goal: ownership markers across chats on the same main branch so work does not collide. Replaces the implicit role of `.tmp/checkpoints/*.txt` plus manual git-status checks.

## Scope
- Source of truth: project-local (never committed). Ground truth for content stays filesystem + git.
- Phase 1 granularity: file level. Hunk/line level is deferred.
- Behavior: soft first (warnings), hardening into rejections once stable.

## Model
- `claim_id`, `session_id`, `branch`, `paths[]`, `since`, `last_heartbeat`, `ttl_seconds` (default 300).
- A claim is live while `now - last_heartbeat <= ttl`; otherwise it is stale and drops from the active list.

## Tools (new, minimal)
- `file_claim(session_id, paths[])` → soft claim, soft-fails when a live session owns it (returns the owner).
- `file_release(session_id, paths[])` → explicit release; `session_close` releases everything.
- `file_list_active(branch)` → live claims per branch.
- `file_check_conflict(session_id, paths[])` → combines active claims + git dirty + mtime into a write/don't-write decision.

## Agent guidance (server only)
- ServerInstructions: must call `file_check_conflict` before writing files owned by another session; must heartbeat on every step change; must call `file_release` when done.
- No AGENTS.md change in this phase.

## Error handling
- Chat dies suddenly → its claims go stale automatically, no manual cleanup.
- Two concurrent claims → first wins, second gets owner + claim age.
- Dirty git tree without a claim → warning in soft phase, not a hard error.

## Validation
- Two simulated chats on main touching the same file: the second chat sees the correct owner warning.
- Stale claims (heartbeat stopped) disappear from the list within one TTL.
