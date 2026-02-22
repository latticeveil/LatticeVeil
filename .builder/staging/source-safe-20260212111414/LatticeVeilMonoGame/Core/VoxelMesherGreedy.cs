using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace LatticeVeilMonoGame.Core;

public static class VoxelMesherGreedy
{
    public static ChunkMesh BuildChunkMesh(VoxelWorld world, VoxelChunkData chunk, CubeNetAtlas atlas, Logger log)
    {
        // Normal mesh with full greedy optimization
        return BuildChunkMeshInternal(chunk, atlas, log, useGreedyOptimization: true, world.GetBlock);
    }

    public static ChunkMesh BuildChunkMeshFast(VoxelWorld world, VoxelChunkData chunk, CubeNetAtlas atlas, Logger log)
    {
        // C) Fast mesh - no greedy merge, no neighbor dependency
        return BuildChunkMeshInternal(chunk, atlas, log, useGreedyOptimization: false, world.GetBlock);
    }

    /// <summary>
    /// Priority mesh path used for interaction feedback.
    /// It snapshots chunk blocks once and avoids expensive world-lock reads for interior neighbors.
    /// </summary>
    public static ChunkMesh BuildChunkMeshPriority(VoxelWorld world, VoxelChunkData chunk, CubeNetAtlas atlas, Logger log)
    {
        var sizeX = VoxelChunkData.ChunkSizeX;
        var sizeY = VoxelChunkData.ChunkSizeY;
        var sizeZ = VoxelChunkData.ChunkSizeZ;

        var originX = chunk.Coord.X * sizeX;
        var originY = chunk.Coord.Y * sizeY;
        var originZ = chunk.Coord.Z * sizeZ;

        var localBlocks = new byte[sizeX, sizeY, sizeZ];
        chunk.CopyBlocksTo(localBlocks);

        byte GetBlockLocalOrWorld(int wx, int wy, int wz)
        {
            var lx = wx - originX;
            var ly = wy - originY;
            var lz = wz - originZ;
            if ((uint)lx < sizeX && (uint)ly < sizeY && (uint)lz < sizeZ)
                return localBlocks[lx, ly, lz];

            return world.GetBlock(wx, wy, wz);
        }

        // Keep priority path fast (no greedy merge) but geometry-identical to normal meshing.
        return BuildChunkMeshInternal(chunk, atlas, log, useGreedyOptimization: false, GetBlockLocalOrWorld);
    }

    private static ChunkMesh BuildChunkMeshInternal(VoxelChunkData chunk, CubeNetAtlas atlas, Logger log, bool useGreedyOptimization, Func<int, int, int, byte> getBlock)
    {
        var opaque = new List<VertexPositionTexture>();
        var transparent = new List<VertexPositionTexture>();
        var water = new List<VertexPositionTexture>();

        var sizeX = VoxelChunkData.ChunkSizeX;
        var sizeY = VoxelChunkData.ChunkSizeY;
        var sizeZ = VoxelChunkData.ChunkSizeZ;

        var bs = Scale.BlockSize;
        var originX = chunk.Coord.X * sizeX;
        var originY = chunk.Coord.Y * sizeY;
        var originZ = chunk.Coord.Z * sizeZ;

        var dims = new[] { sizeX, sizeY, sizeZ };
        var x = new int[3];
        var q = new int[3];

        for (var d = 0; d < 3; d++)
        {
            var u = (d + 1) % 3;
            var v = (d + 2) % 3;
            q[0] = q[1] = q[2] = 0;
            q[d] = 1;

            var mask = new MaskCell[dims[u] * dims[v]];

            for (x[d] = -1; x[d] < dims[d];)
            {
                var n = 0;
                for (x[v] = 0; x[v] < dims[v]; x[v]++)
                {
                    for (x[u] = 0; x[u] < dims[u]; x[u]++)
                    {
                        var ax = originX + x[0];
                        var ay = originY + x[1];
                        var az = originZ + x[2];
                        var bx = originX + x[0] + q[0];
                        var by = originY + x[1] + q[1];
                        var bz = originZ + x[2] + q[2];

                        var a = getBlock(ax, ay, az);
                        var b = getBlock(bx, by, bz);
                        mask[n++] = BuildMaskCell(a, b, d, atlas);
                    }
                }

                x[d]++;

                for (var j = 0; j < dims[v]; j++)
                {
                    for (var i = 0; i < dims[u];)
                    {
                        var index = i + j * dims[u];
                        var cell = mask[index];
                        if (!cell.IsValid)
                        {
                            i++;
                            continue;
                        }

                        // C) FAST-FIRST MESHING - Skip greedy optimization for fast path
                        int w, h;
                        if (useGreedyOptimization)
                        {
                            // Normal greedy optimization
                            w = 1;
                            while (i + w < dims[u] && mask[index + w].SameAs(cell))
                                w++;

                            h = 1;
                            var done = false;
                            while (j + h < dims[v] && !done)
                            {
                                for (var k = 0; k < w; k++)
                                {
                                    if (!mask[index + k + h * dims[u]].SameAs(cell))
                                    {
                                        done = true;
                                        break;
                                    }
                                }

                                if (!done)
                                    h++;
                            }
                        }
                        else
                        {
                            // Fast path - no merging, single quad per face
                            w = 1;
                            h = 1;
                        }

                        var px = d == 0 ? x[d] : 0;
                        var py = d == 1 ? x[d] : 0;
                        var pz = d == 2 ? x[d] : 0;

                        if (u == 0) px = i;
                        else if (u == 1) py = i;
                        else pz = i;

                        if (v == 0) px = j;
                        else if (v == 1) py = j;
                        else pz = j;

                        atlas.GetFaceUvRect(cell.BlockId, cell.Face, out var uv00, out var uv10, out var uv11, out var uv01);
                        AdjustFaceUvs(cell.Face, ref uv00, ref uv10, ref uv11, ref uv01);
                        List<VertexPositionTexture> list;
                        if (cell.BlockId == BlockIds.Water)
                            list = water;
                        else
                            list = cell.Transparent ? transparent : opaque;
                        AddTiledQuad(list, originX, originY, originZ, px, py, pz, u, v, w, h, bs, uv00, uv10, uv11, uv01, IsBackFace(cell.Face));

                        for (var y = 0; y < h; y++)
                        {
                            for (var k = 0; k < w; k++)
                                mask[index + k + y * dims[u]] = default;
                        }

                        i += w;
                    }
                }
            }
        }

        AppendCustomModels(chunk, atlas, log, originX, originY, originZ, bs, opaque, transparent);

        var min = new Vector3(originX * bs, originY * bs, originZ * bs);
        var max = min + new Vector3(sizeX * bs, sizeY * bs, sizeZ * bs);
        return new ChunkMesh(chunk.Coord, opaque.ToArray(), transparent.ToArray(), water.ToArray(), new BoundingBox(min, max));
    }

    private static void AppendCustomModels(
        VoxelChunkData chunk,
        CubeNetAtlas atlas,
        Logger log,
        int originX,
        int originY,
        int originZ,
        float blockSize,
        List<VertexPositionTexture> opaque,
        List<VertexPositionTexture> transparent)
    {
        var cache = new Dictionary<byte, VertexPositionTexture[]>();
        var centerOffset = new Vector3(0.5f * blockSize, 0.5f * blockSize, 0.5f * blockSize);

        for (var y = 0; y < VoxelChunkData.ChunkSizeY; y++)
        {
            for (var z = 0; z < VoxelChunkData.ChunkSizeZ; z++)
            {
                for (var x = 0; x < VoxelChunkData.ChunkSizeX; x++)
                {
                    var id = chunk.GetLocal(x, y, z);
                    if (id == BlockIds.Air)
                        continue;

                    var def = BlockRegistry.Get(id);
                    if (!def.HasCustomModel)
                        continue;

                    if (!cache.TryGetValue(id, out var mesh))
                    {
                        var model = BlockModel.GetModel((BlockId)id, log);
                        mesh = model.BuildMesh(atlas, (BlockId)id);
                        cache[id] = mesh;
                    }

                    if (mesh.Length == 0)
                        continue;

                    var offset = new Vector3(
                        (originX + x) * blockSize,
                        (originY + y) * blockSize,
                        (originZ + z) * blockSize) + centerOffset;

                    var target = def.IsTransparent ? transparent : opaque;
                    for (var i = 0; i < mesh.Length; i++)
                    {
                        var v = mesh[i];
                        target.Add(new VertexPositionTexture(v.Position + offset, v.TextureCoordinate));
                    }
                }
            }
        }
    }

    private static MaskCell BuildMaskCell(byte a, byte b, int axis, CubeNetAtlas atlas)
    {
        var aDef = BlockRegistry.Get(a);
        var bDef = BlockRegistry.Get(b);

        var aFilled = a != BlockIds.Air && !aDef.HasCustomModel;
        var bFilled = b != BlockIds.Air && !bDef.HasCustomModel;

        if (!aFilled && !bFilled)
            return default;

        var aTransparent = aFilled && atlas.IsTransparent(a);
        var bTransparent = bFilled && atlas.IsTransparent(b);

        if (aFilled && !bFilled)
            return new MaskCell(a, FaceDirPos(axis), aTransparent);

        if (!aFilled && bFilled)
            return new MaskCell(b, FaceDirNeg(axis), bTransparent);

        if (a == b)
            return default;

        if (aTransparent && !bTransparent)
            return new MaskCell(a, FaceDirPos(axis), aTransparent);

        if (!aTransparent && bTransparent)
            return new MaskCell(b, FaceDirNeg(axis), bTransparent);

        if (aTransparent && bTransparent)
            return new MaskCell(a, FaceDirPos(axis), aTransparent);

        return default;
    }

    private static FaceDirection FaceDirPos(int axis) => axis switch
    {
        0 => FaceDirection.PosX,
        1 => FaceDirection.PosY,
        _ => FaceDirection.PosZ
    };

    private static FaceDirection FaceDirNeg(int axis) => axis switch
    {
        0 => FaceDirection.NegX,
        1 => FaceDirection.NegY,
        _ => FaceDirection.NegZ
    };

    private static bool IsBackFace(FaceDirection face)
    {
        return face == FaceDirection.NegX || face == FaceDirection.NegY || face == FaceDirection.NegZ;
    }

    private static void AddTiledQuad(List<VertexPositionTexture> verts, int originX, int originY, int originZ, int px, int py, int pz, int u, int v, int w, int h, float bs, Vector2 uv00, Vector2 uv10, Vector2 uv11, Vector2 uv01, bool flip)
    {
        var stepUx = u == 0 ? 1 : 0;
        var stepUy = u == 1 ? 1 : 0;
        var stepUz = u == 2 ? 1 : 0;

        var stepVx = v == 0 ? 1 : 0;
        var stepVy = v == 1 ? 1 : 0;
        var stepVz = v == 2 ? 1 : 0;

        for (var ty = 0; ty < h; ty++)
        {
            for (var tx = 0; tx < w; tx++)
            {
                var x = px + (u == 0 ? tx : 0) + (v == 0 ? ty : 0);
                var y = py + (u == 1 ? tx : 0) + (v == 1 ? ty : 0);
                var z = pz + (u == 2 ? tx : 0) + (v == 2 ? ty : 0);

                var p0 = new Vector3((originX + x) * bs, (originY + y) * bs, (originZ + z) * bs);
                var p1 = new Vector3((originX + x + stepUx) * bs, (originY + y + stepUy) * bs, (originZ + z + stepUz) * bs);
                var p2 = new Vector3((originX + x + stepUx + stepVx) * bs, (originY + y + stepUy + stepVy) * bs, (originZ + z + stepUz + stepVz) * bs);
                var p3 = new Vector3((originX + x + stepVx) * bs, (originY + y + stepVy) * bs, (originZ + z + stepVz) * bs);

                AddQuad(verts, p0, p1, p2, p3, uv00, uv10, uv11, uv01, flip);
            }
        }
    }

    private static void AddQuad(List<VertexPositionTexture> verts, Vector3 p0, Vector3 p1, Vector3 p2, Vector3 p3, Vector2 uv00, Vector2 uv10, Vector2 uv11, Vector2 uv01, bool flip)
    {
        if (!flip)
        {
            verts.Add(new VertexPositionTexture(p0, uv00));
            verts.Add(new VertexPositionTexture(p1, uv10));
            verts.Add(new VertexPositionTexture(p2, uv11));
            verts.Add(new VertexPositionTexture(p0, uv00));
            verts.Add(new VertexPositionTexture(p2, uv11));
            verts.Add(new VertexPositionTexture(p3, uv01));
        }
        else
        {
            verts.Add(new VertexPositionTexture(p0, uv00));
            verts.Add(new VertexPositionTexture(p2, uv11));
            verts.Add(new VertexPositionTexture(p1, uv10));
            verts.Add(new VertexPositionTexture(p0, uv00));
            verts.Add(new VertexPositionTexture(p3, uv01));
            verts.Add(new VertexPositionTexture(p2, uv11));
        }
    }

    private static void AdjustFaceUvs(FaceDirection face, ref Vector2 uv00, ref Vector2 uv10, ref Vector2 uv11, ref Vector2 uv01)
    {
        switch (face)
        {
            case FaceDirection.PosX:
                SwapUvAxes(ref uv00, ref uv10, ref uv11, ref uv01);
                FlipU(ref uv00, ref uv10, ref uv11, ref uv01);
                break;
            case FaceDirection.NegX:
                SwapUvAxes(ref uv00, ref uv10, ref uv11, ref uv01);
                FlipU(ref uv00, ref uv10, ref uv11, ref uv01);
                break;
            case FaceDirection.PosY:
                SwapUvAxes(ref uv00, ref uv10, ref uv11, ref uv01);
                FlipV(ref uv00, ref uv10, ref uv11, ref uv01);
                break;
            case FaceDirection.NegY:
                SwapUvAxes(ref uv00, ref uv10, ref uv11, ref uv01);
                break;
            case FaceDirection.PosZ:
                FlipV(ref uv00, ref uv10, ref uv11, ref uv01);
                break;
            case FaceDirection.NegZ:
                FlipV(ref uv00, ref uv10, ref uv11, ref uv01);
                break;
        }
    }

    private static void SwapUvAxes(ref Vector2 uv00, ref Vector2 uv10, ref Vector2 uv11, ref Vector2 uv01)
    {
        var tmp = uv10;
        uv10 = uv01;
        uv01 = tmp;
    }

    private static void FlipU(ref Vector2 uv00, ref Vector2 uv10, ref Vector2 uv11, ref Vector2 uv01)
    {
        var tmp = uv00;
        uv00 = uv10;
        uv10 = tmp;
        tmp = uv01;
        uv01 = uv11;
        uv11 = tmp;
    }

    private static void FlipV(ref Vector2 uv00, ref Vector2 uv10, ref Vector2 uv11, ref Vector2 uv01)
    {
        var tmp = uv00;
        uv00 = uv01;
        uv01 = tmp;
        tmp = uv10;
        uv10 = uv11;
        uv11 = tmp;
    }

    private readonly struct MaskCell
    {
        public readonly bool IsValid;
        public readonly byte BlockId;
        public readonly FaceDirection Face;
        public readonly bool Transparent;

        public MaskCell(byte blockId, FaceDirection face, bool transparent)
        {
            IsValid = true;
            BlockId = blockId;
            Face = face;
            Transparent = transparent;
        }

        public bool SameAs(MaskCell other)
        {
            return IsValid && other.IsValid && BlockId == other.BlockId && Face == other.Face && Transparent == other.Transparent;
        }
    }
}
