# Tools/CleanZips.ps1
# Removes .gitattributes and .gitignore from inside release ZIP files on GitHub.

function Clean-ReleaseZips($Repo, $Tags, $ZipPattern) {
    Write-Host "Cleaning Repo: $Repo" -ForegroundColor White -BackgroundColor Blue
    $Workspace = "$PSScriptRoot\ZipCleaner\$($Repo.Replace('/', '_'))"
    if (Test-Path $Workspace) { Remove-Item -Recurse -Force $Workspace }
    New-Item -ItemType Directory -Path $Workspace | Out-Null

    foreach ($Tag in $Tags) {
        Write-Host "  Processing Tag: $Tag..." -ForegroundColor Cyan
        $TagDir = "$Workspace\$Tag"
        New-Item -ItemType Directory -Path $TagDir | Out-Null

        # 1. Download matching assets
        gh release download $Tag --repo $Repo --pattern "$ZipPattern" --dir $TagDir

        $Zips = Get-ChildItem -Path $TagDir -Filter "*.zip"
        foreach ($Zip in $Zips) {
            Write-Host "    Found Zip: $($Zip.Name)" -ForegroundColor Gray
            
            $Extracted = "$TagDir\extracted_$($Zip.BaseName)"
            Expand-Archive -Path $Zip.FullName -DestinationPath $Extracted

            # 2. Remove target files
            $FilesToRemove = @(".gitattributes", ".gitignore")
            $RemovedAny = $false
            foreach ($File in $FilesToRemove) {
                $Path = Join-Path $Extracted $File
                if (Test-Path $Path) {
                    Remove-Item $Path -Force
                    Write-Host "      Removed: $File" -ForegroundColor Yellow
                    $RemovedAny = $true
                }
                # Also check subdirectories just in case
                Get-ChildItem -Path $Extracted -Recurse -Filter $File | Remove-Item -Force
            }

            if ($RemovedAny) {
                # 3. Re-zip
                Remove-Item $Zip.FullName -Force
                Compress-Archive -Path "$Extracted\*" -DestinationPath $Zip.FullName -Force
                
                # 4. Upload
                gh release upload $Tag $Zip.FullName --repo $Repo --clobber
                Write-Host "    Cleaned and re-uploaded $($Zip.Name)" -ForegroundColor Green
            } else {
                Write-Host "    No git files found in $($Zip.Name), skipping upload." -ForegroundColor Gray
            }
            
            Remove-Item $Extracted -Recurse -Force
        }
    }
}

# --- Assets Repo ---
$AssetTags = @("v6.0", "v5.4", "v5.3", "v5.2", "v5.0", "v4.1", "v4.0", "v3.0", "v2.2", "v2.1", "v2.0", "v1.2", "v1.1", "v1.0")
Clean-ReleaseZips "latticeveil/Assets" $AssetTags "Assets.zip"

# --- Game Repo ---
$GameTags = @("v0.1.1")
Clean-ReleaseZips "LatticeVeil/LatticeVeil" $GameTags "LatticeVeil_project.zip"

Write-Host "`nAll specified releases cleaned!" -ForegroundColor Magenta
