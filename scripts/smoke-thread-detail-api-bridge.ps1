[CmdletBinding()]
param(
    [int]$ChatGptApiPort = 19931,
    [int]$WindowsCodexApiPort = 19932,
    [int]$LocalApiPort = 19933,
    [int]$TimeoutSeconds = 60,
    [switch]$KeepTemp
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
$chatGptRoot = "C:\apps\TimelineForChatGPT"
$windowsCodexRoot = "C:\apps\TimelineForWindowsCodex"
$chatGptWorkerSrc = Join-Path $chatGptRoot "worker\src"
$windowsCodexWorkerSrc = Join-Path $windowsCodexRoot "worker\src"
$localApiProject = Join-Path $repoRoot "local-api\Timeline.LocalApi.csproj"

function Assert-Smoke {
    param(
        [bool]$Condition,
        [string]$Message
    )

    if (-not $Condition) {
        throw $Message
    }
}

function Assert-PortFree {
    param([int]$Port)

    $listeners = Get-NetTCPConnection -LocalPort $Port -State Listen -ErrorAction SilentlyContinue
    if ($listeners) {
        throw "Port $Port is already in use. Choose another port."
    }
}

function Quote-Arg {
    param([string]$Value)

    return '"' + $Value.Replace('"', '\"') + '"'
}

function Start-PythonApi {
    param(
        [string]$Module,
        [string]$WorkingDirectory,
        [string]$PythonPath,
        [hashtable]$Environment
    )

    $startInfo = [System.Diagnostics.ProcessStartInfo]::new()
    $python = (Get-Command python.exe -ErrorAction SilentlyContinue)
    if ($null -eq $python) {
        $python = Get-Command python -ErrorAction Stop
    }
    $startInfo.FileName = $python.Source
    $startInfo.WorkingDirectory = $WorkingDirectory
    $startInfo.UseShellExecute = $false
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    $startInfo.CreateNoWindow = $true
    foreach ($key in $Environment.Keys) {
        $startInfo.EnvironmentVariables[$key] = [string]$Environment[$key]
    }
    $currentPythonPath = $startInfo.EnvironmentVariables["PYTHONPATH"]
    if ([string]::IsNullOrWhiteSpace($currentPythonPath)) {
        $startInfo.EnvironmentVariables["PYTHONPATH"] = $PythonPath
    }
    else {
        $startInfo.EnvironmentVariables["PYTHONPATH"] = $PythonPath + [System.IO.Path]::PathSeparator + $currentPythonPath
    }

    $startInfo.Arguments = "-m " + $Module
    $process = [System.Diagnostics.Process]::Start($startInfo)
    if ($null -eq $process) {
        throw "Failed to start Python API process for $Module"
    }

    return [pscustomobject]@{
        Process = $process
        StdoutTask = $process.StandardOutput.ReadToEndAsync()
        StderrTask = $process.StandardError.ReadToEndAsync()
        Project = $Module
    }
}

function Start-DotnetApi {
    param(
        [string]$Project,
        [string]$WorkingDirectory,
        [string]$Arguments,
        [hashtable]$Environment
    )

    $startInfo = [System.Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = "dotnet"
    $startInfo.WorkingDirectory = $WorkingDirectory
    $startInfo.UseShellExecute = $false
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    $startInfo.CreateNoWindow = $true
    foreach ($key in $Environment.Keys) {
        $startInfo.EnvironmentVariables[$key] = [string]$Environment[$key]
    }

    $startInfo.Arguments = "run --project " + (Quote-Arg $Project) + " " + $Arguments
    $process = [System.Diagnostics.Process]::Start($startInfo)
    if ($null -eq $process) {
        throw "Failed to start dotnet process for $Project"
    }

    return [pscustomobject]@{
        Process = $process
        StdoutTask = $process.StandardOutput.ReadToEndAsync()
        StderrTask = $process.StandardError.ReadToEndAsync()
        Project = $Project
    }
}

function Wait-Healthy {
    param(
        [string]$Url,
        [int]$TimeoutSeconds
    )

    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    do {
        try {
            $value = Invoke-RestMethod -UseBasicParsing -TimeoutSec 2 -Uri $Url
            if ($value -eq $true -or $value.ok -eq $true) {
                return
            }
        }
        catch {
        }

        Start-Sleep -Milliseconds 400
    } while ((Get-Date) -lt $deadline)

    throw "API did not become healthy: $Url"
}

function Write-JsonFile {
    param(
        [string]$Path,
        [object]$Value
    )

    $Value | ConvertTo-Json -Depth 20 | Set-Content -Path $Path -Encoding UTF8
}

function Assert-ThreadDetail {
    param(
        [object]$Detail,
        [string]$Name,
        [string]$ExpectedItemId,
        [int]$ExpectedMessageCount
    )

    Assert-Smoke -Condition ($Detail.available -eq $true) -Message "$Name detail was not available."
    Assert-Smoke -Condition ([string]::Equals([string]$Detail.itemId, $ExpectedItemId, [StringComparison]::OrdinalIgnoreCase)) -Message "$Name itemId mismatch."
    Assert-Smoke -Condition (-not [string]::IsNullOrWhiteSpace([string]$Detail.title)) -Message "$Name title was empty."
    Assert-Smoke -Condition ($Detail.messageCount -eq $ExpectedMessageCount) -Message "$Name messageCount mismatch."
    Assert-Smoke -Condition ($null -ne $Detail.PSObject.Properties["messages"]) -Message "$Name messages property was missing."
}

Assert-Smoke -Condition (Test-Path -LiteralPath (Join-Path $chatGptWorkerSrc "timeline_for_chatgpt_worker\api_server.py") -PathType Leaf) -Message "TimelineForChatGPT worker API was not found."
Assert-Smoke -Condition (Test-Path -LiteralPath (Join-Path $windowsCodexWorkerSrc "timeline_for_windows_codex_worker\api_server.py") -PathType Leaf) -Message "TimelineForWindowsCodex worker API was not found."
Assert-Smoke -Condition (Test-Path -LiteralPath $localApiProject -PathType Leaf) -Message "Timeline Local API project was not found: $localApiProject"

Assert-PortFree -Port $ChatGptApiPort
Assert-PortFree -Port $WindowsCodexApiPort
Assert-PortFree -Port $LocalApiPort

$tempRoot = Join-Path $env:TEMP ("timeline-thread-detail-api-bridge-" + [guid]::NewGuid().ToString("N"))
$processes = @()

try {
    New-Item -ItemType Directory -Path $tempRoot | Out-Null

    $chatGptSettings = Join-Path $tempRoot "chatgpt-settings.json"
    $chatGptOutput = Join-Path $tempRoot "chatgpt-output"
    $chatGptItem = Join-Path $chatGptOutput "chat-thread-1"
    $windowsCodexSettings = Join-Path $tempRoot "windows-codex-settings.json"
    $windowsCodexOutput = Join-Path $tempRoot "windows-codex-output"
    $windowsCodexItem = Join-Path $windowsCodexOutput "codex-thread-1"

    New-Item -ItemType Directory -Path $chatGptItem, $windowsCodexItem | Out-Null

    Write-JsonFile -Path $chatGptSettings -Value @{
        schemaVersion = 1
        runtime = @{ apiPort = $ChatGptApiPort }
        outputRoot = $chatGptOutput
    }
    Write-JsonFile -Path $windowsCodexSettings -Value @{
        schemaVersion = 1
        runtime = @{
            instanceName = ""
            apiPort = $WindowsCodexApiPort
        }
        outputRoot = $windowsCodexOutput
    }

    Write-JsonFile -Path (Join-Path $chatGptItem "timeline.json") -Value @{
        conversation_id = "chat-thread-1"
        title = "ChatGPT bridge smoke"
        created_at = "2026-05-17T00:00:00Z"
        updated_at = "2026-05-17T00:01:00Z"
        messages = @(
            @{ role = "user"; created_at = "2026-05-17T00:00:00Z"; text = "hello" },
            @{ role = "assistant"; created_at = "2026-05-17T00:01:00Z"; text = "world" }
        )
    }
    Write-JsonFile -Path (Join-Path $chatGptItem "convert_info.json") -Value @{ ok = $true }

    Write-JsonFile -Path (Join-Path $windowsCodexItem "timeline.json") -Value @{
        thread_id = "codex-thread-1"
        title = "Windows Codex bridge smoke"
        created_at = "2026-05-17T01:00:00Z"
        updated_at = "2026-05-17T01:01:00Z"
        messages = @(
            @{ role = "user"; created_at = "2026-05-17T01:00:00Z"; text = "codex hello" }
        )
    }
    Write-JsonFile -Path (Join-Path $windowsCodexItem "convert_info.json") -Value @{ ok = $true }

    $processes += Start-PythonApi `
        -Module "timeline_for_chatgpt_worker.api_server" `
        -WorkingDirectory $chatGptRoot `
        -PythonPath $chatGptWorkerSrc `
        -Environment @{
            TIMELINE_FOR_CHATGPT_API_BIND_HOST = "127.0.0.1"
            TIMELINE_FOR_CHATGPT_API_BIND_PORT = $ChatGptApiPort
            TIMELINE_FOR_CHATGPT_SETTINGS = $chatGptSettings
        }

    $processes += Start-PythonApi `
        -Module "timeline_for_windows_codex_worker.api_server" `
        -WorkingDirectory $windowsCodexRoot `
        -PythonPath $windowsCodexWorkerSrc `
        -Environment @{
            TIMELINE_FOR_WINDOWS_CODEX_API_BIND_HOST = "127.0.0.1"
            TIMELINE_FOR_WINDOWS_CODEX_API_BIND_PORT = $WindowsCodexApiPort
            TIMELINE_FOR_WINDOWS_CODEX_SETTINGS_PATH = $windowsCodexSettings
        }

    Wait-Healthy -Url "http://127.0.0.1:$ChatGptApiPort/health" -TimeoutSeconds $TimeoutSeconds
    Wait-Healthy -Url "http://127.0.0.1:$WindowsCodexApiPort/health" -TimeoutSeconds $TimeoutSeconds

    $directChatGptDetail = Invoke-RestMethod `
        -UseBasicParsing `
        -TimeoutSec 10 `
        -Method Post `
        -ContentType "application/json" `
        -Uri "http://127.0.0.1:$ChatGptApiPort/items/detail" `
        -Body (@{ itemId = "chat-thread-1" } | ConvertTo-Json)
    Assert-ThreadDetail -Detail $directChatGptDetail -Name "ChatGPT direct" -ExpectedItemId "chat-thread-1" -ExpectedMessageCount 2

    $directWindowsCodexDetail = Invoke-RestMethod `
        -UseBasicParsing `
        -TimeoutSec 10 `
        -Method Post `
        -ContentType "application/json" `
        -Uri "http://127.0.0.1:$WindowsCodexApiPort/items/detail" `
        -Body (@{ itemId = "codex-thread-1" } | ConvertTo-Json)
    Assert-ThreadDetail -Detail $directWindowsCodexDetail -Name "WindowsCodex direct" -ExpectedItemId "codex-thread-1" -ExpectedMessageCount 1

    $processes += Start-DotnetApi `
        -Project $localApiProject `
        -WorkingDirectory $repoRoot `
        -Arguments "" `
        -Environment @{
            ASPNETCORE_URLS = "http://127.0.0.1:$LocalApiPort"
            TIMELINE_PRODUCT_PATH = $repoRoot
            TIMELINE_PRODUCT_CHATGPT_API_BASE_URL = "http://127.0.0.1:$ChatGptApiPort"
            TIMELINE_PRODUCT_WINDOWS_CODEX_API_BASE_URL = "http://127.0.0.1:$WindowsCodexApiPort"
        }

    Wait-Healthy -Url "http://127.0.0.1:$LocalApiPort/health" -TimeoutSeconds $TimeoutSeconds

    $timelineChatGptDetail = Invoke-RestMethod `
        -UseBasicParsing `
        -TimeoutSec 10 `
        -Uri "http://127.0.0.1:$LocalApiPort/products/chatgpt/threads/chat-thread-1"
    Assert-ThreadDetail -Detail $timelineChatGptDetail -Name "Timeline ChatGPT bridge" -ExpectedItemId "chat-thread-1" -ExpectedMessageCount 2

    $timelineWindowsCodexDetail = Invoke-RestMethod `
        -UseBasicParsing `
        -TimeoutSec 10 `
        -Uri "http://127.0.0.1:$LocalApiPort/products/windows-codex/threads/codex-thread-1"
    Assert-ThreadDetail -Detail $timelineWindowsCodexDetail -Name "Timeline WindowsCodex bridge" -ExpectedItemId "codex-thread-1" -ExpectedMessageCount 1

    [pscustomobject]@{
        ok = $true
        localApiPort = $LocalApiPort
        chatGptApiPort = $ChatGptApiPort
        windowsCodexApiPort = $WindowsCodexApiPort
        tempRoot = $tempRoot
    } | ConvertTo-Json -Depth 5
}
finally {
    foreach ($entry in $processes) {
        if ($entry.Process -and -not $entry.Process.HasExited) {
            $entry.Process.Kill()
            $entry.Process.WaitForExit()
        }
    }

    if (-not $KeepTemp -and (Test-Path -LiteralPath $tempRoot)) {
        Remove-Item -LiteralPath $tempRoot -Recurse -Force
    }
}
