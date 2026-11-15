# codebase_search `match` parameter Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add a `match` parameter to the `codebase_search` MCP tool (and the in-process agent tool) so the query can be matched against the workspace-relative path, the file name, or (default) the file contents — parity with the dashboard's `search=content|path|file`.

**Architecture:** The Core file-search already exists (`CodebaseIndexService.SearchFilesWithTotalsAsync`, `CodebaseFileMatch`). The MCP tool gains a branch: `match` not `content` → file-search branch that returns file rows in a new additive `Files` list with `Mode="files"`; otherwise the existing content path runs unchanged. The response record grows two positional-record-defaulted members so existing callers and tests keep compiling.

**Tech Stack:** C# / .NET 10, xUnit, SQLite (Eling.Core), MCP SDK attribute hosting.

**Spec:** `docs/superpowers/specs/2026-11-15-codebase-search-match-design.md` — this plan argues from it.

## Global Constraints

- `match` values are exactly `content` | `path` | `file`; absent or `""` means `content`. Parsing is `Trim()` + `ToLowerInvariant()` (mirrors the dashboard's `ParseSearchScope`).
- Unknown `match` value → the tool's existing ArgumentException-equivalent return: zero hits, a hint naming the valid values (`Invalid match '<value>'. Valid: content, path, file.`), an `codebase_search` audit entry with outcome Error.
- `CodebaseSearchResponse` gains `Mode` (`"content"` default | `"files"`) and `Files` (`IReadOnlyList<CodebaseSearchFile>?`, default `null`) — positional-record defaults, no existing member reordered or retyped.
- Files mode: `Hits` is empty; `Stats.TotalHits` is the sum of per-root complete match counts (`SearchFilesWithTotalsAsync` result `TotalByRoot`), never the returned row count; `Fresh` comes from the existing `HasIndexOnDisk`; an empty-match answer carries a hint exactly like the content branch (both fresh/not-fresh wordings).
- `pathPrefix`/`filePattern` apply to `content` only; in files mode they are ignored (the Core file-search takes no such filters) and the `[Description]` says so.
- Roots resolution is shared with the content branch: explicit `projects[]` (validated via `EnsureCodebaseRootsAllowed`) → `scope=project` (local, `roots=null`) → global with `projectFilter` (existing `ApplyProjectFilter` early-return when nothing matches, reused verbatim).
- Empty query stays unchanged: "Query is empty.", and records no audit entry.
- Audit in files mode: one `codebase_search` access entry, hit count = file count, no content rows.
- Repo rules for this checkout: a sibling session's work is in the tree — stage ONLY this plan's paths, never `git add -A`; after EVERY commit lands, run `pwsh scripts/clean-bin.ps1`; do not kill `dotnet watch`; backend test command is `dotnet test tests/Eling.Backend.Tests --filter <filter>`; no frontend build (dashboard untouched).

## Review Focus

1. Case/whitespace in `match` (`"FILE"`, `" path "`) → trimmed, lower-cased, accepted; only genuinely unknown values get the hint. Pinned in Task 1 tests.
2. `projectFilter` that excludes every root in files mode → the same "No indexed workspace matches" hint the content branch returns, never a misleading empty-success. Pinned in Task 1 tests.
3. `Stats.TotalHits` must stay limit-independent: 3 matching files with `limit=2` → `Files.Count == 2` but `TotalHits == 3`. Pinned in Task 1 tests.
4. Federated files mode via explicit `projects[]`: sibling root's rows carry that root in `ProjectRoot`. Pinned in Task 1 tests.
5. `match=file` with zero matches must return `Mode="files"`, empty `Files`, and a hint — never silently fall back to content search. Pinned in Task 1 tests.
6. (Inspection, not a test) The files branch records exactly one `codebase_search` audit entry carrying metadata only — single shared `RecordSearchAsync` call site, verified during Task 3's full-suite run via the existing audit hook tests.

---

### Task 1: Files mode in the MCP tool (DTOs + branch) with tests

**Files:**
- Modify: `src/backend/Eling.Backend/Dtos/CodebaseDtos.cs` (add `CodebaseSearchFile`, extend `CodebaseSearchResponse`)
- Modify: `src/backend/Eling.Backend/Mcp/Tools/CodebaseSearchTool.cs` (new parameter, doc comment, `[Description]`, files branch)
- Create: `tests/Eling.Backend.Tests/CodebaseSearchMatchTests.cs`

**Interfaces:**
- Consumes: `CodebaseIndexService.SearchFilesWithTotalsAsync(string needle, int limit, CodebaseFileMatch match, IEnumerable<string>? projectRoots, CancellationToken)` returning `FileSearchResult(Files, TotalByRoot)`; `CodebaseFileSummary(ProjectRoot, Path, ChunkCount, LastIndexedAt, TotalMatches)`; `CodebaseFileMatch.Path | FileName`; existing tool members `SearchableRootsAsync`, `ApplyProjectFilter`, `HasIndexOnDisk`, `_audit.RecordSearchAsync`.
- Produces: `CodebaseSearchFile(string ProjectRoot, string Path, int ChunkCount, string LastIndexedAt)`; `CodebaseSearchResponse(Hits, Stats, Mode = "content", Files = null)`; `public Task<CodebaseSearchResponse> SearchAsync(string query, int limit = 10, string? pathPrefix = null, string? filePattern = null, string? projectFilter = null, string scope = "all", string[]? projects = null, string? match = null, CancellationToken cancellationToken = default)`.

- [ ] **Step 1: Write the failing tests** in `tests/Eling.Backend.Tests/CodebaseSearchMatchTests.cs`, copying the temp-workspace harness pattern from `CodebaseFileSearchTests.cs` (temp workspace + temp data dir, `CodebaseIndexService` + `SqliteCodebaseIndex`, `svc.IndexAsync(full: true)`) and the tool invocation pattern from `AuditCodebaseHookTests.cs` (`new CodebaseSearchTool(svc, registry: null, logger: null)`). Fixture: write files `src/backend/Widget.cs` and `src/widget/WidgetHelper.cs` into the workspace and index them. Test names and assertions:

  ```csharp
  public async Task File_Mode_Finds_A_File_By_Name()
  {
      var response = await Tool().SearchAsync("widget", match: "file");
      Assert.Equal("files", response.Mode);
      Assert.Empty(response.Hits);
      Assert.Contains(response.Files!, f => f.Path == "src/backend/Widget.cs");
  }

  public async Task Path_Mode_Finds_Files_Under_A_Folder_Name()
  {
      var response = await Tool().SearchAsync("backend", match: "path");
      Assert.Equal("files", response.Mode);
      var file = Assert.Single(response.Files!);
      Assert.Equal("src/backend/Widget.cs", file.Path);
  }

  public async Task File_Mode_Ignores_A_Folder_Name_In_The_Path()
  {
      var response = await Tool().SearchAsync("backend", match: "file");
      Assert.Equal("files", response.Mode);
      Assert.Empty(response.Files!);
      Assert.NotNull(response.Stats.Hint);
  }

  public async Task Match_Is_Case_And_Whitespace_Insensitive()
  {
      var response = await Tool().SearchAsync("widget", match: " FILE ");
      Assert.Equal("files", response.Mode);
  }

  public async Task An_Unknown_Match_Returns_A_Hint_With_Zero_Hits()
  {
      var response = await Tool().SearchAsync("widget", match: "name");
      Assert.Equal("content", response.Mode);
      Assert.Equal(0, response.Stats.TotalHits);
      Assert.Equal("Invalid match 'name'. Valid: content, path, file.", response.Stats.Hint);
  }

  public async Task TotalHits_Is_Limit_Independent()
  {
      var response = await Tool().SearchAsync("widget", limit: 1, match: "path");
      Assert.Single(response.Files!);
      Assert.Equal(2, response.Stats.TotalHits);
  }

  public async Task File_Rows_Carry_ProjectRoot_ChunkCount_And_LastIndexedAt()
  {
      var response = await Tool().SearchAsync("widget", match: "file");
      Assert.All(response.Files!, f =>
      {
          Assert.True(f.ChunkCount > 0);
          Assert.Equal(Path.GetFullPath(_workspace), f.ProjectRoot);
          Assert.NotNull(f.LastIndexedAt);
      });
  }

  public async Task Content_Mode_Is_The_Default_And_Unchanged()
  {
      var response = await Tool().SearchAsync("ZephyrWidget");
      Assert.Equal("content", response.Mode);
      Assert.Null(response.Files);
      Assert.NotEmpty(response.Hits);
  }

  public async Task A_ProjectFilter_That_Excludes_Everything_Reports_It()
  {
      var response = await Tool().SearchAsync("widget", match: "file", projectFilter: "a-workspace-name-that-cannot-match-zzzz");
      Assert.Equal("content", response.Mode);
      Assert.Equal(0, response.Stats.TotalHits);
      Assert.Equal("No indexed workspace matches projectFilter 'a-workspace-name-that-cannot-match-zzzz'.", response.Stats.Hint);
  }

  public async Task A_Zero_Match_Files_Search_Does_Not_Fall_Back_To_Content()
  {
      var response = await Tool().SearchAsync("no-such-file-name-anywhere", match: "file");
      Assert.Equal("files", response.Mode);
      Assert.Empty(response.Files!);
      Assert.Empty(response.Hits);
      Assert.NotNull(response.Stats.Hint);
  }

  public async Task A_Second_Project_Is_Searched_Through_Explicit_Projects()
  {
      var sibling = Path.Combine(Path.GetTempPath(), "eling-match-sibling-" + Guid.NewGuid().ToString("N")[..8]);
      Directory.CreateDirectory(sibling);
      await File.WriteAllTextAsync(Path.Combine(sibling, "SiblingNeedle.cs"), "class SiblingNeedle { }\n");
      var siblingSvc = new CodebaseIndexService(sibling, new SqliteCodebaseIndex(Path.Combine(Path.GetTempPath(), "eling-match-sib-db-" + Guid.NewGuid().ToString("N")[..8], "codebase.db")));
      await siblingSvc.IndexAsync(full: true);

      var response = await Tool().SearchAsync("SiblingNeedle", match: "file", projects: [sibling]);

      Assert.Contains(response.Files!, f => f.ProjectRoot == Path.GetFullPath(sibling) && f.Path == "SiblingNeedle.cs");
  }
  ```

  (`TotalHits_Is_Limit_Independent` expects 2 — both fixture files match `widget` case-insensitively under `path`. The unknown-match and projectFilter-empty tests assert `Mode="content"` because those paths return through the content-mode early-return shape; if the implementer instead tags them `files`, that is a spec-level call to flag back, not to copy silently.)

- [ ] **Step 2: Run the tests to verify they fail**

  Run: `dotnet test tests/Eling.Backend.Tests --filter FullyQualifiedName~CodebaseSearchMatchTests -v minimal`
  Expected: FAIL — the `match` parameter does not exist (compile error) is the cleanest failure; a compile error stopping the whole test project's build is acceptable for this step and this task only.

- [ ] **Step 3: Add the DTOs** in `src/backend/Eling.Backend/Dtos/CodebaseDtos.cs`:

  ```csharp
  public sealed record CodebaseSearchFile(
      string ProjectRoot,
      string Path,
      int ChunkCount,
      string LastIndexedAt);

  public sealed record CodebaseSearchResponse(
      IReadOnlyList<CodebaseSearchHit> Hits,
      CodebaseSearchStats Stats,
      string Mode = "content",
      IReadOnlyList<CodebaseSearchFile>? Files = null);
  ```

  Do not change any other record; the two new members are appended positional parameters with defaults.

- [ ] **Step 4: Implement the tool branch** in `src/backend/Eling.Backend/Mcp/Tools/CodebaseSearchTool.cs`:
  - Class doc comment: add one sentence — files mode matches the query against indexed file paths and answers with file rows, no line ranges.
  - `[Description]`: after `... projects for exact roots.` add `match 'file' or 'path' to find files by name/path instead of contents (returns file rows, no line ranges; pathPrefix/filePattern apply to content only).`
  - New parameter, positioned after `projects` and before `cancellationToken`:

    ```csharp
    [Description("What the query is matched against: 'content' (default, FTS over file contents), 'path' (workspace-relative path, folders included), 'file' (file name only). 'path'/'file' return file rows, not line ranges; pathPrefix/filePattern apply to content only.")] string? match = null,
    ```

  - After the `if (string.IsNullOrWhiteSpace(query))` early return and inside the `try`, parse and guard inline (do NOT throw — a tool has no status code):

    ```csharp
    var parsed = (match ?? "content").Trim().ToLowerInvariant();
    if (parsed is not ("content" or "path" or "file"))
    {
        sw.Stop();
        _logger?.LogWarning("Rejected codebase search match: {Match}", match);
        await _audit.RecordSearchAsync(query, 0, _svc.ProjectRoot, (int)sw.ElapsedMilliseconds, AuditOutcome.Error);
        return new CodebaseSearchResponse([], new CodebaseSearchStats(0, sw.ElapsedMilliseconds, false, $"Invalid match '{match}'. Valid: content, path, file."));
    }
    ```
  - Resolve `roots` exactly as the content branch does (the explicit-roots / `scope=project` / global + `projectFilter` early-returns stay untouched and shared), then inside the `else` global block, before `_svc.SearchAllAsync`, branch on `parsed`:

    ```csharp
    if (parsed != "content")
        return await FilesResponseAsync(query, clamped, parsed == "path" ? CodebaseFileMatch.Path : CodebaseFileMatch.FileName, searchedRoots, _svc.ProjectRoot, sw, _audit, cancellationToken);
    ```

    and the same one-line call at the equivalent points in the explicit-roots and `scope=project` branches (files mode must work in all three root modes).
  - New private helper `FilesResponseAsync` that: calls `_svc.SearchFilesWithTotalsAsync(query, limit, kind, roots, ct)`; builds `CodebaseSearchFile` rows; `totalHits = fileResult.TotalByRoot.Values.Sum()`; `fresh = HasIndexOnDisk(roots, localRoot)`; `hint` null when rows exist else the content-branch wording (fresh ? "No files matched the query and filters." : "No index for this scope. Run codebase_index to build it."); records `codebase_search` audit with the file count; returns
    `new CodebaseSearchResponse([], new CodebaseSearchStats(totalHits, sw.ElapsedMilliseconds, fresh, hint), "files", rows)`.
    Check the exact `RecordSearchAsync` overload and `AuditOutcome` usage against the existing call sites before writing this.

- [ ] **Step 5: Run the tests to verify they pass**

  Run: `dotnet test tests/Eling.Backend.Tests --filter FullyQualifiedName~CodebaseSearchMatchTests -v minimal`
  Expected: PASS, 11 tests.

- [ ] **Step 6: Run the neighbouring suite to catch regressions**

  Run: `dotnet test tests/Eling.Backend.Tests --filter "FullyQualifiedName~Codebase" -v minimal`
  Expected: PASS — `CodebaseFileSearchTests`, `CodebaseSearchStatsTests`, `CodebaseSearchFallbackTests`, `AuditCodebaseHookTests`, `CodebaseRebuildScopeTests`, `CodebaseFileControllerTests` all green.

- [ ] **Step 7: Commit**

  ```bash
  git add docs/superpowers/specs/2026-11-15-codebase-search-match-design.md docs/superpowers/plans/2026-11-15-codebase-search-match.md src/backend/Eling.Backend/Dtos/CodebaseDtos.cs src/backend/Eling.Backend/Mcp/Tools/CodebaseSearchTool.cs tests/Eling.Backend.Tests/CodebaseSearchMatchTests.cs
  git commit -m "feat(codebase): add match parameter to codebase_search for file/path rows"
  pwsh scripts/clean-bin.ps1
  ```

### Task 2: Agent (chat) tool parity

**Files:**
- Modify: `src/backend/Eling.Backend/Agent/Services/Tools/CodebaseSearchAgentTool.cs`
- Test: `tests/Eling.Backend.Tests/CodebaseSearchMatchTests.cs` (add one test)

**Interfaces:**
- Consumes: `CodebaseSearchTool.SearchAsync(..., string? match, ...)` from Task 1.
- Produces: agent tool accepts `match` in its JSON schema and forwards it.

- [ ] **Step 1: Write the failing test** — add to `CodebaseSearchMatchTests.cs`:

  ```csharp
  public async Task Agent_Tool_Forwards_The_Match_Parameter()
  {
      var tool = new CodebaseSearchAgentTool(Tool());
      var json = await tool.ExecuteAsync("{\"query\":\"widget\",\"match\":\"file\"}", CancellationToken.None);
      using var doc = JsonDocument.Parse(json);
      Assert.Equal("files", doc.RootElement.GetProperty("mode").GetString());
      Assert.NotEmpty(doc.RootElement.GetProperty("files").EnumerateArray());
  }
  ```

  (If the agent tool's JSON casing is camelCase — it is, see `JsonOptions` — the property names are `mode` and `files`.)

- [ ] **Step 2: Run the test to verify it fails**

  Run: `dotnet test tests/Eling.Backend.Tests --filter FullyQualifiedName~Agent_Tool_Forwards -v minimal`
  Expected: FAIL — `ArgumentException` (unknown parameter) or `KeyNotFoundException` on `mode`.

- [ ] **Step 3: Wire the parameter** in `CodebaseSearchAgentTool.cs`:
  - `Description` gains: `Pass match='file' or 'path' to find files by name/path.`
  - `ParametersJsonSchema` gains: `"match": { "type": "string", "description": "What the query is matched against: 'content' (default), 'path' (workspace-relative path), 'file' (file name only). 'path'/'file' return file rows." }`
  - In `ExecuteAsync`: read `match` from `root` with the same `TryGetProperty` pattern as `scope`; pass it as named argument `match: match` to `tool.SearchAsync` (construct `match`, not positional — the parameter sits between `projects` and `cancellationToken`).

- [ ] **Step 4: Run the tests to verify they pass**

  Run: `dotnet test tests/Eling.Backend.Tests --filter FullyQualifiedName~CodebaseSearchMatchTests -v minimal`
  Expected: PASS, 12 tests.

- [ ] **Step 5: Commit**

  ```bash
  git add src/backend/Eling.Backend/Agent/Services/Tools/CodebaseSearchAgentTool.cs tests/Eling.Backend.Tests/CodebaseSearchMatchTests.cs
  git commit -m "feat(agent): forward match parameter through codebase_search agent tool"
  pwsh scripts/clean-bin.ps1
  ```

### Task 3: Instruction text alignment, full suite, final review

**Files:**
- Modify: `src/backend/Eling.Backend/Mcp/ServerInstructions.cs` ("Codebase:" section, line ~23)

**Interfaces:**
- Produces: MCP initialize handshake text consistent with the new parameter.

- [ ] **Step 1: Check the sibling session's uncommitted hunk first**

  Run: `git diff src/backend/Eling.Backend/Mcp/ServerInstructions.cs`
  Expected: understand exactly which lines the other session is editing. The "Codebase:" section wording (`narrowed with pathPrefix/filePattern`) is the only hunk this task touches; if their edit overlaps it, STOP, leave the file to them, note it in the final report, and skip to Step 3 with the file uncommitted.

- [ ] **Step 2: Edit only the "Codebase:" sentence**

  Replace `narrowed with pathPrefix/filePattern` with `narrowed with pathPrefix/filePattern, or matched against file paths/names with match='path'|'file'`. Keep the rest of the paragraph byte-identical. Stage only this file:

  ```bash
  git add src/backend/Eling.Backend/Mcp/ServerInstructions.cs
  git diff --cached --stat   # MUST list exactly this one file; if it shows their unrelated hunks too, unstage (git restore --staged <this file>) and STOP for user decision
  git commit -m "docs(instructions): align codebase search guidance with match parameter"
  pwsh scripts/clean-bin.ps1
  ```

- [ ] **Step 3: Run the full backend suite**

  Run: `dotnet test tests/Eling.Backend.Tests -v minimal`
  Expected: PASS (baseline before this work was 2020 tests green on 2026-11-14; count should grow by 12). If a preexisting failure unrelated to this change appears, record it, do not fix it here.

- [ ] **Step 4: Verify the audit trail for the files branch (inspection)**

  Confirm the files branch makes exactly one `RecordSearchAsync` call carrying query + file count and zero content (see Task 1 Step 4). Cross-check with the green `AuditCodebaseHookTests` run from Step 3.

- [ ] **Step 5: Hand back for review**

  Summarize: which commits landed, any deviation (e.g. the ServerInstructions skip in Step 1/2), and the final test count. Do not push; pushing and PR are the user's call.
