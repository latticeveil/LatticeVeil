# LatticeVeil Build GUI (single-file)
# Double-click this script to build/run from one place.

Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing

[System.Windows.Forms.Application]::EnableVisualStyles()

$script:RepoRoot = $null
$script:GameDir = $null
$script:GameProject = $null
$script:LogDir = $null
$script:BuildsDir = $null
$script:DevDropDir = $null
$script:ReleaseDropDir = $null
$script:LogFile = $null
$script:IsBusy = $false

function Test-ProjectRoot {
    param([string]$RootPath)

    if ([string]::IsNullOrWhiteSpace($RootPath)) {
        return $false
    }

    $candidate = Join-Path $RootPath "LatticeVeilMonoGame\LatticeVeilMonoGame.csproj"
    return (Test-Path -LiteralPath $candidate)
}

function Find-ProjectRootFrom {
    param([string]$StartPath)

    if ([string]::IsNullOrWhiteSpace($StartPath)) {
        return $null
    }

    try {
        $dir = [System.IO.DirectoryInfo]([System.IO.Path]::GetFullPath($StartPath))
    }
    catch {
        return $null
    }

    while ($null -ne $dir) {
        if (Test-ProjectRoot $dir.FullName) {
            return $dir.FullName
        }
        $dir = $dir.Parent
    }

    return $null
}

function Resolve-InitialProjectRoot {
    $starts = New-Object System.Collections.Generic.List[string]
    $starts.Add($PSScriptRoot)
    $starts.Add([System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot "..")))
    try {
        $starts.Add((Get-Location).Path)
    }
    catch { }

    foreach ($start in ($starts | Select-Object -Unique)) {
        $found = Find-ProjectRootFrom $start
        if (-not [string]::IsNullOrWhiteSpace($found)) {
            return $found
        }
    }

    return $null
}

function Ensure-WorkingFolders {
    foreach ($path in @($script:LogDir, $script:BuildsDir, $script:DevDropDir, $script:ReleaseDropDir)) {
        if ([string]::IsNullOrWhiteSpace($path)) {
            continue
        }

        if (-not (Test-Path -LiteralPath $path)) {
            New-Item -ItemType Directory -Path $path -Force | Out-Null
        }
    }
}

function Set-ProjectRoot {
    param(
        [string]$RootPath,
        [switch]$Silent
    )

    if ([string]::IsNullOrWhiteSpace($RootPath)) {
        return $false
    }

    $resolved = [System.IO.Path]::GetFullPath($RootPath)
    if (-not (Test-ProjectRoot $resolved)) {
        return $false
    }

    $script:RepoRoot = $resolved
    $script:GameDir = Join-Path $script:RepoRoot "LatticeVeilMonoGame"
    $script:GameProject = Join-Path $script:GameDir "LatticeVeilMonoGame.csproj"
    $script:LogDir = Join-Path $script:RepoRoot ".builder\logs"
    $script:BuildsDir = Join-Path $script:RepoRoot "Builds"
    $script:DevDropDir = Join-Path $script:RepoRoot "DEV"
    $script:ReleaseDropDir = Join-Path $script:RepoRoot "RELEASE"
    $script:LogFile = Join-Path $script:LogDir ("build-gui-{0}.log" -f (Get-Date -Format "yyyyMMdd"))
    Ensure-WorkingFolders

    if (Get-Variable -Name txtProjectRoot -Scope Script -ErrorAction SilentlyContinue) {
        $script:txtProjectRoot.Text = $script:RepoRoot
    }

    return $true
}

$initialRoot = Resolve-InitialProjectRoot
if ([string]::IsNullOrWhiteSpace($initialRoot)) {
    $initialRoot = [System.IO.Path]::GetFullPath((Get-Location).Path)
}
Set-ProjectRoot -RootPath $initialRoot -Silent | Out-Null

$form = New-Object System.Windows.Forms.Form
$form.Text = "LatticeVeil Build GUI"
$form.StartPosition = "CenterScreen"
$form.Size = New-Object System.Drawing.Size(980, 660)
$form.MinimumSize = New-Object System.Drawing.Size(980, 660)
$form.BackColor = [System.Drawing.Color]::FromArgb(12, 12, 12)
$form.ForeColor = [System.Drawing.Color]::White

$header = New-Object System.Windows.Forms.Panel
$header.Dock = "Top"
$header.Height = 78
$header.BackColor = [System.Drawing.Color]::FromArgb(18, 18, 18)
$form.Controls.Add($header)

$title = New-Object System.Windows.Forms.Label
$title.Text = "LatticeVeil Build Launcher"
$title.Font = New-Object System.Drawing.Font("Segoe UI", 19, [System.Drawing.FontStyle]::Bold)
$title.ForeColor = [System.Drawing.Color]::FromArgb(232, 232, 232)
$title.AutoSize = $true
$title.Location = New-Object System.Drawing.Point(20, 16)
$header.Controls.Add($title)

$subtitle = New-Object System.Windows.Forms.Label
$subtitle.Text = "Single-file build tool: test, publish, package, and hash"
$subtitle.Font = New-Object System.Drawing.Font("Segoe UI", 9)
$subtitle.ForeColor = [System.Drawing.Color]::FromArgb(145, 145, 145)
$subtitle.AutoSize = $true
$subtitle.Location = New-Object System.Drawing.Point(22, 50)
$header.Controls.Add($subtitle)

$status = New-Object System.Windows.Forms.Label
$status.Text = "Ready"
$status.Font = New-Object System.Drawing.Font("Segoe UI", 10, [System.Drawing.FontStyle]::Bold)
$status.ForeColor = [System.Drawing.Color]::FromArgb(175, 175, 175)
$status.AutoSize = $true
$status.Location = New-Object System.Drawing.Point(640, 28)
$header.Controls.Add($status)

$progress = New-Object System.Windows.Forms.ProgressBar
$progress.Location = New-Object System.Drawing.Point(20, 88)
$progress.Size = New-Object System.Drawing.Size(938, 18)
$progress.Style = "Continuous"
$progress.Minimum = 0
$progress.Maximum = 100
$form.Controls.Add($progress)

$projectRootLabel = New-Object System.Windows.Forms.Label
$projectRootLabel.Text = "Project Root"
$projectRootLabel.AutoSize = $true
$projectRootLabel.ForeColor = [System.Drawing.Color]::FromArgb(170, 170, 170)
$projectRootLabel.Font = New-Object System.Drawing.Font("Segoe UI", 9, [System.Drawing.FontStyle]::Bold)
$projectRootLabel.Location = New-Object System.Drawing.Point(20, 113)
$form.Controls.Add($projectRootLabel)

$script:txtProjectRoot = New-Object System.Windows.Forms.TextBox
$script:txtProjectRoot.Text = if ($script:RepoRoot) { $script:RepoRoot } else { "" }
$script:txtProjectRoot.Font = New-Object System.Drawing.Font("Segoe UI", 9)
$script:txtProjectRoot.BackColor = [System.Drawing.Color]::FromArgb(22, 22, 22)
$script:txtProjectRoot.ForeColor = [System.Drawing.Color]::FromArgb(230, 230, 230)
$script:txtProjectRoot.BorderStyle = "FixedSingle"
$script:txtProjectRoot.Location = New-Object System.Drawing.Point(110, 110)
$script:txtProjectRoot.Size = New-Object System.Drawing.Size(670, 24)
$form.Controls.Add($script:txtProjectRoot)

$btnBrowseProject = New-Object System.Windows.Forms.Button
$btnBrowseProject.Text = "BROWSE"
$btnBrowseProject.Size = New-Object System.Drawing.Size(86, 24)
$btnBrowseProject.Location = New-Object System.Drawing.Point(788, 109)
$btnBrowseProject.BackColor = [System.Drawing.Color]::FromArgb(30, 30, 30)
$btnBrowseProject.ForeColor = [System.Drawing.Color]::FromArgb(220, 220, 220)
$btnBrowseProject.FlatStyle = "Flat"
$btnBrowseProject.FlatAppearance.BorderColor = [System.Drawing.Color]::FromArgb(70, 70, 70)
$btnBrowseProject.FlatAppearance.BorderSize = 1
$form.Controls.Add($btnBrowseProject)

$btnApplyProject = New-Object System.Windows.Forms.Button
$btnApplyProject.Text = "APPLY"
$btnApplyProject.Size = New-Object System.Drawing.Size(86, 24)
$btnApplyProject.Location = New-Object System.Drawing.Point(880, 109)
$btnApplyProject.BackColor = [System.Drawing.Color]::FromArgb(36, 72, 112)
$btnApplyProject.ForeColor = [System.Drawing.Color]::White
$btnApplyProject.FlatStyle = "Flat"
$btnApplyProject.FlatAppearance.BorderColor = [System.Drawing.Color]::FromArgb(70, 70, 70)
$btnApplyProject.FlatAppearance.BorderSize = 1
$form.Controls.Add($btnApplyProject)

$hashDevLabel = New-Object System.Windows.Forms.Label
$hashDevLabel.Text = "DEV SHA256"
$hashDevLabel.AutoSize = $true
$hashDevLabel.ForeColor = [System.Drawing.Color]::FromArgb(170, 170, 170)
$hashDevLabel.Font = New-Object System.Drawing.Font("Segoe UI", 8.5, [System.Drawing.FontStyle]::Bold)
$hashDevLabel.Location = New-Object System.Drawing.Point(20, 139)
$form.Controls.Add($hashDevLabel)

$script:txtDevHash = New-Object System.Windows.Forms.TextBox
$script:txtDevHash.ReadOnly = $true
$script:txtDevHash.Font = New-Object System.Drawing.Font("Consolas", 8.5)
$script:txtDevHash.BackColor = [System.Drawing.Color]::FromArgb(22, 22, 22)
$script:txtDevHash.ForeColor = [System.Drawing.Color]::FromArgb(210, 210, 210)
$script:txtDevHash.BorderStyle = "FixedSingle"
$script:txtDevHash.Location = New-Object System.Drawing.Point(112, 136)
$script:txtDevHash.Size = New-Object System.Drawing.Size(322, 23)
$form.Controls.Add($script:txtDevHash)

$btnCopyDevHash = New-Object System.Windows.Forms.Button
$btnCopyDevHash.Text = "COPY"
$btnCopyDevHash.Size = New-Object System.Drawing.Size(56, 23)
$btnCopyDevHash.Location = New-Object System.Drawing.Point(438, 136)
$btnCopyDevHash.BackColor = [System.Drawing.Color]::FromArgb(30, 30, 30)
$btnCopyDevHash.ForeColor = [System.Drawing.Color]::FromArgb(220, 220, 220)
$btnCopyDevHash.FlatStyle = "Flat"
$btnCopyDevHash.FlatAppearance.BorderColor = [System.Drawing.Color]::FromArgb(70, 70, 70)
$btnCopyDevHash.FlatAppearance.BorderSize = 1
$form.Controls.Add($btnCopyDevHash)

$hashReleaseLabel = New-Object System.Windows.Forms.Label
$hashReleaseLabel.Text = "RELEASE SHA256"
$hashReleaseLabel.AutoSize = $true
$hashReleaseLabel.ForeColor = [System.Drawing.Color]::FromArgb(170, 170, 170)
$hashReleaseLabel.Font = New-Object System.Drawing.Font("Segoe UI", 8.5, [System.Drawing.FontStyle]::Bold)
$hashReleaseLabel.Location = New-Object System.Drawing.Point(504, 139)
$form.Controls.Add($hashReleaseLabel)

$script:txtReleaseHash = New-Object System.Windows.Forms.TextBox
$script:txtReleaseHash.ReadOnly = $true
$script:txtReleaseHash.Font = New-Object System.Drawing.Font("Consolas", 8.5)
$script:txtReleaseHash.BackColor = [System.Drawing.Color]::FromArgb(22, 22, 22)
$script:txtReleaseHash.ForeColor = [System.Drawing.Color]::FromArgb(210, 210, 210)
$script:txtReleaseHash.BorderStyle = "FixedSingle"
$script:txtReleaseHash.Location = New-Object System.Drawing.Point(614, 136)
$script:txtReleaseHash.Size = New-Object System.Drawing.Size(278, 23)
$form.Controls.Add($script:txtReleaseHash)

$btnCopyReleaseHash = New-Object System.Windows.Forms.Button
$btnCopyReleaseHash.Text = "COPY"
$btnCopyReleaseHash.Size = New-Object System.Drawing.Size(56, 23)
$btnCopyReleaseHash.Location = New-Object System.Drawing.Point(900, 136)
$btnCopyReleaseHash.BackColor = [System.Drawing.Color]::FromArgb(30, 30, 30)
$btnCopyReleaseHash.ForeColor = [System.Drawing.Color]::FromArgb(220, 220, 220)
$btnCopyReleaseHash.FlatStyle = "Flat"
$btnCopyReleaseHash.FlatAppearance.BorderColor = [System.Drawing.Color]::FromArgb(70, 70, 70)
$btnCopyReleaseHash.FlatAppearance.BorderSize = 1
$form.Controls.Add($btnCopyReleaseHash)

$output = New-Object System.Windows.Forms.TextBox
$output.Multiline = $true
$output.ReadOnly = $true
$output.ScrollBars = "Vertical"
$output.Font = New-Object System.Drawing.Font("Consolas", 10)
$output.BackColor = [System.Drawing.Color]::FromArgb(16, 16, 16)
$output.ForeColor = [System.Drawing.Color]::FromArgb(200, 220, 200)
$output.BorderStyle = "FixedSingle"
$output.Location = New-Object System.Drawing.Point(20, 166)
$output.Size = New-Object System.Drawing.Size(938, 362)
$form.Controls.Add($output)

$buttonPanel = New-Object System.Windows.Forms.Panel
$buttonPanel.Location = New-Object System.Drawing.Point(20, 538)
$buttonPanel.Size = New-Object System.Drawing.Size(938, 74)
$buttonPanel.BackColor = [System.Drawing.Color]::FromArgb(20, 20, 20)
$form.Controls.Add($buttonPanel)

function Set-Progress {
    param([int]$Value)
    $safe = [Math]::Max(0, [Math]::Min(100, $Value))
    $progress.Value = $safe
    $form.Refresh()
}

function Write-Log {
    param(
        [string]$Message,
        [string]$Level = "INFO"
    )

    $stamp = Get-Date -Format "HH:mm:ss"
    $line = "[{0}] [{1}] {2}" -f $stamp, $Level, $Message
    $output.AppendText($line + [Environment]::NewLine)
    $output.SelectionStart = $output.TextLength
    $output.ScrollToCaret()
    if (-not [string]::IsNullOrWhiteSpace($script:LogFile)) {
        try {
            Ensure-WorkingFolders
            Add-Content -Path $script:LogFile -Value $line -Encoding UTF8
        }
        catch { }
    }

    $status.Text = $Message
    switch ($Level) {
        "ERROR" { $status.ForeColor = [System.Drawing.Color]::FromArgb(230, 90, 90) }
        "WARN" { $status.ForeColor = [System.Drawing.Color]::FromArgb(230, 190, 85) }
        default { $status.ForeColor = [System.Drawing.Color]::FromArgb(175, 175, 175) }
    }

    [System.Windows.Forms.Application]::DoEvents()
}

function Ensure-ConfiguredProject {
    if ([string]::IsNullOrWhiteSpace($script:RepoRoot) -or -not (Test-ProjectRoot $script:RepoRoot)) {
        throw "Select a valid project root folder first (must contain LatticeVeilMonoGame\LatticeVeilMonoGame.csproj)."
    }

    if (-not (Test-Path -LiteralPath $script:GameProject)) {
        throw "Game project not found: $script:GameProject"
    }
}

function Apply-ProjectRootFromInput {
    param([string]$Candidate)

    $target = if ($Candidate) { $Candidate.Trim() } else { "" }
    if (Set-ProjectRoot -RootPath $target -Silent) {
        Write-Log "Project root set: $script:RepoRoot"
        return $true
    }

    Write-Log "Invalid project root. Pick the folder containing LatticeVeilMonoGame\LatticeVeilMonoGame.csproj." "ERROR"
    return $false
}

function Invoke-External {
    param(
        [string]$Exe,
        [string[]]$Arguments,
        [string]$WorkingDirectory
    )

    $joined = $Arguments -join " "
    Write-Log "Running: $Exe $joined"

    Push-Location $WorkingDirectory
    try {
        $result = & $Exe @Arguments 2>&1
        $exitCode = $LASTEXITCODE
    }
    finally {
        Pop-Location
    }

    foreach ($entry in $result) {
        if ($entry -is [System.Management.Automation.ErrorRecord]) {
            Write-Log ($entry.ToString()) "ERROR"
        }
        else {
            Write-Log "$entry"
        }
    }

    if ($exitCode -ne 0) {
        throw "$Exe exited with code $exitCode"
    }
}

function Clean-Outputs {
    Write-Log "Cleaning bin/obj folders..."
    $targets = @(
        $script:GameDir,
        (Join-Path $script:RepoRoot "GateServer")
    )

    foreach ($target in $targets) {
        if (-not (Test-Path -LiteralPath $target)) {
            continue
        }

        Get-ChildItem -Path $target -Recurse -Directory -ErrorAction SilentlyContinue |
            Where-Object { $_.Name -eq "bin" -or $_.Name -eq "obj" } |
            ForEach-Object {
                try {
                    Remove-Item -Path $_.FullName -Recurse -Force -ErrorAction Stop
                    Write-Log "Removed: $($_.FullName)"
                }
                catch {
                    Write-Log "Skip remove: $($_.FullName)" "WARN"
                }
            }
    }

    # NOTE: We no longer clear DEV/RELEASE drop folders to preserve final EXEs
    Write-Log "Clean complete (DEV/RELEASE folders preserved)"
}

function Get-GameVersion {
    try {
        [xml]$xml = Get-Content -Path $script:GameProject -Raw
        $versionNode = $xml.Project.PropertyGroup | Where-Object { $_.Version } | Select-Object -First 1
        if ($null -ne $versionNode -and -not [string]::IsNullOrWhiteSpace($versionNode.Version)) {
            return $versionNode.Version.Trim()
        }
    }
    catch {
        Write-Log "Could not read version from csproj; using dev" "WARN"
    }
    return "dev"
}

function New-BuildNonce {
    $stamp = Get-Date -Format "yyyyMMddHHmmssfff"
    $rand = [Guid]::NewGuid().ToString("N").Substring(0, 10)
    return "$stamp-$rand"
}

function Get-PublishDirectory {
    return Join-Path $script:GameDir "bin\Release\net8.0-windows\win-x64\publish"
}

function Get-DevPublishDirectory {
    return Join-Path $script:GameDir "bin\Debug\net8.0-windows\win-x64\publish"
}

function Get-PreferredExeInDirectory {
    param([string]$DirectoryPath)

    if ([string]::IsNullOrWhiteSpace($DirectoryPath) -or -not (Test-Path -LiteralPath $DirectoryPath)) {
        return $null
    }

    $preferred = @("LatticeVeilMonoGame.exe", "LatticeVeilGame.exe", "LatticeVeil.exe")
    foreach ($name in $preferred) {
        $candidate = Join-Path $DirectoryPath $name
        if (Test-Path -LiteralPath $candidate) {
            return (Get-Item -LiteralPath $candidate).FullName
        }
    }

    $fallback = Get-ChildItem -Path $DirectoryPath -Filter "*.exe" -File -ErrorAction SilentlyContinue |
        Sort-Object Length -Descending |
        Select-Object -First 1
    if ($fallback) {
        return $fallback.FullName
    }

    return $null
}

function Get-DebugBuildDirectory {
    return Join-Path $script:GameDir "bin\Debug\net8.0-windows\win-x64"
}

function Get-DebugBuildExePath {
    $devPublishExe = Get-PreferredExeInDirectory -DirectoryPath (Get-DevPublishDirectory)
    if (-not [string]::IsNullOrWhiteSpace($devPublishExe)) {
        return $devPublishExe
    }

    return Get-PreferredExeInDirectory -DirectoryPath (Get-DebugBuildDirectory)
}

function Get-ReleasePublishExePath {
    return Get-PreferredExeInDirectory -DirectoryPath (Get-PublishDirectory)
}

function Get-DevDropDirectory {
    return $script:DevDropDir
}

function Get-ReleaseDropDirectory {
    return $script:ReleaseDropDir
}

function Get-DevDropExePath {
    return Get-PreferredExeInDirectory -DirectoryPath (Get-DevDropDirectory)
}

function Get-ReleaseDropExePath {
    return Get-PreferredExeInDirectory -DirectoryPath (Get-ReleaseDropDirectory)
}

function Reset-DropDirectory {
    param([string]$DirectoryPath)

    if ([string]::IsNullOrWhiteSpace($DirectoryPath)) {
        throw "Drop directory path is empty."
    }

    if (-not (Test-Path -LiteralPath $DirectoryPath)) {
        New-Item -ItemType Directory -Path $DirectoryPath -Force | Out-Null
        return
    }

    Get-ChildItem -LiteralPath $DirectoryPath -Force -ErrorAction SilentlyContinue | ForEach-Object {
        Remove-Item -LiteralPath $_.FullName -Recurse -Force -ErrorAction SilentlyContinue
    }
}

function Stop-ProcessesUsingExecutablePath {
    param([string]$ExecutablePath)

    if ([string]::IsNullOrWhiteSpace($ExecutablePath)) {
        return 0
    }

    $target = [System.IO.Path]::GetFullPath($ExecutablePath)
    $stopped = 0
    $matches = @()
    Get-Process -ErrorAction SilentlyContinue | ForEach-Object {
        try {
            $procPath = $_.Path
            if ([string]::IsNullOrWhiteSpace($procPath)) {
                return
            }

            $fullProcPath = [System.IO.Path]::GetFullPath($procPath)
            if ([string]::Equals($fullProcPath, $target, [System.StringComparison]::OrdinalIgnoreCase)) {
                $matches += $_
            }
        }
        catch {
            # Some system processes deny path access; ignore.
        }
    }

    foreach ($proc in $matches) {
        try {
            Write-Log ("Stopping process locking EXE: {0} (PID {1})" -f $proc.ProcessName, $proc.Id) "WARN"
            Stop-Process -Id $proc.Id -Force -ErrorAction Stop
            $stopped++
        }
        catch {
            Write-Log ("Could not stop process {0} (PID {1}): {2}" -f $proc.ProcessName, $proc.Id, $_.Exception.Message) "WARN"
        }
    }

    return $stopped
}

function Copy-ItemWithRetry {
    param(
        [string]$SourcePath,
        [string]$DestinationPath,
        [int]$MaxAttempts = 8,
        [int]$DelayMilliseconds = 600
    )

    if ([string]::IsNullOrWhiteSpace($SourcePath) -or -not (Test-Path -LiteralPath $SourcePath)) {
        throw "Source file not found: $SourcePath"
    }

    if ([string]::IsNullOrWhiteSpace($DestinationPath)) {
        throw "Destination path is empty."
    }

    $lastError = $null
    for ($attempt = 1; $attempt -le $MaxAttempts; $attempt++) {
        try {
            Copy-Item -LiteralPath $SourcePath -Destination $DestinationPath -Force -ErrorAction Stop
            if ($attempt -gt 1) {
                Write-Log "Copy succeeded after retry $attempt/$MaxAttempts."
            }
            return
        }
        catch {
            $lastError = $_.Exception
            $message = $lastError.Message
            $isLockError = $message -match "being used by another process" -or
                $message -match "used by another process" -or
                $message -match "cannot access the file"

            if ($isLockError) {
                Write-Log "Target EXE is locked (attempt $attempt/$MaxAttempts). Retrying..." "WARN"
                [void](Stop-ProcessesUsingExecutablePath -ExecutablePath $DestinationPath)
            }
            else {
                Write-Log "Copy failed (attempt $attempt/$MaxAttempts): $message" "WARN"
            }

            if ($attempt -lt $MaxAttempts) {
                Start-Sleep -Milliseconds $DelayMilliseconds
                continue
            }
        }
    }

    throw ("Failed to stage EXE after {0} attempts. Close any running game/launcher and retry. Last error: {1}" -f $MaxAttempts, $lastError.Message)
}

function Stage-FinalExe {
    param(
        [string]$ChannelName,
        [string]$SourceExePath,
        [string]$DropDirectory
    )

    if ([string]::IsNullOrWhiteSpace($SourceExePath) -or -not (Test-Path -LiteralPath $SourceExePath)) {
        throw "$ChannelName source EXE not found. Build that configuration first."
    }

    if ([string]::IsNullOrWhiteSpace($DropDirectory)) {
        throw "$ChannelName drop directory is not configured."
    }

    $destPath = Join-Path $DropDirectory ([System.IO.Path]::GetFileName($SourceExePath))
    [void](Stop-ProcessesUsingExecutablePath -ExecutablePath $destPath)
    [void](Stop-ProcessesUsingExecutablePath -ExecutablePath $SourceExePath)

    Reset-DropDirectory -DirectoryPath $DropDirectory

    Copy-ItemWithRetry -SourcePath $SourceExePath -DestinationPath $destPath
    Write-Log "$ChannelName final EXE staged: $destPath"
    return $destPath
}

function Stage-DevFinalExe {
    $source = Get-DebugBuildExePath
    if ([string]::IsNullOrWhiteSpace($source)) {
        return $null
    }

    return Stage-FinalExe -ChannelName "DEV" -SourceExePath $source -DropDirectory (Get-DevDropDirectory)
}

function Stage-ReleaseFinalExe {
    $source = Get-ReleasePublishExePath
    if ([string]::IsNullOrWhiteSpace($source)) {
        return $null
    }

    return Stage-FinalExe -ChannelName "RELEASE" -SourceExePath $source -DropDirectory (Get-ReleaseDropDirectory)
}

function Get-DebugExePath {
    $dropExe = Get-DevDropExePath
    if (-not [string]::IsNullOrWhiteSpace($dropExe)) {
        return $dropExe
    }

    return Get-DebugBuildExePath
}

function Get-DebugOutputDirectory {
    $dropDir = Get-DevDropDirectory
    if (-not [string]::IsNullOrWhiteSpace($dropDir) -and (Test-Path -LiteralPath $dropDir)) {
        return $dropDir
    }

    $devPublishDir = Get-DevPublishDirectory
    if (Test-Path -LiteralPath $devPublishDir) {
        return $devPublishDir
    }

    $debugDir = Get-DebugBuildDirectory
    if (Test-Path -LiteralPath $debugDir) {
        return $debugDir
    }

    return $null
}

function Get-ReleaseExePath {
    $dropExe = Get-ReleaseDropExePath
    if (-not [string]::IsNullOrWhiteSpace($dropExe)) {
        return $dropExe
    }

    return Get-ReleasePublishExePath
}

function Get-ReleaseOutputDirectory {
    $dropDir = Get-ReleaseDropDirectory
    if (-not [string]::IsNullOrWhiteSpace($dropDir) -and (Test-Path -LiteralPath $dropDir)) {
        return $dropDir
    }

    $publishDir = Get-PublishDirectory
    if (Test-Path -LiteralPath $publishDir) {
        return $publishDir
    }

    $releaseBinDir = Join-Path $script:GameDir "bin\Release\net8.0-windows\win-x64"
    if (Test-Path -LiteralPath $releaseBinDir) {
        return $releaseBinDir
    }

    return $null
}

function Open-BuildOutput {
    param(
        [string]$Label,
        [string]$OutputDirectory,
        [string]$ExePath
    )

    if (-not [string]::IsNullOrWhiteSpace($ExePath) -and (Test-Path -LiteralPath $ExePath)) {
        Start-Process -FilePath $ExePath -WorkingDirectory ([System.IO.Path]::GetDirectoryName($ExePath)) | Out-Null
        Write-Log "$Label launched: $ExePath"
        return
    }

    if (-not [string]::IsNullOrWhiteSpace($OutputDirectory) -and (Test-Path -LiteralPath $OutputDirectory)) {
        Start-Process explorer.exe $OutputDirectory | Out-Null
        Write-Log "$Label folder opened: $OutputDirectory"
        return
    }

    Write-Log "$Label output not found. Build it first." "WARN"
}

function Set-HashBoxValues {
    param(
        [string]$DevHash,
        [string]$ReleaseHash
    )

    if (Get-Variable -Name txtDevHash -Scope Script -ErrorAction SilentlyContinue) {
        $script:txtDevHash.Text = $DevHash
    }
    if (Get-Variable -Name txtReleaseHash -Scope Script -ErrorAction SilentlyContinue) {
        $script:txtReleaseHash.Text = $ReleaseHash
    }
}

$actionButtons = New-Object System.Collections.Generic.List[System.Windows.Forms.Button]

function Set-Busy {
    param([bool]$Busy)
    $script:IsBusy = $Busy
    foreach ($btn in $actionButtons) {
        $btn.Enabled = -not $Busy
    }
    if (Get-Variable -Name btnBrowseProject -ErrorAction SilentlyContinue) {
        $btnBrowseProject.Enabled = -not $Busy
    }
    if (Get-Variable -Name btnApplyProject -ErrorAction SilentlyContinue) {
        $btnApplyProject.Enabled = -not $Busy
    }
    if (Get-Variable -Name txtProjectRoot -Scope Script -ErrorAction SilentlyContinue) {
        $script:txtProjectRoot.ReadOnly = $Busy
    }
    if (Get-Variable -Name btnCopyDevHash -ErrorAction SilentlyContinue) {
        $btnCopyDevHash.Enabled = -not $Busy
    }
    if (Get-Variable -Name btnCopyReleaseHash -ErrorAction SilentlyContinue) {
        $btnCopyReleaseHash.Enabled = -not $Busy
    }
}

function Run-Task {
    param(
        [string]$Name,
        [scriptblock]$Work
    )

    if ($script:IsBusy) {
        return
    }

    try {
        Ensure-ConfiguredProject
    }
    catch {
        Write-Log $_.Exception.Message "ERROR"
        return
    }

    Set-Busy $true
    Set-Progress 5
    Write-Log "Starting: $Name"

    try {
        & $Work
        Set-Progress 100
        Write-Log "Done: $Name"
    }
    catch {
        Set-Progress 0
        Write-Log "Task failed: $($_.Exception.Message)" "ERROR"
        if ($_.ScriptStackTrace) {
            Write-Log $_.ScriptStackTrace "ERROR"
        }
    }
    finally {
        Set-Busy $false
    }
}

function Style-Button {
    param(
        [System.Windows.Forms.Button]$Button,
        [System.Drawing.Color]$BackColor,
        [System.Drawing.Color]$ForeColor
    )

    $Button.BackColor = $BackColor
    $Button.ForeColor = $ForeColor
    $Button.FlatStyle = "Flat"
    $Button.FlatAppearance.BorderColor = [System.Drawing.Color]::FromArgb(70, 70, 70)
    $Button.FlatAppearance.BorderSize = 1
    $Button.Font = New-Object System.Drawing.Font("Segoe UI", 9, [System.Drawing.FontStyle]::Bold)
}

$btnBrowseProject.Add_Click({
    $dialog = New-Object System.Windows.Forms.FolderBrowserDialog
    $dialog.Description = "Select project root (must contain LatticeVeilMonoGame\\LatticeVeilMonoGame.csproj)"
    if (Test-Path -LiteralPath $script:RepoRoot) {
        $dialog.SelectedPath = $script:RepoRoot
    }

    if ($dialog.ShowDialog() -eq [System.Windows.Forms.DialogResult]::OK) {
        $script:txtProjectRoot.Text = $dialog.SelectedPath
        Apply-ProjectRootFromInput -Candidate $script:txtProjectRoot.Text | Out-Null
    }
})

$btnApplyProject.Add_Click({
    Apply-ProjectRootFromInput -Candidate $script:txtProjectRoot.Text | Out-Null
})

$script:txtProjectRoot.Add_KeyDown({
    param($sender, $e)
    if ($e.KeyCode -eq [System.Windows.Forms.Keys]::Enter) {
        $e.SuppressKeyPress = $true
        Apply-ProjectRootFromInput -Candidate $script:txtProjectRoot.Text | Out-Null
    }
})

$btnCopyDevHash.Add_Click({
    $value = if ($null -ne $script:txtDevHash.Text) { $script:txtDevHash.Text.Trim() } else { "" }
    if ([string]::IsNullOrWhiteSpace($value)) {
        Write-Log "No dev hash available yet." "WARN"
        return
    }
    [System.Windows.Forms.Clipboard]::SetText($value)
    Write-Log "Dev hash copied to clipboard."
})

$btnCopyReleaseHash.Add_Click({
    $value = if ($null -ne $script:txtReleaseHash.Text) { $script:txtReleaseHash.Text.Trim() } else { "" }
    if ([string]::IsNullOrWhiteSpace($value)) {
        Write-Log "No release hash available yet." "WARN"
        return
    }
    [System.Windows.Forms.Clipboard]::SetText($value)
    Write-Log "Release hash copied to clipboard."
})

$btnQuick = New-Object System.Windows.Forms.Button
$btnQuick.Text = "QUICK TEST BUILD"
$btnQuick.Size = New-Object System.Drawing.Size(170, 30)
$btnQuick.Location = New-Object System.Drawing.Point(10, 10)
Style-Button -Button $btnQuick -BackColor ([System.Drawing.Color]::FromArgb(20, 84, 60)) -ForeColor ([System.Drawing.Color]::White)
$btnQuick.Add_Click({
    Run-Task "Quick Test Build (DEV Publish + Run)" {
        $buildNonce = New-BuildNonce
        Write-Log "BuildNonce: $buildNonce"
        Set-Progress 15
        Clean-Outputs
        Set-Progress 35
        Invoke-External "dotnet" @("restore", $script:GameProject) $script:RepoRoot
        Set-Progress 60
        Invoke-External "dotnet" @(
            "publish", $script:GameProject,
            "--configuration", "Debug",
            "-r", "win-x64",
            "--self-contained", "true",
            "-p:PublishSingleFile=true",
            "-p:IncludeNativeLibrariesForSelfExtract=true",
            "-p:IncludeAllContentForSelfExtract=true",
            "-p:BuildNonce=$buildNonce"
        ) $script:RepoRoot
        Set-Progress 72
        $devExe = Stage-DevFinalExe
        if ([string]::IsNullOrWhiteSpace($devExe) -or -not (Test-Path -LiteralPath $devExe)) {
            throw "DEV publish EXE not found after publish."
        }
        Set-Progress 80
        Write-Log "Launching DEV packaged EXE..."
        Start-Process -FilePath $devExe -WorkingDirectory ([System.IO.Path]::GetDirectoryName($devExe)) | Out-Null
    }
})
$buttonPanel.Controls.Add($btnQuick)
$actionButtons.Add($btnQuick)

$btnRelease = New-Object System.Windows.Forms.Button
$btnRelease.Text = "RELEASE BUILD + ZIP"
$btnRelease.Size = New-Object System.Drawing.Size(170, 30)
$btnRelease.Location = New-Object System.Drawing.Point(190, 10)
Style-Button -Button $btnRelease -BackColor ([System.Drawing.Color]::FromArgb(84, 58, 20)) -ForeColor ([System.Drawing.Color]::White)
$btnRelease.Add_Click({
    Run-Task "Release Build + Publish + Zip" {
        $buildNonce = New-BuildNonce
        Write-Log "BuildNonce: $buildNonce"
        Set-Progress 15
        Clean-Outputs
        Set-Progress 35
        Invoke-External "dotnet" @("restore", $script:GameProject) $script:RepoRoot
        Set-Progress 60
        Invoke-External "dotnet" @("publish", $script:GameProject, "--configuration", "Release", "-r", "win-x64", "--self-contained", "true", "-p:PublishSingleFile=true", "-p:BuildNonce=$buildNonce", "-p:PublishReadyToRun=true") $script:RepoRoot
        Set-Progress 74
        Stage-ReleaseFinalExe | Out-Null
        Set-Progress 82

        $publishDir = Get-PublishDirectory
        $publishedExe = Get-PreferredExeInDirectory -DirectoryPath $publishDir
        if (-not $publishedExe) {
            throw "Published EXE not found in: $publishDir"
        }
        $releaseHash = (Get-FileHash -Path $publishedExe -Algorithm SHA256).Hash.ToLowerInvariant()
        Write-Log "Published EXE hash: $releaseHash"

        $version = Get-GameVersion
        $zipName = "LatticeVeil-v{0}-win-x64.zip" -f $version
        $zipPath = Join-Path $script:BuildsDir $zipName
        if (Test-Path -LiteralPath $zipPath) {
            Remove-Item -Path $zipPath -Force
        }

        Compress-Archive -Path (Join-Path $publishDir "*") -DestinationPath $zipPath -Force
        Write-Log "Release package: $zipPath (contains EXE hash: $releaseHash)"
    }
})
$buttonPanel.Controls.Add($btnRelease)
$actionButtons.Add($btnRelease)

$btnBuildOnly = New-Object System.Windows.Forms.Button
$btnBuildOnly.Text = "BUILD ONLY (DEV)"
$btnBuildOnly.Size = New-Object System.Drawing.Size(170, 30)
$btnBuildOnly.Location = New-Object System.Drawing.Point(370, 10)
Style-Button -Button $btnBuildOnly -BackColor ([System.Drawing.Color]::FromArgb(42, 42, 42)) -ForeColor ([System.Drawing.Color]::White)
$btnBuildOnly.Add_Click({
    Run-Task "DEV Publish Only" {
        $buildNonce = New-BuildNonce
        Write-Log "BuildNonce: $buildNonce"
        Set-Progress 15
        Clean-Outputs
        Set-Progress 20
        Invoke-External "dotnet" @("restore", $script:GameProject) $script:RepoRoot
        Set-Progress 60
        Invoke-External "dotnet" @(
            "publish", $script:GameProject,
            "--configuration", "Debug",
            "-r", "win-x64",
            "--self-contained", "true",
            "-p:PublishSingleFile=true",
            "-p:IncludeNativeLibrariesForSelfExtract=true",
            "-p:IncludeAllContentForSelfExtract=true",
            "-p:BuildNonce=$buildNonce"
        ) $script:RepoRoot
        Set-Progress 78
        Stage-DevFinalExe | Out-Null
    }
})
$buttonPanel.Controls.Add($btnBuildOnly)
$actionButtons.Add($btnBuildOnly)

$btnSha = New-Object System.Windows.Forms.Button
$btnSha.Text = "HASHES (DEV+RELEASE)"
$btnSha.Size = New-Object System.Drawing.Size(170, 30)
$btnSha.Location = New-Object System.Drawing.Point(550, 10)
Style-Button -Button $btnSha -BackColor ([System.Drawing.Color]::FromArgb(36, 72, 112)) -ForeColor ([System.Drawing.Color]::White)
$btnSha.Add_Click({
    Run-Task "Compute DEV + RELEASE SHA256" {
        Set-Progress 35
        $debugExePath = Get-DebugExePath
        $exePath = Get-ReleaseExePath

        $devHash = ""
        if (-not [string]::IsNullOrWhiteSpace($debugExePath)) {
            $devHash = (Get-FileHash -Path $debugExePath -Algorithm SHA256).Hash.ToLowerInvariant()
            Write-Log "DEV EXE: $debugExePath"
            Write-Log "DEV SHA256: $devHash"
        }
        else {
            Write-Log "DEV EXE not found. Run a DEV publish build first." "WARN"
        }

        $releaseHash = ""
        if (-not [string]::IsNullOrWhiteSpace($exePath)) {
            $releaseHash = (Get-FileHash -Path $exePath -Algorithm SHA256).Hash.ToLowerInvariant()
            Write-Log "RELEASE EXE: $exePath"
            Write-Log "RELEASE SHA256: $releaseHash"
        }
        else {
            Write-Log "Release EXE not found. Run release build first." "WARN"
        }

        Set-HashBoxValues -DevHash $devHash -ReleaseHash $releaseHash

        if (-not [string]::IsNullOrWhiteSpace($devHash)) {
            [System.Windows.Forms.Clipboard]::SetText($devHash)
            Write-Log "Dev hash copied to clipboard."
        }
        elseif (-not [string]::IsNullOrWhiteSpace($releaseHash)) {
            [System.Windows.Forms.Clipboard]::SetText($releaseHash)
            Write-Log "Release hash copied to clipboard."
        }
    }
})
$buttonPanel.Controls.Add($btnSha)
$actionButtons.Add($btnSha)

$btnClean = New-Object System.Windows.Forms.Button
$btnClean.Text = "CLEAN OUTPUTS"
$btnClean.Size = New-Object System.Drawing.Size(170, 26)
$btnClean.Location = New-Object System.Drawing.Point(10, 42)
Style-Button -Button $btnClean -BackColor ([System.Drawing.Color]::FromArgb(84, 26, 26)) -ForeColor ([System.Drawing.Color]::White)
$btnClean.Add_Click({
    Run-Task "Clean Outputs" {
        Set-Progress 40
        Clean-Outputs
    }
})
$buttonPanel.Controls.Add($btnClean)
$actionButtons.Add($btnClean)

$btnOpenRepo = New-Object System.Windows.Forms.Button
$btnOpenRepo.Text = "OPEN REPO"
$btnOpenRepo.Size = New-Object System.Drawing.Size(120, 26)
$btnOpenRepo.Location = New-Object System.Drawing.Point(190, 42)
Style-Button -Button $btnOpenRepo -BackColor ([System.Drawing.Color]::FromArgb(30, 30, 30)) -ForeColor ([System.Drawing.Color]::FromArgb(220, 220, 220))
$btnOpenRepo.Add_Click({
    if ([string]::IsNullOrWhiteSpace($script:RepoRoot) -or -not (Test-Path -LiteralPath $script:RepoRoot)) {
        Write-Log "No valid project root selected." "ERROR"
        return
    }
    Start-Process explorer.exe $script:RepoRoot | Out-Null
})
$buttonPanel.Controls.Add($btnOpenRepo)
$actionButtons.Add($btnOpenRepo)

$btnOpenLogs = New-Object System.Windows.Forms.Button
$btnOpenLogs.Text = "OPEN LOGS"
$btnOpenLogs.Size = New-Object System.Drawing.Size(120, 26)
$btnOpenLogs.Location = New-Object System.Drawing.Point(320, 42)
Style-Button -Button $btnOpenLogs -BackColor ([System.Drawing.Color]::FromArgb(30, 30, 30)) -ForeColor ([System.Drawing.Color]::FromArgb(220, 220, 220))
$btnOpenLogs.Add_Click({
    Ensure-WorkingFolders
    Start-Process explorer.exe $script:LogDir | Out-Null
})
$buttonPanel.Controls.Add($btnOpenLogs)
$actionButtons.Add($btnOpenLogs)

$btnOpenDebugBuild = New-Object System.Windows.Forms.Button
$btnOpenDebugBuild.Text = "OPEN DEV FOLDER"
$btnOpenDebugBuild.Size = New-Object System.Drawing.Size(126, 26)
$btnOpenDebugBuild.Location = New-Object System.Drawing.Point(580, 42)
Style-Button -Button $btnOpenDebugBuild -BackColor ([System.Drawing.Color]::FromArgb(30, 30, 30)) -ForeColor ([System.Drawing.Color]::FromArgb(220, 220, 220))
$btnOpenDebugBuild.Add_Click({
    try {
        Ensure-ConfiguredProject
    }
    catch {
        Write-Log $_.Exception.Message "ERROR"
        return
    }

    $debugDir = Get-DebugOutputDirectory
    Open-BuildOutput -Label "DEV output" -OutputDirectory $debugDir -ExePath $null
})
$buttonPanel.Controls.Add($btnOpenDebugBuild)
$actionButtons.Add($btnOpenDebugBuild)

$btnOpenReleaseBuild = New-Object System.Windows.Forms.Button
$btnOpenReleaseBuild.Text = "OPEN RELEASE FOLDER"
$btnOpenReleaseBuild.Size = New-Object System.Drawing.Size(126, 26)
$btnOpenReleaseBuild.Location = New-Object System.Drawing.Point(710, 42)
Style-Button -Button $btnOpenReleaseBuild -BackColor ([System.Drawing.Color]::FromArgb(30, 30, 30)) -ForeColor ([System.Drawing.Color]::FromArgb(220, 220, 220))
$btnOpenReleaseBuild.Add_Click({
    try {
        Ensure-ConfiguredProject
    }
    catch {
        Write-Log $_.Exception.Message "ERROR"
        return
    }

    $releaseDir = Get-ReleaseOutputDirectory
    Open-BuildOutput -Label "RELEASE output" -OutputDirectory $releaseDir -ExePath $null
})
$buttonPanel.Controls.Add($btnOpenReleaseBuild)
$actionButtons.Add($btnOpenReleaseBuild)

$btnClearLog = New-Object System.Windows.Forms.Button
$btnClearLog.Text = "CLEAR OUTPUT"
$btnClearLog.Size = New-Object System.Drawing.Size(120, 26)
$btnClearLog.Location = New-Object System.Drawing.Point(840, 42)
Style-Button -Button $btnClearLog -BackColor ([System.Drawing.Color]::FromArgb(30, 30, 30)) -ForeColor ([System.Drawing.Color]::FromArgb(220, 220, 220))
$btnClearLog.Add_Click({
    $output.Clear()
    Write-Log "Output cleared."
})
$buttonPanel.Controls.Add($btnClearLog)
$actionButtons.Add($btnClearLog)

$note = New-Object System.Windows.Forms.Label
$note.Text = "DEV and RELEASE are both published as self-contained single EXEs and staged to DEV\\ / RELEASE\\."
$note.AutoSize = $true
$note.ForeColor = [System.Drawing.Color]::FromArgb(132, 132, 132)
$note.Font = New-Object System.Drawing.Font("Segoe UI", 8)
$note.Location = New-Object System.Drawing.Point(22, 612)
$form.Controls.Add($note)

Write-Log "Build GUI initialized."
if (-not [string]::IsNullOrWhiteSpace($script:RepoRoot) -and (Test-ProjectRoot $script:RepoRoot)) {
    Write-Log "Project root: $script:RepoRoot"
    Write-Log "DEV final EXE folder: $script:DevDropDir"
    Write-Log "RELEASE final EXE folder: $script:ReleaseDropDir"
    Write-Log "Logs: $script:LogFile"
}
else {
    Write-Log "Project root not auto-detected. Use BROWSE/APPLY to select your repo root." "WARN"
}

[void]$form.ShowDialog()
$form.Dispose()
