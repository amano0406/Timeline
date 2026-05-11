[CmdletBinding()]
param(
    [string]$AudioProductPath = "",
    [string]$HelperBaseUrl = "http://127.0.0.1:19001",
    [string]$TimelineBaseUrl = "http://127.0.0.1:19000"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

if (-not $AudioProductPath) {
    $repoRoot = Split-Path -Parent $PSScriptRoot
    $AudioProductPath = Join-Path (Join-Path (Join-Path $repoRoot "data") "products") "TimelineForAudio"
}

function Assert-TimelineSmoke {
    param(
        [bool]$Condition,
        [string]$Message
    )

    if (-not $Condition) {
        throw $Message
    }
}

function ConvertFrom-TimelineSmokeJson {
    param([string]$Text)

    $jsonText = ([string]$Text).Trim()
    $startIndex = $jsonText.IndexOf("{", [System.StringComparison]::Ordinal)
    $endIndex = $jsonText.LastIndexOf("}", [System.StringComparison]::Ordinal)
    if ($startIndex -lt 0 -or $endIndex -lt $startIndex) {
        throw "Command did not return JSON. Output: $jsonText"
    }
    return $jsonText.Substring($startIndex, $endIndex - $startIndex + 1) | ConvertFrom-Json
}

function Convert-TimelineAudioSmokePath {
    param(
        [string]$Path,
        [string]$ProductPath
    )

    $text = ([string]$Path).Trim()
    if (-not $text) {
        return ""
    }
    if ($text.Equals("/workspace", [System.StringComparison]::OrdinalIgnoreCase)) {
        return $ProductPath
    }
    if ($text.StartsWith("/workspace/", [System.StringComparison]::OrdinalIgnoreCase)) {
        return Join-Path $ProductPath $text.Substring("/workspace/".Length).Replace("/", "\")
    }
    if ($text.StartsWith("/mnt/c/", [System.StringComparison]::OrdinalIgnoreCase)) {
        return "C:\" + $text.Substring(7).Replace("/", "\")
    }
    return $text
}

function Assert-ZipReadable {
    param([string]$Path)

    Assert-TimelineSmoke -Condition (Test-Path -LiteralPath $Path -PathType Leaf) -Message "ZIP was not found: $Path"
    Assert-TimelineSmoke -Condition ([System.IO.Path]::GetExtension($Path).Equals(".zip", [System.StringComparison]::OrdinalIgnoreCase)) -Message "File is not a ZIP: $Path"
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $zip = [System.IO.Compression.ZipFile]::OpenRead($Path)
    try {
        Assert-TimelineSmoke -Condition ($zip.Entries.Count -gt 0) -Message "ZIP has no entries: $Path"
        Assert-TimelineSmoke -Condition ([bool]($zip.Entries | Where-Object { $_.FullName -eq "README.md" } | Select-Object -First 1)) -Message "ZIP does not contain README.md: $Path"
    }
    finally {
        $zip.Dispose()
    }
}

$cliPath = Join-Path $AudioProductPath "cli.ps1"
Assert-TimelineSmoke -Condition (Test-Path -LiteralPath $cliPath -PathType Leaf) -Message "TimelineForAudio cli.ps1 was not found: $cliPath"

$directOutput = & powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File $cliPath items download --json 2>&1
$directExitCode = $LASTEXITCODE
$directText = ($directOutput | Out-String).Trim()
Assert-TimelineSmoke -Condition ($directExitCode -eq 0) -Message "TimelineForAudio cli.ps1 items download failed. ExitCode=$directExitCode Output=$directText"

$directPayload = ConvertFrom-TimelineSmokeJson -Text $directText
$directArchivePath = Convert-TimelineAudioSmokePath -Path ([string]$directPayload.archive_path) -ProductPath $AudioProductPath
Assert-ZipReadable -Path $directArchivePath
Write-Host "PASS direct cli.ps1 items download -> $directArchivePath"

$helperPayload = Invoke-RestMethod `
    -Uri "$HelperBaseUrl/products/audio/items/download" `
    -Method Post `
    -Body "{}" `
    -ContentType "application/json"
$helperArchivePath = [string]$helperPayload.archivePath
Assert-ZipReadable -Path $helperArchivePath
Write-Host "PASS Timeline helper audio download staging -> $helperArchivePath"

$downloadUrl = "$TimelineBaseUrl/api/download/file?path=$([uri]::EscapeDataString($helperArchivePath))"
$downloadPath = Join-Path $env:TEMP ("timeline-audio-ps1-download-{0}.zip" -f ([guid]::NewGuid().ToString("N")))
try {
    Invoke-WebRequest -Uri $downloadUrl -OutFile $downloadPath -UseBasicParsing -TimeoutSec 60 | Out-Null
    Assert-ZipReadable -Path $downloadPath
    Write-Host "PASS Timeline web download endpoint -> $downloadPath"
}
finally {
    Remove-Item -LiteralPath $downloadPath -Force -ErrorAction SilentlyContinue
}

Write-Host "TimelineForAudio PS1 download smoke check passed."
