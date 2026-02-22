using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace LatticeVeilMonoGame.Core;

public sealed class FirstPersonHandRenderer
{
    private readonly GraphicsDevice _device;
    private readonly BasicEffect _handEffect;
    private readonly BasicEffect _blockEffect;
    private readonly VertexPositionColor[] _handVerts;
    private VertexPositionTexture[]? _blockVerts;
    private BlockId _cachedBlock = BlockId.Air;
    private BlockModel? _cachedModel;
    public bool IsHandMeshValid => _handVerts.Length % 3 == 0;
    public bool IsHeldBlockMeshValid => _blockVerts == null || _blockVerts.Length % 3 == 0;

    public FirstPersonHandRenderer(GraphicsDevice device)
    {
        _device = device;
        _handEffect = new BasicEffect(device)
        {
            VertexColorEnabled = true,
            TextureEnabled = false,
            LightingEnabled = false
        };
        _blockEffect = new BasicEffect(device)
        {
            VertexColorEnabled = false,
            TextureEnabled = true,
            LightingEnabled = false
        };

        _handVerts = BuildHandMesh();
    }

    public void Draw(Matrix view, Matrix projection, Vector3 camPos, Vector3 forward, Vector3 up, CubeNetAtlas atlas, BlockId heldBlock, BlockModel? model)
    {
        var right = Vector3.Normalize(Vector3.Cross(forward, up));
        var basis = Matrix.CreateWorld(Vector3.Zero, forward, up);

        var handPos = camPos + forward * (0.75f * Scale.BlockSize) + right * (0.35f * Scale.BlockSize) - up * (0.32f * Scale.BlockSize);
        var handWorld = Matrix.CreateScale(Scale.HandScale * Scale.BlockSize) * basis * Matrix.CreateTranslation(handPos);

        _handEffect.View = view;
        _handEffect.Projection = projection;
        _handEffect.World = handWorld;

        foreach (var pass in _handEffect.CurrentTechnique.Passes)
        {
            pass.Apply();
            _device.DrawUserPrimitives(PrimitiveType.TriangleList, _handVerts, 0, _handVerts.Length / 3);
        }

        if (heldBlock == BlockId.Air)
            return;

        if (_blockVerts == null || _cachedBlock != heldBlock || !ReferenceEquals(_cachedModel, model))
        {
            _cachedBlock = heldBlock;
            _cachedModel = model;
            _blockVerts = model?.BuildMesh(atlas, heldBlock) ?? BlockModel.BuildCubeMesh(atlas, heldBlock);
        }

        var blockPos = camPos + forward * (0.8f * Scale.BlockSize) + right * (0.12f * Scale.BlockSize) - up * (0.2f * Scale.BlockSize);
        Matrix display;
        if (model != null && model.TryGetDisplayTransform(BlockModelContext.FirstPersonRightHand, out var displayTransform))
            display = Matrix.CreateScale(0.9f) * displayTransform;
        else
            display = Matrix.CreateScale(Scale.HeldBlockScale * Scale.BlockSize);

        var blockWorld = display * basis * Matrix.CreateTranslation(blockPos);

        _blockEffect.View = view;
        _blockEffect.Projection = projection;
        _blockEffect.World = blockWorld;
        _blockEffect.Texture = atlas.Texture;

        foreach (var pass in _blockEffect.CurrentTechnique.Passes)
        {
            pass.Apply();
            _device.DrawUserPrimitives(PrimitiveType.TriangleList, _blockVerts, 0, _blockVerts.Length / 3);
        }
    }

    private static VertexPositionColor[] BuildHandMesh()
    {
        var verts = new List<VertexPositionColor>();
        var skin = new Color(220, 190, 160);
        AddBox(verts, new Vector3(-0.2f, -0.2f, 0f), new Vector3(0.2f, 0.2f, 0.6f), skin);
        AddBox(verts, new Vector3(-0.25f, -0.25f, 0.6f), new Vector3(0.25f, 0.25f, 0.9f), skin);
        return verts.ToArray();
    }

    private static void AddFace(List<VertexPositionColor> verts, Vector3 p0, Vector3 p1, Vector3 p2, Vector3 p3, Color color)
    {
        verts.Add(new VertexPositionColor(p0, color));
        verts.Add(new VertexPositionColor(p1, color));
        verts.Add(new VertexPositionColor(p2, color));
        verts.Add(new VertexPositionColor(p0, color));
        verts.Add(new VertexPositionColor(p2, color));
        verts.Add(new VertexPositionColor(p3, color));
    }

    private static void AddBox(List<VertexPositionColor> verts, Vector3 min, Vector3 max, Color color)
    {
        var p000 = new Vector3(min.X, min.Y, min.Z);
        var p100 = new Vector3(max.X, min.Y, min.Z);
        var p010 = new Vector3(min.X, max.Y, min.Z);
        var p110 = new Vector3(max.X, max.Y, min.Z);
        var p001 = new Vector3(min.X, min.Y, max.Z);
        var p101 = new Vector3(max.X, min.Y, max.Z);
        var p011 = new Vector3(min.X, max.Y, max.Z);
        var p111 = new Vector3(max.X, max.Y, max.Z);

        AddQuad(verts, p101, p001, p011, p111, color); // front
        AddQuad(verts, p000, p100, p110, p010, color); // back
        AddQuad(verts, p100, p101, p111, p110, color); // right
        AddQuad(verts, p001, p000, p010, p011, color); // left
        AddQuad(verts, p110, p111, p011, p010, color); // top
        AddQuad(verts, p001, p101, p100, p000, color); // bottom
    }

    private static void AddQuad(List<VertexPositionColor> verts, Vector3 p0, Vector3 p1, Vector3 p2, Vector3 p3, Color color)
    {
        AddFace(verts, p0, p1, p2, p3, color);
    }
}
