using System;

namespace LatticeVeilMonoGame.Core;

/// <summary>
/// Deterministic superflat generator (current "flat_v1" worlds).
/// Produces a flat grass surface at a stable height and optionally adds caves, ores, and simple trees.
/// </summary>
public sealed class SuperflatWorldGenerator : IChunkGenerator
{
    private readonly FastNoiseGenerator _noise;
    private readonly Logger _log;
    private readonly int _seed;
    private readonly Settings _settings;
    private bool _disposed;

    // Block type constants using existing BlockIds
    private const byte BLOCK_AIR = BlockIds.Air;
    private const byte BLOCK_STONE = BlockIds.Stone;
    private const byte BLOCK_DIRT = BlockIds.Dirt;
    private const byte BLOCK_GRASS = BlockIds.Grass;
    private const byte BLOCK_WOOD = BlockIds.Wood;
    private const byte BLOCK_LEAVES = BlockIds.Leaves;
    private const byte BLOCK_COAL_ORE = BlockIds.CoalOre;
    private const byte BLOCK_IRON_ORE = BlockIds.IronOre;
    private const byte BLOCK_GOLD_ORE = BlockIds.GoldOre;
    private const byte BLOCK_DIAMOND = BlockIds.Diamond;
    private const byte BLOCK_NULLROCK = BlockIds.Nullrock;

    public readonly struct Settings
    {
        public Settings(int worldHeight, int seaLevel, bool generateCaves, bool generateOres, bool generateTrees)
        {
            WorldHeight = worldHeight;
            SeaLevel = seaLevel;
            GenerateCaves = generateCaves;
            GenerateOres = generateOres;
            GenerateTrees = generateTrees;

            // Keep superflat height stable across world sizes.
            SurfaceY = Math.Clamp(worldHeight / 2, seaLevel + 4, worldHeight - 8);
            CaveThreshold = 0.30f;
        }

        public int WorldHeight { get; }
        public int SeaLevel { get; }
        public int SurfaceY { get; }
        public float CaveThreshold { get; }
        public bool GenerateCaves { get; }
        public bool GenerateOres { get; }
        public bool GenerateTrees { get; }
    }

    public SuperflatWorldGenerator(int seed, Settings settings, Logger log)
    {
        _seed = seed;
        _settings = settings;
        _log = log;
        _noise = new FastNoiseGenerator(seed, log);

        _log.Info($"SuperflatWorldGenerator initialized. SurfaceY={_settings.SurfaceY}, WorldHeight={_settings.WorldHeight}");
    }

    public VoxelChunkData GenerateChunk(ChunkCoord coord)
    {
        var chunk = new VoxelChunkData(coord);
        var sizeX = VoxelChunkData.ChunkSizeX;
        var sizeY = VoxelChunkData.ChunkSizeY;
        var sizeZ = VoxelChunkData.ChunkSizeZ;

        var originX = coord.X * sizeX;
        var originY = coord.Y * sizeY;
        var originZ = coord.Z * sizeZ;

        // Base layers
        for (int x = 0; x < sizeX; x++)
        {
            for (int z = 0; z < sizeZ; z++)
            {
                for (int y = 0; y < sizeY; y++)
                {
                    var worldY = originY + y;
                    if (worldY < 0 || worldY >= _settings.WorldHeight)
                        continue;

                    byte b;
                    if (worldY == 0)
                        b = BLOCK_NULLROCK;
                    else if (worldY < _settings.SurfaceY - 4)
                        b = BLOCK_STONE;
                    else if (worldY < _settings.SurfaceY)
                        b = BLOCK_DIRT;
                    else if (worldY == _settings.SurfaceY)
                        b = BLOCK_GRASS;
                    else
                        b = BLOCK_AIR;

                    chunk.SetBlockRaw(x, y, z, b);
                }
            }
        }

        if (_settings.GenerateCaves)
            GenerateCaves(chunk, originX, originY, originZ, sizeX, sizeY, sizeZ);

        if (_settings.GenerateOres)
            GenerateOres(chunk, originX, originY, originZ, sizeX, sizeY, sizeZ);

        if (_settings.GenerateTrees)
            GenerateTrees(chunk, coord, originX, originY, originZ, sizeX, sizeY, sizeZ);

        _log.Debug($"Generated superflat chunk at {coord}");
        return chunk;
    }

    private void GenerateCaves(VoxelChunkData chunk, int originX, int originY, int originZ, int sizeX, int sizeY, int sizeZ)
    {
        for (int x = 0; x < sizeX; x++)
        {
            for (int y = 0; y < sizeY; y++)
            {
                for (int z = 0; z < sizeZ; z++)
                {
                    var currentBlock = chunk.GetBlockRaw(x, y, z);
                    if (currentBlock == BLOCK_AIR || currentBlock == BLOCK_NULLROCK)
                        continue;

                    var worldX = originX + x;
                    var worldY = originY + y;
                    var worldZ = originZ + z;

                    // Keep caves below the surface band so we don't swiss-cheese the top layers.
                    if (worldY >= _settings.SurfaceY - 2)
                        continue;

                    var density = _noise.GetCaveDensity(worldX, worldY, worldZ);
                    if (density < _settings.CaveThreshold)
                        chunk.SetBlockRaw(x, y, z, BLOCK_AIR);
                }
            }
        }
    }

    private void GenerateOres(VoxelChunkData chunk, int originX, int originY, int originZ, int sizeX, int sizeY, int sizeZ)
    {
        for (int x = 0; x < sizeX; x++)
        {
            for (int y = 0; y < sizeY; y++)
            {
                for (int z = 0; z < sizeZ; z++)
                {
                    if (chunk.GetBlockRaw(x, y, z) != BLOCK_STONE)
                        continue;

                    var worldX = originX + x;
                    var worldY = originY + y;
                    var worldZ = originZ + z;

                    var oreDensity = _noise.GetOreDensity(worldX, worldY, worldZ);
                    if (oreDensity <= 0.86f)
                        continue;

                    var ore = GetOreTypeForDepth(worldY);
                    if (ore != BLOCK_AIR)
                        chunk.SetBlockRaw(x, y, z, ore);
                }
            }
        }
    }

    private void GenerateTrees(VoxelChunkData chunk, ChunkCoord coord, int originX, int originY, int originZ, int sizeX, int sizeY, int sizeZ)
    {
        // Trees only exist on the surface Y layer.
        var surfaceChunkY = _settings.SurfaceY / sizeY;
        if (coord.Y != surfaceChunkY)
            return;

        var localSurfaceY = _settings.SurfaceY - originY;
        if (localSurfaceY < 0 || localSurfaceY >= sizeY)
            return;

        // Biome-aware flatlands trees:
        // - Forest: denser
        // - Hills/Grasslands: sparse
        // - Desert/Ocean: none
        for (var x = 2; x < sizeX - 2; x++)
        {
            for (var z = 2; z < sizeZ - 2; z++)
            {
                var worldX = originX + x;
                var worldZ = originZ + z;
                var sample = BiomeService.GetSampleAt(_seed, worldX, worldZ);
                if (!TryGetFlatTreePlacementSettings(sample.BiomeId, out var cellSize, out var chance, out var minHumidity))
                    continue;

                if (sample.Humidity < minHumidity || !ShouldPlaceTreeCandidate(worldX, worldZ, cellSize, chance))
                    continue;

                if (HasNearbyTrunk(chunk, x, localSurfaceY, z, 3))
                    continue;

                TryPlaceSimpleTree(chunk, x, localSurfaceY, z);
            }
        }
    }

    private static bool TryGetFlatTreePlacementSettings(BiomeId biomeId, out int cellSize, out float chance, out float minHumidity)
    {
        switch (biomeId)
        {
            case BiomeId.Forest:
                cellSize = 6;
                chance = 0.72f;
                minHumidity = 0.36f;
                return true;
            case BiomeId.Hills:
                cellSize = 8;
                chance = 0.48f;
                minHumidity = 0.24f;
                return true;
            case BiomeId.Grasslands:
                cellSize = 9;
                chance = 0.34f;
                minHumidity = 0.30f;
                return true;
            default:
                cellSize = 0;
                chance = 0f;
                minHumidity = 1f;
                return false;
        }
    }

    private bool ShouldPlaceTreeCandidate(int worldX, int worldZ, int cellSize, float chance)
    {
        if (cellSize < 3 || chance <= 0f)
            return false;

        var cellX = FloorDiv(worldX, cellSize);
        var cellZ = FloorDiv(worldZ, cellSize);
        var hash = Hash2D(cellX, cellZ, _seed + 911);
        var innerSpan = Math.Max(1, cellSize - 2);
        var candidateX = cellX * cellSize + 1 + (int)(hash % (uint)innerSpan);
        var candidateZ = cellZ * cellSize + 1 + (int)((hash >> 8) % (uint)innerSpan);
        if (worldX != candidateX || worldZ != candidateZ)
            return false;

        var roll = ((hash >> 16) & 0xFFFFu) / 65535f;
        return roll < chance;
    }

    private static bool HasNearbyTrunk(VoxelChunkData chunk, int x, int groundY, int z, int radius)
    {
        var minX = Math.Max(1, x - radius);
        var maxX = Math.Min(VoxelChunkData.ChunkSizeX - 2, x + radius);
        var minZ = Math.Max(1, z - radius);
        var maxZ = Math.Min(VoxelChunkData.ChunkSizeZ - 2, z + radius);
        var trunkY = groundY + 1;

        for (var nx = minX; nx <= maxX; nx++)
        {
            for (var nz = minZ; nz <= maxZ; nz++)
            {
                if (chunk.GetBlockRaw(nx, trunkY, nz) == BLOCK_WOOD)
                    return true;
            }
        }

        return false;
    }

    private void TryPlaceSimpleTree(VoxelChunkData chunk, int x, int groundY, int z)
    {
        // Ensure grass and headroom.
        if (chunk.GetBlockRaw(x, groundY, z) != BLOCK_GRASS)
            return;

        if (x <= 2 || x >= VoxelChunkData.ChunkSizeX - 3 || z <= 2 || z >= VoxelChunkData.ChunkSizeZ - 3)
            return;

        // Needs: trunk + fuller canopy
        if (groundY + 6 >= VoxelChunkData.ChunkSizeY)
            return;

        // Trunk
        chunk.SetBlockRaw(x, groundY + 1, z, BLOCK_WOOD);
        chunk.SetBlockRaw(x, groundY + 2, z, BLOCK_WOOD);
        chunk.SetBlockRaw(x, groundY + 3, z, BLOCK_WOOD);
        chunk.SetBlockRaw(x, groundY + 4, z, BLOCK_WOOD);

        // Fuller canopy profile.
        PlaceLeafLayer(chunk, x, groundY + 3, z, radius: 2, includeCorners: false);
        PlaceLeafLayer(chunk, x, groundY + 4, z, radius: 2, includeCorners: false);
        PlaceLeafLayer(chunk, x, groundY + 5, z, radius: 1, includeCorners: true);
        SetLeafIfReplaceable(chunk, x, groundY + 6, z);
        SetLeafIfReplaceable(chunk, x - 1, groundY + 6, z);
        SetLeafIfReplaceable(chunk, x + 1, groundY + 6, z);
        SetLeafIfReplaceable(chunk, x, groundY + 6, z - 1);
        SetLeafIfReplaceable(chunk, x, groundY + 6, z + 1);
    }

    private void PlaceLeafLayer(VoxelChunkData chunk, int centerX, int y, int centerZ, int radius, bool includeCorners)
    {
        for (var dx = -radius; dx <= radius; dx++)
        {
            for (var dz = -radius; dz <= radius; dz++)
            {
                if (!includeCorners && Math.Abs(dx) == radius && Math.Abs(dz) == radius)
                    continue;
                SetLeafIfReplaceable(chunk, centerX + dx, y, centerZ + dz);
            }
        }
    }

    private void SetLeafIfReplaceable(VoxelChunkData chunk, int x, int y, int z)
    {
        if (x < 0 || x >= VoxelChunkData.ChunkSizeX || z < 0 || z >= VoxelChunkData.ChunkSizeZ || y < 0 || y >= VoxelChunkData.ChunkSizeY)
            return;

        var existing = chunk.GetBlockRaw(x, y, z);
        if (existing == BLOCK_AIR || existing == BLOCK_LEAVES)
            chunk.SetBlockRaw(x, y, z, BLOCK_LEAVES);
    }

    private static byte GetOreTypeForDepth(int worldY)
    {
        if (worldY < 5) return BLOCK_DIAMOND;
        if (worldY < 20) return BLOCK_GOLD_ORE;
        if (worldY < 40) return BLOCK_IRON_ORE;
        if (worldY < 60) return BLOCK_COAL_ORE;
        return BLOCK_AIR;
    }

    private static uint Hash2D(int x, int z, int seed)
    {
        unchecked
        {
            uint h = (uint)seed;
            h ^= (uint)x * 0x9E3779B9u;
            h ^= (uint)z * 0xC2B2AE35u;
            h ^= (h << 13) | (h >> 19);
            h *= 0x85EBCA6Bu;
            h ^= h >> 16;
            return h;
        }
    }

    private static int FloorDiv(int value, int divisor)
    {
        var q = value / divisor;
        var r = value % divisor;
        if (r != 0 && ((r > 0) != (divisor > 0)))
            q--;
        return q;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _noise.Dispose();
        _disposed = true;
        _log.Debug("SuperflatWorldGenerator disposed");
    }
}
