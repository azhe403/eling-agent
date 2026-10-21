#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Reclaim build output roots that no longer belong to a running process.

.DESCRIPTION
    Directory.Build.props drives <OutputPath> from ElingOutputRoot (default
    .bin, overridable via ELING_OUTPUT_ROOT), so every tool, test runner and MCP
    host can redirect the whole solution's output into its own folder. That is
    what keeps a running `dotnet watch` from locking files another build wants to
    copy to its output folder.

    The flip side is that any ad-hoc build can invent a brand-new root, and
    nothing in the repo owns the resulting folder. .gitignore already matches
    them with `.bin*`, so `git status` reports them as ignored and the
    accumulation never surfaces anywhere. That is how eight `.bin-verify-*`
    roots (~560 MB) piled up between 2026-09-25 and 2026-09-26: each retry of a
    locked build minted a fresh folder instead of reusing one.

    This script removes every root-level folder that matches an output-root
    shape, so a leftover escape hatch costs one command to reclaim instead of
    growing without bound. The rules live in scripts/lib/output-root.ps1, which
    the scripted test runner (scripts/test-backend.ps1) uses as well.

    Two families of names are recognised:

      .bin-*       dash-separated roots: .bin-test, .bin-test-<id>, .bin-agent,
                   .bin-hook-<pid>, .bin-check-isolated, .bin-verify-<n> ...
      .bin.bak*    dot-separated backups: .bin.bak-<date> and friends. A backup
                   is a copy of an output root, so it is as disposable as the
                   root it was copied from — but its dot separator means the
                   dash pattern never matched it, which is how 805 MB of
                   .bin.bak-20261010 survived every sweep.

    Roots that belong to a process that is expected to be running are never
    touched: .bin (dev build and manual runs), .bin-opencode and .bin-vscode
    (MCP hosts), .bin-publish (publish-global.ps1) and .bin-push (ci-check.sh).

    `.bin-test` used to be on that protected list. It is not any more: it is the
    default root for test projects, so it is exactly the folder that has to
    disappear once the run that created it is over. It is reclaimed when it is
    idle — see the activity rules below.

    A root is only reclaimed when nothing is using it. Two signals keep a live
    root alive: a claim file written by a run this repo controls
    (scripts/lib/output-root.ps1), and a file inside the root written less than
    -IdleMinutes ago, which covers runs nobody claimed (a bare `dotnet test`, an
    agent session, a crashed shell). -Force skips both checks.

    `.artifacts/` is intentionally NOT matched. It is dotnet's own artifacts
    layout rather than an ElingOutputRoot, and is out of scope here.

    Delete failures are reported and force a non-zero exit code rather than
    being swallowed. Silently ignoring them is the failure mode that let the
    original pile-up go unnoticed in the first place. Roots that are skipped
    because they are in use are NOT failures: they are the script working.

.PARAMETER DryRun
    List candidates and reclaimable bytes without deleting anything.

.PARAMETER Keep
    Additional root folder names to preserve beyond the protected set.

.PARAMETER IdleMinutes
    How long a root must sit without a write before it counts as idle. The
    default is deliberately generous: a root this repo's own scripts own is
    protected by its claim file instead, so this threshold only governs roots
    nobody claimed — a bare `dotnet test`, a build by another agent session —
    where a false positive costs a rebuild and a false negative costs disk.

.PARAMETER Force
    Reclaim matching roots even when they look busy (no claim, recent write).

.EXAMPLE
    pwsh scripts/clean-bin.ps1 -DryRun

.EXAMPLE
    pwsh scripts/clean-bin.ps1

.EXAMPLE
    pwsh scripts/clean-bin.ps1 -Keep '.bin-scratch'

.EXAMPLE
    pwsh scripts/clean-bin.ps1 -IdleMinutes 5
#>
param(
    [switch]$DryRun,
    [string[]]$Keep = @(),
    [int]$IdleMinutes = 60,
    [switch]$Force
)

$ErrorActionPreference = "Stop"
$root = Split-Path (Split-Path $MyInvocation.MyCommand.Path -Parent) -Parent
. (Join-Path $PSScriptRoot "lib/output-root.ps1")

if (-not (Test-Path -LiteralPath $root -PathType Container)) {
    Write-Error "Repository root not found: $root"
    exit 1
}

if ((Get-OutputRootCandidates -Root $root -Keep $Keep).Count -eq 0) {
    Write-Host "No stale output roots."
    Write-Host "Live roots (never reclaimed):"
    foreach ($name in (Get-OutputRootLiveNames)) {
        $state = if (Test-Path -LiteralPath (Join-Path $root $name) -PathType Container) { "present" } else { "absent" }
        Write-Host ("  {0,-14} {1}" -f $name, $state)
    }
    exit 0
}

$result = Invoke-OutputRootSweep -Root $root -Keep $Keep -IdleMinutes $IdleMinutes -DryRun:$DryRun -Force:$Force

$heading = if ($DryRun) { "Reclaimable" } else { "Freed" }
Write-Host ""
Write-Host ("{0}: {1:N1} MB across {2} folder(s)." -f $heading, ($result.Bytes / 1MB), $result.Removed.Count)

if ($result.Skipped.Count -gt 0) {
    Write-Host ""
    Write-Host "Skipped because they look busy:" -ForegroundColor Yellow
    foreach ($entry in $result.Skipped) {
        Write-Host "  $entry" -ForegroundColor Yellow
    }
    Write-Host "Re-run with -Force to reclaim them anyway." -ForegroundColor Yellow
}

if ($result.Failed.Count -gt 0) {
    Write-Host ""
    Write-Host "Failed to remove:" -ForegroundColor Yellow
    foreach ($entry in $result.Failed) {
        Write-Host "  $entry" -ForegroundColor Yellow
    }
    Write-Host ""
    Write-Host "Stop whatever holds those files open (dotnet watch, the eling host, a stray testhost) and re-run." -ForegroundColor Yellow
    exit 1
}

exit 0
