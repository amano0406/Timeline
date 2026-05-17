[CmdletBinding()]
param(
    [string]$AudioProductPath = "",
    [string]$WindowsCodexProductPath = "",
    [string]$ChatGptProductPath = "",
    [string]$ImageProductPath = "",
    [string]$DownloadRoot = "",
    [switch]$IncludeDownloads
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$apiContractScript = Join-Path $PSScriptRoot "check-product-api-contracts.ps1"
if (-not (Test-Path -LiteralPath $apiContractScript -PathType Leaf)) {
    throw "Product API contract script was not found: $apiContractScript"
}

$apiArgs = @{}
if ($AudioProductPath) {
    $apiArgs.AudioProductPath = $AudioProductPath
}
if ($WindowsCodexProductPath) {
    $apiArgs.WindowsCodexProductPath = $WindowsCodexProductPath
}
if ($ChatGptProductPath) {
    $apiArgs.ChatGptProductPath = $ChatGptProductPath
}
if ($ImageProductPath) {
    $apiArgs.ImageProductPath = $ImageProductPath
}
if ($DownloadRoot) {
    $apiArgs.DownloadRoot = $DownloadRoot
}
if ($IncludeDownloads) {
    $apiArgs.IncludeDownloads = $true
}

Write-Warning "This compatibility script now runs check-product-api-contracts.ps1."
& $apiContractScript @apiArgs
$exitCode = 0
if (Get-Variable -Name LASTEXITCODE -ErrorAction SilentlyContinue) {
    $exitCode = [int]$LASTEXITCODE
}
exit $exitCode
