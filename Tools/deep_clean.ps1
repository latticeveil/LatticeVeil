# Deep Cleanup Script for LatticeVeil_project
# Removes all unnecessary files before git push

param(
    [switch]$Force = $false
)

Write-Host "Starting deep cleanup of LatticeVeil_project folder..."

# Remove all .md files
Write-Host "Removing .md files..."
Get-ChildItem -Path "." -Filter "*.md" -Recurse -Force | ForEach-Object {
    if ($_.Name -ne ".gitignore") {
        Write-Host "  Removing: $($_.FullName)"
        Remove-Item $_.FullName -Force -Recurse
    }
}

# Remove executable files
Write-Host "Removing executable files..."
Get-ChildItem -Path "." -Filter "*.exe" -Recurse -Force | ForEach-Object {
    Write-Host "  Removing: $($_.FullName)"
    Remove-Item $_.FullName -Force
}

# Remove temporary folders
$tempFolders = @("bin", "obj", "_tmp*", "temp*", "Builds", "publish", "out")
foreach ($folder in $tempFolders) {
    Write-Host "Removing temporary folders: $folder"
    Get-ChildItem -Path "." -Filter $folder -Recurse -Force -Directory | ForEach-Object {
        Write-Host "  Removing: $($_.FullName)"
        Remove-Item $_.FullName -Force -Recurse
    }
}

# Remove specific unnecessary files
$unnecessaryFiles = @(
    "*.zip",
    "*.7z", 
    "*.rar",
    "*.tmp",
    "*.cache",
    "Thumbs.db",
    "Desktop.ini"
)

foreach ($pattern in $unnecessaryFiles) {
    Write-Host "Removing files: $pattern"
    Get-ChildItem -Path "." -Filter $pattern -Recurse -Force | ForEach-Object {
        Write-Host "  Removing: $($_.FullName)"
        Remove-Item $_.FullName -Force
    }
}

# Remove VS Code settings that shouldn't be in repo
if (Test-Path ".vscode") {
    Write-Host "Removing .vscode folder..."
    Remove-Item ".vscode" -Force -Recurse
}

# Remove References folder if it contains non-essential files
if (Test-Path "References") {
    Write-Host "Cleaning References folder..."
    Get-ChildItem -Path "References" -Exclude "*.json" | ForEach-Object {
        Write-Host "  Removing: $($_.FullName)"
        Remove-Item $_.FullName -Force -Recurse
    }
}

Write-Host "Deep cleanup completed!"
Write-Host "Repository is now clean for push."
