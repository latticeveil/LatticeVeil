using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LatticeVeilMonoGame.Core;

/// <summary>
/// Consolidated world configuration file that contains all world settings
/// This file is text-editable and stores all configuration in one place
/// </summary>
public sealed class WorldConfig
{
    [JsonPropertyName("version")]
    public int Version { get; set; } = 1;

    [JsonPropertyName("worldName")]
    public string WorldName { get; set; } = string.Empty;

    [JsonPropertyName("gameMode")]
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public GameMode GameMode { get; set; } = GameMode.Artificer;

    [JsonPropertyName("difficulty")]
    public int Difficulty { get; set; } = 1; // 0=Peaceful, 1=Easy, 2=Normal, 3=Hard

    [JsonPropertyName("seed")]
    public int Seed { get; set; }

    [JsonPropertyName("createdAt")]
    public string CreatedAt { get; set; } = string.Empty;

    // World Generation Settings
    [JsonPropertyName("worldGeneration")]
    public WorldGenerationSettings WorldGeneration { get; set; } = new();

    // Gameplay Settings
    [JsonPropertyName("gameplay")]
    public GameplaySettings Gameplay { get; set; } = new();

    // Player Settings
    [JsonPropertyName("player")]
    public PlayerSettings Player { get; set; } = new();

    // Performance Settings
    [JsonPropertyName("performance")]
    public PerformanceSettings Performance { get; set; } = new();

    public void Save(string worldPath, Logger log)
    {
        try
        {
            var configPath = Path.Combine(worldPath, Paths.WorldConfigFileName);
            var options = new JsonSerializerOptions 
            { 
                WriteIndented = true,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            };
            options.Converters.Add(new JsonStringEnumConverter());
            
            var json = JsonSerializer.Serialize(this, options);
            File.WriteAllText(configPath, json);
            
            log.Info($"World configuration saved to: {configPath}");
        }
        catch (Exception ex)
        {
            log.Warn($"Failed to save world configuration: {ex.Message}");
        }
    }

    public static WorldConfig? Load(string worldPath, Logger log)
    {
        try
        {
            var configPath = Path.Combine(worldPath, Paths.WorldConfigFileName);
            if (!File.Exists(configPath))
            {
                var legacyPath = Path.Combine(worldPath, Paths.LegacyWorldConfigFileName);
                if (File.Exists(legacyPath))
                {
                    configPath = legacyPath;
                    TryMigrateLegacyConfigFile(worldPath, legacyPath, log);
                }
            }

            if (!File.Exists(configPath))
            {
                log.Warn($"World configuration file not found: {configPath}");
                return null;
            }

            var json = File.ReadAllText(configPath);
            var options = new JsonSerializerOptions 
            { 
                PropertyNameCaseInsensitive = true
            };
            options.Converters.Add(new JsonStringEnumConverter());
            
            var config = JsonSerializer.Deserialize<WorldConfig>(json, options);
            return config;
        }
        catch (Exception ex)
        {
            log.Warn($"Failed to load world configuration: {ex.Message}");
            return null;
        }
    }

    private static void TryMigrateLegacyConfigFile(string worldPath, string legacyPath, Logger log)
    {
        try
        {
            var targetPath = Path.Combine(worldPath, Paths.WorldConfigFileName);
            if (File.Exists(targetPath))
                return;

            File.Move(legacyPath, targetPath);
            log.Info($"Migrated world config: {Path.GetFileName(legacyPath)} -> {Path.GetFileName(targetPath)}");
        }
        catch (Exception ex)
        {
            log.Warn($"Failed to migrate legacy world config file: {ex.Message}");
        }
    }

    public static WorldConfig FromWorldMeta(WorldMeta meta)
    {
        return new WorldConfig
        {
            WorldName = meta.Name,
            GameMode = meta.GameMode,
            Difficulty = meta.DifficultyLevel,
            Seed = meta.Seed,
            CreatedAt = meta.CreatedAt,
            WorldGeneration = new WorldGenerationSettings
            {
                GenerateStructures = true, // Default - should be from UI
                GenerateCaves = true,     // Default - should be from UI
                GenerateOres = true,      // Default - should be from UI
                WorldSize = new WorldSizeSettings
                {
                    Width = meta.Size.Width,
                    Height = meta.Size.Height,
                    Depth = meta.Size.Depth
                }
            },
            Gameplay = new GameplaySettings
            {
                EnableCheats = meta.EnableCheats,
                EnableMultipleHomes = meta.EnableMultipleHomes,
                MaxHomesPerPlayer = meta.MaxHomesPerPlayer
            },
            Player = new PlayerSettings
            {
                PlayerCollision = meta.PlayerCollision,
                HasCustomSpawn = meta.HasCustomSpawn,
                SpawnX = meta.SpawnX,
                SpawnY = meta.SpawnY,
                SpawnZ = meta.SpawnZ
            },
            Performance = new PerformanceSettings
            {
                MaxLoadedChunks = 512,
                ChunkUnloadDistance = 256,
                EnableLOD = true
            }
        };
    }
}

public sealed class WorldGenerationSettings
{
    [JsonPropertyName("generateStructures")]
    public bool GenerateStructures { get; set; } = true;

    [JsonPropertyName("generateCaves")]
    public bool GenerateCaves { get; set; } = true;

    [JsonPropertyName("generateOres")]
    public bool GenerateOres { get; set; } = true;

    [JsonPropertyName("worldSize")]
    public WorldSizeSettings WorldSize { get; set; } = new();
}

public sealed class WorldSizeSettings
{
    [JsonPropertyName("width")]
    public int Width { get; set; } = 4096;

    [JsonPropertyName("height")]
    public int Height { get; set; } = 256;

    [JsonPropertyName("depth")]
    public int Depth { get; set; } = 4096;
}

public sealed class GameplaySettings
{
    [JsonPropertyName("enableCheats")]
    public bool EnableCheats { get; set; } = true;

    [JsonPropertyName("enableMultipleHomes")]
    public bool EnableMultipleHomes { get; set; } = true;

    [JsonPropertyName("maxHomesPerPlayer")]
    public int MaxHomesPerPlayer { get; set; } = 8;

    [JsonPropertyName("operatorUsernames")]
    public List<string> OperatorUsernames { get; set; } = new();
}

public sealed class PlayerSettings
{
    [JsonPropertyName("playerCollision")]
    public bool PlayerCollision { get; set; } = true;

    [JsonPropertyName("hasCustomSpawn")]
    public bool HasCustomSpawn { get; set; } = false;

    [JsonPropertyName("spawnX")]
    public int SpawnX { get; set; } = 0;

    [JsonPropertyName("spawnY")]
    public int SpawnY { get; set; } = 0;

    [JsonPropertyName("spawnZ")]
    public int SpawnZ { get; set; } = 0;
}

public sealed class PerformanceSettings
{
    [JsonPropertyName("maxLoadedChunks")]
    public int MaxLoadedChunks { get; set; } = 512;

    [JsonPropertyName("chunkUnloadDistance")]
    public int ChunkUnloadDistance { get; set; } = 256;

    [JsonPropertyName("enableLOD")]
    public bool EnableLOD { get; set; } = true;

    [JsonPropertyName("lodLevels")]
    public int LODLevels { get; set; } = 3;
}
