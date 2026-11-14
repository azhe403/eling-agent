# Design: Commit Lease list as workspace-style cards

**Date:** 2026-11-14
**Status:** Approved by user ("gas")
**Scope:** `src/frontend/Eling.Dashboard/src/app/dashboard/commit-lease/CommitLease.tsx` only. No backend, wire-shape, or API changes.

## Intent

The Commit Lease page renders Active and Recently released leases as tables whose row
details (lease_id, session_id, branch, heartbeat, intent, paths) hide behind a
chevron-expand. The user finds it cumbersome that seeing those details requires
clicking expand each time. The Workspaces page already solves the same density
problem with cards that show everything inline; the user asked to copy that pattern.

## Goals / non-goals

Goals:

- Both sections (Active and Recently released) become card grids mirroring the
  Workspaces page: `grid grid-cols-1 2xl:grid-cols-2 gap-3`, cards
  `rounded-xl border bg-card p-4`, skeleton cards while loading, dashed-border
  empty state.
- Every detail currently hidden behind the expand renders inline on the card.
- Paths lists truncate to 3 entries with an inline `+N more` toggle per card.
- Live behaviour introduced with this change: the 1-second wall-clock ticker for
  age/TTL — the base rendered age as a frozen frame value; the ticker is added
  by this branch, anchored to `snapshotAt` + elapsed wall-clock (see the ledger
  ruling). SSE full-snapshot frames, client-side workspace-selection filter,
  force-release confirmation dialog, and toast notifications are unchanged.
- Force release stays a per-card action in a right-side action column
  (`sm:w-40`, self-stretch), mirroring the Workspaces card action column.

Non-goals:

- No backend or payload changes. Age/TTL still derive client-side from the frame
  plus elapsed wall-clock.
- No changes to the workspace scope panel, the header, or the notifications.
- No search/sort toolbar (YAGNI; the page has few rows).

## Card anatomy

Active card:

- Title row: workspace name (semibold, like Workspaces cards) + branch badge
  (secondary) + TTL badge (secondary; amber `expired` treatment when remaining <= 0).
- Path line: checkout via `PathText` with the 📁 prefix (mirrors Workspaces).
- Meta line (small, muted, icons where the Workspaces page uses them): Since
  (local, title = raw ISO), Age (live ticking), TTL left (live ticking),
  Heartbeat (relative).
- Mono detail line: `Lease <id>` and `Session <id>`, small, break-all.
- Intent: full text, `break-words` (no single-line truncation).
- Paths: up to 3 entries, mono, break-all, then `+N more` toggle that reveals the
  rest inline (per-card local state).
- Right action column: Force release (outline, destructive-tinted), same as the
  current button.

Released card: same frame, no action column and no TTL badge; title row carries a
`release_reason` badge (forced = destructive, else secondary). Meta line: Released
at (local + relative) and Since. Everything else (ids, intent, paths-with-toggle)
identical.

## Removed

- `expanded` state, `toggleExpanded`, the chevron expand buttons, the
  `CommitLeaseDetail` component, and the conditional workspace column
  (`showWorkspace` / `activeColSpan` / `releasedColSpan`) — the workspace name is
  always the card title.

## Approaches considered

1. Cards with details still behind expand/modal — rejected: does not fix the
   stated complaint.
2. Leaner table without expand — rejected: intent/paths text is too wide for a
   table on small screens and drifts from the Workspaces pattern the user asked to
   copy.
3. Card grid copying Workspaces (picked): inline details, consistent with the rest
   of the dashboard.

## Verification

- `pnpm --filter eling-dashboard exec tsc --noEmit` (per repo rule: never
  `pnpm build` while the dev server runs on 4427).
