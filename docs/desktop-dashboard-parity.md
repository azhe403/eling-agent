# Dashboard ↔ Desktop Parity

**Date:** 2026-10-23
**Status:** Draft — traceability doc for parity planning
**Scope:** `src/frontend/Eling.Dashboard` (Next.js) vs `src/desktop/Eling.Desktop` (Avalonia)

## 1. Roles (clarified)

- **Dashboard (Next.js)** — runs first. Operations console for users who use *other* agents and treat Eling as an MCP server. Core value: observability & management (memories, codebase, db, logs, audit, judge, tools).
- **Desktop (Avalonia)** — implemented after dashboard is solid. Eling's own agentic chat client. Eling acts as the agent; MCP is bundled via the backend supervisor (auto-start). Core value: chat streaming + code editor.

Both share the same backend API. Target: **1:1 feature parity**, with Desktop additionally providing agentic chat + code editor.

## 2. Health snapshot

| Metric | Dashboard | Desktop |
|---|---|---|
| Build | ✓ (14 routes) | ✓ (1 warning: CS8602 in InlineFormattedTextBlock) |
| Tests | 2/2 pass (vitest) — minimal coverage | 95/95 pass (xunit) |
| Lint | 0 errors, 6 warnings (react-hooks) — cleaned 2026-10-23 | n/a (.NET) |
| Git status | 42 uncommitted changes (logs WIP) | clean |
| Last commit | 2026-10-22 | 2026-10-20 |

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
| Logs (tail live) | WIP | — | port → desktop |
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

1. ~~Commit/benahi logs WIP + fix 3 lint errors in dashboard (`use-log-tail.ts`).~~ ✅ Done (0 errors, 6 warnings remain).
2. Increase dashboard test coverage (currently 2 tests).
3. Port memory-ops (promote/copy/host browse) to dashboard. (Settings/backend URL stays desktop-only.)
4. Port observability (codebase/db/audit/logs/judge/tools) to desktop.
5. Strengthen desktop code editor.

## 6. Traceability

- Dashboard routes: `src/frontend/Eling.Dashboard/src/app/dashboard/` (home, workspaces, codebase, memories, create, databases, audit-trail, logs, judge, tools).
- Desktop views: `src/desktop/Eling.Desktop/Views/` (Chat, Folders, Memories, Settings, HostBrowse).
- Dashboard tests: `src/frontend/Eling.Dashboard/src/.../*.test.tsx` (2 tests).
- Desktop tests: `tests/Eling.Desktop.Tests/` (95 tests).
- Backend API: `src/backend/Eling.Backend` (shared by both).