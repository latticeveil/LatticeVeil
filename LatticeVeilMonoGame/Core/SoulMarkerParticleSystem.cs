using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace LatticeVeilMonoGame.Core;

public sealed class SoulMarkerParticleSystem
{
    private readonly List<Particle> _particles = new();
    private readonly Random _rng = new();
    private int _ambientBurstCount = 2;
    private int _releaseBurstCount = 10;
    private int _maxParticles = 120;
    private float _ambientSpawnIntervalSeconds = 0.30f;
    private float _ambientTimer;
    private bool _particlesEnabled = true;

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
                _particlesEnabled = false;
                _ambientBurstCount = 0;
                _releaseBurstCount = 0;
                _maxParticles = 0;
                _ambientSpawnIntervalSeconds = float.PositiveInfinity;
                _particles.Clear();
                break;
            case "LOW":
                _particlesEnabled = true;
                _ambientBurstCount = 1;
                _releaseBurstCount = 5;
                _maxParticles = 48;
                _ambientSpawnIntervalSeconds = 0.60f;
                break;
            case "HIGH":
                _particlesEnabled = true;
                _ambientBurstCount = 3;
                _releaseBurstCount = 12;
                _maxParticles = 140;
                _ambientSpawnIntervalSeconds = 0.24f;
                break;
            case "ULTRA":
                _particlesEnabled = true;
                _ambientBurstCount = 4;
                _releaseBurstCount = 16;
                _maxParticles = 200;
                _ambientSpawnIntervalSeconds = 0.18f;
                break;
            default:
                _particlesEnabled = true;
                _ambientBurstCount = 2;
                _releaseBurstCount = 10;
                _maxParticles = 120;
                _ambientSpawnIntervalSeconds = 0.30f;
                break;
        }
    }

    public void SpawnRelease(Vector3 position)
    {
        if (!_particlesEnabled || _releaseBurstCount <= 0 || _maxParticles <= 0)
            return;

        TrimOverflow(_releaseBurstCount);
        for (var i = 0; i < _releaseBurstCount; i++)
        {
            var velocity = new Vector3(
                NextRange(-0.18f, 0.18f),
                NextRange(0.36f, 0.92f),
                NextRange(-0.18f, 0.18f));
            var life = NextRange(0.75f, 1.45f);
            var size = NextRange(2.0f, 4.6f);
            _particles.Add(new Particle
            {
                Position = position + new Vector3(NextRange(-0.15f, 0.15f), NextRange(0.25f, 1.55f), NextRange(-0.15f, 0.15f)),
                Velocity = velocity,
                Life = life,
                MaxLife = life,
                Size = size,
                Drift = NextRange(0.4f, 0.9f),
                Color = NextSoulColor()
            });
        }
    }

    public void Update(float dt, bool anchorActive, Vector3 anchorPosition)
    {
        if (dt <= 0f)
            return;

        if (_particlesEnabled && anchorActive && _ambientBurstCount > 0)
        {
            _ambientTimer += dt;
            while (_ambientTimer >= _ambientSpawnIntervalSeconds)
            {
                _ambientTimer -= _ambientSpawnIntervalSeconds;
                SpawnAmbient(anchorPosition);
            }
        }
        else if (!anchorActive)
        {
            _ambientTimer = 0f;
        }

        for (var i = _particles.Count - 1; i >= 0; i--)
        {
            var particle = _particles[i];
            particle.Life -= dt;
            if (particle.Life <= 0f)
            {
                _particles.RemoveAt(i);
                continue;
            }

            particle.Velocity.Y += particle.Drift * dt * 0.18f;
            particle.Velocity.X *= (1f - MathF.Min(0.28f, dt * 0.65f));
            particle.Velocity.Z *= (1f - MathF.Min(0.28f, dt * 0.65f));
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
            var alpha = (byte)Math.Clamp(MathF.Round(180f * life01), 12f, 180f);
            var color = new Color(particle.Color.R, particle.Color.G, particle.Color.B, alpha);
            var size = MathF.Max(1.0f, particle.Size * (0.62f + life01 * 0.45f));
            var rect = new Rectangle(
                (int)MathF.Round(projected.X - size * 0.5f),
                (int)MathF.Round(projected.Y - size * 0.5f),
                Math.Max(1, (int)MathF.Round(size)),
                Math.Max(1, (int)MathF.Round(size)));
            sb.Draw(pixel, rect, color);
        }
    }

    private void SpawnAmbient(Vector3 anchorPosition)
    {
        if (!_particlesEnabled || _ambientBurstCount <= 0)
            return;

        TrimOverflow(_ambientBurstCount);
        for (var i = 0; i < _ambientBurstCount; i++)
        {
            var life = NextRange(1.10f, 2.20f);
            _particles.Add(new Particle
            {
                Position = anchorPosition + new Vector3(NextRange(-0.32f, 0.32f), NextRange(0.45f, 1.95f), NextRange(-0.32f, 0.32f)),
                Velocity = new Vector3(NextRange(-0.05f, 0.05f), NextRange(0.08f, 0.24f), NextRange(-0.05f, 0.05f)),
                Life = life,
                MaxLife = life,
                Size = NextRange(1.4f, 3.4f),
                Drift = NextRange(0.22f, 0.55f),
                Color = NextSoulColor()
            });
        }
    }

    private void TrimOverflow(int incoming)
    {
        if (_maxParticles <= 0)
            return;

        var overflow = (_particles.Count + incoming) - _maxParticles;
        if (overflow > 0)
            _particles.RemoveRange(0, Math.Min(overflow, _particles.Count));
    }

    private Color NextSoulColor()
    {
        var tint = NextRange(0.88f, 1.08f);
        return new Color(
            ClampToByte(216f * tint),
            ClampToByte(227f * tint),
            ClampToByte(234f * tint),
            (byte)180);
    }

    private float NextRange(float min, float max)
        => min + (max - min) * (float)_rng.NextDouble();

    private static byte ClampToByte(float value)
        => (byte)Math.Clamp(MathF.Round(value), 0f, 255f);

    private static string NormalizePreset(string? value)
    {
        var preset = string.IsNullOrWhiteSpace(value) ? "MEDIUM" : value.Trim().ToUpperInvariant();
        return preset is "AUTO" or "OFF" or "LOW" or "MEDIUM" or "HIGH" or "ULTRA" ? preset : "MEDIUM";
    }

    private struct Particle
    {
        public Vector3 Position;
        public Vector3 Velocity;
        public float Life;
        public float MaxLife;
        public float Size;
        public float Drift;
        public Color Color;
    }
}
