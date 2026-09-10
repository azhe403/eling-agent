# Scope Chain + Consensual Project Initialization — Design

Date: 2026-09-09
Status: Draft for review (rev 5 — flat projectName added)
Scope: Eling.Core (scope resolution, merger), Eling.Backend (MCP tools, DI)

> All example project names and paths in this document are anonymized placeholders
> (e.g. `C:\work\acme\acme-platform\integrations\payments`). No real project names,
> usernames, or machine paths are used, per the project hygiene rule.

## Context

Eling today knows exactly two scopes:

- **Project** — the single `.eling` directory found by walking **up** from the process cwd (`ProjectScope.Discover()`). The nearest ancestor containing `.eling` wins; if none exists anywhere, the cwd itself becomes the project root and `.eling` is **created there automatically** (`ProjectContext.Discover()` calls `Directory.CreateDirectory`).
- **Global** — `~/.config/eling/`, the user-level store.

Merged reads (`memory_recall`, `memory_search`, `memory_list` with `merged`) combine exactly project + global. There is no notion of more than one project scope per backend instance, and no scope exists above global other than the single nearest `.eling`.

### Real-world failure

Workspace: `C:\work\acme\acme-platform\integrations\payments` opened in an agent host. `payments` has no `.eling` of its own; `acme-platform` does. Every `memory_save` therefore lands in the `acme-platform` scope, silently pooling memories from many unrelated sub-projects into one bucket. The agent cannot give the opened workspace its own memory tree without manually creating `.eling`, and even then there is no defined relationship between the child scope and its ancestor scopes.

Desired semantics, as stated by the user:

- The ancestor walk must be preserved: a workspace without its own `.eling` keeps resolving to the nearest enclosing scope ("parent scope").
- A project may have **several** enclosing scopes (e.g. `payments` → `integrations` (if it ever gains `.eling`) → `acme-platform` → global = up to 4 levels).
- **Creating `.eling` in a project ALWAYS requires explicit user consent** — both for a nested child scope and for a brand-new workspace that has no scope anywhere. There is no silent auto-creation of a project `.eling`, in any case. (This deliberately removes today's implicit first-run auto-create at cwd.)
- Recall/search from a child scope must still see ancestor memories ("nested memory scope", not isolation).

## Goals

1. Scope resolution becomes an **ordered chain**: every `.eling` directory from cwd upward, nearest first, with global as the implicit terminal. If no `.eling` exists in the ancestry at all, the workspace is **uninitialized** (no project head).
2. Writes (`project`/`auto`/default) target the **head of the chain** (nearest scope). Unchanged for workspaces whose head is an ancestor scope.
3. Merged reads cover the **whole chain + global**, nearest-first priority, dedup by ULID.
4. **Consensual initialization**: `.eling` is created only through an explicit init tool that the agent invokes after the human approves. The backend never auto-creates a project `.eling`.
5. **Uninitialized behavior**: with no project scope and no consent yet, default project-scope `memory_save` writes **nothing** and returns an explicit "init required" signal for the agent to relay — no silent fallback to global, no hard crash. Merged reads degrade to global-only until init.

## Non-Goals (YAGNI)

- No changes to reference-based operations (get/update/delete by `MemoryReference`, copy/move/promote) — they already support arbitrary target roots.
- No new scope tokens in the public API. `project | global | merged | auto` keep their meaning.
- No dashboard/HTTP parity work in this pass (noted as follow-up).
- No auto-adoption and no `ELING_AUTO_INIT`-style silent creation — consent is mandatory for every project `.eling`.
- No changes to global-scope resolution (`~/.config/eling`).

## Design

### 1. Scope chain model

Replace "one project scope" with "a chain of project scopes". Resolution walks from cwd **upward** and collects **every** directory that contains `.eling` (excluding the user home, as today), nearest first. The last element of the chain is implicitly global.

```
payments/.eling            <- head (own scope, once initialized)   [project]
integrations/.eling        <- optional intermediate level           [ancestor]
acme-platform/.eling       <- existing ancestor scope               [ancestor]
~/.config/eling/           <- terminal                              [global]
```

Workspace states:

- **Initialized, own scope** — cwd contains `.eling`. Head = cwd.
- **Initialized, ancestor scope** — cwd has no `.eling`, but at least one ancestor does. Head = nearest ancestor. Default writes land there without consent (nothing is created).
- **Uninitialized** — no `.eling` anywhere in the ancestry (and cwd is not the user home). No head. Default project-scope writes are blocked with an init-required signal until the human consents.
- **User home** — never a project; effective data dir is global (existing strict rule). No init is offered.

Key rules:

- **Head of chain** = first directory starting at cwd that has `.eling`. Writes (`scope=project|auto|default`) always target the head when one exists.
- **Consent boundary** = creation of a project `.eling`. Creating one (first-run or nested) always requires explicit human approval mediated by the agent. Landing a write in an existing ancestor scope is not creation and needs no consent.
- **User-home guard** unchanged: `.eling` directly in the user home is never a project; effective data dir there is global.

### 2. Chain-aware resolution (Core)

New API alongside `ProjectScope.Discover`:

```csharp
// Every ancestor directory (from start upward) that contains .eling,
// nearest first. stopAt is the existing test seam (inspected last, never passed).
public static IReadOnlyList<ProjectScope> DiscoverChain(
    string? startDirectory = null, string? stopAtDirectory = null)
```

- `Discover` (head-only) can remain as-is, defined as `DiscoverChain(...).FirstOrDefault()`; callers distinguish "no head" (uninitialized) from a head.
- `ScopedMemoryService` evolves from holding one project service to holding an **ordered list** of `(ProjectScope, IMemoryService)` levels plus the global service. The head service is the write target; all levels participate in merged reads. When uninitialized, the list is empty and the service reports the blocked state.
- Ancestor services are built like `CopyToProjectAsync` already does today (ephemeral `MemoryService(FileSystemMemoryStorage(dir), SqliteMemoryIndex(dir/index.db))`) — existing pattern, no new storage machinery.
- `IMemoryMerger` is extended from 2-source merge (project + global) to **N-level** merge: **level-grouped** ordering (own-scope block first, then each ancestor block nearest-first, global last; relevance score orders results *within* a level) with dedup by ULID keeping the nearest occurrence. This generalizes today's project-before-global philosophy and honors the "scope is a lens, ULID is identity" tenet. Existing 2-source behavior is the N=1 case and must produce identical ordering (regression guard).
- **`ProjectContext.Discover()` no longer auto-creates a project data directory.** When the chain is empty (uninitialized), the effective project data dir is *absent/pending*; runtime artifacts still live under the user-scope runtime directory. Tooling that needs project storage must handle the pending state (see §3).

### 3. Consensual initialization & blocked saves (Backend)

**Detection — read-only tool `memory_project_status`.** Returns the workspace's scope posture so the agent can decide whether to offer initialization:

```
cwd                  string   absolute path of the process working directory
isUserHome           bool     cwd is the user home (global-only, no init offer)
initialized          bool     at least one .eling exists in the ancestry chain
hasOwnScope          bool     cwd itself contains .eling
headRoot             string?  head-of-chain project root (null when uninitialized)
adoptable            bool     !hasOwnScope && !isUserHome  (init may be offered)
ancestorScopes       string[] ordered chain above cwd (excluding own, excluding global)
posture              string   own-scope | ancestor-scope | uninitialized | user-home
```

No side effects. The agent consults it when relevant; existing tools are not spammed with extra fields.

**Consent flow.** The MCP stdio backend cannot (and should not) prompt the human directly. Consent is mediated by the agent, in both situations:

1. *Uninitialized (fresh workspace):* a default `memory_save` is blocked and returns an init-required signal. The agent relays: "this workspace has no Eling project scope yet; nothing was saved. Create `.eling/` here (with user approval)?"
2. *Nested inside an existing scope:* the agent observes an adoptable workspace (via `memory_project_status`, or because the host opened a folder nested inside a known scope). The agent relays: "memories currently go to `<headRoot>`; give this workspace its own scope?"

In both cases the human approves → agent calls `memory_init_project`. `ServerInstructions` gains a short paragraph describing the flow and the two situations so agents in any host know the pattern.

**Init — mutating tool `memory_init_project`.** Semantics:

- Creates `.eling/` (+ `memories/`) at cwd whenever `adoptable` is true — this is the single path for both first-run initialization and nested child-scope adoption. No other code path creates a project `.eling`.
- Idempotent: if cwd already has `.eling`, no-op with an explicit informational result (not an error).
- Rejected (error) if cwd is the user home.
- `.gitignore` hygiene (existing hard rule): locate the git repo root (nearest `.git` walking up from cwd). If a git repo is found, ensure runtime patterns are ignored (`.eling/index.db*`, `.eling/*.db-journal`, `.eling/*.db-wal`, `.eling/runtime/`) while `.eling/memories/` stays tracked — create/update `.gitignore` at that repo root. When the repo root is an ancestor that already hosts a configured `.eling`, the patterns normally exist already and the step is a no-op. If no git repo is detected, skip silently. Covered by the same single consent as the scope creation.
- Result reports the new head root and the full chain so the agent can confirm the posture changed.

**Blocked save.** When uninitialized, `memory_save` with `project`/`auto`/default scope performs no write and returns a structured result carrying an init-required state (not an exception): no project scope exists; ask the user and call `memory_init_project` on approval. `scope=global` saves are unaffected. Merged reads in the uninitialized state behave as global-only (empty project layer), which is consistent with "global is always available".

### 4. Response shapes & merged ordering

Per-memory provenance in tool responses (decisions confirmed with the user). `projectName` / `projectRoot` identify the scope level the memory **physically lives in — its origin** — not the caller's workspace cwd: a memory stored in the parent repo reports the parent's name (e.g. `acme-platform`), a memory in the adopted child scope reports the child's name (e.g. `payments`), and a global memory reports `null` for both.

- `memory_recall` — each item in `recallMemories` / `recentMemories` gains **flat `projectName` + `projectRoot`** fields (`string?`, both null for global), consistent with `memory_search`. Today's DTO drops both during conversion; restoring them is required once memories can come from any chain level. `projectName` is the last path segment of the level root (e.g. `payments`), so chain levels are readable without parsing paths.
- `memory_get` / `memory_list` — shape unchanged (`scope` + nested `project { name, root }`); with a chain, `root` may point at any level (own scope or an ancestor).
- `memory_search` — shape becomes (`id, rank, scope, projectName, projectRoot`); `projectName` is added so levels are identifiable at a glance, matching recall and the name already present in get/list.

Ordering contract for merged results: **level-grouped**. The own scope's results come first, then each ancestor level nearest-first, then global; within a level, results are ordered by relevance score — ascending `rank`, where rank is the FTS5 bm25 score (more negative = more relevant), so the most relevant result sits on top and larger (less negative) ranks fall to the bottom, exactly as today. Dedup by ULID across levels keeps the nearest occurrence and drops farther duplicates. The N=1 chain (single project + global) must be ordering-identical to today's output. Strict level-grouped ordering supersedes the previous -1000 project boost interleave: N=1 output ordering is unchanged, but the boost mechanism is gone (no cross-level rank adjustment).

```json
// memory_recall items (illustrative): each item reports its ORIGIN scope level
// item 1 -> own scope
{ "id": "01J...", "type": "decision", "status": "active", "content": "...",
  "scope": "project",
  "projectName": "payments",
  "projectRoot": "C:\\work\\acme\\acme-platform\\integrations\\payments",
  "matchedVia": ["porter"], "porterScore": -3.2, "trigramScore": 0.9, "queryMode": "and" }
// item 2 -> ancestor scope (acme-platform), NOT the caller's workspace
{ "id": "01M...", "type": "fact", "status": "active", "content": "...",
  "scope": "project",
  "projectName": "acme-platform",
  "projectRoot": "C:\work\acme\acme-platform",
  "matchedVia": ["trigram"], "porterScore": 0.1, "trigramScore": 2.4, "queryMode": "or" }
```

Contexts without a project layer (uninitialized) return global-only merged results; a blocked `memory_save` returns the init-required signal defined in §3.

### 5. Data flow examples

**Write from nested, un-adopted child (today's behavior, preserved):**
`save` in `payments` while `acme-platform/.eling` exists → head = `acme-platform/.eling` → stored there. No consent involved (nothing is created).

**Write in a fresh workspace before consent:**
`save` in a brand-new repo (no `.eling` anywhere) → blocked; result carries the init-required signal. Agent asks the human. Human approves → `memory_init_project` creates `.eling/` at cwd → subsequent `save` targets the new own scope.

**Write after nested adoption:**
Human consents → `memory_init_project` creates `payments/.eling`. Next `save` → head = `payments/.eling`. The `acme-platform` tree is untouched; it remains visible through merged reads.

**Merged recall with multiple ancestors:**
`memory_recall` from `payments` → chain `[payments, integrations?, acme-platform]` + global, nearest-first ranking, ULID dedup. `scope=project` recall stays strictly head-only (isolation preserved when explicitly requested).

### 6. Error handling

- `memory_init_project` when already scoped → no-op + info (idempotent).
- `memory_init_project` under user home → clear error, nothing created.
- Blocked `memory_save` when uninitialized → structured init-required result (not an exception); agent guidance lives in `ServerInstructions`.
- Chain discovery failures (missing dirs, races) → walk stops at the first unreadable level; states degrade deterministically (empty chain ⇒ uninitialized). No new failure modes surface to callers beyond the cases above.
- `.gitignore` write failures are non-fatal to scope creation: report a warning in the tool result instead of rolling back the `.eling` scaffold.

### 7. Implementation touchpoints

- `Eling.Core/Scope/ProjectScope.cs` — add `DiscoverChain`; keep `Discover` as head-only helper.
- `Eling.Core/Memory/ScopedMemoryService.cs` — multi-level levels list; head = write target; empty-chain (uninitialized) reporting; blocked-save path.
- `Eling.Core/Memory/` merger (interface + implementation) — N-level level-grouped merge with nearest-first ULID dedup.
- `Eling.Backend/Bootstrap/ProjectContext.cs` (+ `TestAppBuilder`, `McpServiceExtensions`) — build/supply chain; **stop auto-creating the project data dir**; effective project data dir pending when uninitialized.
- `Eling.Backend/Mcp/Tools/` — add `MemoryProjectStatusTool`, `MemoryInitProjectTool`; update `MemoryWriteTool` blocked-save result; small `ServerInstructions` addition.
- `Eling.Backend/Dtos/` — result DTOs for the two tools and the blocked-save signal; `MemoryRecallMemory` and `ScopedSearchResultDto` gain flat `projectName` + `projectRoot` (both null for global).
- Tests: `Eling.Core.Tests`, `Eling.Backend.Tests` per conventions below.

## Testing

Follow project conventions: run per-csproj, never solution-wide; `--artifacts-path .bin-test`.

- **Eling.Core.Tests / ScopeTests**: `DiscoverChain` with 2–3 nested levels; `stopAt` seam; user-home exclusion; empty chain when no scope exists. Update any expectations that relied on implicit auto-create at cwd (behavior removed).
- **ProjectContext**: no project data dir created when uninitialized; runtime dir still provisioned under the user scope.
- **Merger N-level**: nearest-first priority; same ULID at two levels resolves to the nearer; global always last; N=1 degenerates to today's project+global behavior (regression guard).
- **Response provenance & ordering**: merged recall/search results are level-grouped (own → ancestors → global) and items carry flat `projectName` + `projectRoot`; duplicate ULIDs across levels resolve to the nearest occurrence.
- **ScopedMemoryService chain**: memory written in an ancestor scope is visible via merged search/recall from a child scope; `scope=project` from the child sees only the child tree; head write target correct before and after init.
- **Blocked save**: default `memory_save` in an uninitialized workspace writes nothing and returns the init-required signal; `scope=global` still writes; merged recall returns global-only.
- **Eling.Backend.Tests / tool tests**: `memory_project_status` posture fields across the four states; `memory_init_project` idempotency, user-home rejection, `.gitignore` creation/update, chain result. Filesystem-backed fixtures following existing `FileSystemMemoryStorageTests` patterns. All fixture paths use dummy names (`eling-dummy-*` style as in existing tests).

## Out of scope / follow-ups

- Dashboard parity (projects list showing chain, init/adopt action in UI).
- Promoting a memory to a specific ancestor level (only global promotion exists today).
- Auto-adoption flags (`ELING_AUTO_INIT`) — deliberately not included; consent is mandatory.

## Open questions

1. Should `memory_project_status` also expose the **host-provided workspace root** when distinguishable from raw process cwd? (Today the backend only sees cwd.) — Likely no for this pass; revisit if multi-root workspaces need it.
2. When the repo root hosting `.gitignore` is an ancestor and the workspace is not itself a git repo, `.gitignore` edits touch the ancestor repo. Current rule (no-op unless patterns missing) is conservative — confirm it is acceptable.

## References

- `docs/superpowers/specs/2026-08-26-pecut-010-scope-aware-memory-spec.md` (established "ProjectScope → nearest .eling")
- `src/backend/Eling.Core/Scope/ProjectScope.cs` — current upward walk + implicit cwd fallback
- `src/backend/Eling.Core/Memory/ScopedMemoryService.cs` — current single project + global model
- `src/backend/Eling.Backend/Bootstrap/ProjectContext.cs` — current auto-create of the data directory
- `src/backend/Eling.Backend/Mcp/ServerInstructions.cs` — agent-facing rules (`.gitignore` prompting pattern)
