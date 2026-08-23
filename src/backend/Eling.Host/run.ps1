# Starts the Next.js dev server in the background, then runs the .NET backend.
# When the backend exits, the dev server is stopped.

$frontendDir = Join-Path $PSScriptRoot ".." ".." "frontend" "Eling.Dashboard"
$devJob = Start-Job -ScriptBlock {
    param($dir)
    Set-Location $dir
    pnpm dev
} -ArgumentList $frontendDir

try {
    dotnet run --project $PSScriptRoot\Eling.Host.csproj
} finally {
    Stop-Job $devJob -Force
    Remove-Job $devJob
}