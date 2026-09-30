# Desktop Built-in MCP Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Enable built-in execution of Eling MCP tools directly within Eling Desktop's conversational AI agent.

**Architecture:** Implement `IAgentTool` adapters for Eling's MCP tools in `Eling.Backend`, register them in the DI container, provide workspace binding for scoped memory/filesystem operations, and ensure `ChatViewModel` in Avalonia renders tool execution and outputs.

**Tech Stack:** .NET 10, C# 13/14, Avalonia 12.1.2, ReactiveUI, OpenAI / MEAI client.

---

### Task 1: Create MCP Tool Adapters as `IAgentTool`

**Files:**
- Create: `src/backend/Eling.Backend/Agent/Services/Tools/MemoryRecallAgentTool.cs`
- Create: `src/backend/Eling.Backend/Agent/Services/Tools/MemorySaveAgentTool.cs`
- Create: `src/backend/Eling.Backend/Agent/Services/Tools/MemoryReadAgentTool.cs`
- Create: `src/backend/Eling.Backend/Agent/Services/Tools/FileSystemAgentTools.cs`
- Test: `tests/Eling.Backend.Tests/Agent/AgentToolTests.cs`

**Interfaces:**
- Consumes: `IMemoryRecallService`, `IScopedMemoryService`, `IFileSystemService`, `IAgentTool`
- Produces: Executable `IAgentTool` instances that expose JSON schemas and execute tool payloads.

- [ ] **Step 1: Write unit tests for agent tools**
- [ ] **Step 2: Run tests to verify failure**
- [ ] **Step 3: Implement `IAgentTool` adapters**
- [ ] **Step 4: Run tests to verify pass**
- [ ] **Step 5: Commit changes**

---

### Task 2: Register Built-in MCP Tools in DI & Configure `AgentTurnService`

**Files:**
- Modify: `src/backend/Eling.Backend/Bootstrap/DashboardServices.cs`
- Modify: `src/backend/Eling.Backend/Agent/Services/AgentTurnService.cs`
- Test: `tests/Eling.Backend.Tests/Agent/AgentTurnServiceTests.cs`

**Interfaces:**
- Consumes: `IServiceCollection`, `IAgentTool`
- Produces: Fully registered tool list in `AgentTurnService`.

- [ ] **Step 1: Add integration test verifying tool discovery in AgentTurnService**
- [ ] **Step 2: Register tool implementations in `DashboardServices.cs`**
- [ ] **Step 3: Verify system prompt and workspace resolution in `AgentTurnService`**
- [ ] **Step 4: Run test suite to verify passing status**
- [ ] **Step 5: Commit changes**

---

### Task 3: Enhance Desktop Chat UI for Tool Call Feedback

**Files:**
- Modify: `src/desktop/Eling.Desktop/ViewModels/ChatViewModel.cs`
- Modify: `src/desktop/Eling.Desktop/Views/ChatView.axaml`

- [ ] **Step 1: Verify tool stream handling and visual formatting in `ChatViewModel.cs`**
- [ ] **Step 2: Test expand/collapse tool call results in Avalonia desktop UI**
- [ ] **Step 3: Run backend and desktop verification**
- [ ] **Step 4: Commit changes**
