# LatticeVeil Build GUI - All-in-One Build Tool
# Double-click this file to run the GUI

Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing

# Global variables
$RepoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot "..\.."))
$ScriptRoot = $RepoRoot
$LogDir = Join-Path $ScriptRoot ".builder\logs"
$LogFile = Join-Path $LogDir "build.log"
Set-Location $ScriptRoot

# Create directories if they don't exist
if (-not (Test-Path -LiteralPath $LogDir)) {
    New-Item -ItemType Directory -Path $LogDir -Force | Out-Null
}

# Main Form
$Form = New-Object System.Windows.Forms.Form
$Form.Text = "LatticeVeil Build GUI"
$Form.Size = New-Object System.Drawing.Size(700, 550)
$Form.StartPosition = "CenterScreen"
$Form.FormBorderStyle = "FixedDialog"
$Form.MaximizeBox = $false
$Form.BackColor = [System.Drawing.Color]::FromArgb(32, 32, 32)
$Form.ForeColor = [System.Drawing.Color]::White

# Title Label
$TitleLabel = New-Object System.Windows.Forms.Label
$TitleLabel.Text = "LatticeVeil Build Tool"
$TitleLabel.Font = New-Object System.Drawing.Font("Segoe UI", 18, [System.Drawing.FontStyle]::Bold)
$TitleLabel.Location = New-Object System.Drawing.Point(200, 20)
$TitleLabel.Size = New-Object System.Drawing.Size(300, 35)
$TitleLabel.ForeColor = [System.Drawing.Color]::White
$Form.Controls.Add($TitleLabel)

# Status Label
$StatusLabel = New-Object System.Windows.Forms.Label
$StatusLabel.Text = "Ready to build"
$StatusLabel.Location = New-Object System.Drawing.Point(30, 70)
$StatusLabel.Size = New-Object System.Drawing.Size(640, 20)
$StatusLabel.ForeColor = [System.Drawing.Color]::LightGray
$StatusLabel.Font = New-Object System.Drawing.Font("Segoe UI", 10)
$Form.Controls.Add($StatusLabel)

# Progress Bar
$ProgressBar = New-Object System.Windows.Forms.ProgressBar
$ProgressBar.Location = New-Object System.Drawing.Point(30, 100)
$ProgressBar.Size = New-Object System.Drawing.Size(640, 25)
$ProgressBar.Style = "Continuous"
$Form.Controls.Add($ProgressBar)

# Output TextBox
$OutputBox = New-Object System.Windows.Forms.TextBox
$OutputBox.Multiline = $true
$OutputBox.ScrollBars = "Vertical"
$OutputBox.Location = New-Object System.Drawing.Point(30, 140)
$OutputBox.Size = New-Object System.Drawing.Size(640, 260)
$OutputBox.Font = New-Object System.Drawing.Font("Consolas", 10)
$OutputBox.BackColor = [System.Drawing.Color]::FromArgb(25, 25, 25)
$OutputBox.ForeColor = [System.Drawing.Color]::LightGreen
$OutputBox.BorderStyle = "FixedSingle"
$Form.Controls.Add($OutputBox)

# Button Panel
$ButtonPanel = New-Object System.Windows.Forms.Panel
$ButtonPanel.Location = New-Object System.Drawing.Point(30, 410)
$ButtonPanel.Size = New-Object System.Drawing.Size(640, 120)
$ButtonPanel.BackColor = [System.Drawing.Color]::FromArgb(40, 40, 40)
$Form.Controls.Add($ButtonPanel)

# Build Functions
function Write-Log {
    param([string]$Message, [string]$Level = "INFO")
    $timestamp = Get-Date -Format "HH:mm:ss"
    $logMessage = "[$timestamp] [$Level] $Message"
    
    # Add to output box
    $OutputBox.AppendText("$logMessage`r`n")
    $OutputBox.ScrollToCaret()
    
    # Write to log file
    Add-Content -Path $LogFile -Value $logMessage -Encoding UTF8
    
    # Update status
    $StatusLabel.Text = $Message
    $Form.Refresh()
}

function Run-BuildTask {
    param([string]$Task, [string]$Description)
    
    # Clear output and reset progress
    $OutputBox.Clear()
    $ProgressBar.Value = 0
    $ProgressBar.Maximum = 100
    
    Write-Log "Starting: $Description"
    $ProgressBar.Value = 10
    
    try {
        $gameDir = Join-Path $ScriptRoot "LatticeVeilMonoGame"
        
        switch ($Task) {
            "dev" {
                Write-Log "Cleaning temporary files..."
                $ProgressBar.Value = 20
                
                # Clean
                Get-ChildItem -Path $gameDir -Include "bin", "obj" -Recurse -Directory | Remove-Item -Recurse -Force -ErrorAction SilentlyContinue
                $ProgressBar.Value = 40
                
                Write-Log "Restoring packages..."
                Set-Location $gameDir
                $restoreResult = dotnet restore 2>&1
                if ($LASTEXITCODE -ne 0) { throw "Package restore failed: $restoreResult" }
                $ProgressBar.Value = 60
                
                Write-Log "Building with launcher..."
                $buildResult = dotnet build --configuration Debug -p:DefineConstants="LAUNCHER_ENABLED" 2>&1
                if ($LASTEXITCODE -ne 0) { throw "Build failed: $buildResult" }
                $ProgressBar.Value = 70
                
                Write-Log "Copying assets to debug directory..."
                $sourceAssets = Join-Path $gameDir "Defaults\Assets"
                $debugDir = Join-Path $gameDir "bin\Debug\net8.0-windows"
                if (Test-Path $sourceAssets) {
                    Copy-Item -Path "$sourceAssets\*" -Destination $debugDir -Recurse -Force
                }
                $ProgressBar.Value = 80
                
                Write-Log "Starting game with launcher..."
                Start-Process dotnet -ArgumentList "run", "--configuration", "Debug" -WorkingDirectory $gameDir
                $ProgressBar.Value = 100
                
                Write-Log "✅ Game started with launcher!"
            }
            
            "build-only" {
                Write-Log "Cleaning temporary files..."
                $ProgressBar.Value = 20
                
                # Clean
                Get-ChildItem -Path $gameDir -Include "bin", "obj" -Recurse -Directory | Remove-Item -Recurse -Force -ErrorAction SilentlyContinue
                $ProgressBar.Value = 40
                
                Write-Log "Restoring packages..."
                Set-Location $gameDir
                $restoreResult = dotnet restore 2>&1
                if ($LASTEXITCODE -ne 0) { throw "Package restore failed: $restoreResult" }
                $ProgressBar.Value = 60
                
                Write-Log "Building without launcher..."
                $buildResult = dotnet build --configuration Debug 2>&1
                if ($LASTEXITCODE -ne 0) { throw "Build failed: $buildResult" }
                $ProgressBar.Value = 100
                
                Write-Log "✅ Build completed without launcher!"
            }
            
            "dev-no-launcher" {
                Write-Log "Cleaning temporary files..."
                $ProgressBar.Value = 20
                
                # Clean
                Get-ChildItem -Path $gameDir -Include "bin", "obj" -Recurse -Directory | Remove-Item -Recurse -Force -ErrorAction SilentlyContinue
                $ProgressBar.Value = 40
                
                Write-Log "Restoring packages..."
                Set-Location $gameDir
                $restoreResult = dotnet restore 2>&1
                if ($LASTEXITCODE -ne 0) { throw "Package restore failed: $restoreResult" }
                $ProgressBar.Value = 60
                
                Write-Log "Building without launcher..."
                $buildResult = dotnet build --configuration Debug 2>&1
                if ($LASTEXITCODE -ne 0) { throw "Build failed: $buildResult" }
                $ProgressBar.Value = 70
                
                Write-Log "Copying assets to debug directory..."
                $sourceAssets = Join-Path $gameDir "Defaults\Assets"
                $debugDir = Join-Path $gameDir "bin\Debug\net8.0-windows"
                if (Test-Path $sourceAssets) {
                    Copy-Item -Path "$sourceAssets\*" -Destination $debugDir -Recurse -Force
                }
                $ProgressBar.Value = 80
                
                Write-Log "Starting game without launcher..."
                Start-Process dotnet -ArgumentList "run", "--configuration", "Debug", "--" , "--game" -WorkingDirectory $gameDir
                $ProgressBar.Value = 100
                
                Write-Log "✅ Game started without launcher!"
            }
            
            "dev-direct" {
                Write-Log "Starting game directly..."
                $ProgressBar.Value = 30
                
                Set-Location $gameDir
                
                Write-Log "Copying assets to debug directory..."
                $sourceAssets = Join-Path $gameDir "Defaults\Assets"
                $debugDir = Join-Path $gameDir "bin\Debug\net8.0-windows"
                if (Test-Path $sourceAssets) {
                    Copy-Item -Path "$sourceAssets\*" -Destination $debugDir -Recurse -Force
                }
                $ProgressBar.Value = 60
                
                Start-Process dotnet -ArgumentList "run", "--configuration", "Debug" -WorkingDirectory $gameDir
                $ProgressBar.Value = 100
                
                Write-Log "✅ Game started directly!"
            }
            
            "release" {
                Write-Log "Building release version..."
                $ProgressBar.Value = 20
                
                # Clean
                Get-ChildItem -Path $gameDir -Include "bin", "obj" -Recurse -Directory | Remove-Item -Recurse -Force -ErrorAction SilentlyContinue
                $ProgressBar.Value = 40
                
                Write-Log "Restoring packages..."
                Set-Location $gameDir
                $restoreResult = dotnet restore 2>&1
                if ($LASTEXITCODE -ne 0) { throw "Package restore failed: $restoreResult" }
                $ProgressBar.Value = 60
                
                Write-Log "Building release with updated back buttons..."
                $buildResult = dotnet build --configuration Release 2>&1
                if ($LASTEXITCODE -ne 0) { throw "Build failed: $buildResult" }
                $ProgressBar.Value = 80
                
                Write-Log "Copying assets to release directory..."
                $sourceAssets = Join-Path $gameDir "Defaults\Assets"
                $releaseDir = Join-Path $gameDir "bin\Release\net8.0-windows"
                if (Test-Path $sourceAssets) {
                    Copy-Item -Path "$sourceAssets\*" -Destination $releaseDir -Recurse -Force
                }
                $ProgressBar.Value = 100
                
                Write-Log "✅ Release build completed with standardized back buttons!"
            }
            
            "clean" {
                Write-Log "Cleaning all temporary files..."
                $ProgressBar.Value = 50
                
                Get-ChildItem -Path $gameDir -Include "bin", "obj" -Recurse -Directory | Remove-Item -Recurse -Force -ErrorAction SilentlyContinue
                
                # Clean builder directories
                if (Test-Path ".builder\staging") { Remove-Item ".builder\staging" -Recurse -Force }
                if (Test-Path ".builder\releases") { Remove-Item ".builder\releases" -Recurse -Force }
                
                $ProgressBar.Value = 100
                Write-Log "✅ Clean completed!"
            }
        }
        
    } catch {
        $ProgressBar.Value = 0
        Write-Log "❌ ERROR: $($_.Exception.Message)" "ERROR"
        Write-Log "Stack trace: $($_.ScriptStackTrace)" "ERROR"
    }
}

# Buttons
$DevButton = New-Object System.Windows.Forms.Button
$DevButton.Text = "DEV BUILD (WITH LAUNCHER)"
$DevButton.Location = New-Object System.Drawing.Point(15, 15)
$DevButton.Size = New-Object System.Drawing.Size(180, 35)
$DevButton.BackColor = [System.Drawing.Color]::FromArgb(0, 120, 0)
$DevButton.ForeColor = [System.Drawing.Color]::White
$DevButton.FlatStyle = "Flat"
$DevButton.FlatAppearance.BorderSize = 0
$DevButton.Font = New-Object System.Drawing.Font("Segoe UI", 10, [System.Drawing.FontStyle]::Bold)
$DevButton.Add_Click({ Run-BuildTask "dev" "Development Build & Run" })
$ButtonPanel.Controls.Add($DevButton)

$DevNoLauncherButton = New-Object System.Windows.Forms.Button
$DevNoLauncherButton.Text = "DEV BUILD (NO LAUNCHER)"
$DevNoLauncherButton.Location = New-Object System.Drawing.Point(205, 15)
$DevNoLauncherButton.Size = New-Object System.Drawing.Size(180, 35)
$DevNoLauncherButton.BackColor = [System.Drawing.Color]::FromArgb(0, 100, 100)
$DevNoLauncherButton.ForeColor = [System.Drawing.Color]::White
$DevNoLauncherButton.FlatStyle = "Flat"
$DevNoLauncherButton.FlatAppearance.BorderSize = 0
$DevNoLauncherButton.Font = New-Object System.Drawing.Font("Segoe UI", 10, [System.Drawing.FontStyle]::Bold)
$DevNoLauncherButton.Add_Click({ Run-BuildTask "dev-no-launcher" "Dev Build No Launcher" })
$ButtonPanel.Controls.Add($DevNoLauncherButton)

$ReleaseButton = New-Object System.Windows.Forms.Button
$ReleaseButton.Text = "RELEASE BUILD"
$ReleaseButton.Location = New-Object System.Drawing.Point(395, 15)
$ReleaseButton.Size = New-Object System.Drawing.Size(140, 35)
$ReleaseButton.BackColor = [System.Drawing.Color]::FromArgb(120, 80, 0)
$ReleaseButton.ForeColor = [System.Drawing.Color]::White
$ReleaseButton.FlatStyle = "Flat"
$ReleaseButton.FlatAppearance.BorderSize = 0
$ReleaseButton.Font = New-Object System.Drawing.Font("Segoe UI", 10, [System.Drawing.FontStyle]::Bold)
$ReleaseButton.Add_Click({ Run-BuildTask "release" "Release Build" })
$ButtonPanel.Controls.Add($ReleaseButton)

$CleanButton = New-Object System.Windows.Forms.Button
$CleanButton.Text = "CLEAN"
$CleanButton.Location = New-Object System.Drawing.Point(545, 15)
$CleanButton.Size = New-Object System.Drawing.Size(80, 35)
$CleanButton.BackColor = [System.Drawing.Color]::FromArgb(120, 0, 0)
$CleanButton.ForeColor = [System.Drawing.Color]::White
$CleanButton.FlatStyle = "Flat"
$CleanButton.FlatAppearance.BorderSize = 0
$CleanButton.Font = New-Object System.Drawing.Font("Segoe UI", 10, [System.Drawing.FontStyle]::Bold)
$CleanButton.Add_Click({ Run-BuildTask "clean" "Clean All" })
$ButtonPanel.Controls.Add($CleanButton)

$BuildOnlyButton = New-Object System.Windows.Forms.Button
$BuildOnlyButton.Text = "BUILD ONLY"
$BuildOnlyButton.Location = New-Object System.Drawing.Point(15, 60)
$BuildOnlyButton.Size = New-Object System.Drawing.Size(120, 30)
$BuildOnlyButton.BackColor = [System.Drawing.Color]::FromArgb(60, 60, 60)
$BuildOnlyButton.ForeColor = [System.Drawing.Color]::White
$BuildOnlyButton.FlatStyle = "Flat"
$BuildOnlyButton.FlatAppearance.BorderSize = 0
$BuildOnlyButton.Font = New-Object System.Drawing.Font("Segoe UI", 9)
$BuildOnlyButton.Add_Click({ Run-BuildTask "build-only" "Build Only" })
$ButtonPanel.Controls.Add($BuildOnlyButton)

$LogCopyButton = New-Object System.Windows.Forms.Button
$LogCopyButton.Text = "COPY LOG"
$LogCopyButton.Location = New-Object System.Drawing.Point(145, 60)
$LogCopyButton.Size = New-Object System.Drawing.Size(100, 30)
$LogCopyButton.BackColor = [System.Drawing.Color]::FromArgb(80, 80, 80)
$LogCopyButton.ForeColor = [System.Drawing.Color]::White
$LogCopyButton.FlatStyle = "Flat"
$LogCopyButton.FlatAppearance.BorderSize = 0
$LogCopyButton.Font = New-Object System.Drawing.Font("Segoe UI", 9)
$LogCopyButton.Add_Click({ 
    $OutputBox.SelectAll()
    $OutputBox.Copy()
    Write-Log "Log copied to clipboard!"
})
$ButtonPanel.Controls.Add($LogCopyButton)

$OpenFolderButton = New-Object System.Windows.Forms.Button
$OpenFolderButton.Text = "OPEN FOLDER"
$OpenFolderButton.Location = New-Object System.Drawing.Point(255, 60)
$OpenFolderButton.Size = New-Object System.Drawing.Size(120, 30)
$OpenFolderButton.BackColor = [System.Drawing.Color]::FromArgb(40, 80, 120)
$OpenFolderButton.ForeColor = [System.Drawing.Color]::White
$OpenFolderButton.FlatStyle = "Flat"
$OpenFolderButton.FlatAppearance.BorderSize = 0
$OpenFolderButton.Font = New-Object System.Drawing.Font("Segoe UI", 9)
$OpenFolderButton.Add_Click({ 
    $gameFolder = Join-Path $ScriptRoot "LatticeVeilMonoGame"
    explorer $gameFolder
    Write-Log "Opened project folder: $gameFolder"
})
$ButtonPanel.Controls.Add($OpenFolderButton)

# Show the form
[void]$Form.ShowDialog()

# Cleanup
$Form.Dispose()
