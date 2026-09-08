# MemoriesList Split — Design (Approach A)

- Status: design approved (approach A, readability-first). Spec uncommitted, awaiting user review before implementation.
- Date: 2026-09-04
- Target: `src/frontend/Eling.Dashboard/src/app/dashboard/memories/MemoriesList.tsx` (628 lines, `"use client"` container)

## Goal

Make the file easy to scan. Success = a reader can understand the screen's structure from the container alone, and each extracted unit answers "what does it do, how do I use it" without reading its internals. No behavior change: same endpoints, same error paths, same filter/sort semantics.

## Why this split

The container mixes five concerns: fetch state plus three effects plus SSE wiring (lines 45–205), four mutations that each hand-build scope-aware URLs (207–286, with the same scope→URL branching duplicated a fifth time inside `load`), ~150 lines of promote/delete dialog JSX (476–625), and the toolbar/grid JSX that is the file's actual job (306–474). Project standards flag exactly this shape: `react-patterns.md` lists "massive components" as an anti-pattern, `code-quality.md` mandates splitting god modules, and `clean-code.md` flags the duplicated URL branching as a DRY violation.

## Rejected alternatives

- **B (lib + dialogs only, effects stay):** less churn, but the three-effects-plus-SSE cluster is the hardest part to scan, so it misses the goal.
- **C (also split toolbar into StatusBar/FilterToolbar/MemoryGrid):** threads 10+ props through three new components — the prop-drilling pattern `react-patterns.md` warns against — for marginal gain. YAGNI.

## Architecture

| File | Responsibility | Approx size |
|---|---|---|
| `src/hooks/use-memories-data.ts` (new) | memories/loading/error state, `load`, `loadRuntimes`, mount/scope effect, 5s registration-poll effect, SSE wiring, and the four mutations (`remove`, `save`, `promote`, `copyToProject`) — mutations live here because they write `setMemories` / call `load` | ~200 lines |
| `src/lib/memory-urls.ts` (new) | pure `buildListUrl(scope)` + `buildMemoryUrl(...)` builders; single home for all scope→URL branching | ~60 lines |
| `src/app/dashboard/memories/PromoteDialog.tsx` (new) | presentational promote dialog; props in, callbacks out | ~100 lines |
| `src/app/dashboard/memories/DeleteDialog.tsx` (new) | presentational delete dialog; props in, callbacks out | ~50 lines |
| `src/hooks/use-clipboard.ts` (new) | `copiedId` state + `copyToClipboard` (keeps the `.catch()` that suppresses Clipboard API rejections) | ~20 lines |
| `MemoriesList.tsx` (shrinks) | toolbar JSX, grid/loading/empty/error states, `editingId` UI state, dialog open-state wiring | ~250 lines, mostly markup |

## Data flow

```ts
// use-memories-data
{
  memories, runtimes, loading, error,
  scope, setScope,
  reload: (triggeredBy?: string) => Promise<void>,
  remove(m: Memory): Promise<void>,
  save(id: string, body: { content: string; type: string; status: string }): Promise<void>,
  promote(m: Memory, move?: boolean): Promise<void>,
  copyToProject(m: Memory, targetRoot: string): Promise<void>,
}
```

```ts
// memory-urls (pure, no React)
buildListUrl(scope: string): string
buildMemoryUrl(args: { id: string; scope?: string; projectRoot?: string; method: "GET" | "DELETE" | "PATCH" }): string
```

Dialogs receive their target object plus `onConfirm`/`onCancel` callbacks and own no fetch logic. The container passes `reload` into SSE wiring and the refresh button exactly as today.

## Error handling

Unchanged by construction. Fetch failures feed the same `setError` paths, promote/copy failures set the same messages, the mount-effect `isMounted` guard and listener cleanup move verbatim into the hook, and the clipboard `.catch()` moves verbatim into `use-clipboard`. No new error states are introduced.

## Constraints

- All new files stay under the existing `"use client"` boundary (imported by the container); no new server/client decisions.
- Dialogs colocate in the route dir next to `MemoryCard`/`MemoryEditor`; shared hook and URL builders go in `src/hooks/` and `src/lib/` per the dashboard README layout.
- Filenames kebab-case (`use-memories-data.ts`, `memory-urls.ts`), one primary export per file, import order builtins → external → internal (per `typescript.md`).
- Pre-implementation requirement (dashboard AGENTS.md): read the version-pinned guide in `node_modules/next/dist/docs/` before writing code — this Next.js version has breaking App Router changes.

## Verification

- `pnpm build` + lint in `src/frontend/Eling.Dashboard`, zero warnings attributable to new files.
- Manual pass over the Memories route: list, scope filter, search, type filter, edit/save, copy-ID, promote (copy + move), copy-to-project, delete, SSE live update, refresh button.
- No test updates: no existing tests cover this route's markup; backend per-csproj test rule unaffected.

## Out of scope

Toolbar/status-bar/grid decomposition, `MemoryCard`/`MemoryEditor` changes, any backend change, any behavior or endpoint change.
