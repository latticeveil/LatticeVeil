# LatticeVeil

LatticeVeil is a MonoGame voxel survival project with Veilnet account integration, online build validation, player skins, and EOS-backed multiplayer services for official builds.

## Downloads

Use the GitHub Releases page for playable Windows builds. The source archive is for code review and development only; it does not include private online service credentials, EOS SDK files, local developer folders, generated build output, or release binaries.

## Repository Layout

- `LatticeVeilMonoGame/` - game and launcher source.
- `build/` - GUI builder source.
- `Tools/BuildGUI.ps1` and `Tools/BuildGUI.cmd` - local GUI builder launch scripts.

## Notes

Online play requires the official released build and the live Veilnet/EOS service configuration. Development source builds can run local/offline workflows, but private service keys and SDK packages are intentionally excluded from the public repository.
