using System;
using System.Collections.Generic;
using System.IO;

namespace LatticeVeilMonoGame.Core;

/// <summary>
/// World configuration - in-memory grouping only (no separate file).
/// All settings are stored in world.lvc via WorldMeta.
/// </summary>
public sealed class WorldConfig
{
    public int Version { get; set; } = 2;
    public string WorldName { get; set; } = "WORLD";
    public GameMode GameMode { get; set; } = GameMode.Artificer;
    public int Difficulty { get; set; } = 1;
    public int Seed { get; set; }
    public string CreatedAt { get; set; } = string.Empty;
    
    public WorldGenerationSettings WorldGeneration { get; set; } = new();
    public GameplaySettings Gameplay { get; set; } = new();
    public PlayerSettings Player { get; set; } = new();
    public PerformanceSettings Performance { get; set; } = new();

    /// <summary>
    /// Load world configuration from world.lvc (single manifest).
    /// </summary>
    public static WorldConfig? Load(string worldPath, Logger log)
    {
        try
        {
            var worldFile = Path.Combine(worldPath, "world.lvc");
            if (!File.Exists(worldFile))
            {
                log.Warn($"World file not found: {worldFile}");
                return null;
            }

            // Reject JSON format
            if (LvcSerializer.IsJsonFormat(worldFile))
                throw new LvcSerializer.LegacyFormatException($"Legacy JSON world detected: {worldFile}");

            // Fail-fast if legacy files exist
            var legacyLevel = Path.Combine(worldPath, "level.lvc");
            var legacyCfg = Path.Combine(worldPath, "world_config.lvc");
            var legacyChunks = Path.Combine(worldPath, "chunks");
            var legacyMeshcache = Path.Combine(worldPath, "meshcache");

            if (File.Exists(legacyLevel))
                throw new LvcSerializer.LegacyFormatException($"Legacy world detected: level.lvc found");
            if (File.Exists(legacyCfg))
                throw new LvcSerializer.LegacyFormatException($"Legacy world detected: world_config.lvc found");
            if (Directory.Exists(legacyChunks))
                throw new LvcSerializer.LegacyFormatException($"Legacy world detected: chunks/ folder found");
            if (Directory.Exists(legacyMeshcache))
                throw new LvcSerializer.LegacyFormatException($"Legacy world detected: meshcache/ folder found");

            var meta = WorldMeta.Load(worldFile, log);
            if (meta == null)
                return null;

            return FromWorldMeta(meta);
        }
        catch (LvcSerializer.LegacyFormatException)
        {
            throw;
        }
        catch (Exception ex)
        {
            log.Warn($"Failed to load world configuration: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Create WorldConfig from WorldMeta (in-memory only).
    /// </summary>
    public static WorldConfig FromWorldMeta(WorldMeta meta)
    {
        return new WorldConfig
        {
            Version = meta.WorldVersion,
            WorldName = meta.Name,
            GameMode = meta.CurrentWorldGameMode,
            Difficulty = meta.DifficultyLevel,
            Seed = meta.Seed,
            CreatedAt = meta.CreatedAt,
            WorldGeneration = meta.WorldGeneration ?? new WorldGenerationSettings(),
            Gameplay = meta.Gameplay ?? new GameplaySettings(),
            Player = meta.Player ?? new PlayerSettings(),
            Performance = meta.Performance ?? new PerformanceSettings()
        };
    }
}

public sealed class WorldGenerationSettings
{
    public bool GenerateStructures { get; set; } = true;
    public bool GenerateCaves { get; set; } = true;
    public bool GenerateOres { get; set; } = true;
    public bool GenerateTrees { get; set; } = true;
    public string WorldType { get; set; } = "terrain";
    public WorldSizeSettings WorldSize { get; set; } = new();
}

public sealed class WorldSizeSettings
{
    public int Width { get; set; } = 4096;
    public int Height { get; set; } = 256;
    public int Depth { get; set; } = 4096;
}

public sealed class GameplaySettings
{
    public bool EnableCheats { get; set; } = false;
    public bool EnableMultipleHomes { get; set; } = true;
    public int MaxHomesPerPlayer { get; set; } = 8;
    public bool TimeCycleEnabled { get; set; } = true;
    public bool WeatherCycleEnabled { get; set; } = true;
    public int TimeOfDayTicks { get; set; } = 1000;
    public string WeatherState { get; set; } = "clear";
    public List<string> OperatorUsernames { get; set; } = new();
}

public sealed class PlayerSettings
{
    public bool PlayerCollision { get; set; } = true;
    public bool HasCustomSpawn { get; set; } = false;
    public int SpawnX { get; set; } = 0;
    public int SpawnY { get; set; } = 0;
    public int SpawnZ { get; set; } = 0;
}

public sealed class PerformanceSettings
{
    public int MaxLoadedChunks { get; set; } = 512;
    public int ChunkUnloadDistance { get; set; } = 256;
    public bool EnableLOD { get; set; } = true;
    public int LODLevels { get; set; } = 3;
}
