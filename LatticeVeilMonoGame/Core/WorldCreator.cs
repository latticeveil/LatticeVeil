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
            
            // Create world.lvc manifest with required keys
            var worldManifestContent = $@"format=LVWORLD
format_version=2
world_name=""{worldName}""
created_utc={DateTimeOffset.UtcNow:yyyy-MM-ddTHH\:mm\:ss.fffZ}
seed={seed}
generator_id=""terrain_v1""
world_type=terrain
generator=terrain_v1
region_size_chunks=32
spawn_x=0
spawn_y=64
spawn_z=0
game_mode={gameMode}
difficulty=1
cheats_enabled=false";
            
            var worldManifestPath = FileConventions.GetWorldManifestPath(worldPath);
            File.WriteAllText(worldManifestPath, worldManifestContent);
            
            // Create required directories (lowercase regions only)
            Directory.CreateDirectory(Path.Combine(worldPath, "regions"));
            Directory.CreateDirectory(Path.Combine(worldPath, "playerdata"));
            Directory.CreateDirectory(Path.Combine(worldPath, "history"));
            
            // DO NOT create legacy directories (chunks, meshcache)
            
            return true;
        }
        catch (Exception)
        {
            // Caller should handle logging
            return false;
        }
    }
}
