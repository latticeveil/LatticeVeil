# Push All Repositories System
# Ensures all 3 repositories are pushed properly when requested

param(
    [string]$Message = "",
    [switch]$Force = $false
)

Write-Host "========================================"
Write-Host "   MULTI-REPOSITORY PUSH SYSTEM"
Write-Host "========================================"
Write-Host ""

# Repository paths and configurations
$Repositories = @(
    @{
        Name = "Main Game Repository"
        Path = "C:\Users\Redacted\Documents\LatticeVeil_project"
        Remote = "origin"
        Branch = "main"
        Description = "Main LatticeVeil game repository"
    },
    @{
        Name = "Assets Repository"
        Path = "C:\Users\Redacted\Documents\LatticeVeil_Assets_repo"
        Remote = "origin"
        Branch = "main"
        Description = "Textures-only assets repository"
    },
    @{
        Name = "Website Repository"
        Path = "C:\Users\Redacted\Documents\latticeveil.github.io"
        Remote = "origin"
        Branch = "main"
        Description = "Website and documentation repository"
    }
)

# Auto-generate commit message if not provided
if ([string]::IsNullOrEmpty($Message)) {
    $Message = "Repository update and maintenance

- Enhanced gravestone textures and block improvements
- Performance optimizations and bug fixes
- Updated build system and asset management
- Clean repository with automated maintenance"
}

Write-Host "Commit Message: $Message"
Write-Host ""

$SuccessCount = 0
$TotalCount = $Repositories.Count

# Process each repository
foreach ($Repo in $Repositories) {
    Write-Host "----------------------------------------"
    Write-Host "Processing: $($Repo.Name)"
    Write-Host "Path: $($Repo.Path)"
    Write-Host "----------------------------------------"
    
    try {
        # Check if repository exists
        if (-not (Test-Path $Repo.Path)) {
            Write-Host "❌ Repository path not found: $($Repo.Path)"
            continue
        }
        
        # Change to repository directory
        Set-Location $Repo.Path
        
        # Check if it's a git repository
        $GitStatus = git status 2>&1
        if ($GitStatus -match "fatal: not a git repository") {
            Write-Host "❌ Not a git repository: $($Repo.Path)"
            continue
        }
        
        # Run pre-push cleanup for main repo
        if ($Repo.Name -eq "Main Game Repository") {
            Write-Host "🧹 Running pre-push cleanup..."
            powershell -ExecutionPolicy Bypass -NoProfile -File "Tools\deep_clean.ps1" -Force
        }

        # Refresh textures-only asset zip before pushing assets repo
        if ($Repo.Name -eq "Assets Repository") {
            $sourceTextures = "C:\Users\Redacted\Documents\LatticeVeil_project\LatticeVeilMonoGame\Defaults\Assets\textures"
            $texturesDir = Join-Path $Repo.Path "textures"
            $zipPath = Join-Path $Repo.Path "Assets.zip"
            $legacyZip = Join-Path $Repo.Path "textures.zip"

            if (Test-Path $sourceTextures) {
                Write-Host "🧹 Syncing textures from game repo..."
                & robocopy $sourceTextures $texturesDir /MIR /NFL /NDL /NJH /NJS /NP | Out-Null
            } else {
                Write-Host "⚠️ Source textures folder not found: $sourceTextures"
            }

            if (Test-Path $legacyZip) { Remove-Item $legacyZip -Force }
            if (Test-Path $zipPath) { Remove-Item $zipPath -Force }
            if (Test-Path $texturesDir) {
                Compress-Archive -Path (Join-Path $texturesDir '*') -DestinationPath $zipPath -Force
                Write-Host "🧹 Refreshed Assets.zip (textures-only)."
            } else {
                Write-Host "⚠️ textures folder not found: $texturesDir"
            }
        }
        
        # Check for changes
        $StatusOutput = git status --porcelain
        if ([string]::IsNullOrWhiteSpace($StatusOutput)) {
            Write-Host "✅ No changes to commit in $($Repo.Name)"
            $SuccessCount++
        } else {
            Write-Host "📝 Committing changes in $($Repo.Name)..."
            
            # Add all changes
            git add -A
            
            # Commit changes
            git commit -m $Message
            
            if ($LASTEXITCODE -eq 0) {
                Write-Host "✅ Changes committed in $($Repo.Name)"
                
                # Push to remote
                Write-Host "🚀 Pushing to $($Repo.Remote)/$($Repo.Branch)..."
                git push $($Repo.Remote) $Repo.Branch
                
                if ($LASTEXITCODE -eq 0) {
                    Write-Host "✅ Successfully pushed $($Repo.Name)"
                    $SuccessCount++
                } else {
                    Write-Host "❌ Failed to push $($Repo.Name)"
                }
            } else {
                Write-Host "❌ Failed to commit changes in $($Repo.Name)"
            }
        }
        
    } catch {
        Write-Host "❌ Error processing $($Repo.Name): $_"
    }
    
    Write-Host ""
}

# Summary
Write-Host "========================================"
Write-Host "           PUSH SUMMARY"
Write-Host "========================================"
Write-Host "Total Repositories: $TotalCount"
Write-Host "Successful: $SuccessCount"
Write-Host "Failed: $($TotalCount - $SuccessCount)"

if ($SuccessCount -eq $TotalCount) {
    Write-Host ""
    Write-Host "🎉 ALL REPOSITORIES PUSHED SUCCESSFULLY!"
    Write-Host ""
    Write-Host "Repositories updated:"
    foreach ($Repo in $Repositories) {
        Write-Host "  - $($Repo.Name)"
    }
} else {
    Write-Host ""
    Write-Host "⚠️  SOME REPOSITORIES FAILED TO PUSH"
    Write-Host "Please check the errors above and retry"
}

Write-Host ""
Write-Host "Multi-repository push completed!"
