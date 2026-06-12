[CmdletBinding()]
param(
    [string]$AudioProductPath = "",
    [string]$VideoProductPath = "",
    [string]$ImageProductPath = "",
    [string]$WindowsCodexProductPath = "",
    [string]$ChatGptProductPath = "",
    [string]$PcProductPath = "",
    [string]$DownloadRoot = "",
    [switch]$IncludeDownloads,
    [switch]$RequireRunning
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
$defaultDataProductsRoot = Join-Path (Join-Path $repoRoot "data") "products"

function Resolve-ProductPath {
    param(
        [string]$SpecifiedPath,
        [string]$AppDirectoryName
    )

    if ($SpecifiedPath) {
        return $SpecifiedPath
    }

    $appPath = Join-Path "C:\apps" $AppDirectoryName
    if (Test-Path -LiteralPath $appPath -PathType Container) {
        return $appPath
    }

    return Join-Path $defaultDataProductsRoot $AppDirectoryName
}

if (-not $AudioProductPath) {
    $AudioProductPath = Resolve-ProductPath -SpecifiedPath $AudioProductPath -AppDirectoryName "TimelineForAudio"
}
if (-not $VideoProductPath) {
    $VideoProductPath = Resolve-ProductPath -SpecifiedPath $VideoProductPath -AppDirectoryName "TimelineForVideo"
}
if (-not $ImageProductPath) {
    $ImageProductPath = Resolve-ProductPath -SpecifiedPath $ImageProductPath -AppDirectoryName "TimelineForImage"
}
if (-not $WindowsCodexProductPath) {
    $WindowsCodexProductPath = Resolve-ProductPath -SpecifiedPath $WindowsCodexProductPath -AppDirectoryName "TimelineForWindowsCodex"
}
if (-not $ChatGptProductPath) {
    $ChatGptProductPath = Resolve-ProductPath -SpecifiedPath $ChatGptProductPath -AppDirectoryName "TimelineForChatGPT"
}
if (-not $PcProductPath) {
    $PcProductPath = Resolve-ProductPath -SpecifiedPath $PcProductPath -AppDirectoryName "TimelineForPcInfo"
}
if (-not $DownloadRoot) {
    $DownloadRoot = Join-Path (Join-Path (Join-Path $repoRoot "data") "work") "product-api-contract-smoke"
}

$script:Failures = New-Object System.Collections.Generic.List[string]

function Add-ContractResult {
    param(
        [string]$Product,
        [string]$Check,
        [string]$Status,
        [string]$Message
    )

    $line = "{0} {1} {2} - {3}" -f $Status, $Product, $Check, $Message
    Write-Host $line
    if ($Status -eq "FAIL") {
        $script:Failures.Add($line)
    }
}

function Get-ObjectProperty {
    param(
        [object]$Object,
        [string[]]$Names,
        [object]$Default = $null
    )

    if ($null -eq $Object) {
        return $Default
    }

    foreach ($name in @($Names)) {
        if ($Object -is [System.Collections.IDictionary] -and $Object.Contains($name)) {
            return $Object[$name]
        }
        $property = $Object.PSObject.Properties[$name]
        if ($null -ne $property) {
            return $property.Value
        }
    }

    return $Default
}

function Test-ObjectPropertyExists {
    param(
        [object]$Object,
        [string[]]$Names
    )

    foreach ($name in @($Names)) {
        if ($null -ne (Get-ObjectProperty -Object $Object -Names @($name) -Default $null)) {
            return $true
        }
    }
    return $false
}

function Get-ProductApiBaseUrl {
    param(
        [string]$ProductPath,
        [int]$FallbackPort
    )

    $manifestPath = Join-Path $ProductPath "timeline-product.json"
    if (Test-Path -LiteralPath $manifestPath -PathType Leaf) {
        try {
            $manifest = Get-Content -LiteralPath $manifestPath -Raw -Encoding UTF8 | ConvertFrom-Json
            $defaultBaseUrl = [string](Get-ObjectProperty -Object $manifest.api -Names @("defaultBaseUrl", "baseUrl") -Default "")
            if ($defaultBaseUrl) {
                return $defaultBaseUrl.TrimEnd("/")
            }
            $port = Get-ObjectProperty -Object $manifest.api -Names @("defaultPort", "port") -Default $null
            if ($null -ne $port) {
                return "http://127.0.0.1:$([int]$port)"
            }
        }
        catch {
        }
    }

    return "http://127.0.0.1:$FallbackPort"
}

function Test-ProductApiRunning {
    param(
        [string]$BaseUrl,
        [string]$Product
    )

    try {
        $response = Invoke-WebRequest -UseBasicParsing -TimeoutSec 10 -Uri "$($BaseUrl.TrimEnd('/'))/health"
        if ($response.StatusCode -lt 200 -or $response.StatusCode -ge 300) {
            Add-ContractResult -Product $Product -Check "health" -Status "FAIL" -Message "Unexpected HTTP status: $($response.StatusCode)"
            return $false
        }

        $body = ([string]$response.Content).Trim()
        if ($body -eq "false") {
            $status = if ($RequireRunning) { "FAIL" } else { "SKIP" }
            Add-ContractResult -Product $Product -Check "health" -Status $status -Message "Health endpoint returned false."
            return $false
        }

        Add-ContractResult -Product $Product -Check "health" -Status "PASS" -Message $BaseUrl
        return $true
    }
    catch {
        $status = if ($RequireRunning) { "FAIL" } else { "SKIP" }
        Add-ContractResult -Product $Product -Check "health" -Status $status -Message "API is not reachable: $($_.Exception.Message)"
        return $false
    }
}

function Invoke-ProductApiJson {
    param(
        [string]$BaseUrl,
        [string]$Path,
        [object]$Body = @{},
        [int]$TimeoutSeconds = 30
    )

    $json = $Body | ConvertTo-Json -Depth 20 -Compress
    return Invoke-RestMethod `
        -UseBasicParsing `
        -TimeoutSec $TimeoutSeconds `
        -Uri "$($BaseUrl.TrimEnd('/'))$Path" `
        -Method Post `
        -ContentType "application/json" `
        -Body $json
}

function Assert-ProductApiJson {
    param(
        [string]$Product,
        [string]$BaseUrl,
        [string]$Check,
        [string]$Path,
        [object]$Body = @{},
        [string[]]$RequiredAnyProperties = @()
    )

    try {
        $payload = Invoke-ProductApiJson -BaseUrl $BaseUrl -Path $Path -Body $Body
        if ($null -eq $payload) {
            throw "Response was empty."
        }

        $okValue = Get-ObjectProperty -Object $payload -Names @("ok") -Default $null
        if ($okValue -is [bool] -and -not [bool]$okValue) {
            $errorMessage = [string](Get-ObjectProperty -Object (Get-ObjectProperty -Object $payload -Names @("error") -Default $null) -Names @("message") -Default "")
            if (-not $errorMessage) {
                $errorMessage = "Response returned ok=false."
            }
            throw $errorMessage
        }

        if ($RequiredAnyProperties.Count -gt 0 -and -not (Test-ObjectPropertyExists -Object $payload -Names $RequiredAnyProperties)) {
            throw "Missing one of properties: $($RequiredAnyProperties -join ', ')"
        }

        Add-ContractResult -Product $Product -Check $Check -Status "PASS" -Message "JSON response matched."
        return $payload
    }
    catch {
        Add-ContractResult -Product $Product -Check $Check -Status "FAIL" -Message $_.Exception.Message
        return $null
    }
}

function Get-ItemCount {
    param([object]$Payload)

    $value = Get-ObjectProperty -Object $Payload -Names @("total_items", "item_count", "total", "count") -Default 0
    if ($value -eq 0) {
        $items = Get-ObjectProperty -Object $Payload -Names @("items", "threads", "files") -Default @()
        return @($items).Count
    }
    try {
        return [int]$value
    }
    catch {
        return 0
    }
}

function Assert-ZipReadable {
    param(
        [string]$Path,
        [string]$Product
    )

    if (-not $Path -or -not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "ZIP was not found. Returned path: $Path"
    }
    if (-not [System.IO.Path]::GetExtension($Path).Equals(".zip", [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Returned file is not a ZIP: $Path"
    }

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $zip = [System.IO.Compression.ZipFile]::OpenRead($Path)
    try {
        if ($zip.Entries.Count -le 0) {
            throw "ZIP has no entries: $Path"
        }
    }
    finally {
        $zip.Dispose()
    }
}

function New-DownloadDirectory {
    param([string]$Product)

    $path = Join-Path $DownloadRoot $Product
    if (Test-Path -LiteralPath $path) {
        Remove-Item -LiteralPath $path -Recurse -Force
    }
    [System.IO.Directory]::CreateDirectory($path) | Out-Null
    return $path
}

function Assert-DownloadEndpoint {
    param(
        [string]$Product,
        [string]$BaseUrl,
        [object]$ItemsPayload,
        [object]$Body
    )

    if (-not $IncludeDownloads) {
        Add-ContractResult -Product $Product -Check "/items/download" -Status "SKIP" -Message "Use -IncludeDownloads to verify ZIP creation."
        return
    }
    if ((Get-ItemCount -Payload $ItemsPayload) -le 0) {
        Add-ContractResult -Product $Product -Check "/items/download" -Status "SKIP" -Message "No items are available."
        return
    }

    try {
        $payload = Invoke-ProductApiJson -BaseUrl $BaseUrl -Path "/items/download" -Body $Body -TimeoutSeconds 120
        $archivePath = [string](Get-ObjectProperty -Object $payload -Names @("archivePath", "archive_path", "downloadPath", "download_path", "destinationPath", "destination_path") -Default "")
        Assert-ZipReadable -Path $archivePath -Product $Product
        Add-ContractResult -Product $Product -Check "/items/download" -Status "PASS" -Message "ZIP was created."
    }
    catch {
        Add-ContractResult -Product $Product -Check "/items/download" -Status "FAIL" -Message $_.Exception.Message
    }
}

function Test-ProductApi {
    param(
        [string]$Product,
        [string]$ProductPath,
        [int]$FallbackPort,
        [bool]$HasFiles,
        [bool]$HasModels,
        [bool]$HasSettingsSave,
        [bool]$SupportsDownloadTo
    )

    $baseUrl = Get-ProductApiBaseUrl -ProductPath $ProductPath -FallbackPort $FallbackPort
    if (-not (Test-ProductApiRunning -BaseUrl $baseUrl -Product $Product)) {
        return
    }

    $settingsPayload = Assert-ProductApiJson `
        -Product $Product `
        -BaseUrl $baseUrl `
        -Check "/settings/status" `
        -Path "/settings/status" `
        -RequiredAnyProperties @("settings", "setup", "outputRoot", "output_root", "outputRoots", "master")

    if ($HasFiles) {
        Assert-ProductApiJson `
            -Product $Product `
            -BaseUrl $baseUrl `
            -Check "/files/list" `
            -Path "/files/list" `
            -Body @{ page = 1; pageSize = 1 } `
            -RequiredAnyProperties @("files", "items", "count", "pagination") | Out-Null
    }

    $itemsPayload = Assert-ProductApiJson `
        -Product $Product `
        -BaseUrl $baseUrl `
        -Check "/items/list" `
        -Path "/items/list" `
        -Body @{ page = 1; pageSize = 1 } `
        -RequiredAnyProperties @("items", "threads", "count", "pagination")

    if ($HasModels) {
        Assert-ProductApiJson `
            -Product $Product `
            -BaseUrl $baseUrl `
            -Check "/models/list" `
            -Path "/models/list" `
            -RequiredAnyProperties @("models", "items", "count") | Out-Null
    }

    if ($HasSettingsSave -and $null -ne $settingsPayload) {
        Add-ContractResult -Product $Product -Check "/settings/save" -Status "SKIP" -Message "Not run by default because it changes settings."
    }

    $downloadBody = @{}
    if ($SupportsDownloadTo) {
        $downloadBody = @{ outputPath = (New-DownloadDirectory -Product $Product) }
    }
    Assert-DownloadEndpoint -Product $Product -BaseUrl $baseUrl -ItemsPayload $itemsPayload -Body $downloadBody
}

Write-Host "Checking sub-product API contracts. This script never starts or stops products."

Test-ProductApi -Product "TimelineForAudio" -ProductPath $AudioProductPath -FallbackPort 19100 -HasFiles $true -HasModels $true -HasSettingsSave $true -SupportsDownloadTo $false
Test-ProductApi -Product "TimelineForVideo" -ProductPath $VideoProductPath -FallbackPort 19500 -HasFiles $true -HasModels $true -HasSettingsSave $true -SupportsDownloadTo $false
Test-ProductApi -Product "TimelineForImage" -ProductPath $ImageProductPath -FallbackPort 19400 -HasFiles $true -HasModels $true -HasSettingsSave $true -SupportsDownloadTo $true
Test-ProductApi -Product "TimelineForWindowsCodex" -ProductPath $WindowsCodexProductPath -FallbackPort 19200 -HasFiles $false -HasModels $false -HasSettingsSave $false -SupportsDownloadTo $true
Test-ProductApi -Product "TimelineForChatGPT" -ProductPath $ChatGptProductPath -FallbackPort 19300 -HasFiles $false -HasModels $false -HasSettingsSave $false -SupportsDownloadTo $true
Test-ProductApi -Product "TimelineForPcInfo" -ProductPath $PcProductPath -FallbackPort 19600 -HasFiles $false -HasModels $false -HasSettingsSave $true -SupportsDownloadTo $true

if ($script:Failures.Count -gt 0) {
    Write-Host ""
    Write-Host "Product API contract check failed."
    foreach ($failure in $script:Failures) {
        Write-Host $failure
    }
    exit 1
}

Write-Host ""
Write-Host "Product API contract check completed."
