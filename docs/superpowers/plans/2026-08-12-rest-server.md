# REST Server Implementation Plan (Pecut 7)

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace the `Eling.Server` weather-forecast template with a minimal ASP.NET Core REST API exposing the memory use cases of `Eling.Application`. This is what the dashboard (Pecut 8) talks to.

**Architecture:** `Eling.Server` is an ASP.NET Core minimal-API app — **a different server from `Eling.Mcp` (Pecut 6)**. The two are separate processes by design:

| | `Eling.Mcp` (Pecut 6) | `Eling.Server` (this plan) |
|---|---|---|
| Transport | MCP over stdio | REST/HTTP |
| Consumers | AI agents (Claude, etc.) | Dashboard (Pecut 8), curl |
| Port | n/a (stdio) | `http://localhost:5275` |
| Dependencies | `Eling.Application` | `Eling.Application` (only) |

`Eling.Server` registers the same storage/index/service stack as `Eling.Mcp` (filesystem storage under `.eling`, SQLite index, `MemoryService`) — shared via `Eling.Application`, NOT via each other — and maps one endpoint group per use case. It holds NO business logic — it parses HTTP, calls `IMemoryService`, maps results to JSON. It does NOT host an MCP endpoint, does NOT reference `Eling.Mcp`, and does NOT share a process with it. Each process runs its own `.eling` root (both default to `.eling` in their working directory).

**Tech Stack:** .NET 10, ASP.NET Core minimal APIs, C# 14, xUnit, `Microsoft.AspNetCore.Mvc.Testing` (10.0.10 already in NuGet cache).

## Global Constraints

- Work ONLY in `src/backend/Eling.Server/`, `tests/Eling.Server.Tests/`, and the solution file `Eling.slnx`.
- `Eling.Server` references `Eling.Application` only — drop the direct `Eling.Core`, `Eling.Storage`, `Eling.Index`, `Eling.Graph`, and `Eling.Mcp` references (Application brings Core/Storage/Index transitively; Graph stays a placeholder).
- **Server ≠ MCP server.** `Eling.Server` must NOT reference `Eling.Mcp`, must NOT host MCP endpoints/transports, and must not share process state with it. The only shared surface between the two servers is `Eling.Application`.
- Do NOT modify `Eling.Core`, `Eling.Storage`, `Eling.Index`, `Eling.Application`, `Eling.Mcp`, `Eling.Graph`, or `src/frontend/`.
- No new NuGet packages for the app itself (OpenApi package already referenced). Test packages may be added to `Eling.Server.Tests`.
- Default JSON = camelCase (use `builder.Services.ConfigureHttpJsonOptions` with `JsonSerializerDefaults.Web`) so the TypeScript dashboard gets camelCase fields for free.

---

### Task 1: Rewire `Eling.Server` to the application layer

**Files:**
- Modify: `src/backend/Eling.Server/Eling.Server.csproj`
- Rewrite: `src/backend/Eling.Server/Program.cs`

- [ ] **Step 1: Clean the csproj.** Remove all ProjectReferences except `Eling.Application` (keep `Microsoft.AspNetCore.OpenApi`).
- [ ] **Step 2: Rewrite `Program.cs`** — remove the `WeatherForecast` record and route; register the stack with a configurable root path (so tests can point at a temp dir):

```csharp
var builder = WebApplication.CreateBuilder(args);

var rootPath = builder.Configuration["Eling:RootPath"] ?? ".eling";

builder.Services.AddSingleton<IMemoryStorage>(new FileSystemMemoryStorage(rootPath));
builder.Services.AddSingleton<IMemoryIndex>(new SqliteMemoryIndex(Path.Combine(rootPath, "index.db")));
builder.Services.AddSingleton<IMemoryService, MemoryService>();
builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase);

if (builder.Environment.IsDevelopment())
{
    builder.Services.AddCors(o => o.AddDefaultPolicy(p => p.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod()));
}
builder.Services.AddOpenApi();

var app = builder.Build();

app.UseCors();
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}
app.UseHttpsRedirection();

// ...endpoints (Task 2)...

app.Run();

public partial class Program;
```

- [ ] **Step 3: Build** `dotnet build Eling.slnx`.

---

### Task 2: Implement the memory endpoints

**Files:**
- Create: `src/backend/Eling.Server/MemoryEndpoints.cs` (or inline in `Program.cs` — follow whichever keeps the diff smallest; one file is fine)

- [ ] **Step 1: Map the endpoints.** One `MapGroup("/api/memories")` group; each handler is a thin adapter over `IMemoryService`:

| Method | Route | Request | Response |
|---|---|---|---|
| `GET` | `/api/memories` | query `status` (optional, default `active`) | `200` list of `Memory` |
| `GET` | `/api/memories/{id}` | path ULID | `200` `Memory` / `404` |
| `POST` | `/api/memories` | body `{ type?, content, tags?, source? }` | `201` saved `Memory` + `Location` header |
| `DELETE` | `/api/memories/{id}` | path ULID | `204` on success / `404` |
| `GET` | `/api/memories/search` | query `q`, `limit` (default 10) | `200` search results |
| `POST` | `/api/memories/rebuild-index` | — | `204` |

- [ ] **Step 2: Input validation in handlers.** `MemoryId.Parse` failures and invalid `type`/`status` strings → `Results.BadRequest` with a short message. Unknown id → `Results.NotFound`.
- [ ] **Step 3: DTO for create.** A small `SaveMemoryRequest` record with nullable `Type`/`Tags`/`Source` and required non-empty `Content`; empty content → `400`.
- [ ] **Step 4: Build** `dotnet build Eling.slnx` and smoke-test locally with `dotnet run` (POST a memory, GET the list, GET by id, DELETE).

---

### Task 3: Integration tests for `Eling.Server`

**Files:**
- Modify: `tests/Eling.Server.Tests/Eling.Server.Tests.csproj` (add `Microsoft.AspNetCore.Mvc.Testing` 10.0.10)
- Rewrite: `tests/Eling.Server.Tests/UnitTest1.cs` → delete; create `tests/Eling.Server.Tests/MemoryApiTests.cs`
- Modify: `Eling.slnx` only if the test project is missing from it

- [ ] **Step 1: Configure the test host.** Use `WebApplicationFactory<Program>` with `WithWebHostBuilder` → `UseSetting("Eling:RootPath", tempDir)` so tests never touch the repo's `.eling`. Create the temp dir in the fixture and delete it on dispose (or leave it under `Path.GetTempPath()` — tests are self-contained either way).
- [ ] **Step 2: Write the tests.** Plain xUnit + `HttpClient` (no extra assertion libs):
  - POST creates → response 201, body has `id`, then GET list contains it.
  - GET by id returns the memory; GET unknown id → 404.
  - POST with empty content → 400.
  - DELETE → 204, subsequent GET → 404.
  - search returns the created memory for its content text; rebuild-index → 204.
- [ ] **Step 3: Run tests** `dotnet test Eling.slnx` (full suite — nothing else may break).

---

## Completion Notes

- Report `git status --short` and summarize changed files. Do NOT commit.
- The dashboard (Pecut 8) depends on this API's routes and camelCase JSON — do not rename routes/fields after this plan without updating that plan.
