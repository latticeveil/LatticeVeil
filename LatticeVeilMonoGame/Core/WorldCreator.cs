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

            var meta = WorldMeta.CreateTerrain(worldName, gameMode, 4096, 256, 4096, seed);
            meta.Player.HasCustomSpawn = true;
            meta.Player.SpawnX = 0;
            meta.Player.SpawnY = 64;
            meta.Player.SpawnZ = 0;
            meta.Save(FileConventions.GetWorldManifestPath(worldPath), new Logger("WorldCreator"));
            
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
