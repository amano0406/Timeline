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

Write-Host "Timeline Web smoke check passed."
