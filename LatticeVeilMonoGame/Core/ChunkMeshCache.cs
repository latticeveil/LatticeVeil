using System.IO;

namespace LatticeVeilMonoGame.Core;

public static class ChunkMeshCache
{
    // Mesh cache is derived data and intentionally not persisted on disk.
    // Methods remain for API compatibility only.

    public static string GetCacheDirectory(string worldPath)
    {
        return Path.Combine(worldPath, "meshcache_disabled");
    }

    public static string GetCachePath(string worldPath, ChunkCoord coord)
    {
        return Path.Combine(GetCacheDirectory(worldPath), $"chunk_{coord.X}_{coord.Y}_{coord.Z}.disabled");
    }

    public static void Save(string worldPath, ChunkMesh mesh)
    {
        // Disabled: do not persist derived mesh data to disk.
    }

    public static bool TryLoadFresh(string worldPath, string chunksDir, ChunkCoord coord, out ChunkMesh mesh)
    {
        mesh = ChunkMesh.Empty;
        return false;
    }
}
