using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text.Json;

namespace LatticeVeilMonoGame.Core;

public sealed class PlayerWorldState
{
    private const int CurrentVersion = 4;

    public int Version { get; set; } = CurrentVersion;
    public string Username { get; set; } = "";
    public float PosX { get; set; }
    public float PosY { get; set; }
    public float PosZ { get; set; }
    public float Yaw { get; set; }
    public float Pitch { get; set; }
    public bool IsFlying { get; set; }
    public GameMode CurrentGameMode { get; set; } = GameMode.Artificer;
    public bool HasHome { get; set; }
    public float HomeX { get; set; }
    public float HomeY { get; set; }
    public float HomeZ { get; set; }
    public List<PlayerHomeState> Homes { get; set; } = new();
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
            Version = CurrentVersion;
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
            bw.Write(IsFlying);
            bw.Write((byte)CurrentGameMode);
            bw.Write(HasHome);
            bw.Write(HomeX);
            bw.Write(HomeY);
            bw.Write(HomeZ);
            bw.Write(SelectedIndex);

            var slots = Hotbar ?? Array.Empty<HotbarSlot>();
            var count = Math.Min(slots.Length, Inventory.HotbarSize);
            bw.Write((byte)count);
            for (int i = 0; i < count; i++)
            {
                bw.Write((byte)slots[i].Id);
                bw.Write(slots[i].Count);
            }

            var homes = Homes ?? new List<PlayerHomeState>();
            var homeCount = Math.Min(homes.Count, 255);
            bw.Write((byte)homeCount);
            for (int i = 0; i < homeCount; i++)
            {
                var home = homes[i] ?? new PlayerHomeState();
                bw.Write(home.Name ?? string.Empty);
                bw.Write(home.PosX);
                bw.Write(home.PosY);
                bw.Write(home.PosZ);
                bw.Write(home.IconBlockId ?? string.Empty);
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
            if (state.Version >= 2)
                state.IsFlying = br.ReadBoolean();
            if (state.Version >= 3)
            {
                state.CurrentGameMode = (GameMode)br.ReadByte();
                state.HasHome = br.ReadBoolean();
                state.HomeX = br.ReadSingle();
                state.HomeY = br.ReadSingle();
                state.HomeZ = br.ReadSingle();
            }
            state.SelectedIndex = br.ReadInt32();

            var count = br.ReadByte();
            state.Hotbar = new HotbarSlot[Inventory.HotbarSize];
            for (int i = 0; i < count && i < state.Hotbar.Length; i++)
            {
                var id = (BlockId)br.ReadByte();
                var c = br.ReadInt32();
                state.Hotbar[i] = new HotbarSlot { Id = id, Count = c };
            }

            if (state.Version >= 4)
            {
                var homeCount = br.ReadByte();
                state.Homes = new List<PlayerHomeState>(homeCount);
                for (int i = 0; i < homeCount; i++)
                {
                    var home = new PlayerHomeState
                    {
                        Name = br.ReadString(),
                        PosX = br.ReadSingle(),
                        PosY = br.ReadSingle(),
                        PosZ = br.ReadSingle(),
                        IconBlockId = br.ReadString()
                    };
                    if (!string.IsNullOrWhiteSpace(home.Name))
                        state.Homes.Add(home);
                }
            }

            // Backward migration from single-home fields.
            if (state.Homes.Count == 0 && state.HasHome)
            {
                state.Homes.Add(new PlayerHomeState
                {
                    Name = "home",
                    PosX = state.HomeX,
                    PosY = state.HomeY,
                    PosZ = state.HomeZ
                });
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
                Pitch = ReadFloat(root, "Pitch") ?? 0f,
                IsFlying = ReadBool(root, "IsFlying") ?? false,
                CurrentGameMode = ParseGameModeToken(ReadString(root, "CurrentGameMode") ?? ReadString(root, "GameMode")),
                HasHome = ReadBool(root, "HasHome") ?? false,
                HomeX = ReadFloat(root, "HomeX") ?? 0f,
                HomeY = ReadFloat(root, "HomeY") ?? 0f,
                HomeZ = ReadFloat(root, "HomeZ") ?? 0f,
                Homes = ReadHomes(root)
            };

            if (state.Homes.Count == 0 && state.HasHome)
            {
                state.Homes.Add(new PlayerHomeState
                {
                    Name = "home",
                    PosX = state.HomeX,
                    PosY = state.HomeY,
                    PosZ = state.HomeZ
                });
            }

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

    private static bool? ReadBool(JsonElement element, string name)
    {
        if (!TryGetProperty(element, name, out var prop))
            return null;
        if (prop.ValueKind == JsonValueKind.True)
            return true;
        if (prop.ValueKind == JsonValueKind.False)
            return false;
        return null;
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

    private static List<PlayerHomeState> ReadHomes(JsonElement root)
    {
        var homes = new List<PlayerHomeState>();
        if (!TryGetProperty(root, "Homes", out var homesNode) && !TryGetProperty(root, "homes", out homesNode))
            return homes;
        if (homesNode.ValueKind != JsonValueKind.Array)
            return homes;

        foreach (var node in homesNode.EnumerateArray())
        {
            var name = ReadString(node, "Name") ?? ReadString(node, "name") ?? string.Empty;
            if (string.IsNullOrWhiteSpace(name))
                continue;

            homes.Add(new PlayerHomeState
            {
                Name = name.Trim(),
                PosX = ReadFloat(node, "PosX") ?? ReadFloat(node, "x") ?? 0f,
                PosY = ReadFloat(node, "PosY") ?? ReadFloat(node, "y") ?? 0f,
                PosZ = ReadFloat(node, "PosZ") ?? ReadFloat(node, "z") ?? 0f,
                IconBlockId = ReadString(node, "IconBlockId") ?? ReadString(node, "iconBlockId") ?? string.Empty
            });
        }

        return homes;
    }

    private static GameMode ParseGameModeToken(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return GameMode.Artificer;

        var token = value.Trim();
        if (Enum.TryParse<GameMode>(token, true, out var parsed))
            return parsed;

        token = token.ToLowerInvariant();
        return token switch
        {
            "creative" or "c" or "1" => GameMode.Artificer,
            "survival" or "s" or "0" => GameMode.Veilwalker,
            "spectator" or "sp" or "3" => GameMode.Veilseer,
            _ => GameMode.Artificer
        };
    }
}

public sealed class PlayerHomeState
{
    public string Name { get; set; } = "home";
    public float PosX { get; set; }
    public float PosY { get; set; }
    public float PosZ { get; set; }
    public string IconBlockId { get; set; } = string.Empty;
}
