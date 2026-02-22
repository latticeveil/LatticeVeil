# V8.0.0 - Worldforge Convergence

Worldforge Convergence is the consolidated V8.0.0 release focused on world generation stability, multiplayer reliability, and launcher/asset hardening.

## Highlights

### World Generation and Loading
- Spawn-first generation flow is finalized around spawn bring-up.
- Chunk mesh cache behavior is stabilized for faster return loads.
- Create-time worldgen path remains the primary generation route.

### Multiplayer and Online
- EOS gate ticket handoff from launcher to game process is stabilized.
- Official online remains protected by server-side allowlist and ticket validation.
- LAN fallback remains available when gate verification is denied or unavailable.

### Launcher and Assets
- Launcher installs assets automatically when required files are missing.
- Existing user assets are not replaced on normal launch.
- Full asset recovery is available via `Reset Assets`.
- Asset runtime path remains `Documents/LatticeVeil/Assets`.

## Compatibility Notice
- Older multiplayer world saves may fail or desync with this release due to world sync and worldgen pipeline changes.

## Release Assets
- `LatticeVeilMonoGame.exe`
- `LatticeVeil-v8.0.0-worldforge-convergence-win-x64.zip`
- `LatticeVeil-v8.0.0-worldforge-convergence-source-safe.zip`

Assets package is distributed separately via `latticeveil/Assets` as `Assets.zip`.
