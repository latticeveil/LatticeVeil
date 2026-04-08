namespace LatticeVeilMonoGame.Core;

public readonly record struct DifficultyTuning(
    float IdleHungerIntervalSeconds,
    float WalkHungerIntervalSeconds,
    float SprintHungerIntervalSeconds,
    float RegenerationIntervalSeconds,
    int RegenerationThreshold,
    int StarvationHealthFloor,
    bool AutoRefillHunger,
    bool StarvationEnabled);

public static class WorldDifficulty
{
    public static int Clamp(int difficulty) => System.Math.Clamp(difficulty, 0, 3);

    public static string GetClassicLabel(int difficulty)
    {
        return Clamp(difficulty) switch
        {
            0 => "Peaceful",
            1 => "Easy",
            2 => "Normal",
            3 => "Hard",
            _ => "Normal"
        };
    }

    public static string GetDisplayLabel(int difficulty)
    {
        return Clamp(difficulty) switch
        {
            0 => "Stillwater (Peaceful)",
            1 => "Wayfarer (Easy)",
            2 => "Veilbound (Normal)",
            3 => "Ruinwake (Hard)",
            _ => "Veilbound (Normal)"
        };
    }

    public static string GetDescription(int difficulty)
    {
        return Clamp(difficulty) switch
        {
            0 => "Hunger refills, health regenerates quickly, starvation disabled",
            1 => "Slower hunger drain, easier recovery, starvation stops at 10 HP",
            2 => "Standard survival tuning, starvation stops at 1 HP",
            3 => "Faster hunger drain, slow recovery, starvation can kill",
            _ => "Standard survival tuning, starvation stops at 1 HP"
        };
    }

    public static DifficultyTuning GetTuning(int difficulty)
    {
        return Clamp(difficulty) switch
        {
            0 => new DifficultyTuning(
                IdleHungerIntervalSeconds: float.PositiveInfinity,
                WalkHungerIntervalSeconds: float.PositiveInfinity,
                SprintHungerIntervalSeconds: float.PositiveInfinity,
                RegenerationIntervalSeconds: 1.5f,
                RegenerationThreshold: 0,
                StarvationHealthFloor: SurvivalVitals.MaxHealth,
                AutoRefillHunger: true,
                StarvationEnabled: false),
            1 => new DifficultyTuning(
                IdleHungerIntervalSeconds: 110f,
                WalkHungerIntervalSeconds: 55f,
                SprintHungerIntervalSeconds: 28f,
                RegenerationIntervalSeconds: 6f,
                RegenerationThreshold: 16,
                StarvationHealthFloor: 10,
                AutoRefillHunger: false,
                StarvationEnabled: true),
            2 => new DifficultyTuning(
                IdleHungerIntervalSeconds: 90f,
                WalkHungerIntervalSeconds: 45f,
                SprintHungerIntervalSeconds: 22f,
                RegenerationIntervalSeconds: 8f,
                RegenerationThreshold: 18,
                StarvationHealthFloor: 1,
                AutoRefillHunger: false,
                StarvationEnabled: true),
            3 => new DifficultyTuning(
                IdleHungerIntervalSeconds: 75f,
                WalkHungerIntervalSeconds: 38f,
                SprintHungerIntervalSeconds: 18f,
                RegenerationIntervalSeconds: 10f,
                RegenerationThreshold: 19,
                StarvationHealthFloor: 0,
                AutoRefillHunger: false,
                StarvationEnabled: true),
            _ => GetTuning(2)
        };
    }
}
