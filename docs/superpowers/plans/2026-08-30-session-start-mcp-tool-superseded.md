# Session Start MCP Tool Implementation Plan

> **Superseded by `memory_recall` rename.** This plan documents the historical implementation of the original `session_start` MCP tool. It is preserved as a record of the design decisions made at the time (e.g. `Intention.Pattern`, sub-folder by feature, tool name `session_start`). The tool has since been renamed to `memory_recall` to better reflect what it does; see CHANGELOG.md `[Unreleased]` for the rename + bug fix (RecallMemories was always `[]`, recentLimit was hard-coded `Take(5)`). The new tool lives in `src/backend/Eling.Core/MemoryRecall/` and `src/backend/Eling.Backend/Mcp/Tools/MemoryRecallTool.cs`. Do not implement this plan as-is — it is no longer the source of truth.

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [x]`) syntax for tracking.

**Goal:** Align the already-built `memory_session_start` MCP tool to the locked spec `docs/superpowers/specs/2026-08-30-session-start-mcp-tool-spec-superseded.md` (this spec is now superseded; see its header for context): structured `context` input, trigger-matched intentions with `matched`/`expired`, stats, ~200-char truncation, and an `Intention.Pattern` model extension.

**Architecture:** `Eling.Application.SessionStartService` owns all aggregation (recall, recent, intentions + trigger matching, stats, truncation) and returns a `SessionStartResult`; the MCP tool `MemoryTools.SessionStartAsync` is a thin DTO mapper. `IIntentionStorage` and `ISessionStartService` are already DI-registered in `AddElingCoreServices`.

**Tech Stack:** .NET 10, ModelContextProtocol (McpServerTool), YamlDotNet (intention front-matter), xUnit.

## Global Constraints

- Spec path: `docs/superpowers/specs/2026-08-30-session-start-mcp-tool-spec-superseded.md` (the original filename was retired when the spec was marked historical after the `memory_recall` rename).
- Backward compatibility: `Intention.Pattern` must be optional — existing intention markdown without `trigger_pattern` reads as `null`.
- Clean layer boundaries: aggregation logic in `Eling.Application`, not the MCP tool.
- All new project memory content and code docs in English.
- Do NOT run `git commit` unless the user explicitly asks.
- Language/scope values: `scope` ∈ `project | global | merged` (default `merged`); invalid scope throws `ArgumentException`.
- Truncation: ~200 chars, append `...`.

---

## File Structure

- Modify: `src/backend/Eling.Core/Intention.cs` — add `Pattern`.
- Modify: `src/backend/Eling.Application/IntentionFrontMatter.cs` — add `Pattern` alias `trigger_pattern`.
- Modify: `src/backend/Eling.Application/FileSystemIntentionStorage.cs` — map `Pattern` in serialize/deserialize.
- Modify: `src/backend/Eling.Application/ISessionStartService.cs` — new `SessionStartContext` + `SessionStartResult`.
- Modify: `src/backend/Eling.Application/SessionStartService.cs` — context input, stats, truncation, trigger matching.
- Modify: `src/backend/Eling.Application/SessionStartResult.cs` — add stats + intention match status.
- Modify: `src/backend/Eling.Mcp/MemoryTools.cs` — `SessionStartAsync` signature (context obj) + mapping.
- Modify: `src/backend/Eling.Mcp/SessionStartResponse.cs`, `SessionStartMemory.cs`, `SessionStartIntention.cs` — add stats + matched/expired.
- Add: `src/backend/Eling.Mcp/SessionStartRequest.cs` — input model.
- Test: `tests/Eling.Application.Tests/SessionStartServiceTests.cs`, `tests/Eling.Application.Tests/IntentionStorageTests.cs` (or extend), `tests/Eling.Mcp.Tests/MemoryToolsTests.cs`.

---

### Task 1: Add `Pattern` to Intention model + storage

**Files:**
- Modify: `src/backend/Eling.Core/Intention.cs`
- Modify: `src/backend/Eling.Application/IntentionFrontMatter.cs`
- Modify: `src/backend/Eling.Application/FileSystemIntentionStorage.cs`
- Test: `tests/Eling.Application.Tests/IntentionStorageTests.cs` (create if absent)

**Interfaces:**
- Consumes: existing `Intention` (constructor unchanged, new settable property).
- Produces: `Intention.Pattern` (`string?`), serialized as front-matter `trigger_pattern`.

- [x] **Step 1: Write failing test — Pattern round-trips through front-matter**

Add to `tests/Eling.Application.Tests/IntentionStorageTests.cs`:

```csharp
[Fact]
public async Task Pattern_round_trips_through_front_matter()
{
    var dir = Path.Combine(Path.GetTempPath(), "eling-int-storage-" + Guid.NewGuid());
    var storage = new FileSystemIntentionStorage(dir);
    var intention = new Intention("check db", TriggerType.FilePattern) { Pattern = "**/*.md" };
    await storage.SaveAsync(intention);
    var loaded = await storage.GetByIdAsync(intention.Id);
    Assert.NotNull(loaded);
    Assert.Equal("**/*.md", loaded!.Pattern);
}
```

- [x] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Eling.Application.Tests/Eling.Application.Tests.csproj --filter "Pattern_round_trips_through_front_matter"`
Expected: FAIL (`Pattern` does not exist yet).

- [x] **Step 3: Implement `Intention.Pattern`**

In `src/backend/Eling.Core/Intention.cs` add:

```csharp
public string? Pattern { get; set; }
```

- [x] **Step 4: Implement front-matter + storage mapping**

In `IntentionFrontMatter.cs`:

```csharp
[YamlMember(Alias = "trigger_pattern")]
public string? Pattern { get; set; }
```

In `FileSystemIntentionStorage.SaveAsync`, add to the front-matter block:

```csharp
Pattern = intention.Pattern
```

and in `ParseIntention`, pass it through the `new Intention(...)` object:

```csharp
{ Status = status, Pattern = frontMatter.Pattern }
```

- [x] **Step 5: Run test to verify it passes**

Run: `dotnet test tests/Eling.Application.Tests/Eling.Application.Tests.csproj --filter "Pattern_round_trips_through_front_matter"`
Expected: PASS.

- [x] **Step 6: Report changed files + `git status --short`; stop for user.**

---

### Task 2: Update `SessionStartContext` + `SessionStartResult` shapes

**Files:**
- Modify: `src/backend/Eling.Application/ISessionStartService.cs`
- Modify: `src/backend/Eling.Application/SessionStartResult.cs`

**Interfaces:**
- Consumes: `SessionStartResult` current 3-field record.
- Produces:
  - `record SessionStartContext(string? FilePath, IReadOnlyCollection<string>? Topics, string? Project)`
  - `record SessionStartIntentionResult(Intention Intention, bool Matched, bool Expired)`
  - `record SessionStartStats(int TotalMemories, int ActiveMemories, int ActiveIntentions)`
  - `record SessionStartResult(IReadOnlyCollection<ScopedMemory> RecallMemories, IReadOnlyCollection<ScopedMemory> RecentMemories, IReadOnlyCollection<SessionStartIntentionResult> Intentions, SessionStartStats Stats)`
  - `ISessionStartService.GetSessionStartAsync(SessionStartContext? context, int recallLimit, int recentLimit, string scope, CancellationToken ct)`

- [x] **Step 1: Update `ISessionStartService`**

Replace the current interface with:

```csharp
Task<SessionStartResult> GetSessionStartAsync(
    SessionStartContext? context = null,
    int recallLimit = 10,
    int recentLimit = 10,
    string scope = "merged",
    CancellationToken cancellationToken = default);
```

- [x] **Step 2: Update `SessionStartResult` records** (add context/stats/intention-result records as listed in Interfaces).
- [x] **Step 3: Build**

Run: `dotnet build Eling.slnx -v minimal`
Expected: FAIL with signature mismatches in `SessionStartService`/tests (expected — they are fixed in later tasks).

- [x] **Step 4: Report changed files + `git status --short`; stop for user.**

---

### Task 3: Implement aggregation in `SessionStartService`

**Files:**
- Modify: `src/backend/Eling.Application/SessionStartService.cs`

**Interfaces:**
- Consumes: `SessionStartContext`, `SessionStartResult`, `SessionStartIntentionResult`, `SessionStartStats` (from Task 2); `IScopedMemoryService`, `IIntentionStorage`, `Intention.Pattern`.
- Produces: full `GetSessionStartAsync` implementation with recall dedup, recent sort+limit, trigger matching, stats, truncation.

- [x] **Step 1: Refactor method signature** to `(SessionStartContext? context, ...)`; derive `topics = context?.Topics`, `filePath = context?.FilePath`, `project = context?.Project`.
- [x] **Step 2: Add truncation helper**

```csharp
private static string Truncate(string content, int max = 200)
{
    if (content.Length <= max) return content;
    return content.Substring(0, max) + "...";
}
```

Apply to recall and recent `content` (via copied `Memory` or truncation flag in DTO — prefer truncating in the DTO mapper using a constant length so the service keeps full content; the spec says ~200 chars, so map here or in mapper consistently).

Decision: truncate in the MCP mapper (`SessionStartMemory.From`) at 200 chars. Service returns full `ScopedMemory`.

- [x] **Step 3: Stats** — derive from loaded data:
  - `TotalMemories` = count of `ListAsync(scope)` (all statuses).
  - `ActiveMemories` = active count.
  - `ActiveIntentions` = non-expired active intention count (reuse intention list).
- [x] **Step 4: Trigger matching** — implement `static (bool Matched, bool Expired) Match(Intention, SessionStartContext, DateTimeOffset now)` per spec §5 (`Topic` overlap vs Pattern/tags/description; `FilePattern` glob vs FilePath; `TimeBased` within 24h). Include glob→regex helper.

Add to the file:

```csharp
using System.Text.RegularExpressions;

private static Regex GlobToRegex(string glob)
{
    // Translate simple glob (**/*.cs, *.md) to regex.
    var pattern = "^" + Regex.Escape(glob)
        .Replace("\\*\\*", ".*")
        .Replace("\\*", "[^/]*")
        .Replace("\\?", ".") + "$";
    return new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.Compiled);
}
```

- [x] **Step 5: Wire intentions** to `SessionStartIntentionResult` list (filter Active; compute matched/expired per intention).
- [x] **Step 6: Build**

Run: `dotnet build Eling.slnx -v minimal`
Expected: PASS (or only remaining test compile mismatches).

- [x] **Step 7: Report changed files + `git status --short`; stop for user.**

---

### Task 4: Update MCP DTOs + tool signature to final contract

**Files:**
- Modify: `src/backend/Eling.Mcp/MemoryTools.cs`
- Modify: `src/backend/Eling.Mcp/SessionStartResponse.cs`
- Modify: `src/backend/Eling.Mcp/SessionStartMemory.cs`
- Modify: `src/backend/Eling.Mcp/SessionStartIntention.cs`
- Add: `src/backend/Eling.Mcp/SessionStartRequest.cs`

**Interfaces:**
- Consumes: `SessionStartContext`, `SessionStartResult`, `SessionStartIntentionResult`, `SessionStartStats`, truncation length 200.
- Produces: MCP tool `memory_session_start` with `context` object input; DTOs with `matched`/`expired` on intentions and a `stats` block.

- [x] **Step 1: Add `SessionStartRequest`**

```csharp
public sealed class SessionStartContextInput
{
    public string? FilePath { get; set; }
    public string[]? Topics { get; set; }
    public string? Project { get; set; }
}
```

- [x] **Step 2: Extend `SessionStartIntention` DTO** with `matched` (bool) and `expired` (bool); add `From(SessionStartIntentionResult)`.
- [x] **Step 3: Truncate content at 200 chars in `SessionStartMemory.From`** (append `...` when longer).
- [x] **Step 4: Extend `SessionStartResponse`** with `Stats` (add `SessionStartStats` DTO or an inline `stats` object).
- [x] **Step 5: Rewrite `MemoryTools.SessionStartAsync`**

```csharp
[McpServerTool(Name = "memory_session_start")]
public async Task<SessionStartResponse> SessionStartAsync(
    [Description("Current task/context (filePath, topics, project). Optional.")] SessionStartContextInput? context = null,
    [Description("Max recalled memories (default 10).")] int recallLimit = 10,
    [Description("Max recent memories (default 10).")] int recentLimit = 10,
    [Description("project | global | merged (default merged).")] string scope = "merged")
{
    if (_sessionStart is null) throw new InvalidOperationException(
        "Scoped session service is not available in this host configuration.");
    var ctx = context is null ? null : new SessionStartContext(context.FilePath, context.Topics, context.Project);
    var result = await _sessionStart.GetSessionStartAsync(ctx, recallLimit, recentLimit, scope);
    // map to SessionStartResponse ...
}
```

- [x] **Step 6: Build**

Run: `dotnet build Eling.slnx -v minimal`
Expected: PASS.

- [x] **Step 7: Report changed files + `git status --short`; stop for user.**

---

### Task 5: Tests for the final service + tool

**Files:**
- Modify: `tests/Eling.Application.Tests/SessionStartServiceTests.cs`
- Modify: `tests/Eling.Mcp.Tests/MemoryToolsTests.cs`

**Interfaces:**
- Consumes: final `ISessionStartService` signature, `SessionStartResult` (with stats + intention results), `memory_session_start` context input.
- Produces: coverage for the new behaviors.

- [x] **Step 1: Service tests** — add:
  - `Session_start_returns_stats_counts` (total/active/activeIntentions).
  - `Session_start_marks_topic_intention_matched_when_overlap`.
  - `Session_start_marks_filepattern_intention_matched_on_filePath`.
  - `Session_start_marks_timebased_intention_expired_when_past`.
  - `Session_start_output_truncates_for_token_economy` (if truncation done in mapper, test the DTO mapper instead).
- [x] **Step 2: MCP tool tests** — update existing `SessionStartAsync_...` to pass `context` object and assert `Stats` + `matched`/`expired` in mapped DTOs; update the "no session service" negative test.
- [x] **Step 3: Run all relevant tests**

Run: `dotnet test tests/Eling.Application.Tests/Eling.Application.Tests.csproj; dotnet test tests/Eling.Mcp.Tests/Eling.Mcp.Tests.csproj`
Expected: all PASS.

- [x] **Step 4: Full build + full test run**

Run: `dotnet build Eling.slnx -v minimal` then `dotnet test Eling.slnx --artifacts-path .bin-test --no-build`
Expected: Build succeeded; Eling.Mcp.Tests and Eling.Application.Tests green. (Eling.Host.Tests may show flaky `hostpolicy.dll` failures in full parallel run — re-run them alone to confirm not a regression.)

- [x] **Step 5: Report changed files + `git status --short`; stop for user.**

---

## Post-Plan Work (after all 5 tasks above)

Done outside the original 5-task plan, in response to user feedback:

- [x] **Refactor cleanup**: applied Eling code style (one type per file, method < 50 lines, extracted IntentionTriggerMatcher + GlobPattern to separate files, removed silent 	ry/catch, removed magic-number 200 in favor of const int ContentMaxLength, dropped _sessionStart param from non-scoped MemoryTools ctor overload).
- [x] **Sub-folder by feature**: Eling.Application/SessionStart/ (interface + records + service + matcher, namespace Eling.Application.SessionStart); Eling.Mcp/Dtos/ (DTOs, namespace Eling.Mcp.Dtos). Matches the Eling.Dashboard/Dtos + Endpoints pattern.
- [x] **Rename + split tool**: tool renamed from memory_session_start to session_start (mirrors Vestige naming). Extracted SessionStartTool into its own file (src/backend/Eling.Mcp/SessionStartTool.cs, [McpServerToolType]). MemoryTools.cs is now pure memory CRUD again. Test moved to SessionStartToolTests.cs.
- [x] **Test naming convention**: renamed all Session_start_* tests to MethodName_Scenario_Return to match Eling convention (e.g. SaveAsync_WithValidInputs_SavesAndReturnsMemory).
- [x] **AGENTS.md wiring**: updated both ~/.config/opencode/AGENTS.md (global, uses mcp_eling_*) and C:\some-folder\Eling\AGENTS.md (project, prefers mcp_eling_dev_* with fallback to global). Both now require Eling session_start as the first tool call of every new chat. Vestige still enabled in opencode.json (user choice � pending removal).
- [x] **Test workflow rule**: recorded in AGENTS.md (project) and Eling project memory: unit tests always one csproj at a time, never dotnet test Eling.slnx, never chained with ;.

## Final Status

- All 5 planned tasks complete.
- All post-plan refinements above complete.
- Build: 0 errors.
- Unit tests: 112/112 pass (22 Core + 56 Application + 34 Mcp; dotnet test Eling.slnx skipped per project rule).
- Tool session_start is live in the project dev MCP server (mcp_eling_dev_session_start, port 4417). Global mcp_eling_session_start requires re-publishing ~/.local/bin/eling.exe (deferred to user).
- Working tree: changes left in place per Eling AGENTS.md Git workflow (user-controlled commits).

