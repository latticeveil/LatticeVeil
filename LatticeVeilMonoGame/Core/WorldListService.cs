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
                    var folderName = Path.GetFileName(path) ?? string.Empty;
                    var metaPath = Paths.ResolveWorldMetaPath(path);
                    var meta = File.Exists(metaPath) ? WorldMeta.Load(metaPath, log) : null;
                    var worldName = !string.IsNullOrWhiteSpace(meta?.Name) ? meta!.Name : folderName;
                    return new WorldListEntry
                    {
                        Name = worldName,
                        FolderName = folderName,
                        WorldPath = path,
                        MetaPath = metaPath,
                        PreviewPath = WorldPreviewGenerator.GetPreviewPath(path),
                        CurrentMode = meta?.CurrentWorldGameMode ?? meta?.GameMode ?? GameMode.Artificer,
                        InitialMode = meta?.InitialGameMode ?? meta?.GameMode ?? GameMode.Artificer,
                        Seed = meta?.Seed ?? 0
                    };
                })
                .Where(x => !string.IsNullOrWhiteSpace(x.FolderName))
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

            var legacyConfig = Path.Combine(worldPath, Paths.LegacyWorldConfigFileName);
            var newConfig = Path.Combine(worldPath, Paths.WorldConfigFileName);
            if (File.Exists(legacyConfig) && !File.Exists(newConfig))
            {
                File.Move(legacyConfig, newConfig);
                log.Info($"Migrated world config: {Path.GetFileName(legacyConfig)} -> {Path.GetFileName(newConfig)}");
            }
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
}
