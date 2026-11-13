# Rename `memory_recall` to `recall` (Full Rename) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans (native, this session) or superpowers:subagent-driven-development. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Rename the Eling MCP tool `memory_recall` to `recall`, and the internal `MemoryRecall*` domain family to `Recall*`, as two reviewable commits in `eling-agent`.

**Architecture:** Clean-cut rename (no alias). Commit 1 renames only the MCP/agent tool contract (`MemoryRecallTool` → `RecallTool`, `MemoryRecallAgentTool` → `RecallAgentTool`, `Name = "recall"`) plus all living docs and test fixtures. Commit 2 does a mechanical full-family rename: `git mv` for folders/files, then identifier replace via checked-in script across `src/`, `tests/`, and current docs, then build + full test suite until green.

**Tech Stack:** .NET 10, xUnit, MCP SDK, PowerShell (bulk rename), git.

**Spec:** `docs/superpowers/specs/2026-11-10-rename-memory-recall-to-recall-design.md`

## Global Constraints
- Scope: `eling-agent` repo only. Out of scope: opencode global skills, other repos (`ZiziBot.TelegramBot`), stored Eling memories, historical docs (`docs/superpowers/specs/**`, `docs/superpowers/plans/**`, `CHANGELOG.md` history). Unknown files are left alone; rename must not touch history.
- No alias for `memory_recall` after this work; the two commits must each leave the build + tests green.
- Identifier rename direction: `MemoryRecall` → `Recall`, `memory_recall` → `recall`, `Eling.Core.MemoryRecall` → `Eling.Core.Recall`.
- `Recall*` DTOs and `RecallResponse` (JSON property names like `recallMemories`, `standingMemories`, `recallLimit`) are untouched; only C# type/member identifiers and the MCP tool name change.
- `CodeOrganizationConventionTests` (currently hardcodes `MemoryRecallTool*` test paths and the `Eling.Core/MemoryRecall/` folder) is authoritative for test file naming: after this work those entries must read `RecallTool*` / `Eling.Core/Recall/`.

## Review Focus
- Bulk replace hits binary/git-ignored paths — guard by restricting to tracked files (`git grep`).
- `Eling.Core/MemoryRecall/IntentionTriggerMatcher.cs` internal class name vs folder path — both rename consistently (namespace `Eling.Core.Recall`).
- `RecallTool` file rename with CRLF line endings — preserve line endings (use `git mv`, not copy/delete guesswork).
- Test classes named `MemoryRecallToolTests` are assert-callers of the MCP tool class — rename with tool class, not with the DTO family.
- `docs/recall-architecture.md` references `memory_recall` and `MemoryRecallService` — update both to `recall` / `RecallService`; the *file name* stays `recall-architecture.md`.

---

## Commit 1 — MCP tool rename (small, contract-facing)

### Task 1: Tool classes + DI + ProtectedTools + Catalog

**Files:**
- Rename: `src/backend/Eling.Backend/Mcp/Tools/MemoryRecallTool.cs` → `RecallTool.cs`
- Rename: `src/backend/Eling.Backend/Agent/Services/Tools/MemoryRecallAgentTool.cs` → `RecallAgentTool.cs`
- Modify: `src/backend/Eling.Backend/Agent/Services/Tools/AgentToolServiceExtensions.cs`
- Modify: `src/backend/Eling.Backend/Tools/ToolPolicyStore.cs`, `src/backend/Eling.Backend/Tools/ToolCatalog.cs`

- [ ] Step 1: `git mv` the two files; rename classes (`MemoryRecallTool` → `RecallTool`, `MemoryRecallAgentTool` → `RecallAgentTool`), `ILogger<>` generics, ctor; set `[McpServerTool(Name = "recall")]` and `Name => "recall"`.
- [ ] Step 2: `AgentToolServiceExtensions.cs`: `AddScoped<RecallTool>()`, `AddScoped<IAgentTool, RecallAgentTool>()`.
- [ ] Step 3: `ToolPolicyStore.cs`: ProtectedTools `"memory_recall"` → `"recall"`; `ToolCatalog.cs`: definition name `"memory_recall"` → `"recall"`, description unchanged (or "Recall relevant workspace context...").
- [ ] Step 4: Build backend (`dotnet build src/backend/Eling.Backend/Eling.Backend.csproj --artifacts-path .bin-recall` or equivalent single-shot build); resolve any compile fallout to tool references in `McpServiceExtensions.cs` (two registration blocks ref `IMemoryRecallService` — leave, that's Commit 2).
- [ ] Step 5: `dotnet test tests/Eling.Backend.Tests/Eling.Backend.Tests.csproj` → filter `FullyQualifiedName~Tool` and `FullyQualifiedName~Policy` to confirm green for this scope.
- [ ] Step 6: Commit: `feat(mcp): rename tool memory_recall to recall`

### Task 2: Instructions, app prompt, DTO-exposure, and doc comments

**Files:**
- Modify: `src/backend/Eling.Backend/Mcp/ServerInstructions.cs`
- Modify: `src/backend/Eling.Backend/Agent/Services/AgentTurnService.cs`
- Modify: `src/backend/Eling.Backend/Mcp/Tools/CodebaseStatusTool.cs` (comment only)
- Modify: `src/backend/Eling.Backend/Dtos/MemoryRecall*.cs` doc comments mentioning `memory_recall` → `recall`
- Modify: `src/backend/Eling.Backend/Bootstrap/ProjectScopePosture.cs`, `src/backend/Eling.Core/Memory/MemoryAuditActions.cs` (doc comment only)

- [ ] Step 1: `ServerInstructions.cs`: Replace every `memory_recall` in the two instruction strings with `recall` (it's what agents read); keep the rest of the sentence verbatim.
- [ ] Step 2: `AgentTurnService.cs`: tool list `memory_recall` → `recall`; comment on line 51 → `recall`.
- [ ] Step 3: Sweep current sources/doc-only references in backend to `memory_recall` → `recall` (skip CHANGELOG, specs, plans — they stay).
- [ ] Step 4: `dotnet build` backend; commit: `refactor(mcp): point instructions and docs at recall`

### Task 3: Tests — tool-name fixtures

**Files:**
- Modify: `tests/Eling.Backend.Tests/AgentBuiltinMcpToolTests.cs`
- Rename: `MemoryRecallToolTests` not present; instead tool tests are named `MemoryRecallTool{Codebase,OverflowSummary,ProjectScope,TopicAlias}Tests.cs` → `RecallTool{...}Tests.cs` (classes too)
- Modify: `tests/Eling.Backend.Tests/Tools/ToolPolicyStoreTests.cs`, `tests/Eling.Backend.Tests/Mcp/ToolPolicyFilterTests.cs`, `tests/Eling.Backend.Tests/ToolTelemetryFilterTests.cs`, `tests/Eling.Backend.Tests/HarvestNudgeFilterTests.cs`, `tests/Eling.Backend.Tests/Endpoints/ToolsEndpointsTests.cs`, `tests/Eling.Backend.Tests/AgentBuiltinMcpToolTests.cs`, `tests/Eling.Backend.Tests/Updates/UpdateNoticeBlockTests.cs` (also uses `MemoryRecallTool` class)
- Modify: `tests/Eling.Backend.Tests/CodeOrganizationConventionTests.cs` (the four `MemoryRecallTool*Tests` paths + related class-count maps → `RecallTool*Tests`)

- [ ] Step 1: `git mv` test files; rename test classes and `new MemoryRecallTool(...)` → `new RecallTool(...)`; `using` unchanged (Commit 1).
- [ ] Step 2: Replace string literals `"memory_recall"` → `"recall"` across these test files only.
- [ ] Step 3: `CodeOrganizationConventionTests`: update the four hardcoded paths.
- [ ] Step 4: `dotnet test tests/Eling.Backend.Tests/Eling.Backend.Tests.csproj` → green.
- [ ] Step 5: Commit: `test(mcp): reference recall tool name`

### Task 4: Living docs + AGENTS.md + INSTALL.md

**Files:**
- Modify: `AGENTS.md`, `INSTALL.md`, `docs/using-eling.md`, `docs/recall-architecture.md`, `docs/agent-setup.md`, `docs/nested-memory.md`, `docs/semantic-judge-architecture.md`, `docs/architecture.md` (folder reference is Commit 2, leave list as-is now? → update only `memory_recall` tokens now)

- [ ] Step 1: Replace `memory_recall` → `recall` in those docs (leave `MemoryRecall*` identifiers to Commit 2).
- [ ] Step 2: Commit: `docs: document recall tool rename`

---

## Commit 2 — Full domain family rename `MemoryRecall*` → `Recall*`

### Task 5: `git mv` folder + DTOs + service files

- [ ] Step 1: `git mv src/backend/Eling.Core/MemoryRecall src/backend/Eling.Core/Recall`; rename inner files (`MemoryRecallContext.cs` → `RecallContext.cs`, etc.) to drop the `Memory` infix.
- [ ] Step 2: DTO files under `Eling.Backend/Dtos/MemoryRecall*.cs` → `Recall*.cs` (same rule).
- [ ] Step 3: Test folder/file renames: `MemoryRecallServiceChainTests.cs` → `RecallServiceChainTests.cs`, `MemoryRecallDtosTests.cs` → `RecallDtosTests.cs`; tool tests already renamed in Commit 1 (`RecallTool*Tests`).

### Task 6: Bulk identifier replace (scripted, tracked files only)

- [ ] Step 1: For every tracked file in `src/`, `tests/`, and the Commit 4 docs: replace `Eling.Core.MemoryRecall` → `Eling.Core.Recall`, `MemoryRecall` → `Recall`, `memory_recall` → `recall`. Includes `McpServiceExtensions.cs`, `MemoryService.cs` comments, `UpdateNoticeDto`, `IMemoryRecallService` references in Agent code and tests, `AgentTurnPromptCompositionTests` stub, `AgentChat*` tests, `ScopeChainSmokeTests`, `MemoryProvenanceDtoTests`, `RecallValueTests`, `CodeOrganizationConventionTests` (`Eling.Core/MemoryRecall/` path → `Eling.Core/Recall/`), `docs/architecture.md`.
- [ ] Step 2: Verify no tracked file contains `MemoryRecall` or `memory_recall` outside the allowed historical paths (`git grep -n "MemoryRecall\|memory_recall" -- src tests docs AGENTS.md INSTALL.md` → only current docs left; then check specs/plans/CHANGELOG explicitly are untouched).

### Task 7: Build + full suite green

- [ ] Step 1: `dotnet build` backend; fix compile fallout (missed references, doc comments are cosmetic).
- [ ] Step 2: `dotnet test tests/Eling.Backend.Tests/Eling.Backend.Tests.csproj` → green.
- [ ] Step 3: `dotnet test tests/Eling.Core.Tests/Eling.Core.Tests.csproj` → green.
- [ ] Step 4: `dotnet test` full solution or at minimum backend+core; record pre-existing flakes only (e.g. `RuntimeRegistryWorkspacePruneTests`), don't chase them.
- [ ] Step 5: Commit: `refactor(core): rename MemoryRecall domain family to Recall`

## Execution notes
- Commit 1's `dotnet test` scope may use a filter; Commit 2 uses full suites.
- Do **not** rename CSS/frontend symbols or `recallSummary`/`recallMemories` JSON properties.
- Do **not** touch `.eling/`, `.bin/`, `.git/`.
- After both commits land, optionally regenerate the `recall-architecture.md` prose to describe `RecallService`; the spec file records the decision, docs must match reality.
