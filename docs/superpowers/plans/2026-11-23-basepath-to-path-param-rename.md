# Rename `basePath` to `path` Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Rename the wire parameter `basePath` to `path` on `glob` and `file_search` across both the MCP tool surface and the internal agent-tool surface, so those calls render a subtitle and both surfaces use one name.

**Architecture:** The MCP SDK derives the JSON schema property name from the C# parameter name, so changing two parameters in `FileSystemTools.cs` changes the wire contract. The internal agent tools hand-write their schemas, so their `required` arrays, `properties` objects, and `ExecuteAsync` reads are changed separately. The service layer keeps its internal `basePath` names; nothing about validation, sandboxing, or results changes.

**Tech Stack:** .NET 10 / C# 14, ModelContextProtocol SDK, xUnit.

**Spec:** `docs/superpowers/specs/2026-11-23-basepath-to-path-param-rename-design.md`

## Global Constraints

- All artifacts in this repo are written in English.
- Change only the parameter name. No behavior, validation, sandbox, or result-shape changes.
- No backward-compatible `basePath` alias; the rename is clean.
- Do not rename service-layer C# parameters (`IFileSystemService`, `FileSystemService`, `FakeFileSystemService`) or any other tool's path-like parameter.
- Do not commit without explicit user approval, even when a step below says Commit.
- Build/test with isolated output; never run `pnpm build` while the frontend dev server is running.
- Run only LITE / changed-file tests, not the full suite.

## Review Focus

- **Caller still sending `basePath=`** → fails clearly with `invalid_argument`, never silently searches an empty root. Pin in Task 2.
- **Description text still saying "base path"** → would keep teaching agents the old name. Pin in Task 1 and Task 2.
- **Positional C# callers (`tool.Glob(".", ...)`)** → keep working after the rename. Pin by re-running `FileSystemToolsTests` in Task 1.
- **Sandbox behavior for `..` and external roots** → unchanged. Pin by re-running `FileSystemToolsTests` in Task 1.
- **Tool registration** → still 21 agent tools with the same names. Pin by re-running `AgentBuiltinMcpToolTests` in Task 2.

## File Structure

Modified:
- `src/backend/Eling.Backend/Mcp/Tools/FileSystemTools.cs` — the two MCP method signatures, call sites and descriptions.
- `src/backend/Eling.Backend/Agent/Services/Tools/GlobAgentTool.cs`, `FileSearchAgentTool.cs` — hand-written JSON schemas, argument parsers and descriptions.
- `tests/Eling.Backend.Tests/FileSystem/FileSystemToolsTests.cs`, `tests/Eling.Backend.Tests/AgentBuiltinMcpToolTests.cs` — wire-name and schema tests.
- `CHANGELOG.md` — one `Unreleased` → `Changed` entry.

Created: none. Deleted: none.

---

### Task 1: MCP tool wire name

**Files:**
- Modify: `src/backend/Eling.Backend/Mcp/Tools/FileSystemTools.cs` (`Glob` signature near line 64, `SearchFiles` signature near line 197)
- Test: `tests/Eling.Backend.Tests/FileSystem/FileSystemToolsTests.cs`

**Interfaces:**
- Consumes: nothing from earlier tasks.
- Produces: `FileSystemTools.Glob(string path, string pattern, ...)` and `FileSystemTools.SearchFiles(string path, string pattern, ...)`; the first C# parameter name is the wire property name.

- [ ] **Step 1: Write the failing wire-name test**

Add to `FileSystemToolsTests`:

```csharp
[Theory]
[InlineData(nameof(FileSystemTools.Glob))]
[InlineData(nameof(FileSystemTools.SearchFiles))]
public void FileSearchTools_ExposePathParameter_NotBasePath(string methodName)
{
    var parameters = typeof(FileSystemTools).GetMethod(methodName)!.GetParameters();

    Assert.Equal("path", parameters[0].Name);
    Assert.DoesNotContain(parameters, p => p.Name == "basePath");
}
```

Also assert that each method's `[Description]` text does not contain the phrase `base path`.

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test tests/Eling.Backend.Tests/Eling.Backend.Tests.csproj --filter "FullyQualifiedName~FileSystemToolsTests.FileSearchTools_ExposePathParameter_NotBasePath" --artifacts-path .bin-test-basepath-mcp`

Expected: FAIL — first parameter is named `basePath`.

- [ ] **Step 3: Rename the two MCP parameters**

In `FileSystemTools.cs`: rename the first parameter of `Glob` and `SearchFiles` from `basePath` to `path`; update the two `_fs.Glob(...)` / `_fs.SearchFiles(...)` call sites; change tool- and parameter-level descriptions that say "base path" to say "path". Do not touch any other tool or the service layer.

- [ ] **Step 4: Run the test to verify it passes**

Run: the same command as Step 2.
Expected: PASS.

- [ ] **Step 5: Run the whole changed test class**

Run: `dotnet test tests/Eling.Backend.Tests/Eling.Backend.Tests.csproj --filter "FullyQualifiedName~FileSystemToolsTests" --artifacts-path .bin-test-basepath-mcp`

Expected: PASS, including the existing glob, file_search, sandbox, and external-root cases — positional callers are unaffected by the rename.

- [ ] **Step 6: Commit (only after explicit user approval)**

Use `commit_lease` (paths: the two files below), stage only those files via an
isolated `GIT_INDEX_FILE`, commit, then `commit_release`.
Message: `refactor(mcp): rename basePath to path on glob and file_search`.

---

### Task 2: Internal agent tools, schema sync, and CHANGELOG

**Files:**
- Modify: `src/backend/Eling.Backend/Agent/Services/Tools/GlobAgentTool.cs`
- Modify: `src/backend/Eling.Backend/Agent/Services/Tools/FileSearchAgentTool.cs`
- Test: `tests/Eling.Backend.Tests/AgentBuiltinMcpToolTests.cs`
- Modify: `CHANGELOG.md`

**Interfaces:**
- Consumes: Task 1's `FileSystemTools.Glob(string path, ...)` and `SearchFiles(string path, ...)`.
- Produces: agent tools `glob` and `file_search` whose JSON schema uses `path` and whose `ExecuteAsync` reads `path`.

- [ ] **Step 1: Write the failing schema test**

Add to `AgentBuiltinMcpToolTests`:

```csharp
[Theory]
[InlineData("glob")]
[InlineData("file_search")]
public void FileTools_SchemaUsesPath_NotBasePath(string toolName)
{
    using var scope = _provider.CreateScope();
    var tool = scope.ServiceProvider.GetServices<IAgentTool>().First(t => t.Name == toolName);

    using var schema = JsonDocument.Parse(tool.ParametersJsonSchema);
    var properties = schema.RootElement.GetProperty("properties");

    Assert.True(properties.TryGetProperty("path", out _));
    Assert.False(properties.TryGetProperty("basePath", out _));
    Assert.Contains("path", schema.RootElement.GetProperty("required").EnumerateArray().Select(e => e.GetString()));
    Assert.DoesNotContain("base path", tool.Description, StringComparison.OrdinalIgnoreCase);
}
```

Add one more assertion for each tool: calling `ExecuteAsync` with `{"basePath": ".", "pattern": "*"}` returns `ok == false` and `code == "invalid_argument"` (the new parser reads an absent `path` as empty, and `ResolvePath` rejects an empty path), while `{"path": ".", "pattern": "*"}` returns `ok != false`.

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test tests/Eling.Backend.Tests/Eling.Backend.Tests.csproj --filter "FullyQualifiedName~AgentBuiltinMcpToolTests.FileTools_SchemaUsesPath_NotBasePath" --artifacts-path .bin-test-basepath-agent`

Expected: FAIL — schema still has `basePath`.

- [ ] **Step 3: Update both agent tools**

In `GlobAgentTool.cs` and `FileSearchAgentTool.cs`: replace `basePath` with `path` in `ParametersJsonSchema` (required array and properties object), in the `ExecuteAsync` property reads, and in the `Description` text. Keep all other properties and defaults unchanged.

- [ ] **Step 4: Run the test to verify it passes**

Run: the same command as Step 2.
Expected: PASS.

- [ ] **Step 5: Run the whole changed test class**

Run: `dotnet test tests/Eling.Backend.Tests/Eling.Backend.Tests.csproj --filter "FullyQualifiedName~AgentBuiltinMcpToolTests" --artifacts-path .bin-test-basepath-agent`

Expected: PASS, including `Tools_AreRegisteredInDi` (still 21 tools, same names) and the file read/write flows.

- [ ] **Step 6: Add the CHANGELOG entry**

Under `## [Unreleased] - 2026-11-23` → `### Changed`, add one bullet: `glob` and `file_search` now expose `path` instead of `basePath` on both MCP and internal agent tool surfaces; parameter name only, no behavior change.

- [ ] **Step 7: Verify no stale wire references remain**

Run a repository search for `"basePath"` limited to `Mcp/Tools` and `Agent/Services/Tools`; expect zero hits. Hits in the service layer, tests' fake service, and the historical design spec are expected and out of scope.

- [ ] **Step 8: Commit (only after explicit user approval)**

Use `commit_lease` (paths: the four files below), stage only those files via an
isolated `GIT_INDEX_FILE`, commit, then `commit_release`.
Message: `refactor(agent): sync glob and file_search schemas to path`.

## Self-Review Notes

- Spec coverage: intent, four tool surfaces, description text, and CHANGELOG each map to a task; non-goals are excluded from both tasks.
- Review focus: each of the five listed risks has either a new assertion or an existing test re-run in the owning task.
- Type consistency: both tasks use the same `path` parameter name and the same tool names (`glob`, `file_search`).
- Proportion: the plan is shorter than the code it describes; no method bodies were written except test assertions.

## Location note (2026-11-24)

Created under `docs/superpowers/plans/` on 2026-11-23. A short detour on
2026-11-24 moved it into `.eling/docs/plans/` under the then-current
managed-docs policy; the approved Unified Docs revamp retired that distinction,
and the one-time migration returned it to `docs/superpowers/plans/`. The
`doc_save` foreign-key defect that motivated the detour is designed out by that
revamp and is not part of this change.

This plan is independent of the docs subsystem: it only renames MCP/agent tool
parameters so OpenChamber renders a tool-call subtitle.
