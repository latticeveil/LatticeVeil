using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace LatticeVeilBuilder
{
    public class AssetManager
    {
        private readonly string _assetsDir;
        private readonly string _downloadsDir;
        private readonly HttpClient _httpClient;
        private readonly Dictionary<string, AssetInfo> _manifest;

        public AssetManager(string assetsDir)
        {
            _assetsDir = assetsDir;
            _downloadsDir = Path.Combine(Path.GetDirectoryName(assetsDir) ?? "", "Downloads");
            _httpClient = new HttpClient();
            _manifest = new Dictionary<string, AssetInfo>();

            Directory.CreateDirectory(_assetsDir);
            Directory.CreateDirectory(_downloadsDir);
        }

        public async Task<bool> LoadManifestAsync(string manifestUrl)
        {
            try
            {
                var response = await _httpClient.GetStringAsync(manifestUrl);
                var manifestData = JsonSerializer.Deserialize<ManifestData>(response);
                
                if (manifestData?.Files != null)
                {
                    _manifest.Clear();
                    foreach (var file in manifestData.Files)
                    {
                        _manifest[file.Path] = file;
                    }
                    return true;
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to load manifest: {ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            return false;
        }

        public async Task<bool> CheckAndUpdateAssetsAsync(IProgress<string> progress)
        {
            progress?.Report("Checking assets...");
            
            var needsUpdate = false;
            foreach (var kvp in _manifest)
            {
                var relativePath = kvp.Key;
                var assetInfo = kvp.Value;
                var localPath = Path.Combine(_assetsDir, relativePath);

                if (!File.Exists(localPath))
                {
                    progress?.Report($"Missing: {relativePath}");
                    needsUpdate = true;
                    continue;
                }

                var localHash = await ComputeFileHashAsync(localPath);
                if (localHash != assetInfo.Sha256)
                {
                    progress?.Report($"Changed: {relativePath}");
                    needsUpdate = true;
                }
            }

            if (!needsUpdate)
            {
                progress?.Report("All assets up to date");
                return true;
            }

            return await DownloadAssetsAsync(progress);
        }

        public async Task<bool> RepairAssetsAsync(IProgress<string> progress)
        {
            progress?.Report("Repairing assets...");
            return await DownloadAssetsAsync(progress);
        }

        private async Task<bool> DownloadAssetsAsync(IProgress<string> progress)
        {
            var success = true;
            var totalFiles = _manifest.Count;
            var processedFiles = 0;

            foreach (var kvp in _manifest)
            {
                var relativePath = kvp.Key;
                var assetInfo = kvp.Value;
                var localPath = Path.Combine(_assetsDir, relativePath);
                var downloadUrl = $"https://raw.githubusercontent.com/LatticeVeil/LatticeVeil/main/Assets/{relativePath}";

                try
                {
                    progress?.Report($"Downloading {processedFiles + 1}/{totalFiles}: {Path.GetFileName(relativePath)}");

                    // Download to temp location first
                    var tempPath = Path.Combine(_downloadsDir, Path.GetFileName(relativePath));
                    var response = await _httpClient.GetAsync(downloadUrl);
                    
                    if (!response.IsSuccessStatusCode)
                    {
                        progress?.Report($"Failed to download {relativePath}: {response.StatusCode}");
                        success = false;
                        continue;
                    }

                    await File.WriteAllBytesAsync(tempPath, await response.Content.ReadAsByteArrayAsync());

                    // Verify hash
                    var downloadedHash = await ComputeFileHashAsync(tempPath);
                    if (downloadedHash != assetInfo.Sha256)
                    {
                        progress?.Report($"Hash mismatch for {relativePath}");
                        File.Delete(tempPath);
                        success = false;
                        continue;
                    }

                    // Move to final location
                    Directory.CreateDirectory(Path.GetDirectoryName(localPath) ?? "");
                    File.Move(tempPath, localPath, true);
                    
                    processedFiles++;
                }
                catch (Exception ex)
                {
                    progress?.Report($"Error downloading {relativePath}: {ex.Message}");
                    success = false;
                }
            }

            // Clean up downloads directory
            try
            {
                if (Directory.Exists(_downloadsDir))
                {
                    Directory.Delete(_downloadsDir, true);
                }
            }
            catch { }

            progress?.Report(success ? "Asset update complete" : "Asset update completed with errors");
            return success;
        }

        private async Task<string> ComputeFileHashAsync(string filePath)
        {
            try
            {
                using var sha256 = SHA256.Create();
                using var stream = File.OpenRead(filePath);
                var hash = await sha256.ComputeHashAsync(stream);
                return Convert.ToHexString(hash).ToLowerInvariant();
            }
            catch
            {
                return string.Empty;
            }
        }

        public void Dispose()
        {
            _httpClient?.Dispose();
        }
    }

    public class ManifestData
    {
        public string Version { get; set; } = "1.0";
        public string BaseUrl { get; set; } = "";
        public List<AssetInfo> Files { get; set; } = new();
    }

    public class AssetInfo
    {
        public string Path { get; set; } = "";
        public long Size { get; set; }
        public string Sha256 { get; set; } = "";
    }
}
