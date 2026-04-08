using System;
using System.IO;

namespace LatticeVeilMonoGame.Core;

public sealed class VeilnetProfileCacheStore
{
    public string Username { get; set; } = string.Empty;
    public string AboutMe { get; set; } = string.Empty;
    public string PictureUrl { get; set; } = string.Empty;
    public string BannerUrl { get; set; } = string.Empty;
    public string ThemeColor { get; set; } = string.Empty;
    public string Theme { get; set; } = string.Empty;
    public string UpdatedAt { get; set; } = string.Empty;
    public string CachedAtUtc { get; set; } = string.Empty;

    public static VeilnetProfileCacheStore? Load(Logger log)
    {
        try
        {
            if (!File.Exists(Paths.VeilnetProfileCachePath))
                return null;

            var data = LvcSerializer.Read(Paths.VeilnetProfileCachePath);
            var cache = new VeilnetProfileCacheStore();
            LvcSerializer.ApplyObject(cache, data);
            return cache;
        }
        catch (Exception ex)
        {
            log.Warn($"Failed to load Veilnet profile cache: {ex.Message}");
            return null;
        }
    }

    public void Save(Logger log)
    {
        try
        {
            Directory.CreateDirectory(Paths.RootDir);
            CachedAtUtc = DateTimeOffset.UtcNow.ToString("O");
            var data = LvcSerializer.SerializeObject(this);
            LvcSerializer.Write(Paths.VeilnetProfileCachePath, data);
        }
        catch (Exception ex)
        {
            log.Warn($"Failed to save Veilnet profile cache: {ex.Message}");
        }
    }
}
