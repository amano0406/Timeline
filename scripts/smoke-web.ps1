[CmdletBinding()]
param(
    [string]$BaseUrl = "http://127.0.0.1:19000",
    [string]$HelperBaseUrl = "http://127.0.0.1:19001",
    [int]$TimeoutSeconds = 20
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

Add-Type -AssemblyName System.Net.Http

function Resolve-TimelineSmokeHelperBaseUrl {
    param([string]$PreferredBaseUrl)

    $candidates = [System.Collections.Generic.List[string]]::new()
    $fallbacks = 19001..19010 | ForEach-Object { "http://127.0.0.1:$_" }
    foreach ($candidate in @($PreferredBaseUrl) + $fallbacks) {
        if ($candidate -and -not $candidates.Contains($candidate)) {
            $candidates.Add($candidate) | Out-Null
        }
    }

    foreach ($candidate in $candidates) {
        try {
            $response = Invoke-WebRequest -Uri "$candidate/health" -UseBasicParsing -TimeoutSec 3
            if ($response.StatusCode -ge 200 -and $response.StatusCode -lt 300) {
                return $candidate
            }
        }
        catch {
        }
    }

    return $PreferredBaseUrl
}

$HelperBaseUrl = Resolve-TimelineSmokeHelperBaseUrl -PreferredBaseUrl $HelperBaseUrl

function Assert-NoUnicodeReplacementCharacter {
    param(
        [object]$Payload,
        [string]$Label
    )

    $replacement = [string][char]0xFFFD
    $json = ConvertTo-Json -InputObject $Payload -Compress -Depth 20
    if ($json.Contains($replacement)) {
        throw "$Label contained Unicode replacement characters."
    }
}

$paths = @(
    "/",
    "/timeline",
    "/timeline/products",
    "/timeline/settings",
    "/scan",
    "/audio/files",
    "/audio/settings",
    "/video",
    "/video/settings",
    "/pc",
    "/pc/settings",
    "/windows-codex",
    "/windows-codex/settings",
    "/chatgpt",
    "/chatgpt/settings",
    "/image",
    "/image/settings",
    "/api/health"
)

foreach ($path in $paths) {
    $url = "$BaseUrl$path"
    $response = Invoke-WebRequest -Uri $url -UseBasicParsing -TimeoutSec $TimeoutSeconds
    if ($response.StatusCode -lt 200 -or $response.StatusCode -ge 300) {
        throw "Unexpected status for ${url}: $($response.StatusCode)"
    }
    Write-Host "PASS $path $($response.RawContentLength) bytes"
}

$helperPaths = @(
    "/health",
    "/products/runtime/status",
    "/products/audio/overview",
    "/products/audio/models",
    "/products/windows-codex/overview",
    "/products/chatgpt/overview",
    "/products/image/overview",
    "/products/image/models",
    "/products/video/overview",
    "/products/pc/overview",
    "/timeline/settings",
    "/timeline/operations?limit=3",
    "/timeline/store/overview",
    "/timeline/worker/status",
    "/timeline/audio-verbalization/bulk/status",
    "/timeline/audio-verbalization/bulk/targets"
)

foreach ($path in $helperPaths) {
    $url = "$HelperBaseUrl$path"
    $requestTimeoutSeconds = $TimeoutSeconds
    if ($path -eq "/timeline/audio-verbalization/bulk/targets") {
        $requestTimeoutSeconds = [Math]::Max($TimeoutSeconds, 90)
    }
    $watch = [System.Diagnostics.Stopwatch]::StartNew()
    $response = Invoke-WebRequest -Uri $url -UseBasicParsing -TimeoutSec $requestTimeoutSeconds
    $watch.Stop()
    if ($response.StatusCode -lt 200 -or $response.StatusCode -ge 300) {
        throw "Unexpected status for ${url}: $($response.StatusCode)"
    }
    $elapsed = [Math]::Round($watch.Elapsed.TotalSeconds, 3)
    Write-Host "PASS helper $path $($response.RawContentLength) bytes ${elapsed}s"
}

$audioFilesUrl = "$HelperBaseUrl/products/audio/files?page=1&pageSize=3"
$audioFilesTimeoutSeconds = [Math]::Max($TimeoutSeconds, 120)
$audioFilesPayload = Invoke-RestMethod -Uri $audioFilesUrl -TimeoutSec $audioFilesTimeoutSeconds
Assert-NoUnicodeReplacementCharacter -Payload $audioFilesPayload -Label "Audio files response"
$audioFilesProperty = $audioFilesPayload.PSObject.Properties["files"]
if ($null -eq $audioFilesProperty) {
    throw "Audio files response did not include files."
}

$audioFiles = @($audioFilesProperty.Value)
if ($audioFiles.Count -gt 0) {
    $firstAudioFile = $audioFiles[0]
    $verbalizationProperty = $firstAudioFile.PSObject.Properties["audioVerbalization"]
    if ($null -eq $verbalizationProperty) {
        throw "Audio file row did not include audioVerbalization."
    }

    $verbalization = $verbalizationProperty.Value
    $stateProperty = $verbalization.PSObject.Properties["state"]
    if ($null -eq $stateProperty -or -not [string]$stateProperty.Value) {
        throw "Audio verbalization state was empty."
    }

    Write-Host "PASS helper /products/audio/files audio verbalization status $($stateProperty.Value)"
}
else {
    Write-Host "SKIP helper /products/audio/files audio verbalization status no files"
}

$imageFilesUrl = "$HelperBaseUrl/products/image/files?page=1&pageSize=8"
$imageFilesTimeoutSeconds = [Math]::Max($TimeoutSeconds, 120)
$imageFilesPayload = Invoke-RestMethod -Uri $imageFilesUrl -TimeoutSec $imageFilesTimeoutSeconds
Assert-NoUnicodeReplacementCharacter -Payload $imageFilesPayload -Label "Image files response"
$imageFilesProperty = $imageFilesPayload.PSObject.Properties["files"]
if ($null -eq $imageFilesProperty) {
    throw "Image files response did not include files."
}
Write-Host "PASS helper /products/image/files text encoding"

$videoFilesUrl = "$HelperBaseUrl/products/video/files?page=1&pageSize=3"
$videoFilesTimeoutSeconds = [Math]::Max($TimeoutSeconds, 120)
$videoFilesPayload = Invoke-RestMethod -Uri $videoFilesUrl -TimeoutSec $videoFilesTimeoutSeconds
Assert-NoUnicodeReplacementCharacter -Payload $videoFilesPayload -Label "Video files response"
$videoFilesProperty = $videoFilesPayload.PSObject.Properties["files"]
if ($null -eq $videoFilesProperty) {
    throw "Video files response did not include files."
}

$videoFiles = @($videoFilesProperty.Value)
if ($videoFiles.Count -gt 0) {
    $firstVideoFile = $videoFiles[0]
    $pathProperty = $firstVideoFile.PSObject.Properties["sourcePath"]
    if ($null -eq $pathProperty -or -not [string]$pathProperty.Value) {
        throw "Video file row did not include sourcePath."
    }

    $encodedVideoPath = [System.Uri]::EscapeDataString([string]$pathProperty.Value)
    $videoDetailUrl = "$HelperBaseUrl/products/video/files/detail?path=$encodedVideoPath"
    $videoDetailPayload = Invoke-RestMethod -Uri $videoDetailUrl -TimeoutSec $videoFilesTimeoutSeconds
    Assert-NoUnicodeReplacementCharacter -Payload $videoDetailPayload -Label "Video detail response"
    $availableProperty = $videoDetailPayload.PSObject.Properties["available"]
    if ($null -eq $availableProperty -or -not [bool]$availableProperty.Value) {
        throw "Video detail response did not report an available file."
    }

    $videoSourceUrl = "$BaseUrl/api/video/source?path=$encodedVideoPath"
    $client = [System.Net.Http.HttpClient]::new()
    try {
        $request = [System.Net.Http.HttpRequestMessage]::new([System.Net.Http.HttpMethod]::Get, $videoSourceUrl)
        $request.Headers.Range = [System.Net.Http.Headers.RangeHeaderValue]::new(0, 0)
        $sourceResponse = $client.SendAsync($request, [System.Net.Http.HttpCompletionOption]::ResponseHeadersRead).GetAwaiter().GetResult()
        try {
            if (-not $sourceResponse.IsSuccessStatusCode) {
                throw "Unexpected status for ${videoSourceUrl}: $([int]$sourceResponse.StatusCode)"
            }

            $sourceStatusCode = [int]$sourceResponse.StatusCode
            if ($sourceStatusCode -ne 200 -and $sourceStatusCode -ne 206) {
                throw "Unexpected range status for ${videoSourceUrl}: $sourceStatusCode"
            }
        }
        finally {
            $sourceResponse.Dispose()
            $request.Dispose()
        }
    }
    finally {
        $client.Dispose()
    }

    Write-Host "PASS helper /products/video/files detail and source"
}
else {
    Write-Host "SKIP helper /products/video/files detail and source no files"
}

$pcItemsUrl = "$HelperBaseUrl/products/pc/items?page=1&pageSize=3"
$pcItemsTimeoutSeconds = [Math]::Max($TimeoutSeconds, 120)
$pcItemsPayload = Invoke-RestMethod -Uri $pcItemsUrl -TimeoutSec $pcItemsTimeoutSeconds
Assert-NoUnicodeReplacementCharacter -Payload $pcItemsPayload -Label "PC items response"
$pcItemsProperty = $pcItemsPayload.PSObject.Properties["items"]
if ($null -eq $pcItemsProperty) {
    throw "PC items response did not include items."
}
Write-Host "PASS helper /products/pc/items"

Write-Host "Timeline Web smoke check passed."
