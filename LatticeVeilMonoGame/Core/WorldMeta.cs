using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace LatticeVeilMonoGame.Core;

/// <summary>
/// World metadata stored in key=value .lvc format (NO JSON).
/// File: Worlds/<WorldName>/world.lvc (or whatever Paths.WorldMetaFileName points to).
/// </summary>
public sealed class WorldMeta
{
    // New-format worlds are v2+. (Legacy conversion is intentionally not supported.)
    public int WorldVersion { get; set; } = 2;
    // New-format marker (replaces legacy level.lvc)
    public string Format { get; set; } = "LVWORLD";
    public int FormatVersion { get; set; } = 2;
    public string Name { get; set; } = "WORLD";

    // Keep these as explicit fields so existing game code can read them.
    public GameMode GameMode { get; set; } = GameMode.Artificer;
    public GameMode InitialGameMode { get; set; } = GameMode.Artificer;
    public GameMode CurrentWorldGameMode { get; set; } = GameMode.Artificer;

    public string Generator { get; set; } = "terrain";
    public int Seed { get; set; }
    public string CreatedAt { get; set; } = string.Empty;
    public string WorldId { get; set; } = string.Empty;

    public WorldSize Size { get; set; } = new();

    public bool PlayerCollision { get; set; } = true;
    public bool HasCustomSpawn { get; set; }
    public int SpawnX { get; set; }
    public int SpawnY { get; set; }
    public int SpawnZ { get; set; }

    public bool EnableMultipleHomes { get; set; } = true;
    public int MaxHomesPerPlayer { get; set; } = 8;
    public bool EnableCheats { get; set; } = false;
    public bool TimeCycleEnabled { get; set; } = true;
    public bool WeatherCycleEnabled { get; set; } = true;
    public int TimeOfDayTicks { get; set; } = 1000;
    public string WeatherState { get; set; } = "clear";

    /// <summary>0=Peaceful, 1=Easy, 2=Normal, 3=Hard</summary>
    public int DifficultyLevel { get; set; } = 1;

    public List<string> OperatorUsernames { get; set; } = new();

    // Consolidated settings (replaces legacy world_config.lvc)
    public WorldGenerationSettings WorldGeneration { get; set; } = new();
    public GameplaySettings Gameplay { get; set; } = new();
    public PlayerSettings Player { get; set; } = new();
    public PerformanceSettings Performance { get; set; } = new();

    public static WorldMeta CreateFlat(string name, GameMode mode, int width, int height, int depth, int seed)
    {
        return new WorldMeta
        {
            WorldVersion = 2,
            Name = name,
            GameMode = mode,
            InitialGameMode = mode,
            CurrentWorldGameMode = mode,
            Generator = "flat_v1",
            Seed = seed,
            CreatedAt = DateTimeOffset.UtcNow.ToString("O"),
            WorldId = CreateWorldId(),
            Size = new WorldSize { Width = width, Height = height, Depth = depth },
            PlayerCollision = true,
            EnableMultipleHomes = true,
            MaxHomesPerPlayer = 8,
            EnableCheats = false,
            TimeCycleEnabled = true,
            WeatherCycleEnabled = true,
            TimeOfDayTicks = 1000,
            WeatherState = "clear",
            DifficultyLevel = 1,
            OperatorUsernames = new List<string>(),
            WorldGeneration = new WorldGenerationSettings
            {
                GenerateStructures = true,
                GenerateCaves = true,
                GenerateOres = true,
                GenerateTrees = true,
                WorldType = "flatlands",
                WorldSize = new WorldSizeSettings { Width = width, Height = height, Depth = depth }
            },
            Gameplay = new GameplaySettings
            {
                EnableCheats = false,
                EnableMultipleHomes = true,
                EnableSigilPower = true,
                MaxHomesPerPlayer = 8,
                TimeCycleEnabled = true,
                WeatherCycleEnabled = true,
                TimeOfDayTicks = 1000,
                WeatherState = "clear",
                OperatorUsernames = new List<string>()
            },
            Player = new PlayerSettings
            {
                PlayerCollision = true,
                HasCustomSpawn = false,
                SpawnX = 0,
                SpawnY = 0,
                SpawnZ = 0
            },
            Performance = new PerformanceSettings
            {
                MaxLoadedChunks = 512,
                ChunkUnloadDistance = 256,
                EnableLOD = true,
                LODLevels = 3
            }
        };
    }

    public static WorldMeta CreateTerrain(string name, GameMode mode, int width, int height, int depth, int seed)
    {
        var meta = CreateFlat(name, mode, width, height, depth, seed);
        meta.WorldGeneration.WorldType = "terrain";
        meta.Generator = "terrain";
        return meta;
    }

    public static string CanonicalWorldType(string? worldType)
    {
        return string.Equals(worldType, "flatlands", StringComparison.OrdinalIgnoreCase)
            ? "flatlands"
            : "terrain";
    }

    public static string CanonicalGeneratorForWorldType(string? worldType)
    {
        return string.Equals(CanonicalWorldType(worldType), "flatlands", StringComparison.OrdinalIgnoreCase)
            ? "flat_v1"
            : "terrain"; // Canonical default generator for all non-flat worlds
    }

    public static bool IsTerrainGeneratorId(string? generator)
    {
        var token = (generator ?? string.Empty).Trim();
        return string.Equals(token, "terrain", StringComparison.OrdinalIgnoreCase)
            || string.Equals(token, "terrain_v2", StringComparison.OrdinalIgnoreCase)
            || string.Equals(token, "landscape_v2", StringComparison.OrdinalIgnoreCase);
    }

    public bool HasFiniteWorldBounds()
        => string.Equals(CanonicalWorldType(WorldGeneration?.WorldType), "flatlands", StringComparison.OrdinalIgnoreCase);

    public static string CanonicalWeatherState(string? weatherState)
    {
        var token = (weatherState ?? string.Empty).Trim().ToLowerInvariant();
        return token switch
        {
            "rain" => "rain",
            "storm" => "storm",
            _ => "clear"
        };
    }

    public static int CanonicalTimeTicks(int ticks)
    {
        return ((ticks % 24000) + 24000) % 24000;
    }

    public void CanonicalizeWorldGenerationContract()
    {
        WorldGeneration ??= new WorldGenerationSettings();
        WorldGeneration.WorldType = CanonicalWorldType(WorldGeneration.WorldType);
        if (!string.Equals(WorldGeneration.WorldType, "flatlands", StringComparison.OrdinalIgnoreCase))
            WorldGeneration.GenerateTrees = true;
        Generator = CanonicalGeneratorForWorldType(WorldGeneration.WorldType);
    }

    public void Save(string path, Logger log)
    {
        try
        {
            // Canonicalize + clamp
            Format = "LVWORLD";
            FormatVersion = 2;
            WorldVersion = 2;

            Gameplay ??= new GameplaySettings();
            WorldGeneration ??= new WorldGenerationSettings();
            Player ??= new PlayerSettings();
            Performance ??= new PerformanceSettings();

            CanonicalizeWorldGenerationContract();
            ApplyGroupedSettingsToLegacyFields();

            GameMode = CurrentWorldGameMode;
            DifficultyLevel = Math.Clamp(DifficultyLevel, 0, 3);

            if (string.IsNullOrWhiteSpace(CreatedAt))
                CreatedAt = DateTimeOffset.UtcNow.ToString("O");

            if (string.IsNullOrWhiteSpace(WorldId))
                WorldId = BuildLegacyWorldId(this);

            MaxHomesPerPlayer = Math.Clamp(MaxHomesPerPlayer, 1, 32);
            if (!EnableMultipleHomes)
                MaxHomesPerPlayer = 1;
            TimeOfDayTicks = CanonicalTimeTicks(TimeOfDayTicks);
            WeatherState = CanonicalWeatherState(WeatherState);

            ApplyLegacyFieldsToGroupedSettings();

            // LVC key=value only
            var dict = LvcSerializer.SerializeObject(this);
            RemoveLegacyMirroredFieldsFromSave(dict);
            LvcSerializer.Write(path, dict);
        }
        catch (LvcSerializer.LegacyFormatException)
        {
            // If the existing file is JSON, we intentionally fail.
            throw;
        }
        catch (Exception ex)
        {
            log.Warn($"Failed to save world data: {ex.Message}");
        }
    }

    public static WorldMeta? Load(string path, Logger log)
    {
        try
        {
            if (!File.Exists(path))
            {
                log.Warn($"World data file missing: {path}");
                return null;
            }

            // Strict: no JSON migration.
            if (LvcSerializer.IsJsonFormat(path))
                throw new LvcSerializer.LegacyFormatException($"Legacy JSON world meta detected: {Paths.ToUiPath(path)}");

            var data = LvcSerializer.Read(path);

            var meta = new WorldMeta();
            LvcSerializer.ApplyObject(meta, data);

            // Ensure required markers
            meta.Format = "LVWORLD";
            meta.FormatVersion = 2;

            // Ensure grouped settings exist
            meta.WorldGeneration ??= new WorldGenerationSettings();
            meta.Gameplay ??= new GameplaySettings();
            meta.Player ??= new PlayerSettings();
            meta.Performance ??= new PerformanceSettings();

            meta.CanonicalizeWorldGenerationContract();

            meta.MergeGroupedAndLegacySettings(data);

            // Post-load canonicalization
            meta.MaxHomesPerPlayer = Math.Clamp(meta.MaxHomesPerPlayer, 1, 32);
            if (!meta.EnableMultipleHomes)
                meta.MaxHomesPerPlayer = 1;
            meta.TimeOfDayTicks = CanonicalTimeTicks(meta.TimeOfDayTicks);
            meta.WeatherState = CanonicalWeatherState(meta.WeatherState);
            meta.DifficultyLevel = Math.Clamp(meta.DifficultyLevel, 0, 3);

            meta.ApplyLegacyFieldsToGroupedSettings();

            // Keep the behavior from older code: GameMode mirrors current.
            if (meta.CurrentWorldGameMode == default)
                meta.CurrentWorldGameMode = meta.GameMode;
            if (meta.InitialGameMode == default)
                meta.InitialGameMode = meta.GameMode;
            meta.GameMode = meta.CurrentWorldGameMode;

            if (string.IsNullOrWhiteSpace(meta.CreatedAt))
                meta.CreatedAt = DateTimeOffset.UtcNow.ToString("O");
            if (string.IsNullOrWhiteSpace(meta.WorldId))
                meta.WorldId = BuildLegacyWorldId(meta);

            // Ensure nested object isn't null
            meta.Size ??= new WorldSize();

            return meta;
        }
        catch (LvcSerializer.LegacyFormatException)
        {
            // Caller decides how to present this (popup + abort).
            throw;
        }
        catch (Exception ex)
        {
            log.Warn($"Failed to load world data: {ex.Message}");
            return null;
        }
    }

    private static string CreateWorldId() => Guid.NewGuid().ToString("N");

    private void ApplyGroupedSettingsToLegacyFields()
    {
        EnableCheats = Gameplay.EnableCheats;
        EnableMultipleHomes = Gameplay.EnableMultipleHomes;
        // Sigil power is stored on Gameplay only; old worlds default to enabled.
        MaxHomesPerPlayer = Gameplay.MaxHomesPerPlayer;
        TimeCycleEnabled = Gameplay.TimeCycleEnabled;
        WeatherCycleEnabled = Gameplay.WeatherCycleEnabled;
        TimeOfDayTicks = Gameplay.TimeOfDayTicks;
        WeatherState = Gameplay.WeatherState;
        OperatorUsernames = Gameplay.OperatorUsernames ?? OperatorUsernames;
        PlayerCollision = Player.PlayerCollision;
        HasCustomSpawn = Player.HasCustomSpawn;
        SpawnX = Player.SpawnX;
        SpawnY = Player.SpawnY;
        SpawnZ = Player.SpawnZ;
    }

    private void ApplyLegacyFieldsToGroupedSettings()
    {
        Gameplay.EnableCheats = EnableCheats;
        Gameplay.EnableMultipleHomes = EnableMultipleHomes;
        Gameplay.MaxHomesPerPlayer = MaxHomesPerPlayer;
        Gameplay.TimeCycleEnabled = TimeCycleEnabled;
        Gameplay.WeatherCycleEnabled = WeatherCycleEnabled;
        Gameplay.TimeOfDayTicks = TimeOfDayTicks;
        Gameplay.WeatherState = WeatherState;
        Gameplay.OperatorUsernames = OperatorUsernames ?? Gameplay.OperatorUsernames;
        Player.PlayerCollision = PlayerCollision;
        Player.HasCustomSpawn = HasCustomSpawn;
        Player.SpawnX = SpawnX;
        Player.SpawnY = SpawnY;
        Player.SpawnZ = SpawnZ;
    }

    private void MergeGroupedAndLegacySettings(Dictionary<string, string> data)
    {
        var hasGameplay = data.Keys.Any(k => k.StartsWith("Gameplay.", StringComparison.OrdinalIgnoreCase));
        var hasPlayer = data.Keys.Any(k => k.StartsWith("Player.", StringComparison.OrdinalIgnoreCase));

        if (hasGameplay)
        {
            EnableCheats = Gameplay.EnableCheats;
            EnableMultipleHomes = Gameplay.EnableMultipleHomes;
            MaxHomesPerPlayer = Gameplay.MaxHomesPerPlayer;
            TimeCycleEnabled = Gameplay.TimeCycleEnabled;
            WeatherCycleEnabled = Gameplay.WeatherCycleEnabled;
            TimeOfDayTicks = Gameplay.TimeOfDayTicks;
            WeatherState = Gameplay.WeatherState;
            OperatorUsernames = Gameplay.OperatorUsernames ?? OperatorUsernames;
        }
        else
        {
            Gameplay.EnableCheats = EnableCheats;
            Gameplay.EnableMultipleHomes = EnableMultipleHomes;
            Gameplay.MaxHomesPerPlayer = MaxHomesPerPlayer;
            Gameplay.TimeCycleEnabled = TimeCycleEnabled;
            Gameplay.WeatherCycleEnabled = WeatherCycleEnabled;
            Gameplay.TimeOfDayTicks = TimeOfDayTicks;
            Gameplay.WeatherState = WeatherState;
            Gameplay.OperatorUsernames = OperatorUsernames ?? Gameplay.OperatorUsernames;
        }

        if (hasPlayer)
        {
            PlayerCollision = Player.PlayerCollision;
            HasCustomSpawn = Player.HasCustomSpawn;
            SpawnX = Player.SpawnX;
            SpawnY = Player.SpawnY;
            SpawnZ = Player.SpawnZ;
        }
        else
        {
            Player.PlayerCollision = PlayerCollision;
            Player.HasCustomSpawn = HasCustomSpawn;
            Player.SpawnX = SpawnX;
            Player.SpawnY = SpawnY;
            Player.SpawnZ = SpawnZ;
        }
    }

    private static void RemoveLegacyMirroredFieldsFromSave(Dictionary<string, string> dict)
    {
        dict.Remove(nameof(EnableCheats));
        dict.Remove(nameof(EnableMultipleHomes));
        dict.Remove(nameof(MaxHomesPerPlayer));
        dict.Remove(nameof(TimeCycleEnabled));
        dict.Remove(nameof(WeatherCycleEnabled));
        dict.Remove(nameof(TimeOfDayTicks));
        dict.Remove(nameof(WeatherState));
        dict.Remove(nameof(OperatorUsernames));
        dict.Remove(nameof(PlayerCollision));
        dict.Remove(nameof(HasCustomSpawn));
        dict.Remove(nameof(SpawnX));
        dict.Remove(nameof(SpawnY));
        dict.Remove(nameof(SpawnZ));
    }

    // NOTE: This keeps compatibility with previous world-id scheme so existing worlds don't change IDs.
    private static string BuildLegacyWorldId(WorldMeta meta)
    {
        var payload = $"{meta.Name}|{meta.Seed}|{meta.Size.Width}|{meta.Size.Height}|{meta.Size.Depth}|{meta.CreatedAt}";
        var bytes = Encoding.UTF8.GetBytes(payload);
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
public sealed class WorldSize
{
    public int Width { get; set; }
    public int Height { get; set; }
    public int Depth { get; set; }
}
