[CmdletBinding()]
param(
    [switch]$NoOpen
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repoRoot = $PSScriptRoot
& (Join-Path $repoRoot "scripts\check-powershell-ascii.ps1") -RepoRoot $repoRoot
. (Join-Path $repoRoot "scripts\docker-runtime.ps1")

$runtime = Ensure-TimelineRuntimeSettings -RepoRoot $repoRoot
Initialize-TimelineDocker -RepoRoot $repoRoot

$helperPort = 0
$helperStartError = $null
foreach ($candidatePort in ([int]$runtime.HelperPortStart)..([int]$runtime.HelperPortEnd)) {
    try {
        Start-TimelineLocalApiServer `
            -RepoRoot $repoRoot `
            -Port $candidatePort `
            -WebPort $runtime.WebPort
        $helperPort = $candidatePort
        break
    }
    catch {
        $helperStartError = $_
        Stop-TimelineLocalApiServer -Port $candidatePort
        Write-Warning "Timeline local API server did not start on port $candidatePort. Trying the next port."
    }
}

if ($helperPort -le 0) {
    throw "Timeline local API server did not start on any candidate port. $($helperStartError.Exception.Message)"
}

$docker = Get-TimelineDockerCommand
$composeArgs = Get-TimelineComposeArgs -RepoRoot $repoRoot
$env:TIMELINE_HELPER_PORT = [string]$helperPort
$env:TIMELINE_WEB_PORT = [string]$runtime.WebPort
$env:TIMELINE_OLLAMA_PORT = [string]$runtime.OllamaPort
$env:TIMELINE_IMAGE_TAG = [string]$runtime.ImageTag
$env:TIMELINE_OLLAMA_VOLUME_NAME = [string]$runtime.OllamaVolumeName
$ollamaModel = [string]$runtime.OllamaModel

function Get-TimelineStartParentForNamedChild {
    param(
        [string]$Path,
        [string]$ChildName
    )

    if (-not $Path) {
        return ""
    }
    $trimmed = $Path.TrimEnd([char[]]@('\', '/'))
    if (-not $trimmed) {
        return ""
    }
    $leaf = Split-Path -Path $trimmed -Leaf
    if (-not $leaf.Equals($ChildName, [System.StringComparison]::OrdinalIgnoreCase)) {
        return ""
    }
    return (Split-Path -Path $trimmed -Parent)
}

function Resolve-TimelineStartDataRoot {
    param([string]$RepoRoot)

    $dataRoot = "data"
    $settingsPath = Join-Path $RepoRoot "settings.json"
    if (Test-Path -LiteralPath $settingsPath -PathType Leaf) {
        try {
            $payload = Get-Content -LiteralPath $settingsPath -Raw -Encoding UTF8 | ConvertFrom-Json
            $dataRootProperty = $payload.PSObject.Properties["dataRoot"]
            $candidate = ""
            if ($null -ne $dataRootProperty) {
                $candidate = [string]$dataRootProperty.Value
            }
            if ($candidate) {
                $dataRoot = $candidate
            }
            else {
                $workDirectoryProperty = $payload.PSObject.Properties["workDirectory"]
                $storeDirectoryProperty = $payload.PSObject.Properties["storeDirectory"]
                $workDirectory = ""
                $storeDirectory = ""
                if ($null -ne $workDirectoryProperty) {
                    $workDirectory = [string]$workDirectoryProperty.Value
                }
                if ($null -ne $storeDirectoryProperty) {
                    $storeDirectory = [string]$storeDirectoryProperty.Value
                }
                $workParent = Get-TimelineStartParentForNamedChild -Path $workDirectory -ChildName "work"
                $storeParent = Get-TimelineStartParentForNamedChild -Path $storeDirectory -ChildName "store"
                $toTimelineParent = Get-TimelineStartParentForNamedChild -Path $storeDirectory -ChildName "to_timeline"
                foreach ($legacy in @($toTimelineParent, $storeParent, $workParent)) {
                    if ($legacy) {
                        $dataRoot = $legacy
                        break
                    }
                }
            }
        }
        catch {
            $dataRoot = "data"
        }
    }

    if ([System.IO.Path]::IsPathRooted($dataRoot)) {
        return [System.IO.Path]::GetFullPath($dataRoot)
    }
    return [System.IO.Path]::GetFullPath((Join-Path $RepoRoot $dataRoot))
}

$timelineDataRoot = Resolve-TimelineStartDataRoot -RepoRoot $repoRoot
$timelineWorkSource = Join-Path $timelineDataRoot "work"
$timelineStoreSource = Join-Path $timelineDataRoot "to_timeline"
$env:TIMELINE_WORK_SOURCE = $timelineWorkSource
$env:TIMELINE_STORE_SOURCE = $timelineStoreSource

foreach ($path in @($timelineDataRoot, $timelineWorkSource, $timelineStoreSource)) {
    if (-not (Test-Path -LiteralPath $path)) {
        New-Item -ItemType Directory -Path $path | Out-Null
    }
}

Write-Host "Starting Timeline web, worker, and Ollama..."
Write-Host ("Compose project: {0}" -f $runtime.ComposeProjectName)
Write-Host ("Image tag: {0}" -f $runtime.ImageTag)
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

    $volumeInspectStdoutLog = Join-Path $logDir "volume-inspect.stdout.log"
    $volumeInspectStderrLog = Join-Path $logDir "volume-inspect.stderr.log"
    $volumeCreateStdoutLog = Join-Path $logDir "volume-create.stdout.log"
    $volumeCreateStderrLog = Join-Path $logDir "volume-create.stderr.log"
    Remove-Item -LiteralPath $volumeInspectStdoutLog, $volumeInspectStderrLog, $volumeCreateStdoutLog, $volumeCreateStderrLog -ErrorAction SilentlyContinue
    $volumeInspectProcess = Start-Process `
        -FilePath $docker `
        -ArgumentList @("volume", "inspect", $runtime.OllamaVolumeName) `
        -WorkingDirectory $repoRoot `
        -NoNewWindow `
        -PassThru `
        -Wait `
        -RedirectStandardOutput $volumeInspectStdoutLog `
        -RedirectStandardError $volumeInspectStderrLog
    if ([int]$volumeInspectProcess.ExitCode -ne 0) {
        $volumeCreateProcess = Start-Process `
            -FilePath $docker `
            -ArgumentList @("volume", "create", $runtime.OllamaVolumeName) `
            -WorkingDirectory $repoRoot `
            -NoNewWindow `
            -PassThru `
            -Wait `
            -RedirectStandardOutput $volumeCreateStdoutLog `
            -RedirectStandardError $volumeCreateStderrLog
        if ([int]$volumeCreateProcess.ExitCode -ne 0) {
            throw "Failed to create Docker volume: $($runtime.OllamaVolumeName)"
        }
    }

    Push-Location $repoRoot
    $previousDockerConfig = $env:DOCKER_CONFIG
    try {
        $env:DOCKER_CONFIG = $dockerConfigDir
        $process = Start-Process `
            -FilePath $docker `
            -ArgumentList (@("compose") + @($composeArgs) + @("up", "-d", "--build", "--remove-orphans", "ollama", "web", "worker")) `
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

$ollamaReady = $false
$ollamaModelReady = $false
$ollamaBaseUrl = "http://127.0.0.1:$($runtime.OllamaPort)"
for ($attempt = 1; $attempt -le 120; $attempt += 1) {
    try {
        $tags = Invoke-RestMethod -UseBasicParsing -TimeoutSec 2 "$ollamaBaseUrl/api/tags"
        $ollamaReady = $true
        foreach ($model in @($tags.models)) {
            if ([string]$model.name -eq $ollamaModel) {
                $ollamaModelReady = $true
                break
            }
        }
        break
    }
    catch {
    }
    Start-Sleep -Seconds 1
}

if (-not $ollamaReady) {
    throw "Ollama did not become ready at $ollamaBaseUrl."
}

if (-not $ollamaModelReady) {
    Write-Host "Pulling Ollama model $ollamaModel. This can take a while on first run..."
    try {
        $pullBody = @{
            name = $ollamaModel
            stream = $false
        } | ConvertTo-Json -Compress
        Invoke-RestMethod `
            -UseBasicParsing `
            -Method Post `
            -ContentType "application/json" `
            -Body $pullBody `
            -TimeoutSec 7200 `
            -Uri "$ollamaBaseUrl/api/pull" | Out-Null
    }
    catch {
        throw "Ollama model pull failed. $($_.Exception.Message)"
    }
    $ollamaModelReady = $true
}

$webReady = $false
$webBaseUrl = "http://127.0.0.1:$($runtime.WebPort)"
for ($attempt = 1; $attempt -le 60; $attempt += 1) {
    try {
        $response = Invoke-WebRequest -UseBasicParsing -TimeoutSec 2 "$webBaseUrl/api/health"
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
    throw "Timeline web did not become ready at $webBaseUrl."
}

Write-Host ""
Write-Host "Timeline is running."
Write-Host "Web UI:"
Write-Host "  $webBaseUrl"
if (-not $NoOpen) {
    Start-Process $webBaseUrl | Out-Null
}
Write-Host "Local API:"
Write-Host "  http://127.0.0.1:$helperPort"
Write-Host ""
Write-Host "Health:"
Write-Host "  Web: OK"
if (Test-TimelineHelperServer -Port $helperPort) {
    Write-Host "  Local API: OK"
}
else {
    Write-Warning "Timeline local API server is not responding."
}
if ($ollamaReady) {
    Write-Host "  Ollama: OK"
}
else {
    Write-Warning "Ollama is not responding."
}
if ($ollamaModelReady) {
    Write-Host "  Ollama model ${ollamaModel}: OK"
}
else {
    Write-Warning "Ollama model $ollamaModel is not available."
}

Write-Host ""
Write-Host "Connected products:"
try {
    $runtime = Invoke-RestMethod -UseBasicParsing -TimeoutSec 30 "http://127.0.0.1:$helperPort/products/runtime/status"
    foreach ($product in @($runtime.products)) {
        Write-Host ("  {0}: {1} [{2}]" -f ([string]$product.displayName), ([string]$product.productPath), ([string]$product.state))
    }
}
catch {
    Write-Host "  Product runtime status is not available."
}
exit 0
