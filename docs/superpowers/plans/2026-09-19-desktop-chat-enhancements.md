# Eling Desktop Chat Visual & Tool Execution Enhancements Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Enhance Eling Desktop UI's chat experience with structured, collapsible tool execution cards, status indicators, and clean typography for message threads.

**Architecture:** Extend `ChatMessageRow` to support tool arguments and expansion toggles, update `ChatViewModel` message parsing, and customize Avalonia DataTemplates in `ChatView.axaml`.

**Tech Stack:** .NET 10, Avalonia UI, ReactiveUI, C# 14.

---

### Task 1: Extend `ChatMessageRow` Model with Tool State & Toggles

**Files:**
- Modify: `src/desktop/Eling.Desktop/ViewModels/ChatViewModel.cs`
- Test: `tests/Eling.Desktop.Tests/ChatViewModelTests.cs`

- [ ] **Step 1: Write failing test for tool row states and toggle behavior**
- [ ] **Step 2: Add properties (`Arguments`, `IsExpanded`, `ToggleExpandCommand`, `StatusBadge`) to `ChatMessageRow`**
- [ ] **Step 3: Run unit tests to verify pass**

---

### Task 2: Update `ChatView.axaml` DataTemplates for Interactive Tool Cards

**Files:**
- Modify: `src/desktop/Eling.Desktop/Views/ChatView.axaml`
- Modify: `src/desktop/Eling.Desktop/Styles.axaml`

- [ ] **Step 1: Add distinct Card styling for User, Assistant, and Tool messages**
- [ ] **Step 2: Add expand/collapse button and formatted parameter display for Tool cards**
- [ ] **Step 3: Verify XAML compilation and visual binding**

---

### Task 3: Build & Automated Test Verification

**Files:**
- Test: `tests/Eling.Desktop.Tests/Eling.Desktop.Tests.csproj`

- [ ] **Step 1: Build the desktop project and run desktop tests**
- [ ] **Step 2: Validate end-to-end message row rendering**
