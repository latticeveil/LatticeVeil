using System;
using LatticeVeilMonoGame.Core;

namespace LatticeVeilMonoGame.Core;

/// <summary>
/// Advanced 3D landscape generator (v2)
/// Features dramatic terrain, overhangs, and biomes.
/// </summary>
public sealed class LandscapeGeneratorV2 : IChunkGenerator
{
    private readonly FastNoiseGenerator _noiseGenerator;
    private readonly Logger _log;
    private readonly int _seed;
    private BasicWorldGenerator.WorldSettings _settings;
    private bool _disposed;

    private const byte BLOCK_AIR = BlockIds.Air;
    private const byte BLOCK_STONE = BlockIds.Stone;
    private const byte BLOCK_DIRT = BlockIds.Dirt;
    private const byte BLOCK_GRASS = BlockIds.Grass;
    private const byte BLOCK_SAND = BlockIds.Sand;
    private const byte BLOCK_WATER = BlockIds.Water;
    private const byte BLOCK_WOOD = BlockIds.Wood;
    private const byte BLOCK_LEAVES = BlockIds.Leaves;
    private const byte BLOCK_NULLROCK = BlockIds.Nullrock;

    public LandscapeGeneratorV2(int seed, BasicWorldGenerator.WorldSettings settings, Logger log)
    {
        _seed = seed;
        _settings = settings;
        _log = log;
        _noiseGenerator = new FastNoiseGenerator(seed, log);
        _log.Info($"LandscapeGeneratorV2 initialized with seed: {seed}");
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

        // Pass 1: Base Terrain shaping with 3D Noise
        GenerateTerrain(chunk, originX, originY, originZ, sizeX, sizeY, sizeZ);

        // Pass 2: Caves
        if (_settings.GenerateCaves)
            GenerateCaves(chunk, originX, originY, originZ, sizeX, sizeY, sizeZ);

        // Pass 3: Ores
        if (_settings.GenerateOres)
            GenerateOres(chunk, originX, originY, originZ, sizeX, sizeY, sizeZ);

        // Pass 4: Trees
        if (_settings.GenerateTrees)
            GenerateTrees(chunk, originX, originY, originZ, sizeX, sizeY, sizeZ);

        // Surface decoration and Biome ID tracking
        FinalizeChunk(chunk, originX, originY, originZ, sizeX, sizeY, sizeZ);

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
                
                // Sample 2D height for baseline
                var biome = BiomeService.GetSampleAt(_seed, worldX, worldZ);
                var heightLimit = ComputeBaselineHeight(worldX, worldZ, biome);

                for (int y = 0; y < sizeY; y++)
                {
                    var worldY = originY + y;
                    if (worldY <= 0)
                    {
                        chunk.SetBlockRaw(x, y, z, BLOCK_NULLROCK);
                        continue;
                    }

                    // 3D density sampling for dramatic terrain / overhangs
                    var density = SampleDensity(worldX, worldY, worldZ, heightLimit, biome);
                    
                    if (density > 0)
                    {
                        chunk.SetBlockRaw(x, y, z, BLOCK_STONE);
                    }
                    else if (worldY <= _settings.SeaLevel)
                    {
                        chunk.SetBlockRaw(x, y, z, BLOCK_WATER);
                    }
                    else
                    {
                        chunk.SetBlockRaw(x, y, z, BLOCK_AIR);
                    }
                }
            }
        }
    }

    private float SampleDensity(int wx, int wy, int wz, int baseline, BiomeSample biome)
    {
        // Gradient: 1 at bottom, 0 at baseline
        float heightGradient = 1.0f - (float)(wy - 0) / (baseline + 1);
        
        // 3D Noise for detail and overhangs
        var noise3d = _noiseGenerator.GetFractalNoise3D(wx * 0.02f, wy * 0.04f, wz * 0.02f);
        
        // Combine gradient and noise. 
        // Bias towards solid at lower heights, bias towards air at higher.
        var density = heightGradient + noise3d * 0.5f;
        
        // Strong suppression above baseline
        if (wy > baseline + 10) density -= (wy - baseline) * 0.1f;

        return density;
    }

    private int ComputeBaselineHeight(int wx, int wz, BiomeSample biome)
    {
        var macro = _noiseGenerator.GetTerrainHeight(wx, wz);
        var baseHeight = _settings.SeaLevel + (int)(macro * 30f);
        
        // Biome specific modifiers
        if (biome.BiomeId == BiomeId.Hills) baseHeight += 40;
        if (biome.BiomeId == BiomeId.Ocean) baseHeight -= 20;

        return Math.Clamp(baseHeight, 10, _settings.WorldHeight - 20);
    }

    private void FinalizeChunk(VoxelChunkData chunk, int originX, int originY, int originZ, int sizeX, int sizeY, int sizeZ)
    {
        for (int x = 0; x < sizeX; x++)
        {
            for (int z = 0; z < sizeZ; z++)
            {
                var worldX = originX + x;
                var worldZ = originZ + z;
                var biome = BiomeService.GetSampleAt(_seed, worldX, worldZ);
                chunk.SetBiomeLocal(x, z, biome.BiomeId);

                // Surface layer (Dirt/Grass/Sand)
                bool foundSurface = false;
                for (int y = sizeY - 1; y >= 0; y--)
                {
                    var block = chunk.GetBlockRaw(x, y, z);
                    if (block == BLOCK_STONE)
                    {
                        if (!foundSurface)
                        {
                            var worldY = originY + y;
                            if (worldY < _settings.SeaLevel - 1)
                                chunk.SetBlockRaw(x, y, z, BLOCK_SAND);
                            else if (biome.BiomeId == BiomeId.Desert)
                                chunk.SetBlockRaw(x, y, z, BLOCK_SAND);
                            else
                                chunk.SetBlockRaw(x, y, z, BLOCK_GRASS);
                            
                            foundSurface = true;
                        }
                        else
                        {
                            // Secondary layers
                            chunk.SetBlockRaw(x, y, z, biome.BiomeId == BiomeId.Desert ? BLOCK_SAND : BLOCK_DIRT);
                            // Stop after 3 layers of dirt/sand
                            if (y < sizeY - 4 && chunk.GetBlockRaw(x, y + 1, z) != BLOCK_STONE)
                                break;
                        }
                    }
                    else if (block != BLOCK_AIR && block != BLOCK_WATER)
                    {
                        // Already handled or non-stone (e.g. ores)
                        foundSurface = true; 
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
                    var worldY = originY + y;
                    if (worldY <= 5) continue; 

                    var block = chunk.GetBlockRaw(x, y, z);
                    if (block != BLOCK_STONE && block != BLOCK_DIRT) continue;

                    if (_noiseGenerator.GetCaveDensity(originX + x, worldY, originZ + z) < _settings.CaveThreshold)
                    {
                        chunk.SetBlockRaw(x, y, z, BLOCK_AIR);
                    }
                }
            }
        }
    }

    private void GenerateOres(VoxelChunkData chunk, int originX, int originY, int originZ, int sizeX, int sizeY, int sizeZ)
    {
        // Same logic as BasicWorldGenerator for now to ensure consistency
        for (int x = 0; x < sizeX; x++)
        {
            for (int y = 0; y < sizeY; y++)
            {
                for (int z = 0; z < sizeZ; z++)
                {
                    if (chunk.GetBlockRaw(x, y, z) != BLOCK_STONE) continue;

                    var worldY = originY + y;
                    var oreDensity = _noiseGenerator.GetOreDensity(originX + x, worldY, originZ + z);
                    
                    byte oreType = BLOCK_AIR;
                    if (worldY < 10) oreType = BlockIds.Diamond;
                    else if (worldY < 30) oreType = BlockIds.GoldOre;
                    else if (worldY < 50) oreType = BlockIds.IronOre;
                    else if (worldY < 80) oreType = BlockIds.CoalOre;

                    if (oreType != BLOCK_AIR && oreDensity > 0.85f)
                        chunk.SetBlockRaw(x, y, z, oreType);
                }
            }
        }
    }

    private void GenerateTrees(VoxelChunkData chunk, int originX, int originY, int originZ, int sizeX, int sizeY, int sizeZ)
    {
        // Tree placement logic would be similar to BasicWorldGenerator but adapted for 3D surfaces
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _noiseGenerator?.Dispose();
            _disposed = true;
        }
    }
}
