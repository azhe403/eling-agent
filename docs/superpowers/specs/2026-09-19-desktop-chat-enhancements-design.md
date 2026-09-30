# Eling Desktop Chat Visual & Tool Execution Enhancements — Design Spec

Date: 2026-09-19
Status: implemented (verified & committed in 5d13c99)
Scope: `src/desktop/Eling.Desktop` (ChatView, ChatViewModel, Models)

## 1. Overview & Goals

The Eling Desktop chat client currently displays basic raw text blocks for user, assistant, and tool executions. The objective of this enhancement is to deliver a clean, structured, and modern desktop chat experience with:
- **Interactive Tool Execution Cards**: Displaying server-side tool calls (`read_file`, `list_dir`, `recall_memory`, `save_memory`) in collapsible, clear status cards with argument summaries and expandable output.
- **Improved Message Typography & Formatting**: Better contrast, distinct styling between user inputs, assistant markdown-like narrative text, and tool execution diagnostics.
- **Smooth Turn-Based Status Feedback**: Clear visual indicator while the backend agent turn is active and tools are being evaluated.

## 2. Architecture & Component Changes

```text
Eling.Desktop
  Views/
    ChatView.axaml              -> Enhanced Message DataTemplates (User, Assistant, ToolCard)
  ViewModels/
    ChatViewModel.cs            -> Manages session, active turn state, and structured rows
    ChatMessageRow.cs           -> Enriched with tool status (Running/Success/Failed), arguments, and toggle state
```

### Data Model (`ChatMessageRow`)
- `Role`: `"User"` | `"Assistant"` | `"Tool"`
- `Text`: Body text or execution result
- `ToolName`: Name of executed tool
- `Arguments`: Parsed or raw tool call arguments
- `IsExpanded`: Boolean property for collapsible tool execution output
- `IsToolRunning`: Visual indicator state

## 3. UI/UX Specifications
- **User Messages**: Accent background pill, right-aligned or distinct container.
- **Assistant Messages**: High readability typography with clear separation.
- **Tool Rows**: Compact banner showing `🔧 Tool: [tool_name]` with execution status badge. Click to expand full payload/result.

## 4. Testing & Verification
- Unit test coverage for `ChatViewModel` message transformation and collapsible state.
- Manual verification on Avalonia UI with sample conversation containing multiple tool invocations.
