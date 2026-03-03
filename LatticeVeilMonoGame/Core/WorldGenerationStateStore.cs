using System;
using System.IO;

namespace LatticeVeilMonoGame.Core;

public static class WorldGenerationStateStore
{
    public const string FileName = "worldgen_state.lvc";

    public static string GetPath(string worldPath)
    {
        return Path.Combine(worldPath, FileName);
    }

    public static WorldGenerationState? Load(string worldPath, Logger? log = null)
    {
        try
        {
            var path = GetPath(worldPath);
            if (!File.Exists(path))
                return null;

            if (LvcSerializer.IsJsonFormat(path))
                return null;

            var dict = LvcSerializer.Read(path);
            var state = new WorldGenerationState();
            LvcSerializer.ApplyObject(state, dict);
            state.Progress = Math.Clamp(state.Progress, 0f, 1f);
            state.WorldType = WorldMeta.CanonicalWorldType(state.WorldType);
            state.Generator = WorldMeta.CanonicalGeneratorForWorldType(state.WorldType);
            if (string.IsNullOrWhiteSpace(state.LastHeartbeatUtc))
                state.LastHeartbeatUtc = DateTimeOffset.UtcNow.ToString("O");
            if (string.IsNullOrWhiteSpace(state.Stage))
                state.Stage = "PREPARING";
            if (string.IsNullOrWhiteSpace(state.Status))
                state.Status = WorldGenerationState.StatusInProgress;
            return state;
        }
        catch (Exception ex)
        {
            log?.Warn($"Failed to load world generation state for {worldPath}: {ex.Message}");
            return null;
        }
    }

    public static bool Save(string worldPath, WorldGenerationState state, Logger? log = null)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(worldPath) || !Directory.Exists(worldPath))
                return false;

            state.WorldType = WorldMeta.CanonicalWorldType(state.WorldType);
            state.Generator = WorldMeta.CanonicalGeneratorForWorldType(state.WorldType);
            state.Progress = Math.Clamp(state.Progress, 0f, 1f);
            if (string.IsNullOrWhiteSpace(state.LastHeartbeatUtc))
                state.LastHeartbeatUtc = DateTimeOffset.UtcNow.ToString("O");
            if (string.IsNullOrWhiteSpace(state.Stage))
                state.Stage = "PREPARING";
            if (string.IsNullOrWhiteSpace(state.Status))
                state.Status = WorldGenerationState.StatusInProgress;

            var path = GetPath(worldPath);
            var tmpPath = path + ".tmp";

            var data = LvcSerializer.SerializeObject(state);
            LvcSerializer.Write(tmpPath, data);

            if (File.Exists(path))
                File.Replace(tmpPath, path, null, ignoreMetadataErrors: true);
            else
                File.Move(tmpPath, path);

            return true;
        }
        catch (Exception ex)
        {
            log?.Warn($"Failed to save world generation state for {worldPath}: {ex.Message}");
            return false;
        }
    }

    public static bool UpdateHeartbeat(
        string worldPath,
        string status,
        string stage,
        float progress,
        int worldSeed,
        string worldType,
        string generator,
        string errorReason,
        string worldName,
        Logger? log = null)
    {
        var state = new WorldGenerationState
        {
            Status = status,
            Stage = stage,
            Progress = progress,
            LastHeartbeatUtc = DateTimeOffset.UtcNow.ToString("O"),
            WorldSeed = worldSeed,
            WorldType = worldType,
            Generator = generator,
            ErrorReason = errorReason ?? string.Empty,
            WorldName = worldName ?? string.Empty
        };
        return Save(worldPath, state, log);
    }

    public static bool TryLoadRecoverableState(string worldPath, TimeSpan staleAfter, out WorldGenerationState? state, Logger? log = null)
    {
        state = Load(worldPath, log);
        if (state == null)
            return false;

        if (state.IsInProgress)
        {
            var heartbeat = state.ParseHeartbeatUtc();
            if (!heartbeat.HasValue || DateTimeOffset.UtcNow - heartbeat.Value > staleAfter)
            {
                state.Status = WorldGenerationState.StatusPaused;
                if (string.IsNullOrWhiteSpace(state.ErrorReason))
                    state.ErrorReason = "Generation paused after stale heartbeat.";
                state.LastHeartbeatUtc = DateTimeOffset.UtcNow.ToString("O");
                Save(worldPath, state, log);
            }
        }

        return true;
    }

    public static bool Delete(string worldPath, Logger? log = null)
    {
        try
        {
            var path = GetPath(worldPath);
            if (File.Exists(path))
                File.Delete(path);
            return true;
        }
        catch (Exception ex)
        {
            log?.Warn($"Failed to delete world generation state for {worldPath}: {ex.Message}");
            return false;
        }
    }
}
