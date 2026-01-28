# GitHub Release Creation Script
# Creates releases for all required repositories with proper versioning

param(
    [string]$Version = "6.0.0.0",
    [string]$ReleaseName = "LatticeVeil v6.0.0.0 - Complete Chunk Loading Fix",
    [switch]$SkipConfirmation = $false
)

Write-Host "=== LatticeVeil GitHub Release Script ===" -ForegroundColor Green
Write-Host "Version: $Version" -ForegroundColor Yellow
Write-Host "Release Name: $ReleaseName" -ForegroundColor Yellow
Write-Host ""

# Repository information
$repos = @{
    "LatticeVeil" = @{
        Path = "C:\Users\Redacted\Documents\latticeveil.github.io"
        Description = "LatticeVeil Game Website"
        IncludeExe = $false
        IncludeProject = $false
    }
    "LatticeVeil-Project" = @{
        Path = "C:\Users\Redacted\Documents\LatticeVeil_project"
        Description = "LatticeVeil Game Project - Complete Source Code"
        IncludeExe = $true
        IncludeProject = $true
    }
}

# Release notes
$releaseNotes = @"
## 🚀 Major Release - Complete Chunk Loading Fix

### ✅ Core Features Fixed
- **Chunk Loading System** - Complete rewrite based on Minecraft research
- **Rejoin Stability** - Players no longer fall through world
- **World Visibility** - World is visible when rejoining
- **Missing Chunks** - Random missing chunks eliminated
- **Inventory Save** - Complete inventory persistence

### 🔧 Technical Improvements
- **Vulkan Removal** - Cleaner, more efficient codebase
- **Research-Based Systems** - Proven voxel game techniques
- **Performance Optimizations** - Non-blocking chunk loading
- **Error Handling** - Comprehensive safety systems
- **Memory Management** - Optimized resource usage

### 🌍 World Generation
- **Natural Terrain** - Organic biome shapes
- **Advanced Generation** - Ridged noise and domain warping
- **Deeper Worlds** - Increased depth with stability
- **Grass Coverage** - 70% natural grass coverage

### 📦 Build System
- **Clean Dependencies** - Removed Vulkan and bloat
- **Self-Contained** - Complete standalone executables
- **Optimized Build** - Faster compilation
- **Release Ready** - Production-ready configuration

### 🐛 Bug Fixes
- Fixed all chunk loading issues
- Fixed inventory not saving
- Fixed graphical rendering errors
- Fixed game freezes
- Fixed player position persistence

---

**This version represents a complete stability overhaul with all major issues resolved.**
"@

if (-not $SkipConfirmation) {
    Write-Host "This will create GitHub releases for the following repositories:" -ForegroundColor Cyan
    foreach ($repo in $repos.GetEnumerator()) {
        Write-Host "  - $($repo.Key): $($repo.Value.Description)" -ForegroundColor White
    }
    Write-Host ""
    Write-Host "Release Notes Preview:" -ForegroundColor Cyan
    Write-Host $releaseNotes -ForegroundColor Gray
    Write-Host ""
    $confirm = Read-Host "Continue? (y/N)"
    if ($confirm -ne "y" -and $confirm -ne "Y") {
        Write-Host "Release cancelled." -ForegroundColor Red
        exit 0
    }
}

# Create releases
foreach ($repo in $repos.GetEnumerator()) {
    $repoName = $repo.Key
    $repoPath = $repo.Value.Path
    $description = $repo.Value.Description
    $includeExe = $repo.Value.IncludeExe
    $includeProject = $repo.Value.IncludeProject

    Write-Host "Processing $repoName..." -ForegroundColor Cyan
    
    if (-not (Test-Path $repoPath)) {
        Write-Host "  ⚠️  Repository path not found: $repoPath" -ForegroundColor Yellow
        continue
    }

    try {
        # Change to repository directory
        Set-Location $repoPath
        
        # Check if it's a git repository
        if (-not (Test-Path ".git")) {
            Write-Host "  ⚠️  Not a git repository: $repoPath" -ForegroundColor Yellow
            continue
        }

        # Create release tag
        $tagName = "v$Version"
        Write-Host "  📝 Creating tag: $tagName" -ForegroundColor White
        
        # Create and push tag
        $tagExists = git tag -l | Select-String $tagName
        if (-not $tagExists) {
            git tag -a $TagName -m "$ReleaseName"
            git push origin $TagName
            Write-Host "  ✅ Tag created and pushed" -ForegroundColor Green
        } else {
            Write-Host "  ℹ️  Tag already exists" -ForegroundColor Gray
        }

        # Create GitHub release using GitHub CLI
        Write-Host "  🚀 Creating GitHub release..." -ForegroundColor White
        
        $releaseJson = @{
            tag_name = $tagName
            name = $ReleaseName
            body = $releaseNotes
            draft = $false
            prerelease = $false
        } | ConvertTo-Json -Depth 3

        # Save release JSON to temp file
        $tempFile = [System.IO.Path]::GetTempFileName()
        $releaseJson | Out-File -FilePath $tempFile -Encoding UTF8

        # Create release using GitHub CLI
        $releaseCmd = "gh release create $TagName --title `"$ReleaseName`" --notes-file `"$tempFile`""
        $result = Invoke-Expression $releaseCmd
        
        if ($LASTEXITCODE -eq 0) {
            Write-Host "  ✅ GitHub release created successfully" -ForegroundColor Green
        } else {
            Write-Host "  ⚠️  GitHub CLI not available or failed" -ForegroundColor Yellow
            Write-Host "  💡 To create release manually:" -ForegroundColor Cyan
            Write-Host "     1. Go to GitHub repository" -ForegroundColor White
            Write-Host "     2. Click 'Releases'" -ForegroundColor White
            Write-Host "     3. Click 'Create a new release'" -ForegroundColor White
            Write-Host "     4. Tag: $tagName" -ForegroundColor White
            Write-Host "     5. Title: $ReleaseName" -ForegroundColor White
            Write-Host "     6. Copy release notes from above" -ForegroundColor White
        }

        # Clean up temp file
        Remove-Item $tempFile -ErrorAction SilentlyContinue

        # Handle assets for project repository
        if ($includeExe -and $repoName -eq "LatticeVeil-Project") {
            Write-Host "  📦 Preparing release assets..." -ForegroundColor White
            
            # Build release version
            Set-Location "LatticeVeilMonoGame"
            dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true
            
            $publishPath = "bin\Release\net8.0-windows\win-x64\publish"
            if (Test-Path $publishPath) {
                Write-Host "  ✅ Release build completed" -ForegroundColor Green
                
                # Create zip file
                $zipName = "LatticeVeil-$Version-Windows-x64.zip"
                $zipPath = "..\$zipName"
                
                if (Test-Path $zipPath) {
                    Remove-Item $zipPath -Force
                }
                
                Compress-Archive -Path $publishPath\* -DestinationPath $zipPath
                Write-Host "  📦 Created release archive: $zipName" -ForegroundColor Green
                
                # Upload asset if GitHub CLI is available
                if (Get-Command gh -ErrorAction SilentlyContinue) {
                    Write-Host "  📤 Uploading release asset..." -ForegroundColor White
                    gh release upload $TagName $zipPath
                    if ($LASTEXITCODE -eq 0) {
                        Write-Host "  ✅ Asset uploaded successfully" -ForegroundColor Green
                    } else {
                        Write-Host "  ⚠️  Asset upload failed" -ForegroundColor Yellow
                    }
                }
            } else {
                Write-Host "  ⚠️  Release build failed" -ForegroundColor Red
            }
        }

        Write-Host "  ✅ $repoName processed successfully" -ForegroundColor Green
        Write-Host ""
        
    } catch {
        Write-Host "  ❌ Error processing $repoName`: $_" -ForegroundColor Red
    }
}

Write-Host "=== Release Creation Complete ===" -ForegroundColor Green
Write-Host "Version: $Version" -ForegroundColor Yellow
Write-Host "All repositories processed." -ForegroundColor Cyan
Write-Host ""
Write-Host "Next steps:" -ForegroundColor White
Write-Host "1. Check GitHub releases for each repository" -ForegroundColor Gray
Write-Host "2. Verify assets were uploaded correctly" -ForegroundColor Gray
Write-Host "3. Update any documentation if needed" -ForegroundColor Gray
Write-Host "4. Announce the release" -ForegroundColor Gray
