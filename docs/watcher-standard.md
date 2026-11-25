# Watcher Standard (dashboard-owner ownership)

2026-11-25 · Applies to every on-disk change watcher that feeds an Eling list or dashboard view. Adopted surface by surface, never in a batch. A watcher is the fallback path for mutations that never pass through Eling — an external tool, an editor, or a git checkout can change a file without calling any Eling API.

## 1. Core rules

| Rule | Standard |
|---|---|
| Who watches | The **dashboard-owner process only**. MCP stdio processes never arm a watcher. |
| Scope | One watcher per active registered workspace, not one global watcher over unrelated roots. Only the workspace's covered paths are watched. |
| Startup | `Start()` first scans every active workspace, so edits made while the dashboard owner was down are still picked up. |
| Registry changes | When the workspace registry changes, re-arm the watcher set: add new roots, stop watching removed roots. |
| Event handling | One O(1) dirty flag per workspace. Clear the flag **before** reconciling; a write landing mid-scan re-arms it, and a burst of events coalesces into one scan per tick. |
| Failure | A scan failure is logged and the flag is re-armed, so the next tick retries even without a file event. |
| Reconcile | Reindex changed files and remove rows for deleted files. Tag every written row with its workspace id. A metadata-only change rebuilds that workspace's rows; it never rebuilds the whole catalog. |
| Broadcast | Publish on the in-process topic after the scan, carrying the workspace id. No cross-process SSE relay is introduced just to wake the dashboard. |
| Writers | The dashboard owner is the sole writer to the shared global index for watched changes. |

## 2. Why the dashboard owner and not stdio processes

MCP stdio processes are ephemeral: one exists only while an agent session is alive. A watcher attached to one misses every edit made while no session is running, so the catalog can stay stale for an unbounded time. The dashboard owner is the long-lived process and already owns the SSE subscribers, so its notifications need no cross-process transport.

Two more failure modes follow from per-process watchers:

- Several stdio processes for the same workspace can watch the same files, producing duplicate work and duplicate events.
- Each process would write to the same global index, adding contention without adding coverage.

The existing cross-process notifiers (`HttpCoordinatorMemoryChangeNotifier` and siblings) solve the opposite problem — relaying a mutation that already happened through Eling. They are not a reason to move watching into stdio processes.

## 3. Implementer contract

- `Start()` is idempotent; a disposed watcher cannot be restarted. Only the dashboard owner calls it.
- `ScanAsync(full, paths, workspaceId, ct)` is the reconcile. Watched changes call it with `full: false` and only the affected workspace.
- Events cover file creation, change, deletion, rename, and metadata-file changes under the workspace.
- The broadcast topic carries `workspace_id` so a dashboard filtered to one workspace refreshes only that scope.
- Every `catch` logs; no silent catch and no rethrow that kills the drain loop.

## 4. Adoption

| Surface | Status |
|---|---|
| Unified docs watcher (`docs/**/*.md` + `docs-metadata.json` + local overlay, one per workspace) | **Planned** — spec `docs/superpowers/specs/2026-11-24-docs-unified-design.md` §7, plan `docs/superpowers/plans/2026-11-24-docs-unified.md` Task 7 |
| Legacy `DocsDirWatcher` (one process, `docs/` + `.eling/docs/`) | Superseded by the unified docs watcher; retired with the managed/external split |
| Codebase, memory, and skill directory watchers | Legacy — not yet migrated; the watcher-sprawl follow-up is tracked separately |

## 5. Relation to other standards

Sorting (`sorting-standard.md`) decides how a list is ordered; this standard decides when the data behind that list is refreshed. They are independent: a workspace selection, a sort control, and a watcher event each travel on their own state.

Adoption is recorded here, not in each feature spec. A feature that introduces a watcher must add a row to §4 and link its spec from that row.
