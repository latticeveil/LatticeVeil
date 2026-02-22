using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace LatticeVeilMonoGame.Core;

public sealed class BlockIconCache : IDisposable
{
    private readonly GraphicsDevice _device;
    private readonly BasicEffect _effect;
    private readonly Dictionary<IconKey, Texture2D> _icons = new();
    private readonly CubeNetAtlas _atlas;
    private readonly BlockModel _model;
    private readonly Logger? _log;

    public BlockIconCache(GraphicsDevice device, CubeNetAtlas atlas, BlockModel model, Logger? log)
    {
        _device = device;
        _atlas = atlas;
        _model = model;
        _log = log;
        _effect = new BasicEffect(device)
        {
            VertexColorEnabled = false,
            TextureEnabled = true,
            LightingEnabled = false
        };
    }

    public void Warm(BlockId id, int size, BlockModelContext context)
    {
        if (id == BlockId.Air)
            return;

        var clamped = Math.Max(8, size);
        var key = new IconKey(id, clamped, context);
        if (_icons.ContainsKey(key))
            return;

        GetOrCreate(id, clamped, context);
    }

    public bool TryGet(BlockId id, int size, BlockModelContext context, out Texture2D? icon)
    {
        var clamped = Math.Max(8, size);
        var key = new IconKey(id, clamped, context);
        return _icons.TryGetValue(key, out icon);
    }

    public Texture2D? GetOrCreate(BlockId id, int size, BlockModelContext context)
    {
        if (id == BlockId.Air)
            return null;

        var clamped = Math.Max(8, size);
        var key = new IconKey(id, clamped, context);
        if (_icons.TryGetValue(key, out var existing))
            return existing;

        try
        {
            var target = RenderIcon(id, clamped, context);
            _icons[key] = target;
            return target;
        }
        catch (Exception ex)
        {
            _log?.Warn($"Block icon render failed for {id}: {ex.Message}");
            return null;
        }
    }

    public void Dispose()
    {
        foreach (var tex in _icons.Values)
            tex.Dispose();
        _icons.Clear();
        _effect.Dispose();
    }

    private RenderTarget2D RenderIcon(BlockId id, int size, BlockModelContext context)
    {
        var target = new RenderTarget2D(_device, size, size, false, SurfaceFormat.Color, DepthFormat.Depth24);
        var prevTargets = _device.GetRenderTargets();
        var prevViewport = _device.Viewport;
        var prevBlend = _device.BlendState;
        var prevDepth = _device.DepthStencilState;
        var prevRaster = _device.RasterizerState;
        var prevSampler = _device.SamplerStates[0];

        _device.SetRenderTarget(target);
        _device.Viewport = new Viewport(0, 0, size, size);
        _device.Clear(ClearOptions.Target | ClearOptions.DepthBuffer, Color.Transparent, 1f, 0);
        _device.DepthStencilState = DepthStencilState.Default;
        _device.RasterizerState = RasterizerState.CullNone;
        _device.BlendState = BlendState.AlphaBlend;
        _device.SamplerStates[0] = SamplerState.PointClamp;

        var model = _model;
        if (BlockRegistry.Get(id).HasCustomModel && _log != null)
            model = BlockModel.GetModel(id, _log);

        var mesh = model.BuildMesh(_atlas, id);
        var view = Matrix.CreateLookAt(new Vector3(0f, 0f, 2.2f), Vector3.Zero, Vector3.Up);
        var projection = Matrix.CreatePerspectiveFieldOfView(MathHelper.ToRadians(30f), 1f, 0.1f, 10f);

        if (!model.TryGetDisplayTransform(context, out var display))
        {
            display = Matrix.CreateScale(0.5f) *
                      Matrix.CreateRotationX(MathHelper.ToRadians(25f)) *
                      Matrix.CreateRotationY(MathHelper.ToRadians(-40f));
        }

        if (context == BlockModelContext.Gui)
            display = Matrix.CreateScale(0.9f) * display;

        _effect.World = display;
        _effect.View = view;
        _effect.Projection = projection;
        _effect.Texture = _atlas.Texture;

        if (mesh.Length >= 3)
        {
            foreach (var pass in _effect.CurrentTechnique.Passes)
            {
                pass.Apply();
                _device.DrawUserPrimitives(PrimitiveType.TriangleList, mesh, 0, mesh.Length / 3);
            }
        }

        _device.SetRenderTargets(prevTargets);
        _device.Viewport = prevViewport;
        _device.BlendState = prevBlend;
        _device.DepthStencilState = prevDepth;
        _device.RasterizerState = prevRaster;
        _device.SamplerStates[0] = prevSampler;

        return target;
    }

    private readonly struct IconKey : IEquatable<IconKey>
    {
        public readonly BlockId Id;
        public readonly int Size;
        public readonly BlockModelContext Context;

        public IconKey(BlockId id, int size, BlockModelContext context)
        {
            Id = id;
            Size = size;
            Context = context;
        }

        public bool Equals(IconKey other) => Id == other.Id && Size == other.Size && Context == other.Context;
        public override bool Equals(object? obj) => obj is IconKey other && Equals(other);
        public override int GetHashCode() => HashCode.Combine((int)Id, Size, (int)Context);
    }
}
