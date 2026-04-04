using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using LatticeVeilMonoGame.Core;

namespace LatticeVeilMonoGame.Launcher;

public sealed class GameReleaseCheckResult
{
    public GameReleaseCheckResult(
        string currentExecutablePath,
        string currentExecutableName,
        Version localVersion,
        string releaseTitle,
        GitHubRelease? release,
        GitHubAsset? asset,
        Version? remoteVersion,
        bool isUpdateAvailable,
        string statusMessage)
    {
        CurrentExecutablePath = currentExecutablePath;
        CurrentExecutableName = currentExecutableName;
        LocalVersion = localVersion;
        ReleaseTitle = releaseTitle;
        Release = release;
        Asset = asset;
        RemoteVersion = remoteVersion;
        IsUpdateAvailable = isUpdateAvailable;
        StatusMessage = statusMessage;
    }

    public string CurrentExecutablePath { get; }
    public string CurrentExecutableName { get; }
    public Version LocalVersion { get; }
    public string ReleaseTitle { get; }
    public GitHubRelease? Release { get; }
    public GitHubAsset? Asset { get; }
    public Version? RemoteVersion { get; }
    public bool IsUpdateAvailable { get; }
    public string StatusMessage { get; }
}

public sealed class GameReleaseUpdater
{
    private const string ReleasesOwner = "latticeveil";
    private const string ReleasesRepo = "LatticeVeil";

    private readonly Logger _log;
    private readonly GitHubReleaseClient _releaseClient;

    public GameReleaseUpdater(Logger log)
    {
        _log = log;
        _releaseClient = new GitHubReleaseClient();
    }

    public static string UpdatesDir => Path.Combine(Paths.RootDir, "_updates");
    public static string DownloadsDir => Path.Combine(Paths.RootDir, "_downloads");

    public static string GetCurrentVersionLabel()
    {
        var version = GetCurrentExecutableVersion(ResolveCurrentExecutablePath());
        return $"v{NormalizeVersion(version)}";
    }

    public async Task<GameReleaseCheckResult> CheckForUpdateAsync(CancellationToken ct)
    {
        var currentExecutablePath = ResolveCurrentExecutablePath();
        if (!File.Exists(currentExecutablePath))
            throw new FileNotFoundException("Current executable was not found.", currentExecutablePath);

        var currentExecutableName = Path.GetFileName(currentExecutablePath);
        var localVersion = GetCurrentExecutableVersion(currentExecutablePath);

        var release = await _releaseClient.FetchLatestReleaseAsync(ReleasesOwner, ReleasesRepo, ct);
        if (release == null)
        {
            return new GameReleaseCheckResult(
                currentExecutablePath,
                currentExecutableName,
                localVersion,
                releaseTitle: string.Empty,
                null,
                null,
                null,
                isUpdateAvailable: false,
                statusMessage: "Latest release could not be fetched.");
        }

        var releaseTitle = GetReleaseTitle(release);
        var remoteVersion = TryParseReleaseVersion(releaseTitle) ?? TryParseReleaseVersion(release.tag_name);
        var asset = FindBestExecutableAsset(release, currentExecutableName);
        if (asset == null)
        {
            return new GameReleaseCheckResult(
                currentExecutablePath,
                currentExecutableName,
                localVersion,
                releaseTitle,
                release,
                null,
                remoteVersion,
                isUpdateAvailable: false,
                statusMessage: "Release found, but no matching EXE asset was detected.");
        }

        var updateAvailable = remoteVersion != null && remoteVersion > localVersion;
        var remoteVersionText = remoteVersion == null ? releaseTitle : $"v{NormalizeVersion(remoteVersion)}";
        var message = updateAvailable
            ? $"Update available: {remoteVersionText}"
            : $"Up to date: v{NormalizeVersion(localVersion)}";

        return new GameReleaseCheckResult(
            currentExecutablePath,
            currentExecutableName,
            localVersion,
            releaseTitle,
            release,
            asset,
            remoteVersion,
            updateAvailable,
            message);
    }

    public async Task<string> DownloadUpdateAsync(GameReleaseCheckResult check, IProgress<float>? progress, CancellationToken ct)
    {
        if (check.Asset == null || string.IsNullOrWhiteSpace(check.Asset.browser_download_url))
            throw new InvalidOperationException("No downloadable EXE asset was available for the latest release.");

        EnsureWritableDirectory(Path.GetDirectoryName(check.CurrentExecutablePath) ?? AppContext.BaseDirectory, "game install directory");
        EnsureWritableDirectory(DownloadsDir, "Documents\\LatticeVeil\\_downloads");
        Directory.CreateDirectory(DownloadsDir);

        var tempPath = Path.Combine(DownloadsDir, check.CurrentExecutableName + ".update.part");
        var finalPath = Path.Combine(DownloadsDir, check.CurrentExecutableName + ".update.exe");

        TryDeleteFile(tempPath);
        TryDeleteFile(finalPath);

        using var http = new System.Net.Http.HttpClient
        {
            Timeout = TimeSpan.FromMinutes(10)
        };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("LatticeVeilMonoGame/1.0");
        http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");

        using var response = await http.GetAsync(check.Asset.browser_download_url, System.Net.Http.HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        var total = response.Content.Headers.ContentLength ?? -1L;
        var buffer = new byte[1024 * 64];
        long read = 0;

        await using (var source = await response.Content.ReadAsStreamAsync(ct))
        await using (var destination = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            while (true)
            {
                var bytes = await source.ReadAsync(buffer.AsMemory(0, buffer.Length), ct);
                if (bytes <= 0)
                    break;

                await destination.WriteAsync(buffer.AsMemory(0, bytes), ct);
                read += bytes;

                if (total > 0)
                    progress?.Report(Math.Clamp(read / (float)total, 0f, 1f));
            }
        }

        File.Move(tempPath, finalPath, true);
        progress?.Report(1f);
        return finalPath;
    }

    public string PrepareDeferredReplacement(string downloadedExePath, string restartArgs)
    {
        if (string.IsNullOrWhiteSpace(downloadedExePath) || !File.Exists(downloadedExePath))
            throw new FileNotFoundException("Downloaded update was not found.", downloadedExePath);

        var currentExecutablePath = ResolveCurrentExecutablePath();
        if (!File.Exists(currentExecutablePath))
            throw new FileNotFoundException("Current executable was not found.", currentExecutablePath);

        Directory.CreateDirectory(UpdatesDir);
        EnsureWritableDirectory(UpdatesDir, "Documents\\LatticeVeil\\_updates");
        EnsureWritableDirectory(Path.GetDirectoryName(currentExecutablePath) ?? AppContext.BaseDirectory, "game install directory");

        var scriptPath = Path.Combine(UpdatesDir, "apply_game_update.cmd");
        var script = BuildUpdateScript(
            Environment.ProcessId,
            downloadedExePath,
            currentExecutablePath,
            restartArgs);

        File.WriteAllText(scriptPath, script, Encoding.ASCII);
        return scriptPath;
    }

    public void LaunchDeferredReplacementScript(string scriptPath)
    {
        if (string.IsNullOrWhiteSpace(scriptPath) || !File.Exists(scriptPath))
            throw new FileNotFoundException("Deferred replacement script was not found.", scriptPath);

        Process.Start(new ProcessStartInfo
        {
            FileName = scriptPath,
            WorkingDirectory = Path.GetDirectoryName(scriptPath) ?? AppContext.BaseDirectory,
            UseShellExecute = true
        });
    }

    private static string ResolveCurrentExecutablePath()
    {
        var processPath = (Environment.ProcessPath ?? string.Empty).Trim();
        if (!string.IsNullOrWhiteSpace(processPath) && File.Exists(processPath))
            return processPath;

        var fallback = Path.Combine(AppContext.BaseDirectory, "LatticeVeilMonoGame.exe");
        return fallback;
    }

    private static Version GetCurrentExecutableVersion(string executablePath)
    {
        try
        {
            var assemblyName = AssemblyName.GetAssemblyName(executablePath);
            if (assemblyName.Version != null)
                return assemblyName.Version;
        }
        catch
        {
            // Fall back below.
        }

        try
        {
            var info = FileVersionInfo.GetVersionInfo(executablePath);
            if (Version.TryParse(info.FileVersion, out var fileVersion))
                return fileVersion;
        }
        catch
        {
            // Fall back below.
        }

        return new Version(0, 0, 0, 0);
    }

    private static Version? TryParseReleaseVersion(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag))
            return null;

        var token = tag.Trim();
        if (token.StartsWith("v", StringComparison.OrdinalIgnoreCase))
            token = token.Substring(1);

        var plus = token.IndexOf('+');
        if (plus >= 0)
            token = token.Substring(0, plus);

        var dash = token.IndexOf('-');
        if (dash >= 0)
            token = token.Substring(0, dash);

        return Version.TryParse(token, out var version) ? version : null;
    }

    private static string GetReleaseTitle(GitHubRelease release)
    {
        var title = (release.name ?? string.Empty).Trim();
        if (!string.IsNullOrWhiteSpace(title))
            return title;

        return (release.tag_name ?? string.Empty).Trim();
    }

    private static string NormalizeVersion(Version version)
    {
        if (version.Build < 0)
            return $"{version.Major}.{version.Minor}";
        if (version.Revision < 0)
            return $"{version.Major}.{version.Minor}.{version.Build}";
        return version.ToString();
    }

    private static GitHubAsset? FindBestExecutableAsset(GitHubRelease release, string currentExecutableName)
    {
        var assets = release.assets ?? Array.Empty<GitHubAsset>();
        if (assets.Length == 0)
            return null;

        var exactCurrent = assets.FirstOrDefault(asset =>
            string.Equals(asset.name, currentExecutableName, StringComparison.OrdinalIgnoreCase));
        if (exactCurrent != null)
            return exactCurrent;

        var exactAssembly = assets.FirstOrDefault(asset =>
            string.Equals(asset.name, "LatticeVeilMonoGame.exe", StringComparison.OrdinalIgnoreCase));
        if (exactAssembly != null)
            return exactAssembly;

        var singleExe = assets
            .Where(asset => asset.name != null
                && asset.name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                && !LooksLikeInstallerAsset(asset.name))
            .ToArray();

        if (singleExe.Length == 1)
            return singleExe[0];

        return singleExe.FirstOrDefault(asset =>
            asset.name != null && asset.name.Contains("LatticeVeil", StringComparison.OrdinalIgnoreCase));
    }

    private static bool LooksLikeInstallerAsset(string name)
    {
        return name.Contains("installer", StringComparison.OrdinalIgnoreCase)
            || name.Contains("uninstaller", StringComparison.OrdinalIgnoreCase)
            || name.Contains("setup", StringComparison.OrdinalIgnoreCase);
    }

    private static string BuildUpdateScript(int currentProcessId, string sourcePath, string targetPath, string restartArgs)
    {
        static string QuoteForCmd(string value) => "\"" + value.Replace("\"", "\"\"") + "\"";

        var quotedSource = QuoteForCmd(sourcePath);
        var quotedTarget = QuoteForCmd(targetPath);
        var quotedRestartArgs = string.IsNullOrWhiteSpace(restartArgs) ? string.Empty : " " + restartArgs.Trim();

        var sb = new StringBuilder();
        sb.AppendLine("@echo off");
        sb.AppendLine("setlocal");
        sb.AppendLine($"set \"LV_PID={currentProcessId}\"");
        sb.AppendLine($"set \"LV_SRC={sourcePath}\"");
        sb.AppendLine($"set \"LV_DST={targetPath}\"");
        sb.AppendLine(":wait_for_launcher");
        sb.AppendLine("tasklist /FI \"PID eq %LV_PID%\" | find /I \"%LV_PID%\" >nul");
        sb.AppendLine("if not errorlevel 1 (");
        sb.AppendLine("  timeout /t 1 /nobreak >nul");
        sb.AppendLine("  goto wait_for_launcher");
        sb.AppendLine(")");
        sb.AppendLine($"copy /Y {quotedSource} {quotedTarget} >nul");
        sb.AppendLine("if errorlevel 1 exit /b 1");
        sb.AppendLine($"del /F /Q {quotedSource} >nul 2>nul");
        sb.AppendLine($"start \"\" {quotedTarget}{quotedRestartArgs}");
        return sb.ToString();
    }

    private static void EnsureWritableDirectory(string path, string displayName)
    {
        Directory.CreateDirectory(path);
        var probePath = Path.Combine(path, ".write_probe");
        try
        {
            using (var stream = new FileStream(probePath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                stream.WriteByte(0);
            }

            File.Delete(probePath);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Write access is required for the {displayName}: {path}", ex);
        }
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
            // Best effort only.
        }
    }
}
