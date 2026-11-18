# Realtime Skills Page via SSE — Design

> **Date:** 2026-11-17 · **Status:** Approved in conversation (approach C) · **Size:** Small

## Intent

The `/dashboard/skills` page must reflect the machine-global skill catalog live. Today it fetches once on mount and never again; a skill added, updated, renamed, trashed, restored or adopted from any tool while the page is open stays invisible until a manual reload. Goal: the list — and the open detail panel — refresh automatically when the catalog changes, including changes made by tools that never call eling's APIs.

## Goals

- Skill added/updated/removed from **any** source (eling MCP tools, dashboard REST, or external tooling writing the catalog folder directly) appears in the open page without a manual reload.
- An open detail panel re-fetches silently on the same signal — including while the user is in edit mode (user decision 2026-11-17: silent auto-reload, no confirm; the edit form keeps its local state because the panel does not remount).
- No new SSE endpoint: reuse the shared `/api/events/memories` stream with a new topic `skills`, mirroring the `commit-lease` precedent.

## Non-goals

- No deletion of the manual Refresh/Rescan buttons (they stay).
- No watching of the trash folder (`~/.agents/.trash/skills`): eling-mediated trash/restore already publishes through the notifier path.
- No per-skill delta payloads on the wire — the frame stays a bare topic string (`skills`); clients re-fetch.
- No change to `/api/skills*` REST contracts.

## Approaches considered

- **A — Watcher only** (databases pattern): one `SkillCatalogWatcherService` watches the catalog folder, debounces, runs the catalog scanner, broadcasts `skills`. Covers everything on disk but routes even eling-mediated mutations through the ~1 s tick, and leaves the existing `ISkillChangeNotifier` seam unused.
- **B — Explicit notifier relay only** (commit-lease pattern): wire `ISkillChangeNotifier` to the broadcaster / an HTTP relay. Instant, but blind to external tools that write the catalog without going through eling — the case the user explicitly wants covered. Rejected as sole mechanism.
- **C — Both (chosen)** (codebase watcher pattern): notifier relay gives instant refresh for eling-mediated mutations; the watcher + rescan covers external/disk writers and doubles as a safety net. Duplicate pings are harmless — rescans are idempotent and the client refetch is cheap.

## Design

### Backend (`Eling.Backend`)

1. **Topic.** `MemoryChangeBroadcaster` already relays arbitrary topic strings; add `skills` as the skill-catalog topic. The `/api/coordinator/notify-change` endpoint needs no change (it forwards the reason verbatim).
2. **Owner (HTTP host, `DashboardServices.Register`).** Register `ISkillChangeNotifier` as an in-proc adapter to the shared/in-proc `MemoryChangeBroadcaster`: `NotifyAsync` → `broadcaster.Notify("skills")`. (Mirrors `IMemoryChangeNotifier` → broadcaster.)
3. **MCP host (`McpHostBuilder`).** Register `ISkillChangeNotifier` → new `HttpCoordinatorSkillChangeNotifier(effectivePort)`, structurally identical to `HttpCoordinatorCommitLeaseChangeNotifier`: fire-and-forget POST of the JSON-serialized string `"skills"` to `/api/coordinator/notify-change`. Self-loopback when this process owns the port, correct relay when it does not.
4. **Catalog watcher (owner-only, new file `SkillCatalogDirWatcher.cs`).** An `IDisposable` watcher mirroring `AuditDirWatcher` (explicit `Start()` from `DashboardRoutes.Map`, tested through `SubscribeAsync`), simpler than `CodebaseWatcherService` because the scanner is a full reconcile rather than an incremental per-path indexer:
   - `FileSystemWatcher` over `SkillCatalogPaths.SkillsDirectory` (`~/.agents/skills` — not the whole `.agents` root) with `IncludeSubdirectories = true`, `NotifyFilter = LastWrite | FileName | DirectoryName`.
   - `public const string Topic = "skills"`.
   - Events do **not** schedule work. Each event just sets a `volatile int _dirty` flag (1 = a scan is pending).
   - Async loop (started in `Start()`, cancelled in `Dispose()`): `PeriodicTimer(1000ms)`; each tick, flag 0 → idle; flag 1 → clear the flag, run `ISkillCatalogScanner.ScanAsync()` (failures logged, never thrown), then `MemoryChangeBroadcaster.Notify(Topic)`. At most one scan per tick, no trailing-edge reset — a continuously-writing tool can never starve the refresh.
   - ctor `(ISkillCatalogScanner scanner, MemoryChangeBroadcaster broadcaster, string? skillsDirectory = null, TimeSpan? interval = null, ILogger<SkillCatalogDirWatcher>? logger = null)` — defaults keep production paths and the 1 s tick; tests inject a temp directory and a ~50 ms interval.
   - Burst of N writes → one pending flag → one scan → one SSE frame → one client refetch.
   - Watcher buffer overflow (`Error` event) sets the same flag (a full scan is already the reconcile); logged, never thrown.
   - The eling process's own catalog writes also trip the watcher — accepted (flag + idempotent rescan + duplicate SSE frame are all harmless).
   - Registered only in `isOwnerMode` (`DashboardServices.Register` owner block, wired to `SkillCatalogPaths.SkillsDirectory`); `app.Services.GetService<SkillCatalogDirWatcher>()?.Start();` added next to the other watchers in `DashboardRoutes.Map`. Peers own no SSE subscribers.
   - Why a rescan: the REST list reads the SQLite index, not the disk. Without the scan, a newly appeared external skill would never surface in the list even though the SSE ping fired. The startup reconciliation is already handled by `SkillCatalogScanHostedService`, so no catch-up pass is needed.
5. **No REST or DTO changes.** The skills page re-fetches through the existing endpoints; the detail 404 → trash fallback is already handled in the UI.

### Frontend (`Eling.Dashboard`)

6. **`use-memories-sse.ts`:** add `"skills"` to the `SseTopic` union. Default topic list unchanged.
7. **`SkillsList.tsx`:**
   - Subscribe once: `useMemoriesSse(handleSkillsChanged, undefined, ["skills"])`.
   - `handleSkillsChanged` = call the existing `load()` (list refetch, same no-store path) and bump a `detailNonce` state.
   - The detail-fetch effect gains `detailNonce` as a dependency alongside `selectedId`, so the open panel re-fetches silently — view or edit mode alike. The panel is keyed by skill id, so its edit-form local state survives the refetch untouched (silent, per user decision).
   - A selected skill that vanishes (deleted/trashed elsewhere) resolves to the existing 404 → trash notice path unchanged.
   - No loading-bar flash: `load()` already toggles `loading`, which only drives `LoadingBar`; this matches how SSE-driven refreshes feel on the memories page.
8. **Tests.** Frontend: extend `skills.test.tsx` with a stubbed `EventSource` asserting a `skills` frame triggers a list refetch and a detail re-fetch. Backend: scanner-watcher behavior covered by a test on the watcher debounce loop with a fake scanner + real `MemoryChangeBroadcaster` (or direct `Notify` assertion), plus `notify-change` round-trip coverage if not already present.

## Constraints

- No new SSE route; no wire-format changes beyond the existing topic-string frame.
- Fire-and-forget everywhere: a failing notification or scan never fails a mutation.
- Watcher runs on the dashboard owner only.
- Repo rules: split commits by layer, hooks mandatory, frontend verified with `tsc --noEmit` + live check in the browser on 4427 (never `next build` while the dev server runs).

## Follow-ups (out of scope, noted 2026-11-17)

- **Standard watcher wrapper.** The repo now has five near-identical watchers (`CodebaseWatcherService`, `CodebaseDirWatcher`, `DatabaseRegistryWatcher`, `AuditDirWatcher`, and this spec's `SkillCatalogDirWatcher`). User idea: extract a shared `BackgroundService` wrapper (watch path + filter + tick + async drain hook; overflow → full drain; clear-before-work; uniform logging/disposal) so future watchers are one-liners. Deliberately NOT done in this work — cross-cutting refactor, separate task.

## What's left to the builder

- Watcher tick is 1000 ms (aligned with the codebase watcher's 900 ms by user request — the 900 itself carries no technical meaning; aligning both to 1000 ms can happen separately).
- Exact shape of the watcher test fixture (fake scanner vs. temp catalog folder) is the builder's call.
- Whether to include the trash directory in the watch later — out of scope unless the user asks.
