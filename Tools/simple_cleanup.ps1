# Simple Cleanup Script
# Removes build artifacts and prepares project for release

Write-Host "=== LatticeVeil Simple Cleanup ===" -ForegroundColor Green

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

# Check project structure
Write-Host "📊 Analyzing project structure..." -ForegroundColor White
$monogameProject = Join-Path $projectRoot "LatticeVeilMonoGame"
if (Test-Path $monogameProject) {
    Write-Host "MonoGame project found at: $monogameProject" -ForegroundColor Green
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
}

Write-Host ""
Write-Host "=== Cleanup Complete ===" -ForegroundColor Green
Write-Host "Project is now clean and ready for release!" -ForegroundColor Yellow
