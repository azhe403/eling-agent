# Eling installer (Windows)
# Usage:  irm https://raw.githubusercontent.com/azhe403/eling-agent/main/scripts/install.ps1 | iex
# Repair dashboard UI only:  .\scripts\install.ps1 -DashboardOnly

param(
    [switch]$DashboardOnly
)

$ErrorActionPreference = "Stop"
$repo = "azhe403/eling-agent"

$binDir = "$env:USERPROFILE\.local\bin"
$tmp = "$env:TEMP\eling-install"
$tmpZip = "$env:TEMP\eling-install.zip"

$release = $null
$releases = Invoke-RestMethod "https://api.github.com/repos/$repo/releases?per_page=20"
$release = $releases | Where-Object { -not $_.draft -and -not $_.prerelease } | Select-Object -First 1
if (-not $release) { $release = $releases | Where-Object { -not $_.draft } | Select-Object -First 1 }
if (-not $release) { throw "No releases (stable or pre-release) found for $repo" }

if ($DashboardOnly) {
    $uiDir = "$binDir\eling-dashboard-ui"
    if (Test-Path "$uiDir\index.html") {
        Write-Host "Dashboard UI looks healthy - re-downloading anyway."
    } else {
        Write-Host "Dashboard UI missing or corrupt - re-downloading."
    }
    $uiAsset = $release.assets | Where-Object { $_.name -eq "eling-dashboard-ui.zip" } | Select-Object -First 1
    if (-not $uiAsset) { throw "eling-dashboard-ui.zip not found in selected release (stable or pre-release)" }
    Invoke-WebRequest $uiAsset.browser_download_url -OutFile $tmpZip -UseBasicParsing
    Get-Process -Name eling-backend, eling -ErrorAction SilentlyContinue |
        Stop-Process -Force -ErrorAction SilentlyContinue
    Start-Sleep -Seconds 1
    Remove-Item $uiDir -Recurse -Force -ErrorAction SilentlyContinue
    Expand-Archive $tmpZip -DestinationPath $uiDir -Force
    if (-not (Test-Path "$uiDir\index.html")) { throw "Repair FAILED: index.html still missing after reinstall." }
    Remove-Item $tmpZip -Force -ErrorAction SilentlyContinue
    Write-Host ""
    Write-Host "Dashboard UI repaired"
    Write-Host "  dir: $uiDir"
    exit 0
}
$asset = $release.assets | Where-Object { $_.name -eq "eling-backend-win-x64.zip" } | Select-Object -First 1
if (-not $asset) { throw "eling-backend-win-x64.zip not found in selected release (stable or pre-release)" }

Write-Host "Downloading eling $($release.tag_name)..."
Invoke-WebRequest $asset.browser_download_url -OutFile $tmpZip -UseBasicParsing

# .local/bin may hold other tools — remove only eling's own files
New-Item $binDir -ItemType Directory -Force | Out-Null
Remove-Item "$binDir\eling-backend.exe", "$binDir\eling-backend.pdb",
            "$binDir\eling.exe", "$binDir\eling.pdb",
            "$binDir\eling-dashboard.exe", "$binDir\eling-dashboard.pdb" -Force -ErrorAction SilentlyContinue

if (Test-Path $tmp) { Remove-Item $tmp -Recurse -Force }
Expand-Archive $tmpZip -DestinationPath $tmp -Force

# Single binary eling-backend (with eling-dashboard-ui next to it).
Copy-Item "$tmp\eling-backend.exe" $binDir -Force
if (Test-Path "$tmp\eling-dashboard-ui") {
    Remove-Item "$binDir\eling-dashboard-ui" -Recurse -Force -ErrorAction SilentlyContinue
    Copy-Item "$tmp\eling-dashboard-ui" "$binDir\eling-dashboard-ui" -Recurse -Force
}

Remove-Item $tmp -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item $tmpZip -Force -ErrorAction SilentlyContinue

$userPath = [Environment]::GetEnvironmentVariable("Path", "User")
if ($userPath -notlike "*$binDir*") {
    [Environment]::SetEnvironmentVariable("Path", "$userPath;$binDir", "User")
    Write-Host "Added $binDir to user PATH (restart terminal to apply)."
}

Write-Host ""
Write-Host "eling $($release.tag_name) installed"
Write-Host "  binary:           $binDir\eling-backend.exe"
Write-Host "Run 'eling-backend' in a NEW terminal to start the MCP server."
