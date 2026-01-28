using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

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
            Directory.CreateDirectory(Paths.ConfigDir);
            if (!File.Exists(Paths.FriendLabelsJsonPath))
            {
                var created = new FriendLabelsStore();
                created.Save(log);
                return created;
            }

            var json = File.ReadAllText(Paths.FriendLabelsJsonPath);
            var store = JsonSerializer.Deserialize<FriendLabelsStore>(json) ?? new FriendLabelsStore();

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
            Directory.CreateDirectory(Paths.ConfigDir);
            var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(Paths.FriendLabelsJsonPath, json);
        }
        catch (Exception ex)
        {
            log.Warn($"Failed to save friend labels: {ex.Message}");
        }
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
