[CmdletBinding()]
param(
    [string]$AudioProductPath = "",
    [string]$WindowsCodexProductPath = "",
    [string]$ChatGptProductPath = "",
    [string]$ImageProductPath = "",
    [string]$DownloadRoot = "",
    [switch]$IncludeDownloads
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
$defaultDataRoot = Join-Path $repoRoot "data"
$defaultProductsRoot = Join-Path $defaultDataRoot "products"
if (-not $AudioProductPath) {
    $AudioProductPath = Join-Path $defaultProductsRoot "TimelineForAudio"
}
if (-not $WindowsCodexProductPath) {
    $WindowsCodexProductPath = Join-Path $defaultProductsRoot "TimelineForWindowsCodex"
}
if (-not $ChatGptProductPath) {
    $ChatGptProductPath = Join-Path $defaultProductsRoot "TimelineForChatGPT"
}
if (-not $ImageProductPath) {
    $ImageProductPath = Join-Path $defaultProductsRoot "TimelineForImage"
}
if (-not $DownloadRoot) {
    $DownloadRoot = Join-Path (Join-Path $defaultDataRoot "work") "contract-smoke"
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

function Get-ContractProperty {
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

function Test-ContractPropertyExists {
    param(
        [object]$Object,
        [string]$Name
    )

    if ($null -eq $Object) {
        return $false
    }
    if ($Object -is [System.Collections.IDictionary]) {
        return $Object.Contains($Name)
    }
    return ($null -ne $Object.PSObject.Properties[$Name])
}

function ConvertFrom-ContractJsonOutput {
    param([string]$Text)

    $jsonText = ([string]$Text).Trim()
    $objectStart = $jsonText.IndexOf("{", [System.StringComparison]::Ordinal)
    $arrayStart = $jsonText.IndexOf("[", [System.StringComparison]::Ordinal)
    if ($arrayStart -ge 0 -and ($objectStart -lt 0 -or $arrayStart -lt $objectStart)) {
        $startIndex = $arrayStart
        $endIndex = $jsonText.LastIndexOf("]", [System.StringComparison]::Ordinal)
    }
    else {
        $startIndex = $objectStart
        $endIndex = $jsonText.LastIndexOf("}", [System.StringComparison]::Ordinal)
    }
    if ($startIndex -lt 0 -or $endIndex -lt $startIndex) {
        throw "Command did not return JSON. Output: $(Get-ContractTextPreview -Text $jsonText)"
    }

    $payload = $jsonText.Substring($startIndex, $endIndex - $startIndex + 1) | ConvertFrom-Json
    $okProperty = $payload.PSObject.Properties["ok"]
    if ($null -ne $okProperty -and $okProperty.Value -is [bool] -and -not [bool]$okProperty.Value) {
        $errorPayload = Get-ContractProperty -Object $payload -Names @("error") -Default @{}
        $message = [string](Get-ContractProperty -Object $errorPayload -Names @("message") -Default "")
        if (-not $message) {
            $message = "CLI returned ok=false."
        }
        throw $message
    }
    return $payload
}

function Get-ContractTextPreview {
    param(
        [string]$Text,
        [int]$MaxLength = 2000
    )

    $value = ([string]$Text).Trim()
    if ($value.Length -le $MaxLength) {
        return $value
    }
    return $value.Substring(0, $MaxLength) + "... <truncated>"
}

function ConvertFrom-ContractJsonStringLiteral {
    param([string]$Value)

    $json = '"' + ([string]$Value) + '"'
    try {
        return [string]($json | ConvertFrom-Json)
    }
    catch {
        return [string]$Value
    }
}

function Get-ContractJsonStringPropertyFromOutput {
    param(
        [string]$Text,
        [string[]]$Names
    )

    foreach ($name in @($Names)) {
        $escapedName = [System.Text.RegularExpressions.Regex]::Escape([string]$name)
        $pattern = '"' + $escapedName + '"\s*:\s*"((?:\\.|[^"\\])*)"'
        $match = [System.Text.RegularExpressions.Regex]::Match([string]$Text, $pattern)
        if ($match.Success) {
            return ConvertFrom-ContractJsonStringLiteral -Value $match.Groups[1].Value
        }
    }
    return ""
}

function Format-ContractProcessArgument {
    param([string]$Value)

    $text = [string]$Value
    if (-not $text) {
        return '""'
    }
    if ($text -notmatch '[\s"]') {
        return $text
    }
    return '"' + $text.Replace('"', '\"') + '"'
}

function Invoke-ContractProcess {
    param(
        [string]$FileName,
        [string[]]$Arguments,
        [string]$WorkingDirectory,
        [int]$TimeoutSeconds = 180
    )

    $startInfo = [System.Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $FileName
    $startInfo.Arguments = (@($Arguments) | ForEach-Object { Format-ContractProcessArgument -Value ([string]$_) }) -join " "
    $startInfo.WorkingDirectory = $WorkingDirectory
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    $startInfo.StandardOutputEncoding = [System.Text.UTF8Encoding]::new($false)
    $startInfo.StandardErrorEncoding = [System.Text.UTF8Encoding]::new($false)

    $process = [System.Diagnostics.Process]::new()
    $process.StartInfo = $startInfo
    [void]$process.Start()
    $stdoutTask = $process.StandardOutput.ReadToEndAsync()
    $stderrTask = $process.StandardError.ReadToEndAsync()
    if (-not $process.WaitForExit([Math]::Max(1, $TimeoutSeconds) * 1000)) {
        try {
            $process.Kill()
        }
        catch {
        }
        throw "$FileName timed out."
    }

    return [ordered]@{
        exitCode = [int]$process.ExitCode
        stdout = [string]$stdoutTask.Result
        stderr = [string]$stderrTask.Result
    }
}

function Get-ContractPowerShellPath {
    $candidate = Join-Path $env:SystemRoot "System32\WindowsPowerShell\v1.0\powershell.exe"
    if (Test-Path -LiteralPath $candidate -PathType Leaf) {
        return $candidate
    }
    return "powershell.exe"
}

function Get-ContractUtf8CliInvokerPath {
    $candidate = Join-Path $PSScriptRoot "invoke-product-cli-utf8.ps1"
    if (Test-Path -LiteralPath $candidate -PathType Leaf) {
        return $candidate
    }
    throw "Product CLI UTF-8 invoker was not found: $candidate"
}

function Invoke-ContractCliJson {
    param(
        [string]$Product,
        [string]$ProductPath,
        [string[]]$CliArgs
    )

    $cliPath = Join-Path $ProductPath "cli.ps1"
    if (-not (Test-Path -LiteralPath $cliPath -PathType Leaf)) {
        throw "cli.ps1 was not found: $cliPath"
    }

    $utf8Invoker = Get-ContractUtf8CliInvokerPath
    $result = Invoke-ContractProcess `
        -FileName (Get-ContractPowerShellPath) `
        -Arguments (@("-NoLogo", "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", $utf8Invoker, "-ScriptPath", $cliPath) + @($CliArgs)) `
        -WorkingDirectory $ProductPath `
        -TimeoutSeconds 300
    $text = ([string]$result.stdout).Trim()
    $stderr = ([string]$result.stderr).Trim()
    if ([int]$result.exitCode -ne 0) {
        $message = if ($stderr) { $stderr } elseif ($text) { $text } else { "exit code $([int]$result.exitCode)" }
        throw $message
    }
    return ConvertFrom-ContractJsonOutput -Text $text
}

function Invoke-ContractCliText {
    param(
        [string]$Product,
        [string]$ProductPath,
        [string[]]$CliArgs
    )

    $cliPath = Join-Path $ProductPath "cli.ps1"
    if (-not (Test-Path -LiteralPath $cliPath -PathType Leaf)) {
        throw "cli.ps1 was not found: $cliPath"
    }

    $utf8Invoker = Get-ContractUtf8CliInvokerPath
    $result = Invoke-ContractProcess `
        -FileName (Get-ContractPowerShellPath) `
        -Arguments (@("-NoLogo", "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", $utf8Invoker, "-ScriptPath", $cliPath) + @($CliArgs)) `
        -WorkingDirectory $ProductPath `
        -TimeoutSeconds 300
    $text = ([string]$result.stdout).Trim()
    $stderr = ([string]$result.stderr).Trim()
    if ([int]$result.exitCode -ne 0) {
        $message = if ($stderr) { $stderr } elseif ($text) { $text } else { "exit code $([int]$result.exitCode)" }
        throw (Get-ContractTextPreview -Text $message)
    }
    return $text
}

function Test-JsonCommand {
    param(
        [string]$Product,
        [string]$ProductPath,
        [string]$Check,
        [string[]]$CliArgs,
        [string[]]$RequiredProperties
    )

    try {
        $payload = Invoke-ContractCliJson -Product $Product -ProductPath $ProductPath -CliArgs $CliArgs
        foreach ($propertyName in @($RequiredProperties)) {
            if (-not (Test-ContractPropertyExists -Object $payload -Name $propertyName)) {
                throw "missing property: $propertyName"
            }
        }
        Add-ContractResult -Product $Product -Check $Check -Status "PASS" -Message "JSON contract matched."
        return $payload
    }
    catch {
        Add-ContractResult -Product $Product -Check $Check -Status "FAIL" -Message $_.Exception.Message
        return $null
    }
}

function Get-ContractItemCount {
    param([object]$Payload)

    $value = Get-ContractProperty -Object $Payload -Names @("total_items", "item_count", "total", "count") -Default 0
    try {
        return [int]$value
    }
    catch {
        return 0
    }
}

function Convert-ContractLocalPath {
    param(
        [string]$Path,
        [string]$ProductPath
    )

    $text = ([string]$Path).Trim()
    if (-not $text) {
        return ""
    }
    if ($text.StartsWith("/mnt/c/", [System.StringComparison]::OrdinalIgnoreCase)) {
        return "C:\" + $text.Substring(7).Replace("/", "\")
    }
    if ($text.Equals("/workspace", [System.StringComparison]::OrdinalIgnoreCase)) {
        return $ProductPath
    }
    if ($text.StartsWith("/workspace/", [System.StringComparison]::OrdinalIgnoreCase)) {
        return Join-Path $ProductPath $text.Substring("/workspace/".Length).Replace("/", "\")
    }
    return $text
}

function Test-ContractContainerPrefixedWindowsPath {
    param([string]$Path)

    $text = ([string]$Path).Trim()
    return ($text -match '^/[A-Za-z0-9_.-]+/[A-Za-z]:[\\/]')
}

function Assert-ContractZip {
    param(
        [string]$Path,
        [string]$Product,
        [string]$ProductPath
    )

    if (Test-ContractContainerPrefixedWindowsPath -Path $Path) {
        throw "CLI returned a container-prefixed Windows path. The product must write to the requested host path and return that host path. Returned path: $Path"
    }

    $localPath = Convert-ContractLocalPath -Path $Path -ProductPath $ProductPath
    if (-not $localPath -or -not (Test-Path -LiteralPath $localPath -PathType Leaf)) {
        throw "ZIP was not found. Returned path: $Path"
    }
    if (-not [System.IO.Path]::GetExtension($localPath).Equals(".zip", [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Returned file is not a ZIP: $localPath"
    }
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $zip = [System.IO.Compression.ZipFile]::OpenRead($localPath)
    try {
        if ($zip.Entries.Count -le 0) {
            throw "ZIP has no entries: $localPath"
        }
    }
    finally {
        $zip.Dispose()
    }
}

function Test-DownloadCommand {
    param(
        [string]$Product,
        [string]$ProductPath,
        [string[]]$CliArgs,
        [object]$ItemsPayload
    )

    if (-not $IncludeDownloads) {
        Add-ContractResult -Product $Product -Check "download" -Status "SKIP" -Message "Use -IncludeDownloads to verify download creation."
        return
    }

    $itemCount = Get-ContractItemCount -Payload $ItemsPayload
    if ($itemCount -le 0) {
        Add-ContractResult -Product $Product -Check "download" -Status "SKIP" -Message "No items are available."
        return
    }

    try {
        $stdout = Invoke-ContractCliText -Product $Product -ProductPath $ProductPath -CliArgs $CliArgs
        $archivePath = [string](Get-ContractJsonStringPropertyFromOutput -Text $stdout -Names @("archive_path", "archivePath", "download_path", "downloadPath", "destination_path", "destinationPath"))
        if (-not $archivePath) {
            $payload = ConvertFrom-ContractJsonOutput -Text $stdout
            $archivePath = [string](Get-ContractProperty -Object $payload -Names @("archive_path", "archivePath", "download_path", "downloadPath", "destination_path", "destinationPath") -Default "")
        }
        if (-not $archivePath) {
            throw "Download command did not return a ZIP path. Output: $(Get-ContractTextPreview -Text $stdout)"
        }
        Assert-ContractZip -Path $archivePath -Product $Product -ProductPath $ProductPath
        Add-ContractResult -Product $Product -Check "download" -Status "PASS" -Message "ZIP was created."
    }
    catch {
        Add-ContractResult -Product $Product -Check "download" -Status "FAIL" -Message $_.Exception.Message
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

Write-Host "Checking sub-product CLI contracts through cli.ps1 only."

$audioSettings = Test-JsonCommand -Product "TimelineForAudio" -ProductPath $AudioProductPath -Check "settings status" -CliArgs @("settings", "status", "--json") -RequiredProperties @("setup", "inputs", "master")
$audioFiles = Test-JsonCommand -Product "TimelineForAudio" -ProductPath $AudioProductPath -Check "files list paging" -CliArgs @("files", "list", "--page", "1", "--page-size", "1", "--json") -RequiredProperties @("pagination", "files")
$audioItems = Test-JsonCommand -Product "TimelineForAudio" -ProductPath $AudioProductPath -Check "items list paging" -CliArgs @("items", "list", "--page", "1", "--page-size", "1", "--json") -RequiredProperties @("pagination", "items")
$audioZip = Join-Path (New-DownloadDirectory -Product "audio") "timelineforaudio-contract.zip"
Test-DownloadCommand -Product "TimelineForAudio" -ProductPath $AudioProductPath -CliArgs @("items", "download", "--output", $audioZip, "--json") -ItemsPayload $audioItems

$windowsSettings = Test-JsonCommand -Product "TimelineForWindowsCodex" -ProductPath $WindowsCodexProductPath -Check "settings status" -CliArgs @("settings", "status", "--json") -RequiredProperties @("outputRoot")
$windowsItems = Test-JsonCommand -Product "TimelineForWindowsCodex" -ProductPath $WindowsCodexProductPath -Check "items list paging" -CliArgs @("items", "list", "--page", "1", "--page-size", "1", "--json") -RequiredProperties @("pagination", "items")
$windowsTo = New-DownloadDirectory -Product "windows-codex"
Test-DownloadCommand -Product "TimelineForWindowsCodex" -ProductPath $WindowsCodexProductPath -CliArgs @("items", "download", "--to", $windowsTo, "--overwrite", "--json") -ItemsPayload $windowsItems

$chatGptSettings = Test-JsonCommand -Product "TimelineForChatGPT" -ProductPath $ChatGptProductPath -Check "settings status" -CliArgs @("settings", "status", "--json") -RequiredProperties @("output_root")
$chatGptItems = Test-JsonCommand -Product "TimelineForChatGPT" -ProductPath $ChatGptProductPath -Check "items list paging" -CliArgs @("items", "list", "--page", "1", "--page-size", "1", "--json") -RequiredProperties @("pagination", "items")
$chatGptTo = New-DownloadDirectory -Product "chatgpt"
Test-DownloadCommand -Product "TimelineForChatGPT" -ProductPath $ChatGptProductPath -CliArgs @("items", "download", "--to", $chatGptTo, "--overwrite", "--json") -ItemsPayload $chatGptItems

$imageSettings = Test-JsonCommand -Product "TimelineForImage" -ProductPath $ImageProductPath -Check "settings status" -CliArgs @("--json", "settings", "status") -RequiredProperties @("settings", "resolved")
$imageFiles = Test-JsonCommand -Product "TimelineForImage" -ProductPath $ImageProductPath -Check "files list paging" -CliArgs @("--json", "files", "list", "--page", "1", "--page-size", "1") -RequiredProperties @("count", "files")
$imageItems = Test-JsonCommand -Product "TimelineForImage" -ProductPath $ImageProductPath -Check "items list paging" -CliArgs @("--json", "items", "list", "--page", "1", "--page-size", "1") -RequiredProperties @("count", "items")
$imageTo = New-DownloadDirectory -Product "image"
Test-DownloadCommand -Product "TimelineForImage" -ProductPath $ImageProductPath -CliArgs @("--json", "items", "download", "--to", $imageTo, "--overwrite") -ItemsPayload $imageItems

if ($script:Failures.Count -gt 0) {
    Write-Host ""
    Write-Host "Product CLI contract check failed."
    foreach ($failure in $script:Failures) {
        Write-Host $failure
    }
    exit 1
}

Write-Host ""
Write-Host "Product CLI contract check passed."
