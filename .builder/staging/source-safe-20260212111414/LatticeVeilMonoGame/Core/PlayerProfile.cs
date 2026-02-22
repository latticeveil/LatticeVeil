using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace LatticeVeilMonoGame.Core;

public sealed class PlayerProfile
{
    public string PlayerId { get; set; } = "";
    /// <summary>
    /// Online (account) username (legacy). For EOS builds this is usually empty and Epic display name is used instead.
    /// </summary>
    public string Username { get; set; } = "";

    /// <summary>
    /// Optional in-game display name (used in worlds/multiplayer).
    /// </summary>
    public string OfflineUsername { get; set; } = "";

    /// <summary>
    /// Saved EOS friends (Product User IDs) so you can join without retyping/copying IDs every time.
    /// </summary>
    public List<FriendEntry> Friends { get; set; } = new();

    public sealed class FriendEntry
    {
        public string Label { get; set; } = "";
        public string UserId { get; set; } = "";
        public string LastKnownDisplayName { get; set; } = "";
        public string LastKnownPresence { get; set; } = "";
    }

    public string GetDisplayUsername()
    {
        if (!string.IsNullOrWhiteSpace(OfflineUsername))
            return OfflineUsername.Trim();
        if (!string.IsNullOrWhiteSpace(Username))
            return Username.Trim();

        var suffix = string.IsNullOrWhiteSpace(PlayerId) ? "0000" : PlayerId.Substring(0, Math.Min(4, PlayerId.Length)).ToUpperInvariant();
        return $"PLAYER-{suffix}";
    }

    public static PlayerProfile LoadOrCreate(Logger log)
    {
        try
        {
            Directory.CreateDirectory(Paths.RootDir);

            if (!File.Exists(Paths.PlayerProfileJsonPath))
            {
                var p = new PlayerProfile();
                p.EnsureDefaults();
                p.Save(log);
                return p;
            }

            var json = File.ReadAllText(Paths.PlayerProfileJsonPath);
            var profile = JsonSerializer.Deserialize<PlayerProfile>(json) ?? new PlayerProfile();
            profile.EnsureDefaults();
            return profile;
        }
        catch (Exception ex)
        {
            log.Warn($"Failed to load player profile: {ex.Message}");
            return new PlayerProfile();
        }
    }

    public void Save(Logger log)
    {
        try
        {
            Directory.CreateDirectory(Paths.RootDir);
            var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(Paths.PlayerProfileJsonPath, json);
        }
        catch (Exception ex)
        {
            log.Warn($"Failed to save player profile: {ex.Message}");
        }
    }

    private void EnsureDefaults()
    {
        if (string.IsNullOrWhiteSpace(PlayerId) || !Guid.TryParse(PlayerId, out _))
            PlayerId = Guid.NewGuid().ToString("N");
        if (string.IsNullOrWhiteSpace(Username) || string.Equals(Username, "PLAYER", StringComparison.OrdinalIgnoreCase))
            Username = "";
        if (string.Equals(OfflineUsername, "PLAYER", StringComparison.OrdinalIgnoreCase))
            OfflineUsername = "";

        Friends ??= new();

        // Basic cleanup / de-dup by UserId (case-insensitive)
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var cleaned = new List<FriendEntry>();
        foreach (var f in Friends)
        {
            if (f == null) continue;
            var id = (f.UserId ?? "").Trim();
            if (id.Length == 0) continue;
            if (!seen.Add(id)) continue;
            cleaned.Add(new FriendEntry
            {
                UserId = id,
                Label = string.IsNullOrWhiteSpace(f.Label) ? ShortId(id) : f.Label.Trim(),
                LastKnownDisplayName = (f.LastKnownDisplayName ?? "").Trim(),
                LastKnownPresence = (f.LastKnownPresence ?? "").Trim()
            });
        }
        Friends = cleaned;
    }

    public bool AddOrUpdateFriend(string userId, string? label = null)
    {
        userId = (userId ?? "").Trim();
        if (userId.Length == 0) return false;

        Friends ??= new();
        var existing = Friends.Find(f => string.Equals(f.UserId, userId, StringComparison.OrdinalIgnoreCase));
        if (existing != null)
        {
            if (!string.IsNullOrWhiteSpace(label))
                existing.Label = label.Trim();
            return true;
        }

        Friends.Add(new FriendEntry
        {
            UserId = userId,
            Label = string.IsNullOrWhiteSpace(label) ? ShortId(userId) : label.Trim()
        });
        return true;
    }

    public bool RemoveFriend(string userId)
    {
        userId = (userId ?? "").Trim();
        if (userId.Length == 0) return false;
        Friends ??= new();
        return Friends.RemoveAll(f => string.Equals(f.UserId, userId, StringComparison.OrdinalIgnoreCase)) > 0;
    }

	// Exposed for UI display (e.g., friends list / join-by-id screens).
	public static string ShortId(string id)
    {
        id = (id ?? "").Trim();
        if (id.Length <= 12) return id;
        return id.Substring(0, 6) + "..." + id.Substring(id.Length - 6, 6);
    }
}
