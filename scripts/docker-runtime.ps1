Set-StrictMode -Version Latest

$script:TimelineDockerCommand = $null

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

    return @("-f", (Join-Path $RepoRoot "docker-compose.yml"))
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

function Start-TimelineHelperServer {
    param(
        [Parameter(Mandatory = $true)]
        [string]$RepoRoot,
        [Parameter(Mandatory = $true)]
        [string]$AudioProductPath,
        [string]$WindowsCodexProductPath = "C:\apps\TimelineForWindowsCodex",
        [string]$ChatGptProductPath = "C:\apps\TimelineForChatGPT",
        [string]$ImageProductPath = "C:\apps\TimelineForImage",
        [int]$Port = 19001
    )

    if (Test-TimelineHelperServer -Port $Port) {
        Stop-TimelineHelperServer
        Start-Sleep -Milliseconds 300
    }

    $scriptPath = Join-Path $RepoRoot "scripts\timeline-helper-server.ps1"
    if (-not (Test-Path -LiteralPath $scriptPath)) {
        throw "Timeline helper server was not found: $scriptPath"
    }

    $arguments = @(
        "-NoLogo",
        "-NoProfile",
        "-STA",
        "-ExecutionPolicy",
        "Bypass",
        "-File",
        "`"$scriptPath`"",
        "-Port",
        "$Port",
        "-AudioProductPath",
        "`"$AudioProductPath`"",
        "-WindowsCodexProductPath",
        "`"$WindowsCodexProductPath`"",
        "-ChatGptProductPath",
        "`"$ChatGptProductPath`"",
        "-ImageProductPath",
        "`"$ImageProductPath`""
    )

    Start-Process -FilePath "powershell.exe" -ArgumentList $arguments -WindowStyle Hidden | Out-Null

    for ($attempt = 1; $attempt -le 120; $attempt += 1) {
        if (Test-TimelineHelperServer -Port $Port) {
            return
        }
        Start-Sleep -Milliseconds 250
    }

    throw "Timeline helper server did not start."
}

function Stop-TimelineHelperServer {
    $processes = Get-CimInstance Win32_Process -ErrorAction SilentlyContinue |
        Where-Object { $_.CommandLine -like "*timeline-helper-server.ps1*" }

    foreach ($process in $processes) {
        Stop-Process -Id $process.ProcessId -Force -ErrorAction SilentlyContinue
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
