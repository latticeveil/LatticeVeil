using System;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using LatticeVeilMonoGame.Core;

namespace LatticeVeilMonoGame.Launcher;

public sealed class DevOwnerBuildCheckResult
{
    public string CurrentExecutablePath { get; init; } = string.Empty;
    public string SourceExecutablePath { get; init; } = string.Empty;
    public string RemoteHost { get; init; } = string.Empty;
    public string RemoteOwnerName { get; init; } = string.Empty;
    public string StatusMessage { get; init; } = string.Empty;
    public bool IsConfigured { get; init; }
    public bool IsRemote { get; init; }
    public bool IsUpdateAvailable { get; init; }
    public long SourceSize { get; init; }
}

public sealed class DevOwnerBuildUpdater
{
    public const string SourceExeEnvironmentVariable = "LATTICEVEIL_DEV_OWNER_EXE";
    public const string SourceDirEnvironmentVariable = "LATTICEVEIL_DEV_OWNER_DIR";
    private const string ExecutableName = "LatticeVeilMonoGame.exe";

    private readonly Logger _log;

    public DevOwnerBuildUpdater(Logger log)
    {
        _log = log;
    }

    public async Task<DevOwnerBuildCheckResult> CheckForUpdateAsync(CancellationToken ct)
    {
        var current = ResolveCurrentExecutablePath();
        var source = ResolveConfiguredSourceExecutablePath();
        if (string.IsNullOrWhiteSpace(source))
        {
            var remoteHost = DevOwnerBuildTransferService.ResolveConfiguredHost();
            if (!string.IsNullOrWhiteSpace(remoteHost))
                return await CheckRemoteForUpdateAsync(current, remoteHost, ct).ConfigureAwait(false);

            var discovered = await DevOwnerBuildTransferService.DiscoverOwnerAsync(_log, ct).ConfigureAwait(false);
            if (discovered != null && !string.IsNullOrWhiteSpace(discovered.Host))
                return await CheckRemoteForUpdateAsync(current, discovered.Host, ct).ConfigureAwait(false);

            return new DevOwnerBuildCheckResult
            {
                CurrentExecutablePath = current,
                StatusMessage = "DEV OWNER was not found. Make sure the main dev PC has the launcher open and is signed into Veilnet."
            };
        }

        if (!File.Exists(source))
        {
            return new DevOwnerBuildCheckResult
            {
                CurrentExecutablePath = current,
                SourceExecutablePath = source,
                IsConfigured = true,
                StatusMessage = $"DEV owner build not reachable: {source}"
            };
        }

        if (string.Equals(Path.GetFullPath(current), Path.GetFullPath(source), StringComparison.OrdinalIgnoreCase))
        {
            return new DevOwnerBuildCheckResult
            {
                CurrentExecutablePath = current,
                SourceExecutablePath = source,
                IsConfigured = true,
                SourceSize = new FileInfo(source).Length,
                StatusMessage = "DEV owner build source is this running EXE."
            };
        }

        var sourceHashTask = ComputeSha256Async(source, ct);
        var currentHashTask = File.Exists(current)
            ? ComputeSha256Async(current, ct)
            : Task.FromResult(string.Empty);
        await Task.WhenAll(sourceHashTask, currentHashTask).ConfigureAwait(false);

        var updateAvailable = !string.Equals(sourceHashTask.Result, currentHashTask.Result, StringComparison.OrdinalIgnoreCase);
        return new DevOwnerBuildCheckResult
        {
            CurrentExecutablePath = current,
            SourceExecutablePath = source,
            IsConfigured = true,
            IsUpdateAvailable = updateAvailable,
            SourceSize = new FileInfo(source).Length,
            StatusMessage = updateAvailable
                ? "DEV owner build update available."
                : "DEV owner build is current."
        };
    }

    public async Task<string> CopyUpdateAsync(DevOwnerBuildCheckResult check, IProgress<float>? progress, CancellationToken ct)
    {
        if (check.IsRemote)
            return await DownloadRemoteUpdateAsync(check, progress, ct).ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(check.SourceExecutablePath) || !File.Exists(check.SourceExecutablePath))
            throw new FileNotFoundException("DEV owner build was not found.", check.SourceExecutablePath);

        Directory.CreateDirectory(GameReleaseUpdater.DownloadsDir);
        var tempPath = Path.Combine(GameReleaseUpdater.DownloadsDir, ExecutableName + ".dev.update.part");
        var finalPath = Path.Combine(GameReleaseUpdater.DownloadsDir, ExecutableName + ".dev.update.exe");
        TryDeleteFile(tempPath);
        TryDeleteFile(finalPath);

        var total = new FileInfo(check.SourceExecutablePath).Length;
        var buffer = new byte[1024 * 1024];
        long copied = 0;

        await using (var source = new FileStream(check.SourceExecutablePath, FileMode.Open, FileAccess.Read, FileShare.Read))
        await using (var target = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            while (true)
            {
                var read = await source.ReadAsync(buffer.AsMemory(0, buffer.Length), ct).ConfigureAwait(false);
                if (read <= 0)
                    break;

                await target.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                copied += read;
                if (total > 0)
                    progress?.Report(Math.Clamp(copied / (float)total, 0f, 1f));
            }
        }

        File.Move(tempPath, finalPath, true);
        progress?.Report(1f);
        _log.Info($"Copied DEV owner build update from {check.SourceExecutablePath} to {finalPath}");
        return finalPath;
    }

    private async Task<DevOwnerBuildCheckResult> CheckRemoteForUpdateAsync(string current, string host, CancellationToken ct)
    {
        var remote = await DevOwnerBuildTransferService.CheckRemoteAsync(host, _log, ct).ConfigureAwait(false);
        if (remote == null)
        {
            return new DevOwnerBuildCheckResult
            {
                CurrentExecutablePath = current,
                RemoteHost = host,
                IsConfigured = true,
                IsRemote = true,
                StatusMessage = $"DEV OWNER did not answer at {host}."
            };
        }

        var currentHash = File.Exists(current)
            ? await ComputeSha256Async(current, ct).ConfigureAwait(false)
            : string.Empty;
        var updateAvailable = !string.Equals(remote.Sha256, currentHash, StringComparison.OrdinalIgnoreCase);

        return new DevOwnerBuildCheckResult
        {
            CurrentExecutablePath = current,
            RemoteHost = host,
            RemoteOwnerName = remote.OwnerName,
            IsConfigured = true,
            IsRemote = true,
            IsUpdateAvailable = updateAvailable,
            SourceSize = remote.Size,
            StatusMessage = updateAvailable
                ? $"DEV OWNER build update available from {remote.OwnerName}."
                : $"DEV OWNER build from {remote.OwnerName} is current."
        };
    }

    private async Task<string> DownloadRemoteUpdateAsync(DevOwnerBuildCheckResult check, IProgress<float>? progress, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(check.RemoteHost))
            throw new InvalidOperationException("DEV OWNER host was not configured.");

        Directory.CreateDirectory(GameReleaseUpdater.DownloadsDir);
        var finalPath = Path.Combine(GameReleaseUpdater.DownloadsDir, ExecutableName + ".dev.remote.update.exe");
        var (_, downloadedPath) = await DevOwnerBuildTransferService.DownloadRemoteAsync(
            check.RemoteHost,
            finalPath,
            progress,
            _log,
            ct).ConfigureAwait(false);
        return downloadedPath;
    }

    private static string ResolveConfiguredSourceExecutablePath()
    {
        var explicitExe = (Environment.GetEnvironmentVariable(SourceExeEnvironmentVariable) ?? string.Empty).Trim();
        if (!string.IsNullOrWhiteSpace(explicitExe))
            return explicitExe;

        var sourceDir = (Environment.GetEnvironmentVariable(SourceDirEnvironmentVariable) ?? string.Empty).Trim();
        if (!string.IsNullOrWhiteSpace(sourceDir))
            return Path.Combine(sourceDir, ExecutableName);

        return string.Empty;
    }

    private static string ResolveCurrentExecutablePath()
    {
        var processPath = (Environment.ProcessPath ?? string.Empty).Trim();
        if (!string.IsNullOrWhiteSpace(processPath) && File.Exists(processPath))
            return processPath;

        return Path.Combine(AppContext.BaseDirectory, ExecutableName);
    }

    private static async Task<string> ComputeSha256Async(string path, CancellationToken ct)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var hash = await SHA256.HashDataAsync(stream, ct).ConfigureAwait(false);
        return Convert.ToHexString(hash);
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
        }
    }
}
