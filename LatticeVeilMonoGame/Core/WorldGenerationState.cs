using System;

namespace LatticeVeilMonoGame.Core;

public sealed class WorldGenerationState
{
    public const string StatusInProgress = "in_progress";
    public const string StatusPaused = "paused";
    public const string StatusCompleted = "completed";
    public const string StatusFailed = "failed";

    public int Version { get; set; } = 1;
    public string Status { get; set; } = StatusInProgress;
    public string Stage { get; set; } = "PREPARING";
    public float Progress { get; set; }
    public string LastHeartbeatUtc { get; set; } = DateTimeOffset.UtcNow.ToString("O");
    public int WorldSeed { get; set; }
    public string WorldType { get; set; } = "terrain";
    public string Generator { get; set; } = "terrain";
    public string ErrorReason { get; set; } = string.Empty;
    public string WorldName { get; set; } = string.Empty;

    public DateTimeOffset? ParseHeartbeatUtc()
    {
        return DateTimeOffset.TryParse(LastHeartbeatUtc, out var parsed) ? parsed : null;
    }

    public bool IsCompleted
        => string.Equals(Status, StatusCompleted, StringComparison.OrdinalIgnoreCase);

    public bool IsInProgress
        => string.Equals(Status, StatusInProgress, StringComparison.OrdinalIgnoreCase);

    public bool IsPaused
        => string.Equals(Status, StatusPaused, StringComparison.OrdinalIgnoreCase);
}
