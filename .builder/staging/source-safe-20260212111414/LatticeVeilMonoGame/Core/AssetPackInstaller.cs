using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace LatticeVeilMonoGame.Core;

public sealed class AssetPackCheckResult
{
    public AssetReleaseInfo? Release { get; }
    public InstalledAssetsMarker? Installed { get; }
    public bool IsUpToDate { get; }

    public AssetPackCheckResult(AssetReleaseInfo? release, InstalledAssetsMarker? installed, bool isUpToDate)
    {
        Release = release;
        Installed = installed;
        IsUpToDate = isUpToDate;
    }
}

public sealed class AssetPackInstaller
{
    private const string AssetName = "Assets.zip";
    private const string AssetsOwner = "latticeveil";
    private const string AssetsRepo = "Assets";
    private readonly Logger _log;
    private readonly HttpClient _http;
    private readonly GitHubRepoClient _repoClient;
    private readonly GitHubReleaseClient _releaseClient;
    private readonly JsonSerializerOptions _jsonOptions = new() { WriteIndented = true };

    private static readonly string[] RequiredAssets =
    {
        "textures/menu/backgrounds/MainMenu.png",
        "textures/menu/buttons/Singleplayer.png",
        "textures/menu/buttons/Multiplayer.png",
        "textures/menu/buttons/options.png",
        "textures/menu/buttons/Quit.png",
        "textures/menu/buttons/Profile.png",
        "textures/blocks/grass.png",
        "textures/blocks/dirt.png",
        "textures/blocks/stone.png"
    };

    public static string DownloadsDir => Path.Combine(Paths.RootDir, "_downloads");
    public static string StagingDir => Path.Combine(Paths.RootDir, "_assets_staging");
    public static string MarkerPath => Path.Combine(Paths.TexturesDir, ".asset_state.json");
    private static string LegacyMarkerPath => Path.Combine(Paths.RootDir, "assets_installed.json");

    public AssetPackInstaller(Logger log)
    {
        _log = log;
        _http = CreateHttpClient();
        _repoClient = new GitHubRepoClient(_http);
        _releaseClient = new GitHubReleaseClient(_http);
    }

    public bool CheckLocalAssetsInstalled(out string[] missing)
    {
        Paths.EnsureAssetDirectoriesExist(_log);
        var list = new List<string>();
        if (!Directory.Exists(Paths.MenuTexturesDir))
            list.Add("textures/menu/");
        if (!Directory.Exists(Paths.BlocksTexturesDir))
            list.Add("textures/blocks/");
        foreach (var asset in RequiredAssets)
        {
            if (!AssetResolver.TryResolve(asset, out _))
                list.Add(asset);
        }

        missing = list.ToArray();
        return list.Count == 0;
    }

    public void PreflightWriteAccess(string assetsDir)
    {
        EnsureWritableDirectory(Paths.RootDir, "Documents\\LatticeVeil");
        EnsureWritableDirectory(assetsDir, "Documents\\LatticeVeil\\Assets");
    }

    public async Task<AssetPackCheckResult> CheckForUpdateAsync(bool useFixedTag, CancellationToken ct)
    {
        // Prefer the latest GitHub release asset only.
        AssetReleaseInfo? release;
        if (useFixedTag)
        {
            release = await GetLatestReleaseAssetAsync(ct);
        }
        else
        {
            // Optional: fallback to a repo archive when explicitly requested.
            release = await GetLatestArchiveAsync(ct) ?? GetRefFallback("main");
        }

        var installed = ReadInstalledMarker();
        var isUpToDate = false;

        if (release != null && installed != null && !release.IsFallback)
        {
            var published = release.PublishedAt?.ToString("O");
            var tagMatch = string.Equals(installed.Tag, release.Tag, StringComparison.OrdinalIgnoreCase);
            var publishedMatch = string.Equals(installed.PublishedAt, published, StringComparison.OrdinalIgnoreCase);
            var sizeMatch = !release.Size.HasValue || installed.Size == release.Size.Value;
            isUpToDate = tagMatch && publishedMatch && sizeMatch;
        }

        return new AssetPackCheckResult(release, installed, isUpToDate);
    }

    public async Task<string> DownloadAssetsZipAsync(string url, IProgress<float>? progress, CancellationToken ct)
    {
        EnsureWritableDirectory(DownloadsDir, "Documents\\LatticeVeil\\_downloads");
        Directory.CreateDirectory(DownloadsDir);

        var partPath = Path.Combine(DownloadsDir, "Assets.zip.part");
        var finalPath = Path.Combine(DownloadsDir, "Assets.zip");

        Exception? lastError = null;
        foreach (var candidate in BuildDownloadUrlCandidates(url))
        {
            TryDeleteFile(partPath);
            TryDeleteFile(finalPath);
            progress?.Report(0f);

            try
            {
                var path = await DownloadAssetsZipOnceAsync(candidate, partPath, finalPath, progress, ct);
                ValidateZip(path);
                return path;
            }
            catch (Exception ex)
            {
                lastError = ex;
                _log.Warn($"Download candidate failed ({candidate}): {ex.Message}");
                // Continue to next candidate
            }
        }

        TryDeleteFile(partPath);
        TryDeleteFile(finalPath);

        if (lastError != null)
            throw lastError;
        throw new InvalidOperationException("Assets download failed with no URL candidates.");
    }

    private void ValidateZip(string path)
    {
        var info = new FileInfo(path);
        if (!info.Exists)
            throw new FileNotFoundException("Downloaded file missing.");

        if (info.Length < 100) // 22 bytes is empty zip header
            throw new InvalidDataException($"Downloaded file is too small ({info.Length} bytes).");

        using var archive = ZipFile.OpenRead(path);
        if (archive.Entries.Count == 0)
            throw new InvalidDataException("Downloaded zip is empty (0 entries).");
        
        _log.Info($"Validated zip: {info.Length / 1024.0 / 1024.0:F2} MB, {archive.Entries.Count} entries.");
    }

    private async Task<string> DownloadAssetsZipOnceAsync(string url, string partPath, string finalPath, IProgress<float>? progress, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        if (resp.StatusCode == HttpStatusCode.NotFound)
            throw new HttpRequestException("Asset download URL not found.", null, resp.StatusCode);
        resp.EnsureSuccessStatusCode();

        var total = resp.Content.Headers.ContentLength ?? -1L;
        long read = 0;
        var buffer = new byte[1024 * 64];

        await using var content = await resp.Content.ReadAsStreamAsync(ct);
        await using var fs = new FileStream(partPath, FileMode.Create, FileAccess.Write, FileShare.None);

        while (true)
        {
            var bytes = await content.ReadAsync(buffer.AsMemory(0, buffer.Length), ct);
            if (bytes <= 0)
                break;
            await fs.WriteAsync(buffer.AsMemory(0, bytes), ct);
            read += bytes;

            if (total > 0)
            {
                var pct = Math.Clamp(read / (float)total, 0f, 1f);
                progress?.Report(pct);
            }
            else
            {
                var pct = Math.Min(0.95f, (float)(read / (1024.0 * 1024.0 * 50.0)));
                progress?.Report(pct);
            }
        }

        fs.Close();
        File.Move(partPath, finalPath, true);
        progress?.Report(1f);
        return finalPath;
    }

    public async Task ExtractZipAsync(string zipPath, string stagingDir, IProgress<float>? progress, CancellationToken ct)
    {
        if (!File.Exists(zipPath))
            throw new FileNotFoundException("Assets.zip not found.");

        _log.Info($"Extracting assets to {stagingDir}...");
        
        EnsureWritableDirectory(Paths.RootDir, "Documents\\LatticeVeil");
        
        if (Directory.Exists(stagingDir)) TryDeleteDirectory(stagingDir);
        Directory.CreateDirectory(stagingDir);

        await Task.Run(() =>
        {
            try
            {
                ZipFile.ExtractToDirectory(zipPath, stagingDir, true);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"Zip extraction failed: {ex.Message}", ex);
            }
        }, ct);
        
        progress?.Report(1.0f);
        _log.Info("Extraction complete. Locating assets...");
        
        FindAndPrepareAssets(stagingDir);
    }

    private void FindAndPrepareAssets(string stagingDir)
    {
        _log.Info($"Locating assets in: {stagingDir}");

        // Strategy: Locate the 'textures' folder. This is the definitive marker.
        // It could be at:
        // - staging/textures
        // - staging/Assets/textures
        // - staging/LatticeVeil-Assets-main/textures
        // - staging/LatticeVeil-Assets-main/Assets/textures

        var texturesDir = Directory.GetDirectories(stagingDir, "textures", SearchOption.AllDirectories)
            .OrderBy(d => d.Length) // Prefer shorter paths (closer to root)
            .FirstOrDefault();

        string? validRoot = null;

        if (texturesDir != null)
        {
            _log.Info($"Found 'textures' directory at: {texturesDir}");
            // The parent of "textures" is what we consider the "Asset Root".
            // e.g. if .../Repo/textures, then Repo is the root containing textures, blocks, etc.
            validRoot = Path.GetDirectoryName(texturesDir);
        }
        else
        {
            // Fallback: Look for "Assets" folder if "textures" is missing (unlikely for valid pack)
            var assetsDir = Directory.GetDirectories(stagingDir, "Assets", SearchOption.AllDirectories)
                .OrderBy(d => d.Length)
                .FirstOrDefault();
            
            if (assetsDir != null)
            {
                _log.Info($"Found 'Assets' directory at: {assetsDir}");
                // If we found .../Repo/Assets, then .../Repo/Assets is the root we want to move content FROM.
                // Wait, if structure is .../Repo/Assets/textures, then validRoot above would be .../Repo/Assets.
                // If structure is .../Repo/Assets (and textures is missing?), we take it.
                validRoot = assetsDir;
            }
        }

        if (validRoot == null)
        {
            var legacyRoot = TryGetSingleRootFolder(stagingDir) ?? stagingDir;
            if (LooksLikeLegacyRoot(legacyRoot))
            {
                _log.Info($"Found legacy asset layout at: {legacyRoot}");
                validRoot = legacyRoot;
            }
        }

        if (validRoot == null)
        {
            var listing = GetTopLevelListing(stagingDir);
            // Dig one level deeper for listing if single folder
            try {
                var subs = Directory.GetDirectories(stagingDir);
                if (subs.Length == 1) listing += " -> " + GetTopLevelListing(subs[0]);
            } catch {}

            throw new DirectoryNotFoundException($"Could not locate 'textures' or 'Assets' folder. Staging content: {listing}");
        }

        _log.Info($"Determined asset root: {validRoot}");

        var targetDir = Path.Combine(stagingDir, "Assets");
        var validRootFull = Path.GetFullPath(validRoot);
        var targetFull = Path.GetFullPath(targetDir);

        // If the found root IS ALREADY staging/Assets, we are done.
        if (string.Equals(validRootFull, targetFull, StringComparison.OrdinalIgnoreCase))
        {
            _log.Info("Assets are already in the correct location.");
            return;
        }

        // Check if we found .../Repo/Assets. We want to move it to .../staging/Assets.
        // We can't rename .../Repo/Assets to .../staging/Assets easily if they overlap or are locked.
        // But .../Repo/Assets is a subdir of staging. .../staging/Assets is a subdir of staging.
        // They are siblings (effectively).
        
        // CASE: validRoot is .../staging/Repo/Assets. target is .../staging/Assets.
        // CASE: validRoot is .../staging/Repo (containing textures). target is .../staging/Assets.

        if (!Directory.Exists(targetDir))
            Directory.CreateDirectory(targetDir);

        _log.Info($"Moving content from {validRoot} to {targetDir}");
        MoveChildren(validRoot, targetDir);
    }

    private static string? TryGetSingleRootFolder(string dir)
    {
        try
        {
            var dirs = Directory.GetDirectories(dir);
            var files = Directory.GetFiles(dir);
            // GitHub archives usually have one folder and no files at the top level.
            if (dirs.Length == 1 && files.Length == 0)
                return dirs[0];
            
            // If there's one folder and maybe some hidden files (like .DS_Store), still count it.
            if (dirs.Length == 1 && files.All(f => Path.GetFileName(f).StartsWith('.')))
                return dirs[0];
        }
        catch
        {
            // Best-effort.
        }

        return null;
    }

    private static bool LooksLikeLegacyRoot(string root)
    {
        try
        {
            var hasBlocks = Directory.Exists(Path.Combine(root, "blocks"));
            var hasMenu = Directory.Exists(Path.Combine(root, "menu"));
            return hasBlocks || hasMenu;
        }
        catch
        {
            return false;
        }
    }

    private static string? FindDirectoryNamed(string root, string name)
    {
        try
        {
            // First check top level
            var top = Path.Combine(root, name);
            if (Directory.Exists(top)) return top;

            return Directory.GetDirectories(root, name, SearchOption.AllDirectories)
                .OrderBy(d => d.Length)
                .FirstOrDefault();
        }
        catch
        {
            return null;
        }
    }

    private static string? FindLikelyAssetRoot(string root)
    {
        // If root directly contains textures/, treat it as the asset root.
        if (Directory.Exists(Path.Combine(root, "textures")))
            return root;

        try
        {
            // Search for textures folder
            var texDir = Directory.GetDirectories(root, "textures", SearchOption.AllDirectories)
                .OrderBy(d => d.Length)
                .FirstOrDefault();

            if (texDir != null)
            {
                return Path.GetDirectoryName(texDir);
            }
        }
        catch
        {
            // Best-effort.
        }

        return null;
    }

    private static string? FindFileNamed(string root, string fileName)
    {
        try
        {
            var top = Path.Combine(root, fileName);
            if (File.Exists(top)) return top;

            return Directory.GetFiles(root, fileName, SearchOption.AllDirectories)
                .OrderBy(f => f.Length)
                .FirstOrDefault();
        }
        catch
        {
            return null;
        }
    }

    private void MoveDirTo(string sourceDir, string destDir)
    {
        if (string.Equals(Path.GetFullPath(sourceDir), Path.GetFullPath(destDir), StringComparison.OrdinalIgnoreCase))
        {
            _log.Info("MoveDirTo: Source and destination are the same. Skipping move.");
            return;
        }

        TryDeleteDirectory(destDir);

        RetryIo(
            () => Directory.Move(sourceDir, destDir),
            _log,
            "Normalize repo archive layout",
            sourceDir);

        // Best-effort cleanup: if the old parent is now empty, remove it.
        TryDeleteIfEmpty(Path.GetDirectoryName(sourceDir) ?? string.Empty);
    }

    private void MoveChildren(string sourceDir, string destDir)
    {
        var destFull = Path.GetFullPath(destDir)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        foreach (var entry in Directory.EnumerateFileSystemEntries(sourceDir))
        {
            var entryFull = Path.GetFullPath(entry)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (string.Equals(entryFull, destFull, StringComparison.OrdinalIgnoreCase))
                continue;

            var name = Path.GetFileName(entry);
            if (string.IsNullOrWhiteSpace(name))
                continue;

            var dest = Path.Combine(destDir, name);

            RetryIo(
                () =>
                {
                    if (Directory.Exists(entry))
                        Directory.Move(entry, dest);
                    else
                        File.Move(entry, dest, true);
                },
                _log,
                "Normalize repo archive layout",
                entry);
        }
    }

    private void ExtractNestedAssetsZip(string zipPath, string destAssetsDir)
    {
        TryDeleteDirectory(destAssetsDir);
        Directory.CreateDirectory(destAssetsDir);

        using var archive = ZipFile.OpenRead(zipPath);
        RejectLegacyFoldersInZip(archive);

        foreach (var entry in archive.Entries)
        {
            if (string.IsNullOrWhiteSpace(entry.Name))
                continue;

            var full = (entry.FullName ?? string.Empty).Replace('\\', '/');
            var parts = full.Split('/', StringSplitOptions.RemoveEmptyEntries);
            var assetsIndex = Array.FindIndex(parts, p => string.Equals(p, "Assets", StringComparison.OrdinalIgnoreCase));
            var relativeParts = assetsIndex >= 0 ? parts[(assetsIndex + 1)..] : parts;
            if (relativeParts.Length == 0)
                continue;

            var relative = Path.Combine(relativeParts);
            var destPath = Path.Combine(destAssetsDir, relative);

            var fullPath = Path.GetFullPath(destPath);
            var root = Path.GetFullPath(destAssetsDir) + Path.DirectorySeparatorChar;
            if (!fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Zip entry path is invalid.");

            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            using var entryStream = entry.Open();
            using var outStream = new FileStream(fullPath, FileMode.Create, FileAccess.Write, FileShare.None);
            entryStream.CopyTo(outStream);
        }
    }

    private static string BuildDirectoryPreview(string dir, int max)
    {
        try
        {
            var parts = new List<string>();

            foreach (var d in Directory.GetDirectories(dir).Take(max))
                parts.Add(Path.GetFileName(d) + "/");

            foreach (var f in Directory.GetFiles(dir).Take(Math.Max(0, max - parts.Count)))
                parts.Add(Path.GetFileName(f));

            if (parts.Count == 0)
                return "(empty)";

            return string.Join(", ", parts);
        }
        catch
        {
            return "(unavailable)";
        }
    }

    private static void RejectLegacyFoldersInZip(ZipArchive archive)
    {
        // When downloading from the repo archive (zipball), GitHub wraps everything in a single root folder.
        // Only validate paths that are inside the Assets/ folder within the archive to avoid false positives.
        var hits = new List<string>();

        foreach (var entry in archive.Entries)
        {
            var full = (entry.FullName ?? string.Empty).Replace('\\', '/');
            if (string.IsNullOrWhiteSpace(full))
                continue;

            var parts = full.Split('/', StringSplitOptions.RemoveEmptyEntries);
            var assetsIndex = Array.FindIndex(parts, p => string.Equals(p, "Assets", StringComparison.OrdinalIgnoreCase));
            if (assetsIndex < 0)
                continue;

            for (var i = assetsIndex + 1; i < parts.Length; i++)
            {
                var name = parts[i];
                var parent = i > assetsIndex + 1 ? parts[i - 1] : string.Empty;
                if (IsLegacyFolder(name, parent))
                {
                    hits.Add(string.Join("/", parts[assetsIndex..(i + 1)]));
                    break;
                }
            }

            if (hits.Count >= 8)
                break;
        }

        if (hits.Count > 0)
        {
            var preview = string.Join(", ", hits);
            throw new InvalidOperationException(
                "Legacy asset folders detected in the package. Zip rejected. Only Assets/textures/menu and Assets/textures/blocks are supported. " +
                $"Found: {preview}");
        }
    }

    public void InstallStagedAssets(string stagingDir, string assetsDir, bool forceCleanInstall = false)
    {
        if (!Directory.Exists(stagingDir))
            throw new DirectoryNotFoundException("Staging folder missing.");

        var hadExisting = Directory.Exists(assetsDir);
        EnsureWritableDirectory(Paths.RootDir, "Documents\\LatticeVeil");
        EnsureWritableDirectory(assetsDir, "Documents\\LatticeVeil\\Assets");
        Directory.CreateDirectory(Paths.RootDir);
        Paths.EnsureAssetDirectoriesExist(_log);

        var stagingRoot = Path.Combine(stagingDir, "Assets");
        if (!Directory.Exists(stagingRoot))
            throw new DirectoryNotFoundException("Staging assets root missing (expected Assets folder in staging).");

        MigrateLegacyContent(stagingRoot);
        ValidateStagingRoot(stagingRoot);

        ClearReadOnlyAttributes(stagingRoot);
        if (Directory.Exists(assetsDir))
            ClearReadOnlyAttributes(assetsDir);

        var backupDir = $"{assetsDir}_backup";

        TryDeleteDirectory(backupDir);

        if (forceCleanInstall)
        {
            InstallStagedAssetsForceClean(stagingDir, assetsDir, stagingRoot, backupDir);
            return;
        }

        var movedToBackup = false;

        try
        {
            if (hadExisting)
            {
                RetryIo(
                    () => Directory.Move(assetsDir, backupDir),
                    _log,
                    "Move Assets to backup",
                    assetsDir);
                movedToBackup = true;
            }

            RetryIo(
                () => Directory.Move(stagingRoot, assetsDir),
                _log,
                "Swap staging to Assets",
                stagingRoot);

            if (!string.Equals(stagingRoot, stagingDir, StringComparison.OrdinalIgnoreCase))
                TryDeleteDirectory(stagingDir);

            if (Directory.Exists(backupDir))
            {
                if (!TryDeleteDirectory(backupDir))
                    _log.Warn($"Asset install left backup folder (locked?): {backupDir}");
            }
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
        {
            _log.Warn($"Swap install failed; falling back to merge install. Reason: {ex.Message}");
            LogInstallFailureDiagnostics(stagingDir, stagingRoot, assetsDir, backupDir, ex);

            if (movedToBackup && !Directory.Exists(assetsDir) && Directory.Exists(backupDir))
                AttemptRollback(assetsDir, backupDir);

            MergeInstall(stagingRoot, assetsDir);
            TryDeleteDirectory(stagingDir);
        }
        catch (Exception ex)
        {
            LogInstallFailureDiagnostics(stagingDir, stagingRoot, assetsDir, backupDir, ex);
            AttemptRollback(assetsDir, backupDir);
            throw;
        }

    }

    private void InstallStagedAssetsForceClean(string stagingDir, string assetsDir, string stagingRoot, string backupDir)
    {
        try
        {
            if (Directory.Exists(assetsDir))
            {
                RetryIo(
                    () => Directory.Move(assetsDir, backupDir),
                    _log,
                    "Move Assets to backup (force reset)",
                    assetsDir);
            }

            RetryIo(
                () => Directory.Move(stagingRoot, assetsDir),
                _log,
                "Swap staging to Assets (force reset)",
                stagingRoot);

            if (!string.Equals(stagingRoot, stagingDir, StringComparison.OrdinalIgnoreCase))
                TryDeleteDirectory(stagingDir);

            if (Directory.Exists(backupDir) && !TryDeleteDirectory(backupDir))
                _log.Warn($"Asset reset left backup folder (locked?): {backupDir}");
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
        {
            LogInstallFailureDiagnostics(stagingDir, stagingRoot, assetsDir, backupDir, ex);
            throw new InvalidOperationException(
                "Asset reset requires replacing the full Assets folder, but some files are locked.\n" +
                "Close the game/launcher and any Explorer windows previewing files, then retry.",
                ex);
        }
    }

    public InstalledAssetsMarker? ReadInstalledMarker()
    {
        try
        {
            var path = MarkerPath;
            if (!File.Exists(path))
            {
                TryDeleteLegacyMarker();
                return null;
            }

            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<InstalledAssetsMarker>(json);
        }
        catch (Exception ex)
        {
            _log.Warn($"Failed to read installed marker: {ex.Message}");
            return null;
        }
    }

    public void WriteInstalledMarker(AssetReleaseInfo release)
    {
        try
        {
            EnsureWritableDirectory(Paths.AssetsDir, "Documents\\LatticeVeil\\Assets");
            Paths.EnsureAssetDirectoriesExist(_log);
            var marker = new InstalledAssetsMarker
            {
                Tag = release.Tag,
                PublishedAt = release.PublishedAt?.ToString("O"),
                AssetName = release.AssetName,
                Size = release.Size ?? 0,
                DownloadUrl = release.DownloadUrl,
                InstalledAt = DateTimeOffset.Now.ToString("O")
            };

            var json = JsonSerializer.Serialize(marker, _jsonOptions);
            File.WriteAllText(MarkerPath, json);
            TryHideMarker(MarkerPath);
            TryDeleteLegacyMarker();
        }
        catch (Exception ex)
        {
            _log.Warn($"Failed to write installed marker: {ex.Message}");
        }
    }

    public void CleanupAfterInstall()
    {
        TryDeleteFile(Path.Combine(DownloadsDir, "Assets.zip.part"));
        TryDeleteFile(Path.Combine(DownloadsDir, "Assets.zip"));
        TryDeleteDirectory(StagingDir);
        TryDeleteIfEmpty(DownloadsDir);
    }

    /// <summary>
    /// If the user manually copied assets (or a previous install succeeded but the marker didn't write),
    /// create a marker so the launcher can skip downloading on future launches.
    /// </summary>
    public void EnsureLocalMarkerExists()
    {
        try
        {
            if (File.Exists(MarkerPath))
                return;

            if (!CheckLocalAssetsInstalled(out _))
                return;

            EnsureWritableDirectory(Paths.AssetsDir, "Documents\\LatticeVeil\\Assets");
            Paths.EnsureAssetDirectoriesExist(_log);

            var marker = new InstalledAssetsMarker
            {
                Tag = "local",
                PublishedAt = null,
                AssetName = AssetName,
                Size = 0,
                DownloadUrl = null,
                InstalledAt = DateTimeOffset.Now.ToString("O")
            };

            var json = JsonSerializer.Serialize(marker, _jsonOptions);
            File.WriteAllText(MarkerPath, json);
            TryHideMarker(MarkerPath);
            TryDeleteLegacyMarker();
        }
        catch
        {
            // Best-effort.
        }
    }

    private async Task<AssetReleaseInfo?> GetLatestArchiveAsync(CancellationToken ct)
    {
        try
        {
            const string owner = AssetsOwner;
            const string repo = AssetsRepo;

            // Prefer the repo's default branch (usually main) and grab the latest commit.
            var meta = await _repoClient.FetchRepoMetaAsync(owner, repo, ct);
            var branch = meta?.default_branch;
            if (string.IsNullOrWhiteSpace(branch))
                branch = "main";

            return await GetArchiveByRefAsync(branch, ct);
        }
        catch (Exception ex)
        {
            _log.Warn($"Repo API failed (latest): {ex.Message}");
            return null;
        }
    }

    private async Task<AssetReleaseInfo?> GetLatestReleaseAssetAsync(CancellationToken ct)
    {
        try
        {
            var release = await _releaseClient.FetchLatestReleaseAsync(ct);
            if (release == null)
                return null;

            var asset = FindReleaseAsset(release);
            if (asset == null || string.IsNullOrWhiteSpace(asset.browser_download_url))
            {
                _log.Warn("Latest release is missing Assets.zip.");
                return null;
            }

            return new AssetReleaseInfo
            {
                Tag = release.tag_name,
                PublishedAt = release.published_at,
                AssetName = asset.name ?? AssetName,
                DownloadUrl = asset.browser_download_url,
                Size = asset.size,
                IsFallback = false
            };
        }
        catch (Exception ex)
        {
            _log.Warn($"Release API failed (latest): {ex.Message}");
            return null;
        }
    }

    private static GitHubAsset? FindReleaseAsset(GitHubRelease release)
    {
        var assets = release.assets;
        if (assets == null || assets.Length == 0)
            return null;

        var exact = assets.FirstOrDefault(a =>
            string.Equals(a.name, AssetName, StringComparison.OrdinalIgnoreCase));
        if (exact != null)
            return exact;

        var named = assets.FirstOrDefault(a =>
            !string.IsNullOrWhiteSpace(a.name) &&
            a.name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) &&
            a.name.Contains("asset", StringComparison.OrdinalIgnoreCase));
        if (named != null)
            return named;

        if (assets.Length == 1)
            return assets[0];

        return null;
    }

    private async Task<AssetReleaseInfo?> GetArchiveByRefAsync(string @ref, CancellationToken ct)
    {
        try
        {
            const string owner = AssetsOwner;
            const string repo = AssetsRepo;

            var commit = await _repoClient.FetchCommitAsync(owner, repo, @ref, ct);
            if (commit?.sha == null)
                return null;

            var date = commit.commit?.committer?.date ?? commit.commit?.author?.date;

            return new AssetReleaseInfo
            {
                Tag = commit.sha,
                PublishedAt = date,
                AssetName = AssetName,
                DownloadUrl = BuildZipballUrl(owner, repo, commit.sha),
                Size = null,
                IsFallback = false
            };
        }
        catch (Exception ex)
        {
            _log.Warn($"Repo API failed (ref={@ref}): {ex.Message}");
            return null;
        }
    }

    private static string BuildZipballUrl(string owner, string repo, string refOrSha)
    {
        // Use GitHub's source archive URLs (github.com/.../archive/...).
        // These are commonly more accessible than api.github.com on restricted networks.
        // See GitHub Docs: "Downloading source code archives".
        if (LooksLikeCommitSha(refOrSha))
        {
            var sha = Uri.EscapeDataString(refOrSha);
            return $"https://github.com/{owner}/{repo}/archive/{sha}.zip";
        }

        var safeRef = EscapeArchiveRef(refOrSha);
        return $"https://github.com/{owner}/{repo}/archive/refs/heads/{safeRef}.zip";
    }

    private static IEnumerable<string> BuildDownloadUrlCandidates(string initialUrl)
    {
        var urls = new List<string>();

        void Add(string candidate)
        {
            if (string.IsNullOrWhiteSpace(candidate))
                return;
            if (urls.Contains(candidate, StringComparer.OrdinalIgnoreCase))
                return;
            urls.Add(candidate);
        }

        Add(initialUrl);
        // Fallbacks in case a specific commit/archive URL 404s.
        Add(BuildZipballUrl(AssetsOwner, AssetsRepo, "main"));
        Add(BuildZipballUrl(AssetsOwner, AssetsRepo, "master"));

        return urls;
    }

    private static bool LooksLikeCommitSha(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;

        var s = value.Trim();
        if (s.Length < 7 || s.Length > 40)
            return false;

        for (var i = 0; i < s.Length; i++)
        {
            var c = s[i];
            var isHex = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');
            if (!isHex)
                return false;
        }

        return true;
    }

    private static string EscapeArchiveRef(string refName)
    {
        // Preserve '/' for branches like "feature/foo".
        return string.Join("/", refName
            .Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Select(Uri.EscapeDataString));
    }

    private static AssetReleaseInfo GetLatestFallback()
    {
        return new AssetReleaseInfo
        {
            Tag = "main",
            AssetName = AssetName,
            // Download the repo archive directly (not pinned).
            DownloadUrl = BuildZipballUrl(AssetsOwner, AssetsRepo, "main"),
            IsFallback = true
        };
    }

    private static AssetReleaseInfo GetRefFallback(string @ref)
    {
        return new AssetReleaseInfo
        {
            Tag = @ref,
            AssetName = AssetName,
            // Fallback: download an archive of a branch/tag ref (not pinned unless @ref is a commit SHA).
            DownloadUrl = BuildZipballUrl(AssetsOwner, AssetsRepo, @ref),
            IsFallback = true
        };
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
            // Best-effort.
        }
    }

    private static void TryDeleteIfEmpty(string path)
    {
        try
        {
            if (!Directory.Exists(path))
                return;
            if (Directory.EnumerateFileSystemEntries(path).Any())
                return;
            Directory.Delete(path);
        }
        catch
        {
            // Best-effort.
        }
    }

    private static bool TryDeleteDirectory(string path)
    {
        try
        {
            if (!Directory.Exists(path))
                return true;
            ClearReadOnlyAttributes(path);
            Directory.Delete(path, true);
            return true;
        }
        catch
        {
            // Best-effort cleanup only.
            return false;
        }
    }

    private static void EnsureWritableDirectory(string path, string displayName)
    {
        try
        {
            if (Directory.Exists(path))
                ClearReadOnlyAttributes(path);
            Directory.CreateDirectory(path);
            var testPath = Path.Combine(path, ".write_test");
            using (var fs = new FileStream(testPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                fs.WriteByte(0x0);
            }
            File.Delete(testPath);
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new InvalidOperationException(
                $"Access denied to {displayName}. Allow write access to \"{path}\" (check folder permissions or Windows Controlled Folder Access).",
                ex);
        }
        catch (IOException ex) when (IsAccessDenied(ex))
        {
            throw new InvalidOperationException(
                $"Access denied to {displayName}. Allow write access to \"{path}\" (check folder permissions or Windows Controlled Folder Access).",
                ex);
        }
    }

    private static bool IsAccessDenied(IOException ex)
    {
        var msg = ex.Message ?? string.Empty;
        return msg.IndexOf("denied", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static void ClearReadOnlyAttributes(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                foreach (var entry in Directory.EnumerateFileSystemEntries(path, "*", SearchOption.AllDirectories))
                {
                    TryClearReadOnly(entry);
                }
            }
            TryClearReadOnly(path);
        }
        catch
        {
            // Best-effort: clearing attributes isn't critical.
        }
    }

    private static void TryClearReadOnly(string path)
    {
        try
        {
            var attrs = File.GetAttributes(path);
            if ((attrs & FileAttributes.ReadOnly) != 0)
                File.SetAttributes(path, attrs & ~FileAttributes.ReadOnly);
        }
        catch
        {
            // Ignore attribute cleanup failures.
        }
    }

    private static HttpClient CreateHttpClient()
    {
        var handler = new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
        };
        var client = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromMinutes(10)
        };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("LatticeVeilMonoGame", "1.0"));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return client;
    }

    private string ResolveStagingRoot(string stagingDir)
    {
        try
        {
            var files = Directory.GetFiles(stagingDir);
            var dirs = Directory.GetDirectories(stagingDir);
            if (files.Length == 0 && dirs.Length == 1)
            {
                var onlyDir = dirs[0];
                if (string.Equals(Path.GetFileName(onlyDir), "Assets", StringComparison.OrdinalIgnoreCase))
                    return onlyDir;
            }
        }
        catch
        {
            // Fall back to stagingDir.
        }

        return stagingDir;
    }

    private void MigrateLegacyContent(string stagingRoot)
    {
        try
        {
            var blocksLegacy = Path.Combine(stagingRoot, "blocks");
            var menuLegacy = Path.Combine(stagingRoot, "menu");
            var texturesDir = Path.Combine(stagingRoot, "textures");

            if (Directory.Exists(blocksLegacy))
            {
                var target = Path.Combine(texturesDir, "blocks");
                Directory.CreateDirectory(texturesDir);
                if (Directory.Exists(target)) TryDeleteDirectory(target);
                Directory.Move(blocksLegacy, target);
                _log.Info("Migrated legacy 'blocks' folder to 'textures/blocks'.");
            }

            if (Directory.Exists(menuLegacy))
            {
                var target = Path.Combine(texturesDir, "menu");
                Directory.CreateDirectory(texturesDir);
                if (Directory.Exists(target)) TryDeleteDirectory(target);
                Directory.Move(menuLegacy, target);
                _log.Info("Migrated legacy 'menu' folder to 'textures/menu'.");
            }
        }
        catch (Exception ex)
        {
            _log.Warn($"Legacy migration failed: {ex.Message}");
        }
    }

    private void ValidateStagingRoot(string stagingRoot)
    {
        try
        {
            if (!Directory.Exists(stagingRoot))
                throw new DirectoryNotFoundException("Staging assets root missing.");

            // Migration logic handles moving legacy folders, so we just check for final validity here.

            var hasRequired = RequiredAssets.Any(r => File.Exists(Path.Combine(stagingRoot, r)));
            var hasFolders = Directory.Exists(Path.Combine(stagingRoot, "textures", "blocks"))
                             || Directory.Exists(Path.Combine(stagingRoot, "textures", "menu"));

            if (!hasRequired && !hasFolders)
                throw new InvalidOperationException("Staging assets appear invalid (missing required files).");
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Staging validation failed: {ex.Message}", ex);
        }
    }

    private static List<string> FindLegacyFolders(string stagingRoot)
    {
        var hits = new List<string>();

        try
        {
            foreach (var dir in Directory.EnumerateDirectories(stagingRoot, "*", SearchOption.AllDirectories))
            {
                var name = Path.GetFileName(dir);
                if (string.IsNullOrWhiteSpace(name))
                    continue;

                var parent = Path.GetFileName(Path.GetDirectoryName(dir) ?? string.Empty);
                if (IsLegacyFolder(name, parent))
                {
                    hits.Add(Path.GetRelativePath(stagingRoot, dir));
                    if (hits.Count >= 12)
                        break;
                }
            }
        }
        catch
        {
            // Best-effort: if enumeration fails, do not block install here.
        }

        return hits;
    }

    private static bool IsLegacyFolder(string name, string parent)
    {
        if (name.Equals("menu", StringComparison.OrdinalIgnoreCase))
        {
            if (!parent.Equals("textures", StringComparison.Ordinal))
                return true;
            return !name.Equals("menu", StringComparison.Ordinal);
        }

        if (name.Equals("blocks", StringComparison.OrdinalIgnoreCase))
        {
            if (!parent.Equals("textures", StringComparison.Ordinal))
                return true;
            return !name.Equals("blocks", StringComparison.Ordinal);
        }

        if (parent.Equals("textures", StringComparison.Ordinal) &&
            (name.Equals("Menu", StringComparison.Ordinal) || name.Equals("Blocks", StringComparison.Ordinal)))
            return true;

        return false;
    }

    private static void RetryIo(Action action, Logger log, string opName, string? normalizePath)
    {
        Exception? last = null;
        for (var i = 0; i < 10; i++)
        {
            try
            {
                action();
                return;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                last = ex;
                if (!string.IsNullOrWhiteSpace(normalizePath))
                    ClearReadOnlyAttributes(normalizePath);
                Thread.Sleep(Math.Min(1000, 50 * (i + 1)));
            }
        }

        log.Warn($"{opName} failed after retries: {last?.Message}");
        throw last ?? new IOException($"{opName} failed.");
    }

    private void AttemptRollback(string assetsDir, string backupDir)
    {
        try
        {
            if (!Directory.Exists(assetsDir) && Directory.Exists(backupDir))
                RetryIo(() => Directory.Move(backupDir, assetsDir), _log, "Rollback Assets from backup", backupDir);
        }
        catch (Exception ex)
        {
            _log.Warn($"Asset rollback failed: {ex.Message}");
        }
    }

    private void MergeInstall(string stagingRoot, string assetsDir)
    {
        Directory.CreateDirectory(assetsDir);
        EnsureWritableDirectory(assetsDir, "Documents\\LatticeVeil\\Assets");

        var failures = new List<string>();
        var createdDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var dir in Directory.GetDirectories(stagingRoot, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(stagingRoot, dir);
            var destDir = Path.Combine(assetsDir, rel);
            if (createdDirs.Contains(destDir))
                continue;

            try
            {
                RetryIo(() => Directory.CreateDirectory(destDir), _log, "Create dir", destDir);
                createdDirs.Add(destDir);
            }
            catch (Exception ex)
            {
                failures.Add($"{rel} (dir) - {ex.Message}");
            }
        }

        foreach (var file in Directory.GetFiles(stagingRoot, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(stagingRoot, file);
            var dest = Path.Combine(assetsDir, rel);
            var destDir = Path.GetDirectoryName(dest) ?? assetsDir;
            var tmp = dest + ".tmp";

            try
            {
                RetryIo(() => Directory.CreateDirectory(destDir), _log, "Ensure dir", destDir);
                RetryIo(() =>
                {
                    // Clear attributes on the destination before attempting an overwrite.
                    if (File.Exists(dest))
                        TryClearReadOnly(dest);

                    if (File.Exists(tmp))
                    {
                        TryClearReadOnly(tmp);
                        File.Delete(tmp);
                    }

                    File.Copy(file, tmp, true);
                    TryClearReadOnly(tmp);
                }, _log, "Copy temp", tmp);

                RetryIo(() =>
                {
                    if (File.Exists(dest))
                        TryClearReadOnly(dest);
                    File.Move(tmp, dest, true);
                    TryClearReadOnly(dest);
                }, _log, "Swap file", dest);
            }
            catch (Exception ex)
            {
                failures.Add($"{rel} - {ex.Message}");
            }
            finally
            {
                // Don't leave behind partial .tmp files if something fails mid-install.
                TryDeleteFile(tmp);
            }
        }

        if (failures.Count > 0)
        {
            var preview = string.Join(Environment.NewLine, failures.Take(8));
            throw new InvalidOperationException(
                "Merge install failed. Some files could not be written.\n" +
                "Close the game/launcher and any Explorer windows previewing files, then retry.\n" +
                "First failures:\n" + preview);
        }

        if (!TryDeleteDirectory(stagingRoot))
            _log.Warn($"Failed to delete staging folder after merge: {stagingRoot}");
    }

    private void LogInstallFailureDiagnostics(string stagingDir, string stagingRoot, string assetsDir, string backupDir, Exception ex)
    {
        try
        {
            _log.Error($"Asset install failed: {ex.Message}");
            _log.Warn($"StagingDir: {stagingDir} (exists={Directory.Exists(stagingDir)})");
            _log.Warn($"StagingRoot: {stagingRoot} (exists={Directory.Exists(stagingRoot)})");
            _log.Warn($"AssetsDir: {assetsDir} (exists={Directory.Exists(assetsDir)})");
            _log.Warn($"BackupDir: {backupDir} (exists={Directory.Exists(backupDir)})");
            _log.Warn($"Staging listing: {GetTopLevelListing(stagingRoot)}");
            _log.Warn($"Assets listing: {GetTopLevelListing(assetsDir)}");
        }
        catch
        {
            // Best-effort logging only.
        }
    }

    private static string GetTopLevelListing(string path)
    {
        try
        {
            if (!Directory.Exists(path))
                return "(missing)";

            var entries = Directory.GetFileSystemEntries(path);
            if (entries.Length == 0)
                return "(empty)";

            return string.Join(", ", entries.Select(Path.GetFileName));
        }
        catch
        {
            return "(unavailable)";
        }
    }

    private static void TryHideMarker(string path)
    {
        try
        {
            if (!File.Exists(path))
                return;

            var attrs = File.GetAttributes(path);
            if ((attrs & FileAttributes.Hidden) == 0)
                File.SetAttributes(path, attrs | FileAttributes.Hidden);
        }
        catch
        {
            // Best-effort.
        }
    }

    private void TryDeleteLegacyMarker()
    {
        try
        {
            if (File.Exists(LegacyMarkerPath))
                File.Delete(LegacyMarkerPath);
        }
        catch (Exception ex)
        {
            _log.Warn($"Failed to remove legacy marker: {ex.Message}");
        }
    }
}
