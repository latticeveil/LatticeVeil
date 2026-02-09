# LatticeVeil Build GUI launcher stub
# Keep this file as the double-click entrypoint.

[CmdletBinding()]
param(
    [Parameter(ValueFromRemainingArguments = $true)]
    [string[]]$PassThruArgs
)

$toolRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $toolRoot ".."))
$target = Join-Path $toolRoot "GUI\BuildGUI.ps1"

if (-not (Test-Path -LiteralPath $target)) {
    Write-Error "Build GUI target script not found: $target"
    exit 1
}

Set-Location $repoRoot
& $target @PassThruArgs
