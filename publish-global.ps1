# Publish Eling from source and install it into ~/.local/bin ("install from source").
# Smoke test verifies: dashboard health + full MCP memory read/write round-trip.
# Usage:  .\publish-global.ps1 [-Configuration Release] [-Rid win-x64] [-SkipSmokeTest]

param(
    [string]$Configuration = "Release",
    [string]$Rid = "win-x64",
    [switch]$SkipSmokeTest
)

$ErrorActionPreference = "Stop"
$repoRoot = $PSScriptRoot
$outDir = Join-Path $env:TEMP "eling-publish-global"
$binDir = Join-Path $env:USERPROFILE ".local\bin"

Write-Host "== Publishing ($Configuration / $Rid) =="
if (Test-Path $outDir) { Remove-Item $outDir -Recurse -Force }

foreach ($project in @("Eling.Host", "Eling.Dashboard")) {
    Write-Host "-- dotnet publish src/backend/$project"
    dotnet publish (Join-Path $repoRoot "src/backend/$project") `
        -c $Configuration -r $Rid `
        --self-contained true `
        -p:PublishSingleFile=true `
        -p:IncludeNativeLibrariesForSelfExtract=true `
        -o $outDir --nologo -v q
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed for $project" }
}

foreach ($expected in @("eling.exe", "eling-dashboard.exe", "eling-dashboard-ui")) {
    if (-not (Test-Path (Join-Path $outDir $expected))) {
        throw "Publish output missing: $expected"
    }
}

Write-Host "== Installing into $binDir =="
New-Item $binDir -ItemType Directory -Force | Out-Null

# Stop only eling's own processes so file copies are not blocked.
Get-Process -Name eling, eling-dashboard -ErrorAction SilentlyContinue |
    Stop-Process -Force -ErrorAction SilentlyContinue
Start-Sleep -Seconds 1

Copy-Item (Join-Path $outDir "eling.exe") $binDir -Force
Copy-Item (Join-Path $outDir "eling-dashboard.exe") $binDir -Force
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
$psi.FileName = (Join-Path $binDir "eling.exe")
$psi.WorkingDirectory = $projectDir
$psi.UseShellExecute = $false
$psi.CreateNoWindow = $true
$psi.RedirectStandardInput = $true
$psi.RedirectStandardOutput = $true
$psi.RedirectStandardError = $true
$process = [System.Diagnostics.Process]::Start($psi)

function Stop-SmokeProcess {
    if (-not $process.HasExited) { Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue }
    Get-CimInstance Win32_Process |
        Where-Object { $_.CommandLine -match 'eling-dashboard' } |
        ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
    Remove-Item $projectDir -Recurse -Force -ErrorAction SilentlyContinue
}

$health = $null
try {
    Start-Sleep -Seconds 15
    $response = Invoke-WebRequest -Uri "http://127.0.0.1:4317/health" -UseBasicParsing -TimeoutSec 3
    $health = $response.Content
} catch {
    $health = $null
}

if (-not $health) {
    Stop-SmokeProcess
    throw "Smoke test FAILED: dashboard did not answer /health."
}
Write-Host "  health: $health"

Write-Host "== Smoke test: MCP memory read/write =="
$script:pending = $null

function Send-Mcp($object) {
    $line = $object | ConvertTo-Json -Depth 8 -Compress
    $process.StandardInput.WriteLine($line)
    $process.StandardInput.Flush()
}

function Wait-McpResponse([int]$targetId, [int]$timeoutSeconds) {
    # One continuous stdout reader; returns the RAW line whose response id matches.
    $deadline = [datetime]::UtcNow.AddSeconds($timeoutSeconds)
    while ([datetime]::UtcNow -lt $deadline) {
        if (-not $script:pending) {
            $script:pending = $process.StandardOutput.ReadLineAsync()
        }
        if ($script:pending.Wait(300)) {
            $line = $script:pending.Result
            $script:pending = $null
            try {
                $json = $line | ConvertFrom-Json
                if ($json.id -eq $targetId) { return $line }
            } catch { }
        }
    }
    return $null
}

Send-Mcp @{ jsonrpc = '2.0'; id = 1; method = 'initialize'; params = @{
    protocolVersion = '2024-11-05'; capabilities = @{};
    clientInfo = @{ name = 'publish-smoke'; version = '1.0' } } }
if (-not (Wait-McpResponse 1 30)) {
    Stop-SmokeProcess
    throw "Smoke test FAILED: MCP initialize got no response."
}

Send-Mcp @{ jsonrpc = '2.0'; method = 'notifications/initialized' }

$stamp = [guid]::NewGuid().ToString("N").Substring(0, 8)
$content = "publish-global smoke test $stamp"

Send-Mcp @{ jsonrpc = '2.0'; id = 2; method = 'tools/call'; params = @{
    name = 'memory_save'; arguments = @{
        content = $content; type = 'note'; tags = @('smoke') } } }
$saveLine = Wait-McpResponse 2 30

Send-Mcp @{ jsonrpc = '2.0'; id = 3; method = 'tools/call'; params = @{
    name = 'memory_search'; arguments = @{ query = $content } } }
$searchLine = Wait-McpResponse 3 30

Stop-SmokeProcess

if (-not $saveLine -or $saveLine -notmatch '01[0-9a-hjkmnp-tv-z]{24}') {
    $preview = if ($saveLine) { $saveLine.Substring(0, [Math]::Min(400, $saveLine.Length)) } else { "<null: no response within timeout>" }
    throw "Smoke test FAILED: memory_save returned no usable response. Raw: $preview"
}
$savedId = $Matches[0]

if (-not $searchLine -or $searchLine -notmatch [regex]::Escape($savedId)) {
    throw "Smoke test FAILED: memory_search did not return the saved memory (id=$savedId)."
}

Write-Host ""
Write-Host "Installed & verified:"
Write-Host "  health:           $health"
Write-Host "  memory read/write: OK (saved & searched back id=$savedId)"
Write-Host "  binary:            $binDir\eling.exe"
Write-Host "  dashboard binary:  $binDir\eling-dashboard.exe"
