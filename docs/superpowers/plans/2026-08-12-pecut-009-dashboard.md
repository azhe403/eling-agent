# Dashboard Implementation Plan (Pecut 9)

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace the create-next-app boilerplate in `Eling.Dashboard` with a minimal memory dashboard that talks ONLY to the `Eling.Server` REST API (Pecut 7): list/search/delete memories, create a new memory, view a memory.

**Architecture:** Next.js 16 App Router + React 19 + TypeScript + Tailwind v4. A thin typed API client (`src/lib/api.ts`) wraps `fetch` against the REST API. Pages are client components (forms, interactive delete/search). No data access to SQLite/markdown from the browser — the dashboard is a pure API consumer.

**Tech Stack:** Next 16.3.0, React 19.2.8, Tailwind v4, `@base-ui/react` (already a dependency), `lucide-react` (already a dependency), pnpm 11.

## Global Constraints

- Work ONLY in `src/frontend/Eling.Dashboard/` (plus nothing else — do not touch `src/backend/`).
- **CRITICAL — READ THE DOCS FIRST:** The Next.js version here has breaking changes vs. training data. Before writing ANY code, read the relevant guides under `node_modules/next/dist/docs/` (resolved from the dashboard directory, e.g. `src/frontend/Eling.Dashboard/node_modules/next/dist/docs/`) — routing, data fetching, and any `next dev`-generated agent notes. Heed deprecation notices.
- No new npm dependencies unless strictly necessary — `@base-ui/react` and `lucide-react` already cover UI needs.
- API base URL: read `NEXT_PUBLIC_ELING_API_URL`, default `http://localhost:5275`. API JSON is camelCase (Pecut 7).
- Keep existing `globals.css`/theme; do not introduce a CSS framework change.

---

### Task 1: API client + types

**Files:**
- Create: `src/frontend/Eling.Dashboard/src/lib/api.ts` (client + types in one file — small enough)

- [ ] **Step 1: Define the types** mirroring the API contract (camelCase):

```ts
type MemoryType = "fact" | "concept" | "event" | "decision" | "pattern" | "note";
type MemoryStatus = "active" | "archived" | "deleted";

interface Memory {
  id: string;
  type: MemoryType;
  content: string;
  status: MemoryStatus;
  source: string | null;
  tags: string[];
  createdAt: string;
  updatedAt: string;
}

interface SaveMemoryRequest {
  type?: MemoryType;
  content: string;
  tags?: string[];
  source?: string | null;
}
```

- [ ] **Step 2: Implement the client.** Small `fetch` wrapper: `listMemories(status?)`, `getMemory(id)`, `saveMemory(input)`, `deleteMemory(id)`, `searchMemories(q, limit?)`, `rebuildIndex()`. Throw a typed error (status + message from response body) on non-2xx. Use the `NEXT_PUBLIC_ELING_API_URL` base with default `http://localhost:5275`.

---

### Task 2: Home page — list, search, delete

**Files:**
- Rewrite: `src/frontend/Eling.Dashboard/src/app/page.tsx`

- [ ] **Step 1: Implement the page.** Client component:
  - Loads memory list (and/or search results when a query is entered).
  - Search box (debounced or submit-based — pick the simplest that works), status filter, refresh button.
  - Table/cards of memories: content preview, type badge, tags, created date.
  - Row action: Delete (with confirm) → calls `deleteMemory` → refreshes list.
  - Empty state + loading state. Keep styling consistent with the existing zinc/dark theme.

---

### Task 3: Create + detail pages

**Files:**
- Create: `src/frontend/Eling.Dashboard/src/app/memories/new/page.tsx`
- Create: `src/frontend/Eling.Dashboard/src/app/memories/[id]/page.tsx`

- [ ] **Step 1: New-memory page.** Form: type select (MemoryType values), content textarea (required), tags input (comma-separated), source input. On submit → `saveMemory` → redirect to the detail page of the created memory. Show inline validation errors from the API.
- [ ] **Step 2: Detail page.** Loads by `id` (dynamic route param per the read docs — note if this Next version changed param passing for client components). Shows full content, type, tags, source, timestamps; Delete button → confirm → back to home.
- [ ] **Step 3: Link pages together** — home list rows link to `/memories/[id]`; a "New memory" button links to `/memories/new`.

---

### Task 4: Verify

- [ ] **Step 1:** `pnpm --prefix src/frontend/Eling.Dashboard build` — must pass (type-check + build).
- [ ] **Step 2:** `pnpm --prefix src/frontend/Eling.Dashboard lint` — must pass.
- [ ] **Step 3 (manual, optional):** With `Eling.Server` running (Pecut 7), `pnpm --prefix src/frontend/Eling.Dashboard dev` and click through list → create → detail → delete against a scratch `.eling` dir. Note results in the plan file's completion notes.

---

## Completion Notes

- Report `git status --short` and summarize changed files. Do NOT commit.
- If the Pecut 7 API contract changed during its implementation, adjust the client in Task 1 to match — but do NOT change the backend from this frontend plan.
