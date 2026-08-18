# MCP Stdio Server Implementation Plan (Pecut 6)

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Turn `Eling.Mcp` into a working MCP (Model Context Protocol) server over stdio exposing the memory operations of `Eling.Application` as MCP tools.

**Architecture:** `Eling.Mcp` becomes an executable console app (`OutputType=Exe`) that wires `IMemoryService` into DI and registers MCP tools via the `ModelContextProtocol` SDK (`AddMcpServer().WithStdioServerTransport().WithToolsFromAssembly()`). A `MemoryTools` class (marked `[McpServerToolType]`) exposes one `[McpServerTool]` per use case. Business logic stays in `Eling.Application`; `Eling.Mcp` only marshals tool calls.

**Tech Stack:** .NET 10, C# 14, `ModelContextProtocol` NuGet package (v2.x), xUnit.

## Global Constraints

- Work ONLY in `src/backend/Eling.Mcp/`, `tests/Eling.Mcp.Tests/`, and the solution file `Eling.slnx`.
- `Eling.Mcp` references `Eling.Application` (which transitively brings Core/Storage/Index) — NOT Core/Storage/Index directly, NOT Graph.
- Do NOT modify `Eling.Core`, `Eling.Storage`, `Eling.Index`, `Eling.Graph`, `Eling.Application`, `Eling.Server`, or `src/frontend/`.
- No MCP SDK version guessing: before coding, read `docs/superpowers/specs/*.md` and the `dotnet-mcp-builder` skill reference for the installed package's exact attribute/extension API. If the API differs from what this plan assumes, follow the installed SDK's shape and note the deviation in the plan file.
- Do NOT introduce HTTP, REST, or SQL into `Eling.Mcp`.
- Keep `Eling.Server` referencing `Eling.Mcp` as-is — the Server project file is NOT part of this pecut.

---

### Task 1: Make `Eling.Mcp` an executable stdio MCP server

**Files:**
- Modify: `src/backend/Eling.Mcp/Eling.Mcp.csproj`
- Create: `src/backend/Eling.Mcp/Program.cs`
- Delete: `src/backend/Eling.Mcp/Class1.cs`

- [x] **Step 1: Update the csproj.** Set `<OutputType>Exe</OutputType>`, remove the direct `Eling.Core` ProjectReference, add `<ProjectReference Include="..\Eling.Application\Eling.Application.csproj" />`, and add `<PackageReference Include="ModelContextProtocol" Version="..." />` (run `dotnet add package ModelContextProtocol` to get the latest v2 stable and let it write the version).
- [x] **Step 2: Delete `Class1.cs`** (placeholder).
- [x] **Step 3: Write `Program.cs`.** Host the MCP server over stdio, resolving storage/index from the same defaults used elsewhere (root dir `".eling"`, index file `index.db` under it):

```csharp
using Eling.Application;
using Eling.Index;
using Eling.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ModelContextProtocol.Server;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddSingleton<IMemoryStorage>(new FileSystemMemoryStorage(".eling"));
builder.Services.AddSingleton<IMemoryIndex>(new SqliteMemoryIndex(Path.Combine(".eling", "index.db")));
builder.Services.AddSingleton<IMemoryService, MemoryService>();

builder.Services
    .AddMcpServer()
    .WithStdioServerTransport()
    .WithToolsFromAssembly();

await builder.Build().RunAsync();
```

- [x] **Step 4: Build** `dotnet build Eling.slnx` and confirm `Eling.Mcp` compiles as an exe.

---

### Task 2: Implement `MemoryTools` MCP tools

**Files:**
- Create: `src/backend/Eling.Mcp/MemoryTools.cs`

- [x] **Step 1: Implement one tool per use case.** Use the `[McpServerToolType]` + `[McpServerTool(Name, Description)]` attribute shape; constructor-inject `IMemoryService`. Tool names and semantics:

| Tool | Inputs | Delegates to |
|---|---|---|
| `memory_save` | `content` (required string), `type` (string, default `"fact"`), `tags` (string[] optional), `source` (string optional) | `IMemoryService.SaveAsync` — return the saved `Memory` |
| `memory_get` | `id` (string, ULID) | `IMemoryService.GetAsync` — return the `Memory` or an error message when not found |
| `memory_delete` | `id` (string, ULID) | `IMemoryService.DeleteAsync` — return `bool` |
| `memory_list` | `status` (string optional, default `"active"`) | `IMemoryService.ListAsync` — return `IReadOnlyCollection<Memory>` |
| `memory_search` | `query` (required string), `limit` (int optional, default 10) | `IMemoryService.SearchAsync` — return search results |
| `memory_rebuild_index` | *(none)* | `IMemoryService.RebuildIndexAsync` — return an empty result |

- [x] **Step 2: Convert input strings to domain types inside the tools.** `Enum.TryParse<MemoryType>` for `type`/`status` (case-insensitive); `MemoryId.Parse` for ids; default `MemoryType.Fact` when `type` is empty. Throw `ArgumentException` with a clear message on invalid values so MCP surfaces them as tool errors.
- [x] **Step 3: Verify serialization of results.** Confirm `Memory`, `MemoryType`, and search-result DTOs serialize to plain JSON via the MCP SDK (public getters only; no cycles). If `Memory` exposes members the MCP SDK can't serialize (e.g. `IReadOnlyCollection<string>` is fine), follow the installed SDK's guidance rather than adding Json.NET attributes to the domain model.
- [x] **Step 4: Build** `dotnet build Eling.slnx`.

---

### Task 3: Tests for `Eling.Mcp`

**Files:**
- Create: `tests/Eling.Mcp.Tests/Eling.Mcp.Tests.csproj`
- Create: `tests/Eling.Mcp.Tests/MemoryToolsTests.cs`
- Modify: `Eling.slnx` (add test project to `/tests/` folder)

- [x] **Step 1: Scaffold the test project.** xUnit (same package versions as `tests/Eling.Server.Tests/Eling.Server.Tests.csproj`), referencing `Eling.Mcp`. Add `ModelContextProtocol` if needed to construct the tools.
- [x] **Step 2: Unit-test the tool methods directly** with a hand-rolled fake `IMemoryService` (no mocking library — match repo convention). Cover: `memory_save` happy path + invalid type + empty content; `memory_get` found + not found; `memory_delete` true/false; `memory_list` filters by status; `memory_search` passes query/limit through; `memory_rebuild_index` returns.
- [x] **Step 3: Verify tool registration** — one test that starts the server host with an in-memory/stdio transport per the `mcp-csharp-test` skill and lists tools, asserting all six names exist. If the installed SDK makes this prohibitively complex, note it and rely on Step 2 plus `dotnet build`.
- [x] **Step 4: Run tests** `dotnet test Eling.slnx`.

---

### Task 4: Manual smoke check (optional)

- [x] **Step 1:** Run `dotnet run --project src/backend/Eling.Mcp` and pipe a JSON-RPC `initialize` + `tools/list` request via stdin to confirm the server responds with the six tools. Document the exact command used in the plan file's completion notes.

---

## Completion Notes

- `Eling.Mcp` project configured as an executable console app exposing stdio Model Context Protocol (MCP) server.
- Tools `memory_save`, `memory_get`, `memory_delete`, `memory_list`, `memory_search`, and `memory_rebuild_index` registered and validated via `MemoryTools.cs`.
- Hand-rolled fake `IMemoryService` unit tests created in `tests/Eling.Mcp.Tests/MemoryToolsTests.cs`, with 28 passing unit tests.
- All tests across the entire solution pass (`dotnet test Eling.slnx`).
