# Commit Lease — Design (Phase 1 companion)

Date: 2026-10-15
Status: draft (advisory first; hardening into git hooks once stable)
Goal: protect the staging→commit window on a shared checkout, so chats working on the same
branch cannot sweep each other's uncommitted work into one commit. Companion to file lease
(2026-10-11): file lease protects files during editing, commit lease protects the commit slot.

## Scope
- Source of truth: global (a single `commit-leases.json` under the global agent directory,
  never committed; shared across all repos/worktrees on this machine) — records are
  distinguished by `checkout` + `branch`, so cross-repo/cross-worktree isolation is
  preserved at read time even though storage is global.
- Hazard boundary: the lease exists to protect the shared working tree and index of one
  checkout. Two different worktrees of the same repo have separate indexes, so collisions
  there are out of scope.
- Protects the local window from "before the first staging" to "commit landed". Does not
  order pushes to a shared remote; git already rejects non-fast-forward and the remainder
  is rebase-and-retry.
- Behavior: hardening included in this phase. Advisory MCP tools remain (for
  visibility and reporting), but enforcement happens via opt-in git hooks per repo:
  `pre-commit` rejects commits while a foreign live commit lease exists on the
  same `checkout` + `branch`, or whose staged paths intersect a foreign live
  file lease. On rejection it prints the holder's session, lease age, and intent,
  plus "retry later or wait for the lease to release", and exits non-zero so the
  commit does not land. `post-commit` auto-releases the committer's lease. Hook
  install is opt-in per repo (not assumed present) - the soft advisory path is
  the default until a repo opts in.

## Model
- `lease_id`, `session_id`, `checkout` (the working-copy root — the directory whose `.git` marker was
  hit; a linked worktree keys by the worktree path, distinct from the main checkout), `branch`, `paths[]` (optional,
  planned paths), `intent` (one-line summary of the planned commit), `since`,
  `last_heartbeat`, `ttl_seconds` (default 300). Released leases additionally carry
  `released_at` and `release_reason` (`explicit` | `stale` | `post-commit`).
- `session_id` must be non-blank and at least 32 characters (a full entropy token; too-short ids collide in the shared store).
- A lease is live while `now - last_heartbeat < ttl` (== ttl is stale); otherwise it drops from
  the active list.
- One live commit lease per `checkout` + `branch` at a time. A second lessee soft-fails
  and receives the holder's session, lease age, and intent.

## Tools (new, minimal)
- `commit_lease(session_id, branch, intent?, paths?)` → takes the slot; soft-fails with
  holder info when a live lease exists on the same `checkout` + `branch`.
- `commit_release(session_id)` → explicit release; `session_close` releases everything
  (per the session-context spec).
- `commit_list_active(branch?)` → the live lease for the checkout: holder, since, intent.
- `commit_check_conflict(session_id, paths[])` → combines a live foreign commit lease
  (same `checkout` + `branch`), file leases on the paths, and staged/dirty git state into a
  stage/don't-stage decision.
- `commit_wait(session_id, total_timeout_seconds?, initial_interval_seconds?)` → server-side
  wait: blocks until no foreign live commit lease exists on the same `checkout` + `branch`
  (or total timeout, default 180s), polling with exponential backoff starting at
  `initial_interval_seconds` (default 5s): 5s → 10s → 20s (cap). Does not take the slot;
  caller still must `commit_lease` after it returns green.

## Agent guidance (server only)
- ServerInstructions: must call `commit_check_conflict` before `git add`; on red, either
  call `commit_wait` (server-side blocking wait with timeout) or stop and report to the
  user (holder info + "retry later or wait for the lease to release"), do not stage; on
  green, must call `commit_lease` before the first staging; must call `commit_release`
  immediately after the commit lands; heartbeat on every step change (already required by
  the session-context phase).
- No AGENTS.md change in this phase.

## Error handling
- Chat dies suddenly → its lease goes stale automatically, no manual cleanup (same rule as
  file lease).
- Two concurrent lease attempts on the same `checkout` + `branch` → first wins, second gets
  holder name + lease age.
- Commit lands without release → agent's explicit `commit_release` in the advisory phase;
  `post-commit` hook auto-release in the hardening phase.
- Staged changes while the owner holds no lease of its own → warning surfaced by
  `commit_check_conflict` in the soft phase, not a hard error; in the hardening phase,
  `pre-commit` rejects commits made while a foreign live lease exists. Strict mode
  (rejecting any commit with no active lease at all) is recorded as a hardening flag, not
  fixed now.
- Pushes racing on the shared remote → out of scope; git rejects the non-fast-forward and
  the sender rebases and retries.

## Composition with file lease
- Advisory layer: `commit_check_conflict` folds file leases on the requested paths into its
  decision.
- Hardening layer: `pre-commit` has two duties — reject while a foreign live commit lease
  exists on the `checkout` + `branch`, and reject commits whose staged paths intersect a
  foreign live file lease. File lease protects the payload, commit lease protects the slot.

## Dashboard monitoring (observability + human force-release)

Companion for the advisory phase: lets a human see who holds the
commit slot on a checkout, and lets ONLY a human release a stuck lease from the
dashboard. Dashboard reads; the dashboard is also the only force-release path.
There is deliberately NO MCP tool for force-release, so agents cannot
self-release foreign leases.

- Source of truth: same lease store (global `commit-leases.json` under the agent
  directory); the dashboard reads it, but never writes except through the
  force-release endpoint below.
- Backend: owner-mode only. `GET /api/commit-lease/active?branch={branch}` returns
  live commit leases for the resolved checkout (branch filter optional; when
  omitted, all branches of that checkout). Response:
  `{ checkout, branch, commit_leases: [ { lease_id, session_id, branch, since,
  last_heartbeat, lease_age_seconds, ttl_seconds, paths, intent, checkout } ] }`.
  Cross-worktree isolation holds for the default: worktree A's leases never appear
  for worktree B. `GET /api/commit-lease/active?scope=all` is the machine-global
  mode the dashboard page consumes: every live lease across every checkout of the
  shared store, plus the global recently-released window (`checkout` is null;
  each row carries its own `checkout`). The default stays this-checkout for
  backwards compatibility.
- Force-release (dashboard-only): `POST /api/commit-lease/{lease_id}/release`
  (owner-mode only). Releases that ONE lease regardless of owner; moves it to
  the recently-released list with reason `forced`. UI must confirm before
  firing (explicit dialog, never a bare button). Every force-release writes an
  audit-trail entry (who/what/when/which lease) via the repo's audit system;
  the entry carries the released lease's own checkout. `POST` on a lease from
  another checkout returns 404; it never releases — unless an explicit
  `?checkout={root}` names that checkout, in which case that checkout's live
  lease releases instead of this host's.
- Release history: the store keeps a capped list of recently released leases
  (cap 50/checkout in the store; 20 per checkout surfaced by the snapshot API, or a
  single global 20 under `scope=all` and in the SSE frame) with `released_at` and `release_reason`
  (`explicit` | `stale` | `post-commit` | `forced`). `GET /api/commit-lease/active` also
  returns `recently_released: [ { lease_id, session_id, since,
  released_at, release_reason, intent, paths, checkout } ]`. This is how the dashboard
  surfaces auto-release (stale session, explicit release, post-commit hook,
  or forced from the dashboard) even though the lease no longer appears in the active list.
- Transport: SSE (no polling). `GET /api/events/commit-lease` pushes a frame whenever
  the lease store changes (lease/release/heartbeat/stale sweep), plus an initial
  snapshot on connect and a reconnect resync. Frame payload matches
  `/api/commit-lease/active?scope=all` (`{ checkout, branch, commit_leases, recently_released }`,
  `checkout` null — every frame is machine-global; the dashboard narrows the
  workspace selection client-side). Cross-process changes reach the dashboard owner via the coordinator notifier
  (same pattern as memory SSE).
- Frontend: a dedicated page at `/dashboard/commit-lease` (NOT a panel on the
  home page), reachable from the sidebar and from a link/card on the dashboard
  home. Sections: "Active" (live leases) and "Recently released" (release
  history with timestamp + reason). The page is workspace-aware like the other
  dashboard pages: a workspace picker with "All workspaces" as the default
  narrows the visible rows client-side (no refetch, no URL binding), the
  meta line reads "All workspaces" / "N workspaces selected" / the workspace
  name, and a per-row checkout (workspace name) column appears when more than
  one root is in view. Per-lease detail: checkout, branch,
  session_id, since, last_heartbeat, ttl_seconds, lease_age_seconds, paths,
  intent, release_reason. Force-release button on each active lease, gated by a
  confirmation dialog, calls `POST /api/commit-lease/{lease_id}/release?checkout={row_checkout}`.
  Subscribes via `EventSource`, reconnects with resync on drop. Empty states for
  both. Stale leases move from Active to Recently released within one TTL.
- Staleness: panel relies on backend filtering; no client-side TTL logic.
- Naming: HTTP surface is `/api/commit-lease/*`; the store file is
  `commit-leases.json`; internal model stays `Lease*` (CommitLeaseRecord, ICommitLeaseStore,
  CommitLeaseKind).
- Page scope: shows commit leases only; the payload carries no `kind` field.
   No file-lease tools in this phase.

## Deferred
- Turn queue (waiter list, promotion on release, waking the next lessee) remains deferred;
  `commit_wait` provides blocking wait with timeout as the server-side primitive, but does
  not maintain a waiter list or promote lessees. `commit_list_active` deliberately
  exposes no queue position.
- Hunk/line granularity (deferred in the file-lease phase as well).
- Strict mode (rejecting any commit with no active lease at all) remains a
  hardening flag, not fixed now.

## Validation
- Two simulated chats on the same `checkout` + `branch`: while chat A holds a lease, chat
  B's `commit_check_conflict` reports the correct holder and lease age, and B does not
  stage.
- Hardening phase: a commit staged while a foreign live lease exists is rejected by
  `pre-commit`; a commit whose staged paths intersect a foreign live file lease is
  rejected.
- A lease whose session dies goes stale and disappears from the active list within one TTL.
- After chat A's commit lands and its lease is released, chat B's lease attempt succeeds.
- Dashboard: a live lease appears in `/api/commit-lease/active` within one refresh; a
  stale lease (heartbeat expired) is absent; a lease in worktree 1 does not
  appear when querying from worktree 2; `?scope=all` returns every checkout's
  leases in one frame (the dashboard's default view), including leases held
  in worktree 2 while running from worktree 1.
- Same repo, different worktree: a live lease in worktree 1 raises no conflict in
  worktree 2.
- Hardening release-on-commit: the `post-commit` hook clears the committer's lease even
  when the agent never called `commit_release`.
- Dashboard force-release: `POST /api/commit-lease/{lease_id}/release` on another
  checkout's lease returns 404 and releases nothing; with an explicit
  `?checkout={root}` naming that checkout it releases the foreign lease instead
  (the dashboard's all-workspaces view uses exactly this); on this checkout it moves
  the lease to recently_released with reason `forced` and writes an audit entry;
  the UI requires an explicit confirmation before the call is made.

## Dependency
- Needs the lease store from file lease (2026-10-11) to exist first; the model above
  extends that store with `checkout`, `branch`, and `intent`.
