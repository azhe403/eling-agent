# Dashboard ↔ Desktop Parity

**Date:** 2026-10-24
**Status:** Draft — traceability doc for parity planning (refreshed)
**Scope:** `src/frontend/Eling.Dashboard` (Next.js) vs `src/desktop/Eling.Desktop` (Avalonia)

## 1. Roles (clarified)

- **Dashboard (Next.js)** — runs first. Operations console for users who use *other* agents and treat Eling as an MCP server. Core value: observability & management (memories, codebase, db, logs, audit, judge, tools).
- **Desktop (Avalonia)** — implemented after dashboard is solid. Eling's own agentic chat client. Eling acts as the agent; MCP is bundled via the backend supervisor (auto-start). Core value: chat streaming + code editor.

Both share the same backend API. Target: **1:1 feature parity**, with Desktop additionally providing agentic chat + code editor.

## 2. Health snapshot

| Metric | Dashboard | Desktop |
|---|---|---|
| Build | ✓ (next build) | ✓ (1 warning: CS8602 in InlineFormattedTextBlock) |
| Tests | 2/2 pass (vitest) — minimal coverage | 95/95 pass (xunit) |
| Lint | 0 errors, 5 warnings (react-hooks) | n/a (.NET) |
| Git status | 1 uncommitted change (backend ServerInstructions.cs) | clean |
| Last commit | 2026-10-24 (workspace panel parity, db read cache, log viewer) | 2026-10-24 |

## 3. Feature matrix (target 1:1)

| Feature | Dashboard | Desktop | Action |
|---|---|---|---|
| Agentic chat (streaming) | — | ✓ | Desktop-only (plus) |
| Code editor | — | ✓ (minimal) | Desktop-only (plus) — strengthen |
| Workspaces / Folders | ✓ | ✓ | shared |
| Memories (CRUD, scope, filter, search) | ✓ | ✓ | shared |
| Promote / Copy memory | — | ✓ | port → dashboard |
| Host browse | — | ✓ | port → dashboard |
| Settings (backend URL) | — | ✓ | desktop-only (backend URL config) |
| Auto-start backend (supervisor) | external | ✓ | desktop-only by nature |
| Dashboard home (vitals, activity) | ✓ | — | port → desktop |
| Codebase search / viewer | ✓ | — | port → desktop |
| Databases (schema / query) | ✓ | — | port → desktop |
| Audit Trail | ✓ | — | port → desktop |
| Logs (tail live) | ✓ | — | port → desktop |
| Judge | ✓ | — | port → desktop |
| Tools catalog | ✓ | — | port → desktop |
| Unit tests | 2 | 95 | dashboard needs coverage |

## 4. Gaps

### Dashboard (to solidify first)
- 6 lint warnings (exhaustive-deps/unused vars) — non-blocking.
- Only 2 tests — needs coverage.
- Missing memory-ops (promote/copy/host browse) — to port from desktop. (Settings/backend URL is desktop-only.)

### Desktop (after dashboard solid)
- Missing observability (codebase, databases, audit, logs, judge, tools) — to port from dashboard.
- Code editor minimal (InlineFormattedTextBlock) — consider strengthening.

## 5. Action plan (ordered)

1. ~~Commit/benahi logs WIP + fix lint errors in dashboard (`use-log-tail.ts`).~~ ✅ Done (0 errors, 5 warnings remain).
2. ~~Workspace panel parity: desktop Folders now matches `/dashboard/workspaces`.~~ ✅ Done (commit f3a9c6b).
3. ~~Memory workspace sidebar parity: desktop now matches the dashboard workspace tree (flat/repo/folder grouping, checkboxes, expand/collapse, worktree rows, live/offline status).~~ ✅ Done (6 new tests, 101 passing).
4. ~~Workspace tree open/closed shape shared by both clients: one store behind `/api/coordinator/workspaces/expanded` (`{ expanded, collapsed }`), read on load and written on every toggle, so a node collapsed in one client is collapsed in the other.~~ ✅ Done (desktop 108 passing, backend 817, dashboard 35).
5. Increase dashboard test coverage (currently 2 tests).
6. Port memory-ops (promote/copy/host browse) to dashboard. (Settings/backend URL stays desktop-only.)
7. Port observability (codebase/db/audit/logs/judge/tools) to desktop.
8. Strengthen desktop code editor.

## 6. Traceability

- Dashboard routes: `src/frontend/Eling.Dashboard/src/app/dashboard/` (home, workspaces, codebase, memories, create, databases, audit-trail, logs, judge, tools).
- Desktop views: `src/desktop/Eling.Desktop/Views/` (Chat, Folders, Memories, Settings, HostBrowse).
- Dashboard tests: `src/frontend/Eling.Dashboard/src/.../*.test.tsx` (2 tests).
- Desktop tests: `tests/Eling.Desktop.Tests/` (95 tests).
- Backend API: `src/backend/Eling.Backend` (shared by both).