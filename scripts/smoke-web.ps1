[CmdletBinding()]
param(
    [string]$BaseUrl = "http://127.0.0.1:19000"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$paths = @(
    "/",
    "/audio/files",
    "/audio/settings",
    "/api/health"
)

foreach ($path in $paths) {
    $url = "$BaseUrl$path"
    $response = Invoke-WebRequest -Uri $url -UseBasicParsing -TimeoutSec 20
    if ($response.StatusCode -lt 200 -or $response.StatusCode -ge 300) {
        throw "Unexpected status for ${url}: $($response.StatusCode)"
    }
    Write-Host "PASS $path $($response.RawContentLength) bytes"
}

Write-Host "Timeline Web smoke check passed."
