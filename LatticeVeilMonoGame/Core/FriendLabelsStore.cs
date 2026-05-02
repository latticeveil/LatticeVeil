using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace LatticeVeilMonoGame.Core;

/// <summary>
/// Client-side only friend labels.
/// 
/// - "Pinned" = shown in a separate "LatticeVeil" section.
/// - "Nicknames" = optional *local alias* for how you see a friend (never shared).
/// 
/// This intentionally does NOT attempt to be globally unique or authoritative.
/// </summary>
public sealed class FriendLabelsStore
{
    public Dictionary<string, string> Nicknames { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public List<string> Pinned { get; set; } = new();

    public static FriendLabelsStore LoadOrCreate(Logger log)
    {
        try
        {
            Directory.CreateDirectory(Paths.RootDir);
            if (!File.Exists(Paths.FriendLabelsJsonPath))
            {
                if (File.Exists(Paths.LegacyFriendLabelsJsonPath))
                    TryMigrateLegacyFile(log);
            }
            if (File.Exists(Paths.FriendLabelsJsonPath) && LvcSerializer.IsJsonFormat(Paths.FriendLabelsJsonPath))
                throw new LvcSerializer.LegacyFormatException($"Legacy JSON friend_labels detected: {Paths.ToUiPath(Paths.FriendLabelsJsonPath)}");

            if (!File.Exists(Paths.FriendLabelsJsonPath))
            {
                var created = new FriendLabelsStore();
                created.Save(log);
                return created;
            }

            var data = LvcSerializer.Read(Paths.FriendLabelsJsonPath);
            var store = new FriendLabelsStore();
            LvcSerializer.ApplyObject(store, data);
// Normalize
            store.Nicknames ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            store.Pinned ??= new List<string>();
            store.Pinned = store.Pinned
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Select(x => x.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            var keys = store.Nicknames.Keys.ToList();
            foreach (var k in keys)
            {
                var v = store.Nicknames[k]?.Trim() ?? "";
                if (string.IsNullOrWhiteSpace(v))
                    store.Nicknames.Remove(k);
                else
                    store.Nicknames[k] = v;
            }

            return store;
        }
        catch (Exception ex)
        {
            log.Warn($"Failed to load friend labels: {ex.Message}");
            return new FriendLabelsStore();
        }
    }

    public void Save(Logger log)
    {
        try
        {
            Directory.CreateDirectory(Paths.RootDir);
            var data = LvcSerializer.SerializeObject(this);
            LvcSerializer.Write(Paths.FriendLabelsJsonPath, data);
}
        catch (Exception ex)
        {
            log.Warn($"Failed to save friend labels: {ex.Message}");
        }
    }

    private static void TryMigrateLegacyFile(Logger log)
    {
        throw new LvcSerializer.LegacyFormatException("Legacy friend_labels format detected (migration disabled).");
    }

    public bool IsPinned(string friendKey)
    {
        friendKey = (friendKey ?? "").Trim();
        if (string.IsNullOrWhiteSpace(friendKey))
            return false;
        return Pinned.Any(x => string.Equals(x, friendKey, StringComparison.OrdinalIgnoreCase));
    }

    public void TogglePinned(Logger log, string friendKey)
    {
        friendKey = (friendKey ?? "").Trim();
        if (string.IsNullOrWhiteSpace(friendKey))
            return;

        var idx = Pinned.FindIndex(x => string.Equals(x, friendKey, StringComparison.OrdinalIgnoreCase));
        if (idx >= 0)
            Pinned.RemoveAt(idx);
        else
            Pinned.Add(friendKey);

        Save(log);
    }

    public string? GetNickname(string friendKey)
    {
        friendKey = (friendKey ?? "").Trim();
        if (string.IsNullOrWhiteSpace(friendKey))
            return null;

        return Nicknames.TryGetValue(friendKey, out var n) && !string.IsNullOrWhiteSpace(n)
            ? n.Trim()
            : null;
    }

    public void SetNickname(Logger log, string friendKey, string? nickname)
    {
        friendKey = (friendKey ?? "").Trim();
        if (string.IsNullOrWhiteSpace(friendKey))
            return;

        nickname = (nickname ?? "").Trim();
        if (string.IsNullOrWhiteSpace(nickname))
            Nicknames.Remove(friendKey);
        else
            Nicknames[friendKey] = nickname;

        Save(log);
    }
}
