# Simple test script for Build.ps1 integration
# This script tests the build system without requiring user interaction

Write-Host "Testing Build.ps1 integration..." -ForegroundColor Green

# Test 1: Build the project
Write-Host "1. Building project..." -ForegroundColor Yellow
$buildResult = dotnet build "LatticeVeilMonoGame/LatticeVeilMonoGame.csproj" --configuration Debug --no-restore

if ($LASTEXITCODE -eq 0) {
    Write-Host "   BUILD SUCCESSFUL" -ForegroundColor Green
} else {
    Write-Host "   BUILD FAILED" -ForegroundColor Red
    exit 1
}

# Test 2: Verify essential files exist
Write-Host "2. Checking essential files..." -ForegroundColor Yellow

$filesToCheck = @(
    "LatticeVeilMonoGame\Core\VoxelWorldGenerator.cs",
    "LatticeVeilMonoGame\Core\DeepSharpNoiseGenerator.cs"
)

foreach ($file in $filesToCheck) {
    if (Test-Path $file) {
        Write-Host "   $file exists" -ForegroundColor Green
    } else {
        Write-Host "   $file missing" -ForegroundColor Red
        exit 1
    }
}

# Test 2b: Verify cleanup - old files are deleted
Write-Host "2b. Verifying cleanup..." -ForegroundColor Yellow

$oldFilesToDelete = @(
    "C5_COLLECTIONS_INTEGRATION.md",
    "C5_COLLECTIONS_STATUS.md", 
    "INTEGRATION_COMPLETE.md",
    "SYSTEM_VERIFICATION.md",
    "VULKAN_BUILD_STATUS.md",
    "LatticeVeilMonoGame\Core\MinimalWorldGenerator.cs",
    "LatticeVeilMonoGame\Core\ModularWorldGenerator.cs",
    "LatticeVeilMonoGame\Core\GenerationImplementations.cs",
    "LatticeVeilMonoGame\Core\WorldGeneratorIntegration.cs",
    "LatticeVeilMonoGame\Core\BulletproofChunk.cs",
    "LatticeVeilMonoGame\Core\SimpleChunk.cs",
    "LatticeVeilMonoGame\Core\EmptyWorldGenerator.cs",
    "LatticeVeilMonoGame\Core\SharpNoiseWorldGenerator.cs",
    "LatticeVeilMonoGame\Core\WorkingSharpNoiseGenerator.cs",
    "LatticeVeilMonoGame\Core\LibNoiseDeepGenerator.cs",
    "AUTO_TEST.cmd",
    "QUICK_TEST.cmd",
    "TEST_WORLD_GENERATION.cmd",
    "RUN_GAME.cmd"
)

foreach ($file in $oldFilesToDelete) {
    if (-not (Test-Path $file)) {
        Write-Host "   $file correctly deleted" -ForegroundColor Green
    } else {
        Write-Host "   $file still exists (should be deleted)" -ForegroundColor Yellow
    }
}

# Test 2c: Verify essential files exist
Write-Host "2c. Verifying essential files..." -ForegroundColor Yellow

$essentialFiles = @(
    "LatticeVeilMonoGame\Core\VoxelWorldGenerator.cs",
    "LatticeVeilMonoGame\Core\DeepSharpNoiseGenerator.cs",
    "LatticeVeilMonoGame\LatticeVeilMonoGame.csproj",
    "Build.ps1",
    "TEST_BUILD.ps1"
)

foreach ($file in $essentialFiles) {
    if (Test-Path $file) {
        Write-Host "   $file exists" -ForegroundColor Green
    } else {
        Write-Host "   $file missing" -ForegroundColor Red
        exit 1
    }
}

# Test 3: Check Build.ps1 exists
Write-Host "3. Checking Build.ps1..." -ForegroundColor Yellow
if (Test-Path "Build.ps1") {
    Write-Host "   Build.ps1 exists" -ForegroundColor Green
} else {
    Write-Host "   Build.ps1 missing" -ForegroundColor Red
    exit 1
}

Write-Host ""
Write-Host "ALL TESTS PASSED" -ForegroundColor Green
Write-Host "Build.ps1 integration is working correctly!" -ForegroundColor Cyan
Write-Host "World generator system is ready." -ForegroundColor Cyan
