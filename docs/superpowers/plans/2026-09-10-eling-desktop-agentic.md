# Eling Desktop Agentic Transformation Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Transform Eling Desktop into a backend-owned agentic app (Chat, Folders, Memories, Settings) with all operations executed by new `/api/agent/*` backend endpoints.

**Architecture:** Desktop stays pure frontend (HTTP only). Backend gains one route group (`Endpoints/AgentEndpoints.cs` plus sibling endpoint files) over OUR ports (`IChatGateway`, `IProviderClient`, `IAgentTool`); the AI library lives only in `Agent/Infrastructure/Ai/` behind those ports.

**Tech Stack:** .NET 10, C# (nullable enable), ASP.NET Core minimal APIs, xUnit 2.9.0, Avalonia 12.1.2 + ReactiveUI (desktop), `Microsoft.Extensions.AI` + `Microsoft.Extensions.AI.OpenAI` (adapter only), `System.Text.Json` (camelCase on the wire).

## Global Constraints

- Tests run per csproj only: `dotnet test tests/Eling.Backend.Tests/Eling.Backend.Tests.csproj --artifacts-path .bin-test`. Never solution-wide, never chained builds.
- NEVER commit, push, amend, or reset. Leave work in the working tree; end every task by reporting `git status --short`.
- Run shell commands as separate steps (no `&&` chains).
- Every `catch` block must log via `ILogger` (backend) or the injected logger (desktop). No silent catches.
- No machine-specific absolute paths or usernames in tracked files. Tests use temp dirs (`Path.GetTempPath()` + GUID) like `MemoryApiTests` does.
- Wire JSON stays camelCase (backend `ConfigureHttpJsonOptions` already does this); desktop deserializes case-insensitively (already does).
- No `Microsoft.Extensions.AI` / `OpenAI.*` usings outside `src/backend/Eling.Backend/Agent/Infrastructure/Ai/`.
- New `HttpClient` usage in backend goes through `IHttpClientFactory` (`services.AddHttpClient<...>()`, available in the Web SDK shared framework, no extra package).

---

## File Structure

New backend files (all under `src/backend/Eling.Backend/`):

- `Agent/Ports/AgentMessages.cs` — wire-neutral turn DTOs: `AgentRole`, `AgentMessage`, `ToolDefinition`, `ToolCallRequest`, `SingleShotRequest`, `SingleShotResult`.
- `Agent/Ports/IChatGateway.cs` — single-shot completion port.
- `Agent/Props/IProviderClient.cs` — actually `Agent/Ports/IProviderClient.cs`: `ListModelsAsync` + `TestConnectionAsync`.
- `Agent/Ports/IAgentTool.cs` — one tool behind an interface.
- `Dtos/AgentDtos.cs` — HTTP DTOs: `ProviderView`, `UpdateProviderRequest`, `ModelListResponse`, `ProviderTestResponse`, `WorkspaceListResponse`, `AddWorkspaceRequest`, `FileListEntry`, `FileListResponse`, `FileReadResponse`, `WriteFileRequest`, `ChatSummary`, `ChatMessageDto`, `SendMessageRequest`, `TurnResponse`, `ToolCallDto`.
- `Agent/Services/ProviderStore.cs` — server-side provider config JSON in `EffectiveDataDir`.
- `Agent/Services/WorkspaceRegistry.cs` — roots JSON + containment.
- `Agent/Services/BackendFileTools.cs` — list/read/write with caps.
- `Agent/Services/BackendChatStore.cs` — history JSON per workspace.
- `Agent/Services/AgentTurnService.cs` — owns the tool loop (max 3 hops).
- `Agent/Services/AgentTools.cs` — `ReadFileTool`, `ListDirTool`, `RecallMemoryTool`, `SaveMemoryTool`.
- `Agent/Infrastructure/Ai/MeaiChatGateway.cs` — the only MEAI-backed class.
- `Agent/Infrastructure/Ai/HttpProviderClient.cs` — tiny `HttpClient` for `/models` + ping.
- `Endpoints/AgentEndpoints.cs` — `MapAgentEndpoints()` + provider routes.
- `Endpoints/AgentWorkspaceEndpoints.cs`, `Endpoints/AgentChatEndpoints.cs` — remaining routes.

Modified backend files:

- `Bootstrap/DashboardServices.cs` — DI for the above (one block, see Task 2).
- `Bootstrap/DashboardRoutes.cs` — add `app.MapAgentEndpoints();` after `MapScopedMemoryRoutes()`.

New desktop files (under `src/desktop/Eling.Desktop/`):

- `Services/DesktopSettingsStore.cs` — `%AppData%/eling-desktop/settings.json` holding `backendUrl` only.
- `ViewModels/MemoriesViewModel.cs` — extracted memory logic (Task 7).
- `ViewModels/ChatViewModel.cs`, `ViewModels/FoldersViewModel.cs`, `ViewModels/SettingsViewModel.cs`.
- `Views/ChatView.axaml`, `Views/FoldersView.axaml`, `Views/SettingsView.axaml`, `Views/MemoriesView.axaml` (+ `.axaml.cs` code-behinds).
- `tests/Eling.Desktop.Tests/` — new test project (Task 8).

Modified desktop files: `Services/ElingApiClient.cs` (agent methods), `Services/BackendSupervisor.cs` (`ManualBaseUrl`), `Program.cs` (DI), `ViewModels/MainViewModel.cs` (shell), `Views/MainWindow.axaml` (sidebar + content).

---

### Task 1: Agent ports + HTTP DTOs + contract tests

**Files:**
- Create: `src/backend/Eling.Backend/Agent/Ports/AgentMessages.cs`
- Create: `src/backend/Eling.Backend/Agent/Ports/IChatGateway.cs`
- Create: `src/backend/Eling.Backend/Agent/Ports/IProviderClient.cs`
- Create: `src/backend/Eling.Backend/Agent/Ports/IAgentTool.cs`
- Create: `src/backend/Eling.Backend/Dtos/AgentDtos.cs`
- Test: `tests/Eling.Backend.Tests/AgentContractTests.cs`

**Interfaces:**
- Consumes: nothing (foundation).
- Produces (exact names later tasks use):
  - `Eling.Backend.Agent.Ports.AgentRole` enum (`System`, `User`, `Assistant`, `Tool`)
  - `record AgentMessage(AgentRole Role, string Text, string? ToolCallId = null, string? ToolName = null)`
  - `record ToolDefinition(string Name, string Description, string ParametersJsonSchema)`
  - `record ToolCallRequest(string Id, string Name, string ArgumentsJson)`
  - `record SingleShotRequest(string Model, IReadOnlyList<AgentMessage> Messages, IReadOnlyList<ToolDefinition> Tools)`
  - `record SingleShotResult(string? AssistantText, IReadOnlyList<ToolCallRequest> ToolCalls)`
  - `interface IChatGateway { Task<SingleShotResult> CompleteAsync(SingleShotRequest request, CancellationToken ct); }`
  - `interface IProviderClient { Task<IReadOnlyList<string>> ListModelsAsync(string baseUrl, string? apiKey, CancellationToken ct); Task<ProviderProbe> TestConnectionAsync(string baseUrl, string? apiKey, string? model, CancellationToken ct); }`
  - `record ProviderProbe(bool Ok, string Message)`
  - `interface IAgentTool { string Name { get; } string Description { get; } string ParametersJsonSchema { get; } Task<string> ExecuteAsync(string argumentsJson, CancellationToken ct); }`
  - DTOs in `Eling.Backend.Dtos`: `record ProviderView(string? BaseUrl, string? Model, bool HasKey, List<string> ModelsCached)`, `record UpdateProviderRequest(string? BaseUrl, string? Model, string? ApiKey)`, `record ModelListResponse(List<string> Models)`, `record ProviderTestResponse(bool Ok, string Message)`, `record WorkspaceListResponse(List<string> Roots)`, `record AddWorkspaceRequest(string Path)`, `record FileListEntry(string Path, bool IsDirectory)`, `record FileListResponse(List<FileListEntry> Entries)`, `record FileReadResponse(string Content, bool Truncated)`, `record WriteFileRequest(string Root, string Path, string Content)`, `record ChatSummary(string Id, string Workspace, string Title, DateTimeOffset UpdatedAt)`, `record ChatMessageDto(string Role, string Text, string? ToolName)`, `record SendMessageRequest(string Workspace, string Message, string? ChatId)`, `record ToolCallDto(string Name, string Arguments)`, `record TurnResponse(string ChatId, string Assistant, List<ToolCallDto> ToolCalls)`

- [ ] **Step 1: Write the failing test.** Create `tests/Eling.Backend.Tests/AgentContractTests.cs`:

```csharp
using System.Text.Json;
using System.Text.Json.Serialization;
using Eling.Backend.Dtos;

namespace Eling.Backend.Tests;

public class AgentContractTests
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() }
    };

    [Fact]
    public void ProviderView_SerializesCamelCase_AndNeverCarriesKey()
    {
        var json = JsonSerializer.Serialize(new ProviderView("http://127.0.0.1:11434/v1", "qwen3", true, ["qwen3"]), JsonOptions);
        Assert.Contains("\"baseUrl\"", json);
        Assert.Contains("\"hasKey\"", json);
        Assert.DoesNotContain("apiKey", json);
    }

    [Fact]
    public void TurnResponse_RoundTrips()
    {
        var original = new TurnResponse("c1", "hello", [new ToolCallDto("read_file", "{}")]);
        var back = JsonSerializer.Deserialize<TurnResponse>(JsonSerializer.Serialize(original, JsonOptions), JsonOptions);
        Assert.NotNull(back);
        Assert.Equal("c1", back.ChatId);
        Assert.Single(back.ToolCalls);
    }
}
```

- [ ] **Step 2: Run test to verify it fails.**

Run: `dotnet test tests/Eling.Backend.Tests/Eling.Backend.Tests.csproj --filter FullyQualifiedName~AgentContractTests --artifacts-path .bin-test`
Expected: FAIL (build errors, types do not exist yet).

- [ ] **Step 3: Write minimal implementation.** Create the five files with exactly the records/interfaces listed in Produces above. `AgentMessages.cs` holds the six turn records; each interface in its own file; all DTOs in `Dtos/AgentDtos.cs` under `namespace Eling.Backend.Dtos;`.

- [ ] **Step 4: Run test to verify it passes.**

Run: `dotnet test tests/Eling.Backend.Tests/Eling.Backend.Tests.csproj --filter FullyQualifiedName~AgentContractTests --artifacts-path .bin-test`
Expected: PASS (2/2).

- [ ] **Step 5: Report.** Run `git status --short`, report the new files. Do NOT commit.

### Task 2: ProviderStore + provider GET/PUT + route group + wiring

**Files:**
- Create: `src/backend/Eling.Backend/Agent/Services/ProviderStore.cs`
- Create: `src/backend/Eling.Backend/Endpoints/AgentEndpoints.cs`
- Modify: `src/backend/Eling.Backend/Bootstrap/DashboardServices.cs` (DI block)
- Modify: `src/backend/Eling.Backend/Bootstrap/DashboardRoutes.cs` (one Map call)
- Test: `tests/Eling.Backend.Tests/AgentProviderTests.cs`

**Interfaces:**
- Consumes: `ProviderView`, `UpdateProviderRequest` (Task 1).
- Produces:
  - `Eling.Backend.Agent.Services.ProviderStore` with `ProviderView GetView()`, `void Update(string? baseUrl, string? model, string? apiKey)`, `bool TryGetConfig(out string baseUrl, out string? model, out string? apiKey)`
  - `GET /api/agent/provider`, `PUT /api/agent/provider`

`ProviderStore` stores `{ baseUrl, model, apiKey }` as JSON in `Path.Combine(dataDir, "agent-provider.json")`; empty `apiKey` on update keeps the stored one; `GetView()` returns `HasKey` without the key. Constructor: `public ProviderStore(string dataDir, ILogger<ProviderStore> logger)`. Every catch logs.

`AgentEndpoints.cs` (Task 2 scope = provider routes only; later tasks append groups via new files + one-line Map additions):

```csharp
using Eling.Backend.Agent.Services;
using Eling.Backend.Dtos;

namespace Eling.Backend.Endpoints;

public static class AgentEndpoints
{
    public static WebApplication MapAgentEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/agent/provider");
        group.MapGet("/", (ProviderStore store) => TypedResults.Ok(store.GetView()));
        group.MapPut("/", (ProviderStore store, UpdateProviderRequest req) =>
        {
            store.Update(req.BaseUrl, req.Model, req.ApiKey);
            return TypedResults.Ok(store.GetView());
        });
        return app;
    }
}
```

DI addition in `DashboardServices.Register`, right after the `IMemoryService` line:

```csharp
services.AddSingleton(sp => new ProviderStore(
    context.EffectiveDataDir,
    sp.GetRequiredService<ILogger<ProviderStore>>()));
```

`DashboardRoutes.Map`: insert `app.MapAgentEndpoints();` after `app.MapScopedMemoryRoutes();`.

- [ ] **Step 1: Write the failing test.** Create `AgentProviderTests.cs` following the `MemoryApiTests` fixture (temp dir, `TestAppBuilder.CreateSelfContained(port)`, random loopback port). Two facts: `PutThenGet_RoundTripsWithoutLeakingKey` (PUT `{ baseUrl: "http://127.0.0.1:11434/v1", model: "qwen3", apiKey: "secret-1" }`, GET returns `hasKey: true`, body contains no `secret-1`) and `EmptyApiKey_KeepsStoredKey` (second PUT without apiKey, GET still `hasKey: true`).

- [ ] **Step 2: Run test to verify it fails.**

Run: `dotnet test tests/Eling.Backend.Tests/Eling.Backend.Tests.csproj --filter FullyQualifiedName~AgentProviderTests --artifacts-path .bin-test`
Expected: FAIL (404, no routes/types yet).

- [ ] **Step 3: Write minimal implementation** (files above).

- [ ] **Step 4: Run tests to verify.**

Run filtered command above. Expected: PASS. Then run the full project suite: `dotnet test tests/Eling.Backend.Tests/Eling.Backend.Tests.csproj --artifacts-path .bin-test`. Expected: PASS, no regressions.

- [ ] **Step 5: Report.** Run `git status --short`, report. Do NOT commit.

### Task 3: HttpProviderClient + models/test endpoints

**Files:**
- Create: `src/backend/Eling.Backend/Agent/Infrastructure/Ai/HttpProviderClient.cs`
- Modify: `src/backend/Eling.Backend/Endpoints/AgentEndpoints.cs` (add models + test routes)
- Modify: `src/backend/Eling.Backend/Bootstrap/DashboardServices.cs` (add `services.AddHttpClient<IProviderClient, HttpProviderClient>();`)
- Test: extend `tests/Eling.Backend.Tests/AgentProviderTests.cs` (stub-provider tests)

**Interfaces:**
- Consumes: `IProviderClient`, `ProviderProbe` (Task 1), `ProviderStore` (Task 2).
- Produces: `POST /api/agent/provider/models` → `ModelListResponse`; `POST /api/agent/provider/test` → `ProviderTestResponse`.

`HttpProviderClient(HttpClient http, ILogger<HttpProviderClient> logger)`:
- `ListModelsAsync`: `GET {base}/models`, parse `data[].id` via `JsonDocument` (tolerant: missing `data` → empty list + warning log, NOT an exception). Non-success status → throw `HttpRequestException` with status + 200-char body excerpt; every catch logs.
- `TestConnectionAsync`: `POST {base}/chat/completions` with `{ model, messages: [{role:"user",content:"ping"}], max_tokens: 1 }`, `Authorization: Bearer` only when key non-empty, 15s linked cancellation. Returns `ProviderProbe(true/false, message)` — never throws to the endpoint; endpoint maps probe to 200 always (`ProviderTestResponse`).

Endpoints to append in `MapAgentEndpoints`:

```csharp
group.MapPost("/models", async (ProviderStore store, IProviderClient client, CancellationToken ct) =>
{
    if (!store.TryGetConfig(out var baseUrl, out _, out var apiKey))
        return Results.BadRequest("Provider is not configured.");
    try
    {
        var models = await client.ListModelsAsync(baseUrl, apiKey, ct);
        return Results.Ok(new ModelListResponse([.. models]));
    }
    catch (Exception ex)
    {
        return Results.Problem(statusCode: 502, title: "Provider models fetch failed", detail: ex.Message);
    }
});
group.MapPost("/test", async (ProviderStore store, IProviderClient client, UpdateProviderRequest? req, CancellationToken ct) => ...);
```

Note: minimal-API handlers with `Results.*` (not `TypedResults`) to allow the 502 branch; keep the union simple. The `/test` handler prefers the request body baseUrl/model if supplied (so Test works pre-Save), else stored config; 400 when neither exists.

- [ ] **Step 1: Write the failing tests.** Add to `AgentProviderTests`: spin an `HttpListener` stub on a loopback random port serving canned `{"data":[{"id":"m1"},{"id":"m2"}]}` for `/models` and canned `{"choices":[{"message":{"content":"pong"}}]}` for `/chat/completions`; PUT provider config pointing at the stub; assert `/models` returns both ids and `/test` returns `ok: true`. `HttpListener` with `Prefixes.Add($"http://127.0.0.1:{port}/")`, serve on a background task with the test's `CancellationTokenSource`, stop in `finally`.

- [ ] **Step 2: Run to verify they fail.**

Run filtered `AgentProviderTests`. Expected: FAIL (404 routes).

- [ ] **Step 3: Implement** (adapter + routes + DI line).

- [ ] **Step 4: Run filtered, then full project suite.** Expected: PASS.

- [ ] **Step 5: Report** via `git status --short`. Do NOT commit.

### Task 4: WorkspaceRegistry + BackendFileTools + workspace/file endpoints

**Files:**
- Create: `src/backend/Eling.Backend/Agent/Services/WorkspaceRegistry.cs`
- Create: `src/backend/Eling.Backend/Agent/Services/BackendFileTools.cs`
- Create: `src/backend/Eling.Backend/Endpoints/AgentWorkspaceEndpoints.cs`
- Modify: `src/backend/Eling.Backend/Endpoints/AgentEndpoints.cs` (call `app.MapAgentWorkspaceEndpoints();`)
- Modify: `src/backend/Eling.Backend/Bootstrap/DashboardServices.cs` (singletons)
- Test: `tests/Eling.Backend.Tests/AgentWorkspaceTests.cs`

**Interfaces:**
- Consumes: workspace/file DTOs (Task 1).
- Produces:
  - `WorkspaceRegistry(string filePath, ILogger<WorkspaceRegistry>)`: `IReadOnlyList<string> List()`, `void Add(string path)` (400-case: directory must exist → throw `ArgumentException` with message; endpoint maps to 400), `void Remove(string path)`, `string Resolve(string root, string relativePath)` (throws `UnauthorizedAccessException` on escape)
  - `BackendFileTools(ILogger<BackendFileTools>)`: `IReadOnlyList<FileListEntry> ListDir(string dirFullPath)` (dirs first, cap 200), `(string Content, bool Truncated) ReadText(string fullPath)` (NUL-byte sniff → `InvalidOperationException("binary")`; 64 KB cap), `void WriteText(string fullPath, string content)` (128 KB cap, temp + `File.Move(overwrite: true)`)
  - Routes: `GET /api/agent/workspaces`, `POST /api/agent/workspaces`, `DELETE /api/agent/workspaces?path=...`, `GET /api/agent/files/list?root=...&path=...`, `GET /api/agent/files/read?root=...&path=...`, `POST /api/agent/files/write`

`Resolve` implementation (exact rule): `var full = Path.GetFullPath(Path.Combine(root, rel ?? ""));` then `if (!full.Equals(rootFull, OrdinalIgnoreCase) && !full.StartsWith(rootFull + Path.DirectorySeparatorChar, OrdinalIgnoreCase)) throw new UnauthorizedAccessException("Path escapes workspace root.");` where `rootFull = Path.GetFullPath(root)`. Symlink escape is best-effort (documented limit, matches spec).

Endpoints map `UnauthorizedAccessException` → 403, `ArgumentException`/`FileNotFoundException` → 400/404, `InvalidOperationException("binary")` → 415. Each handler catch logs via the injected `ILogger` (add `ILoggerFactory` param or per-endpoint `ILogger<...>` — use `ILoggerFactory` + `CreateLogger("Agent.Workspaces")` to keep handlers static).

DI: `AddSingleton WorkspaceRegistry` with path `Path.Combine(context.EffectiveDataDir, "agent-workspaces.json")`; `AddSingleton BackendFileTools`.

- [ ] **Step 1: Write failing tests** (`AgentWorkspaceTests`, same fixture): attach temp root + list shows it; `../..` read → 403; binary file (bytes with 0x00) read → 415; 100 KB write then read-back equals; oversize write (200 KB) → 400/413; detach removes from list but directory still exists on disk.

- [ ] **Step 2: Run filtered.** Expected: FAIL (404s).

- [ ] **Step 3: Implement.**

- [ ] **Step 4: Filtered run, then full suite.** Expected: PASS.

- [ ] **Step 5: Report** via `git status --short`. Do NOT commit.

### Task 5: MeaiChatGateway adapter (+ packages)

**Files:**
- Create: `src/backend/Eling.Backend/Agent/Infrastructure/Ai/MeaiChatGateway.cs`
- Modify: `src/backend/Eling.Backend/Eling.Backend.csproj` (two PackageReferences)
- Modify: `src/backend/Eling.Backend/Bootstrap/DashboardServices.cs` (`services.AddScoped<IChatGateway, MeaiChatGateway>();`)
- Test: `tests/Eling.Backend.Tests/MeaiGatewayTests.cs` (stub-provider via `HttpListener`, no real network)

**Interfaces:**
- Consumes: `SingleShotRequest/Result`, `ToolDefinition`, `ToolCallRequest`, `ProviderStore` (all prior tasks).
- Produces: scoped `IChatGateway` that performs ONE provider call and returns text and/or tool requests. It never executes tools and never loops.

Packages (run as two separate commands): `dotnet add src/backend/Eling.Backend/Eling.Backend.csproj package Microsoft.Extensions.AI` then `dotnet add src/backend/Eling.Backend/Eling.Backend.csproj package Microsoft.Extensions.AI.OpenAI` (no pinned version = latest stable at implementation time; record the resolved versions in the report).

Adapter design (exact): `MeaiChatGateway(ProviderStore store, ILogger<MeaiChatGateway> logger)`:
- `CompleteAsync`: `TryGetConfig` else throw `InvalidOperationException("Provider is not configured.")`. Build `new OpenAI.Chat.ChatClient(model, new ApiKeyCredential(string.IsNullOrEmpty(key) ? "no-key" : key), new OpenAIClientOptions { Endpoint = new Uri(baseUrl.TrimEnd('/') + "/") })`, `.AsIChatClient()` (from `Microsoft.Extensions.AI.OpenAI`), map our messages to `ChatMessage` list, map our `ToolDefinition`s to the lib's function-tool type per the installed package docs, call `GetResponseAsync` (non-streaming), map back: assistant text + tool requests (`Id`, `Name`, `ArgumentsJson`). No middleware, no auto-invocation.
- Tool-definition mapping rule: use the lib's documented function-tool construction from the installed version's XML docs. If the installed version cannot express an explicit JSON schema per tool, the adapter formats the provider `tools` payload manually with `System.Text.Json` over the same endpoint and still parses via MEAI message types — either way the port signature is unchanged and everything stays inside this one file.

Tests (`MeaiGatewayTests`): `HttpListener` stub returning canned OpenAI wire JSON. Case A (tool call): `{"choices":[{"message":{"role":"assistant","content":null,"tool_calls":[{"id":"c1","type":"function","function":{"name":"read_file","arguments":"{\\"path\\":\\"a.txt\\"}"}}]}}]}` → assert result has one `ToolCallRequest` named `read_file` and the test also captures the request body and asserts it contains `tools` and `tool_choice`. Case B (plain text): `{"choices":[{"message":{"role":"assistant","content":"hi"}}]}` → assert `AssistantText == "hi"`, no tool calls. Both construct `MeaiChatGateway` against a `ProviderStore` pointed at a temp dir pre-seeded via `Update(stubUrl, "stub-model", null)` — no HTTP app needed, pure adapter test.

- [ ] **Step 1: Write failing tests** (they fail to build: no adapter, no packages).

Run: `dotnet test tests/Eling.Backend.Tests/Eling.Backend.Tests.csproj --filter FullyQualifiedName~MeaiGatewayTests --artifacts-path .bin-test`. Expected: FAIL (build error).

- [ ] **Step 2: Add the two packages** (two separate `dotnet add` commands), then implement the adapter + DI line.

- [ ] **Step 3: Run filtered.** Expected: PASS. Then full suite PASS.

- [ ] **Step 4: Report** (`git status --short` + resolved package versions). Do NOT commit.

### Task 6: AgentTools + AgentTurnService + BackendChatStore + chat endpoints

**Files:**
- Create: `src/backend/Eling.Backend/Agent/Services/AgentTools.cs`
- Create: `src/backend/Eling.Backend/Agent/Services/AgentTurnService.cs`
- Create: `src/backend/Eling.Backend/Agent/Services/BackendChatStore.cs`
- Create: `src/backend/Eling.Backend/Endpoints/AgentChatEndpoints.cs`
- Modify: `src/backend/Eling.Backend/Endpoints/AgentEndpoints.cs` (call `app.MapAgentChatEndpoints();`)
- Modify: `src/backend/Eling.Backend/Bootstrap/DashboardServices.cs` (scoped tools + turn service + store singleton)
- Test: `tests/Eling.Backend.Tests/AgentChatTests.cs`

**Interfaces:**
- Consumes: `IChatGateway` (Task 5), `IAgentTool` (Task 1), `IMemoryService` (existing scoped), `BackendFileTools` + `WorkspaceRegistry` (Task 4).
- Produces: `GET /api/agent/chats?workspace=...`, `GET /api/agent/chats/{id}`, `POST /api/agent/chats`, `POST /api/agent/chats/{id}/messages`.

Tool argument schemas (exact JSON Schema strings in code):
- `read_file`: `{"type":"object","required":["path"],"properties":{"path":{"type":"string"},"root":{"type":"string"}}}` — `root` defaults to the turn workspace. Execute: `registry.Resolve` + `tools.ReadText`, return content (prefix `[truncated] ` when truncated) or `error: ...` string on failure (tool errors are DATA, never exceptions past the router, except cancellation).
- `list_dir`: same shape; returns newline `D name` / `F name` list or `error: ...`.
- `recall_memory`: `{"type":"object","required":["query"],"properties":{"query":{"type":"string"},"limit":{"type":"integer"}}}` — via `IMemoryService.SearchAsync`, top N (default 5, max 10), formatted `id | type | excerpt(200)` lines.
- `save_memory`: `{"type":"object","required":["content"],"properties":{"content":{"type":"string"},"type":{"type":"string"},"tags":{"type":"array","items":{"type":"string"}}}}` — `type` parsed to `MemoryType` (fallback `Note`), constructs `new Memory(type, content, tags, source: "agent-chat")`, `SaveAsync`, returns `saved: <id>`.

`AgentTurnService` (scoped; exact loop):

```csharp
public async Task<TurnResponse> SendAsync(string workspace, string? chatId, string message, CancellationToken ct)
{
    // 1. load history (last 40) + append user message
    // 2. repeat max 3 times: build SingleShotRequest(system prompt with roots+tool names, history),
    //    res = await gateway.CompleteAsync(...); append assistant/tool rows to history;
    //    if no tool calls -> break with res.AssistantText;
    //    else execute each via matching IAgentTool by name (unknown -> "error: tool not available"),
    //    append results as Tool-role messages
    // 3. persist via BackendChatStore, return TurnResponse
}
```

System prompt text (exact v1): `"You are Eling, a coding assistant. Workspace roots: {roots}. You have tools: list_dir, read_file, recall_memory, save_memory. Prefer reading files and recalling memories before answering about the project. If a tool reports an error, state it honestly."`

`BackendChatStore(string dir, ILogger)`: `agent-chats/{slug}.json` per chat + per-workspace index; slug = SHA256 hex (first 16) of workspace root. Methods: `GetOrCreate(workspace, chatId?)`, `Append(...)`, `Get(id)`, `List(workspace)`.

DI: tools + `AgentTurnService` as Scoped (they touch scoped `IMemoryService`); store as Singleton.

- [ ] **Step 1: Write failing tests** (`AgentChatTests`): fake `IChatGateway` (hand-written stub class in the test file returning a scripted tool call then text) wired by constructing `AgentTurnService` directly (no HTTP): turn with `read_file` script returns file content in final answer and history persists across two sends; hop-cap test (stub always requests a tool → turn stops after 3 executions, returns text noting the limit). HTTP-level tests (POST `/api/agent/chats` with workspace attached, then GET history) run against the real app (uses the MEAI adapter only if a provider is configured — so for HTTP tests, point provider config at the `HttpListener` stub from Task 5 serving the plain-text canned reply).

- [ ] **Step 2: Run filtered.** Expected: FAIL (no types).

- [ ] **Step 3: Implement.**

- [ ] **Step 4: Filtered run, then full suite.** Expected: PASS.

- [ ] **Step 5: Report** via `git status --short`. Do NOT commit.

### Task 7: Desktop shell + navigation + Memories extraction (no new features)

**Files:**
- Create: `src/desktop/Eling.Desktop/ViewModels/MemoriesViewModel.cs` (moved memory logic, same namespace `Eling.Desktop.ViewModels`)
- Modify: `src/desktop/Eling.Desktop/ViewModels/MainViewModel.cs` (shell only: menu selection, status/SSE, supervisor init, child VMs)
- Modify: `src/desktop/Eling.Desktop/Views/MainWindow.axaml` (sidebar + `ContentControl`)
- Create: `src/desktop/Eling.Desktop/Views/MemoriesView.axaml` (+ `.axaml.cs`) holding the current memory XAML verbatim
- Modify: `src/desktop/Eling.Desktop/Program.cs` (register `MemoriesViewModel`; stub `ChatViewModel`, `FoldersViewModel`, `SettingsViewModel` registered in Tasks 8–10 — Task 7 registers only what exists)

**Interfaces:**
- Consumes: existing memory behavior (must not change).
- Produces: `MainViewModel.SelectedMenu` (`"Chat"|"Folders"|"Memories"|"Settings"`, default `"Memories"` until Chat/Folders land — keeps the app fully usable mid-migration), child `Memories` property.

Mechanical move rule: every member of `MainViewModel` touching `Memories`/`FilteredMemories`/CRUD dialogs moves unchanged into `MemoriesViewModel` (ctor takes `BackendSupervisor`, `ElingApiClient`, loggers — same as today). `MainViewModel.InitializeAsync` keeps supervisor + SSE; memory-changed SSE callback forwards to `Memories.LoadMemoriesAsync`. No behavior change, no new strings.

- [ ] **Step 1: Build baseline.** Run `dotnet build src/desktop/Eling.Desktop/Eling.Desktop.csproj -p:ElingSkipDashboard=true` — must PASS before touching anything (record output).

- [ ] **Step 2: Perform the move** (files above). Sidebar XAML: left `ListBox` bound to a `Menus` array with `SelectedItem="{Binding SelectedMenu}"`, right `ContentControl` with `DataTemplates` mapping each child VM to its View (only Memories template wired in this task).

- [ ] **Step 3: Rebuild.** Same build command. Expected: PASS with zero warnings introduced (compare warning count to baseline).

- [ ] **Step 4: Manual check.** Run the desktop, confirm memory list/filters/CRUD/promote/SSE behave exactly as before. Record result in the report.

- [ ] **Step 5: Report** via `git status --short`. Do NOT commit.

### Task 8: Desktop ApiClient agent methods + Settings view + backend-URL override (+ desktop test project)

**Files:**
- Create: `tests/Eling.Desktop.Tests/Eling.Desktop.Tests.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <IsPackable>false</IsPackable>
    <IsTestProject>true</IsTestProject>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.14.1" />
    <PackageReference Include="xunit" Version="2.9.0" />
    <PackageReference Include="xunit.runner.visualstudio" Version="2.8.2" />
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="..\..\src\desktop\Eling.Desktop\Eling.Desktop.csproj" />
  </ItemGroup>
  <ItemGroup>
    <Using Include="Xunit" />
  </ItemGroup>
</Project>
```

- Create: `tests/Eling.Desktop.Tests/AgentApiTests.cs`
- Modify: `src/desktop/Eling.Desktop/Services/ElingApiClient.cs` (agent methods + internal test ctor)
- Modify: `src/desktop/Eling.Desktop/Eling.Desktop.csproj` (`<InternalsVisibleTo Include="Eling.Desktop.Tests" />`)
- Create: `src/desktop/Eling.Desktop/Services/DesktopSettingsStore.cs`
- Modify: `src/desktop/Eling.Desktop/Services/BackendSupervisor.cs` (`ManualBaseUrl`)
- Create: `src/desktop/Eling.Desktop/ViewModels/SettingsViewModel.cs`, `Views/SettingsView.axaml` (+ code-behind)
- Modify: `src/desktop/Eling.Desktop/Program.cs`, `ViewModels/MainViewModel.cs` (wire Settings)

**Interfaces:**
- Consumes: backend provider routes (Tasks 2–3).
- Produces: `ElingApiClient.GetProviderAsync / UpdateProviderAsync / FetchModelsAsync / TestProviderAsync` (same `new HttpClient` per-call pattern as existing methods); `DesktopSettingsStore.GetBackendUrl()/SetBackendUrl()`.

Test seam (exact): add to `ElingApiClient`:

```csharp
internal ElingApiClient(Func<string> baseUrlProvider, ILogger<ElingApiClient> logger) { ... }
```

backed by storing `Func<string> _baseUrl` (primary ctor passes `() => supervisor.BaseUrl ?? "http://127.0.0.1:4417"`). Tests use `new ElingApiClient(() => "http://127.0.0.1:1", NullLogger...)` with a stub `HttpMessageHandler`? `HttpClient` is created inside `GetClient()` — to intercept, change `GetClient` to `protected virtual`? Simpler seam honest to the existing shape: internal `Func<HttpClient>? HttpClientFactory` property defaulting to null (null = current behavior); tests set it to return a client over the stub handler. Three lines, no behavior change.

Supervisor change (exact): add `public string? ManualBaseUrl { get; set; }`; at the top of `EnsureBackendRunningAsync`, `if (!string.IsNullOrWhiteSpace(ManualBaseUrl) && await IsHealthyAsync(new Uri(ManualBaseUrl).Port...` — precise: parse `new Uri(ManualBaseUrl)` (must be absolute http), probe `GET {ManualBaseUrl}/health` via a small local check reusing `HttpClient`, set `ActivePort` from URI port, return `ManualBaseUrl`. On failure throw with the attempted URL in the message (user-visible). `MainViewModel.InitializeAsync` sets `supervisor.ManualBaseUrl = new DesktopSettingsStore().GetBackendUrl()` before ensuring.

Settings view fields: backend URL (textbox + note "empty = auto"), provider base URL, API key (`PasswordBox` → bound via code-behind event, never logged), Fetch Models → fills ComboBox (editable for manual entry), Test → status text, Save → PUT (+ backend URL to local store). All failures show inline text; every catch logs.

- [ ] **Step 1: Write failing tests** (`AgentApiTests` with stub handler: GET provider returns canned JSON → `HasKey` true; PUT echoes; models list parses two ids). They fail to build (no methods/seam).

Run: `dotnet test tests/Eling.Desktop.Tests/Eling.Desktop.Tests.csproj --artifacts-path .bin-test`. Expected: FAIL (build error).

- [ ] **Step 2: Implement** (seam, methods, store, supervisor, VM, view, DI).

- [ ] **Step 3: Run desktop tests.** Expected: PASS. Rebuild desktop csproj PASS.

- [ ] **Step 4: Manual check** against local backend: save provider, fetch models (stub or real local server), Test ping shows result.

- [ ] **Step 5: Report** via `git status --short`. Do NOT commit.

### Task 9: Desktop Folders view

**Files:**
- Create: `src/desktop/Eling.Desktop/ViewModels/FoldersViewModel.cs`, `Views/FoldersView.axaml` (+ code-behind)
- Modify: `src/desktop/Eling.Desktop/Services/ElingApiClient.cs` (workspace/file methods)
- Modify: `src/desktop/Eling.Desktop/Program.cs`, `ViewModels/MainViewModel.cs` (wire)
- Test: extend `tests/Eling.Desktop.Tests/AgentApiTests.cs` (workspace/file method roundtrips over stub handler)

**Interfaces:**
- Consumes: workspace/file routes (Task 4), ApiClient seam (Task 8).
- Produces: `GetWorkspacesAsync / AddWorkspaceAsync / RemoveWorkspaceAsync / ListDirAsync / ReadFileAsync / WriteFileAsync`; `FoldersViewModel` with `Roots`, `SelectedRoot`, `Entries`, `PreviewText`, `EditText`, `IsEditing`.

Behavior: Add/Remove root (backend-machine paths; inline hint text says so), lazy one-level listing on selection/expand (v1: expand = select folder → list), preview on file select (truncated flag shown), Edit → textbox, Save → write + re-preview, binary/oversize errors shown inline from the backend payload. No local `System.IO` file access in the VM — HTTP only (assert in review: `grep System.IO` in the new VM must be empty).

- [ ] **Step 1: Write failing tests** (stub-handler roundtrips for the six methods + VM state transitions with a fake client? VM takes concrete `ElingApiClient` — construct via the internal ctor with stub factory, same as Task 8; assert selecting a root loads entries).

Run desktop tests. Expected: FAIL (no methods).

- [ ] **Step 2: Implement.**

- [ ] **Step 3: Desktop tests PASS + desktop build PASS.**

- [ ] **Step 4: Manual check** (attach 2 roots, browse, preview, edit+save a text file, detach keeps disk content).

- [ ] **Step 5: Report** via `git status --short`. Do NOT commit.

### Task 10: Desktop Chat view + end-to-end smoke + close-out

**Files:**
- Create: `src/desktop/Eling.Desktop/ViewModels/ChatViewModel.cs`, `Views/ChatView.axaml` (+ code-behind)
- Modify: `src/desktop/Eling.Desktop/Services/ElingApiClient.cs` (chat methods)
- Modify: `src/desktop/Eling.Desktop/Program.cs`, `ViewModels/MainViewModel.cs` (default menu → `"Chat"`)
- Modify: `docs/superpowers/specs/2026-09-10-eling-desktop-agentic-design.md` (rename `HttpModelCatalog` → `HttpProviderClient` with List+Test duties — one edit, keeps spec truthful)

**Interfaces:**
- Consumes: chat routes (Task 6), workspace list (Task 4/9).
- Produces: `GetChatsAsync / GetChatAsync / SendMessageAsync`; `ChatViewModel` with `ActiveWorkspace`, `Messages` (role/text/toolName rows), `Input`, `IsBusy`, `SendCommand` (cancellable via `CancellationTokenSource`, cancel on window close path through `Dispose`).

Behavior: workspace picker (from `Runtimes`? No — from agent `roots`; keep separate to avoid scope confusion), message list with three row styles (user right, assistant left, tool rows muted mono), Send → busy → append; errors inline with attempted base URL; tool rows show `tool: name args…`. No streaming in v1.

- [ ] **Step 1: Write failing tests** (stub handler: send returns canned turn with one tool call → VM appends user + tool + assistant rows in order; busy flag resets; cancel path leaves VM usable).

Run desktop tests. Expected: FAIL (no methods/VM).

- [ ] **Step 2: Implement.**

- [ ] **Step 3: Desktop tests PASS + desktop build PASS + backend full suite PASS.**

- [ ] **Step 4: End-to-end smoke (manual, record each):** local backend: add root, preview/edit file, configure provider (stub or real local OpenAI-compatible server), fetch models, chat turn that reads a file and recalls a memory, restart backend+desktop (history/roots/settings persist), Memories menu unchanged; remote-mode: set backend-URL override to a LAN backend, repeat read-only steps.

- [ ] **Step 5: Final report** (`git status --short`, package versions, smoke results, spec edit). Do NOT commit. Ask the user for the commit checkpoint.

## Self-Review

- **Spec coverage:** ports/DTOs (T1) → provider store+routes (T2) → models/test (T3) → workspaces/files (T4) → MEAI adapter (T5) → tools+turn+chat (T6) → shell+Memories (T7) → desktop Settings+override (T8) → Folders (T9) → Chat+smoke (T10). Remote-PC story covered by T8 override + T10 smoke. Auth token stays an explicit non-goal with the hardening follow-up noted in the spec.
- **Placeholder scan:** every step names exact files, exact member names, exact commands, exact expected outcomes. Error branches return concrete status codes/messages. No TBD/TODO.
- **Type consistency:** `ProviderView/UpdateProviderRequest/ModelListResponse/ProviderTestResponse`, workspace/file DTOs, `ChatSummary/ChatMessageDto/SendMessageRequest/TurnResponse/ToolCallDto` defined once in Task 1 and reused verbatim. `IChatGateway`/`IProviderClient`/`IAgentTool` signatures fixed in Task 1. Note: the plan names the provider port `IProviderClient`/`HttpProviderClient` (List + Test); Task 10 syncs the spec wording.
