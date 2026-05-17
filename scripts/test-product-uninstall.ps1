[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
$settingsPath = Join-Path $repoRoot "settings.json"
$runtimeScript = Join-Path $PSScriptRoot "docker-runtime.ps1"
$originalSettings = if (Test-Path -LiteralPath $settingsPath -PathType Leaf) { Get-Content -LiteralPath $settingsPath -Raw -Encoding UTF8 } else { "" }
$testId = "product-uninstall-" + (Get-Date -Format "yyyyMMdd-HHmmss") + "-" + ([guid]::NewGuid().ToString("N").Substring(0, 8))
$testRoot = Join-Path (Join-Path (Join-Path $repoRoot "data") "test") $testId
$productDir = Join-Path (Join-Path $testRoot "products") "TimelineForAudio"
$generatedDir = Join-Path $testRoot "generated-audio"
$sourceDir = Join-Path $testRoot "source-audio"
$runtimeDir = Join-Path $testRoot "runtime-audio"
$port = 19111
$serverStarted = $false

if (-not (Test-Path -LiteralPath $runtimeScript -PathType Leaf)) {
    throw "Docker runtime script was not found: $runtimeScript"
}

. $runtimeScript

function Write-Utf8Json {
    param(
        [string]$Path,
        [object]$Payload
    )

    $json = ConvertTo-Json -InputObject $Payload -Depth 20
    $encoding = [System.Text.UTF8Encoding]::new($false)
    [System.IO.File]::WriteAllText($Path, $json, $encoding)
}

function Write-Utf8Text {
    param(
        [string]$Path,
        [string]$Text
    )

    $encoding = [System.Text.UTF8Encoding]::new($false)
    [System.IO.File]::WriteAllText($Path, $Text, $encoding)
}

function Reset-TestProduct {
    if (Test-Path -LiteralPath $productDir) {
        Remove-Item -LiteralPath $productDir -Recurse -Force
    }
    if (Test-Path -LiteralPath $generatedDir) {
        Remove-Item -LiteralPath $generatedDir -Recurse -Force
    }
    if (Test-Path -LiteralPath $sourceDir) {
        Remove-Item -LiteralPath $sourceDir -Recurse -Force
    }
    if (Test-Path -LiteralPath $runtimeDir) {
        Remove-Item -LiteralPath $runtimeDir -Recurse -Force
    }

    [System.IO.Directory]::CreateDirectory($productDir) | Out-Null
    [System.IO.Directory]::CreateDirectory($generatedDir) | Out-Null
    [System.IO.Directory]::CreateDirectory($sourceDir) | Out-Null
    [System.IO.Directory]::CreateDirectory($runtimeDir) | Out-Null

    Write-Utf8Text -Path (Join-Path $productDir "start.ps1") -Text "param()`nexit 0`n"
    Write-Utf8Text -Path (Join-Path $productDir "stop.ps1") -Text "param()`nexit 0`n"
    Write-Utf8Text -Path (Join-Path $productDir "README.md") -Text "# Test product`n"
    Write-Utf8Text -Path (Join-Path $productDir "payload.txt") -Text ("x" * 2048)
    Write-Utf8Text -Path (Join-Path $generatedDir "generated.txt") -Text ("y" * 4096)
    Write-Utf8Text -Path (Join-Path $sourceDir "original.wav") -Text "source"
    Write-Utf8Text -Path (Join-Path $runtimeDir "runtime.bin") -Text ("z" * 1024)

    Write-Utf8Json -Path (Join-Path $productDir "timeline-product.json") -Payload ([ordered]@{
        schemaVersion = 1
        id = "audio"
        displayName = "TimelineForAudio"
        runtime = [ordered]@{
            usesDocker = $true
            dockerManagedByTimeline = $true
            docker = [ordered]@{
                volumes = @(
                    [ordered]@{
                        name = "timeline-test-volume"
                    }
                )
            }
            localPaths = @(
                [ordered]@{
                    name = "test-runtime"
                    path = $runtimeDir
                }
            )
        }
    })

    Write-Utf8Json -Path (Join-Path $productDir "settings.json") -Payload ([ordered]@{
        schemaVersion = 1
        inputRoots = @(
            [ordered]@{
                id = "test-source"
                displayName = "Test Source"
                path = $sourceDir
                enabled = $true
            }
        )
        outputRoot = [ordered]@{
            id = "test-output"
            displayName = "Test Output"
            path = $generatedDir
            enabled = $true
        }
        audioExtensions = @(".wav")
        huggingfaceToken = "hf_test"
        computeMode = "cpu"
    })
}

function Invoke-JsonPost {
    param(
        [string]$Path,
        [object]$Payload
    )

    $body = ConvertTo-Json -InputObject $Payload -Compress -Depth 20
    try {
        return Invoke-RestMethod -UseBasicParsing -Method Post -Uri "http://127.0.0.1:$port$Path" -ContentType "application/json" -Body $body
    }
    catch {
        $responseBody = ""
        if ($_.Exception.Response) {
            $stream = $_.Exception.Response.GetResponseStream()
            if ($stream) {
                $reader = [System.IO.StreamReader]::new($stream)
                $responseBody = $reader.ReadToEnd()
                $reader.Dispose()
            }
        }
        throw "POST $Path failed. $responseBody"
    }
}

function Invoke-JsonGet {
    param([string]$Path)

    try {
        return Invoke-RestMethod -UseBasicParsing -Method Get -Uri "http://127.0.0.1:$port$Path"
    }
    catch {
        $responseBody = ""
        if ($_.Exception.Response) {
            $stream = $_.Exception.Response.GetResponseStream()
            if ($stream) {
                $reader = [System.IO.StreamReader]::new($stream)
                $responseBody = $reader.ReadToEnd()
                $reader.Dispose()
            }
        }
        throw "GET $Path failed. $responseBody"
    }
}

function Wait-LocalApi {
    for ($i = 0; $i -lt 60; $i += 1) {
        try {
            $health = Invoke-RestMethod -UseBasicParsing -TimeoutSec 1 -Uri "http://127.0.0.1:$port/health"
            if ($health.ok) {
                return
            }
        }
        catch {
            Start-Sleep -Milliseconds 250
        }
    }
    throw "Local API server did not become ready."
}

function Assert-OperationEvent {
    param(
        [string]$Action,
        [string]$State
    )

    $operationRoot = Join-Path (Join-Path $testRoot "logs") "operations"
    if (-not (Test-Path -LiteralPath $operationRoot -PathType Container)) {
        throw "Operation log root was not created."
    }

    foreach ($file in @(Get-ChildItem -LiteralPath $operationRoot -Recurse -Force -File -Filter "events.jsonl" -ErrorAction SilentlyContinue)) {
        foreach ($line in @(Get-Content -LiteralPath $file.FullName -Encoding UTF8 -ErrorAction SilentlyContinue)) {
            if (-not $line) {
                continue
            }
            try {
                $event = $line | ConvertFrom-Json
                if (([string]$event.action).Equals($Action, [System.StringComparison]::OrdinalIgnoreCase) -and
                    ([string]$event.state).Equals($State, [System.StringComparison]::OrdinalIgnoreCase)) {
                    return
                }
            }
            catch {
            }
        }
    }

    throw "Operation event was not found: $Action / $State"
}

try {
    [System.IO.Directory]::CreateDirectory($testRoot) | Out-Null
    Reset-TestProduct

    $settings = if ($originalSettings) { $originalSettings | ConvertFrom-Json } else { [pscustomobject]@{} }
    if ($null -eq $settings.PSObject.Properties["schemaVersion"]) {
        $settings | Add-Member -NotePropertyName "schemaVersion" -NotePropertyValue 1
    }
    if ($null -eq $settings.PSObject.Properties["dataRoot"]) {
        $settings | Add-Member -NotePropertyName "dataRoot" -NotePropertyValue $testRoot
    }
    else {
        $settings.dataRoot = $testRoot
    }
    if ($null -ne $settings.PSObject.Properties["workDirectory"]) {
        $settings.PSObject.Properties.Remove("workDirectory")
    }
    if ($null -ne $settings.PSObject.Properties["storeDirectory"]) {
        $settings.PSObject.Properties.Remove("storeDirectory")
    }
    if ($null -eq $settings.productRegistry) {
        $settings | Add-Member -NotePropertyName "productRegistry" -NotePropertyValue ([pscustomobject]@{ products = @() })
    }
    $products = @($settings.productRegistry.products)
    $audio = $products | Where-Object { ([string]$_.id).Equals("audio", [System.StringComparison]::OrdinalIgnoreCase) } | Select-Object -First 1
    if ($null -eq $audio) {
        $audio = [pscustomobject]@{
            id = "audio"
            displayName = "TimelineForAudio"
            path = $productDir
            sourceType = "github-source-archive"
            sourceUrl = "https://github.com/amano0406/TimelineForAudio"
            version = ""
            enabled = $true
            required = $false
        }
        $settings.productRegistry.products = @($products + $audio)
    }
    else {
        $audio.path = $productDir
        $audio.sourceType = "github-source-archive"
        $audio.sourceUrl = "https://github.com/amano0406/TimelineForAudio"
    }
    Write-Utf8Json -Path $settingsPath -Payload $settings

    Start-TimelineLocalApiServer -RepoRoot $repoRoot -Port $port -WebPort 19000
    $serverStarted = $true
    Wait-LocalApi

    $plan = Invoke-JsonPost -Path "/products/runtime/audio/uninstall-plan" -Payload ([ordered]@{
        keepSettings = $true
        removeGeneratedData = $false
    })
    if (-not $plan.appDirectory.exists) {
        throw "Plan did not detect the test product directory."
    }
    if ($plan.totalDeleteBytes -le 0) {
        throw "Plan did not include a positive delete size."
    }
    if (-not $plan.runtimeData.managedByTimeline) {
        throw "Runtime data management flag was not read from manifest."
    }
    if (@($plan.runtimeData.resources).Count -ne 2) {
        throw "Runtime resources were not read from manifest."
    }
    if (-not [bool]$plan.runtimeData.willDelete) {
        throw "Runtime data plan did not mark deletable local runtime data."
    }
    if ([int64]$plan.runtimeData.sizeBytes -le 0) {
        throw "Runtime local path size was not estimated."
    }

    [void](Invoke-JsonPost -Path "/products/runtime/audio/uninstall" -Payload ([ordered]@{
        keepSettings = $true
        removeGeneratedData = $false
    }))
    if (Test-Path -LiteralPath $productDir) {
        throw "Product directory was not deleted."
    }
    if (-not (Test-Path -LiteralPath $generatedDir)) {
        throw "Generated data should have been kept."
    }
    if (Test-Path -LiteralPath $runtimeDir) {
        throw "Runtime local path was not deleted."
    }
    Assert-OperationEvent -Action "product_uninstall_settings_backup" -State "completed"
    Assert-OperationEvent -Action "product_uninstall_runtime_delete" -State "completed"
    Assert-OperationEvent -Action "product_uninstall_app_delete" -State "completed"
    $backupSettings = Join-Path (Join-Path (Join-Path $testRoot "backups") "products\audio\settings") "settings.json"
    if (-not (Test-Path -LiteralPath $backupSettings -PathType Leaf)) {
        throw "Settings backup was not created."
    }
    $runtimeStatus = Invoke-JsonGet -Path "/products/runtime/status"
    $audioStatus = @($runtimeStatus.products) | Where-Object { ([string]$_.id).Equals("audio", [System.StringComparison]::OrdinalIgnoreCase) } | Select-Object -First 1
    if ($null -eq $audioStatus -or -not [bool]$audioStatus.settingsBackupAvailable) {
        throw "Settings backup was not reported in runtime status."
    }
    if (-not ([string]$audioStatus.settingsBackupPath).Equals($backupSettings, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Runtime status returned an unexpected settings backup path."
    }

    Reset-TestProduct
    [void](Invoke-JsonPost -Path "/products/runtime/audio/uninstall" -Payload ([ordered]@{
        keepSettings = $true
        removeGeneratedData = $true
    }))
    if (Test-Path -LiteralPath $productDir) {
        throw "Product directory was not deleted in remove-data mode."
    }
    if (Test-Path -LiteralPath $generatedDir) {
        throw "Generated data was not deleted in remove-data mode."
    }

    Reset-TestProduct
    [void](Invoke-JsonPost -Path "/products/runtime/audio/start" -Payload ([ordered]@{}))
    [void](Invoke-JsonPost -Path "/products/runtime/audio/uninstall" -Payload ([ordered]@{
        keepSettings = $true
        removeGeneratedData = $false
    }))
    if (Test-Path -LiteralPath $productDir) {
        throw "Running product was not stopped and deleted."
    }
    if (-not (Test-Path -LiteralPath $generatedDir)) {
        throw "Generated data should have been kept after running uninstall."
    }

    Write-Host "Product uninstall smoke test passed."
}
finally {
    if ($serverStarted) {
        Stop-TimelineLocalApiServer -Port $port
    }
    $encoding = [System.Text.UTF8Encoding]::new($false)
    if ($originalSettings) {
        [System.IO.File]::WriteAllText($settingsPath, $originalSettings, $encoding)
    }
    else {
        Remove-Item -LiteralPath $settingsPath -Force -ErrorAction SilentlyContinue
    }
    if (Test-Path -LiteralPath $testRoot) {
        Remove-Item -LiteralPath $testRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
}
