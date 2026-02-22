using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace LatticeVeilMonoGame.Core;

public sealed class ChunkMesh
{
    public ChunkCoord Coord { get; }
    public VertexPositionTexture[] OpaqueVertices { get; }
    public VertexPositionTexture[] TransparentVertices { get; }
    public VertexPositionTexture[] WaterVertices { get; }
    public BoundingBox Bounds { get; }

    public int OpaqueTriangles => OpaqueVertices.Length / 3;
    public int TransparentTriangles => TransparentVertices.Length / 3;
    public int WaterTriangles => WaterVertices.Length / 3;

    public static ChunkMesh Empty { get; } = new ChunkMesh(
        new ChunkCoord(0, 0, 0),
        Array.Empty<VertexPositionTexture>(),
        Array.Empty<VertexPositionTexture>(),
        Array.Empty<VertexPositionTexture>(),
        new BoundingBox());

    public ChunkMesh(ChunkCoord coord, VertexPositionTexture[] opaque, VertexPositionTexture[] transparent, VertexPositionTexture[] water, BoundingBox bounds)
    {
        Coord = coord;
        OpaqueVertices = opaque;
        TransparentVertices = transparent;
        WaterVertices = water;
        Bounds = bounds;
    }
}
