using System;
using System.Threading.Tasks;
using SharpNoise;
using SharpNoise.Modules;
using LatticeVeilMonoGame.Core;

namespace LatticeVeilMonoGame.Core;

/// <summary>
/// HYPER-OPTIMIZED WORLD GENERATOR
/// Eliminates lagging with cached noise and efficient calculations
/// </summary>
public static class OptimizedWorldGenerator
{
    // Cached noise generators for different scales
    private static readonly Perlin _continentalNoise;
    private static readonly Perlin _regionalNoise;
    private static readonly Perlin _localNoise;
    private static readonly Perlin _detailNoise;
    private static readonly Perlin _caveNoise1;
    private static readonly Perlin _caveNoise2;
    
    // Deterministic noise generators - no shared caches to prevent synchronization issues
    private static readonly int _seed = 12345;

    static OptimizedWorldGenerator()
    {
        // Minecraft-style fractal noise generators for proper terrain
        // Base terrain noise (low frequency)
        _continentalNoise = new Perlin
        {
            Seed = _seed,
            Frequency = 0.005, // Lower frequency for large-scale features
            Lacunarity = 2.0,
            Persistence = 0.5,
            OctaveCount = 4 // More octaves for fractal detail
        };
        
        // Detail noise (medium frequency)
        _regionalNoise = new Perlin
        {
            Seed = _seed + 1,
            Frequency = 0.01, // Medium frequency for regional features
            Lacunarity = 2.0,
            Persistence = 0.5,
            OctaveCount = 3
        };
        
        // Fine detail noise (high frequency)
        _localNoise = new Perlin
        {
            Seed = _seed + 2,
            Frequency = 0.02, // Higher frequency for local details
            Lacunarity = 2.0,
            Persistence = 0.5,
            OctaveCount = 2
        };
        
        // Surface detail noise (very high frequency)
        _detailNoise = new Perlin
        {
            Seed = _seed + 3,
            Frequency = 0.04, // Very high frequency for fine details
            Lacunarity = 2.0,
            Persistence = 0.3,
            OctaveCount = 1
        };

        // Cave generation noise
        _caveNoise1 = new Perlin
        {
            Seed = _seed + 10,
            Frequency = 0.06,
            Lacunarity = 2.0,
            Persistence = 0.4,
            OctaveCount = 2
        };

        _caveNoise2 = new Perlin
        {
            Seed = _seed + 11,
            Frequency = 0.09,
            Lacunarity = 2.0,
            Persistence = 0.4,
            OctaveCount = 2
        };
    }

    /// <summary>
    /// Generate chunk with deterministic, self-contained generation to prevent random blocks
    /// </summary>
    public static void GenerateChunk(WorldMeta meta, ChunkCoord coord, VoxelChunkData chunk)
    {
        // DETERMINISTIC GENERATION - Each chunk generates independently without shared state
        // This prevents synchronization issues that cause random blocks
        
        for (int x = 0; x < VoxelChunkData.ChunkSizeX; x++)
        {
            for (int z = 0; z < VoxelChunkData.ChunkSizeZ; z++)
            {
                double worldX = coord.X * 16 + x;
                double worldZ = coord.Z * 16 + z;

                // DETERMINISTIC HEIGHT CALCULATION - No shared caches
                // Generate consistent height values with proper noise layering
                double continental = _continentalNoise.GetValue(worldX, 0, worldZ);
                double regional = _regionalNoise.GetValue(worldX, 0, worldZ);
                double local = _localNoise.GetValue(worldX, 0, worldZ);
                double detail = _detailNoise.GetValue(worldX, 0, worldZ);

                // ENHANCED CONSISTENCY - Improved noise combination for smoother terrain
                double terrainHeight = 
                    continental * 0.6 +    // Main terrain shape (increased for consistency)
                    regional * 0.25 +       // Regional features (reduced for smoother transitions)
                    local * 0.1 +           // Local details (reduced for consistency)
                    detail * 0.05;          // Fine details (kept minimal)

                // ENHANCED SMOOTHING - Better continuity between adjacent positions
                terrainHeight = ApplyEnhancedSmoothing(terrainHeight, continental, regional, worldX, worldZ);

                // Normalize to reasonable height range (20-60) with clamping
                int surfaceLevel = (int)(terrainHeight * 20 + 40);
                
                // ROBUST CLAMPING - Ensure consistent height range without artifacts
                surfaceLevel = Math.Max(20, Math.Min(60, surfaceLevel));

                // Apply biome-based modifications for natural variation
                double biomeModifier = BiomeSystem.ApplyBiomeModification(surfaceLevel / 60.0, worldX, worldZ);
                surfaceLevel = (int)(biomeModifier * 60);
                
                // Ensure consistent height range
                surfaceLevel = Math.Max(20, Math.Min(60, surfaceLevel));

                // DETERMINISTIC COLUMN GENERATION - Calculate caves on the fly
                GenerateColumnDeterministic(coord, chunk, x, z, surfaceLevel, worldX, worldZ);
            }
        }
    }

    /// <summary>
    /// Generate column deterministically without shared caches to prevent random blocks
    /// </summary>
    private static void GenerateColumnDeterministic(ChunkCoord coord, VoxelChunkData chunk, int x, int z, int surfaceLevel, double worldX, double worldZ)
    {
        // DETERMINISTIC CAVE CALCULATION - Calculate caves on the fly for each position
        double cave1 = _caveNoise1.GetValue(worldX * 0.03, 0, worldZ * 0.03);
        double cave2 = _caveNoise2.GetValue(worldX * 0.05, 0, worldZ * 0.05);
        double caveIntensity = (cave1 + cave2) * 0.5;
        
        var biome = BiomeSystem.GetBiomeCharacteristics(worldX, worldZ);
        
        for (int y = 0; y < VoxelChunkData.ChunkSizeY; y++)
        {
            int worldY = coord.Y * 16 + y;
            byte block = BlockIds.Air; // Default to air

            // SAFEGUARD: Bedrock at bottom
            if (worldY == 0)
            {
                block = BlockIds.Nullblock; // Bedrock at bottom
            }
            else if (worldY <= surfaceLevel)
            {
                if (worldY == surfaceLevel)
                {
                    // Biome-specific surface blocks
                    if (biome.Type == BiomeSystem.BiomeType.Sand)
                    {
                        block = BlockIds.Sand; // Sand surface for sand biomes
                    }
                    else
                    {
                        block = BlockIds.Grass; // Grass for other biomes
                    }
                }
                else if (worldY >= surfaceLevel - 3)
                {
                    // Biome-specific sub-surface blocks
                    if (biome.Type == BiomeSystem.BiomeType.Sand)
                    {
                        block = BlockIds.Sand; // Sand layers for sand biomes
                    }
                    else
                    {
                        block = BlockIds.Dirt; // Dirt for other biomes
                    }
                }
                else
                {
                    // SAFEGUARD: Only generate stone below a certain depth
                    // This prevents massive stone blocks near the surface
                    if (worldY > surfaceLevel - 8)
                    {
                        // Near surface - use dirt instead of stone to prevent massive stone blocks
                        block = BlockIds.Dirt;
                    }
                    else
                    {
                        block = BlockIds.Stone; // Stone only deep underground
                        
                        // SIMPLE SAFEGUARD: Prevent massive stone blocks
                        // Add some variation to break up large stone areas
                        if (worldY < surfaceLevel - 3)
                        {
                            double stoneVariation = _localNoise.GetValue(worldX * 0.1, worldY * 0.1, worldZ * 0.1);
                            
                            // Add some dirt pockets and variation in stone areas
                            if (stoneVariation > 0.3 && worldY < surfaceLevel - 5)
                            {
                                block = BlockIds.Dirt; // Dirt pockets to break up stone
                            }
                            else if (stoneVariation < -0.3 && worldY < surfaceLevel - 8)
                            {
                                block = BlockIds.Gravel; // Gravel pockets for more variation
                            }
                        }
                    }

                    // DETERMINISTIC CAVE GENERATION - Calculate caves on the fly
                    if (worldY < surfaceLevel - 8) // Only caves deep underground
                    {
                        // Use calculated cave intensity with Y-based variation
                        double caveY = _caveNoise1.GetValue(worldX * 0.03, worldY * 0.04, worldZ * 0.03);
                        double combinedCave = (caveIntensity + caveY) * 0.5;
                        
                        // Biome-specific cave generation
                        if (biome.Type == BiomeSystem.BiomeType.Wetlands)
                        {
                            // More caves in wetlands
                            if (combinedCave > 0.1)
                            {
                                if (worldY < surfaceLevel - 10) // Extra safety margin
                                {
                                    block = BlockIds.Air;
                                }
                            }
                        }
                        else
                        {
                            // Standard cave generation for other biomes
                            if (combinedCave > 0.2)
                            {
                                block = BlockIds.Air;
                            }
                        }
                    }
                }
            }

            chunk.SetLocal(x, y, z, block);
        }
    }

    /// <summary>
    /// Apply enhanced smoothing for better terrain consistency and continuity
    /// </summary>
    private static double ApplyEnhancedSmoothing(double terrainHeight, double continental, double regional, double worldX, double worldZ)
    {
        // SAFEGUARD: Prevent extreme values that cause artifacts
        terrainHeight = Math.Clamp(terrainHeight, -1.0, 1.0);
        
        // ENHANCED SMOOTHING - Better continuity between adjacent positions
        // Sample neighboring positions for smoother transitions
        double neighborSampleX = _continentalNoise.GetValue(worldX + 1, 0, worldZ);
        double neighborSampleZ = _continentalNoise.GetValue(worldX, 0, worldZ + 1);
        double neighborDiagonal = _continentalNoise.GetValue(worldX + 1, 0, worldZ + 1);
        
        // Calculate weighted average with neighbors for better continuity
        double neighborAverage = (neighborSampleX + neighborSampleZ + neighborDiagonal) / 3.0;
        
        // Enhanced smoothing factor - more smoothing for better consistency
        double smoothingFactor = 0.15; // Increased from 0.1 for better continuity
        double smoothedHeight = terrainHeight * (1.0 - smoothingFactor) + 
                               neighborAverage * smoothingFactor;
        
        // Additional smoothing based on continental and regional noise
        double baseSmoothing = (continental + regional) * 0.5;
        smoothedHeight = smoothedHeight * 0.9 + baseSmoothing * 0.1;
        
        // Ensure smoothing doesn't create new artifacts
        smoothedHeight = Math.Clamp(smoothedHeight, -1.0, 1.0);
        
        return smoothedHeight;
    }

    /// <summary>
    /// Apply height smoothing to prevent artifacts and ensure robust terrain generation
    /// Based on research best practices for noise combination
    /// </summary>
    private static double ApplyHeightSmoothing(double terrainHeight, double continental, double regional)
    {
        // SAFEGUARD: Prevent extreme values that cause artifacts
        terrainHeight = Math.Clamp(terrainHeight, -1.0, 1.0);
        
        // Apply gentle smoothing based on continental and regional noise
        // This helps prevent sharp transitions and artifacts
        double smoothingFactor = 0.1; // Gentle smoothing to preserve detail
        double smoothedHeight = terrainHeight * (1.0 - smoothingFactor) + 
                               (continental + regional) * 0.5 * smoothingFactor;
        
        // Ensure smoothing doesn't create new artifacts
        smoothedHeight = Math.Clamp(smoothedHeight, -1.0, 1.0);
        
        return smoothedHeight;
    }

    /// <summary>
    /// Generate multiple chunks in parallel for maximum performance
    /// </summary>
    public static void GenerateChunksParallel(WorldMeta meta, ChunkCoord[] coords, VoxelChunkData[] chunks)
    {
        Parallel.For(0, coords.Length, i =>
        {
            GenerateChunk(meta, coords[i], chunks[i]);
        });
    }

    /// <summary>
    /// Clear caches to free memory (no-op for deterministic generation)
    /// </summary>
    public static void ClearCaches()
    {
        // No-op for deterministic generation - no shared caches to clear
    }
}
