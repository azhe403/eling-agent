# Codebase Search Stats & Tab Match Badges — Design

**Date:** 2026-11-01
**Status:** Draft
**Topic:** Search-stats section on `/dashboard/codebase` + actual match count per search tab

## 1. Intent & Why

Every codebase search should answer, at a glance: how long it took, how much of the
codebase it touched (files / chunks), and how many workspaces were involved. Today the
result list shows nothing of the kind — the execution time already measured server-side
is only used for the audit trail, and a tab switched to in the middle of parallel
investigations carries no memory of what its last search found. The stats ribbon sits
directly under the search box; the tab badge carries the last match count per tab.

## 2. Goals

- Ribbon under the search box showing: matched hits, matched files, matched chunks,
  execution time (ms), workspaces involved (searched + with hits), search-scope mode,
  and the limit.
- Each search tab shows the actual match count of its last completed search as a badge
  on the tab strip, next to the existing workspace-count badge.
- Stats are per-tab state (persisted with the tabs in `localStorage`), so switching
  tabs, reloading, or back/forward restores both the ribbon and the badge.
- Empty query ("top" mode) shows no ribbon — there is nothing to measure.

## 3. Non-Goals

- Global totals beyond the returned page: counts reflect the results actually returned
  (limit-capped), and the ribbon says so ("of top N").
- Client-side timing — the server stopwatch is authoritative; client timing would mix
  in proxy/network latency.
- No extra SQL COUNT queries per federated root (kept out on purpose; revisit only if
  the "of top N" framing proves misleading).

## 4. Approaches (decided)

| Approach | Tradeoff | Status |
|---|---|---|
| **Backend stats block in the search response** | One extra property on the existing response; time is server-pure; counts are derived from the hits already returned. | **Chosen** |
| Client-only derivation | Zero backend diff, but timing is network-polluted and workspace counts are approximate. | Rejected |
| Hybrid (backend stats + client fallback) | Fallback is dead code — the endpoint always answers. | Rejected |

Counts semantics (chosen together with approach 1): derived from the returned results.

## 5. Backend — `GET /api/codebase/search`

File: `src/backend/Eling.Backend/Endpoints/CodebaseEndpoints.cs`

Add a `stats` block to the success responses of the two real-search modes ("search"
and "files"); omit it from the empty-query "top" listing. The stopwatch already kept
for the audit entry is reused, so no new timing code.

```jsonc
// both "search" (content) and "files" (path/file) modes gain:
"stats": {
  "tookMs": 18,                  // server-side, excludes network
  "hits": 14,                    // results.Count — the tab badge value
  "files": 4,                    // "search": distinct paths; "files": results.Count
  "chunks": 14,                 // "search": results.Count; "files": Σ ChunkCount
  "workspaces": 3,              // roots searched (single project = 1)
  "workspacesWithHits": 2       // distinct projectRoots among the returned rows
}
```

No audit change: `RecordSearchAsync` already receives the hit count and duration.

## 6. Frontend — Dashboard

Files under `src/frontend/Eling.Dashboard/src/app/dashboard/codebase/`:

- **`searchTabs.ts`** — `SearchTab` gains `lastStats?: SearchStats` (optional, so
  every existing stored tab deserializes unchanged); `loadTabs` passes the field
  through when it is an object, drops it otherwise. `SearchStats` mirrors §5 plus
  `mode: "search" | "files"` so the ribbon labels the numbers correctly.
- **`CodebaseList.tsx`** — `doSearch` stores `data.stats` on the active tab via
  `updateTab` on completion (same `searchSeq` guard that protects the results),
  clears it on error/empty query; renders the ribbon between the hint line and the
  result list; clears a stale stats write on scope switches that reset the lists.
- **`SearchStatsRibbon.tsx`** (new) — one compact row, ribbon style (chosen layout):
  `⚡ 14 matches · 📄 4 files · 🧩 14 chunks · ⏱ 18 ms · 🌐 3 workspaces (2 with
  hits) · scope Content · limit 20`. Numbers that are "of top N" keep their label
  when the limit trimmed the set. Hidden when the active tab has no last stats.
- **`SearchTabsBar.tsx`** — match-count badge from `tab.lastStats?.hits`, shown
  beside the existing workspace-count badge; hidden when absent.

## 7. Verification

- Backend: extend the existing codebase endpoint tests to assert the `stats` block
  (counts, both modes, top-mode omission). Build to a separate output folder — the
  dev backend runs under `dotnet watch` on 4417 and must not be killed.
- Frontend: `pnpm test` (tab mapping + ribbon render) and `pnpm build`.
- Manual: two tabs with different queries — badges and ribbons survive a tab switch
  and a page reload.
