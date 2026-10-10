# Eling Agent Workspace Instructions

## Dev Servers
Backend dev (`eling_dev`) → **4417**; Frontend → **4427** (proxy `/api/*` → 4417).

## Install from URL
- When the user prompts `install <github-url>` (e.g. install https://github.com/azhe403/eling-agent), do NOT clone the repo for exploration. Run the one-line installer for the current OS from README/INSTALL.md (stable first, pre-release fallback). Clone only when the user explicitly asks to build from source or contribute.

## Memory & Conventions
- All memory operations and context hydration use Eling MCP tools (`eling_dev_*` / `eling_*`).

## Weekly NuGet Check (every prompt)
- Read `NUGET-WEEKLY-CHECK.md` at the start of every session. If its "Last run" is **7+ days old** (or absent), the check is due: run `dotnet list package --outdated --include-prerelease` and update "Last run".
- Bump scope is **patch + minor only, stable releases, same major**. Skip major bumps, RCs, and CI/dev builds.
- That file is a provisional stand-in for an Eling memory that could not be written (wedged MCP link). Once opencode is restarted, migrate it to Eling memory, then delete the file and this section.
