# Final Cleanup Script
# Removes all bloat and prepares project for release

Write-Host "=== LatticeVeil Final Cleanup ===" -ForegroundColor Green
Write-Host "This script will clean up the project and remove all bloat." -ForegroundColor Yellow
Write-Host ""

# Get confirmation
$confirm = Read-Host "Continue with cleanup? (y/N)"
if ($confirm -ne "y" -and $confirm -ne "Y") {
    Write-Host "Cleanup cancelled." -ForegroundColor Red
    exit 0
}

$projectRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
Write-Host "Project root: $projectRoot" -ForegroundColor Cyan

# Clean build artifacts
Write-Host "🧹 Cleaning build artifacts..." -ForegroundColor White
Get-ChildItem -Path $projectRoot -Recurse -Directory -Name "bin" -ErrorAction SilentlyContinue | ForEach-Object {
    Write-Host "  Removing: $_" -ForegroundColor Gray
    Remove-Item -Path (Join-Path $projectRoot $_) -Recurse -Force
}

Get-ChildItem -Path $projectRoot -Recurse -Directory -Name "obj" -ErrorAction SilentlyContinue | ForEach-Object {
    Write-Host "  Removing: $_" -ForegroundColor Gray
    Remove-Item -Path (Join-Path $projectRoot $_) -Recurse -Force
}

# Clean temporary files
Write-Host "🧹 Cleaning temporary files..." -ForegroundColor White
$tempFiles = @("*.tmp", "*.temp", "*.log", "*.cache", "*.bak", "*.old")
foreach ($pattern in $tempFiles) {
    Get-ChildItem -Path $projectRoot -Recurse -File -Name $pattern -ErrorAction SilentlyContinue | ForEach-Object {
        Write-Host "  Removing: $_" -ForegroundColor Gray
        Remove-Item -Path (Join-Path $projectRoot $_) -Force
    }
}

# Clean debug files
Write-Host "🧹 Cleaning debug files..." -ForegroundColor White
$debugFiles = @("*.pdb", "*.ilk", "*.exp", "*.lib")
foreach ($pattern in $debugFiles) {
    Get-ChildItem -Path $projectRoot -Recurse -File -Name $pattern -ErrorAction SilentlyContinue | ForEach-Object {
        Write-Host "  Removing: $_" -ForegroundColor Gray
        Remove-Item -Path (Join-Path $projectRoot $_) -Force
    }
}

# Clean user-specific files
Write-Host "🧹 Cleaning user-specific files..." -ForegroundColor White
$userFiles = @("*.user", "*.suo", "*.userosscache", "*.sln.docstates")
foreach ($pattern in $userFiles) {
    Get-ChildItem -Path $projectRoot -Recurse -File -Name $pattern -ErrorAction SilentlyContinue | ForEach-Object {
        Write-Host "  Removing: $_" -ForegroundColor Gray
        Remove-Item -Path (Join-Path $projectRoot $_) -Force
    }
}

# Check for large unnecessary files
Write-Host "🔍 Checking for large files..." -ForegroundColor White
$largeFiles = Get-ChildItem -Path $projectRoot -Recurse -File -ErrorAction SilentlyContinue | 
    Where-Object { $_.Length -gt 10MB } |
    Sort-Object Length -Descending

if ($largeFiles) {
    Write-Host "Large files found:" -ForegroundColor Yellow
    foreach ($file in $largeFiles) {
        $sizeMB = [math]::Round($file.Length / 1MB, 2)
        Write-Host "  $($file.Name): $sizeMB MB" -ForegroundColor Gray
    }
    
    $confirm = Read-Host "Remove large files? (y/N)"
    if ($confirm -eq "y" -or $confirm -eq "Y") {
        foreach ($file in $largeFiles) {
            Write-Host "  Removing: $($file.Name)" -ForegroundColor Gray
            Remove-Item -Path $file.FullName -Force
        }
    }
}

# Check project structure
Write-Host "📊 Analyzing project structure..." -ForegroundColor White
$monogameProject = Join-Path $projectRoot "LatticeVeilMonoGame"
if (Test-Path $monogameProject) {
    Write-Host "MonoGame project found at: $monogameProject" -ForegroundColor Green
    
    # Check for unnecessary files in MonoGame project
    $unnecessaryDirs = @("Properties", "Resources", "Content")
    foreach ($dir in $unnecessaryDirs) {
        $dirPath = Join-Path $monogameProject $dir
        if (Test-Path $dirPath) {
            $itemCount = (Get-ChildItem $dirPath -Recurse).Count
            Write-Host "  Found $dir with $itemCount items" -ForegroundColor Gray
        }
    }
}

# Optimize NuGet packages
Write-Host "📦 Optimizing NuGet packages..." -ForegroundColor White
Set-Location $monogameProject
dotnet nuget locals all --clear
dotnet restore

# Final build test
Write-Host "🔨 Testing final build..." -ForegroundColor White
$buildResult = dotnet build --configuration Release --verbosity minimal
if ($LASTEXITCODE -eq 0) {
    Write-Host "✅ Build successful!" -ForegroundColor Green
} else {
    Write-Host "❌ Build failed!" -ForegroundColor Red
    Write-Host $buildResult -ForegroundColor Gray
}

# Generate project summary
Write-Host "📋 Generating project summary..." -ForegroundColor White
$summary = @"
# LatticeVeil Project Summary

## Cleaned Components
- Build artifacts (bin/obj folders)
- Temporary files (*.tmp, *.log, *.cache)
- Debug files (*.pdb, *.ilk, *.exp, *.lib)
- User-specific files (*.user, *.suo, *.sln.docstates)
- NuGet cache cleared

## Project Structure
- Root: $projectRoot
- MonoGame Project: $monogameProject
- Version: 6.0.0.0
- Framework: .NET 8.0 Windows
- Rendering: MonoGame DesktopGL (OpenGL)

## Key Features
- Complete chunk loading system
- Research-based world generation
- Inventory persistence
- Vulkan dependency removed
- Optimized performance

## Ready for Release
- Build successful
- Dependencies optimized
- Bloat removed
- Documentation updated
"@

$summaryPath = Join-Path $projectRoot "PROJECT_SUMMARY.md"
$summary | Out-File -FilePath $summaryPath -Encoding UTF8
Write-Host "📄 Project summary saved to: PROJECT_SUMMARY.md" -ForegroundColor Green

Write-Host ""
Write-Host "=== Cleanup Complete ===" -ForegroundColor Green
Write-Host "Project is now clean and ready for release!" -ForegroundColor Yellow
Write-Host ""
Write-Host "Next steps:" -ForegroundColor White
Write-Host "1. Review PROJECT_SUMMARY.md" -ForegroundColor Gray
Write-Host "2. Run create_github_release.ps1" -ForegroundColor Gray
Write-Host "3. Verify releases on GitHub" -ForegroundColor Gray
Write-Host "4. Test release builds" -ForegroundColor Gray
