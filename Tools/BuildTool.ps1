Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing

# --- Configuration ---
$ProjectName = "RedactedCraftMonoGame"
$ProjectFile = "$PSScriptRoot\..\RedactedCraftMonoGame\RedactedCraftMonoGame.csproj"
$SolutionRoot = Resolve-Path "$PSScriptRoot\.."
$IconPath = "$PSScriptRoot\..\RedactedCraftMonoGame\Icon.ico"

# --- Visual Styling (Dark Mode) ---
$ColorBackground = [System.Drawing.Color]::FromArgb(30, 30, 30)
$ColorPanel      = [System.Drawing.Color]::FromArgb(45, 45, 48)
$ColorText       = [System.Drawing.Color]::White
$ColorAccent     = [System.Drawing.Color]::FromArgb(0, 122, 204)
$ColorButton     = [System.Drawing.Color]::FromArgb(60, 60, 60)
$ColorButtonHover= [System.Drawing.Color]::FromArgb(80, 80, 80)
$ColorSuccess    = [System.Drawing.Color]::SeaGreen
$ColorError      = [System.Drawing.Color]::IndianRed

# --- Functions ---

$script:busy = $false

function Pump-Ui() {
    [System.Windows.Forms.Application]::DoEvents()
}

function Invoke-Ui([Action]$action) {
    if ($form -ne $null -and $form.InvokeRequired) {
        $form.BeginInvoke($action) | Out-Null
    } else {
        $action.Invoke()
    }
}

function Set-ButtonsEnabled($build, $ship, $clean, $open, $lore) {
    Invoke-Ui([Action]{
        if ($build -ne $null) { $btnBuild.Enabled = $build }
        if ($ship -ne $null) { $btnShip.Enabled = $ship }
        if ($clean -ne $null) { $btnClean.Enabled = $clean }
        if ($open -ne $null) { $btnOpen.Enabled = $open }
        if ($lore -ne $null -and $btnLore -ne $null) { $btnLore.Enabled = $lore }
    })
}

function Start-BackgroundTask([ScriptBlock]$work) {
    if ($script:busy) {
        Log "Busy: another task is still running."
        return
    }
    $script:busy = $true
    try {
        & $work
    } catch {
        Log "ERROR: $_"
    } finally {
        $script:busy = $false
        Set-ButtonsEnabled $true $true $true $true $true
    }
}

function Log($message) {
    Invoke-Ui([Action]{
        $txtOutput.AppendText("[$([DateTime]::Now.ToString('HH:mm:ss'))] $message`r`n")
        $txtOutput.ScrollToCaret()
        $form.Refresh()
    })
}

function Run-Process($program, $cmdArgs) {
    Log "Exec: $program $cmdArgs"
    $pinfo = New-Object System.Diagnostics.ProcessStartInfo
    $pinfo.FileName = $program
    $pinfo.Arguments = $cmdArgs
    $pinfo.RedirectStandardOutput = $true
    $pinfo.RedirectStandardError = $true
    $pinfo.UseShellExecute = $false
    $pinfo.CreateNoWindow = $true
    $pinfo.WorkingDirectory = $SolutionRoot

    $p = New-Object System.Diagnostics.Process
    $p.StartInfo = $pinfo
    $p.EnableRaisingEvents = $true

    $outId = "rc_out_$([Guid]::NewGuid().ToString())"
    $errId = "rc_err_$([Guid]::NewGuid().ToString())"
    $outEvent = Register-ObjectEvent -InputObject $p -EventName OutputDataReceived -SourceIdentifier $outId -Action {
        if ($EventArgs.Data) { Log $EventArgs.Data }
    }
    $errEvent = Register-ObjectEvent -InputObject $p -EventName ErrorDataReceived -SourceIdentifier $errId -Action {
        if ($EventArgs.Data) { Log "ERROR: $($EventArgs.Data)" }
    }

    $p.Start() | Out-Null
    $p.BeginOutputReadLine()
    $p.BeginErrorReadLine()
    while (-not $p.HasExited) {
        Pump-Ui
        Start-Sleep -Milliseconds 50
    }
    $p.WaitForExit()

    Unregister-Event -SourceIdentifier $outId -ErrorAction SilentlyContinue
    Unregister-Event -SourceIdentifier $errId -ErrorAction SilentlyContinue
    if ($outEvent) { Remove-Job -Id $outEvent.Id -Force -ErrorAction SilentlyContinue }
    if ($errEvent) { Remove-Job -Id $errEvent.Id -Force -ErrorAction SilentlyContinue }
    $p.Dispose()

    return $p.ExitCode
}

function Set-Progress($value, $status) {
    Invoke-Ui([Action]{
        if ($progressBar -ne $null) {
            $progressBar.Style = "Continuous"
            $progressBar.MarqueeAnimationSpeed = 0
            $progressBar.Value = [Math]::Max(0, [Math]::Min(100, [int]$value))
        }
        if ($lblProgress -ne $null -and $status) {
            $lblProgress.Text = $status
        }
        $form.Refresh()
    })
}

function Set-ProgressMarquee($status) {
    Invoke-Ui([Action]{
        if ($progressBar -ne $null) {
            $progressBar.Style = "Marquee"
            $progressBar.MarqueeAnimationSpeed = 30
        }
        if ($lblProgress -ne $null -and $status) {
            $lblProgress.Text = $status
        }
        $form.Refresh()
    })
}

function Build-Game {
    Log "Starting Build (Release)..."
    Set-ButtonsEnabled $false $false $false $null $false

    Set-ProgressMarquee "Building (Release)..."
    $code = Run-Process "dotnet" "build `"$ProjectFile`" -c Release"

    if ($code -eq 0) {
        Log "Build Successful."
        Set-Progress 100 "Build complete."
    } else {
        Log "Build Failed."
        Set-Progress 0 "Build failed."
    }

    Set-ButtonsEnabled $true $true $true $null $true
    return $code
}

function Run-Game {
    if ((Build-Game) -eq 0) {
        Log "Launching Game..."
        Set-Progress 100 "Game launched."
        # Launch detached
        Start-Process "dotnet" -ArgumentList "run --project `"$ProjectFile`" -c Release" -WorkingDirectory $SolutionRoot
    }
}

function Prepare-Ship([bool]$runAfter = $false) {
    Set-ButtonsEnabled $false $false $false $null $false
    Log "Preparing for Deployment..."
    Set-Progress 5 "Cleaning old artifacts..."

    $BuildDir = "$SolutionRoot\Builds"
    $PublishDir = "$BuildDir\Publish_Temp"
    $FinalExe = "$BuildDir\RedactedCraft.exe"

    # 1. Clean old artifacts
    if (Test-Path "$SolutionRoot\RedactedCraft.exe") { Remove-Item "$SolutionRoot\RedactedCraft.exe" }
    if (Test-Path "$SolutionRoot\RedactedcraftCsharp.zip") { Remove-Item "$SolutionRoot\RedactedcraftCsharp.zip" }
    if (Test-Path $BuildDir) { Remove-Item $BuildDir -Recurse -Force }
    New-Item -ItemType Directory -Force -Path $BuildDir | Out-Null

    # 2. Build/Publish Single File to Temp
    Log "Publishing Single File Exe..."
    Set-ProgressMarquee "Publishing single file exe..."
    $code = Run-Process "dotnet" "publish `"$ProjectFile`" -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -p:DebugType=None -p:DebugSymbols=false -o `"$PublishDir`""
    $exeSource = "$PublishDir\RedactedCraftMonoGame.exe"

    if ($code -ne 0 -and -not (Test-Path $exeSource)) {
        Log "Publish Failed. (exit code: $code)"
        Set-Progress 0 "Publish failed."
        Set-ButtonsEnabled $true $true $true $null $true
        return
    }

    if ($code -ne 0) {
        Log "Publish returned non-zero ($code), but output exists. Continuing..."
    }

    Set-Progress 60 "Publish complete."

    # 3. Move EXE to Builds Folder & Clean Temp
    if (Test-Path $exeSource) {
        Move-Item $exeSource $FinalExe -Force
        Log "Placed Executable: $FinalExe"
        Set-Progress 75 "Executable packaged."
    } else {
        Log "Error: Published EXE not found at $exeSource"
    }

    # Remove temp folder and any extra files (like pdbs if they slipped through)
    if (Test-Path $PublishDir) { Remove-Item $PublishDir -Recurse -Force }

    # 4. Create Source Zip
    Log "Creating Source Zip (git archive)..."
    Set-ProgressMarquee "Creating source zip..."
    try {
        Set-Location $SolutionRoot
        git archive -o RedactedcraftCsharp.zip HEAD --format=zip --worktree-attributes
        Log "Created RedactedcraftCsharp.zip"
        Set-Progress 95 "Source zip complete."
    } catch {
        Log "Git archive failed: $_"
    }

    Log "Deployment Ready!"
    Set-Progress 100 "Deployment ready."
    Log "Files created:"
    Log "  - Builds\RedactedCraft.exe"
    Log "  - RedactedcraftCsharp.zip"

    if ($runAfter -and (Test-Path $FinalExe)) {
        Log "Launching packaged build..."
        Start-Process $FinalExe -WorkingDirectory $BuildDir
    }

    Set-ButtonsEnabled $true $true $true $null $true
}

function Export-LorePack {
    Log "Exporting lore pack (data + textures)..."
    Set-ButtonsEnabled $false $false $false $false $false
    Set-ProgressMarquee "Exporting lore pack..."

    $BuildDir = "$SolutionRoot\Builds"
    $LoreOutDir = "$BuildDir\LorePack"
    $LoreDataSrc = "$SolutionRoot\RedactedCraftMonoGame\Defaults\Assets\data\lore"
    $LoreBlocksSrc = "$SolutionRoot\RedactedCraftMonoGame\Defaults\Assets\textures\blocks"
    $LoreZip = "$SolutionRoot\RedactedCraft_LorePack_FULL_v1_1d_LOREPLUS_TEXTURES.zip"

    if (Test-Path $LoreOutDir) { Remove-Item $LoreOutDir -Recurse -Force }
    New-Item -ItemType Directory -Force -Path $BuildDir | Out-Null
    New-Item -ItemType Directory -Force -Path $LoreOutDir | Out-Null

    Set-Progress 20 "Copying lore data..."
    if (Test-Path $LoreDataSrc) {
        $dataDest = "$LoreOutDir\Assets\data\lore"
        New-Item -ItemType Directory -Force -Path $dataDest | Out-Null
        Copy-Item -Path (Join-Path $LoreDataSrc "*") -Destination $dataDest -Recurse -Force
        Log "Lore data copied."
    } else {
        Log "Lore data folder not found: $LoreDataSrc"
    }

    Set-Progress 55 "Copying lore textures..."
    $blocksDest = "$LoreOutDir\Assets\textures\blocks"
    New-Item -ItemType Directory -Force -Path $blocksDest | Out-Null
    $textures = @(
        "runestone.png",
        "veinstone.png",
        "veilglass.png",
        "resonance_core.png",
        "waybound_frame.png",
        "transit_regulator.png"
    )
    foreach ($tex in $textures) {
        $src = Join-Path $LoreBlocksSrc $tex
        if (Test-Path $src) {
            Copy-Item $src $blocksDest -Force
            Log "Copied texture: $tex"
        } else {
            Log "Missing texture: $src"
        }
        Pump-Ui
    }

    Set-Progress 85 "Copying lore pack zip..."
    if (Test-Path $LoreZip) {
        $zipDest = Join-Path $BuildDir (Split-Path $LoreZip -Leaf)
        Copy-Item $LoreZip $zipDest -Force
        Log "Copied lore pack zip: $zipDest"
    } else {
        Log "Lore pack zip not found: $LoreZip"
    }

    Set-Progress 100 "Lore pack export complete."
    Set-ButtonsEnabled $true $true $true $true $true
}

function Clean-Project {
    Log "Cleaning Project Artifacts..."
    Set-ButtonsEnabled $false $false $false $null $false
    Set-ProgressMarquee "Cleaning project..."

    # Files/Folders to remove from Root
    $itemsToRemove = @(
        "Builds",
        "artifacts",
        "publish",
        "out",
        "RedactedcraftCsharp.zip",
        "RedactedCraft.exe"
    )

    foreach ($item in $itemsToRemove) {
        $path = "$SolutionRoot\$item"
        if (Test-Path $path) {
            try {
                Remove-Item -Recurse -Force $path -ErrorAction Stop
                Log "Deleted: $item"
                Pump-Ui
            } catch {
                Log "Error deleting $item : $_"
                Pump-Ui
            }
        }
    }

    Log "Removing bin/obj folders..."
    try {
        Get-ChildItem -Path $SolutionRoot -Include bin,obj -Recurse -Force | Where-Object { $_.PSIsContainer } | ForEach-Object {
            try {
                Remove-Item -Recurse -Force $_.FullName -ErrorAction Stop
            } catch {
                Log "Error deleting $($_.Name): $_"
                Pump-Ui
            }
        }
        Log "Bin/Obj folders cleared."
    } catch {
        Log "Error scanning for folders: $_"
    }

    Log "Cleanup Complete."
    Set-Progress 100 "Cleanup complete."
    Set-ButtonsEnabled $true $true $true $null $true
}

function Open-BuildFolder {
    $path = "$SolutionRoot\Builds"
    if (-not (Test-Path $path)) {
        New-Item -ItemType Directory -Force -Path $path | Out-Null
    }
    Invoke-Item $path
}

# --- UI Layout ---
$form = New-Object System.Windows.Forms.Form
$form.Text = "RedactedCraft Builder"
$form.Size = New-Object System.Drawing.Size(500, 450)
$form.StartPosition = "CenterScreen"
$form.BackColor = $ColorBackground
$form.FormBorderStyle = "FixedSingle"
$form.MaximizeBox = $false
if (Test-Path $IconPath) { $form.Icon = New-Object System.Drawing.Icon($IconPath) }

$lblTitle = New-Object System.Windows.Forms.Label
$lblTitle.Text = "REDACTED CRAFT"
$lblTitle.Font = New-Object System.Drawing.Font("Consolas", 20, [System.Drawing.FontStyle]::Bold)
$lblTitle.ForeColor = $ColorAccent
$lblTitle.AutoSize = $true
$lblTitle.Location = New-Object System.Drawing.Point(20, 20)
$form.Controls.Add($lblTitle)

$lblSub = New-Object System.Windows.Forms.Label
$lblSub.Text = "Build & Deployment Tool"
$lblSub.Font = New-Object System.Drawing.Font("Segoe UI", 10)
$lblSub.ForeColor = $ColorText
$lblSub.AutoSize = $true
$lblSub.Location = New-Object System.Drawing.Point(25, 55)
$form.Controls.Add($lblSub)

# Buttons
$btnBuild = New-Object System.Windows.Forms.Button
$btnBuild.Text = "PLAY (Build & Run)"
$btnBuild.Location = New-Object System.Drawing.Point(25, 90)
$btnBuild.Size = New-Object System.Drawing.Size(200, 50)
$btnBuild.FlatStyle = "Flat"
$btnBuild.BackColor = $ColorButton
$btnBuild.ForeColor = $ColorSuccess
$btnBuild.Font = New-Object System.Drawing.Font("Segoe UI", 10, [System.Drawing.FontStyle]::Bold)
$btnBuild.Add_Click({ Start-BackgroundTask { Run-Game } })
$form.Controls.Add($btnBuild)

$btnShip = New-Object System.Windows.Forms.Button
$btnShip.Text = "PREPARE + RUN`n(Zip + Exe)"
$btnShip.Location = New-Object System.Drawing.Point(240, 90)
$btnShip.Size = New-Object System.Drawing.Size(180, 50)
$btnShip.FlatStyle = "Flat"
$btnShip.BackColor = $ColorButton
$btnShip.ForeColor = $ColorAccent
$btnShip.Font = New-Object System.Drawing.Font("Segoe UI", 9)
$btnShip.Add_Click({ Start-BackgroundTask { Prepare-Ship $true } })
$form.Controls.Add($btnShip)

$btnOpen = New-Object System.Windows.Forms.Button

# P/Invoke to extract shell icons
$Win32 = Add-Type -MemberDefinition '
    [DllImport("shell32.dll")] public static extern IntPtr ExtractIcon(IntPtr hInst, string lpszExeFileName, int nIconIndex);
' -Name "Win32Utils" -Namespace Win32 -PassThru

try {
    # Index 3 in shell32.dll is the standard closed folder icon
    $hIcon = $Win32::ExtractIcon([IntPtr]::Zero, "$env:SystemRoot\system32\shell32.dll", 3)
    if ($hIcon -ne [IntPtr]::Zero) {
        $icon = [System.Drawing.Icon]::FromHandle($hIcon)
        $btnOpen.Image = $icon.ToBitmap()
        $btnOpen.TextImageRelation = "Overlay"
    } else {
        $btnOpen.Text = "OPEN"
    }
} catch {
    $btnOpen.Text = "OPEN"
}
$btnOpen.Location = New-Object System.Drawing.Point(425, 90)
$btnOpen.Size = New-Object System.Drawing.Size(40, 50)
$btnOpen.FlatStyle = "Flat"
$btnOpen.BackColor = $ColorButton
$btnOpen.ForeColor = $ColorText
$btnOpen.Font = New-Object System.Drawing.Font("Segoe UI", 14)
$btnOpen.Add_Click({ Open-BuildFolder })
$form.Controls.Add($btnOpen)

$btnClean = New-Object System.Windows.Forms.Button
$btnClean.Text = "Clean Project"
$btnClean.Location = New-Object System.Drawing.Point(25, 150)
$btnClean.Size = New-Object System.Drawing.Size(120, 30)
$btnClean.FlatStyle = "Flat"
$btnClean.BackColor = $ColorButton
$btnClean.ForeColor = $ColorError
$btnClean.Add_Click({ Start-BackgroundTask { Clean-Project } })
$form.Controls.Add($btnClean)

$btnLore = New-Object System.Windows.Forms.Button
$btnLore.Text = "Export Lore Pack"
$btnLore.Location = New-Object System.Drawing.Point(160, 150)
$btnLore.Size = New-Object System.Drawing.Size(200, 30)
$btnLore.FlatStyle = "Flat"
$btnLore.BackColor = $ColorButton
$btnLore.ForeColor = $ColorAccent
$btnLore.Font = New-Object System.Drawing.Font("Segoe UI", 9)
$btnLore.Add_Click({ Start-BackgroundTask { Export-LorePack } })
$form.Controls.Add($btnLore)

# Progress
$lblProgress = New-Object System.Windows.Forms.Label
$lblProgress.Text = "Ready."
$lblProgress.Font = New-Object System.Drawing.Font("Segoe UI", 9)
$lblProgress.ForeColor = $ColorText
$lblProgress.AutoSize = $true
$lblProgress.Location = New-Object System.Drawing.Point(25, 185)
$form.Controls.Add($lblProgress)

$progressBar = New-Object System.Windows.Forms.ProgressBar
$progressBar.Location = New-Object System.Drawing.Point(25, 205)
$progressBar.Size = New-Object System.Drawing.Size(435, 14)
$progressBar.Style = "Blocks"
$progressBar.Value = 0
$form.Controls.Add($progressBar)

# Output Box
$txtOutput = New-Object System.Windows.Forms.TextBox
$txtOutput.Multiline = $true
$txtOutput.ReadOnly = $true
$txtOutput.ScrollBars = "Vertical"
$txtOutput.Location = New-Object System.Drawing.Point(25, 230)
$txtOutput.Size = New-Object System.Drawing.Size(435, 170)
$txtOutput.BackColor = $ColorPanel
$txtOutput.ForeColor = $ColorText
$txtOutput.Font = New-Object System.Drawing.Font("Consolas", 9)
$txtOutput.BorderStyle = "FixedSingle"
$form.Controls.Add($txtOutput)

Log "Welcome. Ready to build."

[void]$form.ShowDialog()
