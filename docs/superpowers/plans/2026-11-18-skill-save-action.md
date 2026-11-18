# Skill Save Reports Create vs Update - Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** `skill_save` returns an `action` field (`created` | `updated`) in its response, mirroring `memory_save`'s wire format.

**Architecture:** `ISkillService.SaveAsync` re-types from `Task<Skill>` to `Task<SkillSaveResult>` (a new record pairing the saved `Skill` with a `SkillSaveAction` enum). The tool maps the action into `SaveSkillResponse.Action` (`"created"`/`"updated"`, always serialized). REST callers unwrap `.Skill` with no behavioral change. This is approach A from the spec.

**Tech Stack:** C# / .NET, xUnit (`Eling.Core.Tests`, `Eling.Backend.Tests`), existing `Eling.Core.Skill` subsystem.

**Spec:** `docs/superpowers/specs/2026-11-18-skill-save-action-design.md`

## Global Constraints

- Wire value spelling is `"created"` / `"updated"` lowercase — identical to `memory_save`; the JSON key is `action`.
- The skill subsystem must NOT reference `Eling.Core.Memory` — hence a separate `SkillSaveAction` enum, not reused `SaveAction`.
- Reads stay origin-agnostic; saves stay Eling-origin only. No change to trashed-name behavior or error semantics.
- REST `/api/skills*` response bodies are unchanged (my call recorded in the spec, confirmed by user 2026-11-18: skill tool only).
- One type per file, per repo convention.
- Repo rules: hooks mandatory, no `.next/` build while the dev server runs (n/a here), split commits by layer where more than one commit is needed.
- Pre-commit hook runs the backend suite; flaky Windows temp-file failures in unrelated tests are environmental — re-run before concluding a regression.

## Review Focus

1. Trashed-marker drift case — a row exists (name held in the index) but its folder was trashed and then deleted out of band, so the trash guard does not fire and the file cannot be read: the save still performs an **update** and `action` must be `"updated"`, not `"created"` (pinned in Task 2, `Save_after_trash_folder_deleted_out_of_band_reports_updated`).
2. External-origin name — `skill_save` on a name held by an external skill throws `SkillReadOnlyException` before any response is built; no `action` is ever exposed for external skills (pinned in Task 4, Step 3 — existing `SkillToolsTests` read-only tests stay green and unmodified).
3. Unparsable SKILL.md on update — the file exists but fails to parse; `TryGetExistingAsync` falls back to the index mirror, so the aggregate is an update, `action` must be `"updated"` (pinned in Task 2, `Save_over_unparsable_file_reports_updated_via_index_mirror`).
4. REST wire unchanged — `POST /api/skills` still returns 201 with a `SkillDto` body that carries **no** `action` key; a future reader of the DTO list should not assume symmetry with the tool envelope (pinned in Task 4 by running the existing endpoint tests unmodified).
5. Validation-before-action — empty body / oversized description throws `SkillValidationException` before `SkillSaveResult` exists, so no caller can observe a half-built action (pinned by keeping existing validation tests green).

---

### Task 1: Core types + service signature re-type

**Files:**
- Create: `src/backend/Eling.Core/Skill/SkillSaveAction.cs`
- Create: `src/backend/Eling.Core/Skill/SkillSaveResult.cs`
- Modify: `src/backend/Eling.Core/Skill/ISkillService.cs` (`SaveAsync` signature + doc comment)
- Modify: `src/backend/Eling.Core/Skill/SkillService.cs` (`SaveAsync`: build `SkillSaveResult` with `existing is null ? SkillSaveAction.Created : SkillSaveAction.Updated`)
- Modify: `tests/Eling.Backend.Tests/SkillToolsTests.cs:406` (`CountingSkillService.SaveAsync` return type)
- Modify: every call site the compiler flags (mechanical `.Skill` unwrap): `src/backend/Eling.Backend/Mcp/Tools/SkillWriteTool.cs`, `src/backend/Eling.Backend/Endpoints/SkillEndpoints.cs` (`CreateSkillAsync`, `UpdateSkillAsync`), all test files under `tests/` calling `service.SaveAsync(...)` and using the result as a `Skill`

**Interfaces:**
- Produces: `public enum SkillSaveAction { Created, Updated }` in `Eling.Core.Skill`; `public sealed record SkillSaveResult(Skill Skill, SkillSaveAction Action)` in `Eling.Core.Skill`; `ISkillService.SaveAsync(SkillSaveInput) → Task<SkillSaveResult>` — Tasks 2–4 rely on these exact names.

- [ ] **Step 1: Add `SkillSaveAction.cs`** — enum with `Created`, `Updated`, namespace `Eling.Core.Skill`, with a doc comment pointing at the tool envelope (`SaveSkillResponse.Action`, wire `created`/`updated`).

- [ ] **Step 2: Add `SkillSaveResult.cs`** — `public sealed record SkillSaveResult(Skill Skill, SkillSaveAction Action);` namespace `Eling.Core.Skill`. Doc comment: the input cannot express `Skill.Source`; `Action` is decided by whether an index row existed at save time.

- [ ] **Step 3: Re-type `ISkillService.SaveAsync`** — `Task<SkillSaveResult> SaveAsync(SkillSaveInput input);` and extend the existing doc comment with one line: the result's `Action` reports `Created` when no index row existed for the name, `Updated` otherwise (a drifted-but-present row counts as `Updated`).

- [ ] **Step 4: Implement in `SkillService.SaveAsync`** — `SaveAsync` already computes `existing`; change the tail from `return skill;` to `return new SkillSaveResult(skill, existing is null ? SkillSaveAction.Created : SkillSaveAction.Updated);`. No other logic changes.

- [ ] **Step 5: Fix the interface implementer in `SkillToolsTests.cs`** — `public Task<SkillSaveResult> SaveAsync(SkillSaveInput input) => _inner.SaveAsync(input);`

- [ ] **Step 6: Fix `SkillWriteTool.SaveAsync` minimally so it compiles** — `var result = await _skills.SaveAsync(...);` then use `result.Skill` for the log line and for `SaveSkillResponse.From(result.Skill, ...)` (the DTO gains the action in Task 3; for now keep the existing `From(Skill, string)` overload).

- [ ] **Step 7: Fix `SkillEndpoints`** — `CreateSkillAsync`: `var result = await service.SaveAsync(input);` then use `result.Skill` for the 201 `Location` header and the `SkillDto`. `UpdateSkillAsync`: same unwrap after the rename-then-save path. No other changes; HTTP behavior identical.

- [ ] **Step 8: Fix every remaining call site the compiler flags** — the mechanical rule: where the code uses the return value as a `Skill`, replace with `.Skill` (e.g. `var saved = await service.SaveAsync(...)` → `var saved = (await service.SaveAsync(...)).Skill;` or leave `saved.Skill` at each use — pick whichever matches local style, keep diffs minimal). Expected files include `tests/Eling.Core.Tests/SkillServiceTests.cs`, `SkillCatalogScannerTests.cs`, `RecallTests.cs`, `tests/Eling.Backend.Tests/SkillChangeNotifierTests.cs` and any scanner/tool callers. Build until zero errors: `dotnet build` from repo root (or `dotnet build src/backend/Eling.Backend` plus test projects).

- [ ] **Step 9: Run the existing suites to confirm no behavior change**

Run: `dotnet test tests/Eling.Core.Tests --filter "FullyQualifiedName~Skill" --nologo` and `dotnet test tests/Eling.Backend.Tests --filter "FullyQualifiedName~Skill" --nologo`
Expected: all pre-existing tests PASS unchanged (wire format of the REST envelope untouched in this task).

- [ ] **Step 10: Commit**

```bash
git add src/backend/Eling.Core/Skill src/backend/Eling.Backend/Mcp/Tools/SkillWriteTool.cs src/backend/Eling.Backend/Endpoints/SkillEndpoints.cs tests
git commit -m "refactor(skills): SaveAsync returns SkillSaveResult (created|updated)"
```

### Task 2: Service-level action semantics tests

**Files:**
- Modify: `tests/Eling.Core.Tests/SkillServiceTests.cs` (add two tests)
- Modify: `tests/Eling.Core.Tests/SkillServiceTests.cs` existing create test (extend with action assert) if not covered by the new tests

**Interfaces:**
- Consumes: `SkillSaveResult` / `SkillSaveAction` from Task 1.

- [ ] **Step 1: Write the failing/passing-as-written test `Save_creates_new_skill_reports_created_action`**

```csharp
[Fact]
public async Task Save_creates_new_skill_reports_created_action()
{
    var service = NewService();

    var result = await service.SaveAsync(NewInput("action-create"));

    Assert.Equal(SkillSaveAction.Created, result.Action);
}
```

Run: `dotnet test tests/Eling.Core.Tests --filter "FullyQualifiedName~Save_creates_new_skill_reports_created_action"`
Expected: PASS (Task 1 already computes it; this test pins it).

- [ ] **Step 2: Add `Save_over_existing_skill_reports_updated_action`**

```csharp
[Fact]
public async Task Save_over_existing_skill_reports_updated_action()
{
    var service = NewService();
    var first = await service.SaveAsync(NewInput("action-update", "first description", "# first"));

    var second = await service.SaveAsync(NewInput("action-update", "second description", "# second"));

    Assert.Equal(SkillSaveAction.Updated, second.Action);
    Assert.Equal(first.Skill.Id, second.Skill.Id);
    Assert.Equal(first.Skill.CreatedAt, second.Skill.CreatedAt);
}
```

Note: adapt member access to the local shape established in Task 1 (if Task 1 deconstructed instead of `.Skill`, match it).

- [ ] **Step 3: Add `Save_after_trash_folder_deleted_out_of_band_reports_updated`** (pins Review Focus 1)

```csharp
[Fact]
public async Task Save_after_trash_folder_deleted_out_of_band_reports_updated()
{
    var service = NewService();
    await service.SaveAsync(NewInput("drift-action-skill"));
    await service.DeleteAsync("drift-action-skill");
    Directory.Delete(TrashFolder("drift-action-skill"), recursive: true);

    var result = await service.SaveAsync(NewInput("drift-action-skill"));

    Assert.Equal(SkillSaveAction.Updated, result.Action);
}
```

- [ ] **Step 4: Add `Save_over_unparsable_file_reports_updated_via_index_mirror`** (pins Review Focus 3)

```csharp
[Fact]
public async Task Save_over_unparsable_file_reports_updated_via_index_mirror()
{
    var service = NewService();
    var created = await service.SaveAsync(NewInput("broken-skill"));
    await File.WriteAllTextAsync(SkillFile("broken-skill"), "---\nnot: [valid yaml: : :\n---\n\nbroken");

    var result = await service.SaveAsync(NewInput("broken-skill", "rewritten", "# rewritten"));

    Assert.Equal(SkillSaveAction.Updated, result.Action);
    Assert.Equal(created.Skill.Id, result.Skill.Id);
}
```

Adjust the broken-YAML fixture to whatever actually fails `SkillFileFormat` parsing in this repo (copy the malformed document used by existing unparsable-file tests — search `SkillServiceTests.cs` / `FileSystemSkillStorageTests.cs` for `TryGetExistingAsync`-mirror-fallback fixtures and reuse the exact fixture).

- [ ] **Step 5: Run the skill suite**

Run: `dotnet test tests/Eling.Core.Tests --filter "FullyQualifiedName~Skill" --nologo`
Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add tests/Eling.Core.Tests/SkillServiceTests.cs
git commit -m "test(skills): pin created/updated action semantics for skill save"
```

### Task 3: DTO + tool mapping + wire test

**Files:**
- Modify: `src/backend/Eling.Backend/Dtos/SaveSkillResponse.cs` (add `Action` property)
- Modify: `src/backend/Eling.Backend/Mcp/Tools/SkillWriteTool.cs` (`SaveAsync`: log line gains action; `From` call passes the result)
- Modify: `src/backend/Eling.Backend/Mcp/Tools/SkillWriteTool.cs` `[McpServerTool]` description (one sentence about the `action` field)
- Modify: `tests/Eling.Backend.Tests/SkillToolsTests.cs` (add tool-level assertions)

**Interfaces:**
- Consumes: `SkillSaveResult`, `SkillSaveAction` from Task 1.

- [ ] **Step 1: Extend `SaveSkillResponse`** — add

```csharp
[JsonPropertyName("action")]
public string Action { get; set; } = "";
```

(always serialized, no `WhenWritingNull`). Change `From` to `From(SkillSaveResult result, string path)` setting `Action = result.Action == SkillSaveAction.Created ? "created" : "updated"`, `Id/Name/Path/Origin` from `result.Skill`. Delete or keep the old `From(Skill, …)` overload only if nothing else uses it — prefer a single `From(SkillSaveResult, string)` (DRY); fix remaining callers.

- [ ] **Step 2: `SkillWriteTool.SaveAsync`** — `_logger?.LogInformation("Saved skill '{Name}' (id {Id}) ({Action})", result.Skill.Name, result.Skill.Id, result.Action);` and return `SaveSkillResponse.From(result, SkillTools.MarkdownPath(_catalogRoot, result.Skill.Name));`

- [ ] **Step 3: Tool description sentence** — append to the existing `[McpServerTool]` description: `The response's \`action\` field reports \`created\` or \`updated\`.`

- [ ] **Step 4: Tool tests** — in `SkillToolsTests.cs` add `Skill_save_reports_action_created_then_updated` over a fresh `SkillService` + `SkillWriteTool`:

```csharp
[Fact]
public async Task Skill_save_reports_action_created_then_updated()
{
    var tool = NewWriteTool(NewService());

    var created = await tool.SaveAsync("wire-action-skill", "desc", "# body");
    var updated = await tool.SaveAsync("wire-action-skill", "desc v2", "# body v2");

    Assert.Equal("created", created.Action);
    Assert.Equal("updated", updated.Action);
}
```

Plus one wire-name pin: serialize `created` with the test's existing `JsonSerializerOptions` and assert `JsonDocument` contains property `action` with value `"created"`.

- [ ] **Step 5: Run backend tool tests**

Run: `dotnet test tests/Eling.Backend.Tests --filter "FullyQualifiedName~Skill" --nologo`
Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add src/backend/Eling.Backend/Dtos/SaveSkillResponse.cs src/backend/Eling.Backend/Mcp/Tools/SkillWriteTool.cs tests/Eling.Backend.Tests/SkillToolsTests.cs
git commit -m "feat(skills): skill_save response carries action (created|updated)"
```

### Task 4: End-to-end verification

**Files:** none (verification only).

- [ ] **Step 1: Full skill-related suites green**

Run: `dotnet test tests/Eling.Core.Tests --filter "FullyQualifiedName~Skill" --nologo` and `dotnet test tests/Eling.Backend.Tests --filter "FullyQualifiedName~Skill" --nologo`
Expected: all PASS, including `SkillChangeNotifierTests`.

- [ ] **Step 2: REST envelope check** — run the endpoint tests unmodified and confirm no test needed to change:

Run: `dotnet test tests/Eling.Backend.Tests --filter "FullyQualifiedName~SkillEndpoints|FullyQualifiedName~SkillRest" --nologo`
Expected: PASS with zero edits (pins Review Focus 4).

- [ ] **Step 3: External-skill guard still silent** — confirm the existing `SkillReadOnlyException` tool-path test(s) in `SkillToolsTests.cs` still pass and no response is constructed for an external-name save (Review Focus 2); no code change expected.

- [ ] **Step 4: Smoke the MCP surface if the dev backend is running** — call `eling_dev`'s `skill_save` with a throwaway fixture skill, observe `action: "created"`, call again with identical content, observe `action: "updated"`, then trash the fixture (`skill_delete`) so the catalog is left clean. Skip if the dev backend is not running; note it in the commit message or the hand-off.
