# Server-side workspace grouping

**Date:** 2026-10-26
**Status:** Implemented
**Scope:** `Eling.Core`, `Eling.Backend`, `Eling.Dashboard`, `Eling.Desktop`

## 1. Problem

The workspace list is arranged three ways — flat, one entry per git repository, or
nested by the folders that hold the workspaces. That arrangement is currently
computed by the clients, and it has been written three times:

| Implementation | Surface |
|---|---|
| `src/frontend/Eling.Dashboard/src/components/workspaces/grouping.ts` | dashboard sidebar, dashboard picker popover |
| `src/desktop/Eling.Desktop/Services/WorkspaceGrouping.cs` | desktop Folders page |
| `src/desktop/Eling.Desktop/Services/MemoryWorkspaceTree.cs` | desktop Memory panel tree |

All three implement the same rules: bucket by `repositoryRoot` (case- and
separator-insensitive), pick the main checkout as the primary member, nest linked
worktrees under it, and for the folder mode nest each repository under the nearest
other repository whose path contains it. The rules agree today by coincidence of
copying, not by construction. A fix to path normalisation, or to how a repository
with no registered main checkout is labelled, has to be made in three places and
will silently diverge the moment one is missed.

## 2. Intent

One server-side definition of the arrangement, so every client renders the same
tree and no client computes one.

## 3. Goals

- The grouping rules exist in exactly one place, in code both the backend and the
  desktop can reach (`Eling.Core`).
- The dashboard and the desktop receive a ready-to-render tree; neither runs
  grouping code.
- The mode toggle (`flat` / `repo` / `folder`) keeps working, with the person's
  choice still remembered across restarts.
- Filtering (search text, active/offline) is applied by the same server call that
  groups, so a filtered list is still a server-computed tree.

## 4. Non-goals

- Process metrics (pid, memory, CPU, uptime) and codebase statistics (files,
  chunks, db size) are **not** folded into this endpoint. `WorkspaceAggregator.cs`
  and `aggregate.ts` keep their current job. Folding them in is a separate,
  larger change and is explicitly out of scope here.
- No change to the `GET /api/coordinator/runtimes` contract, and no change to
  `GET /api/agent/workspaces` (the registry CRUD routes).
- No change to how a workspace's `repositoryRoot` is resolved; the existing
  `GitRepositoryResolver` keeps that job.
- Expanding and collapsing nodes, indentation, and checkbox state stay in the UI.

## 5. Anti-goals

- Shipping a second, "temporary" client-side grouping path for offline use. That
  would restore the drift this change exists to remove.

## 6. Constraints

- **Measured cost** (live backend on `127.0.0.1:4417`, curl, warm, 5 runs):
  `/api/tools` ≈ 8.6 ms is the framework floor;
  `/api/coordinator/runtimes?includeRegistered=true&allProcesses=true` ≈ 36 ms and
  25.7 KB for 41 runtime rows; `/api/coordinator/projects/codebase-statuses` ≈
  36.6 ms. About 27 ms per list call is real work above the floor, because
  `GetRuntimesAsync` resolves the repository root for every row by walking the
  filesystem for `.git` markers, with no caching. This machine: 41 runtime rows,
  40 distinct workspaces, 25 distinct repositories, 5 alive.
- A grouping call therefore costs roughly one existing runtimes call. It must not
  be issued per keystroke.
- The dashboard reaches the backend through the Next.js proxy on port 4427
  (`/api/*` → 4417).
- New backend routes go in a controller, not the minimal-API style being retired
  (`CoordinatorController` already documents this).

## 7. Approaches considered

**A. One endpoint, one mode per request** — `GET .../workspaces?group=repo`
returns the tree for that mode. Smallest contract. Chosen.

**B. One endpoint, all three trees in one payload** — instant toggling, no
per-mode request, but a larger payload and a contract that answers three questions
at once.

**C. Canonical node graph plus per-mode roots** — smallest payload, but the client
still walks the graph to nest it, which is grouping logic leaking back across the
seam.

The final design is **approach A**: one endpoint, one arrangement per request,
selected by the `group` query parameter. `rows` is always sent so node
references stay cheap and the client can join `nodes` against `rows` by root.
Section 8.1 below is the authoritative contract.

## 8. Design

### 8.1 Endpoint

```
GET /api/coordinator/workspaces?group=flat|repo|folder&status=all|active|offline&q=<text>
```

- Lives on `CoordinatorController`, next to `/runtimes`. Under `/coordinator` so it
  cannot be mistaken for the `/api/agent/workspaces` registry CRUD routes.
- `group` selects the arrangement: `flat`, `repo`, or `folder`. Missing or invalid
  → `400`. The response carries `rows` plus **that single arrangement** as
  `nodes`, so each mode is a fresh, authoritative server answer — no client keeps
  a per-mode cache and no mode switch is a client-side lookup.
- `status` and `q` filter **before** grouping, so a filtered list is still a
  server-computed tree. `q` matches the workspace name, its root, or any owning
  process id, case-insensitively.
- Row source is `RuntimeRegistry.AliveOrRegisteredAsync(ct, allProcesses: true)`,
  the same call `WorkspaceList` consumers make today. Rows where
  `CodebaseEnabled == false` and the `UserScope` sentinel are excluded, as both
  clients do now.
- `repositoryRoot` per row still comes from `GitRepositoryResolver`.

Response:

```json
{
  "group": "repo",
  "rows": [
    {
      "root": "/work/org/alpha",
      "name": "alpha",
      "repositoryRoot": "/work/org/alpha",
      "alive": true,
      "codebaseEnabled": true,
      "processCount": 2
    },
    {
      "root": "/work/org/beta/beta-order",
      "name": "beta-order",
      "repositoryRoot": "/work/org/beta",
      "alive": false,
      "codebaseEnabled": true,
      "processCount": 0
    }
  ],
  "nodes": [
    {
      "id": "repo:/work/org/beta",
      "label": "beta",
      "path": "/work/org/beta",
      "root": null,
      "roots": ["/work/org/beta", "/work/org/beta/beta-order"],
      "isRepository": true,
      "isWorktree": false,
      "children": [
        {
          "id": "ws:/work/org/beta/beta-order",
          "label": "beta-order",
          "path": "/work/org/beta/beta-order",
          "root": "/work/org/beta/beta-order",
          "roots": ["/work/org/beta/beta-order"],
          "isRepository": false,
          "isWorktree": true,
          "children": []
        }
      ]
    }
  ]
}
```

`rows` is the flat list, sent once so node references stay cheap: a node names its
workspaces by `root`/`roots`, and the client joins them against `rows` by root.
`nodes` is the arrangement for the requested `group`. `isRepository` is true for
a repository node (one whose `id` starts with `repo:`). A repository node's
`root` is its main checkout when that checkout is itself registered, otherwise
`null` — the same distinction `WorkspaceNode.row` carries today. Three rules
visible from the example:

1. **`root` may be `null`.** A repository node whose main checkout is not itself
   a registered workspace still stands for the group; its checkbox scope comes
   from `roots[]`, not `root`.
2. **`roots[]` is the checkbox scope.** For a leaf it holds one path; for a
   repository node it holds every checkout in that group.
3. **Idempotent across modes.** The same workspace produces identical `id` and
   `root` values in `flat`, `repo`, and `folder`, so expansion and selection
   persist when the mode changes.

### 8.2 Where the rules live

New `Eling.Core/Workspaces/WorkspaceTree.cs` — a pure static module:

- `Build(IReadOnlyList<WorkspaceRow> rows, WorkspaceGroupMode mode) → IReadOnlyList<WorkspaceTreeNode>`
- path normalisation (`PathKey`), ancestry test, repository bucketing, primary
  selection, and folder nesting, moved verbatim in behaviour from the three
  existing implementations.

`Eling.Core` because it has no ASP.NET dependency, is already referenced by both
`Eling.Backend` and `Eling.Desktop`, and is the natural home for the Core test
project. The backend endpoint becomes a thin adapter: fetch runtimes → project to
rows → resolve repository roots → filter → `WorkspaceTree.Build` → map to DTO.

### 8.3 What gets deleted

- `src/frontend/Eling.Dashboard/src/components/workspaces/grouping.ts` — whole
  file. `WorkspaceNode` becomes a plain DTO type in the workspaces module;
  `WorkspaceGroupMode` moves to the DTO module and is still imported by the mode
  preference hook.
- `src/desktop/Eling.Desktop/Services/WorkspaceGrouping.cs` — whole file. `Mode`
  moves to `Eling.Core` (shared with the endpoint contract).
- `MemoryWorkspaceTree.Build` and its private helpers (`RepositoryNodes`,
  `FolderNodes`, `BuildFolder`, `CreateLeaf`, `CreateRepository`,
  `RepositoryGroups`, `NearestContainer`, `PathKey`, `NameOf`, `IsAncestor`).
  What survives in `MemoryWorkspaceTree` is presentation only: `FlattenVisible`,
  `ApplyExpansion`, `RefreshAggregates`.

### 8.4 Client behaviour

The mode toggle remains a client preference (`useWorkspaceGroupMode` localStorage /
`DesktopSettingsStore`), because it is a person's reading choice, not a fact about
the machine. Only the tree moves to the server.

A mode change is a fresh `?group=…` request for that mode — that is the point of
approach A: every tree the client renders is a current, authoritative server
answer, so there is no per-mode cache to invalidate and no stale tree that can
outlive its rows. The client never computes a tree; it only asks which
arrangement to render and renders the `nodes` the server returns.

This is not grouping: the client never decides what belongs under what; it only
picks which pre-computed arrangement to request.

Two surfaces on the same page (the scope panel and the list beside it) ask for
the same mode, so they share one request rather than issuing two; a mode switch
is still a fresh request, because the mode changed.

### 8.5 The tree's open/closed shape

Which nodes the user opened and closed is a reading choice, like the mode, so it
is remembered — and remembered in one place, so the desktop app and the
dashboard show the same tree:

- `GET/PUT /api/coordinator/workspaces/expanded` carries
  `{ expanded: string[], collapsed: string[] }` — node ids the user explicitly
  opened and closed. A node nobody touched keeps the default for its kind (a
  repository with several checkouts starts open).
- `Eling.Core/Workspaces/WorkspaceExpansionStore.cs` owns the file, under the
  user's config directory. The backend is the only writer, so there is exactly
  one copy.
- Both clients read it on load and write it after a toggle. A failed read keeps
  the shape already in memory rather than pretending the user closed
  everything.

### 8.6 Consumers

| Consumer | Change |
|---|---|
| `use-workspace-rows.ts` | fetches `.../workspaces?group=…` for the requested mode, exposes `rows` + `nodes`; one store per page, so two surfaces on the same mode share one request |
| `workspace-tree.tsx` | renders the `nodes` it is given; no `buildWorkspaceTree` call; expansion comes from the shared store |
| `workspace-scope-panel.tsx`, `workspace-select.tsx` | pass `nodes` instead of `rows` + `mode` |
| `CodebaseList.tsx`, `MemoriesList.tsx` | unchanged call site; the hook now returns a node list too |
| `FoldersViewModel.cs` | `RebuildGroupedNodes` maps the API `nodes` instead of calling `WorkspaceGrouping.Build`; a filter or mode change re-fetches |
| `MainViewModel.cs` | maps the API `nodes` into the memory sidebar instead of `MemoryWorkspaceTree.Build`; reads and writes the shared expansion |

## 9. Testing

- `Eling.Core.Tests`: the tree rules — repository bucketing, primary selection,
  worktree nesting, folder nesting by nearest container, a workspace outside any
  repository, mixed path casing and separators, and the no-registered-main-checkout
  case. These replace the current `MemoryWorkspaceTreeTests` coverage of `Build`.
  The expansion store's round trip, de-duplication and corrupt-file handling are
  covered here too.
- `Eling.Backend.Tests`: the endpoint — mode selection, `status`/`q` filtering
  before grouping, `UserScope` and `codebaseEnabled: false` exclusion, and that a
  repository node reports `root: null` when its main checkout is not registered.
  The expansion endpoints are covered as a round trip.
- `Eling.Desktop.Tests`: keep flatten/expand/collapse tests against a tree parsed
  from a canned API response.
- Dashboard: a test that a mode switch issues a fresh request, and that two
  surfaces on the same mode share one.

## 10. Risks

- **Debounced refetch on filter keystrokes.** The desktop Folders search now costs
  a request per pause. Mitigated by a ~150 ms debounce; accepted because the
  alternative is client-side set logic.
- **Contract drift if a client keeps a fallback path.** Guarded by deleting the
  client implementations outright rather than leaving them unused.
- **`GitRepositoryResolver` per row remains the dominant cost** of this endpoint
  (~27 ms). Not addressed here; a per-root memo in the coordinator would help and
  is worth a separate change.

## 11. Builder's calls (made here, not by the user)

- Route under `/api/coordinator/` rather than a new `/api/workspaces` root, to
  avoid collision with the registry CRUD routes.
- `status` and `q` accepted by the endpoint rather than filtering in the client.
- Metrics and codebase statistics left out of the payload (section 4).
- Default mode `repo`, matching both clients' current default.

## 12. Implementation notes

The endpoint ships as `GET /api/coordinator/workspaces?group=flat|repo|folder&status=…&q=…`
— **approach A** from section 7. One arrangement per request, selected by the
`group` parameter; the response carries `rows` plus that single arrangement as
`nodes`. Switching mode is a fresh request, so no client keeps a per-mode cache
and no tree can outlive its rows.

Concrete implementation notes:

- The wire contract is `{ rows, group, nodes }`; node shape
  `{ id, label, path, root, alive, roots[], isRepository, isWorktree,
  children[] }`; row shape `{ root, name, repositoryRoot, alive,
  processCount }`. `processCount` counts live processes only, so a
  remembered workspace whose process has exited reports `0`.
- The dashboard's `useWorkspaceTrees(mode)` issues one request per mode. One
  store per page holds the current answer, so a scope panel and the list beside
  it that share a mode share one request. The store remembers which mode the
  page is asking for and stamps each request with a per-mode generation, so an
  answer for a mode the reader already left — or a superseded answer for the
  same mode — is dropped rather than rendered under the wrong arrangement.
- The desktop `MainViewModel` keeps the last good response in a `_trees` field
  and rebuilds the memory sidebar from it; a mode toggle re-fetches, and a
  superseded request is cancelled so it can never overwrite the new answer.
  The Folders page refetches through `GetWorkspaceTreesAsync(group, status, q)`
  on a filter change, debounced ~150 ms.
- The tree's open/closed shape lives in the backend
  (`GET/PUT /api/coordinator/workspaces/expanded`, section 8.5). Both clients
  read it when they load the tree and again on a runtime refresh, and write it
  after a toggle — a toggle is never followed by a re-read, so a failed write
  cannot be overwritten by the copy it failed to update.
- All wire examples in this document and in the Core tree tests use dummy
  paths (`/work/org/alpha`, `/work/org/beta`, …) and neutral names — no real
  machine paths or repository names.
