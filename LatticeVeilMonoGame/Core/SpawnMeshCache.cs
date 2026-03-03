using System.Collections.Generic;
using System.IO;
using System.Threading;

namespace LatticeVeilMonoGame.Core;

public static class SpawnMeshCache
{
    public static string GetCacheDirectory(string worldPath)
    {
        return Path.Combine(worldPath, "spawn_meshcache_disabled");
    }

    public static string GetCachePath(string worldPath, ChunkCoord coord)
    {
        return Path.Combine(GetCacheDirectory(worldPath), $"chunk_{coord.X}_{coord.Y}_{coord.Z}.disabled");
    }

    public static void Save(string worldPath, ChunkMesh mesh)
    {
        // Disabled: no persistent mesh cache files for spawn gate.
    }

    public static bool TryLoadFresh(string worldPath, ChunkCoord coord, out ChunkMesh mesh)
    {
        mesh = ChunkMesh.Empty;
        return false;
    }

    public static int BuildAndSaveBatch(
        string worldPath,
        VoxelWorld world,
        IReadOnlyList<ChunkCoord> coords,
        CubeNetAtlas atlas,
        Logger log,
        CancellationToken cancellationToken,
        Action<int, int>? onProgress = null)
    {
        // Disabled: no persistent spawn mesh cache.
        return 0;
    }
}
