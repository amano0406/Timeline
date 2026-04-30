[CmdletBinding()]
param(
    [int]$Port = 19001,
    [string]$AudioProductPath = "C:\apps\TimelineForAudio"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System.Web

$allowedOrigins = @(
    "http://127.0.0.1:19000",
    "http://localhost:19000"
)

function ConvertTo-TimelineJson {
    param([Parameter(Mandatory = $true)][object]$Payload)
    return ConvertTo-Json -InputObject $Payload -Compress -Depth 20
}

function Get-PropertyValue {
    param(
        [object]$Object,
        [string]$Name,
        [object]$Default = $null
    )

    if ($null -eq $Object) {
        return $Default
    }
    if ($Object.PSObject.Properties.Name -contains $Name) {
        return $Object.$Name
    }
    return $Default
}

function New-TimelineRootRow {
    param(
        [object]$Source,
        [string]$FallbackId
    )

    $path = [string](Get-PropertyValue -Object $Source -Name "path" -Default "")
    $id = [string](Get-PropertyValue -Object $Source -Name "id" -Default $FallbackId)
    $displayName = [string](Get-PropertyValue -Object $Source -Name "displayName" -Default $id)
    $enabledValue = Get-PropertyValue -Object $Source -Name "enabled" -Default $true
    return [ordered]@{
        id = if ($id.Trim()) { $id.Trim() } else { $FallbackId }
        displayName = if ($displayName.Trim()) { $displayName.Trim() } else { $FallbackId }
        path = $path
        enabled = [bool]$enabledValue
    }
}

function Get-TimelineSettingsPath {
    $settingsPath = Join-Path $AudioProductPath "settings.json"
    if (Test-Path -LiteralPath $settingsPath) {
        return $settingsPath
    }
    return Join-Path $AudioProductPath "settings.example.json"
}

function Read-TimelineAudioSettings {
    $path = Get-TimelineSettingsPath
    if (-not (Test-Path -LiteralPath $path)) {
        return [ordered]@{
            schemaVersion = 1
            inputRoots = @()
            outputRoots = @()
            audioExtensions = @(".mp3", ".wav", ".m4a", ".aac", ".flac")
            huggingfaceToken = ""
            computeMode = "cpu"
        }
    }

    $payload = Get-Content -LiteralPath $path -Raw | ConvertFrom-Json
    $inputRows = @()
    $inputIndex = 1
    foreach ($row in @(Get-PropertyValue -Object $payload -Name "inputRoots" -Default @())) {
        $inputRows += New-TimelineRootRow -Source $row -FallbackId "audio-$inputIndex"
        $inputIndex += 1
    }

    $outputRows = @()
    $outputIndex = 1
    foreach ($row in @(Get-PropertyValue -Object $payload -Name "outputRoots" -Default @())) {
        $outputRows += New-TimelineRootRow -Source $row -FallbackId "runs"
        $outputIndex += 1
    }

    $extensions = @(Get-PropertyValue -Object $payload -Name "audioExtensions" -Default @(".mp3", ".wav", ".m4a", ".aac", ".flac"))
    if ($extensions.Count -eq 0) {
        $extensions = @(".mp3", ".wav", ".m4a", ".aac", ".flac")
    }

    $computeMode = ([string](Get-PropertyValue -Object $payload -Name "computeMode" -Default "cpu")).Trim().ToLowerInvariant()
    if ($computeMode -notin @("cpu", "gpu")) {
        $computeMode = "cpu"
    }

    return [ordered]@{
        schemaVersion = 1
        inputRoots = $inputRows
        outputRoots = $outputRows
        audioExtensions = $extensions
        huggingfaceToken = [string](Get-PropertyValue -Object $payload -Name "huggingfaceToken" -Default "")
        computeMode = $computeMode
    }
}

function Get-TimelineTokenPreview {
    param([string]$Token)

    $value = ([string]$Token).Trim()
    if (-not $value) {
        return ""
    }
    $bullet = [char]0x2022
    if ($value.Length -le 8) {
        return ([string]$bullet) * $value.Length
    }
    return $value.Substring(0, 4) + (([string]$bullet) * [Math]::Max(4, $value.Length - 8)) + $value.Substring($value.Length - 4)
}

function Write-TimelineAudioSettings {
    param([object]$Request)

    if (-not (Test-Path -LiteralPath $AudioProductPath)) {
        throw "TimelineForAudio was not found: $AudioProductPath"
    }

    $current = Read-TimelineAudioSettings
    $inputRoots = @()
    $inputIndex = 1
    foreach ($row in @(Get-PropertyValue -Object $Request -Name "inputRoots" -Default @())) {
        $root = New-TimelineRootRow -Source $row -FallbackId "audio-$inputIndex"
        if ([string]$root.path) {
            $inputRoots += $root
            $inputIndex += 1
        }
    }

    $outputPath = [string](Get-PropertyValue -Object $Request -Name "outputPath" -Default "")
    $outputRoot = Get-PropertyValue -Object $Request -Name "outputRoot" -Default $null
    if ($outputRoot) {
        $outputPath = [string](Get-PropertyValue -Object $outputRoot -Name "path" -Default $outputPath)
    }
    if (-not $outputPath.Trim()) {
        $existingOutput = @($current.outputRoots) | Select-Object -First 1
        $outputPath = [string](Get-PropertyValue -Object $existingOutput -Name "path" -Default "")
    }

    $computeMode = ([string](Get-PropertyValue -Object $Request -Name "computeMode" -Default $current.computeMode)).Trim().ToLowerInvariant()
    if ($computeMode -notin @("cpu", "gpu")) {
        $computeMode = "cpu"
    }

    $token = [string]$current.huggingfaceToken
    if ($Request.PSObject.Properties.Name -contains "token") {
        $requestToken = Get-PropertyValue -Object $Request -Name "token" -Default $null
        if ($null -ne $requestToken) {
            $token = [string]$requestToken
        }
    }

    $payload = [ordered]@{
        schemaVersion = 1
        inputRoots = $inputRoots
        outputRoots = @(
            [ordered]@{
                id = "master"
                displayName = "TimelineForAudio Master"
                path = $outputPath
                enabled = $true
            }
        )
        audioExtensions = @($current.audioExtensions)
        huggingfaceToken = $token.Trim()
        computeMode = $computeMode
    }

    $settingsPath = Join-Path $AudioProductPath "settings.json"
    $json = ConvertTo-Json -InputObject $payload -Depth 20
    $utf8NoBom = [System.Text.UTF8Encoding]::new($false)
    [System.IO.File]::WriteAllText($settingsPath, $json + [Environment]::NewLine, $utf8NoBom)
    return Get-TimelineAudioOverview
}

function Get-TimelineWorkerState {
    try {
        $docker = Get-Command docker.exe -ErrorAction SilentlyContinue
        if (-not $docker) {
            $docker = Get-Command docker -ErrorAction SilentlyContinue
        }
        if (-not $docker) {
            return "unknown"
        }
        $dockerPath = $docker.Source
        $dockerArgs = @("ps", "--filter", "name=timeline-for-audio-worker", "--quiet")
        $status = & $dockerPath @dockerArgs 2>$null | Select-Object -First 1
        if ($status) {
            return "running"
        }
        return "stopped"
    }
    catch {
        return "unknown"
    }
}

function Get-TimelineAudioFiles {
    $settings = Read-TimelineAudioSettings
    $extensions = @($settings.audioExtensions | ForEach-Object {
        $text = ([string]$_).Trim().ToLowerInvariant()
        if ($text.StartsWith(".")) { $text } else { ".$text" }
    })
    if ($extensions.Count -eq 0) {
        $extensions = @(".mp3", ".wav", ".m4a", ".aac", ".flac")
    }

    $rows = @()
    $total = 0
    foreach ($root in @($settings.inputRoots)) {
        if (-not [bool]$root.enabled -or -not [string]$root.path) {
            continue
        }
        if (-not (Test-Path -LiteralPath ([string]$root.path))) {
            continue
        }

        $rootPath = (Resolve-Path -LiteralPath ([string]$root.path)).Path
        foreach ($file in Get-ChildItem -LiteralPath $rootPath -Recurse -File -ErrorAction SilentlyContinue) {
            if ($extensions -notcontains $file.Extension.ToLowerInvariant()) {
                continue
            }
            $total += 1
            if ($rows.Count -ge 500) {
                continue
            }

            $relativePath = $file.FullName
            if ($file.FullName.StartsWith($rootPath, [System.StringComparison]::OrdinalIgnoreCase)) {
                $relativePath = $file.FullName.Substring($rootPath.Length).TrimStart('\', '/')
            }
            $directory = [System.IO.Path]::GetDirectoryName($relativePath)
            if ($null -eq $directory) {
                $directory = ""
            }
            $rows += [ordered]@{
                sourceId = [string]$root.id
                sourceDisplayName = [string]$root.displayName
                sourceName = [string]$root.displayName
                rootPath = [string]$root.path
                displayPath = $file.FullName
                relativePath = $relativePath
                directory = $directory
                fileName = $file.Name
                sizeBytes = [int64]$file.Length
                modifiedAt = $file.LastWriteTime.ToString("yyyy-MM-dd HH:mm:ss")
                status = "detected"
                durationSec = $null
                hasTimeline = $false
                hasAudio = $false
                runId = ""
                mediaId = ""
                turnCount = 0
                speakerCount = 0
            }
        }
    }

    return [ordered]@{
        total = $total
        truncated = ($total -gt $rows.Count)
        files = @($rows | Sort-Object sourceDisplayName, relativePath)
    }
}

function Get-TimelineAudioOverview {
    $settings = Read-TimelineAudioSettings
    $files = Get-TimelineAudioFiles
    $outputRoot = @($settings.outputRoots) | Select-Object -First 1
    return [ordered]@{
        productFound = (Test-Path -LiteralPath $AudioProductPath)
        productPath = $AudioProductPath
        hasToken = [bool](([string]$settings.huggingfaceToken).Trim())
        tokenPreview = Get-TimelineTokenPreview -Token ([string]$settings.huggingfaceToken)
        computeMode = [string]$settings.computeMode
        inputRoots = @($settings.inputRoots)
        outputRoot = $outputRoot
        audioFileCount = [int]$files.total
        workerState = Get-TimelineWorkerState
        restartRequired = $false
        message = "TimelineForAudio is linked as a local product."
    }
}

function Show-TimelineDirectoryPicker {
    param(
        [string]$Title,
        [string]$InitialPath
    )

    $dialog = [System.Windows.Forms.FolderBrowserDialog]::new()
    $dialog.Description = if ($Title) { $Title } else { "Select directory" }
    $dialog.ShowNewFolderButton = $true

    if ($InitialPath -and (Test-Path -LiteralPath $InitialPath)) {
        $dialog.SelectedPath = (Resolve-Path -LiteralPath $InitialPath).Path
    }

    $owner = [System.Windows.Forms.Form]::new()
    $owner.TopMost = $true
    $owner.ShowInTaskbar = $false
    $owner.StartPosition = [System.Windows.Forms.FormStartPosition]::CenterScreen
    $owner.Size = [System.Drawing.Size]::new(1, 1)

    try {
        $result = $dialog.ShowDialog($owner)
        if ($result -eq [System.Windows.Forms.DialogResult]::OK) {
            return [ordered]@{ ok = $true; cancelled = $false; path = $dialog.SelectedPath }
        }
        return [ordered]@{ ok = $true; cancelled = $true; path = $null }
    }
    finally {
        $dialog.Dispose()
        $owner.Dispose()
    }
}

function Read-TimelineRequest {
    param([Parameter(Mandatory = $true)][System.Net.Sockets.TcpClient]$Client)

    $stream = $Client.GetStream()
    $buffer = New-Object byte[] 8192
    $bytes = [System.Collections.Generic.List[byte]]::new()
    $headerEnd = -1

    while ($headerEnd -lt 0) {
        $read = $stream.Read($buffer, 0, $buffer.Length)
        if ($read -le 0) {
            break
        }
        for ($index = 0; $index -lt $read; $index += 1) {
            $bytes.Add($buffer[$index]) | Out-Null
        }
        $text = [System.Text.Encoding]::ASCII.GetString($bytes.ToArray())
        $headerEnd = $text.IndexOf("`r`n`r`n", [System.StringComparison]::Ordinal)
    }

    $allBytes = $bytes.ToArray()
    $requestText = [System.Text.Encoding]::ASCII.GetString($allBytes)
    $headerText = if ($headerEnd -ge 0) { $requestText.Substring(0, $headerEnd) } else { $requestText }
    $lines = $headerText -split "`r`n"
    $contentLength = 0
    foreach ($line in $lines) {
        if ($line.StartsWith("Content-Length:", [System.StringComparison]::OrdinalIgnoreCase)) {
            [void][int]::TryParse($line.Substring("Content-Length:".Length).Trim(), [ref]$contentLength)
        }
    }

    $bodyStart = if ($headerEnd -ge 0) { $headerEnd + 4 } else { $allBytes.Length }
    $bodyBytes = [System.Collections.Generic.List[byte]]::new()
    for ($index = $bodyStart; $index -lt $allBytes.Length; $index += 1) {
        $bodyBytes.Add($allBytes[$index]) | Out-Null
    }
    while ($bodyBytes.Count -lt $contentLength) {
        $read = $stream.Read($buffer, 0, [Math]::Min($buffer.Length, $contentLength - $bodyBytes.Count))
        if ($read -le 0) {
            break
        }
        for ($index = 0; $index -lt $read; $index += 1) {
            $bodyBytes.Add($buffer[$index]) | Out-Null
        }
    }

    return [ordered]@{
        Lines = $lines
        Body = [System.Text.Encoding]::UTF8.GetString($bodyBytes.ToArray())
    }
}

function Get-TimelineHeader {
    param(
        [string[]]$Lines,
        [string]$Name
    )

    $prefix = "$Name`:"
    foreach ($line in $Lines) {
        if ($line.StartsWith($prefix, [System.StringComparison]::OrdinalIgnoreCase)) {
            return $line.Substring($prefix.Length).Trim()
        }
    }
    return ""
}

function Send-TimelineResponse {
    param(
        [Parameter(Mandatory = $true)][System.Net.Sockets.TcpClient]$Client,
        [int]$StatusCode,
        [string]$StatusText,
        [string]$Body,
        [string]$Origin = ""
    )

    $headers = [System.Collections.Generic.List[string]]::new()
    $headers.Add("HTTP/1.1 $StatusCode $StatusText") | Out-Null
    $headers.Add("Content-Type: application/json; charset=utf-8") | Out-Null
    $headers.Add("Cache-Control: no-store") | Out-Null
    $headers.Add("Connection: close") | Out-Null
    $headers.Add("Access-Control-Allow-Methods: GET, POST, OPTIONS") | Out-Null
    $headers.Add("Access-Control-Allow-Headers: Content-Type") | Out-Null
    if ($allowedOrigins -contains $Origin) {
        $headers.Add("Access-Control-Allow-Origin: $Origin") | Out-Null
        $headers.Add("Vary: Origin") | Out-Null
    }
    elseif (-not $Origin) {
        $headers.Add("Access-Control-Allow-Origin: *") | Out-Null
    }

    $bodyBytes = [System.Text.Encoding]::UTF8.GetBytes($Body)
    $headers.Add("Content-Length: $($bodyBytes.Length)") | Out-Null
    $headers.Add("") | Out-Null
    $headers.Add("") | Out-Null

    $headerBytes = [System.Text.Encoding]::ASCII.GetBytes([string]::Join("`r`n", $headers.ToArray()))
    $stream = $Client.GetStream()
    $stream.Write($headerBytes, 0, $headerBytes.Length)
    if ($bodyBytes.Length -gt 0) {
        $stream.Write($bodyBytes, 0, $bodyBytes.Length)
    }
}

$listener = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Parse("127.0.0.1"), $Port)
try {
    $listener.Start()
}
catch {
    exit 0
}

try {
    while ($true) {
        $client = $listener.AcceptTcpClient()
        try {
            $request = Read-TimelineRequest -Client $client
            $lines = [string[]]$request.Lines
            $requestLine = if ($lines.Count -gt 0) { $lines[0] } else { "" }
            $origin = Get-TimelineHeader -Lines $lines -Name "Origin"

            if ($requestLine -notmatch '^(?<method>GET|POST|OPTIONS) (?<target>[^ ]+) HTTP/') {
                Send-TimelineResponse -Client $client -StatusCode 400 -StatusText "Bad Request" -Origin $origin -Body (ConvertTo-TimelineJson @{ ok = $false })
                continue
            }

            $method = $Matches.method
            $target = $Matches.target
            $uri = [System.Uri]::new("http://127.0.0.1:$Port$target")

            if ($method -eq "OPTIONS") {
                Send-TimelineResponse -Client $client -StatusCode 200 -StatusText "OK" -Origin $origin -Body "{}"
                continue
            }

            if ($method -eq "GET" -and $uri.AbsolutePath -eq "/health") {
                Send-TimelineResponse -Client $client -StatusCode 200 -StatusText "OK" -Origin $origin -Body (ConvertTo-TimelineJson @{ ok = $true })
                continue
            }

            if ($method -eq "GET" -and $uri.AbsolutePath -eq "/pick-directory") {
                $query = [System.Web.HttpUtility]::ParseQueryString($uri.Query)
                $payload = Show-TimelineDirectoryPicker `
                    -Title ([string]$query["title"]) `
                    -InitialPath ([string]$query["initialPath"])
                Send-TimelineResponse -Client $client -StatusCode 200 -StatusText "OK" -Origin $origin -Body (ConvertTo-TimelineJson $payload)
                continue
            }

            if ($method -eq "GET" -and $uri.AbsolutePath -eq "/products/audio/overview") {
                Send-TimelineResponse -Client $client -StatusCode 200 -StatusText "OK" -Origin $origin -Body (ConvertTo-TimelineJson (Get-TimelineAudioOverview))
                continue
            }

            if ($method -eq "GET" -and $uri.AbsolutePath -eq "/products/audio/files") {
                Send-TimelineResponse -Client $client -StatusCode 200 -StatusText "OK" -Origin $origin -Body (ConvertTo-TimelineJson (Get-TimelineAudioFiles))
                continue
            }

            if ($method -eq "POST" -and $uri.AbsolutePath -eq "/products/audio/settings") {
                $payload = if ([string]$request.Body) { $request.Body | ConvertFrom-Json } else { @{} }
                Send-TimelineResponse -Client $client -StatusCode 200 -StatusText "OK" -Origin $origin -Body (ConvertTo-TimelineJson (Write-TimelineAudioSettings -Request $payload))
                continue
            }

            Send-TimelineResponse -Client $client -StatusCode 404 -StatusText "Not Found" -Origin $origin -Body (ConvertTo-TimelineJson @{ ok = $false })
        }
        catch {
            try {
                Send-TimelineResponse -Client $client -StatusCode 500 -StatusText "Internal Server Error" -Body (ConvertTo-TimelineJson @{ ok = $false; message = $_.Exception.Message })
            }
            catch {
            }
        }
        finally {
            $client.Close()
        }
    }
}
finally {
    $listener.Stop()
}
