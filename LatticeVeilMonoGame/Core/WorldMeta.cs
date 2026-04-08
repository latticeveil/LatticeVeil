using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace LatticeVeilMonoGame.Core;

/// <summary>
/// World metadata stored in key=value .lvc format (NO JSON).
/// File: Worlds/<WorldName>/world.lvc (or whatever Paths.WorldMetaFileName points to).
/// </summary>
public sealed class WorldMeta
{
    // New-format worlds are v2+. Sectioned world.lvc manifests are v3.
    public int WorldVersion { get; set; } = 3;
    // New-format marker (replaces legacy level.lvc)
    public string Format { get; set; } = "LVWORLD";
    public int FormatVersion { get; set; } = 3;
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
            WorldVersion = 3,
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
                CheatsEverEnabled = false,
                EnableGiveItems = true,
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
            FormatVersion = 3;
            WorldVersion = 3;

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

            WriteSectionedWorldManifest(path);
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

            var data = LvcSerializer.IsJsonFormat(path)
                ? ReadLegacyJsonWorldManifest(path)
                : ReadWorldManifest(path);

            var meta = new WorldMeta();
            LvcSerializer.ApplyObject(meta, data);

            // Ensure required markers
            meta.Format = "LVWORLD";
            meta.FormatVersion = Math.Max(meta.FormatVersion, 3);
            meta.WorldVersion = Math.Max(meta.WorldVersion, 3);

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
            meta.ApplyCheatLock();

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

            if (ManifestNeedsRewrite(path, data) || LvcSerializer.IsJsonFormat(path))
                meta.Save(path, log);

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
        Gameplay.CheatsEverEnabled = Gameplay.CheatsEverEnabled || EnableCheats;
        Gameplay.EnableGiveItems = Gameplay.EnableGiveItems;
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
            Gameplay.EnableGiveItems = Gameplay.EnableGiveItems;
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

    private void ApplyCheatLock()
    {
        Gameplay.CheatsEverEnabled = Gameplay.CheatsEverEnabled || Gameplay.EnableCheats || EnableCheats;
        if (Gameplay.CheatsEverEnabled)
        {
            Gameplay.EnableCheats = true;
            EnableCheats = true;
        }
    }

    private static Dictionary<string, string> ReadWorldManifest(string path)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(path))
            return result;

        string? currentSection = null;
        var lines = File.ReadAllLines(path);
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#') || line.StartsWith(';'))
                continue;

            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                currentSection = line.Substring(1, line.Length - 2).Trim();
                continue;
            }

            var eq = line.IndexOf('=');
            if (eq <= 0)
                continue;

            var key = line.Substring(0, eq).Trim();
            if (key.Length == 0)
                continue;

            var normalizedKey = NormalizeManifestKey(currentSection, key);
            if (string.IsNullOrWhiteSpace(normalizedKey))
                continue;

            if (result.ContainsKey(normalizedKey))
                throw new InvalidDataException($"Duplicate world setting detected: {normalizedKey}");

            var value = line.Substring(eq + 1).Trim();
            result[normalizedKey] = UnquoteManifestValue(value);
        }

        return result;
    }

    private static Dictionary<string, string> ReadLegacyJsonWorldManifest(string path)
    {
        using var stream = File.OpenRead(path);
        using var doc = JsonDocument.Parse(stream);

        if (doc.RootElement.ValueKind != JsonValueKind.Object)
            throw new LvcSerializer.LegacyFormatException($"Unsupported legacy JSON world meta shape: {Paths.ToUiPath(path)}");

        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        FlattenLegacyJsonObject(doc.RootElement, result, prefix: string.Empty);
        return result;
    }

    private static void FlattenLegacyJsonObject(JsonElement element, Dictionary<string, string> output, string prefix)
    {
        foreach (var property in element.EnumerateObject())
        {
            var key = string.IsNullOrWhiteSpace(prefix) ? property.Name : prefix + "." + property.Name;
            var value = property.Value;

            switch (value.ValueKind)
            {
                case JsonValueKind.Object:
                    FlattenLegacyJsonObject(value, output, key);
                    break;
                case JsonValueKind.Array:
                    if (value.EnumerateArray().All(item => item.ValueKind == JsonValueKind.String))
                    {
                        output[key] = string.Join(";", value.EnumerateArray().Select(item => EscapeListItem(item.GetString() ?? string.Empty)));
                    }
                    break;
                case JsonValueKind.String:
                    output[key] = value.GetString() ?? string.Empty;
                    break;
                case JsonValueKind.True:
                case JsonValueKind.False:
                    output[key] = value.GetBoolean() ? "true" : "false";
                    break;
                case JsonValueKind.Number:
                    output[key] = value.GetRawText();
                    break;
            }
        }
    }

    private static bool ManifestNeedsRewrite(string path, Dictionary<string, string> data)
    {
        if (!File.Exists(path))
            return false;

        var text = File.ReadAllText(path);
        if (!text.Contains("[WORLD]", StringComparison.OrdinalIgnoreCase))
            return true;

        if (data.TryGetValue(nameof(FormatVersion), out var formatVersion)
            && int.TryParse(formatVersion, out var ver)
            && ver < 3)
        {
            return true;
        }

        return false;
    }

    private void WriteSectionedWorldManifest(string path)
    {
        var gameplayEntries = new List<KeyValuePair<string, string>>
        {
            Kv("cheats", Gameplay.EnableCheats ? "true" : "false"),
            Kv("cheats_ever_enabled", Gameplay.CheatsEverEnabled ? "true" : "false"),
            Kv("Giveitems", Gameplay.EnableGiveItems ? "true" : "false"),
            Kv("multiple_homes", Gameplay.EnableMultipleHomes ? "true" : "false"),
            Kv("max_homes", Gameplay.MaxHomesPerPlayer.ToString()),
            Kv("sigil_power", Gameplay.EnableSigilPower ? "true" : "false"),
            Kv("time_cycle", Gameplay.TimeCycleEnabled ? "true" : "false"),
            Kv("weather_cycle", Gameplay.WeatherCycleEnabled ? "true" : "false"),
            Kv("time_ticks", Gameplay.TimeOfDayTicks.ToString()),
            Kv("weather", Gameplay.WeatherState)
        };

        var operatorsValue = BuildOperatorsValue(Gameplay.OperatorUsernames);
        if (!string.IsNullOrWhiteSpace(operatorsValue))
            gameplayEntries.Add(Kv("operators", operatorsValue));

        var sections = new (string Name, IReadOnlyList<KeyValuePair<string, string>> Entries)[]
        {
            ("WORLD", new[]
            {
                Kv("format", Format),
                Kv("format_version", FormatVersion.ToString()),
                Kv("version", WorldVersion.ToString()),
                Kv("name", Name),
                Kv("id", WorldId),
                Kv("created_at", CreatedAt),
                Kv("seed", Seed.ToString()),
                Kv("generator", Generator),
                Kv("world_type", WorldGeneration.WorldType)
            }),
            ("MODES", new[]
            {
                Kv("game_mode", GameMode.ToString()),
                Kv("initial_mode", InitialGameMode.ToString()),
                Kv("current_mode", CurrentWorldGameMode.ToString()),
                Kv("difficulty", DifficultyLevel.ToString())
            }),
            ("SIZE", new[]
            {
                Kv("width", Size.Width.ToString()),
                Kv("height", Size.Height.ToString()),
                Kv("depth", Size.Depth.ToString())
            }),
            ("SPAWN", new[]
            {
                Kv("custom_spawn", Player.HasCustomSpawn ? "true" : "false"),
                Kv("x", Player.SpawnX.ToString()),
                Kv("y", Player.SpawnY.ToString()),
                Kv("z", Player.SpawnZ.ToString())
            }),
            ("GAMEPLAY", gameplayEntries),
            ("PLAYER", new[]
            {
                Kv("collision", Player.PlayerCollision ? "true" : "false")
            }),
            ("WORLDGEN", new[]
            {
                Kv("structures", WorldGeneration.GenerateStructures ? "true" : "false"),
                Kv("caves", WorldGeneration.GenerateCaves ? "true" : "false"),
                Kv("ores", WorldGeneration.GenerateOres ? "true" : "false"),
                Kv("trees", WorldGeneration.GenerateTrees ? "true" : "false")
            }),
            ("PERFORMANCE", new[]
            {
                Kv("max_loaded_chunks", Performance.MaxLoadedChunks.ToString()),
                Kv("chunk_unload_distance", Performance.ChunkUnloadDistance.ToString()),
                Kv("lod", Performance.EnableLOD ? "true" : "false"),
                Kv("lod_levels", Performance.LODLevels.ToString())
            })
        };

        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        var lines = new List<string>();
        foreach (var section in sections)
        {
            if (lines.Count > 0)
                lines.Add(string.Empty);

            lines.Add($"[{section.Name}]");
            foreach (var entry in section.Entries)
                lines.Add($"{entry.Key}={QuoteManifestValue(entry.Value)}");
        }

        File.WriteAllLines(path, lines);
    }

    private static string NormalizeManifestKey(string? section, string key)
    {
        var normalizedKey = (key ?? string.Empty).Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(section))
            return normalizedKey switch
            {
                "world_name" => nameof(Name),
                "created_utc" => nameof(CreatedAt),
                "generator_id" => nameof(Generator),
                "world_type" => "WorldGeneration.WorldType",
                "region_size_chunks" => string.Empty,
                "game_mode" => nameof(GameMode),
                "difficulty" => nameof(DifficultyLevel),
                "cheats_enabled" => nameof(EnableCheats),
                "spawn_x" => nameof(SpawnX),
                "spawn_y" => nameof(SpawnY),
                "spawn_z" => nameof(SpawnZ),
                _ => key
            };

        return section.Trim().ToUpperInvariant() switch
        {
            "WORLD" => normalizedKey switch
            {
                "format" => nameof(Format),
                "format_version" => nameof(FormatVersion),
                "version" => nameof(WorldVersion),
                "name" => nameof(Name),
                "id" => nameof(WorldId),
                "created_at" => nameof(CreatedAt),
                "seed" => nameof(Seed),
                "generator" => nameof(Generator),
                "world_type" => "WorldGeneration.WorldType",
                _ => $"World.{key}"
            },
            "MODES" => normalizedKey switch
            {
                "game_mode" => nameof(GameMode),
                "initial_mode" => nameof(InitialGameMode),
                "current_mode" => nameof(CurrentWorldGameMode),
                "difficulty" => nameof(DifficultyLevel),
                _ => $"Modes.{key}"
            },
            "SIZE" => normalizedKey switch
            {
                "width" => "Size.Width",
                "height" => "Size.Height",
                "depth" => "Size.Depth",
                _ => $"Size.{key}"
            },
            "SPAWN" => normalizedKey switch
            {
                "custom_spawn" => "Player.HasCustomSpawn",
                "x" => "Player.SpawnX",
                "y" => "Player.SpawnY",
                "z" => "Player.SpawnZ",
                _ => $"Spawn.{key}"
            },
            "GAMEPLAY" => normalizedKey switch
            {
                "cheats" => "Gameplay.EnableCheats",
                "cheats_ever_enabled" => "Gameplay.CheatsEverEnabled",
                "giveitems" => "Gameplay.EnableGiveItems",
                "multiple_homes" => "Gameplay.EnableMultipleHomes",
                "max_homes" => "Gameplay.MaxHomesPerPlayer",
                "sigil_power" => "Gameplay.EnableSigilPower",
                "time_cycle" => "Gameplay.TimeCycleEnabled",
                "weather_cycle" => "Gameplay.WeatherCycleEnabled",
                "time_ticks" => "Gameplay.TimeOfDayTicks",
                "weather" => "Gameplay.WeatherState",
                "operators" => "Gameplay.OperatorUsernames",
                _ => $"Gameplay.{key}"
            },
            "PLAYER" => normalizedKey switch
            {
                "collision" => "Player.PlayerCollision",
                _ => $"Player.{key}"
            },
            "WORLDGEN" => normalizedKey switch
            {
                "structures" => "WorldGeneration.GenerateStructures",
                "caves" => "WorldGeneration.GenerateCaves",
                "ores" => "WorldGeneration.GenerateOres",
                "trees" => "WorldGeneration.GenerateTrees",
                _ => $"WorldGeneration.{key}"
            },
            "PERFORMANCE" => normalizedKey switch
            {
                "max_loaded_chunks" => "Performance.MaxLoadedChunks",
                "chunk_unload_distance" => "Performance.ChunkUnloadDistance",
                "lod" => "Performance.EnableLOD",
                "lod_levels" => "Performance.LODLevels",
                _ => $"Performance.{key}"
            },
            _ => $"{section}.{key}"
        };
    }

    private static KeyValuePair<string, string> Kv(string key, string value) => new(key, value ?? string.Empty);

    private static string QuoteManifestValue(string value)
    {
        value ??= string.Empty;
        if (value.Length == 0)
            return "\"\"";

        foreach (var ch in value)
        {
            if (char.IsWhiteSpace(ch) || ch == '"' || ch == '=' || ch == '#' || ch == ';')
                return "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
        }

        return value;
    }

    private static string UnquoteManifestValue(string value)
    {
        if (value.Length >= 2 && value[0] == '"' && value[^1] == '"')
            return value.Substring(1, value.Length - 2).Replace("\\\"", "\"").Replace("\\\\", "\\");
        return value;
    }

    private static string EscapeListItem(string value)
    {
        value ??= string.Empty;
        return value.Replace("\\", "\\\\").Replace(";", "\\;");
    }

    private static string BuildOperatorsValue(List<string>? operators)
    {
        if (operators == null || operators.Count == 0)
            return string.Empty;

        return string.Join(";", operators
            .Where(op => !string.IsNullOrWhiteSpace(op))
            .Select(op => EscapeListItem(op.Trim())));
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
