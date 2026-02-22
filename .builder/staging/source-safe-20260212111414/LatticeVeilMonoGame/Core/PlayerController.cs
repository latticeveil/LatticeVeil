using System;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;

namespace LatticeVeilMonoGame.Core;

public sealed class PlayerController
{
    private const float HalfWidth = Scale.PlayerWidth * 0.5f;
    private const float Height = Scale.PlayerHeight;
    private const float Skin = 0.001f;
    private const float Gravity = -25f;
    private const float JumpSpeed = 8f;
    private const float WalkSpeed = 6f * Scale.BlockSize;
    private const float FlySpeed = 18f * Scale.BlockSize;
    private const double DoubleTapSeconds = 0.30;
    private const float Eps = 0.001f;

    private double _lastSpaceTapTime;
    private Vector3 _moveIntent;
    private float _swayAccumulator;

    public Vector3 Position { get; set; } = new(8f, 6f, -12f);
    public Vector3 Velocity { get; set; }
    public float Yaw { get; set; } = MathHelper.PiOver2;
    public float Pitch { get; set; } = -0.25f;
    public bool IsGrounded { get; private set; }
    public bool IsFlying { get; private set; }
    public Vector3 MoveIntent => _moveIntent;
    public bool AllowFlying { get; set; } = true;

    public const float ColliderHalfWidth = HalfWidth;
    public const float ColliderHeight = Height;

    public void SetFlying(bool value)
    {
        IsFlying = value;
        Velocity = Vector3.Zero;
    }

    public Vector3 HeadOffset
    {
        get
        {
            var offset = new Vector3(0f, Scale.PlayerHeadHeight, 0f);
            if (IsFlying)
            {
                offset.Y += MathF.Sin(_swayAccumulator * 1.5f) * 0.04f;
                offset.X += MathF.Cos(_swayAccumulator * 0.8f) * 0.02f;
                offset.Z += MathF.Sin(_swayAccumulator * 1.1f) * 0.02f;
            }
            return offset;
        }
    }

    public void Update(float dt, double nowSeconds, InputState input, Func<int, int, int, byte> getBlock)
    {
        _swayAccumulator += dt;
        ApplyLook(input);
        HandleFlyToggle(nowSeconds, input);
        if (!AllowFlying && IsFlying)
            SetFlying(false);
        ApplyMovement(dt, input, getBlock);
    }

    public Vector3 Forward => GetForwardVector(Yaw, Pitch);

    public static Vector3 GetForwardVector(float yaw, float pitch)
    {
        var cosPitch = (float)Math.Cos(pitch);
        var sinPitch = (float)Math.Sin(pitch);
        var cosYaw = (float)Math.Cos(yaw);
        var sinYaw = (float)Math.Sin(yaw);
        return new Vector3(cosYaw * cosPitch, sinPitch, sinYaw * cosPitch);
    }

    private void ApplyLook(InputState input)
    {
        var delta = input.LookDelta;
        if (delta.X != 0f || delta.Y != 0f)
        {
            Yaw += delta.X;
            Pitch -= delta.Y;
            Pitch = Math.Clamp(Pitch, -1.4f, 1.4f);
        }
    }

    private void HandleFlyToggle(double nowSeconds, InputState input)
    {
        if (!AllowFlying)
            return;
        if (!input.IsNewKeyPress(Keys.Space))
            return;

        if (nowSeconds - _lastSpaceTapTime <= DoubleTapSeconds)
        {
            IsFlying = !IsFlying;
            Velocity = Vector3.Zero;
        }

        _lastSpaceTapTime = nowSeconds;
    }

    private void ApplyMovement(float dt, InputState input, Func<int, int, int, byte> getBlock)
    {
        var forward = new Vector3((float)Math.Cos(Yaw), 0f, (float)Math.Sin(Yaw));
        var right = new Vector3(-forward.Z, 0f, forward.X);
        var moveXZ = Vector3.Zero;

        if (input.IsKeyDown(Keys.W)) moveXZ += forward;
        if (input.IsKeyDown(Keys.S)) moveXZ -= forward;
        if (input.IsKeyDown(Keys.A)) moveXZ -= right;
        if (input.IsKeyDown(Keys.D)) moveXZ += right;

        if (moveXZ != Vector3.Zero)
            moveXZ.Normalize();

        var speed = IsFlying ? FlySpeed : WalkSpeed;

        if (IsFlying)
        {
            var vertical = 0f;
            if (input.IsKeyDown(Keys.Space)) vertical += 1f;
            if (input.IsKeyDown(Keys.LeftControl) || input.IsKeyDown(Keys.RightControl) || input.IsKeyDown(Keys.C)) vertical -= 1f;

            var move = new Vector3(moveXZ.X, vertical, moveXZ.Z);
            if (move.LengthSquared() > 1f)
                move.Normalize();

            var flyVel = move * speed;
            MoveWithCollisions(ref flyVel, dt, getBlock);
            Velocity = Vector3.Zero; // Reset velocity after movement to prevent accumulation
            IsGrounded = false;
            _moveIntent = move;
            return;
        }

        var vel = Velocity;
        vel.X = moveXZ.X * speed;
        vel.Z = moveXZ.Z * speed;
        var wasGrounded = IsGrounded;
        IsGrounded = false;

        var groundedNow = wasGrounded || IsGroundedCheck(Position, getBlock);
        if (input.IsNewKeyPress(Keys.Space) && groundedNow)
        {
            vel.Y = JumpSpeed;
            wasGrounded = false;
        }

        if (!wasGrounded)
            vel.Y += Gravity * dt;
        else
            vel.Y = Math.Min(vel.Y, 0f);
        _moveIntent = new Vector3(moveXZ.X, 0f, moveXZ.Z);

        MoveWithCollisions(ref vel, dt, getBlock);
        Velocity = vel;
    }

    private void MoveWithCollisions(ref Vector3 vel, float dt, Func<int, int, int, byte> getBlock)
    {
        var pos = Position;

        pos.X = MoveAxisX(pos, vel.X * dt, getBlock, ref vel);
        pos.Y = MoveAxisY(pos, vel.Y * dt, getBlock, ref vel);
        pos.Z = MoveAxisZ(pos, vel.Z * dt, getBlock, ref vel);

        if (!IsGrounded && vel.Y <= 0f && IsGroundedCheck(pos, getBlock))
            IsGrounded = true;

        Position = pos;
    }

    private static float MoveAxisX(Vector3 pos, float delta, Func<int, int, int, byte> getBlock, ref Vector3 vel)
    {
        if (delta == 0f)
            return pos.X;

        pos.X += delta;
        GetAabb(pos, out var min, out var max);

        var minX = (int)Math.Floor(min.X + Eps);
        var maxX = (int)Math.Floor(max.X - Eps);
        var minY = (int)Math.Floor(min.Y + Eps);
        var maxY = (int)Math.Floor(max.Y - Eps);
        var minZ = (int)Math.Floor(min.Z + Eps);
        var maxZ = (int)Math.Floor(max.Z - Eps);

        if (delta > 0f)
        {
            var x = maxX;
            for (var y = minY; y <= maxY; y++)
            for (var z = minZ; z <= maxZ; z++)
            {
                if (!BlockRegistry.IsSolid(getBlock(x, y, z)))
                    continue;
                pos.X = x - HalfWidth;
                vel.X = 0f;
                return pos.X;
            }
        }
        else
        {
            var x = minX;
            for (var y = minY; y <= maxY; y++)
            for (var z = minZ; z <= maxZ; z++)
            {
                if (!BlockRegistry.IsSolid(getBlock(x, y, z)))
                    continue;
                pos.X = x + 1 + HalfWidth;
                vel.X = 0f;
                return pos.X;
            }
        }

        return pos.X;
    }

    private float MoveAxisY(Vector3 pos, float delta, Func<int, int, int, byte> getBlock, ref Vector3 vel)
    {
        if (delta == 0f)
            return pos.Y;

        pos.Y += delta;
        GetAabb(pos, out var min, out var max);

        var minX = (int)Math.Floor(min.X + Eps);
        var maxX = (int)Math.Floor(max.X - Eps);
        var minY = (int)Math.Floor(min.Y + Eps);
        var maxY = (int)Math.Floor(max.Y - Eps);
        var minZ = (int)Math.Floor(min.Z + Eps);
        var maxZ = (int)Math.Floor(max.Z - Eps);

        if (delta > 0f)
        {
            var y = maxY;
            for (var x = minX; x <= maxX; x++)
            for (var z = minZ; z <= maxZ; z++)
            {
                if (!BlockRegistry.IsSolid(getBlock(x, y, z)))
                    continue;
                pos.Y = y - Height;
                vel.Y = 0f;
                return pos.Y;
            }
        }
        else
        {
            var y = minY;
            for (var x = minX; x <= maxX; x++)
            for (var z = minZ; z <= maxZ; z++)
            {
                if (!BlockRegistry.IsSolid(getBlock(x, y, z)))
                    continue;
                pos.Y = y + 1;
                vel.Y = 0f;
                IsGrounded = true;
                return pos.Y;
            }
        }

        return pos.Y;
    }

    private static float MoveAxisZ(Vector3 pos, float delta, Func<int, int, int, byte> getBlock, ref Vector3 vel)
    {
        if (delta == 0f)
            return pos.Z;

        pos.Z += delta;
        GetAabb(pos, out var min, out var max);

        var minX = (int)Math.Floor(min.X + Eps);
        var maxX = (int)Math.Floor(max.X - Eps);
        var minY = (int)Math.Floor(min.Y + Eps);
        var maxY = (int)Math.Floor(max.Y - Eps);
        var minZ = (int)Math.Floor(min.Z + Eps);
        var maxZ = (int)Math.Floor(max.Z - Eps);

        if (delta > 0f)
        {
            var z = maxZ;
            for (var y = minY; y <= maxY; y++)
            for (var x = minX; x <= maxX; x++)
            {
                if (!BlockRegistry.IsSolid(getBlock(x, y, z)))
                    continue;
                pos.Z = z - HalfWidth;
                vel.Z = 0f;
                return pos.Z;
            }
        }
        else
        {
            var z = minZ;
            for (var y = minY; y <= maxY; y++)
            for (var x = minX; x <= maxX; x++)
            {
                if (!BlockRegistry.IsSolid(getBlock(x, y, z)))
                    continue;
                pos.Z = z + 1 + HalfWidth;
                vel.Z = 0f;
                return pos.Z;
            }
        }

        return pos.Z;
    }

    private static bool IsGroundedCheck(Vector3 pos, Func<int, int, int, byte> getBlock)
    {
        GetAabb(pos, out var min, out var max);
        var minX = (int)Math.Floor(min.X + Eps);
        var maxX = (int)Math.Floor(max.X - Eps);
        var minZ = (int)Math.Floor(min.Z + Eps);
        var maxZ = (int)Math.Floor(max.Z - Eps);
        var y = (int)Math.Floor(pos.Y - Skin - 0.01f);
        for (var x = minX; x <= maxX; x++)
        for (var z = minZ; z <= maxZ; z++)
            if (BlockRegistry.IsSolid(getBlock(x, y, z)))
                return true;
        return false;
    }

    private static void GetAabb(Vector3 pos, out Vector3 min, out Vector3 max)
    {
        min = new Vector3(pos.X - HalfWidth + Skin, pos.Y + Skin, pos.Z - HalfWidth + Skin);
        max = new Vector3(pos.X + HalfWidth - Skin, pos.Y + Height - Skin, pos.Z + HalfWidth - Skin);
    }
}
