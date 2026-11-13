# Commit Lease Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add the `commit_lease` / `commit_release` / `commit_list_active` / `commit_check_conflict` MCP tools plus the shared project-local lease store they rest on, so concurrent chats on one checkout+branch stop sweeping each other's uncommitted work into a single commit.

**Architecture:** A single JSON-backed lease store lives under the project-local `.eling/runtime/` directory (never committed, shared across worktrees of one repo), guarded by a cross-process mutex like `JsonDatabaseConnectionStore`. Records carry a `Kind` discriminator (`file` | `commit`) so the same store serves both file lease and commit lease; commit records add `checkout`, `branch`, and `intent`. Liveness is derived from `last_heartbeat` vs `ttl_seconds` (default 300) — stale records drop from the active list with no cleanup. MCP tools in `Eling.Backend.Mcp.Tools` consume the store plus `GitRepositoryResolver` (for the checkout root) and the current branch. `commit_check_conflict` folds commit-lease ownership, git dirty/staged state, and (when available) file leases on the requested paths into one stage/don't-stage verdict.

**Tech Stack:** .NET 10 (ASP.NET Core), ModelContextProtocol.Server, System.Text.Json, xUnit. Storage mirrors `JsonDatabaseConnectionStore` (mutex + atomic `.tmp` write).

**Spec:** `docs/superpowers/specs/2026-10-15-commit-lease-design.md` (depends on `docs/superpowers/specs/2026-10-11-file-lease-design.md` for the lease store; this plan builds the store first since it does not yet exist in the tree).

## Global Constraints

- **NO COMMITS for this execution (user decision 2026-11-12).** Implementers must NOT run `git add`, `git commit`, or `git push`. Skip every "Step 5: Commit" (and any equivalent) in the tasks below; report file changes instead. The user commits manually.
- **Shared checkout, other agent's WIP present.** The working tree contains uncommitted changes that are NOT ours (tests/Eling.Backend.Tests/*, CHANGELOG.md, opencode.json, src/backend/Eling.Backend/Mcp/Tools/ShellTools.cs, next.config.ts, untracked frontend/test files, docs/superpowers/specs+plans from other work). DO NOT modify, revert, or commit any of those. Touch only files listed in your task (plus the shared-file edits your task names).
- **Task 9 (git hook hardening) is SKIPPED** for this execution (user decision 2026-11-12). Do not create hooks or the install tool.
- Store path: `{globalAgentDir}/commit-leases.json` (global, never committed; shared across all repos/worktrees on this machine). Records are scoped by `checkout` + `branch`, so a live lease in worktree A raises no conflict in worktree B despite shared storage. Mutex: `Global\eling-commit-leases`.
- HTTP surface naming: endpoints live under `/api/commit-lease/*`, the payload field is `commit_leases`, the SSE endpoint is `/api/events/commit-lease`. Internal model stays `Lease*` (CommitLeaseRecord/ICommitLeaseStore/CommitLeaseKind). Store file `commit-leases.json`.
- Release reasons now include `forced` (dashboard force-release): valid values are `explicit` | `stale` | `post-commit` | `forced`.
- TTL default 300s; a record is live while `now - last_heartbeat <= ttl`. No background sweeper - staleness is filtered at read time.
- One live commit lease per `checkout` + `branch` at a time; a second lessee soft-fails and receives holder session, lease age, and intent. First-write-wins under the mutex.
- Soft phase only: tools return verdicts/warnings; no git hook is installed by this plan. Hook hardening is a follow-up.
- `session_id` is a required argument on every tool (the MCP client supplies it); the store never fabricates one.
- Force-release is **dashboard-only** (`POST /api/commit-lease/{lease_id}/release`, owner-mode): there is intentionally NO MCP tool for it, so agents cannot self-release foreign leases. Every force-release writes an audit-trail entry and moves the lease to recently_released with reason `forced`.
- Conventional commits for any code change: `feat(backend): ...` (only if the user ever authorizes commits; currently none).
- No AGENTS.md change in this phase (guidance lives in `ServerInstructions`).

## Review Focus

- **Stale lease resurrected by a heartbeat from a dead session:** `commit_release` and heartbeat updates must be scoped to the lease-owning `session_id`; a stray heartbeat must not revive a lease whose session is gone. Pinned in Task 1 (heartbeat only extends a lease owned by the same session).
- **Two lessees racing on the same mutex:** first-wins must hold even under concurrent calls; the loser must receive the winner's holder info, not an exception. Pinned in Task 2 (concurrent-lease test).
- **Commit lands but lease never released:** `commit_check_conflict` must surface a warning (not a hard error) when staged changes exist while the caller holds no lease; the design's strict mode is deferred. Pinned in Task 5 (staged-without-lease warning).
- **Different worktree, same repo:** a live lease in worktree 1 must raise no conflict in worktree 2 because `checkout` (resolved repo root) differs... actually the spec says same repo different worktree has separate indexes and is out of scope — but `checkout` is the resolved repo root which is SHARED across worktrees. Re-read: scope says hazard boundary is one checkout's working tree+index; different worktrees are out of scope. So `checkout` here means the *worktree root* (the actual checkout path), not the repo root. Pinned in Task 1 (lease key uses worktree root, not repo root) and Task 5 (cross-worktree no-conflict test).
- **TTL boundary off-by-one:** a lease exactly at `now - last_heartbeat == ttl` is stale. Pinned in Task 1 (boundary test).

---

### Task 1: Lease record, `ICommitLeaseStore`, and `JsonCommitLeaseStore`

**Files:**
- Create: `src/backend/Eling.Core/CommitLease/CommitLeaseKind.cs`
- Create: `src/backend/Eling.Core/CommitLease/CommitLeaseRecord.cs`
- Create: `src/backend/Eling.Core/CommitLease/ICommitLeaseStore.cs`
- Create: `src/backend/Eling.Core/CommitLease/JsonCommitLeaseStore.cs`
- Test: `tests/Eling.Backend.Tests/JsonCommitLeaseStoreTests.cs`

**Interfaces:**
- Consumes: a storage file path (global `commit-leases.json` under the agent directory) injected via constructor; optional `ICommitLeaseChangeNotifier` (cross-process notify on mutation, mirroring `IMemoryChangeNotifier`); `System.Text.Json` via `JsonDefaults.Shared`.
- Produces:
  - `enum CommitLeaseKind { File, Commit }`
  - `sealed record CommitLeaseRecord` (property-based, `required` properties: LeaseId, SessionId, Kind, Checkout, Branch, Since, LastHeartbeat, TtlSeconds, Paths, Intent; ReleasedAt/ReleaseReason default null)
  - `interface ICommitLeaseStore` with:
    - `Task<IReadOnlyList<CommitLeaseRecord>> ListActiveAsync(string checkout, string? branch, CancellationToken ct = default)` (filters by checkout+branch when branch given; drops stale)
    - `Task<CommitLeaseRecord?> TryLeaseAsync(CommitLeaseRecord candidate, CancellationToken ct = default)` → returns the existing live conflicting lease when one exists, else stores candidate and returns null
    - `Task<bool> ReleaseAsync(string sessionId, CommitLeaseKind? kind, CancellationToken ct = default)` → removes all leases owned by that session (optionally filtered by kind), moving them to the recently-released list with reason `explicit`; returns true if any removed
    - `Task<bool> HeartbeatAsync(string leaseId, string sessionId, CancellationToken ct = default)` → extends `LastHeartbeat` only if owned by sessionId; returns false otherwise
    - `Task<IReadOnlyList<CommitLeaseRecord>> ListRecentlyReleasedAsync(string checkout, int limit = 20, CancellationToken ct = default)` → released leases for the checkout, newest first, capped
    - `Task<IReadOnlyList<CommitLeaseRecord>> ListAllAsync(CancellationToken ct = default)` (raw, for tests)
    - New interface `ICommitLeaseChangeNotifier { Task NotifyAsync(CancellationToken ct = default); }` with `HttpCoordinatorCommitLeaseChangeNotifier` impl (mirroring `HttpCoordinatorMemoryChangeNotifier`) so MCP-side mutations reach the dashboard owner.

- [ ] **Step 1: Write the failing test** — `JsonCommitLeaseStoreTests` with a temp dir. Tests: (a) lease then `ListActiveAsync` returns it; (b) second `TryLeaseAsync` with same checkout+branch+kind=Commit returns the first record (first-wins) and leaves store unchanged; (c) different `Checkout` → no conflict; (d) `ReleaseAsync(sessionId)` removes it; (e) a record with `LastHeartbeat = now - (ttl+1)s` is absent from `ListActiveAsync`; (f) boundary: `now - last_heartbeat == ttl` is stale; (g) `HeartbeatAsync` with wrong sessionId returns false and does not extend; (h) `HeartbeatAsync` with right sessionId extends and returns true. Use a short TTL (e.g. 60s) and inject `TimeProvider`? If `JsonCommitLeaseStore` takes `TimeProvider? timeProvider = null` defaulting to `TimeProvider.System`, tests pass a fake. Decide: yes, inject `TimeProvider`.

- [ ] **Step 2: Run to verify red** — `dotnet test tests/Eling.Backend.Tests/Eling.Backend.Tests.csproj --filter "FullyQualifiedName~JsonCommitLeaseStoreTests" --artifacts-path .bin-test`.

- [ ] **Step 3: Implement** — `CommitLeaseRecord` is a record; `JsonCommitLeaseStore` mirrors `JsonDatabaseConnectionStore` (cross-process `Mutex` named `Global\eling-commit-leases`, atomic `.tmp` write, swallows read errors returning []). Store holds two collections: active leases and recently-released (capped at 50 per checkout). `TryLeaseAsync` under the gate: load, find a live lease with same `Checkout`+`Branch`+`Kind` (for commit kind, branch equality; for file kind, path intersection — but Phase 1 of this plan only needs commit; implement path-intersection for file kind too for future use, using `Paths` overlap), if found return it; else append candidate, save, return null. `ListActiveAsync` filters stale by `TimeProvider.GetUtcNow()` and moves stale records to recently-released with reason `stale`. `ReleaseAsync` moves owned leases to recently-released with reason `explicit` (or `post-commit` when called from hook path). `HeartbeatAsync` updates only when owner matches. `ListRecentlyReleasedAsync` returns newest-first, capped.

- [ ] **Step 4: Run to verify green** — same filter, all tests pass.

- [ ] **Step 5: Commit** — `git add src/backend/Eling.Core/CommitLease tests/Eling.Backend.Tests/JsonCommitLeaseStoreTests.cs && git commit -m "feat(core): add project-local lease store with TTL liveness"`.

---

### Task 2: `commit_lease` MCP tool

**Files:**
- Create: `src/backend/Eling.Backend/Mcp/Tools/CommitLeaseTool.cs`
- Test: `tests/Eling.Backend.Tests/CommitLeaseToolTests.cs`

**Interfaces:**
- Consumes: `ICommitLeaseStore`, `GitRepositoryResolver` (static), `TimeProvider`.
- Produces: tool `commit_lease(session_id, branch?, intent?, paths?)` returning either `{ lease_id, since }` on success or `{ holder_session_id, lease_age_seconds, intent }` on soft-fail. When `branch` is null/blank, resolve via `git`? The store needs a branch. Resolve branch by running `git rev-parse --abbrev-ref HEAD` in the checkout, OR accept null and treat null branch as "current" resolved inside the tool. Simpler: tool requires `branch`? Spec says `commit_lease(session_id, branch, intent?, paths?)` with branch required. Keep branch required (non-blank) to match spec; document that callers pass the current branch. Checkout resolved from `Environment.CurrentDirectory` via `GitRepositoryResolver.ResolveRepositoryRoot` (worktree root — see Review Focus).

- [ ] **Step 1: Write the failing test** — `CommitLeaseToolTests` with a temp store dir and a fake `TimeProvider`. Tests: (a) first lease succeeds and returns `lease_id`; (b) second lease on same checkout+branch returns holder info and does not overwrite; (c) lease on different checkout succeeds; (d) `paths` and `intent` round-trip into the stored record.

- [ ] **Step 2: Run to verify red**.

- [ ] **Step 3: Implement** — generate `lease_id` via `System.Ulid.NewUlid()`. Resolve `checkout` = `GitRepositoryResolver.ResolveRepositoryRoot(cwd)` ?? cwd (fallback so non-git dirs still work, keyed by cwd). Build `CommitLeaseRecord(Kind=Commit, ...)`, call `TryLeaseAsync`; on null return success DTO, else soft-fail DTO.

- [ ] **Step 4: Run to verify green**.

- [ ] **Step 5: Commit** — `git add ... && git commit -m "feat(backend): add commit_lease MCP tool"`.

---

### Task 3: `commit_release` MCP tool

**Files:**
- Create: `src/backend/Eling.Backend/Mcp/Tools/CommitReleaseTool.cs`
- Test: `tests/Eling.Backend.Tests/CommitReleaseToolTests.cs`

**Interfaces:**
- Consumes: `ICommitLeaseStore`.
- Produces: tool `commit_release(session_id)` → `{ released: bool }`, removing all commit leases owned by that session.

- [ ] **Step 1: Write the failing test** — lease then release returns `released=true` and list is empty; release with no lease returns `released=false`; release by a different session returns `released=false` and leaves the lease.

- [ ] **Step 2: Run to verify red**.

- [ ] **Step 3: Implement** — call `ReleaseAsync(sessionId, CommitLeaseKind.Commit)`.

- [ ] **Step 4: Run to verify green**.

- [ ] **Step 5: Commit**.

---

### Task 4: `commit_list_active` MCP tool

**Files:**
- Create: `src/backend/Eling.Backend/Mcp/Tools/CommitListActiveTool.cs`
- Test: `tests/Eling.Backend.Tests/CommitListActiveToolTests.cs`

**Interfaces:**
- Consumes: `ICommitLeaseStore`, `GitRepositoryResolver`.
- Produces: tool `commit_list_active(branch?)` → list of `{ holder_session_id, since, lease_age_seconds, intent, paths }` for live commit leases on this checkout (filtered by branch when given).

- [ ] **Step 1: Write the failing test** — two leases on different checkouts both visible when querying without branch? Actually list is scoped to current checkout. Test: lease on current checkout appears; stale lease does not; branch filter works.

- [ ] **Step 2–5: red → implement → green → commit**.

---

### Task 5: `commit_check_conflict` MCP tool

**Files:**
- Create: `src/backend/Eling.Backend/Mcp/Tools/CommitCheckConflictTool.cs`
- Test: `tests/Eling.Backend.Tests/CommitCheckConflictToolTests.cs`

**Interfaces:**
- Consumes: `ICommitLeaseStore`, `GitRepositoryResolver`, and shell-out to `git status --porcelain` + `git diff --cached --name-only` (reuse existing git helpers if any; otherwise a small `GitStatusReader`).
- Produces: tool `commit_check_conflict(session_id, paths[])` → `{ verdict: "green" | "red", holder?: {...}, staged_without_lease: bool, conflicting_file_leases?: [...] }`.
  - Red when a *foreign* live commit lease exists on same checkout+branch.
  - `staged_without_lease=true` (warning) when staged files exist and the caller holds no commit lease of its own.
  - File-lease folding: when the lease store holds live `File` leases on any of `paths` owned by another session, list them in `conflicting_file_leases` and mark red. (Store already supports this via `CommitLeaseKind.File`.)

- [ ] **Step 1: Write the failing test** — (a) no leases + clean tree → green; (b) foreign commit lease → red with holder info; (c) staged files while caller holds no lease → green verdict but `staged_without_lease=true`; (d) foreign file lease on a requested path → red with that lease listed; (e) cross-worktree: lease in worktree 1 does not make worktree 2 red.

- [ ] **Step 2: Run to verify red**.

- [ ] **Step 3: Implement** — read git state via `Process` (git) with a 5s timeout; parse porcelain output. Tolerate missing git (treat as no git state, still evaluate leases).

- [ ] **Step 4: Run to verify green**.

- [ ] **Step 5: Commit**.

---

### Task 6: `commit_wait` MCP tool (server-side wait)

**Files:**
- Create: `src/backend/Eling.Backend/Mcp/Tools/CommitWaitTool.cs`
- Test: `tests/Eling.Backend.Tests/CommitWaitToolTests.cs`

**Interfaces:**
- Consumes: `ICommitLeaseStore`, `TimeProvider`.
- Produces: tool `commit_wait(session_id, total_timeout_seconds = 180, initial_interval_seconds = 5)` → blocks until no foreign live commit lease exists on the same checkout+branch (or total timeout), polling with exponential backoff: 5s → 10s → 20s (cap). Returns `{ verdict: "green" | "red", waited_seconds, holder? }`. On timeout returns `red` with the current holder info. Does not take the slot; caller must `commit_lease` after green.

- [ ] **Step 1: Write the failing test** — `CommitWaitToolTests`: (a) no foreign lease → returns green immediately with `waited_seconds` ~0; (b) foreign lease released mid-wait → returns green after release, `waited_seconds` > 0; (c) timeout → returns red with holder info and `waited_seconds` ~timeout.

- [ ] **Step 2: Run to verify red**.

- [ ] **Step 3: Implement** — loop: check store for foreign live lease; if none return green; if timeout reached return red; else `Task.Delay(2000, ct)` and repeat. Respect cancellation token.

- [ ] **Step 4: Run to verify green**.

- [ ] **Step 5: Commit** — `git add ... && git commit -m "feat(backend): add commit_wait server-side blocking wait"`.

---

### Task 7: DI registration and McpHostBuilder wiring

**Files:**
- Modify: `src/backend/Eling.Backend/Bootstrap/McpHostBuilder.cs` (register `ICommitLeaseStore` → `JsonCommitLeaseStore` pointed at `Path.Combine(CentralAgentDirectory.Resolve(), "commit-leases.json")`, plus `TimeProvider.System`). Mirror the `JsonDatabaseConnectionStore` registration pattern.
- Modify: `src/backend/Eling.Backend/Bootstrap/AppServices.cs` if it aggregates shared services used by both MCP and dashboard hosts (check whether `shared` object carries stores like `Registry`/`Broadcaster`; mirror that pattern so the dashboard host shares one store).

- [ ] **Step 1: Inspect** `AppServices.cs` to see the shared-services pattern; add `LeaseStore` to it if applicable.
- [ ] **Step 2: Register** in both `McpHostBuilder` and `DashboardServices` (if dashboard exposes tools).
- [ ] **Step 3: Build** — `dotnet build src/backend/Eling.Backend/Eling.Backend.csproj` → clean.
- [ ] **Step 4: Commit**.

---

### Task 8: Agent guidance in `ServerInstructions`

**Files:**
- Modify: `src/backend/Eling.Backend/Mcp/ServerInstructions.cs` — append a section (as a new entry in `Sections`) mirroring the spec's "Agent guidance": must call `commit_check_conflict` before `git add`; on red, stop and report; on green, must call `commit_lease` before first staging; must call `commit_release` immediately after commit lands; heartbeat on every step change.

- [ ] **Step 1: Add the section** (verbatim-ish from spec §Agent guidance).
- [ ] **Step 2: Verify** `ServerInstructions.Default` contains the new text (unit test in existing `ServerInstructions` tests if any; else add a small assertion test).
- [ ] **Step 3: Commit**.

---

### Task 9: Git hook hardening (opt-in per repo) — **SKIPPED (user decision 2026-11-12); do not implement**

**Files:**
- Create: `src/backend/Eling.Backend/Hooks/CommitLeasePreCommitHook.cs` (or a shell script template `hooks/pre-commit` shipped as embedded resource)
- Create: `src/backend/Eling.Backend/Hooks/CommitLeasePostCommitHook.cs`
- Create: `src/backend/Eling.Backend/Mcp/Tools/CommitHookInstallTool.cs` (MCP tool `commit_hook_install` / `commit_hook_uninstall`)
- Test: `tests/Eling.Backend.Tests/CommitHookTests.cs`

**Interfaces:**
- `pre-commit`: resolves current checkout+branch; if a foreign live commit lease exists → print holder session, lease age, intent + "retry later or wait for the lease to release", exit 1; if staged paths intersect a foreign live file lease → exit 1 with the conflicting lease. Otherwise exit 0.
- `post-commit`: removes the committer's commit lease (reason `post-commit`) from the store; best-effort, never fails the commit.
- `commit_hook_install(repo_root)` writes both hooks into `{repo_root}/.git/hooks/` (or worktree hooks dir) with executable bit; `commit_hook_uninstall` removes them. Idempotent; refuses to overwrite a non-Eling hook without `--force`.

- [ ] **Step 1: Write the failing test** — `CommitHookTests`: (a) pre-commit exits 1 when foreign live lease exists; (b) exits 0 when no foreign lease; (c) post-commit clears the committer's lease; (d) install is idempotent and does not clobber a foreign hook.

- [ ] **Step 2: Run to verify red**.

- [ ] **Step 3: Implement** — hook as a small console entry point (or script invoking the MCP host's `commit_hook_check` subcommand). Reuse `ICommitLeaseStore` + `GitRepositoryResolver`. Keep hook fast (<100ms) by reading the JSON directly.

- [ ] **Step 4: Run to verify green**.

- [ ] **Step 5: Commit** — `git add ... && git commit -m "feat(backend): add opt-in git hooks for commit lease enforcement"`.

---

### Task 10: Dashboard backend — commit-lease snapshot, SSE, force-release (no polling)

**Files:**
- Create: `src/backend/Eling.Backend/Dtos/CommitLeaseDto.cs`
- Create: `src/backend/Eling.Backend/Services/CommitLeaseBroadcaster.cs` (mirrors `RuntimeMetricsBroadcaster`: subscriber-gated pump, or event-driven push on store change; cross-process changes via coordinator notifier mirroring `HttpCoordinatorMemoryChangeNotifier`)
- Create: `src/backend/Eling.Backend/Controllers/CommitLeaseController.cs`
- Modify: `src/backend/Eling.Backend/Bootstrap/DashboardServices.cs` — register `CommitLeaseBroadcaster` (and wire store change → `Publish`)
- Modify: `src/backend/Eling.Backend/Bootstrap/DashboardRoutes.cs` — map `GET /api/commit-lease/active` (snapshot), `GET /api/events/commit-lease` (SSE), `POST /api/commit-lease/{leaseId}/release` (force-release); all inside the owner-mode block
- Modify: `src/backend/Eling.Core/CommitLease/ICommitLeaseStore.cs` and `JsonCommitLeaseStore.cs` — ADD `Task<bool> ForceReleaseAsync(string leaseId, CancellationToken ct = default)`: releases that ONE lease by id regardless of owning session; moves it to recently-released with reason `forced`; returns false when no live lease with that id exists. Add unit cases to `JsonCommitLeaseStoreTests` for it.
- Test: `tests/Eling.Backend.Tests/CommitLeaseEndpointTests.cs`, `tests/Eling.Backend.Tests/CommitLeaseBroadcasterTests.cs`

**Interfaces:**
- `GET /api/commit-lease/active?branch={branch}` (owner-mode) → `{ checkout, branch, commit_leases: [ { lease_id, session_id, branch, since, last_heartbeat, lease_age_seconds, ttl_seconds, paths, intent, checkout } ], recently_released: [ { lease_id, session_id, since, released_at, release_reason, intent, paths, checkout } ] }`. Stale leases absent; branch filter optional (omit = all branches of this checkout); cross-worktree isolation (leases from a different checkout root never returned). 404/not registered on non-owner hosts.
- `GET /api/events/commit-lease` (SSE, owner-mode) — `data: connected`, then frames `{ checkout, branch, commit_leases, recently_released }` on every store change (lease/release/heartbeat/stale sweep); initial snapshot on connect; reconnect resync. Cross-process changes reach the owner via the coordinator notifier (same pattern as memory SSE).
- `POST /api/commit-lease/{leaseId}/release` (owner-mode) — force-releases that ONE lease by id regardless of owning session; moves it to recently_released with `release_reason = forced`; writes an audit-trail entry via the repository's existing audit system (inspect how shell/audit tools record audit events and mirror the call); returns 404 when no such live lease exists on this checkout. NO MCP tool counterpart is created. `release_reason` valid set becomes `explicit` | `stale` | `post-commit` | `forced`.

- [ ] **Step 1: Write the failing test** — `CommitLeaseEndpointTests`: (a) seed a lease via `ICommitLeaseStore`, GET returns it with correct fields; (b) stale lease (heartbeat expired) is absent; (c) different checkout's leases are absent; (d) non-owner host returns 404/not registered; (e) POST release force-releases the named lease even when owned by another session, and it appears in recently_released with reason `forced`; (f) POST on an unknown or already-released id returns 404 and releases nothing. Plus `ForceReleaseAsync` unit cases in the store tests (own lease, foreign lease, stale id).

- [ ] **Step 2: Run to verify red**.

- [ ] **Step 3: Implement** — DTO record; controller reads `ICommitLeaseStore.ListActiveAsync(checkout, branch)` and `ListRecentlyReleasedAsync` and projects to DTO with `lease_age_seconds = (now - since).TotalSeconds`; force-release path calls `ForceReleaseAsync`. Register routes in `DashboardRoutes.MapCommitLeaseEndpoints` inside the owner block. Wire `CommitLeaseBroadcaster` so store mutations notify subscribers.

- [ ] **Step 4: Run to verify green**.

- [ ] **Step 5: Commit — SKIPPED (no commits this execution).**

---

### Task 11: Dashboard frontend — Commit Lease page

**Files:**
- Create: `src/frontend/Eling.Dashboard/src/app/dashboard/commit-lease/page.tsx`
- Create: `src/frontend/Eling.Dashboard/src/app/dashboard/commit-lease/CommitLease.tsx` (client component, mirror `Logs.tsx` structure from `src/app/dashboard/logs/`)
- Modify: `src/frontend/Eling.Dashboard/src/components/app-sidebar.tsx` — add a nav item `{ title: "Commit Lease", url: "/dashboard/commit-lease", icon: <KeyRound className="size-4" /> }` (import KeyRound from lucide-react; place near Logs/Databases — do not reorder existing items)
- Modify: `src/frontend/Eling.Dashboard/src/app/dashboard/home/` — add a link to the page from the dashboard home (mirror the `recent-activity.tsx`/`platform.tsx` pattern: a Card with a CardAction Link to `/dashboard/commit-lease`, or a small new card component rendered in `DashboardHome.tsx`; check `DashboardHome.tsx` and `data.ts` to see how home sections are composed)
- Test: (typecheck only; no unit test infra for components in this repo) — `pnpm --dir src/frontend/Eling.Dashboard exec tsc --noEmit`

**Interfaces:**
- Consumes: `GET /api/commit-lease/active` (snapshot, Task 10) + `GET /api/events/commit-lease` (SSE) + `POST /api/commit-lease/{lease_id}/release` (force-release).
- Page at `/dashboard/commit-lease`, label "Commit Lease". Two sections: "Active" (live leases: detail per row — session_id (truncated display), checkout, branch, since, last_heartbeat, ttl_seconds, lease_age_seconds, paths, intent) and "Recently released" (timestamp + `release_reason` incl. `forced`, intent, paths). Per-row expandable detail or dialog. "Force release" button on each active lease, gated by a confirmation dialog (reuse the existing dialog component from `components/ui` — inspect what the repo uses), which POSTs to the force-release endpoint; success/error surfaced as a toast (mirror existing toast usage). Subscribes via `EventSource("/api/events/commit-lease")` with reconnect resync (mirror `use-codebase-rebuild-sse.ts`); NO polling. Empty states for both sections. Stale leases move from Active to Recently released within one TTL (backend filters; no client-side TTL math). Cross-checkout isolation is the backend's job; the page is a dumb renderer of the frame plus the snapshot on mount.

- [ ] **Step 1: Read the frontend agent rules' referenced docs** — from `src/frontend/Eling.Dashboard/`, skim `node_modules/next/dist/docs/` client-component guidance before writing.

- [ ] **Step 2: Write the page and component** — mount `Suspense` + `Skeleton` like the Logs route; fetch the snapshot on mount; subscribe to the SSE stream; render both sections; implement the confirm + force-release flow; keep everything inside existing styling tokens.

- [ ] **Step 3: Wire navigation** — add the sidebar item in `app-sidebar.tsx`; add the home link/card; keep diffs minimal.

- [ ] **Step 4: Typecheck** — `pnpm --dir src/frontend/Eling.Dashboard exec tsc --noEmit` → clean. NEVER `pnpm build` (the dev server may be running on 4427 and `next build` would clobber `.next/`).

- [ ] **Step 5: Commit — SKIPPED (no commits this execution).**

---

### Task 12: Full verification

**Files:** none — verification only. Do NOT commit anything; report instead.

- [ ] **Step 1: Backend suite** - `dotnet test Eling.slnx --artifacts-path .bin-test` → green (note: `Eling.Core.Tests.BufferedAuditLoggerTests.RecordAsync_WhenTheChannelBacksUp_NeverDropsAnEntry` is known-flaky; if it fails on attempt 1, re-run it in isolation before treating it as a regression).
- [ ] **Step 1b: NO frontend build** - do NOT run `pnpm --dir src/frontend/Eling.Dashboard build` (dev server may run on 4427). Typecheck only: `pnpm --dir src/frontend/Eling.Dashboard exec tsc --noEmit` → clean.
- [ ] **Step 2: MCP smoke** — start the MCP host, call `commit_lease` twice on the same branch, confirm second returns holder info; call `commit_release`, confirm `commit_lease` then succeeds.
- [ ] **Step 3: Cross-worktree check** — lease in worktree 1, run `commit_check_conflict` in worktree 2 → green.
- [ ] **Step 4: Force-release smoke** — against the running owner host: GET `/api/commit-lease/active` shows the seeded lease; POST `/api/commit-lease/{leaseId}/release` releases it; it appears in `recently_released` with reason `forced`.
- [ ] **Step 5: Ask the user whether to clean up `.tmp`/session artifacts**.

---

## Self-Review Notes

- Spec coverage: store (Task 1), `commit_lease` (Task 2), `commit_release` (Task 3), `commit_list_active` (Task 4), `commit_check_conflict` (Task 5), `commit_wait` (Task 6), registration (Task 7), agent guidance (Task 8), git hook hardening (Task 9, SKIPPED by user decision 2026-11-12), dashboard backend endpoints + SSE + force-release (Task 10), dashboard page (Task 11). Turn queue and hunk/line granularity remain deferred per spec §Deferred.
- File-lease tools themselves are out of scope for this plan (only the shared store + commit tools). The store supports `CommitLeaseKind.File` so file-lease tools can be added in a follow-up plan without store changes. `commit_check_conflict` already consumes file leases from the store.
- Type consistency: `CommitLeaseRecord` field names used consistently across tasks; `ICommitLeaseStore` method names match.
- Release reasons now include `forced` (dashboard force-release) alongside `explicit` / `stale` / `post-commit`.
- HTTP surface is `/api/commit-lease/*` (`GET /api/commit-lease/active`, `GET /api/events/commit-lease`, `POST /api/commit-lease/{lease_id}/release`); payload field is `commit_leases`; store file is `commit-leases.json`; SSE payload matches the snapshot.
- Review Focus items each have a pinning task noted.