[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repoRoot = $PSScriptRoot
& (Join-Path $repoRoot "scripts\check-powershell-ascii.ps1") -RepoRoot $repoRoot
. (Join-Path $repoRoot "scripts\docker-runtime.ps1")

Initialize-TimelineDocker -RepoRoot $repoRoot
Start-TimelineHelperServer -RepoRoot $repoRoot -AudioProductPath "C:\apps\TimelineForAudio"

$docker = Get-TimelineDockerCommand
$composeArgs = Get-TimelineComposeArgs -RepoRoot $repoRoot

foreach ($path in @("C:\TimelineData\Timeline\work", "C:\TimelineData\Timeline\store")) {
    if (-not (Test-Path -LiteralPath $path)) {
        New-Item -ItemType Directory -Path $path | Out-Null
    }
}

Write-Host "Starting Timeline web and worker..."
Invoke-TimelineWithFileLock -RepoRoot $repoRoot -LockName "docker-compose.lock" -ScriptBlock {
    $logDir = Join-Path $repoRoot ".docker"
    if (-not (Test-Path -LiteralPath $logDir)) {
        New-Item -ItemType Directory -Path $logDir | Out-Null
    }
    $stdoutLog = Join-Path $logDir "compose-up.stdout.log"
    $stderrLog = Join-Path $logDir "compose-up.stderr.log"
    $dockerConfigDir = Join-Path $logDir "docker-config"
    $dockerConfigPath = Join-Path $dockerConfigDir "config.json"
    Remove-Item -LiteralPath $stdoutLog, $stderrLog -ErrorAction SilentlyContinue
    if (-not (Test-Path -LiteralPath $dockerConfigDir)) {
        New-Item -ItemType Directory -Path $dockerConfigDir | Out-Null
    }
    if (-not (Test-Path -LiteralPath $dockerConfigPath)) {
        Set-Content -LiteralPath $dockerConfigPath -Value "{}" -Encoding ASCII
    }

    Push-Location $repoRoot
    $previousDockerConfig = $env:DOCKER_CONFIG
    try {
        $env:DOCKER_CONFIG = $dockerConfigDir
        $process = Start-Process `
            -FilePath $docker `
            -ArgumentList (@("compose") + @($composeArgs) + @("up", "-d", "--build", "--remove-orphans", "web", "worker")) `
            -WorkingDirectory $repoRoot `
            -NoNewWindow `
            -PassThru `
            -Wait `
            -RedirectStandardOutput $stdoutLog `
            -RedirectStandardError $stderrLog

        $composeExitCode = [int]$process.ExitCode
    }
    finally {
        $env:DOCKER_CONFIG = $previousDockerConfig
        Pop-Location
    }

    foreach ($logPath in @($stdoutLog, $stderrLog)) {
        if (Test-Path -LiteralPath $logPath) {
            Get-Content -LiteralPath $logPath -ErrorAction SilentlyContinue | ForEach-Object { Write-Host $_ }
        }
    }

    if ($composeExitCode -ne 0) {
        throw "docker compose failed with exit code $composeExitCode."
    }
}

$webReady = $false
for ($attempt = 1; $attempt -le 60; $attempt += 1) {
    try {
        $response = Invoke-WebRequest -UseBasicParsing -TimeoutSec 2 "http://127.0.0.1:19000/api/health"
        if ([int]$response.StatusCode -ge 200 -and [int]$response.StatusCode -lt 300) {
            $webReady = $true
            break
        }
    }
    catch {
    }
    Start-Sleep -Seconds 1
}

if (-not $webReady) {
    throw "Timeline web did not become ready at http://127.0.0.1:19000."
}

Write-Host ""
Write-Host "Timeline is running."
Write-Host "Web UI:"
Write-Host "  http://127.0.0.1:19000"
Write-Host ""
Write-Host "Connected local products:"
Write-Host "  C:\apps\TimelineForAudio"
Write-Host "  C:\apps\TimelineForWindowsCodex"
Write-Host "  C:\apps\TimelineForChatGPT"
Write-Host "  C:\apps\TimelineForImage"
Write-Host ""
Write-Host "Health:"
Write-Host "  Web: OK"
if (Test-TimelineHelperServer) {
    Write-Host "  Helper: OK"
}
else {
    Write-Warning "Timeline helper server is not responding."
}
Start-Process "http://127.0.0.1:19000" | Out-Null
exit 0
