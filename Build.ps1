# LatticeVeil Builder - WPF GUI with worker process
# Runs build tasks in a separate PowerShell process to avoid UI hangs.

param(
    [switch]$Worker,
    [ValidateSet('dev-launcher','dev-direct','release','cleanup','export-bundle')]
    [string]$Task,
    [string]$SolutionRoot,
    [string]$LogFile,
    [switch]$DeepClean,
    [switch]$CleanCache
)

if (-not $SolutionRoot) { $SolutionRoot = $PSScriptRoot }
$GameRepoRoot = Join-Path $SolutionRoot 'LatticeVeilMonoGame'
$BuilderDir = Join-Path $SolutionRoot '.builder'
$LogsDir = Join-Path $BuilderDir 'logs'
$StagingDir = Join-Path $BuilderDir 'staging'
$ReleasesDir = Join-Path $BuilderDir 'releases'
$ExportOutRoot = 'C:\Users\Redacted\Documents\LatticeVeil_project_export'
$ThisScript = $PSCommandPath
if (-not $ThisScript) { $ThisScript = $MyInvocation.MyCommand.Path }

function Ensure-Dir {
    param([string]$Path)
    if (-not (Test-Path $Path)) { New-Item -ItemType Directory -Path $Path -Force | Out-Null }
}

function Quote-Arg {
    param([string]$Value)
    if ([string]::IsNullOrEmpty($Value)) { return '""' }
    if ($Value -match '[\s"]') {
        return '"' + $Value.Replace('"','""') + '"'
    }
    return $Value
}

function Get-BuilderLogPath { return (Join-Path $LogsDir 'builder_latest.log') }
function Get-CleanupLogPath { return (Join-Path $LogsDir 'cleanup_latest.log') }
function Get-ExportLogPath { return (Join-Path $LogsDir 'export_latest.log') }

function Write-WorkerLog {
    param([string]$LogFile, [string]$Message)
    $ts = Get-Date -Format 'MM/dd/yyyy HH:mm:ss'
    $line = "[$ts] $Message"
    $maxAttempts = 10
    for ($i = 0; $i -lt $maxAttempts; $i++) {
        try {
            $logDir = Split-Path $LogFile -Parent
            if (-not (Test-Path $logDir)) { New-Item -ItemType Directory -Path $logDir -Force | Out-Null }
            $fs = [System.IO.File]::Open($LogFile, [System.IO.FileMode]::Append, [System.IO.FileAccess]::Write, [System.IO.FileShare]::ReadWrite)
            $sw = New-Object System.IO.StreamWriter($fs, [System.Text.Encoding]::ASCII)
            $sw.WriteLine($line)
            $sw.Flush()
            $sw.Dispose()
            $fs.Dispose()
            return
        } catch {
            if ($i -ge ($maxAttempts - 1)) { return }
            Start-Sleep -Milliseconds 40
        }
    }
}

function Invoke-CommandLogged {
    param(
        [Parameter(Mandatory=$true)][string]$Executable,
        [string[]]$Arguments = @(),
        [int]$TimeoutSeconds = 900,
        [string]$WorkingDirectory = "",
        [Parameter(Mandatory=$true)][string]$LogFile
    )

    $argsText = ($Arguments -join ' ')
    Write-WorkerLog -LogFile $LogFile -Message ("CMD: {0} {1}" -f $Executable, $argsText)

    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = $Executable
    $psi.Arguments = $argsText
    $psi.UseShellExecute = $false
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    $psi.CreateNoWindow = $true
    if (-not [string]::IsNullOrWhiteSpace($WorkingDirectory)) {
        $psi.WorkingDirectory = $WorkingDirectory
    }

    $p = New-Object System.Diagnostics.Process
    $p.StartInfo = $psi

    try {
        $null = $p.Start()
    } catch {
        Write-WorkerLog -LogFile $LogFile -Message ("ERROR: Failed to start process: {0}" -f $_.Exception.Message)
        return @{ Success = $false; ExitCode = -1; TimedOut = $false }
    }

    $stdout = $p.StandardOutput
    $stderr = $p.StandardError

    $stdoutTask = $stdout.ReadLineAsync()
    $stderrTask = $stderr.ReadLineAsync()
    $stdoutDone = $false
    $stderrDone = $false

    $start = Get-Date
    while ($true) {
        $didRead = $false
        do {
            $progress = $false

            if (-not $stdoutDone) {
                if ($stdoutTask.IsCanceled -or $stdoutTask.IsFaulted) {
                    $stdoutDone = $true
                } elseif ($stdoutTask.IsCompleted) {
                    $line = $stdoutTask.Result
                    if ($null -ne $line) {
                        if ($line.Length -gt 0) { Write-WorkerLog -LogFile $LogFile -Message $line }
                    } else {
                        $stdoutDone = $true
                    }
                    if (-not $stdoutDone) { $stdoutTask = $stdout.ReadLineAsync() }
                    $progress = $true
                    $didRead = $true
                }
            }

            if (-not $stderrDone) {
                if ($stderrTask.IsCanceled -or $stderrTask.IsFaulted) {
                    $stderrDone = $true
                } elseif ($stderrTask.IsCompleted) {
                    $line = $stderrTask.Result
                    if ($null -ne $line) {
                        if ($line.Length -gt 0) { Write-WorkerLog -LogFile $LogFile -Message $line }
                    } else {
                        $stderrDone = $true
                    }
                    if (-not $stderrDone) { $stderrTask = $stderr.ReadLineAsync() }
                    $progress = $true
                    $didRead = $true
                }
            }
        } while ($progress)

        if ($p.HasExited -and $stdoutDone -and $stderrDone) { break }

        if ($TimeoutSeconds -gt 0) {
            $elapsed = (Get-Date) - $start
            if ($elapsed.TotalSeconds -ge $TimeoutSeconds) {
                try { $p.Kill() } catch {}
                try { cmd /c "taskkill /T /F /PID $($p.Id) >nul 2>&1" | Out-Null } catch {}
                Write-WorkerLog -LogFile $LogFile -Message ("PROCESS TIMEOUT after {0}s" -f $TimeoutSeconds)
                return @{ Success = $false; ExitCode = -1; TimedOut = $true }
            }
        }

        if (-not $didRead) { Start-Sleep -Milliseconds 50 }
    }

    $exitCode = $p.ExitCode
    Write-WorkerLog -LogFile $LogFile -Message ("ExitCode: {0}" -f $exitCode)

    return @{ Success = ($exitCode -eq 0); ExitCode = $exitCode; TimedOut = $false }
}

function Resolve-GameOutput {
    param(
        [string]$Configuration = 'Debug',
        [string]$TargetFramework = 'net8.0-windows'
    )

    $base = Join-Path $GameRepoRoot ("bin\\{0}\\{1}" -f $Configuration, $TargetFramework)
    $exeCandidates = @('LatticeVeil.exe','LatticeVeilGame.exe','LatticeVeilMonoGame.exe')
    $dllCandidates = @('LatticeVeilGame.dll','LatticeVeilMonoGame.dll')

    foreach ($n in $exeCandidates) {
        $p = Join-Path $base $n
        if (Test-Path $p) { return @{ Kind = 'exe'; Path = $p } }
    }

    if (Test-Path $base) {
        $found = Get-ChildItem -Path $base -Recurse -File -ErrorAction SilentlyContinue |
            Where-Object { $exeCandidates -contains $_.Name } |
            Sort-Object @{Expression='LastWriteTime';Descending=$true} |
            Select-Object -First 1
        if ($found) { return @{ Kind = 'exe'; Path = $found.FullName } }

        $foundDll = Get-ChildItem -Path $base -Recurse -File -ErrorAction SilentlyContinue |
            Where-Object { $dllCandidates -contains $_.Name } |
            Sort-Object @{Expression='LastWriteTime';Descending=$true} |
            Select-Object -First 1
        if ($foundDll) { return @{ Kind = 'dll'; Path = $foundDll.FullName } }
    }

    return $null
}

function Resolve-GameBuildTarget {
    param(
        [Parameter(Mandatory=$true)][string]$GameRepoRoot,
        [Parameter(Mandatory=$false)][string]$LogFile = ''
    )

    # Prefer the known game project name first.
    $preferred = Join-Path $GameRepoRoot 'LatticeVeilMonoGame.csproj'
    if (Test-Path $preferred) { return $preferred }

    # If a solution exists, build that.
    $sln = Get-ChildItem -Path $GameRepoRoot -Filter *.sln -File -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($sln) { return $sln.FullName }

    # Otherwise, pick a single csproj if there is only one.
    $csprojs = Get-ChildItem -Path $GameRepoRoot -Filter *.csproj -File -ErrorAction SilentlyContinue
    if ($csprojs.Count -eq 1) { return $csprojs[0].FullName }

    # Heuristic: prefer something containing "LatticeVeil".
    $best = $csprojs | Where-Object { $_.Name -match 'LatticeVeil' } | Sort-Object Name | Select-Object -First 1
    if ($best) { return $best.FullName }

    if ($csprojs.Count -gt 0) { return $csprojs[0].FullName }

    throw "Could not resolve a build target (.csproj or .sln) under: $GameRepoRoot"
}


function Start-GameMode {
    param(
        [Parameter(Mandatory=$true)][ValidateSet('launcher','game')][string]$Mode,
        [string]$Configuration = 'Debug'
    )

    $out = Resolve-GameOutput -Configuration $Configuration
    if (-not $out) {
        throw "Game output not found (.exe or .dll) under: $GameRepoRoot\\bin\\$Configuration\\net8.0-windows"
    }

    $args = @()
    if ($Mode -eq 'launcher') { $args = @('--launcher') }
    if ($Mode -eq 'game') { $args = @('--game') }

    $wd = Split-Path -Parent $out.Path

    if ($out.Kind -eq 'exe') {
        Start-Process -FilePath $out.Path -ArgumentList $args -WorkingDirectory $wd
        return
    }

    Start-Process -FilePath 'dotnet' -ArgumentList @($out.Path) + $args -WorkingDirectory $wd
}

function Invoke-WorkerTask {
    param(
        [string]$Task,
        [string]$SolutionRoot,
        [string]$GameRepoRoot,
        [string]$LogsDir,
        [string]$StagingDir,
        [string]$ReleasesDir,
        [string]$LogFile,
        [switch]$DeepClean,
        [switch]$CleanCache
    )

    $ErrorActionPreference = 'Stop'

    Ensure-Dir $LogsDir
    Ensure-Dir (Split-Path $LogFile -Parent)
    if (-not (Test-Path $LogFile)) { New-Item -ItemType File -Path $LogFile -Force | Out-Null }

    $BuildTarget = Resolve-GameBuildTarget -GameRepoRoot $GameRepoRoot -LogFile $LogFile
    Write-WorkerLog -LogFile $LogFile -Message ("Build target: {0}" -f $BuildTarget)

    try {
        switch ($Task) {
            'dev-launcher' {
                $env:LATTICEVEIL_DEV_LOCAL = '1'
                $env:LATTICEVEIL_LOCAL_ASSETS = '1'

                Write-WorkerLog -LogFile $LogFile -Message 'Starting DEV BUILD + RUN (Launcher) in worker...'
                Write-WorkerLog -LogFile $LogFile -Message 'Killing existing processes...'
                cmd /c "taskkill /IM LatticeVeil.exe /F >nul 2>&1" | Out-Null
                cmd /c "taskkill /IM LatticeVeilGame.exe /F >nul 2>&1" | Out-Null
                Start-Sleep -Seconds 1

                Write-WorkerLog -LogFile $LogFile -Message 'Restoring packages...'
                $restore = Invoke-CommandLogged -Executable 'dotnet' -Arguments @('restore',$BuildTarget,'--runtime','win-x64') -TimeoutSeconds 900 -WorkingDirectory $GameRepoRoot -LogFile $LogFile
                if (-not $restore.Success) { throw 'Restore failed.' }
                Write-WorkerLog -LogFile $LogFile -Message 'Restore completed'

                Write-WorkerLog -LogFile $LogFile -Message 'Building game...'
                $build = Invoke-CommandLogged -Executable 'dotnet' -Arguments @('build',$BuildTarget,'--configuration','Debug','--no-restore') -TimeoutSeconds 900 -WorkingDirectory $GameRepoRoot -LogFile $LogFile
                if (-not $build.Success) { throw 'Build failed.' }
                Write-WorkerLog -LogFile $LogFile -Message 'Build completed successfully'

                Write-WorkerLog -LogFile $LogFile -Message 'Starting game with launcher...'
                Start-GameMode -Mode 'launcher' -Configuration 'Debug'
                Write-WorkerLog -LogFile $LogFile -Message 'Game started with launcher'
            }
            'dev-direct' {
                $env:LATTICEVEIL_DEV_LOCAL = '1'
                $env:LATTICEVEIL_LOCAL_ASSETS = '1'

                Write-WorkerLog -LogFile $LogFile -Message 'Starting DEV BUILD + RUN (No Launcher) in worker...'
                Write-WorkerLog -LogFile $LogFile -Message 'Killing existing processes...'
                cmd /c "taskkill /IM LatticeVeil.exe /F >nul 2>&1" | Out-Null
                cmd /c "taskkill /IM LatticeVeilGame.exe /F >nul 2>&1" | Out-Null
                Start-Sleep -Seconds 1

                Write-WorkerLog -LogFile $LogFile -Message 'Restoring packages...'
                $restore = Invoke-CommandLogged -Executable 'dotnet' -Arguments @('restore',$BuildTarget,'--runtime','win-x64') -TimeoutSeconds 900 -WorkingDirectory $GameRepoRoot -LogFile $LogFile
                if (-not $restore.Success) { throw 'Restore failed.' }
                Write-WorkerLog -LogFile $LogFile -Message 'Restore completed'

                Write-WorkerLog -LogFile $LogFile -Message 'Building game...'
                $build = Invoke-CommandLogged -Executable 'dotnet' -Arguments @('build',$BuildTarget,'--configuration','Debug','--no-restore') -TimeoutSeconds 900 -WorkingDirectory $GameRepoRoot -LogFile $LogFile
                if (-not $build.Success) { throw 'Build failed.' }
                Write-WorkerLog -LogFile $LogFile -Message 'Build completed successfully'

                Write-WorkerLog -LogFile $LogFile -Message 'Starting game directly...'
                Start-GameMode -Mode 'game' -Configuration 'Debug'
                Write-WorkerLog -LogFile $LogFile -Message 'Game started directly'
            }
            'release' {
                Write-WorkerLog -LogFile $LogFile -Message 'Starting RELEASE PUBLISH + PACKAGE in worker...'

                $timestamp = Get-Date -Format 'yyyyMMdd_HHmmss'
                $releaseStaging = Join-Path $StagingDir "Release_$timestamp"
                Ensure-Dir $releaseStaging
                Write-WorkerLog -LogFile $LogFile -Message "Staging to: $releaseStaging"

                Write-WorkerLog -LogFile $LogFile -Message 'Restoring packages...'
                $restore = Invoke-CommandLogged -Executable 'dotnet' -Arguments @('restore',$BuildTarget,'--runtime','win-x64') -TimeoutSeconds 900 -WorkingDirectory $GameRepoRoot -LogFile $LogFile
                if (-not $restore.Success) { throw 'Restore failed.' }
                Write-WorkerLog -LogFile $LogFile -Message 'Restore completed'

                Write-WorkerLog -LogFile $LogFile -Message 'Building release configuration...'
                $publishArgs = @(
                    'publish',
                    $BuildTarget,
                    '--configuration','Release',
                    '--runtime','win-x64',
                    '--no-restore',
                    '-p:PublishSingleFile=true',
                    '-p:SelfContained=true',
                    '-p:IncludeNativeLibrariesForSelfExtract=true',
                    '-p:IncludeAllContentForSelfExtract=true',
                    '-p:DebugType=None',
                    '-p:DebugSymbols=false'
                )
                $publish = Invoke-CommandLogged -Executable 'dotnet' -Arguments $publishArgs -TimeoutSeconds 1200 -WorkingDirectory $GameRepoRoot -LogFile $LogFile
                if (-not $publish.Success) { throw 'Publish failed.' }
                Write-WorkerLog -LogFile $LogFile -Message 'Release publish completed'

                Write-WorkerLog -LogFile $LogFile -Message 'Copying single-file executable to staging...'
                $publishDir = "$GameRepoRoot\\bin\\Release\\net8.0-windows\\win-x64\\publish"
                $publishExe = Join-Path $publishDir 'LatticeVeilGame.exe'
                if (-not (Test-Path $publishExe)) { throw 'Published executable not found.' }
                Copy-Item $publishExe "$releaseStaging\\" -Force

                Write-WorkerLog -LogFile $LogFile -Message 'Creating release package...'
                Ensure-Dir $ReleasesDir
                $zipName = "LatticeVeil_Release_$timestamp.zip"
                $zipPath = Join-Path $ReleasesDir $zipName
                if (Test-Path $zipPath) { Remove-Item -Path $zipPath -Force }
                Compress-Archive -Path (Join-Path $releaseStaging '*') -DestinationPath $zipPath -Force
                Write-WorkerLog -LogFile $LogFile -Message "Release package created: $zipPath"
            }
            'cleanup' {
                Write-WorkerLog -LogFile $LogFile -Message 'Starting cleanup in worker...'
                Write-WorkerLog -LogFile $LogFile -Message 'Level 1 cleanup (safe)...'

                if (Test-Path $StagingDir) {
                    Remove-Item "$StagingDir\\*" -Recurse -Force -ErrorAction SilentlyContinue
                    Write-WorkerLog -LogFile $LogFile -Message 'Cleaned staging directory'
                } else {
                    Ensure-Dir $StagingDir
                }

                if (Test-Path $ReleasesDir) {
                    Remove-Item "$ReleasesDir\\*.zip" -Force -ErrorAction SilentlyContinue
                    Write-WorkerLog -LogFile $LogFile -Message 'Cleaned release packages'
                } else {
                    Ensure-Dir $ReleasesDir
                }

                $cutoffDate = (Get-Date).AddDays(-7)
                Get-ChildItem "$LogsDir\\*.log" -ErrorAction SilentlyContinue |
                    Where-Object { $_.CreationTime -lt $cutoffDate } |
                    Remove-Item -Force -ErrorAction SilentlyContinue
                Write-WorkerLog -LogFile $LogFile -Message 'Cleaned old log files'

                if ($DeepClean) {
                    Write-WorkerLog -LogFile $LogFile -Message 'Level 2 cleanup (deep)...'
                    $cmd = 'for /d /r "' + $SolutionRoot + '" %D in (bin obj .vs TestResults) do @if exist "%D" rmdir /s /q "%D"'
                    cmd /c $cmd | Out-Null
                    Write-WorkerLog -LogFile $LogFile -Message 'Cleaned bin/obj/.vs/TestResults folders'

                    if ($CleanCache) {
                        Write-WorkerLog -LogFile $LogFile -Message 'Cleaning NuGet caches (folder delete)...'
                        $paths = @(
                            (Join-Path $env:USERPROFILE '.nuget\\http-cache'),
                            (Join-Path $env:USERPROFILE '.nuget\\plugins-cache'),
                            (Join-Path $env:LOCALAPPDATA 'NuGet\\v3-cache'),
                            (Join-Path $env:LOCALAPPDATA 'NuGet\\plugins-cache'),
                            (Join-Path $env:LOCALAPPDATA 'NuGet\\Cache'),
                            (Join-Path $env:LOCALAPPDATA 'NuGet\\Scratch')
                        ) | Select-Object -Unique

                        foreach ($p in $paths) {
                            if ([string]::IsNullOrWhiteSpace($p)) { continue }
                            if (Test-Path $p) {
                                Write-WorkerLog -LogFile $LogFile -Message "Deleting: $p"
                                try { cmd /c "rmdir /s /q \"$p\" >nul 2>&1" | Out-Null } catch {}
                            }
                        }

                        Write-WorkerLog -LogFile $LogFile -Message 'NuGet cache cleanup done. (global-packages not cleared)'
                    }
                }

                Write-WorkerLog -LogFile $LogFile -Message 'Cleanup completed successfully'
            }
            'export-bundle' {
                Write-WorkerLog -LogFile $LogFile -Message 'Starting export bundle...'

                $timestamp = Get-Date -Format 'yyyyMMdd_HHmm'
                $outputRoot = $ExportOutRoot
                Ensure-Dir $outputRoot

                $stageRoot = Join-Path $outputRoot ("{0}_ReviewBundleStaging\\ReviewBundle" -f $timestamp)
                $gameDest = Join-Path $stageRoot 'GAME'
                $webDest = Join-Path $stageRoot 'WEBSITE'
                $diagDest = Join-Path $stageRoot 'DIAGNOSTICS'
                Ensure-Dir $gameDest
                Ensure-Dir $webDest
                Ensure-Dir $diagDest

                $websiteRepoRoot = Join-Path (Split-Path $SolutionRoot -Parent) 'latticeveil.github.io'
                if (-not (Test-Path $websiteRepoRoot)) {
                    $websiteRepoRoot = 'C:\Users\Redacted\Documents\latticeveil.github.io'
                }
                if (-not (Test-Path $websiteRepoRoot)) {
                    throw "Website repo not found: $websiteRepoRoot"
                }

                $excludeDirs = @(
                    'bin','obj','.git','.vs','.idea','node_modules','dist','build','out','packages',
                    'TestResults','.builder\\staging','.builder\\cache','.builder\\releases','.plans'
                )

                Write-WorkerLog -LogFile $LogFile -Message 'Copying GAME repo...'
                & robocopy $SolutionRoot $gameDest /E /XD $excludeDirs | Out-Null
                if ($LASTEXITCODE -ge 8) { throw "Robocopy failed for GAME (code $LASTEXITCODE)" }

                Write-WorkerLog -LogFile $LogFile -Message 'Copying WEBSITE repo...'
                & robocopy $websiteRepoRoot $webDest /E /XD $excludeDirs | Out-Null
                if ($LASTEXITCODE -ge 8) { throw "Robocopy failed for WEBSITE (code $LASTEXITCODE)" }

                Write-WorkerLog -LogFile $LogFile -Message 'Creating diagnostics...'
                cmd /c "tree /A /F `"$gameDest`" > `"$diagDest\\game_tree.txt`""
                cmd /c "tree /A /F `"$webDest`" > `"$diagDest\\website_tree.txt`""

                Get-ChildItem -Path $gameDest -Recurse -File -Force |
                    Sort-Object Length -Descending |
                    Select-Object -First 50 FullName,Length |
                    Out-File -FilePath "$diagDest\\game_top_files_by_size.txt" -Encoding ASCII

                Get-ChildItem -Path $webDest -Recurse -File -Force |
                    Sort-Object Length -Descending |
                    Select-Object -First 50 FullName,Length |
                    Out-File -FilePath "$diagDest\\website_top_files_by_size.txt" -Encoding ASCII

                "== git status ==" | Out-File -FilePath "$diagDest\\git_info_game.txt" -Encoding ASCII
                (git -C $SolutionRoot status --short) | Out-File -FilePath "$diagDest\\git_info_game.txt" -Append -Encoding ASCII
                "" | Out-File -FilePath "$diagDest\\git_info_game.txt" -Append -Encoding ASCII
                "== git remote -v ==" | Out-File -FilePath "$diagDest\\git_info_game.txt" -Append -Encoding ASCII
                (git -C $SolutionRoot remote -v) | Out-File -FilePath "$diagDest\\git_info_game.txt" -Append -Encoding ASCII
                "" | Out-File -FilePath "$diagDest\\git_info_game.txt" -Append -Encoding ASCII
                "== git log -n 30 --oneline ==" | Out-File -FilePath "$diagDest\\git_info_game.txt" -Append -Encoding ASCII
                (git -C $SolutionRoot log -n 30 --oneline) | Out-File -FilePath "$diagDest\\git_info_game.txt" -Append -Encoding ASCII

                if (Test-Path (Join-Path $websiteRepoRoot '.git')) {
                    "== git status ==" | Out-File -FilePath "$diagDest\\git_info_website.txt" -Encoding ASCII
                    (git -C $websiteRepoRoot status --short) | Out-File -FilePath "$diagDest\\git_info_website.txt" -Append -Encoding ASCII
                    "" | Out-File -FilePath "$diagDest\\git_info_website.txt" -Append -Encoding ASCII
                    "== git remote -v ==" | Out-File -FilePath "$diagDest\\git_info_website.txt" -Append -Encoding ASCII
                    (git -C $websiteRepoRoot remote -v) | Out-File -FilePath "$diagDest\\git_info_website.txt" -Append -Encoding ASCII
                    "" | Out-File -FilePath "$diagDest\\git_info_website.txt" -Append -Encoding ASCII
                    "== git log -n 30 --oneline ==" | Out-File -FilePath "$diagDest\\git_info_website.txt" -Append -Encoding ASCII
                    (git -C $websiteRepoRoot log -n 30 --oneline) | Out-File -FilePath "$diagDest\\git_info_website.txt" -Append -Encoding ASCII
                }

                $pattern = 'REDACTEDCRAFT|RedactedCraft|Redactedcraft|Redcraft|RedcraftMonoGame|RedactedcraftCsharp|Documents\\\\RedactedCraft|Documents/RedactedCraft|REDACTEDCRAFT_LOCAL_ASSETS'
                $searchOut = "$diagDest\\search_old_name_tokens.txt"
                if (Get-Command rg -ErrorAction SilentlyContinue) {
                    $matches = & rg -n -S $pattern $gameDest $webDest 2>$null
                    if ($LASTEXITCODE -ne 0) { $matches = @() }
                    if ($matches.Count -eq 0) { "No matches found." | Out-File -FilePath $searchOut -Encoding ASCII }
                    else { $matches | Out-File -FilePath $searchOut -Encoding ASCII }
                } else {
                    $matches = Get-ChildItem -Path @($gameDest, $webDest) -Recurse -File -Force |
                        Select-String -Pattern $pattern -CaseSensitive
                    if ($matches) { $matches | Out-File -FilePath $searchOut -Encoding ASCII }
                    else { "No matches found." | Out-File -FilePath $searchOut -Encoding ASCII }
                }

                $readme = @"
LatticeVeil context bundle

Contents
- GAME: snapshot of the game repo (build/cache folders excluded)
- WEBSITE: snapshot of the website repo (build/cache folders excluded)
- DIAGNOSTICS: tree, largest files, git info, token scan

Build/run
- Builder GUI: powershell -ExecutionPolicy Bypass -File Build.ps1
- Dev assets: LatticeVeilMonoGame/Defaults/Assets
- Release assets: Documents\\LatticeVeil\\Assets

Notes
- RELEASE_NOTES.txt
"@
                $readme | Out-File -FilePath "$stageRoot\\README_CONTEXT.md" -Encoding ASCII

                Write-WorkerLog -LogFile $LogFile -Message 'Creating bundle zip...'
                $zipName = "LatticeVeil_ReviewBundle_$timestamp.zip"
                $zipPath = Join-Path $outputRoot $zipName
                if (Test-Path $zipPath) { Remove-Item -Path $zipPath -Force }
                Compress-Archive -Path $stageRoot -DestinationPath $zipPath -Force

                Write-WorkerLog -LogFile $LogFile -Message "Export bundle created: $zipPath"
            }
            default {
                throw "Unknown task: $Task"
            }
        }

        exit 0
    } catch {
        Write-WorkerLog -LogFile $LogFile -Message ("ERROR: {0}" -f $_.Exception.Message)
        exit 1
    }
}

if ($Worker) {
    Invoke-WorkerTask -Task $Task -SolutionRoot $SolutionRoot -GameRepoRoot $GameRepoRoot -LogsDir $LogsDir -StagingDir $StagingDir -ReleasesDir $ReleasesDir -LogFile $LogFile -DeepClean:$DeepClean -CleanCache:$CleanCache
    exit
}

# UI mode
Add-Type -AssemblyName PresentationFramework
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing

Ensure-Dir $LogsDir
Ensure-Dir $StagingDir
Ensure-Dir $ReleasesDir

$script:UiTimer = $null
$script:ActiveLogFile = $null
$script:ActiveProcess = $null

function Get-LastLogLines {
    param([string]$LogFile, [int]$Lines = 200, [int]$MaxBytes = 1048576)
    if ([string]::IsNullOrEmpty($LogFile)) { return '' }
    if (-not (Test-Path $LogFile)) { return '' }
    try {
        $fs = [System.IO.File]::Open($LogFile, [System.IO.FileMode]::Open, [System.IO.FileAccess]::Read, [System.IO.FileShare]::ReadWrite)
        if ($fs.Length -eq 0) { $fs.Dispose(); return '' }
        $readBytes = [Math]::Min($fs.Length, $MaxBytes)
        $fs.Seek(-1 * $readBytes, [System.IO.SeekOrigin]::End) | Out-Null
        $buffer = New-Object byte[] $readBytes
        $null = $fs.Read($buffer, 0, $readBytes)
        $fs.Dispose()
        $text = [System.Text.Encoding]::UTF8.GetString($buffer)
        if ([string]::IsNullOrEmpty($text)) { return '' }
        $linesArr = $text -split "`r?`n"
        if ($linesArr.Length -le $Lines) { return ($linesArr -join "`n") }
        return ($linesArr[-$Lines..-1] -join "`n")
    } catch {
        return ''
    }
}

function Get-ProgressFromLogText {
    param([string]$Text, [int]$CurrentValue = 0)

    $progress = $CurrentValue
    $milestones = @(
        @{ Pattern = 'Starting (DEV BUILD \+ RUN|RELEASE PUBLISH \+ PACKAGE|cleanup|export bundle)'; Value = 5 },
        @{ Pattern = 'Killing existing processes'; Value = 10 },
        @{ Pattern = 'Restoring packages'; Value = 35 },
        @{ Pattern = 'Building game|Building release configuration'; Value = 60 },
        @{ Pattern = 'Release publish completed|Copying single-file executable|Copying published output|Copying assets|Creating release package|Copying GAME repo|Copying WEBSITE repo|Creating diagnostics'; Value = 80 },
        @{ Pattern = 'Creating bundle zip'; Value = 90 },
        @{ Pattern = 'Starting game'; Value = 90 },
        @{ Pattern = 'Game started|Release package created|Cleanup completed successfully|Export bundle created'; Value = 100 }
    )

    foreach ($m in $milestones) {
        if ($Text -match $m.Pattern) {
            if ($progress -lt $m.Value) { $progress = $m.Value }
        }
    }

    if ($progress -lt 0) { $progress = 0 }
    if ($progress -gt 100) { $progress = 100 }
    return $progress
}

function Update-UiFromLog {
    $logFile = $script:ActiveLogFile
    if ([string]::IsNullOrWhiteSpace($logFile)) { return }

    $text = Get-LastLogLines -LogFile $logFile -Lines 200
    if (-not [string]::IsNullOrEmpty($text)) {
        $logText.Text = $text
        $logText.ScrollToEnd()
        $next = Get-ProgressFromLogText -Text $text -CurrentValue $progressBar.Value
        if ($next -gt $progressBar.Value) { $progressBar.Value = $next }
    } else {
        if ($progressBar.Value -lt 1) {
            $progressBar.Value = 1
        } elseif ($progressBar.Value -ge 3) {
            $progressBar.Value = 1
        } else {
            $progressBar.Value = [Math]::Min($progressBar.Value + 1, 3)
        }
    }
}

function Stop-UiLiveUpdates {
    if ($script:UiTimer) {
        try { $script:UiTimer.Stop() } catch {}
        $script:UiTimer = $null
    }
}

function Complete-ActiveProcess {
    param([System.Diagnostics.Process]$Process)

    $exitCode = 1
    try { $exitCode = $Process.ExitCode } catch {}
    $script:ActiveProcess = $null

    if ($exitCode -eq 0) {
        Set-UIState -Enabled $true -Status 'Success' -UiAction {
            $logText.Text = Get-LastLogLines -LogFile $script:ActiveLogFile -Lines 200
            $logText.ScrollToEnd()
        }
    } else {
        Set-UIState -Enabled $true -Status 'Failed' -UiAction {
            $tail = Get-LastLogLines -LogFile $script:ActiveLogFile -Lines 60
            $logText.Text = "FAILED: ExitCode $exitCode`n`n$tail"
            $logText.ScrollToEnd()
        }
    }
}

function Check-ActiveProcessCompletion {
    if (-not $script:ActiveProcess) { return $false }

    $exited = $false
    try { $exited = $script:ActiveProcess.HasExited } catch { $exited = $true }
    if ($exited) {
        Complete-ActiveProcess -Process $script:ActiveProcess
        return $true
    }

    return $false
}

function Start-UiLiveUpdates {
    if ($script:UiTimer) { return }

    $script:UiTimer = New-Object Windows.Threading.DispatcherTimer
    $script:UiTimer.Interval = [TimeSpan]::FromMilliseconds(400)
    $script:UiTimer.Add_Tick({
        try {
            if (Check-ActiveProcessCompletion) { return }
            Update-UiFromLog
        } catch {
        }
    })
    $script:UiTimer.Start()
}

function Set-UIState {
    param([bool]$Enabled, [string]$Status = 'Idle', [scriptblock]$UiAction = {})

    $btnDevLauncher.IsEnabled = $Enabled
    $btnDevDirect.IsEnabled = $Enabled
    $btnRelease.IsEnabled = $Enabled
    $btnCleanup.IsEnabled = $Enabled
    $btnOpenDev.IsEnabled = $Enabled
    $btnOpenRelease.IsEnabled = $Enabled
    $btnOpenLogs.IsEnabled = $Enabled
    $btnExportBundle.IsEnabled = $Enabled
    $refreshBtn.IsEnabled = $Enabled
    $clearBtn.IsEnabled = $Enabled
    $cbDeepClean.IsEnabled = $Enabled
    $cbCleanCache.IsEnabled = $Enabled

    $lblStatus.Content = "Status: $Status"
    $progressBar.IsIndeterminate = $false
    $progressBar.Maximum = 100

    if ($Status -eq 'Running') {
        Start-UiLiveUpdates
        if ($progressBar.Value -lt 1) { $progressBar.Value = 1 }
    } else {
        Stop-UiLiveUpdates
        switch ($Status) {
            'Idle'    { $progressBar.Value = 0 }
            'Success' { $progressBar.Value = 100 }
            'Failed'  { if ($progressBar.Value -lt 1) { $progressBar.Value = 0 } }
            default   { }
        }
    }

    if ($null -ne $window -and $null -ne $window.Dispatcher) {
        if ($window.Dispatcher.CheckAccess()) {
            & $UiAction
        } else {
            $window.Dispatcher.Invoke($UiAction, 'Normal')
            $window.Dispatcher.Invoke([action]{}, 'Normal')
        }
    }
}

function Update-LogDisplay {
    try {
        $logFile = $script:ActiveLogFile
        if ([string]::IsNullOrWhiteSpace($logFile)) { $logFile = Get-BuilderLogPath }
        $logText.Text = Get-LastLogLines -LogFile $logFile -Lines 200
        $logText.ScrollToEnd()
    } catch {
        $logText.Text = "Error updating log display: $_"
    }
}

function Start-WorkerProcess {
    param(
        [Parameter(Mandatory=$true)][string]$Task,
        [Parameter(Mandatory=$true)][string]$LogFile,
        [switch]$DeepClean,
        [switch]$CleanCache
    )

    if ($script:ActiveProcess) {
        try { if (-not $script:ActiveProcess.HasExited) { return } } catch { }
    }

    Ensure-Dir (Split-Path $LogFile -Parent)
    Set-Content -Path $LogFile -Value '' -Encoding ASCII
    $script:ActiveLogFile = $LogFile

    $argList = @(
        '-NoProfile',
        '-ExecutionPolicy', 'Bypass',
        '-File', (Quote-Arg $ThisScript),
        '-Worker',
        '-Task', $Task,
        '-SolutionRoot', (Quote-Arg $SolutionRoot),
        '-LogFile', (Quote-Arg $LogFile)
    )

    if ($DeepClean) { $argList += '-DeepClean' }
    if ($CleanCache) { $argList += '-CleanCache' }

    $argString = ($argList | Where-Object { $_ -ne '' }) -join ' '

    try {
        $psExe = Join-Path $env:WINDIR 'System32\\WindowsPowerShell\\v1.0\\powershell.exe'
        $script:ActiveProcess = Start-Process -FilePath $psExe -ArgumentList $argString -WorkingDirectory $SolutionRoot -WindowStyle Hidden -PassThru
    } catch {
        Set-UIState -Enabled $true -Status 'Failed' -UiAction {
            $logText.Text = "FAILED: $_"
            $logText.ScrollToEnd()
        }
        return
    }

    Set-UIState -Enabled $false -Status 'Running'
}

function Invoke-DevBuildLauncher {
    Start-WorkerProcess -Task 'dev-launcher' -LogFile (Get-BuilderLogPath)
}

function Invoke-DevBuildDirect {
    Start-WorkerProcess -Task 'dev-direct' -LogFile (Get-BuilderLogPath)
}

function Invoke-ReleasePackage {
    Start-WorkerProcess -Task 'release' -LogFile (Get-BuilderLogPath)
}

function Invoke-Cleanup {
    Start-WorkerProcess -Task 'cleanup' -LogFile (Get-CleanupLogPath) -DeepClean:$cbDeepClean.IsChecked -CleanCache:$cbCleanCache.IsChecked
}

function Invoke-ExportBundle {
    Start-WorkerProcess -Task 'export-bundle' -LogFile (Get-ExportLogPath)
}

function Open-DevOutput {
    $devOutput = "$GameRepoRoot\\bin\\Debug\\net8.0-windows"
    if (Test-Path $devOutput) {
        Start-Process explorer $devOutput
    } else {
        [System.Windows.Forms.MessageBox]::Show('Dev output folder not found. Run a dev build first.', 'Folder Not Found', 'OK', 'Warning')
    }
}

function Open-ReleaseOutput {
    if (Test-Path $ReleasesDir) {
        Start-Process explorer $ReleasesDir
    } else {
        [System.Windows.Forms.MessageBox]::Show('Release output folder not found. Run a release package first.', 'Folder Not Found', 'OK', 'Warning')
    }
}

function Open-LogsFolder {
    if (Test-Path $LogsDir) {
        Start-Process explorer $LogsDir
    } else {
        [System.Windows.Forms.MessageBox]::Show('Logs folder not found.', 'Folder Not Found', 'OK', 'Warning')
    }
}

# Create WPF Window
[xml]$xaml = @"
<Window xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        Title="LatticeVeil Builder" Height="700" Width="900"
        WindowStartupLocation="CenterScreen" Background="#1e1e1e">
    <Grid Margin="10">
        <Grid.RowDefinitions>
            <RowDefinition Height="Auto"/>
            <RowDefinition Height="Auto"/>
            <RowDefinition Height="*"/>
            <RowDefinition Height="Auto"/>
        </Grid.RowDefinitions>

        <StackPanel Grid.Row="0" Orientation="Horizontal" Margin="0,0,0,10">
            <Label Name="lblStatus" Content="Status: Idle" Foreground="#d4d4d4" FontSize="14" FontWeight="Bold" VerticalAlignment="Center"/>
            <ProgressBar Name="progressBar" Width="200" Height="20" Margin="10,0,0,0" IsIndeterminate="False" Foreground="#00ff7f" Background="#2d2d2d"/>
        </StackPanel>

        <Grid Grid.Row="1" Margin="0,0,0,10">
            <Grid.ColumnDefinitions>
                <ColumnDefinition Width="*"/>
                <ColumnDefinition Width="*"/>
                <ColumnDefinition Width="*"/>
            </Grid.ColumnDefinitions>
            <Grid.RowDefinitions>
                <RowDefinition Height="Auto"/>
                <RowDefinition Height="Auto"/>
                <RowDefinition Height="Auto"/>
                <RowDefinition Height="Auto"/>
            </Grid.RowDefinitions>

            <Button Name="btnDevLauncher" Grid.Column="0" Grid.Row="0" Content="DEV Build + Run (Launcher)" Margin="5" Padding="10" Background="#2d2d2d" Foreground="#d4d4d4"/>
            <Button Name="btnDevDirect" Grid.Column="1" Grid.Row="0" Content="DEV Build + Run (No Launcher)" Margin="5" Padding="10" Background="#2d2d2d" Foreground="#d4d4d4"/>
            <Button Name="btnRelease" Grid.Column="2" Grid.Row="0" Content="RELEASE Publish + Package" Margin="5" Padding="10" Background="#2d2d2d" Foreground="#d4d4d4"/>

            <Button Name="btnCleanup" Grid.Column="0" Grid.Row="1" Content="CLEANUP" Margin="5" Padding="10" Background="#2d2d2d" Foreground="#d4d4d4"/>
            <Button Name="btnOpenDev" Grid.Column="1" Grid.Row="1" Content="Open Dev Output" Margin="5" Padding="10" Background="#2d2d2d" Foreground="#d4d4d4"/>
            <Button Name="btnOpenRelease" Grid.Column="2" Grid.Row="1" Content="Open Release Output" Margin="5" Padding="10" Background="#2d2d2d" Foreground="#d4d4d4"/>

            <StackPanel Grid.Column="0" Grid.Row="2" Orientation="Horizontal" Margin="5">
                <CheckBox Name="cbDeepClean" Content="Deep Clean" Foreground="#d4d4d4" Margin="0,0,10,0"/>
                <CheckBox Name="cbCleanCache" Content="Clean NuGet Cache" Foreground="#d4d4d4"/>
            </StackPanel>
            <Button Name="btnOpenLogs" Grid.Column="1" Grid.Row="2" Content="Open Logs Folder" Margin="5" Padding="10" Background="#2d2d2d" Foreground="#d4d4d4"/>
            <Button Name="btnExportBundle" Grid.Column="2" Grid.Row="2" Content="EXPORT REVIEW BUNDLE" Margin="5" Padding="10" Background="#2d2d2d" Foreground="#d4d4d4"/>
        </Grid>

        <TextBox Name="logText" Grid.Row="2" Background="#2d2d2d" Foreground="#d4d4d4"
                 FontFamily="Consolas" FontSize="10" IsReadOnly="True"
                 VerticalScrollBarVisibility="Auto" HorizontalScrollBarVisibility="Auto"
                 TextWrapping="NoWrap" Margin="5"/>

        <StackPanel Grid.Row="3" Orientation="Horizontal" Margin="0,10,0,0">
            <Button Name="refreshBtn" Content="Refresh Log" Margin="5" Padding="10" Background="#2d2d2d" Foreground="#d4d4d4"/>
            <Button Name="clearBtn" Content="Clear Log" Margin="5" Padding="10" Background="#2d2d2d" Foreground="#d4d4d4"/>
        </StackPanel>
    </Grid>
</Window>
"@

$window = [Windows.Markup.XamlReader]::Load((New-Object System.Xml.XmlNodeReader $xaml))

$btnDevLauncher = $window.FindName('btnDevLauncher')
$btnDevDirect = $window.FindName('btnDevDirect')
$btnRelease = $window.FindName('btnRelease')
$btnCleanup = $window.FindName('btnCleanup')
$btnOpenDev = $window.FindName('btnOpenDev')
$btnOpenRelease = $window.FindName('btnOpenRelease')
$btnOpenLogs = $window.FindName('btnOpenLogs')
$btnExportBundle = $window.FindName('btnExportBundle')
$cbDeepClean = $window.FindName('cbDeepClean')
$cbCleanCache = $window.FindName('cbCleanCache')
$lblStatus = $window.FindName('lblStatus')
$progressBar = $window.FindName('progressBar')
$logText = $window.FindName('logText')
$refreshBtn = $window.FindName('refreshBtn')
$clearBtn = $window.FindName('clearBtn')

$buttons = @($btnDevLauncher, $btnDevDirect, $btnRelease, $btnCleanup, $btnOpenDev, $btnOpenRelease, $btnOpenLogs, $btnExportBundle, $refreshBtn, $clearBtn)
foreach ($btn in $buttons) {
    $btn.Add_MouseEnter({ $this.Background = '#3d3d3d' })
    $btn.Add_MouseLeave({ $this.Background = '#2d2d2d' })
}

$btnDevLauncher.Add_Click({ Invoke-DevBuildLauncher })
$btnDevDirect.Add_Click({ Invoke-DevBuildDirect })
$btnRelease.Add_Click({ Invoke-ReleasePackage })
$btnCleanup.Add_Click({ Invoke-Cleanup })
$btnOpenDev.Add_Click({ Open-DevOutput })
$btnOpenRelease.Add_Click({ Open-ReleaseOutput })
$btnOpenLogs.Add_Click({ Open-LogsFolder })
$btnExportBundle.Add_Click({ Invoke-ExportBundle })

$refreshBtn.Add_Click({ Update-LogDisplay })
$clearBtn.Add_Click({
    try {
        $logFile = $script:ActiveLogFile
        if ([string]::IsNullOrWhiteSpace($logFile)) { $logFile = Get-BuilderLogPath }
        if (Test-Path $logFile) {
            Clear-Content $logFile
            $logText.Text = ''
        }
    } catch {
        $logText.Text = "Error clearing log: $_"
    }
})

Set-UIState -Enabled $true -Status 'Idle'
Update-LogDisplay

$window.ShowDialog() | Out-Null
