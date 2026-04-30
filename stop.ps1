[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repoRoot = $PSScriptRoot
. (Join-Path $repoRoot "scripts\docker-runtime.ps1")

Initialize-TimelineDocker -RepoRoot $repoRoot
$docker = Get-TimelineDockerCommand
$composeArgs = Get-TimelineComposeArgs -RepoRoot $repoRoot

try {
    Invoke-TimelineWithFileLock -RepoRoot $repoRoot -LockName "docker-compose.lock" -ScriptBlock {
        & $docker compose @composeArgs down --remove-orphans
    }
}
finally {
    Stop-TimelineHelperServer
}

exit (Get-TimelineLastExitCode)
