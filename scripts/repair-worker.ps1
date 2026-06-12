[CmdletBinding()]
param(
    [string]$RepoRoot = ""
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

if (-not $RepoRoot) {
    $scriptPath = $PSCommandPath
    if (-not $scriptPath) {
        $scriptPath = $MyInvocation.MyCommand.Path
    }
    if (-not $scriptPath) {
        throw "Script path was not available."
    }
    $RepoRoot = Split-Path -Parent (Split-Path -Parent $scriptPath)
}

$RepoRoot = [System.IO.Path]::GetFullPath($RepoRoot)
. (Join-Path $RepoRoot "scripts\docker-runtime.ps1")

function Get-TimelineRepairJsonProperty {
    param(
        [object]$Object,
        [string]$Name,
        [object]$Default = $null
    )

    if ($null -eq $Object) {
        return $Default
    }
    $property = $Object.PSObject.Properties[$Name]
    if ($null -ne $property) {
        return $property.Value
    }
    return $Default
}

function Resolve-TimelineRepairDataRoot {
    param([string]$Root)

    $dataRoot = "data"
    $settingsPath = Join-Path $Root "settings.json"
    if (Test-Path -LiteralPath $settingsPath -PathType Leaf) {
        try {
            $payload = Get-Content -LiteralPath $settingsPath -Raw -Encoding UTF8 | ConvertFrom-Json
            $candidate = [string](Get-TimelineRepairJsonProperty -Object $payload -Name "dataRoot" -Default "")
            if ($candidate) {
                $dataRoot = $candidate
            }
        }
        catch {
            $dataRoot = "data"
        }
    }

    if ([System.IO.Path]::IsPathRooted($dataRoot)) {
        return [System.IO.Path]::GetFullPath($dataRoot)
    }
    return [System.IO.Path]::GetFullPath((Join-Path $Root $dataRoot))
}

function Wait-TimelineRepairWorkerHeartbeat {
    param(
        [string]$HeartbeatPath,
        [DateTimeOffset]$MinimumUpdatedAt,
        [int]$MaxAttempts = 40
    )

    for ($attempt = 1; $attempt -le $MaxAttempts; $attempt += 1) {
        if (Test-Path -LiteralPath $HeartbeatPath -PathType Leaf) {
            try {
                $payload = Get-Content -LiteralPath $HeartbeatPath -Raw -Encoding UTF8 | ConvertFrom-Json
                $state = [string](Get-TimelineRepairJsonProperty -Object $payload -Name "state" -Default "")
                $updatedAtText = [string](Get-TimelineRepairJsonProperty -Object $payload -Name "updatedAt" -Default "")
                $updatedAt = [DateTimeOffset]::MinValue
                $updatedAtValid = [DateTimeOffset]::TryParse($updatedAtText, [ref]$updatedAt)
                if ($state -eq "running" -and $updatedAtValid -and $updatedAt -ge $MinimumUpdatedAt) {
                    return
                }
            }
            catch {
            }
        }
        Start-Sleep -Seconds 1
    }

    throw "Timeline worker did not write a running heartbeat."
}

if (-not (Test-Path -LiteralPath $RepoRoot -PathType Container)) {
    throw "Repository root was not found: $RepoRoot"
}

$runtime = Ensure-TimelineRuntimeSettings -RepoRoot $RepoRoot
Initialize-TimelineDocker -RepoRoot $RepoRoot

$dataRoot = Resolve-TimelineRepairDataRoot -Root $RepoRoot
$workSource = Join-Path $dataRoot "work"
$storeSource = Join-Path $dataRoot "to_timeline"
$workerDirectory = Join-Path $workSource "worker"
$heartbeatPath = Join-Path $workerDirectory "docker-worker-heartbeat.json"

foreach ($path in @($dataRoot, $workSource, $storeSource, $workerDirectory)) {
    if (-not (Test-Path -LiteralPath $path -PathType Container)) {
        New-Item -ItemType Directory -Path $path -Force | Out-Null
    }
}

$docker = Get-TimelineDockerCommand
$composeArgs = Get-TimelineComposeArgs -RepoRoot $RepoRoot
$env:TIMELINE_HELPER_PORT = [string]$runtime.HelperPortStart
$env:TIMELINE_WEB_PORT = [string]$runtime.WebPort
$env:TIMELINE_OLLAMA_PORT = [string]$runtime.OllamaPort
$env:TIMELINE_IMAGE_TAG = [string]$runtime.ImageTag
$env:TIMELINE_OLLAMA_VOLUME_NAME = [string]$runtime.OllamaVolumeName
$env:TIMELINE_WORK_SOURCE = $workSource
$env:TIMELINE_STORE_SOURCE = $storeSource

$repairStartedAt = [DateTimeOffset]::Now.AddSeconds(-2)

Invoke-TimelineWithFileLock -RepoRoot $RepoRoot -LockName "docker-compose.lock" -ScriptBlock {
    Push-Location $RepoRoot
    try {
        & $docker @("compose") @($composeArgs) @("up", "-d", "--build", "worker")
        if (-not $?) {
            throw "docker compose up for Timeline worker failed."
        }
    }
    finally {
        Pop-Location
    }
}

Wait-TimelineRepairWorkerHeartbeat -HeartbeatPath $heartbeatPath -MinimumUpdatedAt $repairStartedAt

Write-Host "Timeline worker repair completed."
