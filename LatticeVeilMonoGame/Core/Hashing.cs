using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;

namespace LatticeVeilMonoGame.Core;

public static class Hashing
{
    public static string? ResolveCurrentProcessExecutablePath()
    {
        try
        {
            var processPath = Process.GetCurrentProcess().MainModule?.FileName;
            if (!string.IsNullOrWhiteSpace(processPath)
                && File.Exists(processPath)
                && !IsDotNetHostPath(processPath))
            {
                return processPath;
            }
        }
        catch
        {
            // Best-effort only.
        }

        try
        {
            var processPath = (Environment.ProcessPath ?? string.Empty).Trim();
            if (!string.IsNullOrWhiteSpace(processPath)
                && File.Exists(processPath)
                && !IsDotNetHostPath(processPath))
            {
                return processPath;
            }
        }
        catch
        {
            // Best-effort only.
        }

        var assemblyName = Assembly.GetEntryAssembly()?.GetName().Name
            ?? Assembly.GetExecutingAssembly().GetName().Name
            ?? "LatticeVeilMonoGame";
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "LatticeVeilMonoGame.exe"),
            Path.Combine(AppContext.BaseDirectory, "LatticeVeil.exe"),
            Path.Combine(AppContext.BaseDirectory, assemblyName + ".exe")
        };

        for (var i = 0; i < candidates.Length; i++)
        {
            var candidate = candidates[i];
            if (string.IsNullOrWhiteSpace(candidate)
                || !File.Exists(candidate)
                || IsDotNetHostPath(candidate))
            {
                continue;
            }

            return candidate;
        }

        return null;
    }

    public static string Sha256File(string filePath)
    {
        filePath = (filePath ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(filePath))
            throw new ArgumentException("filePath is required", nameof(filePath));

        using var stream = new FileStream(
            filePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            1024 * 64,
            FileOptions.SequentialScan);
        using var sha = SHA256.Create();
        return Convert.ToHexString(sha.ComputeHash(stream)).ToLowerInvariant();
    }

    private static bool IsDotNetHostPath(string path)
    {
        var fileName = Path.GetFileName(path);
        return fileName.Equals("dotnet", StringComparison.OrdinalIgnoreCase)
            || fileName.Equals("dotnet.exe", StringComparison.OrdinalIgnoreCase);
    }
}
