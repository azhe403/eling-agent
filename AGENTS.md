# Eling Agent Workspace Instructions

## Build backend (dev: shared .bin)
dotnet build Eling.slnx

## Run backend tests (isolated .bin-test)
dotnet test Eling.slnx --artifacts-path .bin-test

## Install frontend deps
pnpm --prefix src/frontend/Eling.Dashboard install

## Build frontend
pnpm --prefix src/frontend/Eling.Dashboard build

## Git Workflow

The agent must treat Git commits as user-controlled checkpoints.

Default workflow:

1. Implement the requested change.
2. Run relevant tests and validation.
3. Report the changed files and validation results.
4. Stop and wait for the user.

Rules:

* Do NOT run `git commit` unless the user explicitly asks for a commit.
* Do NOT run `git push` unless the user explicitly asks for a push.
* Do NOT amend commits unless explicitly requested.
* Do NOT reset, rebase, or rewrite Git history unless explicitly requested.
* Do NOT use force push.
* Do NOT create commits merely because a task is complete.
* Do NOT modify unrelated files just to create a clean commit.
* Leave completed work in the working tree so the user can review it.
* Before stopping, report `git status --short` and summarize the changes.

The user decides when a checkpoint is committed.

## Eling Memory Management

Manage project memories through the Eling MCP servers, NOT by direct file edits.

Two Eling MCP servers are available; global and project memory data is shared between them:
- `eling_dev` (project `opencode.json`, port 4417, `dotnet watch` on dev backend) — **PRIMARY** for all memory read/write.
- `eling` (global config, port 4317, global `eling` binary) — **FALLBACK**; use only if `eling_dev` is unavailable/failing.

Available tools (prefixed by server name, e.g. `eling_dev_memory_save`):
- `*_memory_save` — create/update memories
- `*_memory_get` — retrieve by ID
- `*_memory_list` — list all
- `*_memory_search` — search by query
- `*_memory_delete` — remove by ID
- `*_memory_rebuild_index` — rebuild search index

Rules:
* Use `eling_dev` for all memory read/write by default. Fall back to `eling` (global) only when `eling_dev` is unavailable.
* Data is shared: both servers read/write the same global memory (`~/.config/eling`) and the same project memory (`.eling/` in this repo).
* Always sync memories to BOTH Vestige AND Eling MCP when saving.
* Memory content must be project-specific (e.g., "For Eling project"), never generic.
* When MCP needs restart (code changes to MCP server), ask user to restart — never edit memory files directly.
* Memory files live in `.eling/memories/` and are tracked in Git.
