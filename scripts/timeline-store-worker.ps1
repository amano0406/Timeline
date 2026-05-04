[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$JobId,
    [string]$TimelineProductPath = "C:\apps\Timeline",
    [string]$AudioProductPath = "C:\apps\TimelineForAudio",
    [string]$WindowsCodexProductPath = "C:\apps\TimelineForWindowsCodex",
    [string]$ChatGptProductPath = "C:\apps\TimelineForChatGPT",
    [string]$ImageProductPath = "C:\apps\TimelineForImage"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

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
    -ImportOnly

function Set-TimelineStoreWorkerStatus {
    param(
        [string]$State,
        [string]$Stage,
        [string]$Message,
        [string]$ErrorMessage = "",
        [object]$Result = $null
    )

    $now = [DateTimeOffset]::Now.ToString("o")
    $itemCount = 0
    $eventCount = 0
    if ($null -ne $Result) {
        $itemCount = Convert-TimelineAudioInt -Value (Get-PropertyValue -Object $Result -Name "itemCount" -Default 0)
        $eventCount = Convert-TimelineAudioInt -Value (Get-PropertyValue -Object $Result -Name "eventCount" -Default 0)
    }

    Write-TimelineWorkerJobStatus -Status ([ordered]@{
        jobId = $JobId
        kind = "timeline_rebuild"
        state = $State
        stage = $Stage
        message = $Message
        error = $ErrorMessage
        startedAt = $script:TimelineStoreWorkerStartedAt
        updatedAt = $now
        completedAt = if (@("completed", "failed") -contains $State) { $now } else { "" }
        itemCount = $itemCount
        eventCount = $eventCount
        result = $Result
    }) | Out-Null
}

$script:TimelineStoreWorkerStartedAt = [DateTimeOffset]::Now.ToString("o")

try {
    Set-TimelineStoreWorkerStatus `
        -State "running" `
        -Stage "collecting" `
        -Message "Collecting product downloads through product CLI scripts."

    $result = New-TimelineStoreRebuild

    Set-TimelineStoreWorkerStatus `
        -State "completed" `
        -Stage "completed" `
        -Message "Timeline store rebuild completed." `
        -Result $result
}
catch {
    Set-TimelineStoreWorkerStatus `
        -State "failed" `
        -Stage "failed" `
        -Message "Timeline store rebuild failed." `
        -ErrorMessage $_.Exception.Message
    exit 1
}
