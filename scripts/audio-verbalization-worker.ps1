[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$JobId,
    [Parameter(Mandatory = $true)]
    [string]$AudioItemId,
    [string]$TimelineProductPath = "",
    [string]$AudioProductPath = "",
    [string]$WindowsCodexProductPath = "",
    [string]$ChatGptProductPath = "",
    [string]$ImageProductPath = "",
    [string]$VideoProductPath = "",
    [string]$PcProductPath = ""
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

if (-not $TimelineProductPath) {
    $TimelineProductPath = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
}

$helperScript = Join-Path $TimelineProductPath "scripts\timeline-helper-server.ps1"
if (-not (Test-Path -LiteralPath $helperScript -PathType Leaf)) {
    throw "Timeline helper script was not found: $helperScript"
}

. $helperScript `
    -TimelineProductPath $TimelineProductPath `
    -AudioProductPath $AudioProductPath `
    -WindowsCodexProductPath $WindowsCodexProductPath `
    -ChatGptProductPath $ChatGptProductPath `
    -ImageProductPath $ImageProductPath `
    -VideoProductPath $VideoProductPath `
    -PcProductPath $PcProductPath `
    -ImportOnly

$directory = Get-TimelineAudioVerbalizationDirectory -AudioItemId $AudioItemId -Create
$planPath = Join-Path $directory "verbalization-plan.json"
$resultPath = Join-Path $directory "audio-verbalization.json"

function Write-AudioVerbalizationWorkerFailure {
    param([string]$Message)

    $status = [ordered]@{
        available = $true
        state = "failed"
        audioItemId = $AudioItemId
        sourceFileIdentity = ""
        language = "ja-JP"
        model = "qwen3.5:9b"
        totalTurns = 0
        verbalizedTurns = 0
        totalChunks = 0
        completedChunks = 0
        jobId = $JobId
        currentChunkId = ""
        planPath = $planPath
        resultPath = $resultPath
        startedAt = ""
        elapsedSec = 0
        estimatedRemainingSec = 0
        updatedAt = [DateTimeOffset]::Now.ToString("o")
        message = $Message
    }
    $turns = @()
    $chunks = @()

    if (Test-Path -LiteralPath $resultPath -PathType Leaf) {
        try {
            $payload = Get-Content -LiteralPath $resultPath -Raw -Encoding UTF8 | ConvertFrom-Json
            $status = Copy-TimelineAudioVerbalizationStatus -Status (Get-PropertyValue -Object $payload -Name "status" -Default $status)
            $status["state"] = "failed"
            $status["jobId"] = $JobId
            $status["updatedAt"] = [DateTimeOffset]::Now.ToString("o")
            $status["message"] = $Message
            $turns = @(Get-PropertyValue -Object $payload -Name "turns" -Default @())
            $chunks = @(Get-PropertyValue -Object $payload -Name "chunks" -Default @())
        }
        catch {
        }
    }

    Write-TimelineAudioVerbalizationResultPayload -ResultPath $resultPath -Status $status -Chunks $chunks -Turns $turns
    Write-TimelineOperationEvent `
        -OperationId $JobId `
        -Kind "worker" `
        -ProductName "Timeline" `
        -Action "audio_verbalization" `
        -State "failed" `
        -Message $Message `
        -Details ([ordered]@{
            audioItemId = $AudioItemId
            planPath = $planPath
            resultPath = $resultPath
        })
}

try {
    Write-TimelineOperationEvent `
        -OperationId $JobId `
        -Kind "worker" `
        -ProductName "Timeline" `
        -Action "audio_verbalization" `
        -State "running" `
        -Message "Audio verbalization worker started." `
        -Details ([ordered]@{
            audioItemId = $AudioItemId
            planPath = $planPath
            resultPath = $resultPath
        })

    if (-not (Test-Path -LiteralPath $planPath -PathType Leaf)) {
        throw "Audio verbalization plan was not found."
    }
    if (-not (Test-Path -LiteralPath $resultPath -PathType Leaf)) {
        throw "Audio verbalization result file was not found."
    }

    $plan = Get-Content -LiteralPath $planPath -Raw -Encoding UTF8 | ConvertFrom-Json
    $resultPayload = Get-Content -LiteralPath $resultPath -Raw -Encoding UTF8 | ConvertFrom-Json
    $initialStatus = Copy-TimelineAudioVerbalizationStatus -Status (Get-PropertyValue -Object $resultPayload -Name "status" -Default @{})
    $initialStatus["jobId"] = $JobId

    Invoke-TimelineAudioVerbalizationExecution `
        -Plan $plan `
        -Directory $directory `
        -InitialStatus $initialStatus `
        -ResultPath $resultPath | Out-Null
}
catch {
    Write-AudioVerbalizationWorkerFailure -Message $_.Exception.Message
    exit 1
}
