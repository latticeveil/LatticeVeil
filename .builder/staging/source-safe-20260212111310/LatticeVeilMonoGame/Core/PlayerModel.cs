using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace LatticeVeilMonoGame.Core;

// Stub humanoid model for future multiplayer use. Not wired into gameplay.
public sealed class PlayerModel
{
    private readonly GraphicsDevice _device;
    private readonly BasicEffect _effect;
    private readonly VertexPositionColor[] _verts;

    public Vector3 Position { get; set; }
    public float Yaw { get; set; }
    public Color Color { get; set; } = new(200, 200, 200);
    public bool IsFlying { get; set; }

    public PlayerModel(GraphicsDevice device)
    {
        _device = device;
        _effect = new BasicEffect(device)
        {
            VertexColorEnabled = true,
            LightingEnabled = false,
            TextureEnabled = false
        };

        _verts = BuildHumanoidMesh();
    }

    public void Render(Matrix view, Matrix projection)
    {
        _effect.View = view;
        _effect.Projection = projection;
        var world = Matrix.CreateRotationY(Yaw);
        if (IsFlying)
            world *= Matrix.CreateRotationX(-0.5f); // Tilt forward
        world *= Matrix.CreateTranslation(Position);
        _effect.World = world;

        foreach (var pass in _effect.CurrentTechnique.Passes)
        {
            pass.Apply();
            _device.DrawUserPrimitives(PrimitiveType.TriangleList, _verts, 0, _verts.Length / 3);
        }
    }

    private VertexPositionColor[] BuildHumanoidMesh()
    {
        var verts = new List<VertexPositionColor>();
        var headColor = new Color(220, 190, 160);
        var bodyColor = Color;

        // Head
        AddBox(verts, new Vector3(-0.2f, 1.6f, -0.2f), new Vector3(0.2f, 2.0f, 0.2f), headColor);
        // Torso
        AddBox(verts, new Vector3(-0.25f, 0.9f, -0.15f), new Vector3(0.25f, 1.6f, 0.15f), bodyColor);
        // Arms
        AddBox(verts, new Vector3(-0.45f, 0.95f, -0.12f), new Vector3(-0.25f, 1.55f, 0.12f), bodyColor);
        AddBox(verts, new Vector3(0.25f, 0.95f, -0.12f), new Vector3(0.45f, 1.55f, 0.12f), bodyColor);
        // Legs
        AddBox(verts, new Vector3(-0.2f, 0.0f, -0.12f), new Vector3(0.0f, 0.9f, 0.12f), bodyColor);
        AddBox(verts, new Vector3(0.0f, 0.0f, -0.12f), new Vector3(0.2f, 0.9f, 0.12f), bodyColor);

        return verts.ToArray();
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
        verts.Add(new VertexPositionColor(p0, color));
        verts.Add(new VertexPositionColor(p1, color));
        verts.Add(new VertexPositionColor(p2, color));
        verts.Add(new VertexPositionColor(p0, color));
        verts.Add(new VertexPositionColor(p2, color));
        verts.Add(new VertexPositionColor(p3, color));
    }
}
