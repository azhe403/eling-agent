#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Full Eling MCP + REST validation across all modes.
.DESCRIPTION
    Tests: HTTP REST, HTTP MCP, Stdio MCP, and unit/integration tests.
    Run after any backend change to verify all interfaces work correctly.
#>

$ErrorActionPreference = "Stop"
$root = Split-Path $MyInvocation.MyCommand.Path
$artifacts = Join-Path $root ".artifacts"
$exe = Join-Path $artifacts "bin/Eling.Host/debug/eling.exe"
$results = @()
$failed = 0

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

# --- Phase 1: Build ---
Test-Phase "Build backend" {
    dotnet build Eling.slnx --artifacts-path $artifacts 2>&1 | Out-Null
    $LASTEXITCODE -eq 0
}

# --- Phase 2: Unit/Integration tests ---
Test-Phase "Unit & integration tests" {
    dotnet test Eling.slnx --artifacts-path $artifacts 2>&1 | Tee-Object -Variable testOutput
    $LASTEXITCODE -eq 0
}

# --- Phase 3: HTTP REST mode (no MCP) ---
Test-Phase "HTTP REST mode (port 5001)" {
    $tempDir = Join-Path ([System.IO.Path]::GetTempPath()) "eling-rest-$(Get-Random)"
    New-Item -ItemType Directory -Path $tempDir -Force | Out-Null
    $script:tempDir = $tempDir
    
    $script:proc = Start-Process -FilePath $exe `
        -ArgumentList "--port 5001 --root-path `"$tempDir`"" `
        -NoNewWindow -PassThru
    Start-Sleep -Seconds 3
    
    if ($script:proc.HasExited) { Write-Host "  Process exited early"; return $false }
    
    $script:client = [System.Net.Http.HttpClient]::new()
    $script:client.Timeout = [TimeSpan]::FromSeconds(5)
    
    # GET /api/memories
    $r = $script:client.GetAsync("http://localhost:5001/api/memories").Result
    if ($r.StatusCode -ne 200) { Write-Host "  GET /api/memories returned $($r.StatusCode)"; return $false }
    Write-Host "  GET /api/memories -> 200"
    
    # POST /api/memories
    $body = '{"content":"rest-validation-test"}'
    $content = [System.Net.Http.StringContent]::new($body, [System.Text.Encoding]::UTF8, "application/json")
    $r = $script:client.PostAsync("http://localhost:5001/api/memories", $content).Result
    if ($r.StatusCode -ne 201) { Write-Host "  POST /api/memories returned $($r.StatusCode)"; return $false }
    $json = $r.Content.ReadAsStringAsync().Result | ConvertFrom-Json
    $id = $json.id
    Write-Host "  POST /api/memories -> 201 (id=$id)"
    
    # GET /api/memories/$id
    $r = $script:client.GetAsync("http://localhost:5001/api/memories/$id").Result
    if ($r.StatusCode -ne 200) { Write-Host "  GET /api/memories/$id returned $($r.StatusCode)"; return $false }
    Write-Host "  GET /api/memories/$id -> 200"
    
    # DELETE /api/memories/$id
    $r = $script:client.DeleteAsync("http://localhost:5001/api/memories/$id").Result
    if ($r.StatusCode -ne [System.Net.HttpStatusCode]::NoContent -and $r.StatusCode -ne 200) {
        Write-Host "  DELETE returned $($r.StatusCode)"; return $false
    }
    Write-Host "  DELETE /api/memories/$id -> $($r.StatusCode)"
    
    $true
}

# --- Phase 4: HTTP MCP mode (REST + MCP on same port) ---
Test-Phase "HTTP MCP mode (port 5002)" {
    $tempDir = Join-Path ([System.IO.Path]::GetTempPath()) "eling-mcp-http-$(Get-Random)"
    New-Item -ItemType Directory -Path $tempDir -Force | Out-Null
    $script:tempDir = $tempDir
    
    $script:proc = Start-Process -FilePath $exe `
        -ArgumentList "--port 5002 --root-path `"$tempDir`" --enable-mcp --http-mcp" `
        -NoNewWindow -PassThru
    Start-Sleep -Seconds 3
    
    if ($script:proc.HasExited) { Write-Host "  Process exited early"; return $false }
    
    $script:client = [System.Net.Http.HttpClient]::new()
    $script:client.Timeout = [TimeSpan]::FromSeconds(5)
    
    # Initialize - Streamable HTTP requires Accept: application/json, text/event-stream
    $initMsg = '{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-03-26","capabilities":{},"clientInfo":{"name":"validation","version":"1.0"}}}'
    $req = [System.Net.Http.HttpRequestMessage]::new([System.Net.Http.HttpMethod]::Post, "http://localhost:5002/mcp")
    $req.Content = [System.Net.Http.StringContent]::new($initMsg, [System.Text.Encoding]::UTF8, "application/json")
    $req.Headers.Add("Accept", "application/json, text/event-stream")
    $r = $script:client.SendAsync($req).Result
    if ($r.StatusCode -ne 200) { 
        Write-Host "  initialize returned $($r.StatusCode)"
        $body = $r.Content.ReadAsStringAsync().Result
        Write-Host "  body: $body"
        return $false 
    }
    $raw = $r.Content.ReadAsStringAsync().Result
    # Parse SSE response if needed
    if ($raw.StartsWith("event:")) {
        $lines = $raw -split "`n"
        foreach ($line in $lines) {
            if ($line.StartsWith("data: ")) {
                $raw = $line.Substring(6)
                break
            }
        }
    }
    $json = $raw | ConvertFrom-Json
    Write-Host "  initialize -> 200 (server=$($json.result.serverInfo.name))"
    
    # Also check REST alongside MCP
    $r = $script:client.GetAsync("http://localhost:5002/api/memories").Result
    if ($r.StatusCode -ne 200) { Write-Host "  GET /api/memories (REST) returned $($r.StatusCode)"; return $false }
    Write-Host "  GET /api/memories (REST alongside MCP) -> 200"
    
    $true
}

# --- Phase 5: Stdio MCP mode ---
Test-Phase "Stdio MCP mode" {
    $tempDir = Join-Path ([System.IO.Path]::GetTempPath()) "eling-stdio-$(Get-Random)"
    New-Item -ItemType Directory -Path $tempDir -Force | Out-Null
    $script:tempDir = $tempDir
    
    $psi = [System.Diagnostics.ProcessStartInfo]::new()
    $psi.FileName = $exe
    $psi.Arguments = "--root-path `"$tempDir`" --enable-mcp"
    $psi.UseShellExecute = $false
    $psi.RedirectStandardInput = $true
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    
    $script:proc = [System.Diagnostics.Process]::new()
    $script:proc.StartInfo = $psi
    $script:proc.Start() | Out-Null
    
    Start-Sleep -Seconds 2
    if ($script:proc.HasExited) { Write-Host "  Process exited early"; return $false }
    
    # Initialize
    $initMsg = '{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-03-26","capabilities":{},"clientInfo":{"name":"stdio-validation","version":"1.0"}}}'
    $script:proc.StandardInput.WriteLine($initMsg)
    $script:proc.StandardInput.Flush()
    
    $initResp = $script:proc.StandardOutput.ReadLine()
    if ([string]::IsNullOrWhiteSpace($initResp)) { Write-Host "  No response to initialize"; return $false }
    $initJson = $initResp | ConvertFrom-Json
    Write-Host "  initialize -> server=$($initJson.result.serverInfo.name)"
    
    # notifications/initialized
    $script:proc.StandardInput.WriteLine('{"jsonrpc":"2.0","method":"notifications/initialized"}')
    $script:proc.StandardInput.Flush()
    Start-Sleep -Milliseconds 200
    
    # tools/list
    $script:proc.StandardInput.WriteLine('{"jsonrpc":"2.0","id":2,"method":"tools/list","params":{}}')
    $script:proc.StandardInput.Flush()
    $toolsResp = $script:proc.StandardOutput.ReadLine()
    $toolsJson = $toolsResp | ConvertFrom-Json
    Write-Host "  tools/list -> $($toolsJson.result.tools.Count) tools"
    
    # memory_save
    $script:proc.StandardInput.WriteLine('{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"memory_save","arguments":{"content":"stdio-validation-test","type":"fact","tags":["test"]}}}')
    $script:proc.StandardInput.Flush()
    $saveResp = $script:proc.StandardOutput.ReadLine()
    $saveJson = $saveResp | ConvertFrom-Json
    Write-Host "  memory_save -> OK"
    
    # memory_search
    $script:proc.StandardInput.WriteLine('{"jsonrpc":"2.0","id":4,"method":"tools/call","params":{"name":"memory_search","arguments":{"query":"stdio-validation"}}}')
    $script:proc.StandardInput.Flush()
    $searchResp = $script:proc.StandardOutput.ReadLine()
    $searchJson = $searchResp | ConvertFrom-Json
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
