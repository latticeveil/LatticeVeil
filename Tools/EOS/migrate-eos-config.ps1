param(
    [string]$LegacyPath = ".\eos.config.json",
    [string]$PublicPath = ".\eos.public.json",
    [string]$PrivatePath = ".\eos.private.json"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

if (-not (Test-Path -LiteralPath $LegacyPath)) {
    throw "Legacy EOS config not found at: $LegacyPath"
}

$legacy = Get-Content -LiteralPath $LegacyPath -Raw | ConvertFrom-Json

$public = [ordered]@{}
foreach ($prop in $legacy.PSObject.Properties) {
    if ($prop.Name -ne "ClientSecret") {
        $public[$prop.Name] = $prop.Value
    }
}

$private = [ordered]@{
    ClientSecret = $legacy.ClientSecret
}

$public | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $PublicPath -NoNewline -Encoding UTF8
$private | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $PrivatePath -NoNewline -Encoding UTF8

Write-Host "EOS migration complete."
Write-Host "Created: $PublicPath"
Write-Host "Created: $PrivatePath"
Write-Host "Reminder: ensure eos.private.json stays untracked."
