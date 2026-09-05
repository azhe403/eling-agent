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

1. Download `eling-<rid>.zip/.tar.gz` from the latest Release (or pre-release `v0.1.0-pre.*`).
2. Unzip. You get `eling-backend` (or `.exe` on Windows) + `eling-dashboard-ui/` next to it. Keep them together.
3. Move to a PATH dir:
   ```bash
   # Windows
   move eling-backend.exe $HOME/.local/bin/eling-backend.exe
   # Linux/macOS
   mv eling-backend ~/.local/bin/eling-backend && chmod +x ~/.local/bin/eling-backend
   ```
4. Register with your agent host:
   ```json
   // ~/.config/opencode/opencode.json  (global) — staging 4317
   { "mcp": { "eling": { "command": ["~/.local/bin/eling-backend.exe"], "enabled": true } } }
   // <project>/opencode.json (dev) — 4417
   { "mcp": { "eling_dev": { "command": ["dotnet","watch","--project","src/backend/Eling.Backend/Eling.Backend.csproj"], "environment": { "ELING_DASHBOARD_PORT":"4417" } } } }
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

## Verify

```bash
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
  ports: { staging: 4317, dev: 4417, frontend: 4427 }
  storage_canonical: .eling/memories/*.md   # tracked in Git
  storage_cache: .eling/index.db*           # gitignored, rebuildable
  runtime_probe: "GET http://127.0.0.1:$PORT/health"
  mcp_transport: stdio
  session_start_tool: memory_recall   # on-demand context hydration (renamed from session_start; call any time during a conversation)
  memory_tools: [memory_save, memory_get, memory_search, memory_list, memory_update, memory_delete]
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
