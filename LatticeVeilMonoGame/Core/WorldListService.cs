using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace LatticeVeilMonoGame.Core;

public static class WorldListService
{
    public static List<WorldListEntry> LoadSingleplayerWorlds(Logger log)
    {
        try
        {
            Directory.CreateDirectory(Paths.WorldsDir);
            return Directory.GetDirectories(Paths.WorldsDir)
                .Select(path =>
                {
                    MigrateLegacyWorldFiles(path, log);
                    WorldGenerationStateStore.TryLoadRecoverableState(path, TimeSpan.FromSeconds(90), out var worldgenState, log);
                    var validation = WorldValidator.ValidateWorldFolder(path);
                    var folderName = Path.GetFileName(path) ?? string.Empty;
                    var metaPath = Paths.ResolveWorldMetaPath(path);
                    var meta = File.Exists(metaPath) ? WorldMeta.Load(metaPath, log) : null;
                    var worldName = !string.IsNullOrWhiteSpace(meta?.Name)
                        ? meta!.Name
                        : (!string.IsNullOrWhiteSpace(worldgenState?.WorldName) ? worldgenState!.WorldName : folderName);
                    var budget = WorldStorageBudgetService.GetBudgetState(path);
                    return new WorldListEntry
                    {
                        Name = worldName,
                        FolderName = folderName,
                        WorldPath = path,
                        MetaPath = metaPath,
                        PreviewPath = WorldPreviewGenerator.GetPreviewPath(path),
                        CurrentMode = meta?.CurrentWorldGameMode ?? meta?.GameMode ?? GameMode.Artificer,
                        InitialMode = meta?.InitialGameMode ?? meta?.GameMode ?? GameMode.Artificer,
                        Seed = meta?.Seed ?? 0,
                        ValidationStatus = validation.Status,
                        ValidationReason = validation.Reason ?? string.Empty,
                        GenerationState = worldgenState,
                        WorldSizeBytes = budget.SizeBytes,
                        StorageBudgetState = budget.State
                    };
                })
                .Where(x => x.ValidationStatus != WorldValidationStatus.NotAWorld
                    && !string.IsNullOrWhiteSpace(x.FolderName))
                .OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (Exception ex)
        {
            log.Warn($"WorldListService failed to load worlds: {ex.Message}");
            return new List<WorldListEntry>();
        }
    }

    public static string BuildDisplayTitle(WorldListEntry entry)
    {
        return $"{entry.Name} [{entry.CurrentMode.ToString().ToUpperInvariant()}]";
    }

    private static void MigrateLegacyWorldFiles(string worldPath, Logger log)
    {
        try
        {
            var legacyMeta = Paths.GetLegacyWorldMetaPath(worldPath);
            var newMeta = Paths.GetWorldMetaPath(worldPath);
            if (File.Exists(legacyMeta) && !File.Exists(newMeta))
            {
                File.Move(legacyMeta, newMeta);
                log.Info($"Migrated world meta: {Path.GetFileName(legacyMeta)} -> {Path.GetFileName(newMeta)}");
            }

            // Legacy world_config.lvc migration removed - new worlds use world.lvc only
        }
        catch (Exception ex)
        {
            log.Warn($"World file migration skipped for {worldPath}: {ex.Message}");
        }
    }
}

public sealed class WorldListEntry
{
    public string Name { get; init; } = string.Empty;
    public string FolderName { get; init; } = string.Empty;
    public string WorldPath { get; init; } = string.Empty;
    public string MetaPath { get; init; } = string.Empty;
    public string PreviewPath { get; init; } = string.Empty;
    public GameMode CurrentMode { get; init; } = GameMode.Artificer;
    public GameMode InitialMode { get; init; } = GameMode.Artificer;
    public int Seed { get; init; }
    public WorldValidationStatus ValidationStatus { get; init; } = WorldValidationStatus.NotAWorld;
    public string ValidationReason { get; init; } = string.Empty;
    public WorldGenerationState? GenerationState { get; init; }
    public long WorldSizeBytes { get; init; }
    public WorldStorageBudgetState StorageBudgetState { get; init; } = WorldStorageBudgetState.Normal;
}
