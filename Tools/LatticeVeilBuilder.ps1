Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing

# --- Configuration ---
$SolutionRoot = "C:\Users\Redacted\Documents\LatticeVeil_project"
$IconPath = "$SolutionRoot\LatticeVeilMonoGame\icon.ico"
$BuildCmd = "$SolutionRoot\build.cmd"

# --- Colors ---
$ColorBackground = [System.Drawing.Color]::FromArgb(45, 45, 48)
$ColorButton = [System.Drawing.Color]::FromArgb(0, 120, 215)
$ColorText = [System.Drawing.Color]::FromArgb(255, 255, 255)
$ColorAccent = [System.Drawing.Color]::FromArgb(0, 255, 127)

# --- Global Variables ---
$ProgressBar = $null
$lblStatus = $null

function Write-Log($message) {
    $lblStatus.Text = $message
    $form.Refresh()
}

function Set-Progress($percent, $status) {
    $ProgressBar.Value = $percent
    Write-Log $status
}

function Set-ButtonsEnabled($dev, $release) {
    $btnDev.Enabled = $dev
    $btnRelease.Enabled = $release
}

function Invoke-DevBuild {
    Set-ButtonsEnabled $false $false
    Write-Log "Starting Development Build..."
    
    Set-Progress 30 "Running DEV build..."
    
    $process = New-Object System.Diagnostics.Process
    $process.StartInfo.FileName = $BuildCmd
    $process.StartInfo.Arguments = "dev"
    $process.StartInfo.UseShellExecute = $false
    $process.StartInfo.RedirectStandardOutput = $true
    $process.StartInfo.RedirectStandardError = $true
    $process.StartInfo.CreateNoWindow = $true
    
    $process.Start() | Out-Null
    
    while (-not $process.HasExited) {
        $output = $process.StandardOutput.ReadLine()
        if ($output) {
            Write-Log $output
        }
        Start-Sleep -Milliseconds 100
    }
    
    $errorOutput = $process.StandardError.ReadToEnd()
    if ($errorOutput) {
        Write-Log "ERROR: $errorOutput"
    }
    
    Set-Progress 100 "Development build complete."
    Set-ButtonsEnabled $true $true
}

function Invoke-ReleaseBuild {
    Set-ButtonsEnabled $false $false
    Write-Log "Starting Release Build..."
    
    Set-Progress 30 "Running RELEASE build..."
    
    $process = New-Object System.Diagnostics.Process
    $process.StartInfo.FileName = $BuildCmd
    $process.StartInfo.Arguments = "release"
    $process.StartInfo.UseShellExecute = $false
    $process.StartInfo.RedirectStandardOutput = $true
    $process.StartInfo.RedirectStandardError = $true
    $process.StartInfo.CreateNoWindow = $true
    
    $process.Start() | Out-Null
    
    while (-not $process.HasExited) {
        $output = $process.StandardOutput.ReadLine()
        if ($output) {
            Write-Log $output
        }
        Start-Sleep -Milliseconds 100
    }
    
    $errorOutput = $process.StandardError.ReadToEnd()
    if ($errorOutput) {
        Write-Log "ERROR: $errorOutput"
    }
    
    Set-Progress 100 "Release build complete."
    Set-ButtonsEnabled $true $true
}


# --- UI Setup ---
$form = New-Object System.Windows.Forms.Form
$form.Text = "LatticeVeil Builder"
$form.Size = New-Object System.Drawing.Size(550, 500)
$form.StartPosition = "CenterScreen"
$form.FormBorderStyle = "FixedSingle"
$form.BackColor = $ColorBackground
if (Test-Path $IconPath) { $form.Icon = New-Object System.Drawing.Icon($IconPath) }

# Title
$lblTitle = New-Object System.Windows.Forms.Label
$lblTitle.Text = "LATTICEVEIL BUILDER"
$lblTitle.Font = New-Object System.Drawing.Font("Consolas", 20, [System.Drawing.FontStyle]::Bold)
$lblTitle.ForeColor = $ColorAccent
$lblTitle.AutoSize = $true
$lblTitle.Location = New-Object System.Drawing.Point(20, 20)
$form.Controls.Add($lblTitle)

$lblSub = New-Object System.Windows.Forms.Label
$lblSub.Text = "DEV/RELEASE Build System"
$lblSub.Font = New-Object System.Drawing.Font("Segoe UI", 10)
$lblSub.ForeColor = $ColorText
$lblSub.AutoSize = $true
$lblSub.Location = New-Object System.Drawing.Point(20, 55)
$form.Controls.Add($lblSub)

# Progress Bar
$ProgressBar = New-Object System.Windows.Forms.ProgressBar
$ProgressBar.Location = New-Object System.Drawing.Point(20, 90)
$ProgressBar.Size = New-Object System.Drawing.Size(490, 20)
$ProgressBar.Style = "Continuous"
$form.Controls.Add($ProgressBar)

# Status Label
$lblStatus = New-Object System.Windows.Forms.Label
$lblStatus.Text = "Ready to build..."
$lblStatus.Font = New-Object System.Drawing.Font("Segoe UI", 9)
$lblStatus.ForeColor = $ColorText
$lblStatus.AutoSize = $true
$lblStatus.Location = New-Object System.Drawing.Point(20, 115)
$form.Controls.Add($lblStatus)

# Buttons
$btnDev = New-Object System.Windows.Forms.Button
$btnDev.Text = "DEV (Local)"
$btnDev.Location = New-Object System.Drawing.Point(20, 160)
$btnDev.Size = New-Object System.Drawing.Size(150, 50)
$btnDev.FlatStyle = "Flat"
$btnDev.BackColor = $ColorButton
$btnDev.ForeColor = $ColorText
$btnDev.Font = New-Object System.Drawing.Font("Segoe UI", 9)
$btnDev.Add_Click({ Invoke-DevBuild })
$form.Controls.Add($btnDev)

$btnRelease = New-Object System.Windows.Forms.Button
$btnRelease.Text = "RELEASE (Docs)"
$btnRelease.Location = New-Object System.Drawing.Point(180, 160)
$btnRelease.Size = New-Object System.Drawing.Size(150, 50)
$btnRelease.FlatStyle = "Flat"
$btnRelease.BackColor = $ColorButton
$btnRelease.ForeColor = $ColorText
$btnRelease.Font = New-Object System.Drawing.Font("Segoe UI", 9)
$btnRelease.Add_Click({ Invoke-ReleaseBuild })
$form.Controls.Add($btnRelease)

# Show form
[void][System.Windows.Forms.Application]::Run($form)
