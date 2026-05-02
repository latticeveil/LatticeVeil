using System;
using System.Collections.Generic;
using System.IO;

namespace LatticeVeilMonoGame.Core;

public enum WorldStorageBudgetState
{
    Normal = 0,
    Warn = 1,
    Conserve = 2,
    OverTarget = 3
}

public readonly struct WorldStorageBudgetThresholds
{
    public WorldStorageBudgetThresholds(long warnAtBytes, long conserveAtBytes, long targetMaxBytes)
    {
        WarnAtBytes = Math.Max(0, warnAtBytes);
        ConserveAtBytes = Math.Max(WarnAtBytes, conserveAtBytes);
        TargetMaxBytes = Math.Max(ConserveAtBytes, targetMaxBytes);
    }

    public long WarnAtBytes { get; }
    public long ConserveAtBytes { get; }
    public long TargetMaxBytes { get; }
}

public readonly struct WorldStorageBudgetSnapshot
{
    public WorldStorageBudgetSnapshot(long sizeBytes, WorldStorageBudgetThresholds thresholds)
    {
        SizeBytes = Math.Max(0, sizeBytes);
        Thresholds = thresholds;
        State = SizeBytes >= thresholds.TargetMaxBytes
            ? WorldStorageBudgetState.OverTarget
            : SizeBytes >= thresholds.ConserveAtBytes
                ? WorldStorageBudgetState.Conserve
                : SizeBytes >= thresholds.WarnAtBytes
                    ? WorldStorageBudgetState.Warn
                    : WorldStorageBudgetState.Normal;
    }

    public long SizeBytes { get; }
    public WorldStorageBudgetThresholds Thresholds { get; }
    public WorldStorageBudgetState State { get; }
    public bool IsWarn => State >= WorldStorageBudgetState.Warn;
    public bool IsConserve => State >= WorldStorageBudgetState.Conserve;
    public bool IsOverTarget => State >= WorldStorageBudgetState.OverTarget;
}

public static class WorldStorageBudgetService
{
    // 1GB target, with warn/conserve thresholds requested by design.
    public static readonly WorldStorageBudgetThresholds DefaultThresholds = new(
        warnAtBytes: 900L * 1024L * 1024L,
        conserveAtBytes: 960L * 1024L * 1024L,
        targetMaxBytes: 1024L * 1024L * 1024L);

    private static readonly Dictionary<string, (DateTime utc, long bytes)> Cache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object CacheGate = new();
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(5);

    public static long GetWorldSizeBytes(string worldPath)
    {
        if (string.IsNullOrWhiteSpace(worldPath) || !Directory.Exists(worldPath))
            return 0L;

        var now = DateTime.UtcNow;
        lock (CacheGate)
        {
            if (Cache.TryGetValue(worldPath, out var cached) && now - cached.utc <= CacheTtl)
                return cached.bytes;
        }

        var total = MeasureDirectoryBytes(worldPath);
        lock (CacheGate)
        {
            Cache[worldPath] = (now, total);
        }

        return total;
    }

    public static WorldStorageBudgetSnapshot GetBudgetState(string worldPath, WorldStorageBudgetThresholds? thresholds = null)
    {
        var t = thresholds ?? DefaultThresholds;
        var bytes = GetWorldSizeBytes(worldPath);
        return new WorldStorageBudgetSnapshot(bytes, t);
    }

    public static bool ShouldSkipNonCriticalWrites(
        string worldPath,
        out WorldStorageBudgetSnapshot snapshot,
        WorldStorageBudgetThresholds? thresholds = null)
    {
        snapshot = GetBudgetState(worldPath, thresholds);
        return snapshot.IsConserve;
    }

    public static string FormatBytes(long bytes)
    {
        if (bytes <= 0)
            return "0 B";

        string[] units = { "B", "KB", "MB", "GB", "TB" };
        var value = (double)bytes;
        var idx = 0;
        while (value >= 1024d && idx < units.Length - 1)
        {
            value /= 1024d;
            idx++;
        }

        return $"{value:0.##} {units[idx]}";
    }

    private static long MeasureDirectoryBytes(string root)
    {
        try
        {
            long total = 0;
            var pending = new Stack<string>();
            pending.Push(root);

            while (pending.Count > 0)
            {
                var dir = pending.Pop();
                string[] files;
                try
                {
                    files = Directory.GetFiles(dir, "*", SearchOption.TopDirectoryOnly);
                }
                catch
                {
                    continue;
                }

                for (var i = 0; i < files.Length; i++)
                {
                    try
                    {
                        total += new FileInfo(files[i]).Length;
                    }
                    catch
                    {
                        // Best effort.
                    }
                }

                string[] subdirs;
                try
                {
                    subdirs = Directory.GetDirectories(dir, "*", SearchOption.TopDirectoryOnly);
                }
                catch
                {
                    continue;
                }

                for (var i = 0; i < subdirs.Length; i++)
                    pending.Push(subdirs[i]);
            }

            return Math.Max(0, total);
        }
        catch
        {
            return 0L;
        }
    }
}
