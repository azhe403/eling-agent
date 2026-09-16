# Design: Flat 4-Bin Output Layout (Lockfree per Use-Case)

Date: 2026-08-30
Status: Draft
Scope: Backend .NET build/test/MCP configurations

## Problem

Today the Eling backend has four execution modes that each land their build
output in a different place:

| Mode | Command (today) | Output path | Layout |
|---|---|---|---|
| Dev loop (default) | `dotnet build Eling.slnx` | `.bin\Debug\net10.0\` | Flat |
| VS Code MCP | `dotnet watch --artifacts-path .bin-vscode` | `.bin-vscode\bin\Eling.Host\debug\` | Artifacts (`bin\<Project>\<Config>\`) |
| OpenCode MCP | `dotnet watch --artifacts-path .bin-opencode` | `.bin-opencode\bin\Eling.Host\debug\` | Artifacts |
| Test runner | `dotnet test --artifacts-path .bin-test` | `.bin-test\bin\<Project>\<debug>\` | Artifacts |

Three modes use MSBuild's artifact-pipeline layout (`bin\<Project>\<Config>\`)
because `--artifacts-path` is set. Artifact layout puts `eling.exe` and
`Eling.Mcp.dll` under `.bin-opencode\bin\Eling.Host\debug\` but
`Eling.Dashboard.csproj`'s output (`eling-dashboard.exe`,
`Eling.Dashboard.dll`, `eling-dashboard-ui/`) under a sibling folder
`.bin-opencode\bin\Eling.Dashboard\debug\`.

This breaks `SpawnDashboard()` in `Eling.Host/Program.cs` when launched from
`.bin-opencode\`: the dashboard exe is not beside the host exe, so the runtime
must either fall back to `dotnet run --no-build` from the csproj (which writes
to `.bin\Debug\net10.0\` — the default path) or fail. Neither outcome mirrors
staging, where all exes sit in one publish folder.

The artifact-pipeline layout was originally adopted to avoid file locks between
`dotnet run` and `dotnet test` running concurrently. That goal is still valid,
but the implementation breaks staging fidelity.

## Goal

Each of the four modes writes to its own isolated output folder with a
**flat** layout: `eling.exe`, `eling-dashboard.exe`, `Eling.Mcp.dll`,
`Eling.Application.dll`, `Eling.Core.dll`, and `eling-dashboard-ui/` all sit
beside each other inside the same folder. Four such folders exist, one per
mode, and each one is lock-free with respect to the others.

### Principles

- Lockfree by construction: no two processes ever write to the same folder.
- Default safe: `dotnet build Eling.slnx` with no env var still works,
  writing to `.bin\Debug\net10.0\` exactly like today.
- Backward compatible: existing flags (`-c Release`, `-r win-x64`, etc.) keep
  working; only the layout-driving flag (`--artifacts-path`) is removed from
  the four dev/test/MCP entry points.
- Staging publish pipeline (`publish-global.ps1` / `publish-global.sh`) is
  untouched: it publishes to `%TEMP%\eling-publish-artifacts` and `dotnet
  publish` already produces a flat `PublishDir`.

## Target layout

Each of the four bins looks like this internally:

```
<root>\<Configuration>\<TargetFramework>\
├── eling.exe                       (Eling.Host output)
├── eling.dll
├── Eling.Mcp.dll
├── Eling.Application.dll
├── Eling.Core.dll
├── eling.runtimeconfig.json
├── eling-dashboard.exe             (Eling.Dashboard output)
├── eling-dashboard.dll
├── eling-dashboard-ui\             (frontend bundle copied by BuildDashboard target)
├── Microsoft.Extensions.*.dll
└── (all transitive runtime deps)
```

The four bins:

| Folder | Driven by | What writes here |
|---|---|---|
| `.bin\Debug\net10.0\` | Default (no env var) | `dotnet build`, manual `dotnet eling.exe` |
| `.bin-vscode\Debug\net10.0\` | `ELING_OUTPUT_ROOT=.bin-vscode` | VS Code MCP `dotnet watch` |
| `.bin-opencode\Debug\net10.0\` | `ELING_OUTPUT_ROOT=.bin-opencode` | OpenCode MCP `dotnet watch` |
| `.bin-test\Debug\net10.0\` | `ELING_OUTPUT_ROOT=.bin-test` | `dotnet test`, `validate-eling.ps1` |

## Design

### 1. Drive OutputPath from `ELING_OUTPUT_ROOT`

`Directory.Build.props` reads an environment variable `ELING_OUTPUT_ROOT` and
uses its value as the root for `<OutputPath>`. If the env var is unset, the
default is `.bin` (same as today).

```xml
<Project>
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <Version>0.1.0</Version>

    <!-- Read ELING_OUTPUT_ROOT env var via property function.
         Default to .bin when unset so `dotnet build` works with no setup. -->
    <ElingOutputRoot Condition="'$(ElingOutputRoot)' == ''">$([System.Environment]::GetEnvironmentVariable('ELING_OUTPUT_ROOT'))</ElingOutputRoot>
    <ElingOutputRoot Condition="'$(ElingOutputRoot)' == ''">.bin</ElingOutputRoot>

    <!-- Flat layout: all projects in the solution land in the same folder,
         matching staging publish output. -->
    <OutputPath>$(ElingOutputRoot)\$(Configuration)\$(TargetFramework)\</OutputPath>

    <NuGetAudit>false</NuGetAudit>
  </PropertyGroup>
</Project>
```

Notes:
- `$([System.Environment]::GetEnvironmentVariable(...))` returns an empty string when the
  variable is unset; the second `Condition` covers that empty-string case.
- Setting `<OutputPath>` unconditionally (no `ArtifactsPath` guard) means
  `--artifacts-path` is no longer needed for these modes; the three MCP / test
  entry points drop the flag.
- The `<NuGetAudit>false</NuGetAudit>` and comment about MCP stdout JSON-RPC
  purity are preserved.

### 2. MCP configuration files set the env var

**`opencode.json`** — drop `--artifacts-path`, add env var:

```json
{
  "$schema": "https://opencode.ai/config.json",
  "mcp": {
    "eling_dev": {
      "enabled": true,
      "type": "local",
      "timeout": 60000,
      "command": [
        "dotnet",
        "watch",
        "--project",
        "src/backend/Eling.Host/Eling.Host.csproj"
      ],
      "environment": {
        "ELING_OUTPUT_ROOT": ".bin-opencode",
        "ELING_DASHBOARD_PORT": "4417",
        "ELING_WATCH_DASHBOARD": "1"
      }
    },
    "shadcn": { "type": "local", "command": ["npx", "shadcn@latest", "mcp"], "enabled": true },
    "next-devtools": { "type": "local", "command": ["npx", "-y", "@vercel/next-devtools-mcp"], "enabled": true }
  }
}
```

**`.vscode/mcp.json`** — drop `--artifacts-path`, add env via options:

```json
{
  "servers": {
    "eling_dev": {
      "type": "stdio",
      "command": "dotnet",
      "args": [
        "watch",
        "--project",
        "src/backend/Eling.Host/Eling.Host.csproj"
      ],
      "options": {
        "env": {
          "ELING_OUTPUT_ROOT": ".bin-vscode"
        }
      }
    }
  },
  "inputs": []
}
```

Both files inherit `ELING_OUTPUT_ROOT` to the spawned `dotnet watch` process,
which forwards it to MSBuild during `Build`/`Watch` and to `dotnet run` child
processes that `SpawnDashboard()` may launch. `SpawnDashboard()`'s fallback
that runs `dotnet run --project ... --no-build` will see the same env var and
write to the same bin as the parent host.

### 3. SpawnDashboard() — no code change required

`src/backend/Eling.Host/Program.cs:128-180` already has a 4-branch fallback:
1. `dotnet run --project <Dashboard.csproj> --no-build` from repo root
2. `eling-dashboard.exe` paired beside `Environment.ProcessPath`
3. `dotnet exec <Eling.Dashboard.dll>` from `AppContext.BaseDirectory`
4. `dotnet exec <repo-dashboard.dll>` (historical)

With flat layout, branch 2 succeeds whenever `AppContext.BaseDirectory`
contains `eling-dashboard.exe` — which it does, because all projects now write
to the same folder. Branch 1 is still useful when running from a bare
`dotnet build` output before any host process has spawned (the `dotnet run`
child inherits `ELING_OUTPUT_ROOT` if set in the shell, so it writes to the
same bin).

The comment block at `Program.cs:135-136` ("In dev mode ... launch `dotnet
watch --project ...` so backend dashboard also hot-reloads on C# changes!")
can be tightened to note that with flat layout, `SpawnDashboard()` simply
resolves the sibling `eling-dashboard.exe` and runs it directly — no nested
watcher needed. This is purely a doc tweak, no behavior change.

### 4. Test infrastructure

**`tests/Eling.Host.Tests/TestProcesses.cs:25`** currently hard-codes:

```csharp
var testArtifact = Path.Combine(RepoRoot, ".bin-test", "bin", projectName, "debug", binaryName);
```

Update to:

```csharp
var root = System.Environment.GetEnvironmentVariable("ELING_OUTPUT_ROOT");
if (string.IsNullOrEmpty(root)) root = ".bin-test";
var testArtifact = Path.Combine(RepoRoot, root, "Debug", "net10.0", binaryName);
```

This keeps the test resilient: if a developer runs `dotnet test` with a
custom `ELING_OUTPUT_ROOT`, the test resolves to the matching bin. The default
remains `.bin-test`.

### 5. Scripts

**`scripts/test-single-mode.ps1`** (lines 6-10) — replace hard-coded path:

```powershell
$root = Split-Path (Split-Path $MyInvocation.MyCommand.Path -Parent) -Parent
$elingOutputRoot = if ($env:ELING_OUTPUT_ROOT) { $env:ELING_OUTPUT_ROOT } else { ".bin-test" }
$exe = Join-Path $root "$elingOutputRoot/Debug/net10.0/eling.exe"
if (-not (Test-Path $exe)) {
    $exe = Join-Path $root ".bin/Debug/net10.0/eling.exe"
}
```

**`scripts/validate-eling.ps1`** (lines 31-35, 151, 159) — same pattern.
Replace `$binTest = Join-Path $root ".bin-test"` block with reading
`$env:ELING_OUTPUT_ROOT` (default `.bin-test`). Replace
`--artifacts-path $binTest` in the `dotnet build` and `dotnet test` lines with
no flag (the env var alone drives layout). Update the path used at line 32:
`$binTest/bin/Eling.Host/debug/eling.exe` → `<root>/Debug/net10.0/eling.exe`
under the same env var.

### 6. AGENTS.md and README.md

**`AGENTS.md`** (lines 6-15) — update the test invocation rule. Replace
`dotnet test tests/<X>/<X>.csproj --artifacts-path .bin-test` with a shell
prefix that sets the env var. Two acceptable forms:

- Inline env: `ELING_OUTPUT_ROOT=.bin-test dotnet test tests/<X>/<X>.csproj`
- PowerShell-friendly: `$env:ELING_OUTPUT_ROOT = ".bin-test"; dotnet test tests/<X>/<X>.csproj`

The rule about "one project at a time, never batched" stays. The
"isolated `.bin-test`" wording updates to "isolated bin driven by
`ELING_OUTPUT_ROOT` (defaults to `.bin-test`)".

**`README.md`** (line 68) — same update for the documented test command.

**`package.json`** (lines 12-14) — `validate:backend` and `test:backend` use
`--artifacts-path .bin-test`. Replace with cross-platform env:
- `"validate:backend": "cross-env ELING_OUTPUT_ROOT=.bin-test dotnet restore Eling.slnx"`
- `"test:backend": "cross-env ELING_OUTPUT_ROOT=.bin-test dotnet test Eling.slnx --no-build"`

Add `cross-env` to devDependencies if not present. (Alternative without a new
dep: a small Node script that spawns the dotnet process with the env set.)

### 7. .gitignore

Add `.bin-vscode/` and `.bin-opencode/` alongside the existing `.bin/` and
`.bin-test/` entries.

### 8. publish-global.ps1 / publish-global.sh — no change

These scripts target `dotnet publish`, which has its own `PublishDir` and
layout. `ELING_OUTPUT_ROOT` is a build-time concern; `dotnet publish` reads
it too (since it does an implicit build first), but it then writes to `PublishDir`
flatly anyway. The temp artifact path (`%TEMP%\eling-publish-artifacts`) is
already isolated from the four dev bins. No edits required, and the smoke
test in those scripts runs against `~/.local/bin`, which is independent.

### 9. Edge cases

- **Env var unset**: defaults to `.bin`. `dotnet build` works without setup.
- **Env var set to nonexistent path**: MSBuild creates the directory tree.
- **Two parallel `dotnet build` Debug sessions**: still lock because they
  both default to `.bin`. Mitigation: run them with `ELING_OUTPUT_ROOT`
  pointing at different bins. Same caveat as before this design.
- **Rider**: Rider by default uses standard layout (no `--artifacts-path`),
  so it lands in `.bin\Debug\net10.0\` — sharing with `dotnet build`. If a
  developer wants Rider and CLI builds isolated, set
  `ELING_OUTPUT_ROOT=.bin-rider` in Rider's launchSettings or environment
  file. Out of scope for this design; documented as known limitation.
- **`dotnet build` with `-c Release`**: writes to `.bin\Release\net10.0\`
  (or `ELING_OUTPUT_ROOT\Release\net10.0\`). Flat. Matches staging.
- **`dotnet watch` reuses obj/ cache**: `obj/` stays per-project
  (`obj/<Project>/<Config>/<Framework>/`) regardless of `ELING_OUTPUT_ROOT`,
  so each project's incremental cache is independent. No regression.

### 10. Testing strategy

After implementation:

1. **Smoke each bin**:
   - `dotnet build Eling.slnx` → verify `.bin\Debug\net10.0\eling.exe`,
     `eling-dashboard.exe`, `Eling.Mcp.dll`, `eling-dashboard-ui\` exist.
   - `ELING_OUTPUT_ROOT=.bin-vscode dotnet watch --project src/backend/Eling.Host/Eling.Host.csproj`
     (kill after first build) → verify `.bin-vscode\Debug\net10.0\` has the
     same set of files.
   - Same for `.bin-opencode`.
   - `ELING_OUTPUT_ROOT=.bin-test dotnet test tests/Eling.Core.Tests/Eling.Core.Tests.csproj`
     → verify `.bin-test\Debug\net10.0\` has test + host exes and all tests
     pass.

2. **End-to-end host spawns dashboard**:
   - Start `dotnet .bin\Debug\net10.0\eling.exe` and confirm
     `SpawnDashboard()` resolves the sibling `eling-dashboard.exe` (no
     `dotnet run --no-build` log spam).
   - Start the VS Code MCP entry point and confirm the same.

3. **`validate-eling.ps1 -RuntimeOnly`**:
   - After implementation, the script must find the host exe at the new
     flat path. Run it and confirm the dashboard HTTP API phase and stdio
     MCP phase still pass.

4. **Regression check**:
   - `publish-global.ps1` smoke test still passes (no change to that script,
     but verify).

## Files to modify

| File | Change |
|---|---|
| `Directory.Build.props` | Add `ElingOutputRoot` from env, unconditional `<OutputPath>` |
| `opencode.json` | Drop `--artifacts-path`, add `ELING_OUTPUT_ROOT` to env |
| `.vscode/mcp.json` | Drop `--artifacts-path`, add env via `options.env` |
| `tests/Eling.Host.Tests/TestProcesses.cs` | Resolve exe path from `ELING_OUTPUT_ROOT` (default `.bin-test`) |
| `scripts/test-single-mode.ps1` | Resolve exe path from `$env:ELING_OUTPUT_ROOT` |
| `scripts/validate-eling.ps1` | Same, plus drop `--artifacts-path $binTest` from build/test calls |
| `AGENTS.md` | Update test rule: env var instead of `--artifacts-path` |
| `README.md` | Update documented test command |
| `package.json` | Update `validate:backend` and `test:backend` scripts to use env var |
| `.gitignore` | Add `.bin-vscode/`, `.bin-opencode/` |

## Out of scope

- Rider integration beyond noting the `ELING_OUTPUT_ROOT=.bin-rider` workaround.
- Consolidating all four bins into a single bin with subfolders (the user
  explicitly chose four separate bins).
- Removing the `artifacts/` legacy folder (cache from older solution
  structure; not part of `Eling.slnx`).
- Changing `Eling.Dashboard.csproj`'s `BuildDashboard` target (already writes
  `eling-dashboard-ui/` to `$(OutputPath)`, which now points at the right
  flat location automatically).

## Success criteria

1. All four bins end up with identical internal layout: `eling.exe`,
   `eling-dashboard.exe`, `Eling.Mcp.dll`, `Eling.Application.dll`,
   `Eling.Core.dll`, `eling-dashboard-ui\` all in one folder.
2. `SpawnDashboard()` resolves the sibling `eling-dashboard.exe` directly
   from the host's `AppContext.BaseDirectory` — no nested `dotnet run`
   needed.
3. `dotnet build Eling.slnx` (no flags) still works and produces
   `.bin\Debug\net10.0\` exactly as today.
4. `dotnet test`, `dotnet watch`, `validate-eling.ps1`,
   `test-single-mode.ps1` all use `ELING_OUTPUT_ROOT` instead of
   `--artifacts-path`.
5. `publish-global.ps1` / `.sh` unchanged; smoke test still passes.