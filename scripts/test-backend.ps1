#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Run Eling's test layers in an output root that disappears when the run ends.

.DESCRIPTION
    The repo builds every project into a folder named after ELING_OUTPUT_ROOT
    (Directory.Build.props). Pointing that variable at one fixed folder is what
    lets a bare `dotnet test` work, but it is also why the folder used to grow
    without bound: nothing owned it, so nothing ever removed it. Seven folders
    and ~7 GB later, the pile was invisible — .gitignore hides it and
    `git status` never mentions it.

    This script is the owner. It runs the requested layer(s) in a disposable
    root, .bin-test-<pid>-<id>, and removes that root once the run is over:

      - the run passed  -> the root is deleted, and the reclaimed size is
        reported;
      - the run failed  -> the root is kept, so the failure can still be
        inspected, and its path is printed. The next run collects it.

    That is the same disposable-root shape the git hooks already use
    (.husky/pre-commit and .husky/pre-push build under .bin-hook-<pid> and
    delete it in a trap), so a scripted test run and a hook run now behave the
    same way. It also means two runs never share an output folder, which is
    what the isolation rule in the project memory asks for.

    Before the run, leftover output roots are swept (the same rules
    scripts/clean-bin.ps1 applies): roots from earlier runs that crashed, from
    bare `dotnet test` invocations nobody claimed, and from ad-hoc builds. A
    root that is still in use is skipped, not deleted.

    When ELING_OUTPUT_ROOT is already set in the environment, that folder is
    used as-is and is NOT deleted at the end: the caller owns it. That keeps
    scripts/ci-check.sh and any CI job working unchanged.

.PARAMETER Layer
    Which layer to run: core, api, desktop, all (the three, in order) or full
    (the whole solution).

.PARAMETER KeepOutput
    Keep the output root even when the run passed.

.PARAMETER IdleMinutes
    How long a leftover output root must sit without a write before the sweep
    reclaims it.

.EXAMPLE
    pwsh scripts/test-backend.ps1 -Layer core

.EXAMPLE
    pwsh scripts/test-backend.ps1 -Layer all
#>
param(
    [ValidateSet("core", "api", "desktop", "all", "full")]
    [string]$Layer = "all",
    [switch]$KeepOutput,
    [int]$IdleMinutes = 60
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path (Split-Path $MyInvocation.MyCommand.Path -Parent) -Parent
. (Join-Path $PSScriptRoot "lib/output-root.ps1")

# ELING_OUTPUT_ROOT has to stay relative: Directory.Build.props builds
# OutputPath from it against the folder holding Directory.Build.props, so an
# absolute path would place the output outside the repository.
function New-DisposableOutputRoot {
    $suffix = [guid]::NewGuid().ToString("N").Substring(0, 6)
    return ".bin-test-$PID-$suffix"
}

function Resolve-OutputRootPath {
    <#
    .SYNOPSIS
        Absolute path of an output root, which may be relative to the repo.
    #>
    param(
        [Parameter(Mandatory = $true)]
        [string]$Root
    )

    if ([System.IO.Path]::IsPathRooted($Root)) {
        return $Root
    }
    return (Join-Path $repoRoot $Root)
}

function Invoke-Sweep {
    <#
    .SYNOPSIS
        Reclaim output roots that no run owns.
    .DESCRIPTION
        Housekeeping only: a sweep that fails must never fail the test run, so
        the failures are reported and the exit code is left alone. The rules
        live in scripts/lib/output-root.ps1 — the same module
        scripts/clean-bin.ps1 uses — so the two entry points cannot drift apart.
    #>
    $result = Invoke-OutputRootSweep -Root $repoRoot -IdleMinutes $IdleMinutes

    if ($result.Failed.Count -gt 0) {
        Write-Host "  (sweep could not reclaim: $($result.Failed -join '; '))" -ForegroundColor Yellow
    }
    elseif ($result.Removed.Count -eq 0 -and $result.Skipped.Count -eq 0) {
        Write-Host "  (nothing to reclaim)"
    }
}

function Invoke-TestLayer {
    <#
    .SYNOPSIS
        Run one test layer and report whether it passed.
    .DESCRIPTION
        Out-Host keeps the dotnet output on the console while leaving the
        function's return value a single boolean.
    #>
    param(
        [Parameter(Mandatory = $true)]
        [string]$Name,
        [Parameter(Mandatory = $true)]
        [string[]]$Arguments
    )

    Write-Host ""
    Write-Host "=== $Name ===" -ForegroundColor Cyan
    & dotnet @Arguments | Out-Host
    return ($LASTEXITCODE -eq 0)
}

function Get-LayerPlan {
    <#
    .SYNOPSIS
        The dotnet invocations that make up the requested layer.
    #>
    param(
        [Parameter(Mandatory = $true)]
        [string]$Requested
    )

    $core = @{
        Name      = "core"
        Arguments = @("test", "tests/Eling.Core.Tests/Eling.Core.Tests.csproj")
    }
    $api = @{
        Name      = "api"
        Arguments = @("test", "tests/Eling.Backend.Tests/Eling.Backend.Tests.csproj", "--filter", "FullyQualifiedName!~DashboardLifecycleTests")
    }
    $desktop = @{
        Name      = "desktop"
        Arguments = @("test", "tests/Eling.Desktop.Tests/Eling.Desktop.Tests.csproj")
    }

    switch ($Requested) {
        "core" { return @($core) }
        "api" { return @($api) }
        "desktop" { return @($desktop) }
        "full" {
            return @(@{ Name = "full"; Arguments = @("test", "Eling.slnx") })
        }
        default { return @($core, $api, $desktop) }
    }
}

# --- Resolve the output root -------------------------------------------------
$callerRoot = $env:ELING_OUTPUT_ROOT
if ([string]::IsNullOrWhiteSpace($callerRoot)) {
    $outputRoot = New-DisposableOutputRoot
    $ownsOutputRoot = $true
}
else {
    $outputRoot = $callerRoot
    $ownsOutputRoot = $false
}
$outputRootPath = Resolve-OutputRootPath -Root $outputRoot

# MSBuild reads ELING_OUTPUT_ROOT from the environment (Directory.Build.props),
# so the variable has to be set in THIS process before dotnet is started. The
# child inherits it.
$env:ELING_OUTPUT_ROOT = $outputRoot

Write-Host "output root: $outputRoot" -ForegroundColor DarkGray
if ($ownsOutputRoot) {
    Write-Host "  (disposable: removed when the run ends)" -ForegroundColor DarkGray
}
else {
    Write-Host "  (from ELING_OUTPUT_ROOT: left in place, the caller owns it)" -ForegroundColor DarkGray
}

# --- Reclaim leftovers from earlier runs -------------------------------------
Write-Host ""
Write-Host "sweeping leftover output roots" -ForegroundColor DarkGray
Invoke-Sweep

# --- Run the layers ----------------------------------------------------------
$failed = $false
$plan = Get-LayerPlan -Requested $Layer

Push-Location $repoRoot
try {
    New-OutputRootClaim -Root $outputRootPath
    foreach ($step in $plan) {
        if (-not (Invoke-TestLayer -Name $step.Name -Arguments $step.Arguments)) {
            $failed = $true
            Write-Host ""
            Write-Host "layer '$($step.Name)' FAILED" -ForegroundColor Red
            break
        }
    }
}
finally {
    Pop-Location
    Remove-OutputRootClaim -Root $outputRootPath
}

# --- Reclaim the output root -------------------------------------------------
$reclaimed = 0L
$rootRemoved = $false
if ($ownsOutputRoot -and -not $KeepOutput -and -not $failed) {
    $state = Get-OutputRootState -Root $outputRootPath
    $reclaimed = $state.Bytes
    try {
        Remove-Item -LiteralPath $outputRootPath -Recurse -Force -ErrorAction Stop
        $rootRemoved = -not (Test-Path -LiteralPath $outputRootPath)
    }
    catch {
        Write-Host ""
        Write-Host "could not remove '$outputRoot': $($_.Exception.Message)" -ForegroundColor Yellow
    }

    if ($rootRemoved) {
        Write-Host ""
        Write-Host ("removed '{0}' ({1:N1} MB reclaimed)" -f $outputRoot, ($reclaimed / 1MB)) -ForegroundColor Green
    }
    else {
        Write-Host ""
        Write-Host "'$outputRoot' is still there; the next run will collect it." -ForegroundColor Yellow
    }
}
elseif ($ownsOutputRoot -and $failed) {
    Write-Host ""
    Write-Host "'$outputRoot' kept for inspection (the run failed). The next run collects it." -ForegroundColor Yellow
}
elseif ($ownsOutputRoot -and $KeepOutput) {
    Write-Host ""
    Write-Host "'$outputRoot' kept (-KeepOutput)." -ForegroundColor Yellow
}

if ($failed) {
    exit 1
}
exit 0
