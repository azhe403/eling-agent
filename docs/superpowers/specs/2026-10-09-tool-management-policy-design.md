# Dynamic Tool Management & Enable/Disable Policy Specification

**Date:** 2026-10-09  
**Status:** Draft (Awaiting User Review)  
**Scope:** `Eling.Core`, `Eling.Backend`, `Eling.Dashboard`, and test suites

---

## 1. Overview & Goals

Eling exposes MCP tools across memory operations, codebase indexing/search, and sandboxed filesystem manipulations. Currently, all discovered tools are unconditionally active.

This specification introduces **Dynamic Tool Management**, allowing users to:
1. Enable or disable individual MCP tools or functional tool groups.
2. Configure settings from the web dashboard (`/dashboard/tools`) with instant toggle switches.
3. Manage settings directly via chat using a dedicated MCP tool (`tools_policy`).
4. Apply state changes **immediately in real time without restarting** the MCP server or the host client process.

---

## 2. Architecture & Components

```
┌────────────────────────────────────────────────────────┐
│                   Eling Dashboard (Web UI)             │
│   - Route: /dashboard/tools                            │
│   - Instant switches (shadcn/ui Switch)                │
│   - Group toggles (Memory, Codebase, Filesystem)       │
└───────────────────────────┬────────────────────────────┘
                            │ HTTP GET / PUT /api/tools
                            ▼
┌────────────────────────────────────────────────────────┐
│                   Eling Backend Engine                 │
│                                                        │
│  ┌──────────────────────────────────────────────────┐  │
│  │ ToolPolicyStore (Thread-Safe File/Cache Store)   │  │
│  │ Path: <user-scope>/config/tools-policy.json       │  │
│  └──────────┬───────────────────────────▲───────────┘  │
│             │                           │              │
│      inspect│                      write│              │
│             ▼                           │              │
│  ┌────────────────────────┐    ┌────────┴───────────┐  │
│  │ MCP Request Filters    │    │ ToolsPolicyTool    │  │
│  │ - ListToolsFilter      │    │ (tools_policy)     │  │
│  │ - CallToolFilter       │    │ Chat interface     │  │
│  └──────────┬─────────────┘    └────────────────────┘  │
│             │                                          │
│             │ JSON-RPC stdio                           │
│             ▼                                          │
│      Agent / MCP Client (OpenCode, Claude, etc.)       │
└────────────────────────────────────────────────────────┘
```

---

## 3. Storage & Domain Model (`ToolPolicyStore`)

### 3.1 File Location & Persistence
- Location: `<user-scope>/config/tools-policy.json` (machine-local / global user level, matching `<user-scope>/config` pattern).
- Serialization: `snake_case` JSON fields (per repository config standard: `JsonNamingPolicy.SnakeCaseLower`), indented, resilient against missing or corrupted files.

### 3.2 Schema Definition & Mutation Semantics
```json
{
  "disabled_tools": [
    "file_delete",
    "directory_delete"
  ],
  "updated_at": "2026-10-09T08:00:00.0000000Z"
}
```

*Mutation Flow:*
- `updatedAt` is generated and assigned in backend code (`DateTimeOffset.UtcNow`) at the end of the mutation, after validation succeeds.
- Serialization writes to a temporary file (`.tmp`) first, followed by `File.Move` with overwrite/replace.
- In-memory cache is swapped only after the physical file write succeeds, avoiding partial state drift on I/O failures.

### 3.3 Immunity & Protected Tools
Certain tools are mission-critical for system integrity and self-recovery. They are protected and cannot be disabled:
- `tools_policy`: Must remain active so the agent can always inspect or re-enable disabled tools via chat.
- `memory_recall`: Mandatory context hydration foundation for agent workflows.

Attempting to disable a protected tool produces a clear validation error without mutating state.

---

## 4. MCP Runtime Filtering & Dynamic Enforcement

### 4.1 Discovery Filtering (`AddListToolsFilter`)
- Intercepts incoming `tools/list` JSON-RPC requests.
- Reads `ToolPolicyStore.GetDisabledTools()`.
- Filters out any tool whose name is present in `disabledTools`.
- Output: The agent's prompt/tool context only receives active tools, reducing token overhead and preventing invalid attempts.

### 4.2 Execution Guard (`AddCallToolFilter`)
- Intercepts incoming `tools/call` JSON-RPC requests.
- If a disabled tool is targeted (e.g. from cached model history):
  - Returns `CallToolResult` with `IsError = true`.
  - Error message block: `tool_disabled: Tool '{toolName}' is currently disabled by user policy.`
  - Execution short-circuits immediately without invoking the target handler.

### 4.3 Client Notification
- After updates via REST or `tools_policy`, backend emits an MCP notification:
  `notifications/tools/list_changed`
  allowing supported clients to refresh their tool manifests automatically.

---

## 5. Chat Interface: `tools_policy` Tool

- **Class**: `ToolsPolicyTool` (`[McpServerToolType]`)
- **Tool Name**: `tools_policy`
- **Description**: *"Inspect or modify the enabled/disabled state of MCP tools in real-time. Actions: 'list', 'enable', 'disable', 'reset'. Changes take effect immediately without restarting."*

### Parameters
| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `action` | string | Yes | One of: `"list"`, `"enable"`, `"disable"`, `"reset"`. |
| `tools` | string[] | No | Target tool names (e.g. `["file_delete", "file_edit"]`). |
| `group` | string | No | Target group (`"filesystem"`, `"codebase"`, `"memory"`). |

### Action Behaviors
- `list`: Returns the current state of all tools grouped by category.
- `enable`: Removes specified tools or group from `disabledTools`.
- `disable`: Appends specified tools or group to `disabledTools` (skipping protected tools).
- `reset`: Clears all disable rules (enables all tools).

---

## 6. Dashboard Web Interface & REST Endpoints

### 6.1 Endpoints (`/api/tools`)
- **`GET /api/tools`**: Returns tool list with group, description, `enabled`, and `isProtected`.
- **`PUT /api/tools`**: Body `{ "tool": string, "enabled": boolean }` or `{ "group": string, "enabled": boolean }`.

### 6.2 Frontend Page: `/dashboard/tools`
- **Navigation**: Added to `app-sidebar.tsx` with a `Wrench` or `Sliders` icon.
- **Controls**:
  - Global Search bar (filter tools by name or description).
  - Categorized Cards (`Memory`, `Codebase`, `Filesystem`).
  - Per-group Quick Actions ("Enable All", "Disable All").
  - Per-tool Row: Name, description, category badge, protected badge, and an instant **Switch (shadcn/ui)**.

---

## 7. Verification & Testing Plan

1. **Unit Tests (`Eling.Backend.Tests/Tools/ToolPolicyStoreTests.cs`)**:
   - Persistence round-trip to JSON.
   - Immunity enforcement against protected tools.
   - Bulk group enablement/disablement logic.
2. **MCP Filter Tests (`Eling.Backend.Tests/Mcp/ToolPolicyFilterTests.cs`)**:
   - `tools/list` filters disabled tools from response.
   - `tools/call` blocks disabled tools with `tool_disabled` error.
3. **Chat Tool Tests (`Eling.Backend.Tests/Mcp/ToolsPolicyToolTests.cs`)**:
   - Validates `list`, `enable`, `disable`, and `reset` actions.
4. **Dashboard Smoke & Validation**:
   - Verify `/api/tools` endpoints respond and update correctly.
   - Run `pnpm build` in `Eling.Dashboard` to ensure zero compilation or lint errors.
