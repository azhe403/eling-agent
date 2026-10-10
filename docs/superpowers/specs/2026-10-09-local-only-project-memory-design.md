# Local-Only Project Memory (Central) — Design

**Date:** 2026-10-09
**Status:** Implemented (2026-10-10: Batch 1–4 landed; Core 191/191, Backend 557/557, Desktop 40/40; dashboard tsc+eslint clean)
**Scope:** `Eling.Core` (resolver + paths), `Eling.Backend` (policy store, MCP tools, recall merge), tests in `Eling.Core.Tests` + `Eling.Backend.Tests`
**Related:** `docs/superpowers/specs/2026-09-11-project-scope-policy-design.md` (machine-local opt-out), `docs/superpowers/plans/2026-08-26-pecut-010-scope-aware-memory.md` (scope-aware model), `docs/nested-memory.md`, `docs/recall-architecture.md`

> Paths below use placeholders. `DATA/eling` = user data root (`~/.local/share/eling` by default). `CONFIG/eling` = user config root (`~/.config/eling`). No real usernames or machine paths appear here.

---

## 1. Goal

Add a third memory tier: **project-local**. Project-scoped like `project`, but machine-local like `global` — never committed, never leaves this machine.

Gap today:

| Tier | Scope | Persisted | Shared via git |
|---|---|---|---|
| `project` | this project | `<repo>/.eling/memories/` | yes |
| `global` | all projects | `CONFIG/eling` + `DATA/eling` | no |
| `project-local` (new) | this project | `DATA/eling/projects/<shard>/` | no |

Use cases: machine-specific paths, local tokens/endpoints, worktree workflows, anything project-bound but unfit for commit.

---

## 2. Storage Topology

One shard per canonical project, beside existing central stores — never inside the repo, never inside the global DB:

```
DATA/eling/
  codebase/<name>-<hash>.db      (existing)
  projects.db                    (existing registry)
  projects/<name>-<hash>/        (new)
    memories/*.md
    memory.db + sidecars
```

Rules (from Pecut 10):

- Do NOT merge into one DB. No shared project-local DB. No project-local rows inside global DB.
- Every result returned outside its native store carries scope identity.

---

## 3. Canonical Project Identity (worktree sharing)

Key requirement: all worktrees of one repo share one shard.

- `ResolveCanonicalProjectRoot(cwd)`:
  1. Try git: `git rev-parse --show-toplevel` of the main worktree (via `--git-common-dir`), normalized. All linked worktrees resolve to the same main root.
  2. Fallback (non-git): normalized absolute `cwd` root (same semantics as today).
- `ResolveProjectLocalDir(canonicalRoot)` → `DATA/eling/projects/<name>-<hash16>/`, same `name-hash16` scheme as `ResolveCodebaseDbPath` (project folder name + 16-hex-char hash of normalized canonical root).
- Pure functions (no disk I/O except the git probe, which is injected/mockable for tests).

Non-goals: per-worktree isolation (explicitly sharing-full for now), orphan GC UI (log only; cleanup is a future task).

---

## 4. Scope Model

```csharp
enum MemoryScopeKind { Project, ProjectLocal, Global }
```

- `project` = shared, committed (`<repo>/.eling/`).
- `project-local` = same project boundary, machine-only (central shard).
- `global` = cross-project, machine-only (existing user scope).
- Same `MemoryId` may exist independently per tier — operations are scope-qualified.
- `merged` = `project-local` + `project` + `global` of the current canonical project only. Never other projects on the machine.

---

## 5. Write / Read Behavior

Explicit-only. Default save stays `project` (no behavior change for existing callers):

```
scope = project        → project (default, unchanged)
scope = project-local  → project-local shard (explicit)
scope = global         → global (unchanged)
scope = auto / omitted → project (unchanged)
```

Reads:

```
scope = project        → project only
scope = project-local  → project-local only
scope = global         → global only
scope = merged         → project-local + project + global, nearest-first
```

Merge order: `project-local > project > global`. Same normalized content across tiers dedups to the nearest tier (ULID dedup per tier preserved).

`memory_project_status` reports the extra tier (initialized when shard exists or parent project exists — shard creation is lazy, consent model unchanged).

---

## 6. MCP Contracts

- `memory_save`: `scope = project|project-local|global|auto` (default `project`). `project-local` never takes the ancestor `project` parameter (contradictory — throw).
- `memory_search` / `memory_recall`: `scope = project|project-local|global|merged` (default `merged`).
- `memory_get` / `memory_delete`: scope-qualified reference (`scope + id`); delete from aggregate view targets origin tier.
- Responses carry `scope: "project-local"` plus canonical project identity (same shape as existing `project` responses).

---

## 7. Security / Isolation

- Canonical project A never reads/writes canonical project B's shard.
- Dashboard aggregation is a view; MCP authority unchanged.
- No filesystem scanning for shards — resolution only via canonical root of the calling workspace.

---

## 8. Dashboard (minimal)

- Scope selector gains `Project-Local` alongside `Global` / `Project` / `All`. Inside a project view, a `Shared | Local | All` tier toggle switches tiers (All merges both, local wins ties).
- Badges distinguish `project-local` from `project` (shared) and `global`.
- Create from aggregate view requires explicit destination (never save to "All").

## 8b. Move Between Tiers (no dialog)

- Inline `Shared | Local` toggle on each project/project-local card. Click moves immediately (no confirmation dialog).
- Backend move endpoints work both directions: shared→local, local→shared, local→global. Global items keep the existing copy dialog.
- Move = copy + delete source (confirmed-saved first). A move preserves ULID and creation date (UpdatedAt refreshes); a copy mints fresh identity. An item lives in exactly one tier.
- `disabled`-policy workspaces: move targets follow save routing (project-bound ends local).

---

## 9. Tests Required

- Shard identity: same repo worktrees (main + 2 linked) → same shard; different repos → different shards.
- Non-git fallback: cwd root hashing stable.
- Write policy: default `project`, explicit `project-local`/`global`, `auto` → `project`.
- Isolation: A cannot read/write/delete B; A reads `global`.
- Merge: `merged` = local + project + global, order + dedup, no cross-project leak.
- No regression: existing `project`/`global` paths, scope-chain, policy `disabled` reroute.

---

## 10. Build / Validation

```
dotnet build Eling.slnx --artifacts-path .artifacts
dotnet test Eling.slnx --artifacts-path .artifacts
```

Targeted tests per edit during development; full suite only pre-commit. `git push` stays manual (never via tool).

---

## 11. Acceptance

Given repo R with worktrees W1 + W2, and unrelated repo S:

- Save `project-local` in W1 → visible in W2 `merged`, not in S.
- Save `project` in W1 → committed under `<repo>/.eling/memories/`, nothing new appears in repo working tree for `project-local` saves (`git status` clean).
- `merged` order: local hit ranks above same-content project/global hit.
- `disabled` policy reroutes project-bound saves to `project-local` (machine-only), never to `global` or a shared scope.
- Empty chain (no `.eling` anywhere, no decision yet): default saves go straight to `project-local` with a note offering `memory_init_project` for git sharing. Ancestor-scope workspaces keep the consent-gated adoption flow.
