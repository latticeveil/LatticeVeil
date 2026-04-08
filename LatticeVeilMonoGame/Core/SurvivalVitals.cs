namespace LatticeVeilMonoGame.Core;

public readonly record struct SurvivalTickResult(bool HealthChanged, bool HungerChanged)
{
    public bool AnyChange => HealthChanged || HungerChanged;
}

public sealed class SurvivalVitals
{
    public const int MaxHealth = 20;
    public const int MaxHunger = 20;
    public const float StarvationIntervalSeconds = 4f;
    public const float SafeFallDistance = 4.25f;

    private float _hungerTickSeconds;
    private float _regenTickSeconds;
    private float _starvationTickSeconds;

    public int Health { get; private set; } = MaxHealth;
    public int Hunger { get; private set; } = MaxHunger;
    public bool IsDead => Health <= 0;

    public void Load(int health, int hunger)
    {
        Health = ClampHealth(health);
        Hunger = ClampHunger(hunger);
        ResetTransientTimers();
    }

    public void RestoreToFull()
    {
        Health = MaxHealth;
        Hunger = MaxHunger;
        ResetTransientTimers();
    }

    public SurvivalTickResult Tick(float dt, bool isMoving, bool isSprinting, int difficulty)
    {
        if (dt <= 0f || IsDead)
            return default;

        var healthChanged = false;
        var hungerChanged = false;
        var tuning = WorldDifficulty.GetTuning(difficulty);

        if (tuning.AutoRefillHunger && Hunger < MaxHunger)
        {
            Hunger = MaxHunger;
            hungerChanged = true;
            _hungerTickSeconds = 0f;
        }
        else
        {
            _hungerTickSeconds += dt;
            var hungerInterval = isSprinting && isMoving
                ? tuning.SprintHungerIntervalSeconds
                : isMoving
                    ? tuning.WalkHungerIntervalSeconds
                    : tuning.IdleHungerIntervalSeconds;

            while (_hungerTickSeconds >= hungerInterval)
            {
                _hungerTickSeconds -= hungerInterval;
                if (Hunger <= 0)
                    break;

                Hunger = ClampHunger(Hunger - 1);
                hungerChanged = true;
            }
        }

        var canRegenerate = Hunger >= tuning.RegenerationThreshold && Health < MaxHealth;
        if (canRegenerate)
        {
            _regenTickSeconds += dt;
            while (_regenTickSeconds >= tuning.RegenerationIntervalSeconds && Health < MaxHealth)
            {
                _regenTickSeconds -= tuning.RegenerationIntervalSeconds;
                Health = ClampHealth(Health + 1);
                healthChanged = true;
            }

            _starvationTickSeconds = 0f;
        }
        else if (Hunger <= 0 && tuning.StarvationEnabled)
        {
            _starvationTickSeconds += dt;
            while (_starvationTickSeconds >= StarvationIntervalSeconds && Health > tuning.StarvationHealthFloor)
            {
                _starvationTickSeconds -= StarvationIntervalSeconds;
                Health = ClampHealth(Health - 1);
                healthChanged = true;
            }

            _regenTickSeconds = 0f;
        }
        else
        {
            _regenTickSeconds = 0f;
            _starvationTickSeconds = 0f;
        }

        return new SurvivalTickResult(healthChanged, hungerChanged);
    }

    public bool RestoreHealth(int amount)
    {
        if (amount <= 0 || Health >= MaxHealth)
            return false;

        Health = ClampHealth(Health + amount);
        return true;
    }

    public bool RestoreHunger(int amount)
    {
        if (amount <= 0 || Hunger >= MaxHunger)
            return false;

        Hunger = ClampHunger(Hunger + amount);
        return true;
    }

    public bool ApplyDamage(int amount)
    {
        if (amount <= 0 || IsDead)
            return false;

        Health = ClampHealth(Health - amount);
        _regenTickSeconds = 0f;
        return true;
    }

    public int CalculateFallDamage(float fallDistanceBlocks)
    {
        if (fallDistanceBlocks <= SafeFallDistance)
            return 0;

        return (int)System.MathF.Ceiling(fallDistanceBlocks - SafeFallDistance);
    }

    private void ResetTransientTimers()
    {
        _hungerTickSeconds = 0f;
        _regenTickSeconds = 0f;
        _starvationTickSeconds = 0f;
    }

    private static int ClampHealth(int value) => System.Math.Clamp(value, 0, MaxHealth);
    private static int ClampHunger(int value) => System.Math.Clamp(value, 0, MaxHunger);
}
