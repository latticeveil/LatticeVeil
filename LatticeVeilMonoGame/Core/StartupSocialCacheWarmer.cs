using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LatticeVeilMonoGame.Online.Gate;
using LatticeVeilMonoGame.UI;

namespace LatticeVeilMonoGame.Core;

internal sealed class StartupSocialCacheWarmResult
{
    public List<PlayerProfile.FriendEntry> Friends { get; init; } = new();
    public bool HasFriendUpdates { get; init; }
}

internal static class StartupSocialCacheWarmer
{
    public static Task<StartupSocialCacheWarmResult> WarmAsync(Logger log, PlayerProfile profile, CancellationToken ct = default)
    {
        return Task.Run(async () =>
        {
            var result = new StartupSocialCacheWarmResult();

            await WarmOwnProfileCacheAsync(log, ct).ConfigureAwait(false);
            var refreshedFriends = await WarmFriendsCacheAsync(log, profile, ct).ConfigureAwait(false);
            if (refreshedFriends.Count > 0)
            {
                result = new StartupSocialCacheWarmResult
                {
                    Friends = refreshedFriends,
                    HasFriendUpdates = true
                };
            }

            return result;
        }, ct);
    }

    private static async Task WarmOwnProfileCacheAsync(Logger log, CancellationToken ct)
    {
        var token = ResolveVeilnetToken();
        if (!IsUsableToken(token))
            return;

        try
        {
            var client = new VeilnetProfileClient(log);
            using var loader = new MemoryWebTextureLoader();
            var profileResult = await client.GetProfileAsync(token, ct).ConfigureAwait(false);
            if (!profileResult.Ok || profileResult.Profile == null)
                return;

            var profile = profileResult.Profile;
            var cache = new VeilnetProfileCacheStore
            {
                Username = (profile.Username ?? string.Empty).Trim(),
                AboutMe = NormalizeMultiline(profile.AboutMe),
                PictureUrl = (profile.PictureUrl ?? string.Empty).Trim(),
                BannerUrl = (profile.BannerUrl ?? string.Empty).Trim(),
                ThemeColor = (profile.ThemeColor ?? string.Empty).Trim(),
                Theme = (profile.Theme ?? string.Empty).Trim(),
                UpdatedAt = (profile.UpdatedAtRaw ?? string.Empty).Trim()
            };
            cache.Save(log);

            if (!string.IsNullOrWhiteSpace(cache.PictureUrl))
            {
                var avatarBytes = await loader.DownloadImageBytesAsync(cache.PictureUrl, ct).ConfigureAwait(false);
                if (avatarBytes is { Length: > 0 })
                    SaveBinaryCache(log, Paths.VeilnetAvatarCachePath, avatarBytes);
            }

            if (!string.IsNullOrWhiteSpace(cache.BannerUrl))
            {
                var bannerBytes = await loader.DownloadImageBytesAsync(cache.BannerUrl, ct).ConfigureAwait(false);
                if (bannerBytes is { Length: > 0 })
                    SaveBinaryCache(log, Paths.VeilnetBannerCachePath, bannerBytes);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            log.Warn($"Startup profile cache warm failed: {ex.Message}");
        }
    }

    private static async Task<List<PlayerProfile.FriendEntry>> WarmFriendsCacheAsync(Logger log, PlayerProfile profile, CancellationToken ct)
    {
        var gate = OnlineGateClient.GetOrCreate();
        if (!gate.CanUseOfficialOnline(log, out _))
            return new List<PlayerProfile.FriendEntry>();

        try
        {
            ct.ThrowIfCancellationRequested();
            var serverFriends = await gate.GetFriendsAsync().ConfigureAwait(false);
            if (!serverFriends.Ok)
                return new List<PlayerProfile.FriendEntry>();

            if (serverFriends.Friends.Count == 0 && profile.Friends.Count > 0)
                return new List<PlayerProfile.FriendEntry>();

            var existingPresence = profile.Friends
                .Where(f => !string.IsNullOrWhiteSpace(f.UserId))
                .ToDictionary(
                    f => f.UserId,
                    f => f.LastKnownPresence ?? string.Empty,
                    StringComparer.OrdinalIgnoreCase);

            var refreshed = new List<PlayerProfile.FriendEntry>();
            foreach (var user in serverFriends.Friends)
            {
                var id = (user.ProductUserId ?? string.Empty).Trim();
                if (string.IsNullOrWhiteSpace(id))
                    continue;

                var label = string.IsNullOrWhiteSpace(user.DisplayName) ? user.Username : user.DisplayName;
                refreshed.Add(new PlayerProfile.FriendEntry
                {
                    UserId = id,
                    Label = (label ?? string.Empty).Trim(),
                    LastKnownDisplayName = (label ?? string.Empty).Trim(),
                    LastKnownPresence = existingPresence.TryGetValue(id, out var presence) ? presence : string.Empty
                });
            }

            return refreshed;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            log.Warn($"Startup friends cache warm failed: {ex.Message}");
            return new List<PlayerProfile.FriendEntry>();
        }
    }

    private static void SaveBinaryCache(Logger log, string path, byte[] bytes)
    {
        try
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(dir))
                Directory.CreateDirectory(dir);
            File.WriteAllBytes(path, bytes);
        }
        catch (Exception ex)
        {
            log.Warn($"Failed to save startup cache '{Path.GetFileName(path)}': {ex.Message}");
        }
    }

    private static string NormalizeMultiline(string text)
    {
        return (text ?? string.Empty).Replace("\r\n", "\n").Trim();
    }

    private static string ResolveVeilnetToken()
    {
        return (Environment.GetEnvironmentVariable("LV_VEILNET_ACCESS_TOKEN") ?? string.Empty).Trim();
    }

    private static bool IsUsableToken(string token)
    {
        if (string.IsNullOrWhiteSpace(token))
            return false;

        return !string.Equals(token, "null", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(token, "undefined", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(token, "placeholder", StringComparison.OrdinalIgnoreCase);
    }
}
