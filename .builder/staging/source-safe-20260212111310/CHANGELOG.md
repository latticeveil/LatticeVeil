# LatticeVeil Changelog

## V8.0.0 - Worldforge Convergence (2026-02-12)

### World Generation and Loading
- Finalized spawn-first generation flow so initial world bring-up is deterministic around spawn.
- Kept chunk mesh caching active for faster return loads and reduced visible chunk pop-in.
- Consolidated create-time worldgen toggles and removed legacy runtime startup generation path.
- Continued cache freshness/version enforcement to avoid stale chunk mesh reuse.

### Multiplayer and Online Gate
- Stabilized online gate ticket handoff from launcher to game process.
- Kept official online restricted to allowlisted builds via server-side validation.
- Preserved LAN fallback behavior when gate verification is denied or unavailable.
- Maintained friend/join-by-code flow improvements and host-side command control updates.

### Launcher and Asset Delivery
- Launcher now preserves user assets unless install is required or `Reset Assets` is explicitly used.
- Asset reset path continues to support full delete/reinstall for reliable recovery.
- Assets release feed remains `Assets.zip` from `latticeveil/Assets`.
- Runtime assets continue to load from `Documents\\LatticeVeil\\Assets`.

### Release Packaging
- Main release artifacts standardized to:
  - `LatticeVeilMonoGame.exe`
  - `LatticeVeil-v8.0.0-worldforge-convergence-win-x64.zip`
  - `LatticeVeil-v8.0.0-worldforge-convergence-source-safe.zip`
- No rebuild required for this release finalization; existing release EXE is used as-is.

### Compatibility Note
- Older multiplayer world saves may not be compatible with this release due to world synchronization and worldgen pipeline changes.
