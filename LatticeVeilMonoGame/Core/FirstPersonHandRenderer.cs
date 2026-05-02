using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace LatticeVeilMonoGame.Core;

public sealed class FirstPersonHandRenderer : IDisposable
{
    private const float SkinHotReloadIntervalSeconds = 0.8f;
    private const float ArmSwingXPosScale = -0.10f;
    private const float ArmSwingYPosScale = 0.14f;
    private const float ArmSwingZPosScale = -0.14f;
    private const float ArmSwingYawAmountDeg = 22f;
    private const float ArmSwingRollAmountDeg = -8f;
    private const float ArmPosX = 0.50f;
    private const float ArmPosY = -0.62f;
    private const float ArmPosZ = -0.72f;
    private const float ArmEquipYOffsetScale = -0.60f;
    private const float ArmPreswingYawDeg = 45f;
    private const float ArmPostSwingYawDeg = -45f;
    private const float ArmSwingPitchDeg = -42f;
    private const float HeldBlockRightOffset = 0.30f;
    private const float DamageKickYawDeg = -12f;
    private const float DamageKickPitchDeg = 15f;
    private const float DamageKickRollDeg = 11f;
    private const float HandLayerSurfaceOffset = 0.006f;
    private const float HandLayerVoxelDepth = 0.055f;

    private readonly GraphicsDevice _device;
    private readonly BasicEffect _handEffect;
    private readonly AlphaTestEffect _handOverlayEffect;
    private readonly BasicEffect _blockEffect;
    private readonly AlphaTestEffect _blockCutoutEffect;
    private readonly VertexPositionTexture[] _handVerts;
    private VertexPositionTexture[] _handOverlayVerts;
    private Texture2D _skinTexture;
    private bool _ownsSkinTexture;
    private string? _skinFilePath;
    private DateTime _skinFileWriteUtc;
    private float _skinHotReloadTimer;
    private float _lastSkinSampleTime = float.NaN;
    private float _holdPoseBlend;
    private VertexPositionTexture[]? _blockVerts;
    private BlockId _cachedBlock = BlockId.Air;
    private BlockModel? _cachedModel;
    private bool _disposed;

    public bool IsHandMeshValid => _handVerts.Length % 3 == 0;
    public bool IsHeldBlockMeshValid => _blockVerts == null || _blockVerts.Length % 3 == 0;

    public FirstPersonHandRenderer(GraphicsDevice device, AssetLoader? assets = null)
    {
        _device = device;
        _handEffect = new BasicEffect(device)
        {
            VertexColorEnabled = false,
            TextureEnabled = true,
            LightingEnabled = false
        };
        _handOverlayEffect = new AlphaTestEffect(device)
        {
            VertexColorEnabled = false,
            AlphaFunction = CompareFunction.Greater,
            ReferenceAlpha = 8
        };
        _blockEffect = new BasicEffect(device)
        {
            VertexColorEnabled = false,
            TextureEnabled = true,
            LightingEnabled = false
        };
        _blockCutoutEffect = new AlphaTestEffect(device)
        {
            VertexColorEnabled = false,
            AlphaFunction = CompareFunction.Greater,
            ReferenceAlpha = 128
        };

        if (PlayerSkinSource.TryLoadTexture(device, out var skinTexture, out var skinPath, out var writeUtc))
        {
            _skinTexture = skinTexture;
            _ownsSkinTexture = true;
            _skinFilePath = skinPath;
            _skinFileWriteUtc = writeUtc;
        }
        else
        {
            _skinTexture = DefaultPlayerSkinFactory.Create(device);
            _ownsSkinTexture = true;
        }

        _handVerts = BuildHandMesh(overlay: false);
        _handOverlayVerts = Array.Empty<VertexPositionTexture>();
    }

    public void Draw(
        Matrix view,
        Matrix projection,
        Vector3 camPos,
        Vector3 forward,
        Vector3 up,
        CubeNetAtlas atlas,
        BlockId heldBlock,
        BlockModel? model,
        float worldTimeSeconds,
        float moveAmount,
        bool isFlying,
        bool isGrounded,
        float verticalVelocity,
        float actionSwingProgress,
        float damageKickProgress,
        Texture2D? skinTextureOverride = null)
    {
        _ = isGrounded;
        _ = verticalVelocity;
        var skinDt = 1f / 60f;
        if (!float.IsNaN(_lastSkinSampleTime))
            skinDt = Math.Max(0f, worldTimeSeconds - _lastSkinSampleTime);
        _lastSkinSampleTime = worldTimeSeconds;

        _skinHotReloadTimer -= skinDt;
        if (skinTextureOverride == null && _skinHotReloadTimer <= 0f)
        {
            _skinHotReloadTimer = SkinHotReloadIntervalSeconds;
            TryHotReloadSkinFromFile();
        }

        var skinTexture = skinTextureOverride ?? _skinTexture;
        var right = Vector3.Normalize(Vector3.Cross(forward, up));
        var basis = Matrix.CreateWorld(Vector3.Zero, forward, up);

        var hasHeldBlock = heldBlock != BlockId.Air;
        var holdTarget = hasHeldBlock ? 1f : 0f;
        var blendStep = Math.Clamp(skinDt * 10f, 0f, 1f);
        _holdPoseBlend = MathHelper.Lerp(_holdPoseBlend, holdTarget, blendStep);
        var move = Math.Clamp(moveAmount, 0f, 1.25f);
        var walkCycle = worldTimeSeconds * (6.2f + (move * 3.1f));
        var walkBobY = isFlying ? 0f : MathF.Sin(walkCycle * 2f) * (0.020f * move);
        var walkBobX = isFlying ? 0f : MathF.Cos(walkCycle) * (0.010f * move);

        var handAlpha = 1f;
        var swing = Math.Clamp(actionSwingProgress, 0f, 1f);
        var damageKick = MathF.Sin(Math.Clamp(damageKickProgress, 0f, 1f) * MathF.PI * 0.5f);
        var swingRoot = MathF.Sqrt(swing);
        var swingSin = MathF.Sin(swing * MathF.PI);
        var swingSquaredSin = MathF.Sin(swing * swing * MathF.PI);
        var swingRootSin = MathF.Sin(swingRoot * MathF.PI);
        var swingRootDoubleSin = MathF.Sin(swingRoot * MathF.PI * 2f);
        var damageKickPosX = -0.07f * damageKick;
        var damageKickPosY = 0.08f * damageKick;
        var damageKickPosZ = 0.12f * damageKick;
        if (!hasHeldBlock)
        {
            var equipProgress = _holdPoseBlend;

            var local = Matrix.Identity;
            local *= Matrix.CreateTranslation(
                ArmSwingXPosScale * swingRootSin + damageKickPosX,
                ArmSwingYPosScale * swingRootDoubleSin + (isFlying ? MathF.Sin(worldTimeSeconds * 2.8f) * 0.02f : 0f) + damageKickPosY,
                ArmSwingZPosScale * swingSin + damageKickPosZ);
            local *= Matrix.CreateTranslation(walkBobX, walkBobY, 0f);
            local *= Matrix.CreateTranslation(
                ArmPosX,
                ArmPosY + (ArmEquipYOffsetScale * equipProgress),
                ArmPosZ);
            local *= Matrix.CreateRotationY(MathHelper.ToRadians(ArmPreswingYawDeg));
            local *= Matrix.CreateRotationY(MathHelper.ToRadians(ArmSwingYawAmountDeg * swingSquaredSin));
            local *= Matrix.CreateRotationZ(MathHelper.ToRadians(ArmSwingRollAmountDeg * swingRootSin));
            local *= Matrix.CreateRotationX(MathHelper.ToRadians(ArmSwingPitchDeg * swingRootSin));
            local *= Matrix.CreateRotationY(MathHelper.ToRadians(DamageKickYawDeg * damageKick));
            local *= Matrix.CreateRotationX(MathHelper.ToRadians(DamageKickPitchDeg * damageKick));
            local *= Matrix.CreateRotationZ(MathHelper.ToRadians(DamageKickRollDeg * damageKick));
            local *= Matrix.CreateRotationY(MathHelper.ToRadians(ArmPostSwingYawDeg));

            var handScale = (Scale.HandScale * 2.32f) * Scale.BlockSize;
            var handWorld = Matrix.CreateScale(handScale) * local * basis * Matrix.CreateTranslation(camPos);

            _handEffect.View = view;
            _handEffect.Projection = projection;
            _handEffect.World = handWorld;
            _handEffect.Texture = skinTexture;
            _handEffect.Alpha = handAlpha;

            var prevHandRaster = _device.RasterizerState;
            try
            {
                _device.RasterizerState = RasterizerState.CullCounterClockwise;
                foreach (var pass in _handEffect.CurrentTechnique.Passes)
                {
                    pass.Apply();
                    _device.DrawUserPrimitives(PrimitiveType.TriangleList, _handVerts, 0, _handVerts.Length / 3);
                }

                if (_handOverlayVerts.Length > 0)
                {
                    _handOverlayEffect.View = view;
                    _handOverlayEffect.Projection = projection;
                    _handOverlayEffect.World = handWorld;
                    _handOverlayEffect.Texture = skinTexture;
                    _handOverlayEffect.Alpha = handAlpha;
                    foreach (var pass in _handOverlayEffect.CurrentTechnique.Passes)
                    {
                        pass.Apply();
                        _device.DrawUserPrimitives(PrimitiveType.TriangleList, _handOverlayVerts, 0, _handOverlayVerts.Length / 3);
                    }
                }
            }
            finally
            {
                _device.RasterizerState = prevHandRaster;
            }
        }

        if (heldBlock == BlockId.Air)
            return;

        if (_blockVerts == null || _cachedBlock != heldBlock || !ReferenceEquals(_cachedModel, model))
        {
            _cachedBlock = heldBlock;
            _cachedModel = model;
            _blockVerts = model?.BuildMesh(atlas, heldBlock) ?? BlockModel.BuildCubeMesh(atlas, heldBlock);
        }

        if (_blockVerts == null || _blockVerts.Length == 0)
            return;

        var blockPos = camPos
            + forward * ((0.66f + (0.08f * damageKick)) * Scale.BlockSize)
            + right * (HeldBlockRightOffset * Scale.BlockSize)
            - up * ((0.40f - (0.16f * _holdPoseBlend) - (0.05f * damageKick)) * Scale.BlockSize);

        Matrix display;
        if (model != null && model.TryGetDisplayTransform(BlockModelContext.FirstPersonRightHand, out var displayTransform))
            display = Matrix.CreateScale(0.9f) * displayTransform;
        else
            display = Matrix.CreateScale(Scale.HeldBlockScale * Scale.BlockSize);

        var blockRot = Matrix.CreateRotationX(0.22f - (swingRootSin * 0.30f) + MathHelper.ToRadians(DamageKickPitchDeg * damageKick));
        blockRot *= Matrix.CreateRotationY(-0.36f + (swingSquaredSin * 0.10f) + MathHelper.ToRadians(DamageKickYawDeg * damageKick));
        blockRot *= Matrix.CreateRotationZ(MathHelper.ToRadians(DamageKickRollDeg * damageKick));
        var blockWorld = display * blockRot * basis * Matrix.CreateTranslation(blockPos);
        var layer = BlockRegistry.Get(heldBlock).RenderLayer;

        var prevBlend = _device.BlendState;
        var prevDepth = _device.DepthStencilState;
        try
        {
            _device.DepthStencilState = DepthStencilState.Default;
            switch (layer)
            {
                case BlockRenderLayer.Cutout:
                    _device.BlendState = BlendState.Opaque;
                    DrawHeldBlockCutout(view, projection, blockWorld, atlas.Texture, _blockVerts);
                    break;
                case BlockRenderLayer.Blended:
                case BlockRenderLayer.Water:
                    _device.BlendState = BlendState.NonPremultiplied;
                    DrawHeldBlockTextured(view, projection, blockWorld, atlas.Texture, _blockVerts);
                    break;
                default:
                    _device.BlendState = BlendState.Opaque;
                    DrawHeldBlockTextured(view, projection, blockWorld, atlas.Texture, _blockVerts);
                    break;
            }
        }
        finally
        {
            _device.BlendState = prevBlend;
            _device.DepthStencilState = prevDepth;
        }
    }

    private void DrawHeldBlockTextured(Matrix view, Matrix projection, Matrix blockWorld, Texture2D texture, VertexPositionTexture[] blockVerts)
    {
        _blockEffect.View = view;
        _blockEffect.Projection = projection;
        _blockEffect.World = blockWorld;
        _blockEffect.Texture = texture;
        _blockEffect.Alpha = 1f;

        foreach (var pass in _blockEffect.CurrentTechnique.Passes)
        {
            pass.Apply();
            _device.DrawUserPrimitives(PrimitiveType.TriangleList, blockVerts, 0, blockVerts.Length / 3);
        }
    }

    private void DrawHeldBlockCutout(Matrix view, Matrix projection, Matrix blockWorld, Texture2D texture, VertexPositionTexture[] blockVerts)
    {
        _blockCutoutEffect.View = view;
        _blockCutoutEffect.Projection = projection;
        _blockCutoutEffect.World = blockWorld;
        _blockCutoutEffect.Texture = texture;
        _blockCutoutEffect.Alpha = 1f;

        foreach (var pass in _blockCutoutEffect.CurrentTechnique.Passes)
        {
            pass.Apply();
            _device.DrawUserPrimitives(PrimitiveType.TriangleList, blockVerts, 0, blockVerts.Length / 3);
        }
    }

    private static VertexPositionTexture[] BuildHandMesh(bool overlay, bool[]? skinMask = null)
    {
        if (overlay)
            return Array.Empty<VertexPositionTexture>();

        var inflate = 0f;
        var halfWidth = 0.18f + inflate;
        var halfHeight = 0.50f + inflate;
        var halfDepth = 0.20f + inflate;
        var verts = new List<VertexPositionTexture>(36);
        var min = new Vector3(-halfWidth, -halfHeight, -halfDepth);
        var max = new Vector3(halfWidth, halfHeight, halfDepth);
        var p000 = new Vector3(min.X, min.Y, min.Z);
        var p100 = new Vector3(max.X, min.Y, min.Z);
        var p010 = new Vector3(min.X, max.Y, min.Z);
        var p110 = new Vector3(max.X, max.Y, min.Z);
        var p001 = new Vector3(min.X, min.Y, max.Z);
        var p101 = new Vector3(max.X, min.Y, max.Z);
        var p011 = new Vector3(min.X, max.Y, max.Z);
        var p111 = new Vector3(max.X, max.Y, max.Z);

        AddQuad(verts, p001, p101, p111, p011, RightArmFrontRect);
        AddQuad(verts, p100, p000, p010, p110, RightArmBackRect);
        AddQuad(verts, p000, p001, p011, p010, RightArmLeftRect);
        AddQuad(verts, p101, p100, p110, p111, RightArmRightRect);
        AddQuad(verts, p011, p111, p110, p010, RightArmTopRect);
        AddQuad(verts, p000, p100, p101, p001, RightArmBottomRect);
        return verts.ToArray();
    }

    private static VertexPositionTexture[] BuildHandVoxelLayer(float halfWidth, float halfHeight, float halfDepth, float depth, bool[]? skinMask)
    {
        var verts = new List<VertexPositionTexture>(3072);
        var min = new Vector3(-halfWidth, -halfHeight, -halfDepth);
        var max = new Vector3(halfWidth, halfHeight, halfDepth);
        var p000 = new Vector3(min.X, min.Y, min.Z);
        var p100 = new Vector3(max.X, min.Y, min.Z);
        var p010 = new Vector3(min.X, max.Y, min.Z);
        var p110 = new Vector3(max.X, max.Y, min.Z);
        var p001 = new Vector3(min.X, min.Y, max.Z);
        var p101 = new Vector3(max.X, min.Y, max.Z);
        var p011 = new Vector3(min.X, max.Y, max.Z);
        var p111 = new Vector3(max.X, max.Y, max.Z);

        AddVoxelFace(verts, p001, p101, p111, p011, RightArmOverlayFrontRect, depth, skinMask);
        AddVoxelFace(verts, p100, p000, p010, p110, RightArmOverlayBackRect, depth, skinMask);
        AddVoxelFace(verts, p000, p001, p011, p010, RightArmOverlayLeftRect, depth, skinMask);
        AddVoxelFace(verts, p101, p100, p110, p111, RightArmOverlayRightRect, depth, skinMask);
        AddVoxelFace(verts, p011, p111, p110, p010, RightArmOverlayTopRect, depth, skinMask);
        AddVoxelFace(verts, p000, p100, p101, p001, RightArmOverlayBottomRect, depth, skinMask);
        return verts.ToArray();
    }

    private static void AddVoxelFace(
        List<VertexPositionTexture> verts,
        Vector3 p0,
        Vector3 p1,
        Vector3 p2,
        Vector3 p3,
        SkinRect rect,
        float depth,
        bool[]? skinMask)
    {
        var columns = Math.Max(1, (int)MathF.Round(rect.W));
        var rows = Math.Max(1, (int)MathF.Round(rect.H));
        var right = (p1 - p0) / columns;
        var up = (p3 - p0) / rows;
        var normal = Vector3.Normalize(Vector3.Cross(p1 - p0, p2 - p0));
        var push = normal * depth;

        for (var y = 0; y < rows; y++)
        {
            for (var x = 0; x < columns; x++)
            {
                var skinX = (int)rect.X + x;
                var skinY = (int)rect.Y + y;
                if (!IsLayerPixelVisible(skinMask, skinX, skinY))
                    continue;

                var i0 = p0 + (right * x) + (up * y);
                var i1 = i0 + right;
                var i3 = i0 + up;
                var i2 = i1 + up;
                var o0 = i0 + push;
                var o1 = i1 + push;
                var o2 = i2 + push;
                var o3 = i3 + push;
                var pixelRect = new SkinRect(rect.X + x, rect.Y + y, 1, 1);

                AddQuad(verts, o0, o1, o2, o3, pixelRect);
                if (!IsLayerPixelVisible(skinMask, skinX, skinY - 1))
                    AddQuad(verts, i1, i0, o0, o1, pixelRect);
                if (!IsLayerPixelVisible(skinMask, skinX, skinY + 1))
                    AddQuad(verts, i3, i2, o2, o3, pixelRect);
                if (!IsLayerPixelVisible(skinMask, skinX - 1, skinY))
                    AddQuad(verts, i0, i3, o3, o0, pixelRect);
                if (!IsLayerPixelVisible(skinMask, skinX + 1, skinY))
                    AddQuad(verts, i2, i1, o1, o2, pixelRect);
            }
        }
    }

    private static bool IsLayerPixelVisible(bool[]? skinMask, int x, int y)
    {
        if (x < 0 || x >= 64 || y < 0 || y >= 64)
            return false;

        return skinMask != null && skinMask[(y * 64) + x];
    }

    private static void AddQuad(
        List<VertexPositionTexture> verts,
        Vector3 p0,
        Vector3 p1,
        Vector3 p2,
        Vector3 p3,
        SkinRect rect)
    {
        var uv = RectToUv(rect);
        verts.Add(new VertexPositionTexture(p0, uv[0]));
        verts.Add(new VertexPositionTexture(p1, uv[1]));
        verts.Add(new VertexPositionTexture(p2, uv[2]));
        verts.Add(new VertexPositionTexture(p0, uv[0]));
        verts.Add(new VertexPositionTexture(p2, uv[2]));
        verts.Add(new VertexPositionTexture(p3, uv[3]));
    }

    private static Vector2[] RectToUv(SkinRect rect)
    {
        const float texSize = 64f;
        var left = rect.X / texSize;
        var right = (rect.X + rect.W) / texSize;
        var u0 = left;
        var u1 = right;
        var vTop = rect.Y / texSize;
        var vBottom = (rect.Y + rect.H) / texSize;
        return new[]
        {
            new Vector2(u0, vTop),
            new Vector2(u1, vTop),
            new Vector2(u1, vBottom),
            new Vector2(u0, vBottom)
        };
    }

    private void TryHotReloadSkinFromFile()
    {
        var resolvedPath = PlayerSkinSource.ResolvePath();
        var pathChanged = !string.Equals(_skinFilePath ?? string.Empty, resolvedPath ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        if (pathChanged)
        {
            _skinFilePath = resolvedPath;
            _skinFileWriteUtc = DateTime.MinValue;
            if (string.IsNullOrWhiteSpace(_skinFilePath))
            {
                SetSkinTexture(DefaultPlayerSkinFactory.Create(_device), takeOwnership: true);
                _handOverlayVerts = Array.Empty<VertexPositionTexture>();
                return;
            }
        }

        if (string.IsNullOrWhiteSpace(_skinFilePath) || !File.Exists(_skinFilePath))
            return;

        DateTime writeUtc;
        try
        {
            writeUtc = File.GetLastWriteTimeUtc(_skinFilePath);
        }
        catch
        {
            return;
        }

        if (!pathChanged && writeUtc <= _skinFileWriteUtc)
            return;

        try
        {
            var skinBytes = File.ReadAllBytes(_skinFilePath);
            var reloaded = PlayerSkinSource.LoadCompositedTexture(_device, skinBytes);
            SetSkinTexture(reloaded, takeOwnership: true);
            _handOverlayVerts = Array.Empty<VertexPositionTexture>();
            _skinFileWriteUtc = writeUtc;
        }
        catch
        {
            // Ignore transient read failures while file is being edited.
        }
    }

    private void SetSkinTexture(Texture2D skinTexture, bool takeOwnership)
    {
        if (ReferenceEquals(_skinTexture, skinTexture))
        {
            _ownsSkinTexture = takeOwnership;
            return;
        }

        if (_ownsSkinTexture)
            _skinTexture.Dispose();

        _skinTexture = skinTexture;
        _ownsSkinTexture = takeOwnership;
    }

    private static Texture2D CreateDefaultSkin(GraphicsDevice device)
    {
        return DefaultPlayerSkinFactory.Create(device);
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _handEffect.Dispose();
        _handOverlayEffect.Dispose();
        _blockEffect.Dispose();
        _blockCutoutEffect.Dispose();
        if (_ownsSkinTexture)
            _skinTexture.Dispose();
        _disposed = true;
    }

    private readonly struct SkinRect
    {
        public SkinRect(float x, float y, float w, float h)
        {
            X = x;
            Y = y;
            W = w;
            H = h;
        }

        public float X { get; }
        public float Y { get; }
        public float W { get; }
        public float H { get; }
    }

    private static readonly SkinRect RightArmFrontRect = new(44, 20, 4, 12);
    private static readonly SkinRect RightArmBackRect = new(52, 20, 4, 12);
    private static readonly SkinRect RightArmLeftRect = new(40, 20, 4, 12);
    private static readonly SkinRect RightArmRightRect = new(48, 20, 4, 12);
    private static readonly SkinRect RightArmTopRect = new(44, 16, 4, 4);
    private static readonly SkinRect RightArmBottomRect = new(48, 16, 4, 4);
    private static readonly SkinRect RightArmOverlayFrontRect = new(44, 36, 4, 12);
    private static readonly SkinRect RightArmOverlayBackRect = new(52, 36, 4, 12);
    private static readonly SkinRect RightArmOverlayLeftRect = new(40, 36, 4, 12);
    private static readonly SkinRect RightArmOverlayRightRect = new(48, 36, 4, 12);
    private static readonly SkinRect RightArmOverlayTopRect = new(44, 32, 4, 4);
    private static readonly SkinRect RightArmOverlayBottomRect = new(48, 32, 4, 4);
}
