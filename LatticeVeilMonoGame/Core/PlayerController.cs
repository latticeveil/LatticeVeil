using System;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;

namespace LatticeVeilMonoGame.Core;

public sealed class PlayerController
{
    private const float HalfWidth = Scale.PlayerWidth * 0.5f;
    private const float StandingHeight = Scale.PlayerHeight;
    private const float SneakHeight = Scale.PlayerHeight - 0.5f;
    private const float StandingHeadHeight = Scale.PlayerHeadHeight;
    private const float SneakHeadHeight = Scale.PlayerHeadHeight - 0.5f;
    private const float SneakMoveMultiplier = 0.58f;
    private const float SprintMoveMultiplier = 1.35f;
    private const float Skin = 0.001f;
    private const float Gravity = -25f;
    private const float JumpSpeed = 8f;
    private const float WalkSpeed = 6f * Scale.BlockSize;
    private const float BaseFlySpeed = 18f * Scale.BlockSize;
    private const float MinFlySpeedMultiplier = 0.35f;
    private const float MaxFlySpeedMultiplier = 3.00f;
    private const float FlySpeedAdjustStep = 0.10f;
    private const float MaxCollisionStep = 0.25f;
    private const double DoubleTapSeconds = 0.30;
    private const float ExternalImpulseDecay = 34f * Scale.BlockSize;
    private const float Eps = 0.001f;

    private double _lastSpaceTapTime;
    private Vector3 _moveIntent;
    private float _swayAccumulator;
    private float _flySpeedMultiplier = 1.0f;
    private Vector2 _externalImpulseVelocity;
    private bool _toggleSneakLatched;
    private bool _sprintLatched;

    public Vector3 Position { get; set; } = new(8f, 6f, -12f);
    public Vector3 Velocity { get; set; }
    public float Yaw { get; set; } = MathHelper.PiOver2;
    public float Pitch { get; set; } = -0.25f;
    public bool IsGrounded { get; private set; }
    public bool IsFlying { get; private set; }
    public bool IsSneaking { get; private set; }
    public bool IsSprinting { get; private set; }
    public Vector3 MoveIntent => _moveIntent;
    public bool AllowFlying { get; set; } = true;
    public bool AllowCrouch { get; set; } = true;
    public bool ToggleCrouchEnabled { get; set; }
    public bool SprintLatchEnabled { get; set; }
    public bool NoClipEnabled { get; set; }
    public bool SuppressLookInput { get; set; }
    public float FlySpeedMultiplier => _flySpeedMultiplier;
    public float FlySpeedMinMultiplier => MinFlySpeedMultiplier;
    public float FlySpeedMaxMultiplier => MaxFlySpeedMultiplier;
    public float FlySpeedNormalized => (_flySpeedMultiplier - MinFlySpeedMultiplier) / (MaxFlySpeedMultiplier - MinFlySpeedMultiplier);
    public float CurrentFlySpeed => BaseFlySpeed * _flySpeedMultiplier;
    public float ColliderHeight => IsSneaking ? SneakHeight : StandingHeight;
    public Keys CrouchKey { get; set; } = Keys.LeftShift;
    public Keys SprintKey { get; set; } = Keys.LeftControl;
    public Keys FlyDescendKey { get; set; } = Keys.LeftShift;
    public Keys MoveUpKey { get; set; } = Keys.W;
    public Keys MoveDownKey { get; set; } = Keys.S;
    public Keys MoveLeftKey { get; set; } = Keys.A;
    public Keys MoveRightKey { get; set; } = Keys.D;
    public Keys JumpKey { get; set; } = Keys.Space;
    public string? MoveUpMouseBind { get; set; }
    public string? MoveDownMouseBind { get; set; }
    public string? MoveLeftMouseBind { get; set; }
    public string? MoveRightMouseBind { get; set; }
    public string? JumpMouseBind { get; set; }
    public string? CrouchMouseBind { get; set; }
    public string? SprintMouseBind { get; set; }
    public string? FlyDescendMouseBind { get; set; }

    public const float ColliderHalfWidth = HalfWidth;
    public const float ColliderStandingHeight = StandingHeight;

    public void SetFlying(bool value)
    {
        IsFlying = value;
        if (value)
        {
            IsSneaking = false;
            _toggleSneakLatched = false;
        }
        _sprintLatched = false;
        IsSprinting = false;
        Velocity = Vector3.Zero;
        _externalImpulseVelocity = Vector2.Zero;
    }

    public bool AdjustFlySpeedMultiplier(int wheelStep)
    {
        if (wheelStep == 0)
            return false;

        var next = Math.Clamp(_flySpeedMultiplier + wheelStep * FlySpeedAdjustStep, MinFlySpeedMultiplier, MaxFlySpeedMultiplier);
        if (MathF.Abs(next - _flySpeedMultiplier) < 0.0001f)
            return false;

        _flySpeedMultiplier = next;
        return true;
    }

    public Vector3 HeadOffset
    {
        get
        {
            var offset = new Vector3(0f, IsSneaking ? SneakHeadHeight : StandingHeadHeight, 0f);
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

    public void AddImpulse(Vector3 impulse)
    {
        _externalImpulseVelocity += new Vector2(impulse.X, impulse.Z);

        if (impulse.Y != 0f)
        {
            var velocity = Velocity;
            velocity.Y = Math.Max(velocity.Y, 0f) + impulse.Y;
            Velocity = velocity;
            IsGrounded = false;
        }
    }

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
        if (SuppressLookInput)
            return;

        var delta = input.LookDelta;
        if (delta.X != 0f || delta.Y != 0f)
        {
            Yaw += delta.X;
            Pitch -= delta.Y;
            Pitch = Math.Clamp(Pitch, -1.4f, 1.4f);
            Yaw = MathHelper.WrapAngle(Yaw);
        }
    }

    private void HandleFlyToggle(double nowSeconds, InputState input)
    {
        if (!AllowFlying || !IsBoundNewPress(input, JumpKey, JumpMouseBind))
            return;

        if (nowSeconds - _lastSpaceTapTime <= DoubleTapSeconds)
        {
            IsFlying = !IsFlying;
            if (IsFlying)
                IsSneaking = false;
            if (IsFlying)
                _sprintLatched = false;
            Velocity = Vector3.Zero;
        }

        _lastSpaceTapTime = nowSeconds;
    }

    private void ApplyMovement(float dt, InputState input, Func<int, int, int, byte> getBlock)
    {
        var forward = new Vector3((float)Math.Cos(Yaw), 0f, (float)Math.Sin(Yaw));
        var right = new Vector3(-forward.Z, 0f, forward.X);
        var moveXZ = Vector3.Zero;

        if (IsBoundDown(input, MoveUpKey, MoveUpMouseBind)) moveXZ += forward;
        if (IsBoundDown(input, MoveDownKey, MoveDownMouseBind)) moveXZ -= forward;
        if (IsBoundDown(input, MoveLeftKey, MoveLeftMouseBind)) moveXZ -= right;
        if (IsBoundDown(input, MoveRightKey, MoveRightMouseBind)) moveXZ += right;

        if (moveXZ != Vector3.Zero)
            moveXZ.Normalize();

        if (IsFlying)
        {
            IsSprinting = false;
            IsSneaking = false;
            _toggleSneakLatched = false;
            _sprintLatched = false;

            var vertical = 0f;
            if (IsBoundDown(input, JumpKey, JumpMouseBind)) vertical += 1f;
            if (IsControlDown(input, FlyDescendKey, FlyDescendMouseBind)) vertical -= 1f;

            var move = new Vector3(moveXZ.X, vertical, moveXZ.Z);
            if (move.LengthSquared() > 1f)
                move.Normalize();

            if (NoClipEnabled)
            {
                var impulse = new Vector3(_externalImpulseVelocity.X, 0f, _externalImpulseVelocity.Y);
                Position += (move * CurrentFlySpeed + impulse) * dt;
                Velocity = Vector3.Zero;
                IsGrounded = false;
                _moveIntent = move;
                DecayExternalImpulse(dt);
                return;
            }

            var flyVel = move * CurrentFlySpeed + new Vector3(_externalImpulseVelocity.X, 0f, _externalImpulseVelocity.Y);
            MoveWithCollisions(ref flyVel, dt, getBlock);
            Velocity = Vector3.Zero;
            IsGrounded = false;
            _moveIntent = move;
            DecayExternalImpulse(dt);
            return;
        }

        if (!AllowCrouch)
        {
            IsSneaking = false;
            _toggleSneakLatched = false;
        }
        else if (ToggleCrouchEnabled)
        {
            if (IsControlNewPress(input, CrouchKey, CrouchMouseBind))
            {
                if (_toggleSneakLatched)
                {
                    if (CanStandUp(Position, getBlock))
                    {
                        IsSneaking = false;
                        _toggleSneakLatched = false;
                    }
                }
                else
                {
                    IsSneaking = true;
                    _toggleSneakLatched = true;
                }
            }
        }
        else
        {
            _toggleSneakLatched = false;
            var crouchRequested = IsControlDown(input, CrouchKey, CrouchMouseBind);
            if (crouchRequested)
            {
                IsSneaking = true;
            }
            else if (IsSneaking)
            {
                if (CanStandUp(Position, getBlock))
                    IsSneaking = false;
            }
        }

        var vel = Velocity;
        var speed = WalkSpeed;
        if (IsSneaking)
        {
            speed *= SneakMoveMultiplier;
            _sprintLatched = false;
        }

        var groundedNow = IsGrounded || IsGroundedCheck(Position, getBlock, ColliderHeight);
        var hasMoveInput = moveXZ != Vector3.Zero;
        if (!hasMoveInput)
            _sprintLatched = false;

        if (!SprintLatchEnabled)
        {
            _sprintLatched = false;
        }
        else if (IsControlNewPress(input, SprintKey, SprintMouseBind) && !IsSneaking && !IsFlying)
        {
            // Arm sprint even before movement begins so sprint starts as soon as motion resumes.
            _sprintLatched = true;
        }

        var canSprint = groundedNow
            && !IsSneaking
            && hasMoveInput
            && (SprintLatchEnabled ? _sprintLatched : IsControlDown(input, SprintKey, SprintMouseBind));
        IsSprinting = canSprint;
        if (IsSprinting)
            speed *= SprintMoveMultiplier;

        vel.X = moveXZ.X * speed + _externalImpulseVelocity.X;
        vel.Z = moveXZ.Z * speed + _externalImpulseVelocity.Y;
        var wasGrounded = IsGrounded;
        IsGrounded = false;

        if (IsBoundNewPress(input, JumpKey, JumpMouseBind) && groundedNow)
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
        DecayExternalImpulse(dt);
    }

    private void DecayExternalImpulse(float dt)
    {
        _externalImpulseVelocity = DecayVector(_externalImpulseVelocity, ExternalImpulseDecay * dt);
    }

    private static Vector2 DecayVector(Vector2 value, float amount)
    {
        var length = value.Length();
        if (length <= Eps)
            return Vector2.Zero;

        var next = Math.Max(0f, length - amount);
        if (next <= Eps)
            return Vector2.Zero;

        return value * (next / length);
    }

    private void MoveWithCollisions(ref Vector3 vel, float dt, Func<int, int, int, byte> getBlock)
    {
        var pos = Position;
        var height = ColliderHeight;
        var maxDelta = MathF.Max(MathF.Abs(vel.X * dt), MathF.Max(MathF.Abs(vel.Y * dt), MathF.Abs(vel.Z * dt)));
        var steps = Math.Max(1, (int)MathF.Ceiling(maxDelta / MaxCollisionStep));
        var stepDt = dt / steps;

        for (var i = 0; i < steps; i++)
        {
            pos.X = MoveAxisX(pos, vel.X * stepDt, getBlock, ref vel, height);
            pos.Y = MoveAxisY(pos, vel.Y * stepDt, getBlock, ref vel, height);
            pos.Z = MoveAxisZ(pos, vel.Z * stepDt, getBlock, ref vel, height);
        }

        if (!IsGrounded && vel.Y <= 0f && IsGroundedCheck(pos, getBlock, height))
            IsGrounded = true;

        Position = pos;
    }

    private float MoveAxisX(Vector3 pos, float delta, Func<int, int, int, byte> getBlock, ref Vector3 vel, float height)
    {
        if (delta == 0f)
            return pos.X;

        pos.X += delta;
        GetAabb(pos, height, out var min, out var max);

        var minX = (int)Math.Floor(min.X + Eps);
        var maxX = (int)Math.Floor(max.X - Eps);
        var minY = (int)Math.Floor(min.Y + Eps);
        var maxY = (int)Math.Floor(max.Y - Eps);
        var minZ = (int)Math.Floor(min.Z + Eps);
        var maxZ = (int)Math.Floor(max.Z - Eps);

        // Anti-fall check: if crouching and not flying, prevent walking off edges
        if (IsSneaking && !IsFlying && IsGrounded)
        {
            var testPos = new Vector3(pos.X, Position.Y, Position.Z);
            if (!HasSolidGroundBelow(testPos, getBlock))
            {
                // Don't allow movement - there's no solid ground below
                vel.X = 0f;
                return Position.X; // Return original position
            }
        }

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

    private float MoveAxisY(Vector3 pos, float delta, Func<int, int, int, byte> getBlock, ref Vector3 vel, float height)
    {
        if (delta == 0f)
            return pos.Y;

        pos.Y += delta;
        GetAabb(pos, height, out var min, out var max);

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
                pos.Y = y - height;
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

    private float MoveAxisZ(Vector3 pos, float delta, Func<int, int, int, byte> getBlock, ref Vector3 vel, float height)
    {
        if (delta == 0f)
            return pos.Z;

        pos.Z += delta;
        GetAabb(pos, height, out var min, out var max);

        var minX = (int)Math.Floor(min.X + Eps);
        var maxX = (int)Math.Floor(max.X - Eps);
        var minY = (int)Math.Floor(min.Y + Eps);
        var maxY = (int)Math.Floor(max.Y - Eps);
        var minZ = (int)Math.Floor(min.Z + Eps);
        var maxZ = (int)Math.Floor(max.Z - Eps);

        // Anti-fall check: if crouching and not flying, prevent walking off edges
        if (IsSneaking && !IsFlying && IsGrounded)
        {
            var testPos = new Vector3(Position.X, Position.Y, pos.Z);
            if (!HasSolidGroundBelow(testPos, getBlock))
            {
                // Don't allow movement - there's no solid ground below
                vel.Z = 0f;
                return Position.Z; // Return original position
            }
        }

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

    private bool HasSolidGroundBelow(Vector3 pos, Func<int, int, int, byte> getBlock)
    {
        // Check blocks just below the player's feet
        var checkY = (int)Math.Floor(pos.Y - 0.1f); // Slightly below feet
        var minX = (int)Math.Floor(pos.X - HalfWidth + Eps);
        var maxX = (int)Math.Floor(pos.X + HalfWidth - Eps);
        var minZ = (int)Math.Floor(pos.Z - HalfWidth + Eps);
        var maxZ = (int)Math.Floor(pos.Z + HalfWidth - Eps);

        for (var x = minX; x <= maxX; x++)
        for (var z = minZ; z <= maxZ; z++)
        {
            if (BlockRegistry.IsSolid(getBlock(x, checkY, z)))
                return true;
        }
        return false;
    }

    private bool IsGroundedCheck(Vector3 pos, Func<int, int, int, byte> getBlock, float height)
    {
        GetAabb(pos, height, out var min, out var max);
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

    private bool CanStandUp(Vector3 pos, Func<int, int, int, byte> getBlock)
    {
        GetAabb(pos, StandingHeight, out var min, out var max);
        var minX = (int)Math.Floor(min.X + Eps);
        var maxX = (int)Math.Floor(max.X - Eps);
        var minY = (int)Math.Floor(min.Y + Eps);
        var maxY = (int)Math.Floor(max.Y - Eps);
        var minZ = (int)Math.Floor(min.Z + Eps);
        var maxZ = (int)Math.Floor(max.Z - Eps);

        for (var x = minX; x <= maxX; x++)
        for (var y = minY; y <= maxY; y++)
        for (var z = minZ; z <= maxZ; z++)
            if (BlockRegistry.IsSolid(getBlock(x, y, z)))
                return false;

        return true;
    }

    private static void GetAabb(Vector3 pos, float colliderHeight, out Vector3 min, out Vector3 max)
    {
        min = new Vector3(pos.X - HalfWidth + Skin, pos.Y + Skin, pos.Z - HalfWidth + Skin);
        max = new Vector3(pos.X + HalfWidth - Skin, pos.Y + colliderHeight - Skin, pos.Z + HalfWidth - Skin);
    }

    private static bool IsControlDown(InputState input, Keys key, string? mouseBind = null)
    {
        if (IsMouseBindDown(input, mouseBind))
            return true;
        return key switch
        {
            Keys.LeftShift or Keys.RightShift => input.IsKeyDown(Keys.LeftShift) || input.IsKeyDown(Keys.RightShift),
            Keys.LeftControl or Keys.RightControl => input.IsKeyDown(Keys.LeftControl) || input.IsKeyDown(Keys.RightControl),
            Keys.LeftAlt or Keys.RightAlt => input.IsKeyDown(Keys.LeftAlt) || input.IsKeyDown(Keys.RightAlt),
            _ => input.IsKeyDown(key)
        };
    }

    private static bool IsControlNewPress(InputState input, Keys key, string? mouseBind = null)
    {
        if (IsMouseBindNewPress(input, mouseBind))
            return true;
        return key switch
        {
            Keys.LeftShift or Keys.RightShift => input.IsNewKeyPress(Keys.LeftShift) || input.IsNewKeyPress(Keys.RightShift),
            Keys.LeftControl or Keys.RightControl => input.IsNewKeyPress(Keys.LeftControl) || input.IsNewKeyPress(Keys.RightControl),
            Keys.LeftAlt or Keys.RightAlt => input.IsNewKeyPress(Keys.LeftAlt) || input.IsNewKeyPress(Keys.RightAlt),
            _ => input.IsNewKeyPress(key)
        };
    }

    private static bool IsBoundDown(InputState input, Keys key, string? mouseBind)
        => IsControlDown(input, key, mouseBind);

    private static bool IsBoundNewPress(InputState input, Keys key, string? mouseBind)
        => IsControlNewPress(input, key, mouseBind);

    private static bool IsMouseBindDown(InputState input, string? mouseBind)
    {
        return NormalizeMouseBind(mouseBind) switch
        {
            "mouseleft" => input.IsLeftDown(),
            "mouseright" => input.IsRightDown(),
            "mousemiddle" => input.IsMiddleDown(),
            "mousex1" => input.IsXButton1Down(),
            "mousex2" => input.IsXButton2Down(),
            _ => false
        };
    }

    private static bool IsMouseBindNewPress(InputState input, string? mouseBind)
    {
        return NormalizeMouseBind(mouseBind) switch
        {
            "mouseleft" => input.IsNewLeftClick(),
            "mouseright" => input.IsNewRightClick(),
            "mousemiddle" => input.IsNewMiddleClick(),
            "mousex1" => input.IsNewXButton1Click(),
            "mousex2" => input.IsNewXButton2Click(),
            _ => false
        };
    }

    private static string NormalizeMouseBind(string? mouseBind)
        => (mouseBind ?? string.Empty).Trim().ToLowerInvariant();
}
