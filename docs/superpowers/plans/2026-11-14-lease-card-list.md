# Lease Card List Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use the executing-plans approach the user picks (subagent-driven or native). Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace the two expandable tables on `/dashboard/commit-lease` (Active, Recently released) with inline workspace-style cards, so every lease detail is visible without clicking expand.

**Architecture:** One new presentational component (`LeasePaths`, in its own file, unit-tested) that renders up to 3 paths with an inline `+N more` toggle. The `CommitLease.tsx` page keeps all data flow (SSE snapshot, 1s ticker, workspace-selection filter, force-release dialog, toasts) untouched and swaps only the rendering of the two `<Card>` sections to card grids modeled on `WorkspacesList.tsx`.

**Tech Stack:** Next.js App Router (client component), React, Tailwind, vitest + @testing-library/react, lucide-react.

**Spec:** `docs/superpowers/specs/2026-11-14-lease-card-list-design.md`

## Global Constraints

- Dashboard layout convention (standing memory): `DashboardHeader` unchanged; content column stays `flex min-w-0 w-full flex-1 flex-col gap-4 p-4`.
- Never run `pnpm build` / `next build` while the dev server runs on 4427 (overwrites `.next/`, deadlocks Turbopack on Windows). Type-check with `pnpm --filter eling-dashboard exec tsc --noEmit`.
- Timestamps render via `utcToLocal` (+ `relativeTime` for "x ago") from `@/lib/date-utils`; ticking Age/TTL keep the existing frame-anchored model (`lease.lease_age_seconds + elapsedSinceFrame`).
- Paths comparison/filter logic (`normalizeRoot`, `selectedRoots`, `isIncluded`) and the SSE/ticker/force-release logic are out of scope — do not touch them.
- `PathText` (`@/components/path-text`) is the checkout-path renderer; `workspaceName` comes from `@/components/workspaces/runtime-rows` (already imported).
- C# naming rules do not apply here; but no generic placeholders in new variables either — use domain names (`visiblePaths`, `pathsExpanded`, `leasePaths`).

## Review Focus

Failure modes most likely to bite a reader of this page, and where each is pinned:

1. **Paths boundary at exactly 3** — 3 paths must render with NO toggle button; 4+ must show `+N more`. → Task 1 unit tests.
2. **Null/absent fields** — `released_at`, `release_reason`, `intent`, `paths` may be null; cards must render `—` placeholders, never crash the page. → Task 2 step 6 (visual pass) + the null-safe branch kept from the current table code.
3. **Expired TTL** — `liveTtlRemaining(lease) <= 0` must keep the amber `0s · expired` treatment, now inside the card's TTL badge. → Task 2 step 6 (visual pass); rendering expression copied verbatim from the current table cell.
4. **SSE reconnection ticking** — Age/TTL must keep ticking between frames; the ticker and `elapsedSinceFrame` must not be deleted or re-anchored. → Task 2 constraint; verified by the untouched `now`/`snapshotAt` state and the 1s interval effect surviving the refactor (grep check in Task 2 step 5).
5. **Selection filter with partial selection** — a subset workspace selection must still hide cards from other checkouts (both sections). → Task 2 does not modify `isIncluded`/`selectedRoots`; verified by grep + visual pass in Task 2 step 6.

---

### Task 1: `LeasePaths` component with unit tests

**Files:**
- Create: `src/frontend/Eling.Dashboard/src/app/dashboard/commit-lease/lease-paths.tsx`
- Test: `src/frontend/Eling.Dashboard/src/app/dashboard/commit-lease/lease-paths.test.tsx`

**Interfaces:**
- Consumes: nothing (standalone).
- Produces: `export function LeasePaths({ paths }: { paths: string[] })` — renders up to 3 `<li>` entries (mono, `break-all`, `title` = full path) in a `<ul>`; if `paths.length > 3`, renders a toggle button (`+N more` / `Show less`) that reveals/hides the remainder inline. Internal state `const [expanded, setExpanded] = useState(false)`. `paths` is expected pre-normalized (`lease.paths ?? []`) by the caller. Empty array renders `—` (muted).

- [ ] **Step 1: Commit the spec and plan docs**

```bash
git add docs/superpowers/specs/2026-11-14-lease-card-list-design.md docs/superpowers/plans/2026-11-14-lease-card-list.md
git commit -m "docs: add commit-lease card list design and plan"
```

- [ ] **Step 2: Write the failing test** in `lease-paths.test.tsx` (vitest + RTL, follow `SearchTabsBar.test.tsx` conventions):

```tsx
import { describe, it, expect } from "vitest"
import { render, screen } from "@testing-library/react"
import userEvent from "@testing-library/user-event"
import { LeasePaths } from "./lease-paths"

const three = ["src/a.ts", "src/b.ts", "src/c.ts"]
const five = [...three, "src/d.ts", "src/e.ts"]

describe("LeasePaths", () => {
  it("renders all entries when there are exactly 3, with no toggle", () => {
    render(<LeasePaths paths={three} />)
    expect(screen.getByText("src/a.ts")).toBeTruthy()
    expect(screen.queryByRole("button")).toBeNull()
    expect(screen.getAllByRole("listitem")).toHaveLength(3)
  })

  it("shows 3 and a +2 more toggle when there are 5 paths", () => {
    render(<LeasePaths paths={five} />)
    expect(screen.getAllByRole("listitem")).toHaveLength(3)
    expect(screen.getByRole("button", { name: /\+2 more/i })).toBeTruthy()
  })

  it("reveals the remaining paths on toggle and hides them again on second click", async () => {
    const user = userEvent.setup()
    render(<LeasePaths paths={five} />)
    await user.click(screen.getByRole("button", { name: /\+2 more/i }))
    expect(screen.getAllByRole("listitem")).toHaveLength(5)
    await user.click(screen.getByRole("button", { name: /show less/i }))
    expect(screen.getAllByRole("listitem")).toHaveLength(3)
  })

  it("renders a dash for an empty list", () => {
    render(<LeasePaths paths={[]} />)
    expect(screen.getByText("—")).toBeTruthy()
  })
})
```

If `@testing-library/user-event` is not in `package.json`, use `fireEvent.click` from `@testing-library/react` instead (check first; do not add dependencies).

- [ ] **Step 3: Run the test to verify it fails**

Run: `pnpm --filter eling-dashboard exec vitest run lease-paths`
Expected: FAIL — `./lease-paths` module not found.

- [ ] **Step 4: Implement `LeasePaths`** in `lease-paths.tsx`. Signature and behavior exactly as in Interfaces above; the toggle button uses `type="button"`, plain muted text style (`text-xs text-muted-foreground hover:text-foreground`), and `aria-expanded` reflecting the state.

- [ ] **Step 5: Run the test to verify it passes**

Run: `pnpm --filter eling-dashboard exec vitest run lease-paths`
Expected: PASS (4 tests).

- [ ] **Step 6: Commit**

```bash
git add src/frontend/Eling.Dashboard/src/app/dashboard/commit-lease/lease-paths.tsx src/frontend/Eling.Dashboard/src/app/dashboard/commit-lease/lease-paths.test.tsx
git commit -m "feat(dashboard): add LeasePaths with truncated inline rendering"
```

### Task 2: Render both lease sections as workspace-style cards

**Files:**
- Modify: `src/frontend/Eling.Dashboard/src/app/dashboard/commit-lease/CommitLease.tsx`

**Interfaces:**
- Consumes: `LeasePaths` from `./lease-paths` (Task 1). Everything already imported stays; newly needed imports: `PathText` (`@/components/path-text`), `Clock`/`GitBranch` icons from `lucide-react` if used for the meta line, `Card`, `CardContent` stay, `CardHeader`/`CardAction`/`CardTitle` no longer needed for the two sections (or kept if still used — keep whatever remains referenced).
- Produces: the page at `/dashboard/commit-lease` renders the two sections as card grids; no remaining references to `expanded`, `toggleExpanded`, `CommitLeaseDetail`, `showWorkspace`, `activeColSpan`, `releasedColSpan`, or `ChevronDown`/`ChevronRight`.

- [ ] **Step 1: Replace the Active `<Card>` section** with a grid. Keep the `<Card>`/`<CardHeader>`/`<Badge>{active.length}</Badge>` wrapper row (it carries the section count) but swap its inner `overflow-x-auto rounded-xl border`/`<table>` for `<div className="grid min-w-0 grid-cols-1 gap-3 2xl:grid-cols-2">`. Each active card:

```tsx
<div key={lease.lease_id} className="flex flex-col justify-between gap-3 rounded-xl border bg-card p-4 sm:flex-row sm:items-start sm:gap-4">
  <div className="flex min-w-0 flex-1 flex-col gap-1.5">
    {/* Title row: workspace name + branch badge + TTL badge */}
    {/* PathText checkout line (prefix="📁", className="text-xs") */}
    {/* Meta line: Since · Age · TTL left · Heartbeat (small, muted, flex-wrap gap-x-4 gap-y-1) */}
    {/* Mono line: Lease {lease.lease_id} · Session {lease.session_id} (text-[11px], break-all, muted) */}
    {/* Intent: {lease.intent ?? "—"} with break-words text-sm */}
    {/* <LeasePaths paths={lease.paths ?? []} /> */}
  </div>
  <div className="flex shrink-0 flex-col justify-center self-stretch sm:w-40">
    {/* Force release Button, identical props to today's */}
  </div>
</div>
```

Details to carry over verbatim:
- Title: `workspaceName(lease.checkout)` (already the page's import); `title={lease.checkout}` on the name span.
- Branch badge: `<Badge variant="secondary" className="font-mono">{lease.branch ?? "—"}</Badge>` placed next to the name.
- TTL badge: `liveTtlRemaining(lease) <= 0 ?` amber badge `0s · expired` (`border-amber-500/40 bg-amber-500/10 text-amber-600 dark:text-amber-400`) `: <Badge variant="secondary" className="tabular-nums" title={`TTL remaining (${lease.ttl_seconds}s total)`}>{formatAge(liveTtlRemaining(lease))} left</Badge>`.
- Since: `utcToLocal(lease.since) || lease.since`, title = raw `lease.since`.
- Age: `formatAge(liveAge(lease))`, title `"Live age: frame value + elapsed wall-clock"`, `tabular-nums`.
- TTL left: same expression as the badge content, kept in the meta line as well is NOT needed — the TTL badge carries it; meta line holds Since, Age, Heartbeat.
- Heartbeat: `relativeTime(lease.last_heartbeat)` (e.g. "4s ago"), title = `utcToLocal(lease.last_heartbeat) || lease.last_heartbeat`.
- Force release button: same variant/size/className and `onClick={() => setForceTarget(lease)}` as today.

- [ ] **Step 2: Replace the Recently released `<Card>` section** the same way. Differences: no action column; title row carries `<ReleaseReasonBadge reason={lease.release_reason} />` instead of the TTL badge and no branch badge (unless `branch` exists — it does NOT on `CommitLeaseReleasedRow`, do not invent one); meta line holds `Released {utcToLocal(lease.released_at) || lease.released_at} ({relativeTime(lease.released_at)})` (render `—` when `released_at` is null) and `Since {utcToLocal(lease.since) || lease.since}`; mono line with Lease/Session ids; intent; `<LeasePaths>`.

- [ ] **Step 3: Delete dead code** — the `expanded` state, `toggleExpanded` callback, the `ChevronDown`/`ChevronRight` imports, the local `CommitLeaseDetail` function, `showWorkspace`, `activeColSpan`, `releasedColSpan`, and the conditional `{showWorkspace && ...}` columns. The expand column header `<th scope="col" className="w-8 p-3" aria-label="Expand row" />` disappears with the tables.

- [ ] **Step 4: Type-check**

Run: `pnpm --filter eling-dashboard exec tsc --noEmit`
Expected: exits 0, no output.

- [ ] **Step 5: Grep for leftovers**

Run: `rg "toggleExpanded|CommitLeaseDetail|activeColSpan|releasedColSpan|showWorkspace|expanded\[" src/frontend/Eling.Dashboard/src/app/dashboard/commit-lease/`
Expected: no matches.

- [ ] **Step 6: Visual pass on the dev server (4427 is already running — do NOT build)**

Open `http://localhost:4427/dashboard/commit-lease`. Check: both sections render cards; intent and paths are fully visible without any expand; a lease with 4+ paths shows `+N more` and toggles; empty states still render (dashed box or the same `text-center text-muted-foreground` empty text inside the grid — keep the existing empty-state wording, just placed in the grid with `col-span-full` semantics: use a single full-width dashed container like the Workspaces empty state); skeletons render on hard reload; SSE "● Live" pill and Refresh button still work; force-release dialog still opens from a card.

- [ ] **Step 7: Run the full dashboard test suite**

Run: `pnpm --filter eling-dashboard test`
Expected: all tests PASS, including the new `lease-paths` suite.

- [ ] **Step 8: Commit**

```bash
git add src/frontend/Eling.Dashboard/src/app/dashboard/commit-lease/CommitLease.tsx
git commit -m "feat(dashboard): render commit-lease lists as cards"
```
