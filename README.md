# Eling

Persistent memory and context layer for AI coding agents — durable recall over MCP, with codebase search, read-only database access, and a full audit trail. Markdown-first storage; SQLite as a rebuildable index. Web dashboard included; desktop app experimental.

## Install with your agent (one paste)

Paste this into your coding agent — it installs the binary and registers the MCP server for you:

```text
Install Eling from https://github.com/azhe403/eling-agent:
1. Detect this OS and run the matching one-line installer from that repo's INSTALL.md (TL;DR) — latest stable, pre-release fallback.
2. Then fetch and follow https://raw.githubusercontent.com/azhe403/eling-agent/main/docs/agent-setup.md exactly: register the `eling` MCP server for this host (merge into the existing config, never clobber), verify the stdio handshake, and tell me if a host restart is needed.
3. Do not create `.eling/` silently — ask me first before calling memory_init_project.
4. Report the binary path, the host config file that changed, and the MCP status.
```

## Install the binary (one line)

One line per OS installs the `eling-backend` binary + dashboard UI and adds it to `PATH` — commands per OS, staging, troubleshooting and uninstall: [INSTALL.md](INSTALL.md) (TL;DR). It does **not** register the MCP server in your agent host — for the full setup (binary + MCP config + verify), use the agent paste block above. Installs latest stable, fallback to latest pre-release.

## What's in it

- **Persistent agent memory over MCP** — `memory_save` / `memory_get` / `memory_search` / `recall` with hybrid FTS search, scope-aware ranking, and overflow summaries; plus `memory_list`, `memory_delete`, `memory_maintenance` (dedup), and ULID-based identity.
- **Three scopes** — `project` (`.eling/memories/`, committed to Git), `project-local` (machine-only, default), and `global` (`~/.config/eling/`).
- **Codebase index & search** — per-workspace FTS index, `codebase_search` with content/path/file scopes, worktree-aware indexing.
- **Read-only database access** — `db_list_connections`, `db_detect_connections`, `db_import_connections`, `db_get_schema`, `db_query` (single-statement, read-only) over MySQL/SQLite; plus `redis_scan` / `redis_inspect` with a scan cache.
- **Audit trail** — change and access trails as JSONL (`AuditEvent` schema), SQLite read cache, retention and rollups.
- **Operational tooling** — structured logs (`log_list` / `log_read`, text and JSONL), a policy-gated `shell_execute` MCP tool, and a per-user tool policy (`tools_policy`).
- **Web dashboard** (Next.js) — memories, workspaces, codebase search, databases/Redis explorer, tools, audit trail, logs; served by the same binary.
- **Desktop app** (Avalonia, experimental) — standalone memory browsing and agent chat.

Detailed history in [CHANGELOG.md](CHANGELOG.md).

## Stack

- `.NET 10` backend, single binary `eling-backend` (MCP + HTTP API + static dashboard UI).
- Next.js dashboard in `src/frontend/Eling.Dashboard` (prod: static export served by the backend).
- Avalonia desktop client in `src/desktop/Eling.Desktop` (experimental).

## Build & Test

### Backend
```bash
# Dev build (outputs to shared .bin/)
dotnet build Eling.slnx
dotnet build src/backend/Eling.Backend/Eling.Backend.csproj -p:ElingSkipDashboard=true  # fast, skip pnpm

# Run tests (one project at a time). Each run builds into a disposable
# output root that is deleted when the run ends.
pnpm test:backend:core
pnpm test:backend:api
pnpm test:backend:desktop
# Reclaim any build output root left behind by an earlier run
pwsh scripts/clean-bin.ps1
```

### Frontend
```bash
pnpm --prefix src/frontend/Eling.Dashboard install
pnpm --prefix src/frontend/Eling.Dashboard build   # or pnpm dev → 4427 → backend 4417
```

`validate-eling.ps1` runs the full flow (build → tests → dashboard HTTP API → stdio MCP); scripts now target `eling-backend.exe`.

## Docs

- [INSTALL.md](INSTALL.md) — binary + MCP setup, staging, troubleshooting
- [docs/agent-setup.md](docs/agent-setup.md) — host MCP registration playbook
- [docs/using-eling.md](docs/using-eling.md) — day-to-day usage
- [docs/architecture.md](docs/architecture.md) — layers, ports, feature map
- [docs/philosophy.md](docs/philosophy.md) — why Eling exists
- [docs/recall-architecture.md](docs/recall-architecture.md) — hybrid recall & FTS engine
- [docs/semantic-judge-architecture.md](docs/semantic-judge-architecture.md) — semantic judge & dedup
- [CHANGELOG.md](CHANGELOG.md) — what changed, when
