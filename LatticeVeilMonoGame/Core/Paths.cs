using System;
using System.IO;

namespace LatticeVeilMonoGame.Core;

public static class Paths
{
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
        Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Defaults", "Assets");

    /// <summary>
    /// Returns the appropriate assets directory based on environment.
    /// Dev builds use local Defaults assets; release builds use Documents assets.
    /// </summary>
    public static string GetAssetsDir()
    {
        if (IsDevBuild)
            return LocalAssetsDir;
        return AssetsDir;
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
        Path.Combine(RootDir, "Worlds_Multiplayer");

    public static string BackupsDir =>
        Path.Combine(RootDir, "Backups");

    public static string ActiveLogPath =>
        Path.Combine(LogsDir, "current.log");

    public static string GamePidPath =>
        Path.Combine(RootDir, "game.pid");

    public static string SettingsJsonPath =>
#if PRIVATE_CLIENT
        Path.Combine(ConfigDir, "settings.private.json");
#else
        Path.Combine(RootDir, "settings.json");
#endif

    public static string PlayerProfileJsonPath =>
        Path.Combine(RootDir, "player_profile.json");

    public static string ConfigDir =>
#if PRIVATE_CLIENT
        Path.Combine(RootDir, "config");
#else
        Path.Combine(RootDir, "Config");
#endif

    /// <summary>
    /// Local friend labels (nicknames + pinned list). This is client-side only.
    /// </summary>
    public static string FriendLabelsJsonPath =>
        Path.Combine(ConfigDir, "friend_labels.json");




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
}
