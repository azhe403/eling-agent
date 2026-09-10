# Install Eling

> Readable by humans and agents. Every step is copy-pasteable.

## TL;DR

```bash
# 1. Grab the binary for your OS from Releases (win-x64 / linux-x64 / osx-arm64)
# 2. Put it on PATH as eling-backend
# 3. Verify
eling-backend --help   # starts MCP on stdio, port 4317 if free
curl http://127.0.0.1:4317/health
```

## Prerequisites

- .NET 10 SDK (`dotnet --version` → 10.0.x) — only for building from source.
- Node 20 + pnpm 9 — only for building the dashboard UI.
- No DB setup. Storage is `.eling/memories/*.md` + `index.db` (auto-created).

## Option A — Prebuilt binary (recommended for users & agents)

Pick the asset for your need (full matrix: `docs/release.md`):

- `eling-backend-<rid>.zip/.tar.gz` — **default install**: backend + dashboard UI (agents, headless). This is what the install scripts fetch.
- `eling-<rid>.zip/.tar.gz` — Desktop + backend + dashboard UI. Manual download, for desktop users only.
- `eling-dashboard-ui.zip` — dashboard UI only (for repair via the installer, not manual download).

1. Download the asset from the latest Release (or pre-release `v0.1.0-pre.*`).
2. Unzip. You get `eling-backend` (or `.exe` on Windows) + `eling-dashboard-ui/` next to it (plus `eling-desktop` in the mix asset). Keep them together.
3. Move to a PATH dir:
   ```bash
   # Windows
   move eling-backend.exe $HOME/.local/bin/eling-backend.exe
   # Linux/macOS
   mv eling-backend ~/.local/bin/eling-backend && chmod +x ~/.local/bin/eling-backend
   ```
4. Register with your agent host:

   **OpenCode**:
   ```json
   // ~/.config/opencode/opencode.json  (global) — staging 4317
   { "mcp": { "eling": { "command": ["~/.local/bin/eling-backend.exe"], "enabled": true } } }
   // <project>/opencode.json (dev) — 4417
   { "mcp": { "eling_dev": { "command": ["dotnet","watch","--project","src/backend/Eling.Backend/Eling.Backend.csproj"], "environment": { "ELING_DASHBOARD_PORT":"4417" } } } }
   ```

   **Claude Code** (stdio, from any shell):
   ```bash
   # personal, this project only (default scope)
   claude mcp add eling -- ~/.local/bin/eling-backend
   # shared with the team (writes project-root .mcp.json — commit it)
   claude mcp add --scope project eling -- ~/.local/bin/eling-backend
   claude mcp list   # verify
   ```
   Or edit `.mcp.json` at the project root directly:
   ```json
   { "mcpServers": { "eling": { "command": "~/.local/bin/eling-backend" } } }
   ```

   **Claude Desktop** — add to `claude_desktop_config.json`
   (`~/Library/Application Support/Claude/` on macOS, `%APPDATA%\Claude\` on Windows),
   then restart Claude Desktop:
   ```json
   { "mcpServers": { "eling": { "command": "~/.local/bin/eling-backend" } } }
   ```

   **Antigravity** — add to `mcp_config.json`, either globally
   (`~/.gemini/config/mcp_config.json`) or per workspace
   (`.agents/mcp_config.json` in the project root). In the IDE:
   agent panel `…` → MCP Servers → Manage MCP Servers → View raw config,
   or edit the file directly:
   ```json
   { "mcpServers": { "eling": { "command": "~/.local/bin/eling-backend" } } }
   ```

## Option B — Build from source (for contributors & agents that live in the repo)

```bash
git clone https://github.com/<org>/eling && cd eling

# Backend + UI (single binary: MCP + REST + UI on 4317/4417)
dotnet build Eling.slnx
# Isolated test artifacts (one project at a time per Eling convention)
dotnet test tests/Eling.Core.Tests/Eling.Core.Tests.csproj --artifacts-path .bin-test
dotnet test tests/Eling.Backend.Tests/Eling.Backend.Tests.csproj --artifacts-path .bin-test

# Frontend dev (optional, for UI work)
pnpm --prefix src/frontend/Eling.Dashboard install
pnpm --prefix src/frontend/Eling.Dashboard dev   # http://localhost:4427 → proxies to 4417
# Or full stack
pnpm dev    # concurrently: backend 4417 + frontend 4427 (0.0.0.0)
```

Build outputs:
- `dotnet run` → `.bin/Debug/net10.0/eling-backend.dll` (shared `.bin`, flat layout)
- `dotnet test --artifacts-path .bin-test` → isolated `.bin-test/`

## Option C — Publish to staging from source (local install)

Ships the current working tree (uncommitted changes included) as the staging binary. Use it to validate working-tree changes against the real installed binary instead of the dev `dotnet watch` process. No commits, pushes, or tags — the install is independent of git history.

```powershell
# Windows (PowerShell, from repo root)
.\scripts\publish-global.ps1
# POSIX
./scripts/publish-global.sh
```

What it does:
- `dotnet publish` Release / self-contained / single-file (`win-x64` on Windows, auto-detected RID on POSIX; dashboard UI included via the `BuildDashboard` target)
- Installs to user-local bin (`%USERPROFILE%\.local\bin` / `~/.local/bin`): `eling-backend(.exe)` + `eling-dashboard-ui/`
- Stops the running staging process (port 4317) so files are not locked; the dev process (port 4417) is untouched
- Smoke test: `GET /health` on 4317, then an MCP `memory_save` → `memory_get` round-trip in a throwaway temp dir

```powershell
# Faster iteration when you already trust the smoke test
.\scripts\publish-global.ps1 -SkipSmokeTest
```

If the dashboard UI breaks locally (blank page, stale assets), redownload just the UI without touching the binary:

```powershell
# Windows
.\scripts\install.ps1 -DashboardOnly
# POSIX
./scripts/install.sh --dashboard-only
```

Expected output (ids/pids vary per run):

```
Installed & verified:
  health:           {"status":"Healthy","pid":34468}
  memory read/write: OK (saved & searched back id=01m250g7sj6qx4bqgy1p2h88ce)
  binary:            ~/.local/bin/eling-backend.exe
```

## Verify

```bash
# Dashboard (same port serves UI + API) — open in a browser
http://localhost:4317          # staging
http://localhost:4417          # dev (ELING_DASHBOARD_PORT=4417)

# Health
curl http://127.0.0.1:4317/health          # staging
curl http://127.0.0.1:4417/health          # dev (ELING_DASHBOARD_PORT=4417)
curl http://127.0.0.1:4417/api/coordinator/runtimes | jq  # no UserScope, only real projects

# MCP (stdio) — from any project dir with .eling/
echo '{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-03-26","capabilities":{},"clientInfo":{"name":"test","version":"1.0"}}}' | eling-backend
# then tools/list, tools/call memory_save, etc.

# Automated
pwsh scripts/validate-eling.ps1            # build + tests + HTTP + MCP
pwsh scripts/validate-eling.ps1 -RuntimeOnly
```

## Agent checklist (copy for system prompt)

```yaml
eling_install:
  binary: ~/.local/bin/eling-backend   # single binary, not eling.exe
  publish_staging: scripts/publish-global.ps1  # Release self-contained single-file → user-local bin + smoke test
  ports: { staging: 4317, dev: 4417, frontend: 4427 }
  storage_canonical: .eling/memories/*.md   # tracked in Git
  storage_cache: .eling/index.db*           # gitignored, rebuildable
  runtime_probe: "GET http://127.0.0.1:$PORT/health"
  mcp_transport: stdio
  session_start_tool: memory_recall   # on-demand context hydration (renamed from session_start; call any time during a conversation)
  memory_tools: [memory_save, memory_get, memory_search, memory_list, memory_delete]
```

## Troubleshooting

- `address already in use 127.0.0.1:4317/4417` → another `eling-backend` owns it. Probe first: `Test-NetConnection 127.0.0.1 -Port 4317` or `ss -tlnp | grep 4317`. Second instance skips Kestrel and runs MCP-only.
- `pnpm dev` fails with `ELING_OUTPUT_ROOT` mismatch → use `.bin-test` for tests, `.bin` for dev. Never share `.bin/` between test and dev.
- `dotnet watch` slow → set `ELING_SKIP_DASHBOARD=true` to skip `pnpm build` in watch loop.
- `UserScope` showing in dashboard → fixed in `RuntimeRegistry.Alive()` (single `Dtos/` folder). Pull latest.

## Uninstall

```bash
rm ~/.local/bin/eling-backend(.exe)
# keep .eling/memories/ — it's your data
```
