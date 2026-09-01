using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;

namespace LatticeVeilMonoGame.Core;

/// <summary>
/// Runs a read-only support diagnostic without starting the launcher or game window.
/// </summary>
public static class DiagnosticRunner
{
    public static int Run(Logger log)
    {
        var report = new StringBuilder();
        var warningCount = 0;
        var failureCount = 0;

        Write(report, log, "LATTICEVEIL_DIAGNOSTICS=1");
        Write(report, log, $"StartedUtc={DateTime.UtcNow:O}");
        Write(report, log, $"AppVersion={GetAppVersion()}");
        Write(report, log, $"Runtime={Environment.Version}");
        Write(report, log, $"OS={Environment.OSVersion}");
        Write(report, log, $"ProcessArchitecture={System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture}");
        Write(report, log, $"Is64BitProcess={Environment.Is64BitProcess}");
        Write(report, log, $"RootDir={Paths.RootDir}");
        Write(report, log, $"AssetsDir={Paths.GetAssetsDir()}");
        Write(report, log, $"WorldsDir={Paths.WorldsDir}");

        CheckDirectory(report, log, "ROOT", Paths.RootDir, ref warningCount);
        CheckDirectory(report, log, "ASSETS", Paths.GetAssetsDir(), ref failureCount);
        CheckFile(report, log, "ASSET_MANIFEST", Path.Combine(Paths.GetAssetsDir(), "assets_manifest.lvc"), ref warningCount);
        CheckDirectory(report, log, "BLOCK_TEXTURES", Paths.BlocksTexturesDir, ref warningCount);
        CheckFreeSpace(report, log, Paths.RootDir, ref warningCount);
        CheckWorlds(report, log, ref warningCount, ref failureCount);

        if (log.TrySaveSnapshot(out var snapshotPath, out var snapshotError))
            Write(report, log, $"LOG_SNAPSHOT={snapshotPath}");
        else
        {
            warningCount++;
            Write(report, log, $"WARN LOG_SNAPSHOT_FAILED={snapshotError}");
        }

        Write(report, log, $"SUMMARY warnings={warningCount}; failures={failureCount}");
        var reportPath = WriteReport(report.ToString());
        if (string.IsNullOrWhiteSpace(reportPath))
        {
            log.Error("DIAGNOSTICS_REPORT_WRITE_FAILED");
            return 2;
        }

        log.Info($"DIAGNOSTICS_REPORT={reportPath}");
        return failureCount > 0 ? 1 : 0;
    }

    private static void CheckDirectory(StringBuilder report, Logger log, string name, string path, ref int warningCount)
    {
        if (Directory.Exists(path))
        {
            Write(report, log, $"OK {name}_DIRECTORY={path}");
            return;
        }

        warningCount++;
        Write(report, log, $"WARN {name}_DIRECTORY_MISSING={path}");
    }

    private static void CheckFile(StringBuilder report, Logger log, string name, string path, ref int warningCount)
    {
        if (File.Exists(path))
        {
            Write(report, log, $"OK {name}_FILE={path}");
            return;
        }

        warningCount++;
        Write(report, log, $"WARN {name}_FILE_MISSING={path}");
    }

    private static void CheckFreeSpace(StringBuilder report, Logger log, string path, ref int warningCount)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(path));
            if (string.IsNullOrWhiteSpace(root))
                throw new IOException("Could not determine the data drive.");

            var drive = new DriveInfo(root);
            Write(report, log, $"DATA_DRIVE_FREE={WorldStorageBudgetService.FormatBytes(drive.AvailableFreeSpace)}");
            Write(report, log, $"DATA_DRIVE_TOTAL={WorldStorageBudgetService.FormatBytes(drive.TotalSize)}");
        }
        catch (Exception ex)
        {
            warningCount++;
            Write(report, log, $"WARN DATA_DRIVE_CHECK_FAILED={ex.Message}");
        }
    }

    private static void CheckWorlds(StringBuilder report, Logger log, ref int warningCount, ref int failureCount)
    {
        if (!Directory.Exists(Paths.WorldsDir))
        {
            warningCount++;
            Write(report, log, "WARN WORLDS_DIRECTORY_MISSING");
            return;
        }

        string[] worldPaths;
        try
        {
            worldPaths = Directory.GetDirectories(Paths.WorldsDir, "*", SearchOption.TopDirectoryOnly);
        }
        catch (Exception ex)
        {
            failureCount++;
            Write(report, log, $"FAIL WORLDS_DIRECTORY_READ_FAILED={ex.Message}");
            return;
        }

        Write(report, log, $"WORLD_COUNT={worldPaths.Length}");
        for (var i = 0; i < worldPaths.Length; i++)
        {
            var worldPath = worldPaths[i];
            var worldName = Path.GetFileName(worldPath);
            var result = WorldValidator.ValidateWorldFolder(worldPath);
            var budget = WorldStorageBudgetService.GetBudgetState(worldPath);
            var level = result.Status == WorldValidationStatus.ValidNew ? "OK" : "WARN";
            if (result.Status != WorldValidationStatus.ValidNew)
                warningCount++;
            if (budget.IsWarn)
                warningCount++;

            Write(
                report,
                log,
                $"{level} WORLD name={worldName}; status={result.Status}; size={WorldStorageBudgetService.FormatBytes(budget.SizeBytes)}; budget={budget.State}; reason={result.Reason}");
        }
    }

    private static void Write(StringBuilder report, Logger log, string line)
    {
        report.AppendLine(line);
        log.Info($"DIAGNOSTICS {line}");
    }

    private static string? WriteReport(string content)
    {
        try
        {
            Directory.CreateDirectory(Paths.LogsDir);
            var stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");
            var path = Path.Combine(Paths.LogsDir, $"diagnostic-{stamp}.lvdiag");
            File.WriteAllText(path, content, Encoding.UTF8);
            return path;
        }
        catch
        {
            return null;
        }
    }

    private static string GetAppVersion()
    {
        var version = typeof(DiagnosticRunner).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion;
        return string.IsNullOrWhiteSpace(version) ? "unknown" : version;
    }
}
