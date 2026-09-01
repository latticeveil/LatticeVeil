using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace LatticeVeilMonoGame.Core;

public sealed class TorchFlameParticleSystem : IDisposable
{
    private int _maxVisibleFlames = 96;
    private bool _enabled = true;
    private BasicEffect? _effect;
    private readonly List<VertexPositionColor> _vertices = new(96 * 18);

    public string EffectivePreset { get; private set; } = "MEDIUM";

    public void ApplyPreset(string? particlePreset, string? qualityPreset)
    {
        var preset = NormalizePreset(particlePreset);
        if (preset == "AUTO")
            preset = NormalizePreset(qualityPreset);

        EffectivePreset = preset;
        switch (preset)
        {
            case "OFF":
                _enabled = false;
                _maxVisibleFlames = 0;
                break;
            case "LOW":
                _enabled = true;
                _maxVisibleFlames = 32;
                break;
            case "HIGH":
                _enabled = true;
                _maxVisibleFlames = 160;
                break;
            case "ULTRA":
                _enabled = true;
                _maxVisibleFlames = 240;
                break;
            default:
                _enabled = true;
                _maxVisibleFlames = 96;
                break;
        }
    }

    public void Draw(
        GraphicsDevice device,
        IReadOnlyList<Vector3> flameAnchors,
        Vector3 cameraPosition,
        Matrix view,
        Matrix projection,
        float worldTimeSeconds)
    {
        if (!_enabled || _maxVisibleFlames <= 0 || flameAnchors.Count == 0)
            return;

        _effect ??= new BasicEffect(device)
        {
            VertexColorEnabled = true,
            LightingEnabled = false,
            FogEnabled = false
        };

        var inverseView = Matrix.Invert(view);
        var right = Vector3.Normalize(new Vector3(inverseView.M11, inverseView.M12, inverseView.M13));
        var up = Vector3.Normalize(new Vector3(inverseView.M21, inverseView.M22, inverseView.M23));

        _vertices.Clear();
        var drawn = 0;
        for (var i = 0; i < flameAnchors.Count && drawn < _maxVisibleFlames; i++)
        {
            var anchor = flameAnchors[i];
            var distanceSq = Vector3.DistanceSquared(cameraPosition, anchor);
            if (distanceSq > 80f * 80f)
                continue;

            AddSingleFlame(anchor, right, up, worldTimeSeconds, i);
            drawn++;
        }

        if (_vertices.Count == 0)
            return;

        var previousBlend = device.BlendState;
        var previousDepth = device.DepthStencilState;
        var previousRasterizer = device.RasterizerState;
        device.BlendState = BlendState.AlphaBlend;
        device.DepthStencilState = DepthStencilState.DepthRead;
        device.RasterizerState = RasterizerState.CullNone;

        _effect.View = view;
        _effect.Projection = projection;
        _effect.World = Matrix.Identity;
        foreach (var pass in _effect.CurrentTechnique.Passes)
        {
            pass.Apply();
            device.DrawUserPrimitives(PrimitiveType.TriangleList, _vertices.ToArray(), 0, _vertices.Count / 3);
        }

        device.BlendState = previousBlend;
        device.DepthStencilState = previousDepth;
        device.RasterizerState = previousRasterizer;
    }

    private void AddSingleFlame(Vector3 anchor, Vector3 right, Vector3 up, float time, int index)
    {
        var phase = time * 8.5f + index * 1.719f;
        var flicker = 0.88f + ((MathF.Sin(phase) + MathF.Sin(phase * 1.73f)) * 0.07f);
        var side = right * (MathF.Sin(phase * 1.31f) * 0.018f);
        var basePos = anchor + side;

        // Reduced flame sizes by 25% (0.75x multiplier)
        AddBillboard(basePos + up * 0.05f, right, up, 0.255f, 0.345f * flicker, new Color(255, 112, 24, 80));
        AddBillboard(basePos + up * 0.03f, right, up, 0.12f, 0.195f * flicker, new Color(235, 58, 10, 220));
        AddBillboard(basePos + up * 0.09f, right, up, 0.09f, 0.1875f * flicker, new Color(255, 126, 20, 235));
        AddBillboard(basePos + up * 0.12f, right, up, 0.0525f, 0.135f * flicker, new Color(255, 205, 70, 245));
    }

    private void AddBillboard(Vector3 center, Vector3 right, Vector3 up, float width, float height, Color color)
    {
        var halfW = width * 0.5f;
        var halfH = height * 0.5f;
        var p0 = center - right * halfW - up * halfH;
        var p1 = center + right * halfW - up * halfH;
        var p2 = center + right * halfW + up * halfH;
        var p3 = center - right * halfW + up * halfH;

        _vertices.Add(new VertexPositionColor(p0, color));
        _vertices.Add(new VertexPositionColor(p1, color));
        _vertices.Add(new VertexPositionColor(p2, color));
        _vertices.Add(new VertexPositionColor(p0, color));
        _vertices.Add(new VertexPositionColor(p2, color));
        _vertices.Add(new VertexPositionColor(p3, color));
    }

    private static string NormalizePreset(string? value)
    {
        var preset = string.IsNullOrWhiteSpace(value) ? "MEDIUM" : value.Trim().ToUpperInvariant();
        return preset is "AUTO" or "OFF" or "LOW" or "MEDIUM" or "HIGH" or "ULTRA" ? preset : "MEDIUM";
    }

    public void Dispose()
    {
        _effect?.Dispose();
        _effect = null;
    }
}
