using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace LatticeVeilMonoGame.Core;

public sealed class PlayerModel : IDisposable
{
    public const int OutlineVertexCapacity = 144;

    private const float ActionSwingDuration = 0.28f;
    private const float ModelForwardYawOffset = MathHelper.PiOver2;
    private const float SneakHeight = Scale.PlayerHeight - 0.5f;
    private const float MaxHeadYaw = 0.95f;
    private const float SkinHotReloadIntervalSeconds = 0.8f;
    private const float SneakBodyDrop = -0.18f;

    private readonly GraphicsDevice _device;
    private readonly BasicEffect _baseEffect;
    private readonly AlphaTestEffect _overlayEffect;
    private readonly BasicEffect _heldBlockEffect;
    private readonly Dictionary<BlockId, VertexPositionTexture[]> _heldBlockMeshCache = new();
    private readonly PartMesh _head;
    private readonly PartMesh _body;
    private readonly PartMesh _rightArm;
    private readonly PartMesh _leftArm;
    private readonly PartMesh _rightLeg;
    private readonly PartMesh _leftLeg;
    private Texture2D _skinTexture;
    private bool _ownsSkinTexture;
    private CubeNetAtlas? _atlas;
    private string? _skinFilePath;
    private DateTime _skinFileWriteUtc;
    private float _skinHotReloadTimer;

    private Vector3 _lastSamplePosition;
    private float _lastSampleTime = float.NaN;
    private float _estimatedMoveSpeed;
    private float _estimatedVerticalSpeed;
    private float _walkAmountSmoothed;
    private float _moveCyclePhase;
    private float _actionSwingTimer;
    private float _actionSwingAimYawOffset;
    private float _actionSwingAimPitch;
    private Matrix _headWorld;
    private Matrix _bodyWorld;
    private Matrix _rightArmWorld;
    private Matrix _leftArmWorld;
    private Matrix _rightLegWorld;
    private Matrix _leftLegWorld;
    private bool _hasPoseCache;
    private bool _disposed;

    public Vector3 Position { get; set; }
    public float Yaw { get; set; }
    public float Pitch { get; set; }
    public float HeadYawOffset { get; set; }
    public bool IsFlying { get; set; }
    public bool IsSneaking { get; set; }
    public bool IsSprinting { get; set; }
    public bool IsGrounded { get; set; } = true;
    public float VerticalVelocity { get; set; }
    public BlockId HeldBlockId { get; set; }
    public bool HasHeldBlock => HeldBlockId != BlockId.Air;
    public float ColliderHeight => IsSneaking ? SneakHeight : Scale.PlayerHeight;

    public PlayerModel(GraphicsDevice device, AssetLoader? assets = null)
    {
        _device = device;
        _baseEffect = new BasicEffect(device)
        {
            VertexColorEnabled = false,
            TextureEnabled = true,
            LightingEnabled = false
        };
        _overlayEffect = new AlphaTestEffect(device)
        {
            VertexColorEnabled = false,
            AlphaFunction = CompareFunction.Greater,
            ReferenceAlpha = 8
        };
        _heldBlockEffect = new BasicEffect(device)
        {
            VertexColorEnabled = false,
            TextureEnabled = true,
            LightingEnabled = false
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

        _head = CreatePart(0.42f, 0.42f, 0.42f, HeadBaseMap, HeadOverlayMap, 0.022f);
        _body = CreatePart(0.52f, 0.70f, 0.30f, BodyBaseMap, BodyOverlayMap, 0.018f);
        _rightArm = CreatePart(0.24f, 0.72f, 0.24f, RightArmBaseMap, RightArmOverlayMap, 0.018f);
        _leftArm = CreatePart(0.24f, 0.72f, 0.24f, LeftArmBaseMap, LeftArmOverlayMap, 0.018f);
        _rightLeg = CreatePart(0.22f, 0.72f, 0.24f, RightLegBaseMap, RightLegOverlayMap, 0.018f);
        _leftLeg = CreatePart(0.22f, 0.72f, 0.24f, LeftLegBaseMap, LeftLegOverlayMap, 0.018f);
    }

    public void SetAtlas(CubeNetAtlas? atlas)
    {
        if (ReferenceEquals(_atlas, atlas))
            return;

        _atlas = atlas;
        _heldBlockMeshCache.Clear();
    }

    public void SetSkinTexture(Texture2D skinTexture, bool takeOwnership = false)
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

    public void TriggerActionSwing()
    {
        _actionSwingAimYawOffset = Math.Clamp(HeadYawOffset, -MaxHeadYaw, MaxHeadYaw);
        _actionSwingAimPitch = Pitch;
        _actionSwingTimer = ActionSwingDuration;
    }

    public void TriggerActionSwing(Vector3 worldAimDirection)
    {
        if (worldAimDirection.LengthSquared() <= 0.0001f)
        {
            TriggerActionSwing();
            return;
        }

        worldAimDirection.Normalize();
        var aimYaw = MathF.Atan2(worldAimDirection.Z, worldAimDirection.X);
        var aimPitch = MathF.Asin(Math.Clamp(worldAimDirection.Y, -1f, 1f));
        _actionSwingAimYawOffset = Math.Clamp(MathHelper.WrapAngle(aimYaw - Yaw), -MaxHeadYaw, MaxHeadYaw);
        _actionSwingAimPitch = aimPitch;
        _actionSwingTimer = ActionSwingDuration;
    }

    public void Render(Matrix view, Matrix projection, float worldTimeSeconds)
    {
        _hasPoseCache = false;

        var dt = 0f;
        if (!float.IsNaN(_lastSampleTime))
            dt = Math.Max(0f, worldTimeSeconds - _lastSampleTime);

        if (dt > 0f)
        {
            var delta = Position - _lastSamplePosition;
            var horizontal = MathF.Sqrt((delta.X * delta.X) + (delta.Z * delta.Z));
            _estimatedMoveSpeed = MathHelper.Lerp(_estimatedMoveSpeed, horizontal / dt, 0.22f);
            _estimatedVerticalSpeed = MathHelper.Lerp(_estimatedVerticalSpeed, delta.Y / dt, 0.26f);
            _actionSwingTimer = Math.Max(0f, _actionSwingTimer - dt);

            _skinHotReloadTimer -= dt;
            if (_skinHotReloadTimer <= 0f)
            {
                _skinHotReloadTimer = SkinHotReloadIntervalSeconds;
                TryHotReloadSkinFromFile();
            }
        }

        _lastSamplePosition = Position;
        _lastSampleTime = worldTimeSeconds;

        _baseEffect.View = view;
        _baseEffect.Projection = projection;
        _baseEffect.Texture = _skinTexture;
        _baseEffect.Alpha = 1f;

        _overlayEffect.View = view;
        _overlayEffect.Projection = projection;
        _overlayEffect.Texture = _skinTexture;
        _overlayEffect.Alpha = 1f;

        var walkAmount = Math.Clamp(_estimatedMoveSpeed / (6f * Scale.BlockSize), 0f, 1.35f);
        if (IsSprinting)
            walkAmount = Math.Clamp(walkAmount * 1.12f, 0f, 1.35f);

        var walkBlendLerp = dt > 0f ? Math.Clamp(dt * 10f, 0f, 1f) : 0f;
        _walkAmountSmoothed = MathHelper.Lerp(_walkAmountSmoothed, walkAmount, walkBlendLerp);
        walkAmount = _walkAmountSmoothed;

        if (dt > 0f)
        {
            var cycleSpeed = 1.6f + (IsSprinting ? 8.8f : 6.0f) * walkAmount;
            _moveCyclePhase += dt * cycleSpeed;
            if (_moveCyclePhase > MathHelper.TwoPi * 64f)
                _moveCyclePhase = MathF.IEEERemainder(_moveCyclePhase, MathHelper.TwoPi);
        }
        var moveCycle = _moveCyclePhase;
        var legSwing = MathF.Sin(moveCycle) * (0.90f * walkAmount);
        var armSwing = MathF.Sin(moveCycle + MathF.PI) * (0.86f * walkAmount);
        var torsoSway = MathF.Sin(moveCycle * 1.6f) * (0.03f * walkAmount);
        var bodyBob = IsFlying
            ? MathF.Sin(worldTimeSeconds * 2.6f) * 0.06f
            : MathF.Sin(moveCycle * 2f) * (0.03f * walkAmount);
        var hoverSway = MathF.Sin(worldTimeSeconds * 3.3f) * 0.18f;

        var airborne = !IsFlying && !IsGrounded;
        var effectiveVy = MathF.Abs(VerticalVelocity) > 0.001f ? VerticalVelocity : _estimatedVerticalSpeed;
        var jumping = airborne && effectiveVy > 0.2f;
        var airBlend = airborne ? Math.Clamp(MathF.Abs(effectiveVy) / 6f, 0f, 1f) : 0f;

        var punch = 0f;
        var punchBlend = 0f;
        if (_actionSwingTimer > 0f)
        {
            var phase = 1f - (_actionSwingTimer / ActionSwingDuration);
            punchBlend = MathF.Sin(Math.Clamp(phase, 0f, 1f) * MathF.PI);
            punch = punchBlend * (HasHeldBlock ? 0.95f : 1.20f);
        }

        var crouchOffset = IsSneaking ? SneakBodyDrop : 0f;
        // PlayerController yaw uses +X as forward with clockwise-positive heading on XZ.
        // Convert that to model-space (+Z front) so visual facing matches camera/movement.
        var rootPosition = Position;
        if (IsFlying)
            rootPosition += new Vector3(0f, bodyBob, 0f);
        var upperBodyBob = IsFlying ? 0f : bodyBob;
        var root = Matrix.CreateRotationY(ModelForwardYawOffset - Yaw);
        root *= Matrix.CreateTranslation(rootPosition);

        var bodyRot = Matrix.CreateRotationX(
            IsFlying
                ? -0.36f + hoverSway * 0.22f
                : (IsSneaking ? 0.18f : 0f) + (IsSprinting ? 0.08f : 0f) + (jumping ? -0.08f : 0.04f * airBlend));
        bodyRot *= Matrix.CreateRotationZ(torsoSway);
        var sneakForwardShift = IsSneaking ? 0.05f : 0f;
        _bodyWorld = DrawPart(_body, root, new Vector3(0f, 1.07f + crouchOffset + upperBodyBob, sneakForwardShift), bodyRot, Vector3.Zero);

        var clampedHeadYaw = Math.Clamp(HeadYawOffset, -MaxHeadYaw, MaxHeadYaw);
        var headYaw = clampedHeadYaw + (MathF.Sin(moveCycle * 0.85f) * (0.04f * walkAmount));
        var headPitch = Math.Clamp(Pitch, -1.05f, 1.05f) * 0.80f;
        var headRot = Matrix.CreateRotationY(headYaw);
        // Positive player pitch means "look up"; model-space X rotation is inverted for that.
        headRot *= Matrix.CreateRotationX(-headPitch);
        headRot *= Matrix.CreateRotationZ(IsFlying ? hoverSway * 0.06f : torsoSway * 0.5f);
        _headWorld = DrawPart(_head, root, new Vector3(0f, 1.59f + crouchOffset + upperBodyBob, sneakForwardShift * 0.7f), headRot, new Vector3(0f, -_head.HalfSize.Y + 0.03f, 0f));

        var armBaseY = 1.06f + crouchOffset + upperBodyBob;
        var actionAimYaw = Math.Clamp(_actionSwingAimYawOffset, -MaxHeadYaw, MaxHeadYaw);
        var actionAimPitch = Math.Clamp(_actionSwingAimPitch, -1.05f, 1.05f) * 0.80f;
        var punchAimYaw = actionAimYaw * (0.82f * punchBlend);
        var punchAimPitch = -actionAimPitch * (0.68f * punchBlend);
        var leftArmPitch = IsFlying
            ? -0.34f + hoverSway * 0.45f
            : (armSwing * 0.78f) - (jumping ? 0.35f : 0.14f * airBlend) - (punch * 1.18f) + punchAimPitch;
        var leftArmRot = Matrix.CreateRotationX(leftArmPitch);
        leftArmRot *= Matrix.CreateRotationY(punchAimYaw);
        leftArmRot *= Matrix.CreateRotationZ(IsFlying ? -0.26f : 0.06f + punch * 0.22f);

        var rightArmRot = Matrix.CreateRotationX(
            IsFlying
                ? -0.34f - hoverSway * 0.45f
                : (-armSwing * 0.58f) - (jumping ? 0.32f : 0.12f * airBlend));
        rightArmRot *= Matrix.CreateRotationZ(IsFlying ? 0.26f : -0.08f);

        var armPivot = new Vector3(0f, _rightArm.HalfSize.Y - 0.02f, 0f);
        _leftArmWorld = DrawPart(_leftArm, root, new Vector3(-0.38f, armBaseY, sneakForwardShift), leftArmRot, armPivot);
        _rightArmWorld = DrawPart(_rightArm, root, new Vector3(0.38f, armBaseY, sneakForwardShift), rightArmRot, armPivot);
        DrawHeldBlock(_leftArmWorld);

        var legBaseY = _rightLeg.HalfSize.Y + (IsFlying ? 0.03f : 0f);
        var legTuck = airborne ? (jumping ? 0.22f : 0.42f) : 0f;
        var sneakLegBend = IsSneaking ? 0.08f : 0f;
        var leftLegRot = Matrix.CreateRotationX(
            IsFlying
                ? 0.20f + hoverSway * 0.24f
                : (legSwing * 0.90f) + legTuck + sneakLegBend);
        leftLegRot *= Matrix.CreateRotationZ(IsFlying ? -0.06f : (IsSneaking ? 0.05f : 0f));

        var rightLegRot = Matrix.CreateRotationX(
            IsFlying
                ? 0.20f - hoverSway * 0.24f
                : (-legSwing * 0.90f) + legTuck + sneakLegBend);
        rightLegRot *= Matrix.CreateRotationZ(IsFlying ? 0.06f : (IsSneaking ? -0.05f : 0f));

        var legPivot = new Vector3(0f, _rightLeg.HalfSize.Y - 0.01f, 0f);
        var legOffsetX = IsFlying ? 0.17f : 0.13f;
        _leftLegWorld = DrawPart(_leftLeg, root, new Vector3(-legOffsetX, legBaseY, 0f), leftLegRot, legPivot);
        _rightLegWorld = DrawPart(_rightLeg, root, new Vector3(legOffsetX, legBaseY, 0f), rightLegRot, legPivot);
        _hasPoseCache = true;
    }

    public int WriteOutlineVertices(Span<VertexPositionColor> destination, Color color, float inflate = 0.012f)
    {
        if (!_hasPoseCache || destination.Length < OutlineVertexCapacity)
            return 0;

        var cursor = 0;
        WritePartOutline(destination, ref cursor, _headWorld, _head.HalfSize, color, inflate);
        WritePartOutline(destination, ref cursor, _bodyWorld, _body.HalfSize, color, inflate);
        WritePartOutline(destination, ref cursor, _leftArmWorld, _leftArm.HalfSize, color, inflate);
        WritePartOutline(destination, ref cursor, _rightArmWorld, _rightArm.HalfSize, color, inflate);
        WritePartOutline(destination, ref cursor, _leftLegWorld, _leftLeg.HalfSize, color, inflate);
        WritePartOutline(destination, ref cursor, _rightLegWorld, _rightLeg.HalfSize, color, inflate);
        return cursor;
    }

    private Matrix DrawPart(PartMesh part, Matrix root, Vector3 partCenter, Matrix localRotation, Vector3 pivot)
    {
        var world = BuildPartWorld(root, partCenter, localRotation, pivot);

        DrawMesh(part.BaseVerts, world, overlay: false);
        if (part.OverlayVerts.Length > 0)
            DrawMesh(part.OverlayVerts, world, overlay: true);

        return world;
    }

    private static void WritePartOutline(
        Span<VertexPositionColor> destination,
        ref int cursor,
        Matrix world,
        Vector3 halfSize,
        Color color,
        float inflate)
    {
        var inflatedHalf = halfSize + new Vector3(inflate);
        var min = -inflatedHalf;
        var max = inflatedHalf;

        var v000 = Vector3.Transform(new Vector3(min.X, min.Y, min.Z), world);
        var v100 = Vector3.Transform(new Vector3(max.X, min.Y, min.Z), world);
        var v110 = Vector3.Transform(new Vector3(max.X, max.Y, min.Z), world);
        var v010 = Vector3.Transform(new Vector3(min.X, max.Y, min.Z), world);
        var v001 = Vector3.Transform(new Vector3(min.X, min.Y, max.Z), world);
        var v101 = Vector3.Transform(new Vector3(max.X, min.Y, max.Z), world);
        var v111 = Vector3.Transform(new Vector3(max.X, max.Y, max.Z), world);
        var v011 = Vector3.Transform(new Vector3(min.X, max.Y, max.Z), world);

        WriteLine(destination, ref cursor, v000, v100, color);
        WriteLine(destination, ref cursor, v100, v110, color);
        WriteLine(destination, ref cursor, v110, v010, color);
        WriteLine(destination, ref cursor, v010, v000, color);

        WriteLine(destination, ref cursor, v001, v101, color);
        WriteLine(destination, ref cursor, v101, v111, color);
        WriteLine(destination, ref cursor, v111, v011, color);
        WriteLine(destination, ref cursor, v011, v001, color);

        WriteLine(destination, ref cursor, v000, v001, color);
        WriteLine(destination, ref cursor, v100, v101, color);
        WriteLine(destination, ref cursor, v110, v111, color);
        WriteLine(destination, ref cursor, v010, v011, color);
    }

    private static void WriteLine(Span<VertexPositionColor> destination, ref int cursor, Vector3 a, Vector3 b, Color color)
    {
        destination[cursor++] = new VertexPositionColor(a, color);
        destination[cursor++] = new VertexPositionColor(b, color);
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
            // Ignore transient file write/read errors while the image is being edited.
        }
    }

    private static Matrix BuildPartWorld(Matrix root, Vector3 partCenter, Matrix localRotation, Vector3 pivot)
    {
        return Matrix.CreateTranslation(-pivot)
            * localRotation
            * Matrix.CreateTranslation(pivot + partCenter)
            * root;
    }

    private void DrawHeldBlock(Matrix handArmWorld)
    {
        if (HeldBlockId == BlockId.Air || _atlas == null)
            return;

        if (!_heldBlockMeshCache.TryGetValue(HeldBlockId, out var mesh))
        {
            mesh = BlockModel.BuildCubeMesh(_atlas, HeldBlockId);
            if (mesh == null || mesh.Length == 0 || mesh.Length % 3 != 0)
            {
                _heldBlockMeshCache[HeldBlockId] = Array.Empty<VertexPositionTexture>();
                return;
            }

            _heldBlockMeshCache[HeldBlockId] = mesh;
        }

        if (mesh.Length == 0)
            return;

        var localAnchor = new Vector3(
            -_rightArm.HalfSize.X * 0.62f,
            -_rightArm.HalfSize.Y + 0.10f,
            _rightArm.HalfSize.Z * 1.02f);
        var worldAnchor = Vector3.Transform(localAnchor, handArmWorld);
        var world = Matrix.CreateScale(0.25f)
            * Matrix.CreateRotationX(-0.42f)
            * Matrix.CreateRotationY(-0.24f)
            * Matrix.CreateTranslation(worldAnchor);

        _heldBlockEffect.World = world;
        _heldBlockEffect.View = _baseEffect.View;
        _heldBlockEffect.Projection = _baseEffect.Projection;
        _heldBlockEffect.Texture = _atlas.Texture;
        _heldBlockEffect.Alpha = 1f;
        foreach (var pass in _heldBlockEffect.CurrentTechnique.Passes)
        {
            pass.Apply();
            _device.DrawUserPrimitives(PrimitiveType.TriangleList, mesh, 0, mesh.Length / 3);
        }
    }

    private void DrawMesh(VertexPositionTexture[] verts, Matrix world, bool overlay)
    {
        if (verts.Length == 0 || verts.Length % 3 != 0)
            return;

        if (overlay)
        {
            _overlayEffect.World = world;
            foreach (var pass in _overlayEffect.CurrentTechnique.Passes)
            {
                pass.Apply();
                _device.DrawUserPrimitives(PrimitiveType.TriangleList, verts, 0, verts.Length / 3);
            }
            return;
        }

        _baseEffect.World = world;
        foreach (var pass in _baseEffect.CurrentTechnique.Passes)
        {
            pass.Apply();
            _device.DrawUserPrimitives(PrimitiveType.TriangleList, verts, 0, verts.Length / 3);
        }
    }

    private static PartMesh CreatePart(
        float sizeX,
        float sizeY,
        float sizeZ,
        SkinBoxMap baseMap,
        SkinBoxMap overlayMap,
        float overlayInflate)
    {
        var half = new Vector3(sizeX * 0.5f, sizeY * 0.5f, sizeZ * 0.5f);
        var baseVerts = BuildTexturedBox(half, baseMap);
        // Layers are intentionally disabled for now to guarantee stable baked skin rendering.
        var overlayVerts = Array.Empty<VertexPositionTexture>();
        return new PartMesh(half, baseVerts, overlayVerts);
    }

    private static VertexPositionTexture[] BuildTexturedBox(Vector3 half, SkinBoxMap map)
    {
        var verts = new List<VertexPositionTexture>(36);
        var min = -half;
        var max = half;

        var p000 = new Vector3(min.X, min.Y, min.Z);
        var p100 = new Vector3(max.X, min.Y, min.Z);
        var p010 = new Vector3(min.X, max.Y, min.Z);
        var p110 = new Vector3(max.X, max.Y, min.Z);
        var p001 = new Vector3(min.X, min.Y, max.Z);
        var p101 = new Vector3(max.X, min.Y, max.Z);
        var p011 = new Vector3(min.X, max.Y, max.Z);
        var p111 = new Vector3(max.X, max.Y, max.Z);

        AddQuad(verts, p001, p101, p111, p011, map.Front);
        AddQuad(verts, p100, p000, p010, p110, map.Back);
        AddQuad(verts, p000, p001, p011, p010, map.Left);
        AddQuad(verts, p101, p100, p110, p111, map.Right);
        AddQuad(verts, p011, p111, p110, p010, map.Top);
        AddQuad(verts, p000, p100, p101, p001, map.Bottom);

        return verts.ToArray();
    }

    private static void AddQuad(List<VertexPositionTexture> verts, Vector3 p0, Vector3 p1, Vector3 p2, Vector3 p3, SkinRect rect)
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
        var u0 = rect.X / texSize;
        var u1 = (rect.X + rect.W) / texSize;
        // Quads are built from bottom-left -> bottom-right -> top-right -> top-left.
        // Keep UV order aligned with that winding so skin faces are not vertically inverted.
        var vTop = rect.Y / texSize;
        var vBottom = (rect.Y + rect.H) / texSize;
        return new[]
        {
            new Vector2(u0, vBottom),
            new Vector2(u1, vBottom),
            new Vector2(u1, vTop),
            new Vector2(u0, vTop)
        };
    }

    private static Texture2D CreateDefaultSkin(GraphicsDevice device)
    {
        return DefaultPlayerSkinFactory.Create(device);
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _baseEffect.Dispose();
        _overlayEffect.Dispose();
        _heldBlockEffect.Dispose();
        if (_ownsSkinTexture)
            _skinTexture.Dispose();

        _heldBlockMeshCache.Clear();
        _disposed = true;
    }

    private sealed class PartMesh
    {
        public PartMesh(Vector3 halfSize, VertexPositionTexture[] baseVerts, VertexPositionTexture[] overlayVerts)
        {
            HalfSize = halfSize;
            BaseVerts = baseVerts;
            OverlayVerts = overlayVerts;
        }

        public Vector3 HalfSize { get; }
        public VertexPositionTexture[] BaseVerts { get; }
        public VertexPositionTexture[] OverlayVerts { get; }
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

    private readonly struct SkinBoxMap
    {
        public SkinBoxMap(SkinRect top, SkinRect bottom, SkinRect left, SkinRect front, SkinRect right, SkinRect back)
        {
            Top = top;
            Bottom = bottom;
            Left = left;
            Front = front;
            Right = right;
            Back = back;
        }

        public SkinRect Top { get; }
        public SkinRect Bottom { get; }
        public SkinRect Left { get; }
        public SkinRect Front { get; }
        public SkinRect Right { get; }
        public SkinRect Back { get; }
    }

    private static readonly SkinBoxMap HeadBaseMap = new(
        new SkinRect(8, 0, 8, 8),
        new SkinRect(16, 0, 8, 8),
        new SkinRect(0, 8, 8, 8),
        new SkinRect(8, 8, 8, 8),
        new SkinRect(16, 8, 8, 8),
        new SkinRect(24, 8, 8, 8));

    private static readonly SkinBoxMap HeadOverlayMap = new(
        new SkinRect(40, 0, 8, 8),
        new SkinRect(48, 0, 8, 8),
        new SkinRect(32, 8, 8, 8),
        new SkinRect(40, 8, 8, 8),
        new SkinRect(48, 8, 8, 8),
        new SkinRect(56, 8, 8, 8));

    private static readonly SkinBoxMap BodyBaseMap = new(
        new SkinRect(20, 16, 8, 4),
        new SkinRect(28, 16, 8, 4),
        new SkinRect(16, 20, 4, 12),
        new SkinRect(20, 20, 8, 12),
        new SkinRect(28, 20, 4, 12),
        new SkinRect(32, 20, 8, 12));

    private static readonly SkinBoxMap BodyOverlayMap = new(
        new SkinRect(20, 32, 8, 4),
        new SkinRect(28, 32, 8, 4),
        new SkinRect(16, 36, 4, 12),
        new SkinRect(20, 36, 8, 12),
        new SkinRect(28, 36, 4, 12),
        new SkinRect(32, 36, 8, 12));

    private static readonly SkinBoxMap RightArmBaseMap = new(
        new SkinRect(44, 16, 4, 4),
        new SkinRect(48, 16, 4, 4),
        new SkinRect(40, 20, 4, 12),
        new SkinRect(44, 20, 4, 12),
        new SkinRect(48, 20, 4, 12),
        new SkinRect(52, 20, 4, 12));

    private static readonly SkinBoxMap RightArmOverlayMap = new(
        new SkinRect(44, 32, 4, 4),
        new SkinRect(48, 32, 4, 4),
        new SkinRect(40, 36, 4, 12),
        new SkinRect(44, 36, 4, 12),
        new SkinRect(48, 36, 4, 12),
        new SkinRect(52, 36, 4, 12));

    private static readonly SkinBoxMap LeftArmBaseMap = new(
        new SkinRect(36, 48, 4, 4),
        new SkinRect(40, 48, 4, 4),
        new SkinRect(32, 52, 4, 12),
        new SkinRect(36, 52, 4, 12),
        new SkinRect(40, 52, 4, 12),
        new SkinRect(44, 52, 4, 12));

    private static readonly SkinBoxMap LeftArmOverlayMap = new(
        new SkinRect(52, 48, 4, 4),
        new SkinRect(56, 48, 4, 4),
        new SkinRect(48, 52, 4, 12),
        new SkinRect(52, 52, 4, 12),
        new SkinRect(56, 52, 4, 12),
        new SkinRect(60, 52, 4, 12));

    private static readonly SkinBoxMap RightLegBaseMap = new(
        new SkinRect(4, 16, 4, 4),
        new SkinRect(8, 16, 4, 4),
        new SkinRect(0, 20, 4, 12),
        new SkinRect(4, 20, 4, 12),
        new SkinRect(8, 20, 4, 12),
        new SkinRect(12, 20, 4, 12));

    private static readonly SkinBoxMap RightLegOverlayMap = new(
        new SkinRect(4, 32, 4, 4),
        new SkinRect(8, 32, 4, 4),
        new SkinRect(0, 36, 4, 12),
        new SkinRect(4, 36, 4, 12),
        new SkinRect(8, 36, 4, 12),
        new SkinRect(12, 36, 4, 12));

    private static readonly SkinBoxMap LeftLegBaseMap = new(
        new SkinRect(20, 48, 4, 4),
        new SkinRect(24, 48, 4, 4),
        new SkinRect(16, 52, 4, 12),
        new SkinRect(20, 52, 4, 12),
        new SkinRect(24, 52, 4, 12),
        new SkinRect(28, 52, 4, 12));

    private static readonly SkinBoxMap LeftLegOverlayMap = new(
        new SkinRect(4, 48, 4, 4),
        new SkinRect(8, 48, 4, 4),
        new SkinRect(0, 52, 4, 12),
        new SkinRect(4, 52, 4, 12),
        new SkinRect(8, 52, 4, 12),
        new SkinRect(12, 52, 4, 12));
}
