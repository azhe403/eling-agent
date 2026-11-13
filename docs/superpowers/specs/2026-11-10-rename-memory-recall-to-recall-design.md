# Design: Rename `memory_recall` to `recall` MCP Tool

**Date:** 2026-11-10  
**Status:** Approved (scope cut: eling-agent repo only)  
**Author:** Antigravity / User  

## 1. Intent & Why

The on-demand context hydration tool in Eling was historically renamed from `session_start` to `memory_recall`. However, as Eling has evolved, its response is no longer just memory search results. It returns a comprehensive workspace context hydration payload:
- `recallMemories` (topic FTS & BM25 search)
- `standingMemories` (standing rules, preferences, baseline spine)
- `recentMemories` (recently updated memories across agents)
- `intentions` (active task triggers)
- `projectScope` (workspace posture, policy, adoptability)
- `codebase` (codebase FTS status: file count, chunk count, watcher state, db path)
- `update` (Eling update notices)
- `recallSummary` (judge / heuristic overflow digest)

Calling this tool `memory_recall` is a misnomer that causes agents to treat it as purely a memory lookup rather than the mandatory turn-0 and topic-shift context hydration gate. Renaming it to `recall` reflects its real identity and purpose.

## 2. Scope & Decision: Clean Cut + Balanced Naming → Two Commits (Now)

The user explicitly selected a **Clean Cut (Hard Rename)**:
- No backward-compatibility alias `memory_recall`.
- The MCP tool name will be strictly `recall`.

**Approved scope expansion (2026-11-13, "Sekarang, 2 commit"): full rename now, two commits.** Commit 1 = §4 (tool classes + contract); Commit 2 = full `MemoryRecall*` → `Recall*` family rename (see §5). Both done in this session, each with `dotnet test` green. — full rename will be done now, in two commits on the same branch, not split across sessions:
- **Commit 1 (MCP tool rename, the pre-existing §4 plan):** tool classes `MemoryRecallTool` → `RecallTool`, `MemoryRecallAgentTool` → `RecallAgentTool`, MCP `Name = "recall"`, ProtectedTools, ToolCatalog, instructions, docs, tests. Commit 1 leaves the internal `MemoryRecall*` domain family untouched (small reviewable contract change).
- **Commit 2 (full domain family rename):** `IMemoryRecallService` → `IRecallService`, `MemoryRecallService` → `RecallService`, namespace `Eling.Core.MemoryRecall` → `Eling.Core.Recall`, folder `Eling.Core/MemoryRecall/` → `Eling.Core/Recall/`, all `MemoryRecall*` DTOs → `Recall*` (`RecallResponse`, `RecallMemory`, `RecallIntention`, `RecallProjectScopeDto`, `RecallStatsDto`, `RecallContextInput`, `RecallStats`, `RecallResult`, `RecallHit`, `RecallContext`, `MemoryRecallIntentionResult` → `RecallIntentionResult`), plus every call site, test fixture, and doc reference within the repo (mechanical identifier replace; verify with build + full test suite).
- Each commit is independently reviewable and revertable; no file is renamed twice.

## 3. Scope: eling-agent Repository Only

Per user decision 2026-11-13: everything **outside** `eling-agent` is explicitly out of scope and handled in a separate session:
- opencode global skills (`git-hygiene-and-safety-check`, `dotnet-publish-staging` SKILL.md)
- `AGENTS.md` in other repos (e.g. ZiziBot.TelegramBot)
- Stored Eling memories referencing `memory_recall` (9 global + 1 project)
- Pre-existing audit trail rows in SQLite keep the old action name (historical data, no migration)

All **current** docs inside this repo (AGENTS.md, INSTALL.md, docs/using-eling.md, docs/recall-architecture.md, docs/agent-setup.md, docs/nested-memory.md, docs/semantic-judge-architecture.md) are updated as part of this change; historical specs and plans are left untouched.

## 4. Changes by Component

### 4.1 Backend C#
1. **`src/backend/Eling.Backend/Mcp/Tools/RecallTool.cs`** (renamed from `MemoryRecallTool.cs`):
   - Class `MemoryRecallTool` → `RecallTool`; `ILogger<RecallTool>`; constructor parameter/references; `[McpServerTool(Name = "recall")]`.
   - Update log messages mentioning `memory_recall` to `recall`.
2. **`src/backend/Eling.Backend/Agent/Services/Tools/RecallAgentTool.cs`** (renamed from `MemoryRecallAgentTool.cs`):
   - Class `MemoryRecallAgentTool` → `RecallAgentTool`; `public string Name => "recall";`.
3. **`src/backend/Eling.Backend/Agent/Services/Tools/AgentToolServiceExtensions.cs`**:
   - Update `AddScoped<MemoryRecallTool>()` → `AddScoped<RecallTool>()` and `AddScoped<IAgentTool, MemoryRecallAgentTool>()` → `AddScoped<IAgentTool, RecallAgentTool>()`.
4. **`src/backend/Eling.Backend/Tools/ToolPolicyStore.cs`**:
   - Update `ProtectedTools` set from `"memory_recall"` to `"recall"`.
5. **`src/backend/Eling.Backend/Tools/ToolCatalog.cs`**:
   - Update tool definition from `"memory_recall"` to `"recall"`.
6. **`src/backend/Eling.Backend/Mcp/ServerInstructions.cs`**:
   - Update instruction text: `call recall BEFORE any response or tool call...`.
7. **`src/backend/Eling.Backend/Agent/Services/AgentTurnService.cs`**:
   - Update system prompt tool list to include `recall` instead of `memory_recall`.
8. **`src/backend/Eling.Core/Memory/MemoryAuditActions.cs`**:
   - Update the `<see cref>` doc comment to `recall` (it is an internal action-name mapping doc, not a tool name).

### 4.2 Unit Tests
0. Rename tool-class-related test files/classes: `MemoryRecallToolTests` → `RecallToolTests`, `MemoryRecallToolProjectScopeTests` → `RecallToolProjectScopeTests`, `MemoryRecallToolCodebaseTests` → `RecallToolCodebaseTests`, `MemoryRecallToolOverflowSummaryTests` → `RecallToolOverflowSummaryTests`, `MemoryRecallToolTopicAliasTests` → `RecallToolTopicAliasTests`; update `new MemoryRecallTool(...)` constructions to `new RecallTool(...)`.
1. **`tests/Eling.Backend.Tests/AgentBuiltinMcpToolTests.cs`**:
   - Update assertion `Assert.Contains(tools, t => t.Name == "recall");` and the `First(t => t.Name == "memory_recall")` lookup.
2. **`tests/Eling.Backend.Tests/Tools/ToolPolicyStoreTests.cs`**:
   - Update test references from `memory_recall` to `recall`.
3. **`tests/Eling.Backend.Tests/Mcp/ToolPolicyFilterTests.cs`**:
   - Update test tool lists from `memory_recall` to `recall`.
4. **`tests/Eling.Backend.Tests/ToolTelemetryFilterTests.cs`**:
   - Update tool call test payload from `memory_recall` to `recall`.
5. **`tests/Eling.Backend.Tests/HarvestNudgeFilterTests.cs`**:
   - Update test `CallAsync(filter, "recall")`.
6. **`tests/Eling.Backend.Tests/Endpoints/ToolsEndpointsTests.cs`** and any other assertion touching the tool name: same substitution.

### 4.3 Documentation & Prompts
1. **`AGENTS.md`**:
   - Update session start instructions to refer to `recall`.
2. **`INSTALL.md`**:
   - Update `session_start_tool: recall`.

## 5. Commit Plan

**Commit 1 — clean cut MCP tool rename** (pre-existing §4.1–4.3; unchanged):
- Tool classes + files (`MemoryRecallTool` → `RecallTool`, `MemoryRecallAgentTool` → `RecallAgentTool`), DI registrations, ProtectedTools, ToolCatalog, ServerInstructions, AgentTurnService, MCP `Name = "recall"`, docs + AGENTS.md + INSTALL.md, tool-name test fixtures.
- Result: agent-facing contract is `recall`; internal `MemoryRecall*` domain family untouched.

**Commit 2 — full domain family rename:**
- `IMemoryRecallService` → `IRecallService`; `MemoryRecallService` → `RecallService`
- Namespace `Eling.Core.MemoryRecall` → `Eling.Core.Recall`; folder `Eling.Core/MemoryRecall/` → `Eling.Core/Recall/`
- DTOs/types: `MemoryRecallResponse` → `RecallResponse`, `MemoryRecallMemory` → `RecallMemory`, `MemoryRecallIntention` → `RecallIntention`, `MemoryRecallIntentionResult` → `RecallIntentionResult`, `MemoryRecallProjectScopeDto` → `RecallProjectScopeDto`, `MemoryRecallStats*` → `RecallStats*`, `MemoryRecallContext` → `RecallContext`, `MemoryRecallContextInput` → `RecallContextInput`, `MemoryRecallHit` → `RecallHit`, `MemoryRecallResult` → `RecallResult`
- Every remaining `memory_recall` string (log messages, doc comments, specs/plans left in place) replaced; `MemoryService.cs` comments updated
- Mechanical identifier replace across the repo (`git mv` for files+folders, then bulk rename, then build + fix fallout)

Both commits verified with `dotnet test` after each. Historical docs (specs, plans, CHANGELOG) stay untouched.

## 6. Verification Plan
- Run `dotnet test tests/Eling.Backend.Tests/Eling.Backend.Tests.csproj`.
- Run `dotnet test tests/Eling.Core.Tests/Eling.Core.Tests.csproj`.
- Run smoke test or check MCP tool catalog output.
