using System;
using System.IO;

namespace LatticeVeilMonoGame.Core;

internal static class AppStateMigration
{
    public static void MigrateToRoaming(Logger log)
    {
        try
        {
            var sourceRoot = Paths.LegacyLocalAppStateDir;
            var destinationRoot = Paths.AppStateDir;

            if (string.IsNullOrWhiteSpace(sourceRoot)
                || string.IsNullOrWhiteSpace(destinationRoot)
                || string.Equals(sourceRoot, destinationRoot, StringComparison.OrdinalIgnoreCase)
                || !Directory.Exists(sourceRoot))
            {
                return;
            }

            Directory.CreateDirectory(destinationRoot);
            MoveDirectoryContents(sourceRoot, destinationRoot, log);
            TryDeleteIfEmpty(sourceRoot, log);
        }
        catch (Exception ex)
        {
            log.Warn($"App-state migration failed: {ex.Message}");
        }

        try
        {
            MigrateLegacyOnlineCache(log);
        }
        catch (Exception ex)
        {
            log.Warn($"Online-cache migration failed: {ex.Message}");
        }
    }

    private static void MigrateLegacyOnlineCache(Logger log)
    {
        var legacyRoots = new[]
        {
            Path.Combine(Paths.RootDir, "_OnlineCache"),
            Path.Combine(Paths.WorldsDir, "_OnlineCache")
        };

        Directory.CreateDirectory(Paths.MultiplayerWorldsDir);
        for (var i = 0; i < legacyRoots.Length; i++)
        {
            var legacyRoot = legacyRoots[i];
            if (!Directory.Exists(legacyRoot))
                continue;

            MoveDirectoryContents(legacyRoot, Paths.MultiplayerWorldsDir, log);
            TryDeleteIfEmpty(legacyRoot, log);
        }
    }

    private static void MoveDirectoryContents(string sourceDir, string destinationDir, Logger log)
    {
        Directory.CreateDirectory(destinationDir);

        foreach (var directory in Directory.GetDirectories(sourceDir))
        {
            var name = Path.GetFileName(directory);
            if (string.IsNullOrWhiteSpace(name))
                continue;

            var destinationChild = Path.Combine(destinationDir, name);
            MoveDirectoryContents(directory, destinationChild, log);
            TryDeleteIfEmpty(directory, log);
        }

        foreach (var file in Directory.GetFiles(sourceDir))
        {
            var name = Path.GetFileName(file);
            if (string.IsNullOrWhiteSpace(name))
                continue;

            var destinationFile = Path.Combine(destinationDir, name);
            MoveFile(file, destinationFile, log);
        }
    }

    private static void MoveFile(string sourceFile, string destinationFile, Logger log)
    {
        try
        {
            var destinationParent = Path.GetDirectoryName(destinationFile);
            if (!string.IsNullOrWhiteSpace(destinationParent))
                Directory.CreateDirectory(destinationParent);

            if (!File.Exists(destinationFile))
            {
                File.Move(sourceFile, destinationFile);
                log.Info($"Migrated app-state file: {sourceFile} -> {destinationFile}");
                return;
            }

            var sourceInfo = new FileInfo(sourceFile);
            var destinationInfo = new FileInfo(destinationFile);
            if (sourceInfo.Length == destinationInfo.Length
                && sourceInfo.LastWriteTimeUtc == destinationInfo.LastWriteTimeUtc)
            {
                File.Delete(sourceFile);
                return;
            }

            var archivePath = destinationFile + ".migrated_old";
            archivePath = ResolveUniqueArchivePath(archivePath);
            File.Move(sourceFile, archivePath);
            log.Warn($"Destination file already existed; preserved migrated source as '{archivePath}'.");
        }
        catch (Exception ex)
        {
            log.Warn($"Failed to migrate file '{sourceFile}': {ex.Message}");
        }
    }

    private static string ResolveUniqueArchivePath(string candidate)
    {
        if (!File.Exists(candidate))
            return candidate;

        var directory = Path.GetDirectoryName(candidate) ?? string.Empty;
        var name = Path.GetFileNameWithoutExtension(candidate);
        var extension = Path.GetExtension(candidate);
        for (var i = 1; i < 1000; i++)
        {
            var next = Path.Combine(directory, $"{name}_{i}{extension}");
            if (!File.Exists(next))
                return next;
        }

        return Path.Combine(directory, $"{name}_{Guid.NewGuid():N}{extension}");
    }

    private static void TryDeleteIfEmpty(string directory, Logger log)
    {
        try
        {
            if (!Directory.Exists(directory))
                return;

            if (Directory.GetFiles(directory).Length != 0 || Directory.GetDirectories(directory).Length != 0)
                return;

            Directory.Delete(directory, recursive: false);
        }
        catch (Exception ex)
        {
            log.Warn($"Failed to clean migrated folder '{directory}': {ex.Message}");
        }
    }
}
