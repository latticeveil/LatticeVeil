# LatticeVeil Project

LatticeVeil is a voxel survival game with procedural world generation, EOS-backed online play, and moddable user assets.

## Current Release

- Version: `v8.0.0`
- Release name: `Worldforge Convergence`
- Platform: Windows x64 (`net8.0-windows`, single-file EXE)

This release ships without changing game protocol contracts. It focuses on worldgen/loading stability, multiplayer fixes, and launcher/asset reliability.

## Distribution Model

Player-facing release artifacts are:

- `LatticeVeilMonoGame.exe`
- `LatticeVeil-v8.0.0-worldforge-convergence-win-x64.zip`
- `LatticeVeil-v8.0.0-worldforge-convergence-source-safe.zip`

Assets are distributed through the separate `latticeveil/Assets` release feed as `Assets.zip`.

## Assets Behavior

Runtime asset path:

- `Documents/LatticeVeil/Assets`

Launcher behavior:

- Installs assets automatically if required files are missing.
- Does not force overwrite on normal launch when assets are already present.
- `Reset Assets` performs a full reinstall (delete/replace flow) of official defaults.

Compatibility notice:

- Older multiplayer world saves may not be compatible with this release due to worldgen and sync pipeline changes.

## Build and Local Dev

Run the build GUI:

```powershell
.\Tools\BuildGUI.ps1
```

Output staging:

- `DEV/LatticeVeilMonoGame.exe`
- `RELEASE/LatticeVeilMonoGame.exe`

Both are single-file self-contained publishes. `BuildNonce` metadata is stamped by build tools to support allowlist/hash workflows.

## Official Online Gate

Official online access is protected by server-side gate validation.

Flow:

1. Launcher requests a gate ticket for the local EXE hash.
2. Game process receives a pre-authorized ticket from launcher.
3. EOS host validates peer tickets using `POST /ticket/validate`.
4. If gate verification fails, game falls back to LAN-only behavior.

Important:

- Secrets are not shipped in client artifacts.
- Official EOS private credentials remain server-side on Render.
- Public forks can run offline/LAN or their own EOS/gate backend, but not the official backend unless explicitly allowlisted.

See `OFFICIAL_ONLINE_SERVICE_TERMS.md` for policy and restrictions.

### Client Environment Variables

- `LV_GATE_URL`
- `LV_GATE_DEFAULT_URL`
- `LV_EOS_CONFIG_URL`
- `LV_GATE_REQUIRED`
- `LV_REQUIRE_LAUNCHER_HANDSHAKE`
- `LV_ALLOWLIST_URL`
- `LV_OFFICIAL_PROOF_PATH`
- `LV_DEV_ONLINE_KEY`

### Gate Server Environment Variables

- `GATE_JWT_SIGNING_KEY` (required)
- `GATE_ADMIN_TOKEN` (required for admin runtime endpoints)
- `EOS_PRODUCT_ID`
- `EOS_SANDBOX_ID`
- `EOS_DEPLOYMENT_ID`
- `EOS_CLIENT_ID`
- `EOS_CLIENT_SECRET`
- `GATE_VERIFICATION_MODE`
- `GATE_EXPECTED_SANDBOX_ID`
- `GATE_EXPECTED_DEPLOYMENT_ID`
- `GATE_PUBLIC_ID_POLICY`
- `GATE_DEV_KEY` (optional)
- `ALLOWLIST_SOURCE`
- `ALLOWLIST_JSON_PATH` (recommended)

Template:

- `GateServer/render.example.env`

## EOS Config Split

The project supports split EOS config files:

- `eos.public.json` (safe IDs only)
- `eos.private.json` (secret material, never commit)
- `eos.public.example.json` (tracked placeholder template)

If EOS config is unavailable, EOS is disabled and LAN remains available.

## Runtime Hash Rotation (No Redeploy)

Maintainer workflow only:

```powershell
$env:GATE_ADMIN_TOKEN = "<your-admin-token>"
.\Tools\UpdateGateHash.ps1 -BuildType release -Target auto -ShowRuntime
```

Or GUI:

```powershell
.\Tools\UpdateGateHashGUI.ps1
```

This updates runtime allowlist memory on the gate service. A Render restart clears runtime overrides unless persisted in source allowlist config.

## Contributing

1. Fork the repository.
2. Create a branch.
3. Implement and test changes.
4. Submit a pull request.

## License

This repository is MIT licensed (`LICENSE`).
Official hosted backend access is governed separately by `OFFICIAL_ONLINE_SERVICE_TERMS.md`.

