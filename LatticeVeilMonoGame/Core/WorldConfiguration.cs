using System;

namespace LatticeVeilMonoGame.Core;

/// <summary>
/// Configuration for hybrid world generation approach
/// Combines pregenerated core world with procedural expansion
/// </summary>
public class WorldConfiguration
{
    /// <summary>
    /// World generation modes
    /// </summary>
    public enum WorldMode
    {
        /// <summary>
        /// Fully procedural - infinite world generation
        /// </summary>
        Procedural,
        
        /// <summary>
        /// Hybrid - pregenerated core with procedural expansion
        /// </summary>
        Hybrid,
        
        /// <summary>
        /// Fully pregenerated - fixed size world only
        /// </summary>
        Pregenerated
    }
    
    /// <summary>
    /// Predefined world sizes
    /// </summary>
    public enum WorldSize
    {
        /// <summary>
        /// Small: 2,000 x 2,000 blocks (~4M blocks²)
        /// </summary>
        Small = 2000,
        
        /// <summary>
        /// Medium: 10,000 x 10,000 blocks (~100M blocks²)
        /// </summary>
        Medium = 10000,
        
        /// <summary>
        /// Large: 50,000 x 50,000 blocks (~2.5B blocks²)
        /// </summary>
        Large = 50000,
        
        /// <summary>
        /// Custom size defined by CoreWorldRadius
        /// </summary>
        Custom = 0
    }
    
    /// <summary>
    /// Performance optimization levels
    /// </summary>
    public enum PerformanceMode
    {
        /// <summary>
        /// Maximum quality, lower performance
        /// </summary>
        Quality,
        
        /// <summary>
        /// Balanced approach
        /// </summary>
        Balanced,
        
        /// <summary>
        /// Maximum performance, lower quality
        /// </summary>
        Performance
    }
    
    // Core world configuration
    public WorldMode Mode { get; set; } = WorldMode.Hybrid;
    public WorldSize Size { get; set; } = WorldSize.Medium;
    public int CoreWorldRadius { get; set; } = 10000; // In blocks
    public PerformanceMode Performance { get; set; } = PerformanceMode.Balanced;
    
    // Pregeneration settings
    public bool EnablePregeneration { get; set; } = true;
    public int PregenerationRadius { get; set; } = 10000; // In blocks
    public int MaxConcurrentPregenTasks { get; set; } = 8;
    public bool BackgroundPregeneration { get; set; } = true;
    
    // Procedural expansion settings
    public bool EnableProceduralExpansion { get; set; } = true;
    public int ExpansionChunkCacheSize { get; set; } = 1000;
    public bool AutoConvertExploredAreas { get; set; } = true;
    public int ConversionThreshold { get; set; } = 5; // Visits before conversion
    
    // Performance settings
    public int MaxLoadedChunks { get; set; } = 512;
    public int ChunkUnloadDistance { get; set; } = 256;
    public bool EnableLOD { get; set; } = true;
    public int LODLevels { get; set; } = 3;
    
    // World border settings
    public bool EnforceWorldBorder { get; set; } = true;
    public string BorderMessage { get; set; } = "You've reached the world boundary!";
    public bool SoftBorder { get; set; } = true; // Gradual slowdown vs hard wall
    
    // Debug and monitoring
    public bool EnableDebugInfo { get; set; } = false;
    public bool LogChunkGeneration { get; set; } = false;
    public bool TrackPerformanceMetrics { get; set; } = true;
    
    /// <summary>
    /// Create default configuration
    /// </summary>
    public static WorldConfiguration Default => new WorldConfiguration();
    
    /// <summary>
    /// Create small world configuration
    /// </summary>
    public static WorldConfiguration SmallWorld => new WorldConfiguration
    {
        Size = WorldSize.Small,
        CoreWorldRadius = 2000,
        PregenerationRadius = 2000,
        MaxLoadedChunks = 256,
        MaxConcurrentPregenTasks = 4
    };
    
    /// <summary>
    /// Create large world configuration
    /// </summary>
    public static WorldConfiguration LargeWorld => new WorldConfiguration
    {
        Size = WorldSize.Large,
        CoreWorldRadius = 50000,
        PregenerationRadius = 50000,
        MaxLoadedChunks = 1024,
        MaxConcurrentPregenTasks = 16,
        Performance = PerformanceMode.Performance
    };
    
    /// <summary>
    /// Create performance-focused configuration
    /// </summary>
    public static WorldConfiguration PerformanceFocused => new WorldConfiguration
    {
        Mode = WorldMode.Hybrid,
        Size = WorldSize.Medium,
        Performance = PerformanceMode.Performance,
        MaxLoadedChunks = 256,
        ChunkUnloadDistance = 128,
        EnableLOD = true,
        LODLevels = 4
    };
    
    /// <summary>
    /// Validate configuration settings
    /// </summary>
    public bool Validate(out string errorMessage)
    {
        errorMessage = string.Empty;
        
        // Validate world mode and size compatibility
        if (Mode == WorldMode.Pregenerated && Size == WorldSize.Custom && CoreWorldRadius <= 0)
        {
            errorMessage = "Custom world size requires valid CoreWorldRadius > 0";
            return false;
        }
        
        if (Mode == WorldMode.Pregenerated && EnableProceduralExpansion)
        {
            errorMessage = "Procedural expansion cannot be enabled in Pregenerated mode";
            return false;
        }
        
        // Validate radius settings
        if (CoreWorldRadius <= 0 || CoreWorldRadius > 100000)
        {
            errorMessage = "CoreWorldRadius must be between 1 and 100,000 blocks";
            return false;
        }
        
        if (PregenerationRadius <= 0 || PregenerationRadius > 100000)
        {
            errorMessage = "PregenerationRadius must be between 1 and 100,000 blocks";
            return false;
        }
        
        // Validate performance settings
        if (MaxLoadedChunks < 64 || MaxLoadedChunks > 4096)
        {
            errorMessage = "MaxLoadedChunks must be between 64 and 4096";
            return false;
        }
        
        if (MaxConcurrentPregenTasks < 1 || MaxConcurrentPregenTasks > 32)
        {
            errorMessage = "MaxConcurrentPregenTasks must be between 1 and 32";
            return false;
        }
        
        return true;
    }
    
    /// <summary>
    /// Get effective world radius based on mode and size
    /// </summary>
    public int GetEffectiveWorldRadius()
    {
        return Mode switch
        {
            WorldMode.Procedural => int.MaxValue, // Infinite
            WorldMode.Hybrid => CoreWorldRadius,
            WorldMode.Pregenerated => CoreWorldRadius,
            _ => CoreWorldRadius
        };
    }
    
    /// <summary>
    /// Check if position is within world bounds
    /// </summary>
    public bool IsWithinWorldBounds(int x, int z)
    {
        if (Mode == WorldMode.Procedural)
            return true; // Infinite world
            
        var radius = GetEffectiveWorldRadius();
        return x * x + z * z <= radius * radius;
    }
    
    /// <summary>
    /// Check if chunk should be pregenerated vs procedural
    /// </summary>
    public bool ShouldPregenerateChunk(int chunkX, int chunkZ)
    {
        if (Mode == WorldMode.Procedural || !EnablePregeneration)
            return false;
            
        if (Mode == WorldMode.Pregenerated)
            return true;
            
        // Hybrid mode: check if within pregeneration radius
        var worldX = chunkX * 16; // Assuming 16 blocks per chunk
        var worldZ = chunkZ * 16;
        return worldX * worldX + worldZ * worldZ <= PregenerationRadius * PregenerationRadius;
    }
    
    /// <summary>
    /// Get configuration summary for logging
    /// </summary>
    public string GetSummary()
    {
        return $"World: {Mode} {Size} (Radius: {GetEffectiveWorldRadius():N0}), " +
               $"Performance: {Performance}, " +
               $"Pregen: {(EnablePregeneration ? $"{PregenerationRadius:N0}" : "Disabled")}, " +
               $"Expansion: {(EnableProceduralExpansion ? "Enabled" : "Disabled")}";
    }
}
