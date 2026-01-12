$Tags = @("v5.3", "v5.2", "v5.0", "v4.0", "v3.0", "v2.0", "v1.2", "v1.1", "AssetsV4.1", "AssetsV4", "V3", "V2.2", "AssetsV2.1", "v1.0", "AssetsV2")
$Repo = "Redactedcraft/Assets"
$Workspace = "$PSScriptRoot\HistoryCleaner"

if (Test-Path $Workspace) { Remove-Item -Recurse -Force $Workspace }
New-Item -ItemType Directory -Path $Workspace | Out-Null

foreach ($Tag in $Tags) {
    Write-Host "Processing Tag: $Tag..." -ForegroundColor Cyan
    
    $TagDir = "$Workspace\$Tag"
    New-Item -ItemType Directory -Path $TagDir | Out-Null
    
    # 1. Download
    gh release download $Tag --repo $Repo --pattern "Assets.zip" --dir $TagDir
    
    if (-not (Test-Path "$TagDir\Assets.zip")) {
        Write-Host "No Assets.zip found for $Tag, skipping." -ForegroundColor Yellow
        continue
    }
    
    # 2. Extract
    $Extracted = "$TagDir\extracted"
    Expand-Archive -Path "$TagDir\Assets.zip" -DestinationPath $Extracted
    
    # 3. Rename File
    $Files = Get-ChildItem -Path $Extracted -Recurse -Filter "bedrock.png"
    foreach ($File in $Files) {
        $NewPath = Join-Path $File.DirectoryName "corestone.png"
        Move-Item $File.FullName $NewPath -Force
        Write-Host "  Renamed file in zip: $($File.Name) -> corestone.png"
    }
    
    # 4. Replace Text Evidence in files
    $TextFiles = Get-ChildItem -Path $Extracted -Recurse -Include "*.md","*.txt","*.json","*.fnt"
    foreach ($TFile in $TextFiles) {
        $Content = Get-Content $TFile.FullName -Raw
        if ($Content -match "bedrock") {
            $Content = $Content -replace "bedrock", "corestone"
            $Content = $Content -replace "Bedrock", "Corestone"
            Set-Content $TFile.FullName $Content -NoNewline
            Write-Host "  Cleaned text evidence in: $($TFile.Name)"
        }
    }
    
    # 5. Re-zip (Careful with structure)
    Remove-Item "$TagDir\Assets.zip" -Force
    Compress-Archive -Path "$Extracted\*" -DestinationPath "$TagDir\Assets.zip" -Force
    
    # 6. Upload
    gh release upload $Tag "$TagDir\Assets.zip" --repo $Repo --clobber
    
    # 7. Update Release Metadata (Title and Notes)
    $ReleaseInfo = gh release view $Tag --repo $Repo --json title,body | ConvertFrom-Json
    $NewTitle = $ReleaseInfo.title -replace "bedrock", "corestone" -replace "Bedrock", "Corestone"
    $NewBody = $ReleaseInfo.body -replace "bedrock", "corestone" -replace "Bedrock", "Corestone"
    
    gh release edit $Tag --repo $Repo --title $NewTitle --notes $NewBody
    
    Write-Host "Completed $Tag." -ForegroundColor Green
}

Write-Host "All historical releases cleaned!" -ForegroundColor Magenta
