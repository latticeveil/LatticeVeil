using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace LatticeVeilMonoGame.Core;

/// <summary>
/// Voxel world implementation with region-based chunk storage for new format worlds
/// </summary>
public sealed class VoxelWorld : IDisposable
{
    private readonly Dictionary<ChunkCoord, VoxelChunkData> _chunks = new();
    private readonly object _chunksLock = new();
    private readonly Logger _log;
    private readonly int _maxChunkY;
    
    // Chunk storage backend
    private readonly IChunkStore _chunkStore;
    private readonly bool _isNewFormat;

    // Deterministic generator used when a v2 chunk isn't present in region storage yet.
    // Without this, unexplored/unloaded areas become all-air "void" chunks.
    private IChunkGenerator? _generator;
    private readonly object _generatorLock = new();
    private bool _disposed;

    public WorldMeta Meta { get; }
    public string WorldPath { get; }
    public int MaxChunkY => _maxChunkY;

    /// <summary>
    /// World height in blocks.
    /// </summary>
    public int WorldHeight => Meta?.Size.Height ?? 0;

    /// <summary>
    /// Back-compat helper: some code expects a GetOrCreateChunkData API.
    /// </summary>
    public VoxelChunkData GetOrCreateChunkData(ChunkCoord coord) => GetOrCreateChunk(coord);

    /// <summary>
    /// Back-compat helper overload: some legacy call sites pass a Logger as a second parameter.
    /// The logger is ignored here; chunk creation is deterministic and logging occurs at higher layers.
    /// </summary>
    public VoxelChunkData GetOrCreateChunkData(ChunkCoord coord, Logger log) => GetOrCreateChunk(coord);

    /// <summary>
    /// Back-compat helper overload.
    /// </summary>
    public VoxelChunkData GetOrCreateChunkData(int chunkX, int chunkY, int chunkZ)
        => GetOrCreateChunk(new ChunkCoord(chunkX, chunkY, chunkZ));

    /// <summary>
    /// Back-compat helper overload (many callers assume a 2D world where chunkY is 0).
    /// Interprets parameters as (chunkX, chunkZ).
    /// </summary>
    public VoxelChunkData GetOrCreateChunkData(int chunkX, int chunkZ)
        => GetOrCreateChunk(new ChunkCoord(chunkX, 0, chunkZ));

    /// <summary>
    /// Back-compat helper: some code expects a GetBlockIdAtWorld API.
    /// </summary>
    public byte GetBlockIdAtWorld(int worldX, int worldY, int worldZ) => GetBlock(worldX, worldY, worldZ);

    /// <summary>
    /// Back-compat helper overload.
    /// </summary>
    public byte GetBlockIdAtWorld(float worldX, float worldY, float worldZ)
        => GetBlock((int)System.MathF.Floor(worldX), (int)System.MathF.Floor(worldY), (int)System.MathF.Floor(worldZ));


    // Compatibility API for existing code
    public VoxelChunkData? TryGetChunk(ChunkCoord coord)
    {
        lock (_chunksLock)
        {
            return _chunks.TryGetValue(coord, out var chunk) ? chunk : null;
        }
    }

    public IEnumerable<VoxelChunkData> AllChunks
    {
        get
        {
            lock (_chunksLock)
            {
                return _chunks.Values.ToList();
            }
        }
    }

    public int ChunkCount
    {
        get
        {
            lock (_chunksLock)
            {
                return _chunks.Count;
            }
        }
    }

    public void AddChunkDirect(VoxelChunkData chunk)
    {
        lock (_chunksLock)
        {
            _chunks[chunk.Coord] = chunk;
        }
    }

    public void CopyChunksTo(Dictionary<ChunkCoord, VoxelChunkData> target)
    {
        lock (_chunksLock)
        {
            foreach (var kvp in _chunks)
            {
                target[kvp.Key] = kvp.Value;
            }
        }
    }

    // Back-compat: several call sites snapshot chunks into a List (UI/debug, save & exit, etc.)
    public void CopyChunksTo(List<VoxelChunkData> target)
    {
        lock (_chunksLock)
        {
            target.Clear();
            target.AddRange(_chunks.Values);
        }
    }


    public void UnloadChunks()
    {
        lock (_chunksLock)
        {
            _chunks.Clear();
        }
    }

    /// <summary>
    /// Unload chunks not in <paramref name="keep"/>. For each dirty chunk that is being unloaded,
    /// <paramref name="onSave"/> is invoked with a flat block snapshot.
    /// </summary>
    public void UnloadChunks(HashSet<ChunkCoord> keep, Action<ChunkCoord, byte[]> onSave)
    {
        if (keep == null) throw new ArgumentNullException(nameof(keep));
        if (onSave == null) throw new ArgumentNullException(nameof(onSave));

        List<VoxelChunkData> toUnload = new();
        lock (_chunksLock)
        {
            foreach (var kv in _chunks)
            {
                if (!keep.Contains(kv.Key))
                    toUnload.Add(kv.Value);
            }

            foreach (var chunk in toUnload)
                _chunks.Remove(chunk.Coord);
        }

        bool usedStore = false;
        foreach (var chunk in toUnload)
        {
            if (!(chunk.NeedsSave || chunk.IsDirty))
                continue;

            // V2 worlds: persist directly to region store so unloaded chunks never revert.
            if (_isNewFormat && _chunkStore != null && (chunk.IsDirty || chunk.NeedsSave))
            {
                _chunkStore.SaveChunk(chunk.Coord, chunk);
                chunk.MarkClean();
                usedStore = true;
            }
            else
            {
                // Legacy fallback: let caller decide how to persist.
                onSave(chunk.Coord, chunk.Blocks);
            }
        }

        if (usedStore)
            _chunkStore!.Flush();
    }

    /// <summary>
    /// Block read API used by PlayerController / meshing (byte block ids).
    /// </summary>
    public byte GetBlock(int x, int y, int z)
    {
        VoxelWorld.WorldToChunkCoords(x, y, z, out var cx, out var cy, out var cz);
        var chunk = TryGetChunk(new ChunkCoord(cx, cy, cz));
        if (chunk == null) return BlockIds.Air;
        
        var lx = x - cx * VoxelChunkData.ChunkSizeX;
        var ly = y - cy * VoxelChunkData.ChunkSizeY;
        var lz = z - cz * VoxelChunkData.ChunkSizeZ;
        
        return chunk.GetBlock(lx, ly, lz);
    }

    /// <summary>
    /// Legacy/compat overload.
    /// </summary>
    public ushort GetBlockU16(int x, int y, int z) => GetBlock(x, y, z);

    /// <summary>
    /// Set a block in a loaded chunk. Returns true if the chunk existed and the write was applied.
    /// </summary>
    public bool SetBlock(int x, int y, int z, byte block)
    {
        VoxelWorld.WorldToChunkCoords(x, y, z, out var cx, out var cy, out var cz);
        var chunk = TryGetChunk(new ChunkCoord(cx, cy, cz));
        if (chunk == null) return false;
        
        var lx = x - cx * VoxelChunkData.ChunkSizeX;
        var ly = y - cy * VoxelChunkData.ChunkSizeY;
        var lz = z - cz * VoxelChunkData.ChunkSizeZ;
        
        var oldBlock = chunk.GetBlock(lx, ly, lz);
        if (oldBlock == block) return false; // No change needed
        
        chunk.SetBlock(lx, ly, lz, block);
        chunk.MarkDirty(); // Mark chunk as dirty for saving
        
        _log.Debug($"ChunkDirty {chunk.Coord.X},{chunk.Coord.Y},{chunk.Coord.Z} reason=blockchange pos=({x},{y},{z}) old={oldBlock} new={block}");
        return true;
    }

    /// <summary>
    /// Legacy/compat overload.
    /// </summary>
    public void SetBlock(int x, int y, int z, ushort block)
    {
        _ = SetBlock(x, y, z, (byte)block);
    }

    /// <summary>
    /// Compatibility overload used by older UI code.
    /// </summary>
    public bool TryGetChunk(ChunkCoord coord, out VoxelChunkData? chunk)
    {
        chunk = TryGetChunk(coord);
        return chunk != null;
    }

    /// <summary>
    /// Compatibility overload used by older streaming code.
    /// </summary>
    public void AddChunkDirect(ChunkCoord coord, VoxelChunkData chunk)
    {
        if (chunk == null) throw new ArgumentNullException(nameof(chunk));
        chunk.Coord = coord;
        AddChunkDirect(chunk);
    }

    /// <summary>
    /// Save API alias expected by some codepaths.
    /// </summary>
    public int SaveModifiedChunks() => SaveAllLoadedChunks();

    /// <summary>
    /// Persist a chunk snapshot (flat blocks) to the active chunk store (v2 regions).
    /// Safe to call from unloading paths.
    /// </summary>
    public void SaveChunkSnapshot(ChunkCoord coord, byte[] blocks)
    {
        if (!_isNewFormat || _chunkStore == null) return;
        if (blocks == null || blocks.Length != VoxelChunkData.Volume) return;

        var chunk = new VoxelChunkData(coord);
        chunk.Load(blocks);
        _chunkStore.SaveChunk(coord, chunk);
    }

    // Biome compatibility methods
    public string GetBiomeNameAt(int x, int z)
    {
        var sample = BiomeService.GetSampleAt(Meta, x, z);
        return BiomeService.GetBiomeName(sample.BiomeId);
    }

    public float GetOceanWeightAt(int x, int z)
    {
        return BiomeService.GetSampleAt(Meta, x, z).OceanWeight;
    }

    public float GetDesertWeightAt(int x, int z)
    {
        return BiomeService.GetSampleAt(Meta, x, z).EffectiveDesertWeight;
    }

    public float GetForestWeightAt(int x, int z)
    {
        return BiomeService.GetSampleAt(Meta, x, z).ForestWeight;
    }

    public float GetHillsWeightAt(int x, int z)
    {
        return BiomeService.GetSampleAt(Meta, x, z).HillsWeight;
    }

    public VoxelWorld(WorldMeta meta, string worldPath, Logger log)
    {
        Meta = meta;
        WorldPath = worldPath;
        _log = log;

        // Determine format based on the *world format* marker.
        // WorldVersion was used in older code paths, but v2 worlds are keyed off Format/FormatVersion.
        _isNewFormat = meta.FormatVersion >= 2 || string.Equals(meta.Format, "LVWORLD", StringComparison.OrdinalIgnoreCase);
        
        // Canonical runtime storage uses region files only.
        _chunkStore = new RegionChunkStore(worldPath, enableCompression: true);

        // Backward compatibility: repair legacy metadata with missing/zero world sizes.
        // Backward compatibility: repair legacy metadata with missing/invalid world sizes.
        // Height values like 16 (one chunk) were used as placeholders during early v2 work and break streaming (maxCy=0).
        Meta.Size ??= new WorldSize();
        if (Meta.Size.Width <= 0)
            Meta.Size.Width = 512;
        if (Meta.Size.Depth <= 0)
            Meta.Size.Depth = 512;

        int originalHeight = Meta.Size.Height;
        // Treat anything below 64 as invalid/placeholder and use a sane default (v2 worlds are effectively 'tall').
        if (Meta.Size.Height < 64)
            Meta.Size.Height = 256;
        if (Meta.Size.Height != originalHeight)
            _log.Warn($"World meta height was {originalHeight}; normalized to {Meta.Size.Height} to avoid maxCy=0 streaming.");

        var height = Math.Max(1, Meta.Size.Height);
        _maxChunkY = (height - 1) / VoxelChunkData.ChunkSizeY;
        _maxChunkY = (height - 1) / VoxelChunkData.ChunkSizeY;

        if (_isNewFormat)
        {
            try
            {
                _generator = CreateChunkGenerator(Meta);
            }
            catch (Exception ex)
            {
                _log.Warn($"Failed to init chunk generator; missing chunks may appear empty. {ex.Message}");
                _generator = null;
            }
        }
    }

    public static VoxelWorld? Load(string worldPath, string metaPath, Logger log)
    {
        var meta = WorldMeta.Load(metaPath, log);
        if (meta == null)
            return null;

        // Handle Regions/ vs regions/ folder naming for new format worlds
        if (meta.FormatVersion >= 2 || string.Equals(meta.Format, "LVWORLD", StringComparison.OrdinalIgnoreCase)) // New format world
        {
            var regionsDir = Path.Combine(worldPath, "regions");
            var regionsDirUpper = Path.Combine(worldPath, "Regions");
            
            // Prefer lowercase regions/. If only Regions/ exists, try to rename; if that fails,
            // RegionChunkStore will still fall back to Regions/ at runtime.
            if (!Directory.Exists(regionsDir) && Directory.Exists(regionsDirUpper))
            {
                try
                {
                    Directory.Move(regionsDirUpper, regionsDir);
                    log.Info("Renamed Regions/ to regions/ for consistency");
                }
                catch (Exception ex)
                {
                    log.Warn($"Failed to rename Regions/ to regions/: {ex.Message}");
                }
            }
            
            // Delete legacy chunks/ and biome_catalog.bin for new format worlds
            try
            {
                var chunksDir = Path.Combine(worldPath, "chunks");
                if (Directory.Exists(chunksDir))
                {
                    Directory.Delete(chunksDir, true);
                    log.Info("Deleted legacy chunks/ folder for new format world");
                }
                
                var biomeCatalogPath = Path.Combine(worldPath, "biome_catalog.bin");
                if (File.Exists(biomeCatalogPath))
                {
                    File.Delete(biomeCatalogPath);
                    log.Info("Deleted legacy biome_catalog.bin for new format world");
                }
            }
            catch (Exception ex)
            {
                log.Warn($"Failed to clean legacy artifacts: {ex.Message}");
            }
        }

        return new VoxelWorld(meta, worldPath, log);
    }

    public VoxelChunkData? GetOrCreateChunk(ChunkCoord coord)
    {
        lock (_chunksLock)
        {
            if (_chunks.TryGetValue(coord, out var existing))
                return existing;

            // Try load from storage
            if (_chunkStore.TryLoadChunk(coord, out var data))
            {
                _chunks[coord] = data;
                return data;
            }

            // v2 worlds: if not present in region storage yet, generate deterministic base terrain.
            if (_isNewFormat && _generator != null)
            {
                try
                {
                    VoxelChunkData generated;
                    lock (_generatorLock)
                    {
                        generated = _generator.GenerateChunk(coord);
                    }
                    // Base terrain should be considered clean; only player edits should mark dirty.
                    generated.MarkClean();
                    _chunks[coord] = generated;
                    _log.Info($"GeneratedChunkV2 coord={coord} source=generator");
                    return generated;
                }
                catch (Exception ex)
                {
                    _log.Warn($"Chunk generator failed for {coord}: {ex.Message}");
                }
            }

            // Generate new chunk
            var newChunk = new VoxelChunkData(coord);
            _chunks[coord] = newChunk;
            return newChunk;
        }
    }

    public void SaveChunk(ChunkCoord coord)
    {
        lock (_chunksLock)
        {
            if (_chunks.TryGetValue(coord, out var chunk))
            {
                // Only save if chunk is actually dirty or needs save
                if (chunk.IsDirty || chunk.NeedsSave)
                {
                    _chunkStore.SaveChunk(coord, chunk);
                }
            }
        }
    }

    public int SaveAllLoadedChunks()
    {
        List<VoxelChunkData> chunksToSave;
        lock (_chunksLock)
        {
            chunksToSave = _chunks.Values.Where(c => c.IsDirty || c.NeedsSave).ToList();
        }

        // Avoid no-op save churn/log spam when nothing changed.
        if (chunksToSave.Count == 0)
            return 0;

        var savedCount = 0;
        var failedCount = 0;

        _log.Info($"SaveDirtyChunksV2 count={chunksToSave.Count}");
        
        foreach (var chunk in chunksToSave)
        {
            try
            {
                var regionX = (int)Math.Floor(chunk.Coord.X / 32.0);
                var regionZ = (int)Math.Floor(chunk.Coord.Z / 32.0);
                
                _chunkStore.SaveChunk(chunk.Coord, chunk);
                savedCount++;
                
                _log.Info($"SavedChunkV2 coord={chunk.Coord.X},{chunk.Coord.Y},{chunk.Coord.Z} -> regions/r.{regionX}.{regionZ}.lvregion bytes={chunk.Blocks.Length}");
            }
            catch (Exception ex)
            {
                failedCount++;
                _log.Error($"Failed to save chunk {chunk.Coord}: {ex.Message}");
                // Keep chunk dirty so it will retry next autosave
            }
        }

        _chunkStore.Flush();
        _log.Info($"SaveDirtyChunksV2 complete: dirty={chunksToSave.Count}, saved={savedCount}, failed={failedCount}");

        return savedCount;
    }

    public void MarkAllChunksDirty()
    {
        lock (_chunksLock)
        {
            foreach (var chunk in _chunks.Values)
                chunk.IsDirty = true;
        }
    }

    // Chunk coordinate only (used internally where local coords are computed separately).
    public static void WorldToChunkCoords(int worldX, int worldY, int worldZ, out int chunkX, out int chunkY, out int chunkZ)
    {
        // Preserve the original negative-coordinate behavior.
        chunkX = worldX < 0 ? (worldX - (VoxelChunkData.ChunkSizeX - 1)) / VoxelChunkData.ChunkSizeX : worldX / VoxelChunkData.ChunkSizeX;
        chunkY = worldY < 0 ? (worldY - (VoxelChunkData.ChunkSizeY - 1)) / VoxelChunkData.ChunkSizeY : worldY / VoxelChunkData.ChunkSizeY;
        chunkZ = worldZ < 0 ? (worldZ - (VoxelChunkData.ChunkSizeZ - 1)) / VoxelChunkData.ChunkSizeZ : worldZ / VoxelChunkData.ChunkSizeZ;
    }

    /// <summary>
    /// Converts world coordinates to a chunk coordinate and also returns local block coordinates.
    /// Many callers (notably GameWorldScreen) require this method to return ChunkCoord.
    /// </summary>
    public static ChunkCoord WorldToChunk(int worldX, int worldY, int worldZ, out int localX, out int localY, out int localZ)
    {
        WorldToChunkCoords(worldX, worldY, worldZ, out int cx, out int cy, out int cz);

        localX = worldX - (cx * VoxelChunkData.ChunkSizeX);
        localY = worldY - (cy * VoxelChunkData.ChunkSizeY);
        localZ = worldZ - (cz * VoxelChunkData.ChunkSizeZ);

        if (localX < 0) localX += VoxelChunkData.ChunkSizeX;
        if (localY < 0) localY += VoxelChunkData.ChunkSizeY;
        if (localZ < 0) localZ += VoxelChunkData.ChunkSizeZ;

        return new ChunkCoord(cx, cy, cz);
    }

    public static ChunkCoord WorldToChunk(int worldX, int worldY, int worldZ)
        => WorldToChunk(worldX, worldY, worldZ, out _, out _, out _);

    private IChunkGenerator CreateChunkGenerator(WorldMeta meta)
    {
        meta.WorldGeneration ??= new WorldGenerationSettings();
        var requestedGenerator = (meta.Generator ?? string.Empty).Trim();
        var requestedWorldType = (meta.WorldGeneration.WorldType ?? string.Empty).Trim();
        var worldType = WorldMeta.CanonicalWorldType(requestedWorldType);
        var expectedGenerator = WorldMeta.CanonicalGeneratorForWorldType(worldType);

        if (!string.Equals(requestedGenerator, expectedGenerator, StringComparison.OrdinalIgnoreCase))
            _log.Warn($"Generator mismatch detected (Generator=\"{requestedGenerator}\", WorldType=\"{requestedWorldType}\"). Using \"{expectedGenerator}\".");

        meta.WorldGeneration.WorldType = worldType;
        if (!string.Equals(worldType, "flatlands", StringComparison.OrdinalIgnoreCase))
            meta.WorldGeneration.GenerateTrees = true;
        meta.Generator = expectedGenerator;

        if (string.Equals(worldType, "flatlands", StringComparison.OrdinalIgnoreCase))
        {
            var settings = BuildSuperflatSettings(meta);
            _log.Info($"Using SuperflatWorldGenerator (Generator=\"{meta.Generator}\", WorldType=\"{meta.WorldGeneration.WorldType}\")");
            return new SuperflatWorldGenerator(meta.Seed, settings, _log);
        }

        _log.Info($"Using BasicWorldGenerator (Generator=\"{meta.Generator}\", WorldType=\"{meta.WorldGeneration.WorldType}\")");
        return new BasicWorldGenerator(meta.Seed, BuildGeneratorSettings(meta), _log);
    }

    private static SuperflatWorldGenerator.Settings BuildSuperflatSettings(WorldMeta meta)
    {
        var worldHeight = 256;
        if (meta?.WorldGeneration?.WorldSize != null && meta.WorldGeneration.WorldSize.Height > 0)
            worldHeight = meta.WorldGeneration.WorldSize.Height;
        else if (meta?.Size != null && meta.Size.Height > 0)
            worldHeight = meta.Size.Height;

        var caves = meta?.WorldGeneration?.GenerateCaves ?? true;
        var ores = meta?.WorldGeneration?.GenerateOres ?? true;
        var trees = meta?.WorldGeneration?.GenerateTrees ?? true;

        return new SuperflatWorldGenerator.Settings(worldHeight: Math.Max(64, worldHeight), seaLevel: 64, generateCaves: caves, generateOres: ores, generateTrees: trees);
    }


    private static BasicWorldGenerator.WorldSettings BuildGeneratorSettings(WorldMeta meta)
    {
        // Use stable defaults; WorldMeta currently stores mostly high-level toggles.
        // WorldGenerationSettings (WorldMeta.WorldGeneration) does NOT expose nested "Features" or "SeaLevel".
        var worldHeight = 256;
        if (meta?.WorldGeneration?.WorldSize != null && meta.WorldGeneration.WorldSize.Height > 0)
            worldHeight = meta.WorldGeneration.WorldSize.Height;
        else if (meta?.Size != null && meta.Size.Height > 0)
            worldHeight = meta.Size.Height;

        var settings = new BasicWorldGenerator.WorldSettings
        {
            ChunkSize = 16,
            WorldHeight = Math.Max(64, worldHeight),
            SeaLevel = 64,
            GenerateStructures = meta?.WorldGeneration?.GenerateStructures ?? true,
            GenerateCaves = meta?.WorldGeneration?.GenerateCaves ?? true,
            GenerateOres = meta?.WorldGeneration?.GenerateOres ?? true,
            // Terrain worlds always generate trees regardless of metadata toggle.
            GenerateTrees = true,
        };

        // Log generator settings for debugging
        Console.WriteLine($"BasicWorldGenerator settings: WorldHeight={settings.WorldHeight}, ChunkSize={settings.ChunkSize}, SeaLevel={settings.SeaLevel}");
        
        return settings;
    }

    private static float HashNoise3D(int x, int y, int z, int seed)
    {
        unchecked
        {
            var h = (uint)seed;
            h ^= (uint)x * 0x9E3779B9u;
            h ^= (uint)y * 0xC2B2AE35u;
            h ^= (uint)z * 0x85EBCA6Bu;
            h = (h << 13) | (h >> 19);
            return h * (1.0f / int.MaxValue);
        }
    }

    public void Dispose() => Dispose(saveDirtyChunks: true);

    /// <summary>
    /// Dispose the world and release all file handles.
    /// Some call sites (e.g. client-side/joined sessions) need to release region locks
    /// without writing to disk.
    /// </summary>
    public void Dispose(bool saveDirtyChunks)
    {
        if (_disposed)
            return;

        if (saveDirtyChunks)
        {
            // Save any dirty chunks before disposing
            try
            {
                SaveAllLoadedChunks();
            }
            catch (Exception ex)
            {
                _log.Warn($"Error saving chunks during dispose: {ex.Message}");
            }
        }

        // Dispose chunk store (closes all file handles)
        try
        {
            if (_chunkStore is IDisposable disposableStore)
            {
                disposableStore.Dispose();
            }
        }
        catch (Exception ex)
        {
            _log.Warn($"Error disposing chunk store: {ex.Message}");
        }

        // Dispose generator
        try
        {
            lock (_generatorLock)
            {
                _generator?.Dispose();
                _generator = null;
            }
        }
        catch (Exception ex)
        {
            _log.Warn($"Error disposing generator: {ex.Message}");
        }

        // Clear chunks
        lock (_chunksLock)
        {
            _chunks.Clear();
        }

        _disposed = true;
        _log.Info("VoxelWorld disposed");
    }
}
