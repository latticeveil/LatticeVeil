using System;
using System.Runtime.InteropServices;
using LatticeVeilMonoGame.Core;

namespace LatticeVeilMonoGame.Core;

/// <summary>
/// High-performance noise generator using FastNoiseLite library
/// Provides optimized noise generation for terrain, caves, biomes, and structures
/// </summary>
public sealed class FastNoiseGenerator : IDisposable
{
    private readonly FastNoiseLite _terrainNoise;
    private readonly FastNoiseLite _caveNoise;
    private readonly FastNoiseLite _biomeNoise;
    private readonly FastNoiseLite _oreNoise;
    private readonly FastNoiseLite _structureNoise;
    private readonly Logger _log;
    private readonly int _seed;
    private bool _disposed;

    public FastNoiseGenerator(int seed, Logger log)
    {
        _seed = seed;
        _log = log;
        
        // Initialize different noise generators for different purposes
        _terrainNoise = new FastNoiseLite(seed);
        _terrainNoise.Noise = FastNoiseLite.NoiseType.Simplex;
        _terrainNoise.Frequency = 0.01f;
        
        _caveNoise = new FastNoiseLite(seed + 1);
        _caveNoise.Noise = FastNoiseLite.NoiseType.Simplex;
        _caveNoise.Frequency = 0.05f;
        
        _biomeNoise = new FastNoiseLite(seed + 2);
        _biomeNoise.Noise = FastNoiseLite.NoiseType.Simplex;
        _biomeNoise.Frequency = 0.001f; // Low frequency for large biome areas
        
        _oreNoise = new FastNoiseLite(seed + 3);
        _oreNoise.Noise = FastNoiseLite.NoiseType.Cellular;
        _oreNoise.Frequency = 0.1f;
        
        _structureNoise = new FastNoiseLite(seed + 4);
        _structureNoise.Noise = FastNoiseLite.NoiseType.Simplex;
        _structureNoise.Frequency = 0.02f;
        
        _log.Info($"FastNoiseGenerator initialized with seed: {seed}");
    }

    /// <summary>
    /// Generate terrain height for a given world position using fractal Brownian motion
    /// </summary>
    public float GetTerrainHeight(float worldX, float worldZ)
    {
        return _terrainNoise.GetFBM(worldX, worldZ);
    }

    /// <summary>
    /// Generate cave density at a given world position (3D)
    /// Returns value between -1 and 1, where positive values are solid rock
    /// </summary>
    public float GetCaveDensity(float worldX, float worldY, float worldZ)
    {
        // Base cave noise
        var caveValue = _caveNoise.GetNoise(worldX, worldY, worldZ);
        
        // Add Y-axis modulation for more interesting cave shapes
        var yModulation = (float)Math.Pow(worldY * 0.1f, 2) - 1;
        
        return caveValue + yModulation;
    }

    /// <summary>
    /// Get temperature value for biome generation (0-1 range)
    /// </summary>
    public float GetTemperature(float worldX, float worldZ)
    {
        var temp = _biomeNoise.GetNoise(worldX, worldZ);
        return (temp + 1.0f) * 0.5f; // Normalize to 0-1
    }

    /// <summary>
    /// Get humidity value for biome generation (0-1 range)
    /// </summary>
    public float GetHumidity(float worldX, float worldZ)
    {
        var humidity = _biomeNoise.GetNoise(worldX + 1000, worldZ + 1000);
        return (humidity + 1.0f) * 0.5f; // Normalize to 0-1
    }

    /// <summary>
    /// Get ore density at a given world position
    /// Returns value between 0 and 1, where higher values indicate more ore
    /// </summary>
    public float GetOreDensity(float worldX, float worldY, float worldZ)
    {
        var oreValue = _oreNoise.GetNoise(worldX, worldY, worldZ);
        return (oreValue + 1.0f) * 0.5f; // Normalize to 0-1
    }

    /// <summary>
    /// Get structure placement value for determining if structures should spawn
    /// Returns value between 0 and 1
    /// </summary>
    public float GetStructureValue(float worldX, float worldZ)
    {
        var structureValue = _structureNoise.GetNoise(worldX, worldZ);
        return (structureValue + 1.0f) * 0.5f; // Normalize to 0-1
    }

    /// <summary>
    /// Generate a 2D noise array for chunk generation
    /// </summary>
    public float[] GenerateNoise2D(int startX, int startZ, int sizeX, int sizeZ, float frequency)
    {
        var noise = new float[sizeX * sizeZ];
        var index = 0;
        
        for (int x = 0; x < sizeX; x++)
        {
            for (int z = 0; z < sizeZ; z++)
            {
                var worldX = startX + x;
                var worldZ = startZ + z;
                noise[index++] = _terrainNoise.GetNoise(worldX * frequency, worldZ * frequency);
            }
        }
        
        return noise;
    }

    /// <summary>
    /// Generate a 3D noise array for cave generation
    /// </summary>
    public float[] GenerateNoise3D(int startX, int startY, int startZ, int sizeX, int sizeY, int sizeZ, float frequency)
    {
        var noise = new float[sizeX * sizeY * sizeZ];
        var index = 0;
        
        for (int x = 0; x < sizeX; x++)
        {
            for (int y = 0; y < sizeY; y++)
            {
                for (int z = 0; z < sizeZ; z++)
                {
                    var worldX = startX + x;
                    var worldY = startY + y;
                    var worldZ = startZ + z;
                    noise[index++] = GetCaveDensity(worldX * frequency, worldY * frequency, worldZ * frequency);
                }
            }
        }
        
        return noise;
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            // FastNoiseLite doesn't implement IDisposable, so nothing to clean up
            _disposed = true;
            _log.Debug("FastNoiseGenerator disposed");
        }
    }
}
