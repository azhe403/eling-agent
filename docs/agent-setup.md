# Agent Setup — install → MCP config → verify

> When a user prompts `install <github-url>` (for example `install https://github.com/azhe403/eling-agent`),
> the job is **not done** when the binary lands. The agent must finish the whole chain:
> install the binary, register the MCP server in the host the user actually runs, verify it, and offer project init.

This is the agent-facing playbook. Humans can follow it too. Every step is copy-pasteable.

## Contract (do this every time)

1. **Default scope is global.** Eling is cross-project memory — register once, use everywhere. Only use project scope when the user asks for it.
2. **Merge, never clobber.** Preserve every other key in the config. Update an existing `eling` entry in place; do not append a duplicate.
3. **No secrets, no absolute user paths in tracked files.** Config values point at the binary; nothing else.
4. **Report what changed** and tell the user to restart/reload the host.
5. **Project `.eling` init needs consent.** Never call `memory_init_project` without asking first.

## Step 0 — Resolve the binary path

| OS | Binary |
|---|---|
| Windows | `%USERPROFILE%\.local\bin\eling-backend.exe` |
| macOS / Linux | `$HOME/.local/bin/eling-backend` |

On Windows, many hosts spawn the command **without a shell**, so they neither expand `~` nor append `.exe`.
Use the absolute path with `.exe` in JSON (and escape backslashes as `\\`).

```bash
# Windows PowerShell
$eling = "$env:USERPROFILE\.local\bin\eling-backend.exe"; Test-Path $eling
# POSIX
command -v eling-backend || ls -l "$HOME/.local/bin/eling-backend"
```

## Step 1 — Install the binary

```powershell
# Windows PowerShell
irm https://raw.githubusercontent.com/azhe403/eling-agent/main/scripts/install.ps1 | iex
```

```cmd
REM Windows cmd.exe
powershell -ExecutionPolicy Bypass -c "irm https://raw.githubusercontent.com/azhe403/eling-agent/main/scripts/install.ps1 | iex"
```

```bash
# Linux / macOS
curl -fsSL https://raw.githubusercontent.com/azhe403/eling-agent/main/scripts/install.sh | bash
```

The installer fetches the latest stable release, falling back to the latest pre-release when no stable exists.
It drops the binary in the user-local bin and adds that dir to `PATH`.

## Step 2 — Detect the host

Prefer the host the agent is currently running in. If that is unknown, pick the host whose config file exists; if several match, ask the user.

| Host | Global config | Project config | Detect |
|---|---|---|---|
| OpenCode | `~/.config/opencode/opencode.json` | `<repo>/opencode.json` | file exists; agent is OpenCode |
| Claude Code | `~/.claude.json` | `<repo>/.mcp.json` | `claude` CLI on PATH, or `.mcp.json` |
| Claude Desktop | `%APPDATA%\Claude\claude_desktop_config.json` (Windows) / `~/Library/Application Support/Claude/claude_desktop_config.json` (macOS) | — | file exists |
| Antigravity | `~/.gemini/config/mcp_config.json` | `<repo>/.agents/mcp_config.json` | file exists |
| Cursor | `~/.cursor/mcp.json` | `<repo>/.cursor/mcp.json` | file/dir exists |
| Codex | `~/.codex/config.toml` | — | file exists |

## Step 3 — Register the MCP server

### OpenCode

`~/.config/opencode/opencode.json` (valid JSON — no comments):

```json
{
  "mcp": {
    "eling": {
      "type": "local",
      "command": ["eling-backend"],
      "enabled": true
    }
  }
}
```

- `eling-backend` resolves from `PATH` on both OSes. If the host cannot find it, use the absolute path from Step 0.
- Windows absolute form: `"command": ["C:\\Users\\<you>\\.local\\bin\\eling-backend.exe"]`.
- Merge into the existing `"mcp"` object; keep other servers untouched.

### Claude Code

```bash
# user scope (default) — available in every project
claude mcp add eling -- eling-backend
# shared with the team — writes <repo>/.mcp.json (commit it)
claude mcp add --scope project eling -- eling-backend
claude mcp list   # verify
```

Or edit `<repo>/.mcp.json` directly:

```json
{ "mcpServers": { "eling": { "command": "eling-backend" } } }
```

### Claude Desktop

Edit `claude_desktop_config.json`, then restart Claude Desktop:

```json
{
  "mcpServers": {
    "eling": { "command": "/absolute/path/to/eling-backend" }
  }
}
```

On Windows use the `.exe` path with escaped backslashes, e.g.
`"command": "C:\\Users\\<you>\\.local\\bin\\eling-backend.exe"`.

### Antigravity

Edit `mcp_config.json` — global (`~/.gemini/config/mcp_config.json`) or workspace (`<repo>/.agents/mcp_config.json`).
In the IDE: agent panel `…` → MCP Servers → Manage MCP Servers → View raw config.

```json
{
  "mcpServers": {
    "eling": { "command": "eling-backend" }
  }
}
```

### Cursor

`~/.cursor/mcp.json` (global) or `<repo>/.cursor/mcp.json` (project):

```json
{
  "mcpServers": {
    "eling": { "command": "eling-backend" }
  }
}
```

### Codex

`~/.codex/config.toml`:

```toml
[mcp_servers.eling]
command = "eling-backend"
```

> Cursor and Codex paths can vary by version. If the file does not exist, check that host's MCP docs before creating it.

## Step 4 — Verify

```bash
# 1. Binary reachable (hosts that spawn without a shell need the absolute path)
eling-backend --version 2>/dev/null || command -v eling-backend

# 2. MCP stdio handshake — must answer with a JSON-RPC initialize result
echo '{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-03-26","capabilities":{},"clientInfo":{"name":"setup","version":"1.0"}}}' | eling-backend

# 3. Dashboard / REST (same port serves UI + API) — only if a runtime owns 4317
curl http://127.0.0.1:4317/health
```

Then have the user **restart or reload the host** so it picks up the new MCP server, and confirm the tools appear
(`eling*` tools such as `memory_recall`, `memory_save`, `memory_search`).

## Step 5 — Project scope (consent required)

Project memories live in `<repo>/.eling/memories/*.md` and are meant to be committed to Git.
Never create `.eling` silently — ask first:

> "Do you want Eling project memory initialized here (`.eling/memories/`, committed to Git)? It keeps this
> repo's memories with the code."

On approval only, call the MCP tool `memory_init_project`. If the user declines, stop — global memory still works.

## Step 6 — Report

```text
Eling install complete
  binary:  ~/.local/bin/eling-backend(.exe)
  host:    <OpenCode | Claude Code | Claude Desktop | Antigravity | Cursor | Codex>
  config:  <path that changed>
  mcp:     registered (needs host restart)
  project: <initialized | skipped (user declined | not asked)>
```

## Merge rules

- **JSON hosts** (OpenCode, Claude Code `.mcp.json`, Claude Desktop, Antigravity, Cursor): keep the file valid JSON, preserve all existing keys, update `eling` in place.
- **TOML host** (Codex): add or update only the `[mcp_servers.eling]` table.
- **Idempotent**: re-running setup must not create a second `eling` entry or duplicate `mcpServers`.
- **Windows**: absolute `.exe` path, backslashes escaped (`\\`).
- **No comments** inside `.json` config files — strip any `//` when merging.

## Troubleshooting

- **Host cannot start the server** → on Windows use the absolute `.exe` path; hosts often spawn without a shell.
- **Tools do not appear after editing config** → restart the host; some hosts cache config at startup.
- **`address already in use 127.0.0.1:4317`** → another `eling-backend` owns the port; the second instance runs MCP-only. Probe with `Test-NetConnection 127.0.0.1 -Port 4317` or `ss -tlnp | grep 4317`.
- **`eling-backend` still not found after install** → the `PATH` change only applies to new terminals; open a fresh shell or call the absolute path.
- **Config parse error** → remove comments / trailing commas and re-validate the JSON.
