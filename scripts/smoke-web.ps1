[CmdletBinding()]
param(
    [string]$BaseUrl = "http://127.0.0.1:19000",
    [string]$HelperBaseUrl = "http://127.0.0.1:19001",
    [int]$TimeoutSeconds = 20
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

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
    "/timeline/settings",
    "/timeline/operations",
    "/audio/files",
    "/windows-codex",
    "/chatgpt",
    "/image",
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
    "/timeline/operations?limit=3",
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

$imageFilesUrl = "$HelperBaseUrl/products/image/files?page=1&pageSize=3"
$imageFilesTimeoutSeconds = [Math]::Max($TimeoutSeconds, 120)
$imageFilesPayload = Invoke-RestMethod -Uri $imageFilesUrl -TimeoutSec $imageFilesTimeoutSeconds
Assert-NoUnicodeReplacementCharacter -Payload $imageFilesPayload -Label "Image files response"
$imageFilesProperty = $imageFilesPayload.PSObject.Properties["files"]
if ($null -eq $imageFilesProperty) {
    throw "Image files response did not include files."
}
Write-Host "PASS helper /products/image/files text encoding"

Write-Host "Timeline Web smoke check passed."
