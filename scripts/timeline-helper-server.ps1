[CmdletBinding()]
param(
    [int]$Port = 19001,
    [string]$TimelineProductPath = "C:\apps\Timeline",
    [string]$AudioProductPath = "C:\apps\TimelineForAudio",
    [string]$WindowsCodexProductPath = "C:\apps\TimelineForWindowsCodex",
    [string]$ChatGptProductPath = "C:\apps\TimelineForChatGPT",
    [string]$ImageProductPath = "C:\apps\TimelineForImage",
    [switch]$ImportOnly
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
$script:TimelineConsoleLogEntries = [System.Collections.Generic.List[object]]::new()
$script:TimelineConsoleLogNextId = [long]0
$script:TimelineConsoleLogLimit = 300

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

function New-TimelineDefaultAudioVerbalizationSettings {
    return [ordered]@{
        enabled = $true
        provider = "ollama"
        ollamaBaseUrl = "http://127.0.0.1:11434"
        model = "qwen3.5:9b"
        fastModel = "qwen3.5:4b"
        language = "ja-JP"
        chunkMinMinutes = 5
        chunkMaxMinutes = 10
        chunkMaxTurns = 80
        nearbyContextMinutes = 10
        maxConcurrentJobs = 1
        autoRun = $false
        usePreviousChunkSummary = $true
        useUnconfirmedVerbalizationAsWeakHint = $true
    }
}

function Resolve-TimelineInternalAudioVerbalizationSettings {
    param([string]$DisplayLanguageId)

    $settings = New-TimelineDefaultAudioVerbalizationSettings
    $language = Convert-TimelineText -Value $DisplayLanguageId
    if (-not $language) {
        $language = "ja-JP"
    }

    $settings["enabled"] = $true
    $settings["provider"] = "ollama"
    $settings["ollamaBaseUrl"] = "http://127.0.0.1:11434"
    $settings["model"] = "qwen3.5:9b"
    $settings["fastModel"] = "qwen3.5:4b"
    $settings["language"] = $language
    $settings["chunkMinMinutes"] = 5
    $settings["chunkMaxMinutes"] = 10
    $settings["chunkMaxTurns"] = 80
    $settings["nearbyContextMinutes"] = 10
    $settings["maxConcurrentJobs"] = 1
    $settings["autoRun"] = $false
    $settings["usePreviousChunkSummary"] = $true
    $settings["useUnconfirmedVerbalizationAsWeakHint"] = $true
    return $settings
}

function Read-TimelineAppSettings {
    $path = Get-TimelineAppSettingsPath
    $displayLanguageId = "ja-JP"
    $timeZoneId = "Asia/Tokyo"
    $workDirectory = "C:\TimelineData\Timeline\work"
    $storeDirectory = "C:\TimelineData\Timeline\store"
    $audioVerbalization = New-TimelineDefaultAudioVerbalizationSettings
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
            $storeDirectoryCandidate = Convert-TimelineText -Value (Get-PropertyValue -Object $payload -Name "storeDirectory" -Default "")
            if ($storeDirectoryCandidate) {
                $storeDirectory = $storeDirectoryCandidate
            }
        }
        catch {
            $displayLanguageId = "ja-JP"
            $timeZoneId = "Asia/Tokyo"
            $workDirectory = "C:\TimelineData\Timeline\work"
            $storeDirectory = "C:\TimelineData\Timeline\store"
            $audioVerbalization = New-TimelineDefaultAudioVerbalizationSettings
        }
    }

    $allowedLanguages = @(Get-TimelineDisplayLanguageOptions | ForEach-Object { [string]$_.id })
    if ($allowedLanguages -notcontains $displayLanguageId) {
        $displayLanguageId = "ja-JP"
    }
    $audioVerbalization = Resolve-TimelineInternalAudioVerbalizationSettings -DisplayLanguageId $displayLanguageId

    return [ordered]@{
        schemaVersion = 1
        displayLanguageId = $displayLanguageId
        displayLanguages = @(Get-TimelineDisplayLanguageOptions)
        timeZoneId = $timeZoneId
        timeZones = @(Get-TimelineTimeZoneOptions)
        workDirectory = $workDirectory
        storeDirectory = $storeDirectory
        audioVerbalization = $audioVerbalization
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
    $storeDirectory = Convert-TimelineText -Value (Get-PropertyValue -Object $Request -Name "storeDirectory" -Default "")
    if (-not $storeDirectory) {
        $storeDirectory = Convert-TimelineText -Value (Get-PropertyValue -Object $current -Name "storeDirectory" -Default "C:\TimelineData\Timeline\store")
    }
    $audioVerbalization = Resolve-TimelineInternalAudioVerbalizationSettings -DisplayLanguageId $displayLanguageId

    if (-not (Test-Path -LiteralPath $TimelineProductPath)) {
        [System.IO.Directory]::CreateDirectory($TimelineProductPath) | Out-Null
    }

    $payload = [ordered]@{
        schemaVersion = 1
        displayLanguageId = $displayLanguageId
        timeZoneId = $timeZoneId
        workDirectory = $workDirectory
        storeDirectory = $storeDirectory
        audioVerbalization = $audioVerbalization
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

function Convert-TimelineLong {
    param([object]$Value)

    if ($null -eq $Value) {
        return 0
    }
    try {
        return [long]$Value
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

function Protect-TimelineConsoleText {
    param([string]$Text)

    $value = [string]$Text
    if (-not $value) {
        return ""
    }

    $value = $value -replace 'hf_[A-Za-z0-9_\-=]{8,}', 'hf_****'
    $value = $value -replace '(?i)(--(?:token|hf-token|huggingface-token|api-key|password|secret)\s+)([^\s]+)', '$1[hidden]'
    $value = $value -replace '(?i)(--(?:token|hf-token|huggingface-token|api-key|password|secret)=)([^\s]+)', '$1[hidden]'
    return $value
}

function Get-TimelineRedactedArguments {
    param([string[]]$Arguments = @())

    $redacted = @()
    $hideNext = $false
    $sensitiveNames = @("token", "hf-token", "huggingface-token", "api-key", "password", "secret")
    foreach ($argument in @($Arguments)) {
        $text = [string]$argument
        if ($hideNext) {
            $redacted += "[hidden]"
            $hideNext = $false
            continue
        }

        if ($text.StartsWith("--")) {
            $withoutPrefix = $text.TrimStart("-")
            $name = $withoutPrefix
            $hasInlineValue = $false
            if ($withoutPrefix.Contains("=")) {
                $name = $withoutPrefix.Substring(0, $withoutPrefix.IndexOf("="))
                $hasInlineValue = $true
            }
            if ($sensitiveNames -contains $name.ToLowerInvariant()) {
                if ($hasInlineValue) {
                    $redacted += "--$name=[hidden]"
                }
                else {
                    $redacted += $text
                    $hideNext = $true
                }
                continue
            }
        }

        $redacted += (Protect-TimelineConsoleText -Text $text)
    }

    return $redacted
}

function Join-TimelineCommandLine {
    param(
        [string]$FileName,
        [string[]]$Arguments = @()
    )

    return ((@($FileName) + @(Get-TimelineRedactedArguments -Arguments $Arguments)) | ForEach-Object { Format-TimelineProcessArgument -Value ([string]$_) }) -join " "
}

function Get-TimelineConsoleTextPreview {
    param(
        [string]$Text,
        [int]$MaxLength = 3000
    )

    $value = Protect-TimelineConsoleText -Text $Text
    if ($value.Length -le $MaxLength) {
        return $value
    }
    return "... (trimmed)`n" + $value.Substring($value.Length - $MaxLength)
}

function New-TimelineOperationId {
    param([string]$Prefix = "operation")

    $safePrefix = Get-TimelineZipSafeSegment -Value $Prefix
    if (-not $safePrefix) {
        $safePrefix = "operation"
    }
    $stamp = Get-Date -Format "yyyyMMdd-HHmmss"
    $suffix = ([guid]::NewGuid().ToString("N")).Substring(0, 8)
    return "$safePrefix-$stamp-$suffix"
}

function Get-TimelineOperationLogRoot {
    $settings = Read-TimelineAppSettings
    $storeDirectory = Convert-TimelineText -Value (Get-PropertyValue -Object $settings -Name "storeDirectory" -Default "C:\TimelineData\Timeline\store")
    $storePath = Convert-TimelineWindowsPath -Path $storeDirectory
    if (-not $storePath) {
        $storePath = $storeDirectory
    }
    if (-not $storePath) {
        $storePath = "C:\TimelineData\Timeline\store"
    }

    $baseDirectory = Split-Path -Parent $storePath
    if (-not $baseDirectory) {
        $baseDirectory = $storePath
    }

    $root = Join-Path (Join-Path $baseDirectory "logs") "operations"
    [System.IO.Directory]::CreateDirectory($root) | Out-Null
    return [System.IO.Path]::GetFullPath($root)
}

function Get-TimelineOperationDirectory {
    param(
        [string]$OperationId,
        [switch]$Create
    )

    $safeOperationId = Get-TimelineZipSafeSegment -Value $OperationId
    if (-not $safeOperationId) {
        $safeOperationId = New-TimelineOperationId
    }
    $directory = Join-Path (Get-TimelineOperationLogRoot) $safeOperationId
    if ($Create) {
        [System.IO.Directory]::CreateDirectory($directory) | Out-Null
    }
    return $directory
}

function Write-TimelineOperationJsonLine {
    param(
        [string]$Path,
        [object]$Payload
    )

    $encoding = [System.Text.UTF8Encoding]::new($false)
    $line = (ConvertTo-Json -InputObject $Payload -Compress -Depth 20) + [Environment]::NewLine
    $parent = [System.IO.Path]::GetDirectoryName($Path)
    if ($parent) {
        [System.IO.Directory]::CreateDirectory($parent) | Out-Null
    }

    for ($attempt = 0; $attempt -lt 3; $attempt += 1) {
        try {
            [System.IO.File]::AppendAllText($Path, $line, $encoding)
            return
        }
        catch {
            Start-Sleep -Milliseconds (50 * ($attempt + 1))
        }
    }
}

function Write-TimelineOperationEvent {
    param(
        [string]$OperationId,
        [string]$ParentOperationId = "",
        [string]$Kind = "operation",
        [string]$ProductName = "",
        [string]$Action = "",
        [string]$State = "",
        [string]$Message = "",
        [string]$CommandLine = "",
        [Nullable[int]]$ExitCode = $null,
        [Nullable[int]]$DurationMs = $null,
        [string]$Stdout = "",
        [string]$Stderr = "",
        [object]$Details = $null
    )

    if (-not $OperationId) {
        return
    }

    try {
        $directory = Get-TimelineOperationDirectory -OperationId $OperationId -Create
        $now = [DateTimeOffset]::Now.ToString("o")
        $entry = [ordered]@{
            schemaVersion = 1
            operationId = $OperationId
            parentOperationId = $ParentOperationId
            occurredAt = $now
            kind = $Kind
            productName = $ProductName
            action = $Action
            state = $State
            message = $Message
            commandLine = Protect-TimelineConsoleText -Text $CommandLine
            exitCode = $ExitCode
            durationMs = $DurationMs
            stdoutTail = Get-TimelineConsoleTextPreview -Text $Stdout
            stderrTail = Get-TimelineConsoleTextPreview -Text $Stderr
            details = $Details
        }

        Write-TimelineOperationJsonLine -Path (Join-Path $directory "events.jsonl") -Payload $entry

        $summary = [ordered]@{
            schemaVersion = 1
            operationId = $OperationId
            parentOperationId = $ParentOperationId
            kind = $Kind
            productName = $ProductName
            action = $Action
            state = $State
            message = $Message
            commandLine = Protect-TimelineConsoleText -Text $CommandLine
            exitCode = $ExitCode
            durationMs = $DurationMs
            updatedAt = $now
            details = $Details
        }
        Write-TimelineUtf8JsonFile -Path (Join-Path $directory "summary.json") -Payload $summary
    }
    catch {
    }
}

function Add-TimelineConsoleLog {
    param(
        [string]$Level = "info",
        [string]$Kind = "message",
        [string]$ProductName = "",
        [string]$CommandLine = "",
        [string]$OperationId = "",
        [Nullable[int]]$ExitCode = $null,
        [Nullable[int]]$DurationMs = $null,
        [string]$Stdout = "",
        [string]$Stderr = "",
        [string]$Message = ""
    )

    $script:TimelineConsoleLogNextId += 1
    $entry = [ordered]@{
        id = $script:TimelineConsoleLogNextId
        occurredAt = [DateTimeOffset]::Now.ToString("o")
        level = $Level
        kind = $Kind
        productName = $ProductName
        commandLine = $CommandLine
        operationId = $OperationId
        exitCode = $ExitCode
        durationMs = $DurationMs
        stdout = Get-TimelineConsoleTextPreview -Text $Stdout
        stderr = Get-TimelineConsoleTextPreview -Text $Stderr
        message = $Message
    }

    $script:TimelineConsoleLogEntries.Add($entry)
    while ($script:TimelineConsoleLogEntries.Count -gt $script:TimelineConsoleLogLimit) {
        $script:TimelineConsoleLogEntries.RemoveAt(0)
    }

    if ($OperationId) {
        Write-TimelineOperationEvent `
            -OperationId $OperationId `
            -Kind $Kind `
            -ProductName $ProductName `
            -Action "cli" `
            -State $Level `
            -Message $Message `
            -CommandLine $CommandLine `
            -ExitCode $ExitCode `
            -DurationMs $DurationMs `
            -Stdout $Stdout `
            -Stderr $Stderr
    }
}

function Get-TimelineConsoleLogs {
    param(
        [long]$AfterId = 0,
        [int]$Limit = 120
    )

    $take = [Math]::Min([Math]::Max(1, $Limit), 300)
    $currentLastId = $script:TimelineConsoleLogNextId
    if ($AfterId -gt $currentLastId) {
        $AfterId = 0
    }
    $entries = @($script:TimelineConsoleLogEntries |
        Where-Object { [long](Get-PropertyValue -Object $_ -Name "id" -Default 0) -gt $AfterId } |
        Select-Object -Last $take)
    $lastId = $AfterId
    if ($entries.Count -gt 0) {
        $lastId = [long](Get-PropertyValue -Object $entries[-1] -Name "id" -Default $AfterId)
    }

    return [ordered]@{
        entries = @($entries)
        lastId = $lastId
        count = $script:TimelineConsoleLogEntries.Count
    }
}

function Clear-TimelineConsoleLogs {
    $script:TimelineConsoleLogEntries.Clear()
    return [ordered]@{
        entries = @()
        lastId = $script:TimelineConsoleLogNextId
        count = 0
    }
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

function Invoke-TimelineLoggedProcess {
    param(
        [string]$FileName,
        [string[]]$Arguments,
        [string]$WorkingDirectory,
        [int]$TimeoutSeconds = 25,
        [hashtable]$Environment = @{},
        [string]$ProductName = "",
        [string]$OperationId = ""
    )

    $operationId = Convert-TimelineText -Value $OperationId
    if (-not $operationId) {
        $operationId = New-TimelineOperationId -Prefix "cli"
    }
    $commandLine = Join-TimelineCommandLine -FileName $FileName -Arguments $Arguments
    Add-TimelineConsoleLog `
        -Level "info" `
        -Kind "command" `
        -ProductName $ProductName `
        -CommandLine $commandLine `
        -OperationId $operationId `
        -Message "CLI start."

    $startedAt = [DateTimeOffset]::Now
    try {
        $result = Invoke-TimelineProcess `
            -FileName $FileName `
            -Arguments $Arguments `
            -WorkingDirectory $WorkingDirectory `
            -TimeoutSeconds $TimeoutSeconds `
            -Environment $Environment

        $durationMs = [int]([DateTimeOffset]::Now - $startedAt).TotalMilliseconds
        $exitCode = [int]$result.exitCode
        $level = if ($exitCode -eq 0) { "success" } else { "error" }
        $message = if ($exitCode -eq 0) { "CLI completed." } else { "CLI failed." }
        Add-TimelineConsoleLog `
            -Level $level `
            -Kind "result" `
            -ProductName $ProductName `
            -CommandLine $commandLine `
            -OperationId $operationId `
            -ExitCode $exitCode `
            -DurationMs $durationMs `
            -Stdout ([string]$result.stdout) `
            -Stderr ([string]$result.stderr) `
            -Message $message
        return $result
    }
    catch {
        $durationMs = [int]([DateTimeOffset]::Now - $startedAt).TotalMilliseconds
        Add-TimelineConsoleLog `
            -Level "error" `
            -Kind "result" `
            -ProductName $ProductName `
            -CommandLine $commandLine `
            -OperationId $operationId `
            -DurationMs $durationMs `
            -Stderr $_.Exception.Message `
            -Message "CLI execution error."
        throw
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
        throw "Product CLI did not return JSON."
    }
    $jsonText = $jsonText.Substring($startIndex, $endIndex - $startIndex + 1)
    $payload = $jsonText | ConvertFrom-Json
    $okProperty = $payload.PSObject.Properties["ok"]
    if ($null -ne $okProperty -and $okProperty.Value -is [bool] -and -not [bool]$okProperty.Value) {
        $errorPayload = Get-PropertyValue -Object $payload -Name "error" -Default @{}
        $message = Convert-TimelineText -Value (Get-PropertyValue -Object $errorPayload -Name "message" -Default "")
        if (-not $message) {
            $message = Convert-TimelineText -Value (Get-PropertyValue -Object $payload -Name "message" -Default "")
        }
        if (-not $message) {
            $message = "Product CLI returned ok=false."
        }
        throw $message
    }
    return $payload
}

function ConvertFrom-TimelineJsonStringLiteral {
    param([string]$Text)

    $builder = [System.Text.StringBuilder]::new()
    for ($index = 0; $index -lt $Text.Length; $index++) {
        $char = $Text[$index]
        if ($char -ne [char]92) {
            [void]$builder.Append($char)
            continue
        }

        $index++
        if ($index -ge $Text.Length) {
            [void]$builder.Append([char]92)
            break
        }

        $escaped = $Text[$index]
        switch ([string]$escaped) {
            '"' { [void]$builder.Append('"') }
            '\' { [void]$builder.Append('\') }
            '/' { [void]$builder.Append('/') }
            'b' { [void]$builder.Append([char]8) }
            'f' { [void]$builder.Append([char]12) }
            'n' { [void]$builder.Append([char]10) }
            'r' { [void]$builder.Append([char]13) }
            't' { [void]$builder.Append([char]9) }
            'u' {
                if ($index + 4 -lt $Text.Length) {
                    $hex = $Text.Substring($index + 1, 4)
                    if ($hex -match '^[0-9a-fA-F]{4}$') {
                        [void]$builder.Append([char]([Convert]::ToInt32($hex, 16)))
                        $index += 4
                        break
                    }
                }
                [void]$builder.Append($escaped)
            }
            default {
                [void]$builder.Append($escaped)
            }
        }
    }

    return $builder.ToString()
}

function Get-TimelineJsonStringPropertyFromOutput {
    param(
        [string]$Text,
        [string[]]$Names
    )

    $source = [string]$Text
    foreach ($name in @($Names)) {
        $pattern = '"{0}"\s*:\s*"((?:\\.|[^"\\])*)"' -f [System.Text.RegularExpressions.Regex]::Escape([string]$name)
        $match = [System.Text.RegularExpressions.Regex]::Match(
            $source,
            $pattern,
            [System.Text.RegularExpressions.RegexOptions]::Singleline)
        if ($match.Success) {
            return ConvertFrom-TimelineJsonStringLiteral -Text $match.Groups[1].Value
        }
    }

    return ""
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
        $result = Invoke-TimelineLoggedProcess `
            -FileName (Get-TimelinePowerShellPath) `
            -Arguments $powershellArgs `
            -WorkingDirectory $ProductPath `
            -TimeoutSeconds $TimeoutSeconds `
            -Environment (Get-TimelineChildProcessEnvironment) `
            -ProductName $ProductName
    }
    elseif (Test-Path -LiteralPath $cliBatch) {
        $result = Invoke-TimelineLoggedProcess `
            -FileName (Join-Path $env:SystemRoot "System32\cmd.exe") `
            -Arguments (@("/d", "/c", $cliBatch) + @($CliArgs)) `
            -WorkingDirectory $ProductPath `
            -TimeoutSeconds $TimeoutSeconds `
            -Environment (Get-TimelineChildProcessEnvironment) `
            -ProductName $ProductName
    }
    else {
        throw "$ProductName CLI launcher was not found. Expected cli.bat or cli.ps1 under: $ProductPath"
    }

    $stdout = [string]$result.stdout
    $stderr = [string]$result.stderr
    if ([int]$result.exitCode -ne 0 -and -not $AllowFailure) {
        $jsonMessage = ""
        foreach ($candidate in @($stdout, $stderr)) {
            if (-not ([string]$candidate).Trim()) {
                continue
            }
            try {
                [void](ConvertFrom-TimelineJsonOutput -Text ([string]$candidate))
            }
            catch {
                $candidateMessage = [string]$_.Exception.Message
                if ($candidateMessage -and -not $candidateMessage.Equals("Product CLI did not return JSON.", [System.StringComparison]::OrdinalIgnoreCase)) {
                    $jsonMessage = $candidateMessage
                    break
                }
            }
        }
        $message = if ($jsonMessage) { $jsonMessage } elseif ($stderr.Trim()) { $stderr.Trim() } elseif ($stdout.Trim()) { $stdout.Trim() } else { "exit code $([int]$result.exitCode)" }
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

function Test-TimelineContainerPrefixedWindowsPath {
    param([string]$Path)

    $text = Convert-TimelineText -Value $Path
    return ($text -match '^/[A-Za-z0-9_.-]+/[A-Za-z]:[\\/]')
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

function Get-TimelineAppStoreDirectory {
    $defaultRoot = "C:\TimelineData\Timeline\store"
    $storeDirectory = $defaultRoot
    $path = Get-TimelineAppSettingsPath
    if (Test-Path -LiteralPath $path) {
        try {
            $payload = Get-Content -LiteralPath $path -Raw -Encoding UTF8 | ConvertFrom-Json
            $candidate = Convert-TimelineText -Value (Get-PropertyValue -Object $payload -Name "storeDirectory" -Default "")
            if ($candidate) {
                $storeDirectory = $candidate
            }
        }
        catch {
            $storeDirectory = $defaultRoot
        }
    }

    $localPath = Convert-TimelineWindowsPath -Path $storeDirectory
    if (-not $localPath) {
        $localPath = $defaultRoot
    }
    if (-not [System.IO.Path]::IsPathRooted($localPath)) {
        $localPath = Join-Path $defaultRoot $localPath
    }
    [System.IO.Directory]::CreateDirectory($localPath) | Out-Null
    return [System.IO.Path]::GetFullPath($localPath)
}

function Get-TimelineStoreManifestPath {
    return Join-Path (Get-TimelineAppStoreDirectory) "manifest.json"
}

function Get-TimelineStoreItemsPath {
    return Join-Path (Get-TimelineAppStoreDirectory) "items.jsonl"
}

function Get-TimelineStoreEventsPath {
    return Join-Path (Get-TimelineAppStoreDirectory) "events.jsonl"
}

function Get-TimelineWorkerDirectory {
    $root = Join-Path (Get-TimelineAppWorkDirectory) "worker"
    [System.IO.Directory]::CreateDirectory($root) | Out-Null
    return [System.IO.Path]::GetFullPath($root)
}

function Get-TimelineWorkerJobStatusPath {
    param([string]$JobId)

    $safeJobId = Get-TimelineZipSafeSegment -Value $JobId
    return Join-Path (Get-TimelineWorkerDirectory) "$safeJobId.json"
}

function Get-TimelineDockerWorkerHeartbeatPath {
    return Join-Path (Get-TimelineWorkerDirectory) "docker-worker-heartbeat.json"
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

function Get-TimelineLocalDownloadRoots {
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

    $itemId = Convert-TimelineText -Value (Get-PropertyValueAny -Object $Item -Names @("item_id", "itemId", "thread_id", "threadId", "conversation_id", "conversationId", "id") -Default "")
    $title = Convert-TimelineText -Value (Get-PropertyValueAny -Object $Item -Names @("title", "preferred_title", "preferredTitle", "name") -Default "")
    if (-not $title) {
        $title = Convert-TimelineText -Value (Get-PropertyValue -Object $Item -Name "first_user_message_excerpt" -Default "")
    }
    if (-not $title) {
        $title = $itemId
    }

    $directoryPath = Convert-TimelineText -Value (Get-PropertyValueAny -Object $Item -Names @("directoryPath", "directory_path", "item_dir", "itemDir") -Default "")
    $timelinePath = Resolve-TimelineThreadArtifactPath `
        -Value (Convert-TimelineText -Value (Get-PropertyValueAny -Object $Item -Names @("timeline_path", "timelinePath") -Default "")) `
        -RootPath $RootPath
    $convertInfoPath = Resolve-TimelineThreadArtifactPath `
        -Value (Convert-TimelineText -Value (Get-PropertyValueAny -Object $Item -Names @("convert_info_path", "convertInfoPath") -Default "")) `
        -RootPath $RootPath
    if (-not $directoryPath -and $timelinePath) {
        $directoryPath = [System.IO.Path]::GetDirectoryName($timelinePath)
    }
    if (-not $directoryPath -and $RootPath -and $itemId) {
        $directoryPath = Join-Path $RootPath $itemId
    }
    if (-not $timelinePath -and $directoryPath) {
        $timelinePath = Join-Path $directoryPath "timeline.json"
    }
    if (-not $convertInfoPath -and $directoryPath) {
        $convertInfoPath = Join-Path $directoryPath "convert_info.json"
    }

    $createdAt = Convert-TimelineText -Value (Get-PropertyValueAny -Object $Item -Names @("created_at", "createdAt", "started_at_utc", "startedAtUtc") -Default "")
    $updatedAt = Convert-TimelineText -Value (Get-PropertyValueAny -Object $Item -Names @("updated_at", "updatedAt", "ended_at_utc", "endedAtUtc") -Default "")
    $messageCount = Convert-TimelineAudioInt -Value (Get-PropertyValueAny -Object $Item -Names @("message_count", "messageCount", "event_count", "eventCount") -Default 0)
    if ($timelinePath -and (Test-Path -LiteralPath $timelinePath -PathType Leaf)) {
        $timeline = Read-TimelineChatGptJsonFile -Path $timelinePath
        if ($null -ne $timeline) {
            $timelineTitle = Convert-TimelineText -Value (Get-PropertyValue -Object $timeline -Name "title" -Default "")
            if ($timelineTitle) {
                $title = $timelineTitle
            }
            if (-not $createdAt) {
                $createdAt = Convert-TimelineText -Value (Get-PropertyValue -Object $timeline -Name "created_at" -Default "")
            }
            if (-not $updatedAt) {
                $updatedAt = Convert-TimelineText -Value (Get-PropertyValue -Object $timeline -Name "updated_at" -Default "")
            }
            if ($messageCount -le 0) {
                $messageCount = @(Get-PropertyValue -Object $timeline -Name "messages" -Default @()).Count
            }
        }
    }

    return [ordered]@{
        itemId = $itemId
        title = $title
        createdAt = $createdAt
        updatedAt = $updatedAt
        messageCount = $messageCount
        directoryPath = $directoryPath
        timelinePath = $timelinePath
        convertInfoPath = $convertInfoPath
    }
}

function Resolve-TimelineThreadArtifactPath {
    param(
        [string]$Value,
        [string]$RootPath = ""
    )

    $text = Convert-TimelineText -Value $Value
    if (-not $text) {
        return ""
    }
    $localPath = Convert-TimelineWindowsPath -Path $text
    if ([System.IO.Path]::IsPathRooted($localPath)) {
        return $localPath
    }
    if ($RootPath) {
        return Join-Path $RootPath $localPath.Replace("/", "\")
    }
    return $localPath
}

function Convert-TimelineThreadItemsListResult {
    param(
        [object]$Payload,
        [string]$RootPath = ""
    )

    $items = @(Get-PropertyValue -Object $Payload -Name "items" -Default @())
    $threads = @()
    foreach ($item in @($items)) {
        $threads += Convert-TimelineThreadItemRow -Item $item -RootPath $RootPath
    }

    $total = Convert-TimelineAudioInt -Value (Get-PropertyValueAny -Object $Payload -Names @("total_items", "totalItems", "item_count", "itemCount", "total") -Default $threads.Count)
    $pagination = Convert-TimelinePagination `
        -Payload $Payload `
        -TotalNames @("total_items", "totalItems", "item_count", "itemCount", "total") `
        -ReturnedNames @("returned_items", "returnedItems")
    return New-TimelineThreadListResult `
        -Threads $threads `
        -Pagination $pagination `
        -Total $total
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

function Get-TimelineImageSourceFileCount {
    param([object]$Settings)

    $extensions = @(Get-PropertyValueAny -Object $Settings -Names @("imageExtensions", "image_extensions") -Default @(".jpg", ".jpeg", ".png", ".webp", ".gif", ".bmp", ".tif", ".tiff", ".heic"))
    $extensionSet = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
    foreach ($extension in @($extensions)) {
        $text = ([string]$extension).Trim()
        if (-not $text) {
            continue
        }
        if (-not $text.StartsWith(".")) {
            $text = ".$text"
        }
        [void]$extensionSet.Add($text)
    }

    $count = 0
    foreach ($root in @(Get-PropertyValueAny -Object $Settings -Names @("inputRoots", "input_roots") -Default @())) {
        $rootPath = Convert-TimelineImageLocalPath -Path (Convert-TimelineText -Value $root)
        if (-not $rootPath -or -not (Test-Path -LiteralPath $rootPath)) {
            continue
        }
        foreach ($file in @(Get-ChildItem -LiteralPath $rootPath -File -Recurse -ErrorAction SilentlyContinue)) {
            if ($extensionSet.Contains($file.Extension)) {
                $count += 1
            }
        }
    }
    return $count
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
    $payload = Invoke-TimelineWindowsCodexCliJson `
        -CliArgs @("items", "list", "--page", ([string][Math]::Max(1, $Page)), "--page-size", ([string][Math]::Max(1, $PageSize)), "--json") `
        -TimeoutSeconds 180
    return Convert-TimelineThreadItemsListResult -Payload $payload -RootPath $masterLocalPath
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

    [void](Invoke-TimelineWindowsCodexCliJson `
        -CliArgs @("settings", "master", "set", $outputsRoot, "--json") `
        -TimeoutSeconds 120)

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

    $stdout = Invoke-TimelineProductCliText `
        -ProductPath $WindowsCodexProductPath `
        -ProductName "TimelineForWindowsCodex" `
        -CliArgs $args `
        -TimeoutSeconds 900
    $archivePath = Convert-TimelineDownloadLocalPath -Path (Get-TimelineJsonStringPropertyFromOutput `
        -Text $stdout `
        -Names @("destination_path", "destinationPath", "archive_path", "archivePath", "download_path", "downloadPath"))
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
        },
        [ordered]@{
            id = "image"
            displayName = "TimelineForImage"
            description = "image"
            pagePath = "image"
            settingsPath = "image/settings"
            productPath = $ImageProductPath
            cliPath = (Join-Path $ImageProductPath "cli.ps1")
            startPath = (Join-Path $ImageProductPath "start.ps1")
            stopPath = (Join-Path $ImageProductPath "stop.ps1")
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
        [void](Invoke-TimelineLoggedProcess `
            -FileName $powershell `
            -Arguments @("-NoLogo", "-NoProfile", "-WindowStyle", "Hidden", "-ExecutionPolicy", "Bypass", "-File", $stopScript) `
            -WorkingDirectory $productPath `
            -TimeoutSeconds 180 `
            -Environment (Get-TimelineChildProcessEnvironment) `
            -ProductName ([string]$definition.displayName))
    }

    if (Test-Path -LiteralPath ([string]$definition.startPath)) {
        $startScript = [string]$definition.startPath
        $result = Invoke-TimelineLoggedProcess `
            -FileName $powershell `
            -Arguments @("-NoLogo", "-NoProfile", "-WindowStyle", "Hidden", "-ExecutionPolicy", "Bypass", "-File", $startScript) `
            -WorkingDirectory $productPath `
            -TimeoutSeconds 240 `
            -Environment (Get-TimelineChildProcessEnvironment) `
            -ProductName ([string]$definition.displayName)
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

function Invoke-TimelineImageCliJson {
    param(
        [string[]]$CliArgs,
        [int]$TimeoutSeconds = 120
    )

    $stdout = Invoke-TimelineImageCliText -CliArgs $CliArgs -TimeoutSeconds $TimeoutSeconds
    return ConvertFrom-TimelineJsonOutput -Text $stdout
}

function Invoke-TimelineImageCliText {
    param(
        [string[]]$CliArgs,
        [int]$TimeoutSeconds = 120
    )

    $stdout = Invoke-TimelineProductCliText `
        -ProductPath $ImageProductPath `
        -ProductName "TimelineForImage" `
        -CliArgs (@("--json") + @($CliArgs)) `
        -TimeoutSeconds $TimeoutSeconds
    return $stdout
}

function Convert-TimelineImageLocalPath {
    param([string]$Path)

    $text = ([string]$Path).Trim()
    if (-not $text) {
        return ""
    }
    if ($text.Equals("/workspace", [System.StringComparison]::OrdinalIgnoreCase)) {
        return $ImageProductPath
    }
    if ($text.StartsWith("/workspace/", [System.StringComparison]::OrdinalIgnoreCase)) {
        return (Join-Path $ImageProductPath $text.Substring("/workspace/".Length).Replace("/", "\"))
    }
    if ($text.StartsWith("/mnt/c/", [System.StringComparison]::OrdinalIgnoreCase)) {
        return "C:\" + $text.Substring(7).Replace("/", "\")
    }
    if ([System.IO.Path]::IsPathRooted($text)) {
        return $text
    }
    return Join-Path $ImageProductPath $text
}

function Convert-TimelineImageDirectoryRoot {
    param(
        [string]$Id,
        [string]$DisplayName,
        [string]$Path
    )

    $localPath = Convert-TimelineImageLocalPath -Path $Path
    return [ordered]@{
        id = $Id
        displayName = $DisplayName
        path = $Path
        displayPath = if ($localPath) { $localPath } else { $Path }
        exists = if ($localPath) { Test-Path -LiteralPath $localPath } else { $false }
    }
}

function Convert-TimelineImageInputRoot {
    param(
        [string]$Path,
        [int]$Index
    )

    $localPath = Convert-TimelineImageLocalPath -Path $Path
    return [ordered]@{
        id = "input-$Index"
        displayName = if ($localPath) { Split-Path -Leaf $localPath.TrimEnd('\', '/') } else { "Input $Index" }
        path = $Path
        displayPath = if ($localPath) { $localPath } else { $Path }
        enabled = $true
        exists = if ($localPath) { Test-Path -LiteralPath $localPath } else { $false }
    }
}

function Convert-TimelineImageSettingsStatus {
    param([object]$Payload)

    $settings = Get-PropertyValue -Object $Payload -Name "settings" -Default @{}
    $resolved = Get-PropertyValue -Object $Payload -Name "resolved" -Default @{}
    $inputRoots = @()
    $index = 1
    foreach ($root in @(Get-PropertyValue -Object $settings -Name "input_roots" -Default @())) {
        $inputRoots += Convert-TimelineImageInputRoot -Path (Convert-TimelineText -Value $root) -Index $index
        $index += 1
    }

    $outputRoot = Convert-TimelineText -Value (Get-PropertyValue -Object $settings -Name "output_root" -Default "")
    if (-not $outputRoot) {
        $outputRoot = Convert-TimelineText -Value (Get-PropertyValue -Object $resolved -Name "output_root" -Default "")
    }
    return [ordered]@{
        settingsPath = Convert-TimelineImageLocalPath -Path (Convert-TimelineText -Value (Get-PropertyValue -Object $Payload -Name "settings_path" -Default ""))
        inputRoots = @($inputRoots)
        outputRoot = Convert-TimelineImageDirectoryRoot -Id "output" -DisplayName "Output" -Path $outputRoot
        issues = @()
    }
}

function Get-TimelineImageSettingsFilePath {
    $settingsPath = Join-Path $ImageProductPath "settings.json"
    if (Test-Path -LiteralPath $settingsPath) {
        return $settingsPath
    }
    return Join-Path $ImageProductPath "settings.example.json"
}

function Read-TimelineImageSettingsPayload {
    $path = Get-TimelineImageSettingsFilePath
    if (-not (Test-Path -LiteralPath $path)) {
        return [ordered]@{
            schemaVersion = 1
            inputRoots = @("C:\TimelineData\input-image\")
            outputRoot = "C:\TimelineData\image"
        }
    }
    try {
        return Get-Content -LiteralPath $path -Raw -Encoding UTF8 | ConvertFrom-Json
    }
    catch {
        return [ordered]@{
            schemaVersion = 1
            inputRoots = @("C:\TimelineData\input-image\")
            outputRoot = "C:\TimelineData\image"
        }
    }
}

function Convert-TimelineImageSettingsFile {
    param([object]$Payload)

    $inputRoots = @()
    $index = 1
    foreach ($root in @(Get-PropertyValueAny -Object $Payload -Names @("inputRoots", "input_roots") -Default @())) {
        $inputRoots += Convert-TimelineImageInputRoot -Path (Convert-TimelineText -Value $root) -Index $index
        $index += 1
    }

    $outputRoot = Convert-TimelineText -Value (Get-PropertyValueAny -Object $Payload -Names @("outputRoot", "output_root") -Default "C:\TimelineData\image")

    return [ordered]@{
        settingsPath = Get-TimelineImageSettingsFilePath
        inputRoots = @($inputRoots)
        outputRoot = Convert-TimelineImageDirectoryRoot -Id "output" -DisplayName "Output" -Path $outputRoot
        issues = @()
    }
}

function Convert-TimelineImagePagination {
    param(
        [object]$Payload,
        [string]$RowsProperty = "items"
    )

    $count = Convert-TimelineAudioInt -Value (Get-PropertyValue -Object $Payload -Name "count" -Default 0)
    $page = Convert-TimelineAudioInt -Value (Get-PropertyValue -Object $Payload -Name "page" -Default 1)
    $pageSize = Convert-TimelineAudioInt -Value (Get-PropertyValue -Object $Payload -Name "page_size" -Default 50)
    $pageCount = Convert-TimelineAudioInt -Value (Get-PropertyValue -Object $Payload -Name "page_count" -Default 1)
    if ($pageSize -le 0) {
        $pageSize = 50
    }
    $offset = [Math]::Max(0, ($page - 1) * $pageSize)
    $returned = @(Get-PropertyValue -Object $Payload -Name $RowsProperty -Default @()).Count
    return [ordered]@{
        mode = "page"
        page = $page
        pageSize = $pageSize
        totalItems = $count
        totalPages = $pageCount
        returnedItems = $returned
        offset = $offset
        rangeStart = if ($count -gt 0) { $offset + 1 } else { 0 }
        rangeEnd = [Math]::Min($count, $offset + $returned)
        hasPrevious = $page -gt 1
        hasNext = $page -lt $pageCount
    }
}

function Convert-TimelineImageItemRow {
    param([object]$Row)

    $itemId = Convert-TimelineText -Value (Get-PropertyValue -Object $Row -Name "item_id" -Default "")
    $sourcePath = Convert-TimelineText -Value (Get-PropertyValue -Object $Row -Name "source_path" -Default "")
    $outputDir = Convert-TimelineText -Value (Get-PropertyValue -Object $Row -Name "output_dir" -Default "")
    $localOutputDir = Convert-TimelineImageLocalPath -Path $outputDir
    $timelinePath = if ($localOutputDir) { Join-Path $localOutputDir "timeline.json" } else { "" }
    $convertInfoPath = if ($localOutputDir) { Join-Path $localOutputDir "convert_info.json" } else { "" }
    $imageRecordPath = if ($localOutputDir) { Join-Path $localOutputDir "image_record.json" } else { "" }
    return [ordered]@{
        itemId = $itemId
        relativePath = Convert-TimelineText -Value (Get-PropertyValue -Object $Row -Name "relative_path" -Default "")
        sourcePath = Convert-TimelineImageLocalPath -Path $sourcePath
        sourceDisplayName = Split-Path -Leaf (Convert-TimelineImageLocalPath -Path $sourcePath)
        sizeBytes = Convert-TimelineLong -Value (Get-PropertyValue -Object $Row -Name "size_bytes" -Default 0)
        modifiedAt = Convert-TimelineText -Value (Get-PropertyValue -Object $Row -Name "modified_at" -Default "")
        outputDirectory = $localOutputDir
        timelinePath = $timelinePath
        convertInfoPath = $convertInfoPath
        imageRecordPath = $imageRecordPath
        hasTimeline = if ($timelinePath) { Test-Path -LiteralPath $timelinePath -PathType Leaf } else { $false }
        hasImageRecord = if ($imageRecordPath) { Test-Path -LiteralPath $imageRecordPath -PathType Leaf } else { $false }
    }
}

function Get-TimelineImageCurrentOutputRoot {
    $payload = Read-TimelineImageSettingsPayload
    $outputRoot = Convert-TimelineText -Value (Get-PropertyValueAny -Object $payload -Names @("outputRoot", "output_root") -Default "C:\TimelineData\image")
    return Convert-TimelineImageLocalPath -Path $outputRoot
}

function Convert-TimelineImageFileRow {
    param(
        [object]$Row,
        [string]$OutputRoot
    )

    $itemId = Convert-TimelineText -Value (Get-PropertyValue -Object $Row -Name "item_id" -Default "")
    $sourcePath = Convert-TimelineText -Value (Get-PropertyValue -Object $Row -Name "source_path" -Default "")
    $localSourcePath = Convert-TimelineImageLocalPath -Path $sourcePath
    $localOutputDir = if ($OutputRoot -and $itemId) { Join-Path (Join-Path $OutputRoot "items") $itemId } else { "" }
    $timelinePath = if ($localOutputDir) { Join-Path $localOutputDir "timeline.json" } else { "" }
    $convertInfoPath = if ($localOutputDir) { Join-Path $localOutputDir "convert_info.json" } else { "" }
    $imageRecordPath = if ($localOutputDir) { Join-Path $localOutputDir "image_record.json" } else { "" }
    return [ordered]@{
        itemId = $itemId
        relativePath = Convert-TimelineText -Value (Get-PropertyValue -Object $Row -Name "relative_path" -Default "")
        sourcePath = $localSourcePath
        sourceDisplayName = if ($localSourcePath) { Split-Path -Leaf $localSourcePath } else { Convert-TimelineText -Value (Get-PropertyValue -Object $Row -Name "display_name" -Default "") }
        sizeBytes = Convert-TimelineLong -Value (Get-PropertyValue -Object $Row -Name "size_bytes" -Default 0)
        modifiedAt = Convert-TimelineText -Value (Get-PropertyValue -Object $Row -Name "modified_at" -Default "")
        outputDirectory = $localOutputDir
        timelinePath = $timelinePath
        convertInfoPath = $convertInfoPath
        imageRecordPath = $imageRecordPath
        hasTimeline = if ($timelinePath) { Test-Path -LiteralPath $timelinePath -PathType Leaf } else { $false }
        hasImageRecord = if ($imageRecordPath) { Test-Path -LiteralPath $imageRecordPath -PathType Leaf } else { $false }
    }
}

function Get-TimelineImageOverview {
    $productFound = Test-Path -LiteralPath $ImageProductPath
    if (-not $productFound) {
        return [ordered]@{
            productFound = $false
            productPath = $ImageProductPath
            settingsValid = $false
            settings = [ordered]@{}
            sourceFileCount = 0
            itemCount = 0
            latestRefresh = [ordered]@{}
            message = "TimelineForImage was not found."
        }
    }

    try {
        $settingsPayload = Read-TimelineImageSettingsPayload
        $settings = Convert-TimelineImageSettingsFile -Payload $settingsPayload
        $outputPath = Convert-TimelineText -Value (Get-PropertyValueAny -Object $settingsPayload -Names @("outputRoot", "output_root") -Default "")
        $outputLocalPath = Convert-TimelineImageLocalPath -Path $outputPath
        return [ordered]@{
            productFound = $true
            productPath = $ImageProductPath
            settingsValid = $true
            settings = $settings
            sourceFileCount = Get-TimelineImageSourceFileCount -Settings $settingsPayload
            itemCount = Get-TimelineManifestItemCount -RootPath $outputLocalPath
            latestRefresh = [ordered]@{}
            message = ""
        }
    }
    catch {
        $settings = Convert-TimelineImageSettingsFile -Payload (Read-TimelineImageSettingsPayload)
        return [ordered]@{
            productFound = $true
            productPath = $ImageProductPath
            settingsValid = $false
            settings = $settings
            sourceFileCount = 0
            itemCount = 0
            latestRefresh = [ordered]@{}
            message = $_.Exception.Message
        }
    }
}

function Get-TimelineImageItems {
    param(
        [int]$Page = 1,
        [int]$PageSize = 100
    )

    $payload = Invoke-TimelineImageCliJson -CliArgs @("items", "list", "--page", ([string][Math]::Max(1, $Page)), "--page-size", ([string][Math]::Max(1, $PageSize))) -TimeoutSeconds 120
    $items = @()
    foreach ($row in @(Get-PropertyValue -Object $payload -Name "items" -Default @())) {
        $items += Convert-TimelineImageItemRow -Row $row
    }
    return [ordered]@{
        total = Convert-TimelineAudioInt -Value (Get-PropertyValue -Object $payload -Name "count" -Default 0)
        pagination = Convert-TimelineImagePagination -Payload $payload
        items = @($items)
    }
}

function Get-TimelineImageFiles {
    param(
        [int]$Page = 1,
        [int]$PageSize = 100
    )

    $payload = Invoke-TimelineImageCliJson -CliArgs @("files", "list", "--page", ([string][Math]::Max(1, $Page)), "--page-size", ([string][Math]::Max(1, $PageSize))) -TimeoutSeconds 120
    $outputRoot = Get-TimelineImageCurrentOutputRoot
    $files = @()
    foreach ($row in @(Get-PropertyValue -Object $payload -Name "files" -Default @())) {
        $files += Convert-TimelineImageFileRow -Row $row -OutputRoot $outputRoot
    }
    return [ordered]@{
        total = Convert-TimelineAudioInt -Value (Get-PropertyValue -Object $payload -Name "count" -Default 0)
        pagination = Convert-TimelineImagePagination -Payload $payload -RowsProperty "files"
        files = @($files)
    }
}

function Start-TimelineImageRefresh {
    param([object]$Request)

    $args = @("items", "refresh")
    $maxItems = Convert-TimelineNullableInt -Value (Get-PropertyValue -Object $Request -Name "maxItems" -Default $null)
    if ($null -ne $maxItems -and $maxItems -gt 0) {
        $args += @("--max-items", ([string]$maxItems))
    }
    if ([bool](Get-PropertyValue -Object $Request -Name "reprocessDuplicates" -Default $false)) {
        $args += "--reprocess-duplicates"
    }
    $payload = Invoke-TimelineImageCliJson -CliArgs $args -TimeoutSeconds 900
    return [ordered]@{
        runId = Convert-TimelineText -Value (Get-PropertyValue -Object $payload -Name "run_id" -Default "")
        state = Convert-TimelineText -Value (Get-PropertyValue -Object $payload -Name "state" -Default "")
        sourceCount = Convert-TimelineAudioInt -Value (Get-PropertyValue -Object $payload -Name "source_count" -Default 0)
        processedCount = Convert-TimelineAudioInt -Value (Get-PropertyValue -Object $payload -Name "processed_count" -Default 0)
        skippedCount = Convert-TimelineAudioInt -Value (Get-PropertyValue -Object $payload -Name "skipped_count" -Default 0)
        failedCount = Convert-TimelineAudioInt -Value (Get-PropertyValue -Object $payload -Name "failed_count" -Default 0)
        archivePath = Convert-TimelineImageLocalPath -Path (Convert-TimelineText -Value (Get-PropertyValue -Object $payload -Name "archive_path" -Default ""))
    }
}

function Start-TimelineImageDownload {
    param([object]$Request)

    $itemIds = @(Get-TimelineRequestItemIds -Request $Request)
    $destination = Resolve-TimelineManagedDownloadDirectory `
        -ProductId "image" `
        -RequestedPath (Convert-TimelineText -Value (Get-PropertyValueAny -Object $Request -Names @("destinationPath", "downloadPath", "to") -Default ""))
    $args = @("items", "download")
    if ($itemIds.Count -gt 0) {
        foreach ($itemId in $itemIds) {
            $args += @("--item-id", $itemId)
        }
    }
    $args += @("--to", $destination, "--overwrite")

    $payload = Invoke-TimelineImageCliJson -CliArgs $args -TimeoutSeconds 900
    $archivePath = Convert-TimelineImageLocalPath -Path (Convert-TimelineText -Value (Get-PropertyValueAny -Object $payload -Names @("archive_path", "archivePath", "download_path", "downloadPath") -Default ""))
    if (-not $archivePath -or -not (Test-Path -LiteralPath $archivePath -PathType Leaf)) {
        throw "TimelineForImage CLI did not create a download ZIP."
    }
    if (-not (Test-TimelineDownloadFileAllowed -Path $archivePath)) {
        throw "TimelineForImage CLI does not support Timeline-managed download destination yet."
    }
    return [ordered]@{
        archivePath = [string]$archivePath
        itemIds = @($itemIds)
    }
}

function Remove-TimelineImageItems {
    param([object]$Request)

    $itemIds = @(Get-TimelineRequestItemIds -Request $Request)
    $args = @("items", "remove")
    foreach ($itemId in $itemIds) {
        $args += @("--item-id", $itemId)
    }
    if ([bool](Get-PropertyValue -Object $Request -Name "dryRun" -Default $false)) {
        $args += "--dry-run"
    }
    $payload = Invoke-TimelineImageCliJson -CliArgs $args -TimeoutSeconds 900
    return [ordered]@{
        itemIds = @($itemIds)
        deletedCount = Convert-TimelineAudioInt -Value (Get-PropertyValue -Object $payload -Name "removed_count" -Default 0)
        missingItemIds = @(Convert-TimelineStringArray -Value (Get-PropertyValue -Object $payload -Name "missing" -Default @()))
    }
}

function Write-TimelineImageSettings {
    param([object]$Request)

    $args = @("settings", "save")
    foreach ($root in @(Get-PropertyValue -Object $Request -Name "inputRoots" -Default @())) {
        $path = Convert-TimelineText -Value (Get-PropertyValue -Object $root -Name "path" -Default "")
        if ($path) {
            $args += @("--input-root", $path)
        }
    }
    $outputRoot = Get-PropertyValue -Object $Request -Name "outputRoot" -Default @{}
    $outputPath = Convert-TimelineText -Value (Get-PropertyValue -Object $outputRoot -Name "path" -Default (Get-PropertyValue -Object $Request -Name "outputRootPath" -Default ""))
    if ($outputPath) {
        $args += @("--output-root", $outputPath)
    }

    [void](Invoke-TimelineImageCliText -CliArgs $args -TimeoutSeconds 120)
    return Get-TimelineImageOverview
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

    $localPath = Convert-TimelineChatGptLocalPath -Path $outputPath
    if ($localPath) {
        [System.IO.Directory]::CreateDirectory($localPath) | Out-Null
    }

    [void](Invoke-TimelineChatGptCliJson `
        -CliArgs @("settings", "output", "set", $outputPath, "--json") `
        -TimeoutSeconds 120)
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
    $payload = Invoke-TimelineChatGptCliJson `
        -CliArgs @("items", "list", "--page", ([string][Math]::Max(1, $Page)), "--page-size", ([string][Math]::Max(1, $PageSize)), "--json") `
        -TimeoutSeconds 180
    return Convert-TimelineThreadItemsListResult -Payload $payload -RootPath $masterLocalPath
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

    $stdout = Invoke-TimelineProductCliText `
        -ProductPath $ChatGptProductPath `
        -ProductName "TimelineForChatGPT" `
        -CliArgs @("items", "download", "--to", $hostOutputPath, "--overwrite", "--json") `
        -TimeoutSeconds 900
    $archivePath = Convert-TimelineDownloadLocalPath -Path (Get-TimelineJsonStringPropertyFromOutput `
        -Text $stdout `
        -Names @("download_path", "downloadPath", "destination_path", "destinationPath", "archive_path", "archivePath"))
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

    $outputPath = Convert-TimelineText -Value (Get-PropertyValue -Object $Request -Name "outputPath" -Default "")
    $hostOutputPath = Resolve-TimelineManagedDownloadFile `
        -ProductId "audio" `
        -FilePrefix "TimelineForAudio-items" `
        -RequestedPath $outputPath

    $args = @("items", "download", "--output", $hostOutputPath, "--json")
    if ($itemIds.Count -gt 0) {
        $args += @("--item-id", ($itemIds -join ","))
    }

    $payload = Invoke-TimelineAudioCliJson -CliArgs $args -TimeoutSeconds 900
    $result = Convert-TimelineAudioDownloadItemsResult -Payload $payload
    $returnedArchivePath = Convert-TimelineText -Value (Get-PropertyValue -Object $result -Name "archivePath" -Default "")
    if (Test-TimelineContainerPrefixedWindowsPath -Path $returnedArchivePath) {
        throw "TimelineForAudio CLI returned a container-prefixed Windows path. The product must write to the requested host path and return that host path. Returned path: $returnedArchivePath"
    }

    $archivePath = Convert-TimelineDownloadLocalPath -Path $returnedArchivePath
    if (-not $archivePath -or -not (Test-Path -LiteralPath $archivePath -PathType Leaf)) {
        throw "TimelineForAudio CLI did not create a downloadable ZIP. Returned path: $returnedArchivePath"
    }
    if (-not [System.IO.Path]::GetExtension($archivePath).Equals(".zip", [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "TimelineForAudio CLI created an unexpected download file type."
    }
    if (-not $archivePath -or -not (Test-TimelineDownloadFileAllowed -Path $archivePath)) {
        throw "TimelineForAudio CLI did not create the ZIP in the Timeline work directory. Returned path: $returnedArchivePath"
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

function Write-TimelineExportImageTimeline {
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
    $resolvedItemId = Convert-TimelineText -Value (Get-PropertyValueAny -Object $Timeline -Names @("item_id", "itemId", "record_id", "recordId") -Default $ItemId)
    if (-not $resolvedItemId) {
        $resolvedItemId = $ItemId
    }
    $title = Convert-TimelineText -Value (Get-PropertyValueAny -Object $source -Names @("relative_path", "path", "display_name") -Default $resolvedItemId)
    $events = @(Get-PropertyValue -Object $Timeline -Name "events" -Default @())

    Write-TimelineExportJsonLine -Writer $ItemsWriter -Payload ([ordered]@{
        schemaVersion = 1
        product = $ProductId
        productName = $DisplayName
        itemId = $resolvedItemId
        itemType = "image"
        title = $title
        createdAt = ""
        updatedAt = ""
        eventCount = $events.Count
        sourceRef = [ordered]@{
            timelinePath = $RawTimelinePath
            convertInfoPath = $RawConvertInfoPath
        }
    })

    $eventCount = 0
    $sequence = 0
    foreach ($event in $events) {
        $time = Convert-TimelineText -Value (Get-PropertyValueAny -Object $event -Names @("time", "created_at", "createdAt", "timestamp") -Default "")
        $summary = Get-PropertyValue -Object $event -Name "summary" -Default @{}
        Write-TimelineExportJsonLine -Writer $EventsWriter -Payload ([ordered]@{
            schemaVersion = 1
            eventId = "${ProductId}:${resolvedItemId}:image:$sequence"
            product = $ProductId
            itemId = $resolvedItemId
            eventType = Convert-TimelineText -Value (Get-PropertyValue -Object $event -Name "type" -Default "image_event")
            sequence = $sequence
            time = [ordered]@{
                absoluteStartAt = $time
                absoluteEndAt = $time
                relativeStartSec = $null
                relativeEndSec = $null
                timeBasis = if ($time) { "absolute" } else { "sequence" }
            }
            actor = [ordered]@{
                type = "source"
                label = "image"
            }
            content = [ordered]@{
                kind = "image_summary"
                value = ConvertTo-TimelineJson $summary
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
            elseif ($ProductId.Equals("image", [System.StringComparison]::OrdinalIgnoreCase)) {
                $eventCount = Write-TimelineExportImageTimeline `
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
    if ($ProductId -eq "image") {
        $overview = Get-TimelineImageOverview
        $itemCount = Convert-TimelineAudioInt -Value (Get-PropertyValue -Object $overview -Name "itemCount" -Default 0)
        if ($itemCount -le 0) {
            return [ordered]@{
                productId = $ProductId
                displayName = $DisplayName
                archivePath = ""
            }
        }
        $payload = Start-TimelineImageDownload -Request ([pscustomobject]@{})
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
        [ordered]@{ productId = "chatgpt"; displayName = "TimelineForChatGPT" },
        [ordered]@{ productId = "image"; displayName = "TimelineForImage" }
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

function Get-TimelineStoreProductDisplayName {
    param([string]$ProductId)

    switch ($ProductId) {
        "audio" { return "TimelineForAudio" }
        "windows-codex" { return "TimelineForWindowsCodex" }
        "chatgpt" { return "TimelineForChatGPT" }
        "image" { return "TimelineForImage" }
        default { return $ProductId }
    }
}

function Get-TimelineStoreEventSortKey {
    param(
        [object]$Event,
        [int]$Ordinal
    )

    $product = Convert-TimelineText -Value (Get-PropertyValue -Object $Event -Name "product" -Default "")
    $itemId = Convert-TimelineText -Value (Get-PropertyValue -Object $Event -Name "itemId" -Default "")
    $sequence = Convert-TimelineAudioInt -Value (Get-PropertyValue -Object $Event -Name "sequence" -Default 0)
    $time = Get-PropertyValue -Object $Event -Name "time" -Default @{}
    $absoluteStartAt = Convert-TimelineText -Value (Get-PropertyValue -Object $time -Name "absoluteStartAt" -Default "")
    if ($absoluteStartAt) {
        return ("0|{0}|{1}|{2}|{3:D10}|{4:D10}" -f $absoluteStartAt, $product, $itemId, $sequence, $Ordinal)
    }

    $relativeStart = Get-PropertyValue -Object $time -Name "relativeStartSec" -Default $null
    $relativeText = Convert-TimelineText -Value $relativeStart
    if ($relativeText) {
        $relativeNumber = Convert-TimelineAudioNumber -Value $relativeStart
        return ("1|{0}|{1}|{2}|{3:D10}|{4:D10}" -f $product, $itemId, ("{0:0000000000.000000}" -f [double]$relativeNumber), $sequence, $Ordinal)
    }

    return ("2|{0}|{1}|{2:D10}|{3:D10}" -f $product, $itemId, $sequence, $Ordinal)
}

function Sort-TimelineStoreEventsFile {
    param([string]$EventsPath)

    if (-not $EventsPath -or -not (Test-Path -LiteralPath $EventsPath -PathType Leaf)) {
        return
    }

    $rows = [System.Collections.Generic.List[object]]::new()
    $ordinal = 0
    foreach ($line in [System.IO.File]::ReadLines($EventsPath)) {
        $text = ([string]$line).Trim()
        if (-not $text) {
            continue
        }
        try {
            $event = $text | ConvertFrom-Json
            $rows.Add([pscustomobject]@{
                sortKey = Get-TimelineStoreEventSortKey -Event $event -Ordinal $ordinal
                ordinal = $ordinal
                line = $text
            })
        }
        catch {
            $rows.Add([pscustomobject]@{
                sortKey = ("9|{0:D10}" -f $ordinal)
                ordinal = $ordinal
                line = $text
            })
        }
        $ordinal += 1
    }

    if ($rows.Count -le 1) {
        return
    }

    $tempPath = "$EventsPath.tmp"
    $writer = [System.IO.StreamWriter]::new($tempPath, $false, [System.Text.UTF8Encoding]::new($false))
    try {
        foreach ($row in @($rows | Sort-Object sortKey, ordinal)) {
            $writer.WriteLine([string]$row.line)
        }
    }
    finally {
        $writer.Dispose()
    }
    Move-Item -LiteralPath $tempPath -Destination $EventsPath -Force
}

function New-TimelineStoreRebuild {
    $storeRoot = Get-TimelineAppStoreDirectory
    $rebuildsRoot = Join-Path $storeRoot "rebuilds"
    [System.IO.Directory]::CreateDirectory($rebuildsRoot) | Out-Null

    $stamp = Get-Date -Format "yyyyMMdd-HHmmss"
    $suffix = ([guid]::NewGuid().ToString("N")).Substring(0, 8)
    $rebuildId = "rebuild-$stamp-$suffix"
    $stagingRoot = Join-Path (Join-Path (Get-TimelineAppWorkDirectory) "timeline-store-staging") $rebuildId
    $packageRoot = Join-Path $stagingRoot "package"
    $rebuildRoot = Join-Path $rebuildsRoot $rebuildId
    if (Test-Path -LiteralPath $rebuildRoot) {
        Remove-Item -LiteralPath $rebuildRoot -Recurse -Force
    }
    [System.IO.Directory]::CreateDirectory((Join-Path $packageRoot "timeline")) | Out-Null

    $products = @(
        [ordered]@{ productId = "audio"; displayName = "TimelineForAudio" },
        [ordered]@{ productId = "windows-codex"; displayName = "TimelineForWindowsCodex" },
        [ordered]@{ productId = "chatgpt"; displayName = "TimelineForChatGPT" },
        [ordered]@{ productId = "image"; displayName = "TimelineForImage" }
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
        throw "No Timeline items were found. Check each product list first."
    }

    Sort-TimelineStoreEventsFile -EventsPath $eventsPath

    $manifest = [ordered]@{
        schemaVersion = 1
        artifactType = "timeline_store"
        createdAt = (Get-Date).ToString("o")
        rebuildId = $rebuildId
        packagePath = $rebuildRoot
        itemCount = $itemCount
        eventCount = $eventCount
        products = @($productResults)
        files = [ordered]@{
            items = "items.jsonl"
            events = "events.jsonl"
            packageItems = "timeline/items.jsonl"
            packageEvents = "timeline/events.jsonl"
        }
    }
    Write-TimelineUtf8JsonFile -Path (Join-Path $packageRoot "manifest.json") -Payload $manifest
    Set-Content -LiteralPath (Join-Path $packageRoot "README.md") -Encoding UTF8 -Value @(
        "# Timeline Store",
        "",
        "This directory is the current Timeline store package.",
        "",
        "- timeline/items.jsonl: one row per managed item.",
        "- timeline/events.jsonl: one row per timeline event, sorted for Timeline browsing.",
        "- products/: product download contents expanded for inspection.",
        "- source-downloads/: raw product CLI download ZIPs."
    )

    try {
        Move-Item -LiteralPath $packageRoot -Destination $rebuildRoot
        Copy-Item -LiteralPath (Join-Path $rebuildRoot "manifest.json") -Destination (Get-TimelineStoreManifestPath) -Force
        Copy-Item -LiteralPath (Join-Path $rebuildRoot "timeline\items.jsonl") -Destination (Get-TimelineStoreItemsPath) -Force
        Copy-Item -LiteralPath (Join-Path $rebuildRoot "timeline\events.jsonl") -Destination (Get-TimelineStoreEventsPath) -Force
    }
    catch {
        Remove-Item -LiteralPath $rebuildRoot -Recurse -Force -ErrorAction SilentlyContinue
        throw
    }
    finally {
        Remove-Item -LiteralPath $stagingRoot -Recurse -Force -ErrorAction SilentlyContinue
    }

    return [ordered]@{
        rebuildId = $rebuildId
        storeDirectory = $storeRoot
        packagePath = $rebuildRoot
        manifestPath = Get-TimelineStoreManifestPath
        itemsPath = Get-TimelineStoreItemsPath
        eventsPath = Get-TimelineStoreEventsPath
        itemCount = $itemCount
        eventCount = $eventCount
        products = @($productResults)
    }
}

function Get-TimelineStoreOverview {
    $storeRoot = Get-TimelineAppStoreDirectory
    $manifestPath = Get-TimelineStoreManifestPath
    $itemsPath = Get-TimelineStoreItemsPath
    $eventsPath = Get-TimelineStoreEventsPath
    if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) {
        return [ordered]@{
            available = $false
            storeDirectory = $storeRoot
            rebuildId = ""
            createdAt = ""
            itemCount = 0
            eventCount = 0
            productCount = 0
            products = @()
            manifestPath = $manifestPath
            itemsPath = $itemsPath
            eventsPath = $eventsPath
            message = "Timeline store has not been rebuilt yet."
        }
    }

    try {
        $manifest = Get-Content -LiteralPath $manifestPath -Raw -Encoding UTF8 | ConvertFrom-Json
        $products = @(Get-PropertyValue -Object $manifest -Name "products" -Default @())
        $available = (Test-Path -LiteralPath $itemsPath -PathType Leaf) -and (Test-Path -LiteralPath $eventsPath -PathType Leaf)
        return [ordered]@{
            available = $available
            storeDirectory = $storeRoot
            rebuildId = Convert-TimelineText -Value (Get-PropertyValue -Object $manifest -Name "rebuildId" -Default "")
            createdAt = Convert-TimelineText -Value (Get-PropertyValue -Object $manifest -Name "createdAt" -Default "")
            itemCount = Convert-TimelineAudioInt -Value (Get-PropertyValue -Object $manifest -Name "itemCount" -Default 0)
            eventCount = Convert-TimelineAudioInt -Value (Get-PropertyValue -Object $manifest -Name "eventCount" -Default 0)
            productCount = $products.Count
            products = @($products)
            manifestPath = $manifestPath
            itemsPath = $itemsPath
            eventsPath = $eventsPath
            message = if ($available) { "" } else { "Timeline store files were not found. Rebuild the Timeline store." }
        }
    }
    catch {
        return [ordered]@{
            available = $false
            storeDirectory = $storeRoot
            rebuildId = ""
            createdAt = ""
            itemCount = 0
            eventCount = 0
            productCount = 0
            products = @()
            manifestPath = $manifestPath
            itemsPath = $itemsPath
            eventsPath = $eventsPath
            message = "Timeline store could not be read. Rebuild the Timeline store."
        }
    }
}

function Convert-TimelineStoreEventRow {
    param([object]$Event)

    $time = Get-PropertyValue -Object $Event -Name "time" -Default @{}
    $actor = Get-PropertyValue -Object $Event -Name "actor" -Default @{}
    $content = Get-PropertyValue -Object $Event -Name "content" -Default @{}
    $productId = Convert-TimelineText -Value (Get-PropertyValue -Object $Event -Name "product" -Default "")

    return [ordered]@{
        eventId = Convert-TimelineText -Value (Get-PropertyValue -Object $Event -Name "eventId" -Default "")
        product = $productId
        productName = Get-TimelineStoreProductDisplayName -ProductId $productId
        itemId = Convert-TimelineText -Value (Get-PropertyValue -Object $Event -Name "itemId" -Default "")
        eventType = Convert-TimelineText -Value (Get-PropertyValue -Object $Event -Name "eventType" -Default "")
        sequence = Convert-TimelineAudioInt -Value (Get-PropertyValue -Object $Event -Name "sequence" -Default 0)
        occurredAt = Convert-TimelineText -Value (Get-PropertyValue -Object $time -Name "absoluteStartAt" -Default "")
        endedAt = Convert-TimelineText -Value (Get-PropertyValue -Object $time -Name "absoluteEndAt" -Default "")
        relativeStartSec = Get-PropertyValue -Object $time -Name "relativeStartSec" -Default $null
        relativeEndSec = Get-PropertyValue -Object $time -Name "relativeEndSec" -Default $null
        timeBasis = Convert-TimelineText -Value (Get-PropertyValue -Object $time -Name "timeBasis" -Default "")
        actorType = Convert-TimelineText -Value (Get-PropertyValue -Object $actor -Name "type" -Default "")
        actorLabel = Convert-TimelineText -Value (Get-PropertyValue -Object $actor -Name "label" -Default "")
        contentKind = Convert-TimelineText -Value (Get-PropertyValue -Object $content -Name "kind" -Default "")
        contentValue = Convert-TimelineText -Value (Get-PropertyValue -Object $content -Name "value" -Default "")
    }
}

function Get-TimelineStoreEvents {
    param(
        [int]$Page = 1,
        [int]$PageSize = 100
    )

    $overview = Get-TimelineStoreOverview
    if (-not [bool](Get-PropertyValue -Object $overview -Name "available" -Default $false)) {
        return [ordered]@{
            available = $false
            total = 0
            pagination = New-TimelinePagination -Page $Page -PageSize $PageSize -TotalItems 0 -ReturnedItems 0
            events = @()
            message = Convert-TimelineText -Value (Get-PropertyValue -Object $overview -Name "message" -Default "")
        }
    }

    $eventsPath = Get-TimelineStoreEventsPath
    $effectivePage = [Math]::Max(1, $Page)
    $effectivePageSize = [Math]::Max(1, $PageSize)
    $offset = ($effectivePage - 1) * $effectivePageSize
    $rows = @()
    $total = 0
    foreach ($line in [System.IO.File]::ReadLines($eventsPath)) {
        $text = ([string]$line).Trim()
        if (-not $text) {
            continue
        }

        if ($total -ge $offset -and $rows.Count -lt $effectivePageSize) {
            try {
                $rows += Convert-TimelineStoreEventRow -Event ($text | ConvertFrom-Json)
            }
            catch {
            }
        }
        $total += 1
    }

    return [ordered]@{
        available = $true
        total = $total
        pagination = New-TimelinePagination -Page $effectivePage -PageSize $effectivePageSize -TotalItems $total -ReturnedItems $rows.Count
        events = @($rows)
        message = ""
    }
}

function New-TimelineStoreDownload {
    $overview = Get-TimelineStoreOverview
    if (-not [bool](Get-PropertyValue -Object $overview -Name "available" -Default $false)) {
        throw "Timeline store has not been rebuilt yet. Rebuild the Timeline store first."
    }

    $manifestPath = Get-TimelineStoreManifestPath
    $manifest = Get-Content -LiteralPath $manifestPath -Raw -Encoding UTF8 | ConvertFrom-Json
    $packagePath = Convert-TimelineWindowsPath -Path (Convert-TimelineText -Value (Get-PropertyValue -Object $manifest -Name "packagePath" -Default ""))
    if (-not $packagePath -or -not (Test-Path -LiteralPath $packagePath -PathType Container)) {
        throw "Timeline store package was not found. Rebuild the Timeline store."
    }

    $downloadRoot = Get-TimelineExportDownloadRoot
    $stamp = Get-Date -Format "yyyyMMdd-HHmmss"
    $archivePath = Join-Path $downloadRoot "Timeline-store-$stamp.zip"
    if (Test-Path -LiteralPath $archivePath) {
        Remove-Item -LiteralPath $archivePath -Force
    }
    [System.IO.Compression.ZipFile]::CreateFromDirectory($packagePath, $archivePath, [System.IO.Compression.CompressionLevel]::Optimal, $false)

    return [ordered]@{
        archivePath = $archivePath
        itemCount = Convert-TimelineAudioInt -Value (Get-PropertyValue -Object $manifest -Name "itemCount" -Default 0)
        eventCount = Convert-TimelineAudioInt -Value (Get-PropertyValue -Object $manifest -Name "eventCount" -Default 0)
        products = @(Get-PropertyValue -Object $manifest -Name "products" -Default @())
    }
}

function New-TimelineWorkerJobId {
    $stamp = Get-Date -Format "yyyyMMdd-HHmmss"
    $suffix = ([guid]::NewGuid().ToString("N")).Substring(0, 8)
    return "timeline-$stamp-$suffix"
}

function Write-TimelineWorkerJobStatus {
    param([object]$Status)

    $jobId = Convert-TimelineText -Value (Get-PropertyValue -Object $Status -Name "jobId" -Default "")
    if (-not $jobId) {
        throw "Worker job id is required."
    }
    $path = Get-TimelineWorkerJobStatusPath -JobId $jobId
    Write-TimelineUtf8JsonFile -Path $path -Payload $Status
    Write-TimelineOperationEvent `
        -OperationId $jobId `
        -Kind "worker" `
        -ProductName "Timeline" `
        -Action (Convert-TimelineText -Value (Get-PropertyValue -Object $Status -Name "kind" -Default "timeline_worker")) `
        -State (Convert-TimelineText -Value (Get-PropertyValue -Object $Status -Name "state" -Default "")) `
        -Message (Convert-TimelineText -Value (Get-PropertyValue -Object $Status -Name "message" -Default "")) `
        -Details ([ordered]@{
            stage = Convert-TimelineText -Value (Get-PropertyValue -Object $Status -Name "stage" -Default "")
            error = Convert-TimelineText -Value (Get-PropertyValue -Object $Status -Name "error" -Default "")
            itemCount = Convert-TimelineAudioInt -Value (Get-PropertyValue -Object $Status -Name "itemCount" -Default 0)
            eventCount = Convert-TimelineAudioInt -Value (Get-PropertyValue -Object $Status -Name "eventCount" -Default 0)
            completedAt = Convert-TimelineText -Value (Get-PropertyValue -Object $Status -Name "completedAt" -Default "")
        })
    return $Status
}

function Read-TimelineWorkerJobStatus {
    param([string]$JobId)

    $path = Get-TimelineWorkerJobStatusPath -JobId $JobId
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        return [ordered]@{
            jobId = $JobId
            kind = "timeline_rebuild"
            state = "missing"
            stage = ""
            message = "Worker job was not found."
            error = ""
            startedAt = ""
            updatedAt = ""
            completedAt = ""
            itemCount = 0
            eventCount = 0
            result = $null
        }
    }
    return Get-Content -LiteralPath $path -Raw -Encoding UTF8 | ConvertFrom-Json
}

function Get-TimelineLatestWorkerJobStatus {
    $jobs = @(Get-ChildItem -LiteralPath (Get-TimelineWorkerDirectory) -Filter "timeline-*.json" -File -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTimeUtc -Descending)
    if ($jobs.Count -eq 0) {
        return [ordered]@{
            jobId = ""
            kind = "timeline_rebuild"
            state = "none"
            stage = ""
            message = "No Timeline worker job has been started."
            error = ""
            startedAt = ""
            updatedAt = ""
            completedAt = ""
            itemCount = 0
            eventCount = 0
            result = $null
        }
    }

    try {
        return Get-Content -LiteralPath ([string]$jobs[0].FullName) -Raw -Encoding UTF8 | ConvertFrom-Json
    }
    catch {
        return [ordered]@{
            jobId = ""
            kind = "timeline_rebuild"
            state = "unreadable"
            stage = ""
            message = "Latest Timeline worker job could not be read."
            error = $_.Exception.Message
            startedAt = ""
            updatedAt = ""
            completedAt = ""
            itemCount = 0
            eventCount = 0
            result = $null
        }
    }
}

function Get-TimelineDockerWorkerStatus {
    $path = Get-TimelineDockerWorkerHeartbeatPath
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        return [ordered]@{
            available = $false
            worker = "timeline-worker"
            state = "missing"
            updatedAt = ""
            workDirectory = ""
            storeDirectory = ""
            storeAvailable = $false
            rebuildId = ""
            createdAt = ""
            itemCount = 0
            eventCount = 0
            message = "Timeline Docker worker heartbeat was not found."
        }
    }

    try {
        $payload = Get-Content -LiteralPath $path -Raw -Encoding UTF8 | ConvertFrom-Json
        return [ordered]@{
            available = $true
            worker = Convert-TimelineText -Value (Get-PropertyValue -Object $payload -Name "worker" -Default "timeline-worker")
            state = Convert-TimelineText -Value (Get-PropertyValue -Object $payload -Name "state" -Default "")
            updatedAt = Convert-TimelineText -Value (Get-PropertyValue -Object $payload -Name "updatedAt" -Default "")
            workDirectory = Convert-TimelineText -Value (Get-PropertyValue -Object $payload -Name "workDirectory" -Default "")
            storeDirectory = Convert-TimelineText -Value (Get-PropertyValue -Object $payload -Name "storeDirectory" -Default "")
            storeAvailable = [bool](Get-PropertyValue -Object $payload -Name "storeAvailable" -Default $false)
            rebuildId = Convert-TimelineText -Value (Get-PropertyValue -Object $payload -Name "rebuildId" -Default "")
            createdAt = Convert-TimelineText -Value (Get-PropertyValue -Object $payload -Name "createdAt" -Default "")
            itemCount = Convert-TimelineAudioInt -Value (Get-PropertyValue -Object $payload -Name "itemCount" -Default 0)
            eventCount = Convert-TimelineAudioInt -Value (Get-PropertyValue -Object $payload -Name "eventCount" -Default 0)
            message = ""
        }
    }
    catch {
        return [ordered]@{
            available = $false
            worker = "timeline-worker"
            state = "unreadable"
            updatedAt = ""
            workDirectory = ""
            storeDirectory = ""
            storeAvailable = $false
            rebuildId = ""
            createdAt = ""
            itemCount = 0
            eventCount = 0
            message = $_.Exception.Message
        }
    }
}

function Test-TimelineWorkerJobActive {
    param([object]$Status)

    $state = Convert-TimelineText -Value (Get-PropertyValue -Object $Status -Name "state" -Default "")
    return @("queued", "running") -contains $state
}

function Start-TimelineStoreRebuildWorker {
    $latest = Get-TimelineLatestWorkerJobStatus
    if (Test-TimelineWorkerJobActive -Status $latest) {
        return $latest
    }

    $jobId = New-TimelineWorkerJobId
    $now = [DateTimeOffset]::Now.ToString("o")
    $status = [ordered]@{
        jobId = $jobId
        kind = "timeline_rebuild"
        state = "queued"
        stage = "queued"
        message = "Timeline rebuild worker has been queued."
        error = ""
        startedAt = $now
        updatedAt = $now
        completedAt = ""
        itemCount = 0
        eventCount = 0
        result = $null
    }
    Write-TimelineWorkerJobStatus -Status $status | Out-Null

    $workerScript = Join-Path $TimelineProductPath "scripts\timeline-store-worker.ps1"
    if (-not (Test-Path -LiteralPath $workerScript -PathType Leaf)) {
        $status.state = "failed"
        $status.stage = "failed"
        $status.message = "Timeline worker script was not found."
        $status.error = $workerScript
        $status.completedAt = [DateTimeOffset]::Now.ToString("o")
        $status.updatedAt = $status.completedAt
        Write-TimelineWorkerJobStatus -Status $status | Out-Null
        return $status
    }

    $arguments = @(
        "-NoLogo",
        "-NoProfile",
        "-STA",
        "-ExecutionPolicy",
        "Bypass",
        "-File",
        $workerScript,
        "-JobId",
        $jobId,
        "-TimelineProductPath",
        $TimelineProductPath,
        "-AudioProductPath",
        $AudioProductPath,
        "-WindowsCodexProductPath",
        $WindowsCodexProductPath,
        "-ChatGptProductPath",
        $ChatGptProductPath,
        "-ImageProductPath",
        $ImageProductPath
    )

    $startInfo = [System.Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = Get-TimelinePowerShellPath
    $startInfo.Arguments = ($arguments | ForEach-Object { Format-TimelineProcessArgument -Value ([string]$_) }) -join " "
    $startInfo.WorkingDirectory = $TimelineProductPath
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    [System.Diagnostics.Process]::Start($startInfo) | Out-Null
    return Read-TimelineWorkerJobStatus -JobId $jobId
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

    $detail = [ordered]@{
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
    $detail["audioVerbalization"] = Get-TimelineAudioVerbalizationStatusFromDetail -Detail $detail
    return $detail
}

function Get-TimelineAudioVerbalizationRoot {
    $root = Join-Path (Get-TimelineAppStoreDirectory) "audio-verbalizations"
    [System.IO.Directory]::CreateDirectory($root) | Out-Null
    return [System.IO.Path]::GetFullPath($root)
}

function Get-TimelineAudioVerbalizationDirectory {
    param(
        [string]$AudioItemId,
        [switch]$Create
    )

    $safeItemId = Get-TimelineZipSafeSegment -Value $AudioItemId
    if (-not $safeItemId) {
        $safeItemId = "unknown"
    }
    $path = Join-Path (Get-TimelineAudioVerbalizationRoot) $safeItemId
    if ($Create) {
        [System.IO.Directory]::CreateDirectory($path) | Out-Null
    }
    return [System.IO.Path]::GetFullPath($path)
}

function Get-TimelineAudioVerbalizationChunkPlan {
    param(
        [object[]]$Turns,
        [object]$Settings
    )

    $chunkMaxMinutes = Convert-TimelineAudioInt -Value (Get-PropertyValue -Object $Settings -Name "chunkMaxMinutes" -Default 10)
    $chunkMaxTurns = Convert-TimelineAudioInt -Value (Get-PropertyValue -Object $Settings -Name "chunkMaxTurns" -Default 80)
    $chunkMaxSeconds = [Math]::Max(60, $chunkMaxMinutes * 60)
    $chunkMaxTurns = [Math]::Max(1, $chunkMaxTurns)

    $chunks = @()
    $current = @()
    $currentStart = $null

    foreach ($turn in @($Turns | Sort-Object { Convert-TimelineAudioNumber -Value (Get-PropertyValue -Object $_ -Name "startSec" -Default 0) })) {
        $startSec = Convert-TimelineAudioNumber -Value (Get-PropertyValue -Object $turn -Name "startSec" -Default 0)
        $endSec = Convert-TimelineAudioNumber -Value (Get-PropertyValue -Object $turn -Name "endSec" -Default $startSec)
        if ($null -eq $startSec) {
            $startSec = 0
        }
        if ($null -eq $endSec) {
            $endSec = $startSec
        }

        if ($current.Count -eq 0) {
            $currentStart = $startSec
        }

        $prospectiveDuration = [double]$endSec - [double]$currentStart
        $exceedsDuration = $current.Count -gt 0 -and $prospectiveDuration -gt $chunkMaxSeconds
        $exceedsTurns = $current.Count -ge $chunkMaxTurns
        if ($exceedsDuration -or $exceedsTurns) {
            $chunks += New-TimelineAudioVerbalizationChunk -Index ($chunks.Count + 1) -Turns $current
            $current = @()
            $currentStart = $startSec
        }

        $current += $turn
    }

    if ($current.Count -gt 0) {
        $chunks += New-TimelineAudioVerbalizationChunk -Index ($chunks.Count + 1) -Turns $current
    }

    return @($chunks)
}

function New-TimelineAudioVerbalizationChunk {
    param(
        [int]$Index,
        [object[]]$Turns
    )

    $chunkId = "chunk-{0:D4}" -f $Index
    $startSec = 0.0
    $endSec = 0.0
    if ($Turns.Count -gt 0) {
        $startSec = [double](Convert-TimelineAudioNumber -Value (Get-PropertyValue -Object $Turns[0] -Name "startSec" -Default 0))
        $lastTurn = $Turns[$Turns.Count - 1]
        $endSec = [double](Convert-TimelineAudioNumber -Value (Get-PropertyValue -Object $lastTurn -Name "endSec" -Default $startSec))
    }

    $plannedTurns = @()
    $tokenEstimate = 0
    foreach ($turn in @($Turns)) {
        $turnIndex = Convert-TimelineAudioInt -Value (Get-PropertyValue -Object $turn -Name "index" -Default 0)
        $phoneTokens = Convert-TimelineText -Value (Get-PropertyValue -Object $turn -Name "phoneTokens" -Default "")
        $tokenEstimate += [Math]::Max(1, [int][Math]::Ceiling($phoneTokens.Length / 4.0))
        $plannedTurns += [ordered]@{
            turnId = "turn-{0:D6}" -f $turnIndex
            index = $turnIndex
            startSec = Convert-TimelineAudioNumber -Value (Get-PropertyValue -Object $turn -Name "startSec" -Default 0)
            endSec = Convert-TimelineAudioNumber -Value (Get-PropertyValue -Object $turn -Name "endSec" -Default 0)
            speaker = Convert-TimelineText -Value (Get-PropertyValue -Object $turn -Name "speaker" -Default "")
            phoneTokens = $phoneTokens
            confidence = Get-PropertyValue -Object $turn -Name "confidence" -Default $null
        }
    }

    return [ordered]@{
        chunkId = $chunkId
        sequence = $Index
        state = "planned"
        startSec = $startSec
        endSec = $endSec
        durationSec = [Math]::Max(0, $endSec - $startSec)
        turnCount = $plannedTurns.Count
        inputTokenEstimate = $tokenEstimate
        turns = @($plannedTurns)
    }
}

function New-TimelineAudioVerbalizationPlan {
    param(
        [object]$Detail,
        [object]$VerbalizationSettings
    )

    $file = Get-PropertyValue -Object $Detail -Name "file" -Default $null
    $turns = @(Get-PropertyValue -Object $Detail -Name "turns" -Default @())
    $chunks = Get-TimelineAudioVerbalizationChunkPlan -Turns $turns -Settings $VerbalizationSettings
    $now = [DateTimeOffset]::Now.ToString("o")

    return [ordered]@{
        schemaVersion = 1
        createdAt = $now
        source = [ordered]@{
            audioItemId = Convert-TimelineText -Value (Get-PropertyValue -Object $file -Name "itemId" -Default "")
            sourceFileIdentity = Convert-TimelineText -Value (Get-PropertyValue -Object $file -Name "sourceFileIdentity" -Default "")
            fileName = Convert-TimelineText -Value (Get-PropertyValue -Object $file -Name "fileName" -Default "")
            displayPath = Convert-TimelineText -Value (Get-PropertyValue -Object $file -Name "displayPath" -Default "")
            durationSec = Get-PropertyValue -Object $file -Name "durationSec" -Default $null
            turnCount = @($turns).Count
        }
        settings = $VerbalizationSettings
        chunks = @($chunks)
    }
}

function New-TimelineAudioVerbalizationContext {
    param(
        [object]$Plan,
        [object]$Chunk,
        [object]$PreviousChunk,
        [string]$PreviousSummaryPath = ""
    )

    $source = Get-PropertyValue -Object $Plan -Name "source" -Default @{}
    $settings = Get-PropertyValue -Object $Plan -Name "settings" -Default @{}
    $chunkId = Convert-TimelineText -Value (Get-PropertyValue -Object $Chunk -Name "chunkId" -Default "")
    $previousChunkId = ""
    if ($null -ne $PreviousChunk) {
        $previousChunkId = Convert-TimelineText -Value (Get-PropertyValue -Object $PreviousChunk -Name "chunkId" -Default "")
    }

    return [ordered]@{
        schemaVersion = 1
        createdAt = [DateTimeOffset]::Now.ToString("o")
        chunkId = $chunkId
        language = Convert-TimelineText -Value (Get-PropertyValue -Object $settings -Name "language" -Default "ja-JP")
        model = Convert-TimelineText -Value (Get-PropertyValue -Object $settings -Name "model" -Default "qwen3.5:9b")
        source = $source
        timeRange = [ordered]@{
            startSec = Get-PropertyValue -Object $Chunk -Name "startSec" -Default 0
            endSec = Get-PropertyValue -Object $Chunk -Name "endSec" -Default 0
            durationSec = Get-PropertyValue -Object $Chunk -Name "durationSec" -Default 0
        }
        rollingContext = [ordered]@{
            previousChunkId = $previousChunkId
            previousSummaryPath = $PreviousSummaryPath
            previousSummary = ""
            nearbyContextMinutes = Convert-TimelineAudioInt -Value (Get-PropertyValue -Object $settings -Name "nearbyContextMinutes" -Default 10)
            usePreviousChunkSummary = [bool](Get-PropertyValue -Object $settings -Name "usePreviousChunkSummary" -Default $true)
            useUnconfirmedVerbalizationAsWeakHint = [bool](Get-PropertyValue -Object $settings -Name "useUnconfirmedVerbalizationAsWeakHint" -Default $true)
        }
        turns = @(Get-PropertyValue -Object $Chunk -Name "turns" -Default @())
    }
}

function Write-TimelineAudioVerbalizationContextFiles {
    param(
        [object]$Plan,
        [string]$Directory
    )

    $contextDirectory = Join-Path $Directory "context"
    [System.IO.Directory]::CreateDirectory($contextDirectory) | Out-Null
    $chunks = @(Get-PropertyValue -Object $Plan -Name "chunks" -Default @())
    $written = @()
    $previousChunk = $null
    $previousSummaryPath = ""

    foreach ($chunk in $chunks) {
        $chunkId = Convert-TimelineText -Value (Get-PropertyValue -Object $chunk -Name "chunkId" -Default "")
        if (-not $chunkId) {
            continue
        }

        $context = New-TimelineAudioVerbalizationContext `
            -Plan $Plan `
            -Chunk $chunk `
            -PreviousChunk $previousChunk `
            -PreviousSummaryPath $previousSummaryPath
        $contextPath = Join-Path $contextDirectory "$chunkId.context.json"
        $summaryPath = Join-Path $contextDirectory "$chunkId.summary.json"
        Write-TimelineUtf8JsonFile -Path $contextPath -Payload $context
        if (-not (Test-Path -LiteralPath $summaryPath -PathType Leaf)) {
            Write-TimelineUtf8JsonFile -Path $summaryPath -Payload ([ordered]@{
                schemaVersion = 1
                chunkId = $chunkId
                state = "empty"
                summary = ""
                updatedAt = ""
            })
        }

        $written += [ordered]@{
            chunkId = $chunkId
            contextPath = $contextPath
            summaryPath = $summaryPath
        }
        $previousChunk = $chunk
        $previousSummaryPath = $summaryPath
    }

    return @($written)
}

function Get-TimelineOllamaChatUrl {
    param([string]$BaseUrl)

    $base = (Convert-TimelineText -Value $BaseUrl).TrimEnd("/")
    if (-not $base) {
        $base = "http://127.0.0.1:11434"
    }
    return "$base/api/chat"
}

function ConvertFrom-TimelineLlmJsonText {
    param([string]$Text)

    $jsonText = (Convert-TimelineText -Value $Text)
    $fence = ([string][char]0x60) + ([string][char]0x60) + ([string][char]0x60)
    if ($jsonText.StartsWith($fence)) {
        $escapedFence = [System.Text.RegularExpressions.Regex]::Escape($fence)
        $jsonText = $jsonText -replace ("^" + $escapedFence + "[a-zA-Z0-9_-]*\s*"), ''
        $jsonText = $jsonText -replace ("\s*" + $escapedFence + "$"), ''
        $jsonText = $jsonText.Trim()
    }

    $startIndex = $jsonText.IndexOf("{", [System.StringComparison]::Ordinal)
    $endIndex = $jsonText.LastIndexOf("}", [System.StringComparison]::Ordinal)
    if ($startIndex -ge 0 -and $endIndex -gt $startIndex) {
        $jsonText = $jsonText.Substring($startIndex, $endIndex - $startIndex + 1)
    }

    return $jsonText | ConvertFrom-Json
}

function Invoke-TimelineOllamaChatJson {
    param(
        [object]$VerbalizationSettings,
        [object]$Context
    )

    $model = Convert-TimelineText -Value (Get-PropertyValue -Object $VerbalizationSettings -Name "model" -Default "qwen3.5:9b")
    $baseUrl = Convert-TimelineText -Value (Get-PropertyValue -Object $VerbalizationSettings -Name "ollamaBaseUrl" -Default "http://127.0.0.1:11434")
    $url = Get-TimelineOllamaChatUrl -BaseUrl $baseUrl
    $contextJson = ConvertTo-Json -InputObject $Context -Depth 20
    $systemPrompt = @"
You convert uncertain acoustic phone-token timelines into likely readable text.
Target language is the context language. Treat source tokens as uncertain.
Use file name, timestamps, speaker labels, and rolling context only as weak hints.
Do not invent unsupported details. Prefer low confidence when ambiguous.
Return strict JSON only. Schema:
{
  "summary": "short summary for the next chunk",
  "turns": [
    {
      "turnId": "turn-000001",
      "text": "candidate readable text",
      "confidence": 0.0,
      "status": "candidate|needs_review|unresolved",
      "basis": ["short reason"],
      "uncertainTerms": ["term"]
    }
  ]
}
"@

    $body = [ordered]@{
        model = $model
        stream = $false
        format = "json"
        messages = @(
            [ordered]@{ role = "system"; content = $systemPrompt },
            [ordered]@{ role = "user"; content = $contextJson }
        )
        options = [ordered]@{
            temperature = 0.2
            num_ctx = 8192
        }
    }

    try {
        $response = Invoke-RestMethod `
            -Method Post `
            -Uri $url `
            -Body (ConvertTo-Json -InputObject $body -Depth 20) `
            -ContentType "application/json; charset=utf-8" `
            -TimeoutSec 900
    }
    catch {
        throw "Ollama request failed. Make sure Ollama is running at $baseUrl and model '$model' is available."
    }
    $message = Get-PropertyValue -Object $response -Name "message" -Default $null
    $content = Convert-TimelineText -Value (Get-PropertyValue -Object $message -Name "content" -Default "")
    if (-not $content) {
        throw "Ollama response did not contain message content."
    }
    return ConvertFrom-TimelineLlmJsonText -Text $content
}

function Get-TimelineAudioVerbalizationOllamaStatus {
    param(
        [string]$BaseUrl = "",
        [string]$Model = ""
    )

    $settings = Read-TimelineAppSettings
    $verbalizationSettings = Get-PropertyValue -Object $settings -Name "audioVerbalization" -Default (New-TimelineDefaultAudioVerbalizationSettings)
    $baseUrl = Convert-TimelineText -Value $BaseUrl
    if (-not $baseUrl) {
        $baseUrl = Convert-TimelineText -Value (Get-PropertyValue -Object $verbalizationSettings -Name "ollamaBaseUrl" -Default "http://127.0.0.1:11434")
    }
    if (-not $baseUrl) {
        $baseUrl = "http://127.0.0.1:11434"
    }
    $model = Convert-TimelineText -Value $Model
    if (-not $model) {
        $model = Convert-TimelineText -Value (Get-PropertyValue -Object $verbalizationSettings -Name "model" -Default "qwen3.5:9b")
    }
    $tagsUrl = $baseUrl.TrimEnd("/") + "/api/tags"

    try {
        $response = Invoke-RestMethod -Method Get -Uri $tagsUrl -TimeoutSec 5
        $modelNames = @()
        foreach ($item in @(Get-PropertyValue -Object $response -Name "models" -Default @())) {
            $name = Convert-TimelineText -Value (Get-PropertyValueAny -Object $item -Names @("name", "model") -Default "")
            if ($name) {
                $modelNames += $name
            }
        }

        $modelAvailable = $false
        foreach ($name in $modelNames) {
            if ([string]::Equals($name, $model, [System.StringComparison]::OrdinalIgnoreCase)) {
                $modelAvailable = $true
                break
            }
        }

        return [ordered]@{
            available = $true
            baseUrl = $baseUrl
            model = $model
            modelAvailable = $modelAvailable
            modelNames = @($modelNames)
            message = if ($modelAvailable) { "Ollama is available." } else { "Ollama is running, but the configured model was not found." }
        }
    }
    catch {
        return [ordered]@{
            available = $false
            baseUrl = $baseUrl
            model = $model
            modelAvailable = $false
            modelNames = @()
            message = "Ollama is not reachable."
        }
    }
}

function Convert-TimelineAudioVerbalizedTurn {
    param(
        [object]$SourceTurn,
        [object[]]$LlmTurns
    )

    $turnId = Convert-TimelineText -Value (Get-PropertyValue -Object $SourceTurn -Name "turnId" -Default "")
    $match = $null
    foreach ($candidate in @($LlmTurns)) {
        $candidateId = Convert-TimelineText -Value (Get-PropertyValue -Object $candidate -Name "turnId" -Default "")
        if ($candidateId -eq $turnId) {
            $match = $candidate
            break
        }
    }

    $basis = @()
    $basisValue = Get-PropertyValue -Object $match -Name "basis" -Default @()
    foreach ($item in @($basisValue)) {
        $text = Convert-TimelineText -Value $item
        if ($text) {
            $basis += $text
        }
    }

    $uncertainTerms = @()
    $uncertainValue = Get-PropertyValue -Object $match -Name "uncertainTerms" -Default @()
    foreach ($item in @($uncertainValue)) {
        $text = Convert-TimelineText -Value $item
        if ($text) {
            $uncertainTerms += $text
        }
    }

    return [ordered]@{
        turnId = $turnId
        index = Convert-TimelineAudioInt -Value (Get-PropertyValue -Object $SourceTurn -Name "index" -Default 0)
        startSec = Get-PropertyValue -Object $SourceTurn -Name "startSec" -Default 0
        endSec = Get-PropertyValue -Object $SourceTurn -Name "endSec" -Default 0
        speaker = Convert-TimelineText -Value (Get-PropertyValue -Object $SourceTurn -Name "speaker" -Default "")
        text = Convert-TimelineText -Value (Get-PropertyValue -Object $match -Name "text" -Default "")
        confidence = Convert-TimelineAudioNumber -Value (Get-PropertyValue -Object $match -Name "confidence" -Default $null)
        status = Convert-TimelineText -Value (Get-PropertyValue -Object $match -Name "status" -Default "needs_review")
        basis = @($basis)
        uncertainTerms = @($uncertainTerms)
    }
}

function Write-TimelineAudioVerbalizationResultPayload {
    param(
        [string]$ResultPath,
        [object]$Status,
        [object[]]$Chunks,
        [object[]]$Turns
    )

    Write-TimelineUtf8JsonFile -Path $ResultPath -Payload ([ordered]@{
        schemaVersion = 1
        status = $Status
        turns = @($Turns)
        chunks = @($Chunks)
    })
}

function Update-TimelineAudioVerbalizationTiming {
    param(
        [object]$Status,
        [DateTimeOffset]$StartedAt,
        [int]$CompletedChunks,
        [int]$TotalChunks
    )

    $now = [DateTimeOffset]::Now
    $elapsedSec = [Math]::Max(0, ($now - $StartedAt).TotalSeconds)
    $remainingSec = 0
    if ($CompletedChunks -gt 0 -and $TotalChunks -gt $CompletedChunks) {
        $averageSec = $elapsedSec / $CompletedChunks
        $remainingSec = $averageSec * ($TotalChunks - $CompletedChunks)
    }

    $Status["startedAt"] = $StartedAt.ToString("o")
    $Status["elapsedSec"] = [Math]::Round($elapsedSec, 1)
    $Status["estimatedRemainingSec"] = [Math]::Round($remainingSec, 1)
}

function Copy-TimelineAudioVerbalizationStatus {
    param([object]$Status)

    $copy = [ordered]@{}
    if ($null -eq $Status) {
        return $copy
    }

    if ($Status -is [System.Collections.IDictionary]) {
        foreach ($key in @($Status.Keys)) {
            $copy[[string]$key] = $Status[$key]
        }
        return $copy
    }

    foreach ($property in @($Status.PSObject.Properties)) {
        $copy[[string]$property.Name] = $property.Value
    }
    return $copy
}

function Invoke-TimelineAudioVerbalizationExecution {
    param(
        [object]$Plan,
        [string]$Directory,
        [object]$InitialStatus,
        [string]$ResultPath
    )

    $settings = Get-PropertyValue -Object $Plan -Name "settings" -Default @{}
    if (-not [bool](Get-PropertyValue -Object $settings -Name "enabled" -Default $true)) {
        return $InitialStatus
    }

    $provider = (Convert-TimelineText -Value (Get-PropertyValue -Object $settings -Name "provider" -Default "ollama")).ToLowerInvariant()
    if ($provider -ne "ollama") {
        return $InitialStatus
    }

    $resultsDirectory = Join-Path $Directory "results"
    [System.IO.Directory]::CreateDirectory($resultsDirectory) | Out-Null
    $contextDirectory = Join-Path $Directory "context"
    $chunks = @(Get-PropertyValue -Object $Plan -Name "chunks" -Default @())
    $resultChunks = @()
    $allTurns = @()
    $startedAt = [DateTimeOffset]::Now

    $status = Copy-TimelineAudioVerbalizationStatus -Status $InitialStatus
    $operationId = Convert-TimelineText -Value (Get-PropertyValue -Object $status -Name "jobId" -Default "")
    $status["state"] = "running"
    $status["updatedAt"] = $startedAt.ToString("o")
    $status["message"] = "Audio verbalization is running."
    Update-TimelineAudioVerbalizationTiming -Status $status -StartedAt $startedAt -CompletedChunks 0 -TotalChunks $chunks.Count
    Write-TimelineOperationEvent `
        -OperationId $operationId `
        -Kind "llm" `
        -ProductName "Timeline" `
        -Action "audio_verbalization" `
        -State "running" `
        -Message "Audio verbalization execution started." `
        -Details ([ordered]@{
            totalChunks = $chunks.Count
            resultPath = $ResultPath
        })

    foreach ($chunk in $chunks) {
        $chunkId = Convert-TimelineText -Value (Get-PropertyValue -Object $chunk -Name "chunkId" -Default "")
        if (-not $chunkId) {
            continue
        }

        $status["currentChunkId"] = $chunkId
        $status["updatedAt"] = [DateTimeOffset]::Now.ToString("o")
        Update-TimelineAudioVerbalizationTiming -Status $status -StartedAt $startedAt -CompletedChunks $resultChunks.Count -TotalChunks $chunks.Count
        Write-TimelineAudioVerbalizationResultPayload -ResultPath $ResultPath -Status $status -Chunks $resultChunks -Turns $allTurns
        Write-TimelineOperationEvent `
            -OperationId $operationId `
            -Kind "llm" `
            -ProductName "Timeline" `
            -Action "audio_verbalization_chunk" `
            -State "running" `
            -Message "Audio verbalization chunk started." `
            -Details ([ordered]@{
                chunkId = $chunkId
                completedChunks = $resultChunks.Count
                totalChunks = $chunks.Count
            })

        $contextPath = Join-Path $contextDirectory "$chunkId.context.json"
        $summaryPath = Join-Path $contextDirectory "$chunkId.summary.json"
        $resultChunkPath = Join-Path $resultsDirectory "$chunkId.result.json"
        $contextPayload = Get-Content -LiteralPath $contextPath -Raw -Encoding UTF8 | ConvertFrom-Json
        $rollingContext = Get-PropertyValue -Object $contextPayload -Name "rollingContext" -Default $null
        if ($null -ne $rollingContext) {
            $previousSummaryPath = Convert-TimelineText -Value (Get-PropertyValue -Object $rollingContext -Name "previousSummaryPath" -Default "")
            if ($previousSummaryPath -and (Test-Path -LiteralPath $previousSummaryPath -PathType Leaf)) {
                try {
                    $previousSummary = Convert-TimelineText -Value (Get-PropertyValue -Object (Get-Content -LiteralPath $previousSummaryPath -Raw -Encoding UTF8 | ConvertFrom-Json) -Name "summary" -Default "")
                    $rollingContext.previousSummary = $previousSummary
                    Write-TimelineUtf8JsonFile -Path $contextPath -Payload $contextPayload
                }
                catch {
                }
            }
        }

        try {
            $llmPayload = Invoke-TimelineOllamaChatJson -VerbalizationSettings $settings -Context $contextPayload
            $llmTurns = @(Get-PropertyValue -Object $llmPayload -Name "turns" -Default @())
            $chunkTurns = @(Get-PropertyValue -Object $chunk -Name "turns" -Default @())
            $verbalizedTurns = @()
            foreach ($sourceTurn in $chunkTurns) {
                $verbalizedTurns += Convert-TimelineAudioVerbalizedTurn -SourceTurn $sourceTurn -LlmTurns $llmTurns
            }

            $summary = Convert-TimelineText -Value (Get-PropertyValue -Object $llmPayload -Name "summary" -Default "")
            Write-TimelineUtf8JsonFile -Path $summaryPath -Payload ([ordered]@{
                schemaVersion = 1
                chunkId = $chunkId
                state = "completed"
                summary = $summary
                updatedAt = [DateTimeOffset]::Now.ToString("o")
            })

            $resultChunk = [ordered]@{
                chunkId = $chunkId
                sequence = Convert-TimelineAudioInt -Value (Get-PropertyValue -Object $chunk -Name "sequence" -Default 0)
                state = "completed"
                startSec = Get-PropertyValue -Object $chunk -Name "startSec" -Default 0
                endSec = Get-PropertyValue -Object $chunk -Name "endSec" -Default 0
                turnCount = $verbalizedTurns.Count
                contextPath = $contextPath
                summaryPath = $summaryPath
                resultPath = $resultChunkPath
                summary = $summary
            }
            Write-TimelineUtf8JsonFile -Path $resultChunkPath -Payload ([ordered]@{
                schemaVersion = 1
                chunk = $resultChunk
                turns = @($verbalizedTurns)
            })

            $resultChunks += $resultChunk
            $allTurns += $verbalizedTurns
            $status["completedChunks"] = $resultChunks.Count
            $status["verbalizedTurns"] = $allTurns.Count
            Update-TimelineAudioVerbalizationTiming -Status $status -StartedAt $startedAt -CompletedChunks $resultChunks.Count -TotalChunks $chunks.Count
            Write-TimelineOperationEvent `
                -OperationId $operationId `
                -Kind "llm" `
                -ProductName "Timeline" `
                -Action "audio_verbalization_chunk" `
                -State "completed" `
                -Message "Audio verbalization chunk completed." `
                -Details ([ordered]@{
                    chunkId = $chunkId
                    completedChunks = $resultChunks.Count
                    totalChunks = $chunks.Count
                    turnCount = $verbalizedTurns.Count
                })
        }
        catch {
            $failedChunk = [ordered]@{
                chunkId = $chunkId
                sequence = Convert-TimelineAudioInt -Value (Get-PropertyValue -Object $chunk -Name "sequence" -Default 0)
                state = "failed"
                startSec = Get-PropertyValue -Object $chunk -Name "startSec" -Default 0
                endSec = Get-PropertyValue -Object $chunk -Name "endSec" -Default 0
                turnCount = Convert-TimelineAudioInt -Value (Get-PropertyValue -Object $chunk -Name "turnCount" -Default 0)
                contextPath = $contextPath
                summaryPath = $summaryPath
                resultPath = $resultChunkPath
                retryCount = 0
                error = $_.Exception.Message
            }
            Write-TimelineUtf8JsonFile -Path $resultChunkPath -Payload ([ordered]@{
                schemaVersion = 1
                chunk = $failedChunk
                turns = @()
            })
            $resultChunks += $failedChunk
            $status["state"] = "failed"
            $status["updatedAt"] = [DateTimeOffset]::Now.ToString("o")
            $status["message"] = $_.Exception.Message
            Update-TimelineAudioVerbalizationTiming -Status $status -StartedAt $startedAt -CompletedChunks $resultChunks.Count -TotalChunks $chunks.Count
            Write-TimelineAudioVerbalizationResultPayload -ResultPath $ResultPath -Status $status -Chunks $resultChunks -Turns $allTurns
            Write-TimelineOperationEvent `
                -OperationId $operationId `
                -Kind "llm" `
                -ProductName "Timeline" `
                -Action "audio_verbalization_chunk" `
                -State "failed" `
                -Message $_.Exception.Message `
                -Details ([ordered]@{
                    chunkId = $chunkId
                    completedChunks = $resultChunks.Count
                    totalChunks = $chunks.Count
                })
            return $status
        }
    }

    $status["state"] = "completed"
    $status["currentChunkId"] = ""
    $status["updatedAt"] = [DateTimeOffset]::Now.ToString("o")
    $status["message"] = "Audio verbalization completed."
    $status["estimatedRemainingSec"] = 0
    Update-TimelineAudioVerbalizationTiming -Status $status -StartedAt $startedAt -CompletedChunks $resultChunks.Count -TotalChunks $chunks.Count
    Write-TimelineAudioVerbalizationResultPayload -ResultPath $ResultPath -Status $status -Chunks $resultChunks -Turns $allTurns
    Write-TimelineOperationEvent `
        -OperationId $operationId `
        -Kind "llm" `
        -ProductName "Timeline" `
        -Action "audio_verbalization" `
        -State "completed" `
        -Message "Audio verbalization completed." `
        -DurationMs ([int]([DateTimeOffset]::Now - $startedAt).TotalMilliseconds) `
        -Details ([ordered]@{
            completedChunks = $resultChunks.Count
            totalChunks = $chunks.Count
            verbalizedTurns = $allTurns.Count
            resultPath = $ResultPath
        })
    return $status
}

function New-TimelineAudioVerbalizationJobId {
    $stamp = Get-Date -Format "yyyyMMdd-HHmmss"
    $suffix = ([guid]::NewGuid().ToString("N")).Substring(0, 8)
    return "audio-verbalization-$stamp-$suffix"
}

function Start-TimelineAudioVerbalizationWorker {
    param(
        [string]$AudioItemId,
        [string]$JobId
    )

    if (-not $AudioItemId) {
        throw "Audio item id is required."
    }
    if (-not $JobId) {
        throw "Audio verbalization job id is required."
    }

    $workerScript = Join-Path $TimelineProductPath "scripts\audio-verbalization-worker.ps1"
    if (-not (Test-Path -LiteralPath $workerScript -PathType Leaf)) {
        throw "Audio verbalization worker script was not found."
    }

    Write-TimelineOperationEvent `
        -OperationId $JobId `
        -Kind "worker" `
        -ProductName "Timeline" `
        -Action "audio_verbalization" `
        -State "starting" `
        -Message "Audio verbalization worker process is starting." `
        -Details ([ordered]@{
            audioItemId = $AudioItemId
            workerScript = $workerScript
        })

    $arguments = @(
        "-NoLogo",
        "-NoProfile",
        "-STA",
        "-ExecutionPolicy",
        "Bypass",
        "-File",
        $workerScript,
        "-JobId",
        $JobId,
        "-AudioItemId",
        $AudioItemId,
        "-TimelineProductPath",
        $TimelineProductPath,
        "-AudioProductPath",
        $AudioProductPath,
        "-WindowsCodexProductPath",
        $WindowsCodexProductPath,
        "-ChatGptProductPath",
        $ChatGptProductPath,
        "-ImageProductPath",
        $ImageProductPath
    )

    $startInfo = [System.Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = Get-TimelinePowerShellPath
    $startInfo.Arguments = ($arguments | ForEach-Object { Format-TimelineProcessArgument -Value ([string]$_) }) -join " "
    $startInfo.WorkingDirectory = $TimelineProductPath
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $environment = Get-TimelineChildProcessEnvironment
    foreach ($key in @($environment.Keys)) {
        $startInfo.EnvironmentVariables[[string]$key] = [string]$environment[$key]
    }

    [System.Diagnostics.Process]::Start($startInfo) | Out-Null
    Write-TimelineOperationEvent `
        -OperationId $JobId `
        -Kind "worker" `
        -ProductName "Timeline" `
        -Action "audio_verbalization" `
        -State "queued" `
        -Message "Audio verbalization worker process was started." `
        -Details ([ordered]@{
            audioItemId = $AudioItemId
            workerScript = $workerScript
        })
}

function Get-TimelineAudioVerbalizationStatusFromDetail {
    param([object]$Detail)

    if ($null -eq $Detail -or -not [bool](Get-PropertyValue -Object $Detail -Name "available" -Default $false)) {
        return [ordered]@{
            available = $false
            state = "unavailable"
            audioItemId = ""
            sourceFileIdentity = ""
            language = "ja-JP"
            model = "qwen3.5:9b"
            totalTurns = 0
            verbalizedTurns = 0
            totalChunks = 0
            completedChunks = 0
            jobId = ""
            currentChunkId = ""
            planPath = ""
            resultPath = ""
            updatedAt = ""
            message = "Audio file detail was not available."
        }
    }

    $file = Get-PropertyValue -Object $Detail -Name "file" -Default $null
    $audioItemId = Convert-TimelineText -Value (Get-PropertyValue -Object $file -Name "itemId" -Default "")
    $sourceFileIdentity = Convert-TimelineText -Value (Get-PropertyValue -Object $file -Name "sourceFileIdentity" -Default "")
    $turns = @(Get-PropertyValue -Object $Detail -Name "turns" -Default @())
    $settings = Read-TimelineAppSettings
    $verbalizationSettings = Get-PropertyValue -Object $settings -Name "audioVerbalization" -Default (New-TimelineDefaultAudioVerbalizationSettings)
    $language = Convert-TimelineText -Value (Get-PropertyValue -Object $verbalizationSettings -Name "language" -Default "ja-JP")
    $model = Convert-TimelineText -Value (Get-PropertyValue -Object $verbalizationSettings -Name "model" -Default "qwen3.5:9b")

    if (-not [bool](Get-PropertyValue -Object $Detail -Name "timelineAvailable" -Default $false)) {
        return [ordered]@{
            available = $false
            state = "unavailable"
            audioItemId = $audioItemId
            sourceFileIdentity = $sourceFileIdentity
            language = $language
            model = $model
            totalTurns = @($turns).Count
            verbalizedTurns = 0
            totalChunks = 0
            completedChunks = 0
            jobId = ""
            currentChunkId = ""
            planPath = ""
            resultPath = ""
            updatedAt = ""
            message = "Audio timeline was not available."
        }
    }

    $directory = Get-TimelineAudioVerbalizationDirectory -AudioItemId $audioItemId
    $planPath = Join-Path $directory "verbalization-plan.json"
    $resultPath = Join-Path $directory "audio-verbalization.json"
    if (-not (Test-Path -LiteralPath $resultPath -PathType Leaf)) {
        $state = "not_started"
        $totalChunks = 0
        $updatedAt = ""
        if (Test-Path -LiteralPath $planPath -PathType Leaf) {
            try {
                $planPayload = Get-Content -LiteralPath $planPath -Raw -Encoding UTF8 | ConvertFrom-Json
                $totalChunks = @(Get-PropertyValue -Object $planPayload -Name "chunks" -Default @()).Count
                $updatedAt = Convert-TimelineText -Value (Get-PropertyValue -Object $planPayload -Name "createdAt" -Default "")
                $state = "planned"
            }
            catch {
                $state = "unreadable"
            }
        }
        return [ordered]@{
            available = $true
            state = $state
            audioItemId = $audioItemId
            sourceFileIdentity = $sourceFileIdentity
            language = $language
            model = $model
            totalTurns = @($turns).Count
            verbalizedTurns = 0
            totalChunks = $totalChunks
            completedChunks = 0
            jobId = ""
            currentChunkId = ""
            planPath = $planPath
            resultPath = $resultPath
            updatedAt = $updatedAt
            message = ""
        }
    }

    try {
        $payload = Get-Content -LiteralPath $resultPath -Raw -Encoding UTF8 | ConvertFrom-Json
        $status = Get-PropertyValue -Object $payload -Name "status" -Default @{}
        $verbalizedTurns = @(Get-PropertyValue -Object $payload -Name "turns" -Default @()).Count
        return [ordered]@{
            available = $true
            state = Convert-TimelineText -Value (Get-PropertyValue -Object $status -Name "state" -Default "completed")
            audioItemId = $audioItemId
            sourceFileIdentity = $sourceFileIdentity
            language = Convert-TimelineText -Value (Get-PropertyValue -Object $status -Name "language" -Default $language)
            model = Convert-TimelineText -Value (Get-PropertyValue -Object $status -Name "model" -Default $model)
            totalTurns = Convert-TimelineAudioInt -Value (Get-PropertyValue -Object $status -Name "totalTurns" -Default @($turns).Count)
            verbalizedTurns = Convert-TimelineAudioInt -Value (Get-PropertyValue -Object $status -Name "verbalizedTurns" -Default $verbalizedTurns)
            totalChunks = Convert-TimelineAudioInt -Value (Get-PropertyValue -Object $status -Name "totalChunks" -Default 0)
            completedChunks = Convert-TimelineAudioInt -Value (Get-PropertyValue -Object $status -Name "completedChunks" -Default 0)
            jobId = Convert-TimelineText -Value (Get-PropertyValue -Object $status -Name "jobId" -Default "")
            currentChunkId = Convert-TimelineText -Value (Get-PropertyValue -Object $status -Name "currentChunkId" -Default "")
            planPath = $planPath
            resultPath = $resultPath
            startedAt = Convert-TimelineText -Value (Get-PropertyValue -Object $status -Name "startedAt" -Default "")
            elapsedSec = [double](Convert-TimelineAudioNumber -Value (Get-PropertyValue -Object $status -Name "elapsedSec" -Default 0))
            estimatedRemainingSec = [double](Convert-TimelineAudioNumber -Value (Get-PropertyValue -Object $status -Name "estimatedRemainingSec" -Default 0))
            updatedAt = Convert-TimelineText -Value (Get-PropertyValue -Object $status -Name "updatedAt" -Default "")
            message = Convert-TimelineText -Value (Get-PropertyValue -Object $status -Name "message" -Default "")
        }
    }
    catch {
        return [ordered]@{
            available = $true
            state = "unreadable"
            audioItemId = $audioItemId
            sourceFileIdentity = $sourceFileIdentity
            language = $language
            model = $model
            totalTurns = @($turns).Count
            verbalizedTurns = 0
            totalChunks = 0
            completedChunks = 0
            jobId = ""
            currentChunkId = ""
            planPath = $planPath
            resultPath = $resultPath
            updatedAt = ""
            message = $_.Exception.Message
        }
    }
}

function Get-TimelineAudioVerbalizationStatusFromFileRow {
    param(
        [object]$FileRow,
        [object]$AppSettings = $null
    )

    if ($null -eq $FileRow) {
        return [ordered]@{
            available = $false
            state = "unavailable"
            audioItemId = ""
            sourceFileIdentity = ""
            language = "ja-JP"
            model = "qwen3.5:9b"
            totalTurns = 0
            verbalizedTurns = 0
            totalChunks = 0
            completedChunks = 0
            jobId = ""
            currentChunkId = ""
            planPath = ""
            resultPath = ""
            updatedAt = ""
            message = "Audio file row was not available."
        }
    }

    $settings = $AppSettings
    if ($null -eq $settings) {
        $settings = Read-TimelineAppSettings
    }
    $verbalizationSettings = Get-PropertyValue -Object $settings -Name "audioVerbalization" -Default (New-TimelineDefaultAudioVerbalizationSettings)
    $language = Convert-TimelineText -Value (Get-PropertyValue -Object $verbalizationSettings -Name "language" -Default "ja-JP")
    $model = Convert-TimelineText -Value (Get-PropertyValue -Object $verbalizationSettings -Name "model" -Default "qwen3.5:9b")
    $audioItemId = Convert-TimelineText -Value (Get-PropertyValue -Object $FileRow -Name "itemId" -Default "")
    $sourceFileIdentity = Convert-TimelineText -Value (Get-PropertyValue -Object $FileRow -Name "sourceFileIdentity" -Default "")
    $totalTurns = Convert-TimelineAudioInt -Value (Get-PropertyValue -Object $FileRow -Name "turnCount" -Default 0)

    if (-not [bool](Get-PropertyValue -Object $FileRow -Name "hasTimeline" -Default $false)) {
        return [ordered]@{
            available = $false
            state = "unavailable"
            audioItemId = $audioItemId
            sourceFileIdentity = $sourceFileIdentity
            language = $language
            model = $model
            totalTurns = $totalTurns
            verbalizedTurns = 0
            totalChunks = 0
            completedChunks = 0
            jobId = ""
            currentChunkId = ""
            planPath = ""
            resultPath = ""
            updatedAt = ""
            message = "Audio timeline was not available."
        }
    }

    if (-not $audioItemId) {
        return [ordered]@{
            available = $false
            state = "unavailable"
            audioItemId = ""
            sourceFileIdentity = $sourceFileIdentity
            language = $language
            model = $model
            totalTurns = $totalTurns
            verbalizedTurns = 0
            totalChunks = 0
            completedChunks = 0
            jobId = ""
            currentChunkId = ""
            planPath = ""
            resultPath = ""
            updatedAt = ""
            message = "Audio item ID was not available."
        }
    }

    $directory = Get-TimelineAudioVerbalizationDirectory -AudioItemId $audioItemId
    $planPath = Join-Path $directory "verbalization-plan.json"
    $resultPath = Join-Path $directory "audio-verbalization.json"

    if (-not (Test-Path -LiteralPath $resultPath -PathType Leaf)) {
        $state = "not_started"
        $totalChunks = 0
        $updatedAt = ""
        if (Test-Path -LiteralPath $planPath -PathType Leaf) {
            try {
                $planPayload = Get-Content -LiteralPath $planPath -Raw -Encoding UTF8 | ConvertFrom-Json
                $totalChunks = @(Get-PropertyValue -Object $planPayload -Name "chunks" -Default @()).Count
                $updatedAt = Convert-TimelineText -Value (Get-PropertyValue -Object $planPayload -Name "createdAt" -Default "")
                $state = "planned"
            }
            catch {
                $state = "unreadable"
            }
        }
        return [ordered]@{
            available = $true
            state = $state
            audioItemId = $audioItemId
            sourceFileIdentity = $sourceFileIdentity
            language = $language
            model = $model
            totalTurns = $totalTurns
            verbalizedTurns = 0
            totalChunks = $totalChunks
            completedChunks = 0
            jobId = ""
            currentChunkId = ""
            planPath = $planPath
            resultPath = $resultPath
            updatedAt = $updatedAt
            message = ""
        }
    }

    try {
        $payload = Get-Content -LiteralPath $resultPath -Raw -Encoding UTF8 | ConvertFrom-Json
        $status = Get-PropertyValue -Object $payload -Name "status" -Default @{}
        $verbalizedTurns = @(Get-PropertyValue -Object $payload -Name "turns" -Default @()).Count
        return [ordered]@{
            available = $true
            state = Convert-TimelineText -Value (Get-PropertyValue -Object $status -Name "state" -Default "completed")
            audioItemId = $audioItemId
            sourceFileIdentity = $sourceFileIdentity
            language = Convert-TimelineText -Value (Get-PropertyValue -Object $status -Name "language" -Default $language)
            model = Convert-TimelineText -Value (Get-PropertyValue -Object $status -Name "model" -Default $model)
            totalTurns = Convert-TimelineAudioInt -Value (Get-PropertyValue -Object $status -Name "totalTurns" -Default $totalTurns)
            verbalizedTurns = Convert-TimelineAudioInt -Value (Get-PropertyValue -Object $status -Name "verbalizedTurns" -Default $verbalizedTurns)
            totalChunks = Convert-TimelineAudioInt -Value (Get-PropertyValue -Object $status -Name "totalChunks" -Default 0)
            completedChunks = Convert-TimelineAudioInt -Value (Get-PropertyValue -Object $status -Name "completedChunks" -Default 0)
            jobId = Convert-TimelineText -Value (Get-PropertyValue -Object $status -Name "jobId" -Default "")
            currentChunkId = Convert-TimelineText -Value (Get-PropertyValue -Object $status -Name "currentChunkId" -Default "")
            planPath = $planPath
            resultPath = $resultPath
            startedAt = Convert-TimelineText -Value (Get-PropertyValue -Object $status -Name "startedAt" -Default "")
            elapsedSec = [double](Convert-TimelineAudioNumber -Value (Get-PropertyValue -Object $status -Name "elapsedSec" -Default 0))
            estimatedRemainingSec = [double](Convert-TimelineAudioNumber -Value (Get-PropertyValue -Object $status -Name "estimatedRemainingSec" -Default 0))
            updatedAt = Convert-TimelineText -Value (Get-PropertyValue -Object $status -Name "updatedAt" -Default "")
            message = Convert-TimelineText -Value (Get-PropertyValue -Object $status -Name "message" -Default "")
        }
    }
    catch {
        return [ordered]@{
            available = $true
            state = "unreadable"
            audioItemId = $audioItemId
            sourceFileIdentity = $sourceFileIdentity
            language = $language
            model = $model
            totalTurns = $totalTurns
            verbalizedTurns = 0
            totalChunks = 0
            completedChunks = 0
            jobId = ""
            currentChunkId = ""
            planPath = $planPath
            resultPath = $resultPath
            updatedAt = ""
            message = $_.Exception.Message
        }
    }
}

function Get-TimelineAudioVerbalizationStatus {
    param(
        [string]$SourceId,
        [string]$RelativePath
    )

    $detail = Get-TimelineAudioFileDetail -SourceId $SourceId -RelativePath $RelativePath
    return Get-TimelineAudioVerbalizationStatusFromDetail -Detail $detail
}

function Get-TimelineAudioVerbalizationResult {
    param(
        [string]$SourceId,
        [string]$RelativePath
    )

    $detail = Get-TimelineAudioFileDetail -SourceId $SourceId -RelativePath $RelativePath
    $status = Get-TimelineAudioVerbalizationStatusFromDetail -Detail $detail
    $resultPath = Convert-TimelineText -Value (Get-PropertyValue -Object $status -Name "resultPath" -Default "")
    if (-not $resultPath -or -not (Test-Path -LiteralPath $resultPath -PathType Leaf)) {
        return [ordered]@{
            available = [bool](Get-PropertyValue -Object $status -Name "available" -Default $false)
            status = $status
            turns = @()
            chunks = @()
            message = "Audio verbalization result was not found."
        }
    }

    try {
        $payload = Get-Content -LiteralPath $resultPath -Raw -Encoding UTF8 | ConvertFrom-Json
        return [ordered]@{
            available = $true
            status = (Get-PropertyValue -Object $payload -Name "status" -Default $status)
            turns = @(Get-PropertyValue -Object $payload -Name "turns" -Default @())
            chunks = @(Get-PropertyValue -Object $payload -Name "chunks" -Default @())
            message = ""
        }
    }
    catch {
        return [ordered]@{
            available = $false
            status = $status
            turns = @()
            chunks = @()
            message = $_.Exception.Message
        }
    }
}

function Start-TimelineAudioVerbalization {
    param([object]$Request)

    $sourceId = Convert-TimelineText -Value (Get-PropertyValue -Object $Request -Name "sourceId" -Default "")
    $relativePath = Convert-TimelineText -Value (Get-PropertyValue -Object $Request -Name "relativePath" -Default "")
    if (-not $relativePath) {
        $relativePath = Convert-TimelineText -Value (Get-PropertyValue -Object $Request -Name "path" -Default "")
    }

    $detail = Get-TimelineAudioFileDetail -SourceId $sourceId -RelativePath $relativePath
    $status = Get-TimelineAudioVerbalizationStatusFromDetail -Detail $detail
    if (-not [bool](Get-PropertyValue -Object $status -Name "available" -Default $false)) {
        return $status
    }
    $force = [bool](Get-PropertyValue -Object $Request -Name "force" -Default $false)
    $currentState = (Convert-TimelineText -Value (Get-PropertyValue -Object $status -Name "state" -Default "")).ToLowerInvariant()
    if (-not $force -and @("queued", "running") -contains $currentState) {
        return $status
    }

    $settings = Read-TimelineAppSettings
    $verbalizationSettings = Get-PropertyValue -Object $settings -Name "audioVerbalization" -Default (New-TimelineDefaultAudioVerbalizationSettings)
    $audioItemId = Convert-TimelineText -Value (Get-PropertyValue -Object $status -Name "audioItemId" -Default "")
    $directory = Get-TimelineAudioVerbalizationDirectory -AudioItemId $audioItemId -Create
    [System.IO.Directory]::CreateDirectory((Join-Path $directory "context")) | Out-Null
    [System.IO.Directory]::CreateDirectory((Join-Path $directory "results")) | Out-Null

    $plan = New-TimelineAudioVerbalizationPlan -Detail $detail -VerbalizationSettings $verbalizationSettings
    $planPath = Join-Path $directory "verbalization-plan.json"
    $resultPath = Join-Path $directory "audio-verbalization.json"
    Write-TimelineUtf8JsonFile -Path $planPath -Payload $plan
    [void](Write-TimelineAudioVerbalizationContextFiles -Plan $plan -Directory $directory)
    $contextDirectory = Join-Path $directory "context"

    $chunks = @(Get-PropertyValue -Object $plan -Name "chunks" -Default @())
    $now = [DateTimeOffset]::Now.ToString("o")
    $jobId = New-TimelineAudioVerbalizationJobId
    $plannedStatus = [ordered]@{
        available = $true
        state = "queued"
        audioItemId = $audioItemId
        sourceFileIdentity = Convert-TimelineText -Value (Get-PropertyValue -Object $status -Name "sourceFileIdentity" -Default "")
        language = Convert-TimelineText -Value (Get-PropertyValue -Object $verbalizationSettings -Name "language" -Default "ja-JP")
        model = Convert-TimelineText -Value (Get-PropertyValue -Object $verbalizationSettings -Name "model" -Default "qwen3.5:9b")
        totalTurns = Convert-TimelineAudioInt -Value (Get-PropertyValue -Object $status -Name "totalTurns" -Default 0)
        verbalizedTurns = 0
        totalChunks = $chunks.Count
        completedChunks = 0
        jobId = $jobId
        currentChunkId = if ($chunks.Count -gt 0) { Convert-TimelineText -Value (Get-PropertyValue -Object $chunks[0] -Name "chunkId" -Default "") } else { "" }
        planPath = $planPath
        resultPath = $resultPath
        startedAt = ""
        elapsedSec = 0
        estimatedRemainingSec = 0
        updatedAt = $now
        message = "Audio verbalization worker has been queued."
    }
    Write-TimelineOperationEvent `
        -OperationId $jobId `
        -Kind "operation" `
        -ProductName "Timeline" `
        -Action "audio_verbalization" `
        -State "queued" `
        -Message "Audio verbalization was queued from the helper API." `
        -Details ([ordered]@{
            sourceId = $sourceId
            relativePath = $relativePath
            audioItemId = $audioItemId
            totalChunks = $chunks.Count
            totalTurns = Convert-TimelineAudioInt -Value (Get-PropertyValue -Object $status -Name "totalTurns" -Default 0)
            resultPath = $resultPath
        })

    Write-TimelineUtf8JsonFile -Path $resultPath -Payload ([ordered]@{
        schemaVersion = 1
        status = $plannedStatus
        turns = @()
        chunks = @($chunks | ForEach-Object {
            $chunkId = Convert-TimelineText -Value (Get-PropertyValue -Object $_ -Name "chunkId" -Default "")
            [ordered]@{
                chunkId = $chunkId
                sequence = Convert-TimelineAudioInt -Value (Get-PropertyValue -Object $_ -Name "sequence" -Default 0)
                state = Convert-TimelineText -Value (Get-PropertyValue -Object $_ -Name "state" -Default "planned")
                startSec = Get-PropertyValue -Object $_ -Name "startSec" -Default 0
                endSec = Get-PropertyValue -Object $_ -Name "endSec" -Default 0
                turnCount = Convert-TimelineAudioInt -Value (Get-PropertyValue -Object $_ -Name "turnCount" -Default 0)
                contextPath = if ($chunkId) { Join-Path $contextDirectory "$chunkId.context.json" } else { "" }
                summaryPath = if ($chunkId) { Join-Path $contextDirectory "$chunkId.summary.json" } else { "" }
            }
        })
    })

    try {
        Start-TimelineAudioVerbalizationWorker -AudioItemId $audioItemId -JobId $jobId
    }
    catch {
        $plannedStatus["state"] = "failed"
        $plannedStatus["updatedAt"] = [DateTimeOffset]::Now.ToString("o")
        $plannedStatus["message"] = $_.Exception.Message
        Write-TimelineOperationEvent `
            -OperationId $jobId `
            -Kind "worker" `
            -ProductName "Timeline" `
            -Action "audio_verbalization" `
            -State "failed" `
            -Message $_.Exception.Message `
            -Details ([ordered]@{
                audioItemId = $audioItemId
                resultPath = $resultPath
            })
        $existingChunks = @()
        try {
            $existingPayload = Get-Content -LiteralPath $resultPath -Raw -Encoding UTF8 | ConvertFrom-Json
            $existingChunks = @(Get-PropertyValue -Object $existingPayload -Name "chunks" -Default @())
        }
        catch {
        }
        Write-TimelineAudioVerbalizationResultPayload -ResultPath $resultPath -Status $plannedStatus -Chunks $existingChunks -Turns @()
    }

    return $plannedStatus
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
    if (Test-Path -LiteralPath $catalogPath) {
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
    }

    if ($rows.Count -gt 0 -or -not (Test-Path -LiteralPath $outputRootPath -PathType Container)) {
        return $rows
    }

    foreach ($directory in Get-ChildItem -LiteralPath $outputRootPath -Directory -ErrorAction SilentlyContinue) {
        $convertInfoPath = Join-Path $directory.FullName "convert_info.json"
        if (-not (Test-Path -LiteralPath $convertInfoPath -PathType Leaf)) {
            continue
        }

        try {
            $convertInfo = Get-Content -LiteralPath $convertInfoPath -Raw -Encoding UTF8 | ConvertFrom-Json
            $source = Get-PropertyValue -Object $convertInfo -Name "source" -Default @{}
            $identity = [string](Get-PropertyValue -Object $source -Name "source_file_identity" -Default "")
            if (-not $identity) {
                continue
            }

            $pipeline = Get-PropertyValue -Object $convertInfo -Name "pipeline" -Default @{}
            $phone = Get-PropertyValue -Object $pipeline -Name "phone_recognition" -Default @{}
            $rows[$identity] = [ordered]@{
                source_file_identity = $identity
                duration_sec = Get-PropertyValue -Object $source -Name "duration_sec" -Default $null
                duration_seconds = Get-PropertyValue -Object $source -Name "duration_sec" -Default $null
                media_id = [string]$directory.Name
                audio_id = [string]$directory.Name
                run_id = ""
                turn_count = Get-PropertyValue -Object $phone -Name "turn_count" -Default 0
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
    if (-not $mediaId) {
        return ""
    }

    $directMediaDir = Join-Path $OutputRootPath $mediaId
    if (Test-Path -LiteralPath $directMediaDir -PathType Container) {
        return $directMediaDir
    }

    if (-not $runId) {
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
        $timelinePath = Join-Path $MediaDirectory "timeline.json"
    }
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

    $appSettings = Read-TimelineAppSettings
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

        $fileRow = [ordered]@{
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
        $fileRow["audioVerbalization"] = Get-TimelineAudioVerbalizationStatusFromFileRow -FileRow $fileRow -AppSettings $appSettings
        $rows += $fileRow
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
    $appSettings = Read-TimelineAppSettings
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
            $sourceId = [string]$root.path
            $sourceFileIdentity = "$sourceId::$identityRelativePath"
            $catalogRow = $null
            if ($catalogByIdentity.ContainsKey($sourceFileIdentity)) {
                $catalogRow = $catalogByIdentity[$sourceFileIdentity]
            }

            $durationSec = $null
            if ($null -ne $catalogRow) {
                $durationSec = Convert-TimelineAudioNumber -Value (Get-PropertyValue -Object $catalogRow -Name "duration_seconds" -Default (Get-PropertyValue -Object $catalogRow -Name "duration_sec" -Default $null))
            }

            $mediaId = [string](Get-PropertyValue -Object $catalogRow -Name "audio_id" -Default (Get-PropertyValue -Object $catalogRow -Name "media_id" -Default ""))
            $itemId = if ($mediaId) { $mediaId } else { $sourceFileIdentity }
            $mediaDirectory = Get-TimelineAudioMediaDirectory -OutputRootPath $outputRootPath -CatalogRow $catalogRow
            $hasTimeline = $false
            $hasAudio = $false
            if ($mediaDirectory) {
                $hasTimeline = (Test-Path -LiteralPath (Join-Path $mediaDirectory "timeline\speaker-acoustic-units-timeline.json") -PathType Leaf) `
                    -or (Test-Path -LiteralPath (Join-Path $mediaDirectory "timeline.json") -PathType Leaf)
                $hasAudio = Test-Path -LiteralPath (Join-Path $mediaDirectory "source\audio-normalized.wav") -PathType Leaf
            }
            $status = if ($hasTimeline) { "completed" } else { "detected" }
            $turnCount = Convert-TimelineAudioInt -Value (Get-PropertyValueAny -Object $catalogRow -Names @("turn_count", "turnCount") -Default 0)
            $speakerCount = Convert-TimelineAudioInt -Value (Get-PropertyValueAny -Object $catalogRow -Names @("speaker_count", "speakerCount") -Default 0)

            $fileRow = [ordered]@{
                itemId = $itemId
                sourceId = $sourceId
                sourceFileIdentity = $sourceFileIdentity
                sourceDisplayName = $sourceId
                sourceName = $sourceId
                rootPath = [string]$root.path
                displayPath = $file.FullName
                relativePath = $relativePath
                directory = $directory
                fileName = $file.Name
                sizeBytes = [int64]$file.Length
                modifiedAt = $file.LastWriteTime.ToString("yyyy-MM-dd HH:mm:ss")
                status = $status
                durationSec = $durationSec
                hasTimeline = [bool]$hasTimeline
                hasAudio = [bool]$hasAudio
                runId = [string](Get-PropertyValue -Object $catalogRow -Name "run_id" -Default "")
                mediaId = $mediaId
                turnCount = $turnCount
                speakerCount = $speakerCount
            }
            $fileRow["audioVerbalization"] = Get-TimelineAudioVerbalizationStatusFromFileRow -FileRow $fileRow -AppSettings $appSettings
            $allRows += $fileRow
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

    return Get-TimelineAudioFilesFromSettings -Page $Page -PageSize $PageSize
}

function Get-TimelineAudioOverview {
    $settings = Read-TimelineAudioSettings
    $outputRoot = Get-TimelineAudioOutputRoot -Settings $settings
    $hardware = Get-TimelineHardwareDevices
    $activeRun = Get-TimelineActiveAudioRun -Settings $settings
    $audioFileCount = Convert-TimelineAudioInt -Value (Get-PropertyValue -Object $activeRun -Name "itemsTotal" -Default 0)
    if ($audioFileCount -le 0) {
        $files = Get-TimelineAudioFilesFromSettings -Page 1 -PageSize 1
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

if ($ImportOnly) {
    return
}

$listener = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Any, $Port)
try {
    $listener.Start()
}
catch {
    throw "Timeline helper server failed to listen on port $Port. $($_.Exception.Message)"
}

try {
    while ($true) {
        $client = $listener.AcceptTcpClient()
        $client.ReceiveTimeout = 10000
        $client.SendTimeout = 10000
        $client.NoDelay = $true
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

            if ($method -eq "GET" -and $uri.AbsolutePath -eq "/timeline/console/logs") {
                $query = [System.Web.HttpUtility]::ParseQueryString($uri.Query)
                $afterId = 0L
                [void][long]::TryParse(([string]$query["afterId"]), [ref]$afterId)
                $limit = 120
                [void][int]::TryParse(([string]$query["limit"]), [ref]$limit)
                Send-TimelineResponse -Client $client -StatusCode 200 -StatusText "OK" -Origin $origin -Body (ConvertTo-TimelineJson (Get-TimelineConsoleLogs -AfterId $afterId -Limit $limit))
                continue
            }

            if ($method -eq "POST" -and $uri.AbsolutePath -eq "/timeline/console/clear") {
                Send-TimelineResponse -Client $client -StatusCode 200 -StatusText "OK" -Origin $origin -Body (ConvertTo-TimelineJson (Clear-TimelineConsoleLogs))
                continue
            }

            if ($method -eq "GET" -and $uri.AbsolutePath -eq "/timeline/store/overview") {
                Send-TimelineResponse -Client $client -StatusCode 200 -StatusText "OK" -Origin $origin -Body (ConvertTo-TimelineJson (Get-TimelineStoreOverview))
                continue
            }

            if ($method -eq "POST" -and $uri.AbsolutePath -eq "/timeline/rebuild") {
                Send-TimelineResponse -Client $client -StatusCode 200 -StatusText "OK" -Origin $origin -Body (ConvertTo-TimelineJson (Start-TimelineStoreRebuildWorker))
                continue
            }

            if ($method -eq "GET" -and $uri.AbsolutePath -eq "/timeline/rebuild/status") {
                $query = [System.Web.HttpUtility]::ParseQueryString($uri.Query)
                $jobId = Convert-TimelineText -Value ([string]$query["jobId"])
                $status = if ($jobId) { Read-TimelineWorkerJobStatus -JobId $jobId } else { Get-TimelineLatestWorkerJobStatus }
                Send-TimelineResponse -Client $client -StatusCode 200 -StatusText "OK" -Origin $origin -Body (ConvertTo-TimelineJson $status)
                continue
            }

            if ($method -eq "GET" -and $uri.AbsolutePath -eq "/timeline/worker/status") {
                Send-TimelineResponse -Client $client -StatusCode 200 -StatusText "OK" -Origin $origin -Body (ConvertTo-TimelineJson (Get-TimelineDockerWorkerStatus))
                continue
            }

            if ($method -eq "GET" -and $uri.AbsolutePath -eq "/timeline/events") {
                $query = [System.Web.HttpUtility]::ParseQueryString($uri.Query)
                Send-TimelineResponse -Client $client -StatusCode 200 -StatusText "OK" -Origin $origin -Body (ConvertTo-TimelineJson (Get-TimelineStoreEvents -Page (Get-TimelineRequestPage -Query $query) -PageSize (Get-TimelineRequestPageSize -Query $query)))
                continue
            }

            if ($method -eq "GET" -and $uri.AbsolutePath -eq "/timeline/audio-verbalization/status") {
                $query = [System.Web.HttpUtility]::ParseQueryString($uri.Query)
                Send-TimelineResponse -Client $client -StatusCode 200 -StatusText "OK" -Origin $origin -Body (ConvertTo-TimelineJson (Get-TimelineAudioVerbalizationStatus -SourceId ([string]$query["sourceId"]) -RelativePath ([string]$query["path"])))
                continue
            }

            if ($method -eq "GET" -and $uri.AbsolutePath -eq "/timeline/audio-verbalization/result") {
                $query = [System.Web.HttpUtility]::ParseQueryString($uri.Query)
                Send-TimelineResponse -Client $client -StatusCode 200 -StatusText "OK" -Origin $origin -Body (ConvertTo-TimelineJson (Get-TimelineAudioVerbalizationResult -SourceId ([string]$query["sourceId"]) -RelativePath ([string]$query["path"])))
                continue
            }

            if ($method -eq "GET" -and $uri.AbsolutePath -eq "/timeline/audio-verbalization/ollama/status") {
                $query = [System.Web.HttpUtility]::ParseQueryString($uri.Query)
                Send-TimelineResponse -Client $client -StatusCode 200 -StatusText "OK" -Origin $origin -Body (ConvertTo-TimelineJson (Get-TimelineAudioVerbalizationOllamaStatus -BaseUrl ([string]$query["baseUrl"]) -Model ([string]$query["model"])))
                continue
            }

            if ($method -eq "POST" -and $uri.AbsolutePath -eq "/timeline/audio-verbalization/start") {
                $payload = if ([string]$request.Body) { $request.Body | ConvertFrom-Json } else { @{} }
                Send-TimelineResponse -Client $client -StatusCode 200 -StatusText "OK" -Origin $origin -Body (ConvertTo-TimelineJson (Start-TimelineAudioVerbalization -Request $payload))
                continue
            }

            if ($method -eq "POST" -and $uri.AbsolutePath -eq "/timeline/export/download") {
                Send-TimelineResponse -Client $client -StatusCode 200 -StatusText "OK" -Origin $origin -Body (ConvertTo-TimelineJson (New-TimelineStoreDownload))
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

            if ($method -eq "GET" -and $uri.AbsolutePath -eq "/products/image/overview") {
                Send-TimelineResponse -Client $client -StatusCode 200 -StatusText "OK" -Origin $origin -Body (ConvertTo-TimelineJson (Get-TimelineImageOverview))
                continue
            }

            if ($method -eq "GET" -and $uri.AbsolutePath -eq "/products/image/items") {
                $query = [System.Web.HttpUtility]::ParseQueryString($uri.Query)
                Send-TimelineResponse -Client $client -StatusCode 200 -StatusText "OK" -Origin $origin -Body (ConvertTo-TimelineJson (Get-TimelineImageItems -Page (Get-TimelineRequestPage -Query $query) -PageSize (Get-TimelineRequestPageSize -Query $query)))
                continue
            }

            if ($method -eq "GET" -and $uri.AbsolutePath -eq "/products/image/files") {
                $query = [System.Web.HttpUtility]::ParseQueryString($uri.Query)
                Send-TimelineResponse -Client $client -StatusCode 200 -StatusText "OK" -Origin $origin -Body (ConvertTo-TimelineJson (Get-TimelineImageFiles -Page (Get-TimelineRequestPage -Query $query) -PageSize (Get-TimelineRequestPageSize -Query $query)))
                continue
            }

            if ($method -eq "POST" -and $uri.AbsolutePath -eq "/products/image/refresh") {
                $payload = if ([string]$request.Body) { $request.Body | ConvertFrom-Json } else { @{} }
                Send-TimelineResponse -Client $client -StatusCode 200 -StatusText "OK" -Origin $origin -Body (ConvertTo-TimelineJson (Start-TimelineImageRefresh -Request $payload))
                continue
            }

            if ($method -eq "POST" -and $uri.AbsolutePath -eq "/products/image/items/download") {
                $payload = if ([string]$request.Body) { $request.Body | ConvertFrom-Json } else { @{} }
                Send-TimelineResponse -Client $client -StatusCode 200 -StatusText "OK" -Origin $origin -Body (ConvertTo-TimelineJson (Start-TimelineImageDownload -Request $payload))
                continue
            }

            if ($method -eq "POST" -and $uri.AbsolutePath -eq "/products/image/items/delete-generated") {
                $payload = if ([string]$request.Body) { $request.Body | ConvertFrom-Json } else { @{} }
                Send-TimelineResponse -Client $client -StatusCode 200 -StatusText "OK" -Origin $origin -Body (ConvertTo-TimelineJson (Remove-TimelineImageItems -Request $payload))
                continue
            }

            if ($method -eq "POST" -and $uri.AbsolutePath -eq "/products/image/settings") {
                $payload = if ([string]$request.Body) { $request.Body | ConvertFrom-Json } else { @{} }
                Send-TimelineResponse -Client $client -StatusCode 200 -StatusText "OK" -Origin $origin -Body (ConvertTo-TimelineJson (Write-TimelineImageSettings -Request $payload))
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
