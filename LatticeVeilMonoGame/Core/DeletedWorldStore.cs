using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace LatticeVeilMonoGame.Core;

public static class DeletedWorldStore
{
    private const string ManifestFileName = "deleted_world.lvc";
    public static readonly TimeSpan RetentionPeriod = TimeSpan.FromDays(14);

    public static List<DeletedWorldEntry> LoadDeletedWorlds(Logger log)
    {
        try
        {
            Directory.CreateDirectory(Paths.DeletedWorldsDir);
            PurgeExpired(log);
            return Directory.GetDirectories(Paths.DeletedWorldsDir)
                .Select(path => TryLoadDeletedWorld(path, log))
                .Where(entry => entry != null)
                .Cast<DeletedWorldEntry>()
                .OrderByDescending(entry => entry.DeletedUtc)
                .ThenBy(entry => entry.WorldName, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (Exception ex)
        {
            log.Warn($"Failed to load deleted worlds: {ex.Message}");
            return new List<DeletedWorldEntry>();
        }
    }

    public static bool MoveToDeletedWorlds(WorldListEntry entry, Logger log, out string? error)
    {
        error = null;
        try
        {
            if (string.IsNullOrWhiteSpace(entry.WorldPath) || !Directory.Exists(entry.WorldPath))
            {
                error = "WORLD DATA MISSING";
                return false;
            }

            Directory.CreateDirectory(Paths.DeletedWorldsDir);
            ClearReadOnlyAttributes(entry.WorldPath);

            var deletedUtc = DateTimeOffset.UtcNow;
            var deleteId = $"{deletedUtc:yyyyMMddHHmmssfff}-{Guid.NewGuid():N}";
            var targetPath = Path.Combine(Paths.DeletedWorldsDir, deleteId);
            Directory.Move(entry.WorldPath, targetPath);

            var manifest = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["DeleteId"] = deleteId,
                ["WorldName"] = entry.Name,
                ["OriginalFolderName"] = entry.FolderName,
                ["OriginalWorldPath"] = entry.WorldPath,
                ["DeletedAtUtc"] = deletedUtc.ToString("O"),
                ["CreatedAtUtc"] = entry.CreatedAt,
                ["WorldId"] = entry.WorldId
            };
            SaveManifest(GetManifestPath(targetPath), manifest);

            log.Info($"Moved world to DeletedWorlds: name={entry.Name}, from={entry.WorldPath}, to={targetPath}");
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            log.Warn($"Failed to move world '{entry.Name}' to DeletedWorlds: {ex.Message}");
            return false;
        }
    }

    public static bool PermanentlyDeleteWorld(WorldListEntry entry, Logger log, out string? error)
    {
        error = null;
        try
        {
            if (string.IsNullOrWhiteSpace(entry.WorldPath) || !Directory.Exists(entry.WorldPath))
                return true;

            ClearReadOnlyAttributes(entry.WorldPath);
            Directory.Delete(entry.WorldPath, recursive: true);
            log.Info($"Permanently deleted world: name={entry.Name}, path={entry.WorldPath}");
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            log.Warn($"Failed to permanently delete world '{entry.Name}': {ex.Message}");
            return false;
        }
    }

    public static bool Restore(DeletedWorldEntry entry, Logger log, out string? restoredPath, out string? error)
    {
        restoredPath = null;
        error = null;
        try
        {
            if (!Directory.Exists(entry.DeletedPath))
            {
                error = "DELETED WORLD DATA MISSING";
                return false;
            }

            Directory.CreateDirectory(Paths.WorldsDir);
            ClearReadOnlyAttributes(entry.DeletedPath);

            var folderName = MakeSafeFolderName(entry.OriginalFolderName);
            if (string.IsNullOrWhiteSpace(folderName))
                folderName = MakeSafeFolderName(entry.WorldName);
            if (string.IsNullOrWhiteSpace(folderName))
                folderName = "Restored World";

            var targetPath = GetAvailableWorldPath(folderName);
            File.Delete(GetManifestPath(entry.DeletedPath));
            Directory.Move(entry.DeletedPath, targetPath);
            restoredPath = targetPath;
            log.Info($"Restored deleted world: name={entry.WorldName}, to={targetPath}");
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            log.Warn($"Failed to restore deleted world '{entry.WorldName}': {ex.Message}");
            return false;
        }
    }

    public static bool PermanentlyDelete(DeletedWorldEntry entry, Logger log, out string? error)
    {
        error = null;
        try
        {
            if (Directory.Exists(entry.DeletedPath))
            {
                ClearReadOnlyAttributes(entry.DeletedPath);
                Directory.Delete(entry.DeletedPath, recursive: true);
            }

            log.Info($"Permanently deleted world: name={entry.WorldName}, path={entry.DeletedPath}");
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            log.Warn($"Failed to permanently delete world '{entry.WorldName}': {ex.Message}");
            return false;
        }
    }

    public static int PurgeExpired(Logger log)
    {
        var purged = 0;
        try
        {
            Directory.CreateDirectory(Paths.DeletedWorldsDir);
            foreach (var dir in Directory.GetDirectories(Paths.DeletedWorldsDir))
            {
                var entry = TryLoadDeletedWorld(dir, log);
                if (entry == null || DateTimeOffset.UtcNow < entry.ExpiresUtc)
                    continue;

                if (PermanentlyDelete(entry, log, out _))
                    purged++;
            }
        }
        catch (Exception ex)
        {
            log.Warn($"Failed to purge expired deleted worlds: {ex.Message}");
        }

        return purged;
    }

    private static DeletedWorldEntry? TryLoadDeletedWorld(string deletedPath, Logger log)
    {
        try
        {
            var manifest = LoadManifest(GetManifestPath(deletedPath));
            var metaPath = Paths.ResolveWorldMetaPath(deletedPath);
            var meta = File.Exists(metaPath) ? WorldMeta.Load(metaPath, log) : null;
            var folder = Path.GetFileName(deletedPath) ?? string.Empty;
            var worldName = GetValue(manifest, "WorldName");
            if (string.IsNullOrWhiteSpace(worldName))
                worldName = !string.IsNullOrWhiteSpace(meta?.Name) ? meta!.Name : folder;

            var originalFolder = GetValue(manifest, "OriginalFolderName");
            if (string.IsNullOrWhiteSpace(originalFolder))
                originalFolder = !string.IsNullOrWhiteSpace(meta?.Name) ? meta!.Name : worldName;

            var created = GetValue(manifest, "CreatedAtUtc");
            if (string.IsNullOrWhiteSpace(created))
                created = meta?.CreatedAt ?? string.Empty;

            var deletedRaw = GetValue(manifest, "DeletedAtUtc");
            var deletedUtc = DateTimeOffset.TryParse(deletedRaw, out var parsedDeleted)
                ? parsedDeleted
                : Directory.GetLastWriteTimeUtc(deletedPath);

            return new DeletedWorldEntry
            {
                DeleteId = GetValue(manifest, "DeleteId", folder),
                WorldName = worldName,
                OriginalFolderName = originalFolder,
                DeletedPath = deletedPath,
                MetaPath = metaPath,
                PreviewPath = WorldPreviewGenerator.GetPreviewPath(deletedPath),
                CreatedAt = created,
                DeletedAt = deletedUtc.ToString("O"),
                DeletedUtc = deletedUtc,
                ExpiresUtc = deletedUtc.Add(RetentionPeriod),
                WorldId = GetValue(manifest, "WorldId", meta?.WorldId ?? string.Empty),
                Seed = meta?.Seed ?? 0,
                CurrentMode = meta?.CurrentWorldGameMode ?? meta?.GameMode ?? GameMode.Artificer
            };
        }
        catch (Exception ex)
        {
            log.Warn($"Skipped deleted world '{deletedPath}': {ex.Message}");
            return null;
        }
    }

    private static string GetAvailableWorldPath(string folderName)
    {
        var baseName = MakeSafeFolderName(folderName);
        var candidate = Path.Combine(Paths.WorldsDir, baseName);
        if (!Directory.Exists(candidate))
            return candidate;

        for (var i = 2; i < 10_000; i++)
        {
            candidate = Path.Combine(Paths.WorldsDir, $"{baseName} ({i})");
            if (!Directory.Exists(candidate))
                return candidate;
        }

        return Path.Combine(Paths.WorldsDir, $"{baseName} {Guid.NewGuid():N}");
    }

    private static string MakeSafeFolderName(string value)
    {
        value = (value ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        foreach (var c in Path.GetInvalidFileNameChars())
            value = value.Replace(c, '_');

        return value.Trim().TrimEnd('.');
    }

    private static string GetManifestPath(string deletedPath)
        => Path.Combine(deletedPath, ManifestFileName);

    private static Dictionary<string, string> LoadManifest(string path)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(path))
            return values;

        foreach (var line in File.ReadAllLines(path))
        {
            var idx = line.IndexOf('=');
            if (idx <= 0)
                continue;
            values[line[..idx].Trim()] = line[(idx + 1)..].Trim();
        }

        return values;
    }

    private static void SaveManifest(string path, Dictionary<string, string> values)
    {
        var lines = values.Select(kv => $"{kv.Key}={kv.Value ?? string.Empty}");
        File.WriteAllLines(path, lines);
    }

    private static string GetValue(Dictionary<string, string> values, string key, string fallback = "")
        => values.TryGetValue(key, out var value) ? value : fallback;

    private static void ClearReadOnlyAttributes(string path)
    {
        var dirInfo = new DirectoryInfo(path);
        if (!dirInfo.Exists)
            return;

        dirInfo.Attributes &= ~FileAttributes.ReadOnly;
        foreach (var file in dirInfo.GetFiles("*", SearchOption.AllDirectories))
            file.Attributes &= ~FileAttributes.ReadOnly;
        foreach (var dir in dirInfo.GetDirectories("*", SearchOption.AllDirectories))
            dir.Attributes &= ~FileAttributes.ReadOnly;
    }
}

public sealed class DeletedWorldEntry
{
    public string DeleteId { get; init; } = string.Empty;
    public string WorldName { get; init; } = string.Empty;
    public string OriginalFolderName { get; init; } = string.Empty;
    public string DeletedPath { get; init; } = string.Empty;
    public string MetaPath { get; init; } = string.Empty;
    public string PreviewPath { get; init; } = string.Empty;
    public string CreatedAt { get; init; } = string.Empty;
    public string DeletedAt { get; init; } = string.Empty;
    public DateTimeOffset DeletedUtc { get; init; }
    public DateTimeOffset ExpiresUtc { get; init; }
    public string WorldId { get; init; } = string.Empty;
    public int Seed { get; init; }
    public GameMode CurrentMode { get; init; } = GameMode.Artificer;
}
