# Commit Claim — Design (Phase 1 companion)

Date: 2026-10-15
Status: draft (advisory first; hardening into git hooks once stable)
Goal: protect the staging→commit window on a shared checkout, so chats working on the same
branch cannot sweep each other's uncommitted work into one commit. Companion to file claim
(2026-10-11): file claim protects files during editing, commit claim protects the commit slot.

## Scope
- Source of truth: project-local (never committed; shared across worktrees of one repo) —
  the same claim store as file claim, records distinguished by `checkout` + `branch`.
- Hazard boundary: the claim exists to protect the shared working tree and index of one
  checkout. Two different worktrees of the same repo have separate indexes, so collisions
  there are out of scope.
- Protects the local window from "before the first staging" to "commit landed". Does not
  order pushes to a shared remote; git already rejects non-fast-forward and the remainder
  is rebase-and-retry.
- Behavior: soft first (advisory via MCP), hardening into git hooks once stable. Hook
  install is opt-in per repo, not assumed present.

## Model
- `claim_id`, `session_id`, `checkout` (resolved repo root), `branch`, `paths[]` (optional,
  planned paths), `intent` (one-line summary of the planned commit), `since`,
  `last_heartbeat`, `ttl_seconds` (default 300).
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

## Agent guidance (server only)
- ServerInstructions: must call `commit_check_conflict` before `git add`; on red, stop and
  report to the user, do not stage; on green, must call `commit_claim` before the first
  staging; must call `commit_release` immediately after the commit lands; heartbeat on
  every step change (already required by the session-context phase).
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

## Deferred
- Turn queue: waiters, promotion on release, waking the next claimant — not this phase;
  exclusion plus visibility covers the hazard. `commit_list_active` deliberately exposes
  no queue position.
- Hunk/line granularity (deferred in the file-claim phase as well).

## Validation
- Two simulated chats on the same `checkout` + `branch`: while chat A holds a claim, chat
  B's `commit_check_conflict` reports the correct holder and claim age, and B does not
  stage.
- Hardening phase: a commit staged while a foreign live claim exists is rejected by
  `pre-commit`; a commit whose staged paths intersect a foreign live file claim is
  rejected.
- A claim whose session dies goes stale and disappears from the active list within one TTL.
- After chat A's commit lands and its claim is released, chat B's claim attempt succeeds.
- Same repo, different worktree: a live claim in worktree 1 raises no conflict in
  worktree 2.
- Hardening release-on-commit: the `post-commit` hook clears the committer's claim even
  when the agent never called `commit_release`.

## Dependency
- Needs the claim store from file claim (2026-10-11) to exist first; the model above
  extends that store with `checkout`, `branch`, and `intent`.
