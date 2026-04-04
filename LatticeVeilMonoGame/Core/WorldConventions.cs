using System.IO;

namespace LatticeVeilMonoGame.Core;

/// <summary>
/// Canonical world file/folder conventions for the current vNext runtime.
/// </summary>
public static class WorldConventions
{
    // Directories
    public const string RegionsDirName = "regions";
    public const string PlayerDataDirName = "playerdata";
    public const string HistoryDirName = "history";
    public const string LegacyMeshCacheDirName = "meshcache";

    // Files
    public const string WorldManifestFileName = "world.lvc";
    public const string WorldStateFileName = "worldstate.lvc";
    public const string PlayerIndexFileName = "pindex.lvc";
    public const string HistoryConfigFileName = "history.lvc";
    public const string ConsoleLogFileName = "console.lvlog";
    public const string SpawnPrewarmFileName = "Spawn.lvpwarm";

    // Extensions
    public const string RegionExtension = ".lvregion";
    public const string PlayerExtension = ".lvplayer";
    public const string DisabledSuffix = ".lvdisabled";

    public static string GetWorldManifestPath(string worldPath) => Path.Combine(worldPath, WorldManifestFileName);
    public static string GetWorldStatePath(string worldPath) => Path.Combine(worldPath, WorldStateFileName);
    public static string GetRegionsDir(string worldPath) => Path.Combine(worldPath, RegionsDirName);
    public static string GetPlayerDataDir(string worldPath) => Path.Combine(worldPath, PlayerDataDirName);
    public static string GetPlayerIndexPath(string worldPath) => Path.Combine(GetPlayerDataDir(worldPath), PlayerIndexFileName);
    public static string GetHistoryDir(string worldPath) => Path.Combine(worldPath, HistoryDirName);
    public static string GetHistoryConfigPath(string worldPath) => Path.Combine(GetHistoryDir(worldPath), HistoryConfigFileName);
    public static string GetConsoleLogPath(string worldPath) => Path.Combine(worldPath, ConsoleLogFileName);
    public static string GetSpawnPrewarmPath(string worldPath) => Path.Combine(worldPath, SpawnPrewarmFileName);
}
