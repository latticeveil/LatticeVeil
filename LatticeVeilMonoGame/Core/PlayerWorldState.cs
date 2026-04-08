using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;

namespace LatticeVeilMonoGame.Core;

public sealed class PlayerWorldState
{
    private const int CurrentVersion = 9;

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
    public HotbarSlot[] InventoryGrid { get; set; } = new HotbarSlot[Inventory.GridSize];
    public int[] ArtificerFavoriteBlockIds { get; set; } = Array.Empty<int>();
    public int Health { get; set; } = SurvivalVitals.MaxHealth;
    public int Hunger { get; set; } = SurvivalVitals.MaxHunger;
    public float SigilAtonement { get; set; }
    public float SigilCurse { get; set; }
    public bool HasSeenAttunementWipPopup { get; set; }

    public static PlayerWorldState LoadOrDefault(string worldPath, string username, Func<PlayerWorldState> defaultFactory, Logger log)
    {
        var safeName = SanitizeUsername(username);
        var path = GetSavePath(worldPath, safeName);
        if (File.Exists(path))
        {
            // Try LVC format first (new format)
            if (LvcSerializer.IsJsonFormat(path))
            {
                log.Warn($"Legacy JSON player state detected: {path}");
                // Could migrate here, but for now fall back to default
                return defaultFactory();
            }
            
            try
            {
                var data = LvcSerializer.Read(path);
                if (data.Count > 0)
                {
                    var state = new PlayerWorldState();
                    if (int.TryParse(data.GetValueOrDefault("version"), out var version))
                        state.Version = version;
                    state.Username = data.GetValueOrDefault("username") ?? username;
                    if (float.TryParse(data.GetValueOrDefault("posX"), out var posX))
                        state.PosX = posX;
                    if (float.TryParse(data.GetValueOrDefault("posY"), out var posY))
                        state.PosY = posY;
                    if (float.TryParse(data.GetValueOrDefault("posZ"), out var posZ))
                        state.PosZ = posZ;
                    if (float.TryParse(data.GetValueOrDefault("yaw"), out var yaw))
                        state.Yaw = yaw;
                    if (float.TryParse(data.GetValueOrDefault("pitch"), out var pitch))
                        state.Pitch = pitch;
                    if (bool.TryParse(data.GetValueOrDefault("isFlying"), out var isFlying))
                        state.IsFlying = isFlying;
                    if (Enum.TryParse<GameMode>(data.GetValueOrDefault("currentGameMode"), true, out var gameMode))
                        state.CurrentGameMode = gameMode;
                    if (bool.TryParse(data.GetValueOrDefault("hasHome"), out var hasHome))
                        state.HasHome = hasHome;
                    if (float.TryParse(data.GetValueOrDefault("homeX"), out var homeX))
                        state.HomeX = homeX;
                    if (float.TryParse(data.GetValueOrDefault("homeY"), out var homeY))
                        state.HomeY = homeY;
                    if (float.TryParse(data.GetValueOrDefault("homeZ"), out var homeZ))
                        state.HomeZ = homeZ;
                    if (int.TryParse(data.GetValueOrDefault("selectedIndex"), out var selectedIndex))
                        state.SelectedIndex = selectedIndex;
                    if (int.TryParse(data.GetValueOrDefault("health"), out var health))
                        state.Health = health;
                    if (int.TryParse(data.GetValueOrDefault("hunger"), out var hunger))
                        state.Hunger = hunger;
                    if (float.TryParse(data.GetValueOrDefault("sigilAtonement"), out var sigilAtonement))
                        state.SigilAtonement = sigilAtonement;
                    if (float.TryParse(data.GetValueOrDefault("sigilCurse"), out var sigilCurse))
                        state.SigilCurse = sigilCurse;
                    if (bool.TryParse(data.GetValueOrDefault("hasSeenAttunementWipPopup"), out var hasSeenAttunementWipPopup))
                        state.HasSeenAttunementWipPopup = hasSeenAttunementWipPopup;
                    
                    // Load homes
                    if (int.TryParse(data.GetValueOrDefault("homesCount"), out var homesCount) && homesCount > 0)
                    {
                        state.Homes = new List<PlayerHomeState>();
                        for (int i = 0; i < homesCount; i++)
                        {
                            var home = new PlayerHomeState
                            {
                                Name = data.GetValueOrDefault($"home.{i}.name") ?? $"home_{i}",
                                PosX = float.TryParse(data.GetValueOrDefault($"home.{i}.posX"), out var hx) ? hx : 0f,
                                PosY = float.TryParse(data.GetValueOrDefault($"home.{i}.posY"), out var hy) ? hy : 0f,
                                PosZ = float.TryParse(data.GetValueOrDefault($"home.{i}.posZ"), out var hz) ? hz : 0f,
                                IconBlockId = data.GetValueOrDefault($"home.{i}.iconBlockId") ?? string.Empty
                            };
                            state.Homes.Add(home);
                        }
                    }
                    
                    // Load hotbar
                    if (int.TryParse(data.GetValueOrDefault("hotbarCount"), out var hotbarCount) && hotbarCount > 0)
                    {
                        state.Hotbar = new HotbarSlot[hotbarCount];
                        for (int i = 0; i < hotbarCount; i++)
                        {
                            var id = BlockId.Air;
                            var count = 0;
                            if (int.TryParse(data.GetValueOrDefault($"hotbar.{i}.id"), out var blockId))
                                id = (BlockId)blockId;
                            if (int.TryParse(data.GetValueOrDefault($"hotbar.{i}.count"), out var stackCount))
                                count = stackCount;
                            
                            state.Hotbar[i] = new HotbarSlot { Id = id, Count = count };
                        }
                    }
                    
                    // Load inventory grid
                    if (int.TryParse(data.GetValueOrDefault("gridCount"), out var gridCount) && gridCount > 0)
                    {
                        state.InventoryGrid = new HotbarSlot[gridCount];
                        for (int i = 0; i < gridCount; i++)
                        {
                            var id = BlockId.Air;
                            var count = 0;
                            if (int.TryParse(data.GetValueOrDefault($"grid.{i}.id"), out var blockId))
                                id = (BlockId)blockId;
                            if (int.TryParse(data.GetValueOrDefault($"grid.{i}.count"), out var stackCount))
                                count = stackCount;
                            
                            state.InventoryGrid[i] = new HotbarSlot { Id = id, Count = count };
                        }
                    }

                    if (int.TryParse(data.GetValueOrDefault("favoriteCount"), out var favoriteCount) && favoriteCount > 0)
                    {
                        var favorites = new List<int>(favoriteCount);
                        for (int i = 0; i < favoriteCount; i++)
                        {
                            if (!int.TryParse(data.GetValueOrDefault($"favorite.{i}.id"), out var favoriteId))
                                continue;
                            if (favoriteId <= 0 || favoriteId > byte.MaxValue)
                                continue;
                            favorites.Add(favoriteId);
                        }

                        state.ArtificerFavoriteBlockIds = favorites.ToArray();
                    }
                    
                    return state;
                }
            }
            catch (Exception ex)
            {
                log.Warn($"Failed to load LVC player state: {ex.Message}");
            }
            
            // Fallback to binary format for existing saves
            if (TryLoadBinary(path, log, out var binary))
                return binary;
        }
        return defaultFactory();
    }

    public void Save(string worldPath, Logger log)
    {
        Save(worldPath, Username, log);
    }

    /// <summary>
    /// Save the player state to a per-world file using a stable save key (PlayerId).
    /// Username remains the display name stored inside the file.
    /// </summary>
    public void Save(string worldPath, string saveKey, Logger log)
    {
        try
        {
            Version = CurrentVersion;
            var safeName = SanitizeUsername(saveKey);
            var playerdataDir = Path.Combine(worldPath, "playerdata");
            Directory.CreateDirectory(playerdataDir);
            var path = GetSavePath(worldPath, safeName);

            // Save as LVC format (readable key=value)
            var data = new Dictionary<string, string>
            {
                ["version"] = Version.ToString(),
                ["username"] = Username,
                ["posX"] = PosX.ToString("F6"),
                ["posY"] = PosY.ToString("F6"),
                ["posZ"] = PosZ.ToString("F6"),
                ["yaw"] = Yaw.ToString("F6"),
                ["pitch"] = Pitch.ToString("F6"),
                ["isFlying"] = IsFlying.ToString(),
                ["currentGameMode"] = CurrentGameMode.ToString(),
                ["hasHome"] = HasHome.ToString(),
                ["homeX"] = HomeX.ToString("F6"),
                ["homeY"] = HomeY.ToString("F6"),
                ["homeZ"] = HomeZ.ToString("F6"),
                ["selectedIndex"] = SelectedIndex.ToString(),
                ["health"] = Health.ToString(),
                ["hunger"] = Hunger.ToString(),
                ["sigilAtonement"] = SigilAtonement.ToString("F6"),
                ["sigilCurse"] = SigilCurse.ToString("F6"),
                ["hasSeenAttunementWipPopup"] = HasSeenAttunementWipPopup.ToString()
            };
            
            // Save homes
            if (Homes != null && Homes.Count > 0)
            {
                data["homesCount"] = Homes.Count.ToString();
                for (int i = 0; i < Homes.Count; i++)
                {
                    var home = Homes[i];
                    data[$"home.{i}.name"] = home.Name ?? string.Empty;
                    data[$"home.{i}.posX"] = home.PosX.ToString("F6");
                    data[$"home.{i}.posY"] = home.PosY.ToString("F6");
                    data[$"home.{i}.posZ"] = home.PosZ.ToString("F6");
                    data[$"home.{i}.iconBlockId"] = home.IconBlockId ?? string.Empty;
                }
            }
            
            // Save hotbar
            if (Hotbar != null && Hotbar.Length > 0)
            {
                data["hotbarCount"] = Hotbar.Length.ToString();
                for (int i = 0; i < Hotbar.Length; i++)
                {
                    var slot = Hotbar[i];
                    data[$"hotbar.{i}.id"] = ((int)slot.Id).ToString();
                    data[$"hotbar.{i}.count"] = slot.Count.ToString();
                }
            }
            
            // Save inventory grid
            if (InventoryGrid != null && InventoryGrid.Length > 0)
            {
                data["gridCount"] = InventoryGrid.Length.ToString();
                for (int i = 0; i < InventoryGrid.Length; i++)
                {
                    var slot = InventoryGrid[i];
                    data[$"grid.{i}.id"] = ((int)slot.Id).ToString();
                    data[$"grid.{i}.count"] = slot.Count.ToString();
                }
            }

            if (ArtificerFavoriteBlockIds != null && ArtificerFavoriteBlockIds.Length > 0)
            {
                data["favoriteCount"] = ArtificerFavoriteBlockIds.Length.ToString();
                for (int i = 0; i < ArtificerFavoriteBlockIds.Length; i++)
                    data[$"favorite.{i}.id"] = ArtificerFavoriteBlockIds[i].ToString();
            }
            
            LvcSerializer.Write(path, data);
        }
        catch (Exception ex)
        {
            log.Warn($"Failed to save player state: {ex.Message}");
        }
    }

    public byte[] ToCompressedBytes(Logger? log = null)
    {
        try
        {
            Version = CurrentVersion;
            using var ms = new MemoryStream();
            using (var ds = new DeflateStream(ms, CompressionLevel.Fastest, leaveOpen: true))
            using (var bw = new BinaryWriter(ds))
                WriteBinaryPayload(bw);
            return ms.ToArray();
        }
        catch (Exception ex)
        {
            log?.Warn($"Failed to serialize player state payload: {ex.Message}");
            return Array.Empty<byte>();
        }
    }

    public static bool TryFromCompressedBytes(byte[] payload, Logger? log, out PlayerWorldState state)
    {
        state = new PlayerWorldState();
        if (payload == null || payload.Length == 0)
            return false;

        try
        {
            using var ms = new MemoryStream(payload, writable: false);
            using var ds = new DeflateStream(ms, CompressionMode.Decompress);
            using var br = new BinaryReader(ds);
            return TryReadBinaryPayload(br, log, out state);
        }
        catch (Exception ex)
        {
            log?.Warn($"Failed to deserialize player state payload: {ex.Message}");
            return false;
        }
    }

    private void WriteBinaryPayload(BinaryWriter bw)
    {
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
        bw.Write(Health);
        bw.Write(Hunger);
        bw.Write(SigilAtonement);
        bw.Write(SigilCurse);
        bw.Write(HasSeenAttunementWipPopup);

        var hotbarSlots = Hotbar ?? Array.Empty<HotbarSlot>();
        var hotbarCount = Math.Min(hotbarSlots.Length, Inventory.HotbarSize);
        bw.Write((byte)hotbarCount);
        for (int i = 0; i < hotbarCount; i++)
        {
            bw.Write((byte)hotbarSlots[i].Id);
            bw.Write(hotbarSlots[i].Count);
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

        var gridSlots = InventoryGrid ?? Array.Empty<HotbarSlot>();
        var gridCount = Math.Min(gridSlots.Length, Inventory.GridSize);
        bw.Write((byte)gridCount);
        for (int i = 0; i < gridCount; i++)
        {
            bw.Write((byte)gridSlots[i].Id);
            bw.Write(gridSlots[i].Count);
        }

        var favorites = ArtificerFavoriteBlockIds ?? Array.Empty<int>();
        var favoriteCount = Math.Min(favorites.Length, byte.MaxValue);
        bw.Write((byte)favoriteCount);
        for (int i = 0; i < favoriteCount; i++)
            bw.Write(favorites[i]);
    }

    private static string GetSavePath(string worldPath, string safeName)
    {
        var playerdataDir = Path.Combine(worldPath, "playerdata");
        return Path.Combine(playerdataDir, $"{safeName}{FileConventions.PlayerExtension}");
    }

    private static string SanitizeUsername(string? username)
    {
        if (string.IsNullOrWhiteSpace(username))
            return "player";

        // Keep it filename-safe across platforms
        var name = username.Trim();
        foreach (var c in Path.GetInvalidFileNameChars())
            name = name.Replace(c, '_');
        name = name.Replace(' ', '_');
        return string.IsNullOrWhiteSpace(name) ? "player" : name;
    }

    private static bool TryLoadBinary(string path, Logger log, out PlayerWorldState state)
    {
        state = new PlayerWorldState();
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var ds = new DeflateStream(fs, CompressionMode.Decompress);
            using var br = new BinaryReader(ds);
            return TryReadBinaryPayload(br, log, out state);
        }
        catch (Exception ex)
        {
            log.Warn($"Failed to load player state: {ex.Message}");
            return false;
        }
    }

    private static bool TryReadBinaryPayload(BinaryReader br, Logger? log, out PlayerWorldState state)
    {
        state = new PlayerWorldState();
        try
        {
            var version = br.ReadInt32();
            state.Version = version;
            state.Username = br.ReadString();
            state.PosX = br.ReadSingle();
            state.PosY = br.ReadSingle();
            state.PosZ = br.ReadSingle();
            state.Yaw = br.ReadSingle();
            state.Pitch = br.ReadSingle();
            state.IsFlying = br.ReadBoolean();
            state.CurrentGameMode = (GameMode)br.ReadByte();
            state.HasHome = br.ReadBoolean();
            state.HomeX = br.ReadSingle();
            state.HomeY = br.ReadSingle();
            state.HomeZ = br.ReadSingle();
            state.SelectedIndex = br.ReadInt32();
            if (version >= 7)
            {
                state.Health = br.ReadInt32();
                state.Hunger = br.ReadInt32();
            }

            if (version >= 8)
            {
                state.SigilAtonement = br.ReadSingle();
                state.SigilCurse = br.ReadSingle();
            }

            if (version >= 9)
                state.HasSeenAttunementWipPopup = br.ReadBoolean();

            var hotbarCount = br.ReadByte();
            state.Hotbar = new HotbarSlot[Inventory.HotbarSize];
            for (int i = 0; i < Math.Min((int)hotbarCount, Inventory.HotbarSize); i++)
            {
                state.Hotbar[i] = new HotbarSlot { Id = (BlockId)br.ReadByte(), Count = br.ReadInt32() };
            }

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
                state.Homes.Add(home);
            }

            var gridCount = br.ReadByte();
            state.InventoryGrid = new HotbarSlot[Inventory.GridSize];
            for (int i = 0; i < Math.Min((int)gridCount, Inventory.GridSize); i++)
            {
                state.InventoryGrid[i] = new HotbarSlot { Id = (BlockId)br.ReadByte(), Count = br.ReadInt32() };
            }

            if (version >= 6)
            {
                var favoriteCount = br.ReadByte();
                var favorites = new List<int>(favoriteCount);
                for (int i = 0; i < favoriteCount; i++)
                {
                    var favoriteId = br.ReadInt32();
                    if (favoriteId <= 0 || favoriteId > byte.MaxValue)
                        continue;
                    favorites.Add(favoriteId);
                }

                state.ArtificerFavoriteBlockIds = favorites.ToArray();
            }

            // Future-proofing: if old versions had fewer fields, they should have been handled before.
            return true;
        }
        catch (Exception ex)
        {
            log?.Warn($"Failed to read player state payload: {ex.Message}");
            state = new PlayerWorldState();
            return false;
        }
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
