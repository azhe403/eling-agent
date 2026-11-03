# Commit Claim — Design (Phase 1 companion)

Date: 2026-10-15
Status: draft (advisory first; hardening into git hooks once stable)
Goal: protect the staging→commit window on a shared checkout, so chats working on the same
branch cannot sweep each other's uncommitted work into one commit. Companion to file claim
(2026-10-11): file claim protects files during editing, commit claim protects the commit slot.

## Scope
- Source of truth: global (a single `claims.json` under the global agent directory,
  never committed; shared across all repos/worktrees on this machine) — records are
  distinguished by `checkout` + `branch`, so cross-repo/cross-worktree isolation is
  preserved at read time even though storage is global.
- Hazard boundary: the claim exists to protect the shared working tree and index of one
  checkout. Two different worktrees of the same repo have separate indexes, so collisions
  there are out of scope.
- Protects the local window from "before the first staging" to "commit landed". Does not
  order pushes to a shared remote; git already rejects non-fast-forward and the remainder
  is rebase-and-retry.
- Behavior: hardening included in this phase. Advisory MCP tools remain (for
  visibility and reporting), but enforcement happens via opt-in git hooks per repo:
  `pre-commit` rejects commits while a foreign live commit claim exists on the
  same `checkout` + `branch`, or whose staged paths intersect a foreign live
  file claim. On rejection it prints the holder's session, claim age, and intent,
  plus "retry later or wait for the claim to release", and exits non-zero so the
  commit does not land. `post-commit` auto-releases the committer's claim. Hook
  install is opt-in per repo (not assumed present) - the soft advisory path is
  the default until a repo opts in.

## Model
- `claim_id`, `session_id`, `checkout` (resolved repo root), `branch`, `paths[]` (optional,
  planned paths), `intent` (one-line summary of the planned commit), `since`,
  `last_heartbeat`, `ttl_seconds` (default 300). Released claims additionally carry
  `released_at` and `release_reason` (`explicit` | `stale` | `post-commit`).
- A claim is live while `now - last_heartbeat <= ttl`; otherwise it is stale and drops from
  the active list.
- One live commit claim per `checkout` + `branch` at a time. A second claimant soft-fails
  and receives the holder's session, claim age, and intent.

## Tools (new, minimal)
- `commit_claim(session_id, branch, intent?, paths?)` → takes the slot; soft-fails with
  holder info when a live claim exists on the same `checkout` + `branch`.
- `commit_release(session_id)` → explicit release; `session_close` releases everything
  (per the session-context spec).
- `commit_list_active(branch?)` → the live claim for the checkout: holder, since, intent.
- `commit_check_conflict(session_id, paths[])` → combines a live foreign commit claim
  (same `checkout` + `branch`), file claims on the paths, and staged/dirty git state into a
  stage/don't-stage decision.
- `commit_wait(session_id, total_timeout_seconds?, initial_interval_seconds?)` → server-side
  wait: blocks until no foreign live commit claim exists on the same `checkout` + `branch`
  (or total timeout, default 180s), polling with exponential backoff starting at
  `initial_interval_seconds` (default 5s): 5s → 10s → 20s (cap). Does not take the slot;
  caller still must `commit_claim` after it returns green.

## Agent guidance (server only)
- ServerInstructions: must call `commit_check_conflict` before `git add`; on red, either
  call `commit_wait` (server-side blocking wait with timeout) or stop and report to the
  user (holder info + "retry later or wait for the claim to release"), do not stage; on
  green, must call `commit_claim` before the first staging; must call `commit_release`
  immediately after the commit lands; heartbeat on every step change (already required by
  the session-context phase).
- No AGENTS.md change in this phase.

## Error handling
- Chat dies suddenly → its claim goes stale automatically, no manual cleanup (same rule as
  file claim).
- Two concurrent claim attempts on the same `checkout` + `branch` → first wins, second gets
  holder name + claim age.
- Commit lands without release → agent's explicit `commit_release` in the advisory phase;
  `post-commit` hook auto-release in the hardening phase.
- Staged changes while the owner holds no claim of its own → warning surfaced by
  `commit_check_conflict` in the soft phase, not a hard error; in the hardening phase,
  `pre-commit` rejects commits made while a foreign live claim exists. Strict mode
  (rejecting any commit with no active claim at all) is recorded as a hardening flag, not
  fixed now.
- Pushes racing on the shared remote → out of scope; git rejects the non-fast-forward and
  the sender rebases and retries.

## Composition with file claim
- Advisory layer: `commit_check_conflict` folds file claims on the requested paths into its
  decision.
- Hardening layer: `pre-commit` has two duties — reject while a foreign live commit claim
  exists on the `checkout` + `branch`, and reject commits whose staged paths intersect a
  foreign live file claim. File claim protects the payload, commit claim protects the slot.

## Dashboard monitoring (soft phase)

Companion observability for the advisory phase: lets a human see who holds the
commit slot on a checkout without leaving the dashboard. Advisory only — no
enforcement.

- Source of truth: same claim store (global `claims.json` under the agent
  directory); the dashboard reads, never writes.
- Backend: owner-mode only. `GET /api/claims/active?branch={branch}` returns
  live commit claims for the resolved checkout (branch filter optional; when
  omitted, all branches of that checkout). Response:
  `{ checkout, branch, claims: [ { claim_id, session_id, kind, since,
  claim_age_seconds, ttl_seconds, paths, intent } ] }`. Cross-worktree
  isolation holds: worktree A's claims never appear for worktree B.
- Release history: the store keeps a capped list of recently released claims
  (default 20 per checkout) with `released_at` and `release_reason`
  (`explicit` | `stale` | `post-commit`). `GET /api/claims/active` also
  returns `recently_released: [ { claim_id, session_id, kind, since,
  released_at, release_reason, intent, paths } ]`. This is how the dashboard
  surfaces auto-release (stale session, explicit release, or post-commit hook)
  even though the claim no longer appears in the active list.
- Transport: SSE (no polling). `GET /api/events/claims` pushes a frame whenever
  the claim store changes (claim/release/heartbeat/stale sweep), plus an initial
  snapshot on connect and a reconnect resync. Frame payload matches
  `/api/claims/active` (`{ checkout, branch, claims, recently_released }`).
  Cross-process changes reach the dashboard owner via the coordinator notifier
  (same pattern as memory SSE).
- Frontend: a panel on the dashboard home with two sections - "Active" (live
  claims) and "Recently released" (release history with timestamp + reason).
  Subscribes via `EventSource`, reconnects with resync on drop. Empty states for
  both. Stale claims move from Active to Recently released within one TTL.
- Staleness: panel relies on backend filtering; no client-side TTL logic.

## Deferred
- Turn queue (waiter list, promotion on release, waking the next claimant) remains deferred;
  `commit_wait` provides blocking wait with timeout as the server-side primitive, but does
  not maintain a waiter list or promote claimants. `commit_list_active` deliberately
  exposes no queue position.
- Hunk/line granularity (deferred in the file-claim phase as well).
- Strict mode (rejecting any commit with no active claim at all) remains a
  hardening flag, not fixed now.

## Validation
- Two simulated chats on the same `checkout` + `branch`: while chat A holds a claim, chat
  B's `commit_check_conflict` reports the correct holder and claim age, and B does not
  stage.
- Hardening phase: a commit staged while a foreign live claim exists is rejected by
  `pre-commit`; a commit whose staged paths intersect a foreign live file claim is
  rejected.
- A claim whose session dies goes stale and disappears from the active list within one TTL.
- After chat A's commit lands and its claim is released, chat B's claim attempt succeeds.
- Dashboard: a live claim appears in `/api/claims/active` within one refresh; a
  stale claim (heartbeat expired) is absent; a claim in worktree 1 does not
  appear when querying from worktree 2.
- Same repo, different worktree: a live claim in worktree 1 raises no conflict in
  worktree 2.
- Hardening release-on-commit: the `post-commit` hook clears the committer's claim even
  when the agent never called `commit_release`.

## Dependency
- Needs the claim store from file claim (2026-10-11) to exist first; the model above
  extends that store with `checkout`, `branch`, and `intent`.
