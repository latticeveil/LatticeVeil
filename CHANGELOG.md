# LatticeVeil Changelog

## V8.0.0 - Cacheforge Update (2026-02-08)

### Performance & Loading
- Spawn-first pipeline now front-loads generation work around spawn before gameplay begins.
- Spawn chunk meshes are cached to disk through `ChunkMeshCache`, so returning to existing worlds with warm cache data loads nearly instantly.
- Streaming, prewarm, and job budgets were tuned to reduce hitching and visible chunk pop-in during movement.

### World Creation
- World creation keeps generation work in the create flow instead of runtime gameplay startup.
- Create World now includes generation toggles for structures, caves, and ores.
- Multiple Homes creation options and UI groundwork were added for upcoming worldgen and progression extensions.

### Engine & Infrastructure
- Runtime world generation flow on the in-world screen was removed/stubbed in favor of the spawn-first creation pipeline.
- `ChunkMeshCache` now uses explicit versioning and freshness checks to avoid stale mesh reuse.
- Asset and cache plumbing was refreshed to support deterministic cache reuse across join/rejoin cycles.

### Known Issues / Next
- First-time joins on uncached worlds still incur generation and cache warm-up time.
- Continue validating cache invalidation paths after large terrain-generation changes.
- Next milestone includes broader worldgen module hooks and additional profile-safe online hosting diagnostics.
