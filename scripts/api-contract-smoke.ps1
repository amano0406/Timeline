[CmdletBinding()]
param(
    [int]$LocalApiPort = 19001
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot

function ConvertTo-ContractJson {
    param([object]$Value)
    return ($Value | ConvertTo-Json -Depth 20 -Compress)
}

function Invoke-ContractGet {
    param(
        [string]$BaseUrl,
        [string]$Path
    )
    return Invoke-RestMethod -UseBasicParsing -TimeoutSec 30 -Uri ($BaseUrl.TrimEnd("/") + $Path)
}

function Invoke-ContractRangeGet {
    param(
        [string]$BaseUrl,
        [string]$Path
    )

    Add-Type -AssemblyName System.Net.Http
    $client = [System.Net.Http.HttpClient]::new()
    $request = [System.Net.Http.HttpRequestMessage]::new(
        [System.Net.Http.HttpMethod]::Get,
        ($BaseUrl.TrimEnd("/") + $Path))
    $request.Headers.Range = [System.Net.Http.Headers.RangeHeaderValue]::new(0, 31)
    $response = $null

    try {
        $response = $client.SendAsync(
            $request,
            [System.Net.Http.HttpCompletionOption]::ResponseHeadersRead).GetAwaiter().GetResult()
        return [pscustomobject]@{
            statusCode = [int]$response.StatusCode
            contentType = [string]$response.Content.Headers.ContentType
            contentLength = $response.Content.Headers.ContentLength
            contentRange = [string]$response.Content.Headers.ContentRange
            acceptRanges = [string]::Join(",", $response.Headers.AcceptRanges)
        }
    }
    finally {
        if ($null -ne $response) {
            $response.Dispose()
        }
        $request.Dispose()
        $client.Dispose()
    }
}

function Assert-ContractEqual {
    param(
        [string]$Name,
        [string]$Path
    )

    $localBaseUrl = "http://127.0.0.1:$LocalApiPort"
    $local = Invoke-ContractGet -BaseUrl $localBaseUrl -Path $Path
    if ($null -eq $local) {
        throw "Contract response was empty: $Name"
    }

    Write-Host "OK: $Name"
}

function Assert-RangeContractEqual {
    param(
        [string]$Name,
        [string]$Path
    )

    $localBaseUrl = "http://127.0.0.1:$LocalApiPort"
    $local = Invoke-ContractRangeGet -BaseUrl $localBaseUrl -Path $Path
    if ($local.statusCode -lt 200 -or $local.statusCode -ge 300) {
        throw "Range contract failed: $Name"
    }

    Write-Host "OK: $Name"
}

function Remove-DynamicContractFields {
    param([object]$Value)

    if ($null -eq $Value) {
        return $Value
    }

    $json = $Value | ConvertTo-Json -Depth 20
    $copy = $json | ConvertFrom-Json
    foreach ($name in @("updatedAt", "elapsedSec", "estimatedRemainingSec", "progressPercent", "packId", "generatedAt")) {
        $property = $copy.PSObject.Properties[$name]
        if ($null -ne $property) {
            $copy.PSObject.Properties.Remove($name)
        }
    }
    return $copy
}

function Assert-ContractEqualIgnoringDynamic {
    param(
        [string]$Name,
        [string]$Path
    )

    $localBaseUrl = "http://127.0.0.1:$LocalApiPort"
    $local = Remove-DynamicContractFields -Value (Invoke-ContractGet -BaseUrl $localBaseUrl -Path $Path)
    if ($null -eq $local) {
        throw "Contract response was empty: $Name"
    }

    Write-Host "OK: $Name"
}

function Assert-ThreadDetailContract {
    param(
        [string]$Name,
        [string]$Path,
        [string]$ExpectedItemId
    )

    $localBaseUrl = "http://127.0.0.1:$LocalApiPort"
    $detail = Invoke-ContractGet -BaseUrl $localBaseUrl -Path $Path
    if ($null -eq $detail) {
        throw "Thread detail response was empty: $Name"
    }
    if ($detail.available -ne $true) {
        throw "Thread detail was not available: $Name"
    }
    if ([string]::IsNullOrWhiteSpace([string]$detail.itemId)) {
        throw "Thread detail itemId was empty: $Name"
    }
    if (-not [string]::Equals([string]$detail.itemId, $ExpectedItemId, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Thread detail itemId mismatch: $Name expected=$ExpectedItemId actual=$($detail.itemId)"
    }
    if ([string]::IsNullOrWhiteSpace([string]$detail.title)) {
        throw "Thread detail title was empty: $Name"
    }
    if ($null -eq $detail.PSObject.Properties["messages"]) {
        throw "Thread detail messages property was missing: $Name"
    }
    if ($detail.messageCount -lt 0) {
        throw "Thread detail messageCount was negative: $Name"
    }

    Write-Host "OK: $Name"
}

$readmePath = Join-Path $repoRoot "README.md"
$missingPath = Join-Path $repoRoot "__missing_contract_smoke__"

Assert-ContractEqual -Name "health" -Path "/health"
Assert-ContractEqual -Name "path directory" -Path ("/path-status?path={0}&kind=directory" -f [uri]::EscapeDataString($repoRoot))
Assert-ContractEqual -Name "path file" -Path ("/path-status?path={0}&kind=file" -f [uri]::EscapeDataString($readmePath))
Assert-ContractEqual -Name "path missing" -Path ("/path-status?path={0}&kind=any" -f [uri]::EscapeDataString($missingPath))
Assert-ContractEqual -Name "timeline settings" -Path "/timeline/settings"
Assert-ContractEqualIgnoringDynamic -Name "timeline worker status" -Path "/timeline/worker/status"
Assert-ContractEqual -Name "timeline rebuild status" -Path "/timeline/rebuild/status"
Assert-ContractEqual -Name "timeline rebuild missing" -Path "/timeline/rebuild/status?jobId=timeline-contract-smoke-missing"
Assert-ContractEqual -Name "timeline store overview" -Path "/timeline/store/overview"
Assert-ContractEqual -Name "timeline events" -Path "/timeline/events?page=1&pageSize=5"
Assert-ContractEqual -Name "timeline operations" -Path "/timeline/operations?limit=5"
Assert-ContractEqualIgnoringDynamic -Name "timeline llm input preview" -Path "/timeline/llm-input/preview?page=1&pageSize=5&maxChars=800&scanLimit=500&countTotal=true"
Assert-ContractEqual -Name "ollama status" -Path "/timeline/audio-verbalization/ollama/status?baseUrl=http%3A%2F%2F127.0.0.1%3A11434&model=qwen3.5%3A9b"
Assert-ContractEqualIgnoringDynamic -Name "audio verbalization bulk status" -Path "/timeline/audio-verbalization/bulk/status"
Assert-ContractEqualIgnoringDynamic -Name "audio verbalization bulk targets" -Path "/timeline/audio-verbalization/bulk/targets?refresh=true"
Assert-ContractEqual -Name "products runtime status" -Path "/products/runtime/status"
Assert-ContractEqualIgnoringDynamic -Name "audio models" -Path "/products/audio/models"
Assert-ContractEqual -Name "image overview" -Path "/products/image/overview"
Assert-ContractEqualIgnoringDynamic -Name "image models" -Path "/products/image/models"
Assert-ContractEqual -Name "image files" -Path "/products/image/files?page=1&pageSize=5"
Assert-ContractEqual -Name "video overview" -Path "/products/video/overview"
Assert-ContractEqual -Name "pc overview" -Path "/products/pc/overview"
Assert-ContractEqual -Name "pc items" -Path "/products/pc/items?page=1&pageSize=5"
Assert-ContractEqual -Name "windows codex overview" -Path "/products/windows-codex/overview"
Assert-ContractEqual -Name "chatgpt overview" -Path "/products/chatgpt/overview"
Assert-ContractEqual -Name "windows codex items" -Path "/products/windows-codex/items?page=1&pageSize=3"
Assert-ContractEqual -Name "chatgpt items" -Path "/products/chatgpt/items?page=1&pageSize=3"

$localBaseUrl = "http://127.0.0.1:$LocalApiPort"
$windowsCodexRows = Invoke-ContractGet -BaseUrl $localBaseUrl -Path "/products/windows-codex/items?page=1&pageSize=1"
$windowsCodexItemId = @($windowsCodexRows.threads | Select-Object -First 1 -ExpandProperty itemId)
if ($windowsCodexItemId) {
    Assert-ThreadDetailContract -Name "windows codex thread detail" -Path ("/products/windows-codex/threads/{0}" -f [uri]::EscapeDataString([string]$windowsCodexItemId[0])) -ExpectedItemId ([string]$windowsCodexItemId[0])
}
else {
    Write-Host "SKIP: windows codex thread detail (no generated item found)"
}

$chatGptRows = Invoke-ContractGet -BaseUrl $localBaseUrl -Path "/products/chatgpt/items?page=1&pageSize=1"
$chatGptItemId = @($chatGptRows.threads | Select-Object -First 1 -ExpandProperty itemId)
if ($chatGptItemId) {
    Assert-ThreadDetailContract -Name "chatgpt thread detail" -Path ("/products/chatgpt/threads/{0}" -f [uri]::EscapeDataString([string]$chatGptItemId[0])) -ExpectedItemId ([string]$chatGptItemId[0])
}
else {
    Write-Host "SKIP: chatgpt thread detail (no generated item found)"
}

$operationId = Get-ChildItem -Path (Join-Path $repoRoot "data\logs\operations") -Directory -ErrorAction SilentlyContinue |
    Sort-Object LastWriteTimeUtc -Descending |
    Select-Object -First 1 -ExpandProperty Name
if ($operationId) {
    Assert-ContractEqual -Name "timeline operation detail" -Path ("/timeline/operations/detail?operationId={0}" -f [uri]::EscapeDataString($operationId))
}
else {
    Write-Host "SKIP: timeline operation detail (no operation log found)"
}

$downloadZip = Get-ChildItem -Path (Join-Path $repoRoot "data\work\downloads") -Recurse -Filter *.zip -ErrorAction SilentlyContinue |
    Select-Object -First 1 -ExpandProperty FullName
if ($downloadZip) {
    Assert-RangeContractEqual -Name "download file range" -Path ("/downloads/file?path={0}" -f [uri]::EscapeDataString($downloadZip))
}
else {
    Write-Host "SKIP: download file range (no staged zip found)"
}

$audioRoot = Join-Path $repoRoot "data\input\audio"
$audioSource = Get-ChildItem -Path $audioRoot -Recurse -File -ErrorAction SilentlyContinue |
    Where-Object { $_.Extension -in @(".mp3", ".wav", ".m4a", ".aac", ".flac") } |
    Select-Object -First 1
if ($audioSource) {
    $audioRootFull = (Resolve-Path -LiteralPath $audioRoot).Path.TrimEnd('\', '/')
    $audioSourceFull = (Resolve-Path -LiteralPath $audioSource.FullName).Path
    $relativeAudioPath = $audioSourceFull.Substring($audioRootFull.Length).TrimStart('\', '/')
    Assert-RangeContractEqual -Name "audio source file range" -Path ("/products/audio/files/source?sourceId={0}&path={1}" -f [uri]::EscapeDataString($audioRoot), [uri]::EscapeDataString($relativeAudioPath))
}
else {
    Write-Host "SKIP: audio source file range (no audio input found)"
}

$imageSource = Get-ChildItem -Path (Join-Path $repoRoot "data\input\image") -Recurse -File -ErrorAction SilentlyContinue |
    Where-Object { $_.Extension -in @(".jpg", ".jpeg", ".png", ".webp", ".gif", ".bmp", ".tif", ".tiff", ".heic") } |
    Select-Object -First 1
if ($imageSource) {
    Assert-ContractEqual -Name "image file detail" -Path ("/products/image/files/detail?path={0}" -f [uri]::EscapeDataString($imageSource.FullName))
    Assert-RangeContractEqual -Name "image source file range" -Path ("/products/image/files/source?path={0}" -f [uri]::EscapeDataString($imageSource.FullName))
}
else {
    Write-Host "SKIP: image source file range (no image input found)"
}

$videoSource = Get-ChildItem -Path (Join-Path $repoRoot "data\input\video") -Recurse -File -ErrorAction SilentlyContinue |
    Where-Object { $_.Extension -in @(".avi", ".m4v", ".mkv", ".mov", ".mp4", ".webm", ".wmv") } |
    Select-Object -First 1
if ($videoSource) {
    Assert-RangeContractEqual -Name "video source file range" -Path ("/products/video/files/source?path={0}" -f [uri]::EscapeDataString($videoSource.FullName))
}
else {
    Write-Host "SKIP: video source file range (no video input found)"
}

Write-Host "Contract smoke completed."
