# Eling Agent Workspace Instructions

## Dev Servers
Backend dev (`eling_dev`) → **4417**; Frontend → **4427** (proxy `/api/*` → 4417).

## Install from URL
- When the user prompts `install <github-url>` (e.g. install https://github.com/azhe403/eling-agent), do NOT clone the repo for exploration. Run the one-line installer for the current OS from README/INSTALL.md (stable first, pre-release fallback). Clone only when the user explicitly asks to build from source or contribute.

## Memory & Conventions
- All memory operations and context hydration use Eling MCP tools (`eling_dev_*` / `eling_*`) following Server Instructions.
