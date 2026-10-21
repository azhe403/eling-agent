#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Shared rules for claiming, measuring and reclaiming Eling output roots.

.DESCRIPTION
    Every build in this repo writes into a folder named after ELING_OUTPUT_ROOT
    (see Directory.Build.props). Those folders are disposable by construction —
    they hold MSBuild output, never source — but "disposable" only helps if
    something actually removes them, and it only stays safe if nothing removes a
    folder that is still being written to.

    This module holds those rules once, so the two entry points that need them
    cannot drift apart:

      scripts/clean-bin.ps1     the CLI a human runs;
      scripts/test-backend.ps1  the sweep a scripted test run performs before
                                it starts.

    Two parties need one shared definition of "in use":

      - the process that OWNS a root (scripts/test-backend.ps1,
        scripts/validate-eling.ps1) writes a claim file into it, so a sweep that
        happens to run mid-flight leaves it alone;
      - the process that SWEEPS roots checks that claim first, and falls back to
        the age of the newest file inside the root for runs that never wrote a
        claim: a bare `dotnet test`, an agent session, a crashed run.

    The claim is one small JSON file, .eling-run-claim, holding the owning pid
    and the UTC timestamp the claim was taken. A claim older than $ClaimMinutes
    is treated as abandoned: the process that took it is gone, and the folder is
    garbage like any other.

    The module is dot-sourced, so it must stay side-effect free at load time:
    only function definitions, no state changes, no script-level execution.

.EXAMPLE
    . "$PSScriptRoot/lib/output-root.ps1"
    New-OutputRootClaim -Root $root
#>

$script:OutputRootClaimFileName = ".eling-run-claim"

# Roots that belong to a process that is expected to be running. They are never
# reclaimed: deleting one out from under a live dev server or MCP host breaks
# the process using it, not just the build.
$script:OutputRootLiveNames = @(".bin", ".bin-opencode", ".bin-vscode", ".bin-publish", ".bin-push")

function Get-OutputRootClaimPath {
    <#
    .SYNOPSIS
        Path of the claim file that lives inside an output root.
    #>
    param(
        [Parameter(Mandatory = $true)]
        [string]$Root
    )

    return (Join-Path $Root $script:OutputRootClaimFileName)
}

function New-OutputRootClaim {
    <#
    .SYNOPSIS
        Claim an output root for the current process, so a concurrent sweep skips it.
    .DESCRIPTION
        Creates the root if it does not exist yet, then writes the claim file.
        The claim records the owning pid and the UTC time it was taken; the
        sweeper treats a claim older than its claim window as abandoned.
    #>
    param(
        [Parameter(Mandatory = $true)]
        [string]$Root
    )

    if (-not (Test-Path -LiteralPath $Root -PathType Container)) {
        New-Item -ItemType Directory -Path $Root -Force | Out-Null
    }

    $payload = @{
        pid       = $PID
        startedAt = [DateTime]::UtcNow.ToString("o")
    } | ConvertTo-Json

    Set-Content -LiteralPath (Get-OutputRootClaimPath -Root $Root) -Value $payload -Encoding utf8
}

function Remove-OutputRootClaim {
    <#
    .SYNOPSIS
        Release a previously claimed output root.
    #>
    param(
        [Parameter(Mandatory = $true)]
        [string]$Root
    )

    $path = Get-OutputRootClaimPath -Root $Root
    if (Test-Path -LiteralPath $path) {
        Remove-Item -LiteralPath $path -Force -ErrorAction SilentlyContinue
    }
}

function Get-OutputRootClaimAgeMinutes {
    <#
    .SYNOPSIS
        Age of an output root's claim in minutes, or $null when there is none.
    #>
    param(
        [Parameter(Mandatory = $true)]
        [string]$Root
    )

    $path = Get-OutputRootClaimPath -Root $Root
    if (-not (Test-Path -LiteralPath $path)) {
        return $null
    }

    try {
        $claim = Get-Content -LiteralPath $path -Raw | ConvertFrom-Json
        $started = [DateTimeOffset]::Parse([string]$claim.startedAt).UtcDateTime
        return [math]::Round(((Get-Date).ToUniversalTime() - $started).TotalMinutes, 1)
    }
    catch {
        # An unreadable or truncated claim is treated as no claim at all.
        return $null
    }
}

function Get-OutputRootStats {
    <#
    .SYNOPSIS
        Measure an output root in a single directory walk.
    .DESCRIPTION
        Returns the total byte size, the file count and the newest file write
        time in UTC. The newest write time is what tells a sweeper whether the
        folder is still being written to, so it has to come from the files and
        not from the folder's own timestamp (which changes when entries are
        added or removed, not when their content is rewritten).
    #>
    param(
        [Parameter(Mandatory = $true)]
        [string]$Root
    )

    $bytes = 0L
    $count = 0
    $newest = $null

    if (Test-Path -LiteralPath $Root -PathType Container) {
        foreach ($file in (Get-ChildItem -LiteralPath $Root -Recurse -Force -File -ErrorAction SilentlyContinue)) {
            $bytes += $file.Length
            $count++
            if ($null -eq $newest -or $file.LastWriteTimeUtc -gt $newest) {
                $newest = $file.LastWriteTimeUtc
            }
        }
    }

    return [PSCustomObject]@{
        Bytes          = $bytes
        FileCount      = $count
        NewestWriteUtc = $newest
    }
}

function Get-OutputRootState {
    <#
    .SYNOPSIS
        Measure an output root and say whether it is still in use.
    .DESCRIPTION
        Returns the size, the file count, the newest write time, the age of any
        claim, and a short activity reason. The reason is $null exactly when the
        root is idle — nothing writing to it and nothing waiting on it — and
        that is the only case in which reclaiming it cannot break a live
        process.

        Two signals keep a root alive:

          - a live claim: a claim file taken less than $ClaimMinutes ago. This
            covers runs the repo controls, including long ones whose build
            phase stopped writing files minutes before the test phase began.
          - recent writes: a file inside the root written less than
            $IdleMinutes ago. This covers runs nobody claimed, where the only
            evidence of activity is the output itself.

        One directory walk answers all of it, so the sweeper never walks a
        multi-gigabyte tree twice.
    #>
    param(
        [Parameter(Mandatory = $true)]
        [string]$Root,
        [int]$IdleMinutes = 60,
        [int]$ClaimMinutes = 120
    )

    $stats = Get-OutputRootStats -Root $Root
    $claimAge = Get-OutputRootClaimAgeMinutes -Root $Root
    $activity = $null

    if ($null -ne $claimAge -and $claimAge -lt $ClaimMinutes) {
        $activity = "claimed $claimAge min ago"
    }
    elseif ($null -ne $stats.NewestWriteUtc) {
        $idleFor = [math]::Round(((Get-Date).ToUniversalTime() - $stats.NewestWriteUtc).TotalMinutes, 0)
        if ($idleFor -lt $IdleMinutes) {
            $activity = "written $idleFor min ago"
        }
    }

    return [PSCustomObject]@{
        Bytes           = $stats.Bytes
        FileCount       = $stats.FileCount
        NewestWriteUtc  = $stats.NewestWriteUtc
        ClaimAgeMinutes = $claimAge
        Activity        = $activity
        IsIdle          = ($null -eq $activity)
    }
}

function Test-OutputRootIdle {
    <#
    .SYNOPSIS
        Decide whether an output root is free to be reclaimed.
    #>
    param(
        [Parameter(Mandatory = $true)]
        [string]$Root,
        [int]$IdleMinutes = 60,
        [int]$ClaimMinutes = 120
    )

    $state = Get-OutputRootState -Root $Root -IdleMinutes $IdleMinutes -ClaimMinutes $ClaimMinutes
    return $state.IsIdle
}

function Test-IsOutputRootName {
    <#
    .SYNOPSIS
        True when a folder name is shaped like an Eling build output root.
    .DESCRIPTION
        Two shapes are recognised:

          .bin-*     the dash-separated family ELING_OUTPUT_ROOT produces:
                     .bin-test, .bin-test-<id>, .bin-agent, .bin-hook-<pid>,
                     .bin-check-isolated, .bin-verify-<n> ...
          .bin.bak*  the dot-separated backup family: .bin.bak-<date> and
                     friends. A backup is a copy of an output root, so it is as
                     disposable as the root it was copied from — but its dot
                     separator means the dash pattern never matched it, which
                     is how 805 MB of .bin.bak-20261010 survived every sweep.
    #>
    param(
        [Parameter(Mandatory = $true)]
        [string]$Name
    )

    if ($Name -like ".bin-*") { return $true }
    if ($Name -like ".bin.bak*") { return $true }
    return $false
}

function Get-OutputRootLiveNames {
    <#
    .SYNOPSIS
        The output roots that back a live process and are never reclaimed.
    #>
    return $script:OutputRootLiveNames
}

function Get-OutputRootCandidates {
    <#
    .SYNOPSIS
        The output roots under a repository root that may be reclaimed.
    .DESCRIPTION
        Root-level directories only, excluding the live roots and anything the
        caller asked to keep.
    #>
    param(
        [Parameter(Mandatory = $true)]
        [string]$Root,
        [string[]]$Keep = @()
    )

    $protected = $script:OutputRootLiveNames + $Keep

    return @(
        Get-ChildItem -LiteralPath $Root -Force -Directory |
            Where-Object { (Test-IsOutputRootName -Name $_.Name) -and ($protected -notcontains $_.Name) } |
            Sort-Object Name
    )
}

function Remove-OutputRootFolder {
    <#
    .SYNOPSIS
        Delete one output root, and confirm it is actually gone.
    .DESCRIPTION
        Remove-Item can report success while a folder it could not fully clear
        survives, so the directory is checked afterwards instead of trusting
        the return value.
    #>
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path
    )

    try {
        Remove-Item -LiteralPath $Path -Recurse -Force -ErrorAction Stop
    }
    catch {
        return [PSCustomObject]@{
            Removed = $false
            Error   = $_.Exception.Message
        }
    }

    if (Test-Path -LiteralPath $Path) {
        return [PSCustomObject]@{
            Removed = $false
            Error   = "removal reported success but the directory still exists"
        }
    }

    return [PSCustomObject]@{
        Removed = $true
        Error   = $null
    }
}

function Invoke-OutputRootSweep {
    <#
    .SYNOPSIS
        Reclaim the output roots under a repository root that nothing is using.
    .DESCRIPTION
        A root is reclaimed when it matches an output-root shape, is not on the
        protected list, and looks idle: no live claim and no write inside it
        for $IdleMinutes. -Force skips the idle check.

        The result object reports what happened so a caller can decide what to
        do about it: a root that could not be removed is a failure, a root that
        was skipped because it looked busy is not.
    #>
    param(
        [Parameter(Mandatory = $true)]
        [string]$Root,
        [string[]]$Keep = @(),
        [int]$IdleMinutes = 60,
        [switch]$DryRun,
        [switch]$Force,
        [switch]$Quiet
    )

    $removed = @()
    $skipped = @()
    $failed = @()
    $bytes = 0L

    foreach ($dir in (Get-OutputRootCandidates -Root $Root -Keep $Keep)) {
        $state = Get-OutputRootState -Root $dir.FullName -IdleMinutes $IdleMinutes

        if (-not $Force -and -not $state.IsIdle) {
            $skipped += "$($dir.Name): $($state.Activity)"
            if (-not $Quiet) {
                Write-Host ("  skipped       {0,-28} {1,8:N1} MB  ({2})" -f $dir.Name, ($state.Bytes / 1MB), $state.Activity)
            }
            continue
        }

        if ($DryRun) {
            $removed += $dir.Name
            $bytes += $state.Bytes
            if (-not $Quiet) {
                Write-Host ("  would remove  {0,-28} {1,8:N1} MB" -f $dir.Name, ($state.Bytes / 1MB))
            }
            continue
        }

        $result = Remove-OutputRootFolder -Path $dir.FullName
        if (-not $result.Removed) {
            $failed += "$($dir.Name): $($result.Error)"
            if (-not $Quiet) {
                Write-Host ("  FAILED        {0,-28} {1}" -f $dir.Name, $result.Error) -ForegroundColor Yellow
            }
            continue
        }

        $removed += $dir.Name
        $bytes += $state.Bytes
        if (-not $Quiet) {
            Write-Host ("  removed       {0,-28} {1,8:N1} MB" -f $dir.Name, ($state.Bytes / 1MB))
        }
    }

    return [PSCustomObject]@{
        Removed = $removed
        Skipped = $skipped
        Failed  = $failed
        Bytes   = $bytes
    }
}
