using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using LatticeVeilMonoGame.Core;

namespace LatticeVeilMonoGame.Launcher;

public sealed class DevGitHubReleaseService
{
    private const string Owner = "latticeveil";
    private const string Repo = "Lvdev";
    private const string Tag = "dev";
    private const string AssetsZipName = "DevAssets.zip";
    private const string ExecutableName = "LatticeVeilMonoGame.exe";

    private readonly Logger _log;
    private readonly GitHubReleaseClient _releaseClient = new();

    public DevGitHubReleaseService(Logger log)
    {
        _log = log;
    }

    public async Task<AssetReleaseInfo?> GetDevAssetsReleaseAsync(CancellationToken ct)
    {
        var fromGh = await GetDevAssetsReleaseViaGhAsync(ct).ConfigureAwait(false);
        if (fromGh != null)
            return fromGh;

        var release = await _releaseClient.FetchReleaseByTagAsync(Owner, Repo, Tag, ct).ConfigureAwait(false);
        if (release == null)
        {
            _log.Warn($"DEV GitHub release not readable: {Owner}/{Repo} tag {Tag}. Private repo access requires GitHub auth.");
            return null;
        }

        var asset = FindAsset(release, AssetsZipName);

        if (asset == null || string.IsNullOrWhiteSpace(asset.browser_download_url))
        {
            var names = string.Join(", ", release.assets?.Select(a => a.name).Where(n => !string.IsNullOrWhiteSpace(n)) ?? Array.Empty<string>());
            _log.Warn($"DEV GitHub release {Owner}/{Repo}@{Tag} is missing exact asset {AssetsZipName}. Assets present: {names}");
            return null;
        }

        return new AssetReleaseInfo
        {
            Tag = release.tag_name ?? Tag,
            PublishedAt = release.published_at,
            AssetName = asset.name ?? AssetsZipName,
            DownloadUrl = asset.browser_download_url,
            Size = asset.size,
            IsFallback = false
        };
    }

    private async Task<AssetReleaseInfo?> GetDevAssetsReleaseViaGhAsync(CancellationToken ct)
    {
        var gh = await ResolveGhPathAsync(ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(gh))
            return null;

        var result = await RunProcessAsync(gh, $"release view {Tag} --repo {Owner}/{Repo} --json tagName,publishedAt,assets", ct)
            .ConfigureAwait(false);
        if (result.ExitCode != 0 || string.IsNullOrWhiteSpace(result.Output))
        {
            _log.Warn($"DEV GitHub release not readable through gh: {result.Error}".Trim());
            return null;
        }

        try
        {
            var release = JsonSerializer.Deserialize<GhReleaseView>(result.Output, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });
            var asset = release?.Assets?.FirstOrDefault(a => string.Equals(a.Name, AssetsZipName, StringComparison.OrdinalIgnoreCase));
            if (asset == null || string.IsNullOrWhiteSpace(asset.Url))
            {
                var names = string.Join(", ", release?.Assets?.Select(a => a.Name).Where(n => !string.IsNullOrWhiteSpace(n)) ?? Array.Empty<string>());
                _log.Warn($"DEV GitHub release {Owner}/{Repo}@{Tag} is missing exact asset {AssetsZipName}. Assets present: {names}");
                return null;
            }

            return new AssetReleaseInfo
            {
                Tag = release?.TagName ?? Tag,
                PublishedAt = release?.PublishedAt,
                AssetName = asset.Name ?? AssetsZipName,
                DownloadUrl = asset.Url,
                Size = asset.Size,
                IsFallback = false
            };
        }
        catch (Exception ex)
        {
            _log.Warn($"Failed to parse gh DEV release response: {ex.Message}");
            return null;
        }
    }

    public Task UploadCurrentDevBuildAsync(IProgress<string>? status, CancellationToken ct)
    {
        _log.Info("DEV asset upload is disabled.");
        status?.Report("DEV asset upload is disabled.");
        return Task.CompletedTask;
    }

    public async Task<string> DownloadDevAssetsZipAsync(AssetReleaseInfo release, IProgress<string>? status, CancellationToken ct)
    {
        status?.Report("DEV download: locating GitHub CLI...");
        var gh = await ResolveGhPathAsync(ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(gh))
            throw new InvalidOperationException("GitHub CLI is required to download DEV assets from private Lvdev. Install GitHub CLI and run gh auth login.");

        Directory.CreateDirectory(GameReleaseUpdater.DownloadsDir);
        var finalPath = Path.Combine(GameReleaseUpdater.DownloadsDir, AssetsZipName);
        TryDeleteFile(finalPath);

        status?.Report("DEV download: downloading DevAssets.zip...");
        await RunGhAsync(gh, $"release download {Tag} --repo {Owner}/{Repo} --pattern {AssetsZipName} --dir \"{GameReleaseUpdater.DownloadsDir}\" --clobber", ct)
            .ConfigureAwait(false);

        if (!File.Exists(finalPath))
            throw new FileNotFoundException($"GitHub CLI finished but {AssetsZipName} was not found in downloads.", finalPath);

        var size = new FileInfo(finalPath).Length;
        if (size < 100)
            throw new InvalidDataException($"Downloaded {AssetsZipName} is too small ({size} bytes).");

        if (release.Size.HasValue && release.Size.Value > 0 && size != release.Size.Value)
            _log.Warn($"DEV asset zip size differs from release metadata. expected={release.Size.Value} actual={size}");

        status?.Report("DEV download: DevAssets.zip ready.");
        return finalPath;
    }

    private async Task EnsureReleaseExistsAsync(string gh, CancellationToken ct)
    {
        var result = await RunProcessAsync(gh, $"release view {Tag} --repo {Owner}/{Repo}", ct).ConfigureAwait(false);
        if (result.ExitCode == 0)
            return;

        await RunGhAsync(gh, $"release create {Tag} --repo {Owner}/{Repo} --title DEV --notes \"LatticeVeil DEV build channel\" --prerelease", ct)
            .ConfigureAwait(false);
    }

    private async Task RunGhAsync(string gh, string arguments, CancellationToken ct)
    {
        var result = await RunProcessAsync(gh, arguments, ct).ConfigureAwait(false);
        if (result.ExitCode != 0)
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(result.Error) ? result.Output : result.Error);
    }

    private static async Task<string> ResolveGhPathAsync(CancellationToken ct)
    {
        var result = await RunProcessAsync("gh", "--version", ct).ConfigureAwait(false);
        if (result.ExitCode == 0)
            return "gh";

        var candidates = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "GitHub CLI", "gh.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GitHubCLI", "gh.exe")
        };

        foreach (var candidate in candidates)
        {
            if (!File.Exists(candidate))
                continue;

            result = await RunProcessAsync(candidate, "--version", ct).ConfigureAwait(false);
            if (result.ExitCode == 0)
                return candidate;
        }

        return string.Empty;
    }

    private static async Task<(int ExitCode, string Output, string Error)> RunProcessAsync(string fileName, string arguments, CancellationToken ct)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            });
            if (process == null)
                return (-1, string.Empty, "Process did not start.");

            await process.WaitForExitAsync(ct).ConfigureAwait(false);
            return (process.ExitCode, await process.StandardOutput.ReadToEndAsync(ct).ConfigureAwait(false), await process.StandardError.ReadToEndAsync(ct).ConfigureAwait(false));
        }
        catch (Exception ex)
        {
            return (-1, string.Empty, ex.Message);
        }
    }

    private static GitHubAsset? FindAsset(GitHubRelease release, string name)
        => release.assets?.FirstOrDefault(a => string.Equals(a.name, name, StringComparison.OrdinalIgnoreCase));

    private sealed class GhReleaseView
    {
        public string? TagName { get; set; }
        public DateTimeOffset? PublishedAt { get; set; }
        public GhAsset[]? Assets { get; set; }
    }

    private sealed class GhAsset
    {
        public string? Name { get; set; }
        public string? Url { get; set; }
        public long Size { get; set; }
    }

    private static string ResolveCurrentExecutablePath()
    {
        var processPath = (Environment.ProcessPath ?? string.Empty).Trim();
        return !string.IsNullOrWhiteSpace(processPath) ? processPath : Path.Combine(AppContext.BaseDirectory, ExecutableName);
    }

    private static void CopyDirectory(string sourceDir, string destDir)
    {
        Directory.CreateDirectory(destDir);
        foreach (var dir in Directory.GetDirectories(sourceDir, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(sourceDir, dir);
            if (Paths.IsDisallowedAssetRelativePath(relative))
                continue;
            Directory.CreateDirectory(Path.Combine(destDir, relative));
        }

        foreach (var file in Directory.GetFiles(sourceDir, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(sourceDir, file);
            if (Paths.IsDisallowedAssetRelativePath(relative))
                continue;
            var target = Path.Combine(destDir, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
        }
    }

    private static void TryDeleteFile(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    private static void TryDeleteDirectory(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); } catch { }
    }
}
