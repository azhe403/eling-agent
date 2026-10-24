# Commit Claim Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add the `commit_claim` / `commit_release` / `commit_list_active` / `commit_check_conflict` MCP tools plus the shared project-local claim store they rest on, so concurrent chats on one checkout+branch stop sweeping each other's uncommitted work into a single commit.

**Architecture:** A single JSON-backed claim store lives under the project-local `.eling/runtime/` directory (never committed, shared across worktrees of one repo), guarded by a cross-process mutex like `JsonDatabaseConnectionStore`. Records carry a `Kind` discriminator (`file` | `commit`) so the same store serves both file claim and commit claim; commit records add `checkout`, `branch`, and `intent`. Liveness is derived from `last_heartbeat` vs `ttl_seconds` (default 300) — stale records drop from the active list with no cleanup. MCP tools in `Eling.Backend.Mcp.Tools` consume the store plus `GitRepositoryResolver` (for the checkout root) and the current branch. `commit_check_conflict` folds commit-claim ownership, git dirty/staged state, and (when available) file claims on the requested paths into one stage/don't-stage verdict.

**Tech Stack:** .NET 10 (ASP.NET Core), ModelContextProtocol.Server, System.Text.Json, xUnit. Storage mirrors `JsonDatabaseConnectionStore` (mutex + atomic `.tmp` write).

**Spec:** `docs/superpowers/specs/2026-10-15-commit-claim-design.md` (depends on `docs/superpowers/specs/2026-10-11-file-claim-design.md` for the claim store; this plan builds the store first since it does not yet exist in the tree).

## Global Constraints

- Store path: `{globalAgentDir}/claims.json` (global, never committed; shared across all repos/worktrees on this machine). Records are scoped by `checkout` + `branch`, so a live claim in worktree A raises no conflict in worktree B despite shared storage. Mutex: `Global\eling-claims`.
- TTL default 300s; a record is live while `now - last_heartbeat <= ttl`. No background sweeper — staleness is filtered at read time.
- One live commit claim per `checkout` + `branch` at a time; a second claimant soft-fails and receives holder session, claim age, and intent. First-write-wins under the mutex.
- Soft phase only: tools return verdicts/warnings; no git hook is installed by this plan. Hook hardening is a follow-up.
- `session_id` is a required argument on every tool (the MCP client supplies it); the store never fabricates one.
- Conventional commits for any code change: `feat(backend): ...`.
- No AGENTS.md change in this phase (guidance lives in `ServerInstructions`).

## Review Focus

- **Stale claim resurrected by a heartbeat from a dead session:** `commit_release` and heartbeat updates must be scoped to the claiming `session_id`; a stray heartbeat must not revive a claim whose session is gone. Pinned in Task 1 (heartbeat only extends a claim owned by the same session).
- **Two claimants racing on the same mutex:** first-wins must hold even under concurrent calls; the loser must receive the winner's holder info, not an exception. Pinned in Task 2 (concurrent-claim test).
- **Commit lands but claim never released:** `commit_check_conflict` must surface a warning (not a hard error) when staged changes exist while the caller holds no claim; the design's strict mode is deferred. Pinned in Task 5 (staged-without-claim warning).
- **Different worktree, same repo:** a live claim in worktree 1 must raise no conflict in worktree 2 because `checkout` (resolved repo root) differs... actually the spec says same repo different worktree has separate indexes and is out of scope — but `checkout` is the resolved repo root which is SHARED across worktrees. Re-read: scope says hazard boundary is one checkout's working tree+index; different worktrees are out of scope. So `checkout` here means the *worktree root* (the actual checkout path), not the repo root. Pinned in Task 1 (claim key uses worktree root, not repo root) and Task 5 (cross-worktree no-conflict test).
- **TTL boundary off-by-one:** a claim exactly at `now - last_heartbeat == ttl` is stale. Pinned in Task 1 (boundary test).

---

### Task 1: Claim record, `IClaimStore`, and `JsonClaimStore`

**Files:**
- Create: `src/backend/Eling.Core/Claims/ClaimKind.cs`
- Create: `src/backend/Eling.Core/Claims/ClaimRecord.cs`
- Create: `src/backend/Eling.Core/Claims/IClaimStore.cs`
- Create: `src/backend/Eling.Core/Claims/JsonClaimStore.cs`
- Test: `tests/Eling.Backend.Tests/JsonClaimStoreTests.cs`

**Interfaces:**
- Consumes: a storage file path (global `claims.json` under the agent directory) injected via constructor; optional `IClaimChangeNotifier` (cross-process notify on mutation, mirroring `IMemoryChangeNotifier`); `System.Text.Json` via `JsonDefaults.Shared`.
- Produces:
  - `enum ClaimKind { File, Commit }`
  - `sealed record ClaimRecord(string ClaimId, string SessionId, ClaimKind Kind, string Checkout, string? Branch, DateTimeOffset Since, DateTimeOffset LastHeartbeat, int TtlSeconds, string[]? Paths, string? Intent, DateTimeOffset? ReleasedAt = null, string? ReleaseReason = null)`
  - `interface IClaimStore` with:
    - `Task<IReadOnlyList<ClaimRecord>> ListActiveAsync(string checkout, string? branch, CancellationToken ct = default)` (filters by checkout+branch when branch given; drops stale)
    - `Task<ClaimRecord?> TryClaimAsync(ClaimRecord candidate, CancellationToken ct = default)` → returns the existing live conflicting claim when one exists, else stores candidate and returns null
    - `Task<bool> ReleaseAsync(string sessionId, ClaimKind? kind, CancellationToken ct = default)` → removes all claims owned by that session (optionally filtered by kind), moving them to the recently-released list with reason `explicit`; returns true if any removed
    - `Task<bool> HeartbeatAsync(string claimId, string sessionId, CancellationToken ct = default)` → extends `LastHeartbeat` only if owned by sessionId; returns false otherwise
    - `Task<IReadOnlyList<ClaimRecord>> ListRecentlyReleasedAsync(string checkout, int limit = 20, CancellationToken ct = default)` → released claims for the checkout, newest first, capped
    - `Task<IReadOnlyList<ClaimRecord>> ListAllAsync(CancellationToken ct = default)` (raw, for tests)
    - New interface `IClaimChangeNotifier { Task NotifyAsync(CancellationToken ct = default); }` with `HttpCoordinatorClaimChangeNotifier` impl (mirroring `HttpCoordinatorMemoryChangeNotifier`) so MCP-side mutations reach the dashboard owner.

- [ ] **Step 1: Write the failing test** — `JsonClaimStoreTests` with a temp dir. Tests: (a) claim then `ListActiveAsync` returns it; (b) second `TryClaimAsync` with same checkout+branch+kind=Commit returns the first record (first-wins) and leaves store unchanged; (c) different `Checkout` → no conflict; (d) `ReleaseAsync(sessionId)` removes it; (e) a record with `LastHeartbeat = now - (ttl+1)s` is absent from `ListActiveAsync`; (f) boundary: `now - last_heartbeat == ttl` is stale; (g) `HeartbeatAsync` with wrong sessionId returns false and does not extend; (h) `HeartbeatAsync` with right sessionId extends and returns true. Use a short TTL (e.g. 60s) and inject `TimeProvider`? If `JsonClaimStore` takes `TimeProvider? timeProvider = null` defaulting to `TimeProvider.System`, tests pass a fake. Decide: yes, inject `TimeProvider`.

- [ ] **Step 2: Run to verify red** — `dotnet test tests/Eling.Backend.Tests/Eling.Backend.Tests.csproj --filter "FullyQualifiedName~JsonClaimStoreTests" --artifacts-path .bin-test`.

- [ ] **Step 3: Implement** — `ClaimRecord` is a record; `JsonClaimStore` mirrors `JsonDatabaseConnectionStore` (cross-process `Mutex` named `Global\eling-claims`, atomic `.tmp` write, swallows read errors returning []). Store holds two collections: active claims and recently-released (capped at 50 per checkout). `TryClaimAsync` under the gate: load, find a live claim with same `Checkout`+`Branch`+`Kind` (for commit kind, branch equality; for file kind, path intersection — but Phase 1 of this plan only needs commit; implement path-intersection for file kind too for future use, using `Paths` overlap), if found return it; else append candidate, save, return null. `ListActiveAsync` filters stale by `TimeProvider.GetUtcNow()` and moves stale records to recently-released with reason `stale`. `ReleaseAsync` moves owned claims to recently-released with reason `explicit` (or `post-commit` when called from hook path). `HeartbeatAsync` updates only when owner matches. `ListRecentlyReleasedAsync` returns newest-first, capped.

- [ ] **Step 4: Run to verify green** — same filter, all tests pass.

- [ ] **Step 5: Commit** — `git add src/backend/Eling.Core/Claims tests/Eling.Backend.Tests/JsonClaimStoreTests.cs && git commit -m "feat(core): add project-local claim store with TTL liveness"`.

---

### Task 2: `commit_claim` MCP tool

**Files:**
- Create: `src/backend/Eling.Backend/Mcp/Tools/CommitClaimTool.cs`
- Test: `tests/Eling.Backend.Tests/CommitClaimToolTests.cs`

**Interfaces:**
- Consumes: `IClaimStore`, `GitRepositoryResolver` (static), `TimeProvider`.
- Produces: tool `commit_claim(session_id, branch?, intent?, paths?)` returning either `{ claim_id, since }` on success or `{ holder_session_id, claim_age_seconds, intent }` on soft-fail. When `branch` is null/blank, resolve via `git`? The store needs a branch. Resolve branch by running `git rev-parse --abbrev-ref HEAD` in the checkout, OR accept null and treat null branch as "current" resolved inside the tool. Simpler: tool requires `branch`? Spec says `commit_claim(session_id, branch, intent?, paths?)` with branch required. Keep branch required (non-blank) to match spec; document that callers pass the current branch. Checkout resolved from `Environment.CurrentDirectory` via `GitRepositoryResolver.ResolveRepositoryRoot` (worktree root — see Review Focus).

- [ ] **Step 1: Write the failing test** — `CommitClaimToolTests` with a temp store dir and a fake `TimeProvider`. Tests: (a) first claim succeeds and returns `claim_id`; (b) second claim on same checkout+branch returns holder info and does not overwrite; (c) claim on different checkout succeeds; (d) `paths` and `intent` round-trip into the stored record.

- [ ] **Step 2: Run to verify red**.

- [ ] **Step 3: Implement** — generate `claim_id` via `System.Ulid.NewUlid()`. Resolve `checkout` = `GitRepositoryResolver.ResolveRepositoryRoot(cwd)` ?? cwd (fallback so non-git dirs still work, keyed by cwd). Build `ClaimRecord(Kind=Commit, ...)`, call `TryClaimAsync`; on null return success DTO, else soft-fail DTO.

- [ ] **Step 4: Run to verify green**.

- [ ] **Step 5: Commit** — `git add ... && git commit -m "feat(backend): add commit_claim MCP tool"`.

---

### Task 3: `commit_release` MCP tool

**Files:**
- Create: `src/backend/Eling.Backend/Mcp/Tools/CommitReleaseTool.cs`
- Test: `tests/Eling.Backend.Tests/CommitReleaseToolTests.cs`

**Interfaces:**
- Consumes: `IClaimStore`.
- Produces: tool `commit_release(session_id)` → `{ released: bool }`, removing all commit claims owned by that session.

- [ ] **Step 1: Write the failing test** — claim then release returns `released=true` and list is empty; release with no claim returns `released=false`; release by a different session returns `released=false` and leaves the claim.

- [ ] **Step 2: Run to verify red**.

- [ ] **Step 3: Implement** — call `ReleaseAsync(sessionId, ClaimKind.Commit)`.

- [ ] **Step 4: Run to verify green**.

- [ ] **Step 5: Commit**.

---

### Task 4: `commit_list_active` MCP tool

**Files:**
- Create: `src/backend/Eling.Backend/Mcp/Tools/CommitListActiveTool.cs`
- Test: `tests/Eling.Backend.Tests/CommitListActiveToolTests.cs`

**Interfaces:**
- Consumes: `IClaimStore`, `GitRepositoryResolver`.
- Produces: tool `commit_list_active(branch?)` → list of `{ holder_session_id, since, claim_age_seconds, intent, paths }` for live commit claims on this checkout (filtered by branch when given).

- [ ] **Step 1: Write the failing test** — two claims on different checkouts both visible when querying without branch? Actually list is scoped to current checkout. Test: claim on current checkout appears; stale claim does not; branch filter works.

- [ ] **Step 2–5: red → implement → green → commit**.

---

### Task 5: `commit_check_conflict` MCP tool

**Files:**
- Create: `src/backend/Eling.Backend/Mcp/Tools/CommitCheckConflictTool.cs`
- Test: `tests/Eling.Backend.Tests/CommitCheckConflictToolTests.cs`

**Interfaces:**
- Consumes: `IClaimStore`, `GitRepositoryResolver`, and shell-out to `git status --porcelain` + `git diff --cached --name-only` (reuse existing git helpers if any; otherwise a small `GitStatusReader`).
- Produces: tool `commit_check_conflict(session_id, paths[])` → `{ verdict: "green" | "red", holder?: {...}, staged_without_claim: bool, conflicting_file_claims?: [...] }`.
  - Red when a *foreign* live commit claim exists on same checkout+branch.
  - `staged_without_claim=true` (warning) when staged files exist and the caller holds no commit claim of its own.
  - File-claim folding: when the claim store holds live `File` claims on any of `paths` owned by another session, list them in `conflicting_file_claims` and mark red. (Store already supports this via `ClaimKind.File`.)

- [ ] **Step 1: Write the failing test** — (a) no claims + clean tree → green; (b) foreign commit claim → red with holder info; (c) staged files while caller holds no claim → green verdict but `staged_without_claim=true`; (d) foreign file claim on a requested path → red with that claim listed; (e) cross-worktree: claim in worktree 1 does not make worktree 2 red.

- [ ] **Step 2: Run to verify red**.

- [ ] **Step 3: Implement** — read git state via `Process` (git) with a 5s timeout; parse porcelain output. Tolerate missing git (treat as no git state, still evaluate claims).

- [ ] **Step 4: Run to verify green**.

- [ ] **Step 5: Commit**.

---

### Task 6: `commit_wait` MCP tool (server-side wait)

**Files:**
- Create: `src/backend/Eling.Backend/Mcp/Tools/CommitWaitTool.cs`
- Test: `tests/Eling.Backend.Tests/CommitWaitToolTests.cs`

**Interfaces:**
- Consumes: `IClaimStore`, `TimeProvider`.
- Produces: tool `commit_wait(session_id, total_timeout_seconds = 180, initial_interval_seconds = 5)` → blocks until no foreign live commit claim exists on the same checkout+branch (or total timeout), polling with exponential backoff: 5s → 10s → 20s (cap). Returns `{ verdict: "green" | "red", waited_seconds, holder? }`. On timeout returns `red` with the current holder info. Does not take the slot; caller must `commit_claim` after green.

- [ ] **Step 1: Write the failing test** — `CommitWaitToolTests`: (a) no foreign claim → returns green immediately with `waited_seconds` ~0; (b) foreign claim released mid-wait → returns green after release, `waited_seconds` > 0; (c) timeout → returns red with holder info and `waited_seconds` ~timeout.

- [ ] **Step 2: Run to verify red**.

- [ ] **Step 3: Implement** — loop: check store for foreign live claim; if none return green; if timeout reached return red; else `Task.Delay(2000, ct)` and repeat. Respect cancellation token.

- [ ] **Step 4: Run to verify green**.

- [ ] **Step 5: Commit** — `git add ... && git commit -m "feat(backend): add commit_wait server-side blocking wait"`.

---

### Task 7: DI registration and McpHostBuilder wiring

**Files:**
- Modify: `src/backend/Eling.Backend/Bootstrap/McpHostBuilder.cs` (register `IClaimStore` → `JsonClaimStore` pointed at `Path.Combine(CentralAgentDirectory.Resolve(), "claims.json")`, plus `TimeProvider.System`). Mirror the `JsonDatabaseConnectionStore` registration pattern.
- Modify: `src/backend/Eling.Backend/Bootstrap/AppServices.cs` if it aggregates shared services used by both MCP and dashboard hosts (check whether `shared` object carries stores like `Registry`/`Broadcaster`; mirror that pattern so the dashboard host shares one store).

- [ ] **Step 1: Inspect** `AppServices.cs` to see the shared-services pattern; add `ClaimStore` to it if applicable.
- [ ] **Step 2: Register** in both `McpHostBuilder` and `DashboardServices` (if dashboard exposes tools).
- [ ] **Step 3: Build** — `dotnet build src/backend/Eling.Backend/Eling.Backend.csproj` → clean.
- [ ] **Step 4: Commit**.

---

### Task 8: Agent guidance in `ServerInstructions`

**Files:**
- Modify: `src/backend/Eling.Backend/Mcp/ServerInstructions.cs` — append a section (as a new entry in `Sections`) mirroring the spec's "Agent guidance": must call `commit_check_conflict` before `git add`; on red, stop and report; on green, must call `commit_claim` before first staging; must call `commit_release` immediately after commit lands; heartbeat on every step change.

- [ ] **Step 1: Add the section** (verbatim-ish from spec §Agent guidance).
- [ ] **Step 2: Verify** `ServerInstructions.Default` contains the new text (unit test in existing `ServerInstructions` tests if any; else add a small assertion test).
- [ ] **Step 3: Commit**.

---

### Task 9: Git hook hardening (opt-in per repo)

**Files:**
- Create: `src/backend/Eling.Backend/Hooks/CommitClaimPreCommitHook.cs` (or a shell script template `hooks/pre-commit` shipped as embedded resource)
- Create: `src/backend/Eling.Backend/Hooks/CommitClaimPostCommitHook.cs`
- Create: `src/backend/Eling.Backend/Mcp/Tools/CommitHookInstallTool.cs` (MCP tool `commit_hook_install` / `commit_hook_uninstall`)
- Test: `tests/Eling.Backend.Tests/CommitHookTests.cs`

**Interfaces:**
- `pre-commit`: resolves current checkout+branch; if a foreign live commit claim exists → print holder session, claim age, intent + "retry later or wait for the claim to release", exit 1; if staged paths intersect a foreign live file claim → exit 1 with the conflicting claim. Otherwise exit 0.
- `post-commit`: removes the committer's commit claim (reason `post-commit`) from the store; best-effort, never fails the commit.
- `commit_hook_install(repo_root)` writes both hooks into `{repo_root}/.git/hooks/` (or worktree hooks dir) with executable bit; `commit_hook_uninstall` removes them. Idempotent; refuses to overwrite a non-Eling hook without `--force`.

- [ ] **Step 1: Write the failing test** — `CommitHookTests`: (a) pre-commit exits 1 when foreign live claim exists; (b) exits 0 when no foreign claim; (c) post-commit clears the committer's claim; (d) install is idempotent and does not clobber a foreign hook.

- [ ] **Step 2: Run to verify red**.

- [ ] **Step 3: Implement** — hook as a small console entry point (or script invoking the MCP host's `commit_hook_check` subcommand). Reuse `IClaimStore` + `GitRepositoryResolver`. Keep hook fast (<100ms) by reading the JSON directly.

- [ ] **Step 4: Run to verify green**.

- [ ] **Step 5: Commit** — `git add ... && git commit -m "feat(backend): add opt-in git hooks for commit claim enforcement"`.

---

### Task 10: Dashboard backend — claims SSE (no polling)

**Files:**
- Create: `src/backend/Eling.Backend/Dtos/ClaimDto.cs`
- Create: `src/backend/Eling.Backend/Services/ClaimBroadcaster.cs` (mirrors `RuntimeMetricsBroadcaster`: subscriber-gated pump, or event-driven push on store change)
- Create: `src/backend/Eling.Backend/Controllers/ClaimsController.cs`
- Modify: `src/backend/Eling.Backend/Bootstrap/DashboardServices.cs` — register `ClaimBroadcaster` (and wire store change → `Publish`)
- Modify: `src/backend/Eling.Backend/Bootstrap/DashboardRoutes.cs` — map `GET /api/events/claims` (SSE) and keep `GET /api/claims/active` (snapshot)
- Test: `tests/Eling.Backend.Tests/ClaimsEndpointTests.cs`, `tests/Eling.Backend.Tests/ClaimBroadcasterTests.cs`

**Interfaces:**
- Consumes: `IClaimStore`, `GitRepositoryResolver`, `ProjectContext`, `IMemoryChangeNotifier`-style coordinator notifier for cross-process claim changes.
- Produces: `GET /api/events/claims` (SSE, owner-mode) — `data: connected`, then frames `{ checkout, branch, claims, recently_released }` on every store change (claim/release/heartbeat/stale sweep), reconnect resync via initial snapshot. `GET /api/claims/active` remains for one-shot snapshot.

- [ ] **Step 1: Write the failing test** — `ClaimsEndpointTests`: (a) seed a claim via `IClaimStore`, GET returns it with correct fields; (b) stale claim (heartbeat expired) is absent; (c) different checkout's claims are absent; (d) non-owner host returns 404/not registered.

- [ ] **Step 2: Run to verify red**.

- [ ] **Step 3: Implement** — DTO record; controller reads `IClaimStore.ListActiveAsync(checkout, branch)` and projects to DTO with `claim_age_seconds = (now - since).TotalSeconds`. Register route in `DashboardRoutes.MapClaimsEndpoints` inside the owner block.

- [ ] **Step 4: Run to verify green**.

- [ ] **Step 5: Commit** — `git add ... && git commit -m "feat(backend): expose active claims over /api/claims/active"`.

---

### Task 11: Dashboard frontend — commit claims panel

**Files:**
- Create: `src/frontend/Eling.Dashboard/src/components/commit-claims-panel.tsx`
- Modify: `src/frontend/Eling.Dashboard/src/app/dashboard/page.tsx` (or wherever the dashboard home renders panels) — mount the panel.
- Test: (typecheck only; no unit test infra for components in this repo) — `pnpm --dir src/frontend/Eling.Dashboard exec tsc --noEmit`.

**Interfaces:**
- Consumes: `GET /api/claims/active` (Task 8).
- Produces: a panel with two sections — "Active" (live commit claims: holder session truncated, claim age, intent, paths) and "Recently released" (release history: timestamp + reason `explicit`/`stale`/`post-commit`, intent, paths). Subscribes via `EventSource("/api/events/claims")` with reconnect resync (mirror `use-codebase-rebuild-sse.ts`); no polling. Empty states for both. Stale claims move from Active to Recently released within one TTL.

- [ ] **Step 1: Read the frontend agent rules' referenced docs** — from `src/frontend/Eling.Dashboard/`, skim `node_modules/next/dist/docs/` client-component guidance before writing.

- [ ] **Step 2: Write the component** — fetch on mount + 5s interval, cleanup on unmount; render a table (holder, age, intent, paths) using existing dashboard styling tokens; show a small badge for claim kind if file claims are ever surfaced.

- [ ] **Step 3: Mount** the panel on the dashboard home (or a dedicated "Claims" section).

- [ ] **Step 4: Typecheck** — `pnpm --dir src/frontend/Eling.Dashboard exec tsc --noEmit` → clean.

- [ ] **Step 5: Commit** — `git add ... && git commit -m "feat(dashboard): show active commit claims panel"`.

---

### Task 12: Full verification

**Files:** none — verification only.

- [ ] **Step 1: Backend suite** - `dotnet test Eling.slnx --artifacts-path .bin-test` → green.
- [ ] **Step 1b: Frontend build** - `pnpm --dir src/frontend/Eling.Dashboard build` → clean.
- [ ] **Step 2: MCP smoke** — start the MCP host, call `commit_claim` twice on the same branch, confirm second returns holder info; call `commit_release`, confirm `commit_claim` then succeeds.
- [ ] **Step 3: Cross-worktree check** — claim in worktree 1, run `commit_check_conflict` in worktree 2 → green.
- [ ] **Step 4: Ask the user whether to clean up `.tmp`/session artifacts**.

---

## Self-Review Notes

- Spec coverage: store (Task 1), `commit_claim` (Task 2), `commit_release` (Task 3), `commit_list_active` (Task 4), `commit_check_conflict` (Task 5), `commit_wait` (Task 6), registration (Task 7), agent guidance (Task 8), git hook hardening (Task 9), dashboard backend endpoint (Task 10), dashboard panel (Task 11). Turn queue and hunk/line granularity remain deferred per spec §Deferred.
- File-claim tools themselves are out of scope for this plan (only the shared store + commit tools). The store supports `ClaimKind.File` so file-claim tools can be added in a follow-up plan without store changes. `commit_check_conflict` already consumes file claims from the store.
- Type consistency: `ClaimRecord` field names used consistently across tasks; `IClaimStore` method names match.
- Review Focus items each have a pinning task noted.