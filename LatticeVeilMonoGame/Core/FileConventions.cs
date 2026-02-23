using System.IO;

namespace LatticeVeilMonoGame.Core;

/// <summary>
/// Defines file/folder naming conventions and directory structures for LatticeVeil world formats.
/// This file does not change runtime behavior; it is for future milestones.
/// 
/// Canonical reference: DEV/WORLD_FORMAT_PLAN.md → File Ecosystem Reference (Canonical).
/// </summary>
public static class FileConventions
{
    // ================================
    // DIRECTORY NAME CONSTANTS
    // ================================
    
    /// <summary>
    /// Directory name for region files (chunk storage)
    /// </summary>
    public const string RegionsDirName = "Regions";
    
    /// <summary>
    /// Directory name for mesh cache files
    /// </summary>
    public const string MeshCacheDirName = "MeshCache";
    
    /// <summary>
    /// Directory name for player data files
    /// </summary>
    public const string PlayerDataDirName = "playerdata";
    
    /// <summary>
    /// Directory name for world history files
    /// </summary>
    public const string HistoryDirName = "history";
    
    // ================================
    // FILE NAME CONSTANTS
    // ================================
    
    /// <summary>
    /// World metadata file name (future format)
    /// </summary>
    public const string LevelFileName = "level.lvc";
    
    /// <summary>
    /// World state file name (future format)
    /// </summary>
    public const string WorldStateFileName = "worldstate.lvc";
    
    /// <summary>
    /// Player index file name (future format)
    /// </summary>
    public const string PlayerIndexFileName = "pindex.lvc";
    
    /// <summary>
    /// History configuration file name (future format)
    /// </summary>
    public const string HistoryConfigFileName = "history.lvc";
    
    /// <summary>
    /// Console log file name (future format)
    /// </summary>
    public const string ConsoleLogFileName = "console.lvlog";
    
    // ================================
    // FILE EXTENSION CONSTANTS
    // ================================
    
    /// <summary>
    /// Region file extension (future format)
    /// </summary>
    public const string RegionExtension = ".lvregion";
    
    /// <summary>
    /// Mesh cache file extension (future format)
    /// </summary>
    public const string MeshExtension = ".lvmesh";
    
    /// <summary>
    /// Player data file extension (future format)
    /// </summary>
    public const string PlayerExtension = ".lvplayer";
    
    /// <summary>
    /// Suffix for disabled packs/mods/etc. (game should ignore these later)
    /// </summary>
    public const string DisabledSuffix = ".lvdisabled";
    
    // ================================
    // FUTURE FORMAT HELPERS
    // ================================
    
    /// <summary>
    /// Gets the path to the future level.lvc file for a world
    /// </summary>
    /// <param name="worldPath">Base world directory path</param>
    /// <returns>Full path to level.lvc</returns>
    public static string GetFutureLevelPath(string worldPath)
    {
        return Path.Combine(worldPath, LevelFileName);
    }
    
    /// <summary>
    /// Gets the path to the future worldstate.lvc file for a world
    /// </summary>
    /// <param name="worldPath">Base world directory path</param>
    /// <returns>Full path to worldstate.lvc</returns>
    public static string GetFutureWorldStatePath(string worldPath)
    {
        return Path.Combine(worldPath, WorldStateFileName);
    }
    
    /// <summary>
    /// Gets the path to the future Regions directory for a world
    /// </summary>
    /// <param name="worldPath">Base world directory path</param>
    /// <returns>Full path to Regions directory</returns>
    public static string GetFutureRegionsDir(string worldPath)
    {
        return Path.Combine(worldPath, RegionsDirName);
    }
    
    /// <summary>
    /// Gets the path to the future MeshCache directory for a world
    /// </summary>
    /// <param name="worldPath">Base world directory path</param>
    /// <returns>Full path to MeshCache directory</returns>
    public static string GetFutureMeshCacheDir(string worldPath)
    {
        return Path.Combine(worldPath, MeshCacheDirName);
    }
    
    /// <summary>
    /// Gets the path to the future playerdata directory for a world
    /// </summary>
    /// <param name="worldPath">Base world directory path</param>
    /// <returns>Full path to playerdata directory</returns>
    public static string GetFuturePlayerDataDir(string worldPath)
    {
        return Path.Combine(worldPath, PlayerDataDirName);
    }
    
    /// <summary>
    /// Gets the path to the future player index file for a world
    /// </summary>
    /// <param name="worldPath">Base world directory path</param>
    /// <returns>Full path to pindex.lvc</returns>
    public static string GetFuturePlayerIndexPath(string worldPath)
    {
        return Path.Combine(GetFuturePlayerDataDir(worldPath), PlayerIndexFileName);
    }
    
    /// <summary>
    /// Gets the path to the future history directory for a world
    /// </summary>
    /// <param name="worldPath">Base world directory path</param>
    /// <returns>Full path to history directory</returns>
    public static string GetFutureHistoryDir(string worldPath)
    {
        return Path.Combine(worldPath, HistoryDirName);
    }
    
    /// <summary>
    /// Gets the path to the future history configuration file for a world
    /// </summary>
    /// <param name="worldPath">Base world directory path</param>
    /// <returns>Full path to history.lvc</returns>
    public static string GetFutureHistoryConfigPath(string worldPath)
    {
        return Path.Combine(GetFutureHistoryDir(worldPath), HistoryConfigFileName);
    }
    
    /// <summary>
    /// Gets the path to the future console log file for a world
    /// </summary>
    /// <param name="worldPath">Base world directory path</param>
    /// <returns>Full path to console.lvlog</returns>
    public static string GetFutureConsoleLogPath(string worldPath)
    {
        return Path.Combine(worldPath, ConsoleLogFileName);
    }
}
