using System;
using System.IO;
using System.Linq;

namespace LatticeVeilMonoGame.Core;

public static class Paths
{
    public const string ConfigExtension = ".lvc";
    public const string ListExtension = ".lvlist";
    public const string LogExtension = ".lvlog";
    public const string WorldMetaFileName = "world.lvc";
    public const string LegacyWorldMetaFileName = "world.json";
    public const string WorldConfigFileName = "world_config.lvc";
    public const string LegacyWorldConfigFileName = "world_config.json";

    public static bool IsDevBuild
    {
#if DEBUG
        get { return true; }
#else
        get { return false; }
#endif
    }

    public static string DocumentsDir =>
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);

    public static string RootDir
    {
        get
        {
            var overrideDir = Environment.GetEnvironmentVariable("LATTICEVEIL_ROOT");
            if (!string.IsNullOrWhiteSpace(overrideDir))
                return overrideDir.Trim();

            return Path.Combine(DocumentsDir, "LatticeVeil");
        }
    }

    public static string AssetsDir =>
        Path.Combine(RootDir, "Assets");

    /// <summary>
    /// Local development assets directory for dev builds.
    /// </summary>
    public static string LocalAssetsDir =>
        Path.Combine(DocumentsDir, "LatticeVeil_project", "LatticeVeilMonoGame", "Defaults", "Assets");

    /// <summary>
    /// Returns the appropriate assets directory based on environment.
    /// Dev builds prefer local Defaults assets when present, otherwise fall back
    /// to Documents assets so packaged/dev test EXEs still run on other machines.
    /// </summary>
    public static string GetAssetsDir()
    {
        if (IsDevBuild && HasUsableLocalDefaultsAssets())
            return LocalAssetsDir;
        return AssetsDir;
    }

    private static bool HasUsableLocalDefaultsAssets()
    {
        try
        {
            if (!Directory.Exists(LocalAssetsDir))
                return false;

            var texturesDir = Path.Combine(LocalAssetsDir, "textures");
            var menuDir = Path.Combine(texturesDir, "menu");
            var blocksDir = Path.Combine(texturesDir, "blocks");
            return Directory.Exists(texturesDir)
                || (Directory.Exists(menuDir) && Directory.Exists(blocksDir));
        }
        catch
        {
            return false;
        }
    }

    public static string TexturesDir =>
        Path.Combine(GetAssetsDir(), "textures");

    public static string MenuTexturesDir =>
        Path.Combine(GetAssetsDir(), "textures", "menu");

    public static string BlocksTexturesDir =>
        Path.Combine(GetAssetsDir(), "textures", "blocks");

    public static string LowQualityBlocksTexturesDir =>
        Path.Combine(GetAssetsDir(), "textures", "blocks_low");

    public static string BlocksAtlasPath =>
        Path.Combine(TexturesDir, "blocks_cubenet_atlas.png");

    public static string LowQualityBlocksAtlasPath =>
        Path.Combine(TexturesDir, "blocks_cubenet_atlas_low.png");

    public static string LogsDir =>
        Path.Combine(RootDir, "logs");

    public static string ScreenshotsDir =>
        Path.Combine(RootDir, "Screenshots");

    public static string WorldsDir =>
        Path.Combine(RootDir, "Worlds");

    public static string MultiplayerWorldsDir =>
        Path.Combine(RootDir, "_OnlineCache");

    public static string BackupsDir =>
        Path.Combine(RootDir, "Backups");

    public static string ActiveLogPath =>
        Path.Combine(LogsDir, "current.lvlog");

    public static string GamePidPath =>
        Path.Combine(RootDir, "game.pid");

    public static string SettingsJsonPath =>
#if PRIVATE_CLIENT
        Path.Combine(RootDir, "options.private.lvc");
#else
        Path.Combine(RootDir, "options.lvc");
#endif

    public static string LegacySettingsLvcPath =>
        Path.Combine(RootDir, "settings.lvc");

    public static string LegacySettingsJsonPath =>
        Path.Combine(RootDir, "settings.json");

    public static string PlayerProfileJsonPath =>
        Path.Combine(RootDir, "player_profile.lvc");

    public static string LegacyPlayerProfileJsonPath =>
        Path.Combine(RootDir, "player_profile.json");

    public static string ConfigDir =>
        RootDir;

    /// <summary>
    /// Local friend labels (nicknames + pinned list). This is client-side only.
    /// </summary>
    public static string FriendLabelsJsonPath =>
        Path.Combine(RootDir, "friend_labels.lvc");

    public static string LegacyFriendLabelsJsonPath =>
        Path.Combine(RootDir, "friend_labels.json");

    public static string GetWorldMetaPath(string worldPath) =>
        Path.Combine(worldPath, WorldMetaFileName);

    public static string GetLegacyWorldMetaPath(string worldPath) =>
        Path.Combine(worldPath, LegacyWorldMetaFileName);

    public static string ResolveWorldMetaPath(string worldPath)
    {
        var preferred = GetWorldMetaPath(worldPath);
        if (File.Exists(preferred))
            return preferred;

        var legacy = GetLegacyWorldMetaPath(worldPath);
        return File.Exists(legacy) ? legacy : preferred;
    }




    public static void EnsureAssetDirectoriesExist(Logger log)
    {
        try
        {
            Directory.CreateDirectory(AssetsDir);
            Directory.CreateDirectory(TexturesDir);
            Directory.CreateDirectory(MenuTexturesDir);
            Directory.CreateDirectory(BlocksTexturesDir);
            Directory.CreateDirectory(Path.Combine(AssetsDir, "Models", "Blocks"));

            WarnIfLegacyAssetFoldersExist(log);
        }
        catch (Exception ex)
        {
            log.Warn($"Failed to create asset directories: {ex.Message}");
        }
    }

    private static void WarnIfLegacyAssetFoldersExist(Logger log)
    {
        var legacyPaths = new[]
        {
            Path.Combine(AssetsDir, "menu"),
            Path.Combine(AssetsDir, "Menu"),
            Path.Combine(AssetsDir, "blocks"),
            Path.Combine(AssetsDir, "Blocks"),
            Path.Combine(AssetsDir, "textures", "Menu"),
            Path.Combine(AssetsDir, "textures", "Blocks")
        };

        for (var i = 0; i < legacyPaths.Length; i++)
        {
            if (!Directory.Exists(legacyPaths[i]))
                continue;

            log.Warn("Legacy asset folders detected (Assets\\menu or Assets\\blocks). These are no longer used. Please remove them from your Assets.zip.");
            break;
        }
    }

    /// <summary>
    /// Returns a UI-friendly version of a path. This does NOT change the actual filesystem path used.
    /// Pixel-font rendering may not support backslashes in some builds, so we display '/'.
    /// </summary>
    public static string ToUiPath(string path) => path.Replace('\\', '/');

    /// <summary>
    /// Some users accidentally extract to Documents\LatticeVeil\Assets\Assets\... .
    /// This stays within Documents\LatticeVeil but allows locating files in that nested structure.
    /// </summary>
    public static string ResolveAssetPath(string relativePath)
    {
        return Path.Combine(AssetsDir, relativePath);
    }

    public static bool IsDisallowedAssetRelativePath(string? relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
            return false;

        var normalized = relativePath.Replace('\\', '/').TrimStart('/');
        if (string.IsNullOrWhiteSpace(normalized))
            return false;

        var first = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        if (string.IsNullOrWhiteSpace(first))
            return false;

        var hasNestedPath = normalized.Contains('/');
        if (!hasNestedPath && normalized.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            return true;

        return first.Equals("packs", StringComparison.OrdinalIgnoreCase)
            || first.Equals("data", StringComparison.OrdinalIgnoreCase)
            || first.Equals(".github", StringComparison.OrdinalIgnoreCase)
            || first.Equals(".git", StringComparison.OrdinalIgnoreCase)
            || first.Equals(".gitignore", StringComparison.OrdinalIgnoreCase)
            || first.Equals(".gitattributes", StringComparison.OrdinalIgnoreCase);
    }

    public static void RemoveDisallowedAssetEntries(Logger log)
    {
        try
        {
            if (!Directory.Exists(AssetsDir))
                return;

            foreach (var entry in Directory.GetFileSystemEntries(AssetsDir, "*", SearchOption.TopDirectoryOnly))
            {
                var name = Path.GetFileName(entry);
                if (!IsDisallowedAssetRelativePath(name))
                    continue;

                try
                {
                    if (Directory.Exists(entry))
                        Directory.Delete(entry, recursive: true);
                    else if (File.Exists(entry))
                        File.Delete(entry);

                    log.Info($"Removed disallowed asset entry: {name}");
                }
                catch (Exception ex)
                {
                    log.Warn($"Failed removing disallowed asset entry '{name}': {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            log.Warn($"Failed cleaning disallowed asset entries: {ex.Message}");
        }
    }
}
