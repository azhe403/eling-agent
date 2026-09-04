#!/usr/bin/env pwsh
# ==============================================================================
# sim-mcp-stdio.ps1 — Spawn an eling-backend process and probe its MCP stdio.
#
# Mirrors what opencode does at MCP handshake time: writes an `initialize`
# JSON-RPC request to stdin, then reads the response. Reports whether the
# process actually answers or just sits there.
#
# Two intended use cases:
#   1. Owner mode  (port free)        -> confirms MCP over stdio works.
#   2. Peer  mode  (port taken)       -> confirms peer promotes / stays alive.
#
# Usage:
#   pwsh scripts/sim-mcp-stdio.ps1 -Port 4417           # peer if 4417 owned
#   pwsh scripts/sim-mcp-stdio.ps1 -Port 4499           # owner (free port)
#   pwsh scripts/sim-mcp-stdio.ps1 -Port 4417 -Keep     # don't auto-kill
#
# Optional flags:
#   -Binary   path to eling-backend dll/ exe (default: fresh build under .bin/)
#   -Timeout  ms to wait for stdio response (default: 8000)
#   -Probe    what to send after initialize (default: tools/list)
#   -Keep     leave the process running for further inspection
# ==============================================================================

[CmdletBinding()]
param(
    [int]$Port = 4499,
    [string]$Binary = "",
    [int]$Timeout = 8000,
    [string]$Probe = "tools/list",
    [switch]$Keep,
    [switch]$SeedOwner,
    [int]$HoldSeconds = 0
)

$ErrorActionPreference = "Stop"
$root = Split-Path (Split-Path $MyInvocation.MyCommand.Path -Parent) -Parent

# --- resolve binary ------------------------------------------------------------
if (-not $Binary) {
    $candidates = @(
        (Join-Path $root ".bin/Debug/net10.0/eling-backend.dll"),
        (Join-Path $root ".bin/Debug/net10.0/eling-backend.exe"),
        (Join-Path $env:USERPROFILE ".local/bin/eling-backend.exe")
    )
    foreach ($c in $candidates) {
        if (Test-Path $c) { $Binary = $c; break }
    }
}
if (-not $Binary -or -not (Test-Path $Binary)) {
    throw "No eling-backend binary found. Build first or pass -Binary."
}
Write-Host "Using binary: $Binary" -ForegroundColor DarkGray
Write-Host "Last modified: $((Get-Item $Binary).LastWriteTime)" -ForegroundColor DarkGray
Write-Host ""

# --- check if port is currently free or owned ---------------------------------
$portListening = netstat -ano -p TCP | Select-String ":$Port\s.*LISTENING"
$isOwner = [string]::IsNullOrWhiteSpace($portListening)
$mode = if ($isOwner) { "OWNER" } else { "PEER" }
Write-Host "[setup] Port $Port is $(if ($isOwner) {'FREE'} else { "TAKEN by: $portListening" }) -> $mode mode expected" -ForegroundColor Cyan

# optional: spawn a temporary owner first so the real probe is forced into PEER mode
$seedProc = $null
if ($SeedOwner -and $isOwner) {
    Write-Host "[seed] spawning temporary owner to make port $Port busy..." -ForegroundColor DarkYellow
    $seedPsi = New-Object System.Diagnostics.ProcessStartInfo
    if ($Binary.EndsWith(".dll")) {
        $seedPsi.FileName = (Get-Command dotnet).Source
        [void]$seedPsi.ArgumentList.Add($Binary)
    } else {
        $seedPsi.FileName = $Binary
    }
    $seedPsi.UseShellExecute = $false
    # CRITICAL: redirect stdin/stdout for the seed too, otherwise the MCP stdio
    # transport inherits the parent pwsh's stdin/stdout, hits EOF immediately,
    # and the seed backend self-exits. We don't read from it, just keep the
    # pipes open.
    $seedPsi.RedirectStandardInput = $true
    $seedPsi.RedirectStandardOutput = $true
    $seedPsi.RedirectStandardError = $true
    $seedPsi.CreateNoWindow = $true
    $seedPsi.EnvironmentVariables["ELING_DASHBOARD_PORT"] = "$Port"
    $seedProc = [System.Diagnostics.Process]::Start($seedPsi)
    Write-Host "[seed] owner PID=$($seedProc.Id) (will be killed at end)"
    # Drain stdout/stderr in background to prevent the pipes from filling
    # and blocking the seed backend.
    $seedDrain = $seedProc.StandardOutput.ReadToEndAsync()
    $seedErrDrain = $seedProc.StandardError.ReadToEndAsync()
    Start-Sleep -Seconds 2
    if ($seedProc.HasExited) {
        Write-Host "[seed] WARNING: seed owner exited prematurely (code $($seedProc.ExitCode))" -ForegroundColor Red
    }
    $isOwner = $false
    $mode = "PEER"
    Write-Host "[seed] -> forcing PEER mode for real probe" -ForegroundColor DarkYellow
}

# --- spawn ---------------------------------------------------------------------
$psi = New-Object System.Diagnostics.ProcessStartInfo
if ($Binary.EndsWith(".dll")) {
    $psi.FileName = (Get-Command dotnet).Source
    [void]$psi.ArgumentList.Add($Binary)
} else {
    $psi.FileName = $Binary
}
$psi.UseShellExecute = $false
$psi.RedirectStandardInput = $true
$psi.RedirectStandardOutput = $true
$psi.RedirectStandardError = $true
$psi.CreateNoWindow = $true
$psi.EnvironmentVariables["ELING_DASHBOARD_PORT"] = "$Port"

$proc = [System.Diagnostics.Process]::Start($psi)
Write-Host "[spawn] PID=$($proc.Id) at $(Get-Date -Format 'HH:mm:ss.fff')"

# give the host a moment to settle (build DI, start stdio transport, etc.)
Start-Sleep -Seconds 2

# liveness check
if ($proc.HasExited) {
    Write-Host "[FAIL] Process exited immediately with code $($proc.ExitCode)" -ForegroundColor Red
    return
}
Write-Host "[alive] After 2s: HasExited=$($proc.HasExited)" -ForegroundColor Green

# --- send JSON-RPC over stdio --------------------------------------------------
$initReq = @{
    jsonrpc = "2.0"
    id      = 1
    method  = "initialize"
    params  = @{
        protocolVersion = "2024-11-05"
        capabilities    = @{}
        clientInfo      = @{ name = "sim-mcp-stdio"; version = "1.0" }
    }
} | ConvertTo-Json -Compress
$initLine = $initReq + "`n"

$initializedNote = '{"jsonrpc":"2.0","method":"notifications/initialized"}' + "`n"
$probeReq = @{
    jsonrpc = "2.0"
    id      = 2
    method  = $Probe
    params  = @{}
} | ConvertTo-Json -Compress
$probeLine = $probeReq + "`n"

# write all three: initialize -> notification -> probe
$proc.StandardInput.Write($initLine)
$proc.StandardInput.Write($initializedNote)
$proc.StandardInput.Write($probeLine)
$proc.StandardInput.Flush()
Write-Host "[send] initialize + initialized + $Probe at $(Get-Date -Format 'HH:mm:ss.fff')"

# --- read until we see a result for id=2 (probe) or timeout --------------------
# MCP servers emit one JSON object per line. We drain lines until we see a
# `result` (or `error`) for id=2, or until timeout elapses.
$deadline = [DateTime]::UtcNow.AddMilliseconds($Timeout)
$collected = New-Object System.Collections.Generic.List[string]
$finalResult = $null
while ([DateTime]::UtcNow -lt $deadline) {
    $remaining = [int]([DateTime]::UtcNow -lt $deadline) * 0  # no-op, just clearer
    $waitMs = [int][Math]::Max(100, ($deadline - [DateTime]::UtcNow).TotalMilliseconds)
    $lineTask = $proc.StandardOutput.ReadLineAsync()
    if ($lineTask.Wait($waitMs)) {
        $line = $lineTask.Result
        if ($null -eq $line) { break }
        $collected.Add($line)
        try {
            $obj = $line | ConvertFrom-Json -ErrorAction Stop
            if ($obj.id -eq 2) {
                $finalResult = $obj
                break
            }
        } catch {
            # not JSON, keep collecting
        }
    } else {
        break
    }
}

# --- report --------------------------------------------------------------------
Write-Host ""
Write-Host "===== RESULTS =====" -ForegroundColor Cyan
Write-Host "Mode expected:        $mode"
Write-Host "Process alive:        $(-not $proc.HasExited)"

if ($finalResult) {
    Write-Host "MCP stdio:            OK" -ForegroundColor Green
    $preview = ($finalResult | ConvertTo-Json -Depth 6 -Compress)
    if ($preview.Length -gt 240) { $preview = $preview.Substring(0, 240) + "..." }
    Write-Host "Response preview:     $preview"
} else {
    Write-Host "MCP stdio:            NO RESPONSE in ${Timeout}ms" -ForegroundColor Red
    if ($collected.Count -gt 0) {
        Write-Host "Partial output:" -ForegroundColor Yellow
        $collected | ForEach-Object { Write-Host "  $_" }
    } else {
        Write-Host "No output at all — stdio transport is dead or not started." -ForegroundColor Yellow
    }
}

# --- optional: take over (kill owner of the port) -----------------------------
# Skipped by default. Enable with -Takeover to exercise the peer->owner promotion.
if ($args -contains "-Takeover") {
    Write-Host ""
    Write-Host "[takeover] killing current owner of port $Port..."
    $ownerLine = netstat -ano -p TCP | Select-String ":$Port\s.*LISTENING" | Select-Object -First 1
    if ($ownerLine) {
        $ownerPid = ($ownerLine -split '\s+')[-1]
        Write-Host "[takeover] killing PID $ownerPid"
        Stop-Process -Id $ownerPid -Force -ErrorAction SilentlyContinue
        Start-Sleep -Seconds 5
        $newOwner = netstat -ano -p TCP | Select-String ":$Port\s.*LISTENING"
        Write-Host "[takeover] port $Port now: $newOwner"
    } else {
        Write-Host "[takeover] no current owner found" -ForegroundColor Yellow
    }
}

# --- cleanup -------------------------------------------------------------------
if ($HoldSeconds -gt 0 -and -not $proc.HasExited) {
    Write-Host ""
    Write-Host "[hold] keeping probe alive for $HoldSeconds s to observe peer polling logs..." -ForegroundColor Cyan
    Start-Sleep -Seconds $HoldSeconds
}
if (-not $Keep) {
    Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue
    Write-Host ""
    Write-Host "[cleanup] killed probe PID $($proc.Id)" -ForegroundColor DarkGray
} else {
    Write-Host ""
    Write-Host "[keep] probe PID $($proc.Id) left running. Stop with: Stop-Process -Id $($proc.Id) -Force" -ForegroundColor DarkGray
}
if ($seedProc -and -not $seedProc.HasExited) {
    Stop-Process -Id $seedProc.Id -Force -ErrorAction SilentlyContinue
    Write-Host "[cleanup] killed seed owner PID $($seedProc.Id)" -ForegroundColor DarkGray
}
