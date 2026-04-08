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

    private readonly GraphicsDevice _device;
    private readonly BasicEffect _handEffect;
    private readonly BasicEffect _blockEffect;
    private readonly AlphaTestEffect _blockCutoutEffect;
    private readonly VertexPositionTexture[] _handVerts;
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

        _handVerts = BuildHandMesh();
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
        float damageKickProgress)
    {
        _ = isGrounded;
        _ = verticalVelocity;
        var skinDt = 1f / 60f;
        if (!float.IsNaN(_lastSkinSampleTime))
            skinDt = Math.Max(0f, worldTimeSeconds - _lastSkinSampleTime);
        _lastSkinSampleTime = worldTimeSeconds;

        _skinHotReloadTimer -= skinDt;
        if (_skinHotReloadTimer <= 0f)
        {
            _skinHotReloadTimer = SkinHotReloadIntervalSeconds;
            TryHotReloadSkinFromFile();
        }

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
            _handEffect.Texture = _skinTexture;
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

    private static VertexPositionTexture[] BuildHandMesh()
    {
        var verts = new List<VertexPositionTexture>(36);
        var halfWidth = 0.18f;
        var halfHeight = 0.50f;
        var halfDepth = 0.20f;
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
        if (string.IsNullOrWhiteSpace(_skinFilePath) || !File.Exists(_skinFilePath))
            _skinFilePath = PlayerSkinSource.ResolvePath();

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

        if (writeUtc <= _skinFileWriteUtc)
            return;

        try
        {
            using var fs = File.OpenRead(_skinFilePath);
            var reloaded = Texture2D.FromStream(_device, fs);
            SetSkinTexture(reloaded, takeOwnership: true);
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
}
