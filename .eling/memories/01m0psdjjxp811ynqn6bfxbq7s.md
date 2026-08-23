---
id: 01m0psdjjxp811ynqn6bfxbq7s
type: fact
status: active
tags:
- eling
- dashboard
- mcp
- aspnet
- infrastructure
created_at: 2026-08-23T07:47:32.0969213+00:00
updated_at: 2026-08-23T07:47:32.0969213+00:00
source: Eling session Aug 2026
---
For Eling project - dashboard & runtime infra decisions (Aug 2026):
1. MCP stdio purity: opencode.json runs `dotnet exec .artifacts/bin/Eling.Host/debug/eling.dll` (NOT dotnet run — MSBuild output pollutes stdout JSON-RPC; rebuild includes pnpm build = slow start). Must rebuild before MCP picks up changes.
2. wwwroot served from folder NEXT TO the binary: csproj BuildDashboard copies to $(OutputPath)wwwroot; Program.cs sets WebApplicationOptions.WebRootPath = AppContext.BaseDirectory/wwwroot (builder.WebHost.UseWebRoot() throws NotSupportedException).
3. Static file pipeline order matters: WebApplication inserts implicit UseRouting at pipeline START, so MapFallbackToFile captured deep links like /dashboard/memories/ before static middleware → wrong page hydrated ("redirect to dashboard" symptom). Fix: explicit app.UseDefaultFiles(); app.UseStaticFiles(); app.UseRouting(); AFTER static files.
4. GET /api/memories sorts by CreatedAt DESC before limit/offset paging (was alphabetical by filename → oldest 100 first, new memories invisible).
5. Dashboard auto-starts from stdio MCP mode via EnsureDashboardAsync (fire-and-forget): coordinator alive → register only; else spawn detached `dotnet exec eling.dll --http-mcp`, wait /health max 5s, register, heartbeat every 15s. Coordinator self-heartbeats; shuts down when all EXTERNAL runtimes stale.
6. Coordinator register POST must target http://localhost:4317 (RequireHost("localhost:4317") rejects 127.0.0.1).
7. UserScope global dir = ~/.config/eling on ALL platforms.
8. WebApplicationFactory tests: set env ELING_NO_DASHBOARD=1 to force web host path; stdio MCP must NEVER be registered in web host (stdin EOF kills app).
9. MemoryApiTests pollute real .eling/memories storage (ProjectScope = repo root) — cleanup needed after test runs; root cause not yet fixed.