using System;

namespace LatticeVeilMonoGame.Core;

/// <summary>
/// Phase of the day based on time-of-day
/// </summary>
public enum TimePhase
{
    Dawn = 0,
    Day = 1,
    Dusk = 2,
    Night = 3
}

/// <summary>
/// Authoritative world time system for day/night cycle
/// Uses deterministic game-time representation with 40-minute real-time days
/// </summary>
public sealed class WorldTime
{
    // Time constants (2400 real seconds = 40 real minutes = 86400 game seconds)
    public const double RealSecondsPerDay = 2400.0;
    public const double GameSecondsPerDay = 86400.0;
    public const double GameSecondsPerRealSecond = 36.0;
    
    // Phase boundaries (in game seconds 0-86400)
    public const double DawnStart = 5.0 * 3600.0;  // 05:00
    public const double DayStart = 6.0 * 3600.0;   // 06:00
    public const double DuskStart = 18.0 * 3600.0; // 18:00
    public const double NightStart = 19.0 * 3600.0; // 19:00
    
    // Sleep window (19:00 to 05:00)
    public const double SleepWindowStart = NightStart; // 19:00
    public const double SleepWindowEnd = DawnStart;     // 05:00
    
    // Initial world time (Day 1, 12:00 PM - stable daytime)
    public const double InitialTotalGameSeconds = 12.0 * 3600.0;
    
    /// <summary>
    /// Authoritative total elapsed game time in seconds
    /// 0 = beginning of Day 1, 86400 = beginning of Day 2, etc.
    /// </summary>
    public double TotalElapsedGameSeconds { get; private set; }
    
    /// <summary>
    /// Whether time progression is enabled
    /// </summary>
    public bool TimeEnabled { get; set; } = true;
    

    
    public WorldTime()
    {
        TotalElapsedGameSeconds = InitialTotalGameSeconds;
        LogTimeState("WorldTime initialized");
    }
    
    public WorldTime(double totalElapsedGameSeconds)
    {
        TotalElapsedGameSeconds = Math.Max(0.0, totalElapsedGameSeconds);
        LogTimeState("WorldTime constructed with custom time");
    }
    
    /// <summary>
    /// Advance world time by the given delta time (real seconds)
    /// </summary>
    public void Tick(float deltaTimeSeconds)
    {
        if (!TimeEnabled || deltaTimeSeconds <= 0f)
            return;
        
        var previousPhase = Phase;
        var previousDay = DayNumber;
        
        TotalElapsedGameSeconds += deltaTimeSeconds * GameSecondsPerRealSecond;
        
        var currentPhase = Phase;
        var currentDay = DayNumber;
        
        // Log phase transitions
        if (currentPhase != previousPhase)
        {
            System.Diagnostics.Debug.WriteLine($"[LIGHTING DIAGNOSTIC] WorldTime phase transition: {previousPhase} -> {currentPhase} at {GetFormattedTime()}");
        }
        
        // Log day transitions
        if (currentDay != previousDay)
        {
            System.Diagnostics.Debug.WriteLine($"[LIGHTING DIAGNOSTIC] WorldTime day transition: Day {previousDay} -> Day {currentDay} at {GetFormattedTime()}");
        }
    }
    
    /// <summary>
    /// Set world time directly (for sleep operations or loading)
    /// </summary>
    public void SetTime(double totalElapsedGameSeconds)
    {
        var previousTime = TotalElapsedGameSeconds;
        var previousPhase = Phase;
        var previousDay = DayNumber;
        
        TotalElapsedGameSeconds = Math.Max(0.0, totalElapsedGameSeconds);
        
        var currentPhase = Phase;
        var currentDay = DayNumber;
        
        System.Diagnostics.Debug.WriteLine($"[LIGHTING DIAGNOSTIC] WorldTime.SetTime: {previousTime:F2}s -> {TotalElapsedGameSeconds:F2}s, Phase: {previousPhase} -> {currentPhase}, Day: {previousDay} -> {currentDay}");
    }
    
    private void LogTimeState(string context)
    {
        System.Diagnostics.Debug.WriteLine($"[LIGHTING DIAGNOSTIC] {context}: TotalSeconds={TotalElapsedGameSeconds:F2}, TimeOfDay={TimeOfDaySeconds:F2}, Phase={Phase}, Day={DayNumber}, Formatted={GetFormattedTime()}, LegacyTicks={GameSecondsToLegacyTicks(TotalElapsedGameSeconds)}");
    }
    
    /// <summary>
    /// Get current day number (1-indexed)
    /// </summary>
    public int DayNumber => (int)(TotalElapsedGameSeconds / GameSecondsPerDay) + 1;
    
    /// <summary>
    /// Get current time-of-day in seconds (0-86400)
    /// </summary>
    public double TimeOfDaySeconds => TotalElapsedGameSeconds % GameSecondsPerDay;
    
    /// <summary>
    /// Get current hour (0-23)
    /// </summary>
    public int Hour => (int)(TimeOfDaySeconds / 3600.0);
    
    /// <summary>
    /// Get current minute (0-59)
    /// </summary>
    public int Minute => (int)((TimeOfDaySeconds % 3600.0) / 60.0);
    
    /// <summary>
    /// Get current second (0-59)
    /// </summary>
    public int Second => (int)(TimeOfDaySeconds % 60.0);
    
    /// <summary>
    /// Get current time phase
    /// </summary>
    public TimePhase Phase => CalculatePhase(TimeOfDaySeconds);
    
    /// <summary>
    /// Get formatted time string (12-hour format: "Day 1 - 5:00 AM")
    /// </summary>
    public string GetFormattedTime()
    {
        var hour = Hour;
        var minute = Minute;
        var isPm = hour >= 12;
        var displayHour = hour % 12;
        if (displayHour == 0) displayHour = 12;
        var amPm = isPm ? "PM" : "AM";
        return $"Day {DayNumber} - {displayHour}:{minute:D2} {amPm}";
    }
    
    /// <summary>
    /// Check if current time is within sleep window (19:00-05:00)
    /// </summary>
    public bool IsSleepWindow => IsInSleepWindow(TimeOfDaySeconds);
    
    /// <summary>
    /// Calculate the target time for sleep (next day 05:00)
    /// </summary>
    public double CalculateSleepTarget()
    {
        var currentDay = (int)(TotalElapsedGameSeconds / GameSecondsPerDay);
        var nextDayDawn = (currentDay + 1) * GameSecondsPerDay + DawnStart;
        return nextDayDawn;
    }
    
    /// <summary>
    /// Perform sleep time skip to next dawn
    /// </summary>
    public void SleepToNextDawn()
    {
        SetTime(CalculateSleepTarget());
    }
    
    /// <summary>
    /// Convert legacy ticks (0-24000) to game seconds
    /// </summary>
    public static double LegacyTicksToGameSeconds(int ticks)
    {
        return (ticks / 24000.0) * GameSecondsPerDay;
    }
    
    /// <summary>
    /// Convert game seconds to legacy ticks (0-24000)
    /// </summary>
    public static int GameSecondsToLegacyTicks(double gameSeconds)
    {
        var timeOfDay = gameSeconds % GameSecondsPerDay;
        return (int)((timeOfDay / GameSecondsPerDay) * 24000.0);
    }
    
    /// <summary>
    /// Calculate phase from time-of-day seconds
    /// </summary>
    private static TimePhase CalculatePhase(double timeOfDay)
    {
        if (timeOfDay >= DawnStart && timeOfDay < DayStart)
            return TimePhase.Dawn;
        if (timeOfDay >= DayStart && timeOfDay < DuskStart)
            return TimePhase.Day;
        if (timeOfDay >= DuskStart && timeOfDay < NightStart)
            return TimePhase.Dusk;
        return TimePhase.Night;
    }
    
    /// <summary>
    /// Check if given time-of-day is in sleep window
    /// </summary>
    private static bool IsInSleepWindow(double timeOfDay)
    {
        // Sleep window: 19:00-05:00 (wraps around midnight)
        return timeOfDay >= SleepWindowStart || timeOfDay < SleepWindowEnd;
    }
}