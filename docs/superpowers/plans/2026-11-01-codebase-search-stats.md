# Codebase Search Stats Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add search execution stats (tookMs, files, chunks, workspaces, workspacesWithHits) to the codebase search backend response, render a compact stats ribbon below the search box, and display match count badges on the search tabs.

**Architecture:** Backend `CodebaseEndpoints.cs` calculates and returns a `stats` object in `/api/codebase/search` for both `search` (content) and `files` (path/file) modes. Frontend `searchTabs.ts` persists `lastStats` per tab in localStorage. A new `SearchStatsRibbon.tsx` component renders the compact ribbon under the search box, and `SearchTabsBar.tsx` displays the match count badge.

**Tech Stack:** ASP.NET Core (.NET 9), C#, Next.js 16 (App Router), React 19, TypeScript, Vitest, Tailwind CSS, Lucide icons.

**Spec:** `docs/superpowers/specs/2026-11-01-codebase-search-stats-design.md`

## Global Constraints

- Backend response backward compatibility: existing fields (`query`, `limit`, `scope`, `mode`, `results`) remain unchanged; `stats` is an additive object.
- Stats block is omitted in top mode (empty query).
- When building backend during dev, build to a separate output directory to avoid locking running processes (`-p:ElingSkipDashboard=true`).
- Frontend components must follow existing shadcn/ui and Tailwind styles.

## Review Focus

- Empty query ("top" mode) should never display the ribbon or populate `lastStats`.
- Switching between tabs should restore the ribbon and badge specific to the selected tab.
- Results with 0 matches should display "0 matches" in the ribbon and a "0" badge on the tab.
- Closing or adding tabs must preserve existing tabs' `lastStats`.
- Scope toggling (e.g. content -> path) resets results and clears stale stats until the new search runs.

---

### Task 1: Backend Search Stats Payload

**Files:**
- Modify: `src/backend/Eling.Backend/Endpoints/CodebaseEndpoints.cs`
- Test: `tests/Eling.Backend.Tests/CodebaseEndpointsTests.cs` (or create if needed)

**Interfaces:**
- Produces: `stats: { tookMs, hits, files, chunks, workspaces, workspacesWithHits }` in `/api/codebase/search` response JSON.

- [x] **Step 1: Write backend unit test verifying `stats` payload in search responses**
- [x] **Step 2: Run test to verify failure**
- [x] **Step 3: Modify `CodebaseEndpoints.cs` to add `stats` in `SearchAsync` for `searchScope != Content` and `Content` modes**
- [x] **Step 4: Run test to verify it passes**
- [x] **Step 5: Commit backend changes**

---

### Task 2: Frontend Tab State & Serialization (`searchTabs.ts`)

**Files:**
- Modify: `src/frontend/Eling.Dashboard/src/app/dashboard/codebase/searchTabs.ts`
- Modify: `src/frontend/Eling.Dashboard/src/app/dashboard/codebase/searchTabs.test.ts`

**Interfaces:**
- Produces: `export type SearchStats = { tookMs: number; hits: number; files: number; chunks: number; workspaces: number; workspacesWithHits: number; mode?: "search" | "files" }`
- Updates: `SearchTab` has `lastStats?: SearchStats`

- [x] **Step 1: Write unit tests in `searchTabs.test.ts` for `lastStats` loading, saving, and updating**
- [x] **Step 2: Run tests via `pnpm test` to verify failure**
- [x] **Step 3: Update `searchTabs.ts` with `SearchStats` type, `lastStats` field in `SearchTab`, `createTab`, and `loadTabs` validation**
- [x] **Step 4: Run tests via `pnpm test` to verify it passes**
- [x] **Step 5: Commit tab state changes**

---

### Task 3: Tab Bar Match Count Badge (`SearchTabsBar.tsx`)

**Files:**
- Modify: `src/frontend/Eling.Dashboard/src/app/dashboard/codebase/SearchTabsBar.tsx`

**Interfaces:**
- Consumes: `tab.lastStats?.hits` from `SearchTab`

- [x] **Step 1: Update `SearchTabsBar.tsx` to render a match count badge beside the label and workspace count**
- [x] **Step 2: Verify tab bar renders cleanly and match badge shows only when `lastStats` is present and query is not empty**
- [x] **Step 3: Commit tab bar changes**

---

### Task 4: Stats Ribbon Component (`SearchStatsRibbon.tsx`)

**Files:**
- Create: `src/frontend/Eling.Dashboard/src/app/dashboard/codebase/SearchStatsRibbon.tsx`

**Interfaces:**
- Produces: `<SearchStatsRibbon stats={SearchStats} scope={SearchScope} limit={number} />`

- [x] **Step 1: Create `SearchStatsRibbon.tsx` with ribbon layout (Option A): matches, files, chunks, execution time, workspaces, and scope/limit**
- [x] **Step 2: Add visual styling matching Tailwind tokens and responsive wrapping**
- [x] **Step 3: Commit `SearchStatsRibbon.tsx`**

---

### Task 5: Integration in CodebaseList (`CodebaseList.tsx`)

**Files:**
- Modify: `src/frontend/Eling.Dashboard/src/app/dashboard/codebase/CodebaseList.tsx`

**Interfaces:**
- Consumes: `SearchStatsRibbon`, `data.stats` from `/api/codebase/search`, and `updateTab`

- [x] **Step 1: Update `doSearch` in `CodebaseList.tsx` to update `activeTab.lastStats` from `data.stats`**
- [x] **Step 2: Clear `lastStats` when query is empty or cleared**
- [x] **Step 3: Mount `<SearchStatsRibbon />` directly below the query hint and above the results list**
- [x] **Step 4: Verify tab switching updates the displayed ribbon and query**
- [x] **Step 5: Commit `CodebaseList.tsx` changes**

---

### Task 6: Verification & Quality Checks

- [x] **Step 1: Run frontend tests via `pnpm test`**
- [x] **Step 2: Run frontend build via `pnpm build`**
- [x] **Step 3: Run backend build and tests**
