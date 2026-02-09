using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;

namespace LatticeVeilMonoGame.Core;

public sealed class VoxelWorld
{
    private static readonly Regex ChunkNameRegex = new(@"chunk_(?<x>-?\d+)_(?<y>-?\d+)_(?<z>-?\d+)\.bin", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private const float OceanColumnThreshold = 0.70f;
    private const float ShallowWaterThreshold = 0.56f;
    private const float OceanBiomeThreshold = 0.67f;
    private const float DesertMaterialThreshold = 0.45f;
    private const float DesertBiomeThreshold = 0.44f;
    private const float BeachMaterialThreshold = 0.46f;
    private const float TreeSpawnChance = 0.065f;

    private readonly Dictionary<ChunkCoord, VoxelChunkData> _chunks = new();
    private readonly object _chunksLock = new();
    private readonly Logger _log;
    private readonly int _maxChunkY;

    public WorldMeta Meta { get; }
    public string WorldPath { get; }
    public string ChunksDir { get; }

    public VoxelWorld(WorldMeta meta, string worldPath, Logger log)
    {
        Meta = meta;
        WorldPath = worldPath;
        ChunksDir = Path.Combine(worldPath, "chunks");
        _log = log;

        // Backward compatibility: repair legacy metadata with missing/zero world sizes.
        Meta.Size ??= new WorldSize();
        if (Meta.Size.Width <= 0)
            Meta.Size.Width = 512;
        if (Meta.Size.Depth <= 0)
            Meta.Size.Depth = 512;
        if (Meta.Size.Height <= 0)
            Meta.Size.Height = VoxelChunkData.ChunkSizeY;

        var height = Math.Max(1, Meta.Size.Height);
        _maxChunkY = (height - 1) / VoxelChunkData.ChunkSizeY;
    }

    public static VoxelWorld? Load(string worldPath, string metaPath, Logger log)
    {
        var meta = WorldMeta.Load(metaPath, log);
        if (meta == null)
            return null;

        var world = new VoxelWorld(meta, worldPath, log);
        world.LoadChunks();
        return world;
    }

    public List<VoxelChunkData> AllChunks() 
    { 
        lock (_chunksLock) 
            return _chunks.Values.ToList(); 
    }

    public int ChunkCount
    {
        get
        {
            lock (_chunksLock)
                return _chunks.Count;
        }
    }

    public void CopyChunksTo(List<VoxelChunkData> destination)
    {
        if (destination == null)
            throw new ArgumentNullException(nameof(destination));

        lock (_chunksLock)
        {
            destination.Clear();
            destination.AddRange(_chunks.Values);
        }
    }

    public bool TryGetChunk(ChunkCoord coord, out VoxelChunkData? chunk) 
    { 
        lock (_chunksLock) 
            return _chunks.TryGetValue(coord, out chunk); 
    }

    public void AddChunkDirect(ChunkCoord coord, VoxelChunkData chunk)
    {
        lock (_chunksLock)
            _chunks[coord] = chunk;
    }

    public int MaxChunkY => _maxChunkY;

    public byte GetBlock(int wx, int wy, int wz)
    {
        if (!IsWithinWorld(wx, wy, wz))
            return BlockIds.Air;

        var coord = WorldToChunk(wx, wy, wz, out var lx, out var ly, out var lz);
        if (!IsChunkYInRange(coord.Y))
            return BlockIds.Air;

        lock (_chunksLock)
        {
            if (_chunks.TryGetValue(coord, out var chunk))
                return chunk.GetLocal(lx, ly, lz);
        }

        // Read path must stay non-generating to avoid heavy stalls while meshing/collision query neighbors.
        return BlockIds.Air;
    }

    public bool SetBlock(int wx, int wy, int wz, byte id)
    {
        if (!IsWithinWorld(wx, wy, wz))
            return false;

        var coord = WorldToChunk(wx, wy, wz, out var lx, out var ly, out var lz);
        if (!IsChunkYInRange(coord.Y))
            return false;

        var chunk = GetOrCreateChunk(coord);
        chunk.SetLocal(lx, ly, lz, id);
        ChunkSeamRegistry.Register(coord, ChunkSurfaceProfile.FromChunk(chunk));
        MarkNeighborDirty(coord, lx, ly, lz);
        return true;
    }

    private void MarkNeighborDirty(ChunkCoord coord, int lx, int ly, int lz)
    {
        if (lx == 0) MarkDirty(new ChunkCoord(coord.X - 1, coord.Y, coord.Z));
        if (lx == VoxelChunkData.ChunkSizeX - 1) MarkDirty(new ChunkCoord(coord.X + 1, coord.Y, coord.Z));
        if (ly == 0) MarkDirty(new ChunkCoord(coord.X, coord.Y - 1, coord.Z));
        if (ly == VoxelChunkData.ChunkSizeY - 1) MarkDirty(new ChunkCoord(coord.X, coord.Y + 1, coord.Z));
        if (lz == 0) MarkDirty(new ChunkCoord(coord.X, coord.Y, coord.Z - 1));
        if (lz == VoxelChunkData.ChunkSizeZ - 1) MarkDirty(new ChunkCoord(coord.X, coord.Y, coord.Z + 1));
    }

    private void MarkDirty(ChunkCoord coord)
    {
        lock (_chunksLock)
        {
            if (_chunks.TryGetValue(coord, out var chunk))
                chunk.IsDirty = true;
        }
    }

    public void MarkAllChunksDirty()
    {
        lock (_chunksLock)
        {
            foreach (var chunk in _chunks.Values)
                chunk.IsDirty = true;
        }
    }

    public void SaveModifiedChunks()
    {
        if (!Directory.Exists(ChunksDir))
            Directory.CreateDirectory(ChunksDir);

        var chunksToSave = new List<VoxelChunkData>();
        
        // Collect chunks that need saving
        lock (_chunksLock)
        {
            foreach (var chunk in _chunks.Values)
            {
                if (chunk.NeedsSave)
                {
                    chunksToSave.Add(chunk);
                }
            }
        }

        // Limit saves to prevent freezing (max 50 chunks at once)
        const int maxChunksPerSave = 50;
        var savedCount = 0;
        
        foreach (var chunk in chunksToSave)
        {
            if (savedCount >= maxChunksPerSave)
            {
                // Mark remaining chunks as still needing save for next time
                break;
            }
            
            try
            {
                var path = Path.Combine(ChunksDir, $"chunk_{chunk.Coord.X}_{chunk.Coord.Y}_{chunk.Coord.Z}.bin");
                chunk.Save(path);
                savedCount++;
            }
            catch (Exception ex)
            {
                // Log error but continue saving other chunks
                System.Diagnostics.Debug.WriteLine($"Failed to save chunk {chunk.Coord}: {ex.Message}");
            }
        }
    }

    public void SaveChunk(ChunkCoord coord)
    {
        VoxelChunkData? chunk;
        lock (_chunksLock)
        {
            if (!_chunks.TryGetValue(coord, out chunk))
                return;
        }
        
        var path = Path.Combine(ChunksDir, $"chunk_{coord.X}_{coord.Y}_{coord.Z}.bin");
        chunk.Save(path);
    }

    public bool IsWithinWorld(int wx, int wy, int wz)
    {
        if (wy < 0)
            return false;
        if (wy >= Meta.Size.Height)
            return false;
        return IsWithinWorldXZ(wx, wz);
    }

    public bool IsWithinWorldXZ(int wx, int wz)
    {
        return wx >= 0
            && wz >= 0
            && wx < Meta.Size.Width
            && wz < Meta.Size.Depth;
    }

    public VoxelChunkData GetOrCreateChunk(ChunkCoord coord)
    {
        if (!IsChunkYInRange(coord.Y) || !IsChunkWithinWorldXZ(coord))
            return CreateVoidChunk(coord);

        lock (_chunksLock)
        {
            if (_chunks.TryGetValue(coord, out var existing))
                return existing;
        }

        var loaded = TryLoadChunk(coord);
        if (loaded != null)
        {
            lock (_chunksLock)
                _chunks[coord] = loaded;
            return loaded;
        }

        var chunk = new VoxelChunkData(coord);
        GenerateDefaultTerrain(chunk);
        chunk.IsDirty = true;
        
        lock (_chunksLock)
            _chunks[coord] = chunk;
        ChunkSeamRegistry.Register(coord, ChunkSurfaceProfile.FromChunk(chunk));
        return chunk;
    }

    private void GenerateDefaultTerrain(VoxelChunkData chunk)
    {
        // Deterministic fallback terrain used when no chunk file exists.
        // Generated per chunk-section to keep logic cache-friendly while allowing richer features.
        var originX = chunk.Coord.X * VoxelChunkData.ChunkSizeX;
        var originY = chunk.Coord.Y * VoxelChunkData.ChunkSizeY;
        var maxHeight = Math.Max(1, Meta.Size.Height - 1);
        const int seaLevel = 42;
        const int sectionHeight = 16;
        var originZ = chunk.Coord.Z * VoxelChunkData.ChunkSizeZ;
        var surfaceHeights = new int[VoxelChunkData.ChunkSizeX, VoxelChunkData.ChunkSizeZ];
        var poolLevels = new int[VoxelChunkData.ChunkSizeX, VoxelChunkData.ChunkSizeZ];
        var desertWeights = new float[VoxelChunkData.ChunkSizeX, VoxelChunkData.ChunkSizeZ];
        var oceanWeights = new float[VoxelChunkData.ChunkSizeX, VoxelChunkData.ChunkSizeZ];
        var blocks = new byte[VoxelChunkData.ChunkVolume];
        var seed = Meta.Seed == 0 ? 1337 : Meta.Seed;

        for (var lx = 0; lx < VoxelChunkData.ChunkSizeX; lx++)
        {
            for (var lz = 0; lz < VoxelChunkData.ChunkSizeZ; lz++)
            {
                var wx = originX + lx;
                var wz = originZ + lz;

                GetBiomeWeightsAt(wx, wz, seed, out var desertWeight, out var oceanWeight);
                desertWeights[lx, lz] = desertWeight;
                oceanWeights[lx, lz] = oceanWeight;

                var effectiveDesertWeight = desertWeight * (1f - oceanWeight * 0.9f);
                var baseSurface = ComputeBaseSurfaceHeight(wx, wz, effectiveDesertWeight, oceanWeight, seed, maxHeight, seaLevel);
                var coastBlend = ComputeCoastBlend(oceanWeight);
                var shoreNoise = FractalValueNoise2D(wx * 0.013f, wz * 0.013f, seed + 1603, octaves: 2, lacunarity: 2.0f, persistence: 0.5f);
                var shore01 = Math.Clamp((shoreNoise + 1f) * 0.5f, 0f, 1f);
                var coastalShelfHeight = seaLevel - Lerp(2.6f, 0.4f, shore01);
                var coastalBeachHeight = seaLevel + Lerp(0.6f, 2.8f, shore01);
                var coastShaped = Lerp(baseSurface, coastalShelfHeight, coastBlend * 0.82f);
                coastShaped = Lerp(coastShaped, coastalBeachHeight, coastBlend * 0.34f);
                var surface = Math.Clamp((int)MathF.Round(coastShaped), 10, maxHeight);
                var poolDepth = ComputePoolDepth(wx, wz, surface, effectiveDesertWeight, seed, seaLevel);
                var carvedSurface = Math.Clamp(surface - poolDepth, 8, maxHeight);
                var oceanColumn = oceanWeight >= OceanColumnThreshold;
                var shallowWaterColumn = !oceanColumn && oceanWeight >= ShallowWaterThreshold && carvedSurface <= seaLevel + 2;
                if (oceanColumn || shallowWaterColumn)
                    carvedSurface = Math.Min(seaLevel - 1, carvedSurface);

                surfaceHeights[lx, lz] = carvedSurface;
                poolLevels[lx, lz] = (poolDepth > 0 && !oceanColumn && !shallowWaterColumn)
                    ? Math.Clamp(carvedSurface + 1, carvedSurface + 1, maxHeight)
                    : -1;
            }
        }

        var sectionCount = VoxelChunkData.ChunkSizeY / sectionHeight;
        for (var section = 0; section < sectionCount; section++)
        {
            var sectionStartY = section * sectionHeight;
            var sectionEndY = sectionStartY + sectionHeight;

            for (var lx = 0; lx < VoxelChunkData.ChunkSizeX; lx++)
            {
                for (var lz = 0; lz < VoxelChunkData.ChunkSizeZ; lz++)
                {
                    var wx = originX + lx;
                    var wz = originZ + lz;
                    var surface = surfaceHeights[lx, lz];
                    var poolLevel = poolLevels[lx, lz];
                    var desertWeight = desertWeights[lx, lz];
                    var oceanWeight = oceanWeights[lx, lz];
                    var oceanColumn = oceanWeight >= OceanColumnThreshold && surface <= seaLevel - 1;
                    var shallowWaterColumn = !oceanColumn && oceanWeight >= ShallowWaterThreshold && surface <= seaLevel - 1;
                    var waterColumn = oceanColumn || shallowWaterColumn;
                    var effectiveDesertWeight = waterColumn ? 0f : desertWeight * (1f - oceanWeight * 0.9f);
                    var useDesertMaterial = !oceanColumn && effectiveDesertWeight >= DesertMaterialThreshold;
                    var beachWeight = ComputeBeachWeight(surface, seaLevel, oceanWeight);
                    var useBeachMaterial = !waterColumn && !useDesertMaterial && beachWeight >= BeachMaterialThreshold;
                    var beachPatchNoise = useBeachMaterial
                        ? ValueNoise2D(wx * 0.23f, wz * 0.23f, seed + 5501)
                        : -1f;
                    var useBeachGravelPatch = useBeachMaterial && beachPatchNoise > 0.92f;

                    var fillerDepth = waterColumn
                        ? 4
                        : useBeachMaterial
                            ? 2 + (beachWeight > 0.72f ? 1 : 0)
                            : (int)MathF.Round(Lerp(3f, 5f, effectiveDesertWeight));
                    // Desert and ocean columns keep a gravel sub-layer under sand before reaching stone.
                    var gravelDepth = waterColumn
                        ? 3
                        : useBeachMaterial
                            ? (useBeachGravelPatch ? 1 : 0)
                            : (useDesertMaterial ? 2 + (int)MathF.Round(Lerp(0f, 3f, effectiveDesertWeight)) : 0);
                    var waterTopY = waterColumn
                        ? seaLevel
                        : (poolLevel >= 0 ? poolLevel : -1);

                    for (var ly = sectionStartY; ly < sectionEndY; ly++)
                    {
                        var wy = originY + ly;
                        byte block;

                        if (wy > maxHeight)
                        {
                            block = BlockIds.Air;
                        }
                        else if (wy == 0)
                        {
                            block = BlockIds.Nullblock;
                        }
                        else if (wy > surface)
                        {
                            if (wy == waterTopY)
                                block = BlockIds.Water;
                            else
                                block = BlockIds.Air;
                        }
                        else if (wy == surface)
                        {
                            if (useBeachMaterial)
                                block = useBeachGravelPatch ? BlockIds.Gravel : BlockIds.Sand;
                            else
                                block = (useDesertMaterial || waterColumn) ? BlockIds.Sand : BlockIds.Grass;
                        }
                        else if (wy >= surface - fillerDepth)
                        {
                            if (useBeachMaterial)
                            {
                                var depthFromTop = surface - wy;
                                block = (useBeachGravelPatch && depthFromTop >= 1 && depthFromTop <= 2)
                                    ? BlockIds.Gravel
                                    : BlockIds.Sand;
                            }
                            else
                            {
                                block = (useDesertMaterial || waterColumn) ? BlockIds.Sand : BlockIds.Dirt;
                            }
                        }
                        else if ((useDesertMaterial || waterColumn || useBeachMaterial) && wy >= surface - fillerDepth - gravelDepth)
                        {
                            block = BlockIds.Gravel;
                        }
                        else
                        {
                            block = BlockIds.Stone;
                        }

                        if (block != BlockIds.Air
                            && block != BlockIds.Water
                            && ShouldCarveCave(wx, wy, wz, surface, seed))
                        {
                            block = BlockIds.Air;
                        }

                        var index = ((lx * VoxelChunkData.ChunkSizeY) + ly) * VoxelChunkData.ChunkSizeZ + lz;
                        blocks[index] = block;
                    }
                }
            }
        }

        ApplyDeterministicTreePass(chunk, blocks, surfaceHeights, desertWeights, oceanWeights, seaLevel, maxHeight, seed);
        chunk.Load(blocks);
    }

    private void ApplyDeterministicTreePass(
        VoxelChunkData chunk,
        byte[] blocks,
        int[,] surfaceHeights,
        float[,] desertWeights,
        float[,] oceanWeights,
        int seaLevel,
        int maxHeight,
        int seed)
    {
        var originX = chunk.Coord.X * VoxelChunkData.ChunkSizeX;
        var originY = chunk.Coord.Y * VoxelChunkData.ChunkSizeY;
        var originZ = chunk.Coord.Z * VoxelChunkData.ChunkSizeZ;

        for (var lx = 0; lx < VoxelChunkData.ChunkSizeX; lx++)
        {
            for (var lz = 0; lz < VoxelChunkData.ChunkSizeZ; lz++)
            {
                var surface = surfaceHeights[lx, lz];
                if (surface <= seaLevel + 2 || surface >= maxHeight - 10)
                    continue;

                var localSurfaceY = surface - originY;
                if ((uint)localSurfaceY >= VoxelChunkData.ChunkSizeY)
                    continue;

                var surfaceIndex = ((lx * VoxelChunkData.ChunkSizeY) + localSurfaceY) * VoxelChunkData.ChunkSizeZ + lz;
                if (blocks[surfaceIndex] != BlockIds.Grass)
                    continue;

                var oceanWeight = oceanWeights[lx, lz];
                if (oceanWeight >= 0.34f)
                    continue;

                var effectiveDesert = desertWeights[lx, lz] * (1f - oceanWeight * 0.9f);
                if (effectiveDesert >= DesertMaterialThreshold - 0.05f)
                    continue;

                if (!HasGentleSlope(surfaceHeights, lx, lz, maxDelta: 2))
                    continue;

                var wx = originX + lx;
                var wz = originZ + lz;
                if (!ShouldSpawnTreeAt(wx, wz, seed))
                    continue;

                var heightSignal = HashNoise2D(wx + 5021, wz - 1877, seed + 7711);
                var trunkHeight = heightSignal > 0.10f ? 5 : 4;
                var canopyRadius = trunkHeight >= 5 ? 2 : 1;
                var treeBaseY = localSurfaceY + 1;
                if (!CanPlaceTreeChunkSafe(blocks, lx, treeBaseY, lz, trunkHeight, canopyRadius))
                    continue;

                TryPlaceTreeChunkSafe(blocks, lx, treeBaseY, lz, trunkHeight, canopyRadius);
            }
        }
    }

    private static bool HasGentleSlope(int[,] surfaceHeights, int lx, int lz, int maxDelta)
    {
        var center = surfaceHeights[lx, lz];
        for (var dz = -1; dz <= 1; dz++)
        {
            for (var dx = -1; dx <= 1; dx++)
            {
                if (dx == 0 && dz == 0)
                    continue;

                var nx = lx + dx;
                var nz = lz + dz;
                if ((uint)nx >= VoxelChunkData.ChunkSizeX || (uint)nz >= VoxelChunkData.ChunkSizeZ)
                    continue;

                if (Math.Abs(surfaceHeights[nx, nz] - center) > maxDelta)
                    return false;
            }
        }

        return true;
    }

    private static bool ShouldSpawnTreeAt(int wx, int wz, int seed)
    {
        const int cellSize = 7;
        var cellX = FloorDiv(wx, cellSize);
        var cellZ = FloorDiv(wz, cellSize);
        var baseX = cellX * cellSize;
        var baseZ = cellZ * cellSize;

        var jitterX01 = Math.Clamp((HashNoise2D(cellX + 137, cellZ - 73, seed + 1811) + 1f) * 0.5f, 0f, 1f);
        var jitterZ01 = Math.Clamp((HashNoise2D(cellX - 211, cellZ + 59, seed + 2467) + 1f) * 0.5f, 0f, 1f);
        var candidateX = baseX + Math.Min(cellSize - 1, (int)MathF.Floor(jitterX01 * cellSize));
        var candidateZ = baseZ + Math.Min(cellSize - 1, (int)MathF.Floor(jitterZ01 * cellSize));
        if (wx != candidateX || wz != candidateZ)
            return false;

        var spawnSignal = Math.Clamp((HashNoise2D(wx + 401, wz - 619, seed + 9031) + 1f) * 0.5f, 0f, 1f);
        if (spawnSignal < 1f - TreeSpawnChance)
            return false;

        var clusterSignal = Math.Clamp((FractalValueNoise2D(wx * 0.0036f, wz * 0.0036f, seed + 12791, octaves: 2, lacunarity: 2.0f, persistence: 0.5f) + 1f) * 0.5f, 0f, 1f);
        return clusterSignal >= 0.45f;
    }

    private static void TryPlaceTreeChunkSafe(byte[] blocks, int baseLocalX, int baseLocalY, int baseLocalZ, int trunkHeight, int canopyRadius)
    {
        for (var i = 0; i < trunkHeight; i++)
            TrySetLocalBlockReplaceable(blocks, baseLocalX, baseLocalY + i, baseLocalZ, BlockIds.Wood);

        var canopyCenterY = baseLocalY + trunkHeight - 1;
        for (var dy = -2; dy <= 2; dy++)
        {
            var layerRadius = canopyRadius - (Math.Abs(dy) > 1 ? 1 : 0);
            if (layerRadius <= 0)
                continue;

            for (var dz = -layerRadius; dz <= layerRadius; dz++)
            {
                for (var dx = -layerRadius; dx <= layerRadius; dx++)
                {
                    if (Math.Abs(dx) + Math.Abs(dz) > layerRadius + 1)
                        continue;
                    TrySetLocalBlockReplaceable(blocks, baseLocalX + dx, canopyCenterY + dy, baseLocalZ + dz, BlockIds.Leaves);
                }
            }
        }

        TrySetLocalBlockReplaceable(blocks, baseLocalX, canopyCenterY + 2, baseLocalZ, BlockIds.Leaves);
    }

    private static bool CanPlaceTreeChunkSafe(byte[] blocks, int baseLocalX, int baseLocalY, int baseLocalZ, int trunkHeight, int canopyRadius)
    {
        if (!IsLocalInBounds(baseLocalX, baseLocalY, baseLocalZ))
            return false;

        // Ensure tree footprint stays fully inside this chunk section so trees are never sliced.
        var canopyCenterY = baseLocalY + trunkHeight - 1;
        var minX = baseLocalX - canopyRadius;
        var maxX = baseLocalX + canopyRadius;
        var minZ = baseLocalZ - canopyRadius;
        var maxZ = baseLocalZ + canopyRadius;
        var minY = baseLocalY;
        var maxY = canopyCenterY + 2;
        if (!IsLocalInBounds(minX, minY, minZ) || !IsLocalInBounds(maxX, maxY, maxZ))
            return false;

        for (var i = 0; i < trunkHeight; i++)
        {
            if (!IsLocalReplaceable(blocks, baseLocalX, baseLocalY + i, baseLocalZ))
                return false;
        }

        for (var dy = -2; dy <= 2; dy++)
        {
            var layerRadius = canopyRadius - (Math.Abs(dy) > 1 ? 1 : 0);
            if (layerRadius <= 0)
                continue;

            for (var dz = -layerRadius; dz <= layerRadius; dz++)
            {
                for (var dx = -layerRadius; dx <= layerRadius; dx++)
                {
                    if (Math.Abs(dx) + Math.Abs(dz) > layerRadius + 1)
                        continue;
                    if (!IsLocalReplaceable(blocks, baseLocalX + dx, canopyCenterY + dy, baseLocalZ + dz))
                        return false;
                }
            }
        }

        if (!IsLocalReplaceable(blocks, baseLocalX, canopyCenterY + 2, baseLocalZ))
            return false;

        return true;
    }

    private static bool IsLocalReplaceable(byte[] blocks, int lx, int ly, int lz)
    {
        if (!IsLocalInBounds(lx, ly, lz))
            return false;

        var index = ((lx * VoxelChunkData.ChunkSizeY) + ly) * VoxelChunkData.ChunkSizeZ + lz;
        var existing = blocks[index];
        return existing == BlockIds.Air || existing == BlockIds.Leaves;
    }

    private static bool IsLocalInBounds(int lx, int ly, int lz)
    {
        return (uint)lx < VoxelChunkData.ChunkSizeX
            && (uint)ly < VoxelChunkData.ChunkSizeY
            && (uint)lz < VoxelChunkData.ChunkSizeZ;
    }

    private static void TrySetLocalBlockReplaceable(byte[] blocks, int lx, int ly, int lz, byte block)
    {
        if ((uint)lx >= VoxelChunkData.ChunkSizeX || (uint)ly >= VoxelChunkData.ChunkSizeY || (uint)lz >= VoxelChunkData.ChunkSizeZ)
            return;

        var index = ((lx * VoxelChunkData.ChunkSizeY) + ly) * VoxelChunkData.ChunkSizeZ + lz;
        var existing = blocks[index];
        if (existing != BlockIds.Air && existing != BlockIds.Leaves)
            return;

        blocks[index] = block;
    }

    private static float ComputeDesertWeight(int wx, int wz, int seed)
    {
        // Blend wide climate regions with controlled pocketing so deserts spawn consistently.
        const int smoothOffset = 24;
        var center = ComputeRawDesertClimateSignal(wx, wz, seed);
        var north = ComputeRawDesertClimateSignal(wx, wz - smoothOffset, seed);
        var south = ComputeRawDesertClimateSignal(wx, wz + smoothOffset, seed);
        var east = ComputeRawDesertClimateSignal(wx + smoothOffset, wz, seed);
        var west = ComputeRawDesertClimateSignal(wx - smoothOffset, wz, seed);
        var climate = center * 0.44f + (north + south + east + west) * 0.14f;

        // Macro bands are the primary source of desert regions.
        var macroWeight = ComputeBandWeight(climate, center: 0.30f, halfWidth: 0.15f);

        // Medium pockets add variation while preserving larger biome identity.
        var mediumPocketNoise = FractalValueNoise2D(wx * 0.0026f, wz * 0.0026f, seed + 941, octaves: 2, lacunarity: 2.0f, persistence: 0.5f);
        var mediumPocketWeight = ComputeBandWeight(mediumPocketNoise, center: 0.67f, halfWidth: 0.09f) * 0.38f;

        // Small pockets remain possible but do not dominate.
        var smallPocketNoise = FractalValueNoise2D(wx * 0.0064f, wz * 0.0064f, seed + 2141, octaves: 2, lacunarity: 2.0f, persistence: 0.53f);
        var smallPocketWeight = ComputeBandWeight(smallPocketNoise, center: 0.76f, halfWidth: 0.06f) * 0.18f;

        // Wet climates suppress pockets so grasslands still remain dominant overall.
        var pocketClimateGate = ComputeBandWeight(climate, center: 0.18f, halfWidth: 0.34f);
        mediumPocketWeight *= Lerp(0.45f, 1f, pocketClimateGate);
        smallPocketWeight *= Lerp(0.35f, 0.9f, pocketClimateGate);

        var combined = macroWeight * 0.92f + mediumPocketWeight * 0.24f + smallPocketWeight * 0.08f;
        combined = MathF.Max(combined, macroWeight * 0.92f);
        return Math.Clamp(combined, 0f, 1f);
    }

    private static float ComputeOceanWeight(int wx, int wz, int seed)
    {
        // Ocean macro signal tuned for medium-size basins with clear coastlines.
        const int smoothOffset = 20;
        var center = ComputeRawOceanClimateSignal(wx, wz, seed);
        var north = ComputeRawOceanClimateSignal(wx, wz - smoothOffset, seed);
        var south = ComputeRawOceanClimateSignal(wx, wz + smoothOffset, seed);
        var east = ComputeRawOceanClimateSignal(wx + smoothOffset, wz, seed);
        var west = ComputeRawOceanClimateSignal(wx - smoothOffset, wz, seed);
        var continental = center * 0.46f + (north + south + east + west) * 0.135f;

        // Convert to [0..1], then derive ocean primarily from low continental values.
        var continental01 = Math.Clamp((continental + 1f) * 0.5f, 0f, 1f);
        var macroOcean = ComputeBandWeight(1f - continental01, center: 0.68f, halfWidth: 0.21f);

        // Coast variation adds inlets and bays.
        var coastNoise = FractalValueNoise2D(wx * 0.0042f, wz * 0.0042f, seed + 1451, octaves: 2, lacunarity: 2.0f, persistence: 0.5f);
        var coast01 = Math.Clamp((coastNoise + 1f) * 0.5f, 0f, 1f);
        var coastPerturb = (coast01 - 0.5f) * 0.16f;

        // Basin clusters create medium pockets instead of one giant connected ocean.
        var basinNoise = FractalValueNoise2D(wx * 0.0025f, wz * 0.0025f, seed + 2453, octaves: 3, lacunarity: 2.0f, persistence: 0.5f);
        var basin01 = Math.Clamp((basinNoise + 1f) * 0.5f, 0f, 1f);
        var basinWeight = ComputeBandWeight(basin01, center: 0.70f, halfWidth: 0.16f) * 0.24f;

        // Inland break-up noise prevents ocean blankets while keeping water common.
        var inlandBreakNoise = FractalValueNoise2D(wx * 0.0028f, wz * 0.0028f, seed + 1993, octaves: 3, lacunarity: 2.0f, persistence: 0.5f);
        var inlandBreak01 = Math.Clamp((inlandBreakNoise + 1f) * 0.5f, 0f, 1f);
        var inlandBreak = ComputeBandWeight(inlandBreak01, center: 0.62f, halfWidth: 0.18f) * 0.32f;

        var ocean = macroOcean * 0.82f + basinWeight + coastPerturb - inlandBreak;
        if (macroOcean < 0.32f)
            ocean *= 0.80f;
        return Math.Clamp(ocean, 0f, 1f);
    }

    private static float ComputeBandWeight(float sample, float center, float halfWidth)
    {
        var range = MathF.Max(0.0001f, halfWidth * 2f);
        var t = (sample - (center - halfWidth)) / range;
        return SmoothStep(Math.Clamp(t, 0f, 1f));
    }

    private static float ComputeCoastBlend(float oceanWeight)
    {
        // Wide coastal band so shore transitions are gradual instead of abrupt walls.
        var band = 1f - MathF.Abs(oceanWeight - 0.58f) / 0.34f;
        return SmoothStep(Math.Clamp(band, 0f, 1f));
    }

    private static float ComputeBeachWeight(int surface, int seaLevel, float oceanWeight)
    {
        // Beach likelihood rises near sea level and in coastal climate bands.
        var heightBand = 1f - Math.Clamp(MathF.Abs(surface - seaLevel) / 7f, 0f, 1f);
        var coastBand = ComputeCoastBlend(oceanWeight);
        return Math.Clamp(coastBand * 0.76f + heightBand * 0.56f, 0f, 1f);
    }

    private static int ComputeBaseSurfaceHeight(int wx, int wz, float desertWeight, float oceanWeight, int seed, int maxHeight, int seaLevel)
    {
        var macro = FractalValueNoise2D(wx * 0.0042f, wz * 0.0042f, seed + 911, octaves: 4, lacunarity: 2.0f, persistence: 0.52f);
        var detail = FractalValueNoise2D(wx * 0.016f, wz * 0.016f, seed + 307, octaves: 2, lacunarity: 2.1f, persistence: 0.5f);
        var dunes = FractalValueNoise2D(wx * 0.0105f, wz * 0.0105f, seed + 577, octaves: 3, lacunarity: 1.95f, persistence: 0.45f);
        var oceanRipple = FractalValueNoise2D(wx * 0.0075f, wz * 0.0075f, seed + 1301, octaves: 2, lacunarity: 2.0f, persistence: 0.5f);

        var grassyHeight = 58f + macro * 10f + detail * 3.5f;
        var desertHeight = 55f + macro * 6.5f + detail * 2.2f + dunes * 3.8f;
        var landHeight = Lerp(grassyHeight, desertHeight, desertWeight);
        var coastErosionNoise = FractalValueNoise2D(wx * 0.013f, wz * 0.013f, seed + 2711, octaves: 2, lacunarity: 2.0f, persistence: 0.5f);
        var coastErosion01 = Math.Clamp((coastErosionNoise + 1f) * 0.5f, 0f, 1f);
        var coastErosionStrength = Lerp(2.5f, 5.5f, coastErosion01);
        landHeight -= ComputeCoastBlend(oceanWeight) * coastErosionStrength;
        var oceanFloorHeight = (seaLevel - 1f) + oceanRipple * 0.45f;
        var rawHeight = Lerp(landHeight, oceanFloorHeight, oceanWeight);

        return Math.Clamp((int)MathF.Round(rawHeight), 10, maxHeight);
    }

    private static int ComputePoolDepth(int wx, int wz, int surface, float desertWeight, int seed, int seaLevel)
    {
        if (surface <= seaLevel + 2)
            return 0;

        var broad = FractalValueNoise2D(wx * 0.021f, wz * 0.021f, seed + 4241, octaves: 2, lacunarity: 2.0f, persistence: 0.5f);
        var detail = FractalValueNoise2D(wx * 0.067f, wz * 0.067f, seed + 8819, octaves: 1, lacunarity: 2.0f, persistence: 0.5f);
        var signal = broad * 0.78f + detail * 0.22f;
        var threshold = Lerp(0.72f, 0.79f, desertWeight);

        if (signal <= threshold)
            return 0;

        var normalized = (signal - threshold) / MathF.Max(0.001f, 1f - threshold);
        var depth = 1 + (int)MathF.Round(normalized * 3f);
        return Math.Clamp(depth, 1, 4);
    }

    private static float ComputeRawDesertClimateSignal(int wx, int wz, int seed)
    {
        var climate = FractalValueNoise2D(wx * 0.00125f, wz * 0.00125f, seed + 173, octaves: 3, lacunarity: 2.02f, persistence: 0.5f);
        var warp = FractalValueNoise2D(wx * 0.0024f, wz * 0.0024f, seed + 281, octaves: 2, lacunarity: 2.0f, persistence: 0.5f) * 0.05f;
        return climate + warp;
    }

    private static float ComputeRawOceanClimateSignal(int wx, int wz, int seed)
    {
        var continental = FractalValueNoise2D(wx * 0.00085f, wz * 0.00085f, seed + 701, octaves: 3, lacunarity: 2.0f, persistence: 0.5f);
        var warp = FractalValueNoise2D(wx * 0.0017f, wz * 0.0017f, seed + 977, octaves: 2, lacunarity: 2.0f, persistence: 0.5f) * 0.08f;
        return continental + warp;
    }

    public float GetDesertWeightAt(int wx, int wz)
    {
        var seed = Meta.Seed == 0 ? 1337 : Meta.Seed;
        GetBiomeWeightsAt(wx, wz, seed, out var desertWeight, out _);
        return desertWeight;
    }

    public float GetOceanWeightAt(int wx, int wz)
    {
        var seed = Meta.Seed == 0 ? 1337 : Meta.Seed;
        GetBiomeWeightsAt(wx, wz, seed, out _, out var oceanWeight);
        return oceanWeight;
    }

    private void GetBiomeWeightsAt(int wx, int wz, int seed, out float desertWeight, out float oceanWeight)
    {
        desertWeight = ComputeDesertWeight(wx, wz, seed);
        oceanWeight = ComputeOceanWeight(wx, wz, seed);
        ApplyBiomeAnchors(wx, wz, seed, ref desertWeight, ref oceanWeight);
    }

    private void ApplyBiomeAnchors(int wx, int wz, int seed, ref float desertWeight, ref float oceanWeight)
    {
        var width = Math.Max(1, Meta.Size.Width);
        var depth = Math.Max(1, Meta.Size.Depth);
        var minDim = MathF.Min(width, depth);
        if (minDim <= 0f)
            return;

        var marginX = Math.Max(48, width / 12);
        var marginZ = Math.Max(48, depth / 12);

        GetSeededAnchor(seed, width, depth, marginX, marginZ, salt: 97, out var oceanAx, out var oceanAz);
        GetSeededAnchor(seed, width, depth, marginX, marginZ, salt: 191, out var oceanBx, out var oceanBz);
        GetSeededAnchor(seed, width, depth, marginX, marginZ, salt: 313, out var desertAx, out var desertAz);

        var minSeparation = MathF.Max(128f, minDim * 0.18f);
        var minSeparationSq = minSeparation * minSeparation;
        if (DistanceSq(desertAx, desertAz, oceanAx, oceanAz) < minSeparationSq
            || DistanceSq(desertAx, desertAz, oceanBx, oceanBz) < minSeparationSq)
        {
            desertAx = (desertAx + Math.Max(64, width / 3)) % width;
            desertAz = (desertAz + Math.Max(64, depth / 4)) % depth;
        }

        var oceanRadius = MathF.Max(180f, minDim * 0.17f);
        var desertRadius = MathF.Max(150f, minDim * 0.13f);

        var oceanAnchor = MathF.Max(
            ComputeAnchorInfluence(wx, wz, oceanAx, oceanAz, oceanRadius),
            ComputeAnchorInfluence(wx, wz, oceanBx, oceanBz, oceanRadius * 0.9f));
        var desertAnchor = ComputeAnchorInfluence(wx, wz, desertAx, desertAz, desertRadius);

        oceanWeight = Math.Clamp(MathF.Max(oceanWeight * 0.90f + oceanAnchor * 0.80f, oceanAnchor * 0.78f), 0f, 1f);
        desertWeight = Math.Clamp(MathF.Max(desertWeight * 0.88f + desertAnchor * 0.76f, desertAnchor * 0.72f), 0f, 1f);

        if (desertAnchor > 0.85f)
            oceanWeight = MathF.Min(oceanWeight, 0.46f);
        if (oceanAnchor > 0.85f)
            desertWeight *= 0.25f;

        desertWeight *= 1f - oceanWeight * 0.70f;
    }

    private static float DistanceSq(int ax, int az, int bx, int bz)
    {
        var dx = ax - bx;
        var dz = az - bz;
        return dx * dx + dz * dz;
    }

    private static void GetSeededAnchor(int seed, int width, int depth, int marginX, int marginZ, int salt, out int x, out int z)
    {
        var x01 = Math.Clamp((HashNoise2D(101 + salt, seed + salt * 13, seed + salt * 97) + 1f) * 0.5f, 0f, 1f);
        var z01 = Math.Clamp((HashNoise2D(211 + salt, seed + salt * 29, seed + salt * 151) + 1f) * 0.5f, 0f, 1f);

        var spanX = Math.Max(1, width - marginX * 2 - 1);
        var spanZ = Math.Max(1, depth - marginZ * 2 - 1);

        x = marginX + (int)MathF.Round(x01 * spanX);
        z = marginZ + (int)MathF.Round(z01 * spanZ);

        x = Math.Clamp(x, 0, Math.Max(0, width - 1));
        z = Math.Clamp(z, 0, Math.Max(0, depth - 1));
    }

    private static float ComputeAnchorInfluence(int wx, int wz, int anchorX, int anchorZ, float radius)
    {
        if (radius <= 0f)
            return 0f;

        var dx = wx - anchorX;
        var dz = wz - anchorZ;
        var dist = MathF.Sqrt(dx * dx + dz * dz);
        if (dist >= radius)
            return 0f;

        var t = 1f - dist / radius;
        var eased = SmoothStep(Math.Clamp(t, 0f, 1f));
        return eased * eased;
    }

    public string GetBiomeNameAt(int wx, int wz)
    {
        var oceanWeight = GetOceanWeightAt(wx, wz);
        if (oceanWeight >= OceanBiomeThreshold)
            return "Ocean";
        var desertWeight = GetDesertWeightAt(wx, wz);
        var effectiveDesert = desertWeight * (1f - oceanWeight * 0.9f);
        return effectiveDesert >= DesertBiomeThreshold ? "Desert" : "Grasslands";
    }

    private static bool ShouldCarveCave(int wx, int wy, int wz, int surface, int seed)
    {
        var depth = surface - wy;
        if (depth < 6 || wy <= 3)
            return false;

        // Two fields mixed together form blob-like cave pockets while remaining deterministic.
        var coarse = ValueNoise3D(wx * 0.035f, wy * 0.028f, wz * 0.035f, seed + 4099);
        var detail = ValueNoise3D(wx * 0.082f, wy * 0.071f, wz * 0.082f, seed + 8221);
        var shape = coarse * 0.74f + detail * 0.26f;

        var depthBias = Math.Clamp((depth - 12f) / 96f, 0f, 1f) * 0.16f;
        var threshold = 0.68f - depthBias;
        return shape > threshold;
    }

    private static float FractalValueNoise2D(float x, float z, int seed, int octaves, float lacunarity, float persistence)
    {
        var value = 0f;
        var amplitude = 1f;
        var frequency = 1f;
        var amplitudeSum = 0f;

        for (var i = 0; i < octaves; i++)
        {
            value += ValueNoise2D(x * frequency, z * frequency, seed + i * 1013) * amplitude;
            amplitudeSum += amplitude;
            amplitude *= persistence;
            frequency *= lacunarity;
        }

        if (amplitudeSum <= 0f)
            return 0f;

        return value / amplitudeSum;
    }

    private static float ValueNoise2D(float x, float z, int seed)
    {
        var x0 = (int)MathF.Floor(x);
        var z0 = (int)MathF.Floor(z);
        var x1 = x0 + 1;
        var z1 = z0 + 1;

        var tx = SmoothStep(x - x0);
        var tz = SmoothStep(z - z0);

        var v00 = HashNoise2D(x0, z0, seed);
        var v10 = HashNoise2D(x1, z0, seed);
        var v01 = HashNoise2D(x0, z1, seed);
        var v11 = HashNoise2D(x1, z1, seed);

        var a = Lerp(v00, v10, tx);
        var b = Lerp(v01, v11, tx);
        return Lerp(a, b, tz);
    }

    private static float ValueNoise3D(float x, float y, float z, int seed)
    {
        var x0 = (int)MathF.Floor(x);
        var y0 = (int)MathF.Floor(y);
        var z0 = (int)MathF.Floor(z);
        var x1 = x0 + 1;
        var y1 = y0 + 1;
        var z1 = z0 + 1;

        var tx = SmoothStep(x - x0);
        var ty = SmoothStep(y - y0);
        var tz = SmoothStep(z - z0);

        var c000 = HashNoise3D(x0, y0, z0, seed);
        var c100 = HashNoise3D(x1, y0, z0, seed);
        var c010 = HashNoise3D(x0, y1, z0, seed);
        var c110 = HashNoise3D(x1, y1, z0, seed);
        var c001 = HashNoise3D(x0, y0, z1, seed);
        var c101 = HashNoise3D(x1, y0, z1, seed);
        var c011 = HashNoise3D(x0, y1, z1, seed);
        var c111 = HashNoise3D(x1, y1, z1, seed);

        var x00 = Lerp(c000, c100, tx);
        var x10 = Lerp(c010, c110, tx);
        var x01 = Lerp(c001, c101, tx);
        var x11 = Lerp(c011, c111, tx);
        var y0Lerp = Lerp(x00, x10, ty);
        var y1Lerp = Lerp(x01, x11, ty);
        return Lerp(y0Lerp, y1Lerp, tz);
    }

    private static float HashNoise2D(int x, int z, int seed)
    {
        unchecked
        {
            var h = (uint)seed;
            h ^= (uint)x * 0x9E3779B9u;
            h ^= (uint)z * 0x85EBCA6Bu;
            h ^= h >> 16;
            h *= 0x7FEB352Du;
            h ^= h >> 15;
            h *= 0x846CA68Bu;
            h ^= h >> 16;
            return (h & 0x00FFFFFFu) / 8388607.5f - 1f;
        }
    }

    public int SaveAllLoadedChunks()
    {
        if (!Directory.Exists(ChunksDir))
            Directory.CreateDirectory(ChunksDir);

        List<VoxelChunkData> chunksToSave;
        lock (_chunksLock)
            chunksToSave = _chunks.Values.ToList();

        var savedCount = 0;
        foreach (var chunk in chunksToSave)
        {
            try
            {
                var path = Path.Combine(ChunksDir, $"chunk_{chunk.Coord.X}_{chunk.Coord.Y}_{chunk.Coord.Z}.bin");
                chunk.Save(path);
                savedCount++;
            }
            catch (Exception ex)
            {
                _log.Warn($"Failed to save chunk {chunk.Coord}: {ex.Message}");
            }
        }

        return savedCount;
    }

    private static float HashNoise3D(int x, int y, int z, int seed)
    {
        unchecked
        {
            var h = (uint)seed;
            h ^= (uint)x * 0x9E3779B9u;
            h ^= (uint)y * 0xC2B2AE35u;
            h ^= (uint)z * 0x85EBCA6Bu;
            h ^= h >> 16;
            h *= 0x7FEB352Du;
            h ^= h >> 15;
            h *= 0x846CA68Bu;
            h ^= h >> 16;
            return (h & 0x00FFFFFFu) / 8388607.5f - 1f;
        }
    }

    private static float SmoothStep(float t) => t * t * (3f - 2f * t);
    private static float Lerp(float a, float b, float t) => a + (b - a) * t;

    public void UnloadChunks(HashSet<ChunkCoord> keep, Action<ChunkCoord, byte[]>? onSaveRequest = null)
    {
        List<(ChunkCoord coord, byte[]? blocks)> toSave;
        List<ChunkCoord> toRemove;
        
        lock (_chunksLock)
        {
            // Create snapshot of keys to avoid modification during enumeration
            var keys = _chunks.Keys.ToArray();
            toRemove = new List<ChunkCoord>();
            
            foreach (var coord in keys)
            {
                if (!keep.Contains(coord))
                    toRemove.Add(coord);
            }

            // Collect save data while still under lock
            toSave = new List<(ChunkCoord, byte[]?)>();
            foreach (var coord in toRemove)
            {
                if (_chunks.TryGetValue(coord, out var chunk) && chunk.NeedsSave)
                {
                    // Create a copy of blocks for async save
                    var blocksCopy = new byte[chunk.Blocks.Length];
                    Array.Copy(chunk.Blocks, blocksCopy, blocksCopy.Length);
                    toSave.Add((coord, blocksCopy));
                }
            }

            // Remove chunks from memory while still under lock
            foreach (var coord in toRemove)
            {
                _chunks.Remove(coord);
                ChunkSeamRegistry.Unregister(coord);
            }
        }

        // Call save requests OUTSIDE the lock (prefer pattern A)
        if (onSaveRequest != null)
        {
            foreach (var (coord, blocks) in toSave)
            {
                if (blocks != null)
                    onSaveRequest(coord, blocks);
            }
        }
    }

    public static ChunkCoord WorldToChunk(int wx, int wy, int wz, out int lx, out int ly, out int lz)
    {
        var cx = FloorDiv(wx, VoxelChunkData.ChunkSizeX);
        var cy = FloorDiv(wy, VoxelChunkData.ChunkSizeY);
        var cz = FloorDiv(wz, VoxelChunkData.ChunkSizeZ);

        lx = Mod(wx, VoxelChunkData.ChunkSizeX);
        ly = Mod(wy, VoxelChunkData.ChunkSizeY);
        lz = Mod(wz, VoxelChunkData.ChunkSizeZ);

        return new ChunkCoord(cx, cy, cz);
    }

    private void LoadChunks()
    {
        if (!Directory.Exists(ChunksDir))
        {
            _log.Warn($"Chunks folder missing: {ChunksDir}");
            return;
        }

        var files = Directory.GetFiles(ChunksDir, "chunk_*.bin");
        foreach (var file in files)
        {
            var fileName = Path.GetFileName(file);
            ChunkCoord? coord = null;
            if (fileName != null)
                coord = TryParseCoord(fileName);

            var chunk = new VoxelChunkData(coord ?? new ChunkCoord(0, 0, 0));
            chunk.Load(file);
            if (chunk == null)
                continue;

            if (_chunks.ContainsKey(chunk.Coord))
            {
                _log.Warn($"Duplicate chunk at {chunk.Coord} in {file}");
                continue;
            }

            if (!IsChunkYInRange(chunk.Coord.Y) || !IsChunkWithinWorldXZ(chunk.Coord))
                continue;

            _chunks[chunk.Coord] = chunk;
            ChunkSeamRegistry.Register(chunk.Coord, ChunkSurfaceProfile.FromChunk(chunk));
        }

        if (_chunks.Count == 0)
            _log.Warn($"No chunks loaded from {ChunksDir}");
    }

    private VoxelChunkData? TryLoadChunk(ChunkCoord coord)
    {
        if (!IsChunkYInRange(coord.Y) || !IsChunkWithinWorldXZ(coord))
            return null;

        var path = Path.Combine(ChunksDir, $"chunk_{coord.X}_{coord.Y}_{coord.Z}.bin");
        if (!File.Exists(path))
            return null;
        var chunk = new VoxelChunkData(coord);
        chunk.Load(path);
        return chunk;
    }

    private bool IsChunkYInRange(int cy) => cy >= 0 && cy <= _maxChunkY;

    private bool IsChunkWithinWorldXZ(ChunkCoord coord)
    {
        var minX = coord.X * VoxelChunkData.ChunkSizeX;
        var minZ = coord.Z * VoxelChunkData.ChunkSizeZ;
        var maxX = minX + VoxelChunkData.ChunkSizeX - 1;
        var maxZ = minZ + VoxelChunkData.ChunkSizeZ - 1;
        return maxX >= 0
            && maxZ >= 0
            && minX < Meta.Size.Width
            && minZ < Meta.Size.Depth;
    }

    private static VoxelChunkData CreateVoidChunk(ChunkCoord coord)
    {
        var chunk = new VoxelChunkData(coord)
        {
            IsDirty = false,
            NeedsSave = false
        };
        return chunk;
    }

    private static ChunkCoord? TryParseCoord(string fileName)
    {
        var match = ChunkNameRegex.Match(fileName);
        if (!match.Success)
            return null;

        if (!int.TryParse(match.Groups["x"].Value, out var x))
            return null;
        if (!int.TryParse(match.Groups["y"].Value, out var y))
            return null;
        if (!int.TryParse(match.Groups["z"].Value, out var z))
            return null;

        return new ChunkCoord(x, y, z);
    }

    private static int FloorDiv(int value, int divisor)
    {
        var q = value / divisor;
        var r = value % divisor;
        if (r != 0 && ((r > 0) != (divisor > 0)))
            q--;
        return q;
    }

    private static int Mod(int value, int divisor)
    {
        var m = value % divisor;
        if (m < 0)
            m += Math.Abs(divisor);
        return m;
    }
}
