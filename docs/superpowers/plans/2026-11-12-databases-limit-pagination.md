# Databases Data tab: per-tab limit dropdown + pagination — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Give the Databases page's Data tab a row-limit dropdown that is remembered per tab (with a dynamic default for new tabs) and server-side Prev/Next pagination.

**Architecture:** `DbTab` carries two new optional fields (`browseLimit`, `browsePage`); pure helpers in a new `utils/browse-paging.ts` compute effective limit/page, the default for new tabs (mode of open table tabs), and page clamping. `useTableData` reads limit/page from `activeTableRef` at request time and issues `LIMIT n OFFSET (page-1)*n`. The footer bar in `DataGrid` (today "Total rows: N …") gains the Select and Prev/Next. Persisted `db.explorer.state` strips the two fields on save and on restore (spec: not persisted).

**Tech Stack:** Next.js (this repo's version — see `src/frontend/Eling.Dashboard/AGENTS.md`), React, shadcn-style `@/components/ui/select` + `@/components/ui/button`, vitest.

**Spec:** `docs/superpowers/specs/2026-11-12-databases-limit-dropdown-design.md`

## Global Constraints

- Data tab only. SQL runner (`maxRows: 100`, auto-append `LIMIT 100`) and Redis browser are NOT touched.
- Limit options exactly: `50 / 100 / 250 / 500 / 1000`. Default 100.
- Limit is per tab and IS the page size. New-tab default = mode of open table tabs' effective limits; tie → larger value; no table tabs → 100. Redis tabs never count.
- Page state per tab (`browsePage`, 1-based); changing the limit resets page to 1.
- No `ORDER BY` (user decision) — row order across pages may shift.
- No persistence to localStorage/server for `browseLimit`/`browsePage`: strip on save AND on restore of `db.explorer.state`.
- Poll `fromCache` keeps running at the tab's current limit/page (read from `activeTableRef` at fire time).
- The existing request-number guard (`browseInFlightRef`) stays as the stale-answer protection.
- Verification: `pnpm --filter eling-dashboard test` (or `pnpm test` in `src/frontend/Eling.Dashboard`) for unit tests; `pnpm --filter eling-dashboard exec tsc --noEmit` for types. NEVER `pnpm build` while the dev server on 4427 runs.
- Commits: split by logical context (one logical change per commit); no `--no-verify`.

## Review Focus

1. **Persisted state containing `browseLimit`/`browsePage`** (old dev builds, hand-edited localStorage) — restore must drop them; save must not write them. → Task 5 (`persistableTab`) + its test.
2. **Garbage `browseLimit` in a tab object** (NaN, 0, negative, non-option value from storage) — `effectiveBrowseLimit` must fall back to 100. → Task 1 test.
3. **Page beyond the last page after rows are deleted** — `clampPage` must pin to the last valid page; `browseData` auto-clamps and refetches once instead of showing an empty grid with "Halaman 99 dari 3". → Task 1 test + Task 4 behavior.
4. **Redis tabs leaking into the mode computation or receiving browse fields** — `defaultBrowseLimit` ignores `isRedisTab` tabs and tabs without table-kind; selectTreeTable only sets the default for SQL table tabs. → Task 1/Task 3 tests.
5. **`COUNT(*)` failing while the page query succeeds** — `browseTotal` stays null; footer must render "Halaman X dari ?" style or keep total unknown without crashing; Next stays enabled only while `rows.length === limit`. → Task 6 components render from `browseTotal === null` without assuming a number.

---

### Task 1: Pure paging helpers + DbTab fields (TDD)

**Files:**
- Create: `src/frontend/Eling.Dashboard/src/app/dashboard/databases/utils/browse-paging.ts`
- Create: `src/frontend/Eling.Dashboard/src/app/dashboard/databases/utils/browse-paging.test.ts`
- Modify: `src/frontend/Eling.Dashboard/src/app/dashboard/databases/db-tabs.ts` (DbTab type + `persistableTab`)
- Modify: `src/frontend/Eling.Dashboard/src/app/dashboard/databases/db-tabs.test.ts` (persistableTab tests)

**Interfaces:**
- Produces: `BROWSE_LIMIT_OPTIONS: readonly [50, 100, 250, 500, 1000]`; `DEFAULT_BROWSE_LIMIT = 100`; `effectiveBrowseLimit(tab): number`; `effectiveBrowsePage(tab): number`; `defaultBrowseLimit(openTabs): number`; `totalPages(total: number | null, limit: number): number | null`; `clampPage(page: number, total: number | null, limit: number): number`; `persistableTab(tab: DbTab): DbTab`. `DbTab` gains `browseLimit?: number` and `browsePage?: number`.

- [ ] **Step 1: Write failing tests** in `browse-paging.test.ts`

```ts
import { describe, it, expect } from "vitest"
import { BROWSE_LIMIT_OPTIONS, DEFAULT_BROWSE_LIMIT, effectiveBrowseLimit, effectiveBrowsePage, defaultBrowseLimit, totalPages, clampPage } from "./browse-paging"

describe("effectiveBrowseLimit", () => {
  it("returns 100 without a tab or field", () => {
    expect(effectiveBrowseLimit(null)).toBe(100)
    expect(effectiveBrowseLimit({ connId: "c", table: "t" })).toBe(100)
  })
  it("honours a tab's limit", () => {
    expect(effectiveBrowseLimit({ connId: "c", table: "t", browseLimit: 500 })).toBe(500)
  })
  it("falls back to 100 for garbage (0, NaN, negative, non-member)", () => {
    expect(effectiveBrowseLimit({ connId: "c", table: "t", browseLimit: 0 })).toBe(100)
    expect(effectiveBrowseLimit({ connId: "c", table: "t", browseLimit: Number.NaN })).toBe(100)
    expect(effectiveBrowseLimit({ connId: "c", table: "t", browseLimit: -50 })).toBe(100)
    expect(effectiveBrowseLimit({ connId: "c", table: "t", browseLimit: 7 })).toBe(100)
  })
})

describe("effectiveBrowsePage", () => {
  it("returns 1 by default and clamps garbage to 1", () => {
    expect(effectiveBrowsePage(null)).toBe(1)
    expect(effectiveBrowsePage({ connId: "c", table: "t" })).toBe(1)
    expect(effectiveBrowsePage({ connId: "c", table: "t", browsePage: 0 })).toBe(1)
    expect(effectiveBrowsePage({ connId: "c", table: "t", browsePage: 3 })).toBe(3)
  })
})

describe("defaultBrowseLimit (mode of open table tabs)", () => {
  const tab = (browseLimit?: number, kind?: "redis") => ({ connId: "c", table: kind === "redis" ? "Redis keys" : "t", kind, browseLimit })
  it("returns 100 with no tabs", () => {
    expect(defaultBrowseLimit([])).toBe(100)
  })
  it("picks the most frequent effective limit", () => {
    expect(defaultBrowseLimit([tab(100), tab(500), tab(100), tab(500), tab(500)])).toBe(500)
  })
  it("counts a missing browseLimit as 100", () => {
    expect(defaultBrowseLimit([tab(), tab(), tab(500)])).toBe(100)
  })
  it("breaks ties toward the larger value", () => {
    expect(defaultBrowseLimit([tab(100), tab(250), tab(500), tab(250), tab(500)])).toBe(500)
  })
  it("ignores Redis tabs", () => {
    expect(defaultBrowseLimit([tab(500, "redis"), tab(100), tab(100)])).toBe(100)
  })
})

describe("totalPages", () => {
  it("is null when total is unknown", () => {
    expect(totalPages(null, 100)).toBeNull()
  })
  it("rounds up", () => {
    expect(totalPages(250, 100)).toBe(3)
    expect(totalPages(0, 100)).toBe(0)
    expect(totalPages(100, 100)).toBe(1)
  })
})

describe("clampPage", () => {
  it("keeps a valid page", () => {
    expect(clampPage(2, 250, 100)).toBe(2)
  })
  it("pins to the last page when out of range", () => {
    expect(clampPage(99, 250, 100)).toBe(3)
  })
  it("pins to 1 when the table is empty or total unknown", () => {
    expect(clampPage(5, 0, 100)).toBe(1)
    expect(clampPage(5, null, 100)).toBe(1)
  })
  it("pins sub-1 pages to 1", () => {
    expect(clampPage(0, 250, 100)).toBe(1)
  })
})
```

Note: the `200 as never` line above is awkward — drop it; use `[tab(100), tab(500), tab(100), tab(500), tab(500)]` expecting 500, or a 250 tie case already covered.

Also `db-tabs.test.ts` gains:

```ts
import { persistableTab } from "./db-tabs"
describe("persistableTab", () => {
  it("drops browseLimit and browsePage but keeps identity fields", () => {
    const t = { connId: "c1", table: "orders", database: "db", browseLimit: 500, browsePage: 3 }
    expect(persistableTab(t)).toEqual({ connId: "c1", table: "orders", database: "db" })
  })
  it("keeps redis identity fields", () => {
    const t = { connId: "c1", table: "Redis keys", kind: "redis" as const, redisDb: 2, browseLimit: 250, browsePage: 2 }
    expect(persistableTab(t)).toEqual({ connId: "c1", table: "Redis keys", kind: "redis", redisDb: 2 })
  })
})
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `cd src/frontend/Eling.Dashboard && pnpm test -- browse-paging db-tabs`
Expected: FAIL — module `./browse-paging` not found / `persistableTab` is not exported.

- [ ] **Step 3: Implement**

`db-tabs.ts`: extend `DbTab` with `browseLimit?: number` and `browsePage?: number` (with JSDoc: session-only view state, stripped from persistence). Add:

```ts
/** The persisted form of a tab: identity only. browseLimit/browsePage are session view state and never hit localStorage. */
export const persistableTab = (tab: DbTab): DbTab => {
  const { browseLimit: _browseLimit, browsePage: _browsePage, ...rest } = tab
  return rest
}
```

`utils/browse-paging.ts`:

```ts
import { isRedisTab, type DbTab } from "../db-tabs"

export const BROWSE_LIMIT_OPTIONS = [50, 100, 250, 500, 1000] as const
export const DEFAULT_BROWSE_LIMIT = 100

export const effectiveBrowseLimit = (tab: DbTab | null | undefined): number => {
  const v = tab?.browseLimit
  return typeof v === "number" && Number.isFinite(v) && (BROWSE_LIMIT_OPTIONS as readonly number[]).includes(v) ? v : DEFAULT_BROWSE_LIMIT
}

export const effectiveBrowsePage = (tab: DbTab | null | undefined): number => {
  const v = tab?.browsePage
  return typeof v === "number" && Number.isFinite(v) && v >= 1 ? Math.floor(v) : 1
}

/** Mode (most frequent) of effective limits over open TABLE tabs; ties → larger; none → 100. */
export const defaultBrowseLimit = (openTabs: DbTab[]): number => {
  const counts = new Map<number, number>()
  for (const t of openTabs) {
    if (isRedisTab(t)) continue
    const v = effectiveBrowseLimit(t)
    counts.set(v, (counts.get(v) ?? 0) + 1)
  }
  let best = DEFAULT_BROWSE_LIMIT
  let bestCount = 0
  for (const [value, count] of counts) {
    if (count > bestCount || (count === bestCount && value > best)) {
      best = value
      bestCount = count
    }
  }
  return best
}

export const totalPages = (total: number | null, limit: number): number | null =>
  total === null ? null : total <= 0 ? 0 : Math.ceil(total / limit)

export const clampPage = (page: number, total: number | null, limit: number): number => {
  const p = Number.isFinite(page) && page >= 1 ? Math.floor(page) : 1
  const pages = totalPages(total, limit)
  if (pages === null || pages <= 0) return 1
  return Math.min(p, pages)
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `cd src/frontend/Eling.Dashboard && pnpm test -- browse-paging db-tabs`
Expected: PASS

- [ ] **Step 5: Commit**

```bash
git add src/frontend/Eling.Dashboard/src/app/dashboard/databases/utils/browse-paging.ts src/frontend/Eling.Dashboard/src/app/dashboard/databases/utils/browse-paging.test.ts src/frontend/Eling.Dashboard/src/app/dashboard/databases/db-tabs.ts src/frontend/Eling.Dashboard/src/app/dashboard/databases/db-tabs.test.ts docs/superpowers/specs/2026-11-12-databases-limit-dropdown-design.md
git commit -m "feat(databases): add per-tab browse limit/page types and pure paging helpers"
```

---

### Task 2: New table tabs get the dynamic default limit

**Files:**
- Modify: `src/frontend/Eling.Dashboard/src/app/dashboard/databases/hooks/useExplorerTabs.ts` (selectTreeTable)

**Interfaces:**
- Consumes: `defaultBrowseLimit(openTabs)` from Task 1; the hook already receives `openTabs`.
- Produces: new SQL table tabs created by `selectTreeTable` carry `browseLimit: defaultBrowseLimit(openTabs)`; existing open tabs are reused untouched (no default applied).

- [ ] **Step 1: Implement** — in `selectTreeTable`, when building `newActive` for a NOT-already-open tab, set `browseLimit: defaultBrowseLimit(openTabs)`. When the tab already exists in `openTabs`, reuse the existing tab object (so its remembered limit/page survives re-activation) — today the code creates `newActive` unconditionally for the active view; keep that, but for the `setOpenTabs` branch the existing-tab check stays as is. Concretely:

```ts
const defaultLimit = defaultBrowseLimit(openTabs)
const newActive = { connId, table, database: database ?? null, browseLimit: defaultLimit }
```

and in the `setOpenTabs` updater, unchanged: existing tab → return prev; else append `newActive`. `activeTable`/`activeTableRef` are set to `newActive` even when the tab already exists — fix that too: if the tab exists, activate the EXISTING object (with its browseLimit/browsePage) instead of the fresh one, or the remembered limit is lost on every re-click. Compute:

```ts
const existing = openTabs.find((t) => t.connId === connId && t.table === table && (t.database ?? null) === (database ?? null))
const active = existing ?? newActive
setActiveTable(active)
activeTableRef.current = active
```

Redis tabs (`openRedisDb` in `useRedisBrowser.ts`) are out of scope — do not add browse fields there.

- [ ] **Step 2: Verify** — `pnpm --filter eling-dashboard exec tsc --noEmit` passes. Behavioural check comes in Task 6 (needs the UI).

- [ ] **Step 3: Commit**

```bash
git add src/frontend/Eling.Dashboard/src/app/dashboard/databases/hooks/useExplorerTabs.ts
git commit -m "feat(databases): default new table tabs to the mode of open tabs' limits"
```

---

### Task 3: browseData honours per-tab limit/page (LIMIT/OFFSET)

**Files:**
- Modify: `src/frontend/Eling.Dashboard/src/app/dashboard/databases/hooks/useTableData.ts`

**Interfaces:**
- Consumes: `effectiveBrowseLimit`, `effectiveBrowsePage`, `totalPages`, `clampPage` from Task 1.
- Produces: `browseData` issues `SELECT * FROM <t> LIMIT <limit> OFFSET <(page-1)*limit>` with `maxRows: limit`, reads limit/page from `activeTableRef.current` at request time. Same external signature — callers unchanged.

- [ ] **Step 1: Implement** — in `browseData`, after the stale-tab guard:

```ts
const tab = activeTableRef.current ?? null
const limit = effectiveBrowseLimit(tab)
const page = effectiveBrowsePage(tab)
const offset = (page - 1) * limit
const selectSql = "SELECT * FROM " + quote + escaped + quote + " LIMIT " + limit + " OFFSET " + offset
```

`maxRows: limit` in the page query. The `COUNT(*)` query is unchanged. After both resolves (count known), clamp: `const safePage = clampPage(page, countTotal, limit)`; if `safePage !== page`, update the active tab's `browsePage` — but `useTableData` has no `setOpenTabs`; instead re-issue the fetch? No: simplest correct behaviour — if `safePage !== page`, set `browseResult` from the data already fetched is WRONG (offset too large → empty rows). So: if clamped differs, skip painting these rows and trigger one refetch at the clamped page. Since the hook cannot set the tab state, expose the clamp via a ref/callback: add an optional `onClampPage?: (page: number) => void` parameter to `useTableData` options; `Databases.tsx` (Task 4) passes a setter that updates tab state + refetches. If `onClampPage` is absent, fall back to painting what came back (builder's documented last-resort).

Poll timer stays `POLL_MS`, reads fresh limit/page at fire time via the same ref (already the case — the poll re-invokes `browseData`).

- [ ] **Step 2: Verify** — `pnpm --filter eling-dashboard exec tsc --noEmit`.

- [ ] **Step 3: Commit**

```bash
git add src/frontend/Eling.Dashboard/src/app/dashboard/databases/hooks/useTableData.ts
git commit -m "feat(databases): browse fetch uses per-tab LIMIT/OFFSET"
```

---

### Task 4: Tab state callbacks in Databases.tsx

**Files:**
- Modify: `src/frontend/Eling.Dashboard/src/app/dashboard/databases/Databases.tsx`

**Interfaces:**
- Consumes: `useTableData`'s new `onClampPage` option (Task 3); existing `setOpenTabs`, `setActiveTable`, `activeTableRef`.
- Produces: `setBrowseLimit(limit: number)` and `setBrowsePage(page: number)` callbacks passed to `ExplorerPanels`; `onClampPage` wired to `setBrowsePage` + refetch.

- [ ] **Step 1: Implement** — two callbacks in `Databases.tsx`:

```ts
const patchActiveTab = useCallback((patch: Partial<DbTab>) => {
  const cur = activeTableRef.current
  if (!cur) return null
  const nextTab = { ...cur, ...patch }
  activeTableRef.current = nextTab
  setActiveTable(nextTab)
  setOpenTabs((prev) => prev.map((t) => (tabKey(t) === tabKey(cur) ? nextTab : t)))
  return nextTab
}, [setActiveTable, setOpenTabs])

const setBrowseLimit = useCallback((limit: number) => {
  if (!patchActiveTab({ browseLimit: limit, browsePage: 1 })) return
  // refreshData re-reads activeTableRef, which patchActiveTab already updated.
  void refreshData()
}, [patchActiveTab, refreshData])

const setBrowsePage = useCallback((page: number) => {
  if (!patchActiveTab({ browsePage: Math.max(1, Math.floor(page)) })) return
  void refreshData()
}, [patchActiveTab, refreshData])
```

(Where `refreshData` is the existing callback that calls `browseData(..., false, true)`; if `refreshData` reads `activeTable` state instead of the ref, switch its read to `activeTableRef.current ?? activeTable` or accept the patched state — check its body at line ~372 and make the ref the source of truth.)

- `useTableData` call site: pass `onClampPage: (page: number) => { patchActiveTab({ browsePage: page }); void refreshData() }`.
- Pass `onBrowseLimitChange={setBrowseLimit}` and `onBrowsePageChange={setBrowsePage}` into `<ExplorerPanels … />`.

- [ ] **Step 2: Persist strip** — in the `db.explorer.state` save effect, write `activeTable: activeTable ? persistableTab(activeTable) : null` and `openTabs: openTabs.map(persistableTab)`. In the restore branch, when reading `st?.openTabs` and `st?.activeTable`, map through `persistableTab` as well (old saves may carry the fields). Import `persistableTab` from `./db-tabs`.

- [ ] **Step 3: Verify** — `pnpm --filter eling-dashboard exec tsc --noEmit`.

- [ ] **Step 4: Commit**

```bash
git add src/frontend/Eling.Dashboard/src/app/dashboard/databases/Databases.tsx
git commit -m "feat(databases): wire per-tab limit/page callbacks and strip them from persisted state"
```

---

### Task 5: Footer controls in DataGrid + ExplorerPanels threading

**Files:**
- Modify: `src/frontend/Eling.Dashboard/src/app/dashboard/databases/components/ExplorerPanels.tsx` (props + pass-through)
- Modify: `src/frontend/Eling.Dashboard/src/app/dashboard/databases/components/data-grid/DataGrid.tsx` (footer)

**Interfaces:**
- Consumes: `BROWSE_LIMIT_OPTIONS` from Task 1; `effectiveBrowseLimit`, `effectiveBrowsePage`, `totalPages` for rendering.
- Produces: `DataGrid` renders, in the existing footer bar (left: "Total rows: N (showing M)" unchanged), right-aligned: `[Rows <limit> ▾]` `‹` "Halaman X dari Y" `›`.

- [ ] **Step 1: ExplorerPanels** — add to props type + destructure + pass to `<DataGrid>`: `browseLimit: number`, `browsePage: number`, `onBrowseLimitChange: (limit: number) => void`, `onBrowsePageChange: (page: number) => void`. Pass plain values (effective limit/page computed in `Databases.tsx` via the helpers), not the raw tab.

`Databases.tsx` computes `const browseLimit = effectiveBrowseLimit(activeTable)` and `const browsePage = effectiveBrowsePage(activeTable)` and passes them.

- [ ] **Step 2: DataGrid footer** — replace the current footer `<div className="sticky bottom-0 …">` content with flex row: left span unchanged text; right cluster:

```tsx
<span className="ml-auto flex items-center gap-2">
  <Select value={String(browseLimit)} onValueChange={(v) => v && onBrowseLimitChange(Number(v))} disabled={browseLoading}>
    <SelectTrigger className="h-6 w-24 text-xs"><SelectValue /></SelectTrigger>
    <SelectContent>
      {BROWSE_LIMIT_OPTIONS.map((n) => <SelectItem key={n} value={String(n)}>Rows {n}</SelectItem>)}
    </SelectContent>
  </Select>
  <Button variant="outline" size="sm" className="h-6 w-6 p-0" disabled={browseLoading || browsePage <= 1} onClick={() => onBrowsePageChange(browsePage - 1)} aria-label="Previous page">‹</Button>
  <span className="tabular-nums">{pages === null ? `Halaman ${browsePage} dari ?` : `Halaman ${browsePage} dari ${pages}`}</span>
  <Button variant="outline" size="sm" className="h-6 w-6 p-0" disabled={browseLoading || (pages !== null ? browsePage >= pages : browseResult.rows.length < browseLimit)} onClick={() => onBrowsePageChange(browsePage + 1)} aria-label="Next page">›</Button>
</span>
```

where `const pages = totalPages(browseTotal, browseLimit)`. Import `Select, SelectContent, SelectItem, SelectTrigger, SelectValue` from `@/components/ui/select` (pattern from `Logs.tsx`), keep the visual language of the existing bar (text-xs, muted). Toolbar stays UNCHANGED (varian B).

- [ ] **Step 3: Verify** — `pnpm --filter eling-dashboard exec tsc --noEmit` and `pnpm test` (existing suite still green).

- [ ] **Step 4: Commit**

```bash
git add src/frontend/Eling.Dashboard/src/app/dashboard/databases/components/ExplorerPanels.tsx src/frontend/Eling.Dashboard/src/app/dashboard/databases/components/data-grid/DataGrid.tsx src/frontend/Eling.Dashboard/src/app/dashboard/databases/Databases.tsx
git commit -m "feat(databases): footer limit select and Prev/Next pagination controls"
```

---

### Task 6: Manual QA + spec/plan commit

**Files:** none (verification only).

- [ ] **Step 1: Run the dashboard** (or ask the user): dev server 4427 (`pnpm --filter eling-dashboard dev`) + backend 4417. Verify in the browser:
  1. Open a table → footer shows `Rows 100`, `Halaman 1 dari Y`, left text unchanged.
  2. Switch to Rows 50 → grid refetches, page resets to 1, footer updates.
  3. Next/Prev → SQL shows correct OFFSET (via network tab: body `sql` contains `OFFSET 50` / `LIMIT 50`).
  4. Open two more tables with different limits (e.g. 250 and 500) → open a NEW table → it starts at the mode (ties → larger).
  5. Switch back to an earlier tab → its limit and page are remembered.
  6. Reload the page → tabs restored WITHOUT limit/page (back to 100 / page 1); `localStorage["db.explorer.state"]` contains no `browseLimit`/`browsePage`.
  7. Shrink a table (delete rows so current page vanishes) → grid shows the clamped last page, not an empty page.
  8. SQL runner tab untouched; Redis tab untouched (no Rows control in its footer).
  9. Cached (fromCache) state → poll still fires at the tab's limit/page.
- [ ] **Step 2: Commit the plan + spec** (if not already committed in Task 1):

```bash
git add docs/superpowers/plans/2026-11-12-databases-limit-pagination.md docs/superpowers/specs/2026-11-12-databases-limit-dropdown-design.md
git commit -m "docs: design and plan for per-tab limit dropdown and pagination"
```
