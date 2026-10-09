# Dynamic Tool Management & Enable/Disable Policy Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Implement dynamic MCP tool enablement/disablement in Eling, allowing users to toggle individual tools or groups via the Dashboard web UI and via Chat (`tools_policy`), applying changes in real time without restarting the MCP server.

**Architecture:** A thread-safe `ToolPolicyStore` persists policies to `<user-scope>/config/tools-policy.json` with atomic disk writes and trailing `updatedAt` timestamps generated in code. Middleware filters (`AddListToolsFilter` and `AddCallToolFilter`) enforce policies on MCP stdio in real time and emit `notifications/tools/list_changed`. A dedicated `ToolsPolicyTool` MCP tool provides natural-language chat control, while `/api/tools` endpoints power a new `/dashboard/tools` page in the Next.js frontend with shadcn/ui Switches.

**Tech Stack:** .NET 10, C# 14, `ModelContextProtocol` v2.1.0, ASP.NET Core Minimal APIs, Next.js 15 / React 19, Tailwind CSS, shadcn/ui components, xUnit.

**Spec:** `docs/superpowers/specs/2026-10-09-tool-management-policy-design.md`

## Global Constraints

- Anti-tuple rule: Never use `ValueTuple` anywhere; use explicit named records or classes.
- Atomic mutation timestamps: Always generate and set `updatedAt` in backend code (`DateTimeOffset.UtcNow`) at the end of domain mutations; never trust client-supplied timestamps.
- Atomic file writes: Configuration files must be written to `.tmp` first then moved/replaced to prevent partial writes.
- Immunity rule: Core tools `tools_policy` and `memory_recall` are protected and can never be disabled.
- Real-time enforcement: Policy changes must apply immediately to in-memory filter evaluations without restarting the backend process.

## Review Focus

1. Disabling non-existent tool name: System ignores or reports invalid tool gracefully without corrupting store.
2. Attempting to disable protected tools (`tools_policy`, `memory_recall`): Tool returns validation warning and does not disable them.
3. Rapid concurrent toggles: Thread-safe lock in `ToolPolicyStore` ensures zero race conditions or partial writes.
4. Calling an already-disabled tool: `CallToolFilter` short-circuits execution and returns standard `tool_disabled` error result.
5. Group toggles (`filesystem`, `codebase`, `memory`): Correctly maps and bulk-enables/disables all member tools.

---

### Task 1: Domain Models and `ToolPolicyStore`

**Files:**
- Create: `src/backend/Eling.Core/Tools/ToolPolicyConfig.cs`
- Create: `src/backend/Eling.Core/Tools/ToolDefinitionDto.cs`
- Create: `src/backend/Eling.Backend/Tools/ToolPolicyStore.cs`
- Test: `tests/Eling.Backend.Tests/Tools/ToolPolicyStoreTests.cs`

**Interfaces:**
- Produces:
  - `ToolPolicyConfig(IReadOnlyCollection<string> DisabledTools, DateTimeOffset UpdatedAt)`
  - `ToolPolicyStore`:
    - `IReadOnlySet<string> GetDisabledTools()`
    - `bool IsToolDisabled(string toolName)`
    - `bool IsProtected(string toolName)`
    - `ToolPolicyConfig DisableTools(IEnumerable<string> toolNames)`
    - `ToolPolicyConfig EnableTools(IEnumerable<string> toolNames)`
    - `ToolPolicyConfig Reset()`

- [ ] **Step 1: Write failing unit test for `ToolPolicyStore`**

Cover:
- Default state (empty disabled tools, protected tools immune).
- Disabling tools updates `disabledTools` and sets `updatedAt` generated from backend code.
- Trying to disable `memory_recall` or `tools_policy` does not add them to `disabledTools`.
- Reset clears disabled tools.
- Atomic file write persistence round-trip.

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Eling.Backend.Tests/Eling.Backend.Tests.csproj --filter "FullyQualifiedName~ToolPolicyStoreTests"`
Expected: FAIL (types not found).

- [ ] **Step 3: Implement `ToolPolicyConfig` and `ToolPolicyStore`**

Implement `ToolPolicyConfig` in `Eling.Core.Tools`.
Implement `ToolPolicyStore` in `Eling.Backend.Tools`:
- Thread-safe `_gate` lock.
- Atomic file write: write to `.tmp` file, then `File.Move(tmp, path, overwrite: true)`.
- Trailing `DateTimeOffset.UtcNow` set from code.
- Protected tools list: `{"tools_policy", "memory_recall"}`.

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/Eling.Backend.Tests/Eling.Backend.Tests.csproj --filter "FullyQualifiedName~ToolPolicyStoreTests"`
Expected: PASS.

- [ ] **Step 5: Commit changes**

```bash
git add src/backend/Eling.Core/Tools/ src/backend/Eling.Backend/Tools/ tests/Eling.Backend.Tests/Tools/
git commit -m "feat: implement ToolPolicyStore with atomic writes and protected tools"
```

---

### Task 2: MCP Request Filters (ListToolsFilter & CallToolFilter)

**Files:**
- Create: `src/backend/Eling.Backend/Mcp/Filters/ToolPolicyFilters.cs`
- Modify: `src/backend/Eling.Backend/Mcp/McpServiceExtensions.cs`
- Test: `tests/Eling.Backend.Tests/Mcp/ToolPolicyFilterTests.cs`

**Interfaces:**
- Consumes: `ToolPolicyStore`
- Produces:
  - `ToolPolicyFilters.CreateListFilter(ToolPolicyStore store)`: `McpRequestFilter<ListToolsRequestParams, ListToolsResult>`
  - `ToolPolicyFilters.CreateCallFilter(ToolPolicyStore store)`: `McpRequestFilter<CallToolRequestParams, CallToolResult>`

- [ ] **Step 1: Write failing unit test for MCP tool filters**

Cover:
- `ListToolsFilter` removes disabled tools from `ListToolsResult.Tools`.
- `ListToolsFilter` retains enabled and protected tools.
- `CallToolFilter` allows calls to enabled tools.
- `CallToolFilter` blocks calls to disabled tools, returning `CallToolResult` with `IsError = true` and `"tool_disabled"`.

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Eling.Backend.Tests/Eling.Backend.Tests.csproj --filter "FullyQualifiedName~ToolPolicyFilterTests"`
Expected: FAIL.

- [ ] **Step 3: Implement `ToolPolicyFilters` and register in `McpServiceExtensions`**

Implement `ToolPolicyFilters`:
- `CreateListFilter`: filters `context.Result.Tools` where `!store.IsToolDisabled(tool.Name)`.
- `CreateCallFilter`: checks `store.IsToolDisabled(context.Params.Name)`; if disabled, returns error result before calling `next()`.
Wire filters into `McpServiceExtensions.AddElingMcpServerStdio`:
```csharp
filters.AddListToolsFilter(ToolPolicyFilters.CreateListFilter(toolPolicyStore));
filters.AddCallToolFilter(ToolPolicyFilters.CreateCallFilter(toolPolicyStore));
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/Eling.Backend.Tests/Eling.Backend.Tests.csproj --filter "FullyQualifiedName~ToolPolicyFilterTests"`
Expected: PASS.

- [ ] **Step 5: Commit changes**

```bash
git add src/backend/Eling.Backend/Mcp/ tests/Eling.Backend.Tests/Mcp/
git commit -m "feat: add real-time MCP ListTools and CallTool policy filters"
```

---

### Task 3: Chat Agent Tool (`tools_policy`)

**Files:**
- Create: `src/backend/Eling.Backend/Mcp/Tools/ToolsPolicyTool.cs`
- Test: `tests/Eling.Backend.Tests/Mcp/ToolsPolicyToolTests.cs`

**Interfaces:**
- Consumes: `ToolPolicyStore`, `ToolMetadataRegistry` (or tool catalog mapping)
- Produces: `ToolsPolicyTool` with `[McpServerTool(Name = "tools_policy")]`

- [ ] **Step 1: Write failing unit test for `ToolsPolicyTool`**

Cover:
- `action = "list"`: returns tool status map grouped by category.
- `action = "disable"`: adds tool names to disabled set and returns updated status.
- `action = "disable"` with protected tool: rejects with warning and leaves protected tool active.
- `action = "enable"`: removes tool names from disabled set.
- `action = "reset"`: clears all disabled tools.
- Group-based toggle: `group = "filesystem", action = "disable"` disables all filesystem tools.

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Eling.Backend.Tests/Eling.Backend.Tests.csproj --filter "FullyQualifiedName~ToolsPolicyToolTests"`
Expected: FAIL.

- [ ] **Step 3: Implement `ToolsPolicyTool`**

Implement `ToolsPolicyTool` with categorized metadata definitions:
- Category mapping:
  - `memory`: `memory_recall`, `memory_save`, `memory_get`, `memory_list`, `memory_search`, `memory_delete`, `memory_rebuild_index`, `memory_init_project`, `memory_project_status`, `memory_project_scope_policy`, `memory_maintenance`, `memory_copy_to_project`, `memory_promote_to_global`.
  - `codebase`: `codebase_status`, `codebase_search`, `codebase_index`.
  - `filesystem`: `workspace_root`, `path_test`, `directory_create`, `directory_list`, `glob`, `file_read`, `file_read_any`, `file_write`, `file_delete`, `directory_delete`, `file_move`, `directory_move`, `file_copy`, `directory_copy`, `file_edit`, `file_append`, `file_search`.
- Validate actions and protected tool guards.
- Save through `ToolPolicyStore`.

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/Eling.Backend.Tests/Eling.Backend.Tests.csproj --filter "FullyQualifiedName~ToolsPolicyToolTests"`
Expected: PASS.

- [ ] **Step 5: Commit changes**

```bash
git add src/backend/Eling.Backend/Mcp/Tools/ToolsPolicyTool.cs tests/Eling.Backend.Tests/Mcp/ToolsPolicyToolTests.cs
git commit -m "feat: implement tools_policy MCP tool for conversational agent control"
```

---

### Task 4: Backend REST Endpoints (`/api/tools`)

**Files:**
- Create: `src/backend/Eling.Backend/Endpoints/ToolsEndpoints.cs`
- Modify: `src/backend/Eling.Backend/Bootstrap/DashboardRoutes.cs`
- Test: `tests/Eling.Backend.Tests/Endpoints/ToolsEndpointsTests.cs`

**Interfaces:**
- Produces:
  - `GET /api/tools`: Returns array of `ToolItemDto(string Name, string Group, string Description, bool Enabled, bool IsProtected)`
  - `PUT /api/tools`: Accepts `UpdateToolPolicyRequest(string? ToolName, string? Group, bool Enabled)`

- [ ] **Step 1: Write integration tests for `/api/tools` endpoints**

Cover:
- `GET /api/tools` returns 200 with list of tools, groups, and status flags.
- `PUT /api/tools` with single tool updates its status.
- `PUT /api/tools` with protected tool rejects disabling with 400 Bad Request.
- `PUT /api/tools` with group bulk-updates all tools in the group.

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Eling.Backend.Tests/Eling.Backend.Tests.csproj --filter "FullyQualifiedName~ToolsEndpointsTests"`
Expected: FAIL.

- [ ] **Step 3: Implement `ToolsEndpoints` and register in `DashboardRoutes`**

Implement endpoint handlers and route mappings in `src/backend/Eling.Backend/Endpoints/ToolsEndpoints.cs`.
Register `app.MapToolsEndpoints();` in `DashboardRoutes.cs`.

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/Eling.Backend.Tests/Eling.Backend.Tests.csproj --filter "FullyQualifiedName~ToolsEndpointsTests"`
Expected: PASS.

- [ ] **Step 5: Commit changes**

```bash
git add src/backend/Eling.Backend/Endpoints/ src/backend/Eling.Backend/Bootstrap/ tests/Eling.Backend.Tests/Endpoints/
git commit -m "feat: add /api/tools REST endpoints for tool management"
```

---

### Task 5: Dashboard Web UI (`/dashboard/tools`)

**Files:**
- Create: `src/frontend/Eling.Dashboard/src/app/dashboard/tools/page.tsx`
- Create: `src/frontend/Eling.Dashboard/src/components/tools/ToolCard.tsx`
- Create: `src/frontend/Eling.Dashboard/src/components/tools/ToolGroupSection.tsx`
- Modify: `src/frontend/Eling.Dashboard/src/components/app-sidebar.tsx`

**Interfaces:**
- Consumes: `/api/tools` REST endpoints
- Uses: `Switch`, `Badge`, `Button`, `Input`, `Card` shadcn/ui components

- [ ] **Step 1: Add Tools item to `app-sidebar.tsx`**

Add navigation item:
```typescript
{
  title: "Tools",
  url: "/dashboard/tools",
  icon: <Sliders className="size-4" />,
  isActive: true,
}
```

- [ ] **Step 2: Implement `/dashboard/tools/page.tsx` and tool components**

Build responsive UI:
- Search filter input for filtering tools by name or description.
- Grouped sections: **Memory Tools**, **Codebase Tools**, **Filesystem Tools**.
- Category-level quick actions: "Enable All" and "Disable All" buttons.
- Tool row: Tool name, description, category badge, protected badge (if protected), and instant `Switch` (shadcn/ui).
- Instant PUT call on toggle with optimistic UI update and error rollback toast/alert.

- [ ] **Step 3: Verify frontend compilation and linting**

Run: `pnpm --dir src/frontend/Eling.Dashboard build`
Expected: Build succeeds with 0 errors.

- [ ] **Step 4: Commit changes**

```bash
git add src/frontend/Eling.Dashboard/
git commit -m "feat: add /dashboard/tools page with instant switches and group controls"
```

---

### Task 6: End-to-End Validation & Smoke Test

**Files:**
- Test: `tests/Eling.Backend.Tests/`
- Script: `scripts/validate-eling.ps1`

- [ ] **Step 1: Run full backend test suite**

Run: `dotnet test Eling.slnx`
Expected: All backend unit and integration tests PASS.

- [ ] **Step 2: Run full Eling runtime validation**

Run: `pwsh scripts/validate-eling.ps1 -RuntimeOnly`
Expected: Dashboard API and stdio MCP phases PASS.

- [ ] **Step 3: Commit completion and update documentation**

```bash
git add .
git commit -m "docs: complete implementation plan for dynamic tool management"
```
