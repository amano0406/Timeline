[CmdletBinding()]
param(
    [string]$BaseUrl = "http://127.0.0.1:19000",
    [string]$HelperBaseUrl = "http://127.0.0.1:19001",
    [int]$TimeoutSeconds = 20
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$paths = @(
    "/",
    "/timeline",
    "/timeline/settings",
    "/audio/files",
    "/audio/settings",
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
    "/products/windows-codex/overview",
    "/products/chatgpt/overview",
    "/products/image/overview",
    "/timeline/settings",
    "/timeline/store/overview",
    "/timeline/worker/status"
)

foreach ($path in $helperPaths) {
    $url = "$HelperBaseUrl$path"
    $watch = [System.Diagnostics.Stopwatch]::StartNew()
    $response = Invoke-WebRequest -Uri $url -UseBasicParsing -TimeoutSec $TimeoutSeconds
    $watch.Stop()
    if ($response.StatusCode -lt 200 -or $response.StatusCode -ge 300) {
        throw "Unexpected status for ${url}: $($response.StatusCode)"
    }
    $elapsed = [Math]::Round($watch.Elapsed.TotalSeconds, 3)
    Write-Host "PASS helper $path $($response.RawContentLength) bytes ${elapsed}s"
}

$audioFilesUrl = "$HelperBaseUrl/products/audio/files?page=1&pageSize=3"
$audioFilesPayload = Invoke-RestMethod -Uri $audioFilesUrl -TimeoutSec $TimeoutSeconds
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

Write-Host "Timeline Web smoke check passed."
