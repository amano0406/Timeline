[CmdletBinding()]
param(
    [int]$Port = 19001,
    [string]$TimelineProductPath = "C:\apps\Timeline",
    [string]$AudioProductPath = "C:\apps\TimelineForAudio",
    [string]$WindowsCodexProductPath = "C:\apps\TimelineForWindowsCodex",
    [string]$ChatGptProductPath = "C:\apps\TimelineForChatGPT"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System.Web
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem

$allowedOrigins = @(
    "http://127.0.0.1:19000",
    "http://localhost:19000"
)

$script:TimelineHardwareDevicesCache = $null
$script:TimelineModelInventoryCache = $null
$script:TimelineModelInventoryCacheAt = $null

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
    if ($Object -is [System.Collections.IDictionary] -and $Object.Contains($Name)) {
        return $Object[$Name]
    }
    $property = $Object.PSObject.Properties[$Name]
    if ($null -ne $property) {
        return $property.Value
    }
    return $Default
}

function Get-PropertyValueAny {
    param(
        [object]$Object,
        [string[]]$Names,
        [object]$Default = $null
    )

    foreach ($name in @($Names)) {
        $value = Get-PropertyValue -Object $Object -Name $name -Default $null
        if ($null -ne $value) {
            return $value
        }
    }
    return $Default
}

function Get-TimelineAppSettingsPath {
    return Join-Path $TimelineProductPath "settings.json"
}

function Get-TimelineTimeZoneOptions {
    return @(
        [ordered]@{ id = "Asia/Tokyo"; label = "Japan (Asia/Tokyo)" },
        [ordered]@{ id = "UTC"; label = "UTC" },
        [ordered]@{ id = "America/Los_Angeles"; label = "US Pacific (America/Los_Angeles)" },
        [ordered]@{ id = "America/New_York"; label = "US Eastern (America/New_York)" },
        [ordered]@{ id = "Europe/London"; label = "UK (Europe/London)" }
    )
}

function Get-TimelineDisplayLanguageOptions {
    $japaneseLabel = ([string][char]0x65E5) + ([string][char]0x672C) + ([string][char]0x8A9E)
    return @(
        [ordered]@{ id = "ja-JP"; label = $japaneseLabel },
        [ordered]@{ id = "en-US"; label = "English" }
    )
}

function Read-TimelineAppSettings {
    $path = Get-TimelineAppSettingsPath
    $displayLanguageId = "ja-JP"
    $timeZoneId = "Asia/Tokyo"
    $workDirectory = "C:\TimelineData\Timeline\work"
    if (Test-Path -LiteralPath $path) {
        try {
            $payload = Get-Content -LiteralPath $path -Raw -Encoding UTF8 | ConvertFrom-Json
            $languageCandidate = Convert-TimelineText -Value (Get-PropertyValue -Object $payload -Name "displayLanguageId" -Default "")
            if ($languageCandidate) {
                $displayLanguageId = $languageCandidate
            }
            $candidate = Convert-TimelineText -Value (Get-PropertyValue -Object $payload -Name "timeZoneId" -Default "")
            if ($candidate) {
                $timeZoneId = $candidate
            }
            $workDirectoryCandidate = Convert-TimelineText -Value (Get-PropertyValue -Object $payload -Name "workDirectory" -Default "")
            if ($workDirectoryCandidate) {
                $workDirectory = $workDirectoryCandidate
            }
        }
        catch {
            $displayLanguageId = "ja-JP"
            $timeZoneId = "Asia/Tokyo"
            $workDirectory = "C:\TimelineData\Timeline\work"
        }
    }

    $allowedLanguages = @(Get-TimelineDisplayLanguageOptions | ForEach-Object { [string]$_.id })
    if ($allowedLanguages -notcontains $displayLanguageId) {
        $displayLanguageId = "ja-JP"
    }

    return [ordered]@{
        schemaVersion = 1
        displayLanguageId = $displayLanguageId
        displayLanguages = @(Get-TimelineDisplayLanguageOptions)
        timeZoneId = $timeZoneId
        timeZones = @(Get-TimelineTimeZoneOptions)
        workDirectory = $workDirectory
    }
}

function Write-TimelineAppSettings {
    param([object]$Request)

    $displayLanguageId = Convert-TimelineText -Value (Get-PropertyValue -Object $Request -Name "displayLanguageId" -Default "")
    if (-not $displayLanguageId) {
        throw "Display language is required."
    }

    $allowedLanguages = @(Get-TimelineDisplayLanguageOptions | ForEach-Object { [string]$_.id })
    if ($allowedLanguages -notcontains $displayLanguageId) {
        throw "Unsupported display language."
    }

    $timeZoneId = Convert-TimelineText -Value (Get-PropertyValue -Object $Request -Name "timeZoneId" -Default "")
    if (-not $timeZoneId) {
        throw "Time zone is required."
    }

    $allowed = @(Get-TimelineTimeZoneOptions | ForEach-Object { [string]$_.id })
    if ($allowed -notcontains $timeZoneId) {
        throw "Unsupported time zone."
    }

    $current = Read-TimelineAppSettings
    $workDirectory = Convert-TimelineText -Value (Get-PropertyValue -Object $Request -Name "workDirectory" -Default "")
    if (-not $workDirectory) {
        $workDirectory = Convert-TimelineText -Value (Get-PropertyValue -Object $current -Name "workDirectory" -Default "C:\TimelineData\Timeline\work")
    }

    if (-not (Test-Path -LiteralPath $TimelineProductPath)) {
        [System.IO.Directory]::CreateDirectory($TimelineProductPath) | Out-Null
    }

    $payload = [ordered]@{
        schemaVersion = 1
        displayLanguageId = $displayLanguageId
        timeZoneId = $timeZoneId
        workDirectory = $workDirectory
    }
    Write-TimelineUtf8JsonFile -Path (Get-TimelineAppSettingsPath) -Payload $payload
    return Read-TimelineAppSettings
}

function New-TimelineRootRow {
    param(
        [object]$Source,
        [string]$FallbackId
    )

    if ($Source -is [string]) {
        $path = ([string]$Source).Trim()
        $id = if ($FallbackId) { $FallbackId } else { $path }
        return [ordered]@{
            id = $id
            displayName = if ($path) { Split-Path -Leaf $path.TrimEnd('\', '/') } else { $id }
            path = $path
            enabled = $true
        }
    }

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

function Convert-TimelineRootPath {
    param([object]$Root)

    if ($Root -is [string]) {
        return ([string]$Root).Trim()
    }
    return Convert-TimelineText -Value (Get-PropertyValue -Object $Root -Name "path" -Default "")
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
            outputRoot = $null
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
    $outputRootValue = Get-PropertyValue -Object $payload -Name "outputRoot" -Default $null
    if ($null -ne $outputRootValue) {
        $outputRows += New-TimelineRootRow -Source $outputRootValue -FallbackId "master"
    }
    else {
        foreach ($row in @(Get-PropertyValue -Object $payload -Name "outputRoots" -Default @())) {
            $outputRows += New-TimelineRootRow -Source $row -FallbackId "master"
        }
    }
    $outputRoot = @($outputRows) | Select-Object -First 1

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
        outputRoot = $outputRoot
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
    $requestedInputRoots = @()
    foreach ($row in @(Get-PropertyValue -Object $Request -Name "inputRoots" -Default @())) {
        $root = New-TimelineRootRow -Source $row -FallbackId ""
        if ([string]$root.path) {
            $requestedInputRoots += $root
        }
    }

    $outputPath = [string](Get-PropertyValue -Object $Request -Name "outputPath" -Default "")
    $outputRoot = Get-PropertyValue -Object $Request -Name "outputRoot" -Default $null
    if ($outputRoot) {
        $outputPath = [string](Get-PropertyValue -Object $outputRoot -Name "path" -Default $outputPath)
    }
    if (-not $outputPath.Trim()) {
        $existingOutput = Get-TimelineAudioOutputRoot -Settings $current
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

    $saveArgs = @("settings", "save", "--compute-mode", $computeMode, "--json")
    if ($Request.PSObject.Properties.Name -contains "token") {
        $saveArgs += @("--token", $token.Trim())
    }
    [void](Invoke-TimelineAudioCliJson -CliArgs $saveArgs -TimeoutSeconds 60)

    $currentInputRoots = @(Invoke-TimelineAudioCliJson -CliArgs @("settings", "inputs", "list", "--json") -TimeoutSeconds 60)
    $requestedPaths = @($requestedInputRoots | ForEach-Object { ([string]$_.path).Trim() } | Where-Object { $_ })
    $requestedPathKeys = @{}
    foreach ($path in $requestedPaths) {
        $requestedPathKeys[$path.Trim().TrimEnd('\', '/').ToLowerInvariant()] = $true
    }

    foreach ($row in @($currentInputRoots)) {
        $currentPath = Convert-TimelineRootPath -Root $row
        $currentKey = $currentPath.Trim().TrimEnd('\', '/').ToLowerInvariant()
        if ($currentPath -and -not $requestedPathKeys.ContainsKey($currentKey)) {
            [void](Invoke-TimelineAudioCliJson -CliArgs @("settings", "inputs", "remove", $currentPath, "--json") -TimeoutSeconds 60)
        }
    }

    $afterRemoveRoots = @(Invoke-TimelineAudioCliJson -CliArgs @("settings", "inputs", "list", "--json") -TimeoutSeconds 60)
    $existingPathKeys = @{}
    foreach ($row in @($afterRemoveRoots)) {
        $existingPath = Convert-TimelineRootPath -Root $row
        if ($existingPath) {
            $existingPathKeys[$existingPath.Trim().TrimEnd('\', '/').ToLowerInvariant()] = $true
        }
    }

    foreach ($path in $requestedPaths) {
        $key = $path.Trim().TrimEnd('\', '/').ToLowerInvariant()
        if (-not $existingPathKeys.ContainsKey($key)) {
            [void](Invoke-TimelineAudioCliJson -CliArgs @("settings", "inputs", "add", $path, "--json") -TimeoutSeconds 60)
            $existingPathKeys[$key] = $true
        }
    }

    if ($outputPath.Trim()) {
        [void](Invoke-TimelineAudioCliJson -CliArgs @("settings", "master", "set", $outputPath.Trim(), "--json") -TimeoutSeconds 60)
    }

    $script:TimelineModelInventoryCache = $null
    $script:TimelineModelInventoryCacheAt = $null
    return Get-TimelineAudioOverview
}

function Convert-TimelineAudioRunProgressPayload {
    param([object]$Run)

    $state = (Convert-TimelineText -Value (Get-PropertyValue -Object $Run -Name "state" -Default "")).ToLowerInvariant()
    if (-not $state) {
        return $null
    }

    $itemsTotal = Convert-TimelineAudioInt -Value (Get-PropertyValueAny -Object $Run -Names @("items_total", "itemsTotal") -Default 0)
    $itemsDoneRaw = Convert-TimelineAudioInt -Value (Get-PropertyValueAny -Object $Run -Names @("items_done", "itemsDone") -Default 0)
    $itemsSkipped = Convert-TimelineAudioInt -Value (Get-PropertyValueAny -Object $Run -Names @("items_skipped", "itemsSkipped") -Default 0)
    $itemsFailed = Convert-TimelineAudioInt -Value (Get-PropertyValueAny -Object $Run -Names @("items_failed", "itemsFailed") -Default 0)
    $itemsProcessed = $itemsDoneRaw + $itemsSkipped + $itemsFailed
    if ($itemsTotal -gt 0) {
        $itemsProcessed = [Math]::Min($itemsTotal, $itemsProcessed)
    }

    $progress = Convert-TimelineAudioNumber -Value (Get-PropertyValueAny -Object $Run -Names @("progress_percent", "progressPercent") -Default $null)
    if ($null -eq $progress -and $itemsTotal -gt 0) {
        $progress = [Math]::Round(($itemsProcessed / [double]$itemsTotal) * 100, 1)
    }
    if ($null -eq $progress) {
        $progress = 0
    }

    return [ordered]@{
        runId = Convert-TimelineText -Value (Get-PropertyValueAny -Object $Run -Names @("run_id", "runId") -Default "")
        state = $state
        currentStage = Convert-TimelineText -Value (Get-PropertyValueAny -Object $Run -Names @("current_stage", "currentStage") -Default "")
        message = Convert-TimelineText -Value (Get-PropertyValue -Object $Run -Name "message" -Default "")
        itemsTotal = $itemsTotal
        itemsDone = $itemsProcessed
        itemsSkipped = $itemsSkipped
        itemsFailed = $itemsFailed
        progressPercent = [Math]::Max(0, [Math]::Min(100, [double]$progress))
        processedDurationSec = [double](Convert-TimelineAudioNumber -Value (Get-PropertyValueAny -Object $Run -Names @("processed_duration_sec", "processedDurationSec") -Default 0))
        totalDurationSec = [double](Convert-TimelineAudioNumber -Value (Get-PropertyValueAny -Object $Run -Names @("total_duration_sec", "totalDurationSec") -Default 0))
        estimatedRemainingSec = [double](Convert-TimelineAudioNumber -Value (Get-PropertyValueAny -Object $Run -Names @("estimated_remaining_sec", "estimatedRemainingSec") -Default 0))
        currentItem = Convert-TimelineText -Value (Get-PropertyValueAny -Object $Run -Names @("current_item", "currentItem", "current_file", "currentFile") -Default "")
        updatedAt = Convert-TimelineText -Value (Get-PropertyValueAny -Object $Run -Names @("updated_at", "updatedAt", "created_at", "createdAt") -Default "")
    }
}

function New-TimelineAudioRunProgressRow {
    param([object]$Run)

    $payload = Convert-TimelineAudioRunProgressPayload -Run $Run
    if ($null -eq $payload) {
        return $null
    }

    $sortDate = [datetime]::MinValue
    $updatedAt = Convert-TimelineText -Value (Get-PropertyValue -Object $payload -Name "updatedAt" -Default "")
    if ($updatedAt) {
        try {
            $sortDate = ([datetime]::Parse($updatedAt)).ToUniversalTime()
        }
        catch {
            $sortDate = [datetime]::MinValue
        }
    }

    return [pscustomobject]@{
        State = Convert-TimelineText -Value (Get-PropertyValue -Object $payload -Name "state" -Default "")
        SortDate = $sortDate
        Payload = $payload
    }
}

function Get-TimelineAudioRunProgressRows {
    param([object]$Settings)

    $rows = @()
    $outputRootPath = Get-TimelineAudioOutputRootPath -Settings $Settings
    if (-not $outputRootPath -or -not (Test-Path -LiteralPath $outputRootPath)) {
        return @()
    }

    foreach ($runDir in @(Get-ChildItem -LiteralPath $outputRootPath -Directory -Filter "run-*" -ErrorAction SilentlyContinue)) {
        $statusPath = Join-Path $runDir.FullName "status.json"
        if (-not (Test-Path -LiteralPath $statusPath)) {
            continue
        }

        try {
            $status = Get-Content -LiteralPath $statusPath -Raw | ConvertFrom-Json
            $state = ([string](Get-PropertyValue -Object $status -Name "state" -Default "")).Trim().ToLowerInvariant()
            if (-not $state) {
                continue
            }

            $itemsTotal = Convert-TimelineAudioInt -Value (Get-PropertyValue -Object $status -Name "items_total" -Default 0)
            $itemsDone = Convert-TimelineAudioInt -Value (Get-PropertyValue -Object $status -Name "items_done" -Default 0)
            $progress = Convert-TimelineAudioNumber -Value (Get-PropertyValue -Object $status -Name "progress_percent" -Default $null)
            if ($null -eq $progress -and $itemsTotal -gt 0) {
                $progress = [Math]::Round(($itemsDone / [double]$itemsTotal) * 100, 1)
            }

            $payload = [ordered]@{
                runId = Convert-TimelineText -Value (Get-PropertyValue -Object $status -Name "run_id" -Default $runDir.Name)
                state = $state
                currentStage = Convert-TimelineText -Value (Get-PropertyValue -Object $status -Name "current_stage" -Default "")
                message = Convert-TimelineText -Value (Get-PropertyValue -Object $status -Name "message" -Default "")
                itemsTotal = $itemsTotal
                itemsDone = $itemsDone
                itemsSkipped = Convert-TimelineAudioInt -Value (Get-PropertyValue -Object $status -Name "items_skipped" -Default 0)
                itemsFailed = Convert-TimelineAudioInt -Value (Get-PropertyValue -Object $status -Name "items_failed" -Default 0)
                progressPercent = if ($null -ne $progress) { [Math]::Max(0, [Math]::Min(100, [double]$progress)) } else { 0 }
                processedDurationSec = [double](Convert-TimelineAudioNumber -Value (Get-PropertyValue -Object $status -Name "processed_duration_sec" -Default 0))
                totalDurationSec = [double](Convert-TimelineAudioNumber -Value (Get-PropertyValue -Object $status -Name "total_duration_sec" -Default 0))
                estimatedRemainingSec = [double](Convert-TimelineAudioNumber -Value (Get-PropertyValue -Object $status -Name "estimated_remaining_sec" -Default 0))
                currentItem = Convert-TimelineText -Value (Get-PropertyValue -Object $status -Name "current_item" -Default "")
                updatedAt = Convert-TimelineText -Value (Get-PropertyValue -Object $status -Name "updated_at" -Default "")
            }

            $rows += [pscustomobject]@{
                State = $state
                SortDate = (Get-Item -LiteralPath $statusPath).LastWriteTimeUtc
                Payload = $payload
            }
        }
        catch {
        }
    }

    return @($rows | Sort-Object SortDate -Descending)
}

function Get-TimelineActiveAudioRun {
    param([object]$Settings)

    $runs = @(Get-TimelineAudioRunProgressRows -Settings $Settings)
    foreach ($state in @("running", "processing", "pending", "queued")) {
        $match = @($runs | Where-Object { $_.State -eq $state } | Select-Object -First 1)
        if ($match.Count -gt 0) {
            return $match[0].Payload
        }
    }
    return $null
}

function Get-TimelineWorkerState {
    param([object]$Settings = $null)

    try {
        if ($null -eq $Settings) {
            $Settings = Read-TimelineAudioSettings
        }
        $activeRun = Get-TimelineActiveAudioRun -Settings $Settings
        if ($null -ne $activeRun) {
            $state = Convert-TimelineText -Value (Get-PropertyValue -Object $activeRun -Name "state" -Default "")
            if ($state.Equals("running", [System.StringComparison]::OrdinalIgnoreCase) -or $state.Equals("processing", [System.StringComparison]::OrdinalIgnoreCase)) {
                return "processing"
            }
            if ($state.Equals("pending", [System.StringComparison]::OrdinalIgnoreCase) -or $state.Equals("queued", [System.StringComparison]::OrdinalIgnoreCase)) {
                return "starting"
            }
        }
    }
    catch {
    }

    return "unknown"
}

function Get-TimelineHardwareDeviceNames {
    param([string]$ClassName)

    try {
        return @(
            Get-CimInstance -ClassName $ClassName -ErrorAction Stop |
                ForEach-Object { ([string]$_.Name).Trim() } |
                Where-Object { $_ } |
                Select-Object -Unique
        )
    }
    catch {
        return @()
    }
}

function Get-TimelineHardwareDevices {
    if ($null -ne $script:TimelineHardwareDevicesCache) {
        return $script:TimelineHardwareDevicesCache
    }

    $script:TimelineHardwareDevicesCache = [ordered]@{
        cpuDevices = @(Get-TimelineHardwareDeviceNames -ClassName "Win32_Processor")
        gpuDevices = @(Get-TimelineHardwareDeviceNames -ClassName "Win32_VideoController")
    }
    return $script:TimelineHardwareDevicesCache
}

function Convert-TimelineAudioNumber {
    param([object]$Value)

    if ($null -eq $Value) {
        return $null
    }
    try {
        return [double]$Value
    }
    catch {
        return $null
    }
}

function Convert-TimelineAudioInt {
    param([object]$Value)

    if ($null -eq $Value) {
        return 0
    }
    try {
        return [int]$Value
    }
    catch {
        return 0
    }
}

function Convert-TimelineText {
    param([object]$Value)

    if ($null -eq $Value) {
        return ""
    }
    if ($Value -is [bool]) {
        if ($Value) {
            return "true"
        }
        return "false"
    }
    return ([string]$Value).Trim()
}

function Format-TimelineProcessArgument {
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

function Get-TimelinePowerShellPath {
    $candidate = Join-Path $env:SystemRoot "System32\WindowsPowerShell\v1.0\powershell.exe"
    if (Test-Path -LiteralPath $candidate) {
        return $candidate
    }
    return "powershell.exe"
}

function Get-TimelineScopedDockerConfigDir {
    $configDir = Join-Path $TimelineProductPath ".docker\docker-config"
    $configPath = Join-Path $configDir "config.json"
    if (-not (Test-Path -LiteralPath $configDir)) {
        New-Item -ItemType Directory -Path $configDir -Force | Out-Null
    }
    if (-not (Test-Path -LiteralPath $configPath)) {
        Set-Content -LiteralPath $configPath -Value "{}" -Encoding ASCII
    }
    return $configDir
}

function Get-TimelineChildProcessEnvironment {
    $environment = @{}
    $currentPath = [Environment]::GetEnvironmentVariable("PATH", "Process")
    if (-not $currentPath) {
        $currentPath = $env:PATH
    }

    $dockerBin = Join-Path $env:ProgramFiles "Docker\Docker\resources\bin"
    $system32 = Join-Path $env:SystemRoot "System32"
    $powerShellBin = Join-Path $system32 "WindowsPowerShell\v1.0"
    if (Test-Path -LiteralPath (Join-Path $dockerBin "docker.exe")) {
        $pathParts = @($currentPath -split ";") |
            Where-Object { $_ } |
            ForEach-Object { $_.Trim() }

        $extraPaths = @($dockerBin, $system32, $powerShellBin)
        foreach ($extraPath in $extraPaths) {
            if (-not $extraPath -or -not (Test-Path -LiteralPath $extraPath)) {
                continue
            }
            $containsPath = $false
            foreach ($pathPart in $pathParts) {
                if ([string]::Equals($pathPart, $extraPath, [System.StringComparison]::OrdinalIgnoreCase)) {
                    $containsPath = $true
                    break
                }
            }
            if (-not $containsPath) {
                $currentPath = "$extraPath;$currentPath"
                $pathParts = @($currentPath -split ";") |
                    Where-Object { $_ } |
                    ForEach-Object { $_.Trim() }
            }
        }
    }
    else {
        $pathParts = @($currentPath -split ";") |
            Where-Object { $_ } |
            ForEach-Object { $_.Trim() }
        foreach ($extraPath in @($system32, $powerShellBin)) {
            if (-not $extraPath -or -not (Test-Path -LiteralPath $extraPath)) {
                continue
            }
            $containsPath = $false
            foreach ($pathPart in $pathParts) {
                if ([string]::Equals($pathPart, $extraPath, [System.StringComparison]::OrdinalIgnoreCase)) {
                    $containsPath = $true
                    break
                }
            }
            if (-not $containsPath) {
                $currentPath = "$extraPath;$currentPath"
                $pathParts = @($currentPath -split ";") |
                    Where-Object { $_ } |
                    ForEach-Object { $_.Trim() }
            }
        }
    }

    if (Test-Path -LiteralPath (Join-Path $dockerBin "docker.exe")) {
        $pathParts = @($currentPath -split ";") |
            Where-Object { $_ } |
            ForEach-Object { $_.Trim() }

        $containsDockerBin = $false
        foreach ($pathPart in $pathParts) {
            if ([string]::Equals($pathPart, $dockerBin, [System.StringComparison]::OrdinalIgnoreCase)) {
                $containsDockerBin = $true
                break
            }
        }

        if (-not $containsDockerBin) {
            $currentPath = "$dockerBin;$currentPath"
        }
    }

    $environment["PATH"] = $currentPath
    $environment["Path"] = $currentPath
    $environment["PATHEXT"] = ".COM;.EXE;.BAT;.CMD;.VBS;.VBE;.JS;.JSE;.WSF;.WSH;.MSC;.CPL"
    $environment["DOCKER_CONFIG"] = Get-TimelineScopedDockerConfigDir
    return $environment
}

function Invoke-TimelineProcess {
    param(
        [string]$FileName,
        [string[]]$Arguments,
        [string]$WorkingDirectory,
        [int]$TimeoutSeconds = 25,
        [hashtable]$Environment = @{}
    )

    $startInfo = [System.Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $FileName
    $startInfo.Arguments = (@($Arguments) | ForEach-Object { Format-TimelineProcessArgument -Value ([string]$_) }) -join " "
    $startInfo.WorkingDirectory = $WorkingDirectory
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    $startInfo.StandardOutputEncoding = [System.Text.UTF8Encoding]::new($false)
    $startInfo.StandardErrorEncoding = [System.Text.UTF8Encoding]::new($false)
    foreach ($key in @($Environment.Keys)) {
        $startInfo.EnvironmentVariables[[string]$key] = [string]$Environment[$key]
    }

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

function Invoke-TimelineProcessNoOutput {
    param(
        [string]$FileName,
        [string[]]$Arguments,
        [string]$WorkingDirectory,
        [int]$TimeoutSeconds = 25,
        [hashtable]$Environment = @{}
    )

    $startInfo = [System.Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $FileName
    $startInfo.Arguments = (@($Arguments) | ForEach-Object { Format-TimelineProcessArgument -Value ([string]$_) }) -join " "
    $startInfo.WorkingDirectory = $WorkingDirectory
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.RedirectStandardOutput = $false
    $startInfo.RedirectStandardError = $false
    foreach ($key in @($Environment.Keys)) {
        $startInfo.EnvironmentVariables[[string]$key] = [string]$Environment[$key]
    }

    $process = [System.Diagnostics.Process]::new()
    $process.StartInfo = $startInfo
    [void]$process.Start()
    if (-not $process.WaitForExit([Math]::Max(1, $TimeoutSeconds) * 1000)) {
        try {
            $process.Kill()
        }
        catch {
        }
        throw "$FileName timed out."
    }

    return [int]$process.ExitCode
}

function ConvertFrom-TimelineJsonOutput {
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
        throw "TimelineForAudio CLI did not return JSON."
    }
    $jsonText = $jsonText.Substring($startIndex, $endIndex - $startIndex + 1)
    return $jsonText | ConvertFrom-Json
}

function Invoke-TimelineProductCliText {
    param(
        [Parameter(Mandatory = $true)][string]$ProductPath,
        [Parameter(Mandatory = $true)][string]$ProductName,
        [string[]]$CliArgs,
        [int]$TimeoutSeconds = 120,
        [switch]$AllowFailure
    )

    if (-not (Test-Path -LiteralPath $ProductPath)) {
        throw "$ProductName was not found: $ProductPath"
    }

    $cliBatch = Join-Path $ProductPath "cli.bat"
    $cliScript = Join-Path $ProductPath "cli.ps1"
    if (Test-Path -LiteralPath $cliScript) {
        $powershellArgs = @("-NoLogo", "-NoProfile", "-WindowStyle", "Hidden", "-ExecutionPolicy", "Bypass", "-File", $cliScript) + @($CliArgs)
        $result = Invoke-TimelineProcess `
            -FileName (Get-TimelinePowerShellPath) `
            -Arguments $powershellArgs `
            -WorkingDirectory $ProductPath `
            -TimeoutSeconds $TimeoutSeconds `
            -Environment (Get-TimelineChildProcessEnvironment)
    }
    elseif (Test-Path -LiteralPath $cliBatch) {
        $result = Invoke-TimelineProcess `
            -FileName (Join-Path $env:SystemRoot "System32\cmd.exe") `
            -Arguments (@("/d", "/c", $cliBatch) + @($CliArgs)) `
            -WorkingDirectory $ProductPath `
            -TimeoutSeconds $TimeoutSeconds `
            -Environment (Get-TimelineChildProcessEnvironment)
    }
    else {
        throw "$ProductName CLI launcher was not found. Expected cli.bat or cli.ps1 under: $ProductPath"
    }

    $stdout = [string]$result.stdout
    $stderr = [string]$result.stderr
    if ([int]$result.exitCode -ne 0 -and -not $AllowFailure) {
        $message = if ($stderr.Trim()) { $stderr.Trim() } elseif ($stdout.Trim()) { $stdout.Trim() } else { "exit code $([int]$result.exitCode)" }
        throw "$ProductName CLI failed: $message"
    }

    return $stdout
}

function Invoke-TimelineAudioCliJson {
    param(
        [string[]]$CliArgs,
        [int]$TimeoutSeconds = 25
    )

    $stdout = Invoke-TimelineProductCliText `
        -ProductPath $AudioProductPath `
        -ProductName "TimelineForAudio" `
        -CliArgs $CliArgs `
        -TimeoutSeconds $TimeoutSeconds
    return ConvertFrom-TimelineJsonOutput -Text $stdout
}

function Invoke-TimelineWindowsCodexCliJson {
    param(
        [string[]]$CliArgs,
        [int]$TimeoutSeconds = 60,
        [switch]$AllowFailure
    )

    if (-not (Test-Path -LiteralPath $WindowsCodexProductPath)) {
        throw "TimelineForWindowsCodex was not found: $WindowsCodexProductPath"
    }

    $stdout = Invoke-TimelineProductCliText `
        -ProductPath $WindowsCodexProductPath `
        -ProductName "TimelineForWindowsCodex" `
        -CliArgs $CliArgs `
        -TimeoutSeconds $TimeoutSeconds `
        -AllowFailure:$AllowFailure
    return ConvertFrom-TimelineJsonOutput -Text $stdout
}

function Convert-TimelineWindowsPath {
    param([string]$Path)

    $text = ([string]$Path).Trim()
    if (-not $text) {
        return ""
    }

    if ($text.StartsWith("/mnt/c/", [System.StringComparison]::OrdinalIgnoreCase)) {
        return "C:\" + $text.Substring(7).Replace("/", "\")
    }

    $mountMap = @{
        "/input/codex-home" = "C:\Users\amano\.codex"
        "/input/codex-backup" = "C:\Codex\archive\migration-backup-2026-03-27\codex-home"
        "/input/codex-root" = "C:\Codex"
        "/shared/outputs" = (Join-Path $WindowsCodexProductPath "outputs")
    }
    foreach ($key in $mountMap.Keys) {
        if ($text.Equals($key, [System.StringComparison]::OrdinalIgnoreCase)) {
            return [string]$mountMap[$key]
        }
        if ($text.StartsWith("$key/", [System.StringComparison]::OrdinalIgnoreCase)) {
            return ([string]$mountMap[$key]) + "\" + $text.Substring($key.Length + 1).Replace("/", "\")
        }
    }

    return $text
}

function Convert-TimelineAudioLocalPath {
    param([string]$Path)

    $text = ([string]$Path).Trim()
    if (-not $text) {
        return ""
    }
    if ($text.Equals("/workspace", [System.StringComparison]::OrdinalIgnoreCase)) {
        return $AudioProductPath
    }
    if ($text.StartsWith("/workspace/", [System.StringComparison]::OrdinalIgnoreCase)) {
        return (Join-Path $AudioProductPath $text.Substring("/workspace/".Length).Replace("/", "\"))
    }
    if ($text.StartsWith("/mnt/c/", [System.StringComparison]::OrdinalIgnoreCase)) {
        return "C:\" + $text.Substring(7).Replace("/", "\")
    }
    if ([System.IO.Path]::IsPathRooted($text)) {
        return $text
    }
    return Join-Path $AudioProductPath $text
}

function Convert-TimelineDownloadLocalPath {
    param([string]$Path)

    $text = Convert-TimelineText -Value $Path
    if (-not $text) {
        return ""
    }
    if ($text.StartsWith("/workspace/", [System.StringComparison]::OrdinalIgnoreCase) -or
        $text.Equals("/workspace", [System.StringComparison]::OrdinalIgnoreCase)) {
        $audioPath = Convert-TimelineAudioLocalPath -Path $text
        if ($audioPath -and (Test-Path -LiteralPath $audioPath)) {
            return $audioPath
        }
        $chatGptPath = Convert-TimelineChatGptLocalPath -Path $text
        if ($chatGptPath -and (Test-Path -LiteralPath $chatGptPath)) {
            return $chatGptPath
        }
        return $audioPath
    }
    if ($text.StartsWith("/mnt/c/", [System.StringComparison]::OrdinalIgnoreCase)) {
        return "C:\" + $text.Substring(7).Replace("/", "\")
    }
    return Convert-TimelineWindowsPath -Path $text
}

function Get-TimelineAppWorkDirectory {
    $defaultRoot = "C:\TimelineData\Timeline\work"
    $workDirectory = $defaultRoot
    $path = Get-TimelineAppSettingsPath
    if (Test-Path -LiteralPath $path) {
        try {
            $payload = Get-Content -LiteralPath $path -Raw -Encoding UTF8 | ConvertFrom-Json
            $candidate = Convert-TimelineText -Value (Get-PropertyValue -Object $payload -Name "workDirectory" -Default "")
            if ($candidate) {
                $workDirectory = $candidate
            }
        }
        catch {
            $workDirectory = $defaultRoot
        }
    }

    $localPath = Convert-TimelineWindowsPath -Path $workDirectory
    if (-not $localPath) {
        $localPath = $defaultRoot
    }
    if (-not [System.IO.Path]::IsPathRooted($localPath)) {
        $localPath = Join-Path $defaultRoot $localPath
    }
    [System.IO.Directory]::CreateDirectory($localPath) | Out-Null
    return [System.IO.Path]::GetFullPath($localPath)
}

function Get-TimelineLocalDownloadRoot {
    $root = Join-Path (Get-TimelineAppWorkDirectory) "downloads"
    [System.IO.Directory]::CreateDirectory($root) | Out-Null
    return [System.IO.Path]::GetFullPath($root)
}

function Resolve-TimelineManagedDownloadDirectory {
    param(
        [string]$ProductId,
        [string]$RequestedPath
    )

    $downloadRoot = Get-TimelineLocalDownloadRoot
    $candidate = Convert-TimelineText -Value $RequestedPath
    if ($candidate) {
        $localPath = Convert-TimelineWindowsPath -Path $candidate
        if (-not $localPath) {
            $localPath = $candidate
        }
        if (-not [System.IO.Path]::IsPathRooted($localPath)) {
            $localPath = Join-Path $downloadRoot $localPath
        }
    }
    else {
        $localPath = Join-Path $downloadRoot $ProductId
    }

    if (-not (Test-TimelinePathUnderRoot -Path $localPath -Root $downloadRoot)) {
        throw "Download staging path must be under the Timeline work directory."
    }
    [System.IO.Directory]::CreateDirectory($localPath) | Out-Null
    return [System.IO.Path]::GetFullPath($localPath)
}

function Resolve-TimelineManagedDownloadFile {
    param(
        [string]$ProductId,
        [string]$FilePrefix,
        [string]$RequestedPath
    )

    $downloadRoot = Get-TimelineLocalDownloadRoot
    $candidate = Convert-TimelineText -Value $RequestedPath
    if ($candidate) {
        $localPath = Convert-TimelineWindowsPath -Path $candidate
        if (-not $localPath) {
            $localPath = $candidate
        }
        if (-not [System.IO.Path]::IsPathRooted($localPath)) {
            $localPath = Join-Path $downloadRoot $localPath
        }
        if (-not [System.IO.Path]::GetExtension($localPath)) {
            $localPath = "$localPath.zip"
        }
    }
    else {
        $directory = Join-Path $downloadRoot $ProductId
        [System.IO.Directory]::CreateDirectory($directory) | Out-Null
        $stamp = Get-Date -Format "yyyyMMdd-HHmmss"
        $localPath = Join-Path $directory "$FilePrefix-$stamp.zip"
    }

    if (-not (Test-TimelinePathUnderRoot -Path $localPath -Root $downloadRoot)) {
        throw "Download staging path must be under the Timeline work directory."
    }

    $parent = [System.IO.Path]::GetDirectoryName($localPath)
    if ($parent) {
        [System.IO.Directory]::CreateDirectory($parent) | Out-Null
    }
    return [System.IO.Path]::GetFullPath($localPath)
}

function Test-TimelinePathUnderRoot {
    param(
        [string]$Path,
        [string]$Root
    )

    if (-not $Path -or -not $Root) {
        return $false
    }
    try {
        $pathFull = [System.IO.Path]::GetFullPath($Path).TrimEnd([char[]]@('\', '/'))
        $rootFull = [System.IO.Path]::GetFullPath($Root).TrimEnd([char[]]@('\', '/'))
        if ($pathFull.Equals($rootFull, [System.StringComparison]::OrdinalIgnoreCase)) {
            return $true
        }
        $prefix = $rootFull + [System.IO.Path]::DirectorySeparatorChar
        return $pathFull.StartsWith($prefix, [System.StringComparison]::OrdinalIgnoreCase)
    }
    catch {
        return $false
    }
}

function Get-TimelineDownloadRoots {
    return @(
        (Get-TimelineLocalDownloadRoot)
    )
}

function Test-TimelineDownloadFileAllowed {
    param([string]$Path)

    if (-not $Path -or -not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        return $false
    }
    if (-not [System.IO.Path]::GetExtension($Path).Equals(".zip", [System.StringComparison]::OrdinalIgnoreCase)) {
        return $false
    }
    foreach ($root in @(Get-TimelineDownloadRoots)) {
        if ($root -and (Test-TimelinePathUnderRoot -Path $Path -Root $root)) {
            return $true
        }
    }
    return $false
}

function Get-TimelineWindowsCodexSettingsPath {
    $settingsPath = Join-Path $WindowsCodexProductPath "settings.json"
    if (Test-Path -LiteralPath $settingsPath) {
        return $settingsPath
    }
    return Join-Path $WindowsCodexProductPath "settings.example.json"
}

function Read-TimelineWindowsCodexSettingsFile {
    $path = Get-TimelineWindowsCodexSettingsPath
    $payload = Read-TimelineChatGptJsonFile -Path $path
    $fixedSources = @("/input/codex-home", "/input/codex-backup")
    if ($null -eq $payload) {
        return [ordered]@{
            settings_path = $path
            source_roots = @($fixedSources)
            effective_source_roots = @($fixedSources)
            outputRoot = "C:\TimelineData\windows-codex"
            outputs_root = "C:\TimelineData\windows-codex"
            redaction_profile = ""
            include_archived_sources = $null
            include_tool_outputs = $null
            include_compaction_recovery = $null
            using_default_source_roots = $true
        }
    }

    $outputRoot = Convert-TimelineText -Value (Get-PropertyValueAny -Object $payload -Names @("outputRoot", "outputs_root") -Default "C:\TimelineData\windows-codex")
    return [ordered]@{
        settings_path = $path
        source_roots = @($fixedSources)
        effective_source_roots = @($fixedSources)
        outputRoot = $outputRoot
        outputs_root = $outputRoot
        redaction_profile = Convert-TimelineText -Value (Get-PropertyValue -Object $payload -Name "redaction_profile" -Default "")
        include_archived_sources = Get-PropertyValue -Object $payload -Name "include_archived_sources" -Default $null
        include_tool_outputs = Get-PropertyValue -Object $payload -Name "include_tool_outputs" -Default $null
        include_compaction_recovery = Get-PropertyValue -Object $payload -Name "include_compaction_recovery" -Default $null
        using_default_source_roots = $true
    }
}

function Convert-TimelineWindowsCodexSourceRoot {
    param([object]$Source)

    $path = Convert-TimelineText -Value (Get-PropertyValue -Object $Source -Name "path" -Default "")
    if (-not $path) {
        $path = Convert-TimelineText -Value (Get-PropertyValue -Object $Source -Name "source_root" -Default "")
    }
    return [ordered]@{
        path = $path
        displayPath = Convert-TimelineWindowsPath -Path $path
        kind = Convert-TimelineText -Value (Get-PropertyValue -Object $Source -Name "kind" -Default "")
        exists = [bool](Get-PropertyValue -Object $Source -Name "exists" -Default (Test-Path -LiteralPath (Convert-TimelineWindowsPath -Path $path)))
        readable = [bool](Get-PropertyValue -Object $Source -Name "readable" -Default (Test-Path -LiteralPath (Convert-TimelineWindowsPath -Path $path)))
    }
}

function Convert-TimelineWindowsCodexUpdateCounts {
    param([object]$Payload)

    $counts = Get-PropertyValue -Object $Payload -Name "update_counts" -Default @{}
    return [ordered]@{
        new = Convert-TimelineAudioInt -Value (Get-PropertyValue -Object $counts -Name "new" -Default 0)
        changed = Convert-TimelineAudioInt -Value (Get-PropertyValue -Object $counts -Name "changed" -Default 0)
        unchanged = Convert-TimelineAudioInt -Value (Get-PropertyValue -Object $counts -Name "unchanged" -Default 0)
        missing = Convert-TimelineAudioInt -Value (Get-PropertyValue -Object $counts -Name "missing" -Default 0)
        degraded = Convert-TimelineAudioInt -Value (Get-PropertyValue -Object $counts -Name "degraded" -Default 0)
    }
}

function Convert-TimelineWindowsCodexCurrent {
    param(
        [object]$Payload,
        [string]$Message = ""
    )

    if ($null -eq $Payload) {
        return [ordered]@{
            available = $false
            state = ""
            runId = ""
            updatedAt = ""
            runDirectory = ""
            archivePath = ""
            archiveExists = $false
            archiveSizeBytes = 0
            catalogPath = ""
            processingMode = ""
            threadCount = 0
            eventCount = 0
            reusedThreadCount = 0
            renderedThreadCount = 0
            fidelityWarningCount = 0
            updateCounts = Convert-TimelineWindowsCodexUpdateCounts -Payload @{}
            message = $Message
        }
    }

    return [ordered]@{
        available = $true
        state = Convert-TimelineText -Value (Get-PropertyValue -Object $Payload -Name "state" -Default "")
        runId = Convert-TimelineText -Value (Get-PropertyValueAny -Object $Payload -Names @("run_id", "job_id") -Default "")
        updatedAt = Convert-TimelineText -Value (Get-PropertyValue -Object $Payload -Name "updated_at" -Default "")
        runDirectory = Convert-TimelineText -Value (Get-PropertyValue -Object $Payload -Name "run_directory" -Default "")
        archivePath = Convert-TimelineText -Value (Get-PropertyValue -Object $Payload -Name "archive_path" -Default "")
        archiveExists = [bool](Get-PropertyValue -Object $Payload -Name "archive_exists" -Default $false)
        archiveSizeBytes = [int64](Get-PropertyValue -Object $Payload -Name "archive_size_bytes" -Default 0)
        catalogPath = Convert-TimelineText -Value (Get-PropertyValue -Object $Payload -Name "catalog_path" -Default "")
        processingMode = Convert-TimelineText -Value (Get-PropertyValue -Object $Payload -Name "processing_mode" -Default "")
        threadCount = Convert-TimelineAudioInt -Value (Get-PropertyValue -Object $Payload -Name "thread_count" -Default 0)
        eventCount = Convert-TimelineAudioInt -Value (Get-PropertyValue -Object $Payload -Name "event_count" -Default 0)
        reusedThreadCount = Convert-TimelineAudioInt -Value (Get-PropertyValue -Object $Payload -Name "reused_thread_count" -Default 0)
        renderedThreadCount = Convert-TimelineAudioInt -Value (Get-PropertyValue -Object $Payload -Name "rendered_thread_count" -Default 0)
        fidelityWarningCount = Convert-TimelineAudioInt -Value (Get-PropertyValue -Object $Payload -Name "fidelity_warning_count" -Default 0)
        updateCounts = Convert-TimelineWindowsCodexUpdateCounts -Payload $Payload
        message = $Message
    }
}

function Convert-TimelineWindowsCodexJob {
    param([object]$Payload)

    return [ordered]@{
        runId = Convert-TimelineText -Value (Get-PropertyValueAny -Object $Payload -Names @("run_id", "job_id") -Default "")
        state = Convert-TimelineText -Value (Get-PropertyValue -Object $Payload -Name "state" -Default "")
        currentStage = Convert-TimelineText -Value (Get-PropertyValue -Object $Payload -Name "current_stage" -Default "")
        createdAt = Convert-TimelineText -Value (Get-PropertyValue -Object $Payload -Name "created_at" -Default "")
        updatedAt = Convert-TimelineText -Value (Get-PropertyValue -Object $Payload -Name "updated_at" -Default "")
        threadCount = Convert-TimelineAudioInt -Value (Get-PropertyValue -Object $Payload -Name "thread_count" -Default 0)
        threadsDone = Convert-TimelineAudioInt -Value (Get-PropertyValue -Object $Payload -Name "threads_done" -Default 0)
        archivePath = Convert-TimelineText -Value (Get-PropertyValue -Object $Payload -Name "archive_path" -Default "")
    }
}

function Convert-TimelineWindowsCodexSettings {
    param(
        [object]$SettingsPayload,
        [object]$InputsPayload,
        [object]$MasterPayload
    )

    $sourceRows = @()
    $inputRows = @(Get-PropertyValue -Object $InputsPayload -Name "inputs" -Default @())
    if ($inputRows.Count -eq 0) {
        $inputRows = @(Get-PropertyValue -Object $InputsPayload -Name "effective_inputs" -Default @())
    }
    foreach ($source in @($inputRows)) {
        $sourceRows += Convert-TimelineWindowsCodexSourceRoot -Source $source
    }
    if ($sourceRows.Count -eq 0) {
        $fallbackSources = @(Get-PropertyValue -Object $SettingsPayload -Name "source_roots" -Default @())
        if ($fallbackSources.Count -eq 0) {
            $fallbackSources = @(Get-PropertyValue -Object $SettingsPayload -Name "effective_source_roots" -Default @())
        }
        foreach ($sourcePath in @($fallbackSources)) {
            $sourceRows += [ordered]@{
                path = Convert-TimelineText -Value $sourcePath
                displayPath = Convert-TimelineWindowsPath -Path (Convert-TimelineText -Value $sourcePath)
                kind = ""
                exists = [bool](Test-Path -LiteralPath (Convert-TimelineWindowsPath -Path (Convert-TimelineText -Value $sourcePath)))
                readable = [bool](Test-Path -LiteralPath (Convert-TimelineWindowsPath -Path (Convert-TimelineText -Value $sourcePath)))
            }
        }
    }

    $outputsRootPath = Convert-TimelineText -Value (Get-PropertyValueAny -Object $MasterPayload -Names @("master_root", "outputRoot") -Default (Get-PropertyValueAny -Object $SettingsPayload -Names @("outputRoot", "outputs_root") -Default ""))
    $outputsRootDisplayPath = Convert-TimelineWindowsPath -Path $outputsRootPath

    return [ordered]@{
        settingsPath = Convert-TimelineText -Value (Get-PropertyValue -Object $SettingsPayload -Name "settings_path" -Default (Get-PropertyValue -Object $InputsPayload -Name "settings_path" -Default ""))
        sourceRoots = @($sourceRows)
        outputsRoot = $outputsRootPath
        outputsRootDisplayPath = $outputsRootDisplayPath
        outputsRootReady = [bool]($outputsRootDisplayPath -and (Test-Path -LiteralPath $outputsRootDisplayPath))
        redactionProfile = Convert-TimelineText -Value (Get-PropertyValue -Object $SettingsPayload -Name "redaction_profile" -Default "")
        includeArchivedSources = Get-PropertyValue -Object $SettingsPayload -Name "include_archived_sources" -Default $null
        includeToolOutputs = Get-PropertyValue -Object $SettingsPayload -Name "include_tool_outputs" -Default $null
        usingDefaultSourceRoots = [bool](Get-PropertyValue -Object $SettingsPayload -Name "using_default_source_roots" -Default $true)
        issues = @()
    }
}

function Convert-TimelineWindowsCodexItemsCurrent {
    param(
        [object]$ItemsPayload,
        [string]$Message = ""
    )

    $items = @($ItemsPayload)
    return [ordered]@{
        available = ($items.Count -gt 0)
        state = if ($items.Count -gt 0) { "available" } else { "" }
        runId = ""
        updatedAt = ""
        runDirectory = ""
        archivePath = ""
        archiveExists = $false
        archiveSizeBytes = 0
        catalogPath = ""
        processingMode = ""
        threadCount = $items.Count
        eventCount = 0
        reusedThreadCount = 0
        renderedThreadCount = 0
        fidelityWarningCount = 0
        updateCounts = Convert-TimelineWindowsCodexUpdateCounts -Payload @{}
        message = $Message
    }
}

function Get-TimelineThreadRows {
    param([string]$RootPath)

    if (-not $RootPath -or -not (Test-Path -LiteralPath $RootPath)) {
        return @()
    }

    $rows = @()
    foreach ($dir in @(Get-ChildItem -LiteralPath $RootPath -Directory -ErrorAction SilentlyContinue | Select-Object -First 500)) {
        $timelinePath = Join-Path $dir.FullName "timeline.json"
        if (-not (Test-Path -LiteralPath $timelinePath)) {
            continue
        }

        $timeline = Read-TimelineChatGptJsonFile -Path $timelinePath
        $messages = @(Get-PropertyValue -Object $timeline -Name "messages" -Default @())
        $itemId = Convert-TimelineText -Value (Get-PropertyValueAny -Object $timeline -Names @("thread_id", "conversation_id", "item_id", "id") -Default $dir.Name)
        $title = Convert-TimelineText -Value (Get-PropertyValue -Object $timeline -Name "title" -Default "")
        if (-not $title) {
            $title = $dir.Name
        }

        $rows += [pscustomobject]@{
            SortDate = (Get-Item -LiteralPath $timelinePath).LastWriteTimeUtc
            Payload = [ordered]@{
                itemId = $itemId
                title = $title
                createdAt = Convert-TimelineText -Value (Get-PropertyValue -Object $timeline -Name "created_at" -Default "")
                updatedAt = Convert-TimelineText -Value (Get-PropertyValue -Object $timeline -Name "updated_at" -Default "")
                messageCount = $messages.Count
                directoryPath = $dir.FullName
                timelinePath = $timelinePath
                convertInfoPath = Join-Path $dir.FullName "convert_info.json"
            }
        }
    }

    return @($rows | Sort-Object SortDate -Descending | ForEach-Object { $_.Payload })
}

function Convert-TimelineThreadItemRow {
    param(
        [object]$Item,
        [string]$RootPath = ""
    )

    $itemId = Convert-TimelineText -Value (Get-PropertyValueAny -Object $Item -Names @("item_id", "itemId", "thread_id", "threadId", "conversation_id", "conversationId") -Default "")
    $title = Convert-TimelineText -Value (Get-PropertyValueAny -Object $Item -Names @("title", "preferred_title", "preferredTitle", "name") -Default "")
    if (-not $title) {
        $title = Convert-TimelineText -Value (Get-PropertyValue -Object $Item -Name "first_user_message_excerpt" -Default "")
    }
    if (-not $title) {
        $title = $itemId
    }

    $directoryPath = Convert-TimelineText -Value (Get-PropertyValueAny -Object $Item -Names @("directoryPath", "directory_path", "item_dir", "itemDir") -Default "")
    if (-not $directoryPath -and $RootPath -and $itemId) {
        $directoryPath = Join-Path $RootPath $itemId
    }

    return [ordered]@{
        itemId = $itemId
        title = $title
        createdAt = Convert-TimelineText -Value (Get-PropertyValueAny -Object $Item -Names @("created_at", "createdAt", "started_at_utc", "startedAtUtc") -Default "")
        updatedAt = Convert-TimelineText -Value (Get-PropertyValueAny -Object $Item -Names @("updated_at", "updatedAt", "ended_at_utc", "endedAtUtc") -Default "")
        messageCount = Convert-TimelineAudioInt -Value (Get-PropertyValueAny -Object $Item -Names @("message_count", "messageCount", "event_count", "eventCount") -Default 0)
        directoryPath = $directoryPath
        timelinePath = if ($directoryPath) { Join-Path $directoryPath "timeline.json" } else { "" }
        convertInfoPath = if ($directoryPath) { Join-Path $directoryPath "convert_info.json" } else { "" }
    }
}

function New-TimelineThreadListResult {
    param(
        [object[]]$Threads,
        [object]$Pagination,
        [int]$Total
    )

    return [ordered]@{
        total = $Total
        pagination = $Pagination
        threads = @($Threads)
    }
}

function Get-TimelineThreadRowsPageFromRoot {
    param(
        [string]$RootPath,
        [int]$Page = 1,
        [int]$PageSize = 100
    )

    $allRows = @(Get-TimelineThreadRows -RootPath $RootPath)
    $total = $allRows.Count
    $effectivePage = [Math]::Max(1, $Page)
    $effectivePageSize = [Math]::Max(1, $PageSize)
    $offset = ($effectivePage - 1) * $effectivePageSize
    $pageRows = @($allRows | Select-Object -Skip $offset -First $effectivePageSize)
    return New-TimelineThreadListResult `
        -Threads $pageRows `
        -Pagination (New-TimelinePagination -Page $effectivePage -PageSize $effectivePageSize -TotalItems $total -ReturnedItems $pageRows.Count) `
        -Total $total
}

function Get-TimelineThreadDirectoryCount {
    param([string]$RootPath)

    if (-not $RootPath -or -not (Test-Path -LiteralPath $RootPath)) {
        return 0
    }

    $count = 0
    foreach ($dir in @(Get-ChildItem -LiteralPath $RootPath -Directory -ErrorAction SilentlyContinue)) {
        if (Test-Path -LiteralPath (Join-Path $dir.FullName "timeline.json")) {
            $count += 1
        }
    }
    return $count
}

function Get-TimelineManifestItemCount {
    param([string]$RootPath)

    if (-not $RootPath -or -not (Test-Path -LiteralPath $RootPath)) {
        return 0
    }

    $manifestPath = Join-Path $RootPath "manifest.json"
    $manifest = Read-TimelineChatGptJsonFile -Path $manifestPath
    if ($null -eq $manifest) {
        return Get-TimelineThreadDirectoryCount -RootPath $RootPath
    }

    $itemCount = Convert-TimelineAudioInt -Value (Get-PropertyValue -Object $manifest -Name "item_count" -Default 0)
    if ($itemCount -gt 0) {
        return $itemCount
    }
    return @(Get-PropertyValue -Object $manifest -Name "items" -Default @()).Count
}

function Get-TimelineSafeChildDirectory {
    param(
        [string]$RootPath,
        [string]$ChildName
    )

    if (-not $RootPath -or -not $ChildName) {
        return ""
    }

    $rootFull = [System.IO.Path]::GetFullPath($RootPath).TrimEnd([char[]]@('\', '/'))
    $candidate = Join-Path $rootFull $ChildName
    $candidateFull = [System.IO.Path]::GetFullPath($candidate).TrimEnd([char[]]@('\', '/'))
    $prefix = $rootFull + [System.IO.Path]::DirectorySeparatorChar
    if (-not $candidateFull.StartsWith($prefix, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Invalid thread id."
    }

    return $candidateFull
}

function Get-TimelineRequestItemIds {
    param([object]$Request)

    $itemIds = @()
    foreach ($itemId in @(Get-PropertyValue -Object $Request -Name "itemIds" -Default @())) {
        $text = Convert-TimelineText -Value $itemId
        if ($text) {
            $itemIds += $text
        }
    }
    return @($itemIds | Select-Object -Unique)
}

function Remove-TimelineThreadItems {
    param(
        [string]$RootPath,
        [string[]]$ItemIds
    )

    if (-not $RootPath -or -not (Test-Path -LiteralPath $RootPath)) {
        throw "Output directory is not configured."
    }
    if (@($ItemIds).Count -eq 0) {
        throw "No items were selected."
    }

    $deleted = 0
    $missing = @()
    foreach ($itemId in @($ItemIds)) {
        $itemRoot = Get-TimelineSafeChildDirectory -RootPath $RootPath -ChildName $itemId
        if (Test-Path -LiteralPath $itemRoot) {
            Remove-Item -LiteralPath $itemRoot -Recurse -Force
            $deleted += 1
        }
        else {
            $missing += $itemId
        }
    }

    return [ordered]@{
        itemIds = @($ItemIds)
        deletedCount = $deleted
        missingItemIds = @($missing)
    }
}

function Convert-TimelineThreadMessage {
    param(
        [object]$Message,
        [int]$Index
    )

    return [ordered]@{
        index = $Index
        role = Convert-TimelineText -Value (Get-PropertyValue -Object $Message -Name "role" -Default "")
        createdAt = Convert-TimelineText -Value (Get-PropertyValue -Object $Message -Name "created_at" -Default "")
        text = Convert-TimelineText -Value (Get-PropertyValue -Object $Message -Name "text" -Default "")
    }
}

function Get-TimelineWindowsCodexThreadDetail {
    param([string]$ItemId)

    $settingsPayload = Read-TimelineWindowsCodexSettingsFile
    $outputRoot = Convert-TimelineText -Value (Get-PropertyValueAny -Object $settingsPayload -Names @("outputRoot", "outputs_root") -Default "")
    $masterLocalPath = Convert-TimelineWindowsPath -Path $outputRoot
    if (-not $masterLocalPath -or -not (Test-Path -LiteralPath $masterLocalPath)) {
        return [ordered]@{
            available = $false
            itemId = $ItemId
            title = ""
            createdAt = ""
            updatedAt = ""
            messageCount = 0
            messages = @()
            directoryPath = ""
            timelinePath = ""
            convertInfoPath = ""
            message = "Output directory is not configured."
        }
    }

    $threadDirectory = Get-TimelineSafeChildDirectory -RootPath $masterLocalPath -ChildName $ItemId
    $timelinePath = Join-Path $threadDirectory "timeline.json"
    if (-not (Test-Path -LiteralPath $timelinePath)) {
        return [ordered]@{
            available = $false
            itemId = $ItemId
            title = ""
            createdAt = ""
            updatedAt = ""
            messageCount = 0
            messages = @()
            directoryPath = $threadDirectory
            timelinePath = $timelinePath
            convertInfoPath = Join-Path $threadDirectory "convert_info.json"
            message = "Thread was not found."
        }
    }

    $timeline = Read-TimelineChatGptJsonFile -Path $timelinePath
    if ($null -eq $timeline) {
        return [ordered]@{
            available = $false
            itemId = $ItemId
            title = $ItemId
            createdAt = ""
            updatedAt = ""
            messageCount = 0
            messages = @()
            directoryPath = $threadDirectory
            timelinePath = $timelinePath
            convertInfoPath = Join-Path $threadDirectory "convert_info.json"
            message = "Thread could not be read."
        }
    }

    $messages = @()
    $index = 0
    foreach ($message in @(Get-PropertyValue -Object $timeline -Name "messages" -Default @())) {
        $messages += Convert-TimelineThreadMessage -Message $message -Index $index
        $index += 1
    }

    $resolvedItemId = Convert-TimelineText -Value (Get-PropertyValueAny -Object $timeline -Names @("thread_id", "conversation_id", "item_id", "id") -Default $ItemId)
    $title = Convert-TimelineText -Value (Get-PropertyValue -Object $timeline -Name "title" -Default "")
    if (-not $title) {
        $title = $resolvedItemId
    }

    return [ordered]@{
        available = $true
        itemId = $resolvedItemId
        title = $title
        createdAt = Convert-TimelineText -Value (Get-PropertyValue -Object $timeline -Name "created_at" -Default "")
        updatedAt = Convert-TimelineText -Value (Get-PropertyValue -Object $timeline -Name "updated_at" -Default "")
        messageCount = $messages.Count
        messages = @($messages)
        directoryPath = $threadDirectory
        timelinePath = $timelinePath
        convertInfoPath = Join-Path $threadDirectory "convert_info.json"
        message = ""
    }
}

function Get-TimelineChatGptThreadDetail {
    param([string]$ItemId)

    $settings = Read-TimelineChatGptSettings
    $outputRoot = Get-PropertyValue -Object $settings -Name "outputRoot" -Default @{}
    $masterLocalPath = Convert-TimelineChatGptLocalPath -Path (Convert-TimelineText -Value (Get-PropertyValue -Object $outputRoot -Name "path" -Default ""))
    if (-not $masterLocalPath -or -not (Test-Path -LiteralPath $masterLocalPath)) {
        return [ordered]@{
            available = $false
            itemId = $ItemId
            title = ""
            createdAt = ""
            updatedAt = ""
            messageCount = 0
            messages = @()
            directoryPath = ""
            timelinePath = ""
            convertInfoPath = ""
            message = "Output directory is not configured."
        }
    }

    $threadDirectory = Get-TimelineSafeChildDirectory -RootPath $masterLocalPath -ChildName $ItemId
    $timelinePath = Join-Path $threadDirectory "timeline.json"
    if (-not (Test-Path -LiteralPath $timelinePath)) {
        return [ordered]@{
            available = $false
            itemId = $ItemId
            title = ""
            createdAt = ""
            updatedAt = ""
            messageCount = 0
            messages = @()
            directoryPath = $threadDirectory
            timelinePath = $timelinePath
            convertInfoPath = Join-Path $threadDirectory "convert_info.json"
            message = "Thread was not found."
        }
    }

    $timeline = Read-TimelineChatGptJsonFile -Path $timelinePath
    if ($null -eq $timeline) {
        return [ordered]@{
            available = $false
            itemId = $ItemId
            title = $ItemId
            createdAt = ""
            updatedAt = ""
            messageCount = 0
            messages = @()
            directoryPath = $threadDirectory
            timelinePath = $timelinePath
            convertInfoPath = Join-Path $threadDirectory "convert_info.json"
            message = "Thread could not be read."
        }
    }

    $messages = @()
    $index = 0
    foreach ($message in @(Get-PropertyValue -Object $timeline -Name "messages" -Default @())) {
        $messages += Convert-TimelineThreadMessage -Message $message -Index $index
        $index += 1
    }

    $resolvedItemId = Convert-TimelineText -Value (Get-PropertyValueAny -Object $timeline -Names @("conversation_id", "thread_id", "item_id", "id") -Default $ItemId)
    $title = Convert-TimelineText -Value (Get-PropertyValue -Object $timeline -Name "title" -Default "")
    if (-not $title) {
        $title = $resolvedItemId
    }

    return [ordered]@{
        available = $true
        itemId = $resolvedItemId
        title = $title
        createdAt = Convert-TimelineText -Value (Get-PropertyValue -Object $timeline -Name "created_at" -Default "")
        updatedAt = Convert-TimelineText -Value (Get-PropertyValue -Object $timeline -Name "updated_at" -Default "")
        messageCount = $messages.Count
        messages = @($messages)
        directoryPath = $threadDirectory
        timelinePath = $timelinePath
        convertInfoPath = Join-Path $threadDirectory "convert_info.json"
        message = ""
    }
}

function Get-TimelineWindowsCodexThreads {
    param(
        [int]$Page = 1,
        [int]$PageSize = 100
    )

    $settingsPayload = Read-TimelineWindowsCodexSettingsFile
    $outputRoot = Convert-TimelineText -Value (Get-PropertyValueAny -Object $settingsPayload -Names @("outputRoot", "outputs_root") -Default "")
    $masterLocalPath = Convert-TimelineWindowsPath -Path $outputRoot
    return Get-TimelineThreadRowsPageFromRoot -RootPath $masterLocalPath -Page $Page -PageSize $PageSize
}

function Get-TimelineWindowsCodexOverview {
    $productFound = Test-Path -LiteralPath $WindowsCodexProductPath
    $messages = @()
    $settingsPayload = Read-TimelineWindowsCodexSettingsFile
    if (-not $productFound) {
        $messages += "TimelineForWindowsCodex was not found."
    }

    $inputsPayload = [ordered]@{
        settings_path = $settingsPayload.settings_path
        configured = -not [bool]$settingsPayload.using_default_source_roots
        inputs = @(
            @($settingsPayload.source_roots) | ForEach-Object {
                [ordered]@{ input_id = ""; path = $_; kind = "" }
            }
        )
        effective_inputs = @(
            @($settingsPayload.effective_source_roots) | ForEach-Object {
                [ordered]@{ input_id = ""; path = $_; kind = "" }
            }
        )
    }
    $masterPayload = [ordered]@{
        settings_path = $settingsPayload.settings_path
        master_root = $settingsPayload.outputs_root
        configured = [bool]$settingsPayload.outputs_root
    }
    $settings = Convert-TimelineWindowsCodexSettings -SettingsPayload $settingsPayload -InputsPayload $inputsPayload -MasterPayload $masterPayload
    $sourceReady = @($settings.sourceRoots | Where-Object { [bool]$_.exists -and [bool]$_.readable }).Count -gt 0
    $masterReady = [bool]$settings.outputsRoot
    $masterLocalPath = Convert-TimelineWindowsPath -Path ([string]$settings.outputsRoot)
    $threadCount = Get-TimelineThreadDirectoryCount -RootPath $masterLocalPath
    $currentItems = @()
    for ($index = 0; $index -lt $threadCount; $index += 1) {
        $currentItems += $index
    }
    $currentPayload = Convert-TimelineWindowsCodexItemsCurrent -ItemsPayload $currentItems

    return [ordered]@{
        productFound = $productFound
        productPath = $WindowsCodexProductPath
        settingsValid = [bool]($productFound -and $sourceReady -and $masterReady)
        settings = $settings
        current = if ($null -ne $currentPayload) { $currentPayload } else { Convert-TimelineWindowsCodexItemsCurrent -ItemsPayload @() }
        threads = @()
        jobs = @()
        message = (($messages | Where-Object { $_ }) -join " ")
    }
}

function Write-TimelineWindowsCodexSettings {
    param([object]$Request)

    $outputsRoot = Convert-TimelineText -Value (Get-PropertyValueAny -Object $Request -Names @("outputsRoot", "outputRoot") -Default "")
    if (-not $outputsRoot) {
        $outputsRoot = "C:\TimelineData\windows-codex"
    }

    $payload = [ordered]@{
        schemaVersion = 1
        outputRoot = $outputsRoot
    }
    $settingsPath = Join-Path $WindowsCodexProductPath "settings.json"
    Write-TimelineUtf8JsonFile -Path $settingsPath -Payload $payload

    return Get-TimelineWindowsCodexOverview
}

function Start-TimelineWindowsCodexRefresh {
    $payload = Invoke-TimelineWindowsCodexCliJson -CliArgs @("items", "refresh", "--format", "json") -TimeoutSeconds 900
    return Convert-TimelineWindowsCodexCurrent -Payload @{
        state = Get-PropertyValue -Object $payload -Name "state" -Default ""
        job_id = Get-PropertyValueAny -Object $payload -Names @("refresh_id", "run_id") -Default ""
        updated_at = Get-PropertyValue -Object $payload -Name "completed_at" -Default ""
        run_directory = Get-PropertyValue -Object $payload -Name "run_directory" -Default ""
        archive_path = ""
        archive_exists = $false
        archive_size_bytes = 0
        catalog_path = ""
        processing_mode = Get-PropertyValue -Object $payload -Name "processing_mode" -Default ""
        thread_count = Get-PropertyValue -Object $payload -Name "thread_count" -Default 0
        event_count = Get-PropertyValue -Object $payload -Name "message_count" -Default 0
        reused_thread_count = Get-PropertyValue -Object $payload -Name "reused_thread_count" -Default 0
        rendered_thread_count = Get-PropertyValue -Object $payload -Name "rendered_thread_count" -Default 0
        fidelity_warning_count = Get-PropertyValue -Object $payload -Name "fidelity_warning_count" -Default 0
        update_counts = Get-PropertyValue -Object $payload -Name "update_counts" -Default @{}
    }
}

function Start-TimelineWindowsCodexDownload {
    param([object]$Request)

    $itemIds = @(Get-TimelineRequestItemIds -Request $Request)

    $requestedOutputPath = Convert-TimelineText -Value (Get-PropertyValue -Object $Request -Name "outputPath" -Default "")
    $hostOutputPath = Resolve-TimelineManagedDownloadDirectory `
        -ProductId "windows-codex" `
        -RequestedPath $requestedOutputPath

    $args = @("items", "download", "--to", $hostOutputPath, "--overwrite", "--format", "json")
    foreach ($itemId in $itemIds) {
        $args += @("--item-id", $itemId)
    }

    $payload = Invoke-TimelineWindowsCodexCliJson -CliArgs $args -TimeoutSeconds 900
    $archivePath = Convert-TimelineDownloadLocalPath -Path (Convert-TimelineText -Value (Get-PropertyValueAny -Object $payload -Names @("destination_path", "destinationPath", "archive_path", "archivePath", "download_path", "downloadPath") -Default ""))
    if (-not $archivePath -or -not (Test-TimelineDownloadFileAllowed -Path $archivePath)) {
        throw "TimelineForWindowsCodex CLI did not create a downloadable ZIP in the Timeline work directory."
    }

    return [ordered]@{
        archivePath = [string]$archivePath
        itemIds = @($itemIds)
    }
}

function Remove-TimelineWindowsCodexItems {
    param([object]$Request)

    $settingsPayload = Read-TimelineWindowsCodexSettingsFile
    $outputRoot = Convert-TimelineText -Value (Get-PropertyValueAny -Object $settingsPayload -Names @("outputRoot", "outputs_root") -Default "")
    $masterLocalPath = Convert-TimelineWindowsPath -Path $outputRoot
    return Remove-TimelineThreadItems -RootPath $masterLocalPath -ItemIds @(Get-TimelineRequestItemIds -Request $Request)
}

function Get-TimelineRuntimeProductDefinitions {
    return @(
        [ordered]@{
            id = "audio"
            displayName = "TimelineForAudio"
            description = "audio"
            pagePath = "audio/files"
            settingsPath = "audio/settings"
            productPath = $AudioProductPath
            cliPath = (Join-Path $AudioProductPath "cli.ps1")
            startPath = (Join-Path $AudioProductPath "start.ps1")
            stopPath = (Join-Path $AudioProductPath "stop.ps1")
        },
        [ordered]@{
            id = "windows-codex"
            displayName = "TimelineForWindowsCodex"
            description = "codex"
            pagePath = "windows-codex"
            settingsPath = "windows-codex/settings"
            productPath = $WindowsCodexProductPath
            cliPath = (Join-Path $WindowsCodexProductPath "cli.ps1")
            startPath = (Join-Path $WindowsCodexProductPath "start.ps1")
            stopPath = (Join-Path $WindowsCodexProductPath "stop.ps1")
        },
        [ordered]@{
            id = "chatgpt"
            displayName = "TimelineForChatGPT"
            description = "chatgpt"
            pagePath = "chatgpt"
            settingsPath = "chatgpt/settings"
            productPath = $ChatGptProductPath
            cliPath = (Join-Path $ChatGptProductPath "cli.ps1")
            startPath = (Join-Path $ChatGptProductPath "start.ps1")
            stopPath = (Join-Path $ChatGptProductPath "stop.ps1")
        }
    )
}

function Get-TimelineRuntimeProductDefinition {
    param([string]$ProductId)

    foreach ($definition in @(Get-TimelineRuntimeProductDefinitions)) {
        if (([string]$definition.id).Equals($ProductId, [System.StringComparison]::OrdinalIgnoreCase)) {
            return $definition
        }
    }
    throw "Unknown product: $ProductId"
}

function Convert-TimelineRuntimeStatus {
    param([object]$Definition)

    $productPath = [string]$Definition.productPath
    $cliPath = [string]$Definition.cliPath
    $productFound = Test-Path -LiteralPath $productPath
    $cliFound = Test-Path -LiteralPath $cliPath
    $message = ""

    $state = "not-created"
    $running = $false
    $status = ""
    if ($productFound -and $cliFound) {
        $state = "ready"
        $status = "ready"
    }
    elseif (-not $productFound) {
        $message = "Product directory was not found."
    }
    else {
        $message = "CLI launcher was not found."
    }

    return [ordered]@{
        id = [string]$Definition.id
        displayName = [string]$Definition.displayName
        description = [string]$Definition.description
        pagePath = [string]$Definition.pagePath
        settingsPath = [string]$Definition.settingsPath
        productPath = $productPath
        productFound = $productFound
        composeFound = $cliFound
        containerName = if ($cliFound) { Split-Path -Leaf $cliPath } else { "" }
        state = $state
        status = $status
        running = $running
        startedAt = ""
        exitCode = 0
        message = $message
    }
}

function Get-TimelineRuntimeOverview {
    $rows = @()
    foreach ($definition in @(Get-TimelineRuntimeProductDefinitions)) {
        $rows += Convert-TimelineRuntimeStatus -Definition $definition
    }
    return [ordered]@{
        products = @($rows)
        message = ""
    }
}

function Test-TimelineProductStartOutputSuccess {
    param([string]$Text)

    $value = [string]$Text
    return $value -match "is running" -or
        $value -match "was started" -or
        $value -match "worker is running" -or
        $value -match "worker-1 was started"
}

function Invoke-TimelineProductStart {
    param(
        [string]$ProductId,
        [switch]$Restart
    )

    $definition = Get-TimelineRuntimeProductDefinition -ProductId $ProductId
    $productPath = [string]$definition.productPath
    if (-not (Test-Path -LiteralPath $productPath)) {
        throw "Product directory was not found: $productPath"
    }

    $powershell = Get-TimelinePowerShellPath
    if ($Restart -and (Test-Path -LiteralPath ([string]$definition.stopPath))) {
        $stopScript = [string]$definition.stopPath
        [void](Invoke-TimelineProcess `
            -FileName $powershell `
            -Arguments @("-NoLogo", "-NoProfile", "-WindowStyle", "Hidden", "-ExecutionPolicy", "Bypass", "-File", $stopScript) `
            -WorkingDirectory $productPath `
            -TimeoutSeconds 180 `
            -Environment (Get-TimelineChildProcessEnvironment))
    }

    if (Test-Path -LiteralPath ([string]$definition.startPath)) {
        $startScript = [string]$definition.startPath
        $result = Invoke-TimelineProcess `
            -FileName $powershell `
            -Arguments @("-NoLogo", "-NoProfile", "-WindowStyle", "Hidden", "-ExecutionPolicy", "Bypass", "-File", $startScript) `
            -WorkingDirectory $productPath `
            -TimeoutSeconds 240 `
            -Environment (Get-TimelineChildProcessEnvironment)
        if ([int]$result.exitCode -ne 0) {
            $combinedOutput = "$([string]$result.stdout)`n$([string]$result.stderr)"
            if (-not (Test-TimelineProductStartOutputSuccess -Text $combinedOutput)) {
                $message = if (([string]$result.stderr).Trim()) { ([string]$result.stderr).Trim() } elseif (([string]$result.stdout).Trim()) { ([string]$result.stdout).Trim() } else { "exit code $([int]$result.exitCode)" }
                throw "$($definition.displayName) start failed: $message"
            }
        }
        return Convert-TimelineRuntimeStatus -Definition $definition
    }

    return Convert-TimelineRuntimeStatus -Definition $definition
}

function Invoke-TimelineChatGptCliText {
    param(
        [string[]]$CliArgs,
        [int]$TimeoutSeconds = 120
    )

    return Invoke-TimelineProductCliText `
        -ProductPath $ChatGptProductPath `
        -ProductName "TimelineForChatGPT" `
        -CliArgs $CliArgs `
        -TimeoutSeconds $TimeoutSeconds
}

function Invoke-TimelineChatGptCliJson {
    param(
        [string[]]$CliArgs,
        [int]$TimeoutSeconds = 120
    )

    $stdout = Invoke-TimelineChatGptCliText -CliArgs $CliArgs -TimeoutSeconds $TimeoutSeconds
    return ConvertFrom-TimelineJsonOutput -Text $stdout
}

function Convert-TimelineChatGptLocalPath {
    param([string]$Path)

    $text = ([string]$Path).Trim()
    if (-not $text) {
        return ""
    }
    if ($text.Equals("/workspace", [System.StringComparison]::OrdinalIgnoreCase)) {
        return $ChatGptProductPath
    }
    if ($text.StartsWith("/workspace/", [System.StringComparison]::OrdinalIgnoreCase)) {
        return (Join-Path $ChatGptProductPath $text.Substring("/workspace/".Length).Replace("/", "\"))
    }
    if ($text.StartsWith("/mnt/c/", [System.StringComparison]::OrdinalIgnoreCase)) {
        return "C:\" + $text.Substring(7).Replace("/", "\")
    }
    if ([System.IO.Path]::IsPathRooted($text)) {
        return $text
    }
    return Join-Path $ChatGptProductPath $text
}

function Read-TimelineChatGptJsonFile {
    param([string]$Path)

    if (-not $Path -or -not (Test-Path -LiteralPath $Path)) {
        return $null
    }
    try {
        return Get-Content -LiteralPath $Path -Raw -Encoding UTF8 | ConvertFrom-Json
    }
    catch {
        return $null
    }
}

function Get-TimelineChatGptSettingsPath {
    $settingsPath = Join-Path $ChatGptProductPath "settings.json"
    if (Test-Path -LiteralPath $settingsPath) {
        return $settingsPath
    }
    return Join-Path $ChatGptProductPath "settings.example.json"
}

function Read-TimelineChatGptSettings {
    $path = Get-TimelineChatGptSettingsPath
    $payload = Read-TimelineChatGptJsonFile -Path $path
    if ($null -eq $payload) {
        $defaultOutput = "C:\TimelineData\chatgpt"
        return [ordered]@{
            path = $path
            settingsFound = $false
            inputRoots = @()
            masterRoot = [ordered]@{ id = "output"; displayName = "Output"; path = $defaultOutput }
            outputRoot = [ordered]@{ id = "output"; displayName = "Output"; path = $defaultOutput }
            stateRoot = [ordered]@{ id = "runtime"; displayName = "Runtime"; path = "" }
            allowedExtensions = @(".zip")
            recursive = $false
            profile = ""
        }
    }

    $outputPath = Convert-TimelineText -Value (Get-PropertyValue -Object $payload -Name "outputRoot" -Default "C:\TimelineData\chatgpt")
    $outputRoot = [ordered]@{ id = "output"; displayName = "Output"; path = $outputPath }

    return [ordered]@{
        path = $path
        settingsFound = $true
        inputRoots = @()
        masterRoot = $outputRoot
        outputRoot = $outputRoot
        stateRoot = [ordered]@{ id = "runtime"; displayName = "Runtime"; path = "" }
        allowedExtensions = @(".zip")
        recursive = $false
        profile = ""
    }
}

function Get-TimelineChatGptConfiguredOutputRoot {
    $settings = Read-TimelineChatGptSettings
    $outputRoot = Get-PropertyValue -Object $settings -Name "outputRoot" -Default @{}
    $path = Convert-TimelineText -Value (Get-PropertyValue -Object $outputRoot -Name "path" -Default "")
    if (-not $path) {
        return ""
    }
    if (-not [System.IO.Path]::IsPathRooted($path)) {
        $path = Join-Path $ChatGptProductPath $path
    }
    return [System.IO.Path]::GetFullPath($path)
}

function Convert-TimelineChatGptConfigPath {
    param(
        [string]$Path,
        [switch]$RequireProductLocal
    )

    $text = ([string]$Path).Trim()
    if (-not $text) {
        return ""
    }

    if ($text.Equals("/workspace", [System.StringComparison]::OrdinalIgnoreCase)) {
        return "."
    }
    if ($text.StartsWith("/workspace/", [System.StringComparison]::OrdinalIgnoreCase)) {
        return $text.Substring("/workspace/".Length).Replace("\", "/")
    }

    if ([System.IO.Path]::IsPathRooted($text)) {
        $productRoot = [System.IO.Path]::GetFullPath($ChatGptProductPath).TrimEnd([char[]]@('\', '/'))
        $fullPath = [System.IO.Path]::GetFullPath($text).TrimEnd([char[]]@('\', '/'))
        if ($fullPath.Equals($productRoot, [System.StringComparison]::OrdinalIgnoreCase)) {
            return "."
        }

        $prefix = $productRoot + [System.IO.Path]::DirectorySeparatorChar
        if ($fullPath.StartsWith($prefix, [System.StringComparison]::OrdinalIgnoreCase)) {
            return $fullPath.Substring($prefix.Length).Replace("\", "/")
        }

        if ($RequireProductLocal) {
            throw "Select a directory under TimelineForChatGPT."
        }
    }

    return $text.Replace("\", "/")
}

function Write-TimelineUtf8JsonFile {
    param(
        [string]$Path,
        [object]$Payload
    )

    $json = ConvertTo-Json -InputObject $Payload -Depth 20
    $encoding = [System.Text.UTF8Encoding]::new($false)
    [System.IO.File]::WriteAllText($Path, $json, $encoding)
}

function Write-TimelineChatGptSettings {
    param([object]$Request)

    if (-not (Test-Path -LiteralPath $ChatGptProductPath)) {
        throw "TimelineForChatGPT was not found: $ChatGptProductPath"
    }

    $outputRootRequest = Get-PropertyValueAny -Object $Request -Names @("outputRoot", "masterRoot") -Default @{}
    $outputPath = Convert-TimelineText -Value (Get-PropertyValue -Object $outputRootRequest -Name "path" -Default "")
    if (-not $outputPath) {
        $outputPath = Convert-TimelineText -Value (Get-PropertyValueAny -Object $Request -Names @("outputRootPath", "masterRootPath") -Default "")
    }
    if (-not $outputPath) {
        throw "Output directory is required."
    }

    $payload = [ordered]@{
        outputRoot = $outputPath
    }

    $localPath = Convert-TimelineChatGptLocalPath -Path $outputPath
    if ($localPath) {
        [System.IO.Directory]::CreateDirectory($localPath) | Out-Null
    }

    $settingsPath = Join-Path $ChatGptProductPath "settings.json"
    Write-TimelineUtf8JsonFile -Path $settingsPath -Payload $payload
    return Get-TimelineChatGptOverview
}

function Convert-TimelineChatGptRoot {
    param(
        [object]$Root,
        [string[]]$AllowedExtensions
    )

    $path = Convert-TimelineText -Value (Get-PropertyValue -Object $Root -Name "path" -Default "")
    $localPath = Convert-TimelineChatGptLocalPath -Path $path
    $enabled = [bool](Get-PropertyValue -Object $Root -Name "enabled" -Default $true)
    $fileCount = 0
    $sizeBytes = [int64]0
    if ($enabled -and $localPath -and (Test-Path -LiteralPath $localPath)) {
        $extensions = @($AllowedExtensions | ForEach-Object {
            $text = ([string]$_).Trim().ToLowerInvariant()
            if ($text.StartsWith(".")) { $text } else { ".$text" }
        })
        foreach ($file in @(Get-ChildItem -LiteralPath $localPath -File -ErrorAction SilentlyContinue)) {
            if ($extensions -contains $file.Extension.ToLowerInvariant()) {
                $fileCount += 1
                $sizeBytes += [int64]$file.Length
            }
        }
    }

    return [ordered]@{
        id = Convert-TimelineText -Value (Get-PropertyValue -Object $Root -Name "id" -Default "")
        displayName = Convert-TimelineText -Value (Get-PropertyValue -Object $Root -Name "displayName" -Default "")
        path = $path
        displayPath = $localPath
        enabled = $enabled
        exists = [bool]($localPath -and (Test-Path -LiteralPath $localPath))
        fileCount = $fileCount
        sizeBytes = $sizeBytes
    }
}

function Convert-TimelineChatGptDirectoryRoot {
    param(
        [object]$Root,
        [string]$FallbackId,
        [string]$FallbackDisplayName
    )

    $path = Convert-TimelineText -Value (Get-PropertyValue -Object $Root -Name "path" -Default "")
    $localPath = Convert-TimelineChatGptLocalPath -Path $path
    return [ordered]@{
        id = Convert-TimelineText -Value (Get-PropertyValue -Object $Root -Name "id" -Default $FallbackId)
        displayName = Convert-TimelineText -Value (Get-PropertyValue -Object $Root -Name "displayName" -Default $FallbackDisplayName)
        path = $path
        displayPath = $localPath
        exists = [bool]($localPath -and (Test-Path -LiteralPath $localPath))
    }
}

function Get-TimelineChatGptLatestRefreshReport {
    param([string]$OutputRootPath)

    if (-not $OutputRootPath -or -not (Test-Path -LiteralPath $OutputRootPath)) {
        return $null
    }
    $report = Get-ChildItem -LiteralPath $OutputRootPath -File -Filter "refresh-*.json" -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTimeUtc -Descending |
        Select-Object -First 1
    if ($null -eq $report) {
        return $null
    }
    return Read-TimelineChatGptJsonFile -Path $report.FullName
}

function Convert-TimelineChatGptRefreshReport {
    param([object]$Payload)

    if ($null -eq $Payload) {
        return [ordered]@{
            available = $false
            startedAt = ""
            completedAt = ""
            reportPath = ""
            discovered = 0
            processed = 0
            skipped = 0
            failed = 0
            missing = 0
            duplicates = 0
            durationSeconds = 0
        }
    }
    $summary = Get-PropertyValue -Object $Payload -Name "summary" -Default @{}
    return [ordered]@{
        available = $true
        startedAt = Convert-TimelineText -Value (Get-PropertyValue -Object $Payload -Name "started_at" -Default "")
        completedAt = Convert-TimelineText -Value (Get-PropertyValue -Object $Payload -Name "completed_at" -Default "")
        reportPath = Convert-TimelineChatGptLocalPath -Path (Convert-TimelineText -Value (Get-PropertyValue -Object $Payload -Name "report_path" -Default ""))
        discovered = Convert-TimelineAudioInt -Value (Get-PropertyValue -Object $summary -Name "discovered" -Default 0)
        processed = Convert-TimelineAudioInt -Value (Get-PropertyValue -Object $summary -Name "processed" -Default 0)
        skipped = Convert-TimelineAudioInt -Value (Get-PropertyValue -Object $summary -Name "skipped" -Default 0)
        failed = Convert-TimelineAudioInt -Value (Get-PropertyValue -Object $summary -Name "failed" -Default 0)
        missing = Convert-TimelineAudioInt -Value (Get-PropertyValue -Object $summary -Name "missing" -Default 0)
        duplicates = Convert-TimelineAudioInt -Value (Get-PropertyValue -Object $summary -Name "duplicates" -Default 0)
        durationSeconds = [double](Convert-TimelineAudioNumber -Value (Get-PropertyValue -Object $summary -Name "duration_seconds" -Default 0))
    }
}

function Convert-TimelineChatGptJob {
    param([System.IO.DirectoryInfo]$Directory)

    $status = Read-TimelineChatGptJsonFile -Path (Join-Path $Directory.FullName "status.json")
    $result = Read-TimelineChatGptJsonFile -Path (Join-Path $Directory.FullName "result.json")
    $request = Read-TimelineChatGptJsonFile -Path (Join-Path $Directory.FullName "request.json")
    $inputItems = @(Get-PropertyValue -Object $request -Name "input_items" -Default @())
    $firstInput = @($inputItems | Select-Object -First 1)
    $inputPath = if ($firstInput.Count -gt 0) { Convert-TimelineText -Value (Get-PropertyValue -Object $firstInput[0] -Name "original_path" -Default "") } else { "" }
    $archivePath = Convert-TimelineText -Value (Get-PropertyValue -Object $result -Name "archive_path" -Default "")
    $archiveLocalPath = Convert-TimelineChatGptLocalPath -Path $archivePath
    $archiveSize = [int64]0
    if ($archiveLocalPath -and (Test-Path -LiteralPath $archiveLocalPath)) {
        $archiveSize = [int64](Get-Item -LiteralPath $archiveLocalPath).Length
    }

    return [ordered]@{
        jobId = Convert-TimelineText -Value (Get-PropertyValueAny -Object $status -Names @("job_id", "jobId") -Default $Directory.Name)
        state = Convert-TimelineText -Value (Get-PropertyValue -Object $status -Name "state" -Default (Get-PropertyValue -Object $result -Name "state" -Default ""))
        currentStage = Convert-TimelineText -Value (Get-PropertyValue -Object $status -Name "current_stage" -Default "")
        message = Convert-TimelineText -Value (Get-PropertyValue -Object $status -Name "message" -Default "")
        updatedAt = Convert-TimelineText -Value (Get-PropertyValue -Object $status -Name "updated_at" -Default "")
        completedAt = Convert-TimelineText -Value (Get-PropertyValue -Object $status -Name "completed_at" -Default "")
        conversationsTotal = Convert-TimelineAudioInt -Value (Get-PropertyValue -Object $status -Name "conversations_total" -Default 0)
        conversationsDone = Convert-TimelineAudioInt -Value (Get-PropertyValue -Object $status -Name "conversations_done" -Default 0)
        progressPercent = [double](Convert-TimelineAudioNumber -Value (Get-PropertyValue -Object $status -Name "progress_percent" -Default 0))
        processedCount = Convert-TimelineAudioInt -Value (Get-PropertyValue -Object $result -Name "processed_count" -Default 0)
        errorCount = Convert-TimelineAudioInt -Value (Get-PropertyValue -Object $result -Name "error_count" -Default 0)
        batchCount = Convert-TimelineAudioInt -Value (Get-PropertyValue -Object $result -Name "batch_count" -Default 0)
        inputPath = Convert-TimelineChatGptLocalPath -Path $inputPath
        archivePath = $archiveLocalPath
        archiveSizeBytes = $archiveSize
        runDirectory = $Directory.FullName
        currentConversation = Get-TimelineChatGptCurrentConversationLabel -Value (Get-PropertyValueAny -Object $status -Names @("current_conversation", "currentConversation") -Default $null)
    }
}

function Get-TimelineChatGptCurrentConversationLabel {
    param([object]$Value)

    if ($null -eq $Value) {
        return ""
    }
    if ($Value -is [string]) {
        return Convert-TimelineText -Value $Value
    }

    $title = Convert-TimelineText -Value (Get-PropertyValueAny -Object $Value -Names @("title", "display_name", "displayName", "conversation_id", "conversationId", "id") -Default "")
    if ($title) {
        return $title
    }

    return ""
}

function Get-TimelineChatGptJobs {
    param([string[]]$RootPaths)

    $rootSet = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
    foreach ($rootPath in @($RootPaths)) {
        $path = ([string]$rootPath).Trim()
        if ($path -and (Test-Path -LiteralPath $path)) {
            [void]$rootSet.Add($path)
        }
    }

    $jobs = @()
    foreach ($rootPath in @($rootSet)) {
        foreach ($dir in @(Get-ChildItem -LiteralPath $rootPath -Directory -Filter "job-*" -ErrorAction SilentlyContinue)) {
            $jobs += Convert-TimelineChatGptJob -Directory $dir
        }
    }
    return @($jobs | Sort-Object { $_.updatedAt }, { $_.completedAt }, { $_.jobId } -Descending | Select-Object -First 20)
}

function Get-TimelineChatGptThreads {
    param(
        [int]$Page = 1,
        [int]$PageSize = 100
    )

    $settings = Read-TimelineChatGptSettings
    $outputRoot = Convert-TimelineChatGptDirectoryRoot -Root $settings.outputRoot -FallbackId "output" -FallbackDisplayName "Output"
    $masterLocalPath = [string]$outputRoot.displayPath
    return Get-TimelineThreadRowsPageFromRoot -RootPath $masterLocalPath -Page $Page -PageSize $PageSize
}

function Get-TimelineChatGptOverview {
    $productFound = Test-Path -LiteralPath $ChatGptProductPath
    $settings = Read-TimelineChatGptSettings
    $messages = @()
    $inputRoots = @()
    foreach ($root in @($settings.inputRoots)) {
        $inputRoots += Convert-TimelineChatGptRoot -Root $root -AllowedExtensions @($settings.allowedExtensions)
    }
    $outputRoot = Convert-TimelineChatGptDirectoryRoot -Root $settings.outputRoot -FallbackId "output" -FallbackDisplayName "Output"
    $masterRoot = $outputRoot
    $stateRoot = Convert-TimelineChatGptDirectoryRoot -Root $settings.stateRoot -FallbackId "state" -FallbackDisplayName "State"
    $latestRefresh = $null
    $jobs = @()
    $itemCount = 0

    if ($productFound) {
        if ([string]$masterRoot.displayPath) {
            $itemCount = Get-TimelineManifestItemCount -RootPath ([string]$masterRoot.displayPath)
        }
        $jobs = @(Get-TimelineChatGptJobs -RootPaths @([string]$masterRoot.displayPath, [string]$stateRoot.displayPath))
    }

    $issues = @()
    if (-not $productFound) {
        $issues += "TimelineForChatGPT was not found."
    }
    if (-not [bool]$settings.settingsFound) {
        $issues += "settings.json was not found."
    }
    if (-not [string]$outputRoot.path) {
        $issues += "Output root is not configured."
    }

    return [ordered]@{
        productFound = $productFound
        productPath = $ChatGptProductPath
        settingsFound = [bool]$settings.settingsFound
        settingsPath = [string]$settings.path
        settingsValid = ($productFound -and [bool]$settings.settingsFound -and [bool]$outputRoot.path)
        inputRoots = @($inputRoots)
        masterRoot = $masterRoot
        outputRoot = $outputRoot
        stateRoot = $stateRoot
        recursive = [bool]$settings.recursive
        profile = [string]$settings.profile
        processableInputCount = $itemCount
        itemCount = $itemCount
        latestRefresh = Convert-TimelineChatGptRefreshReport -Payload $latestRefresh
        threads = @()
        jobs = @($jobs)
        message = (($issues + $messages | Where-Object { $_ }) -join " ")
    }
}

function Start-TimelineChatGptRefresh {
    param([object]$Request)

    $filePath = Convert-TimelineText -Value (Get-PropertyValue -Object $Request -Name "filePath" -Default "")
    if (-not $filePath) {
        throw "ChatGPT export ZIP is required."
    }

    $args = @("items", "refresh", "--file", $filePath, "--json")
    $downloadTo = Convert-TimelineText -Value (Get-PropertyValue -Object $Request -Name "downloadTo" -Default "")
    if ($downloadTo) {
        $args += @("--download-to", $downloadTo)
    }
    if ([bool](Get-PropertyValue -Object $Request -Name "overwrite" -Default $false)) {
        $args += "--overwrite"
    }

    $payload = Invoke-TimelineChatGptCliJson -CliArgs $args -TimeoutSeconds 1800
    $current = Get-PropertyValue -Object $payload -Name "current" -Default @{}
    $manifest = Get-PropertyValue -Object $payload -Name "manifest" -Default @{}
    return [ordered]@{
        available = $true
        startedAt = Convert-TimelineText -Value (Get-PropertyValue -Object $current -Name "started_at" -Default "")
        completedAt = Convert-TimelineText -Value (Get-PropertyValue -Object $current -Name "completed_at" -Default "")
        reportPath = Convert-TimelineText -Value (Get-PropertyValue -Object $current -Name "download_zip_path" -Default "")
        discovered = Convert-TimelineAudioInt -Value (Get-PropertyValue -Object $manifest -Name "item_count" -Default 0)
        processed = Convert-TimelineAudioInt -Value (Get-PropertyValue -Object $current -Name "item_count" -Default 0)
        skipped = 0
        failed = 0
        missing = 0
        duplicates = 0
        durationSeconds = 0
    }
}

function Get-TimelineChatGptMasterLocalPath {
    $settings = Read-TimelineChatGptSettings
    $outputRoot = Get-PropertyValue -Object $settings -Name "outputRoot" -Default @{}
    return Convert-TimelineChatGptLocalPath -Path (Convert-TimelineText -Value (Get-PropertyValue -Object $outputRoot -Name "path" -Default ""))
}

function Start-TimelineChatGptDownload {
    param([object]$Request)

    $itemIds = @(Get-TimelineRequestItemIds -Request $Request)
    if ($itemIds.Count -gt 0) {
        throw "TimelineForChatGPT CLI does not support selected item download yet."
    }

    $requestedOutputPath = Convert-TimelineText -Value (Get-PropertyValue -Object $Request -Name "outputPath" -Default "")
    $hostOutputPath = Resolve-TimelineManagedDownloadDirectory `
        -ProductId "chatgpt" `
        -RequestedPath $requestedOutputPath

    $payload = Invoke-TimelineChatGptCliJson `
        -CliArgs @("items", "download", "--to", $hostOutputPath, "--overwrite", "--json") `
        -TimeoutSeconds 900
    $archivePath = Convert-TimelineDownloadLocalPath -Path (Convert-TimelineText -Value (Get-PropertyValueAny -Object $payload -Names @("download_path", "downloadPath", "destination_path", "destinationPath", "archive_path", "archivePath") -Default ""))
    if (-not $archivePath -or -not (Test-TimelineDownloadFileAllowed -Path $archivePath)) {
        throw "TimelineForChatGPT CLI did not create a downloadable ZIP in the Timeline work directory."
    }

    return [ordered]@{
        archivePath = [string]$archivePath
        itemIds = @()
    }
}

function Remove-TimelineChatGptItems {
    param([object]$Request)

    return Remove-TimelineThreadItems -RootPath (Get-TimelineChatGptMasterLocalPath) -ItemIds @(Get-TimelineRequestItemIds -Request $Request)
}

function Convert-TimelineAudioModelInventory {
    param([object]$Payload)

    $pipeline = Get-PropertyValue -Object $Payload -Name "pipeline" -Default @{}
    $models = @()
    foreach ($row in @(Get-PropertyValue -Object $Payload -Name "models" -Default @())) {
        $remote = Get-PropertyValue -Object $row -Name "huggingface" -Default $null
        $notes = @()
        foreach ($note in @(Get-PropertyValue -Object $row -Name "notes" -Default @())) {
            $text = Convert-TimelineText -Value $note
            if ($text) {
                $notes += $text
            }
        }

        $models += [ordered]@{
            role = Convert-TimelineText -Value (Get-PropertyValue -Object $row -Name "role" -Default "")
            displayName = Convert-TimelineText -Value (Get-PropertyValue -Object $row -Name "display_name" -Default "")
            source = Convert-TimelineText -Value (Get-PropertyValue -Object $row -Name "source" -Default "")
            modelId = Convert-TimelineText -Value (Get-PropertyValue -Object $row -Name "model_id" -Default "")
            backend = Convert-TimelineText -Value (Get-PropertyValue -Object $row -Name "backend" -Default "")
            required = [bool](Get-PropertyValue -Object $row -Name "required" -Default $false)
            configured = [bool](Get-PropertyValue -Object $row -Name "configured" -Default $false)
            requiresHuggingFaceToken = [bool](Get-PropertyValue -Object $row -Name "requires_huggingface_token" -Default $false)
            requiresAccessApproval = [bool](Get-PropertyValue -Object $row -Name "requires_access_approval" -Default $false)
            unitType = Convert-TimelineText -Value (Get-PropertyValue -Object $row -Name "unit_type" -Default "")
            url = Convert-TimelineText -Value (Get-PropertyValue -Object $row -Name "url" -Default "")
            license = Convert-TimelineText -Value (Get-PropertyValue -Object $remote -Name "license" -Default "")
            gated = Convert-TimelineText -Value (Get-PropertyValue -Object $remote -Name "gated" -Default "")
            remoteStatus = Convert-TimelineText -Value (Get-PropertyValue -Object $remote -Name "remote_status" -Default "")
            remoteMessage = Convert-TimelineText -Value (Get-PropertyValue -Object $remote -Name "error" -Default "")
            notes = @($notes)
        }
    }

    return [ordered]@{
        available = $true
        message = ""
        generatedAt = Convert-TimelineText -Value (Get-PropertyValue -Object $Payload -Name "generated_at" -Default "")
        pipelineName = Convert-TimelineText -Value (Get-PropertyValue -Object $pipeline -Name "name" -Default "")
        pipelineVersion = Convert-TimelineText -Value (Get-PropertyValue -Object $pipeline -Name "pipeline_version" -Default "")
        models = @($models)
    }
}

function Get-TimelineAudioModels {
    $now = Get-Date
    if ($null -ne $script:TimelineModelInventoryCache -and $null -ne $script:TimelineModelInventoryCacheAt) {
        if (($now - $script:TimelineModelInventoryCacheAt).TotalMinutes -lt 15) {
            return $script:TimelineModelInventoryCache
        }
    }

    $result = [ordered]@{
        available = $true
        message = ""
        generatedAt = $now.ToString("s")
        pipelineName = "TimelineForAudio"
        pipelineVersion = ""
        models = @(
            [ordered]@{
                role = "speaker_diarization"
                displayName = "Speaker diarization"
                source = "huggingface"
                modelId = "pyannote/speaker-diarization-community-1"
                backend = "pyannote"
                required = $true
                configured = $true
                requiresHuggingFaceToken = $true
                requiresAccessApproval = $true
                unitType = "speaker turns"
                url = "https://huggingface.co/pyannote/speaker-diarization-community-1"
                license = ""
                gated = ""
                remoteStatus = "not checked"
                remoteMessage = ""
                notes = @()
            },
            [ordered]@{
                role = "acoustic-units"
                displayName = "ZIPA large ONNX"
                source = "local"
                modelId = "zipa-large-onnx"
                backend = "onnx"
                required = $true
                configured = $true
                requiresHuggingFaceToken = $false
                requiresAccessApproval = $false
                unitType = "acoustic units"
                url = ""
                license = ""
                gated = ""
                remoteStatus = "local"
                remoteMessage = ""
                notes = @()
            }
        )
    }
    $script:TimelineModelInventoryCache = $result
    $script:TimelineModelInventoryCacheAt = $now
    return $result
}

function Convert-TimelineStringArray {
    param([object]$Value)

    $rows = @()
    foreach ($item in @($Value)) {
        $text = Convert-TimelineText -Value $item
        if ($text) {
            $rows += $text
        }
    }
    return @($rows)
}

function Convert-TimelineAudioDeleteGeneratedResult {
    param(
        [object]$Payload,
        [string[]]$RequestedItemIds = @(),
        [string[]]$RequestedSourceFileIdentities = @()
    )

    $requestedItemIds = @(Convert-TimelineStringArray -Value (Get-PropertyValue -Object $Payload -Name "requested_item_ids" -Default @()))
    if ($requestedItemIds.Count -eq 0) {
        $requestedItemIds = @($RequestedItemIds)
    }

    $requestedSourceFileIdentities = @(Convert-TimelineStringArray -Value (Get-PropertyValue -Object $Payload -Name "requested_source_file_identities" -Default @()))
    if ($requestedSourceFileIdentities.Count -eq 0) {
        $requestedSourceFileIdentities = @($RequestedSourceFileIdentities)
    }

    $missingItemIds = @(Convert-TimelineStringArray -Value (Get-PropertyValue -Object $Payload -Name "missing_item_ids" -Default @()))
    $missingSourceFileIdentities = @(Convert-TimelineStringArray -Value (Get-PropertyValue -Object $Payload -Name "missing_source_file_identities" -Default @()))
    if ($missingSourceFileIdentities.Count -eq 0) {
        $missingSourceFileIdentities = @($missingItemIds)
    }

    $removedCount = Convert-TimelineAudioInt -Value (Get-PropertyValueAny -Object $Payload -Names @(
        "catalog_rows_removed",
        "removed_count",
        "items_removed",
        "item_count"
    ) -Default 0)

    $matchedCount = Convert-TimelineAudioInt -Value (Get-PropertyValueAny -Object $Payload -Names @(
        "matched_count",
        "removed_count",
        "items_removed",
        "item_count"
    ) -Default $removedCount)

    return [ordered]@{
        dryRun = [bool](Get-PropertyValue -Object $Payload -Name "dry_run" -Default $false)
        outputRootId = Convert-TimelineText -Value (Get-PropertyValue -Object $Payload -Name "output_root_id" -Default "")
        outputRootPath = Convert-TimelineText -Value (Get-PropertyValue -Object $Payload -Name "output_root_path" -Default "")
        requestedItemIds = @($requestedItemIds)
        requestedSourceFileIdentities = @($requestedSourceFileIdentities)
        matchedCount = $matchedCount
        missingItemIds = @($missingItemIds)
        missingSourceFileIdentities = @($missingSourceFileIdentities)
        catalogRowsRemoved = $removedCount
        mediaDirsRemoved = Convert-TimelineAudioInt -Value (Get-PropertyValue -Object $Payload -Name "media_dirs_removed" -Default 0)
        mediaDirs = @(Convert-TimelineStringArray -Value (Get-PropertyValue -Object $Payload -Name "media_dirs" -Default @()))
        unsafeMediaDirs = @(Convert-TimelineStringArray -Value (Get-PropertyValue -Object $Payload -Name "unsafe_media_dirs" -Default @()))
    }
}

function Test-TimelineAudioMissingCommand {
    param(
        [string]$Message,
        [string]$CommandName
    )

    $text = ([string]$Message).ToLowerInvariant()
    return (
        $text.Contains("invalid choice: '$CommandName'") `
        -or $text.Contains("invalid choice: `"$CommandName`"") `
        -or $text.Contains("argument command: invalid choice")
    )
}

function Convert-TimelineNullableInt {
    param([object]$Value)

    if ($null -eq $Value) {
        return $null
    }
    $text = Convert-TimelineText -Value $Value
    if (-not $text) {
        return $null
    }
    try {
        return [int]$text
    }
    catch {
        return $null
    }
}

function Convert-TimelinePagination {
    param(
        [object]$Payload,
        [string[]]$TotalNames = @("total_items", "totalItems", "item_count", "itemCount", "total"),
        [string[]]$ReturnedNames = @("returned_items", "returnedItems")
    )

    $pagination = Get-PropertyValue -Object $Payload -Name "pagination" -Default @{}
    $total = Convert-TimelineAudioInt -Value (Get-PropertyValueAny -Object $pagination -Names $TotalNames -Default (Get-PropertyValueAny -Object $Payload -Names $TotalNames -Default 0))
    $returned = Convert-TimelineAudioInt -Value (Get-PropertyValueAny -Object $pagination -Names $ReturnedNames -Default 0)
    return [ordered]@{
        mode = Convert-TimelineText -Value (Get-PropertyValue -Object $pagination -Name "mode" -Default "")
        page = Convert-TimelineAudioInt -Value (Get-PropertyValue -Object $pagination -Name "page" -Default 0)
        pageSize = Convert-TimelineAudioInt -Value (Get-PropertyValueAny -Object $pagination -Names @("page_size", "pageSize") -Default 0)
        totalItems = $total
        totalPages = Convert-TimelineAudioInt -Value (Get-PropertyValueAny -Object $pagination -Names @("total_pages", "totalPages") -Default 0)
        returnedItems = $returned
        offset = Convert-TimelineAudioInt -Value (Get-PropertyValue -Object $pagination -Name "offset" -Default 0)
        rangeStart = Convert-TimelineAudioInt -Value (Get-PropertyValueAny -Object $pagination -Names @("range_start", "rangeStart") -Default 0)
        rangeEnd = Convert-TimelineAudioInt -Value (Get-PropertyValueAny -Object $pagination -Names @("range_end", "rangeEnd") -Default 0)
        hasPrevious = [bool](Get-PropertyValueAny -Object $pagination -Names @("has_previous", "hasPrevious") -Default $false)
        hasNext = [bool](Get-PropertyValueAny -Object $pagination -Names @("has_next", "hasNext") -Default $false)
    }
}

function New-TimelinePagination {
    param(
        [int]$Page,
        [int]$PageSize,
        [int]$TotalItems,
        [int]$ReturnedItems
    )

    $effectivePage = [Math]::Max(1, $Page)
    $effectivePageSize = [Math]::Max(1, $PageSize)
    $totalPages = if ($TotalItems -gt 0) { [int][Math]::Ceiling($TotalItems / [double]$effectivePageSize) } else { 0 }
    $offset = ($effectivePage - 1) * $effectivePageSize
    return [ordered]@{
        mode = "page"
        page = $effectivePage
        pageSize = $effectivePageSize
        totalItems = $TotalItems
        totalPages = $totalPages
        returnedItems = $ReturnedItems
        offset = $offset
        rangeStart = if ($ReturnedItems -gt 0) { $offset + 1 } else { 0 }
        rangeEnd = if ($ReturnedItems -gt 0) { $offset + $ReturnedItems } else { 0 }
        hasPrevious = ($effectivePage -gt 1 -and $TotalItems -gt 0)
        hasNext = ($effectivePage -lt $totalPages)
    }
}

function Get-TimelineRequestPage {
    param(
        [object]$Query,
        [int]$DefaultPage = 1
    )

    $page = Convert-TimelineNullableInt -Value ([string]$Query["page"])
    if ($null -eq $page -or $page -lt 1) {
        return $DefaultPage
    }
    return $page
}

function Get-TimelineRequestPageSize {
    param(
        [object]$Query,
        [int]$DefaultPageSize = 100,
        [int]$MaxPageSize = 500
    )

    $pageSize = Convert-TimelineNullableInt -Value ([string]$Query["pageSize"])
    if ($null -eq $pageSize) {
        $pageSize = Convert-TimelineNullableInt -Value ([string]$Query["page-size"])
    }
    if ($null -eq $pageSize -or $pageSize -lt 1) {
        return $DefaultPageSize
    }
    return [Math]::Min($pageSize, $MaxPageSize)
}

function Convert-TimelineAudioRefreshResult {
    param([object]$Payload)

    return [ordered]@{
        state = Convert-TimelineText -Value (Get-PropertyValue -Object $Payload -Name "state" -Default "")
        runId = Convert-TimelineText -Value (Get-PropertyValue -Object $Payload -Name "run_id" -Default "")
        runDir = Convert-TimelineText -Value (Get-PropertyValue -Object $Payload -Name "run_dir" -Default "")
        queueOnly = [bool](Get-PropertyValue -Object $Payload -Name "queue_only" -Default $true)
        totalDiscovered = Convert-TimelineAudioInt -Value (Get-PropertyValue -Object $Payload -Name "total_discovered" -Default 0)
        selectedCount = Convert-TimelineAudioInt -Value (Get-PropertyValue -Object $Payload -Name "selected_count" -Default 0)
        queuedCount = Convert-TimelineAudioInt -Value (Get-PropertyValue -Object $Payload -Name "queued_count" -Default 0)
        skippedCount = Convert-TimelineAudioInt -Value (Get-PropertyValue -Object $Payload -Name "skipped_count" -Default 0)
        deferredCount = Convert-TimelineAudioInt -Value (Get-PropertyValue -Object $Payload -Name "deferred_count" -Default 0)
        queuedLimit = Convert-TimelineNullableInt -Value (Get-PropertyValue -Object $Payload -Name "queued_limit" -Default $null)
    }
}

function Convert-TimelineAudioDownloadItemsResult {
    param([object]$Payload)

    $archivePath = Convert-TimelineText -Value (Get-PropertyValueAny -Object $Payload -Names @("archive_path", "archivePath", "destination_path", "destinationPath", "download_path", "downloadPath", "zip_path", "zipPath") -Default "")
    return [ordered]@{
        archivePath = Convert-TimelineAudioLocalPath -Path $archivePath
        itemIds = @(Convert-TimelineStringArray -Value (Get-PropertyValueAny -Object $Payload -Names @("item_ids", "itemIds") -Default @()))
    }
}

function Start-TimelineAudioRefresh {
    param([object]$Request)

    $args = @("items", "refresh", "--queue-only", "--json")
    if ([bool](Get-PropertyValue -Object $Request -Name "reprocessDuplicates" -Default $false)) {
        $args += "--reprocess-duplicates"
    }

    $maxItems = Convert-TimelineNullableInt -Value (Get-PropertyValue -Object $Request -Name "maxItems" -Default $null)
    if ($null -ne $maxItems -and $maxItems -gt 0) {
        $args += @("--max-items", ([string]$maxItems))
    }

    try {
        $payload = Invoke-TimelineAudioCliJson -CliArgs $args -TimeoutSeconds 120
        return Convert-TimelineAudioRefreshResult -Payload $payload
    }
    catch {
        if (([string]$_.Exception.Message).ToLowerInvariant().Contains("unrecognized arguments: --queue-only")) {
            $args = @("items", "refresh", "--json")
            if ([bool](Get-PropertyValue -Object $Request -Name "reprocessDuplicates" -Default $false)) {
                $args += "--reprocess-duplicates"
            }
            if ($null -ne $maxItems -and $maxItems -gt 0) {
                $args += @("--max-items", ([string]$maxItems))
            }
            $payload = Invoke-TimelineAudioCliJson -CliArgs $args -TimeoutSeconds 120
            return Convert-TimelineAudioRefreshResult -Payload $payload
        }
        if (
            -not (Test-TimelineAudioMissingCommand -Message $_.Exception.Message -CommandName "items") `
            -and -not (Test-TimelineAudioMissingCommand -Message $_.Exception.Message -CommandName "refresh")
        ) {
            throw
        }
    }

    $args = @("refresh", "--queue-only", "--json")
    if ([bool](Get-PropertyValue -Object $Request -Name "reprocessDuplicates" -Default $false)) {
        $args += "--reprocess-duplicates"
    }
    if ($null -ne $maxItems -and $maxItems -gt 0) {
        $args += @("--max-items", ([string]$maxItems))
    }

    $payload = Invoke-TimelineAudioCliJson -CliArgs $args -TimeoutSeconds 120
    return Convert-TimelineAudioRefreshResult -Payload $payload
}

function New-TimelineAudioItemsDownload {
    param([object]$Request)

    $itemIds = @()
    foreach ($itemId in @(Get-PropertyValue -Object $Request -Name "itemIds" -Default @())) {
        $text = Convert-TimelineText -Value $itemId
        if ($text -and -not $text.Contains(":")) {
            $itemIds += $text
        }
    }
    $itemIds = @($itemIds | Select-Object -Unique)

    $args = @("items", "download", "--json")
    if ($itemIds.Count -gt 0) {
        $args += @("--item-id", ($itemIds -join ","))
    }
    $outputPath = Convert-TimelineText -Value (Get-PropertyValue -Object $Request -Name "outputPath" -Default "")
    $hostOutputPath = Resolve-TimelineManagedDownloadFile `
        -ProductId "audio" `
        -FilePrefix "TimelineForAudio-items" `
        -RequestedPath $outputPath
    $args += @("--output", $hostOutputPath)

    $payload = Invoke-TimelineAudioCliJson -CliArgs $args -TimeoutSeconds 900
    $result = Convert-TimelineAudioDownloadItemsResult -Payload $payload
    $archivePath = Convert-TimelineDownloadLocalPath -Path (Convert-TimelineText -Value (Get-PropertyValue -Object $result -Name "archivePath" -Default ""))
    if (-not $archivePath -or -not (Test-TimelineDownloadFileAllowed -Path $archivePath)) {
        throw "TimelineForAudio CLI did not create a downloadable ZIP in the Timeline work directory."
    }

    return [ordered]@{
        archivePath = [string]$archivePath
        itemIds = @(Get-PropertyValue -Object $result -Name "itemIds" -Default @())
    }
}

function Get-TimelineExportDownloadRoot {
    $root = Join-Path (Get-TimelineLocalDownloadRoot) "timeline"
    [System.IO.Directory]::CreateDirectory($root) | Out-Null
    return $root
}

function Get-TimelineZipSafeSegment {
    param([string]$Value)

    $text = Convert-TimelineText -Value $Value
    if (-not $text) {
        return "item"
    }
    $safe = [System.Text.RegularExpressions.Regex]::Replace($text, '[^A-Za-z0-9._-]+', "_")
    $safe = $safe.Trim("._-")
    if (-not $safe) {
        return "item"
    }
    if ($safe.Length -gt 120) {
        return $safe.Substring(0, 120)
    }
    return $safe
}

function Read-TimelineZipEntryJson {
    param([System.IO.Compression.ZipArchiveEntry]$Entry)

    try {
        $stream = $Entry.Open()
        $reader = [System.IO.StreamReader]::new($stream, [System.Text.UTF8Encoding]::new($false), $true)
        try {
            $text = $reader.ReadToEnd()
        }
        finally {
            $reader.Dispose()
        }
        if (-not ([string]$text).Trim()) {
            return $null
        }
        return $text | ConvertFrom-Json
    }
    catch {
        return $null
    }
}

function Write-TimelineExportJsonLine {
    param(
        [System.IO.StreamWriter]$Writer,
        [object]$Payload
    )

    $Writer.WriteLine((ConvertTo-TimelineJson $Payload))
}

function Copy-TimelineZipEntryToFile {
    param(
        [System.IO.Compression.ZipArchiveEntry]$Entry,
        [string]$DestinationRoot,
        [string]$Prefix
    )

    $relative = ([string]$Entry.FullName).Replace('\', '/').TrimStart('/')
    if (-not $relative -or $relative.EndsWith("/", [System.StringComparison]::Ordinal)) {
        return
    }

    $targetRelative = if ($Prefix) { "$Prefix/$relative" } else { $relative }
    $destination = Join-Path $DestinationRoot $targetRelative.Replace("/", "\")
    $destinationRootFull = [System.IO.Path]::GetFullPath($DestinationRoot).TrimEnd([char[]]@('\', '/'))
    $destinationFull = [System.IO.Path]::GetFullPath($destination)
    $prefixPath = $destinationRootFull + [System.IO.Path]::DirectorySeparatorChar
    if (-not $destinationFull.StartsWith($prefixPath, [System.StringComparison]::OrdinalIgnoreCase)) {
        return
    }

    $parent = [System.IO.Path]::GetDirectoryName($destinationFull)
    if ($parent) {
        [System.IO.Directory]::CreateDirectory($parent) | Out-Null
    }

    $inputStream = $Entry.Open()
    $outputStream = [System.IO.File]::Open($destinationFull, [System.IO.FileMode]::Create, [System.IO.FileAccess]::Write, [System.IO.FileShare]::None)
    try {
        $inputStream.CopyTo($outputStream)
    }
    finally {
        $outputStream.Dispose()
        $inputStream.Dispose()
    }
}

function Write-TimelineExportAudioTimeline {
    param(
        [object]$Timeline,
        [string]$ProductId,
        [string]$DisplayName,
        [string]$ItemId,
        [string]$RawTimelinePath,
        [string]$RawConvertInfoPath,
        [System.IO.StreamWriter]$ItemsWriter,
        [System.IO.StreamWriter]$EventsWriter
    )

    $source = Get-PropertyValue -Object $Timeline -Name "source" -Default @{}
    $resolvedItemId = Convert-TimelineText -Value (Get-PropertyValueAny -Object $Timeline -Names @("item_id", "itemId", "media_id", "mediaId") -Default $ItemId)
    if (-not $resolvedItemId) {
        $resolvedItemId = $ItemId
    }
    $title = Convert-TimelineText -Value (Get-PropertyValueAny -Object $source -Names @("filename", "file_name", "display_name", "source_file_identity") -Default $resolvedItemId)
    $turns = @(Get-PropertyValue -Object $Timeline -Name "turns" -Default @())

    Write-TimelineExportJsonLine -Writer $ItemsWriter -Payload ([ordered]@{
        schemaVersion = 1
        product = $ProductId
        productName = $DisplayName
        itemId = $resolvedItemId
        itemType = "audio"
        title = $title
        createdAt = Convert-TimelineText -Value (Get-PropertyValueAny -Object $source -Names @("recorded_at", "created_at", "modified_at") -Default "")
        updatedAt = ""
        eventCount = $turns.Count
        sourceRef = [ordered]@{
            timelinePath = $RawTimelinePath
            convertInfoPath = $RawConvertInfoPath
        }
    })

    $eventCount = 0
    $sequence = 0
    foreach ($turn in $turns) {
        $startSec = Convert-TimelineAudioNumber -Value (Get-PropertyValueAny -Object $turn -Names @("start_sec", "startSec") -Default $null)
        $endSec = Convert-TimelineAudioNumber -Value (Get-PropertyValueAny -Object $turn -Names @("end_sec", "endSec") -Default $null)
        $absoluteStartAt = Convert-TimelineText -Value (Get-PropertyValueAny -Object $turn -Names @("absolute_start_at", "absoluteStartAt") -Default "")
        $absoluteEndAt = Convert-TimelineText -Value (Get-PropertyValueAny -Object $turn -Names @("absolute_end_at", "absoluteEndAt") -Default "")
        Write-TimelineExportJsonLine -Writer $EventsWriter -Payload ([ordered]@{
            schemaVersion = 1
            eventId = "${ProductId}:${resolvedItemId}:turn:$sequence"
            product = $ProductId
            itemId = $resolvedItemId
            eventType = "audio_turn"
            sequence = $sequence
            time = [ordered]@{
                absoluteStartAt = $absoluteStartAt
                absoluteEndAt = $absoluteEndAt
                relativeStartSec = $startSec
                relativeEndSec = $endSec
                timeBasis = if ($absoluteStartAt) { "absolute" } else { "source_relative" }
            }
            actor = [ordered]@{
                type = "speaker"
                label = Convert-TimelineText -Value (Get-PropertyValue -Object $turn -Name "speaker" -Default "")
            }
            content = [ordered]@{
                kind = "phone_tokens"
                value = Convert-TimelineText -Value (Get-PropertyValueAny -Object $turn -Names @("phone_tokens", "phoneTokens", "acoustic_units", "acousticUnits") -Default "")
            }
            sourceRef = [ordered]@{
                timelinePath = $RawTimelinePath
                convertInfoPath = $RawConvertInfoPath
            }
        })
        $sequence += 1
        $eventCount += 1
    }
    return $eventCount
}

function Write-TimelineExportThreadTimeline {
    param(
        [object]$Timeline,
        [string]$ProductId,
        [string]$DisplayName,
        [string]$ItemId,
        [string]$RawTimelinePath,
        [string]$RawConvertInfoPath,
        [System.IO.StreamWriter]$ItemsWriter,
        [System.IO.StreamWriter]$EventsWriter
    )

    $resolvedItemId = Convert-TimelineText -Value (Get-PropertyValueAny -Object $Timeline -Names @("thread_id", "conversation_id", "item_id", "id") -Default $ItemId)
    if (-not $resolvedItemId) {
        $resolvedItemId = $ItemId
    }
    $messages = @(Get-PropertyValue -Object $Timeline -Name "messages" -Default @())
    $createdAt = Convert-TimelineText -Value (Get-PropertyValue -Object $Timeline -Name "created_at" -Default "")
    $updatedAt = Convert-TimelineText -Value (Get-PropertyValue -Object $Timeline -Name "updated_at" -Default "")
    $title = Convert-TimelineText -Value (Get-PropertyValue -Object $Timeline -Name "title" -Default $resolvedItemId)

    Write-TimelineExportJsonLine -Writer $ItemsWriter -Payload ([ordered]@{
        schemaVersion = 1
        product = $ProductId
        productName = $DisplayName
        itemId = $resolvedItemId
        itemType = "thread"
        title = $title
        createdAt = $createdAt
        updatedAt = $updatedAt
        eventCount = $messages.Count
        sourceRef = [ordered]@{
            timelinePath = $RawTimelinePath
            convertInfoPath = $RawConvertInfoPath
        }
    })

    $eventCount = 0
    $sequence = 0
    foreach ($message in $messages) {
        $created = Convert-TimelineText -Value (Get-PropertyValueAny -Object $message -Names @("created_at", "createdAt", "timestamp") -Default "")
        Write-TimelineExportJsonLine -Writer $EventsWriter -Payload ([ordered]@{
            schemaVersion = 1
            eventId = "${ProductId}:${resolvedItemId}:message:$sequence"
            product = $ProductId
            itemId = $resolvedItemId
            eventType = "message"
            sequence = $sequence
            time = [ordered]@{
                absoluteStartAt = $created
                absoluteEndAt = $created
                relativeStartSec = $null
                relativeEndSec = $null
                timeBasis = if ($created) { "absolute" } else { "sequence" }
            }
            actor = [ordered]@{
                type = "role"
                label = Convert-TimelineText -Value (Get-PropertyValue -Object $message -Name "role" -Default "")
            }
            content = [ordered]@{
                kind = "text"
                value = Convert-TimelineText -Value (Get-PropertyValueAny -Object $message -Names @("text", "content", "body") -Default "")
            }
            sourceRef = [ordered]@{
                timelinePath = $RawTimelinePath
                convertInfoPath = $RawConvertInfoPath
            }
        })
        $sequence += 1
        $eventCount += 1
    }
    return $eventCount
}

function Add-TimelineExportProductArchive {
    param(
        [string]$ProductId,
        [string]$DisplayName,
        [string]$ArchivePath,
        [string]$PackageRoot,
        [System.IO.StreamWriter]$ItemsWriter,
        [System.IO.StreamWriter]$EventsWriter
    )

    $result = [ordered]@{
        productId = $ProductId
        displayName = $DisplayName
        archivePath = $ArchivePath
        included = $false
        itemCount = 0
        eventCount = 0
        message = ""
    }
    if (-not $ArchivePath -or -not (Test-Path -LiteralPath $ArchivePath -PathType Leaf)) {
        $result.message = "Product download ZIP was not found."
        return $result
    }

    $safeProductId = Get-TimelineZipSafeSegment -Value $ProductId
    $sourceDownloadsRoot = Join-Path $PackageRoot "source-downloads"
    [System.IO.Directory]::CreateDirectory($sourceDownloadsRoot) | Out-Null
    Copy-Item -LiteralPath $ArchivePath -Destination (Join-Path $sourceDownloadsRoot "$safeProductId.zip") -Force

    $zip = [System.IO.Compression.ZipFile]::OpenRead($ArchivePath)
    try {
        foreach ($entry in @($zip.Entries)) {
            Copy-TimelineZipEntryToFile -Entry $entry -DestinationRoot $PackageRoot -Prefix "products/$safeProductId"
            $entryName = ([string]$entry.FullName).Replace('\', '/')
            if ($entryName -notmatch '^items/([^/]+)/timeline\.json$') {
                continue
            }

            $itemId = [System.Uri]::UnescapeDataString([string]$Matches[1])
            $timeline = Read-TimelineZipEntryJson -Entry $entry
            if ($null -eq $timeline) {
                continue
            }

            $rawTimelinePath = "products/$safeProductId/$entryName"
            $rawConvertInfoPath = "products/$safeProductId/items/$itemId/convert_info.json"
            if ($ProductId.Equals("audio", [System.StringComparison]::OrdinalIgnoreCase)) {
                $eventCount = Write-TimelineExportAudioTimeline `
                    -Timeline $timeline `
                    -ProductId $ProductId `
                    -DisplayName $DisplayName `
                    -ItemId $itemId `
                    -RawTimelinePath $rawTimelinePath `
                    -RawConvertInfoPath $rawConvertInfoPath `
                    -ItemsWriter $ItemsWriter `
                    -EventsWriter $EventsWriter
            }
            else {
                $eventCount = Write-TimelineExportThreadTimeline `
                    -Timeline $timeline `
                    -ProductId $ProductId `
                    -DisplayName $DisplayName `
                    -ItemId $itemId `
                    -RawTimelinePath $rawTimelinePath `
                    -RawConvertInfoPath $rawConvertInfoPath `
                    -ItemsWriter $ItemsWriter `
                    -EventsWriter $EventsWriter
            }

            $result.itemCount = [int]$result.itemCount + 1
            $result.eventCount = [int]$result.eventCount + [int]$eventCount
        }
    }
    finally {
        $zip.Dispose()
    }

    $result.included = $true
    return $result
}

function Invoke-TimelineProductDownloadForExport {
    param(
        [string]$ProductId,
        [string]$DisplayName
    )

    if ($ProductId -eq "audio") {
        $payload = New-TimelineAudioItemsDownload -Request ([pscustomobject]@{ all = $true })
        return [ordered]@{
            productId = $ProductId
            displayName = $DisplayName
            archivePath = Convert-TimelineText -Value (Get-PropertyValue -Object $payload -Name "archivePath" -Default "")
        }
    }
    if ($ProductId -eq "windows-codex") {
        $payload = Start-TimelineWindowsCodexDownload -Request ([pscustomobject]@{})
        return [ordered]@{
            productId = $ProductId
            displayName = $DisplayName
            archivePath = Convert-TimelineText -Value (Get-PropertyValue -Object $payload -Name "archivePath" -Default "")
        }
    }
    if ($ProductId -eq "chatgpt") {
        $payload = Start-TimelineChatGptDownload -Request ([pscustomobject]@{})
        return [ordered]@{
            productId = $ProductId
            displayName = $DisplayName
            archivePath = Convert-TimelineText -Value (Get-PropertyValue -Object $payload -Name "archivePath" -Default "")
        }
    }

    throw "Unsupported product: $ProductId"
}

function New-TimelineExportDownload {
    $downloadRoot = Get-TimelineExportDownloadRoot
    $stamp = Get-Date -Format "yyyyMMdd-HHmmss"
    $archivePath = Join-Path $downloadRoot "Timeline-export-$stamp.zip"
    if (Test-Path -LiteralPath $archivePath) {
        Remove-Item -LiteralPath $archivePath -Force
    }

    $stagingRoot = Join-Path (Join-Path $downloadRoot ".staging") ([guid]::NewGuid().ToString("N"))
    $packageRoot = Join-Path $stagingRoot "package"
    [System.IO.Directory]::CreateDirectory((Join-Path $packageRoot "timeline")) | Out-Null

    $products = @(
        [ordered]@{ productId = "audio"; displayName = "TimelineForAudio" },
        [ordered]@{ productId = "windows-codex"; displayName = "TimelineForWindowsCodex" },
        [ordered]@{ productId = "chatgpt"; displayName = "TimelineForChatGPT" }
    )

    $itemsPath = Join-Path $packageRoot "timeline\items.jsonl"
    $eventsPath = Join-Path $packageRoot "timeline\events.jsonl"
    $itemsWriter = [System.IO.StreamWriter]::new($itemsPath, $false, [System.Text.UTF8Encoding]::new($false))
    $eventsWriter = [System.IO.StreamWriter]::new($eventsPath, $false, [System.Text.UTF8Encoding]::new($false))
    $productResults = @()
    try {
        foreach ($product in $products) {
            $download = Invoke-TimelineProductDownloadForExport -ProductId ([string]$product.productId) -DisplayName ([string]$product.displayName)
            $productResults += Add-TimelineExportProductArchive `
                -ProductId ([string]$product.productId) `
                -DisplayName ([string]$product.displayName) `
                -ArchivePath ([string]$download.archivePath) `
                -PackageRoot $packageRoot `
                -ItemsWriter $itemsWriter `
                -EventsWriter $eventsWriter
        }
    }
    catch {
        Remove-Item -LiteralPath $stagingRoot -Recurse -Force -ErrorAction SilentlyContinue
        Remove-Item -LiteralPath $archivePath -Force -ErrorAction SilentlyContinue
        throw
    }
    finally {
        $itemsWriter.Dispose()
        $eventsWriter.Dispose()
    }

    $itemCount = 0
    $eventCount = 0
    foreach ($productResult in $productResults) {
        $itemCount += [int](Get-PropertyValue -Object $productResult -Name "itemCount" -Default 0)
        $eventCount += [int](Get-PropertyValue -Object $productResult -Name "eventCount" -Default 0)
    }

    if ($itemCount -le 0) {
        Remove-Item -LiteralPath $stagingRoot -Recurse -Force -ErrorAction SilentlyContinue
        throw "No downloadable Timeline items were found."
    }

    $manifest = [ordered]@{
        schemaVersion = 1
        artifactType = "timeline_export"
        createdAt = (Get-Date).ToString("o")
        itemCount = $itemCount
        eventCount = $eventCount
        products = @($productResults)
        files = [ordered]@{
            items = "timeline/items.jsonl"
            events = "timeline/events.jsonl"
        }
    }
    Write-TimelineUtf8JsonFile -Path (Join-Path $packageRoot "manifest.json") -Payload $manifest
    Set-Content -LiteralPath (Join-Path $packageRoot "README.md") -Encoding UTF8 -Value @(
        "# Timeline Export",
        "",
        "This ZIP was created by Timeline.",
        "",
        "- timeline/items.jsonl: one row per managed item.",
        "- timeline/events.jsonl: one row per timeline event.",
        "- products/: product download contents expanded for inspection.",
        "- source-downloads/: raw product CLI download ZIPs."
    )

    [System.IO.Compression.ZipFile]::CreateFromDirectory($packageRoot, $archivePath, [System.IO.Compression.CompressionLevel]::Optimal, $false)
    Remove-Item -LiteralPath $stagingRoot -Recurse -Force -ErrorAction SilentlyContinue

    return [ordered]@{
        archivePath = $archivePath
        itemCount = $itemCount
        eventCount = $eventCount
        products = @($productResults)
    }
}

function Remove-TimelineAudioGeneratedArtifacts {
    param([object]$Request)

    $itemIds = @()
    foreach ($itemId in @(Get-PropertyValue -Object $Request -Name "itemIds" -Default @())) {
        $text = Convert-TimelineText -Value $itemId
        if ($text) {
            $itemIds += $text
        }
    }

    $identities = @()
    foreach ($identity in @(Get-PropertyValue -Object $Request -Name "sourceFileIdentities" -Default @())) {
        $text = Convert-TimelineText -Value $identity
        if ($text) {
            $identities += $text
        }
    }
    if ($identities.Count -eq 0) {
        $identities = @($itemIds)
    }
    if ($itemIds.Count -eq 0 -and $identities.Count -eq 0) {
        throw "No audio files were selected for generated artifact deletion."
    }

    $uniqueItemIds = @(
        $itemIds |
            Where-Object { $_ -and -not ([string]$_).Contains(":") } |
            Select-Object -Unique
    )
    if ($uniqueItemIds.Count -eq 0) {
        return Convert-TimelineAudioDeleteGeneratedResult `
            -Payload ([ordered]@{
                dry_run = [bool](Get-PropertyValue -Object $Request -Name "dryRun" -Default $false)
                requested_item_ids = @()
                requested_source_file_identities = @($identities | Select-Object -Unique)
                matched_count = 0
                missing_item_ids = @()
                catalog_rows_removed = 0
                media_dirs_removed = 0
                media_dirs = @()
                unsafe_media_dirs = @()
            }) `
            -RequestedItemIds @() `
            -RequestedSourceFileIdentities $identities
    }

    $args = @("items", "remove", "--item-id", ($uniqueItemIds -join ","), "--json")
    if ([bool](Get-PropertyValue -Object $Request -Name "dryRun" -Default $false)) {
        $args += "--dry-run"
    }
    $payload = Invoke-TimelineAudioCliJson -CliArgs $args -TimeoutSeconds 60
    return Convert-TimelineAudioDeleteGeneratedResult `
        -Payload $payload `
        -RequestedItemIds $uniqueItemIds `
        -RequestedSourceFileIdentities $identities
}

function Get-TimelineAudioOutputRoot {
    param([object]$Settings)

    $outputRoot = Get-PropertyValue -Object $Settings -Name "outputRoot" -Default $null
    if ($null -eq $outputRoot) {
        $outputRoot = @($Settings.outputRoots) | Select-Object -First 1
    }
    return $outputRoot
}

function Get-TimelineAudioOutputRootPath {
    param([object]$Settings)

    $outputRoot = Get-TimelineAudioOutputRoot -Settings $Settings
    if ($null -eq $outputRoot) {
        return ""
    }
    return [string](Get-PropertyValue -Object $outputRoot -Name "path" -Default "")
}

function Get-TimelineNormalizedPathKey {
    param([string]$Path)

    return ([string]$Path).Trim().TrimEnd('\', '/').Replace('/', '\').ToLowerInvariant()
}

function Get-TimelineAudioExtensions {
    param([object]$Settings)

    $extensions = @($Settings.audioExtensions | ForEach-Object {
        $text = ([string]$_).Trim().ToLowerInvariant()
        if (-not $text) {
            return
        }
        if ($text.StartsWith(".")) { $text } else { ".$text" }
    })
    if ($extensions.Count -eq 0) {
        return @(".mp3", ".wav", ".m4a", ".aac", ".flac")
    }
    return $extensions
}

function Resolve-TimelineAudioSourceFile {
    param(
        [object]$Settings,
        [string]$SourceId,
        [string]$RelativePath
    )

    $sourceIdText = ([string]$SourceId).Trim()
    $relativeText = ([string]$RelativePath).Trim().Replace('/', '\').TrimStart('\', '/')
    if (-not $sourceIdText -or -not $relativeText) {
        return $null
    }

    $extensions = @(Get-TimelineAudioExtensions -Settings $Settings)
    foreach ($root in @($Settings.inputRoots)) {
        if (-not [bool]$root.enabled -or -not [string]$root.path) {
            continue
        }

        $rootPathText = [string]$root.path
        $rootMatches = [string]$root.id -eq $sourceIdText `
            -or [string]$root.path -eq $sourceIdText `
            -or (Get-TimelineNormalizedPathKey -Path ([string]$root.path)) -eq (Get-TimelineNormalizedPathKey -Path $sourceIdText)
        if (-not $rootMatches) {
            continue
        }
        if (-not (Test-Path -LiteralPath $rootPathText)) {
            continue
        }

        $rootPath = (Resolve-Path -LiteralPath $rootPathText).Path
        $candidate = Join-Path $rootPath $relativeText
        if (-not (Test-Path -LiteralPath $candidate -PathType Leaf)) {
            continue
        }

        $resolvedCandidate = (Resolve-Path -LiteralPath $candidate).Path
        $rootPrefix = (Get-TimelineNormalizedPathKey -Path $rootPath) + "\"
        $candidateKey = Get-TimelineNormalizedPathKey -Path $resolvedCandidate
        if (-not $candidateKey.StartsWith($rootPrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
            continue
        }

        $extension = [System.IO.Path]::GetExtension($resolvedCandidate).ToLowerInvariant()
        if ($extensions -notcontains $extension) {
            continue
        }

        $file = Get-Item -LiteralPath $resolvedCandidate
        return [ordered]@{
            root = $root
            rootPath = $rootPath
            file = $file
            relativePath = $relativeText.Replace('\', '/')
            sourceId = [string]$root.path
            sourceFileIdentity = "$([string]$root.path)::" + $relativeText.Replace('\', '/')
        }
    }

    return $null
}

function Read-TimelineAudioJsonFile {
    param([string]$Path)

    if (-not $Path -or -not (Test-Path -LiteralPath $Path)) {
        return $null
    }
    try {
        return Get-Content -LiteralPath $Path -Raw -Encoding UTF8 | ConvertFrom-Json
    }
    catch {
        return $null
    }
}

function Find-TimelineAudioItemDirectory {
    param(
        [string]$OutputRootPath,
        [string]$SourceFileIdentity
    )

    if (-not $OutputRootPath -or -not (Test-Path -LiteralPath $OutputRootPath)) {
        return $null
    }

    $targetIdentity = ([string]$SourceFileIdentity).Trim()
    if (-not $targetIdentity) {
        return $null
    }

    foreach ($dir in Get-ChildItem -LiteralPath $OutputRootPath -Directory -ErrorAction SilentlyContinue) {
        $timelinePath = Join-Path $dir.FullName "timeline.json"
        if (-not (Test-Path -LiteralPath $timelinePath)) {
            continue
        }
        $timeline = Read-TimelineAudioJsonFile -Path $timelinePath
        if ($null -eq $timeline) {
            continue
        }
        $source = Get-PropertyValue -Object $timeline -Name "source" -Default $null
        $identity = Convert-TimelineText -Value (Get-PropertyValue -Object $source -Name "source_file_identity" -Default "")
        if ($identity -and $identity.Equals($targetIdentity, [System.StringComparison]::OrdinalIgnoreCase)) {
            return $dir
        }
    }

    return $null
}

function Convert-TimelineAudioTurn {
    param([object]$Turn)

    return [ordered]@{
        index = Convert-TimelineAudioInt -Value (Get-PropertyValue -Object $Turn -Name "index" -Default 0)
        startSec = Convert-TimelineAudioNumber -Value (Get-PropertyValueAny -Object $Turn -Names @("start_sec", "startSec") -Default 0)
        endSec = Convert-TimelineAudioNumber -Value (Get-PropertyValueAny -Object $Turn -Names @("end_sec", "endSec") -Default 0)
        absoluteStartAt = Convert-TimelineText -Value (Get-PropertyValueAny -Object $Turn -Names @("absolute_start_at", "absoluteStartAt") -Default "")
        absoluteEndAt = Convert-TimelineText -Value (Get-PropertyValueAny -Object $Turn -Names @("absolute_end_at", "absoluteEndAt") -Default "")
        speaker = Convert-TimelineText -Value (Get-PropertyValue -Object $Turn -Name "speaker" -Default "")
        phoneTokens = Convert-TimelineText -Value (Get-PropertyValueAny -Object $Turn -Names @("phone_tokens", "phoneTokens", "acoustic_units", "acousticUnits") -Default "")
        unitType = Convert-TimelineText -Value (Get-PropertyValueAny -Object $Turn -Names @("unit_type", "unitType") -Default "")
        confidence = Get-PropertyValue -Object $Turn -Name "confidence" -Default $null
    }
}

function Get-TimelineAudioFileDetail {
    param(
        [string]$SourceId,
        [string]$RelativePath
    )

    $settings = Read-TimelineAudioSettings
    $source = Resolve-TimelineAudioSourceFile -Settings $settings -SourceId $SourceId -RelativePath $RelativePath
    if ($null -eq $source) {
        return [ordered]@{
            available = $false
            message = "Audio source file was not found."
            file = $null
            timelineAvailable = $false
            audioAvailable = $false
            audioUrl = ""
            turns = @()
        }
    }

    $file = $source.file
    $outputRootPath = Get-TimelineAudioOutputRootPath -Settings $settings
    $itemDirectory = Find-TimelineAudioItemDirectory -OutputRootPath $outputRootPath -SourceFileIdentity ([string]$source.sourceFileIdentity)
    $timelinePath = if ($null -ne $itemDirectory) { Join-Path $itemDirectory.FullName "timeline.json" } else { "" }
    $convertInfoPath = if ($null -ne $itemDirectory) { Join-Path $itemDirectory.FullName "convert_info.json" } else { "" }
    $timeline = Read-TimelineAudioJsonFile -Path $timelinePath
    $timelineAvailable = $null -ne $timeline
    $turns = @()
    $speakerSet = @{}
    $unitType = ""
    $pipelineVersion = ""
    if ($timelineAvailable) {
        $pipeline = Get-PropertyValue -Object $timeline -Name "pipeline" -Default $null
        $pipelineVersion = Convert-TimelineText -Value (Get-PropertyValue -Object $pipeline -Name "pipeline_version" -Default "")
        foreach ($turn in @(Get-PropertyValue -Object $timeline -Name "turns" -Default @())) {
            $converted = Convert-TimelineAudioTurn -Turn $turn
            if (-not $unitType) {
                $unitType = [string]$converted.unitType
            }
            if ([string]$converted.speaker) {
                $speakerSet[[string]$converted.speaker] = $true
            }
            $turns += $converted
        }
    }

    $relativeForUrl = [string]$source.relativePath
    $sourceIdForUrl = [System.Uri]::EscapeDataString([string]$source.sourceId)
    $pathForUrl = [System.Uri]::EscapeDataString($relativeForUrl)
    $audioUrl = "http://127.0.0.1:{0}/products/audio/files/source?sourceId={1}&path={2}" -f $Port, $sourceIdForUrl, $pathForUrl

    return [ordered]@{
        available = $true
        message = ""
        file = [ordered]@{
            itemId = if ($null -ne $itemDirectory) { $itemDirectory.Name } else { [string]$source.sourceFileIdentity }
            sourceId = [string]$source.sourceId
            sourceFileIdentity = [string]$source.sourceFileIdentity
            sourceDisplayName = [string]$source.root.displayName
            sourceName = [string]$source.root.displayName
            rootPath = [string]$source.rootPath
            displayPath = $file.FullName
            relativePath = $relativeForUrl
            directory = [System.IO.Path]::GetDirectoryName($relativeForUrl)
            fileName = $file.Name
            sizeBytes = [int64]$file.Length
            modifiedAt = $file.LastWriteTime.ToString("yyyy-MM-dd HH:mm:ss")
            status = if ($timelineAvailable) { "completed" } else { "detected" }
            durationSec = Convert-TimelineAudioNumber -Value (Get-PropertyValue -Object (Get-PropertyValue -Object $timeline -Name "source" -Default $null) -Name "duration_sec" -Default $null)
            hasTimeline = $timelineAvailable
            hasAudio = $true
            runId = ""
            mediaId = if ($null -ne $itemDirectory) { $itemDirectory.Name } else { "" }
            turnCount = @($turns).Count
            speakerCount = $speakerSet.Count
        }
        timelineAvailable = $timelineAvailable
        audioAvailable = $true
        audioUrl = $audioUrl
        timelinePath = $timelinePath
        convertInfoPath = $convertInfoPath
        pipelineVersion = $pipelineVersion
        unitType = $unitType
        turns = @($turns)
    }
}

function Get-TimelineAudioMimeType {
    param([string]$Path)

    switch ([System.IO.Path]::GetExtension($Path).ToLowerInvariant()) {
        ".mp3" { return "audio/mpeg" }
        ".wav" { return "audio/wav" }
        ".m4a" { return "audio/mp4" }
        ".aac" { return "audio/aac" }
        ".flac" { return "audio/flac" }
        default { return "application/octet-stream" }
    }
}

function Get-TimelineAudioCatalogByIdentity {
    param([object]$Settings)

    $rows = @{}
    $outputRootPath = Get-TimelineAudioOutputRootPath -Settings $Settings
    if (-not $outputRootPath) {
        return $rows
    }

    $catalogPath = Join-Path $outputRootPath ".timeline-for-audio\catalog.jsonl"
    if (-not (Test-Path -LiteralPath $catalogPath)) {
        return $rows
    }

    foreach ($line in Get-Content -LiteralPath $catalogPath -ErrorAction SilentlyContinue) {
        if (-not ([string]$line).Trim()) {
            continue
        }

        try {
            $row = $line | ConvertFrom-Json
            $identity = [string](Get-PropertyValue -Object $row -Name "source_file_identity" -Default "")
            if ($identity) {
                $rows[$identity] = $row
            }
        }
        catch {
        }
    }

    return $rows
}

function Get-TimelineAudioMediaDirectory {
    param(
        [string]$OutputRootPath,
        [object]$CatalogRow
    )

    if (-not $OutputRootPath -or $null -eq $CatalogRow) {
        return ""
    }

    $runId = [string](Get-PropertyValue -Object $CatalogRow -Name "run_id" -Default "")
    $mediaId = [string](Get-PropertyValue -Object $CatalogRow -Name "audio_id" -Default (Get-PropertyValue -Object $CatalogRow -Name "media_id" -Default ""))
    if (-not $runId -or -not $mediaId) {
        return ""
    }

    $runDir = Join-Path $OutputRootPath $runId
    $mediaRoot = Join-Path $runDir "media"
    return Join-Path $mediaRoot $mediaId
}

function Get-TimelineAudioArtifactSummary {
    param([string]$MediaDirectory)

    $summary = [ordered]@{
        hasTimeline = $false
        hasAudio = $false
        turnCount = 0
        speakerCount = 0
    }
    if (-not $MediaDirectory) {
        return $summary
    }

    $summary.hasAudio = Test-Path -LiteralPath (Join-Path $MediaDirectory "source\audio-normalized.wav")

    $timelinePath = Join-Path $MediaDirectory "timeline\speaker-acoustic-units-timeline.json"
    if (-not (Test-Path -LiteralPath $timelinePath)) {
        return $summary
    }

    $summary.hasTimeline = $true
    $raw = ""
    try {
        $raw = Get-Content -LiteralPath $timelinePath -Raw
        $payload = $raw | ConvertFrom-Json
        $turnCount = Convert-TimelineAudioInt -Value (Get-PropertyValue -Object $payload -Name "turn_count" -Default $null)
        $turns = @(Get-PropertyValue -Object $payload -Name "turns" -Default @())
        if ($turnCount -le 0) {
            $turnCount = $turns.Count
        }
        $summary.turnCount = $turnCount

        $speakers = @{}
        foreach ($turn in $turns) {
            $speaker = [string](Get-PropertyValue -Object $turn -Name "speaker" -Default (Get-PropertyValue -Object $turn -Name "speaker_label" -Default ""))
            if ($speaker) {
                $speakers[$speaker] = $true
            }
        }

        $summary.speakerCount = $speakers.Count
    }
    catch {
        if ($raw -match '"turn_count"\s*:\s*(\d+)') {
            $summary.turnCount = Convert-TimelineAudioInt -Value $Matches[1]
        }

        $speakers = @{}
        foreach ($match in [System.Text.RegularExpressions.Regex]::Matches($raw, '"speaker"\s*:\s*"([^"]+)"')) {
            $speaker = [string]$match.Groups[1].Value
            if ($speaker) {
                $speakers[$speaker] = $true
            }
        }
        $summary.speakerCount = $speakers.Count
    }

    return $summary
}

function Convert-TimelineAudioFilesResult {
    param(
        [object]$Payload,
        [object]$ItemsPayload = @()
    )

    $payloadFiles = Get-PropertyValueAny -Object $Payload -Names @("files", "items") -Default $null
    $rowsSource = if ($null -ne $payloadFiles) { @($payloadFiles) } else { @($Payload) }
    $rowsSourceCount = @($rowsSource).Count
    $total = Convert-TimelineAudioInt -Value (Get-PropertyValueAny -Object $Payload -Names @("total", "total_files", "totalFiles", "file_count", "fileCount") -Default $rowsSourceCount)
    if ($total -le 0) {
        $total = $rowsSourceCount
    }

    $itemsByIdentity = @{}
    foreach ($item in @($ItemsPayload)) {
        $identity = Convert-TimelineText -Value (Get-PropertyValueAny -Object $item -Names @("source_file_identity", "sourceFileIdentity") -Default "")
        if ($identity) {
            $itemsByIdentity[$identity] = $item
        }
    }

    $rows = @()
    foreach ($row in @($rowsSource)) {
        $sourceFileIdentity = Convert-TimelineText -Value (Get-PropertyValueAny -Object $row -Names @("source_file_identity", "sourceFileIdentity") -Default "")
        $itemId = Convert-TimelineText -Value (Get-PropertyValueAny -Object $row -Names @("item_id", "itemId") -Default "")
        $managedItem = $null
        if ($sourceFileIdentity -and $itemsByIdentity.ContainsKey($sourceFileIdentity)) {
            $managedItem = $itemsByIdentity[$sourceFileIdentity]
        }
        if (-not $itemId -and $null -ne $managedItem) {
            $itemId = Convert-TimelineText -Value (Get-PropertyValueAny -Object $managedItem -Names @("item_id", "itemId") -Default "")
        }
        $mediaId = Convert-TimelineText -Value (Get-PropertyValueAny -Object $row -Names @("media_id", "mediaId", "audio_id", "audioId") -Default "")
        if (-not $itemId -and $mediaId) {
            $itemId = $mediaId
        }

        $sourceDisplayName = Convert-TimelineText -Value (Get-PropertyValueAny -Object $row -Names @("source_display_name", "sourceDisplayName", "source_name", "sourceName") -Default "")
        $sourceId = Convert-TimelineText -Value (Get-PropertyValueAny -Object $row -Names @("source_id", "sourceId") -Default "")
        if (-not $sourceDisplayName) {
            $sourceDisplayName = $sourceId
        }

        $rows += [ordered]@{
            itemId = $itemId
            sourceId = $sourceId
            sourceFileIdentity = $sourceFileIdentity
            sourceDisplayName = $sourceDisplayName
            sourceName = $sourceDisplayName
            rootPath = Convert-TimelineText -Value (Get-PropertyValueAny -Object $row -Names @("root_path", "rootPath") -Default "")
            displayPath = Convert-TimelineText -Value (Get-PropertyValueAny -Object $row -Names @("display_path", "displayPath") -Default "")
            relativePath = Convert-TimelineText -Value (Get-PropertyValueAny -Object $row -Names @("relative_path", "relativePath") -Default "")
            directory = Convert-TimelineText -Value (Get-PropertyValue -Object $row -Name "directory" -Default "")
            fileName = Convert-TimelineText -Value (Get-PropertyValueAny -Object $row -Names @("file_name", "fileName") -Default "")
            sizeBytes = [int64](Convert-TimelineAudioNumber -Value (Get-PropertyValueAny -Object $row -Names @("size_bytes", "sizeBytes") -Default 0))
            modifiedAt = Convert-TimelineText -Value (Get-PropertyValueAny -Object $row -Names @("modified_at", "modifiedAt") -Default "")
            status = Convert-TimelineText -Value (Get-PropertyValue -Object $row -Name "status" -Default "unprocessed")
            durationSec = Convert-TimelineAudioNumber -Value (Get-PropertyValueAny -Object $row -Names @("duration_sec", "durationSec") -Default $null)
            hasTimeline = [bool](Get-PropertyValueAny -Object $row -Names @("has_timeline", "hasTimeline") -Default $false)
            hasAudio = [bool](Get-PropertyValueAny -Object $row -Names @("has_audio", "hasAudio") -Default $false)
            runId = Convert-TimelineText -Value (Get-PropertyValueAny -Object $row -Names @("run_id", "runId") -Default "")
            mediaId = $mediaId
            turnCount = Convert-TimelineAudioInt -Value (Get-PropertyValueAny -Object $row -Names @("turn_count", "turnCount") -Default 0)
            speakerCount = Convert-TimelineAudioInt -Value (Get-PropertyValueAny -Object $row -Names @("speaker_count", "speakerCount") -Default 0)
        }
    }

    $payloadTruncated = [bool](Get-PropertyValue -Object $Payload -Name "truncated" -Default $false)
    $pagination = Convert-TimelinePagination `
        -Payload $Payload `
        -TotalNames @("total_files", "totalFiles", "file_count", "fileCount", "total") `
        -ReturnedNames @("returned_files", "returnedFiles", "returned_items", "returnedItems")
    return [ordered]@{
        total = $total
        truncated = ($payloadTruncated -or $total -gt @($rows).Count)
        pagination = $pagination
        files = @($rows)
    }
}

function Get-TimelineAudioFilesFromSettings {
    param(
        [int]$Page = 1,
        [int]$PageSize = 100
    )

    $settings = Read-TimelineAudioSettings
    $outputRootPath = Get-TimelineAudioOutputRootPath -Settings $settings
    $catalogByIdentity = Get-TimelineAudioCatalogByIdentity -Settings $settings
    $extensions = @($settings.audioExtensions | ForEach-Object {
        $text = ([string]$_).Trim().ToLowerInvariant()
        if ($text.StartsWith(".")) { $text } else { ".$text" }
    })
    if ($extensions.Count -eq 0) {
        $extensions = @(".mp3", ".wav", ".m4a", ".aac", ".flac")
    }

    $allRows = @()
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

            $relativePath = $file.FullName
            if ($file.FullName.StartsWith($rootPath, [System.StringComparison]::OrdinalIgnoreCase)) {
                $relativePath = $file.FullName.Substring($rootPath.Length).TrimStart('\', '/')
            }
            $directory = [System.IO.Path]::GetDirectoryName($relativePath)
            if ($null -eq $directory) {
                $directory = ""
            }
            $identityRelativePath = $relativePath.Replace('\', '/')
            $sourceFileIdentity = "$([string]$root.id):$identityRelativePath"
            $catalogRow = $null
            if ($catalogByIdentity.ContainsKey($sourceFileIdentity)) {
                $catalogRow = $catalogByIdentity[$sourceFileIdentity]
            }

            $durationSec = $null
            if ($null -ne $catalogRow) {
                $durationSec = Convert-TimelineAudioNumber -Value (Get-PropertyValue -Object $catalogRow -Name "duration_seconds" -Default (Get-PropertyValue -Object $catalogRow -Name "duration_sec" -Default $null))
            }

            $mediaDirectory = Get-TimelineAudioMediaDirectory -OutputRootPath $outputRootPath -CatalogRow $catalogRow
            $artifactSummary = Get-TimelineAudioArtifactSummary -MediaDirectory $mediaDirectory
            $status = if ([bool]$artifactSummary.hasTimeline) { "completed" } else { "detected" }

            $allRows += [ordered]@{
                itemId = $sourceFileIdentity
                sourceId = [string]$root.id
                sourceFileIdentity = $sourceFileIdentity
                sourceDisplayName = [string]$root.displayName
                sourceName = [string]$root.displayName
                rootPath = [string]$root.path
                displayPath = $file.FullName
                relativePath = $relativePath
                directory = $directory
                fileName = $file.Name
                sizeBytes = [int64]$file.Length
                modifiedAt = $file.LastWriteTime.ToString("yyyy-MM-dd HH:mm:ss")
                status = $status
                durationSec = $durationSec
                hasTimeline = [bool]$artifactSummary.hasTimeline
                hasAudio = [bool]$artifactSummary.hasAudio
                runId = [string](Get-PropertyValue -Object $catalogRow -Name "run_id" -Default "")
                mediaId = [string](Get-PropertyValue -Object $catalogRow -Name "audio_id" -Default (Get-PropertyValue -Object $catalogRow -Name "media_id" -Default ""))
                turnCount = [int]$artifactSummary.turnCount
                speakerCount = [int]$artifactSummary.speakerCount
            }
        }
    }

    $sortedRows = @($allRows | Sort-Object modifiedAt, sourceFileIdentity -Descending)
    $total = $sortedRows.Count
    $effectivePage = [Math]::Max(1, $Page)
    $effectivePageSize = [Math]::Max(1, $PageSize)
    $offset = ($effectivePage - 1) * $effectivePageSize
    $pageRows = @($sortedRows | Select-Object -Skip $offset -First $effectivePageSize)

    return [ordered]@{
        total = $total
        truncated = $false
        pagination = New-TimelinePagination -Page $effectivePage -PageSize $effectivePageSize -TotalItems $total -ReturnedItems $pageRows.Count
        files = @($pageRows)
    }
}

function Get-TimelineAudioFiles {
    param(
        [int]$Page = 1,
        [int]$PageSize = 100
    )

    $payload = Invoke-TimelineAudioCliJson `
        -CliArgs @("files", "list", "--page", ([string][Math]::Max(1, $Page)), "--page-size", ([string][Math]::Max(1, $PageSize)), "--json") `
        -TimeoutSeconds 120
    return Convert-TimelineAudioFilesResult -Payload $payload
}

function Get-TimelineAudioOverview {
    $settings = Read-TimelineAudioSettings
    $outputRoot = Get-TimelineAudioOutputRoot -Settings $settings
    $hardware = Get-TimelineHardwareDevices
    $activeRun = Get-TimelineActiveAudioRun -Settings $settings
    $audioFileCount = Convert-TimelineAudioInt -Value (Get-PropertyValue -Object $activeRun -Name "itemsTotal" -Default 0)
    if ($audioFileCount -le 0) {
        $files = Get-TimelineAudioFiles -Page 1 -PageSize 1
        $audioFileCount = [int]$files.total
    }
    return [ordered]@{
        productFound = (Test-Path -LiteralPath $AudioProductPath)
        productPath = $AudioProductPath
        hasToken = [bool](([string]$settings.huggingfaceToken).Trim())
        tokenPreview = Get-TimelineTokenPreview -Token ([string]$settings.huggingfaceToken)
        computeMode = [string]$settings.computeMode
        cpuDevices = @($hardware.cpuDevices)
        gpuDevices = @($hardware.gpuDevices)
        inputRoots = @($settings.inputRoots)
        outputRoot = $outputRoot
        audioFileCount = $audioFileCount
        workerState = Get-TimelineWorkerState -Settings $settings
        activeRun = $activeRun
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

function Show-TimelineFilePicker {
    param(
        [string]$Title,
        [string]$InitialPath,
        [string]$Filter
    )

    $dialog = [System.Windows.Forms.OpenFileDialog]::new()
    $dialog.Title = if ($Title) { $Title } else { "Select file" }
    $dialog.CheckFileExists = $true
    $dialog.Multiselect = $false
    $dialog.Filter = if ($Filter) { $Filter } else { "All files (*.*)|*.*" }

    if ($InitialPath) {
        if (Test-Path -LiteralPath $InitialPath -PathType Container) {
            $dialog.InitialDirectory = (Resolve-Path -LiteralPath $InitialPath).Path
        }
        elseif (Test-Path -LiteralPath $InitialPath -PathType Leaf) {
            $item = Get-Item -LiteralPath $InitialPath
            $dialog.InitialDirectory = $item.DirectoryName
            $dialog.FileName = $item.Name
        }
    }

    $owner = [System.Windows.Forms.Form]::new()
    $owner.TopMost = $true
    $owner.ShowInTaskbar = $false
    $owner.StartPosition = [System.Windows.Forms.FormStartPosition]::CenterScreen
    $owner.Size = [System.Drawing.Size]::new(1, 1)

    try {
        $result = $dialog.ShowDialog($owner)
        if ($result -eq [System.Windows.Forms.DialogResult]::OK) {
            return [ordered]@{ ok = $true; cancelled = $false; path = $dialog.FileName }
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
    $transferEncoding = ""
    foreach ($line in $lines) {
        if ($line.StartsWith("Content-Length:", [System.StringComparison]::OrdinalIgnoreCase)) {
            [void][int]::TryParse($line.Substring("Content-Length:".Length).Trim(), [ref]$contentLength)
        }
        elseif ($line.StartsWith("Transfer-Encoding:", [System.StringComparison]::OrdinalIgnoreCase)) {
            $transferEncoding = $line.Substring("Transfer-Encoding:".Length).Trim()
        }
    }

    $bodyStart = if ($headerEnd -ge 0) { $headerEnd + 4 } else { $allBytes.Length }
    $bodyBytes = [System.Collections.Generic.List[byte]]::new()
    for ($index = $bodyStart; $index -lt $allBytes.Length; $index += 1) {
        $bodyBytes.Add($allBytes[$index]) | Out-Null
    }

    if ($transferEncoding -match '(^|,\s*)chunked(\s*,|$)') {
        while (-not (Test-TimelineChunkedBodyComplete -Bytes $bodyBytes)) {
            $read = $stream.Read($buffer, 0, $buffer.Length)
            if ($read -le 0) {
                break
            }
            for ($index = 0; $index -lt $read; $index += 1) {
                $bodyBytes.Add($buffer[$index]) | Out-Null
            }
        }
        $bodyBytes = [System.Collections.Generic.List[byte]]::new(
            [byte[]](ConvertFrom-TimelineChunkedBody -Bytes $bodyBytes)
        )
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

function Find-TimelineByteCrlf {
    param(
        [System.Collections.Generic.List[byte]]$Bytes,
        [int]$StartIndex
    )

    for ($index = $StartIndex; $index -lt ($Bytes.Count - 1); $index += 1) {
        if ($Bytes[$index] -eq 13 -and $Bytes[$index + 1] -eq 10) {
            return $index
        }
    }
    return -1
}

function Test-TimelineChunkedBodyComplete {
    param([System.Collections.Generic.List[byte]]$Bytes)

    $offset = 0
    while ($true) {
        $lineEnd = Find-TimelineByteCrlf -Bytes $Bytes -StartIndex $offset
        if ($lineEnd -lt 0) {
            return $false
        }

        $lineBytes = @()
        for ($index = $offset; $index -lt $lineEnd; $index += 1) {
            $lineBytes += $Bytes[$index]
        }
        $sizeText = ([System.Text.Encoding]::ASCII.GetString([byte[]]$lineBytes) -split ';', 2)[0].Trim()
        if (-not $sizeText) {
            return $false
        }

        try {
            $chunkSize = [Convert]::ToInt32($sizeText, 16)
        }
        catch {
            return $false
        }

        $offset = $lineEnd + 2
        if ($chunkSize -eq 0) {
            return $Bytes.Count -ge ($offset + 2)
        }

        $offset += $chunkSize
        if ($Bytes.Count -lt ($offset + 2)) {
            return $false
        }
        if ($Bytes[$offset] -ne 13 -or $Bytes[$offset + 1] -ne 10) {
            return $false
        }
        $offset += 2
    }
}

function ConvertFrom-TimelineChunkedBody {
    param([System.Collections.Generic.List[byte]]$Bytes)

    $decoded = [System.Collections.Generic.List[byte]]::new()
    $offset = 0
    while ($true) {
        $lineEnd = Find-TimelineByteCrlf -Bytes $Bytes -StartIndex $offset
        if ($lineEnd -lt 0) {
            break
        }

        $lineBytes = @()
        for ($index = $offset; $index -lt $lineEnd; $index += 1) {
            $lineBytes += $Bytes[$index]
        }
        $sizeText = ([System.Text.Encoding]::ASCII.GetString([byte[]]$lineBytes) -split ';', 2)[0].Trim()
        if (-not $sizeText) {
            break
        }

        $chunkSize = [Convert]::ToInt32($sizeText, 16)
        $offset = $lineEnd + 2
        if ($chunkSize -eq 0) {
            break
        }

        for ($index = $offset; $index -lt ($offset + $chunkSize); $index += 1) {
            $decoded.Add($Bytes[$index]) | Out-Null
        }
        $offset += $chunkSize + 2
    }

    return $decoded.ToArray()
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
    $headers.Add("Access-Control-Allow-Headers: Content-Type, Range") | Out-Null
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

function Send-TimelineFileResponse {
    param(
        [Parameter(Mandatory = $true)][System.Net.Sockets.TcpClient]$Client,
        [Parameter(Mandatory = $true)][string]$Path,
        [string]$ContentType = "application/octet-stream",
        [string]$Origin = "",
        [string]$RangeHeader = "",
        [string]$DownloadFileName = ""
    )

    $file = Get-Item -LiteralPath $Path
    $fileLength = [int64]$file.Length
    $rangeStart = [int64]0
    $rangeEnd = [int64]($fileLength - 1)
    $isPartial = $false
    if ($fileLength -gt 0 -and ([string]$RangeHeader).Trim() -match '^bytes=(?<start>\d*)-(?<end>\d*)$') {
        $startText = [string]$Matches.start
        $endText = [string]$Matches.end
        try {
            if (-not $startText -and $endText) {
                $suffixLength = [int64]::Parse($endText)
                if ($suffixLength -gt 0) {
                    $rangeStart = [Math]::Max([int64]0, $fileLength - $suffixLength)
                    $rangeEnd = $fileLength - 1
                    $isPartial = $true
                }
            }
            elseif ($startText) {
                $rangeStart = [int64]::Parse($startText)
                $rangeEnd = if ($endText) { [int64]::Parse($endText) } else { $fileLength - 1 }
                if ($rangeStart -lt $fileLength -and $rangeStart -le $rangeEnd) {
                    $rangeEnd = [Math]::Min($rangeEnd, $fileLength - 1)
                    $isPartial = $true
                }
                else {
                    $rangeStart = [int64]0
                    $rangeEnd = $fileLength - 1
                }
            }
        }
        catch {
            $rangeStart = [int64]0
            $rangeEnd = $fileLength - 1
            $isPartial = $false
        }
    }

    $contentLength = if ($fileLength -gt 0) { [int64]($rangeEnd - $rangeStart + 1) } else { [int64]0 }
    $headers = [System.Collections.Generic.List[string]]::new()
    if ($isPartial) {
        $headers.Add("HTTP/1.1 206 Partial Content") | Out-Null
        $headers.Add("Content-Range: bytes $rangeStart-$rangeEnd/$fileLength") | Out-Null
    }
    else {
        $headers.Add("HTTP/1.1 200 OK") | Out-Null
    }
    $headers.Add("Content-Type: $ContentType") | Out-Null
    $headers.Add("Cache-Control: no-store") | Out-Null
    $headers.Add("Connection: close") | Out-Null
    $headers.Add("Accept-Ranges: bytes") | Out-Null
    if ($DownloadFileName) {
        $safeFileName = ([string]$DownloadFileName).Replace('"', '_').Replace("`r", "_").Replace("`n", "_")
        $headers.Add("Content-Disposition: attachment; filename=""$safeFileName""") | Out-Null
    }
    $headers.Add("Access-Control-Allow-Methods: GET, POST, OPTIONS") | Out-Null
    $headers.Add("Access-Control-Allow-Headers: Content-Type, Range") | Out-Null
    if ($allowedOrigins -contains $Origin) {
        $headers.Add("Access-Control-Allow-Origin: $Origin") | Out-Null
        $headers.Add("Vary: Origin") | Out-Null
    }
    elseif (-not $Origin) {
        $headers.Add("Access-Control-Allow-Origin: *") | Out-Null
    }
    $headers.Add("Content-Length: $contentLength") | Out-Null
    $headers.Add("") | Out-Null
    $headers.Add("") | Out-Null

    $headerBytes = [System.Text.Encoding]::ASCII.GetBytes([string]::Join("`r`n", $headers.ToArray()))
    $stream = $Client.GetStream()
    $stream.Write($headerBytes, 0, $headerBytes.Length)

    $fileStream = [System.IO.File]::OpenRead($file.FullName)
    try {
        if ($rangeStart -gt 0) {
            [void]$fileStream.Seek($rangeStart, [System.IO.SeekOrigin]::Begin)
        }
        $buffer = New-Object byte[] 65536
        $remaining = $contentLength
        while ($remaining -gt 0) {
            $count = [int][Math]::Min([int64]$buffer.Length, [int64]$remaining)
            $read = $fileStream.Read($buffer, 0, $count)
            if ($read -le 0) {
                break
            }
            $stream.Write($buffer, 0, $read)
            $remaining -= $read
        }
    }
    finally {
        $fileStream.Dispose()
    }
}

$listener = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Any, $Port)
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

            if ($method -eq "GET" -and $uri.AbsolutePath -eq "/timeline/settings") {
                Send-TimelineResponse -Client $client -StatusCode 200 -StatusText "OK" -Origin $origin -Body (ConvertTo-TimelineJson (Read-TimelineAppSettings))
                continue
            }

            if ($method -eq "POST" -and $uri.AbsolutePath -eq "/timeline/settings") {
                $payload = if ([string]$request.Body) { $request.Body | ConvertFrom-Json } else { @{} }
                Send-TimelineResponse -Client $client -StatusCode 200 -StatusText "OK" -Origin $origin -Body (ConvertTo-TimelineJson (Write-TimelineAppSettings -Request $payload))
                continue
            }

            if ($method -eq "POST" -and $uri.AbsolutePath -eq "/timeline/export/download") {
                Send-TimelineResponse -Client $client -StatusCode 200 -StatusText "OK" -Origin $origin -Body (ConvertTo-TimelineJson (New-TimelineExportDownload))
                continue
            }

            if ($method -eq "GET" -and $uri.AbsolutePath -eq "/downloads/file") {
                $query = [System.Web.HttpUtility]::ParseQueryString($uri.Query)
                $path = Convert-TimelineDownloadLocalPath -Path ([string]$query["path"])
                if (-not (Test-TimelineDownloadFileAllowed -Path $path)) {
                    Send-TimelineResponse -Client $client -StatusCode 404 -StatusText "Not Found" -Origin $origin -Body (ConvertTo-TimelineJson @{ ok = $false; message = "Download file was not found." })
                    continue
                }
                $rangeHeader = Get-TimelineHeader -Lines $lines -Name "Range"
                Send-TimelineFileResponse `
                    -Client $client `
                    -Path $path `
                    -ContentType "application/zip" `
                    -Origin $origin `
                    -RangeHeader $rangeHeader `
                    -DownloadFileName ([System.IO.Path]::GetFileName($path))
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

            if ($method -eq "GET" -and $uri.AbsolutePath -eq "/pick-file") {
                $query = [System.Web.HttpUtility]::ParseQueryString($uri.Query)
                $payload = Show-TimelineFilePicker `
                    -Title ([string]$query["title"]) `
                    -InitialPath ([string]$query["initialPath"]) `
                    -Filter ([string]$query["filter"])
                Send-TimelineResponse -Client $client -StatusCode 200 -StatusText "OK" -Origin $origin -Body (ConvertTo-TimelineJson $payload)
                continue
            }

            if ($method -eq "GET" -and $uri.AbsolutePath -eq "/products/audio/overview") {
                Send-TimelineResponse -Client $client -StatusCode 200 -StatusText "OK" -Origin $origin -Body (ConvertTo-TimelineJson (Get-TimelineAudioOverview))
                continue
            }

            if ($method -eq "GET" -and $uri.AbsolutePath -eq "/products/runtime/status") {
                Send-TimelineResponse -Client $client -StatusCode 200 -StatusText "OK" -Origin $origin -Body (ConvertTo-TimelineJson (Get-TimelineRuntimeOverview))
                continue
            }

            if ($method -eq "POST" -and $uri.AbsolutePath -like "/products/runtime/*/start") {
                $segments = @($uri.AbsolutePath.Trim("/") -split "/")
                $productId = [System.Uri]::UnescapeDataString([string]$segments[2])
                Send-TimelineResponse -Client $client -StatusCode 200 -StatusText "OK" -Origin $origin -Body (ConvertTo-TimelineJson (Invoke-TimelineProductStart -ProductId $productId))
                continue
            }

            if ($method -eq "POST" -and $uri.AbsolutePath -like "/products/runtime/*/restart") {
                $segments = @($uri.AbsolutePath.Trim("/") -split "/")
                $productId = [System.Uri]::UnescapeDataString([string]$segments[2])
                Send-TimelineResponse -Client $client -StatusCode 200 -StatusText "OK" -Origin $origin -Body (ConvertTo-TimelineJson (Invoke-TimelineProductStart -ProductId $productId -Restart))
                continue
            }

            if ($method -eq "GET" -and $uri.AbsolutePath -eq "/products/windows-codex/overview") {
                Send-TimelineResponse -Client $client -StatusCode 200 -StatusText "OK" -Origin $origin -Body (ConvertTo-TimelineJson (Get-TimelineWindowsCodexOverview))
                continue
            }

            if ($method -eq "GET" -and $uri.AbsolutePath -eq "/products/windows-codex/items") {
                $query = [System.Web.HttpUtility]::ParseQueryString($uri.Query)
                Send-TimelineResponse -Client $client -StatusCode 200 -StatusText "OK" -Origin $origin -Body (ConvertTo-TimelineJson (Get-TimelineWindowsCodexThreads -Page (Get-TimelineRequestPage -Query $query) -PageSize (Get-TimelineRequestPageSize -Query $query)))
                continue
            }

            if ($method -eq "GET" -and $uri.AbsolutePath.StartsWith("/products/windows-codex/threads/", [System.StringComparison]::OrdinalIgnoreCase)) {
                $segments = @($uri.AbsolutePath.Trim("/") -split "/")
                $itemId = if ($segments.Count -ge 4) { [System.Uri]::UnescapeDataString([string]$segments[3]) } else { "" }
                Send-TimelineResponse -Client $client -StatusCode 200 -StatusText "OK" -Origin $origin -Body (ConvertTo-TimelineJson (Get-TimelineWindowsCodexThreadDetail -ItemId $itemId))
                continue
            }

            if ($method -eq "POST" -and $uri.AbsolutePath -eq "/products/windows-codex/refresh") {
                Send-TimelineResponse -Client $client -StatusCode 200 -StatusText "OK" -Origin $origin -Body (ConvertTo-TimelineJson (Start-TimelineWindowsCodexRefresh))
                continue
            }

            if ($method -eq "POST" -and $uri.AbsolutePath -eq "/products/windows-codex/items/download") {
                $payload = if ([string]$request.Body) { $request.Body | ConvertFrom-Json } else { @{} }
                Send-TimelineResponse -Client $client -StatusCode 200 -StatusText "OK" -Origin $origin -Body (ConvertTo-TimelineJson (Start-TimelineWindowsCodexDownload -Request $payload))
                continue
            }

            if ($method -eq "POST" -and $uri.AbsolutePath -eq "/products/windows-codex/items/delete-generated") {
                $payload = if ([string]$request.Body) { $request.Body | ConvertFrom-Json } else { @{} }
                Send-TimelineResponse -Client $client -StatusCode 200 -StatusText "OK" -Origin $origin -Body (ConvertTo-TimelineJson (Remove-TimelineWindowsCodexItems -Request $payload))
                continue
            }

            if ($method -eq "POST" -and $uri.AbsolutePath -eq "/products/windows-codex/settings") {
                $payload = if ([string]$request.Body) { $request.Body | ConvertFrom-Json } else { @{} }
                Send-TimelineResponse -Client $client -StatusCode 200 -StatusText "OK" -Origin $origin -Body (ConvertTo-TimelineJson (Write-TimelineWindowsCodexSettings -Request $payload))
                continue
            }

            if ($method -eq "GET" -and $uri.AbsolutePath -eq "/products/chatgpt/overview") {
                Send-TimelineResponse -Client $client -StatusCode 200 -StatusText "OK" -Origin $origin -Body (ConvertTo-TimelineJson (Get-TimelineChatGptOverview))
                continue
            }

            if ($method -eq "GET" -and $uri.AbsolutePath -eq "/products/chatgpt/items") {
                $query = [System.Web.HttpUtility]::ParseQueryString($uri.Query)
                Send-TimelineResponse -Client $client -StatusCode 200 -StatusText "OK" -Origin $origin -Body (ConvertTo-TimelineJson (Get-TimelineChatGptThreads -Page (Get-TimelineRequestPage -Query $query) -PageSize (Get-TimelineRequestPageSize -Query $query)))
                continue
            }

            if ($method -eq "GET" -and $uri.AbsolutePath.StartsWith("/products/chatgpt/threads/", [System.StringComparison]::OrdinalIgnoreCase)) {
                $segments = @($uri.AbsolutePath.Trim("/") -split "/")
                $itemId = if ($segments.Count -ge 4) { [System.Uri]::UnescapeDataString([string]$segments[3]) } else { "" }
                Send-TimelineResponse -Client $client -StatusCode 200 -StatusText "OK" -Origin $origin -Body (ConvertTo-TimelineJson (Get-TimelineChatGptThreadDetail -ItemId $itemId))
                continue
            }

            if ($method -eq "POST" -and $uri.AbsolutePath -eq "/products/chatgpt/refresh") {
                $payload = if ([string]$request.Body) { $request.Body | ConvertFrom-Json } else { @{} }
                Send-TimelineResponse -Client $client -StatusCode 200 -StatusText "OK" -Origin $origin -Body (ConvertTo-TimelineJson (Start-TimelineChatGptRefresh -Request $payload))
                continue
            }

            if ($method -eq "POST" -and $uri.AbsolutePath -eq "/products/chatgpt/items/download") {
                $payload = if ([string]$request.Body) { $request.Body | ConvertFrom-Json } else { @{} }
                Send-TimelineResponse -Client $client -StatusCode 200 -StatusText "OK" -Origin $origin -Body (ConvertTo-TimelineJson (Start-TimelineChatGptDownload -Request $payload))
                continue
            }

            if ($method -eq "POST" -and $uri.AbsolutePath -eq "/products/chatgpt/items/delete-generated") {
                $payload = if ([string]$request.Body) { $request.Body | ConvertFrom-Json } else { @{} }
                Send-TimelineResponse -Client $client -StatusCode 200 -StatusText "OK" -Origin $origin -Body (ConvertTo-TimelineJson (Remove-TimelineChatGptItems -Request $payload))
                continue
            }

            if ($method -eq "POST" -and $uri.AbsolutePath -eq "/products/chatgpt/settings") {
                $payload = if ([string]$request.Body) { $request.Body | ConvertFrom-Json } else { @{} }
                Send-TimelineResponse -Client $client -StatusCode 200 -StatusText "OK" -Origin $origin -Body (ConvertTo-TimelineJson (Write-TimelineChatGptSettings -Request $payload))
                continue
            }

            if ($method -eq "GET" -and $uri.AbsolutePath -eq "/products/audio/files") {
                $query = [System.Web.HttpUtility]::ParseQueryString($uri.Query)
                Send-TimelineResponse -Client $client -StatusCode 200 -StatusText "OK" -Origin $origin -Body (ConvertTo-TimelineJson (Get-TimelineAudioFiles -Page (Get-TimelineRequestPage -Query $query) -PageSize (Get-TimelineRequestPageSize -Query $query)))
                continue
            }

            if ($method -eq "GET" -and $uri.AbsolutePath -eq "/products/audio/files/detail") {
                $query = [System.Web.HttpUtility]::ParseQueryString($uri.Query)
                Send-TimelineResponse -Client $client -StatusCode 200 -StatusText "OK" -Origin $origin -Body (ConvertTo-TimelineJson (Get-TimelineAudioFileDetail -SourceId ([string]$query["sourceId"]) -RelativePath ([string]$query["path"])))
                continue
            }

            if ($method -eq "GET" -and $uri.AbsolutePath -eq "/products/audio/files/source") {
                $query = [System.Web.HttpUtility]::ParseQueryString($uri.Query)
                $settings = Read-TimelineAudioSettings
                $source = Resolve-TimelineAudioSourceFile -Settings $settings -SourceId ([string]$query["sourceId"]) -RelativePath ([string]$query["path"])
                if ($null -eq $source) {
                    Send-TimelineResponse -Client $client -StatusCode 404 -StatusText "Not Found" -Origin $origin -Body (ConvertTo-TimelineJson @{ ok = $false; message = "Audio source was not found." })
                    continue
                }
                $rangeHeader = Get-TimelineHeader -Lines $lines -Name "Range"
                Send-TimelineFileResponse -Client $client -Path ([string]$source.file.FullName) -ContentType (Get-TimelineAudioMimeType -Path ([string]$source.file.FullName)) -Origin $origin -RangeHeader $rangeHeader
                continue
            }

            if ($method -eq "GET" -and $uri.AbsolutePath -eq "/products/audio/models") {
                Send-TimelineResponse -Client $client -StatusCode 200 -StatusText "OK" -Origin $origin -Body (ConvertTo-TimelineJson (Get-TimelineAudioModels))
                continue
            }

            if ($method -eq "POST" -and $uri.AbsolutePath -eq "/products/audio/files/delete-generated") {
                $payload = if ([string]$request.Body) { $request.Body | ConvertFrom-Json } else { @{} }
                Send-TimelineResponse -Client $client -StatusCode 200 -StatusText "OK" -Origin $origin -Body (ConvertTo-TimelineJson (Remove-TimelineAudioGeneratedArtifacts -Request $payload))
                continue
            }

            if ($method -eq "POST" -and $uri.AbsolutePath -eq "/products/audio/refresh") {
                $payload = if ([string]$request.Body) { $request.Body | ConvertFrom-Json } else { @{} }
                Send-TimelineResponse -Client $client -StatusCode 200 -StatusText "OK" -Origin $origin -Body (ConvertTo-TimelineJson (Start-TimelineAudioRefresh -Request $payload))
                continue
            }

            if ($method -eq "POST" -and $uri.AbsolutePath -eq "/products/audio/items/download") {
                $payload = if ([string]$request.Body) { $request.Body | ConvertFrom-Json } else { @{} }
                Send-TimelineResponse -Client $client -StatusCode 200 -StatusText "OK" -Origin $origin -Body (ConvertTo-TimelineJson (New-TimelineAudioItemsDownload -Request $payload))
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
