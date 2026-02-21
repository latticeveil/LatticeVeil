using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.Win32;
using LatticeVeilMonoGame.Core;

namespace LatticeVeilMonoGame.Launcher;

internal static class LauncherProtocolLinking
{
    private const string Scheme = "latticeveil";
    private const string ClassesRoot = @"Software\Classes";
    private static readonly string QueueDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "LatticeVeil");
    private static readonly string QueuePath = Path.Combine(QueueDir, "veilnet_link_queue.txt");

    public static void TryEnsureProtocolRegistration(Logger log)
    {
        try
        {
            var exePath = ResolveLauncherExecutablePath();
            if (string.IsNullOrWhiteSpace(exePath) || !File.Exists(exePath))
            {
                log.Warn($"Protocol registration skipped: launcher executable not found ({exePath}).");
                return;
            }

            var schemePath = $@"{ClassesRoot}\{Scheme}";
            using var schemeKey = Registry.CurrentUser.CreateSubKey(schemePath);
            if (schemeKey == null)
            {
                log.Warn("Protocol registration skipped: unable to create registry key.");
                return;
            }

            schemeKey.SetValue(string.Empty, "URL:LatticeVeil Launcher Protocol");
            schemeKey.SetValue("URL Protocol", string.Empty);

            using (var iconKey = schemeKey.CreateSubKey("DefaultIcon"))
            {
                iconKey?.SetValue(string.Empty, $"\"{exePath}\",0");
            }

            using var commandKey = schemeKey.CreateSubKey(@"shell\open\command");
            commandKey?.SetValue(string.Empty, $"\"{exePath}\" \"%1\"");
            log.Info("Protocol registration ensured: latticeveil://");
        }
        catch (Exception ex)
        {
            log.Warn($"Protocol registration failed: {ex.Message}");
        }
    }

    public static string? TryExtractLinkCodeFromArgs(string[] args)
    {
        if (args == null || args.Length == 0)
            return null;

        for (var i = 0; i < args.Length; i++)
        {
            var code = TryExtractLinkCodeFromUri(args[i]);
            if (!string.IsNullOrWhiteSpace(code))
                return code;
        }

        return null;
    }

    public static bool TryQueueLinkCodeForRunningLauncher(string code, Logger log)
    {
        try
        {
            var normalized = NormalizeCode(code);
            if (string.IsNullOrWhiteSpace(normalized))
                return false;

            Directory.CreateDirectory(QueueDir);
            File.AppendAllText(QueuePath, normalized + Environment.NewLine, Encoding.UTF8);
            log.Info($"Queued protocol link code for running launcher at {QueuePath}");
            return true;
        }
        catch (Exception ex)
        {
            log.Warn($"Failed to queue protocol link code: {ex.Message}");
            return false;
        }
    }

    public static string[] DequeuePendingLinkCodes(Logger log)
    {
        try
        {
            if (!File.Exists(QueuePath))
                return Array.Empty<string>();

            var lines = File.ReadAllLines(QueuePath);
            File.Delete(QueuePath);

            var codes = lines
                .Select(NormalizeCode)
                .Where(v => !string.IsNullOrWhiteSpace(v))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            if (codes.Length > 0)
                log.Info($"Dequeued {codes.Length} pending protocol link code(s) from {QueuePath}");

            return codes;
        }
        catch (Exception ex)
        {
            log.Warn($"Failed to dequeue protocol link codes: {ex.Message}");
            return Array.Empty<string>();
        }
    }

    public static void ClearPendingLinkCodes(Logger log)
    {
        try
        {
            if (File.Exists(QueuePath))
            {
                File.Delete(QueuePath);
                log.Info($"Cleared pending protocol link code queue at {QueuePath}");
            }
        }
        catch (Exception ex)
        {
            log.Warn($"Failed to clear protocol link code queue: {ex.Message}");
        }
    }

    private static string? TryExtractLinkCodeFromUri(string? rawValue)
    {
        var value = (rawValue ?? string.Empty).Trim().Trim('"');
        if (string.IsNullOrWhiteSpace(value))
            return null;

        if (!value.StartsWith($"{Scheme}://", StringComparison.OrdinalIgnoreCase))
            return null;

        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri))
            return null;

        if (!string.Equals(uri.Scheme, Scheme, StringComparison.OrdinalIgnoreCase))
            return null;

        var action = (uri.Host ?? string.Empty).Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(action))
            action = (uri.AbsolutePath ?? string.Empty).Trim('/').ToLowerInvariant();

        if (!string.Equals(action, "link", StringComparison.OrdinalIgnoreCase))
            return null;

        var query = (uri.Query ?? string.Empty).TrimStart('?');
        if (string.IsNullOrWhiteSpace(query))
            return null;

        var pairs = query.Split('&', StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < pairs.Length; i++)
        {
            var pair = pairs[i];
            var split = pair.Split('=', 2);
            var key = Uri.UnescapeDataString(split[0] ?? string.Empty).Trim();
            if (!string.Equals(key, "code", StringComparison.OrdinalIgnoreCase))
                continue;

            var valuePart = split.Length > 1 ? split[1] : string.Empty;
            var code = Uri.UnescapeDataString(valuePart ?? string.Empty);
            code = string.Concat(code.Where(ch => !char.IsWhiteSpace(ch))).Trim().ToUpperInvariant();
            if (code.Length < 4 || code.Length > 24)
                return null;
            return code;
        }

        return null;
    }

    private static string NormalizeCode(string? raw)
    {
        var value = string.Concat((raw ?? string.Empty).Where(ch => !char.IsWhiteSpace(ch)))
            .Trim()
            .ToUpperInvariant();
        if (value.Length < 4 || value.Length > 24)
            return string.Empty;
        return value;
    }

    private static string ResolveLauncherExecutablePath()
    {
        try
        {
            var processPath = (Environment.ProcessPath ?? string.Empty).Trim();
            if (!string.IsNullOrWhiteSpace(processPath) && !IsDotNetHostPath(processPath) && File.Exists(processPath))
                return processPath;
        }
        catch
        {
            // Best effort.
        }

        try
        {
            var modulePath = Process.GetCurrentProcess().MainModule?.FileName ?? string.Empty;
            modulePath = modulePath.Trim();
            if (!string.IsNullOrWhiteSpace(modulePath) && !IsDotNetHostPath(modulePath) && File.Exists(modulePath))
                return modulePath;
        }
        catch
        {
            // Best effort.
        }

        return Path.Combine(AppContext.BaseDirectory, "LatticeVeilMonoGame.exe");
    }

    private static bool IsDotNetHostPath(string path)
    {
        var fileName = Path.GetFileName(path ?? string.Empty);
        return fileName.Equals("dotnet", StringComparison.OrdinalIgnoreCase)
            || fileName.Equals("dotnet.exe", StringComparison.OrdinalIgnoreCase);
    }
}
