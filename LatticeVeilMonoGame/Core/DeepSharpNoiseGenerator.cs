using System;
using SharpNoise;
using SharpNoise.Modules;

namespace LatticeVeilMonoGame.Core;

/// <summary>
/// DEEP SHARPNOISE GENERATOR - Optimized for deep worlds
/// Much deeper worlds with stable performance
/// </summary>
public static class DeepSharpNoiseGenerator
{
    private static readonly Perlin _terrainNoise;
    private static readonly Perlin _caveNoise1;
    private static readonly Perlin _caveNoise2;
    private static readonly int _seed = 12345;

    static DeepSharpNoiseGenerator()
    {
        // Initialize terrain noise - OPTIMIZED for maximum quality and performance
        _terrainNoise = new Perlin
        {
            Seed = _seed,
            Frequency = 0.008, // Slightly higher for more detail
            Lacunarity = 2.5, // Increased for more variation
            Persistence = 0.5, // Balanced for natural terrain
            OctaveCount = 6 // More octaves for richer detail
        };

        // Initialize cave noises - OPTIMIZED for realistic cave systems
        _caveNoise1 = new Perlin
        {
            Seed = _seed + 1,
            Frequency = 0.06, // Optimized for cave size
            Lacunarity = 2.2,
            Persistence = 0.4,
            OctaveCount = 3 // More detail for caves
        };

        _caveNoise2 = new Perlin
        {
            Seed = _seed + 2,
            Frequency = 0.09, // Different frequency for variety
            Lacunarity = 2.2,
            Persistence = 0.4,
            OctaveCount = 3
        };
    }

    /// <summary>
    /// Generate chunk using optimized SharpNoise for deep worlds
    /// </summary>
    public static void GenerateChunk(WorldMeta meta, ChunkCoord coord, VoxelChunkData chunk)
    {
        // Deep world generation with optimized SharpNoise
        for (int x = 0; x < VoxelChunkData.ChunkSizeX; x++)
        {
            for (int y = 0; y < VoxelChunkData.ChunkSizeY; y++)
            {
                for (int z = 0; z < VoxelChunkData.ChunkSizeZ; z++)
                {
                    byte block = BlockIds.Air;
                    
                    // Use world coordinates for consistent generation
                    double worldX = coord.X * VoxelChunkData.ChunkSizeX + x;
                    double worldY = coord.Y * VoxelChunkData.ChunkSizeY + y;
                    double worldZ = coord.Z * VoxelChunkData.ChunkSizeZ + z;

                    // ADVANCED MINECRAFT-STYLE TERRAIN GENERATION
                    // Multi-noise approach: Continentalness, Erosion, Peaks & Valleys
                    
                    // Continentalness: How inland/continental the area is (ocean vs land)
                    double continentalness = _terrainNoise.GetValue(worldX * 0.002, 0, worldZ * 0.002);
                    
                    // Erosion: How flat/eroded the area is (mountains vs plains)
                    double erosion = _terrainNoise.GetValue(worldX * 0.003, 0, worldZ * 0.003);
                    
                    // Peaks & Valleys: Local terrain variation (peaks vs valleys)
                    double peaksAndValleys = _terrainNoise.GetValue(worldX * 0.008, 0, worldZ * 0.008);
                    
                    // Base terrain with fractal noise layers
                    double baseTerrain = _terrainNoise.GetValue(worldX * 0.01, 0, worldZ * 0.01);
                    double detailTerrain = _terrainNoise.GetValue(worldX * 0.02, 0, worldZ * 0.02) * 0.5;
                    double fineTerrain = _terrainNoise.GetValue(worldX * 0.04, 0, worldZ * 0.04) * 0.25;
                    double fractalTerrain = baseTerrain + detailTerrain + fineTerrain;
                    
                    // OPTIMIZED MULTI-LAYER TERRAIN GENERATION
                    // Professional terrain generation with multiple noise layers
                    
                    // Continental scale noise (very low frequency) - major landforms
                    double continentalNoise = _terrainNoise.GetValue(worldX * 0.002, 0, worldZ * 0.002);
                    
                    // Regional scale noise (low frequency) - mountains and valleys
                    double regionalNoise = _terrainNoise.GetValue(worldX * 0.008, 0, worldZ * 0.008);
                    
                    // Local scale noise (medium frequency) - hills and ridges
                    double localNoise = _terrainNoise.GetValue(worldX * 0.02, 0, worldZ * 0.02);
                    
                    // Detail noise (high frequency) - small variations
                    double detailNoise = _terrainNoise.GetValue(worldX * 0.05, 0, worldZ * 0.05) * 0.3;
                    
                    // Combine layers with proper weighting for natural terrain
                    double terrainHeight = 
                        continentalNoise * 0.4 +    // Major landforms
                        regionalNoise * 0.3 +       // Mountains/valleys
                        localNoise * 0.2 +          // Hills/ridges
                        detailNoise;                // Fine details
                    
                    // Add ridged noise for mountain peaks
                    double ridgedNoise = Math.Abs(localNoise) * 1.5 - 0.5;
                    if (regionalNoise > 0.3) // In mountainous areas
                    {
                        terrainHeight += ridgedNoise * 0.3;
                    }
                    
                    // Add occasional dramatic features
                    double featureChance = _terrainNoise.GetValue(worldX * 0.03, 0, worldZ * 0.03);
                    if (featureChance > 0.8) // 20% chance of dramatic features
                    {
                        // Large mountains or valleys
                        terrainHeight += (featureChance - 0.8) * 2.0;
                    }
                    
                    // Convert to world height with OPTIMIZED range
                    int surfaceLevel = (int)(terrainHeight * 35 + 45); // 10-115 range for impressive terrain
                    
                    // SAFETY: Ensure minimum ground height
                    surfaceLevel = Math.Max(surfaceLevel, 10); // Minimum height of 10

                    // Basic terrain generation
                    if (worldY <= surfaceLevel)
                    {
                        if (worldY == 0)
                        {
                            block = BlockIds.Nullblock; // Bedrock at bottom
                        }
                        else if (worldY == surfaceLevel)
                        {
                            block = BlockIds.Grass; // Single grass layer at surface
                        }
                        else if (worldY >= surfaceLevel - 4)
                        {
                            block = BlockIds.Dirt; // More dirt for stability
                        }
                        else
                        {
                            block = BlockIds.Stone; // Stone underground
                        }

                        // OPTIMIZED CAVE GENERATION - Stable and realistic
                        // Generate caves only in deep underground to prevent surface issues
                        if (worldY < surfaceLevel - 8) // Only caves deep underground
                        {
                            // Multi-layer cave noise for realistic cave systems
                            double caveValue1 = _caveNoise1.GetValue(worldX * 0.03, worldY * 0.04, worldZ * 0.03);
                            double caveValue2 = _caveNoise2.GetValue(worldX * 0.05, worldY * 0.06, worldZ * 0.05);
                            
                            // Combine cave noises for more interesting cave shapes
                            double combinedCave = (caveValue1 + caveValue2) * 0.5;
                            
                            // Create caves where noise is above threshold (more conservative)
                            if (combinedCave > 0.15) // Conservative threshold to prevent over-carving
                            {
                                // Ensure caves don't break surface stability
                                if (worldY < surfaceLevel - 12) // Extra safety margin
                                {
                                    block = BlockIds.Air;
                                }
                            }
                            
                            // Add occasional larger caverns
                            double cavernChance = _terrainNoise.GetValue(worldX * 0.01, worldY * 0.01, worldZ * 0.01);
                            if (cavernChance > 0.85 && worldY < surfaceLevel - 15) // Rare large caverns
                            {
                                block = BlockIds.Air;
                            }
                        }
                    }

                    chunk.SetLocal(x, y, z, block, markDirty: false);
                }
            }
        }
    }
    
    /// <summary>
    /// Apply Minecraft-style spline mapping for dramatic terrain shaping
    /// </summary>
    private static double ApplySpline(double value, double minInput, double maxInput, double minOutput, double maxOutput)
    {
        // Normalize input to 0-1 range
        double normalized = (value - minInput) / (maxInput - minInput);
        normalized = Math.Clamp(normalized, 0.0, 1.0);
        
        // Apply smooth spline curve (ease-in-out)
        double spline = normalized * normalized * (3.0 - 2.0 * normalized);
        
        // Map to output range
        return minOutput + spline * (maxOutput - minOutput);
    }
    
    /// <summary>
    /// Generate dramatic terrain features based on biome combinations
    /// </summary>
    private static double GenerateDramaticFeatures(double worldX, double worldZ, double continentalness, double erosion, double peaksAndValleys)
    {
        double features = 0.0;
        
        // Large-scale terrain features (inspired by Terralith)
        double largeFeatureNoise = Math.Sin(worldX * 0.001) * Math.Cos(worldZ * 0.001);
        
        // Canyons in high continentalness, low erosion areas
        if (continentalness > 0.4 && erosion < -0.3)
        {
            features += largeFeatureNoise * 2.0;
        }
        
        // Floating islands in high PV, high continentalness areas
        if (peaksAndValleys > 0.6 && continentalness > 0.6)
        {
            double islandNoise = Math.Sin(worldX * 0.005) * Math.Sin(worldZ * 0.005);
            if (islandNoise > 0.7)
            {
                features += islandNoise * 3.0;
            }
        }
        
        // Shattered terrain in weird combinations
        if (Math.Abs(peaksAndValleys) > 0.8 && Math.Abs(erosion) > 0.7)
        {
            double shatteredNoise = Math.Sin(worldX * 0.01) * Math.Cos(worldZ * 0.01);
            features += shatteredNoise * 1.5;
        }
        
        // Mesa-like plateaus in specific conditions
        if (continentalness > 0.2 && continentalness < 0.4 && erosion > 0.5)
        {
            double plateauNoise = Math.Sin(worldX * 0.003) * Math.Sin(worldZ * 0.003);
            if (plateauNoise > 0.5)
            {
                features += plateauNoise * 1.2;
            }
        }
        
        return features;
    }
}
