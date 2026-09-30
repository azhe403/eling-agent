# Batch Memory Delete (`memory_delete_many`) — Design

**Date:** 2026-09-29
**Status:** Draft for review
**Scope:** `Eling.Core` (chain-resolving delete on `IScopedMemoryService` / `ScopedMemoryService`, new `ScopedDeleteResult`), `Eling.Backend` (new MCP tool + result DTO), tests in `Eling.Backend.Tests`
**Related:** `docs/superpowers/specs/2026-09-09-scope-chain-adoption-design.md` (scope chain + ancestor targeting), `docs/nested-memory.md`

> All project names and paths in this document are anonymized placeholders
> (e.g. `C:\work\acme\acme-platform`). No real usernames or machine paths
> appear here, per the project hygiene rule.

---

## 1. Goal

Let an agent delete several memories in **one** call instead of N.

Today `memory_delete` takes exactly one `id`. An agent cleaning up after a
merged `memory_recall` holds a list of ids that came from *different* scopes at
once — own project, an ancestor, global — and must issue one call per id. Each
call pays its own `NotifyAsync` **and** its own full index rebuild, so clearing
50 memories costs 50 notifications and 50 rebuilds. Two problems, one fix:
accept a batch, and pay the notification/rebuild cost once.

---

## 2. Non-Goals (YAGNI)

- **No change to `memory_delete`.** It keeps its single-`id`, `bool`-returning
  signature. A separate tool carries zero migration risk for existing callers
  and for agent habits already formed around the singular verb.
- **No REST/endpoint parity.** The dashboard deletes one id at a time via
  `ScopedMemoryEndpoints`; that path is unaffected.
- **No dry-run flag.** Validate-then-delete ordering plus the per-item report
  (§5) already tell the agent what will happen, and dry-run flags on
  destructive tools tend to be omitted under pressure.
- **No cap on `ids.Length`.** Agents realistically pass a handful. A ceiling is
  a one-line change if it is ever needed (§11).
- **No tag/filter-based deletion** ("delete everything tagged X"). That is a
  different, much wider blast radius, and `memory_maintenance` already owns
  bulk detection-and-removal.
- **No restore, soft-delete, or undo.** `memory_delete` is permanently
  destructive; this inherits that.

---

## 3. Tool surface

```
memory_delete_many(ids: string[])
```

That is the whole signature. Notably absent: `scope` and `project`.

**Why no `scope`.** `memory_delete` routes to exactly one target, so it needs
them. This tool resolves each id independently (§4), which makes a single-scope
parameter meaningless — and worse, actively misleading: an agent that passed
`scope: "project"` over a list containing a global id would get a silent
not-found for that row. Dropping the parameters removes the contradictory
input space entirely.

**Description.** Must state plainly that the tool **searches the whole scope
chain including global**, so an agent does not fire it at a list it only
skimmed. This is a wider blast radius than `memory_delete`, which defaults to
the project scope. The description is the only guard against a careless call.

---

## 4. Resolution across the chain

For each id, walk the scope chain **nearest-first** — own scope, then each
ancestor, then global — and delete from the first level that actually holds the
memory. This is exactly the surface a `scope=merged` read covers, so any id
harvested from `memory_recall` / `memory_search` / `memory_list` is reachable.

**No own-scope guard.** `memory_save` blocks project writes when
`!HasOwnScope` and `memory_delete(project=...)` requires an own scope, because
those calls *choose* a target and need consent to write outside the workspace.
This tool does not choose — the caller named exact ids. Blocking on scope
posture would fail a call whose intent is unambiguous, and merged reads already
surface those memories to the agent in the first place. The user's explicit
decision: **if the agent knows the id, delete it, no guard.**

**Why resolution must not go through `ResolveProjectLevel`.**
`ScopedMemoryService.ResolveProjectLevel` (`:193`) falls back to
`_levels[0].Service` when a supplied root matches no chain level (`:208`) —
it cannot fail loudly. For a *read* that is a convenience; for a **destructive**
call it would delete from the wrong scope and report success. The batch walk
therefore resolves level-by-level and deletes only where the memory was
actually found, so an unmatched root is structurally impossible.

**Probe, do not list.** Resolution uses `GetByIdAsync` per level, not
`ListAllAsync`. A 3-id batch against a 10,000-memory store costs 3 file reads
per level instead of deserializing the entire store at every level.

**Uninitialized workspaces** are not a special case: `ChainRoots` is simply
empty, only global is reachable, and any project id reports not-found. No scope
is ever created.

---

## 5. Failure semantics

Two distinct classes of bad input, handled differently on purpose.

**Malformed id → refuse the whole batch.** Every id is parsed as a ULID
*before* any deletion. If one is not a valid ULID, throw `ArgumentException`
naming the offender; nothing is deleted. This is the validate-at-the-boundary
rule, and it guarantees no half-applied state from a typo — a caller that
mistranscribed an id finds out before any data is gone.

The check lives in the **tool**, not in Core: the tool parses
`ids: string[]` into `MemoryId` values and only then calls
`DeleteAcrossChainAsync`, whose parameter is already `IReadOnlyCollection<MemoryId>`.
A malformed id therefore never reaches the storage layer, and the exception
message can name the offending string as the caller typed it.

**Valid but absent → tolerate and report.** Once the ids are known well-formed,
a missing memory is an ordinary outcome, not an error. It is reported with
`deleted: false` and the rest of the batch proceeds. This matches the actual
cleanup use case: of the 50 ids you are clearing, several are frequently already
gone (deleted by a previous run, or by `memory_maintenance` cleanup).

The alternative — all-or-nothing including not-found — was rejected: one
already-deleted memory would abort the entire call and leave the agent unable
to tell what state the store is in. Fully best-effort was also rejected: a typo
that silently "succeeds" is the dangerous failure mode for a destructive tool.

---

## 6. Result shape

Per-item provenance is the point of the response, not a nicety — without it the
agent cannot tell "deleted from my workspace" from "deleted from my global
store," which is exactly the distinction that matters when a cleanup misbehaves.

Field names deliberately **match what recall already emits** (flat `scope`,
`projectName`, `projectRoot`, null for global) so the agent learns no new
vocabulary reading a delete result.

```json
{
  "requested": 3,
  "deleted": 2,
  "notFound": 1,
  "items": [
    { "id": "01J…", "deleted": true,  "scope": "project", "projectName": "acme",      "projectRoot": "C:\\work\\acme" },
    { "id": "01M…", "deleted": true,  "scope": "global",  "projectName": null,        "projectRoot": null },
    { "id": "01K…", "deleted": false, "scope": null,      "projectName": null,        "projectRoot": null }
  ],
  "searchedScopes": ["acme (project)", "global"]
}
```

- `items` is in **input order**, so the agent can zip its input list against
  outcomes positionally.
- `deleted` / `notFound` are counts, for the primary job: confirming "3 in, 3
  accounted for".
- `searchedScopes` is present **only when something was not found**. It turns a
  dead end into a diagnosis — "this id genuinely is not in this workspace"
  versus "the tool looked somewhere I did not expect".

---

## 7. Efficiency

The real win, beyond ergonomics.

Today, N deletions cost N `NotifyAsync` calls and N index rebuilds. This tool:

1. Collects which levels were genuinely touched while deleting.
2. Fires **one** `NotifyAsync("mcp")` for the whole batch, and only if at
   least one memory was actually deleted.
3. Runs `RebuildProjectIndexAsync(root)` once per project level touched, plus
   `RebuildIndexAsync("global")` only if global was touched. Precise, and never
   more expensive than the work justified.

Fifty deletions across three scopes: **1 notification + 3 rebuilds**, versus
today's 50 + 50.

---

## 8. Core API

The chain lives in the private `_levels` field of `ScopedMemoryService` and is
unreachable from a tool — the interface exposes `ProjectService` (head only)
and `GlobalService`, but nothing handing back an arbitrary ancestor's service.
Rebuilding a `MemoryService` per root inside the tool would duplicate storage
and index construction that Core already owns, and risks path-layout drift in a
destructive path. So the walk goes into Core, where the chain lives.

```csharp
Task<IReadOnlyCollection<ScopedDeleteResult>> DeleteAcrossChainAsync(
    IReadOnlyCollection<MemoryId> ids);
```

New record beside `ScopedMemory`:

```csharp
public sealed record ScopedDeleteResult(
    MemoryId Id,
    bool Deleted,
    MemoryScopeKind Scope,
    string? ProjectRoot);
```

`Scope` / `ProjectRoot` are meaningful only when `Deleted` is true; a not-found
id has no origin to report. The tool maps this to the DTO, nulling both.

**Ripple check.** `IScopedMemoryService` has exactly one production
implementation (`ScopedMemoryService`) and one test fake
(`FakeScopedMemoryService`, `MemoryWriteToolProjectScopeTests.cs:73`). The six
DI registrations all build the real service. The interface grows by one method
and one fake gains one method.

---

## 9. Files

| File | Change |
|---|---|
| `Eling.Core/Memory/ScopedDeleteResult.cs` | **new** — result record |
| `Eling.Core/Memory/IScopedMemoryService.cs` | add `DeleteAcrossChainAsync` |
| `Eling.Core/Memory/ScopedMemoryService.cs` | implement over `_levels` + global |
| `Eling.Backend/Mcp/Tools/MemoryDeleteManyTool.cs` | **new** — tool class only |
| `Eling.Backend/Dtos/DeleteMemoriesResultDto.cs` | **new** — response DTO |
| `tests/Eling.Backend.Tests/MemoryDeleteManyToolTests.cs` | **new** — tests |
| `tests/.../MemoryWriteToolProjectScopeTests.cs` | fake gains the new method |
| `docs/nested-memory.md` | list the new tool alongside `memory_delete` |

Conventions honored: one top-level type per file; tool file holds only the tool
class; DTO lives in `Dtos/`; records chopped one parameter per line; no
`ValueTuple` anywhere; XML doc comments on the new public interface method and
record.

---

## 10. Testing

In `Eling.Backend.Tests`, following the existing
`MemoryWriteToolProjectScopeTests` pattern (hand-rolled fake, real filesystem
fixtures under `eling-dummy-*` temp dirs).

- Multi-id happy path: all ids resolve and delete.
- One batch spanning levels: ids land in different scopes; each row reports the
  scope and project it came from.
- Ancestor resolution: a memory in an ancestor is deleted from the ancestor, not
  silently from the head (regression guard for the `ResolveProjectLevel`
  fallback hazard).
- Not-found tolerated: a valid-but-absent id yields `deleted: false` and the
  rest still delete; `searchedScopes` present.
- `searchedScopes` omitted entirely when every id was found (guards §6's
  conditional field against being emitted unconditionally).
- Malformed id aborts: an invalid ULID throws and **nothing** is deleted.
- Ordering: `items` matches input order.
- Provenance: global rows report `scope: "global"` with null project fields.
- Efficiency: exactly one notification regardless of batch size; one rebuild
  per touched level.
- No own scope: deletion from an ancestor succeeds without an own scope
  (the no-guard decision, locked by test).
- Uninitialized workspace: project ids report not-found; global ids still
  delete.

Run per-csproj with `--artifacts-path .bin-test`, never solution-wide.

---

## 11. Known limitations (accepted)

- **No cap on `ids.Length`.** A very large batch completes all its file deletes
  before the first rebuild. Bounded by the caller's list, and realistically
  small; a ceiling is a one-line change if it is ever needed.
- **Sequential deletes.** No batching at the storage layer. Correct and simple;
  parallel file deletes would add ordering nondeterminism for no realistic gain.
- **No partial-failure retry.** A storage error mid-batch propagates; ids
  already deleted stay deleted. The response never lies about what happened,
  because the exception surfaces instead of a report.

---

## 12. Open questions

None blocking. Two follow-ups worth noting for later, not for this pass:

1. Should `memory_maintenance`'s cleanup path reuse `DeleteAcrossChainAsync`
   instead of its own per-id loop (`MemoryMaintenanceService.cs:361`)? It would
   pick up the same one-rebuild-per-level behavior. Deferred: it operates on a
   different abstraction and changing it widens this change.
2. Should the dashboard gain a multi-select delete that calls this same Core
   method? The REST surface stays single-id for now, per §2.
