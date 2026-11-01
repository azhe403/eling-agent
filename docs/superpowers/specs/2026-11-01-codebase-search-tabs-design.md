# Codebase Search Tabs Design

**Date:** 2026-11-01  
**Status:** Approved  
**Topic:** Codebase Search Multi-Tab Support with Per-Tab Workspace Scope

## 1. Intent & Why
Allow developers using the Eling Dashboard to conduct multiple independent searches in parallel on `/dashboard/codebase` without losing context, query text, search scope, or workspace selections when switching between active investigations.

## 2. Architecture & Data Model
- **Storage:** Client-side via browser `localStorage` under `eling.codebase.tabs`.
- **URL Synchronization:** The active tab and its active configuration are reflected in the URL parameters:
  `?tab={id}&project={root1}&project={root2}&search={content|path|file}&q={query}`
- **Tab Model:**
  ```typescript
  export type SearchTab = {
    id: string
    query: string
    scope: SearchScope
    selection: WorkspaceSelection // { kind: "all" } | { kind: "workspaces", roots: string[] }
  }
  ```
- **Tab Lifecycle:**
  - On fresh load: Reads saved tabs from `localStorage`. If empty, creates an initial default tab inheriting any existing URL params or legacy `searchPrefs`.
  - Adding a tab (`+`): Creates a new tab inheriting current workspace selection with an empty query.
  - Closing a tab (`×`): Closes the targeted tab; if the closed tab was active, activates an adjacent tab. At least 1 tab is always preserved.
  - Switching tabs: Updates active tab in state, syncs URL params, updates query, searchScope, and workspace selection.

## 3. UI Components
- `SearchTabsBar`: Horizontal scrollable bar with browser/editor style tabs, close buttons (`×`), and a new tab button (`+`). Rendered immediately above the search input form.
- `CodebaseList`: Orchestrates tab state, synchronization with URL, and persists changes to `localStorage`.
