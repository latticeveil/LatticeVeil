using System;
using System.IO;
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
    public GameMode GameMode { get; set; } = GameMode.Sandbox;

    [JsonPropertyName("generator")]
    public string Generator { get; set; } = "flat_v1";

    [JsonPropertyName("seed")]
    public int Seed { get; set; }

    [JsonPropertyName("created_at")]
    public string CreatedAt { get; set; } = string.Empty;

    [JsonPropertyName("size")]
    public WorldSize Size { get; set; } = new();

    [JsonPropertyName("playerCollision")]
    public bool PlayerCollision { get; set; } = true;

    public static WorldMeta CreateFlat(string name, GameMode mode, int width, int height, int depth, int seed)
    {
        return new WorldMeta
        {
            Name = name,
            GameMode = mode,
            Generator = "flat_v1",
            Seed = seed,
            CreatedAt = DateTimeOffset.UtcNow.ToString("O"),
            Size = new WorldSize { Width = width, Height = height, Depth = depth },
            PlayerCollision = true
        };
    }

    public void Save(string path, Logger log)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(CreatedAt))
                CreatedAt = DateTimeOffset.UtcNow.ToString("O");

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
                log.Warn($"World data file missing: {path}");
                return null;
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
                GameMode = ParseMode(ReadString(root, "gameMode") ?? ReadString(root, "Mode") ?? ReadString(root, "mode")),
                PlayerCollision = ReadBool(root, "playerCollision") ?? ReadBool(root, "PlayerCollision") ?? true
            };

            var size = ReadSize(root);
            if (size != null)
                meta.Size = size;

            if (string.IsNullOrWhiteSpace(meta.CreatedAt))
                meta.CreatedAt = DateTimeOffset.UtcNow.ToString("O");

            return meta;
        }
        catch (Exception ex)
        {
            log.Warn($"Failed to load world data: {ex.Message}");
            return null;
        }
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

    private static GameMode ParseMode(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return GameMode.Sandbox;

        if (Enum.TryParse<GameMode>(value, ignoreCase: true, out var parsed))
            return parsed;

        return GameMode.Sandbox;
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
