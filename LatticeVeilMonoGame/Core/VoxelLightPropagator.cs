using System;
using System.Collections.Generic;

namespace LatticeVeilMonoGame.Core;

/// <summary>
/// Three-phase voxel light propagator.
///
/// Phase 1 — Sky column sweep:
///   Iterates every XZ column top-to-bottom. Any voxel with a clear vertical
///   path to the sky top receives raw skyLight = 15. Propagation stops as soon
///   as an opaque block is hit; below it skyLight stays 0 until Phase 2 fills it.
///
/// Phase 2 — Sky BFS flood-fill:
///   Spreads sky light laterally and downward through transparent/non-opaque
///   voxels, attenuating by 1 per step. Downward propagation from a full-sky
///   voxel does NOT attenuate (models a straight sunlight shaft through air).
///
/// Phase 3 — Block-light BFS:
///   Seeds from every light-emitting block (LightLevel > 0), then BFS-floods
///   outward, attenuating by 1 per step.
///
/// Opacity rule (matches plan): a voxel is opaque when
///   IsSolid &amp;&amp; !IsTransparent for its BlockDef.
///   Air (id==0) is always transparent.
///   Custom-model blocks are treated as transparent for propagation so light
///   passes through torches, flowers, etc.
///
/// Output: chunk._lightMap (nibble-packed skyLight|blockLight per voxel).
/// Also returns affectedNeighbors — a set of neighboring ChunkCoords whose
/// lightmaps are invalidated by light bleeding across the chunk boundary.
/// </summary>
public static class VoxelLightPropagator
{
    // Maximum light value (Minecraft-style 0-15 scale).
    private const int MaxLight = 15;

    // ── Public entry point ────────────────────────────────────────────────────

    /// <summary>
    /// Run all three propagation phases for <paramref name="chunk"/> and write
    /// results into <c>chunk._lightMap</c>.
    /// Also discovers neighbor chunks that need their own lightmaps re-propagated
    /// because light crossed the chunk boundary.
    /// </summary>
    public static void PropagateLight(
        VoxelWorld world,
        VoxelChunkData chunk,
        out HashSet<ChunkCoord> affectedNeighbors)
    {
        affectedNeighbors = new HashSet<ChunkCoord>();

        // Zero the lightmap before re-computing.
        chunk.ClearLightMap();

        var sizeX = VoxelChunkData.ChunkSizeX;
        var sizeY = VoxelChunkData.ChunkSizeY;
        var sizeZ = VoxelChunkData.ChunkSizeZ;
        var originX = chunk.Coord.X * sizeX;
        var originY = chunk.Coord.Y * sizeY;
        var originZ = chunk.Coord.Z * sizeZ;

        // --- Phase 1: Direct sky column sweep ---
        // Queues sky seeds for Phase 2.
        var skyQueue = new Queue<(int lx, int ly, int lz, int val)>();
        PhaseOneSkyColumns(chunk, sizeX, sizeY, sizeZ, originX, originY, originZ, world, skyQueue);

        // --- Phase 2: Sky BFS ---
        PhaseTwoBfs(chunk, sizeX, sizeY, sizeZ, originX, originY, originZ,
                    world, skyQueue, isSky: true, affectedNeighbors);

        // --- Phase 3: Block-light BFS ---
        var blockQueue = new Queue<(int lx, int ly, int lz, int val)>();
        SeedBlockLightSources(chunk, sizeX, sizeY, sizeZ, blockQueue);
        PhaseTwoBfs(chunk, sizeX, sizeY, sizeZ, originX, originY, originZ,
                    world, blockQueue, isSky: false, affectedNeighbors);

        chunk.MarkLightmapValid();
    }

    // ── Phase 1: Sky column sweep ─────────────────────────────────────────────

    private static void PhaseOneSkyColumns(
        VoxelChunkData chunk,
        int sizeX, int sizeY, int sizeZ,
        int originX, int originY, int originZ,
        VoxelWorld world,
        Queue<(int, int, int, int)> skyQueue)
    {
        // We need the world height to know if this is a top chunk.
        // If the chunk top is at or above the world height, sunlight enters from above.
        // We check the voxel directly above sizeY-1 in the world to see if it is open sky.
        int topBlockY = originY + sizeY; // first world-Y above this chunk

        for (int lx = 0; lx < sizeX; lx++)
        {
            for (int lz = 0; lz < sizeZ; lz++)
            {
                // Test if the column above this chunk is open (i.e., all air/transparent).
                // We sample up to a reasonable distance (worldHeight - topBlockY).
                bool columnOpenAbove = IsColumnOpenAbove(lx + originX, topBlockY, lz + originZ, world);

                if (!columnOpenAbove)
                    continue;

                // Sweep top-to-bottom within the chunk.
                for (int ly = sizeY - 1; ly >= 0; ly--)
                {
                    var id = chunk.GetLocal(lx, ly, lz);
                    if (IsOpaque(id))
                        break; // Column is now shadowed; stop propagating downward.

                    chunk.SetSkyLight(lx, ly, lz, MaxLight);
                    skyQueue.Enqueue((lx, ly, lz, MaxLight));
                }
            }
        }
    }

    /// <summary>
    /// Returns true if every voxel in the world column (wx, wy_start..worldTop, wz)
    /// is transparent, meaning sunlight reaches this column unblocked from above.
    /// Caps the sample at 512 blocks above the chunk to stay performant.
    /// </summary>
    private static bool IsColumnOpenAbove(int wx, int wyStart, int wz, VoxelWorld world)
    {
        int worldHeight = world.WorldHeight;
        int limit = Math.Min(wyStart + 512, worldHeight);
        for (int wy = wyStart; wy < limit; wy++)
        {
            var id = (byte)world.GetBlock(wx, wy, wz);
            if (IsOpaque(id))
                return false;
        }
        return true;
    }

    // ── Phase 2/3: BFS flood-fill ─────────────────────────────────────────────

    /// <summary>
    /// Generic BFS that works for both sky-light (isSky=true) and block-light (isSky=false).
    ///
    /// Sky-light special rule: spreading *downward* from a full-sky (val==15) voxel
    /// does not attenuate, modelling an unobstructed sunlight shaft. All other
    /// directions attenuate by 1 per step.
    ///
    /// Cross-chunk propagation: when BFS hits a voxel at a chunk boundary, we look
    /// up the neighbor chunk and write its lightmap if available. The neighbor's
    /// ChunkCoord is added to affectedNeighbors so the caller can queue a rebuild.
    /// </summary>
    private static void PhaseTwoBfs(
        VoxelChunkData chunk,
        int sizeX, int sizeY, int sizeZ,
        int originX, int originY, int originZ,
        VoxelWorld world,
        Queue<(int lx, int ly, int lz, int val)> queue,
        bool isSky,
        HashSet<ChunkCoord> affectedNeighbors)
    {
        // Neighbor deltas: ±X, ±Y, ±Z
        ReadOnlySpan<(int dx, int dy, int dz)> dirs = stackalloc (int, int, int)[]
        {
            ( 1,  0,  0),
            (-1,  0,  0),
            ( 0,  1,  0),
            ( 0, -1,  0),
            ( 0,  0,  1),
            ( 0,  0, -1),
        };

        while (queue.Count > 0)
        {
            var (lx, ly, lz, val) = queue.Dequeue();

            foreach (var (dx, dy, dz) in dirs)
            {
                int nlx = lx + dx;
                int nly = ly + dy;
                int nlz = lz + dz;

                // Sky-light non-attenuation downward: when spreading down from full-sky.
                int nval = (isSky && dy == -1 && val == MaxLight) ? MaxLight : val - 1;
                if (nval <= 0)
                    continue;

                // --- Within-chunk path ---
                if ((uint)nlx < (uint)sizeX && (uint)nly < (uint)sizeY && (uint)nlz < (uint)sizeZ)
                {
                    var nid = chunk.GetLocal(nlx, nly, nlz);
                    if (IsOpaque(nid))
                        continue;

                    int current = isSky
                        ? chunk.GetSkyLight(nlx, nly, nlz)
                        : chunk.GetBlockLight(nlx, nly, nlz);

                    if (nval > current)
                    {
                        if (isSky)
                            chunk.SetSkyLight(nlx, nly, nlz, nval);
                        else
                            chunk.SetBlockLight(nlx, nly, nlz, nval);

                        queue.Enqueue((nlx, nly, nlz, nval));
                    }
                    continue;
                }

                // --- Cross-chunk boundary path ---
                int wx = originX + nlx;
                int wy = originY + nly;
                int wz = originZ + nlz;

                VoxelWorld.WorldToChunkCoords(wx, wy, wz, out var ncx, out var ncy, out var ncz);
                var neighborCoord = new ChunkCoord(ncx, ncy, ncz);

                var neighborChunk = world.TryGetChunk(neighborCoord);
                if (neighborChunk == null)
                    continue;

                int neighborLocalX = wx - ncx * sizeX;
                int neighborLocalY = wy - ncy * sizeY;
                int neighborLocalZ = wz - ncz * sizeZ;

                // Clamp to valid range (should always be valid given WorldToChunkCoords).
                if ((uint)neighborLocalX >= (uint)sizeX ||
                    (uint)neighborLocalY >= (uint)sizeY ||
                    (uint)neighborLocalZ >= (uint)sizeZ)
                    continue;

                var nidNeighbor = neighborChunk.GetLocal(neighborLocalX, neighborLocalY, neighborLocalZ);
                if (IsOpaque(nidNeighbor))
                    continue;

                int currentNeighbor = isSky
                    ? neighborChunk.GetSkyLight(neighborLocalX, neighborLocalY, neighborLocalZ)
                    : neighborChunk.GetBlockLight(neighborLocalX, neighborLocalY, neighborLocalZ);

                if (nval > currentNeighbor)
                {
                    if (isSky)
                        neighborChunk.SetSkyLight(neighborLocalX, neighborLocalY, neighborLocalZ, nval);
                    else
                        neighborChunk.SetBlockLight(neighborLocalX, neighborLocalY, neighborLocalZ, nval);

                    // Mark the neighbor's lightmap as invalid so it is re-propagated before its next mesh build.
                    neighborChunk.InvalidateLightmap();
                    affectedNeighbors.Add(neighborCoord);
                }
            }
        }
    }

    // ── Phase 3 seed helper ───────────────────────────────────────────────────

    private static void SeedBlockLightSources(
        VoxelChunkData chunk,
        int sizeX, int sizeY, int sizeZ,
        Queue<(int, int, int, int)> queue)
    {
        for (int lx = 0; lx < sizeX; lx++)
        {
            for (int ly = 0; ly < sizeY; ly++)
            {
                for (int lz = 0; lz < sizeZ; lz++)
                {
                    var id = chunk.GetLocal(lx, ly, lz);
                    if (id == BlockIds.Air)
                        continue;

                    var def = BlockRegistry.Get(id);
                    if (def.LightLevel <= 0f)
                        continue;

                    // Convert 0..1 light level to 0..15 scale.
                    // A LightLevel of 1.0f maps to blockLight = 14 (one step below max
                    // so adjacent voxels can receive 13, matching the Minecraft torch model).
                    int seedVal = Math.Clamp((int)MathF.Round(def.LightLevel * 14f), 1, MaxLight - 1);
                    chunk.SetBlockLight(lx, ly, lz, seedVal);
                    queue.Enqueue((lx, ly, lz, seedVal));
                }
            }
        }
    }

    // ── Opacity helper ────────────────────────────────────────────────────────

    /// <summary>
    /// A voxel is opaque for light propagation if its BlockDef has IsSolid=true
    /// AND IsTransparent=false. Air (id 0) is always transparent. Custom-model
    /// blocks (torches, flowers) are treated as transparent so light passes through.
    /// </summary>
    [System.Runtime.CompilerServices.MethodImpl(
        System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    private static bool IsOpaque(byte id)
    {
        if (id == BlockIds.Air)
            return false;

        var def = BlockRegistry.Get(id);
        // Custom-model blocks are always transparent for propagation.
        if (def.HasCustomModel)
            return false;

        return def.IsSolid && !def.IsTransparent;
    }
}
