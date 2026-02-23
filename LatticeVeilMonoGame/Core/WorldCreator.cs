using System;
using System.IO;

namespace LatticeVeilMonoGame.Core;

/// <summary>
/// Creates new world format structure with required files and directories
/// </summary>
public static class WorldCreator
{
    /// <summary>
    /// Creates a new world with the new format structure
    /// </summary>
    /// <param name="worldPath">Path where world should be created</param>
    /// <param name="worldName">Name of the world</param>
    /// <param name="seed">World seed</param>
    /// <param name="gameMode">Game mode for the world</param>
    /// <returns>True if successful, false otherwise</returns>
    public static bool CreateNewWorld(string worldPath, string worldName, int seed, GameMode gameMode)
    {
        try
        {
            // Create world directory
            Directory.CreateDirectory(worldPath);
            
            // Create level.lvc with required keys
            var levelContent = $@"format=LVWORLD
format_version=2
world_name=""{worldName}""
created_utc={DateTimeOffset.UtcNow:yyyy-MM-ddTHH\:mm\:ss.fffZ}
seed={seed}
generator_id=""core:nextgen""
region_size_chunks=32";
            
            var levelPath = FileConventions.GetFutureLevelPath(worldPath);
            File.WriteAllText(levelPath, levelContent);
            
            // Create worldstate.lvc with defaults
            var worldstateContent = @"pvp_enabled=true
allow_cheats=false
keep_inventory=false
time_of_day_ticks=6000
day_count=0
weather=""clear""";
            
            var worldstatePath = FileConventions.GetFutureWorldStatePath(worldPath);
            File.WriteAllText(worldstatePath, worldstateContent);
            
            // Create required directories
            Directory.CreateDirectory(FileConventions.GetFutureRegionsDir(worldPath));
            Directory.CreateDirectory(FileConventions.GetFuturePlayerDataDir(worldPath));
            Directory.CreateDirectory(FileConventions.GetFutureHistoryDir(worldPath));
            
            return true;
        }
        catch (Exception)
        {
            // Caller should handle logging
            return false;
        }
    }
}
