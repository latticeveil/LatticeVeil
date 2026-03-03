using System;
using LatticeVeilMonoGame.Core;

namespace LatticeVeilMonoGame.Core;

/// <summary>
/// Basic world generator using FastNoiseGenerator
/// Provides terrain, caves, biomes, and structure generation
/// </summary>
public sealed class BasicWorldGenerator : IChunkGenerator
{
    private readonly FastNoiseGenerator _noiseGenerator;
    private readonly Logger _log;
    private readonly int _seed;
    private WorldSettings _settings;
    private bool _disposed;

    // Block type constants using existing BlockIds
    private const byte BLOCK_AIR = BlockIds.Air;
    private const byte BLOCK_STONE = BlockIds.Stone;
    private const byte BLOCK_DIRT = BlockIds.Dirt;
    private const byte BLOCK_GRASS = BlockIds.Grass;
    private const byte BLOCK_SAND = BlockIds.Sand;
    private const byte BLOCK_WATER = BlockIds.Water;
    private const byte BLOCK_WOOD = BlockIds.Wood;
    private const byte BLOCK_LEAVES = BlockIds.Leaves;
    private const byte BLOCK_COAL_ORE = BlockIds.CoalOre;
    private const byte BLOCK_IRON_ORE = BlockIds.IronOre;
    private const byte BLOCK_GOLD_ORE = BlockIds.GoldOre;
    private const byte BLOCK_DIAMOND = BlockIds.Diamond;
    private const byte BLOCK_NULLROCK = BlockIds.Nullrock; // Bottom unbreakable layer

    public struct WorldSettings
    {
        public int ChunkSize { get; set; }
        public int WorldHeight { get; set; }
        public float TerrainFrequency { get; set; }
        public float CaveFrequency { get; set; }
        public float CaveThreshold { get; set; }
        public int SeaLevel { get; set; }
        public bool GenerateCaves { get; set; }
        public bool GenerateOres { get; set; }
        public bool GenerateStructures { get; set; }
        public bool GenerateTrees { get; set; }
    }

    public BasicWorldGenerator(int seed, WorldSettings settings, Logger log)
    {
        _seed = seed;
        _settings = settings;
        _log = log;
        
        // Defensive guard: prevent void chunks from invalid frequency settings
        bool settingsModified = false;
        if (_settings.TerrainFrequency <= 0)
        {
            _settings.TerrainFrequency = 0.01f;
            settingsModified = true;
        }
        if (_settings.CaveFrequency <= 0)
        {
            _settings.CaveFrequency = 0.05f;
            settingsModified = true;
        }
        if (_settings.CaveThreshold <= 0)
        {
            _settings.CaveThreshold = 0.30f;
            settingsModified = true;
        }
        else
        {
            _settings.CaveThreshold = Math.Clamp(_settings.CaveThreshold, 0.05f, 0.95f);
        }
        
        if (settingsModified)
        {
            _log.Warn($"BasicWorldGenerator corrected invalid frequency settings: TerrainFrequency={_settings.TerrainFrequency}, CaveFrequency={_settings.CaveFrequency}, CaveThreshold={_settings.CaveThreshold}");
        }
        
        _noiseGenerator = new FastNoiseGenerator(seed, log);
        
        _log.Info($"BasicWorldGenerator initialized with seed: {seed}");
        _log.Info($"Settings: ChunkSize={_settings.ChunkSize}, WorldHeight={_settings.WorldHeight}, TerrainFrequency={_settings.TerrainFrequency}, CaveFrequency={_settings.CaveFrequency}, CaveThreshold={_settings.CaveThreshold}");
    }

    /// <summary>
    /// Generate a complete chunk with terrain, caves, ores, and structures
    /// </summary>
    public VoxelChunkData GenerateChunk(ChunkCoord coord)
    {
        var chunk = new VoxelChunkData(coord);
        var sizeX = VoxelChunkData.ChunkSizeX;
        var sizeY = VoxelChunkData.ChunkSizeY;
        var sizeZ = VoxelChunkData.ChunkSizeZ;

        var originX = coord.X * sizeX;
        var originY = coord.Y * sizeY;
        var originZ = coord.Z * sizeZ;

        // Generate base terrain
        GenerateTerrain(chunk, originX, originY, originZ, sizeX, sizeY, sizeZ);

        // Generate caves if enabled
        if (_settings.GenerateCaves)
        {
            GenerateCaves(chunk, originX, originY, originZ, sizeX, sizeY, sizeZ);
        }

        // Generate ores if enabled
        if (_settings.GenerateOres)
        {
            GenerateOres(chunk, originX, originY, originZ, sizeX, sizeY, sizeZ);
        }

        // Generate structures if enabled
        if (_settings.GenerateStructures)
        {
            GenerateStructures(chunk, originX, originY, originZ, sizeX, sizeY, sizeZ);
        }

        if (_settings.GenerateTrees)
        {
            GenerateTrees(chunk, originX, originY, originZ, sizeX, sizeY, sizeZ);
        }

        // Place water
        PlaceWater(chunk, originX, originY, originZ, sizeX, sizeY, sizeZ);

        // Optional validation: expensive (allocates + copies Blocks). Enable only when diagnosing.
        if (Environment.GetEnvironmentVariable("LV_VALIDATE_CHUNKS") == "1")
        {
            var blocks = chunk.Blocks;
            var nonAirCount = 0;
            var nonNullrockCount = 0;
            for (int i = 0; i < blocks.Length; i++)
            {
                var b = blocks[i];
                if (b != BLOCK_AIR)
                {
                    nonAirCount++;
                    if (b != BLOCK_NULLROCK)
                        nonNullrockCount++;
                }
            }

            if (nonNullrockCount < 100)
                _log.Warn($"GeneratedChunkValidation {coord}: non-air={nonAirCount}, non-nullrock={nonNullrockCount} - POSSIBLE VOID CHUNK");
            else
                _log.Debug($"GeneratedChunkValidation {coord}: non-air={nonAirCount}, non-nullrock={nonNullrockCount} - OK");
        }

        _log.Debug($"Generated chunk at {coord}");
        return chunk;
    }

    private void GenerateTerrain(VoxelChunkData chunk, int originX, int originY, int originZ, int sizeX, int sizeY, int sizeZ)
    {
        for (int x = 0; x < sizeX; x++)
        {
            for (int z = 0; z < sizeZ; z++)
            {
                var worldX = originX + x;
                var worldZ = originZ + z;
                var biome = BiomeService.GetSampleAt(_seed, worldX, worldZ);
                var blockHeight = ComputeSurfaceHeight(worldX, worldZ, biome);
                var isDesertSurface = biome.BiomeId == BiomeId.Desert || biome.EffectiveDesertWeight >= 0.40f;

                // Fill terrain column
                for (int y = 0; y < sizeY; y++)
                {
                    var worldY = originY + y;
                    
                    if (worldY < 0 || worldY >= _settings.WorldHeight)
                        continue;

                    if (worldY > blockHeight)
                    {
                        // Above terrain - air or water
                        if (worldY <= _settings.SeaLevel)
                        {
                            chunk.SetBlockRaw(x, y, z, BLOCK_WATER);
                        }
                        else
                        {
                            chunk.SetBlockRaw(x, y, z, BLOCK_AIR);
                        }
                    }
                    else if (worldY == blockHeight)
                    {
                        // Surface block
                        if (worldY <= _settings.SeaLevel)
                        {
                            chunk.SetBlockRaw(x, y, z, BLOCK_SAND);
                        }
                        else if (isDesertSurface)
                        {
                            chunk.SetBlockRaw(x, y, z, BLOCK_SAND);
                        }
                        else
                        {
                            chunk.SetBlockRaw(x, y, z, BLOCK_GRASS);
                        }
                    }
                    else if (worldY >= blockHeight - 3 && worldY > 0)
                    {
                        chunk.SetBlockRaw(x, y, z, isDesertSurface ? BLOCK_SAND : BLOCK_DIRT);
                    }
                    else if (worldY == 0)
                    {
                        chunk.SetBlockRaw(x, y, z, BLOCK_NULLROCK);
                    }
                    else
                    {
                        chunk.SetBlockRaw(x, y, z, BLOCK_STONE);
                    }
                }
            }
        }
    }

    private void GenerateCaves(VoxelChunkData chunk, int originX, int originY, int originZ, int sizeX, int sizeY, int sizeZ)
    {
        for (int x = 0; x < sizeX; x++)
        {
            for (int y = 0; y < sizeY; y++)
            {
                for (int z = 0; z < sizeZ; z++)
                {
                    var worldX = originX + x;
                    var worldY = originY + y;
                    var worldZ = originZ + z;

                    // Skip if already air, water, or Nullrock (unbreakable bottom layer)
                    var currentBlock = chunk.GetBlockRaw(x, y, z);
                    if (currentBlock == BLOCK_AIR || currentBlock == BLOCK_WATER || currentBlock == BLOCK_NULLROCK)
                        continue;

                    // Get cave density
                    var caveDensity = _noiseGenerator.GetCaveDensity(
                        worldX,
                        worldY,
                        worldZ);

                    // Carve cave if density is below threshold
                    if (caveDensity < _settings.CaveThreshold)
                    {
                        chunk.SetBlockRaw(x, y, z, BLOCK_AIR);
                    }
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
                    var worldX = originX + x;
                    var worldY = originY + y;
                    var worldZ = originZ + z;

                    // Skip if not stone
                    if (chunk.GetBlockRaw(x, y, z) != BLOCK_STONE)
                        continue;

                    // Get ore density
                    var oreDensity = _noiseGenerator.GetOreDensity(worldX, worldY, worldZ);

                    // Place ore based on depth and density
                    var oreType = GetOreTypeForDepth(worldY);
                    if (oreType != BLOCK_AIR && oreDensity > 0.8f)
                    {
                        chunk.SetBlockRaw(x, y, z, oreType);
                    }
                }
            }
        }
    }

    private void GenerateStructures(VoxelChunkData chunk, int originX, int originY, int originZ, int sizeX, int sizeY, int sizeZ)
    {
        // Structure generation hooks are reserved for future non-tree features.
    }

    private void GenerateTrees(VoxelChunkData chunk, int originX, int originY, int originZ, int sizeX, int sizeY, int sizeZ)
    {
        for (int x = 0; x < sizeX; x++)
        {
            for (int z = 0; z < sizeZ; z++)
            {
                var worldX = originX + x;
                var worldZ = originZ + z;
                var biome = BiomeService.GetSampleAt(_seed, worldX, worldZ);
                if (!TryGetTreePlacementSettings(biome.BiomeId, out var cellSize, out var spawnChance, out var minHumidity))
                    continue;

                if (biome.Humidity < minHumidity || !ShouldPlaceTreeCandidate(worldX, worldZ, cellSize, spawnChance))
                    continue;

                // Find surface height
                var surfaceY = FindSurfaceY(worldX, worldZ);

                // Check if surface is in this chunk and suitable for tree
                var localY = surfaceY - originY;
                if (localY < 0 || localY >= sizeY - 7 || surfaceY <= _settings.SeaLevel)
                    continue;

                if (chunk.GetBlockRaw(x, localY, z) != BLOCK_GRASS || !IsTreeFootprintSafe(chunk, x, localY, z))
                    continue;

                // Place simple tree (trunk + leaves)
                PlaceSimpleTree(chunk, x, localY, z);
            }
        }
    }

    private int FindSurfaceY(int worldX, int worldZ)
    {
        var biome = BiomeService.GetSampleAt(_seed, worldX, worldZ);
        return ComputeSurfaceHeight(worldX, worldZ, biome);
    }

    private int ComputeSurfaceHeight(int worldX, int worldZ, BiomeSample biome)
    {
        // Terrain baseline from layered noise.
        var macro = _noiseGenerator.GetTerrainHeight(worldX, worldZ);
        var detail = _noiseGenerator.GetTerrainHeight(worldX + 1913, worldZ - 1171) * 0.45f;
        var ridge = _noiseGenerator.GetTerrainHeight(worldX * 0.35f, worldZ * 0.35f) * 0.30f;
        var terrainSignal = macro * 0.72f + detail * 0.20f + ridge * 0.08f;

        // Large-hills profile: stronger uplift where hills remain competitive against ocean/desert.
        var hillRidge = MathF.Abs(_noiseGenerator.GetTerrainHeight(worldX * 0.58f + 911f, worldZ * 0.58f - 127f));
        var hillMacro = MathF.Max(0f, _noiseGenerator.GetTerrainHeight(worldX * 0.14f - 337f, worldZ * 0.14f + 619f));
        var hillsGate = Math.Clamp(1f - MathF.Max(biome.OceanWeight * 1.15f, biome.EffectiveDesertWeight * 1.2f), 0f, 1f);
        var hillsFactor = SmoothStep(0.36f, 0.95f, biome.HillsWeight) * hillsGate;
        var hillsUplift = hillsFactor * (20f + hillRidge * 24f + hillMacro * 18f);
        if (biome.BiomeId == BiomeId.Hills)
            hillsUplift += 4f + hillRidge * 6f;

        var surfaceY = _settings.SeaLevel
            + (int)MathF.Round(terrainSignal * 24f)
            + (int)MathF.Round(hillsUplift)
            + (int)MathF.Round(biome.EffectiveDesertWeight * 5f)
            + (int)MathF.Round(biome.ForestWeight * 3f)
            - (int)MathF.Round(biome.OceanWeight * 20f);

        if (biome.OceanWeight >= 0.74f)
        {
            var basinDepth = (int)MathF.Round((biome.OceanWeight - 0.74f) * 30f);
            surfaceY = Math.Min(surfaceY, _settings.SeaLevel - 2 - basinDepth);
        }

        return Math.Clamp(surfaceY, 1, _settings.WorldHeight - 2);
    }

    private static bool TryGetTreePlacementSettings(BiomeId biomeId, out int cellSize, out float spawnChance, out float minHumidity)
    {
        switch (biomeId)
        {
            case BiomeId.Forest:
                cellSize = 5;
                spawnChance = 0.80f;
                minHumidity = 0.36f;
                return true;
            case BiomeId.Hills:
                cellSize = 7;
                spawnChance = 0.55f;
                minHumidity = 0.24f;
                return true;
            case BiomeId.Grasslands:
                cellSize = 8;
                spawnChance = 0.42f;
                minHumidity = 0.30f;
                return true;
            default:
                cellSize = 0;
                spawnChance = 0f;
                minHumidity = 1f;
                return false;
        }
    }

    private bool ShouldPlaceTreeCandidate(int worldX, int worldZ, int cellSize, float spawnChance)
    {
        if (cellSize < 3 || spawnChance <= 0f)
            return false;

        var cellX = FloorDiv(worldX, cellSize);
        var cellZ = FloorDiv(worldZ, cellSize);
        var hash = Hash2D(cellX, cellZ, _seed + 0x1F2E3D4C);
        var innerSpan = Math.Max(1, cellSize - 2);
        var candidateX = cellX * cellSize + 1 + (int)(hash % (uint)innerSpan);
        var candidateZ = cellZ * cellSize + 1 + (int)((hash >> 8) % (uint)innerSpan);
        if (worldX != candidateX || worldZ != candidateZ)
            return false;

        var roll = ((hash >> 16) & 0xFFFFu) / 65535f;
        return roll < spawnChance;
    }

    private static bool IsTreeFootprintSafe(VoxelChunkData chunk, int x, int groundY, int z)
    {
        if (x <= 2 || x >= VoxelChunkData.ChunkSizeX - 3 || z <= 2 || z >= VoxelChunkData.ChunkSizeZ - 3)
            return false;

        if (groundY + 6 >= VoxelChunkData.ChunkSizeY)
            return false;

        for (var y = groundY + 1; y <= groundY + 4; y++)
        {
            if (chunk.GetBlockRaw(x, y, z) != BLOCK_AIR)
                return false;
        }

        for (var nx = x - 2; nx <= x + 2; nx++)
        {
            for (var nz = z - 2; nz <= z + 2; nz++)
            {
                if (chunk.GetBlockRaw(nx, groundY + 1, nz) == BLOCK_WOOD)
                    return false;
            }
        }

        return true;
    }

    private static float SmoothStep(float edge0, float edge1, float value)
    {
        if (Math.Abs(edge1 - edge0) < float.Epsilon)
            return value >= edge1 ? 1f : 0f;

        var t = Math.Clamp((value - edge0) / (edge1 - edge0), 0f, 1f);
        return t * t * (3f - 2f * t);
    }

    private static int FloorDiv(int value, int divisor)
    {
        var q = value / divisor;
        var r = value % divisor;
        if (r != 0 && ((r > 0) != (divisor > 0)))
            q--;
        return q;
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

    private void PlaceSimpleTree(VoxelChunkData chunk, int x, int y, int z)
    {
        if (x <= 2 || x >= VoxelChunkData.ChunkSizeX - 3 || z <= 2 || z >= VoxelChunkData.ChunkSizeZ - 3)
            return;
        if (y + 6 >= VoxelChunkData.ChunkSizeY)
            return;

        // Trunk (4 blocks)
        chunk.SetBlockRaw(x, y + 1, z, BLOCK_WOOD);
        chunk.SetBlockRaw(x, y + 2, z, BLOCK_WOOD);
        chunk.SetBlockRaw(x, y + 3, z, BLOCK_WOOD);
        chunk.SetBlockRaw(x, y + 4, z, BLOCK_WOOD);

        // Fuller canopy profile:
        // - two 5x5 layers (rounded corners)
        // - one 3x3 top layer
        // - cross crown cap
        PlaceLeafLayer(chunk, x, y + 3, z, radius: 2, includeCorners: false);
        PlaceLeafLayer(chunk, x, y + 4, z, radius: 2, includeCorners: false);
        PlaceLeafLayer(chunk, x, y + 5, z, radius: 1, includeCorners: true);
        SetLeafIfReplaceable(chunk, x, y + 6, z);
        SetLeafIfReplaceable(chunk, x - 1, y + 6, z);
        SetLeafIfReplaceable(chunk, x + 1, y + 6, z);
        SetLeafIfReplaceable(chunk, x, y + 6, z - 1);
        SetLeafIfReplaceable(chunk, x, y + 6, z + 1);
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

    private void PlaceWater(VoxelChunkData chunk, int originX, int originY, int originZ, int sizeX, int sizeY, int sizeZ)
    {
        // Water is already placed in terrain generation
        // This method could be expanded for more complex water systems
    }

    private byte GetOreTypeForDepth(int worldY)
    {
        if (worldY < 5) return BLOCK_DIAMOND;
        if (worldY < 20) return BLOCK_GOLD_ORE;
        if (worldY < 40) return BLOCK_IRON_ORE;
        if (worldY < 60) return BLOCK_COAL_ORE;
        return BLOCK_AIR;
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _noiseGenerator?.Dispose();
            _disposed = true;
            _log.Debug("BasicWorldGenerator disposed");
        }
    }
}
