# Skill Save Reports Create vs Update - Design

> **Date:** 2026-11-18 · **Status:** Approved in conversation (skill tool only) · **Size:** Small

## Intent

The `skill_save` MCP tool performs create-or-update by name, but its response gives no hint of which one happened. An agent calling it cannot tell whether it made a brand-new skill or overwrote an existing one without a separate read. Goal: the tool response carries the action — `created` or `updated` — mirroring the `memory_save` precedent (`SaveMemoryResponse.Action`, wire values `"created"` / `"updated"`, lowercase).

## Goals

- `skill_save` response gains an `action` field: `"created"` when no index row existed for the name (and no unscanned folder either), `"updated"` when an Eling-managed row existed and was overwritten.
- The signal flows out of `SkillService.SaveAsync` — the one place that actually decides — not re-derived by the caller.
- Wire format matches `memory_save` exactly: lowercase `"created"` / `"updated"` under the key `action`.

## Non-goals

- No change to `memory_save` or any other save tool (snippets, etc.) in this work — skill tool first; other tools follow the same pattern later.
- `memory_copy_to_project` / `memory_promote_to_global` internally compute the same Created/Updated outcome via the smart-save path but return a bare `Memory?` — their `action` is dropped at the tool boundary. Known gap, same pattern, deliberately out of scope here (they are copy/move-by-id, not save-by-name); candidates for a follow-up if the standard is applied uniformly.
- No change to REST `/api/skills*` response bodies (my call, correctable: REST already encodes intent in its verbs — POST creates, PUT updates — so the extra field is redundant there. The shared `SaveSkillResponse` is MCP-tool-scoped, so this costs nothing).
- No change to error semantics, status codes, or the trashed-name behavior.
- No change to `skill_restore` / `skill_adopt` / `skill_rename` responses.

## Approaches considered

- **A — Service returns a result record (chosen).** `ISkillService.SaveAsync` returns `Task<SkillSaveResult>` (a record carrying `Skill Skill` and `SkillSaveAction Action`). The tool maps `Action` into `SaveSkillResponse.Action`. REST callers unwrap `.Skill` with no behavioral change — same 201/200, same DTO. Mirrors `MemoryService` → `MemorySaveResult` → `SaveAction` exactly. One decision point, no races, no duplicated guard logic.
- **B — Tool pre-checks with `GetByNameAsync` before saving.** No service change, but the tool re-derives what the service already knows; the two can disagree (e.g. trashed-marker drift: index row exists with folder deleted out-of-band — the tool would report `created` where the service performs an update). Rejected.
- **C — Separate `SaveWithActionAsync` method.** Keeps `SaveAsync` untouched but doubles the service surface for one flag and risks the two paths drifting. Rejected.

## Design

### Core (`Eling.Core.Skill`)

1. New enum `SkillSaveAction` (`Created`, `Updated`) in its own file `SkillSaveAction.cs`, namespace `Eling.Core.Skill`. Deliberately **not** reusing `Eling.Core.Memory.SaveAction`: the skill subsystem must not take a dependency on the memory subsystem; a two-value enum duplicated is cheaper than the coupling.
2. New record `SkillSaveResult` (`Skill Skill`, `SkillSaveAction Action`), one type per file, namespace `Eling.Core.Skill`.
3. `ISkillService.SaveAsync` re-typed to `Task<SkillSaveResult>`. Implementation (`SkillService`): the decision is already localized — `existing = row is null ? null : await TryGetExistingAsync(name)`. `Action` is `existing is null ? Created : Updated`. Everything else unchanged; the notifier still fires `saved`.
4. `RenameAsync`, `RestoreAsync`, etc. keep their `Task<Skill>` / `Task` signatures — out of scope.

### Backend (`Eling.Backend`)

5. `SaveSkillResponse` gains `[JsonPropertyName("action")] string Action { get; set; } = ""`, set in `From` from a new parameter or by taking the result record. Value: `result.Action == SkillSaveAction.Created ? "created" : "updated"`. Property always serialized (no WhenWritingNull) — the field is always known.
6. `SkillWriteTool.SaveAsync`: unwrap the `SkillSaveResult`; log line gains the action (`Saved skill '{Name}' (id {Id}) ({Action})`); return `SaveSkillResponse.From(result, path)`.
7. Tool `[McpServerTool]` description gains one sentence: the response's `action` field reports `"created"` or `"updated"`. (`ToolCatalog.cs` short description already says "create or update by name"; unchanged.)
8. REST callers updated for the new signature only, no behavioral change:
   - `SkillEndpoints.CreateSkillAsync`: `var result = await service.SaveAsync(input);` → use `result.Skill` for the 201 `Location` header and `SkillDto.From(result.Skill, ...)`.
   - `SkillEndpoints.UpdateSkillAsync`: same unwrap for the rename-then-save path.
   - Any other production/test call sites of `SaveAsync` found by the build (e.g. tests, scanners) get the same mechanical unwrap.

### Tests

9. Existing skill-service/tool tests updated to the new signature. Assertions added:
   - Save with no prior row → response `action == "created"`, ULID freshly assigned.
   - Save over an existing Eling-managed skill → `action == "updated"`, same ULID, `createdAt` preserved.
   - The Drifted-index case (row exists, file unparsable → mirror fallback) still reports `updated`.
10. REST endpoints keep existing test expectations untouched (no wire change).

## Constraints

- Wire value spelling is `"created"` / `"updated"` lowercase — identical to `memory_save`; any future save tool follows the same spelling.
- Reads stay origin-agnostic; saves remain Eling-origin only. The action field leaks nothing about external skills (saves on external names still throw `SkillReadOnlyException` before any response is built).
- Repo rules: split commits by layer if more than one commit is needed, hooks mandatory, no `.next/` build while the dev server runs.

## What's left to the builder

- Exact file name/order of the `SkillSaveResult` record (one type per file, per repo convention).
- Whether to thread `SkillSaveResult` through `SaveSkillResponse.From(result, path)` (preferred) or set `Action` inline — builder's call, keep `From` self-contained.
- Updating any test doubles/fakes that implement `ISkillService` (search for `SaveAsync` implementations of the interface, not just call sites).
