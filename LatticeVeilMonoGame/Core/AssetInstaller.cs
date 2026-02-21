using System;
using System.IO;

namespace LatticeVeilMonoGame.Core;

public static class AssetInstaller
{
    /// <summary>
    /// Copies Defaults/Assets into Documents\LatticeVeil\Assets if files are missing.
    /// For dev builds: checks Documents\LatticeVeil_project\LatticeVeilMonoGame\Defaults\Assets first,
    /// then falls back to regular assets download location.
    /// If dev assets are missing, replaces them with fallback assets.
    /// </summary>
    public static void EnsureDefaultsInstalled(string defaultsRoot, Logger log)
    {
        try
        {
            Directory.CreateDirectory(Paths.AssetsDir);

            string? defaultsAssets = null;
            
            if (Paths.IsDevBuild)
            {
                // For dev builds, check the new dev assets location first
                var devAssetsPath = Paths.LocalAssetsDir;
                if (Directory.Exists(devAssetsPath))
                {
                    defaultsAssets = devAssetsPath;
                    log.Info($"Using dev assets from: {defaultsAssets}");
                    
                    // Check if we need to replace missing files from fallback
                    EnsureDevAssetsComplete(defaultsAssets, log);
                }
                else
                {
                    log.Warn($"Dev assets folder missing: {devAssetsPath}");
                }
            }

            // If no dev assets found or not dev build, use regular defaults
            if (defaultsAssets == null)
            {
                var defaultsCandidates = new[]
                {
                    Path.Combine(defaultsRoot, "Defaults", "Assets"),
                    Path.Combine(defaultsRoot, "Assets")
                };

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
                
                log.Info($"Using fallback assets from: {defaultsAssets}");
            }

            foreach (var srcPath in Directory.GetFiles(defaultsAssets, "*", SearchOption.AllDirectories))
            {
                var rel = Path.GetRelativePath(defaultsAssets, srcPath);
                if (Paths.IsDisallowedAssetRelativePath(rel))
                    continue;
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

    /// <summary>
    /// Ensures dev assets are complete by copying missing files from fallback location.
    /// </summary>
    private static void EnsureDevAssetsComplete(string devAssetsPath, Logger log)
    {
        try
        {
            // Look for fallback assets in the regular defaults locations
            var fallbackCandidates = new[]
            {
                Path.Combine(Path.GetDirectoryName(Paths.LocalAssetsDir)!, "..", "..", "..", "Defaults", "Assets"),
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Defaults", "Assets"),
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Assets")
            };

            string? fallbackAssets = null;
            foreach (var candidate in fallbackCandidates)
            {
                if (Directory.Exists(candidate))
                {
                    fallbackAssets = candidate;
                    break;
                }
            }

            if (fallbackAssets == null)
            {
                log.Warn("No fallback assets found to complete dev assets");
                return;
            }

            // Copy missing files from fallback to dev assets
            var missingFiles = 0;
            foreach (var srcPath in Directory.GetFiles(fallbackAssets, "*", SearchOption.AllDirectories))
            {
                var rel = Path.GetRelativePath(fallbackAssets, srcPath);
                if (Paths.IsDisallowedAssetRelativePath(rel))
                    continue;
                var dst = Path.Combine(devAssetsPath, rel);

                if (!File.Exists(dst))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
                    File.Copy(srcPath, dst);
                    missingFiles++;
                    log.Info($"Added missing dev asset: {rel}");
                }
            }

            if (missingFiles > 0)
            {
                log.Info($"Completed dev assets with {missingFiles} missing files from fallback");
            }
        }
        catch (Exception ex)
        {
            log.Warn($"Failed to complete dev assets: {ex.Message}");
        }
    }
}
