using System;
using System.Diagnostics;
using System.IO;

namespace LatticeVeilMonoGame.Core;

public static class DeletedWorldMaintenance
{
    public const string MaintenanceArg = "--maintenance-purge";
    private const string DailyTaskName = @"LatticeVeil\DeletedWorldCleanupDaily";
    private const string LogonTaskName = @"LatticeVeil\DeletedWorldCleanupLogon";

    public static void ConfigureScheduledCleanup(bool enabled, Logger log)
    {
        if (!OperatingSystem.IsWindows())
            return;

        if (enabled)
            RegisterScheduledCleanup(log);
        else
            UnregisterScheduledCleanup(log);
    }

    public static void RunMaintenancePurge(Logger log)
    {
        var purged = DeletedWorldStore.PurgeExpired(log);
        log.Info($"Deleted-world maintenance purge complete. purged={purged}");
    }

    private static void RegisterScheduledCleanup(Logger log)
    {
        var exePath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(exePath) || !File.Exists(exePath))
        {
            log.Warn("Deleted-world scheduled cleanup skipped: current executable path unavailable.");
            return;
        }

        var action = $"\\\"{exePath}\\\" {MaintenanceArg}";
        RunSchTasks($"/Create /TN \"{DailyTaskName}\" /TR \"{action}\" /SC DAILY /ST 03:00 /F", log, "register daily deleted-world cleanup");
        RunSchTasks($"/Create /TN \"{LogonTaskName}\" /TR \"{action}\" /SC ONLOGON /F", log, "register logon deleted-world cleanup");
    }

    private static void UnregisterScheduledCleanup(Logger log)
    {
        RunSchTasks($"/Delete /TN \"{DailyTaskName}\" /F", log, "unregister daily deleted-world cleanup", warnOnFailure: false);
        RunSchTasks($"/Delete /TN \"{LogonTaskName}\" /F", log, "unregister logon deleted-world cleanup", warnOnFailure: false);
    }

    private static void RunSchTasks(string arguments, Logger log, string label, bool warnOnFailure = true)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "schtasks.exe",
                Arguments = arguments,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            });
            if (process == null)
            {
                if (warnOnFailure)
                    log.Warn($"{label}: failed to start schtasks.exe");
                return;
            }

            process.WaitForExit(5000);
            var output = process.StandardOutput.ReadToEnd().Trim();
            var error = process.StandardError.ReadToEnd().Trim();
            if (process.ExitCode == 0)
            {
                log.Info($"{label}: ok{(string.IsNullOrWhiteSpace(output) ? string.Empty : $" ({output})")}");
                return;
            }

            if (warnOnFailure)
                log.Warn($"{label}: schtasks exit={process.ExitCode}; {error} {output}".Trim());
        }
        catch (Exception ex)
        {
            if (warnOnFailure)
                log.Warn($"{label}: {ex.Message}");
        }
    }
}
