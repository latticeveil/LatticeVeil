# LatticeVeil Tools

This folder contains project scripts organized by purpose.  
The Build GUI workflow is preserved.

## Quick Start

- Double-click: `Tools/BuildGUI.ps1`
- Direct GUI script: `Tools/GUI/BuildGUI.ps1`

`Tools/BuildGUI.ps1` is the stable entrypoint and forwards to the GUI script.

## Folder Layout

- `Tools/GUI`
  - `BuildGUI.ps1`
- `Tools/Cleanup`
  - `cleanup.ps1`
  - `CleanupProject.ps1`
- `Tools/Cleanup/Deprecated`
  - `deep_clean.ps1`
- `Tools/EOS`
  - `migrate-eos-config.ps1`
- `Tools/Release`
  - `build_and_release.ps1`
  - `create_release.ps1`
  - `create_github_release.ps1`
  - `verify_release.ps1`
- `Tools/Git`
  - `push_all_repos.ps1`

## WARNING: Deprecated Script

`Tools/Cleanup/Deprecated/deep_clean.ps1` is deprecated and potentially destructive.  
Use `Tools/Cleanup/cleanup.ps1` or `Tools/Cleanup/CleanupProject.ps1` instead.

## Build Notes

If you need a direct CLI build, use:

```powershell
dotnet build ..\LatticeVeilMonoGame\LatticeVeilMonoGame.csproj
```

`Build.ps1` is not part of the current `Tools/` script set in this repo layout.
