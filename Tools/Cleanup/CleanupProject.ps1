[CmdletBinding()]
param(
    [switch]$Commit,
    [switch]$IncludeIdeConfigs
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Write-Section {
    param([string]$Title)
    Write-Host ""
    Write-Host "=== $Title ===" -ForegroundColor Cyan
}

function Normalize-Path {
    param([string]$Path)
    try {
        return [System.IO.Path]::GetFullPath($Path)
    } catch {
        return $Path
    }
}

function Test-IsUnderPath {
    param(
        [string]$Path,
        [string]$BasePath
    )
    $p = Normalize-Path $Path
    $b = Normalize-Path $BasePath
    if (-not $b.EndsWith([System.IO.Path]::DirectorySeparatorChar)) {
        $b += [System.IO.Path]::DirectorySeparatorChar
    }
    return $p.StartsWith($b, [System.StringComparison]::OrdinalIgnoreCase)
}

function Ensure-Directory {
    param([string]$Path)
    if (-not (Test-Path -LiteralPath $Path)) {
        [void](New-Item -ItemType Directory -Path $Path -Force)
    }
}

function Resolve-RelativePath {
    param(
        [string]$BasePath,
        [string]$ChildPath
    )
    $base = Normalize-Path $BasePath
    $child = Normalize-Path $ChildPath
    if (-not $base.EndsWith([System.IO.Path]::DirectorySeparatorChar)) {
        $base += [System.IO.Path]::DirectorySeparatorChar
    }
    if ($child.StartsWith($base, [System.StringComparison]::OrdinalIgnoreCase)) {
        return $child.Substring($base.Length)
    }
    return [System.IO.Path]::GetFileName($child)
}

function Move-ToArchive {
    param(
        [string]$SourcePath,
        [string]$ArchiveRoot,
        [string]$RepoRoot,
        [bool]$DoCommit
    )
    if (-not (Test-Path -LiteralPath $SourcePath)) {
        return $null
    }

    $relative = Resolve-RelativePath -BasePath $RepoRoot -ChildPath $SourcePath
    $destination = Join-Path $ArchiveRoot $relative
    $destinationDir = Split-Path -Parent $destination
    if ($DoCommit) {
        Ensure-Directory $destinationDir
    }

    if (Test-Path -LiteralPath $destination) {
        $name = [System.IO.Path]::GetFileNameWithoutExtension($destination)
        $ext = [System.IO.Path]::GetExtension($destination)
        $parent = Split-Path -Parent $destination
        $i = 1
        do {
            $candidate = Join-Path $parent ("{0}_{1}{2}" -f $name, $i, $ext)
            $i++
        } while (Test-Path -LiteralPath $candidate)
        $destination = $candidate
    }

    if ($DoCommit) {
        Move-Item -LiteralPath $SourcePath -Destination $destination -Force
        return [PSCustomObject]@{
            Source = $SourcePath
            Destination = $destination
            Mode = "Moved"
        }
    }

    return [PSCustomObject]@{
        Source = $SourcePath
        Destination = $destination
        Mode = "WhatIf"
    }
}

function Add-MoveCandidate {
    param(
        [System.Collections.Generic.List[object]]$List,
        [string]$Path,
        [string]$Reason
    )
    if (-not (Test-Path -LiteralPath $Path)) {
        return
    }
    $full = Normalize-Path $Path
    if ($List | Where-Object { $_.Path -eq $full }) {
        return
    }
    $List.Add([PSCustomObject]@{
        Path = $full
        Reason = $Reason
    }) | Out-Null
}

function Remove-NestedCandidates {
    param([System.Collections.Generic.List[object]]$Candidates)

    $ordered = $Candidates | Sort-Object { $_.Path.Length }
    $kept = New-Object 'System.Collections.Generic.List[object]'
    foreach ($candidate in $ordered) {
        $isNested = $false
        foreach ($existing in $kept) {
            if (Test-Path -LiteralPath $existing.Path -PathType Container) {
                if (Test-IsUnderPath -Path $candidate.Path -BasePath $existing.Path) {
                    $isNested = $true
                    break
                }
            }
        }
        if (-not $isNested) {
            $kept.Add($candidate) | Out-Null
        }
    }
    return $kept
}

$repoRoot = Normalize-Path (Join-Path $PSScriptRoot "..\..")
$timestamp = (Get-Date).ToUniversalTime().ToString("yyyyMMdd_HHmmss")
$archiveAuto = Join-Path $repoRoot (Join-Path "_Archive\_auto" $timestamp)
$archiveCandidates = Join-Path $repoRoot (Join-Path "_Archive\_candidates" $timestamp)
$doCommit = [bool]$Commit

Write-Section "Mode"
if ($doCommit) {
    Write-Host "Commit mode: moves WILL be performed." -ForegroundColor Yellow
} else {
    Write-Host "WhatIf mode: no files will be moved." -ForegroundColor Green
}

Write-Section "Step 1 - Build GUI Protected Paths"
$protectedPaths = @(
    (Join-Path $repoRoot "BuildGUI.ps1"),
    (Join-Path $repoRoot "build"),
    (Join-Path $repoRoot "LatticeVeilMonoGame\Launcher"),
    (Join-Path $repoRoot "LatticeVeilMonoGame\Launcher\Resources"),
    (Join-Path $repoRoot "Tools\Build.ps1"),
    (Join-Path $repoRoot "Tools\build_and_release.ps1"),
    (Join-Path $repoRoot "Tools\create_release.ps1"),
    (Join-Path $repoRoot "Tools\create_github_release.ps1")
) | ForEach-Object { Normalize-Path $_ } | Select-Object -Unique

$protectedPaths | ForEach-Object { Write-Host $_ }

function Test-IsProtected {
    param([string]$Path)
    $p = Normalize-Path $Path
    foreach ($protected in $protectedPaths) {
        if ($p -eq $protected -or (Test-IsUnderPath -Path $p -BasePath $protected)) {
            return $true
        }
    }
    return $false
}

Write-Section "Step 2 - Baseline Structure"
Write-Host "Top-level directories and first 2 nested levels:"
Get-ChildItem -LiteralPath $repoRoot -Directory | ForEach-Object {
    Write-Host ("- {0}" -f $_.Name)
    Get-ChildItem -LiteralPath $_.FullName -Directory -ErrorAction SilentlyContinue | ForEach-Object {
        Write-Host ("  - {0}" -f (Resolve-RelativePath -BasePath $repoRoot -ChildPath $_.FullName))
        Get-ChildItem -LiteralPath $_.FullName -Directory -ErrorAction SilentlyContinue | ForEach-Object {
            Write-Host ("    - {0}" -f (Resolve-RelativePath -BasePath $repoRoot -ChildPath $_.FullName))
        }
    }
}

Write-Host ""
Write-Host "Solution / Project files:"
Get-ChildItem -LiteralPath $repoRoot -Recurse -File |
    Where-Object { $_.Extension -in @(".sln", ".csproj") } |
    ForEach-Object { Write-Host ("- {0}" -f (Resolve-RelativePath -BasePath $repoRoot -ChildPath $_.FullName)) }

Write-Host ""
Write-Host "Likely content/runtime asset directories:"
$contentDirs = @(
    "LatticeVeilMonoGame\Defaults\Assets",
    "LatticeVeilMonoGame\Content"
) | ForEach-Object { Join-Path $repoRoot $_ }
foreach ($dir in $contentDirs) {
    if (Test-Path -LiteralPath $dir) {
        Write-Host ("- {0}" -f (Resolve-RelativePath -BasePath $repoRoot -ChildPath $dir))
    }
}

Write-Host ""
Write-Host "Build/temp folders found:"
$tempDirNames = @("bin","obj",".vs",".idea",".vscode","Builds","publish","out")
$tempDirs = Get-ChildItem -LiteralPath $repoRoot -Recurse -Directory -Force -ErrorAction SilentlyContinue |
    Where-Object {
        $n = $_.Name.ToLowerInvariant()
        ($tempDirNames -contains $n) -or $n.StartsWith("_tmp") -or $n.StartsWith("temp")
    }
$tempDirs | ForEach-Object { Write-Host ("- {0}" -f (Resolve-RelativePath -BasePath $repoRoot -ChildPath $_.FullName)) }

Write-Section "Step 3 - Safe Cleanup Candidate Collection"
$autoCandidates = New-Object 'System.Collections.Generic.List[object]'

# 3a) bin/obj (all projects)
Get-ChildItem -LiteralPath $repoRoot -Recurse -Directory -Force -ErrorAction SilentlyContinue |
    Where-Object {
        $_.Name -in @("bin","obj")
    } | ForEach-Object {
        if (-not (Test-IsUnderPath -Path $_.FullName -BasePath (Join-Path $repoRoot "_Archive")) -and -not (Test-IsProtected $_.FullName)) {
            Add-MoveCandidate -List $autoCandidates -Path $_.FullName -Reason "Build output folder"
        }
    }

# 3b) IDE folders
$ideFolders = @(".vs",".idea")
if ($IncludeIdeConfigs) {
    $ideFolders += ".vscode"
}
foreach ($name in $ideFolders) {
    $path = Join-Path $repoRoot $name
    if (Test-Path -LiteralPath $path) {
        Add-MoveCandidate -List $autoCandidates -Path $path -Reason "IDE folder"
    }
}
if (-not $IncludeIdeConfigs -and (Test-Path -LiteralPath (Join-Path $repoRoot ".vscode"))) {
    Write-Host "Skipping .vscode by default. Use -IncludeIdeConfigs to archive it." -ForegroundColor Yellow
}

# 3c) file junk
$junkPatterns = @("*.user","*.suo","*.cache","*.tmp","*.log")
foreach ($pattern in $junkPatterns) {
    Get-ChildItem -LiteralPath $repoRoot -Recurse -File -Force -Filter $pattern -ErrorAction SilentlyContinue |
        ForEach-Object {
            if ($_.Name -eq "DEVELOPMENT_LOG.md") { return }
            if (Test-IsUnderPath -Path $_.FullName -BasePath (Join-Path $repoRoot "_Archive")) { return }
            if (Test-IsProtected $_.FullName) { return }
            Add-MoveCandidate -List $autoCandidates -Path $_.FullName -Reason "Junk file ($pattern)"
        }
}

# 3d) zip artifacts / copy folders
Get-ChildItem -LiteralPath $repoRoot -File -Filter "*.zip" -ErrorAction SilentlyContinue | ForEach-Object {
    Add-MoveCandidate -List $autoCandidates -Path $_.FullName -Reason "Zip artifact"
}
Get-ChildItem -LiteralPath $repoRoot -Recurse -Directory -ErrorAction SilentlyContinue |
    Where-Object { $_.Name -match "(?i)\bcopy\b| - Copy|_copy" } |
    ForEach-Object {
        if (Test-IsProtected $_.FullName) { return }
        Add-MoveCandidate -List $autoCandidates -Path $_.FullName -Reason "Copy folder"
    }

Write-Host ("Auto candidates found: {0}" -f $autoCandidates.Count)
foreach ($item in $autoCandidates) {
    Write-Host ("- [{0}] {1}" -f $item.Reason, (Resolve-RelativePath -BasePath $repoRoot -ChildPath $item.Path))
}
$autoCandidates = Remove-NestedCandidates -Candidates $autoCandidates
Write-Host ("Auto candidates after dedupe: {0}" -f $autoCandidates.Count)

Write-Section "Step 4 - Probably Unused Candidate Collection"
$candidateMoves = New-Object 'System.Collections.Generic.List[object]'

$keepTopFiles = @(
    "README.md",
    "DEVELOPMENT_PLAN.md",
    "DEVELOPMENT_LOG.md",
    ".gitignore",
    ".gitattributes",
    "LatticeVeil.sln"
)

# Collect explicit includes from csproj
$csprojFiles = Get-ChildItem -LiteralPath $repoRoot -Recurse -File -Filter "*.csproj" -ErrorAction SilentlyContinue
$explicitIncludes = New-Object 'System.Collections.Generic.HashSet[string]' ([System.StringComparer]::OrdinalIgnoreCase)
$projectRoots = New-Object 'System.Collections.Generic.HashSet[string]' ([System.StringComparer]::OrdinalIgnoreCase)

foreach ($csproj in $csprojFiles) {
    $projectRoots.Add((Normalize-Path (Split-Path -Parent $csproj.FullName))) | Out-Null
    [xml]$xml = Get-Content -LiteralPath $csproj.FullName
    $nodes = $xml.SelectNodes("//*[local-name()='Compile' or local-name()='None' or local-name()='Content' or local-name()='EmbeddedResource']")
    foreach ($node in $nodes) {
        foreach ($attrName in @("Include","Update")) {
            $attr = $node.Attributes[$attrName]
            if ($null -eq $attr) { continue }
            $includeValue = $attr.Value
            if ([string]::IsNullOrWhiteSpace($includeValue)) { continue }
            if ($includeValue.Contains("*")) { continue }
            $resolved = Normalize-Path (Join-Path (Split-Path -Parent $csproj.FullName) $includeValue)
            $explicitIncludes.Add($resolved) | Out-Null
        }
    }
}

# Add default SDK-style included source files as referenced.
foreach ($projRoot in $projectRoots) {
    if (-not (Test-Path -LiteralPath $projRoot)) { continue }
    Get-ChildItem -LiteralPath $projRoot -Recurse -File -ErrorAction SilentlyContinue |
        Where-Object {
            -not (Test-IsUnderPath -Path $_.FullName -BasePath (Join-Path $projRoot "bin")) -and
            -not (Test-IsUnderPath -Path $_.FullName -BasePath (Join-Path $projRoot "obj"))
        } | ForEach-Object {
            $explicitIncludes.Add((Normalize-Path $_.FullName)) | Out-Null
        }
}

# Parse simple runtime string references from source files.
$stringReferencedNames = New-Object 'System.Collections.Generic.HashSet[string]' ([System.StringComparer]::OrdinalIgnoreCase)
$sourceFiles = Get-ChildItem -LiteralPath $repoRoot -Recurse -File -Include *.cs,*.ps1,*.md -ErrorAction SilentlyContinue |
    Where-Object { -not (Test-IsUnderPath -Path $_.FullName -BasePath (Join-Path $repoRoot "_Archive")) }
foreach ($src in $sourceFiles) {
    $text = Get-Content -LiteralPath $src.FullName -Raw -ErrorAction SilentlyContinue
    if ([string]::IsNullOrEmpty($text)) { continue }
    $matches = [regex]::Matches($text, "(?<q>['""])(?<p>[^'""]+\.(json|png|jpg|jpeg|xnb|fx|mgcb|txt|zip))\k<q>", [System.Text.RegularExpressions.RegexOptions]::IgnoreCase)
    foreach ($m in $matches) {
        $p = $m.Groups["p"].Value
        if ([string]::IsNullOrWhiteSpace($p)) { continue }
        $stringReferencedNames.Add([System.IO.Path]::GetFileName($p)) | Out-Null
    }
}

# Candidate pool: top-level loose files/folders that are not known project roots and not protected.
$topLevel = Get-ChildItem -LiteralPath $repoRoot -Force
foreach ($item in $topLevel) {
    $name = $item.Name
    if ($name -in @("_Archive",".git","LatticeVeilMonoGame","GateServer","OnlineService","build","Tools","ThirdParty","docs","Docs","Experimental",".builder",".cache",".plans")) {
        continue
    }
    if ($item.PSIsContainer) {
        continue
    }
    if ($keepTopFiles -contains $name) { continue }
    if ($name -like "eos*.json") { continue }
    if ($name -like "*.sln") { continue }
    if (Test-IsProtected $item.FullName) { continue }

    $full = Normalize-Path $item.FullName
    $isIncluded = $explicitIncludes.Contains($full)
    $isReferencedByName = $stringReferencedNames.Contains($item.Name)
    if (-not $isIncluded -and -not $isReferencedByName) {
        Add-MoveCandidate -List $candidateMoves -Path $item.FullName -Reason "Top-level probably unused (unreferenced)"
    }
}

Write-Host ("Candidate files found: {0}" -f $candidateMoves.Count)
foreach ($item in $candidateMoves) {
    Write-Host ("- [{0}] {1}" -f $item.Reason, (Resolve-RelativePath -BasePath $repoRoot -ChildPath $item.Path))
}

Write-Section "Step 5 - Minimal Folder Organization"
$artifactsDir = Join-Path $repoRoot "Artifacts"
if ($doCommit) {
    Ensure-Directory $artifactsDir
}
if (Test-Path -LiteralPath $artifactsDir) {
    Write-Host "Artifacts folder: Artifacts (present)"
} else {
    Write-Host "Artifacts folder: Artifacts (would be created in -Commit mode)"
}
Write-Host "Docs folder: docs (already present, preserving existing structure)"
Write-Host "Tools folder: Tools (already present)"

Write-Section "Execution"
$moveResults = New-Object 'System.Collections.Generic.List[object]'

foreach ($item in $autoCandidates) {
    $result = Move-ToArchive -SourcePath $item.Path -ArchiveRoot $archiveAuto -RepoRoot $repoRoot -DoCommit:$doCommit
    if ($null -ne $result) {
        $moveResults.Add([PSCustomObject]@{
            Source = $result.Source
            Destination = $result.Destination
            Reason = $item.Reason
            Bucket = "auto"
            Mode = $result.Mode
        }) | Out-Null
    }
}
foreach ($item in $candidateMoves) {
    $result = Move-ToArchive -SourcePath $item.Path -ArchiveRoot $archiveCandidates -RepoRoot $repoRoot -DoCommit:$doCommit
    if ($null -ne $result) {
        $moveResults.Add([PSCustomObject]@{
            Source = $result.Source
            Destination = $result.Destination
            Reason = $item.Reason
            Bucket = "candidates"
            Mode = $result.Mode
        }) | Out-Null
    }
}

Write-Host ("Total move operations: {0}" -f $moveResults.Count)
foreach ($entry in $moveResults) {
    $src = Resolve-RelativePath -BasePath $repoRoot -ChildPath $entry.Source
    $dst = Resolve-RelativePath -BasePath $repoRoot -ChildPath $entry.Destination
    Write-Host ("- [{0}] {1} -> {2}" -f $entry.Reason, $src, $dst)
}

if ($doCommit) {
    Ensure-Directory (Split-Path -Parent $archiveAuto)
    Ensure-Directory (Split-Path -Parent $archiveCandidates)
    $report = [PSCustomObject]@{
        TimestampUtc = (Get-Date).ToUniversalTime().ToString("o")
        AutoArchiveRoot = Resolve-RelativePath -BasePath $repoRoot -ChildPath $archiveAuto
        CandidateArchiveRoot = Resolve-RelativePath -BasePath $repoRoot -ChildPath $archiveCandidates
        Moved = $moveResults
        ProtectedPaths = $protectedPaths
    }
    $reportPath = Join-Path $repoRoot (Join-Path "_Archive" ("cleanup_report_{0}.json" -f $timestamp))
    $report | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $reportPath -Encoding UTF8
    Write-Host ("Report written: {0}" -f (Resolve-RelativePath -BasePath $repoRoot -ChildPath $reportPath))
} else {
    Write-Host "WhatIf complete. No files moved."
}
