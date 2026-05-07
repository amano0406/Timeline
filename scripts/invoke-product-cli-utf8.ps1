[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$ScriptPath,

    [Parameter(ValueFromRemainingArguments = $true)]
    [string[]]$CliArgs
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$utf8NoBom = [System.Text.UTF8Encoding]::new($false)
[Console]::OutputEncoding = $utf8NoBom
[Console]::InputEncoding = $utf8NoBom
$OutputEncoding = $utf8NoBom
$env:PYTHONUTF8 = "1"
$env:PYTHONIOENCODING = "utf-8"

& $ScriptPath @CliArgs
if ($global:LASTEXITCODE -is [int]) {
    exit $global:LASTEXITCODE
}
exit 0
