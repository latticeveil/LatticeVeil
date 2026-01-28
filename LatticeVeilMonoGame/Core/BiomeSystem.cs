using System;
using SharpNoise;
using SharpNoise.Modules;
using LatticeVeilMonoGame.Core;

namespace LatticeVeilMonoGame.Core;

/// <summary>
/// RANDOMIZED BIOME SYSTEM
/// Creates natural flat plains with varied terrain shapes through biome-based generation
/// </summary>
public static class BiomeSystem
{
    // Biome noise generators for different biome types
    private static readonly Perlin _biomeNoise;
    private static readonly Perlin _temperatureNoise;
    private static readonly Perlin _humidityNoise;
    private static readonly Perlin _biomeDetailNoise;
    
    // Biome type definitions
    public enum BiomeType
    {
        Plains,      // Flat grassy areas
        Forest,      // Dense forest with hills
        Desert,      // Sandy dunes and plateaus
        Sand,        // Natural sand biomes
        Mountains,   // Rugged terrain with peaks
        Hills,        // Rolling hills
        Wetlands,     // Swampy low areas
        Tundra,      // Cold flat areas
        Ocean        // Deep water areas
    }
    
    // Biome characteristics
    private static readonly BiomeCharacteristics[] _biomeCharacteristics;
    
    static BiomeSystem()
    {
        // ENHANCED CONSISTENCY - Improved biome noise generators for smoother transitions
        _biomeNoise = new Perlin
        {
            Seed = 54321,
            Frequency = 0.002, // Lower frequency for larger, more consistent biome areas
            Lacunarity = 2.0,
            Persistence = 0.5,
            OctaveCount = 3 // More octaves for smoother biome transitions
        };
        
        _temperatureNoise = new Perlin
        {
            Seed = 54322,
            Frequency = 0.004, // Lower frequency for smoother temperature gradients
            Lacunarity = 2.0,
            Persistence = 0.6,
            OctaveCount = 2
        };
        
        _humidityNoise = new Perlin
        {
            Seed = 54323,
            Frequency = 0.004, // Lower frequency for smoother humidity gradients
            Lacunarity = 2.0,
            Persistence = 0.5,
            OctaveCount = 2
        };
        
        _biomeDetailNoise = new Perlin
        {
            Seed = 54324,
            Frequency = 0.01, // Lower frequency for smoother biome boundaries
            Lacunarity = 2.0,
            Persistence = 0.3,
            OctaveCount = 1
        };
        
        // Initialize biome characteristics
        _biomeCharacteristics = new[]
        {
            new BiomeCharacteristics
            {
                Type = BiomeSystem.BiomeType.Plains,
                BaseHeight = 0.5, // Normalized height (0-1 range)
                HeightVariation = 0.1,
                Flatness = 0.95,
                TemperatureRange = 0.3,
                HumidityRange = 0.4,
                ColorMultiplier = 1.0,
                TreeDensity = 0.2,
                GrassColor = new double[] { 0.4, 0.8, 0.2 } // Green grass
            },
            new BiomeCharacteristics
            {
                Type = BiomeSystem.BiomeType.Forest,
                BaseHeight = 0.6,
                HeightVariation = 0.2,
                Flatness = 0.6,
                TemperatureRange = 0.2,
                HumidityRange = 0.6,
                ColorMultiplier = 0.8,
                TreeDensity = 0.8,
                GrassColor = new double[] { 0.2, 0.6, 0.1 } // Darker green
            },
            new BiomeCharacteristics
            {
                Type = BiomeSystem.BiomeType.Desert,
                BaseHeight = 0.4,
                HeightVariation = 0.05,
                Flatness = 0.95,
                TemperatureRange = 0.8,
                HumidityRange = 0.1,
                ColorMultiplier = 1.2,
                TreeDensity = 0.05,
                GrassColor = new double[] { 0.9, 0.8, 0.4 } // Sandy
            },
            new BiomeCharacteristics
            {
                Type = BiomeSystem.BiomeType.Sand,
                BaseHeight = 0.35,
                HeightVariation = 0.08,
                Flatness = 0.92,
                TemperatureRange = 0.6,
                HumidityRange = 0.2,
                ColorMultiplier = 1.1,
                TreeDensity = 0.02,
                GrassColor = new double[] { 0.95, 0.85, 0.6 } // Lighter sand
            },
            new BiomeCharacteristics
            {
                Type = BiomeSystem.BiomeType.Mountains,
                BaseHeight = 0.8,
                HeightVariation = 0.3,
                Flatness = 0.2,
                TemperatureRange = 0.4,
                HumidityRange = 0.3,
                ColorMultiplier = 0.7,
                TreeDensity = 0.4,
                GrassColor = new double[] { 0.5, 0.6, 0.3 } // Rocky
            },
            new BiomeCharacteristics
            {
                Type = BiomeSystem.BiomeType.Hills,
                BaseHeight = 0.6,
                HeightVariation = 0.15,
                Flatness = 0.4,
                TemperatureRange = 0.3,
                HumidityRange = 0.5,
                ColorMultiplier = 0.9,
                TreeDensity = 0.5,
                GrassColor = new double[] { 0.3, 0.7, 0.2 } // Rolling green
            },
            new BiomeCharacteristics
            {
                Type = BiomeSystem.BiomeType.Wetlands,
                BaseHeight = 0.3,
                HeightVariation = 0.02,
                Flatness = 0.98,
                TemperatureRange = 0.2,
                HumidityRange = 0.9,
                ColorMultiplier = 0.6,
                TreeDensity = 0.2,
                GrassColor = new double[] { 0.3, 0.5, 0.1 } // Muddy
            },
            new BiomeCharacteristics
            {
                Type = BiomeSystem.BiomeType.Tundra,
                BaseHeight = 0.35,
                HeightVariation = 0.05,
                Flatness = 0.85,
                TemperatureRange = 0.1,
                HumidityRange = 0.2,
                ColorMultiplier = 0.7,
                TreeDensity = 0.1,
                GrassColor = new double[] { 0.6, 0.7, 0.5 } // Cold
            },
            new BiomeCharacteristics
            {
                Type = BiomeSystem.BiomeType.Ocean,
                BaseHeight = 0.2,
                HeightVariation = 0.02,
                Flatness = 0.99,
                TemperatureRange = 0.3,
                HumidityRange = 1.0,
                ColorMultiplier = 0.8,
                TreeDensity = 0.0,
                GrassColor = new double[] { 0.1, 0.3, 0.8 } // Deep blue
            }
        };
    }
    
    /// <summary>
    /// Get biome type for a given world position
    /// </summary>
    public static BiomeType GetBiome(double worldX, double worldZ)
    {
        // ROBUST BIOME SELECTION - Based on research best practices to prevent artifacts
        double biomeValue = _biomeNoise.GetValue(worldX, 0, worldZ);
        
        // SAFEGUARD: Clamp biome value to prevent artifacts
        biomeValue = Math.Clamp(biomeValue, -1.0, 1.0);
        
        // ROBUST SELECTION - Clear boundaries to prevent biome artifacts
        if (biomeValue < -0.15)
        {
            return BiomeType.Ocean;
        }
        else if (biomeValue < 0.35)
        {
            return BiomeType.Plains;
        }
        else if (biomeValue < 0.65)
        {
            return BiomeType.Forest;
        }
        else
        {
            return BiomeType.Hills;
        }
    }
    
    /// <summary>
    /// Get biome characteristics for a given position
    /// </summary>
    public static BiomeCharacteristics GetBiomeCharacteristics(double worldX, double worldZ)
    {
        BiomeType biome = GetBiome(worldX, worldZ);
        return _biomeCharacteristics[(int)biome];
    }
    
    /// <summary>
    /// Apply biome-based terrain modification to terrain height
    /// </summary>
    public static double ApplyBiomeModification(double baseHeight, double worldX, double worldZ)
    {
        var biome = GetBiomeCharacteristics(worldX, worldZ);
        
        // Get biome detail noise for natural variation
        double detailNoise = _biomeDetailNoise.GetValue(worldX * 0.05, 0, worldZ * 0.05);
        
        // Apply biome-specific modifications - work WITH the base height, don't replace it
        double biomeHeight = baseHeight;
        
        switch (biome.Type)
        {
            case BiomeType.Plains:
                // Very flat plains with minimal rolling - grassy areas
                // Apply gentle flattening to the existing terrain
                biomeHeight = baseHeight * biome.Flatness + biome.BaseHeight * (1.0 - biome.Flatness) + 
                             detailNoise * biome.HeightVariation * 0.1;
                break;
                
            case BiomeType.Forest:
                // Forest with rolling hills
                biomeHeight = baseHeight * biome.Flatness + biome.BaseHeight * (1.0 - biome.Flatness) + 
                             detailNoise * biome.HeightVariation * 0.4;
                break;
                
            case BiomeType.Desert:
                // Very flat desert with occasional dunes
                biomeHeight = baseHeight * biome.Flatness + biome.BaseHeight * (1.0 - biome.Flatness) + 
                             detailNoise * biome.HeightVariation * 0.2;
                break;
                
            case BiomeType.Sand:
                // Natural sand biomes with gentle dunes
                biomeHeight = baseHeight * biome.Flatness + biome.BaseHeight * (1.0 - biome.Flatness) + 
                             detailNoise * biome.HeightVariation * 0.25;
                break;
                
            case BiomeType.Mountains:
                // Rugged mountains with dramatic variation
                biomeHeight = baseHeight * biome.Flatness + biome.BaseHeight * (1.0 - biome.Flatness) + 
                             detailNoise * biome.HeightVariation * 0.8;
                
                // Add ridged noise for mountain peaks
                double ridgedNoise = Math.Abs(detailNoise) * 2.0 - 1.0;
                if (detailNoise > 0.5)
                {
                    biomeHeight += ridgedNoise * biome.HeightVariation * 0.5;
                }
                break;
                
            case BiomeType.Hills:
                // Rolling hills with moderate variation
                biomeHeight = baseHeight * biome.Flatness + biome.BaseHeight * (1.0 - biome.Flatness) + 
                             detailNoise * biome.HeightVariation * 0.6;
                break;
                
            case BiomeType.Wetlands:
                // Very flat wetlands with minimal variation
                biomeHeight = baseHeight * biome.Flatness + biome.BaseHeight * (1.0 - biome.Flatness) + 
                             detailNoise * biome.HeightVariation * 0.1;
                break;
                
            case BiomeType.Tundra:
                // Cold flat areas with slight variation
                biomeHeight = baseHeight * biome.Flatness + biome.BaseHeight * (1.0 - biome.Flatness) + 
                             detailNoise * biome.HeightVariation * 0.2;
                break;
                
            case BiomeType.Ocean:
                // Very flat ocean floor
                biomeHeight = baseHeight * biome.Flatness + biome.BaseHeight * (1.0 - biome.Flatness) + 
                             detailNoise * biome.HeightVariation * 0.05;
                break;
        }
        
        return biomeHeight;
    }
    
    /// <summary>
    /// Get biome-appropriate grass color
    /// </summary>
    public static double[] GetGrassColor(double worldX, double worldZ)
    {
        var biome = GetBiomeCharacteristics(worldX, worldZ);
        return biome.GrassColor;
    }
    
    /// <summary>
    /// Check if position should have trees based on biome
    /// </summary>
    public static bool ShouldHaveTrees(double worldX, double worldZ)
    {
        var biome = GetBiomeCharacteristics(worldX, worldZ);
        
        // Random tree placement based on biome tree density
        double treeNoise = _biomeDetailNoise.GetValue(worldX * 0.1, 0, worldZ * 0.1);
        return treeNoise < biome.TreeDensity;
    }
    
    /// <summary>
    /// Get biome temperature for world position
    /// </summary>
    public static double GetTemperature(double worldX, double worldZ)
    {
        double temp = _temperatureNoise.GetValue(worldX, 0, worldZ);
        return (temp + 1.0) * 0.5; // Normalize to 0-1 range
    }
    
    /// <summary>
    /// Get biome humidity for world position
    /// </summary>
    public static double GetHumidity(double worldX, double worldZ)
    {
        double humidity = _humidityNoise.GetValue(worldX, 0, worldZ);
        return (humidity + 1.0) * 0.5; // Normalize to 0-1 range
    }
}

/// <summary>
/// Characteristics for each biome type
/// </summary>
public class BiomeCharacteristics
{
    public BiomeSystem.BiomeType Type { get; set; }
    public double BaseHeight { get; set; }
    public double HeightVariation { get; set; }
    public double Flatness { get; set; }
    public double TemperatureRange { get; set; }
    public double HumidityRange { get; set; }
    public double ColorMultiplier { get; set; }
    public double TreeDensity { get; set; }
    public double[] GrassColor { get; set; }
}
