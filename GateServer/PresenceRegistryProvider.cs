using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

sealed class PresenceRegistryProvider
{
    private const string FriendCodePrefix = "RC-";
    private const string FriendCodeSalt = "RC-FRIENDCODE-V1";
    private const string FriendCodeAlphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
    private const int FriendCodeLength = 8;
    private const int DefaultTtlSeconds = 90;

    private readonly ConcurrentDictionary<string, PresenceEntry> _entries = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, WorldInviteEntry> _invites = new(StringComparer.OrdinalIgnoreCase);

    public PresenceRegistryProvider()
    {
        TtlSeconds = ParsePositiveInt(Environment.GetEnvironmentVariable("PRESENCE_TTL_SECONDS"), DefaultTtlSeconds);
    }

    public int TtlSeconds { get; }

    public PresenceEntry Upsert(PresenceUpsertInput input)
    {
        CleanupExpired();

        var productUserId = NormalizeRequired(input.ProductUserId);
        if (string.IsNullOrWhiteSpace(productUserId)) return null!;

        // If not hosting and status is just "Online", we can treat this as a sign-off from the list if we want it instant.
        if (!input.IsHosting)
        {
            _entries.TryRemove(productUserId, out _);
            
            // Clear any outgoing invites FROM this user (they are no longer hosting)
            var outgoingKeys = _invites.Keys.Where(k => k.StartsWith(productUserId + ":")).ToList();
            foreach (var k in outgoingKeys) _invites.TryRemove(k, out _);

            // Clear any incoming invites TO this user (since they aren't hosting, nobody can join them)
            var incomingKeys = _invites.Keys.Where(k => k.EndsWith(":" + productUserId)).ToList();
            foreach (var k in incomingKeys) _invites.TryRemove(k, out _);
            
            return null!;
        }

        var now = DateTime.UtcNow;
        var expiresUtc = now.AddSeconds(TtlSeconds);
        var displayName = NormalizeDisplayName(input.DisplayName);
        if (string.IsNullOrWhiteSpace(displayName))
            displayName = string.IsNullOrWhiteSpace(input.Username) ? "Player" : input.Username.Trim();

        var entry = new PresenceEntry(
            productUserId,
            displayName,
            (input.Username ?? string.Empty).Trim().ToLowerInvariant(),
            string.IsNullOrWhiteSpace(input.Status) ? "online" : input.Status.Trim(),
            input.IsHosting,
            (input.WorldName ?? string.Empty).Trim(),
            (input.GameMode ?? string.Empty).Trim(),
            string.IsNullOrWhiteSpace(input.JoinTarget) ? productUserId : input.JoinTarget.Trim(),
            GenerateFriendCode(productUserId),
            now,
            expiresUtc);

        _entries[productUserId] = entry;
        return entry;
    }

    public List<PresenceEntry> Query(IEnumerable<string> productUserIds)
    {
        CleanupExpired();
        var result = new List<PresenceEntry>();
        if (productUserIds == null)
            return result;

        foreach (var raw in productUserIds)
        {
            var key = (raw ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(key))
                continue;

            if (_entries.TryGetValue(key, out var entry))
                result.Add(entry);
        }

        return result;
    }

    public WorldInviteEntry SendInvite(string senderPuid, string senderName, string targetPuid, string worldName)
    {
        CleanupExpired();
        
        // Reset invite by removing old one first
        var key = $"{senderPuid}:{targetPuid}";
        _invites.TryRemove(key, out _);

        var invite = new WorldInviteEntry(
            senderPuid,
            senderName,
            targetPuid,
            worldName,
            "pending",
            DateTime.UtcNow,
            DateTime.UtcNow.AddMinutes(5));
        
        _invites[key] = invite;
        return invite;
    }

    public List<WorldInviteEntry> GetInvitesFor(string puid)
    {
        CleanupExpired();
        return _invites.Values.Where(i => string.Equals(i.TargetProductUserId, puid, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    public List<WorldInviteEntry> GetInvitesFrom(string puid)
    {
        CleanupExpired();
        return _invites.Values.Where(i => string.Equals(i.SenderProductUserId, puid, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    public bool RespondToInvite(string responderPuid, string senderPuid, string response)
    {
        var key = $"{senderPuid}:{responderPuid}";
        if (_invites.TryGetValue(key, out var invite))
        {
            _invites[key] = invite with { Status = response.ToLowerInvariant() };
            return true;
        }
        return false;
    }

    private void CleanupExpired()
    {
        var now = DateTime.UtcNow;
        foreach (var pair in _entries)
        {
            if (pair.Value.ExpiresUtc <= now)
                _entries.TryRemove(pair.Key, out _);
        }

        foreach (var pair in _invites)
        {
            if (pair.Value.ExpiresUtc <= now)
                _invites.TryRemove(pair.Key, out _);
        }
    }

    private static string NormalizeRequired(string? value)
    {
        return (value ?? string.Empty).Trim();
    }

    private static string NormalizeDisplayName(string? value)
    {
        var normalized = (value ?? string.Empty).Trim().Replace("\r", string.Empty).Replace("\n", string.Empty);
        if (normalized.Length > 32)
            normalized = normalized[..32];
        return normalized;
    }

    private static int ParsePositiveInt(string? value, int fallback)
    {
        if (!int.TryParse(value, out var parsed))
            return fallback;
        return parsed > 0 ? parsed : fallback;
    }

    private static string GenerateFriendCode(string productUserId)
    {
        if (string.IsNullOrWhiteSpace(productUserId))
            return string.Empty;

        var bytes = Encoding.UTF8.GetBytes($"{productUserId.Trim()}:{FriendCodeSalt}");
        var hash = SHA256.HashData(bytes);
        var chars = new char[FriendCodeLength];
        for (var i = 0; i < chars.Length; i++)
            chars[i] = FriendCodeAlphabet[hash[i] % FriendCodeAlphabet.Length];
        return FriendCodePrefix + new string(chars);
    }
}

sealed record PresenceUpsertInput(
    string ProductUserId,
    string DisplayName,
    string Username,
    string Status,
    bool IsHosting,
    string WorldName,
    string GameMode,
    string JoinTarget);

sealed record PresenceEntry(
    string ProductUserId,
    string DisplayName,
    string Username,
    string Status,
    bool IsHosting,
    string WorldName,
    string GameMode,
    string JoinTarget,
    string FriendCode,
    DateTime UpdatedUtc,
    DateTime ExpiresUtc);

sealed record WorldInviteEntry(
    string SenderProductUserId,
    string SenderDisplayName,
    string TargetProductUserId,
    string WorldName,
    string Status, // pending, accepted, rejected
    DateTime CreatedUtc,
    DateTime ExpiresUtc);

sealed record WorldInviteSendInput(
    string TargetProductUserId,
    string WorldName);

sealed record WorldInviteResponseInput(
    string SenderProductUserId,
    string Response);
