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

    public static string PacksDir =>
        Path.Combine(RootDir, "Packs");

    /// <summary>
    /// Local development assets directory for dev builds.
    /// </summary>
    public static string LocalAssetsDir =>
        ResolveLocalAssetsDir();

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

            return HasExpectedAssetLayout(LocalAssetsDir);
        }
        catch
        {
            return false;
        }
    }

    private static string ResolveLocalAssetsDir()
    {
        var overrideDir = Environment.GetEnvironmentVariable("LATTICEVEIL_LOCAL_ASSETS");
        if (!string.IsNullOrWhiteSpace(overrideDir))
            return overrideDir.Trim();

        foreach (var start in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() })
        {
            var found = TryFindDefaultsAssetsFrom(start);
            if (!string.IsNullOrWhiteSpace(found))
                return found;
        }

        return Path.Combine(DocumentsDir, "LatticeVeil_project", "LatticeVeilMonoGame", "Defaults", "Assets");
    }

    private static string? TryFindDefaultsAssetsFrom(string? startPath)
    {
        if (string.IsNullOrWhiteSpace(startPath))
            return null;

        try
        {
            var current = Directory.Exists(startPath)
                ? new DirectoryInfo(startPath)
                : new DirectoryInfo(Path.GetDirectoryName(startPath) ?? startPath);

            while (current != null)
            {
                var candidates = new[]
                {
                    Path.Combine(current.FullName, "Defaults", "Assets"),
                    Path.Combine(current.FullName, "LatticeVeilMonoGame", "Defaults", "Assets")
                };

                for (var i = 0; i < candidates.Length; i++)
                {
                    if (HasExpectedAssetLayout(candidates[i]))
                        return candidates[i];
                }

                current = current.Parent;
            }
        }
        catch
        {
        }

        return null;
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
        Path.Combine(AppStateDir, "_OnlineCache");

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

    public static string RoamingAppDataDir =>
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

    public static string LegacyLocalAppDataDir =>
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

    public static string AppStateDir =>
        Path.Combine(RoamingAppDataDir, "LatticeVeil");

    public static string LegacyLocalAppStateDir =>
        Path.Combine(LegacyLocalAppDataDir, "LatticeVeil");

    public static string RuntimeStateDir =>
        Path.Combine(AppStateDir, "Runtime");

    public static string RuntimeSkinsDir =>
        Path.Combine(RuntimeStateDir, "skins");

    public static string ActiveSkinHashPath =>
        Path.Combine(RuntimeSkinsDir, "active.txt");

    public static string SkinChangeSignalPath =>
        Path.Combine(RuntimeSkinsDir, "active.lvc");

    public static string RuntimeRemoteSkinsDir =>
        Path.Combine(RuntimeSkinsDir, "remote");

    public static string SocialSessionCacheDir =>
        Path.Combine(RuntimeStateDir, "SocialSession");

    public static string SystemStateDir =>
        Path.Combine(AppStateDir, "System");

    /// <summary>
    /// Local friend labels (nicknames + pinned list). This is client-side only.
    /// </summary>
    public static string FriendLabelsJsonPath =>
        Path.Combine(RootDir, "friend_labels.lvc");

    public static string LegacyFriendLabelsJsonPath =>
        Path.Combine(RootDir, "friend_labels.json");

    public static string VeilnetProfileCachePath =>
        Path.Combine(SocialSessionCacheDir, "veilnet_profile_session.lvc");

    public static string VeilnetAvatarCachePath =>
        Path.Combine(SocialSessionCacheDir, "veilnet_avatar_session.lvimg");

    public static string VeilnetBannerCachePath =>
        Path.Combine(SocialSessionCacheDir, "veilnet_banner_session.lvimg");

    public static string EosIdentityPath =>
        Path.Combine(SystemStateDir, "identity.lvc");

    public static string LegacyEosIdentityPath =>
        Path.Combine(ConfigDir, "eos.identity.json");

    public static string VeilnetLauncherAuthPath =>
        Path.Combine(SystemStateDir, "launcher_auth.lvc");

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
            Directory.CreateDirectory(PacksDir);
            Directory.CreateDirectory(TexturesDir);
            Directory.CreateDirectory(MenuTexturesDir);
            Directory.CreateDirectory(BlocksTexturesDir);
            Directory.CreateDirectory(Path.Combine(GetAssetsDir(), "Models", "Blocks"));
            EnsurePacksReadme(log);

            WarnIfLegacyAssetFoldersExist(log, GetAssetsDir());
        }
        catch (Exception ex)
        {
            log.Warn($"Failed to create asset directories: {ex.Message}");
        }
    }

    private static void EnsurePacksReadme(Logger log)
    {
        try
        {
            var readmePath = Path.Combine(PacksDir, "README.txt");
            if (File.Exists(readmePath))
                return;

            File.WriteAllText(
                readmePath,
                "Drop content packs into this folder.\r\n" +
                "Folder: Documents/LatticeVeil/Packs\r\n" +
                "Each pack should live in its own subfolder.\r\n");
        }
        catch (Exception ex)
        {
            log.Warn($"Failed to create packs README: {ex.Message}");
        }
    }

    private static bool HasExpectedAssetLayout(string assetsRoot)
    {
        var texturesDir = Path.Combine(assetsRoot, "textures");
        return Directory.Exists(texturesDir)
            && Directory.Exists(Path.Combine(texturesDir, "menu"))
            && Directory.Exists(Path.Combine(texturesDir, "blocks"));
    }

    private static void WarnIfLegacyAssetFoldersExist(Logger log, string assetsRoot)
    {
        var found = FindLegacyAssetFolders(assetsRoot);

        if (found.Count > 0)
        {
            log.Warn(
                "Legacy asset folders detected in active asset root. " +
                "The game reads textures\\menu and textures\\blocks. " +
                $"Found: {string.Join(", ", found)}");
        }
    }

    private static System.Collections.Generic.List<string> FindLegacyAssetFolders(string assetsRoot)
    {
        var found = new System.Collections.Generic.List<string>();
        if (!Directory.Exists(assetsRoot))
            return found;

        foreach (var directory in Directory.EnumerateDirectories(assetsRoot, "*", SearchOption.TopDirectoryOnly))
        {
            var name = Path.GetFileName(directory);
            if (name.Equals("menu", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("blocks", StringComparison.OrdinalIgnoreCase))
            {
                found.Add(Path.GetRelativePath(assetsRoot, directory));
            }
        }

        var texturesRoot = Path.Combine(assetsRoot, "textures");
        if (!Directory.Exists(texturesRoot))
            return found;

        foreach (var directory in Directory.EnumerateDirectories(texturesRoot, "*", SearchOption.TopDirectoryOnly))
        {
            var name = Path.GetFileName(directory);
            if ((name.Equals("menu", StringComparison.OrdinalIgnoreCase) && !name.Equals("menu", StringComparison.Ordinal)) ||
                (name.Equals("blocks", StringComparison.OrdinalIgnoreCase) && !name.Equals("blocks", StringComparison.Ordinal)))
            {
                found.Add(Path.GetRelativePath(assetsRoot, directory));
            }
        }

        return found;
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
