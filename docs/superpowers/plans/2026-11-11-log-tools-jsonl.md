# Log Tools Read JSONL Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** MCP `log_read` (and its agent-loop adapter) follow the user's `log.json` format and span both `.log` and `.jsonl` archives for the requested date/today, returning raw lines.

**Architecture:** `LogReader.Read` (Eling.Core) becomes multi-segment: it gathers every file that can hold lines for the request, merges them with a per-line timestamp sort key, filters, and tails the merged sequence — `LogTools` (MCP) and `/api/logs/read` (dashboard) both inherit cross-format reads with no call-site change. A `Func<LogFormat>` seam lets DI inject the user's configured format into `LogTools`; a missing seam means `Text` (today's behavior).

**Tech Stack:** .NET 10, Serilog 4.4 (no new packages), xUnit.

**Spec:** `docs/superpowers/specs/2026-11-11-log-tools-jsonl-design.md` (read the "Cross-format reads" section first — it is the contract for Task 1)

## Global Constraints

- .NET 10, Serilog 4.4; NO new NuGet packages.
- Repo convention: a file holding >1 top-level type may only group model/record DTOs (`LogModels.cs` pattern); new helpers go in their own file.
- `LogReader` and everything in `Eling.Core` stay free of backend/DI references.
- Every catch block logs (standing rule) — no silent catches anywhere in this plan.
- Line output stays **raw**: no JSON parsing of log lines anywhere.
- Do NOT run `pnpm build` / `next build`; no frontend in scope.
- Commits: one logical change per commit; ask the user before committing.
- Build to a separate output dir if the dev backend is running under `dotnet watch` (AGENTS.md); check `git status` cleanliness before starting.

## Review Focus

1. **Dated read where only one extension exists** → today's single-file result; unchanged totals semantics except `Files` added (tests pin).
2. **A line whose timestamp parses in neither format** → kept, in segment order (text segments first, then jsonl); never dropped, never throws (carry-forward key, see Task 1).
3. **Double-switch day** (text → jsonl → text, one shared `.log` archive with two disjoint spans) → per-line key reorders correctly; test with interleaved timestamps (Task 1).
4. **`contains`/`level` against a JSONL line** → match via the existing `LevelTokens` JSONL entries and raw substring; a line matching in the `.log` file and a different line matching in the `.jsonl` file both appear in the merged result (Task 1 test).
5. **Active read on a stale `backend.log`** → the merge must NOT resurrect it: only today's archives + the current-format active file are gathered; `backend.log` stops being opened at all once format is `jsonl` (Task 1 test asserts the stale file's lines are absent).
6. **Explicit `format` passed by the agent** → means "treat THIS as the configured format for this call" (picks which active file is live; dated reads always span both extensions) — NOT a narrowing filter that suppresses lines from the other extension. Pin in Task 2 tests + tool description.

---

### Task 1: `LogReader` — multi-segment merge reads

**Files:**
- Modify: `src/backend/Eling.Core/Logging/LogModels.cs` (`LogReadResult` gains `Files`)
- Modify: `src/backend/Eling.Core/Logging/LogReader.cs` (`Read` reimplemented as multi-segment)
- Create: `tests/Eling.Core.Tests/LogReaderMergeTests.cs`

**Interfaces:**
- Consumes: `LogReadResult` (extended), `ReadFilteredTail(string path, string? level, string? contains, int tail)` (unchanged, `LogReader.cs:275`), `LogFormat`, `ResolvePath`, `Matches`, `IsNoise`, `Sanitize`, `MaxResponseBytes`.
- Produces (later tasks rely on these names):
  - `public sealed record LogReadResult(string File, string Directory, bool Exists, int TotalLines, int FilteredTotal, int Returned, IReadOnlyList<string> Lines, IReadOnlyList<string>? Files = null);`
  - `LogReader.Read(string logsDirectory, string stream, string? date, string? level, string? contains, int tail, LogFormat format)` — same signature as today; now span-all behavior.
  - `LogReader.Read` text-overload (5-arg + format default `LogFormat.Text`) unchanged.
  - Response contract for callers: `Exists == false` ⇒ `Files == null`, `File` = the current-format preferred path (as today).

- [ ] **Step 1: Write the failing tests** in `tests/Eling.Core.Tests/LogReaderMergeTests.cs` (xUnit, same fixtures style as `LogReaderJsonlTests.cs` — read that file first and copy its temp-dir setup). Use temp dirs; write files with exact lines; assert on `LogReadResult` members.

  ```csharp
  // Dated read with both archives present: merged, timestamp-ordered.
  [Fact] DatedRead_BothExtensions_MergesAndOrdersByTimestamp()
  // Dated read filtered by contains: one match in .log, another in .jsonl — both returned.
  [Fact] DatedRead_Contains_MatchesAcrossBothFormats()
  // Dated read where preferred (jsonl) file missing: .log alone returned; Files == [that file].
  [Fact] DatedRead_OnlyOneExtension_ReturnsIt()
  // Dated read where neither exists: Exists == false, Files == null.
  [Fact] DatedRead_NeitherExtension_NotFound()
  // Active read, format Jsonl: today's backend-<today>.log archive (morning lines) + backend.jsonl
  // (afternoon lines) are BOTH returned in timestamp order, and a stale backend.log line dated
  // yesterday is NOT returned (it is not today's archive and not the active file).
  [Fact] ActiveRead_Jsonl_MergesTodaysArchiveAndActiveFile()
  // Active read, format Text: mirror image with backend-<today>.jsonl + backend.log.
  [Fact] ActiveRead_Text_MergesTodaysArchiveAndActiveFile()
  // Interleaved timestamps across a shared .log archive: sorting is per LINE, not per file.
  [Fact] DatedRead_DoubleSwitchDay_InterleavedTimestampsOrderCorrectly()
  // One line is not parseable timestamp: it stays in its segment's order, merge does not throw.
  [Fact] DatedRead_UnparseableTimestampLine_KeepsSegmentOrder()
  // tail=3 with 5 matching in .log + 5 matching in .jsonl: returns the newest 3 by timestamp.
  [Fact] DatedRead_TailAppliesAcrossTheMerge()
  // totalLines / filteredTotal are summed across segments.
  [Fact] DatedRead_TotalsAreSummedAcrossSegments()
  ```

- [ ] **Step 2: Run to verify they fail**
  `dotnet test tests/Eling.Core.Tests --filter "FullyQualifiedName~LogReaderMergeTests"` — expect compile errors (`Files` missing) then assertion failures.

- [ ] **Step 3: Implement**

  In `LogModels.cs`: add `IReadOnlyList<string>? Files = null` as the last positional parameter of `LogReadResult`.

  In `LogReader.cs`, replace the body of `Read(..., LogFormat format)` (currently lines ~115–149):

  ```csharp
  tail = Math.Clamp(tail, 1, 5000);
  var segments = Segments(logsDirectory, stream, date, format); // existing files, [] if none
  if (segments.Count == 0)
      return new LogReadResult(Path.GetFileName(ResolvePath(logsDirectory, stream, date, format)),
          logsDirectory, false, 0, 0, 0, Array.Empty<string>());
  // ReadFilteredTail per segment: sums Total/Filtered, keeps ≤tail lines each.
  var parts = segments.Select(p => (path: p, read: ReadFilteredTail(p, level, contains, tail))).ToList();
  var merged = parts
      .SelectMany((p, segIdx) => p.read.Lines.Select((line, idx) => (line, key: SortKey(line, parts, segIdx, idx))))
      .OrderBy(t => t.key, ...) // stable: then by segment index, then original index
      .Select(t => t.line)
      .ToList();
  var selected = merged.Count <= tail ? merged : merged.Skip(merged.Count - tail).ToList();
  return new LogReadResult(Path.GetFileName(segments[0]), logsDirectory, true, // first segment per spec item 5
      parts.Sum(p => p.read.Total), parts.Sum(p => p.read.Filtered), selected.Count,
      selected, segments.Select(Path.GetFileName).ToList());
  ```

  `Segments(dir, stream, date, format)`:
  - `date != null`: `[{s}-{date}.log, {s}-{date}.jsonl]` filtered by `File.Exists`.
  - `date == null`: today = `DateTime.Now.ToString("yyyy-MM-dd")`; `[{s}-{today}.log, {s}-{today}.jsonl, ResolvePath(dir, s, null, format)]` filtered by existence, deduped by full path; active file LAST (it is the newest segment).

  `SortKey(line, parts, segIdx, lineIdx)` (private static):
  - JSONL: regex `"Timestamp":"([^"]+)"` → `DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var ts)` → ts.
  - Text: `DateTimeOffset.TryParse(line[..Math.Min(30, line.Length)], CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var ts)` → ts (the default output template's prefix is `yyyy-MM-dd HH:mm:ss.fff zzz`, exactly 30 chars with a `+HH:mm` offset).
  - Failure: the previous parsed key in the same segment (carry-forward); if none in that segment, `DateTimeOffset.MinValue`. Tie-break in the OrderBy with `.ThenBy(segment index).ThenBy(line index)` — i.e. return a small record `(DateTimeOffset key, int segment, int index)` and OrderBy key, ThenBy segment, ThenBy index.
  - `OrderBy` with `ThenBy` gives the stable merge the spec requires; unparseable-prefix new segments sort as `MinValue`, keeping text-before-jsonl write order.

  Delete the dated alternate-extension fallback block (subsumed by the merge). Keep `ReadFilteredTail`, `ReadNewLines`, `ReadTailChunk`, `Matches`, `ListFiles` untouched. The `Read(dir, stream, date, level, contains, tail)` overload still forwards with `LogFormat.Text`.

- [ ] **Step 4: Run tests to verify they pass**
  `dotnet test tests/Eling.Core.Tests --filter "FullyQualifiedName~LogReaderMergeTests|FullyQualifiedName~LogReaderJsonlTests|FullyQualifiedName~LogReaderTests"` — expect PASS both new and pre-existing (jsonl/text readers unchanged except merge).

- [ ] **Step 5: Commit** (ask user first)
  `git add src/backend/Eling.Core/Logging/LogModels.cs src/backend/Eling.Core/Logging/LogReader.cs tests/Eling.Core.Tests/LogReaderMergeTests.cs && git commit -m "feat(logs): merge .log and .jsonl segments in log reads"`

---

### Task 2: MCP `LogTools` — `getFormat` seam + `format` parameter

**Files:**
- Modify: `src/backend/Eling.Backend/Mcp/Tools/LogTools.cs`
- Modify: `src/backend/Eling.Backend/Bootstrap/McpHostBuilder.cs` (register `LogConfigStore` + `Func<LogFormat>`)
- Modify: `src/backend/Eling.Backend/Agent/Services/Tools/AgentToolServiceExtensions.cs` (register `Func<LogFormat>` with Text fallback)
- Modify: `src/backend/Eling.Backend/Agent/Services/Tools/LogReadAgentTool.cs` (schema gains `format`; description updated)
- Modify: `tests/Eling.Backend.Tests/Mcp/LogToolsTests.cs`

**Interfaces:**
- Consumes (Task 1): `LogReadResult.Files`; `LogReader.Read(..., LogFormat format)` span-all behavior.
- Produces: response JSON gains `"format"` and `"files"`; `ReadLog` param `string? format = null`; `LogTools` ctor `(string? logsDirectory = null, ILogger<LogTools>? logger = null, Func<LogFormat>? getFormat = null)`.

- [ ] **Step 1: Write the failing tests** (append to `LogToolsTests.cs`; read the existing file first, same temp-dir pattern):

  ```csharp
  // getFormat: () => LogFormat.Jsonl + temp dir holding only backend.jsonl -> lines returned, no not_found.
  [Fact] ReadLog_JsonlConfig_ReadsJsonlActiveFile()
  // Response JSON carries "format":"jsonl" and "files":["backend.jsonl"].
  [Fact] ReadLog_ResponseReportsFormatAndFiles()
  // Explicit format: "jsonl" with getFormat: () => LogFormat.Text and BOTH active files present ->
  // the response's files list contains backend.jsonl as the active segment (override selects live file).
  [Fact] ReadLog_ExplicitFormat_OverridesConfig()
  // format: "yaml" -> ok:false, code:"invalid_argument".
  [Fact] ReadLog_InvalidFormat_ReturnsInvalidArgument()
  // format: "auto" explicitly -> same as omitted.
  [Fact] ReadLog_AutoFormat_EqualsDefault()
  // No getFormat passed, temp dir with only backend.log -> old behavior (existing test suite stays green).
  [Fact] ReadLog_NoGetFormat_DefaultsToText()
  // format: "text" on a dated read still spans both extensions (merge is not suppressible by the override):
  // write backend-2026-11-01.log AND backend-2026-11-01.jsonl, assert lines from both appear.
  [Fact] ReadLog_DatedReadSpansBothExtensions_EvenWithExplicitFormat()
  ```

- [ ] **Step 2: Run to verify they fail**
  `dotnet test tests/Eling.Backend.Tests --filter "FullyQualifiedName~LogToolsTests"` — expect FAIL (ctor/param absent).

- [ ] **Step 3: Implement**

  In `LogTools.cs`:
  - ctor: add `Func<LogFormat>? getFormat = null` (store in field `_getFormat`).
  - `ReadLog` signature: add `[Description("Optional format override: 'text' or 'jsonl'. Empty or 'auto' uses the configured format (log.json). Dated reads always span both extensions; this selects which active file is live.")] string? format = null` at the end.
  - Resolve: `var resolved = (format ?? "auto").Trim();` → if null/empty/`auto` → `resolved = null`; if `text`/`jsonl` (OrdinalIgnoreCase) → `LogFormat`; else return `invalid_argument` JSON (`ErrorJson(new ArgumentException($"Unknown format '{format}'. Use 'text', 'jsonl', or 'auto'."))` produces code `invalid_argument` already — reuse it).
  - `effective = explicit ?? _getFormat?.Invoke() ?? LogFormat.Text`.
  - Pass `effective` to `LogReader.Read(...)`.
  - Response (both the 8 MiB path and the normal path) gains `format = effective.ToString().ToLowerInvariant()` and `files = result.Files`.
  - Update `ReadLog`'s `[Description(...)]` summary: "Reads the format configured in log.json unless format is given. Lines are raw (JSONL lines are full JSON object strings). Dated reads merge both formats."
  - `ListLogs`: description only — append "Files of both formats (.log and .jsonl) are listed."
  - `using Eling.Core.Logging;` already present (`LogFormat` there). `Func<LogFormat>` needs no new usings.

  In `McpHostBuilder.cs` (after `services.AddSingleton(shared.UserScope);`):
  ```csharp
  // The log_read MCP tool follows the user's stored log format; the Func seam
  // keeps LogTools constructible without a store in tests.
  services.TryAddSingleton(_ => new Eling.Backend.Logging.LogConfigStore(
      shared.UserScope,
      NullLogger<Eling.Backend.Logging.LogConfigStore>.Instance));
  services.TryAddSingleton<Func<Eling.Core.Logging.LogFormat>>(sp =>
      () => sp.GetRequiredService<Eling.Backend.Logging.LogConfigStore>().GetFormat());
  ```
  (`TryAddSingleton` is already imported; `Func<...>` needs no using beyond System.)

  In `AgentToolServiceExtensions.cs` (after the `LogTools` registration):
  ```csharp
  // log_read follows <user-scope>/config/log.json when a store is registered
  // (dashboard host); bare agent-tool test containers get Text.
  services.TryAddSingleton<Func<Eling.Core.Logging.LogFormat>>(sp =>
  {
      var store = sp.GetService<Eling.Backend.Logging.LogConfigStore>();
      return store is null ? () => Eling.Core.Logging.LogFormat.Text
                           : () => store.GetFormat();
  });
  ```
  (Dashboard host: `DashboardServices.Register` runs before `AddElingAgentTools`, and registers the store — verify order; if the store registration moves later, the `GetService` inside the factory still resolves lazily at first `LogTools` resolution, which happens after all registration. Leave TryAddSingleton as-is.)

  In `LogReadAgentTool.cs`:
  - Description → "Read diagnostic log entries from a stream ('backend' or 'desktop'). Reads the format configured in log.json unless 'format' is given; dated reads span both .log and .jsonl. JSONL lines are raw JSON object strings."
  - Schema: add `"format": { "type": "string", "description": "Optional format override: 'text' or 'jsonl'. Empty/'auto' = configured format." }`.
  - Parse `root.TryGetProperty("format", ...)` and pass as the last argument to `tool.ReadLog(stream, date, tail, level, filter, format)`.

- [ ] **Step 4: Run to verify they pass**
  `dotnet test tests/Eling.Backend.Tests --filter "FullyQualifiedName~LogToolsTests|FullyQualifiedName~AgentBuiltinMcpToolTests"` — PASS, and the DI test still expects 21 tools.

- [ ] **Step 5: Commit** (ask user first)
  `git add src/backend/Eling.Backend/Mcp/Tools/LogTools.cs src/backend/Eling.Backend/Bootstrap/McpHostBuilder.cs src/backend/Eling.Backend/Agent/Services/Tools/AgentToolServiceExtensions.cs src/backend/Eling.Backend/Agent/Services/Tools/LogReadAgentTool.cs tests/Eling.Backend.Tests/Mcp/LogToolsTests.cs && git commit -m "feat(logs): log_read follows configured format and spans .log/.jsonl"`

---

### Task 3: Verify the whole change

**Files:** none new.

- [ ] **Step 1: Full targeted test suites**
  `dotnet test tests/Eling.Core.Tests` and `dotnet test tests/Eling.Backend.Tests --filter "FullyQualifiedName~LogTools|FullyQualifiedName~LogReader|FullyQualifiedName~LogsEndpoint|FullyQualifiedName~Dashboard"` — all PASS. (Full backend suite: known flaky tests per project memory — `DashboardLifecycleTests*`, `PeerMemoryNotifyTests*`, and the three DB-connection tests; re-run any that fail in isolation before calling it red.)
- [ ] **Step 2: Behavior check against the real log directory**
  `dotnet test --filter ...` is not needed here; instead call the built tool path directly in a scratch console? No — verify via the existing unit tests plus, if the user runs a rebuilt backend, `tools.eling.log_read` returns lines up to today. Note in the handoff: running MCP servers keep the old binary until rebuild/publish; the dashboard `/api/logs/read` picks up the merge automatically once the backend rebuilds (it calls the same `LogReader.Read`).
- [ ] **Step 3: Spec/plan consistency pass** — re-read `docs/superpowers/specs/2026-11-11-log-tools-jsonl-design.md`, fix any drift against what was built (e.g. if the `SortKey` carry-forward detail differs). Spec item 4 (§Cross-format reads) was amended for the window-budget wording: the per-file byte budget applies to the window of kept lines only (oldest matched lines dropped until the kept tail fits `MaxResponseBytes`), never by stopping the scan; the merged selection is re-capped to `MaxResponseBytes` (oldest dropped); totals are counted over the whole file. Make sure the built behavior and the spec both describe it that way.

---

## Calls I made, for review

1. `format` override = "treat this as the configured format" (active-file selection + response echo); it never suppresses lines from the other extension. One read path, no hidden mode switch.
2. Merge order: timestamp sort per line with carry-forward of the last parsed key for unparseable lines, `MinValue` for segments with no prior key, stable tie-break by segment then line index.
3. `backend.log` (stale) is never gathered: undated reads take today's archives + the current-format active file only.
4. `LogReadResult.Files` appended as optional positional parameter; `File` keeps meaning "first/preferred segment" for back-compat.
5. Dashboard `/api/logs/read` changes behavior for free (same `LogReader.Read`) — reads now span both extensions; `LogsEndpoints` tests should still pass (verify in Task 3).
