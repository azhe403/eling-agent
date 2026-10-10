# Eling Desktop Built-in MCP Design Specification

## Overview
This specification details the architecture and implementation plan for making Eling MCP tools directly available ("built-in") to the Eling Desktop conversational AI assistant.

---

## Goals & Objectives
1. **Out-of-the-Box Tool Execution**: Enable the conversational agent in Eling Desktop to autonomously invoke Eling's memory tools (`memory_recall`, `memory_save`, `memory_get`, `memory_search`, `memory_list`, `memory_maintenance`) and filesystem tools (`file_read`, `file_write`, `directory_list`, `glob`, `file_search`).
2. **Seamless Scope & Context Binding**: Ensure tool execution properly resolves the active workspace folder selected in Eling Desktop without requiring manual MCP client/server configuration.
3. **Transparent Execution**: Surface tool call states and outputs in the desktop UI (`ChatView`) cleanly so the user has full visibility into what tools the agent runs.

---

## Architecture & Data Flow

```
┌────────────────────────────────────────────────────────┐
│                   Eling Desktop (Avalonia)             │
│  - ChatViewModel sends message via ElingApiClient      │
│  - Displays streaming events: text delta & tool calls   │
└───────────────────────────┬────────────────────────────┘
                            │ HTTP POST / SSE
                            ▼
┌────────────────────────────────────────────────────────┐
│                   Eling Backend Service                │
│  - AgentTurnService                                    │
│    ├── Gateway (MeaiChatGateway / OpenAI Protocol)    │
│    └── Tool Registry (Collection of IAgentTool)        │
│          ├── MemoryRecallAgentTool                     │
│          ├── MemorySaveAgentTool                       │
│          ├── MemoryReadAgentTool                       │
│          └── FileSystemAgentTools (read/write/list/glob)
│  - McpServer (Stdio transport remains for external AI) │
└────────────────────────────────────────────────────────┘
```

---

## Detailed Components

### 1. `IAgentTool` Built-in Adapters
Create reusable `IAgentTool` implementations under `Eling.Backend.Agent.Services.Tools` (or adapt `[McpServerToolType]` classes) to bridge MCP tool methods into `IAgentTool`:
- **`MemoryRecallAgentTool`**: Provides semantic memory hydration via `IMemoryRecallService`.
- **`MemorySaveAgentTool`**: Persists new memories using `IScopedMemoryService`.
- **`MemoryReadAgentTool`**: Handles `memory_get`, `memory_search`, and `memory_list`.
- **`FileSystemAgentTool`**: Provides sandboxed file operations (`file_read`, `file_write`, `directory_list`, `glob`, `file_search`) rooted at the active workspace.

### 2. Dependency Injection & Service Registration
In `Eling.Backend.Bootstrap.DashboardServices.Register`:
- Register all `IAgentTool` implementations as `IEnumerable<IAgentTool>` so `AgentTurnService` dynamically discovers and includes them in every LLM request.

### 3. System Prompt & Model Context
- Configure `AgentTurnService` system prompt to inform the model about available tools, workspace path, and best practices (such as calling `memory_recall` prior to answering context-sensitive questions).

### 4. Desktop Chat Visualization
- In `ChatViewModel.cs` and `ChatView.axaml`, render `StreamTool` events with clean, expandable UI blocks (`ChatMessageRow` with `IsTool = true`), displaying input arguments and execution output.

---

## Verification & Testing Strategy
1. **Unit & Integration Tests**:
   - `AgentTurnServiceTests`: Verify tool definitions are correctly passed to `IChatGateway`.
   - Verify tool calls return expected results and handle failures gracefully.
2. **End-to-End Desktop Verification**:
   - Start backend and desktop.
   - Send chat prompt: *"Eling, save a note that this project uses .NET 10 and Avalonia 12"*.
   - Verify agent executes `memory_save` tool and reports success.
   - Send follow-up prompt: *"What files are in the root of this project?"*.
   - Verify agent executes `directory_list` tool and lists repository files.
