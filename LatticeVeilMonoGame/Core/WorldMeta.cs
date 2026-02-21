using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LatticeVeilMonoGame.Core;

public sealed class WorldMeta
{
    [JsonPropertyName("worldVersion")]
    public int WorldVersion { get; set; } = 1;

    [JsonPropertyName("name")]
    public string Name { get; set; } = "WORLD";

    [JsonPropertyName("gameMode")]
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public GameMode GameMode { get; set; } = GameMode.Artificer;

    [JsonPropertyName("initialGameMode")]
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public GameMode InitialGameMode { get; set; } = GameMode.Artificer;

    [JsonPropertyName("currentWorldGameMode")]
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public GameMode CurrentWorldGameMode { get; set; } = GameMode.Artificer;

    [JsonPropertyName("generator")]
    public string Generator { get; set; } = "flat_v1";

    [JsonPropertyName("seed")]
    public int Seed { get; set; }

    [JsonPropertyName("created_at")]
    public string CreatedAt { get; set; } = string.Empty;

    [JsonPropertyName("worldId")]
    public string WorldId { get; set; } = string.Empty;

    [JsonPropertyName("size")]
    public WorldSize Size { get; set; } = new();

    [JsonPropertyName("playerCollision")]
    public bool PlayerCollision { get; set; } = true;

    [JsonPropertyName("hasCustomSpawn")]
    public bool HasCustomSpawn { get; set; }

    [JsonPropertyName("spawnX")]
    public int SpawnX { get; set; }

    [JsonPropertyName("spawnY")]
    public int SpawnY { get; set; }

    [JsonPropertyName("spawnZ")]
    public int SpawnZ { get; set; }

    [JsonPropertyName("enableMultipleHomes")]
    public bool EnableMultipleHomes { get; set; } = true;

    [JsonPropertyName("maxHomesPerPlayer")]
    public int MaxHomesPerPlayer { get; set; } = 8;

    [JsonPropertyName("enableCheats")]
    public bool EnableCheats { get; set; } = true;

    [JsonPropertyName("difficulty")]
    public int DifficultyLevel { get; set; } = 1; // 0=Peaceful, 1=Easy, 2=Normal, 3=Hard

    [JsonPropertyName("operatorUsernames")]
    public List<string> OperatorUsernames { get; set; } = new();

    public static WorldMeta CreateFlat(string name, GameMode mode, int width, int height, int depth, int seed)
    {
        return new WorldMeta
        {
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
            EnableCheats = true,
            DifficultyLevel = 1,
            OperatorUsernames = new List<string>()
        };
    }

    public void Save(string path, Logger log)
    {
        try
        {
            GameMode = CurrentWorldGameMode;
            DifficultyLevel = Math.Clamp(DifficultyLevel, 0, 3);
            if (string.IsNullOrWhiteSpace(CreatedAt))
                CreatedAt = DateTimeOffset.UtcNow.ToString("O");
            if (string.IsNullOrWhiteSpace(WorldId))
                WorldId = BuildLegacyWorldId(this);

            var options = new JsonSerializerOptions { WriteIndented = true };
            options.Converters.Add(new JsonStringEnumConverter());
            var json = JsonSerializer.Serialize(this, options);
            File.WriteAllText(path, json);
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
                var directory = Path.GetDirectoryName(path) ?? string.Empty;
                var legacyPath = Path.Combine(directory, Paths.LegacyWorldMetaFileName);
                if (File.Exists(legacyPath))
                {
                    try
                    {
                        File.Move(legacyPath, path);
                        log.Info($"Migrated world meta: {Path.GetFileName(legacyPath)} -> {Path.GetFileName(path)}");
                    }
                    catch (Exception migrateEx)
                    {
                        log.Warn($"Failed to migrate world meta to {Path.GetFileName(path)}: {migrateEx.Message}");
                        path = legacyPath;
                    }
                }
                else
                {
                    log.Warn($"World data file missing: {path}");
                    return null;
                }
            }

            var json = File.ReadAllText(path);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var meta = new WorldMeta
            {
                WorldVersion = ReadInt(root, "worldVersion") ?? ReadInt(root, "Version") ?? 1,
                Name = ReadString(root, "name") ?? ReadString(root, "Name") ?? "WORLD",
                Generator = ReadString(root, "generator") ?? ReadString(root, "Generator") ?? "flat_v1",
                Seed = ReadInt(root, "seed") ?? ReadInt(root, "Seed") ?? 0,
                CreatedAt = ReadString(root, "created_at") ?? ReadString(root, "createdAt") ?? ReadString(root, "CreatedAt") ?? string.Empty,
                WorldId = ReadString(root, "worldId") ?? ReadString(root, "WorldId") ?? string.Empty,
                GameMode = ParseMode(ReadString(root, "gameMode") ?? ReadString(root, "Mode") ?? ReadString(root, "mode")),
                PlayerCollision = ReadBool(root, "playerCollision") ?? ReadBool(root, "PlayerCollision") ?? true,
                HasCustomSpawn = ReadBool(root, "hasCustomSpawn") ?? ReadBool(root, "HasCustomSpawn") ?? false,
                SpawnX = ReadInt(root, "spawnX") ?? ReadInt(root, "SpawnX") ?? 0,
                SpawnY = ReadInt(root, "spawnY") ?? ReadInt(root, "SpawnY") ?? 0,
                SpawnZ = ReadInt(root, "spawnZ") ?? ReadInt(root, "SpawnZ") ?? 0,
                EnableMultipleHomes = ReadBool(root, "enableMultipleHomes") ?? ReadBool(root, "EnableMultipleHomes") ?? true,
                MaxHomesPerPlayer = ReadInt(root, "maxHomesPerPlayer") ?? ReadInt(root, "MaxHomesPerPlayer") ?? 8,
                EnableCheats = ReadBool(root, "enableCheats")
                    ?? ReadBool(root, "EnableCheats")
                    ?? ReadBool(root, "cheatsEnabled")
                    ?? true,
                DifficultyLevel = ReadInt(root, "difficulty")
                    ?? ReadInt(root, "difficultyLevel")
                    ?? ReadInt(root, "Difficulty")
                    ?? 1,
                OperatorUsernames = ReadStringList(root, "operatorUsernames")
                    ?? ReadStringList(root, "OperatorUsernames")
                    ?? ReadStringList(root, "ops")
                    ?? new List<string>()
            };

            meta.MaxHomesPerPlayer = Math.Clamp(meta.MaxHomesPerPlayer, 1, 32);
            if (!meta.EnableMultipleHomes)
                meta.MaxHomesPerPlayer = 1;
            meta.DifficultyLevel = Math.Clamp(meta.DifficultyLevel, 0, 3);

            meta.InitialGameMode = ParseMode(
                ReadString(root, "initialGameMode")
                ?? ReadString(root, "InitialGameMode")
                ?? ReadString(root, "initial_mode")
                ?? meta.GameMode.ToString());

            meta.CurrentWorldGameMode = ParseMode(
                ReadString(root, "currentWorldGameMode")
                ?? ReadString(root, "CurrentWorldGameMode")
                ?? ReadString(root, "current_mode")
                ?? meta.GameMode.ToString());

            meta.GameMode = meta.CurrentWorldGameMode;

            var size = ReadSize(root);
            if (size != null)
                meta.Size = size;

            if (string.IsNullOrWhiteSpace(meta.CreatedAt))
                meta.CreatedAt = DateTimeOffset.UtcNow.ToString("O");
            if (string.IsNullOrWhiteSpace(meta.WorldId))
                meta.WorldId = BuildLegacyWorldId(meta);

            return meta;
        }
        catch (Exception ex)
        {
            log.Warn($"Failed to load world data: {ex.Message}");
            return null;
        }
    }

    private static List<string>? ReadStringList(JsonElement element, string name)
    {
        if (!TryGetProperty(element, name, out var listNode))
            return null;
        if (listNode.ValueKind != JsonValueKind.Array)
            return null;

        var list = new List<string>();
        foreach (var entry in listNode.EnumerateArray())
        {
            var value = entry.ValueKind == JsonValueKind.String ? entry.GetString() : entry.ToString();
            if (!string.IsNullOrWhiteSpace(value))
                list.Add(value.Trim());
        }

        return list;
    }

    private static WorldSize? ReadSize(JsonElement root)
    {
        if (TryGetProperty(root, "size", out var sizeElem) || TryGetProperty(root, "Size", out sizeElem))
        {
            var width = ReadInt(sizeElem, "Width") ?? ReadInt(sizeElem, "width") ?? 0;
            var height = ReadInt(sizeElem, "Height") ?? ReadInt(sizeElem, "height") ?? 0;
            var depth = ReadInt(sizeElem, "Depth") ?? ReadInt(sizeElem, "depth") ?? 0;
            if (width > 0 && height > 0 && depth > 0)
                return new WorldSize { Width = width, Height = height, Depth = depth };
        }

        return null;
    }

    private static string CreateWorldId()
    {
        return Guid.NewGuid().ToString("N");
    }

    private static string BuildLegacyWorldId(WorldMeta meta)
    {
        var payload =
            $"{meta.Name}|{meta.Seed}|{meta.Size.Width}|{meta.Size.Height}|{meta.Size.Depth}|{meta.CreatedAt}";
        var bytes = Encoding.UTF8.GetBytes(payload);
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static GameMode ParseMode(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return GameMode.Artificer;

        if (Enum.TryParse<GameMode>(value, ignoreCase: true, out var parsed))
            return parsed;

        return GameMode.Artificer;
    }

    private static string? ReadString(JsonElement element, string name)
    {
        if (!TryGetProperty(element, name, out var prop))
            return null;
        return prop.ValueKind == JsonValueKind.String ? prop.GetString() : prop.ToString();
    }

    private static int? ReadInt(JsonElement element, string name)
    {
        if (!TryGetProperty(element, name, out var prop))
            return null;
        return prop.ValueKind == JsonValueKind.Number && prop.TryGetInt32(out var v) ? v : null;
    }

    private static bool? ReadBool(JsonElement element, string name)
    {
        if (!TryGetProperty(element, name, out var prop))
            return null;
        return prop.ValueKind == JsonValueKind.True || prop.ValueKind == JsonValueKind.False ? prop.GetBoolean() : null;
    }

    private static bool TryGetProperty(JsonElement element, string name, out JsonElement value)
    {
        foreach (var prop in element.EnumerateObject())
        {
            if (string.Equals(prop.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = prop.Value;
                return true;
            }
        }

        value = default;
        return false;
    }
}

public sealed class WorldSize
{
    public int Width { get; set; }
    public int Height { get; set; }
    public int Depth { get; set; }
}
