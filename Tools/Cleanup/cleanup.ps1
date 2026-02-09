# LatticeVeil Unified Cleanup Script
# Consolidated cleanup with multiple levels

param(
    [ValidateSet("light", "medium", "deep", "all")]
    [string]$Level = "medium",
    
    [switch]$Force = $false,
    [switch]$Verbose = $false,
    [string]$Path = "."
)

Write-Host "=== LatticeVeil Cleanup (Level: $Level) ===" -ForegroundColor Green

# Get confirmation for aggressive operations
if ($Level -eq "deep" -or $Level -eq "all" -and -not $Force) {
    $confirm = Read-Host "This will perform aggressive cleanup. Continue? (y/N)"
    if ($confirm -ne "y" -and $confirm -ne "Y") {
        Write-Host "Cleanup cancelled." -ForegroundColor Red
        exit 0
    }
}

$cleanedFiles = 0
$cleanedDirs = 0

function Write-Verbose-Item {
    param([string]$Action, [string]$Item)
    if ($Verbose) { Write-Host "  $Action`: $Item" -ForegroundColor Cyan }
}

function Remove-Items-Safely {
    param(
        [string[]]$Patterns,
        [string]$ItemType = "File",
        [string]$Description = ""
    )
    
    if ($Description) { Write-Host "Cleaning $Description`..." -ForegroundColor Yellow }
    
    foreach ($pattern in $Patterns) {
        try {
            $items = Get-ChildItem -Path $Path -Filter $pattern -Recurse -ErrorAction SilentlyContinue
            foreach ($item in $items) {
                if ($ItemType -eq "File" -and $item.PSIsContainer) { continue }
                if ($ItemType -eq "Directory" -and -not $item.PSIsContainer) { continue }
                
                Write-Verbose-Item "Removing" $item.FullName
                Remove-Item $item.FullName -Force -Recurse -ErrorAction SilentlyContinue
                if ($item.PSIsContainer) { $cleanedDirs++ } else { $cleanedFiles++ }
            }
        } catch {
            if ($Verbose) { Write-Host "Error with pattern $pattern`: $($_.Exception.Message)" -ForegroundColor Red }
        }
    }
}

# Level 1: Light Cleanup (temp files only)
if ($Level -eq "light" -or $Level -eq "all") {
    Write-Host "Performing LIGHT cleanup..." -ForegroundColor Green
    
    $tempPatterns = @("*.tmp", "*.temp", "*.cache", "*.bak", "*.old", "*_temp*", "~*")
    Remove-Items-Safely -Patterns $tempPatterns -ItemType "File" -Description "temporary files"
}

# Level 2: Medium Cleanup (build artifacts + temp files)
if ($Level -eq "medium" -or $Level -eq "all") {
    Write-Host "Performing MEDIUM cleanup..." -ForegroundColor Green
    
    # Build artifacts
    $buildPatterns = @("**/bin/**", "**/obj/**", "*.pdb", "*.user", "*.suo", "*.userosscache")
    Remove-Items-Safely -Patterns $buildPatterns -ItemType "File" -Description "build artifacts"
    
    # Temp directories
    $tempDirs = @("_tmp*", "temp*", "cache*", ".temp")
    Remove-Items-Safely -Patterns $tempDirs -ItemType "Directory" -Description "temporary directories"
    
    # System files
    $systemFiles = @("Thumbs.db", "Desktop.ini", ".DS_Store", "*.swp", "*.swo")
    Remove-Items-Safely -Patterns $systemFiles -ItemType "File" -Description "system files"
}

# Level 3: Deep Cleanup (aggressive - before git push)
if ($Level -eq "deep" -or $Level -eq "all") {
    Write-Host "Performing DEEP cleanup..." -ForegroundColor Green
    
    # Executable files
    $exePatterns = @("*.exe", "*.dll", "*.so", "*.dylib")
    Remove-Items-Safely -Patterns $exePatterns -ItemType "File" -Description "executable files"
    
    # Archive files
    $archivePatterns = @("*.zip", "*.7z", "*.rar", "*.tar.gz")
    Remove-Items-Safely -Patterns $archivePatterns -ItemType "File" -Description "archive files"
    
    # Log files
    $logPatterns = @("*.log", "*.log.*")
    Remove-Items-Safely -Patterns $logPatterns -ItemType "File" -Description "log files"
}

Write-Host "`nCleanup completed:" -ForegroundColor Green
Write-Host "  Files removed: $cleanedFiles" -ForegroundColor Cyan
Write-Host "  Directories removed: $cleanedDirs" -ForegroundColor Cyan

if ($Level -eq "deep" -or $Level -eq "all") {
    Write-Host "`nProject is ready for git push!" -ForegroundColor Green
}
