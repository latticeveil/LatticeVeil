using System;
using System.IO;
using System.IO.Compression;
using System.Text.Json;

namespace RedactedCraftMonoGame.Core;

public sealed class PlayerWorldState
{
    public int Version { get; set; } = 1;
    public string Username { get; set; } = "";
    public float PosX { get; set; }
    public float PosY { get; set; }
    public float PosZ { get; set; }
    public float Yaw { get; set; }
    public float Pitch { get; set; }
    public int SelectedIndex { get; set; }
    public HotbarSlot[] Hotbar { get; set; } = new HotbarSlot[Inventory.HotbarSize];

    public static PlayerWorldState LoadOrDefault(string worldPath, string username, Func<PlayerWorldState> defaultFactory, Logger log)
    {
        var safeName = SanitizeUsername(username);
        var path = GetSavePath(worldPath, safeName);
        if (File.Exists(path))
        {
            if (TryLoadBinary(path, log, out var binary))
                return binary;
        }

        var legacyPath = GetLegacyJsonPath(worldPath, safeName);
        if (File.Exists(legacyPath))
        {
            var legacy = TryLoadLegacyJson(legacyPath, log);
            if (legacy != null)
                return legacy;
        }

        return defaultFactory();

    }

    public void Save(string worldPath, Logger log)
    {
        try
        {
            var safeName = SanitizeUsername(Username);
            var playersDir = Path.Combine(worldPath, "players");
            Directory.CreateDirectory(playersDir);
            var path = GetSavePath(worldPath, safeName);

            using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
            using var ds = new DeflateStream(fs, CompressionLevel.Fastest);
            using var bw = new BinaryWriter(ds);

            bw.Write(Version);
            bw.Write(Username ?? string.Empty);
            bw.Write(PosX);
            bw.Write(PosY);
            bw.Write(PosZ);
            bw.Write(Yaw);
            bw.Write(Pitch);
            bw.Write(SelectedIndex);

            var slots = Hotbar ?? Array.Empty<HotbarSlot>();
            var count = Math.Min(slots.Length, Inventory.HotbarSize);
            bw.Write((byte)count);
            for (int i = 0; i < count; i++)
            {
                bw.Write((byte)slots[i].Id);
                bw.Write(slots[i].Count);
            }
        }
        catch (Exception ex)
        {
            log.Warn($"Failed to save player state: {ex.Message}");
        }
    }

    private static string GetSavePath(string worldPath, string safeName)
    {
        var playersDir = Path.Combine(worldPath, "players");
        return Path.Combine(playersDir, $"{safeName}.dat");
    }

    private static string GetLegacyJsonPath(string worldPath, string safeName)
    {
        var playersDir = Path.Combine(worldPath, "players");
        return Path.Combine(playersDir, $"{safeName}.json");
    }

    private static string SanitizeUsername(string? username)
    {
        var name = (username ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(name))
            return "PLAYER";

        var invalid = Path.GetInvalidFileNameChars();
        var chars = name.ToCharArray();
        for (int i = 0; i < chars.Length; i++)
        {
            if (Array.IndexOf(invalid, chars[i]) >= 0)
                chars[i] = '_';
        }

        var safe = new string(chars);
        return string.IsNullOrWhiteSpace(safe) ? "PLAYER" : safe;
    }

    private static bool TryLoadBinary(string path, Logger log, out PlayerWorldState state)
    {
        state = new PlayerWorldState();
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var ds = new DeflateStream(fs, CompressionMode.Decompress);
            using var br = new BinaryReader(ds);

            state.Version = br.ReadInt32();
            state.Username = br.ReadString();
            state.PosX = br.ReadSingle();
            state.PosY = br.ReadSingle();
            state.PosZ = br.ReadSingle();
            state.Yaw = br.ReadSingle();
            state.Pitch = br.ReadSingle();
            state.SelectedIndex = br.ReadInt32();

            var count = br.ReadByte();
            state.Hotbar = new HotbarSlot[Inventory.HotbarSize];
            for (int i = 0; i < count && i < state.Hotbar.Length; i++)
            {
                var id = (BlockId)br.ReadByte();
                var c = br.ReadInt32();
                state.Hotbar[i] = new HotbarSlot { Id = id, Count = c };
            }

            return true;
        }
        catch (Exception ex)
        {
            log.Warn($"Failed to load player state (binary): {ex.Message}");
            return false;
        }
    }

    private static PlayerWorldState? TryLoadLegacyJson(string path, Logger log)
    {
        try
        {
            var json = File.ReadAllText(path);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var state = new PlayerWorldState
            {
                Username = ReadString(root, "Username") ?? "PLAYER",
                PosX = ReadFloat(root, "PosX") ?? 0f,
                PosY = ReadFloat(root, "PosY") ?? 0f,
                PosZ = ReadFloat(root, "PosZ") ?? 0f,
                Yaw = ReadFloat(root, "Yaw") ?? 0f,
                Pitch = ReadFloat(root, "Pitch") ?? 0f
            };

            if (TryGetProperty(root, "Inventory", out var inv) && TryGetProperty(inv, "Slots", out var slots) && slots.ValueKind == JsonValueKind.Array)
            {
                var i = 0;
                foreach (var slot in slots.EnumerateArray())
                {
                    if (i >= state.Hotbar.Length)
                        break;
                    var itemId = ReadString(slot, "ItemId");
                    var count = ReadInt(slot, "Count") ?? 0;
                    var id = ResolveBlockId(itemId);
                    state.Hotbar[i] = new HotbarSlot { Id = id, Count = count };
                    i++;
                }
            }

            return state;
        }
        catch (Exception ex)
        {
            log.Warn($"Failed to load player state (legacy JSON): {ex.Message}");
            return null;
        }
    }

    private static BlockId ResolveBlockId(string? itemId)
    {
        if (string.IsNullOrWhiteSpace(itemId))
            return BlockId.Air;

        var normalized = itemId.Trim();
        if (Enum.TryParse<BlockId>(normalized, true, out var parsed))
            return parsed;

        foreach (var def in BlockRegistry.All)
        {
            if (string.Equals(def.Name, normalized, StringComparison.OrdinalIgnoreCase))
                return def.Id;
        }

        return BlockId.Air;
    }

    private static string? ReadString(JsonElement element, string name)
    {
        if (!TryGetProperty(element, name, out var prop))
            return null;
        return prop.ValueKind == JsonValueKind.String ? prop.GetString() : prop.ToString();
    }

    private static float? ReadFloat(JsonElement element, string name)
    {
        if (!TryGetProperty(element, name, out var prop))
            return null;
        return prop.ValueKind == JsonValueKind.Number && prop.TryGetSingle(out var v) ? v : null;
    }

    private static int? ReadInt(JsonElement element, string name)
    {
        if (!TryGetProperty(element, name, out var prop))
            return null;
        return prop.ValueKind == JsonValueKind.Number && prop.TryGetInt32(out var v) ? v : null;
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
