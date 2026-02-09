# LatticeVeil Build and Release Script
# Unified build and release automation

param(
    [ValidateSet("build", "release", "github-release", "verify")]
    [string]$Action = "build",
    
    [string]$Version = "",
    [switch]$SkipTests = $false,
    [switch]$Force = $false
)

Write-Host "=== LatticeVeil Build & Release Automation ===" -ForegroundColor Green

$scriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$toolsRoot = [System.IO.Path]::GetFullPath((Join-Path $scriptRoot ".."))
$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $scriptRoot "..\.."))
$timestamp = Get-Date -Format "yyyyMMdd-HHmmss"

function Build-Project {
    Write-Host "Building LatticeVeil project..." -ForegroundColor Yellow
    
    # Run main build script when present, otherwise use direct dotnet build.
    $buildScript = Join-Path $toolsRoot "Build.ps1"
    if (Test-Path $buildScript) {
        & $buildScript
        if ($LASTEXITCODE -ne 0) {
            Write-Host "Build failed!" -ForegroundColor Red
            exit 1
        }
    } else {
        $project = Join-Path $repoRoot "LatticeVeilMonoGame\LatticeVeilMonoGame.csproj"
        dotnet build $project
        if ($LASTEXITCODE -ne 0) {
            Write-Host "Build failed!" -ForegroundColor Red
            exit 1
        }
    }
    
    Write-Host "Build completed successfully!" -ForegroundColor Green
}

function Create-Release {
    param([string]$ReleaseVersion)
    
    if (-not $ReleaseVersion) {
        $ReleaseVersion = Read-Host "Enter release version (e.g., 1.0.0)"
    }
    
    Write-Host "Creating release $ReleaseVersion..." -ForegroundColor Yellow
    
    # Run release creation script
    $releaseScript = Join-Path $scriptRoot "create_release.ps1"
    if (Test-Path $releaseScript) {
        & $releaseScript -Version $ReleaseVersion
    } else {
        Write-Host "Release script not found!" -ForegroundColor Red
        exit 1
    }
    
    Write-Host "Release $ReleaseVersion created!" -ForegroundColor Green
}

function Create-GitHub-Release {
    param([string]$ReleaseVersion)
    
    if (-not $ReleaseVersion) {
        $ReleaseVersion = Read-Host "Enter release version (e.g., 1.0.0)"
    }
    
    Write-Host "Creating GitHub release $ReleaseVersion..." -ForegroundColor Yellow
    
    # Run GitHub release script
    $githubScript = Join-Path $scriptRoot "create_github_release.ps1"
    if (Test-Path $githubScript) {
        & $githubScript -Version $ReleaseVersion
    } else {
        Write-Host "GitHub release script not found!" -ForegroundColor Red
        exit 1
    }
    
    Write-Host "GitHub release $ReleaseVersion created!" -ForegroundColor Green
}

function Verify-Release {
    param([string]$ReleaseVersion)
    
    if (-not $ReleaseVersion) {
        $ReleaseVersion = Read-Host "Enter release version to verify"
    }
    
    Write-Host "Verifying release $ReleaseVersion..." -ForegroundColor Yellow
    
    # Run verification script
    $verifyScript = Join-Path $scriptRoot "verify_release.ps1"
    if (Test-Path $verifyScript) {
        & $verifyScript -Version $ReleaseVersion
    } else {
        Write-Host "Verification script not found!" -ForegroundColor Red
        exit 1
    }
    
    Write-Host "Release $ReleaseVersion verified!" -ForegroundColor Green
}

# Main execution
switch ($Action) {
    "build" {
        Build-Project
    }
    "release" {
        Create-Release -ReleaseVersion $Version
    }
    "github-release" {
        Create-GitHub-Release -ReleaseVersion $Version
    }
    "verify" {
        Verify-Release -ReleaseVersion $Version
    }
    default {
        Write-Host "Unknown action: $Action" -ForegroundColor Red
        exit 1
    }
}

Write-Host "Operation completed successfully!" -ForegroundColor Green
