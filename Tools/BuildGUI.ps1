Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System.IO.Compression.FileSystem

[System.Windows.Forms.Application]::EnableVisualStyles()

try {
    Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class ConsoleWindow {
    [DllImport("kernel32.dll")]
    public static extern IntPtr GetConsoleWindow();
    [DllImport("user32.dll")]
    public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
}
"@
    $consoleHandle = [ConsoleWindow]::GetConsoleWindow()
    if ($consoleHandle -ne [IntPtr]::Zero) {
        [ConsoleWindow]::ShowWindow($consoleHandle, 0) | Out-Null
    }
}
catch { }

$script:BuildGuiScriptPath = if ($PSCommandPath) { [System.IO.Path]::GetFullPath($PSCommandPath) } else { [System.IO.Path]::GetFullPath($MyInvocation.MyCommand.Path) }
$script:RepoRoot = $null
$script:GameProject = $null
$script:AssetsSource = $null
$script:DevDir = $null
$script:ReleaseDir = $null
$script:LogDir = $null
$script:LogFile = $null
$script:IsBusy = $false
$script:ShouldCloseAfterTask = $false

function Close-ExistingBuildGuis {
    $oldProcessIds = @()

    try {
        $oldProcessIds += Get-Process powershell, pwsh -ErrorAction SilentlyContinue |
            Where-Object { $_.Id -ne $PID -and $_.MainWindowTitle -eq "LatticeVeil Build" } |
            Select-Object -ExpandProperty Id
    }
    catch { }

    try {
        $oldProcessIds += Get-CimInstance Win32_Process -Filter "Name = 'powershell.exe' OR Name = 'pwsh.exe'" |
            Where-Object {
                $_.ProcessId -ne $PID -and
                $_.CommandLine -and
                $_.CommandLine.IndexOf($script:BuildGuiScriptPath, [System.StringComparison]::OrdinalIgnoreCase) -ge 0
            } |
            Select-Object -ExpandProperty ProcessId
    }
    catch { }

    foreach ($processId in @($oldProcessIds | Where-Object { $_ } | Select-Object -Unique)) {
        try {
            $process = Get-Process -Id $processId -ErrorAction Stop
            if (-not $process.CloseMainWindow()) {
                Stop-Process -Id $processId -Force -ErrorAction SilentlyContinue
            }
            else {
                Start-Sleep -Milliseconds 500
                $process.Refresh()
                if (-not $process.HasExited) {
                    Stop-Process -Id $processId -Force -ErrorAction SilentlyContinue
                }
            }
        }
        catch {
            Stop-Process -Id $processId -Force -ErrorAction SilentlyContinue
        }
    }
}

function Test-RepoRoot {
    param([string]$Path)
    if ([string]::IsNullOrWhiteSpace($Path)) { return $false }
    return (Test-Path -LiteralPath (Join-Path $Path "LatticeVeilMonoGame\LatticeVeilMonoGame.csproj"))
}

function Find-RepoRoot {
    $starts = @($PSScriptRoot, (Get-Location).Path)
    foreach ($start in $starts) {
        try { $dir = [System.IO.DirectoryInfo]([System.IO.Path]::GetFullPath($start)) }
        catch { continue }

        while ($null -ne $dir) {
            if (Test-RepoRoot $dir.FullName) { return $dir.FullName }
            $dir = $dir.Parent
        }
    }

    throw "Could not locate LatticeVeil_project from script path: $PSScriptRoot"
}

function Initialize-Paths {
    $script:RepoRoot = Find-RepoRoot
    $script:GameProject = Join-Path $script:RepoRoot "LatticeVeilMonoGame\LatticeVeilMonoGame.csproj"
    $script:AssetsSource = Join-Path $script:RepoRoot "LatticeVeilMonoGame\Defaults\Assets"
    $script:DevDir = Join-Path $script:RepoRoot "DEV"
    $script:ReleaseDir = Join-Path $script:RepoRoot "RELEASE"
    $script:LogDir = Join-Path $script:RepoRoot ".builder\logs"
    $script:LogFile = Join-Path $script:LogDir "BuildGUI.log"
    New-Item -ItemType Directory -Path $script:LogDir -Force | Out-Null
}

Initialize-Paths

Close-ExistingBuildGuis

function Write-Log {
    param(
        [string]$Message,
        [string]$Level = "INFO"
    )

    $line = "[{0}] [{1}] {2}" -f (Get-Date -Format "yyyy-MM-dd HH:mm:ss"), $Level, $Message
    try { Add-Content -LiteralPath $script:LogFile -Value $line -Encoding UTF8 }
    catch { }

    if ($script:LogBox -and -not $script:LogBox.IsDisposed) {
        $append = {
            param($Text)
            $script:LogBox.AppendText($Text + [Environment]::NewLine)
            $script:LogBox.SelectionStart = $script:LogBox.TextLength
            $script:LogBox.ScrollToCaret()
        }

        if ($script:LogBox.InvokeRequired) {
            $script:LogBox.BeginInvoke($append, $line) | Out-Null
        }
        else {
            & $append $line
            [System.Windows.Forms.Application]::DoEvents()
        }
    }
}

function Set-ProgressValue {
    param(
        [int]$Percent,
        [string]$Text = ""
    )

    $Percent = [Math]::Max(0, [Math]::Min(100, $Percent))
    $update = {
        param($Value, $Label)
        if ($script:Progress -and -not $script:Progress.IsDisposed) {
            $script:Progress.Style = "Continuous"
            $script:Progress.Value = $Value
        }
        if ($script:StatusLabel -and -not $script:StatusLabel.IsDisposed -and -not [string]::IsNullOrWhiteSpace($Label)) {
            $script:StatusLabel.Text = "$Label ($Value%)"
        }
    }

    if ($script:Progress -and $script:Progress.InvokeRequired) {
        $script:Progress.BeginInvoke($update, $Percent, $Text) | Out-Null
    }
    else {
        & $update $Percent $Text
        [System.Windows.Forms.Application]::DoEvents()
    }
}

function Set-Busy {
    param([bool]$Busy, [string]$Text = "Ready")
    $script:IsBusy = $Busy
    $script:StatusLabel.Text = $Text
    $script:Progress.Style = "Continuous"
    $script:Progress.Value = if ($Busy) { 0 } else { 100 }
    foreach ($control in @($script:DevButton, $script:ReleaseButton, $script:FolderButton, $script:CleanupButton, $script:ClearLogButton, $script:AutoCloseCheckbox, $script:PostBuildCleanupCheckbox)) {
        if ($control) { $control.Enabled = -not $Busy }
    }
}

function Invoke-LoggedProcess {
    param(
        [string]$FileName,
        [string[]]$Arguments,
        [string]$WorkingDirectory,
        [int]$StartPercent = 10,
        [int]$EndPercent = 90,
        [string]$ProgressText = "Running"
    )

    function Quote-ProcessArgument {
        param([string]$Value)
        if ($null -eq $Value) { return '""' }
        if ($Value -notmatch '[\s"]') { return $Value }
        return '"' + ($Value -replace '"', '\"') + '"'
    }

    $quotedArgs = ($Arguments | ForEach-Object { Quote-ProcessArgument $_ }) -join " "
    Write-Log ("POWERSHELL COMMAND: Set-Location -LiteralPath ""{0}""" -f $WorkingDirectory)
    Write-Log ("POWERSHELL COMMAND: & ""{0}"" {1}" -f $FileName, $quotedArgs)
    Set-ProgressValue $StartPercent $ProgressText

    $previousLocation = (Get-Location).Path
    $lineCount = 0
    $range = [Math]::Max(1, $EndPercent - $StartPercent)
    try {
        Set-Location -LiteralPath $WorkingDirectory
        & $FileName @Arguments 2>&1 | ForEach-Object {
            $text = $_.ToString()
            if ($text.Length -gt 0) {
                Write-Log $text "CMD"
                $lineCount++
                $nextPercent = [Math]::Min($EndPercent - 1, $StartPercent + [Math]::Min($range - 1, [int]($lineCount / 2)))
                Set-ProgressValue $nextPercent $ProgressText
            }
        }

        $exitCode = if ($null -ne $LASTEXITCODE) { $LASTEXITCODE } else { 0 }
        Write-Log ("COMMAND EXIT CODE: {0}" -f $exitCode)
        if ($exitCode -ne 0) {
            throw "$FileName exited with code $exitCode."
        }
        Set-ProgressValue $EndPercent $ProgressText
    }
    finally {
        Set-Location -LiteralPath $previousLocation
    }
}

function Reset-Directory {
    param([string]$Path)
    Write-Log ("POWERSHELL COMMAND: Reset output directory ""{0}""" -f $Path)
    if (Test-Path -LiteralPath $Path) {
        Remove-Item -LiteralPath $Path -Recurse -Force
    }
    New-Item -ItemType Directory -Path $Path -Force | Out-Null
}

function Test-IsUnderAnyRoot {
    param(
        [string]$Path,
        [string[]]$Roots
    )

    if ([string]::IsNullOrWhiteSpace($Path)) { return $false }

    try { $fullPath = [System.IO.Path]::GetFullPath($Path) }
    catch { return $false }

    foreach ($root in $Roots) {
        if ([string]::IsNullOrWhiteSpace($root)) { continue }
        try {
            $fullRoot = [System.IO.Path]::GetFullPath($root).TrimEnd('\', '/') + [System.IO.Path]::DirectorySeparatorChar
            if ($fullPath.StartsWith($fullRoot, [System.StringComparison]::OrdinalIgnoreCase)) {
                return $true
            }
        }
        catch { }
    }

    return $false
}

function Stop-BuildTargetProcess {
    param(
        [int]$ProcessId,
        [string]$Name
    )

    try {
        $process = Get-Process -Id $ProcessId -ErrorAction Stop
        Write-Log ("Closing running build target: {0} (PID {1})" -f $Name, $ProcessId)

        $closed = $false
        try { $closed = $process.CloseMainWindow() }
        catch { $closed = $false }

        if ($closed) {
            Start-Sleep -Milliseconds 1200
            $process.Refresh()
        }

        if (-not $closed -or -not $process.HasExited) {
            Stop-Process -Id $ProcessId -Force -ErrorAction Stop
            Start-Sleep -Milliseconds 300
        }

        return $true
    }
    catch {
        Write-Log ("WARN: Could not close {0} (PID {1}): {2}" -f $Name, $ProcessId, $_.Exception.Message) "WARN"
        return $false
    }
}

function Stop-RunningBuildExecutables {
    param([string]$OutputPath)

    $targetNames = @(
        "LatticeVeil.exe",
        "LatticeVeilGame.exe",
        "LatticeVeilMonoGame.exe"
    )

    $targetRoots = @(
        $OutputPath,
        $script:DevDir,
        $script:ReleaseDir,
        (Join-Path $script:RepoRoot "LatticeVeilMonoGame\bin"),
        (Join-Path $script:RepoRoot ".builder\publish"),
        (Join-Path $script:RepoRoot ".builder\launcher_publish"),
        (Join-Path $script:RepoRoot ".builder\game_publish")
    )

    $runningTargets = @()
    try {
        $runningTargets = @(Get-CimInstance Win32_Process |
            Where-Object {
                $_.ProcessId -ne $PID -and
                $_.ExecutablePath -and
                $targetNames -contains [System.IO.Path]::GetFileName($_.ExecutablePath) -and
                (Test-IsUnderAnyRoot $_.ExecutablePath $targetRoots)
            })
    }
    catch {
        Write-Log ("WARN: Could not inspect running processes before build: {0}" -f $_.Exception.Message) "WARN"
        return
    }

    if ($runningTargets.Count -eq 0) {
        Write-Log "No running LatticeVeil build output executables found."
        return
    }

    $closedCount = 0
    foreach ($target in $runningTargets) {
        if (Stop-BuildTargetProcess -ProcessId ([int]$target.ProcessId) -Name ([System.IO.Path]::GetFileName($target.ExecutablePath))) {
            $closedCount++
        }
    }

    Write-Log ("Closed {0} running build target process(es) before publishing." -f $closedCount)
}

function Publish-Game {
    param(
        [ValidateSet("Debug", "Release")]
        [string]$Configuration,
        [string]$OutputPath
    )

    Set-ProgressValue 3 "Preparing $Configuration"
    Stop-RunningBuildExecutables -OutputPath $OutputPath
    Reset-Directory $OutputPath
    Set-ProgressValue 8 "Publishing $Configuration"
    Invoke-LoggedProcess "dotnet" @(
        "publish",
        $script:GameProject,
        "-c", $Configuration,
        "-r", "win-x64",
        "--self-contained", "true",
        "-p:DebugType=none",
        "-p:DebugSymbols=false",
        "-o", $OutputPath
    ) $script:RepoRoot 10 88 "Publishing $Configuration"
    Write-Log "$Configuration build placed in: $OutputPath"
    Set-ProgressValue 90 "$Configuration published"
}

function New-AssetsZip {
    $zipPath = Join-Path $script:ReleaseDir "assets.zip"
    if (-not (Test-Path -LiteralPath $script:AssetsSource)) {
        throw "Assets source folder missing: $script:AssetsSource"
    }

    if (Test-Path -LiteralPath $zipPath) {
        Remove-Item -LiteralPath $zipPath -Force
    }

    $tempRoot = Join-Path $env:TEMP ("LatticeVeilAssets_" + [Guid]::NewGuid().ToString("N"))
    Write-Log ("POWERSHELL COMMAND: New-Item -ItemType Directory -Path ""{0}"" -Force" -f $tempRoot)
    New-Item -ItemType Directory -Path $tempRoot -Force | Out-Null
    try {
        Write-Log ("POWERSHELL COMMAND: Get-ChildItem ""{0}"" -Recurse -File | Where-Object Extension -ne .json, Name -notlike .git*, and Name -ne .asset_manifest.lvc" -f $script:AssetsSource)
        $files = @(Get-ChildItem -LiteralPath $script:AssetsSource -Recurse -File | Where-Object {
            $_.Extension -ine ".json" -and
            $_.Name -notlike ".git*" -and
            $_.Name -ine ".asset_manifest.lvc" -and
            $_.FullName -notlike "*\.git\*"
        })
        $total = [Math]::Max(1, $files.Count)
        for ($i = 0; $i -lt $files.Count; $i++) {
            $file = $files[$i]
            $relative = $file.FullName.Substring($script:AssetsSource.TrimEnd('\').Length + 1)
            $target = Join-Path $tempRoot $relative
            $targetDir = Split-Path -Parent $target
            if (-not (Test-Path -LiteralPath $targetDir)) {
                New-Item -ItemType Directory -Path $targetDir -Force | Out-Null
            }
            Copy-Item -LiteralPath $file.FullName -Destination $target -Force
            Write-Log ("ASSET COPY: {0}" -f $relative) "CMD"
            Set-ProgressValue (90 + [int](($i + 1) * 5 / $total)) "Packaging assets"
        }

        Write-Log ("POWERSHELL COMMAND: [System.IO.Compression.ZipFile]::CreateFromDirectory(""{0}"", ""{1}"")" -f $tempRoot, $zipPath)
        [System.IO.Compression.ZipFile]::CreateFromDirectory($tempRoot, $zipPath, [System.IO.Compression.CompressionLevel]::Optimal, $false)
        Write-Log "Packaged assets.zip without .json, .git metadata, or installed-marker files: $zipPath"
        Set-ProgressValue 98 "Packaging assets"
    }
    finally {
        if (Test-Path -LiteralPath $tempRoot) {
            Write-Log ("POWERSHELL COMMAND: Remove-Item ""{0}"" -Recurse -Force" -f $tempRoot)
            Remove-Item -LiteralPath $tempRoot -Recurse -Force
        }
    }
}

function Start-Task {
    param(
        [string]$Name,
        [scriptblock]$Action
    )

    if ($script:IsBusy) { return }
    $script:ShouldCloseAfterTask = $false
    Set-Busy $true $Name
    Write-Log "Starting: $Name"
    try {
        & $Action
        Write-Log "Finished: $Name"
    }
    catch {
        Write-Log $_.Exception.Message "ERROR"
        [System.Windows.Forms.MessageBox]::Show($_.Exception.Message, "Build GUI Error", "OK", "Error") | Out-Null
    }
    finally {
        Set-ProgressValue 100 "Ready"
        Set-Busy $false "Ready"
        if ($script:ShouldCloseAfterTask -and $form -and -not $form.IsDisposed) {
            $form.Close()
        }
    }
}

function Complete-BuildOptions {
    param([string]$BuildName)

    if ($script:PostBuildCleanupCheckbox -and $script:PostBuildCleanupCheckbox.Checked) {
        Write-Log "Post build cleanup enabled after $BuildName."
        Invoke-Cleanup
    }

    if ($script:AutoCloseCheckbox -and $script:AutoCloseCheckbox.Checked) {
        Write-Log "Auto close enabled after $BuildName."
        $script:ShouldCloseAfterTask = $true
    }
}

function Remove-SafeDirectory {
    param([string]$Path)
    if ([string]::IsNullOrWhiteSpace($Path)) { return }
    $resolved = [System.IO.Path]::GetFullPath($Path)
    if (-not $resolved.StartsWith($script:RepoRoot, [System.StringComparison]::OrdinalIgnoreCase)) {
        Write-Log "Skipped unsafe cleanup target: $resolved" "WARN"
        return
    }
    if (Test-Path -LiteralPath $resolved) {
        Write-Log "Removing: $resolved"
        Remove-Item -LiteralPath $resolved -Recurse -Force
    }
}

function Invoke-Cleanup {
    $targets = @(
        (Join-Path $script:RepoRoot ".builder\cache"),
        (Join-Path $script:RepoRoot ".builder\staging"),
        (Join-Path $script:RepoRoot "LatticeVeilMonoGame\bin"),
        (Join-Path $script:RepoRoot "LatticeVeilMonoGame\obj")
    )

    Get-ChildItem -LiteralPath $script:RepoRoot -Recurse -Directory -Force |
        Where-Object { $_.Name -in @("bin", "obj") -and $_.FullName -notlike "*\.git\*" } |
        ForEach-Object { $targets += $_.FullName }

    $uniqueTargets = @($targets | Select-Object -Unique)
    $total = [Math]::Max(1, $uniqueTargets.Count)
    for ($i = 0; $i -lt $uniqueTargets.Count; $i++) {
        Set-ProgressValue ([int](($i * 95) / $total)) "Cleaning"
        Remove-SafeDirectory $uniqueTargets[$i]
    }
    Set-ProgressValue 98 "Cleaning"
    Write-Log "Cleanup complete."
}

function Open-RepoFolder {
    Start-Process explorer.exe -ArgumentList "`"$script:RepoRoot`""
}

$form = [System.Windows.Forms.Form]::new()
$form.Text = "LatticeVeil Build"
$form.StartPosition = "CenterScreen"
$form.Size = [System.Drawing.Size]::new(900, 620)
$form.MinimumSize = [System.Drawing.Size]::new(900, 620)
$form.BackColor = [System.Drawing.Color]::FromArgb(10, 12, 18)
$form.ForeColor = [System.Drawing.Color]::White

$header = [System.Windows.Forms.Panel]::new()
$header.Dock = "Top"
$header.Height = 90
$header.BackColor = [System.Drawing.Color]::FromArgb(16, 19, 29)
$form.Controls.Add($header)

$logoPath = Join-Path $script:RepoRoot "LatticeVeil Launcher logo.png"
if (Test-Path -LiteralPath $logoPath) {
    $logo = [System.Windows.Forms.PictureBox]::new()
    $logo.Image = [System.Drawing.Image]::FromFile($logoPath)
    $logo.SizeMode = "Zoom"
    $logo.Location = [System.Drawing.Point]::new(16, 12)
    $logo.Size = [System.Drawing.Size]::new(70, 66)
    $header.Controls.Add($logo)
}

$title = [System.Windows.Forms.Label]::new()
$title.Text = "LatticeVeil Build"
$title.Font = [System.Drawing.Font]::new("Segoe UI", 22, [System.Drawing.FontStyle]::Bold)
$title.ForeColor = [System.Drawing.Color]::FromArgb(232, 238, 248)
$title.AutoSize = $true
$title.Location = [System.Drawing.Point]::new(100, 15)
$header.Controls.Add($title)

$subtitle = [System.Windows.Forms.Label]::new()
$subtitle.Text = "DEV build, release build, assets.zip packaging, and cleanup"
$subtitle.Font = [System.Drawing.Font]::new("Segoe UI", 9.5)
$subtitle.ForeColor = [System.Drawing.Color]::FromArgb(150, 162, 182)
$subtitle.AutoSize = $true
$subtitle.Location = [System.Drawing.Point]::new(104, 55)
$header.Controls.Add($subtitle)

$script:StatusLabel = [System.Windows.Forms.Label]::new()
$script:StatusLabel.Text = "Ready"
$script:StatusLabel.Font = [System.Drawing.Font]::new("Segoe UI", 10, [System.Drawing.FontStyle]::Bold)
$script:StatusLabel.ForeColor = [System.Drawing.Color]::FromArgb(138, 218, 166)
$script:StatusLabel.AutoSize = $true
$script:StatusLabel.Location = [System.Drawing.Point]::new(740, 34)
$header.Controls.Add($script:StatusLabel)

$script:Progress = [System.Windows.Forms.ProgressBar]::new()
$script:Progress.Location = [System.Drawing.Point]::new(20, 106)
$script:Progress.Size = [System.Drawing.Size]::new(842, 16)
$script:Progress.Value = 100
$form.Controls.Add($script:Progress)

$rootLabel = [System.Windows.Forms.Label]::new()
$rootLabel.Text = "Repo: $script:RepoRoot"
$rootLabel.Font = [System.Drawing.Font]::new("Segoe UI", 9)
$rootLabel.ForeColor = [System.Drawing.Color]::FromArgb(160, 172, 192)
$rootLabel.AutoSize = $true
$rootLabel.Location = [System.Drawing.Point]::new(20, 132)
$form.Controls.Add($rootLabel)

function New-BuildButton {
    param([string]$Text, [int]$X, [int]$Y, [int]$W = 195)
    $button = [System.Windows.Forms.Button]::new()
    $button.Text = $Text
    $button.Size = [System.Drawing.Size]::new($W, 48)
    $button.Location = [System.Drawing.Point]::new($X, $Y)
    $button.BackColor = [System.Drawing.Color]::FromArgb(28, 35, 50)
    $button.ForeColor = [System.Drawing.Color]::FromArgb(238, 242, 248)
    $button.FlatStyle = "Flat"
    $button.Font = [System.Drawing.Font]::new("Segoe UI", 10, [System.Drawing.FontStyle]::Bold)
    $button.FlatAppearance.BorderColor = [System.Drawing.Color]::FromArgb(82, 104, 138)
    $button.FlatAppearance.BorderSize = 1
    $form.Controls.Add($button)
    return $button
}

function New-OptionCheckbox {
    param([string]$Text, [int]$X, [int]$Y, [int]$W = 190)
    $checkbox = [System.Windows.Forms.CheckBox]::new()
    $checkbox.Text = $Text
    $checkbox.Size = [System.Drawing.Size]::new($W, 24)
    $checkbox.Location = [System.Drawing.Point]::new($X, $Y)
    $checkbox.BackColor = $form.BackColor
    $checkbox.ForeColor = [System.Drawing.Color]::FromArgb(210, 220, 235)
    $checkbox.FlatStyle = "Flat"
    $checkbox.Font = [System.Drawing.Font]::new("Segoe UI", 9, [System.Drawing.FontStyle]::Bold)
    $form.Controls.Add($checkbox)
    return $checkbox
}

$script:DevButton = New-BuildButton "BUILD DEV" 20 166
$script:ReleaseButton = New-BuildButton "BUILD RELEASE + ASSETS" 230 166 235
$script:FolderButton = New-BuildButton "OPEN REPO FOLDER" 480 166 190
$script:CleanupButton = New-BuildButton "CLEAN CACHE + BIN" 685 166 177
$script:AutoCloseCheckbox = New-OptionCheckbox "AUTO CLOSE AFTER BUILD" 20 224 210
$script:PostBuildCleanupCheckbox = New-OptionCheckbox "POST BUILD CLEANUP" 250 224 190

$logLabel = [System.Windows.Forms.Label]::new()
$logLabel.Text = "Live Log"
$logLabel.Font = [System.Drawing.Font]::new("Segoe UI", 11, [System.Drawing.FontStyle]::Bold)
$logLabel.ForeColor = [System.Drawing.Color]::FromArgb(230, 235, 245)
$logLabel.AutoSize = $true
$logLabel.Location = [System.Drawing.Point]::new(20, 268)
$form.Controls.Add($logLabel)

$logFileLabel = [System.Windows.Forms.Label]::new()
$logFileLabel.Text = $script:LogFile
$logFileLabel.Font = [System.Drawing.Font]::new("Segoe UI", 8.5)
$logFileLabel.ForeColor = [System.Drawing.Color]::FromArgb(130, 142, 162)
$logFileLabel.AutoSize = $true
$logFileLabel.Location = [System.Drawing.Point]::new(92, 272)
$form.Controls.Add($logFileLabel)

$script:ClearLogButton = New-BuildButton "CLEAR LOG" 730 260 132
$script:ClearLogButton.Size = [System.Drawing.Size]::new(132, 34)

$script:LogBox = [System.Windows.Forms.RichTextBox]::new()
$script:LogBox.Location = [System.Drawing.Point]::new(20, 304)
$script:LogBox.Size = [System.Drawing.Size]::new(842, 240)
$script:LogBox.Anchor = "Top,Bottom,Left,Right"
$script:LogBox.BackColor = [System.Drawing.Color]::FromArgb(7, 9, 14)
$script:LogBox.ForeColor = [System.Drawing.Color]::FromArgb(218, 230, 242)
$script:LogBox.BorderStyle = "FixedSingle"
$script:LogBox.Font = [System.Drawing.Font]::new("Consolas", 9)
$script:LogBox.ReadOnly = $true
$form.Controls.Add($script:LogBox)

$script:DevButton.Add_Click({
    Start-Task "Building DEV" {
        Publish-Game -Configuration "Debug" -OutputPath $script:DevDir
        Complete-BuildOptions "DEV"
    }
})

$script:ReleaseButton.Add_Click({
    Start-Task "Building RELEASE and assets.zip" {
        Publish-Game -Configuration "Release" -OutputPath $script:ReleaseDir
        New-AssetsZip
        Complete-BuildOptions "RELEASE"
    }
})

$script:FolderButton.Add_Click({ Open-RepoFolder })

$script:CleanupButton.Add_Click({
    Start-Task "Cleaning cache and bin folders" {
        Invoke-Cleanup
    }
})

$script:ClearLogButton.Add_Click({
    if ($script:IsBusy) { return }
    try {
        Set-Content -LiteralPath $script:LogFile -Value "" -Encoding UTF8
        $script:LogBox.Clear()
        Write-Log "Log cleared."
    }
    catch {
        [System.Windows.Forms.MessageBox]::Show($_.Exception.Message, "Clear Log Failed", "OK", "Error") | Out-Null
    }
})

$form.Add_Shown({
    Write-Log "Build GUI opened."
    Write-Log "DEV output: $script:DevDir"
    Write-Log "RELEASE output: $script:ReleaseDir"
})

[void][System.Windows.Forms.Application]::Run($form)
