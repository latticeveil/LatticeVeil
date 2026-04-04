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
        // Wrap in a lambda so we stay compatible even if the world's GetBlock return type changes.
        byte GetBlockCompat(int wx, int wy, int wz) => (byte)world.GetBlock(wx, wy, wz);
        return BuildChunkMeshInternal(chunk, atlas, log, useGreedyOptimization: true, GetBlockCompat);
    }

    public static ChunkMesh BuildChunkMeshFast(VoxelWorld world, VoxelChunkData chunk, CubeNetAtlas atlas, Logger log)
    {
        // C) Fast mesh - no greedy merge, no neighbor dependency
        // Wrap in a lambda so we stay compatible even if the world's GetBlock return type changes.
        byte GetBlockCompat(int wx, int wy, int wz) => (byte)world.GetBlock(wx, wy, wz);
        return BuildChunkMeshInternal(chunk, atlas, log, useGreedyOptimization: false, GetBlockCompat);
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

            return (byte)world.GetBlock(wx, wy, wz);
        }

        // Keep priority path fast (no greedy merge) but geometry-identical to normal meshing.
        return BuildChunkMeshInternal(chunk, atlas, log, useGreedyOptimization: false, GetBlockLocalOrWorld);
    }

    private static ChunkMesh BuildChunkMeshInternal(VoxelChunkData chunk, CubeNetAtlas atlas, Logger log, bool useGreedyOptimization, Func<int, int, int, byte> getBlock)
    {
        // Max vertices for a chunk (16x16x16 * 6 faces * 6 vertices per face)
        // 4096 * 36 = 147,456 vertices max. Renting a buffer slightly larger.
        const int MaxPossibleVertices = 150000;
        
        var opaqueBuffer = SimpleMemoryManager.RentVertices(MaxPossibleVertices);
        var cutoutBuffer = SimpleMemoryManager.RentVertices(MaxPossibleVertices);
        var transparentBuffer = SimpleMemoryManager.RentVertices(MaxPossibleVertices);
        var waterBuffer = SimpleMemoryManager.RentVertices(MaxPossibleVertices);

        var counts = new int[4]; // opaque, cutout, transparent, water

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
                        mask[n++] = BuildMaskCell(a, b, d);
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

                        int w, h;
                        var useConnectedTexture = BlockRegistry.UsesConnectedTexture(cell.BlockId);
                        var allowMerge = useGreedyOptimization && !useConnectedTexture;
                        if (allowMerge)
                        {
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
                        
                        VertexPositionTexture[] targetBuffer;
                        int bufferIndex;
                        if (cell.RenderLayer == BlockRenderLayer.Water) { targetBuffer = waterBuffer; bufferIndex = 3; }
                        else if (cell.RenderLayer == BlockRenderLayer.Cutout) { targetBuffer = cutoutBuffer; bufferIndex = 1; }
                        else if (cell.RenderLayer == BlockRenderLayer.Blended) { targetBuffer = transparentBuffer; bufferIndex = 2; }
                        else { targetBuffer = opaqueBuffer; bufferIndex = 0; }

                        AddTiledQuad(
                            targetBuffer,
                            ref counts[bufferIndex],
                            originX,
                            originY,
                            originZ,
                            px,
                            py,
                            pz,
                            u,
                            v,
                            w,
                            h,
                            bs,
                            uv00,
                            uv10,
                            uv11,
                            uv01,
                            IsBackFace(cell.Face),
                            useConnectedTexture,
                            cell.BlockId,
                            cell.Face,
                            getBlock,
                            atlas.Texture.Width,
                            atlas.Texture.Height);

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

        AppendCustomModels(chunk, atlas, log, originX, originY, originZ, bs, opaqueBuffer, ref counts[0], cutoutBuffer, ref counts[1], transparentBuffer, ref counts[2]);

        var min = new Vector3(originX * bs, originY * bs, originZ * bs);
        var max = min + new Vector3(sizeX * bs, sizeY * bs, sizeZ * bs);

        var opaque = FinalizeBuffer(opaqueBuffer, counts[0]);
        var cutout = FinalizeBuffer(cutoutBuffer, counts[1]);
        var transparent = FinalizeBuffer(transparentBuffer, counts[2]);
        var water = FinalizeBuffer(waterBuffer, counts[3]);

        SimpleMemoryManager.ReturnVertices(opaqueBuffer);
        SimpleMemoryManager.ReturnVertices(cutoutBuffer);
        SimpleMemoryManager.ReturnVertices(transparentBuffer);
        SimpleMemoryManager.ReturnVertices(waterBuffer);

        return new ChunkMesh(chunk.Coord, opaque, cutout, transparent, water, new BoundingBox(min, max));
    }

    private static VertexPositionTexture[] FinalizeBuffer(VertexPositionTexture[] buffer, int count)
    {
        if (count == 0) return Array.Empty<VertexPositionTexture>();
        var result = new VertexPositionTexture[count];
        Array.Copy(buffer, result, count);
        return result;
    }

    private static void AppendCustomModels(
        VoxelChunkData chunk,
        CubeNetAtlas atlas,
        Logger log,
        int originX,
        int originY,
        int originZ,
        float blockSize,
        VertexPositionTexture[] opaque,
        ref int opaqueCount,
        VertexPositionTexture[] cutout,
        ref int cutoutCount,
        VertexPositionTexture[] transparent,
        ref int transparentCount)
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

                    if (def.RenderLayer == BlockRenderLayer.Cutout)
                    {
                        for (var i = 0; i < mesh.Length; i++)
                            cutout[cutoutCount++] = new VertexPositionTexture(mesh[i].Position + offset, mesh[i].TextureCoordinate);
                    }
                    else if (def.RenderLayer == BlockRenderLayer.Blended)
                    {
                        for (var i = 0; i < mesh.Length; i++)
                            transparent[transparentCount++] = new VertexPositionTexture(mesh[i].Position + offset, mesh[i].TextureCoordinate);
                    }
                    else
                    {
                        for (var i = 0; i < mesh.Length; i++)
                            opaque[opaqueCount++] = new VertexPositionTexture(mesh[i].Position + offset, mesh[i].TextureCoordinate);
                    }
                }
            }
        }
    }

    private static MaskCell BuildMaskCell(byte a, byte b, int axis)
    {
        var aDef = BlockRegistry.Get(a);
        var bDef = BlockRegistry.Get(b);

        var aFilled = a != BlockIds.Air && !aDef.HasCustomModel;
        var bFilled = b != BlockIds.Air && !bDef.HasCustomModel;

        if (!aFilled && !bFilled)
            return default;

        var aLayer = aFilled ? aDef.RenderLayer : BlockRenderLayer.Opaque;
        var bLayer = bFilled ? bDef.RenderLayer : BlockRenderLayer.Opaque;

        if (aFilled && !bFilled)
            return new MaskCell(a, FaceDirPos(axis), aLayer);

        if (!aFilled && bFilled)
            return new MaskCell(b, FaceDirNeg(axis), bLayer);

        if (a == b)
            return default;

        var aOpaque = aLayer == BlockRenderLayer.Opaque;
        var bOpaque = bLayer == BlockRenderLayer.Opaque;

        if (aOpaque && bOpaque)
            return default;

        // Keep opaque boundary faces owned by the opaque side so transparent
        // neighbors do not punch unintended holes into terrain.
        if (aOpaque && !bOpaque)
            return new MaskCell(a, FaceDirPos(axis), aLayer);

        if (!aOpaque && bOpaque)
            return new MaskCell(b, FaceDirNeg(axis), bLayer);

        if (aLayer == bLayer)
        {
            if (a <= b)
                return new MaskCell(a, FaceDirPos(axis), aLayer);
            return new MaskCell(b, FaceDirNeg(axis), bLayer);
        }

        var aPriority = GetNonOpaquePriority(aLayer);
        var bPriority = GetNonOpaquePriority(bLayer);
        if (aPriority > bPriority)
            return new MaskCell(a, FaceDirPos(axis), aLayer);
        if (bPriority > aPriority)
            return new MaskCell(b, FaceDirNeg(axis), bLayer);

        if (a <= b)
            return new MaskCell(a, FaceDirPos(axis), aLayer);
        return new MaskCell(b, FaceDirNeg(axis), bLayer);
    }

    private static int GetNonOpaquePriority(BlockRenderLayer layer) => layer switch
    {
        BlockRenderLayer.Cutout => 3,
        BlockRenderLayer.Blended => 2,
        BlockRenderLayer.Water => 1,
        _ => 0
    };

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

    private static void AddTiledQuad(
        VertexPositionTexture[] verts,
        ref int count,
        int originX,
        int originY,
        int originZ,
        int px,
        int py,
        int pz,
        int u,
        int v,
        int w,
        int h,
        float bs,
        Vector2 uv00,
        Vector2 uv10,
        Vector2 uv11,
        Vector2 uv01,
        bool flip,
        bool useConnectedTexture,
        byte connectedBlockId,
        FaceDirection face,
        Func<int, int, int, byte> getBlock,
        int atlasWidth,
        int atlasHeight)
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
                var tileUv00 = uv00;
                var tileUv10 = uv10;
                var tileUv11 = uv11;
                var tileUv01 = uv01;

                if (useConnectedTexture)
                {
                    var faceWorldX = originX + x;
                    var faceWorldY = originY + y;
                    var faceWorldZ = originZ + z;
                    GetFaceOwnerBlock(faceWorldX, faceWorldY, faceWorldZ, face, out var ownerX, out var ownerY, out var ownerZ);

                    var left = getBlock(ownerX - stepUx, ownerY - stepUy, ownerZ - stepUz);
                    var right = getBlock(ownerX + stepUx, ownerY + stepUy, ownerZ + stepUz);
                    var top = getBlock(ownerX - stepVx, ownerY - stepVy, ownerZ - stepVz);
                    var bottom = getBlock(ownerX + stepVx, ownerY + stepVy, ownerZ + stepVz);

                    var hideLeft = CanSeamlesslyConnect(connectedBlockId, left);
                    var hideRight = CanSeamlesslyConnect(connectedBlockId, right);
                    var hideTop = CanSeamlesslyConnect(connectedBlockId, top);
                    var hideBottom = CanSeamlesslyConnect(connectedBlockId, bottom);

                    ApplyConnectedTextureInsets(
                        ref tileUv00,
                        ref tileUv10,
                        ref tileUv11,
                        ref tileUv01,
                        hideLeft,
                        hideRight,
                        hideTop,
                        hideBottom,
                        1f / atlasWidth,
                        1f / atlasHeight);
                }

                AddQuad(verts, ref count, p0, p1, p2, p3, tileUv00, tileUv10, tileUv11, tileUv01, flip);
            }
        }
    }

    private static bool CanSeamlesslyConnect(byte currentId, byte neighborId)
    {
        if (neighborId == BlockIds.Air)
            return false;
        if (!BlockRegistry.UsesConnectedTexture(neighborId))
            return false;

        var current = BlockRegistry.Get(currentId);
        var neighbor = BlockRegistry.Get(neighborId);
        return current.RenderLayer == neighbor.RenderLayer;
    }

    private static void GetFaceOwnerBlock(int faceX, int faceY, int faceZ, FaceDirection face, out int blockX, out int blockY, out int blockZ)
    {
        blockX = faceX;
        blockY = faceY;
        blockZ = faceZ;
        switch (face)
        {
            case FaceDirection.PosX:
                blockX--;
                break;
            case FaceDirection.PosY:
                blockY--;
                break;
            case FaceDirection.PosZ:
                blockZ--;
                break;
        }
    }

    private static void ApplyConnectedTextureInsets(
        ref Vector2 uv00,
        ref Vector2 uv10,
        ref Vector2 uv11,
        ref Vector2 uv01,
        bool hideLeft,
        bool hideRight,
        bool hideTop,
        bool hideBottom,
        float uStep,
        float vStep)
    {
        if (hideLeft)
        {
            uv00 = StepUvTowards(uv00, uv10, uStep, vStep);
            uv01 = StepUvTowards(uv01, uv11, uStep, vStep);
        }

        if (hideRight)
        {
            uv10 = StepUvTowards(uv10, uv00, uStep, vStep);
            uv11 = StepUvTowards(uv11, uv01, uStep, vStep);
        }

        if (hideTop)
        {
            uv00 = StepUvTowards(uv00, uv01, uStep, vStep);
            uv10 = StepUvTowards(uv10, uv11, uStep, vStep);
        }

        if (hideBottom)
        {
            uv01 = StepUvTowards(uv01, uv00, uStep, vStep);
            uv11 = StepUvTowards(uv11, uv10, uStep, vStep);
        }
    }

    private static Vector2 StepUvTowards(Vector2 from, Vector2 to, float uStep, float vStep)
    {
        var dx = to.X - from.X;
        var dy = to.Y - from.Y;
        if (MathF.Abs(dx) >= MathF.Abs(dy))
        {
            if (dx == 0f)
                return from;
            return new Vector2(from.X + MathF.Sign(dx) * uStep, from.Y);
        }

        if (dy == 0f)
            return from;
        return new Vector2(from.X, from.Y + MathF.Sign(dy) * vStep);
    }

    private static void AddQuad(VertexPositionTexture[] verts, ref int count, Vector3 p0, Vector3 p1, Vector3 p2, Vector3 p3, Vector2 uv00, Vector2 uv10, Vector2 uv11, Vector2 uv01, bool flip)
    {
        if (!flip)
        {
            verts[count++] = new VertexPositionTexture(p0, uv00);
            verts[count++] = new VertexPositionTexture(p1, uv10);
            verts[count++] = new VertexPositionTexture(p2, uv11);
            verts[count++] = new VertexPositionTexture(p0, uv00);
            verts[count++] = new VertexPositionTexture(p2, uv11);
            verts[count++] = new VertexPositionTexture(p3, uv01);
        }
        else
        {
            verts[count++] = new VertexPositionTexture(p0, uv00);
            verts[count++] = new VertexPositionTexture(p2, uv11);
            verts[count++] = new VertexPositionTexture(p1, uv10);
            verts[count++] = new VertexPositionTexture(p0, uv00);
            verts[count++] = new VertexPositionTexture(p3, uv01);
            verts[count++] = new VertexPositionTexture(p2, uv11);
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
        public readonly BlockRenderLayer RenderLayer;

        public MaskCell(byte blockId, FaceDirection face, BlockRenderLayer renderLayer)
        {
            IsValid = true;
            BlockId = blockId;
            Face = face;
            RenderLayer = renderLayer;
        }

        public bool SameAs(MaskCell other)
        {
            return IsValid && other.IsValid && BlockId == other.BlockId && Face == other.Face && RenderLayer == other.RenderLayer;
        }
    }
}
