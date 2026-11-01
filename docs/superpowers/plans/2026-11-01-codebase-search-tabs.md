# Codebase Search Tabs Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Implement browser-style tabs for the Codebase search page in Eling Dashboard, saving search state (query, searchScope, workspaceSelection) per tab in localStorage and synchronizing the active tab with the URL.

**Architecture:** A lightweight pure module `searchTabs.ts` manages tab state mutations and localStorage serialization. `scope.ts` adds URL parsing for the `tab` parameter. `SearchTabsBar.tsx` renders the horizontal tab strip. `CodebaseList.tsx` ties the tabs to the search inputs, workspace selection panel, and URL writer.

**Tech Stack:** Next.js 16 (App Router), React 19, TypeScript, Vitest, Tailwind CSS, Lucide icons.

**Spec:** `docs/superpowers/specs/2026-11-01-codebase-search-tabs-design.md`

## Global Constraints
- Pure functions and immutability for tab data manipulation.
- LocalStorage keys must follow `eling.codebase.*` naming convention (`eling.codebase.tabs`).
- At least 1 tab must always remain present (closing the last tab resets it to a blank search instead of deleting).
- Keep code consistent with shadcn/ui and existing dashboard styling conventions.

---

### Task 1: Tab Model and Storage Logic (`searchTabs.ts`)

**Files:**
- Create: `src/frontend/Eling.Dashboard/src/app/dashboard/codebase/searchTabs.ts`
- Test: `src/frontend/Eling.Dashboard/src/app/dashboard/codebase/searchTabs.test.ts`

- [ ] **Step 1: Write unit tests in `searchTabs.test.ts`**
- [ ] **Step 2: Run test to verify it fails**
- [ ] **Step 3: Implement `searchTabs.ts`**
- [ ] **Step 4: Run test to verify it passes**

### Task 2: URL Parameter Integration (`scope.ts`)

**Files:**
- Modify: `src/frontend/Eling.Dashboard/src/app/dashboard/codebase/scope.ts`

- [ ] **Step 1: Add `tabFromParams(params: URLSearchParams)` and `tabParam(tabId: string)` in `scope.ts`**
- [ ] **Step 2: Verify `selectionFromParams`, `searchScopeFromParams`, and URL builders**

### Task 3: Tab Bar Component (`SearchTabsBar.tsx`)

**Files:**
- Create: `src/frontend/Eling.Dashboard/src/app/dashboard/codebase/SearchTabsBar.tsx`

- [ ] **Step 1: Implement `SearchTabsBar` with tab list, active indicator, close button, and add button**

### Task 4: Integration with CodebaseList (`CodebaseList.tsx`)

**Files:**
- Modify: `src/frontend/Eling.Dashboard/src/app/dashboard/codebase/CodebaseList.tsx`

- [ ] **Step 1: Wire `SearchTabsBar`, active tab state, per-tab query/scope/selection**
- [ ] **Step 2: Connect tab switching to search execution and URL updating**
- [ ] **Step 3: Connect close tab and add tab actions**

### Task 5: Verification & Quality Checks

- [ ] **Step 1: Run frontend tests via `pnpm test`**
- [ ] **Step 2: Run frontend build via `pnpm build`**
