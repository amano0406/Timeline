[CmdletBinding()]
param(
    [string]$RepoRoot = ""
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

if (-not $RepoRoot) {
    $scriptPath = $PSCommandPath
    if (-not $scriptPath) {
        $scriptPath = $MyInvocation.MyCommand.Path
    }
    if (-not $scriptPath) {
        throw "Script path was not available."
    }
    $RepoRoot = Split-Path -Parent (Split-Path -Parent $scriptPath)
}

function Test-TimelineIgnoredPath {
    param([string]$Path)

    $normalized = [string]$Path
    return $normalized.Contains("\.git\") `
        -or $normalized.Contains("\bin\") `
        -or $normalized.Contains("\obj\") `
        -or $normalized.Contains("\node_modules\") `
        -or $normalized.Contains("\data\products\")
}

function Get-TimelineLineColumn {
    param(
        [byte[]]$Bytes,
        [int]$Index
    )

    $line = 1
    $column = 1
    for ($i = 0; $i -lt $Index; $i += 1) {
        if ($Bytes[$i] -eq 10) {
            $line += 1
            $column = 1
        }
        else {
            $column += 1
        }
    }

    return [ordered]@{
        line = $line
        column = $column
    }
}

$root = [System.IO.Path]::GetFullPath($RepoRoot)
if (-not (Test-Path -LiteralPath $root -PathType Container)) {
    throw "Repository root was not found: $root"
}

$violations = @()
$files = Get-ChildItem -LiteralPath $root -Recurse -Filter "*.ps1" -File |
    Where-Object { -not (Test-TimelineIgnoredPath -Path ([string]$_.FullName)) }

foreach ($file in $files) {
    $bytes = [System.IO.File]::ReadAllBytes([string]$file.FullName)
    $start = 0
    if ($bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF) {
        $start = 3
    }

    for ($i = $start; $i -lt $bytes.Length; $i += 1) {
        $byte = $bytes[$i]
        $isAllowedControl = $byte -eq 0x09 -or $byte -eq 0x0A -or $byte -eq 0x0D
        $isAllowedPrintableAscii = $byte -ge 0x20 -and $byte -le 0x7F
        if (-not $isAllowedControl -and -not $isAllowedPrintableAscii) {
            $position = Get-TimelineLineColumn -Bytes $bytes -Index $i
            $violations += [ordered]@{
                path = [string]$file.FullName
                line = [int]$position.line
                column = [int]$position.column
                byte = ("0x{0:X2}" -f $byte)
            }
            break
        }
    }
}

if ($violations.Count -gt 0) {
    Write-Host "PowerShell ASCII guard failed." -ForegroundColor Red
    Write-Host "Windows PowerShell 5.1 can misread UTF-8 without BOM. Keep .ps1 files ASCII-only." -ForegroundColor Red
    Write-Host "Move user-facing Japanese text to Blazor/C# UI files or JSON resources." -ForegroundColor Red
    foreach ($violation in $violations) {
        Write-Host ("- {0}:{1}:{2} {3}" -f $violation.path, $violation.line, $violation.column, $violation.byte) -ForegroundColor Red
    }
    exit 1
}

Write-Host "PowerShell ASCII guard passed."
