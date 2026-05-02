using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

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
    /// Saved Veilnet friends cache (user IDs + usernames). Updated on launcher auth sync.
    /// </summary>
    public List<FriendEntry> Friends { get; set; } = new();
    /// <summary>
    /// Incoming friend requests (Product User IDs).
    /// </summary>
    public List<string> ReceivedRequests { get; set; } = new();
    /// <summary>
    /// Outgoing friend requests (Product User IDs).
    /// </summary>
    public List<string> SentRequests { get; set; } = new();

    public sealed class FriendEntry
    {
        public string Label { get; set; } = "";
        public string UserId { get; set; } = "";
        public string LastKnownDisplayName { get; set; } = "";
        public string LastKnownPresence { get; set; } = "";
        public string PictureUrl { get; set; } = "";
        public string BannerUrl { get; set; } = "";
        public string AboutMe { get; set; } = "";
    }

    public string GetDisplayUsername()
    {
        var veilnetName = (Environment.GetEnvironmentVariable("LV_VEILNET_USERNAME") ?? string.Empty).Trim();
        if (!string.IsNullOrWhiteSpace(veilnetName))
            return veilnetName;

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
                if (File.Exists(Paths.LegacyPlayerProfileJsonPath))
                    TryMigrateLegacyProfileFile(log);
            }

            if (!File.Exists(Paths.PlayerProfileJsonPath))
            {
                var p = new PlayerProfile();
                p.EnsureDefaults();
                p.Save(log);
                return p;
            }

            var data = LvcSerializer.Read(Paths.PlayerProfileJsonPath);
            var profile = new PlayerProfile();
            LvcSerializer.ApplyObject(profile, data);
            profile.ApplyFriends(data);
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
            var data = LvcSerializer.SerializeObject(this);
            // Friends list is serialized manually (non-trivial)
            this.SerializeFriends(data);
            LvcSerializer.Write(Paths.PlayerProfileJsonPath, data);
        }
        catch (Exception ex)
        {
            log.Warn($"Failed to save player profile: {ex.Message}");
        }
    }

    private void SerializeFriends(Dictionary<string, string> data)
    {
        Friends ??= new List<FriendEntry>();
        data["FriendsCount"] = Friends.Count.ToString(System.Globalization.CultureInfo.InvariantCulture);
        for (var i = 0; i < Friends.Count; i++)
        {
            var f = Friends[i];
            if (f == null) continue;
            data[$"Friend.{i}.UserId"] = f.UserId ?? "";
            data[$"Friend.{i}.Label"] = f.Label ?? "";
            data[$"Friend.{i}.LastKnownDisplayName"] = f.LastKnownDisplayName ?? "";
            data[$"Friend.{i}.LastKnownPresence"] = f.LastKnownPresence ?? "";
            data[$"Friend.{i}.PictureUrl"] = f.PictureUrl ?? "";
            data[$"Friend.{i}.BannerUrl"] = f.BannerUrl ?? "";
            data[$"Friend.{i}.AboutMe"] = f.AboutMe ?? "";
        }
    }

    private void ApplyFriends(Dictionary<string, string> data)
    {
        Friends ??= new List<FriendEntry>();
        Friends.Clear();

        if (!data.TryGetValue("FriendsCount", out var raw) || !int.TryParse(raw, out var count) || count < 0 || count > 5000)
            count = 0;

        for (var i = 0; i < count; i++)
        {
            data.TryGetValue($"Friend.{i}.UserId", out var userId);
            if (string.IsNullOrWhiteSpace(userId)) continue;

            data.TryGetValue($"Friend.{i}.Label", out var label);
            data.TryGetValue($"Friend.{i}.LastKnownDisplayName", out var display);
            data.TryGetValue($"Friend.{i}.LastKnownPresence", out var presence);
            data.TryGetValue($"Friend.{i}.PictureUrl", out var pictureUrl);
            data.TryGetValue($"Friend.{i}.BannerUrl", out var bannerUrl);
            data.TryGetValue($"Friend.{i}.AboutMe", out var aboutMe);

            Friends.Add(new FriendEntry
            {
                UserId = (userId ?? "").Trim(),
                Label = (label ?? "").Trim(),
                LastKnownDisplayName = (display ?? "").Trim(),
                LastKnownPresence = (presence ?? "").Trim(),
                PictureUrl = (pictureUrl ?? "").Trim(),
                BannerUrl = (bannerUrl ?? "").Trim(),
                AboutMe = (aboutMe ?? "").Trim()
            });
        }
    }


    private static void TryMigrateLegacyProfileFile(Logger log)
    {
        throw new LvcSerializer.LegacyFormatException("Legacy player profile format detected (migration disabled). ");
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
            var normalizedLabel = NormalizeFriendLabel((f.Label ?? "").Trim(), (f.LastKnownDisplayName ?? "").Trim());
            cleaned.Add(new FriendEntry
            {
                UserId = id,
                Label = normalizedLabel,
                LastKnownDisplayName = (f.LastKnownDisplayName ?? "").Trim(),
                LastKnownPresence = (f.LastKnownPresence ?? "").Trim(),
                PictureUrl = (f.PictureUrl ?? "").Trim(),
                BannerUrl = (f.BannerUrl ?? "").Trim(),
                AboutMe = (f.AboutMe ?? "").Trim()
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
            Label = NormalizeFriendLabel((label ?? "").Trim(), string.Empty)
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

    private static string NormalizeFriendLabel(string label, string fallbackDisplayName)
    {
        if (!LooksLikeIdentityToken(label))
            return label;

        if (!LooksLikeIdentityToken(fallbackDisplayName))
            return fallbackDisplayName;

        return string.Empty;
    }

    private static bool LooksLikeIdentityToken(string value)
    {
        var text = (value ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(text))
            return true;

        if (Guid.TryParse(text, out _))
            return true;

        var compact = text.Replace("-", string.Empty);
        if (compact.Length >= 16 && compact.All(Uri.IsHexDigit))
            return true;

        if (text.Contains("...", StringComparison.Ordinal)
            || text.StartsWith("PLAYER-", StringComparison.OrdinalIgnoreCase)
            || string.Equals(text, "UNKNOWN", StringComparison.OrdinalIgnoreCase))
            return true;

        return false;
    }
}
