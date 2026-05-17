[CmdletBinding()]
param(
    [string[]]$ProductRoots = @(),
    [switch]$SkipBundledProducts
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
$productNames = @(
    "TimelineForAudio",
    "TimelineForImage",
    "TimelineForVideo",
    "TimelineForChatGPT",
    "TimelineForWindowsCodex",
    "TimelineForPC"
)

function Add-ProductRootIfExists {
    param(
        [System.Collections.Generic.List[string]]$Roots,
        [string]$Path
    )

    if (-not [string]::IsNullOrWhiteSpace($Path) -and (Test-Path -LiteralPath $Path -PathType Container)) {
        $fullPath = [System.IO.Path]::GetFullPath($Path)
        if (-not $Roots.Contains($fullPath)) {
            $Roots.Add($fullPath) | Out-Null
        }
    }
}

function Test-IgnoredCliAuditPath {
    param([string]$Path)

    $normalized = [string]$Path
    return $normalized.Contains("\.git\") `
        -or $normalized.Contains("\bin\") `
        -or $normalized.Contains("\obj\") `
        -or $normalized.Contains("\node_modules\") `
        -or $normalized.Contains("\__pycache__\") `
        -or $normalized.Contains("\.pytest_cache\") `
        -or $normalized.Contains("\.playwright-cli\") `
        -or $normalized.Contains("\data\master\") `
        -or $normalized.Contains("\data\runs\") `
        -or $normalized.Contains("\data\cache\") `
        -or $normalized.Contains("\output\") `
        -or $normalized.Contains("\downloads\")
}

function Test-SourceCliAuditPath {
    param([string]$Path)

    $normalized = [string]$Path
    if (Test-IgnoredCliAuditPath -Path $normalized) {
        return $false
    }
    if ($normalized.Contains("\tests\") -or $normalized.Contains("\docs\")) {
        return $false
    }
    return $normalized.EndsWith(".py", [System.StringComparison]::OrdinalIgnoreCase) `
        -or $normalized.EndsWith(".ps1", [System.StringComparison]::OrdinalIgnoreCase) `
        -or $normalized.EndsWith(".cs", [System.StringComparison]::OrdinalIgnoreCase) `
        -or $normalized.EndsWith("\timeline-product.json", [System.StringComparison]::OrdinalIgnoreCase)
}

function Add-Violation {
    param(
        [System.Collections.Generic.List[string]]$Violations,
        [string]$ProductRoot,
        [string]$Message
    )

    $Violations.Add(("{0}: {1}" -f $ProductRoot, $Message)) | Out-Null
}

if ($ProductRoots.Count -eq 0) {
    $roots = [System.Collections.Generic.List[string]]::new()
    foreach ($productName in $productNames) {
        Add-ProductRootIfExists -Roots $roots -Path (Join-Path "C:\apps" $productName)
    }
    if (-not $SkipBundledProducts) {
        $bundledRoot = Join-Path (Join-Path $repoRoot "data") "products"
        foreach ($productName in $productNames) {
            Add-ProductRootIfExists -Roots $roots -Path (Join-Path $bundledRoot $productName)
        }
    }
    $ProductRoots = $roots.ToArray()
}

if ($ProductRoots.Count -eq 0) {
    throw "No product roots were found."
}

$allowedCommands = @("start", "stop", "installAutostart", "uninstallAutostart")
$forbiddenFileNames = @(
    "cli.ps1",
    "cli.bat",
    "cli.cmd",
    "cli.py",
    "__main__.py",
    "operations.py",
    "items_operations.py",
    "settings_operations.py",
    "directory_refresh_operations.py",
    "runs_operations.py"
)
$forbiddenSourcePatterns = @(
    "ProductOperationRunner",
    "cli.ps1",
    "CLI.ps1",
    "__main__.py",
    "items_operations",
    "settings_operations",
    "directory_refresh_operations",
    "runs_operations"
)
$forbiddenHostIntegrationPatterns = @(
    "ProductOperationRunner",
    "Start-ProductOperation",
    "Invoke-ProductOperation",
    "cli.ps1",
    "CLI.ps1",
    "__main__.py",
    "items refresh",
    "items list",
    "items detail",
    "items download",
    "settings status",
    "settings save",
    "files list",
    "models list",
    "doctor --json",
    "--json",
    "--max-items",
    "--page-size"
)

$violations = [System.Collections.Generic.List[string]]::new()

foreach ($root in $ProductRoots) {
    $productRoot = [System.IO.Path]::GetFullPath($root)
    if (-not (Test-Path -LiteralPath $productRoot -PathType Container)) {
        Add-Violation -Violations $violations -ProductRoot $productRoot -Message "product root was not found"
        continue
    }

    $manifestPath = Join-Path $productRoot "timeline-product.json"
    if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) {
        Add-Violation -Violations $violations -ProductRoot $productRoot -Message "timeline-product.json was not found"
    }
    else {
        $manifest = Get-Content -LiteralPath $manifestPath -Raw -Encoding UTF8 | ConvertFrom-Json
        if (-not $manifest.api -or $manifest.api.enabled -ne $true) {
            Add-Violation -Violations $violations -ProductRoot $productRoot -Message "manifest api.enabled must be true"
        }
        if (-not $manifest.api -or [string]$manifest.api.connectionMode -ne "api") {
            Add-Violation -Violations $violations -ProductRoot $productRoot -Message "manifest api.connectionMode must be api"
        }
        if (-not $manifest.api -or [string]::IsNullOrWhiteSpace([string]$manifest.api.healthPath)) {
            Add-Violation -Violations $violations -ProductRoot $productRoot -Message "manifest api.healthPath is required"
        }

        if ($manifest.commands) {
            foreach ($property in $manifest.commands.PSObject.Properties) {
                if ($allowedCommands -notcontains $property.Name) {
                    Add-Violation -Violations $violations -ProductRoot $productRoot -Message ("manifest exposes non-runtime command: {0}" -f $property.Name)
                }
                $commandPath = [string]$property.Value.path
                if ($commandPath -match "(?i)(^|[\\/])cli\\.(ps1|bat|cmd|py)$") {
                    Add-Violation -Violations $violations -ProductRoot $productRoot -Message ("manifest command points to CLI path: {0}" -f $commandPath)
                }
            }
        }
    }

    $files = Get-ChildItem -LiteralPath $productRoot -Recurse -File |
        Where-Object { -not (Test-IgnoredCliAuditPath -Path ([string]$_.FullName)) }

    foreach ($file in $files) {
        $name = [string]$file.Name
        $lowerName = $name.ToLowerInvariant()
        if ($forbiddenFileNames -contains $lowerName -or $lowerName.EndsWith("_operations.py")) {
            Add-Violation -Violations $violations -ProductRoot $productRoot -Message ("forbidden CLI file remains: {0}" -f $file.FullName)
        }
    }

    $sourceFiles = $files | Where-Object { Test-SourceCliAuditPath -Path ([string]$_.FullName) }
    foreach ($file in $sourceFiles) {
        $text = Get-Content -LiteralPath $file.FullName -Raw -Encoding UTF8
        foreach ($pattern in $forbiddenSourcePatterns) {
            if ($text.Contains($pattern)) {
                Add-Violation -Violations $violations -ProductRoot $productRoot -Message ("forbidden source reference `{0}` in {1}" -f $pattern, $file.FullName)
            }
        }
    }
}

$hostIntegrationRoot = Join-Path $repoRoot "local-api"
if (Test-Path -LiteralPath $hostIntegrationRoot -PathType Container) {
    $hostSourceFiles = Get-ChildItem -LiteralPath $hostIntegrationRoot -Recurse -File -Filter *.cs |
        Where-Object { -not (Test-IgnoredCliAuditPath -Path ([string]$_.FullName)) }
    foreach ($file in $hostSourceFiles) {
        $text = Get-Content -LiteralPath $file.FullName -Raw -Encoding UTF8
        foreach ($pattern in $forbiddenHostIntegrationPatterns) {
            if ($text.Contains($pattern)) {
                Add-Violation -Violations $violations -ProductRoot $repoRoot -Message ("forbidden host integration reference `{0}` in {1}" -f $pattern, $file.FullName)
            }
        }
    }
}

if ($violations.Count -gt 0) {
    Write-Host "Sub-product CLI removal audit failed." -ForegroundColor Red
    foreach ($violation in $violations) {
        Write-Host ("- {0}" -f $violation) -ForegroundColor Red
    }
    exit 1
}

Write-Host ("Sub-product CLI removal audit passed for {0} product roots." -f $ProductRoots.Count)
