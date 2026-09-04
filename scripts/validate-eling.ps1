#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Full Eling validation across its runtime modes.
.DESCRIPTION
    Tests: solution build, unit/integration tests, the dashboard HTTP API
    (spawned by the eling host), and the stdio MCP server.

    Matches the current Eling architecture (single binary):
      - Eling.Backend   : unified entry point, runs MCP over stdio + HTTP API
                          + web UI on 127.0.0.1:<port>. No cross-process spawn.

    There is no standalone "HTTP MCP" or "REST server on port 5001" mode
    anymore, so the old --port/--root-path/--enable-mcp/--http-mcp flags are gone.

    Run after any backend change to verify the interfaces work correctly.

    Pass -RuntimeOnly to skip the (slow) build + test phases and only exercise
    the dashboard HTTP API and stdio MCP server against an existing build.
#>
param(
    [switch]$RuntimeOnly
)

$ErrorActionPreference = "Stop"
# HttpClient is used for the dashboard HTTP API phase; PowerShell 5.1 does not
# auto-load System.Net.Http, so pull it in explicitly.
Add-Type -AssemblyName System.Net.Http
$root = Split-Path (Split-Path $MyInvocation.MyCommand.Path -Parent) -Parent
$elingOutputRoot = if ($env:ELING_OUTPUT_ROOT) { $env:ELING_OUTPUT_ROOT } else { ".bin-test" }
$exe = Join-Path $root "$elingOutputRoot/Debug/net10.0/eling-backend.exe"
if (-not (Test-Path $exe)) {
    $exe = Join-Path $root ".bin/Debug/net10.0/eling-backend.exe"
}
if (-not (Test-Path $exe)) {
    $exe = Join-Path $root ".bin/Debug/net10.0/eling.exe"
}
$results = @()
$failed = 0

function Get-FreePort {
    # A free loopback port in a high range so validation never collides with a
    # real dashboard owned by a live eling session on 4317.
    for ($candidate = 45100; $candidate -lt 45200; $candidate++) {
        $listener = $null
        try {
            $listener = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, $candidate)
            $listener.Start()
            return $candidate
        }
        catch {
            # Occupied; try next.
        }
        finally {
            if ($null -ne $listener) { try { $listener.Stop() } catch {} }
        }
    }
    throw "No free port found in range."
}

function Start-ElingProcess {
    param(
        [string]$WorkingDirectory,
        [string[]]$Arguments,
        [int]$Port,
        [switch]$DisableDashboard
    )
    $argsList = $Arguments
    if ($DisableDashboard) { $argsList += "--no-dashboard" }
    $psi = [System.Diagnostics.ProcessStartInfo]::new()
    $psi.FileName = $exe
    $psi.WorkingDirectory = $WorkingDirectory
    $psi.Arguments = ($argsList -join " ")
    $psi.UseShellExecute = $false
    $psi.CreateNoWindow = $true
    $psi.RedirectStandardInput = $true
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    $psi.Environment["ELING_DASHBOARD_PORT"] = $Port.ToString()
    $proc = [System.Diagnostics.Process]::new()
    $proc.StartInfo = $psi
    $proc.Start() | Out-Null
    return $proc
}

function Invoke-McpCall {
    # Writes a JSON-RPC request and reads stdout lines until the response with
    # the requested id arrives (or the timeout elapses). Non-matching or
    # non-JSON lines are skipped so the call never hangs forever. Returns $null
    # on timeout.
    param(
        [System.Diagnostics.Process]$Process,
        [string]$Request,
        [int]$Id,
        [int]$TimeoutSeconds = 30
    )
    $Process.StandardInput.WriteLine($Request)
    $Process.StandardInput.Flush()

    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    while ([DateTime]::UtcNow -lt $deadline) {
        # ReadLineAsync returns a .NET Framework Task<string> in PowerShell 5.1;
        # Wait(TimeSpan) bounds the read so we never hang on a missing reply.
        $readTask = $Process.StandardOutput.ReadLineAsync()
        if (-not $readTask.Wait([TimeSpan]::FromSeconds($TimeoutSeconds))) {
            return $null
        }
        $line = $readTask.Result
        if ($null -eq $line) { return $null }   # stdout closed early
        $line = $line.Trim()
        if ($line.Length -eq 0) { continue }
        try {
            $json = $line | ConvertFrom-Json
            if ($json.id -eq $Id) { return $json }
        } catch {
            # Not JSON (e.g. a stray diagnostic line); skip.
        }
    }
    return $null
}

function Test-Phase {
    param([string]$Name, [scriptblock]$Block)
    Write-Host "`n=== $Name ===" -ForegroundColor Cyan
    $proc = $null
    $client = $null
    try {
        $result = & $Block
        if ($result -eq $false) {
            Write-Host "  FAIL" -ForegroundColor Red
            $script:failed++
            $script:results += [PSCustomObject]@{ Phase = $Name; Status = "FAIL" }
        } else {
            Write-Host "  PASS" -ForegroundColor Green
            $script:results += [PSCustomObject]@{ Phase = $Name; Status = "PASS" }
        }
    } catch {
        Write-Host "  ERROR: $_" -ForegroundColor Red
        $script:failed++
        $script:results += [PSCustomObject]@{ Phase = $Name; Status = "ERROR: $_" }
    } finally {
        if ($null -ne $client) { try { $client.Dispose() } catch {} }
        if ($null -ne $proc -and !$proc.HasExited) {
            try { $proc.Kill($true); $proc.WaitForExit(3000) } catch {}
        }
        if ($null -ne $proc) { try { $proc.Dispose() } catch {} }
    }
}

# --- Phase 1: Build (skipped with -RuntimeOnly) ---
if (-not $RuntimeOnly) {
    Test-Phase "Build backend" {
        dotnet build Eling.slnx 2>&1 | Out-Null
        $LASTEXITCODE -eq 0
    }
}

# --- Phase 2: Unit/integration tests (skipped with -RuntimeOnly) ---
if (-not $RuntimeOnly) {
    Test-Phase "Unit & integration tests" {
        dotnet test Eling.slnx 2>&1 | Tee-Object -Variable testOutput
        $LASTEXITCODE -eq 0
    }
}

# --- Phase 3: Dashboard HTTP API (spawned by the host) ---
Test-Phase "Dashboard HTTP API" {
    $tempDir = Join-Path ([System.IO.Path]::GetTempPath()) "eling-dash-$(Get-Random)"
    New-Item -ItemType Directory -Path $tempDir -Force | Out-Null
    $script:tempDir = $tempDir
    $port = Get-FreePort

    # Host spawns + registers the shared dashboard on an isolated port.
    $script:proc = Start-ElingProcess -WorkingDirectory $tempDir -Port $port

    $script:client = [System.Net.Http.HttpClient]::new()
    $script:client.Timeout = [TimeSpan]::FromSeconds(5)
    $base = "http://127.0.0.1:$port"

    # Wait for the dashboard to become healthy.
    $healthy = $false
    for ($attempt = 0; $attempt -lt 40; $attempt++) {
        try {
            $r = $script:client.GetAsync("$base/health").Result
            if ($r.StatusCode -eq 200) { $healthy = $true; break }
        } catch { }
        Start-Sleep -Milliseconds 250
    }
    if (-not $healthy) { Write-Host "  /health never became healthy"; return $false }
    Write-Host "  /health -> 200"

    # GET /api/memories
    $r = $script:client.GetAsync("$base/api/memories").Result
    if ($r.StatusCode -ne 200) { Write-Host "  GET /api/memories returned $($r.StatusCode)"; return $false }
    Write-Host "  GET /api/memories -> 200"

    # POST /api/memories
    $body = '{"content":"dash-validation-test"}'
    $content = [System.Net.Http.StringContent]::new($body, [System.Text.Encoding]::UTF8, "application/json")
    $r = $script:client.PostAsync("$base/api/memories", $content).Result
    if ($r.StatusCode -ne 201) { Write-Host "  POST /api/memories returned $($r.StatusCode)"; return $false }
    $json = $r.Content.ReadAsStringAsync().Result | ConvertFrom-Json
    $id = $json.id
    Write-Host "  POST /api/memories -> 201 (id=$id)"

    # GET /api/memories/$id
    $r = $script:client.GetAsync("$base/api/memories/$id").Result
    if ($r.StatusCode -ne 200) { Write-Host "  GET /api/memories/$id returned $($r.StatusCode)"; return $false }
    Write-Host "  GET /api/memories/$id -> 200"

    # DELETE /api/memories/$id
    $r = $script:client.DeleteAsync("$base/api/memories/$id").Result
    if ($r.StatusCode -ne [System.Net.HttpStatusCode]::NoContent -and $r.StatusCode -ne 200) {
        Write-Host "  DELETE returned $($r.StatusCode)"; return $false
    }
    Write-Host "  DELETE /api/memories/$id -> $($r.StatusCode)"

    $true
}

# --- Phase 4: Stdio MCP mode ---
Test-Phase "Stdio MCP mode" {
    $tempDir = Join-Path ([System.IO.Path]::GetTempPath()) "eling-stdio-$(Get-Random)"
    New-Item -ItemType Directory -Path $tempDir -Force | Out-Null
    $script:tempDir = $tempDir
    $port = Get-FreePort

    # Dashboard disabled: keep stdout purely JSON for the MCP protocol.
    $script:proc = Start-ElingProcess -WorkingDirectory $tempDir -Port $port -DisableDashboard

    # Initialize
    $initJson = Invoke-McpCall -Process $script:proc -Request '{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-03-26","capabilities":{},"clientInfo":{"name":"stdio-validation","version":"1.0"}}}' -Id 1
    if ($null -eq $initJson) { Write-Host "  No response to initialize"; return $false }
    Write-Host "  initialize -> server=$($initJson.result.serverInfo.name)"

    # notifications/initialized (fire and forget, no response expected)
    $script:proc.StandardInput.WriteLine('{"jsonrpc":"2.0","method":"notifications/initialized"}')
    $script:proc.StandardInput.Flush()
    Start-Sleep -Milliseconds 200

    # tools/list
    $toolsJson = Invoke-McpCall -Process $script:proc -Request '{"jsonrpc":"2.0","id":2,"method":"tools/list","params":{}}' -Id 2
    if ($null -eq $toolsJson) { Write-Host "  No response to tools/list"; return $false }
    Write-Host "  tools/list -> $($toolsJson.result.tools.Count) tools"

    # memory_save
    $saveJson = Invoke-McpCall -Process $script:proc -Request '{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"memory_save","arguments":{"content":"stdio-validation-test","type":"fact","tags":["test"]}}}' -Id 3
    if ($null -eq $saveJson) { Write-Host "  No response to memory_save"; return $false }
    Write-Host "  memory_save -> OK"

    # memory_search
    $searchJson = Invoke-McpCall -Process $script:proc -Request '{"jsonrpc":"2.0","id":4,"method":"tools/call","params":{"name":"memory_search","arguments":{"query":"stdio-validation"}}}' -Id 4
    if ($null -eq $searchJson) { Write-Host "  No response to memory_search"; return $false }
    Write-Host "  memory_search -> OK"

    $true
}

# --- Summary ---
Write-Host "`n========================================" -ForegroundColor Cyan
Write-Host "VALIDATION SUMMARY" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan
$results | Format-Table -AutoSize
if ($failed -gt 0) {
    Write-Host "`n$failed phase(s) FAILED" -ForegroundColor Red
    exit 1
} else {
    Write-Host "`nAll phases PASSED" -ForegroundColor Green
    exit 0
}
