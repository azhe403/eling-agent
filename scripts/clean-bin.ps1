#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Prune build output roots that are not part of the canonical set.

.DESCRIPTION
    Directory.Build.props drives <OutputPath> from ElingOutputRoot (default
    .bin, overridable via ELING_OUTPUT_ROOT), so every tool, test runner and MCP
    host can redirect the whole solution's output into its own folder. That is
    what keeps a running `dotnet watch` from locking files another build wants to
    copy to its output folder.

    The flip side is that any ad-hoc build can invent a brand-new root, and
    nothing in the repo owns the resulting folder. .gitignore already matches
    them with `.bin-*/`, so `git status` reports them as ignored and the
    accumulation never surfaces anywhere. That is how eight `.bin-verify-*`
    roots (~560 MB) piled up between 2026-09-25 and 2026-09-26: each retry of a
    locked build minted a fresh folder instead of reusing one.

    This script removes every root-level `.bin-*` folder that is not canonical,
    so a leftover escape hatch costs one command to reclaim instead of growing
    without bound.

    The canonical roots, and what writes to each:
      .bin           default ElingOutputRoot; plain `dotnet build`, manual runs
      .bin-test      dotnet test, validate-eling.ps1 (Directory.Build.props
                    forces this for *.Tests when ELING_OUTPUT_ROOT is unset)
      .bin-opencode  opencode.json MCP host (ELING_OUTPUT_ROOT=.bin-opencode)
      .bin-vscode    .vscode/mcp.json MCP host (ELING_OUTPUT_ROOT=.bin-vscode)
      .bin-push      scripts/ci-check.sh push verification build

    Anything else matching `.bin-*` is disposable by construction: it is MSBuild
    output, so it cannot hold source code. Use -Keep to exempt an extra root if
    a new legitimate consumer of ElingOutputRoot is added.

    `.artifacts/` is intentionally NOT matched. It is dotnet's own artifacts
    layout rather than an ElingOutputRoot, and is out of scope here.

    Delete failures are reported and force a non-zero exit code rather than
    being swallowed. Silently ignoring them is the failure mode that let the
    original pile-up go unnoticed in the first place.

.PARAMETER DryRun
    List candidates and reclaimable bytes without deleting anything.

.PARAMETER Keep
    Additional root folder names to preserve beyond the canonical set.

.EXAMPLE
    pwsh scripts/clean-bin.ps1 -DryRun

.EXAMPLE
    pwsh scripts/clean-bin.ps1

.EXAMPLE
    pwsh scripts/clean-bin.ps1 -Keep '.bin-scratch'
#>
param(
    [switch]$DryRun,
    [string[]]$Keep = @()
)

$ErrorActionPreference = "Stop"
$root = Split-Path (Split-Path $MyInvocation.MyCommand.Path -Parent) -Parent

if (-not (Test-Path -LiteralPath $root -PathType Container)) {
    Write-Error "Repository root not found: $root"
    exit 1
}

$canonical = @(".bin", ".bin-test", ".bin-opencode", ".bin-vscode", ".bin-push")
$protected = $canonical + $Keep

# Root-level `.bin-*` directories only. `.bin` itself carries no dash and is
# canonical anyway; anchoring the wildcard on ".bin-" keeps a stray folder that
# merely starts with the same letters out of the sweep.
$candidates = @(
    Get-ChildItem -LiteralPath $root -Force -Directory |
        Where-Object { $_.Name -like ".bin-*" -and $protected -notcontains $_.Name } |
        Sort-Object Name
)

if ($candidates.Count -eq 0) {
    Write-Host "No stale output roots."
    Write-Host "Canonical roots:"
    foreach ($name in $canonical) {
        $state = if (Test-Path -LiteralPath (Join-Path $root $name) -PathType Container) { "present" } else { "absent" }
        Write-Host ("  {0,-14} {1}" -f $name, $state)
    }
    exit 0
}

$bytes = 0L
$failed = @()

foreach ($dir in $candidates) {
    $size = (Get-ChildItem -LiteralPath $dir.FullName -Recurse -Force -File -ErrorAction SilentlyContinue |
        Measure-Object -Property Length -Sum).Sum
    if ($null -eq $size) { $size = 0L }

    if ($DryRun) {
        Write-Host ("  would remove  {0,-28} {1,8:N1} MB" -f $dir.Name, ($size / 1MB))
        $bytes += $size
        continue
    }

    try {
        Remove-Item -LiteralPath $dir.FullName -Recurse -Force -ErrorAction Stop
    } catch {
        $failed += "$($dir.Name): $($_.Exception.Message)"
        continue
    }

    # Remove-Item can report success while a folder it could not fully clear
    # survives, so confirm the directory is actually gone instead of trusting
    # the return value.
    if (Test-Path -LiteralPath $dir.FullName) {
        $failed += "$($dir.Name): removal reported success but the directory still exists"
        continue
    }

    $bytes += $size
    Write-Host ("  removed       {0,-28} {1,8:N1} MB" -f $dir.Name, ($size / 1MB))
}

$heading = if ($DryRun) { "Reclaimable" } else { "Freed" }
Write-Host ""
Write-Host ("{0}: {1:N1} MB across {2} folder(s)." -f $heading, ($bytes / 1MB), $candidates.Count)

if ($failed.Count -gt 0) {
    Write-Host ""
    Write-Host "Failed to remove:" -ForegroundColor Yellow
    foreach ($entry in $failed) {
        Write-Host "  $entry" -ForegroundColor Yellow
    }
    Write-Host ""
    Write-Host "Stop whatever holds those files open (dotnet watch, the eling host, a stray testhost) and re-run." -ForegroundColor Yellow
    exit 1
}

exit 0
