# Eling Releases

How GitHub releases are built, what each asset contains, and which one to download.

## Channels

| Channel | Trigger | Tag | Assets |
|---|---|---|---|
| Stable | Push tag `v*` | `v0.2.0`, ... | mix + backend-only + dashboard UI |
| Pre-release | Push to `main` touching a build-relevant path | `v0.1.0-pre.{run_number}` (unique per build, easy rollback) | same three assets as stable |

The pre-release workflow runs only when the changed files are build-relevant (see below), so a push or pull request that touches only memory, docs, scripts, tests, or root metadata builds nothing and publishes nothing. Stable releases are tag-triggered and not path-filtered.

Pull requests only run the build matrix as validation (no release published).
`[skip-ci]` in the commit message skips CI entirely.

Base product version lives in `VERSION` (`pre-release.yml`), kept in sync with `<Version>` in `Directory.Build.props`.

## When CI runs (path filter)

`pre-release.yml` runs only when at least one changed file matches one of these paths:

| Path | Why it is build-relevant |
|---|---|
| `src/**` | Desktop, backend, and dashboard UI source |
| `Directory.Build.props` | Shared MSBuild version/properties |
| `package.json` | Frontend dependency manifest (the dashboard UI is bundled into the backend) |
| `pnpm-lock.yaml` | Frontend dependency lockfile |
| `.github/workflows/**` | CI definitions — a workflow change rebuilds so the new workflow is validated |

Every other tracked path is deliberately excluded because it never changes the published binary — most importantly `.eling/**` (memory) and `docs/**` / `**/*.md`, but also `scripts/**` (installers and dev tooling), `.husky/**`, `tests/**`, and root metadata such as `mise.toml`, `opencode.json`, `Eling.slnx`, and `.gitignore`.

This is an include list (default-deny), not an ignore list: an unlisted path can never accidentally trigger a release, which is what keeps memory and documentation commits from cutting a heavy 5-OS build. The trade-off is that a new code folder or build input added outside this list will silently stop producing releases until it is listed here.

## Assets (per stable release)

Built for 5 RIDs: `win-x64`, `linux-x64`, `linux-arm64`, `osx-x64`, `osx-arm64` (`.zip` on Windows, `.tar.gz` elsewhere).

| Asset | Contents | For |
|---|---|---|
| `eling-<rid>.zip` | Desktop + backend (merged, conflict-verified) + dashboard UI | Users who want both |
| `eling-backend-<rid>.zip` | Backend + dashboard UI | Agents / backend-only installs (what the install scripts fetch) |
| `eling-dashboard-ui.zip` / `.tar.gz` | Dashboard UI only, no RID (static files, one asset for all OS, built once on `linux-x64`) | Repairing a corrupt local UI without touching the binary |

Pre-releases attach the same three asset types as stable releases.

## How it is built (`release.yml`)

1. **Publish Desktop** — self-contained Avalonia app into `publish/`.
2. **Publish Backend** — self-contained single-file into `publish-backend/` (dashboard UI included via the `BuildDashboard` target; never skip it in CI or the web UI silently disappears from the assets).
3. **Package backend-only** — zip/tar `publish-backend/` as-is.
4. **Merge** — copy `eling-backend.*` + DLLs into `publish/` (conflicting DLLs must be byte-identical via `cmp`, else fail), then copy `eling-dashboard-ui/`.
5. **Package mix** — zip/tar `publish/`.
6. **Package dashboard UI** (`linux-x64` job only) — zip + tar.gz of the UI folder.
7. **Upload + attach** — all archives attached to the GitHub release (`dist/*`).

## Installers

```powershell
# Windows PowerShell — backend-only asset from latest stable, fallback to latest pre-release
irm https://raw.githubusercontent.com/azhe403/eling-agent/main/scripts/install.ps1 | iex
# Repair dashboard UI only
.\scripts\install.ps1 -DashboardOnly
```

```cmd
REM Windows cmd.exe — same installer via PowerShell wrapper
powershell -ExecutionPolicy Bypass -c "irm https://raw.githubusercontent.com/azhe403/eling-agent/main/scripts/install.ps1 | iex"
REM Repair dashboard UI only (from repo root)
scripts\install.bat -DashboardOnly
```

```bash
# Linux / macOS — RID auto-detected
curl -fsSL https://raw.githubusercontent.com/azhe403/eling-agent/main/scripts/install.sh | bash
# Repair dashboard UI only
./scripts/install.sh --dashboard-only
```

Install target is always user-local (`%USERPROFILE%\.local\bin` / `~/.local/bin`) — never system locations. Repair mode stops the staging backend, replaces only `eling-dashboard-ui/`, and fails loudly if `index.html` is still missing afterwards.

## Local staging (not a release)

To validate working-tree changes against the real installed binary without tagging:

```powershell
.\scripts\publish-global.ps1            # full smoke test
.\scripts\publish-global.ps1 -SkipSmokeTest
```

Publishes Release / self-contained / single-file to user-local bin and runs health + MCP round-trip smoke tests. No commits, pushes, or tags — see `INSTALL.md` Option C.
