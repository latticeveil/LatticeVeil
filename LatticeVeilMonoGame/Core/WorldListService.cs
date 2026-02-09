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
                    var folderName = Path.GetFileName(path) ?? string.Empty;
                    var metaPath = Path.Combine(path, "world.json");
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
