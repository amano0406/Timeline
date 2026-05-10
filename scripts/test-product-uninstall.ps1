[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
$settingsPath = Join-Path $repoRoot "settings.json"
$serverScript = Join-Path $PSScriptRoot "timeline-helper-server.ps1"
$originalSettings = Get-Content -LiteralPath $settingsPath -Raw -Encoding UTF8
$testId = "product-uninstall-" + (Get-Date -Format "yyyyMMdd-HHmmss") + "-" + ([guid]::NewGuid().ToString("N").Substring(0, 8))
$testRoot = Join-Path "C:\TimelineData\Timeline\test" $testId
$productDir = Join-Path $testRoot "TimelineForAudio"
$generatedDir = Join-Path $testRoot "generated-audio"
$sourceDir = Join-Path $testRoot "source-audio"
$port = 19111
$server = $null

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

    [System.IO.Directory]::CreateDirectory($productDir) | Out-Null
    [System.IO.Directory]::CreateDirectory($generatedDir) | Out-Null
    [System.IO.Directory]::CreateDirectory($sourceDir) | Out-Null

    Write-Utf8Text -Path (Join-Path $productDir "cli.ps1") -Text "param()`nexit 0`n"
    Write-Utf8Text -Path (Join-Path $productDir "start.ps1") -Text "param()`nexit 0`n"
    Write-Utf8Text -Path (Join-Path $productDir "stop.ps1") -Text "param()`nexit 0`n"
    Write-Utf8Text -Path (Join-Path $productDir "README.md") -Text "# Test product`n"
    Write-Utf8Text -Path (Join-Path $productDir "payload.txt") -Text ("x" * 2048)
    Write-Utf8Text -Path (Join-Path $generatedDir "generated.txt") -Text ("y" * 4096)
    Write-Utf8Text -Path (Join-Path $sourceDir "original.wav") -Text "source"

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
    return Invoke-RestMethod -UseBasicParsing -Method Post -Uri "http://127.0.0.1:$port$Path" -ContentType "application/json" -Body $body
}

function Wait-Helper {
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
    throw "Helper server did not become ready."
}

try {
    [System.IO.Directory]::CreateDirectory($testRoot) | Out-Null
    Reset-TestProduct

    $settings = $originalSettings | ConvertFrom-Json
    $settings.workDirectory = Join-Path $testRoot "work"
    $settings.storeDirectory = Join-Path $testRoot "store"
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
            sourceType = "release"
            sourceUrl = ""
            version = ""
            enabled = $true
            required = $false
        }
        $settings.productRegistry.products = @($products + $audio)
    }
    else {
        $audio.path = $productDir
        $audio.sourceType = "release"
        $audio.sourceUrl = ""
    }
    Write-Utf8Json -Path $settingsPath -Payload $settings

    $server = Start-Process `
        -FilePath "powershell.exe" `
        -ArgumentList @("-NoLogo", "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", $serverScript, "-Port", "$port") `
        -WindowStyle Hidden `
        -PassThru
    Wait-Helper

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
    $backupSettings = Join-Path (Join-Path (Join-Path $testRoot "backups") "products\audio\settings") "settings.json"
    if (-not (Test-Path -LiteralPath $backupSettings -PathType Leaf)) {
        throw "Settings backup was not created."
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
    if ($null -ne $server -and -not $server.HasExited) {
        Stop-Process -Id $server.Id -Force -ErrorAction SilentlyContinue
    }
    $encoding = [System.Text.UTF8Encoding]::new($false)
    [System.IO.File]::WriteAllText($settingsPath, $originalSettings, $encoding)
    if (Test-Path -LiteralPath $testRoot) {
        Remove-Item -LiteralPath $testRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
}
