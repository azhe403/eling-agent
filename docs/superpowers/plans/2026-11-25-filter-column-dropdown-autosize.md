# Filter Column Dropdown Auto-size Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make the column dropdowns in the Databases Data-tab filter panel auto-size so long column names are readable and more rows are visible before scrolling.

**Architecture:** The shared shadcn `SelectContent` caps height at Base UI's `--available-height` (space below the trigger), which both shortens the popup and prevents collision-flip; `FilterPanel.tsx` additionally caps column popup width at `max-w-72` and trigger width at `max-w-56`. Override those three caps locally on the two column selects only: width follows the widest column name up to a viewport-aware bound, height follows the row count up to ~20rem, and the existing `overflow-y-auto` plus Base UI scroll arrows handle overflow. No new component or state.

**Tech Stack:** Next.js 16 / React 19 dashboard, shadcn/ui `Select` on `@base-ui/react`, Tailwind v4 utility classes, `cn` (tailwind-merge), vitest.

**Spec:** design approved in chat 2026-11-25 ("Lebar + tinggi"). No separate spec file — the approved short design is the authority for values and scope.

## Global Constraints

- Keep the shadcn `Select` primitive in `src/components/ui/select.tsx` untouched; override classes only at the call sites in `FilterPanel.tsx`.
- `cn` (package `cn`, tailwind-merge) resolves conflicting utilities by last-write-wins, so a caller-supplied `max-h-*` overrides the primitive's `max-h-(--available-height)`. Do not add an inline `style` override.
- Base UI `Positioner` exposes `--available-height`; raising the popup's height cap lets collision avoidance flip it above the trigger when the space below is short. Do not disable collision avoidance.
- Popup keeps `overflow-y-auto` and the up/down scroll arrows from `SelectContent`; no custom virtualization or search box.
- Interactive controls keep `cursor-pointer` (repo convention).
- Do not run `pnpm build` / `next build` while the dev server is on 4427 (AGENTS.md). Use `pnpm --filter eling-dashboard exec tsc --noEmit`.
- Work directly in the main checkout on `main`; no git worktree. Call `eling_commit_lease` before any `git add`/commit and `eling_commit_release` after the attempt lands or fails. Never bypass hooks (`--no-verify`, `HUSKY=0`). One logical change per commit; never stage files owned by another session.
- Chat replies in Indonesian; code, comments, commit messages and docs stay English.

## Review Focus

- A 42-character column name such as `monthly_contract_config_additional_revenue` must be fully visible in both the selected trigger and the popup item — neither clipped at 224px nor at 288px.
- A popup whose trigger sits near the bottom of the viewport must flip above instead of showing only the 2–3 rows that fit below.
- A table with more than ~8 columns must cap the popup and scroll, with the scroll arrows still rendered.
- The sort-row column dropdown gets the same treatment as the condition-row column dropdown; they must not diverge.
- Logic, operator and sort-order dropdowns keep their current sizing — this change must not widen them or alter their item lists.

---

### Task 1: Auto-size the two column selects in `FilterPanel.tsx`

**Files:**
- Modify: `src/frontend/Eling.Dashboard/src/app/dashboard/databases/components/data-grid/FilterPanel.tsx` (condition column select ~lines 216–234; sort column select ~lines 269–287)
- Test: `src/frontend/Eling.Dashboard/src/app/dashboard/databases/components/data-grid/FilterPanel.test.tsx` (existing behavior suite — no new test; this task is styling-only and the existing tests guard column selection, operator defaults and commit behavior)

**Interfaces:**
- Consumes: `columns: string[]`, `Select`/`SelectTrigger`/`SelectContent`/`SelectItem` from `@/components/ui/select`, `identityItems(columns)`, `selectClass` — all already in `FilterPanel.tsx`.
- Produces: unchanged props and behavior; only class strings change.

- [ ] **Step 1: Confirm the current caps before editing**

Read the two column selects and verify the exact strings that must change:
- condition trigger: `${selectClass} min-w-36 max-w-56 truncate`
- condition popup: `w-auto min-w-56 max-w-72`
- sort trigger: `${selectClass} min-w-36 max-w-56 truncate`
- sort popup: `w-auto min-w-56 max-w-72`
Also confirm `SelectContent` in `src/components/ui/select.tsx` still carries `max-h-(--available-height)` and `overflow-y-auto`.

- [ ] **Step 2: Apply the new caps in `FilterPanel.tsx`**

Change both triggers to `${selectClass} min-w-36 max-w-80 truncate` and both popups to `w-auto min-w-56 max-w-[min(90vw,32rem)] max-h-[min(20rem,70vh)]`. Leave every other select, the `items` props and the `onValueChange` handlers untouched.

- [ ] **Step 3: Type-check**

Run: `pnpm --filter eling-dashboard exec tsc --noEmit` from `src/frontend/Eling.Dashboard`
Expected: no errors.

- [ ] **Step 4: Run the existing targeted tests**

Run: `pnpm --filter eling-dashboard exec vitest run src/app/dashboard/databases/components/data-grid/FilterPanel.test.tsx`
Expected: all tests PASS. (If the known vitest CommonJS/ESM config issue appears, note it and use the repo's established alternate invocation rather than changing config.)

- [ ] **Step 5: Verify visually against the dev server**

With `next dev` already serving 4427, open `/dashboard/databases`, open a table whose columns include a 40+ character name, open the filter panel, add a condition, and open the column dropdown. Confirm: full name visible in trigger and items; popup shows ~8 rows; popup flips above the trigger when near the bottom; sort-row column dropdown behaves the same. Do not run `next build`.

- [ ] **Step 6: Commit**

```bash
git add src/frontend/Eling.Dashboard/src/app/dashboard/databases/components/data-grid/FilterPanel.tsx
git commit -m "fix(databases): auto-size filter column dropdowns"
```

Take `eling_commit_lease` before the `git add` and again immediately before the commit; release with `eling_commit_release` once the command returns.

## Verification result (2026-11-25)

- Implemented in `FilterPanel.tsx`: both column triggers now `max-w-80`; both column popups now `w-auto min-w-56 max-w-[min(90vw,32rem)] max-h-[min(20rem,70vh)]`. No other selects touched.
- `pnpm exec tsc --noEmit` (in `src/frontend/Eling.Dashboard`): clean.
- `pnpm exec vitest run --config vitest.temp.mts src/app/dashboard/databases/components/data-grid/FilterPanel.test.tsx`: 1 file / 19 tests passed. The throwaway `vitest.temp.mts` was deleted after the run (the checked-in `vitest.config.ts` hits the known vite 8 CJS/ESM issue).
- Live browser check on dev server 4427 against `dev-ibl-postgres-billing` / `monthly_contract_config_additional_revenue`: the selected trigger renders the full 44-character `monthly_contract_config_additional_revenue_id` (trigger width 307px); the open popup measures 320px tall × ~362px wide with `overflow: hidden auto` and lists all 24 columns, scrolling beyond the visible ~8 rows. Operator/logic/order dropdowns unchanged.
- Committed as `e7d37bd` (`fix(databases): auto-size filter column dropdowns`) with an Eling commit lease; pre-commit lint/typecheck passed (54 pre-existing warnings, 0 errors). Only `FilterPanel.tsx` was staged; unrelated untracked work in the checkout was left alone.
