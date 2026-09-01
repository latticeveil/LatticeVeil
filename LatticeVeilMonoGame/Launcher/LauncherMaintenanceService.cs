using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.Win32;
using LatticeVeilMonoGame.Core;

namespace LatticeVeilMonoGame.Launcher;

internal sealed class LauncherMaintenanceService
{
    private const string UninstallRegistryPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\LatticeVeil";
    private readonly Logger _log;

    public LauncherMaintenanceService(Logger log)
    {
        _log = log;
    }

    public InstalledLatticeVeilInfo? TryGetInstalledCopy()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(UninstallRegistryPath);
            if (key == null)
                return null;

            var location = (key.GetValue("InstallLocation")?.ToString() ?? string.Empty).Trim();
            var uninstall = (key.GetValue("UninstallString")?.ToString() ?? string.Empty).Trim();
            var version = (key.GetValue("DisplayVersion")?.ToString() ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(uninstall) && !string.IsNullOrWhiteSpace(location))
                uninstall = Path.Combine(location, "LatticeVeilUninstaller.exe");

            var uninstallerPath = ExtractExePath(uninstall);
            if (string.IsNullOrWhiteSpace(uninstallerPath) || !File.Exists(uninstallerPath))
                return null;

            return new InstalledLatticeVeilInfo(location, uninstallerPath, version);
        }
        catch (Exception ex)
        {
            _log.Warn($"Installed-copy detection failed: {ex.Message}");
            return null;
        }
    }

    public bool LaunchOfficialUninstaller(out string error)
    {
        error = string.Empty;
        var installed = TryGetInstalledCopy();
        if (installed == null)
        {
            error = "No installed LatticeVeil uninstaller was found.";
            return false;
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = installed.UninstallerPath,
                WorkingDirectory = Path.GetDirectoryName(installed.UninstallerPath) ?? installed.InstallLocation,
                UseShellExecute = true
            });
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            _log.Warn($"Failed to launch official uninstaller: {ex.Message}");
            return false;
        }
    }

    public List<LegacyLatticeVeilEntry> ScanLikelyCopies()
    {
        var results = new List<LegacyLatticeVeilEntry>();
        var roots = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads"),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86)
        }
            .Where(path => !string.IsNullOrWhiteSpace(path) && Directory.Exists(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        foreach (var root in roots)
            ScanRoot(root, results);

        return results
            .GroupBy(x => x.Path, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .OrderBy(x => x.Path, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public MaintenanceCleanupResult Cleanup(MaintenanceCleanupOptions options)
    {
        var result = new MaintenanceCleanupResult();
        void DeleteDirectory(string path, string label)
        {
            if (!Directory.Exists(path))
                return;

            try
            {
                Directory.Delete(path, recursive: true);
                result.Deleted.Add(label);
            }
            catch (Exception ex)
            {
                result.Errors.Add($"{label}: {ex.Message}");
            }
        }

        void DeleteFile(string path, string label)
        {
            if (!File.Exists(path))
                return;

            try
            {
                File.Delete(path);
                result.Deleted.Add(label);
            }
            catch (Exception ex)
            {
                result.Errors.Add($"{label}: {ex.Message}");
            }
        }

        if (options.ClearStartupLegacyOnly)
        {
            DeleteDirectory(Paths.LegacyLocalAppStateDir, "legacy local AppData cache");
            DeleteDirectory(Path.Combine(Paths.AppStateDir, "Updates"), "legacy update staging");
            return result;
        }

        if (options.ClearCaches)
        {
            DeleteDirectory(GameReleaseUpdater.DownloadsDir, "game update downloads");
            DeleteDirectory(GameReleaseUpdater.UpdatesDir, "game update staging");
            DeleteDirectory(AssetPackInstaller.DownloadsDir, "asset downloads");
            DeleteDirectory(AssetPackInstaller.StagingDir, "asset staging");
            DeleteDirectory(Paths.RuntimeStateDir, "runtime cache");
            DeleteDirectory(Paths.LegacyLocalAppStateDir, "legacy local AppData cache");
        }

        if (options.ClearLogs)
            DeleteDirectory(Paths.LogsDir, "logs");

        if (options.ClearSettings)
        {
            DeleteFile(Paths.SettingsJsonPath, "settings");
            DeleteFile(Paths.LegacySettingsJsonPath, "legacy settings json");
            DeleteFile(Paths.LegacySettingsLvcPath, "legacy settings lvc");
            DeleteFile(Paths.PlayerProfileJsonPath, "player profile");
            DeleteFile(Paths.LegacyPlayerProfileJsonPath, "legacy player profile");
        }

        if (options.ClearAuth)
        {
            DeleteDirectory(Paths.SystemStateDir, "launcher/account auth cache");
            DeleteFile(Paths.LegacyEosIdentityPath, "legacy EOS identity");
        }

        if (options.ClearScreenshots)
            DeleteDirectory(Paths.ScreenshotsDir, "screenshots");

        if (options.ClearWorlds)
        {
            DeleteDirectory(Paths.WorldsDir, "worlds");
            DeleteDirectory(Paths.DeletedWorldsDir, "deleted worlds");
            DeleteDirectory(Paths.BackupsDir, "world backups");
        }

        if (options.ClearRegistry)
        {
            TryDeleteRegistryTree(Registry.CurrentUser, @"Software\Classes\latticeveil", "latticeveil protocol", result);
            TryDeleteRegistryTree(Registry.LocalMachine, UninstallRegistryPath, "install registry", result);
            DeleteShortcutIfExists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "LatticeVeil.lnk"), "desktop shortcut", result);
            DeleteDirectoryIfExists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "LatticeVeil"), "Start Menu shortcuts", result);
        }

        if (options.ClearPortableCopies)
        {
            foreach (var entry in ScanLikelyCopies())
            {
                if (!IsSafePortableGameExeDeleteTarget(entry.Path))
                    continue;

                DeleteFile(entry.Path, $"old portable game exe: {entry.Path}");
            }
        }

        return result;
    }

    public MaintenanceCleanupResult CleanupStartupLegacyArtifacts()
    {
        var result = Cleanup(new MaintenanceCleanupOptions
        {
            ClearStartupLegacyOnly = true
        });
        if (result.Deleted.Count > 0)
            _log.Info($"Startup legacy cleanup removed: {string.Join(", ", result.Deleted)}");
        if (result.Errors.Count > 0)
            _log.Warn($"Startup legacy cleanup errors: {string.Join("; ", result.Errors)}");
        return result;
    }

    public bool SchedulePortableSelfDelete(bool relaunch, out string error)
    {
        error = string.Empty;
        try
        {
            var exePath = Environment.ProcessPath ?? string.Empty;
            if (string.IsNullOrWhiteSpace(exePath) || !File.Exists(exePath))
            {
                error = "Current executable path could not be resolved.";
                return false;
            }

            var scriptPath = Path.Combine(Path.GetTempPath(), $"latticeveil_cleanup_{Guid.NewGuid():N}.cmd");
            var restartLine = relaunch
                ? $"start \"\" \"{exePath}\""
                : string.Empty;
            var script =
                "@echo off\r\n" +
                "timeout /t 2 /nobreak >nul\r\n" +
                $":retry\r\n" +
                $"del /f /q \"{exePath}\" >nul 2>nul\r\n" +
                $"if exist \"{exePath}\" (\r\n" +
                "  timeout /t 1 /nobreak >nul\r\n" +
                "  goto retry\r\n" +
                ")\r\n" +
                (string.IsNullOrWhiteSpace(restartLine) ? string.Empty : restartLine + "\r\n") +
                $"del /f /q \"%~f0\" >nul 2>nul\r\n";
            File.WriteAllText(scriptPath, script, Encoding.ASCII);
            Process.Start(new ProcessStartInfo
            {
                FileName = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe",
                Arguments = $"/c \"\"{scriptPath}\"\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            });
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private static void TryDeleteRegistryTree(RegistryKey root, string subKey, string label, MaintenanceCleanupResult result)
    {
        try
        {
            root.DeleteSubKeyTree(subKey, throwOnMissingSubKey: false);
            result.Deleted.Add(label);
        }
        catch (Exception ex)
        {
            result.Errors.Add($"{label}: {ex.Message}");
        }
    }

    private static void DeleteShortcutIfExists(string path, string label, MaintenanceCleanupResult result)
    {
        if (!File.Exists(path))
            return;

        try
        {
            File.Delete(path);
            result.Deleted.Add(label);
        }
        catch (Exception ex)
        {
            result.Errors.Add($"{label}: {ex.Message}");
        }
    }

    private static void DeleteDirectoryIfExists(string path, string label, MaintenanceCleanupResult result)
    {
        if (!Directory.Exists(path))
            return;

        try
        {
            Directory.Delete(path, recursive: true);
            result.Deleted.Add(label);
        }
        catch (Exception ex)
        {
            result.Errors.Add($"{label}: {ex.Message}");
        }
    }

    private static string ExtractExePath(string uninstallString)
    {
        if (string.IsNullOrWhiteSpace(uninstallString))
            return string.Empty;

        var trimmed = uninstallString.Trim();
        if (trimmed.StartsWith("\"", StringComparison.Ordinal))
        {
            var end = trimmed.IndexOf('"', 1);
            return end > 1 ? trimmed.Substring(1, end - 1) : string.Empty;
        }

        var exeIndex = trimmed.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        return exeIndex >= 0 ? trimmed.Substring(0, exeIndex + 4).Trim() : trimmed;
    }

    private static void ScanRoot(string root, List<LegacyLatticeVeilEntry> results)
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(root, "LatticeVeil*.exe", SearchOption.AllDirectories))
            {
                if (file.Contains($"{Path.DirectorySeparatorChar}.git{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
                    || file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
                    || file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
                    continue;

                var name = Path.GetFileName(file);
                if (!name.Contains("LatticeVeil", StringComparison.OrdinalIgnoreCase))
                    continue;

                var version = string.Empty;
                try
                {
                    version = FileVersionInfo.GetVersionInfo(file).ProductVersion ?? string.Empty;
                }
                catch
                {
                }

                results.Add(new LegacyLatticeVeilEntry(file, version));
            }
        }
        catch
        {
            // Some roots are protected. Skip them.
        }
    }

    private static bool IsSafePortableGameExeDeleteTarget(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return false;

        var currentExe = Environment.ProcessPath ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(currentExe) && string.Equals(Path.GetFullPath(path), Path.GetFullPath(currentExe), StringComparison.OrdinalIgnoreCase))
            return false;

        var fileName = Path.GetFileName(path);
        if (!string.Equals(fileName, "LatticeVeilMonoGame.exe", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(fileName, "LatticeVeil.exe", StringComparison.OrdinalIgnoreCase))
            return false;

        if (path.Contains($"{Path.DirectorySeparatorChar}LatticeVeil_project{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
            || path.Contains($"{Path.DirectorySeparatorChar}latticeveil.github.io{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
            return false;

        return true;
    }
}

internal sealed record InstalledLatticeVeilInfo(string InstallLocation, string UninstallerPath, string Version);
internal sealed record LegacyLatticeVeilEntry(string Path, string Version);

internal sealed class MaintenanceCleanupOptions
{
    public bool ClearCaches { get; set; }
    public bool ClearLogs { get; set; }
    public bool ClearSettings { get; set; }
    public bool ClearAuth { get; set; }
    public bool ClearRegistry { get; set; }
    public bool ClearWorlds { get; set; }
    public bool ClearScreenshots { get; set; }
    public bool ClearPortableCopies { get; set; }
    public bool ClearStartupLegacyOnly { get; set; }
}

internal sealed class MaintenanceCleanupResult
{
    public List<string> Deleted { get; } = new();
    public List<string> Errors { get; } = new();
}
