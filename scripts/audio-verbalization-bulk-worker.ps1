[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$JobId,
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

function Write-AudioVerbalizationBulkWorkerFailure {
    param([string]$Message)

    $status = Copy-TimelineAudioVerbalizationStatus -Status (Get-TimelineAudioVerbalizationBulkStatus -JobId $JobId)
    if (-not $status.Contains("jobId") -or -not (Convert-TimelineText -Value (Get-PropertyValue -Object $status -Name "jobId" -Default ""))) {
        $status = New-TimelineAudioVerbalizationBulkStatus -JobId $JobId -State "failed" -Message $Message
    }
    $status["state"] = "failed"
    $status["message"] = $Message
    $status["completedAt"] = [DateTimeOffset]::Now.ToString("o")
    Write-TimelineAudioVerbalizationBulkStatus -Status $status
    Write-TimelineOperationEvent `
        -OperationId $JobId `
        -Kind "worker" `
        -ProductName "Timeline" `
        -Action "audio_verbalization_bulk" `
        -State "failed" `
        -Message $Message
}

try {
    Invoke-TimelineAudioVerbalizationBulkExecution -JobId $JobId | Out-Null
}
catch {
    Write-AudioVerbalizationBulkWorkerFailure -Message $_.Exception.Message
    exit 1
}
