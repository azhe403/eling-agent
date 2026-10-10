# Publish Eling from source and install it into ~/.local/bin ("install from source").
# Smoke test verifies: dashboard health + full MCP memory read/write round-trip.
# Usage:  .\publish-global.ps1 [-Configuration Release] [-Rid win-x64] [-SkipSmokeTest]

param(
    [string]$Configuration = "Release",
    [string]$Rid = "win-x64",
    [switch]$SkipSmokeTest
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path $PSScriptRoot -Parent

# Publishing writes intermediate output to $(ElingOutputRoot), which defaults to
# .bin/ — the same folder a dev `dotnet watch` uses. Two writers on one folder
# means the publish fails with MSB3021/3027 as soon as a dev server is running,
# which is the common case rather than the rare one. Give this script its own
# root so publishing never waits on a running dev process to release a lock.
$env:ELING_OUTPUT_ROOT = ".bin-publish"

$outDir = Join-Path $env:TEMP "eling-publish-global"
$artifactsDir = Join-Path $env:TEMP "eling-publish-artifacts"
$binDir = Join-Path $env:USERPROFILE ".local\bin"

Write-Host "== Publishing ($Configuration / $Rid) =="
if (Test-Path $outDir) { Remove-Item $outDir -Recurse -Force }
if (Test-Path $artifactsDir) { Remove-Item $artifactsDir -Recurse -Force }

Write-Host "-- dotnet publish src/backend/Eling.Backend"
dotnet publish (Join-Path $repoRoot "src/backend/Eling.Backend") `
    -c $Configuration -r $Rid `
    --self-contained true `
    --artifacts-path $artifactsDir `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true `
    -o $outDir --nologo -v q
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed for Eling.Backend" }

foreach ($expected in @("eling-backend.exe", "eling-dashboard-ui")) {
    if (-not (Test-Path (Join-Path $outDir $expected))) {
        throw "Publish output missing: $expected"
    }
}

Write-Host "== Installing into $binDir =="
New-Item $binDir -ItemType Directory -Force | Out-Null

# Kill all global-bin holders (staging must be among them). Dev from source is untouched;
# dev launched from the global exe will also restart. Retried to beat respawn races.
$globalBackend = Join-Path $binDir "eling-backend.exe"
$stagingPid = (Get-NetTCPConnection -LocalPort 4317 -State Listen -ErrorAction SilentlyContinue |
    Select-Object -First 1).OwningProcess
$attempt = 0
$installed = $false
while (-not $installed -and $attempt -lt 5) {
    $attempt++
    $killed = @()
    Get-Process -Name eling-backend, eling -ErrorAction SilentlyContinue |
        Where-Object { $_.Path -and ($_.Path -eq $globalBackend) } |
        ForEach-Object { $killed += $_.Id; Write-Host "  [try $attempt] stopping $($_.Id) ($($_.Path))"; Stop-Process -Id $_.Id -Force -ErrorAction SilentlyContinue }
    Get-CimInstance Win32_Process -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -in @('eling-backend.exe', 'eling.exe') } |
        Where-Object { $_.ExecutablePath -eq $globalBackend -or $_.CommandLine -like "*$binDir*" } |
        Where-Object { $_.ProcessId -notin $killed } |
        ForEach-Object { $killed += $_.ProcessId; Write-Host "  [try $attempt] stopping $($_.ProcessId) ($($_.CommandLine))"; Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
    if ($attempt -eq 1 -and $stagingPid -and ($stagingPid -notin $killed)) { Write-Warning "staging pid $stagingPid not among global-bin holders" }
    Start-Sleep -Milliseconds 500
    try {
        Copy-Item (Join-Path $outDir "eling-backend.exe") $binDir -Force -ErrorAction Stop
        $installed = $true
    } catch {
        if ($attempt -ge 5) { throw }
        Write-Host "  [try $attempt] copy blocked, re-killing..."
        Start-Sleep -Seconds 1
    }
}
Remove-Item (Join-Path $binDir "eling-dashboard-ui") -Recurse -Force -ErrorAction SilentlyContinue
Copy-Item (Join-Path $outDir "eling-dashboard-ui") (Join-Path $binDir "eling-dashboard-ui") -Recurse -Force

if ($SkipSmokeTest) {
    Write-Host "== Installed (smoke test skipped) =="
    exit 0
}

Write-Host "== Smoke test: dashboard health =="
$projectDir = Join-Path $env:TEMP ("eling-smoke-" + [guid]::NewGuid().ToString("N").Substring(0, 8))
New-Item (Join-Path $projectDir ".eling") -ItemType Directory -Force | Out-Null

$psi = New-Object System.Diagnostics.ProcessStartInfo
$psi.FileName = (Join-Path $binDir "eling-backend.exe")
$psi.WorkingDirectory = $projectDir
$psi.UseShellExecute = $false
$psi.CreateNoWindow = $true
$psi.RedirectStandardInput = $true
$psi.RedirectStandardOutput = $true
$psi.RedirectStandardError = $true
$process = [System.Diagnostics.Process]::Start($psi)
[void]$process.StandardError.ReadToEndAsync()

function Stop-SmokeProcess {
    # Collect the smoke tree before killing anything, so nothing is left holding the temp dir.
    $smokeTree = @($process.Id) + @(Get-CimInstance Win32_Process -ErrorAction SilentlyContinue |
        Where-Object { $_.ParentProcessId -eq $process.Id } |
        Select-Object -ExpandProperty ProcessId)
    foreach ($targetPid in $smokeTree) {
        Stop-Process -Id $targetPid -Force -ErrorAction SilentlyContinue
    }
    # Scoped to global-bin holders, matching the install block above. A broad command-line
    # match would also hit a dev launched from .bin-opencode\eling-backend.exe, which is the
    # apphost dotnet watch starts for eling_dev (port 4417) and is not "dev from source".
    Get-CimInstance Win32_Process -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -in @('eling-backend.exe', 'eling.exe') } |
        Where-Object { $_.ExecutablePath -eq $globalBackend } |
        ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }

    # Robust directory cleanup with retry to allow OS handles to release
    for ($i = 0; $i -lt 5; $i++) {
        if (-not (Test-Path $projectDir)) { break }
        Remove-Item $projectDir -Recurse -Force -ErrorAction SilentlyContinue
        if (Test-Path $projectDir) { Start-Sleep -Milliseconds 200 }
    }
}

try {
    $health = $null
    try {
        Start-Sleep -Seconds 15
        $response = Invoke-WebRequest -Uri "http://127.0.0.1:4317/health" -UseBasicParsing -TimeoutSec 3
        $health = $response.Content
    } catch {
        $health = $null
    }

    if (-not $health) {
        throw "Smoke test FAILED: dashboard did not answer /health."
    }
    Write-Host "  health: $health"

    Write-Host "== Smoke test: MCP memory read/write =="
    function Send-Mcp($object) {
        $line = $object | ConvertTo-Json -Depth 8 -Compress
        $process.StandardInput.WriteLine($line)
        $process.StandardInput.Flush()
    }

    function Read-McpResponse {
        return $process.StandardOutput.ReadLine()
    }

    Send-Mcp @{ jsonrpc = '2.0'; id = 1; method = 'initialize'; params = @{
        protocolVersion = '2024-11-05'; capabilities = @{};
        clientInfo = @{ name = 'publish-smoke'; version = '1.0' } } }
    $initLine = Read-McpResponse 30
    if (-not $initLine -or $initLine -notmatch '"id":1') {
        throw "Smoke test FAILED: MCP initialize got no response. Raw: $initLine"
    }

    Send-Mcp @{ jsonrpc = '2.0'; method = 'notifications/initialized' }

    $stamp = [guid]::NewGuid().ToString("N").Substring(0, 8)
    $content = "publish-global smoke test $stamp"

    Send-Mcp @{ jsonrpc = '2.0'; id = 2; method = 'tools/call'; params = @{
        name = 'memory_save'; arguments = @{
            content = $content; type = 'note'; tags = @('smoke') } } }
    $saveLine = Read-McpResponse 30

    if (-not $saveLine -or $saveLine -notmatch '01[0-9a-hjkmnp-tv-z]{24}') {
        $preview = if ($saveLine) { $saveLine.Substring(0, [Math]::Min(400, $saveLine.Length)) } else { "<null: no response within timeout>" }
        throw "Smoke test FAILED: memory_save returned no usable response. Raw: $preview"
    }
    $savedId = $Matches[0]

    Send-Mcp @{ jsonrpc = '2.0'; id = 3; method = 'tools/call'; params = @{
        name = 'memory_get'; arguments = @{ id = $savedId; scope = 'project' } } }
    $getLine = Read-McpResponse 30

    if (-not $getLine -or $getLine -notmatch [regex]::Escape($savedId)) {
        throw "Smoke test FAILED: memory_get did not return the saved memory (id=$savedId). Raw: $getLine"
    }
} finally {
    Stop-SmokeProcess
}

Write-Host ""
Write-Host "Installed & verified:"
Write-Host "  health:           $health"
Write-Host "  memory read/write: OK (saved & searched back id=$savedId)"
Write-Host "  binary:            $binDir\eling-backend.exe"
