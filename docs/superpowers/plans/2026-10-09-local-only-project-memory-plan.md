# Plan: Local-only Project Memory (central)

**Date:** 2026-10-09
**Status:** Complete (2026-10-10)
**Spec:** `docs/superpowers/specs/2026-10-09-local-only-project-memory-design.md`
**Related context:** moved out of `docs/` (session scratch does not belong here)

> Implements the spec. No absolute machine paths in docs.

---

## Architecture

```
[cwd] → ResolveCanonicalProjectRoot (git common, fallback cwd root)
        → ResolveProjectLocalDir → DATA/eling/projects/<name>-<hash>/
        → ScopedMemoryService (project-local + project + global)
        → MCP tools / recall merge / dashboard
```

## Component Order

1. [x] **Canonical + paths** (`Eling.Core/Scope/`): `ResolveCanonicalProjectRoot` + `ResolveProjectLocalDir`, unit tests (worktree sharing, repo isolation, non-git fallback)
2. [x] **Scope + service** (`Eling.Core/Memory/`): `MemoryScopeKind.ProjectLocal`, routing explicit-only, merge order `project-local > project > global`
3. [x] **MCP wiring** (`Eling.Backend/Mcp/Tools/`, recall): save/search/get/delete scope-qualified, `merged` default includes local
4. [x] **Dashboard + hygiene**: scope selector + badges, verify `git status` clean on local saves, docs touch-up

## Task Checklist (Batch 1 first)

- [x] Step 1: `ResolveCanonicalProjectRoot` pure + git probe seam, tests with fake worktrees
- [x] Step 2: `ResolveProjectLocalDir` shard path, tests same-repo-same-shard / diff-repo-diff-shard
- [x] Step 3: review gate — confirm merge order + `disabled`-policy behavior for `project-local` before Batch 2

## Verification

- Targeted tests per edit; full `dotnet test Eling.slnx` pre-commit only
- Acceptance per spec §11
