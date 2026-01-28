# Create Release with Executables and ZIP Files
# Adds .exe and .zip files to GitHub releases using the release template.

param(
    [string]$Version = "",
    [string]$Title = "",
    [string]$Description = "",
    [string]$Summary = "",
    [string]$BannerUrl = "",
    [string]$AssetsUrl = "",
    [string]$TemplatePath = "",
    [switch]$Draft = $false
)

Add-Type -AssemblyName System.Windows.Forms

# Configuration
$RepoPath = "C:\Users\Redacted\Documents\LatticeVeil_project"
$BuildsPath = Join-Path $RepoPath "Builds"
$LegacyExePath = Join-Path $BuildsPath "LatticeVeil.exe"
$PublishExePath = Join-Path $RepoPath "LatticeVeilMonoGame\bin\Release\net8.0-windows\win-x64\publish\LatticeVeilGame.exe"
$ReleaseZipPattern = Join-Path $RepoPath ".builder\releases\LatticeVeil_Release_*.zip"
$SourceZipPath = Join-Path $RepoPath "LatticeVeil_project.zip"
$ReleaseTemplatePath = if ([string]::IsNullOrWhiteSpace($TemplatePath)) { Join-Path $RepoPath "RELEASE_TEMPLATE.md" } else { $TemplatePath }

function Get-LatestReleaseZip {
    $latest = Get-ChildItem -Path $ReleaseZipPattern -ErrorAction SilentlyContinue | Sort-Object LastWriteTime -Desc | Select-Object -First 1
    if ($latest) { return $latest.FullName }
    return $null
}

function New-SourceZip {
    param([string]$RepoRoot, [string]$Destination)
    if (Test-Path $Destination) { Remove-Item $Destination -Force }
    & git -C $RepoRoot archive --format=zip -o $Destination HEAD
    if (-not (Test-Path $Destination)) { throw "Source zip not created." }
}

$ExePath = $null
if (Test-Path $PublishExePath) {
    $ExePath = $PublishExePath
} elseif (Test-Path $LegacyExePath) {
    $ExePath = $LegacyExePath
}

$ReleaseZipPath = Get-LatestReleaseZip

# Check if required files exist
if (-not $ExePath) {
    Write-Host "ERROR: Game executable not found."
    Write-Host "Please run Build.ps1 (RELEASE Publish + Package) first."
    exit 1
}

if (-not $ReleaseZipPath) {
    Write-Host "ERROR: Release zip not found."
    Write-Host "Please run Build.ps1 (RELEASE Publish + Package) first."
    exit 1
}

# Get version from build or parameter
if ([string]::IsNullOrEmpty($Version)) {
    $Version = (Get-Item $ExePath).LastWriteTime.ToString("yyyy.MM.dd-HHmm")
}

if ([string]::IsNullOrEmpty($Title)) {
    $Title = "LatticeVeil v$Version"
}

if ([string]::IsNullOrWhiteSpace($Summary)) {
    $Summary = $Description
}

if ([string]::IsNullOrWhiteSpace($Summary)) {
    $Summary = "Release details to be filled in."
}

if ([string]::IsNullOrWhiteSpace($BannerUrl)) {
    $BannerUrl = "https://raw.githubusercontent.com/latticeveil/LatticeVeil/main/LatticeVeilMonoGame/Defaults/Assets/textures/menu/backgrounds/MainMenu.png"
}

if ([string]::IsNullOrWhiteSpace($AssetsUrl)) {
    $AssetsUrl = "https://github.com/latticeveil/Assets/releases"
}

Write-Host "Creating release: $Title"
Write-Host "Version: $Version"
Write-Host "Executable: $ExePath"
Write-Host "Release ZIP: $ReleaseZipPath"

# Create source zip from tracked files
Write-Host "Creating source ZIP..."
New-SourceZip -RepoRoot $RepoPath -Destination $SourceZipPath
Write-Host "Source ZIP: $SourceZipPath"

# Create release notes from template when available
if (Test-Path $ReleaseTemplatePath) {
    $ReleaseNotes = Get-Content -Raw -Path $ReleaseTemplatePath
    $ReleaseNotes = $ReleaseNotes.Replace('{{TITLE}}', $Title)
    $ReleaseNotes = $ReleaseNotes.Replace('{{VERSION}}', $Version)
    $ReleaseNotes = $ReleaseNotes.Replace('{{SUMMARY}}', $Summary)
    $ReleaseNotes = $ReleaseNotes.Replace('{{BANNER_URL}}', $BannerUrl)
    $ReleaseNotes = $ReleaseNotes.Replace('{{ASSETS_URL}}', $AssetsUrl)
} else {
    $ReleaseNotes = @"
![LatticeVeil Banner]($BannerUrl)

# :compass: $Title

$Summary

## :package: Assets & Distribution
- Related assets release: $AssetsUrl

---
*Included Assets: $AssetsUrl*
"@
}

# Create GitHub release using GitHub CLI
try {
    Write-Host "Creating GitHub release..."

    $ReleaseCmd = "gh release create v$Version --title `"$Title`" --notes `"$ReleaseNotes`""
    if ($Draft) {
        $ReleaseCmd += " --draft"
    }

    Set-Location $RepoPath
    Invoke-Expression $ReleaseCmd

    if ($LASTEXITCODE -eq 0) {
        Write-Host "Release created successfully."

        Write-Host "Uploading LatticeVeilGame.exe..."
        gh release upload "v$Version" "$ExePath" --clobber

        Write-Host "Uploading release ZIP..."
        gh release upload "v$Version" "$ReleaseZipPath" --clobber

        Write-Host "Uploading source ZIP..."
        gh release upload "v$Version" "$SourceZipPath" --clobber

        Write-Host "All files uploaded to release."
        Write-Host "Release available at: https://github.com/latticeveil/LatticeVeil/releases/tag/v$Version"
    } else {
        Write-Host "Failed to create release."
        exit 1
    }

} catch {
    Write-Host "Error creating release: $_"
    Write-Host "Make sure GitHub CLI is installed and authenticated."
    exit 1
}

Write-Host "Release creation completed."
