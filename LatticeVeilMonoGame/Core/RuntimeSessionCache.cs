using System;
using System.IO;

namespace LatticeVeilMonoGame.Core;

internal static class RuntimeSessionCache
{
    public static void ResetSocialSession(Logger log)
    {
        ClearSocialSession(log, recreateDirectory: true);
        DeleteLegacyDocumentsCaches(log);
    }

    public static void DeleteLegacyDocumentsCachesOnly(Logger log)
    {
        DeleteLegacyDocumentsCaches(log);
    }

    public static void ClearSocialSession(Logger log, bool recreateDirectory = false)
    {
        try
        {
            if (Directory.Exists(Paths.SocialSessionCacheDir))
                Directory.Delete(Paths.SocialSessionCacheDir, recursive: true);

            if (recreateDirectory)
                Directory.CreateDirectory(Paths.SocialSessionCacheDir);
        }
        catch (Exception ex)
        {
            log.Warn($"Failed to clear social session cache: {ex.Message}");
        }
    }

    private static void DeleteLegacyDocumentsCaches(Logger log)
    {
        DeleteLegacyFile(log, Path.Combine(Paths.RootDir, "veilnet_profile_cache.lvc"));
        DeleteLegacyFile(log, Path.Combine(Paths.RootDir, "veilnet_avatar_cache.bin"));
        DeleteLegacyFile(log, Path.Combine(Paths.RootDir, "veilnet_banner_cache.bin"));
    }

    private static void DeleteLegacyFile(Logger log, string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (Exception ex)
        {
            log.Warn($"Failed to delete legacy cache '{Path.GetFileName(path)}': {ex.Message}");
        }
    }
}
