[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repoRoot = $PSScriptRoot
. (Join-Path $repoRoot "scripts\docker-runtime.ps1")

Initialize-TimelineDocker -RepoRoot $repoRoot
Start-TimelineHelperServer -RepoRoot $repoRoot -AudioProductPath "C:\apps\TimelineForAudio"

$docker = Get-TimelineDockerCommand
$composeArgs = Get-TimelineComposeArgs -RepoRoot $repoRoot

Write-Host "Starting Timeline web..."
Invoke-TimelineWithFileLock -RepoRoot $repoRoot -LockName "docker-compose.lock" -ScriptBlock {
    & $docker compose @composeArgs up -d --build --remove-orphans web
    if (-not $?) {
        throw "docker compose failed."
    }
}

Write-Host ""
Write-Host "Timeline is running."
Write-Host "Web UI:"
Write-Host "  http://127.0.0.1:19000"
Write-Host ""
Write-Host "Connected local product:"
Write-Host "  C:\apps\TimelineForAudio"
Write-Host ""
Write-Host "Docker status:"
& $docker compose @composeArgs ps
Start-Process "http://127.0.0.1:19000" | Out-Null
exit (Get-TimelineLastExitCode)
