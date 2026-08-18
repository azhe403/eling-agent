#!/usr/bin/env pwsh
param([string]$Mode = "rest")

$ErrorActionPreference = "Stop"
$root = Split-Path $MyInvocation.MyCommand.Path
$artifacts = Join-Path $root ".artifacts"
$exe = Join-Path $artifacts "bin/Eling.Host/debug/eling.exe"
$tempDir = Join-Path ([System.IO.Path]::GetTempPath()) "eling-mode-$Mode-$(Get-Random)"
$proc = $null
$client = $null

New-Item -ItemType Directory -Path $tempDir -Force | Out-Null

function Write-Result([string]$Name, [bool]$Passed) {
    if ($Passed) { Write-Host "  $Name -> PASS" -ForegroundColor Green }
    else { Write-Host "  $Name -> FAIL" -ForegroundColor Red }
}

try {
    switch ($Mode) {
        "rest" {
            $port = 5100
            $proc = Start-Process -FilePath $exe -ArgumentList @("--port", "$port", "--root-path", $tempDir) -PassThru -WindowStyle Hidden
            Start-Sleep -Seconds 3
            if ($proc.HasExited) { Write-Host "Server exited early"; exit 1 }
            
            $client = [System.Net.Http.HttpClient]::new()
            $client.Timeout = [TimeSpan]::FromSeconds(10)
            
            $ok = $true
            $r = $client.GetAsync("http://localhost:$port/api/memories").Result
            $ok = $ok -and ($r.StatusCode -eq 200)
            Write-Result "GET /api/memories -> $($r.StatusCode)" ($r.StatusCode -eq 200)
            
            $content = [System.Net.Http.StringContent]::new('{"content":"rest-test"}', [System.Text.Encoding]::UTF8, "application/json")
            $r = $client.PostAsync("http://localhost:$port/api/memories", $content).Result
            $ok = $ok -and ($r.StatusCode -eq 201)
            Write-Result "POST /api/memories -> $($r.StatusCode)" ($r.StatusCode -eq 201)
            $id = ($r.Content.ReadAsStringAsync().Result | ConvertFrom-Json).id
            
            $r = $client.GetAsync("http://localhost:$port/api/memories/$id").Result
            $ok = $ok -and ($r.StatusCode -eq 200)
            Write-Result "GET /api/memories/$id -> $($r.StatusCode)" ($r.StatusCode -eq 200)
            
            $r = $client.DeleteAsync("http://localhost:$port/api/memories/$id").Result
            $ok = $ok -and ($r.StatusCode -eq 204 -or $r.StatusCode -eq 200)
            Write-Result "DELETE /api/memories/$id -> $($r.StatusCode)" ($r.StatusCode -eq 204 -or $r.StatusCode -eq 200)
            
            if ($ok) { Write-Host "`nREST MODE: ALL PASSED" -ForegroundColor Green } else { exit 1 }
        }
        "http-mcp" {
            $port = 5200
            $proc = Start-Process -FilePath $exe -ArgumentList @("--port", "$port", "--root-path", $tempDir, "--enable-mcp", "--http-mcp") -PassThru -WindowStyle Hidden
            Start-Sleep -Seconds 3
            if ($proc.HasExited) { Write-Host "Server exited early"; exit 1 }
            
            $client = [System.Net.Http.HttpClient]::new()
            $client.Timeout = [TimeSpan]::FromSeconds(10)
            
            $initMsg = '{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-03-26","capabilities":{},"clientInfo":{"name":"validation","version":"1.0"}}}'
            $ok = $false
            
            foreach ($path in @("/mcp", "/")) {
                $req = [System.Net.Http.HttpRequestMessage]::new([System.Net.Http.HttpMethod]::Post, "http://localhost:$port$path")
                $req.Content = [System.Net.Http.StringContent]::new($initMsg, [System.Text.Encoding]::UTF8, "application/json")
                $req.Headers.Add("Accept", "application/json, text/event-stream")
                try {
                    $r = $client.SendAsync($req).Result
                    Write-Host "  POST $path -> $($r.StatusCode)" -ForegroundColor $(if($r.StatusCode -eq 200){"Green"}else{"Yellow"})
                    if ($r.StatusCode -eq 200) {
                        $raw = $r.Content.ReadAsStringAsync().Result
                        if ($raw -match "data: (.+)") { $raw = $matches[1] }
                        $j = $raw | ConvertFrom-Json
                        Write-Host "  server=$($j.result.serverInfo.name)" -ForegroundColor Green
                        $ok = $true
                        break
                    }
                } catch { Write-Host "  POST $path -> ERROR: $_" -ForegroundColor Red }
            }
            
            if ($ok) {
                # REST alongside MCP
                $r = $client.GetAsync("http://localhost:$port/api/memories").Result
                Write-Result "GET /api/memories (REST alongside MCP) -> $($r.StatusCode)" ($r.StatusCode -eq 200)
                Write-Host "`nHTTP MCP MODE: ALL PASSED" -ForegroundColor Green
            } else { exit 1 }
        }
        "stdio" {
            $psi = [System.Diagnostics.ProcessStartInfo]::new()
            $psi.FileName = $exe
            $psi.Arguments = "--root-path `"$tempDir`" --enable-mcp"
            $psi.UseShellExecute = $false
            $psi.RedirectStandardInput = $true
            $psi.RedirectStandardOutput = $true
            $psi.RedirectStandardError = $true
            $psi.CreateNoWindow = $true
            
            $proc = [System.Diagnostics.Process]::new()
            $proc.StartInfo = $psi
            $proc.Start() | Out-Null
            Start-Sleep -Seconds 2
            if ($proc.HasExited) { Write-Host "Server exited early"; exit 1 }
            
            $ok = $true
            
            # Initialize
            $proc.StandardInput.WriteLine('{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-03-26","capabilities":{},"clientInfo":{"name":"test","version":"1.0"}}}')
            $proc.StandardInput.Flush()
            $resp = $proc.StandardOutput.ReadLine()
            $j = $resp | ConvertFrom-Json
            $ok = $ok -and ($j.result.serverInfo.name -eq "eling")
            Write-Result "initialize -> $($j.result.serverInfo.name)" ($j.result.serverInfo.name -eq "eling")
            
            # Initialized notification
            $proc.StandardInput.WriteLine('{"jsonrpc":"2.0","method":"notifications/initialized"}')
            $proc.StandardInput.Flush()
            Start-Sleep -Milliseconds 200
            
            # tools/list
            $proc.StandardInput.WriteLine('{"jsonrpc":"2.0","id":2,"method":"tools/list","params":{}}')
            $proc.StandardInput.Flush()
            $resp = $proc.StandardOutput.ReadLine()
            $j = $resp | ConvertFrom-Json
            $toolCount = $j.result.tools.Count
            $ok = $ok -and ($toolCount -gt 0)
            Write-Result "tools/list -> $toolCount tools" ($toolCount -gt 0)
            
            # memory_save
            $proc.StandardInput.WriteLine('{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"memory_save","arguments":{"content":"stdio-validation-test","type":"fact","tags":["test"]}}}')
            $proc.StandardInput.Flush()
            $resp = $proc.StandardOutput.ReadLine()
            $j = $resp | ConvertFrom-Json
            $saved = $j.result.content[0].text -match "created|updated"
            Write-Result "memory_save -> OK" $saved
            $ok = $ok -and $saved
            
            # memory_search
            $proc.StandardInput.WriteLine('{"jsonrpc":"2.0","id":4,"method":"tools/call","params":{"name":"memory_search","arguments":{"query":"stdio-validation"}}}')
            $proc.StandardInput.Flush()
            $resp = $proc.StandardOutput.ReadLine()
            $j = $resp | ConvertFrom-Json
            $found = $j.result.content[0].text -match "stdio-validation"
            Write-Result "memory_search -> OK" $found
            $ok = $ok -and $found
            
            if ($ok) { Write-Host "`nSTDIO MCP MODE: ALL PASSED" -ForegroundColor Green } else { exit 1 }
        }
    }
} catch {
    Write-Host "ERROR: $_" -ForegroundColor Red
    exit 1
} finally {
    if ($null -ne $proc -and !$proc.HasExited) {
        try { $proc.Kill($true); $proc.WaitForExit(3000) } catch {}
    }
    if ($null -ne $proc) { try { $proc.Dispose() } catch {} }
    if ($null -ne $client) { try { $client.Dispose() } catch {} }
    Remove-Item $tempDir -Recurse -Force -ErrorAction SilentlyContinue
}
