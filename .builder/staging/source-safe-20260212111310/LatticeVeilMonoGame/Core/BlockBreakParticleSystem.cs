using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace LatticeVeilMonoGame.Core;

public sealed class BlockBreakParticleSystem
{
    private readonly List<Particle> _particles = new();
    private readonly Random _rng = new();

    private int _spawnCount = 10;
    private int _maxParticles = 320;
    private float _initialSpeed = 2.2f;
    private float _lifeMin = 0.26f;
    private float _lifeMax = 0.52f;
    private float _gravity = 10.2f;

    public int ActiveCount => _particles.Count;
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
                _spawnCount = 0;
                _maxParticles = 0;
                _initialSpeed = 0f;
                _lifeMin = 0f;
                _lifeMax = 0f;
                _particles.Clear();
                break;
            case "LOW":
                _spawnCount = 6;
                _maxParticles = 160;
                _initialSpeed = 1.8f;
                _lifeMin = 0.20f;
                _lifeMax = 0.40f;
                break;
            case "HIGH":
                _spawnCount = 14;
                _maxParticles = 460;
                _initialSpeed = 2.6f;
                _lifeMin = 0.30f;
                _lifeMax = 0.62f;
                break;
            case "ULTRA":
                _spawnCount = 20;
                _maxParticles = 720;
                _initialSpeed = 3.1f;
                _lifeMin = 0.36f;
                _lifeMax = 0.78f;
                break;
            default:
                _spawnCount = 10;
                _maxParticles = 320;
                _initialSpeed = 2.2f;
                _lifeMin = 0.26f;
                _lifeMax = 0.52f;
                break;
        }
    }

    public void SpawnBlockBreak(int wx, int wy, int wz, byte blockId)
    {
        if (_spawnCount <= 0 || _maxParticles <= 0)
            return;

        var baseColor = ResolveParticleColor(blockId);
        var center = new Vector3(wx + 0.5f, wy + 0.5f, wz + 0.5f);
        for (var i = 0; i < _spawnCount; i++)
        {
            if (_particles.Count >= _maxParticles)
                _particles.RemoveAt(0);

            var jitter = new Vector3(
                NextRange(-0.38f, 0.38f),
                NextRange(-0.38f, 0.38f),
                NextRange(-0.38f, 0.38f));
            var velocity = new Vector3(
                NextRange(-1f, 1f),
                NextRange(0.15f, 1.35f),
                NextRange(-1f, 1f));
            if (velocity.LengthSquared() > 0.0001f)
                velocity.Normalize();
            velocity *= _initialSpeed * NextRange(0.65f, 1.2f);

            var life = NextRange(_lifeMin, _lifeMax);
            var size = NextRange(1.8f, 4.6f);
            var tint = NextRange(0.88f, 1.08f);
            var color = new Color(
                ClampToByte(baseColor.R * tint),
                ClampToByte(baseColor.G * tint),
                ClampToByte(baseColor.B * tint),
                (byte)220);

            _particles.Add(new Particle
            {
                Position = center + jitter,
                Velocity = velocity,
                Life = life,
                MaxLife = life,
                Size = size,
                Color = color
            });
        }
    }

    public void Update(float dt)
    {
        if (_particles.Count == 0)
            return;

        for (var i = _particles.Count - 1; i >= 0; i--)
        {
            var particle = _particles[i];
            particle.Life -= dt;
            if (particle.Life <= 0f)
            {
                _particles.RemoveAt(i);
                continue;
            }

            particle.Velocity.Y -= _gravity * dt;
            particle.Velocity *= (1f - MathF.Min(0.35f, dt * 0.9f));
            particle.Position += particle.Velocity * dt;
            _particles[i] = particle;
        }
    }

    public void Draw(SpriteBatch sb, Texture2D pixel, Matrix view, Matrix projection, Viewport viewport)
    {
        if (_particles.Count == 0)
            return;

        for (var i = 0; i < _particles.Count; i++)
        {
            var particle = _particles[i];
            var projected = viewport.Project(particle.Position, projection, view, Matrix.Identity);
            if (projected.Z <= 0f || projected.Z >= 1f)
                continue;

            var life01 = particle.MaxLife <= 0.0001f ? 0f : Math.Clamp(particle.Life / particle.MaxLife, 0f, 1f);
            var alpha = (byte)Math.Clamp(MathF.Round(220f * life01), 18f, 220f);
            var color = new Color(particle.Color.R, particle.Color.G, particle.Color.B, alpha);
            var size = MathF.Max(1.2f, particle.Size * (0.72f + life01 * 0.38f));
            var rect = new Rectangle(
                (int)MathF.Round(projected.X - size * 0.5f),
                (int)MathF.Round(projected.Y - size * 0.5f),
                Math.Max(1, (int)MathF.Round(size)),
                Math.Max(1, (int)MathF.Round(size)));
            sb.Draw(pixel, rect, color);
        }
    }

    private static Color ResolveParticleColor(byte blockId)
    {
        return blockId switch
        {
            BlockIds.Grass => new Color(90, 158, 78),
            BlockIds.Dirt => new Color(126, 96, 66),
            BlockIds.Stone => new Color(135, 136, 142),
            BlockIds.Sand => new Color(218, 202, 156),
            BlockIds.Gravel => new Color(136, 136, 132),
            BlockIds.Water => new Color(78, 130, 205),
            BlockIds.Wood => new Color(118, 84, 52),
            BlockIds.Leaves => new Color(82, 136, 72),
            _ => new Color(182, 182, 182)
        };
    }

    private float NextRange(float min, float max)
    {
        return min + (max - min) * (float)_rng.NextDouble();
    }

    private static byte ClampToByte(float value)
    {
        return (byte)Math.Clamp(MathF.Round(value), 0f, 255f);
    }

    private static string NormalizePreset(string? value)
    {
        var preset = string.IsNullOrWhiteSpace(value) ? "MEDIUM" : value.Trim().ToUpperInvariant();
        return preset is "OFF" or "LOW" or "MEDIUM" or "HIGH" or "ULTRA" ? preset : "MEDIUM";
    }

    private struct Particle
    {
        public Vector3 Position;
        public Vector3 Velocity;
        public float Life;
        public float MaxLife;
        public float Size;
        public Color Color;
    }
}
