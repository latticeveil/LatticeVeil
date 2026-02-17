param(
    [switch]$NoUi,
    [string]$GateUrl = "",
    [string]$AdminToken = "",
    [string]$ExePath = "",
    [ValidateSet("latest", "release", "debug")]
    [string]$BuildType = "latest",
    [ValidateSet("auto", "dev", "release")]
    [string]$Target = "auto"
)

# Enhanced hash updater GUI for Render gate runtime allowlist.
# Security model:
# - Admin token is saved to config file for persistence.
# - Target is saved to config file for persistence.
# - The update call works with or without a valid GATE_ADMIN_TOKEN.

Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing

[System.Windows.Forms.Application]::EnableVisualStyles()
$ErrorActionPreference = "Stop"
$defaultGateUrl = "https://eos-service.onrender.com"

function Resolve-RepoRoot {
    $current = [System.IO.DirectoryInfo]$PSScriptRoot
    while ($null -ne $current) {
        $projectPath = Join-Path $current.FullName "LatticeVeilMonoGame\LatticeVeilMonoGame.csproj"
        if (Test-Path -LiteralPath $projectPath) {
            return $current.FullName
        }
        $current = $current.Parent
    }
    return $null
}

function Get-GameDir {
    param([string]$RepoRoot)
    return Join-Path $RepoRoot "LatticeVeilMonoGame"
}

function Get-DevDropDir {
    param([string]$RepoRoot)
    return Join-Path $RepoRoot "DEV"
}

function Get-ReleaseDropDir {
    param([string]$RepoRoot)
    return Join-Path $RepoRoot "RELEASE"
}

function Get-PublishDir {
    param([string]$RepoRoot)
    return Join-Path (Get-GameDir -RepoRoot $RepoRoot) "bin\Release\net8.0-windows\win-x64\publish"
}

function Get-DefaultExePath {
    param([string]$RepoRoot, [string]$BuildType = "latest")
    
    $gameDir = Get-GameDir -RepoRoot $RepoRoot
    
    # Check RELEASE folder first
    $releaseExe = Join-Path $RepoRoot "RELEASE\LatticeVeilMonoGame.exe"
    if (Test-Path -LiteralPath $releaseExe) {
        return $releaseExe
    }
    
    # Check DEV folder
    $devExe = Join-Path $RepoRoot "DEV\LatticeVeilMonoGame.exe"
    if (Test-Path -LiteralPath $devExe) {
        return $devExe
    }
    
    # Check standard build folders
    $releaseBuildExe = Join-Path $gameDir "bin\Release\net8.0-windows\win-x64\LatticeVeilMonoGame.exe"
    if (Test-Path -LiteralPath $releaseBuildExe) {
        return $releaseBuildExe
    }
    
    $debugBuildExe = Join-Path $gameDir "bin\Debug\net8.0-windows\win-x64\LatticeVeilMonoGame.exe"
    if (Test-Path -LiteralPath $debugBuildExe) {
        return $debugBuildExe
    }
    
    return ""
}

function Load-Config {
    param([string]$ConfigPath)
    if ([string]::IsNullOrWhiteSpace($ConfigPath) -or -not (Test-Path -LiteralPath $ConfigPath)) {
        return @{}
    }
    
    try {
        $configContent = Get-Content $ConfigPath -Raw
        if ([string]::IsNullOrWhiteSpace($configContent)) {
            return @{}
        }
        # Parse JSON manually to avoid cmdlet issues
        $configObject = @{
            target = ""
            adminToken = ""
            gateUrl = ""
        }
        $configLines = $configContent -split "`n"
        foreach ($line in $configLines) {
            if ($line -match '^\s*"target"\s*:\s*"(.+)"') {
                $configObject.target = $matches[1].Trim()
            }
            elseif ($line -match '^\s*"adminToken"\s*:\s*"(.+)"') {
                $configObject.adminToken = $matches[1].Trim()
            }
            elseif ($line -match '^\s*"gateUrl"\s*:\s*"(.+)"') {
                $configObject.gateUrl = $matches[1].Trim()
            }
        }
        return $configObject
    }
    catch {
        return @{}
    }
}

function Save-Config {
    param(
        [string]$ConfigPath,
        [object]$ConfigData
    )
    try {
        $json = $ConfigData | ConvertTo-Json -Depth 10
        Set-Content $ConfigPath -Value $json -Encoding UTF8
        if ($output) {
            Write-OutputLine -OutputBox $output -Text "Configuration saved to: $ConfigPath"
        }
    }
    catch {
        if ($output) {
            Write-OutputLine -OutputBox $output -Text "Failed to save configuration: $_"
        }
    }
}

function Save-CurrentConfig {
    param(
        [string]$GateUrl,
        [string]$AdminToken,
        [string]$Target
    )
    $configUpdate = @{
        target = $Target
        adminToken = $AdminToken
        gateUrl = $GateUrl
    }
    Save-Config $script:ConfigPath $configUpdate
}

function Write-OutputLine {
    param(
        [System.Windows.Forms.TextBox]$OutputBox,
        [string]$Text
    )
    if ($OutputBox.TextLength -gt 0) {
        $OutputBox.AppendText([Environment]::NewLine)
    }
    $OutputBox.AppendText($Text)
    $OutputBox.SelectionStart = $OutputBox.TextLength
    $OutputBox.ScrollToCaret()
}

function Get-ExePathForTarget {
    param(
        [string]$RepoRoot,
        [string]$Target
    )
    
    if ($Target -eq "dev") {
        # Always return DEV path, even if file doesn't exist
        $devExe = Join-Path $RepoRoot "DEV\LatticeVeilMonoGame.exe"
        return $devExe
    }
    
    if ($Target -eq "release") {
        # Always return RELEASE path, even if file doesn't exist
        $releaseExe = Join-Path $RepoRoot "RELEASE\LatticeVeilMonoGame.exe"
        return $releaseExe
    }
    
    return ""
}

function Resolve-HashTarget {
    param(
        [string]$ResolvedExePath,
        [string]$TargetOverride = "auto"
    )
    $normalizedOverride = ([string]$TargetOverride).Trim().ToLowerInvariant()
    if (-not [string]::IsNullOrWhiteSpace($normalizedOverride) -and $normalizedOverride -ne "auto") {
        return $normalizedOverride
    }
    
    $normalizedPath = ([string]$ResolvedExePath).ToLowerInvariant()
    
    # Check for DEV folder
    if ($normalizedPath -match "[\\/]dev[\\/]") {
        return "dev"
    }
    
    # Check for RELEASE folder
    if ($normalizedPath -match "[\\/]release[\\/]") {
        return "release"
    }
    
    # Check for build folders
    if ($normalizedPath -match "[\\/](debug|dev)[\\/]") {
        return "dev"
    }
    
    if ($normalizedPath -match "[\\/](release|publish)[\\/]") {
        return "release"
    }
    
    return "release"
}

function Invoke-LiveHashReplace {
    param(
        [string]$GateUrl,
        [string]$AdminToken,
        [string]$ExePath,
        [string]$TargetOverride = "auto"
    )
    if ([string]::IsNullOrWhiteSpace($GateUrl)) {
        throw "Gate URL is empty."
    }
    
    if ([string]::IsNullOrWhiteSpace($AdminToken)) {
        throw "Admin token is required."
    }
    
    if ([string]::IsNullOrWhiteSpace($ExePath)) {
        throw "EXE path is required."
    }
    
    $resolvedExe = [System.IO.Path]::GetFullPath($ExePath)
    if (-not (Test-Path -LiteralPath $resolvedExe)) {
        throw "EXE path does not exist: $resolvedExe"
    }
    
    $hash = (Get-FileHash -LiteralPath $resolvedExe -Algorithm SHA256).Hash.ToLowerInvariant()
    $target = Resolve-HashTarget -ResolvedExePath $resolvedExe -TargetOverride $TargetOverride
    $payload = @{
        hash = $hash
        target = $target
        replaceTargetList = $true
        clearOtherHashes = $false
        applyMode = "replace_source"
    } | ConvertTo-Json
    
    # Save to config file
    $configUpdate = @{
        target = $target
        adminToken = $AdminToken
        gateUrl = $GateUrl
    }
    Save-Config $ConfigPath $configUpdate
    
    $baseUrl = $GateUrl.Trim().TrimEnd("/")
    $url = "$baseUrl/admin/allowlist/runtime/current-hash"
    $headers = @{
        Authorization = "Bearer $AdminToken"
    }
    
    $result = $null
    try {
        $result = Invoke-RestMethod -Method Post -Uri $url -Headers $headers -ContentType "application/json" -Body $payload
    }
    catch {
        throw "Gate API call failed: $($_.Exception.Message). Check Gate URL and Admin Token."
    }
    
    if (-not $result.ok) {
        $reason = "unknown reason"
        if ($null -ne $result.reason -and -not [string]::IsNullOrWhiteSpace([string]$result.reason)) {
            $reason = [string]$result.reason
        }
        throw "Gate rejected hash update: $reason"
    }
    
    return [PSCustomObject]@{
        Hash = $hash
        Target = $target
        ExePath = $resolvedExe
        Message = [string]$result.message
        RuntimeHashCount = [string]$result.runtime.hashCount
        RuntimeApplyMode = [string]$result.runtime.applyMode
    }
}

$repoRoot = Resolve-RepoRoot
$detectedExe = Get-DefaultExePath -RepoRoot $repoRoot
$initialExe = ([string]$ExePath).Trim()
if ([string]::IsNullOrWhiteSpace($initialExe)) {
    $initialExe = $detectedExe
}

$initialGateUrl = ([string]$GateUrl).Trim()
if ([string]::IsNullOrWhiteSpace($initialGateUrl)) {
    $initialGateUrl = ([string]$env:LV_GATE_URL).Trim()
}
if ([string]::IsNullOrWhiteSpace($initialGateUrl)) {
    $initialGateUrl = $defaultGateUrl
}

$initialToken = ([string]$AdminToken).Trim()
if ([string]::IsNullOrWhiteSpace($initialToken)) {
    $initialToken = ([string]$env:GATE_ADMIN_TOKEN).Trim()
}

$initialTarget = ([string]$Target).Trim().ToLowerInvariant()
if ([string]::IsNullOrWhiteSpace($initialTarget)) {
    $initialTarget = "auto"
}

# Set config file path to Tools folder (protected by .gitignore to prevent commits)
$script:ConfigPath = Join-Path $PSScriptRoot "gate.cfg"

# Load existing config only if file exists
$existingConfig = @{}
if (Test-Path -LiteralPath $script:ConfigPath) {
    $existingConfig = Load-Config $script:ConfigPath
}

$loadedTarget = if ($existingConfig.ContainsKey("target") -and -not [string]::IsNullOrWhiteSpace($existingConfig.target)) { $existingConfig.target } else { $initialTarget }
$loadedToken = if ($existingConfig.ContainsKey("adminToken") -and -not [string]::IsNullOrWhiteSpace($existingConfig.adminToken)) { $existingConfig.adminToken } else { $initialToken }
$loadedGateUrl = if ($existingConfig.ContainsKey("gateUrl") -and -not [string]::IsNullOrWhiteSpace($existingConfig.gateUrl)) { $existingConfig.gateUrl } else { "EXAMPLE.onrender.com" }

if ($NoUi) {
    $result = Invoke-LiveHashReplace -GateUrl $loadedGateUrl -AdminToken $loadedToken -ExePath $initialExe -TargetOverride $loadedTarget
    Write-Output ("Build selection: " + $BuildType)
    Write-Output ("EXE: " + $result.ExePath)
    Write-Output ("Target: " + $result.Target)
    Write-Output ("SHA256: " + $result.Hash)
    Write-Output ("Result: " + $result.Message)
    Write-Output ("Runtime hash count: " + $result.RuntimeHashCount + " (mode=" + $result.RuntimeApplyMode + ")")
    exit 0
}

# Create GUI components
$form = New-Object System.Windows.Forms.Form
$form.Text = "LatticeVeil Hash Updater"
$form.StartPosition = "CenterScreen"
$form.Size = New-Object System.Drawing.Size(880, 470)
$form.MinimumSize = New-Object System.Drawing.Size(880, 470)
$form.BackColor = [System.Drawing.Color]::FromArgb(16, 16, 16)
$form.ForeColor = [System.Drawing.Color]::White

$title = New-Object System.Windows.Forms.Label
$title.Text = "Live Render Hash Replace"
$title.Font = New-Object System.Drawing.Font("Segoe UI", 16, [System.Drawing.FontStyle]::Bold)
$title.AutoSize = $true
$title.Location = New-Object System.Drawing.Point(18, 14)
$title.ForeColor = [System.Drawing.Color]::FromArgb(235, 235, 235)
$form.Controls.Add($title)

$subtitle = New-Object System.Windows.Forms.Label
$subtitle.Text = "Select DEV or RELEASE explicitly, or use AUTO from EXE path. Token is saved to config file."
$subtitle.Font = New-Object System.Drawing.Font("Segoe UI", 9)
$subtitle.AutoSize = $true
$subtitle.Location = New-Object System.Drawing.Point(20, 47)
$subtitle.ForeColor = [System.Drawing.Color]::FromArgb(170, 170, 170)
$form.Controls.Add($subtitle)

$lblGate = New-Object System.Windows.Forms.Label
$lblGate.Text = "Gate URL:"
$lblGate.AutoSize = $true
$lblGate.Location = New-Object System.Drawing.Point(20, 80)
$lblGate.ForeColor = [System.Drawing.Color]::FromArgb(200, 200, 200)
$form.Controls.Add($lblGate)

$txtGate = New-Object System.Windows.Forms.TextBox
$txtGate.Text = $loadedGateUrl
$txtGate.Location = New-Object System.Drawing.Point(108, 77)
$txtGate.Size = New-Object System.Drawing.Size(640, 24)
$txtGate.BorderStyle = "FixedSingle"
$txtGate.BackColor = [System.Drawing.Color]::FromArgb(24, 24, 24)
$txtGate.ForeColor = [System.Drawing.Color]::FromArgb(235, 235, 235)
$form.Controls.Add($txtGate)

$linkRender = New-Object System.Windows.Forms.LinkLabel
$linkRender.Text = "Open Render"
$linkRender.Location = New-Object System.Drawing.Point(756, 80)
$linkRender.Size = New-Object System.Drawing.Size(92, 20)
$linkRender.LinkColor = [System.Drawing.Color]::FromArgb(100, 149, 237)
$linkRender.ActiveLinkColor = [System.Drawing.Color]::FromArgb(135, 206, 250)
$linkRender.VisitedLinkColor = [System.Drawing.Color]::FromArgb(147, 112, 219)
$linkRender.Cursor = [System.Windows.Forms.Cursors]::Hand
$form.Controls.Add($linkRender)

$lblTarget = New-Object System.Windows.Forms.Label
$lblTarget.Text = "Target:"
$lblTarget.AutoSize = $true
$lblTarget.Location = New-Object System.Drawing.Point(20, 113)
$lblTarget.ForeColor = [System.Drawing.Color]::FromArgb(200, 200, 200)
$form.Controls.Add($lblTarget)

$cmbTarget = New-Object System.Windows.Forms.ComboBox
$cmbTarget.DropDownStyle = "DropDownList"
$cmbTarget.Location = New-Object System.Drawing.Point(108, 110)
$cmbTarget.Size = New-Object System.Drawing.Size(220, 24)
[void]$cmbTarget.Items.Add("AUTO (By EXE Path)")
[void]$cmbTarget.Items.Add("DEV")
[void]$cmbTarget.Items.Add("RELEASE")
switch ($loadedTarget) {
    "dev" { $cmbTarget.SelectedIndex = 1 }
    "release" { $cmbTarget.SelectedIndex = 2 }
    default { $cmbTarget.SelectedIndex = 0 }
}
$cmbTarget.BackColor = [System.Drawing.Color]::FromArgb(24, 24, 24)
$cmbTarget.ForeColor = [System.Drawing.Color]::FromArgb(235, 235, 235)
$form.Controls.Add($cmbTarget)

$lblExe = New-Object System.Windows.Forms.Label
$lblExe.Text = "EXE Path:"
$lblExe.AutoSize = $true
$lblExe.Location = New-Object System.Drawing.Point(20, 146)
$lblExe.ForeColor = [System.Drawing.Color]::FromArgb(200, 200, 200)
$form.Controls.Add($lblExe)

$txtExe = New-Object System.Windows.Forms.TextBox
$txtExe.Text = $initialExe
$txtExe.Location = New-Object System.Drawing.Point(108, 143)
$txtExe.Size = New-Object System.Drawing.Size(640, 24)
$txtExe.BorderStyle = "FixedSingle"
$txtExe.BackColor = [System.Drawing.Color]::FromArgb(24, 24, 24)
$txtExe.ForeColor = [System.Drawing.Color]::FromArgb(235, 235, 235)
$form.Controls.Add($txtExe)

$btnBrowse = New-Object System.Windows.Forms.Button
$btnBrowse.Text = "Browse"
$btnBrowse.Location = New-Object System.Drawing.Point(756, 141)
$btnBrowse.Size = New-Object System.Drawing.Size(92, 27)
$btnBrowse.FlatStyle = "Flat"
$btnBrowse.FlatAppearance.BorderColor = [System.Drawing.Color]::FromArgb(70, 70, 70)
$btnBrowse.FlatAppearance.BorderSize = 1
$btnBrowse.BackColor = [System.Drawing.Color]::FromArgb(34, 34, 34)
$btnBrowse.ForeColor = [System.Drawing.Color]::White
$form.Controls.Add($btnBrowse)

$lblToken = New-Object System.Windows.Forms.Label
$lblToken.Text = "Admin Token:"
$lblToken.AutoSize = $true
$lblToken.Location = New-Object System.Drawing.Point(20, 182)
$lblToken.ForeColor = [System.Drawing.Color]::FromArgb(200, 200, 200)
$form.Controls.Add($lblToken)

$txtToken = New-Object System.Windows.Forms.TextBox
$txtToken.Text = $loadedToken
$txtToken.UseSystemPasswordChar = $true
$txtToken.Location = New-Object System.Drawing.Point(108, 179)
$txtToken.Size = New-Object System.Drawing.Size(640, 24)
$txtToken.BorderStyle = "FixedSingle"
$txtToken.BackColor = [System.Drawing.Color]::FromArgb(24, 24, 24)
$txtToken.ForeColor = [System.Drawing.Color]::FromArgb(235, 235, 235)
$form.Controls.Add($txtToken)

$btnToggleToken = New-Object System.Windows.Forms.Button
$btnToggleToken.Text = "Show"
$btnToggleToken.Location = New-Object System.Drawing.Point(756, 177)
$btnToggleToken.Size = New-Object System.Drawing.Size(92, 27)
$btnToggleToken.FlatStyle = "Flat"
$btnToggleToken.FlatAppearance.BorderColor = [System.Drawing.Color]::FromArgb(70, 70, 70)
$btnToggleToken.FlatAppearance.BorderSize = 1
$btnToggleToken.BackColor = [System.Drawing.Color]::FromArgb(34, 34, 34)
$btnToggleToken.ForeColor = [System.Drawing.Color]::White
$form.Controls.Add($btnToggleToken)

$btnUpdate = New-Object System.Windows.Forms.Button
$btnUpdate.Text = "Replace Live Hash"
$btnUpdate.Location = New-Object System.Drawing.Point(20, 220)
$btnUpdate.Size = New-Object System.Drawing.Size(180, 34)
$btnUpdate.FlatStyle = "Flat"
$btnUpdate.FlatAppearance.BorderColor = [System.Drawing.Color]::FromArgb(80, 80, 80)
$btnUpdate.FlatAppearance.BorderSize = 1
$btnUpdate.BackColor = [System.Drawing.Color]::FromArgb(36, 72, 112)
$btnUpdate.ForeColor = [System.Drawing.Color]::White
$form.Controls.Add($btnUpdate)

$btnCopyHash = New-Object System.Windows.Forms.Button
$btnCopyHash.Text = "Copy Last Hash"
$btnCopyHash.Location = New-Object System.Drawing.Point(210, 220)
$btnCopyHash.Size = New-Object System.Drawing.Size(140, 34)
$btnCopyHash.FlatStyle = "Flat"
$btnCopyHash.FlatAppearance.BorderColor = [System.Drawing.Color]::FromArgb(80, 80, 80)
$btnCopyHash.FlatAppearance.BorderSize = 1
$btnCopyHash.BackColor = [System.Drawing.Color]::FromArgb(34, 34, 34)
$btnCopyHash.ForeColor = [System.Drawing.Color]::White
$btnCopyHash.Enabled = $false
$form.Controls.Add($btnCopyHash)

$btnOpenRender = New-Object System.Windows.Forms.Button
$btnOpenRender.Text = "Open Render URL"
$btnOpenRender.Location = New-Object System.Drawing.Point(360, 220)
$btnOpenRender.Size = New-Object System.Drawing.Size(140, 34)
$btnOpenRender.FlatStyle = "Flat"
$btnOpenRender.FlatAppearance.BorderColor = [System.Drawing.Color]::FromArgb(80, 80, 80)
$btnOpenRender.FlatAppearance.BorderSize = 1
$btnOpenRender.BackColor = [System.Drawing.Color]::FromArgb(34, 34, 34)
$btnOpenRender.ForeColor = [System.Drawing.Color]::White
$form.Controls.Add($btnOpenRender)

$output = New-Object System.Windows.Forms.TextBox
$output.Multiline = $true
$output.ReadOnly = $true
$output.ScrollBars = "Vertical"
$output.Location = New-Object System.Drawing.Point(20, 264)
$output.Size = New-Object System.Drawing.Size(828, 168)
$output.Font = New-Object System.Drawing.Font("Consolas", 9)
$output.BorderStyle = "FixedSingle"
$output.BackColor = [System.Drawing.Color]::FromArgb(20, 20, 20)
$output.ForeColor = [System.Drawing.Color]::FromArgb(220, 220, 220)
$form.Controls.Add($output)

$lastHash = ""

# Add event handlers after GUI components are created
$btnBrowse.Add_Click({
    $dlg = New-Object System.Windows.Forms.OpenFileDialog
    $dlg.Filter = "Executable (*.exe)|*.exe|All files (*.*)|*.*"
    $dlg.Title = "Select LatticeVeilMonoGame.exe"
    if ($dlg.ShowDialog() -eq [System.Windows.Forms.DialogResult]::OK) {
        $txtExe.Text = $dlg.FileName
    }
    $dlg.Dispose()
})

$linkRender.Add_Click({
    try {
        Start-Process "https://render.com" | Out-Null
    }
    catch {
        Write-OutputLine -OutputBox $output -Text ("Failed to open Render website: " + $_.Exception.Message)
    }
})

$btnToggleToken.Add_Click({
    $txtToken.UseSystemPasswordChar = -not $txtToken.UseSystemPasswordChar
    $btnToggleToken.Text = if ($txtToken.UseSystemPasswordChar) { "Show" } else { "Hide" }
})

$btnOpenRender.Add_Click({
    $gate = [string]$txtGate.Text
    $gate = $gate.Trim()
    if ([string]::IsNullOrWhiteSpace($gate)) {
        $gate = $defaultGateUrl
    }
    
    try {
        Start-Process $gate | Out-Null
    }
    catch {
        Write-OutputLine -OutputBox $output -Text ("Failed to open URL: " + $_.Exception.Message)
    }
})

$btnCopyHash.Add_Click({
    if ([string]::IsNullOrWhiteSpace($lastHash)) {
        return
    }
    
    try {
        Set-Clipboard -Value $lastHash
        Write-OutputLine -OutputBox $output -Text "Copied hash to clipboard."
    }
    catch {
        Write-OutputLine -OutputBox $output -Text ("Clipboard copy failed: " + $_.Exception.Message)
    }
})

$txtGate.Add_TextChanged({
    # Save config when gate URL changes
    $currentTarget = "auto"
    $selectedTargetLabel = [string]$cmbTarget.SelectedItem
    if ($selectedTargetLabel -eq "DEV") {
        $currentTarget = "dev"
    }
    elseif ($selectedTargetLabel -eq "RELEASE") {
        $currentTarget = "release"
    }
    
    Save-CurrentConfig -GateUrl $txtGate.Text -AdminToken $txtToken.Text -Target $currentTarget
})

$txtToken.Add_TextChanged({
    # Save config when admin token changes
    $currentTarget = "auto"
    $selectedTargetLabel = [string]$cmbTarget.SelectedItem
    if ($selectedTargetLabel -eq "DEV") {
        $currentTarget = "dev"
    }
    elseif ($selectedTargetLabel -eq "RELEASE") {
        $currentTarget = "release"
    }
    
    Save-CurrentConfig -GateUrl $txtGate.Text -AdminToken $txtToken.Text -Target $currentTarget
})

$cmbTarget.Add_SelectedIndexChanged({
    try {
        $selectedTargetLabel = [string]$cmbTarget.SelectedItem
        Write-OutputLine -OutputBox $output -Text "Target changed to: $selectedTargetLabel"
        
        $target = "auto"
        if ($selectedTargetLabel -eq "DEV") {
            $target = "dev"
        }
        elseif ($selectedTargetLabel -eq "RELEASE") {
            $target = "release"
        }
        
        if ($target -ne "auto") {
            $newExePath = Get-ExePathForTarget -RepoRoot $repoRoot -Target $target
            $txtExe.Text = $newExePath  # Always change the path
            Write-OutputLine -OutputBox $output -Text "Set EXE path to: $newExePath"
            
            # Save config when target changes
            Save-CurrentConfig -GateUrl $txtGate.Text -AdminToken $txtToken.Text -Target $target
            
            # Check if file exists and show popup if not
            if (-not (Test-Path -LiteralPath $newExePath)) {
                [System.Windows.Forms.MessageBox]::Show(
                    "EXE DOES NOT EXIST`n`nTarget: $target`nExpected Path: $newExePath`n`nPlease copy the EXE to this location.",
                    "EXE Not Found",
                    [System.Windows.Forms.MessageBoxButtons]::OK,
                    [System.Windows.Forms.MessageBoxIcon]::Warning
                ) | Out-Null
                Write-OutputLine -OutputBox $output -Text "WARNING: EXE does not exist at $newExePath"
            }
            else {
                Write-OutputLine -OutputBox $output -Text "EXE found and path updated successfully"
            }
        }
        else {
            # For AUTO, reset to default detection
            $defaultExe = Get-DefaultExePath -RepoRoot $repoRoot
            if (-not [string]::IsNullOrWhiteSpace($defaultExe)) {
                $txtExe.Text = $defaultExe
                Write-OutputLine -OutputBox $output -Text "Reset to auto-detected EXE: $defaultExe"
            }
            
            # Save config when target changes to AUTO
            Save-CurrentConfig -GateUrl $txtGate.Text -AdminToken $txtToken.Text -Target "auto"
        }
    }
    catch {
        Write-OutputLine -OutputBox $output -Text "Error switching target: $_"
    }
})

$btnUpdate.Add_Click({
    $btnUpdate.Enabled = $false
    try {
        $gate = [string]$txtGate.Text
        $gate = $gate.Trim()
        $token = [string]$txtToken.Text
        $token = $token.Trim()
        $exe = [string]$txtExe.Text
        $exe = $exe.Trim()
        $targetOverride = "auto"
        $selectedTargetLabel = [string]$cmbTarget.SelectedItem
        if ($selectedTargetLabel -eq "DEV") {
            $targetOverride = "dev"
        }
        elseif ($selectedTargetLabel -eq "RELEASE") {
            $targetOverride = "release"
        }
        
        Write-OutputLine -OutputBox $output -Text ("[" + (Get-Date -Format "HH:mm:ss") + "] Updating live hash (target=" + $targetOverride + ")...")
        
        $result = Invoke-LiveHashReplace -GateUrl $gate -AdminToken $token -ExePath $exe -TargetOverride $targetOverride
        $lastHash = $result.Hash
        $btnCopyHash.Enabled = $true
        
        Write-OutputLine -OutputBox $output -Text ("EXE: " + $result.ExePath)
        Write-OutputLine -OutputBox $output -Text ("Target: " + $result.Target)
        Write-OutputLine -OutputBox $output -Text ("SHA256: " + $result.Hash)
        Write-OutputLine -OutputBox $output -Text ("Result: " + $result.Message)
        Write-OutputLine -OutputBox $output -Text ("Runtime hash count: " + $result.RuntimeHashCount + " (mode=" + $result.RuntimeApplyMode + ")")
        
        try {
            Set-Clipboard -Value $result.Hash
            Write-OutputLine -OutputBox $output -Text "Hash copied to clipboard."
        }
        catch {
            Write-OutputLine -OutputBox $output -Text "Hash updated, but clipboard copy failed."
        }
    }
    catch {
        Write-OutputLine -OutputBox $output -Text ("ERROR: " + $_.Exception.Message)
    }
    finally {
        $btnUpdate.Enabled = $true
    }
})

Write-OutputLine -OutputBox $output -Text "Ready. Configuration is saved to: $script:ConfigPath"
Write-OutputLine -OutputBox $output -Text ("Build selection: " + $BuildType + " (same resolution rules as Build GUI).")
Write-OutputLine -OutputBox $output -Text "Update mode: target=auto|dev|release, replaceTargetList=true, clearOtherHashes=false."

[void]$form.ShowDialog()
$form.Dispose()
