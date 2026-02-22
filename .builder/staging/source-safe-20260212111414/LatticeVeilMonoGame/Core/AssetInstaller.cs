using System;
using System.IO;

namespace LatticeVeilMonoGame.Core;

public static class AssetInstaller
{
    /// <summary>
    /// Copies Defaults/Assets into Documents\LatticeVeil\Assets if files are missing.
    /// Defaults are install-source only; runtime always reads from Documents\LatticeVeil\Assets.
    /// </summary>
    public static void EnsureDefaultsInstalled(string defaultsRoot, Logger log)
    {
        try
        {
            Directory.CreateDirectory(Paths.AssetsDir);

            var defaultsCandidates = new[]
            {
                Path.Combine(defaultsRoot, "Defaults", "Assets"),
                Path.Combine(defaultsRoot, "Assets")
            };

            string? defaultsAssets = null;
            for (var i = 0; i < defaultsCandidates.Length; i++)
            {
                if (Directory.Exists(defaultsCandidates[i]))
                {
                    defaultsAssets = defaultsCandidates[i];
                    break;
                }
            }

            if (defaultsAssets is null)
            {
                log.Warn($"Defaults assets folder missing: {string.Join(" | ", defaultsCandidates)}");
                return;
            }

            foreach (var srcPath in Directory.GetFiles(defaultsAssets, "*", SearchOption.AllDirectories))
            {
                var rel = Path.GetRelativePath(defaultsAssets, srcPath);
                var dst = Path.Combine(Paths.AssetsDir, rel);

                Directory.CreateDirectory(Path.GetDirectoryName(dst)!);

                if (!File.Exists(dst))
                {
                    File.Copy(srcPath, dst);
                    log.Info($"Installed default asset: {rel}");
                }
            }
        }
        catch (Exception ex)
        {
            log.Error($"EnsureDefaultsInstalled failed: {ex.Message}");
        }
    }
}
