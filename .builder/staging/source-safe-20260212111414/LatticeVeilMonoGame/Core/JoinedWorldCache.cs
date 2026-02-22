using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using LatticeVeilMonoGame.Online.Lan;

namespace LatticeVeilMonoGame.Core;

public static class JoinedWorldCache
{
    public static string PrepareJoinedWorldPath(LanWorldInfo info, Logger log)
    {
        var root = Path.Combine(Paths.MultiplayerWorldsDir, "Joined");
        Directory.CreateDirectory(root);

        var worldId = ResolveWorldId(info);
        var worldPath = Path.Combine(root, $"world_{worldId}");

        // Joined worlds are mirror-caches. Reset every join to avoid stale chunk reuse.
        if (Directory.Exists(worldPath))
        {
            try
            {
                Directory.Delete(worldPath, recursive: true);
            }
            catch (Exception ex)
            {
                log.Warn($"Failed to reset joined world cache '{worldPath}': {ex.Message}");
                TryCleanDirectoryContents(worldPath, log);
            }
        }

        Directory.CreateDirectory(worldPath);
        return worldPath;
    }

    public static string ResolveWorldId(LanWorldInfo info)
    {
        var raw = (info.WorldId ?? string.Empty).Trim();
        if (!string.IsNullOrWhiteSpace(raw))
            return SanitizeId(raw);

        var fallback =
            $"{info.Seed}|{info.Width}|{info.Height}|{info.Depth}|{(int)info.GameMode}|{(info.PlayerCollision ? 1 : 0)}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(fallback));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static string SanitizeId(string id)
    {
        var builder = new StringBuilder(id.Length);
        foreach (var c in id)
        {
            if (char.IsLetterOrDigit(c))
                builder.Append(char.ToLowerInvariant(c));
            else if (c == '-' || c == '_')
                builder.Append(c);
        }

        if (builder.Length == 0)
            return "world";
        if (builder.Length > 64)
            return builder.ToString(0, 64);
        return builder.ToString();
    }

    private static void TryCleanDirectoryContents(string worldPath, Logger log)
    {
        if (!Directory.Exists(worldPath))
            return;

        try
        {
            foreach (var file in Directory.GetFiles(worldPath))
            {
                try
                {
                    File.SetAttributes(file, FileAttributes.Normal);
                    File.Delete(file);
                }
                catch (Exception ex)
                {
                    log.Warn($"Failed to delete cache file '{file}': {ex.Message}");
                }
            }

            foreach (var dir in Directory.GetDirectories(worldPath))
            {
                try
                {
                    Directory.Delete(dir, recursive: true);
                }
                catch (Exception ex)
                {
                    log.Warn($"Failed to delete cache folder '{dir}': {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            log.Warn($"Failed to clean joined world cache '{worldPath}': {ex.Message}");
        }
    }
}
