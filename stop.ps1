[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repoRoot = $PSScriptRoot
. (Join-Path $repoRoot "scripts\docker-runtime.ps1")

Initialize-TimelineDocker -RepoRoot $repoRoot
$docker = Get-TimelineDockerCommand
$composeArgs = Get-TimelineComposeArgs -RepoRoot $repoRoot
$composeExitCode = 0

try {
    Invoke-TimelineWithFileLock -RepoRoot $repoRoot -LockName "docker-compose.lock" -ScriptBlock {
        $dockerConfigDir = Join-Path $repoRoot ".docker\docker-config"
        $dockerConfigPath = Join-Path $dockerConfigDir "config.json"
        if (-not (Test-Path -LiteralPath $dockerConfigDir)) {
            New-Item -ItemType Directory -Path $dockerConfigDir | Out-Null
        }
        if (-not (Test-Path -LiteralPath $dockerConfigPath)) {
            Set-Content -LiteralPath $dockerConfigPath -Value "{}" -Encoding ASCII
        }

        $previousDockerConfig = $env:DOCKER_CONFIG
        try {
            $env:DOCKER_CONFIG = $dockerConfigDir
            & $docker compose @composeArgs down --remove-orphans
            $script:composeExitCode = Get-TimelineLastExitCode
        }
        finally {
            $env:DOCKER_CONFIG = $previousDockerConfig
        }
    }
}
finally {
    Stop-TimelineHelperServer
}

if ($composeExitCode -ne 0) {
    $webStillRunning = $false
    try {
        $response = Invoke-WebRequest -UseBasicParsing -TimeoutSec 1 "http://127.0.0.1:19000/api/health"
        $webStillRunning = [int]$response.StatusCode -ge 200 -and [int]$response.StatusCode -lt 300
    }
    catch {
        $webStillRunning = $false
    }
    if ($webStillRunning) {
        Write-Warning "docker compose down reported exit code $composeExitCode."
    }
}

exit 0
