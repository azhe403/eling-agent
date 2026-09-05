# Eling Agent Workspace Instructions

## Language
- "Eling" = Javanese for "ingat / to remember". When the user says "eling <something>", treat it as a recall instruction, not just the project name.
- Chat in English; docs & code in English.

## Build & Test
- Build, unit test commands, & code conventions (HARD RULES): **recall project memory** (build commands, code conventions, unit test rules).
- Unit tests: per csproj, NEVER solution-wide/chain (`;`) — can lock `.bin/` or hang.

## Dev Servers
Backend dev (`eling_dev`) → **4417**; Frontend → **4427** (proxy `/api/*` → 4417). Details: recall project memory.

## Git Workflow
Commits = user-controlled checkpoints: implement → test → report → stop. NEVER commit/push/amend/reset/rebase/force-push unless asked. Leave work in the working tree; report `git status --short` before stopping.

## Eling Memory
All operations via **MCP tools** — NEVER touch `.eling/memories/` files directly. Prefer `mcp_eling_dev_*`, fallback `mcp_eling_*`. `memory_recall` is the on-demand context-hydration tool — invoke when you need to refresh the slice of memory relevant to the current task, then save with `memory_save`.

### Mandatory Session-Start Recall ("Eling session recall" — HARD RULE)
First turn of every new chat, BEFORE writing any response or running any other tool, you MUST invoke **Eling session recall** = `memory_recall` (via `mcp_eling_dev_*`, e.g. `eling_dev_memory_recall` — fallback `mcp_eling_*`) with a topics array derived from the user's opening message. Do not skip this step. Do not announce it in the response. Use the recalled context internally, then answer the user naturally.
