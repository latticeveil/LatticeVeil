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

## Launcher Endpoint Config (Local-Only)

Launcher endpoint overrides can be set without editing source code.

Local config path:

- `%APPDATA%/RedactedCraft/launcher_config.json`

Precedence order:

1. Environment variables (`LV_VEILNET_FUNCTIONS_URL`, `LV_GAME_HASHES_GET_URL`, `LV_VEILNET_LAUNCHER_URL`)
2. `%APPDATA%/RedactedCraft/launcher_config.json`
3. Built-in launcher defaults

Template:

- `LatticeVeilMonoGame/Launcher/launcher_config.template.json`

Important:

- `launcher_config.json` is gitignored and should never be committed.
- Do not place service-role or private keys in launcher config.

## Secret Scan Helper

Run this before committing:

```powershell
.\Tools\scan-secrets.ps1
```

The script reports filename + line for suspicious secret-like strings and exits non-zero when matches are found.

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

## EOS Configuration

### EOS SDK Pathing and DLL Placement

The project is already wired to EOS SDK paths in `LatticeVeilMonoGame/LatticeVeilMonoGame.csproj`:

- EOS C# source path: `ThirdParty/EOS/SDK/Source/**`
- Native DLL path: `ThirdParty/EOS/EOSSDK-Win64-Shipping.dll`

At runtime/publish, `EOSSDK-Win64-Shipping.dll` must be present next to the built game executable.

### Public vs Private Config Files

- `eos/eos.public.json` is committed and safe (public IDs only).
- `eos/eos.private.example.json` is committed as a template only.
- `eos/eos.private.json` is local-only and ignored by git.

`eos/eos.public.json` format (public values only):

```json
{
  "ProductId": "REPLACE_WITH_PRODUCT_ID",
  "SandboxId": "REPLACE_WITH_SANDBOX_ID",
  "DeploymentId": "REPLACE_WITH_DEPLOYMENT_ID",
  "ClientId": "REPLACE_WITH_CLIENT_ID",
  "ProductName": "RedactedCraft",
  "ProductVersion": "1.0"
}
```

`eos/eos.private.example.json` template:

```json
{
  "ClientSecret": "PUT_SECRET_HERE"
}
```

### Runtime Load Order

Public config (`eos.public.json`) load order:

1. `<GameFolder>/eos.public.json`
2. `<GameFolder>/eos/eos.public.json`

Client secret load order (private):

1. `EOS_CLIENT_SECRET` environment variable
2. `<GameFolder>/eos.private.json`
3. `%APPDATA%/RedactedCraft/eos.private.json`

Important:

- `ClientSecret` must never be committed to this repo.
- Secrets must never be shipped in website builds.
- If a secret is ever required for backend operations, handle it server-side only.

### Supabase Public EOS Config Function

This repo includes a Supabase Edge Function at:

- `supabase/functions/eos-config/index.ts`

It serves only public EOS fields:

- `ProductId`
- `SandboxId`
- `DeploymentId`
- `ClientId`
- `ProductName`
- `ProductVersion`

Set function environment variables (public values only):

- `EOS_PRODUCT_ID`
- `EOS_SANDBOX_ID`
- `EOS_DEPLOYMENT_ID`
- `EOS_CLIENT_ID`
- `EOS_PRODUCT_NAME`
- `EOS_PRODUCT_VERSION`

Do not put `EOS_CLIENT_SECRET` in this public config function. If you ever need secret-backed EOS operations, use a separate private server function.

Example deployment flow:

```powershell
supabase functions deploy eos-config
supabase secrets set EOS_PRODUCT_ID=... EOS_SANDBOX_ID=... EOS_DEPLOYMENT_ID=... EOS_CLIENT_ID=... EOS_PRODUCT_NAME=RedactedCraft EOS_PRODUCT_VERSION=1.0
```

## Veilnet Launcher Link

Launcher login now uses a Veilnet link-code flow instead of embedding Google auth in the launcher.

Flow:

1. In the launcher, click `LOGIN WITH VEILNET`.
2. Your browser opens `https://latticeveil.github.io/veilnet/launcher/`.
3. Sign in on Veilnet, ensure your username is set, then generate a one-time code.
4. Paste the code into the launcher prompt.
5. Launcher exchanges the code for a Veilnet launcher token and current username.

Token storage:

- `%APPDATA%/RedactedCraft/veilnet_launcher_token.json` (DPAPI-protected local file)

Startup behavior:

- On each launcher start, if a token exists, launcher calls Supabase Edge Function `launcher-me`.
- The launcher refreshes the current website username and uses it for online identity (`LV_VEILNET_USERNAME`).
- If token validation fails, launcher clears the local token and requires relink.

Official build verification (online only):

- Before online launch, launcher fetches official hashes from `game-hashes-get`.
- Debug/dev launcher builds validate against `dev`; release builds validate against `release`.
- Local SHA256 is computed from the configured file path (`OfficialBuildHashFilePath` in settings) or fallback executable path.
- If hash mismatches, online launch is blocked with an official-build warning.
- Offline launch remains available.

Optional overrides:

- `LV_GAME_HASHES_GET_URL` to override the hash endpoint.
- `OfficialBuildHashFilePath` in `settings.json` to point to a specific file for hashing.

Reset / unlink:

- Click `LOGIN WITH VEILNET` while linked to unlink in the launcher UI.
- Or delete `%APPDATA%/RedactedCraft/veilnet_launcher_token.json` manually.

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

