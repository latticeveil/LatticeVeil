param(
    [string]$RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..\\..")).Path
)

$ErrorActionPreference = "Stop"

function Test-ForbiddenPattern {
    param(
        [string]$Pattern,
        [string]$Label,
        [string[]]$ExcludeGlobs = @()
    )

    $args = @("-n", "--glob", "*.cs", "--glob", "*.ps1", "--glob", "!LatticeVeilMonoGame/Tools/VerifyWorldStorage.ps1")
    foreach ($glob in $ExcludeGlobs) {
        $args += @("--glob", "!$glob")
    }
    $args += @($Pattern, $RepoRoot)
    $matches = & rg @args
    if ($LASTEXITCODE -eq 0 -and $matches) {
        Write-Host "Forbidden pattern found: $Label" -ForegroundColor Red
        Write-Host $matches
        return $true
    }

    return $false
}

$failed = $false
$failed = (Test-ForbiddenPattern "spawn_prewarm\\.bin" "spawn prewarm legacy filename" @("LatticeVeilMonoGame/Core/WorldValidator.cs")) -or $failed
$failed = (Test-ForbiddenPattern "chunk_.*\\.bin" "legacy chunk bin writes" @("LatticeVeilMonoGame/Core/LegacyChunkStore.cs", "LatticeVeilMonoGame/Core/WorldValidator.cs")) -or $failed
$failed = (Test-ForbiddenPattern "\\.meshbin|\\.lvmeshbin" "mesh bin artifacts" @("LatticeVeilMonoGame/Core/WorldValidator.cs", "LatticeVeilMonoGame/Core/ChunkMeshCache.cs", "LatticeVeilMonoGame/Core/SpawnMeshCache.cs")) -or $failed
$failed = (Test-ForbiddenPattern "playerdata[\\\\/].*\\.lvc" "playerdata .lvc save path") -or $failed

if ($failed) {
    Write-Host "World storage verification failed." -ForegroundColor Red
    exit 1
}

Write-Host "World storage verification passed." -ForegroundColor Green
exit 0
