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
. (Join-Path $PSScriptRoot "lib/output-root.ps1")

# The build and the runtime phases have to agree on ONE output root. Without
# this the build honours the default (.bin) while the lookup below expects the
# binary in .bin-test, and the runtime phases end up spawning a path that was
# never produced. ELING_OUTPUT_ROOT wins when the caller set it.
$elingOutputRoot = if ($env:ELING_OUTPUT_ROOT) { $env:ELING_OUTPUT_ROOT } else { ".bin-test" }
$env:ELING_OUTPUT_ROOT = $elingOutputRoot
$elingOutputRootPath = if ([System.IO.Path]::IsPathRooted($elingOutputRoot)) {
    $elingOutputRoot
}
else {
    Join-Path $root $elingOutputRoot
}

function Resolve-ElingBackendExe {
    <#
    .SYNOPSIS
        Locate the backend binary the runtime phases have to spawn.
    .DESCRIPTION
        The output root this validation built into comes first; .bin is the
        fallback for a build that landed in the default dev root.
    #>
    param(
        [Parameter(Mandatory = $true)][string]$Root,
        [Parameter(Mandatory = $true)][string]$OutputRootPath
    )

    $candidates = @(
        (Join-Path $OutputRootPath "eling-backend.exe"),
        (Join-Path $OutputRootPath "eling.exe"),
        (Join-Path $Root ".bin/eling-backend.exe"),
        (Join-Path $Root ".bin/eling.exe")
    )
    foreach ($candidate in $candidates) {
        if (Test-Path -LiteralPath $candidate) { return $candidate }
    }
    return $candidates[0]
}

$exe = Resolve-ElingBackendExe -Root $root -OutputRootPath $elingOutputRootPath

# Claim the output root for the duration of the validation: a sweep running
# while this script still uses the folder has to leave it alone.
New-OutputRootClaim -Root $elingOutputRootPath

$results = @()
$failed = 0

# One scratch scope for the whole run. Every spawned runtime writes its
# workspace catalogue, its heartbeat files, its codebase index and its test
# memories inside it, so a validation run leaves nothing in the developer's
# real Eling stores. Without this the spawned runtime registers its throwaway
# temp folder in the real catalogue (~/.local/share/eling/agent-workspaces.json)
# and writes its test memories into the real global memory store. The
# directory is removed when the script ends.
$script:validationScope = Join-Path ([System.IO.Path]::GetTempPath()) "eling-validate-$(Get-Random)"
New-Item -ItemType Directory -Path $script:validationScope -Force | Out-Null

# The throwaway workspaces the runtime phases ran in. Tracked so the summary
# can retry a directory that was still locked when its phase ended.
$script:tempDirs = @()

function Remove-ValidationTempDir {
    <#
    .SYNOPSIS
        Best-effort removal of a throwaway workspace.
    .DESCRIPTION
        A locked directory is retried rather than given up on. The spawned
        process had it as its working directory, and Windows holds that handle
        until the process is completely gone -- a kill returns before the
        handle is released, so the first attempt fails even though the process
        is already dying. The last attempt gives up quietly: the summary
        sweep retries everything once more when every process is dead.
    #>
    param(
        [string]$Dir,
        [int]$Attempts = 5
    )

    if ([string]::IsNullOrWhiteSpace($Dir)) { return }
    for ($attempt = 1; $attempt -le $Attempts; $attempt++) {
        if (-not (Test-Path -LiteralPath $Dir)) { return }
        try {
            Remove-Item -LiteralPath $Dir -Recurse -Force -ErrorAction Stop
            return
        } catch {
            if ($attempt -eq $Attempts) {
                Write-Host "  (temp dir still locked, will retry at the end: $Dir -- $($_.Exception.Message))"
                return
            }
            Start-Sleep -Milliseconds 250
        }
    }
}

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
        [switch]$DisableDashboard,
        [switch]$DrainStdout
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
    # The validation run must not touch the developer's real Eling stores.
    # ELING_USER_SCOPE relocates the config and data roots, ELING_CONFIG_DIR
    # and ELING_DATA_DIR cover the paths that are resolved directly, and
    # ELING_AGENT_DATA_DIR is what the workspace catalogue
    # (agent-workspaces.json) and the provider/chat stores follow. Without
    # them the spawned runtime registers its throwaway temp folder in the real
    # catalogue and writes its test memories into the real global memory
    # store -- both of which outlive the run.
    $psi.Environment["ELING_USER_SCOPE"] = $script:validationScope
    $psi.Environment["ELING_CONFIG_DIR"] = $script:validationScope
    $psi.Environment["ELING_DATA_DIR"] = $script:validationScope
    $psi.Environment["ELING_AGENT_DATA_DIR"] = $script:validationScope
    $proc = [System.Diagnostics.Process]::new()
    $proc.StartInfo = $psi
    $proc.Start() | Out-Null

    # Drain the redirected streams in the background, or do not drain them at
    # all.
    #
    # A pipe nobody reads fills up - Windows gives an anonymous pipe a 4 KB
    # buffer - and the backend then blocks on its own log write. That is not
    # a slow start: the backend never gets as far as Kestrel's bind, so
    # /health never answers and the phase hangs until it times out. The
    # stdio phase reads stdout line by line itself, so only that phase's
    # stdout is left alone; every other stream is drained here.
    #
    # The captured text is printed when the phase fails, because a hang that
    # says nothing is the hardest kind to diagnose.
    $script:phaseStderr = $proc.StandardError.ReadToEndAsync()
    if ($DrainStdout) { $script:phaseStdout = $proc.StandardOutput.ReadToEndAsync() }

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
    # The phase block publishes what it spawned into the script scope; these
    # locals are the phase's own state and are reset so a phase that spawns
    # nothing never inherits the previous phase's process.
    $script:phaseProc = $null
    $script:phaseClient = $null
    $script:tempDir = $null
    $script:phaseStderr = $null
    $script:phaseStdout = $null
    $phaseOk = $false
    try {
        $result = & $Block
        if ($result -eq $false) {
            Write-Host "  FAIL" -ForegroundColor Red
            $script:failed++
            $script:results += [PSCustomObject]@{ Phase = $Name; Status = "FAIL" }
        } else {
            $phaseOk = $true
            Write-Host "  PASS" -ForegroundColor Green
            $script:results += [PSCustomObject]@{ Phase = $Name; Status = "PASS" }
        }
    } catch {
        Write-Host "  ERROR: $_" -ForegroundColor Red
        $script:failed++
        $script:results += [PSCustomObject]@{ Phase = $Name; Status = "ERROR: $_" }
    } finally {
        if ($null -ne $script:phaseClient) { try { $script:phaseClient.Dispose() } catch {} }
        # The process the phase spawned has to die here. It is not optional:
        # a survivor keeps the throwaway workspace as its working directory,
        # which is what locks the folder against deletion, and it stays
        # registered in the workspace catalogue as a LIVE runtime, so the
        # dashboard lists a workspace that is not going away. Reading the
        # script scope is what makes this cleanup happen at all -- a local
        # $proc here is always null and the process is never touched.
        if ($null -ne $script:phaseProc) {
            if (-not $script:phaseProc.HasExited) {
                # Kill and wait are separate calls on purpose. A kill returns
                # while the process is still dying, and Windows holds the
                # working directory handle until it is completely gone -- so
                # the wait is what lets the temp dir be removed a moment
                # later.
                try { $script:phaseProc.Kill($true) } catch {
                    Write-Host "  (kill failed: $($_.Exception.Message))" -ForegroundColor DarkGray
                }
                try { $script:phaseProc.WaitForExit(5000) } catch {}

                # A survivor holds the throwaway workspace as its working
                # directory, which locks the folder against deletion, and it
                # stays registered as a LIVE runtime. A kill that did not
                # take is not something to swallow: fall back to the shell's
                # own kill, and say so if the process is still there.
                if (-not $script:phaseProc.HasExited) {
                    try { Stop-Process -Id $script:phaseProc.Id -Force -ErrorAction Stop } catch {}
                    try { $script:phaseProc.WaitForExit(5000) } catch {}
                }
                if (-not $script:phaseProc.HasExited) {
                    Write-Host "  (process $($script:phaseProc.Id) is still alive - its workspace folder cannot be removed)" -ForegroundColor Red
                }
            }
            # A failed phase says nothing about WHY, and the reason is usually
            # in the backend's own log. The process is dead by now, so the
            # drained stream is complete -- but the wait is bounded, because
            # a survivor that never died would otherwise turn a failed phase
            # into a hung script.
            if (-not $phaseOk -and $null -ne $script:phaseStderr) {
                try {
                    if ($script:phaseStderr.Wait(3000)) {
                        $text = $script:phaseStderr.Result
                        if (-not [string]::IsNullOrWhiteSpace($text)) {
                            Write-Host "  --- backend stderr (last 20 lines) ---" -ForegroundColor DarkGray
                            ($text -split "`r?`n" | Where-Object { $_ -ne "" } | Select-Object -Last 20) |
                                ForEach-Object { Write-Host "  $_" -ForegroundColor DarkGray }
                        }
                    }
                } catch { }
            }
            try { $script:phaseProc.Dispose() } catch {}
        }
        # The throwaway workspace must not outlive the phase. A folder that
        # stays on disk keeps its row in the workspace catalogue: the
        # catalogue prunes a row only when its folder is GONE -- a dead
        # process never removes one -- so leaving the folder behind is
        # exactly what makes the dashboard list a workspace that no longer
        # exists.
        Remove-ValidationTempDir -Dir $script:tempDir
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
    $script:tempDirs += $tempDir
    $port = Get-FreePort

    # Host spawns + registers the shared dashboard on an isolated port.
    # Nothing reads this phase's stdout, so it is drained too.
    $script:phaseProc = Start-ElingProcess -WorkingDirectory $tempDir -Port $port -DrainStdout

    $script:phaseClient = [System.Net.Http.HttpClient]::new()
    $script:phaseClient.Timeout = [TimeSpan]::FromSeconds(5)
    $base = "http://127.0.0.1:$port"

    # Wait for the dashboard to become healthy.
    $healthy = $false
    for ($attempt = 0; $attempt -lt 40; $attempt++) {
        try {
            $r = $script:phaseClient.GetAsync("$base/health").Result
            if ($r.StatusCode -eq 200) { $healthy = $true; break }
        } catch { }
        Start-Sleep -Milliseconds 250
    }
    if (-not $healthy) { Write-Host "  /health never became healthy"; return $false }
    Write-Host "  /health -> 200"

    # GET /api/memories
    $r = $script:phaseClient.GetAsync("$base/api/memories").Result
    if ($r.StatusCode -ne 200) { Write-Host "  GET /api/memories returned $($r.StatusCode)"; return $false }
    Write-Host "  GET /api/memories -> 200"

    # POST /api/memories
    $body = '{"content":"dash-validation-test"}'
    $content = [System.Net.Http.StringContent]::new($body, [System.Text.Encoding]::UTF8, "application/json")
    $r = $script:phaseClient.PostAsync("$base/api/memories", $content).Result
    if ($r.StatusCode -ne 201) { Write-Host "  POST /api/memories returned $($r.StatusCode)"; return $false }
    $json = $r.Content.ReadAsStringAsync().Result | ConvertFrom-Json
    $id = $json.id
    Write-Host "  POST /api/memories -> 201 (id=$id)"

    # GET /api/memories/$id
    $r = $script:phaseClient.GetAsync("$base/api/memories/$id").Result
    if ($r.StatusCode -ne 200) { Write-Host "  GET /api/memories/$id returned $($r.StatusCode)"; return $false }
    Write-Host "  GET /api/memories/$id -> 200"

    # DELETE /api/memories/$id
    $r = $script:phaseClient.DeleteAsync("$base/api/memories/$id").Result
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
    $script:tempDirs += $tempDir
    $port = Get-FreePort

    # Dashboard disabled: keep stdout purely JSON for the MCP protocol.
    $script:phaseProc = Start-ElingProcess -WorkingDirectory $tempDir -Port $port -DisableDashboard

    # Initialize
    $initJson = Invoke-McpCall -Process $script:phaseProc -Request '{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-03-26","capabilities":{},"clientInfo":{"name":"stdio-validation","version":"1.0"}}}' -Id 1
    if ($null -eq $initJson) { Write-Host "  No response to initialize"; return $false }
    Write-Host "  initialize -> server=$($initJson.result.serverInfo.name)"

    # notifications/initialized (fire and forget, no response expected)
    $script:phaseProc.StandardInput.WriteLine('{"jsonrpc":"2.0","method":"notifications/initialized"}')
    $script:phaseProc.StandardInput.Flush()
    Start-Sleep -Milliseconds 200

    # tools/list
    $toolsJson = Invoke-McpCall -Process $script:phaseProc -Request '{"jsonrpc":"2.0","id":2,"method":"tools/list","params":{}}' -Id 2
    if ($null -eq $toolsJson) { Write-Host "  No response to tools/list"; return $false }
    Write-Host "  tools/list -> $($toolsJson.result.tools.Count) tools"

    # memory_save
    $saveJson = Invoke-McpCall -Process $script:phaseProc -Request '{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"memory_save","arguments":{"content":"stdio-validation-test","type":"fact","tags":["test"]}}}' -Id 3
    if ($null -eq $saveJson) { Write-Host "  No response to memory_save"; return $false }
    Write-Host "  memory_save -> OK"

    # memory_search
    $searchJson = Invoke-McpCall -Process $script:phaseProc -Request '{"jsonrpc":"2.0","id":4,"method":"tools/call","params":{"name":"memory_search","arguments":{"query":"stdio-validation"}}}' -Id 4
    if ($null -eq $searchJson) { Write-Host "  No response to memory_search"; return $false }
    Write-Host "  memory_search -> OK"

    $true
}

# --- Summary ---
# Every spawned process is dead by now, so a temp dir that was locked during
# its phase can finally go -- and with it the scratch scope holding the
# catalogue, the heartbeat files and the test memories this run produced.
foreach ($dir in $script:tempDirs) { Remove-ValidationTempDir -Dir $dir -Attempts 3 }
if (Test-Path -LiteralPath $script:validationScope) {
    try { Remove-Item -LiteralPath $script:validationScope -Recurse -Force -ErrorAction Stop } catch {}
}
Remove-OutputRootClaim -Root $elingOutputRootPath
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
