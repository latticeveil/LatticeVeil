# LatticeVeil Tools

## Minimal Tools Layout

This folder is intentionally minimal.

- `Tools/BuildGUI.ps1` - single-file build GUI (double-click entrypoint)
- `Tools/README.md` - tool usage notes
- `Tools/_Archive/` - archived legacy scripts and folders

## Public Release Boundary

Player-facing distributions do not require admin tooling.

- `Tools/UpdateGateHash.ps1` and `Tools/UpdateGateHashGUI.ps1` are maintainer workflows.
- They are intended for official allowlist/hash operations only.
- They should not be bundled into player runtime release packages.

## Usage

Run:

```powershell
.\Tools\BuildGUI.ps1
```

If the project root is not auto-detected, use the `Project Root` field (`BROWSE` + `APPLY`) and select the folder that contains `LatticeVeilMonoGame\LatticeVeilMonoGame.csproj`.

The GUI includes:

- Quick Test Build (DEV publish + Run)
- Release Build + ZIP (publish)
- Build Only (DEV publish)
- Auto-staging of final EXEs into repo-root `DEV\` and `RELEASE\` folders
- SHA256 helper for both DEV and RELEASE EXEs with copyable text fields
- Clean/Open/Log helpers

Build actions stamp a unique `BuildNonce`, so each GUI build/publish produces a fresh binary hash for allowlist workflows.

## Gate Hash Update (No Redeploy)

Use `Tools/UpdateGateHash.ps1` to push your newest EXE hash to the Render gate runtime allowlist:

```powershell
$env:GATE_ADMIN_TOKEN = "<your GATE_ADMIN_TOKEN>"
.\Tools\UpdateGateHash.ps1 -BuildType release -Target auto -ShowRuntime
```

Notes:

- Default gate URL is `https://eos-service.onrender.com` (override with `-GateUrl`).
- The script auto-selects from `DEV\` / `RELEASE\` first, then falls back to build output folders (unless `-ExePath` is provided).
- This updates runtime allowlist immediately (no redeploy needed). Runtime overrides reset if the service restarts.
- Official online access still depends on server-side secrets and gate policy in Render.

### GUI Version

For a simple two-input UI (`EXE Path` + `Admin Token`), run:

```powershell
.\Tools\UpdateGateHashGUI.ps1
```

Behavior:

- Gate URL is fixed to your EOS service default (`https://eos-service.onrender.com`) unless `LV_GATE_URL` is set.
- Lets you choose `target=dev` or `target=release` explicitly (or `AUTO` from EXE path).
- Replaces the selected target hash list without clearing the other target list.
- Does not store your admin token to disk.
- Anyone without your `GATE_ADMIN_TOKEN` cannot use the endpoint; rotate that token immediately if you think it leaked.

## Archival Policy

Legacy scripts were moved to `Tools/_Archive/<timestamp>/` and not deleted.
If you need an older script, recover it from the archive path.
