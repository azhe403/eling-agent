# Eling Agent Workspace Instructions

## Language
- "Eling" = Javanese for "ingat / to remember". When the user says "eling <something>", treat it as a recall instruction, not just the project name.
- Chat in English; docs & code in English.

## Build & Test
- Build, unit test commands, & code conventions (HARD RULES): **recall project memory** (build commands, code conventions, unit test rules).
- Unit tests: per csproj, NEVER solution-wide/chain (`;`) — can lock `.bin/` or hang.

## Dev Servers
Backend dev (`eling_dev`) → **4417**; Frontend → **4427** (proxy `/api/*` → 4417). Details: recall project memory.

## Install from URL
- When the user prompts `install <github-url>` (e.g. install https://github.com/azhe403/eling-agent), do NOT clone the repo for exploration. Run the one-line installer for the current OS from README/INSTALL.md (stable first, pre-release fallback). Clone only when the user explicitly asks to build from source or contribute.
- After install, do NOT stop at the binary. Follow `docs/agent-setup.md`: detect the host, register the MCP server (`eling`) globally (merge config, never clobber, no secrets), verify the stdio handshake, then ask consent before `memory_init_project`.

## Git Workflow
Commits = user-controlled checkpoints: implement → test → report → stop. NEVER commit/push/amend/reset/rebase/force-push unless asked. Leave work in the working tree; report `git status --short` before stopping.

## Eling Memory
All operations via **MCP tools** — NEVER touch `.eling/memories/` files directly. Prefer `mcp_eling_dev_*`, fallback `mcp_eling_*`. `memory_recall` is the on-demand context-hydration tool — invoke when you need to refresh the slice of memory relevant to the current task, then save with `memory_save`.

### Mandatory Recall ("Eling recall" — HARD RULE)
Invoke `memory_recall` in these situations:

1. **Session start** — First turn of every new chat, BEFORE writing any response or running any other tool, with topics derived from the user's opening message.

2. **Before git commit** — Before any commit preparation, recall git/workflow/conventions memories to ensure no hygiene violations.

3. **Before significant task** — When starting a non-trivial task, check if relevant memories exist and are stale.

Use `mcp_eling_dev_*` tools (e.g. `eling_dev_memory_recall`) — fallback to `mcp_eling_*`. Do not skip. Do not announce it in the response. Use recalled context internally, then execute.
