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

## C# Code Organization (enforced by `CodeOrganizationConventionTests`)
- One top-level type per file. The only exception is records grouped per area (e.g. `Dtos/*Dtos.cs`, `*Models.cs`) — never extend the test's allowlist, split the file instead.
- Records with more than one parameter are chopped one-param-per-line, never single-line.
- MCP tool files hold exactly one type: the tool class. Tool DTOs live in `Dtos/`.
- Never use tuples (`ValueTuple`) — not as return types, not for deconstruction, not as literals. Declare a named record instead (grouped per area, chopped). Legacy plumbing predates this rule and is grandfathered in the test's allowlist — never extend it, convert the file instead.

## External Commands (enforced by `CliWrapConventionTests`)
- Invoke external commands with CliWrap (`CliWrap.Cli.Wrap`) — **never** raw `Process.Start` / `ProcessStartInfo`. It handles argument escaping, output piping, timeout via a linked `CancellationTokenSource`, and process-tree kill. Copy the pattern in `Bootstrap/FrontendDevSpawner.cs`.
- Resolve the executable to an absolute path first when it may not be on `PATH`. `Cli.Wrap("git")` still delegates name resolution to the OS, so a process launched with a reduced environment (IDE, MCP server, service host) will not find it. The test's allowlist is closed — never extend it.
- Carve-outs, grandfathered and not to be migrated: `Eling.Desktop/Services/BackendSupervisor.cs` (long-lived child supervision, holds the handle it kills) and `FrontendDevSpawner.GetPidsListeningOnPort` (trivial synchronous `netstat` probe).
