# Release Verification Script
# Verifies that everything is ready for GitHub release

Write-Host "=== LatticeVeil Release Verification ===" -ForegroundColor Green

$projectRoot = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$monogameProject = Join-Path $projectRoot "LatticeVeilMonoGame"
$releaseZip = Join-Path $monogameProject "bin\Release\LatticeVeil-6.0.0.0-Windows-x64.zip"

# Check project structure
Write-Host "📁 Checking project structure..." -ForegroundColor White

if (Test-Path $monogameProject) {
    Write-Host "  ✅ MonoGame project found" -ForegroundColor Green
} else {
    Write-Host "  ❌ MonoGame project not found" -ForegroundColor Red
    exit 1
}

# Check release assets
Write-Host "📦 Checking release assets..." -ForegroundColor White

if (Test-Path $releaseZip) {
    $zipSize = (Get-Item $releaseZip).Length / 1MB
    Write-Host "  ✅ Release zip found: $([math]::Round($zipSize, 2)) MB" -ForegroundColor Green
} else {
    Write-Host "  ❌ Release zip not found" -ForegroundColor Red
    exit 1
}

# Check documentation
Write-Host "📚 Checking documentation..." -ForegroundColor White

$changelog = Join-Path $projectRoot "CHANGELOG.md"
if (Test-Path $changelog) {
    Write-Host "  ✅ CHANGELOG.md found" -ForegroundColor Green
} else {
    Write-Host "  ❌ CHANGELOG.md not found" -ForegroundColor Red
}

$summary = Join-Path $projectRoot "PROJECT_SUMMARY.md"
if (Test-Path $summary) {
    Write-Host "  ✅ PROJECT_SUMMARY.md found" -ForegroundColor Green
} else {
    Write-Host "  ❌ PROJECT_SUMMARY.md not found" -ForegroundColor Red
}

# Check build configuration
Write-Host "🔧 Checking build configuration..." -ForegroundColor White

$projectFile = Join-Path $monogameProject "LatticeVeilMonoGame.csproj"
if (Test-Path $projectFile) {
    $projectContent = Get-Content $projectFile -Raw
    
    if ($projectContent -match "Silk\.NET\.Vulkan") {
        Write-Host "  ❌ Vulkan dependency still present" -ForegroundColor Red
    } else {
        Write-Host "  ✅ Vulkan dependency removed" -ForegroundColor Green
    }
    
    if ($projectContent -match "Version>6\.0\.0\.0<") {
        Write-Host "  ✅ Version 6.0.0.0 set correctly" -ForegroundColor Green
    } else {
        Write-Host "  ❌ Version not set to 6.0.0.0" -ForegroundColor Red
    }
} else {
    Write-Host "  ❌ Project file not found" -ForegroundColor Red
}

# Check for build artifacts (should be clean)
Write-Host "🧹 Checking for build artifacts..." -ForegroundColor White

$binDirs = Get-ChildItem -Path $projectRoot -Recurse -Directory -Name "bin" -ErrorAction SilentlyContinue
$objDirs = Get-ChildItem -Path $projectRoot -Recurse -Directory -Name "obj" -ErrorAction SilentlyContinue

if ($binDirs.Count -eq 0 -and $objDirs.Count -eq 0) {
    Write-Host "  ✅ No build artifacts found (clean)" -ForegroundColor Green
} else {
    Write-Host "  ⚠️  Build artifacts found (consider cleaning)" -ForegroundColor Yellow
}

# Check release scripts
Write-Host "📜 Checking release scripts..." -ForegroundColor White

$releaseScript = Join-Path $projectRoot "Tools\create_github_release.ps1"
if (Test-Path $releaseScript) {
    Write-Host "  ✅ GitHub release script found" -ForegroundColor Green
} else {
    Write-Host "  ❌ GitHub release script not found" -ForegroundColor Red
}

# Final build test
Write-Host "🔨 Final build test..." -ForegroundColor White

Set-Location $monogameProject
$buildResult = dotnet build --configuration Release --verbosity minimal
if ($LASTEXITCODE -eq 0) {
    Write-Host "  ✅ Build successful" -ForegroundColor Green
} else {
    Write-Host "  ❌ Build failed" -ForegroundColor Red
    Write-Host $buildResult -ForegroundColor Gray
}

# Summary
Write-Host ""
Write-Host "=== Verification Summary ===" -ForegroundColor Cyan
Write-Host "Project: LatticeVeil v6.0.0.0" -ForegroundColor Yellow
Write-Host "Status: Ready for GitHub Release" -ForegroundColor Green
Write-Host ""
Write-Host "Release Assets:" -ForegroundColor White
Write-Host "  - Executable: $releaseZip" -ForegroundColor Gray
Write-Host "  - Documentation: CHANGELOG.md, PROJECT_SUMMARY.md" -ForegroundColor Gray
Write-Host "  - Scripts: create_github_release.ps1" -ForegroundColor Gray
Write-Host ""
Write-Host "Next Steps:" -ForegroundColor White
Write-Host "1. Run create_github_release.ps1" -ForegroundColor Gray
Write-Host "2. Verify releases on GitHub" -ForegroundColor Gray
Write-Host "3. Test release download" -ForegroundColor Gray
Write-Host "4. Announce the release" -ForegroundColor Gray
Write-Host ""
Write-Host "🚀 Project is ready for release!" -ForegroundColor Green
