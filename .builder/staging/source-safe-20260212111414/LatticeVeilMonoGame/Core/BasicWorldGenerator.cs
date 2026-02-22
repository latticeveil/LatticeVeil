using System;
using LatticeVeilMonoGame.Core;

namespace LatticeVeilMonoGame.Core;

/// <summary>
/// Basic world generator using FastNoiseGenerator
/// Provides terrain, caves, biomes, and structure generation
/// </summary>
public sealed class BasicWorldGenerator : IDisposable
{
    private readonly FastNoiseGenerator _noiseGenerator;
    private readonly Logger _log;
    private readonly int _seed;
    private readonly WorldSettings _settings;
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
    private const byte BLOCK_COAL = BlockIds.Coal;
    private const byte BLOCK_IRON = BlockIds.Iron;
    private const byte BLOCK_GOLD = BlockIds.Gold;
    private const byte BLOCK_DIAMOND = BlockIds.Diamond;
    private const byte BLOCK_NULLROCK = BlockIds.Nullrock; // Bedrock equivalent

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
    }

    public BasicWorldGenerator(int seed, WorldSettings settings, Logger log)
    {
        _seed = seed;
        _settings = settings;
        _log = log;
        _noiseGenerator = new FastNoiseGenerator(seed, log);
        
        _log.Info($"BasicWorldGenerator initialized with seed: {seed}");
        _log.Info($"Settings: ChunkSize={settings.ChunkSize}, WorldHeight={settings.WorldHeight}");
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

        // Place water
        PlaceWater(chunk, originX, originY, originZ, sizeX, sizeY, sizeZ);

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

                // Get terrain height using noise
                var terrainHeight = _noiseGenerator.GetTerrainHeight(worldX * _settings.TerrainFrequency, worldZ * _settings.TerrainFrequency);
                var normalizedHeight = (terrainHeight + 1.0f) * 0.5f; // Normalize to 0-1
                var blockHeight = (int)(normalizedHeight * _settings.WorldHeight);

                // Clamp height to world bounds
                blockHeight = Math.Max(0, Math.Min(_settings.WorldHeight - 1, blockHeight));

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
                            chunk.SetBlock(x, y, z, BLOCK_WATER);
                        }
                        else
                        {
                            chunk.SetBlock(x, y, z, BLOCK_AIR);
                        }
                    }
                    else if (worldY == blockHeight)
                    {
                        // Surface block
                        if (worldY <= _settings.SeaLevel)
                        {
                            chunk.SetBlock(x, y, z, BLOCK_SAND); // Sand under water
                        }
                        else
                        {
                            chunk.SetBlock(x, y, z, BLOCK_GRASS); // Grass on land
                        }
                    }
                    else if (worldY >= blockHeight - 3 && worldY > 0)
                    {
                        // Near surface - dirt
                        chunk.SetBlock(x, y, z, BLOCK_DIRT);
                    }
                    else if (worldY == 0)
                    {
                        // Bottom layer - Nullrock (bedrock equivalent)
                        chunk.SetBlock(x, y, z, BLOCK_NULLROCK);
                    }
                    else
                    {
                        // Underground - stone
                        chunk.SetBlock(x, y, z, BLOCK_STONE);
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

                    // Skip if already air, water, or Nullrock (bedrock)
                    var currentBlock = chunk.GetBlock(x, y, z);
                    if (currentBlock == BLOCK_AIR || currentBlock == BLOCK_WATER || currentBlock == BLOCK_NULLROCK)
                        continue;

                    // Get cave density
                    var caveDensity = _noiseGenerator.GetCaveDensity(
                        worldX * _settings.CaveFrequency,
                        worldY * _settings.CaveFrequency,
                        worldZ * _settings.CaveFrequency);

                    // Carve cave if density is below threshold
                    if (caveDensity < _settings.CaveThreshold)
                    {
                        chunk.SetBlock(x, y, z, BLOCK_AIR);
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
                    if (chunk.GetBlock(x, y, z) != BLOCK_STONE)
                        continue;

                    // Get ore density
                    var oreDensity = _noiseGenerator.GetOreDensity(worldX * 0.1f, worldY * 0.1f, worldZ * 0.1f);

                    // Place ore based on depth and density
                    var oreType = GetOreTypeForDepth(worldY);
                    if (oreType != BLOCK_AIR && oreDensity > 0.8f)
                    {
                        chunk.SetBlock(x, y, z, oreType);
                    }
                }
            }
        }
    }

    private void GenerateStructures(VoxelChunkData chunk, int originX, int originY, int originZ, int sizeX, int sizeY, int sizeZ)
    {
        // Simple tree generation
        for (int x = 0; x < sizeX; x++)
        {
            for (int z = 0; z < sizeZ; z++)
            {
                var worldX = originX + x;
                var worldZ = originZ + z;

                // Get structure value
                var structureValue = _noiseGenerator.GetStructureValue(worldX * 0.02f, worldZ * 0.02f);

                // Generate tree if conditions are met
                if (structureValue > 0.95f) // 5% chance
                {
                    // Find surface height
                    var terrainHeight = _noiseGenerator.GetTerrainHeight(worldX * _settings.TerrainFrequency, worldZ * _settings.TerrainFrequency);
                    var normalizedHeight = (terrainHeight + 1.0f) * 0.5f;
                    var surfaceY = (int)(normalizedHeight * _settings.WorldHeight);

                    // Check if surface is in this chunk and suitable for tree
                    var localY = surfaceY - originY;
                    if (localY >= 0 && localY < sizeY - 5 && surfaceY > _settings.SeaLevel)
                    {
                        // Check if surface block is grass
                        if (chunk.GetBlock(x, localY, z) == BLOCK_GRASS)
                        {
                            // Place simple tree (trunk + leaves)
                            PlaceSimpleTree(chunk, x, localY, z);
                        }
                    }
                }
            }
        }
    }

    private void PlaceSimpleTree(VoxelChunkData chunk, int x, int y, int z)
    {
        // Simple 3-block high tree with basic leaves
        if (y + 4 < VoxelChunkData.ChunkSizeY)
        {
            // Trunk
            chunk.SetBlock(x, y + 1, z, BLOCK_WOOD);
            chunk.SetBlock(x, y + 2, z, BLOCK_WOOD);
            chunk.SetBlock(x, y + 3, z, BLOCK_WOOD);

            // Leaves (simple cross pattern)
            if (x > 0 && x < VoxelChunkData.ChunkSizeX - 1 && z > 0 && z < VoxelChunkData.ChunkSizeZ - 1)
            {
                chunk.SetBlock(x - 1, y + 3, z, BLOCK_LEAVES);
                chunk.SetBlock(x + 1, y + 3, z, BLOCK_LEAVES);
                chunk.SetBlock(x, y + 3, z - 1, BLOCK_LEAVES);
                chunk.SetBlock(x, y + 3, z + 1, BLOCK_LEAVES);
                chunk.SetBlock(x, y + 4, z, BLOCK_LEAVES);
            }
        }
    }

    private void PlaceWater(VoxelChunkData chunk, int originX, int originY, int originZ, int sizeX, int sizeY, int sizeZ)
    {
        // Water is already placed in terrain generation
        // This method could be expanded for more complex water systems
    }

    private byte GetOreTypeForDepth(int worldY)
    {
        if (worldY < 5) return BLOCK_DIAMOND;
        if (worldY < 20) return BLOCK_GOLD;
        if (worldY < 40) return BLOCK_IRON;
        if (worldY < 60) return BLOCK_COAL;
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
