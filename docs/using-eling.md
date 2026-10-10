# Using Eling — Quick Start Guide

**Eling** (Javanese: *to remember / to be mindful*) is an autonomous, Git-native persistent memory layer for AI coding agents.

Eling provides **no terminal CLI**. It operates purely behind the scenes through **MCP**, a **Web Dashboard**, and a **Desktop Client**.

---

## 1. Natural Conversation (Zero-Agents Philosophy)

You do **not** need special syntax, magic commands, or prefix keywords like `eling save ...` or `eling recall ...`. 

Just talk to your coding agent naturally:

- *"From now on, always use CliWrap for external processes."* → Agent autonomously invokes `memory_save` (tier-1 preference/rule).
- *"How do we run the backend server?"* → Agent checks memories via `memory_recall` and provides the exact procedure.
- *"We decided to switch the database provider to PostgreSQL."* → Agent records the architectural decision to memory.
- *"Remember this fix for next time."* → Agent persists the lesson learned.

### How it works autonomously
- **Session-Start Recall**: On the very first turn of any conversation, the agent automatically hydrates relevant context from memory without being asked.
- **Phase Transition Recall**: When switching tasks, starting new specs, or preparing commits, the agent recalls project conventions.
- **Autonomous Saving**: When you state standing rules, preferences, corrections, or decisions, the agent saves them immediately.
- **Optional Shorthand**: You can optionally say `eling <topic>` as a casual shorthand, but it is never mandatory.

---

## 2. Three Access Interfaces

| Interface | URL / Access | Description |
|---|---|---|
| **AI Agent (MCP)** | Stdio / in-app | Primary interface. Agents autonomously use tools like `memory_recall`, `memory_save`, `codebase_search`, and sandboxed file tools. |
| **Web Dashboard** | `http://127.0.0.1:4427` | Browser UI for visual memory browsing, real-time SSE event inspection, active runtimes, and audit trail. |
| **Desktop App (`Eling.Desktop`)** | Native Application | Avalonia desktop app for standalone memory browsing, runtime monitoring, and direct agent chat. |

---

## 3. Memory Scopes

- **Project Scope (Default)**: Memories stored in `.eling/memories/` as Markdown files and committed to Git. Shared across the entire team and repository. Use for **durable, shareable knowledge**: architecture decisions, conventions, standing rules, lessons.
- **Project-Local Scope**: Memories stored machine-only under the central shard (`DATA/eling/projects/<name>-<hash>/`, shared across worktrees of one repo), never committed. Use for **progress or temporary state**: where you left off, scratch findings, machine-specific notes. Save with `scope="project-local"`.
- **Global Scope**: Memories stored in `~/.config/eling/memories/`. Ideal for cross-project personal preferences and machine-specific tooling rules.

---

## 4. Guiding Principles

1. **Let the Agent Remember** — Focus on discussing code, problems, and decisions; the agent captures and recalls context seamlessly.
2. **Durable Knowledge Only** — Save architectural choices, conventions, recurring pitfalls, and workflows. Ephemeral task state stays in conversation context.
3. **No Secrets in Memory** — Avoid storing passwords, API tokens, personal credentials, or machine-specific absolute paths in project memories.

---

*For technical architecture and complete protocol details, see `AGENTS.md`, `docs/recall-architecture.md` (FTS hybrid recall engine), and `docs/semantic-judge-architecture.md` (Semantic Judge deduplication).*
