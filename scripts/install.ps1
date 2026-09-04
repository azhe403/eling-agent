# Eling installer (Windows)
# Usage:  irm https://raw.githubusercontent.com/azhe403/eling-agent/main/install.ps1 | iex

$ErrorActionPreference = "Stop"
$repo = "azhe403/eling-agent"

$release = Invoke-RestMethod "https://api.github.com/repos/$repo/releases/latest"
$asset = $release.assets | Where-Object { $_.name -eq "eling-win-x64.zip" } | Select-Object -First 1
if (-not $asset) { throw "eling-win-x64.zip not found in latest release" }

$binDir = "$env:USERPROFILE\.local\bin"
$tmp = "$env:TEMP\eling-install"
$tmpZip = "$env:TEMP\eling-install.zip"

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
