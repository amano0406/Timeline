Set-StrictMode -Version Latest

$script:TimelineDockerCommand = $null

function Get-TimelineJsonProperty {
    param(
        [object]$Object,
        [string]$Name,
        [object]$Default = $null
    )

    if ($null -eq $Object) {
        return $Default
    }
    $property = $Object.PSObject.Properties[$Name]
    if ($null -ne $property) {
        return $property.Value
    }
    return $Default
}

function Convert-TimelineRuntimeNamePart {
    param(
        [string]$Value,
        [string]$Fallback = ""
    )

    $text = ""
    if ($Value) {
        $text = $Value.Trim().ToLowerInvariant()
    }
    $text = [regex]::Replace($text, "[^a-z0-9]+", "-").Trim("-")
    if (-not $text) {
        return $Fallback
    }
    return $text
}

function Convert-TimelineRuntimeResourceName {
    param(
        [string]$Value,
        [string]$Fallback
    )

    $text = ""
    if ($Value) {
        $text = $Value.Trim().ToLowerInvariant()
    }
    $text = [regex]::Replace($text, "[^a-z0-9_.-]+", "-").Trim("-")
    if (-not $text) {
        return $Fallback
    }
    return $text
}

function Set-TimelineJsonProperty {
    param(
        [Parameter(Mandatory = $true)]
        [object]$Object,
        [Parameter(Mandatory = $true)]
        [string]$Name,
        [object]$Value
    )

    $property = $Object.PSObject.Properties[$Name]
    if ($null -ne $property) {
        $property.Value = $Value
        return
    }

    Add-Member -InputObject $Object -NotePropertyName $Name -NotePropertyValue $Value
}

function Write-TimelineRuntimeJsonFile {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path,
        [Parameter(Mandatory = $true)]
        [object]$Payload
    )

    $directory = [System.IO.Path]::GetDirectoryName($Path)
    if ($directory -and -not (Test-Path -LiteralPath $directory -PathType Container)) {
        New-Item -ItemType Directory -Path $directory -Force | Out-Null
    }

    $json = ConvertTo-Json -InputObject $Payload -Depth 20
    [System.IO.File]::WriteAllText($Path, $json + [Environment]::NewLine, [System.Text.UTF8Encoding]::new($false))
}

function New-TimelineRuntimeInstanceName {
    $suffix = [Guid]::NewGuid().ToString("N").Substring(0, 10)
    return "local-$suffix"
}

function Test-TimelineJsonPropertyMissingOrBlank {
    param(
        [object]$Object,
        [string]$Name
    )

    if ($null -eq $Object) {
        return $true
    }
    $property = $Object.PSObject.Properties[$Name]
    if ($null -eq $property) {
        return $true
    }
    return -not ([string]$property.Value)
}

function Ensure-TimelineRuntimeSettings {
    param(
        [Parameter(Mandatory = $true)]
        [string]$RepoRoot
    )

    $settingsPath = Join-Path $RepoRoot "settings.json"
    $payload = $null
    if (Test-Path -LiteralPath $settingsPath -PathType Leaf) {
        try {
            $payload = Get-Content -LiteralPath $settingsPath -Raw -Encoding UTF8 | ConvertFrom-Json
        }
        catch {
            $payload = $null
        }
    }

    if ($null -eq $payload) {
        $payload = [pscustomobject][ordered]@{
            schemaVersion = 1
        }
    }

    $changed = $false
    $runtime = Get-TimelineJsonProperty -Object $payload -Name "runtime" -Default $null
    if ($null -eq $runtime -or $runtime -is [string] -or $runtime -is [ValueType]) {
        $runtime = [pscustomobject][ordered]@{}
        Set-TimelineJsonProperty -Object $payload -Name "runtime" -Value $runtime
        $changed = $true
    }

    if (Test-TimelineJsonPropertyMissingOrBlank -Object $runtime -Name "instanceName") {
        Set-TimelineJsonProperty -Object $runtime -Name "instanceName" -Value (New-TimelineRuntimeInstanceName)
        $changed = $true
    }
    if ($null -eq $runtime.PSObject.Properties["imageTag"]) {
        Set-TimelineJsonProperty -Object $runtime -Name "imageTag" -Value ""
        $changed = $true
    }
    if ($null -eq $runtime.PSObject.Properties["webPort"]) {
        Set-TimelineJsonProperty -Object $runtime -Name "webPort" -Value 19000
        $changed = $true
    }
    if ($null -eq $runtime.PSObject.Properties["helperPortStart"]) {
        Set-TimelineJsonProperty -Object $runtime -Name "helperPortStart" -Value 19001
        $changed = $true
    }
    if ($null -eq $runtime.PSObject.Properties["helperPortEnd"]) {
        Set-TimelineJsonProperty -Object $runtime -Name "helperPortEnd" -Value 19010
        $changed = $true
    }
    if ($null -eq $runtime.PSObject.Properties["ollamaPort"]) {
        Set-TimelineJsonProperty -Object $runtime -Name "ollamaPort" -Value 11434
        $changed = $true
    }
    if (Test-TimelineJsonPropertyMissingOrBlank -Object $runtime -Name "ollamaModel") {
        Set-TimelineJsonProperty -Object $runtime -Name "ollamaModel" -Value "qwen3.5:9b"
        $changed = $true
    }
    if ($null -eq $runtime.PSObject.Properties["shareOllamaVolume"]) {
        Set-TimelineJsonProperty -Object $runtime -Name "shareOllamaVolume" -Value $true
        $changed = $true
    }
    if (Test-TimelineJsonPropertyMissingOrBlank -Object $runtime -Name "ollamaVolumeName") {
        Set-TimelineJsonProperty -Object $runtime -Name "ollamaVolumeName" -Value "timeline-ollama"
        $changed = $true
    }

    if ($changed) {
        Write-TimelineRuntimeJsonFile -Path $settingsPath -Payload $payload
    }

    return Get-TimelineRuntimeSettings -RepoRoot $RepoRoot
}

function Convert-TimelineRuntimePort {
    param(
        [object]$Value,
        [int]$Default,
        [int]$Minimum = 1,
        [int]$Maximum = 65535
    )

    $port = 0
    if ($null -ne $Value -and [int]::TryParse(([string]$Value), [ref]$port)) {
        if ($port -ge $Minimum -and $port -le $Maximum) {
            return $port
        }
    }
    return $Default
}

function Convert-TimelineRuntimeBoolean {
    param(
        [object]$Value,
        [bool]$Default
    )

    if ($null -eq $Value) {
        return $Default
    }
    if ($Value -is [bool]) {
        return [bool]$Value
    }
    $text = ([string]$Value).Trim().ToLowerInvariant()
    if (@("true", "1", "yes", "on") -contains $text) {
        return $true
    }
    if (@("false", "0", "no", "off") -contains $text) {
        return $false
    }
    return $Default
}

function Get-TimelineRuntimeSettings {
    param(
        [Parameter(Mandatory = $true)]
        [string]$RepoRoot
    )

    $runtime = $null
    $settingsPath = Join-Path $RepoRoot "settings.json"
    if (Test-Path -LiteralPath $settingsPath -PathType Leaf) {
        try {
            $payload = Get-Content -LiteralPath $settingsPath -Raw -Encoding UTF8 | ConvertFrom-Json
            $runtime = Get-TimelineJsonProperty -Object $payload -Name "runtime" -Default $null
        }
        catch {
            $runtime = $null
        }
    }

    $instanceName = [string](Get-TimelineJsonProperty -Object $runtime -Name "instanceName" -Default "")
    $instancePart = Convert-TimelineRuntimeNamePart -Value $instanceName
    $projectName = "timeline"
    if ($instancePart) {
        $projectName = "timeline-$instancePart"
    }

    $imageTag = [string](Get-TimelineJsonProperty -Object $runtime -Name "imageTag" -Default "")
    $imageTag = Convert-TimelineRuntimeResourceName -Value $imageTag -Fallback ""
    if (-not $imageTag) {
        if ($instancePart) {
            $imageTag = $projectName
        }
        else {
            $imageTag = "latest"
        }
    }

    $webPort = Convert-TimelineRuntimePort -Value (Get-TimelineJsonProperty -Object $runtime -Name "webPort" -Default $null) -Default 19000
    $helperPortStart = Convert-TimelineRuntimePort -Value (Get-TimelineJsonProperty -Object $runtime -Name "helperPortStart" -Default $null) -Default 19001
    $helperPortEnd = Convert-TimelineRuntimePort -Value (Get-TimelineJsonProperty -Object $runtime -Name "helperPortEnd" -Default $null) -Default 19010
    if ($helperPortEnd -lt $helperPortStart) {
        $helperPortEnd = $helperPortStart
    }
    $ollamaPort = Convert-TimelineRuntimePort -Value (Get-TimelineJsonProperty -Object $runtime -Name "ollamaPort" -Default $null) -Default 11434

    $ollamaModel = [string](Get-TimelineJsonProperty -Object $runtime -Name "ollamaModel" -Default "")
    if (-not $ollamaModel) {
        $ollamaModel = "qwen3.5:9b"
    }

    $shareOllamaVolume = Convert-TimelineRuntimeBoolean -Value (Get-TimelineJsonProperty -Object $runtime -Name "shareOllamaVolume" -Default $null) -Default $true
    $defaultOllamaVolumeName = if ($shareOllamaVolume) { "timeline-ollama" } else { "$projectName-ollama" }
    $ollamaVolumeName = [string](Get-TimelineJsonProperty -Object $runtime -Name "ollamaVolumeName" -Default "")
    $ollamaVolumeName = Convert-TimelineRuntimeResourceName -Value $ollamaVolumeName -Fallback $defaultOllamaVolumeName

    return [pscustomobject]@{
        InstanceName = $instanceName
        ComposeProjectName = $projectName
        ImageTag = $imageTag
        WebPort = $webPort
        HelperPortStart = $helperPortStart
        HelperPortEnd = $helperPortEnd
        OllamaPort = $ollamaPort
        OllamaModel = $ollamaModel
        ShareOllamaVolume = $shareOllamaVolume
        OllamaVolumeName = $ollamaVolumeName
    }
}

function Get-TimelineLastExitCode {
    $variable = Get-Variable -Name LASTEXITCODE -Scope Global -ErrorAction SilentlyContinue
    if ($variable -and $null -ne $variable.Value) {
        return [int]$variable.Value
    }
    if ($?) {
        return 0
    }
    return 1
}

function Add-TimelineDockerPath {
    $dockerBin = Join-Path $env:ProgramFiles "Docker\Docker\resources\bin"
    if (Test-Path -LiteralPath (Join-Path $dockerBin "docker.exe")) {
        $currentPath = [Environment]::GetEnvironmentVariable("PATH", "Process")
        if (-not $currentPath) {
            $currentPath = $env:PATH
        }

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

        [Environment]::SetEnvironmentVariable("PATH", $currentPath, "Process")
        [Environment]::SetEnvironmentVariable("Path", $currentPath, "Process")
        $env:PATH = $currentPath
        $env:Path = $currentPath
    }
}

function Resolve-TimelineDockerCommand {
    $dockerExe = Join-Path $env:ProgramFiles "Docker\Docker\resources\bin\docker.exe"
    if (Test-Path -LiteralPath $dockerExe) {
        return $dockerExe
    }

    $dockerCommand = Get-Command docker.exe -ErrorAction SilentlyContinue
    if ($dockerCommand) {
        return $dockerCommand.Source
    }

    $dockerCommand = Get-Command docker -ErrorAction SilentlyContinue
    if ($dockerCommand) {
        return $dockerCommand.Source
    }

    return $null
}

function Get-TimelineDockerCommand {
    if (-not $script:TimelineDockerCommand) {
        $script:TimelineDockerCommand = Resolve-TimelineDockerCommand
    }
    if (-not $script:TimelineDockerCommand) {
        throw "docker.exe was not found."
    }
    return $script:TimelineDockerCommand
}

function Get-TimelineDockerDesktopPath {
    $candidates = @(
        (Join-Path $env:ProgramFiles "Docker\Docker\Docker Desktop.exe"),
        (Join-Path $env:LocalAppData "Programs\Docker\Docker\Docker Desktop.exe")
    )
    foreach ($candidate in $candidates) {
        if (Test-Path -LiteralPath $candidate) {
            return $candidate
        }
    }
    return $null
}

function Wait-TimelineDockerEngine {
    param(
        [int]$MaxAttempts = 60,
        [int]$SleepSeconds = 2
    )

    for ($attempt = 1; $attempt -le $MaxAttempts; $attempt += 1) {
        & (Get-TimelineDockerCommand) info *> $null
        if ($?) {
            return
        }
        Start-Sleep -Seconds $SleepSeconds
    }
    throw "Docker Desktop did not become ready in time."
}

function Initialize-TimelineDocker {
    param(
        [Parameter(Mandatory = $true)]
        [string]$RepoRoot
    )

    Set-Location $RepoRoot
    Add-TimelineDockerPath

    $dockerCommand = Resolve-TimelineDockerCommand
    $script:TimelineDockerCommand = $dockerCommand
    $dockerDesktop = Get-TimelineDockerDesktopPath
    if (-not $dockerCommand) {
        if ($dockerDesktop) {
            Write-Host "Docker Desktop appears to be installed, but docker.exe is not available from this shell."
            Start-Process -FilePath $dockerDesktop | Out-Null
        }
        else {
            Write-Host "Docker Desktop is not installed, or docker.exe is not on PATH."
            Start-Process "https://docs.docker.com/desktop/setup/install/windows-install/" | Out-Null
        }
        exit 1
    }

    & (Get-TimelineDockerCommand) info *> $null
    if ($?) {
        return
    }

    if ($dockerDesktop) {
        Write-Host "Starting Docker Desktop. This can take a minute..."
        Start-Process -FilePath $dockerDesktop | Out-Null
        Wait-TimelineDockerEngine
        return
    }

    throw "Docker Desktop is installed but the Docker engine is not ready."
}

function Get-TimelineComposeArgs {
    param(
        [Parameter(Mandatory = $true)]
        [string]$RepoRoot
    )

    $runtime = Get-TimelineRuntimeSettings -RepoRoot $RepoRoot
    return @("-f", (Join-Path $RepoRoot "docker-compose.yml"), "-p", $runtime.ComposeProjectName)
}

function Test-TimelineHelperServer {
    param([int]$Port = 19001)

    try {
        $response = Invoke-RestMethod -Uri "http://127.0.0.1:$Port/health" -TimeoutSec 1 -ErrorAction Stop
        return [bool]$response.ok
    }
    catch {
        return $false
    }
}

function Start-TimelineLocalApiServer {
    param(
        [Parameter(Mandatory = $true)]
        [string]$RepoRoot,
        [int]$Port = 19001,
        [int]$WebPort = 19000
    )

    if (Test-TimelineHelperServer -Port $Port) {
        Stop-TimelineLocalApiServer -Port $Port
        Start-Sleep -Milliseconds 300
        if (Test-TimelineHelperServer -Port $Port) {
            throw "Timeline local API server is already running on port $Port and could not be stopped."
        }
    }

    $projectPath = Join-Path $RepoRoot "local-api\Timeline.LocalApi.csproj"
    if (-not (Test-Path -LiteralPath $projectPath)) {
        throw "Timeline local API project was not found: $projectPath"
    }

    $logDir = Join-Path $RepoRoot ".docker"
    if (-not (Test-Path -LiteralPath $logDir)) {
        New-Item -ItemType Directory -Path $logDir | Out-Null
    }
    $stdoutLog = Join-Path $logDir "local-api-$Port.stdout.log"
    $stderrLog = Join-Path $logDir "local-api-$Port.stderr.log"
    $buildLog = Join-Path $logDir "local-api-$Port.build.log"
    $buildRoot = Join-Path $RepoRoot "data\work"
    $buildDir = Join-Path $buildRoot "local-api-build-$Port"
    Remove-Item -LiteralPath $stdoutLog, $stderrLog, $buildLog -ErrorAction SilentlyContinue

    if (-not (Test-Path -LiteralPath $buildRoot)) {
        New-Item -ItemType Directory -Path $buildRoot | Out-Null
    }

    $buildRootFull = [System.IO.Path]::GetFullPath($buildRoot).TrimEnd([System.IO.Path]::DirectorySeparatorChar, [System.IO.Path]::AltDirectorySeparatorChar)
    $buildDirFull = [System.IO.Path]::GetFullPath($buildDir).TrimEnd([System.IO.Path]::DirectorySeparatorChar, [System.IO.Path]::AltDirectorySeparatorChar)
    $expectedPrefix = $buildRootFull + [System.IO.Path]::DirectorySeparatorChar
    if ((Test-Path -LiteralPath $buildDirFull) -and $buildDirFull.StartsWith($expectedPrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
        Remove-Item -LiteralPath $buildDirFull -Recurse -Force
    }
    New-Item -ItemType Directory -Path $buildDirFull | Out-Null

    $buildArguments = @(
        "build",
        $projectPath,
        "-o",
        $buildDirFull
    )
    & dotnet.exe @buildArguments *> $buildLog
    if (-not $?) {
        throw "Timeline local API build failed. See $buildLog"
    }

    $dllPath = Join-Path $buildDirFull "Timeline.LocalApi.dll"
    if (-not (Test-Path -LiteralPath $dllPath -PathType Leaf)) {
        throw "Timeline local API build output was not found: $dllPath"
    }

    $arguments = @(
        "`"$dllPath`"",
        "--urls",
        "http://127.0.0.1:$Port",
        "--Timeline:WebPort=$WebPort",
        "--Timeline:ProductPath=`"$RepoRoot`""
    )

    Start-Process `
        -FilePath "dotnet.exe" `
        -ArgumentList $arguments `
        -WorkingDirectory $RepoRoot `
        -WindowStyle Hidden `
        -RedirectStandardOutput $stdoutLog `
        -RedirectStandardError $stderrLog | Out-Null

    for ($attempt = 1; $attempt -le 240; $attempt += 1) {
        if (Test-TimelineHelperServer -Port $Port) {
            return
        }
        Start-Sleep -Milliseconds 250
    }

    throw "Timeline local API server did not start."
}

function Stop-TimelineRuntimeProcessTree {
    param([int]$ProcessId)

    if ($ProcessId -le 0) {
        return
    }

    $pending = @([int]$ProcessId)
    $processIds = @()
    $seen = @{}
    while ($pending.Count -gt 0) {
        $current = [int]$pending[0]
        if ($pending.Count -eq 1) {
            $pending = @()
        }
        else {
            $pending = @($pending[1..($pending.Count - 1)])
        }

        $key = [string]$current
        if ($seen.ContainsKey($key)) {
            continue
        }
        $seen[$key] = $true
        $processIds += $current

        try {
            $children = @(Get-CimInstance Win32_Process -Filter "ParentProcessId = $current" -ErrorAction Stop)
            foreach ($child in $children) {
                $pending += [int]$child.ProcessId
            }
        }
        catch {
        }
    }

    for ($index = $processIds.Count - 1; $index -ge 0; $index--) {
        $targetProcessId = [int]$processIds[$index]
        if ($targetProcessId -eq $PID) {
            continue
        }
        try {
            Stop-Process -Id $targetProcessId -Force -ErrorAction Stop
        }
        catch {
        }
    }
}

function Stop-TimelineLocalApiServer {
    param([int]$Port = 0)

    $processIds = @()
    $processes = Get-CimInstance Win32_Process -ErrorAction SilentlyContinue |
        Where-Object {
            $commandLine = [string]$_.CommandLine
            $executablePath = [string]$_.ExecutablePath
            $isLocalApiHost = $executablePath -like "*\Timeline.LocalApi.exe" -or $commandLine -like "*Timeline.LocalApi.dll*"
            $isLocalApiRun = $commandLine -like "*dotnet*run*--project*local-api*Timeline.LocalApi.csproj*"
            if (-not ($isLocalApiHost -or $isLocalApiRun)) {
                return $false
            }
            if ($Port -le 0) {
                return $true
            }
            return ($commandLine -like "*:$Port*") -or ($commandLine -like "*local-api-build-$Port*")
        }

    foreach ($process in @($processes)) {
        if ($process.ProcessId) {
            $processIds += [int]$process.ProcessId
        }
    }

    if ($Port -gt 0) {
        try {
            $listeners = Get-NetTCPConnection -LocalPort $Port -State Listen -ErrorAction SilentlyContinue
            foreach ($listener in @($listeners)) {
                if ($listener.OwningProcess) {
                    $processIds += [int]$listener.OwningProcess
                }
            }
        }
        catch {
        }
    }

    foreach ($processId in @($processIds | Select-Object -Unique)) {
        if ($processId -ne $PID) {
            Stop-TimelineRuntimeProcessTree -ProcessId $processId
        }
    }
}

function Invoke-TimelineWithFileLock {
    param(
        [Parameter(Mandatory = $true)]
        [string]$RepoRoot,
        [Parameter(Mandatory = $true)]
        [string]$LockName,
        [Parameter(Mandatory = $true)]
        [scriptblock]$ScriptBlock
    )

    $generatedDir = Join-Path $RepoRoot ".docker"
    New-Item -ItemType Directory -Path $generatedDir -Force | Out-Null
    $lockPath = Join-Path $generatedDir $LockName
    $lockStream = $null

    for ($attempt = 1; $attempt -le 300; $attempt += 1) {
        try {
            $lockStream = [System.IO.File]::Open(
                $lockPath,
                [System.IO.FileMode]::OpenOrCreate,
                [System.IO.FileAccess]::ReadWrite,
                [System.IO.FileShare]::None
            )
            break
        }
        catch [System.IO.IOException] {
            Start-Sleep -Milliseconds 100
        }
    }
    if (-not $lockStream) {
        throw "Timed out waiting for lock: $lockPath"
    }

    try {
        & $ScriptBlock
    }
    finally {
        if ($lockStream) {
            $lockStream.Dispose()
        }
    }
}
