using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;

namespace LatticeVeilMonoGame.Core;

public sealed class SkinLibraryEntry
{
    public string Hash { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public bool HasLayers { get; init; }
    public string PngPath { get; init; } = string.Empty;
    public string LvskinPath { get; init; } = string.Empty;
    public DateTime UpdatedUtc { get; init; }
    public bool IsActive { get; init; }
}

public static class SkinLibrary
{
    public const int SkinSize = 64;
    public const int MaxLocalSkins = 12;
    public const int MaxPngBytes = 32 * 1024;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    public static string SkinsDir => Paths.RuntimeSkinsDir;
    public static string ActiveHashPath => Paths.ActiveSkinHashPath;
    public static string ActiveSignalPath => Paths.SkinChangeSignalPath;

    public static IReadOnlyList<SkinLibraryEntry> ListSkins()
    {
        EnsureDirectory();
        var active = ReadActiveHash();
        return Directory.EnumerateFiles(SkinsDir, "*.png", SearchOption.TopDirectoryOnly)
            .Select(path => BuildEntry(Path.GetFileNameWithoutExtension(path), path, active))
            .Where(entry => entry != null)
            .Cast<SkinLibraryEntry>()
            .OrderByDescending(entry => entry.IsActive)
            .ThenByDescending(entry => entry.UpdatedUtc)
            .ToList();
    }

    public static string ReadActiveHash()
    {
        try
        {
            if (!File.Exists(ActiveHashPath))
                return string.Empty;

            var hash = File.ReadAllText(ActiveHashPath).Trim().ToLowerInvariant();
            return IsValidHash(hash) && File.Exists(GetPngPath(hash)) ? hash : string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    public static bool TryGetActivePngPath(out string path)
    {
        path = string.Empty;
        var hash = ReadActiveHash();
        if (string.IsNullOrWhiteSpace(hash))
            return false;

        var candidate = GetPngPath(hash);
        if (!File.Exists(candidate))
            return false;

        path = candidate;
        return true;
    }

    public static SkinLibraryEntry ImportPng(string sourcePath)
    {
        if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath))
            throw new FileNotFoundException("Skin PNG was not found.", sourcePath);

        return ImportPngBytes(File.ReadAllBytes(sourcePath), Path.GetFileNameWithoutExtension(sourcePath));
    }

    public static SkinLibraryEntry ImportPngBytes(byte[] sourceBytes, string? displayName = null)
    {
        if (sourceBytes == null || sourceBytes.Length == 0 || sourceBytes.Length > MaxPngBytes)
            throw new InvalidDataException($"Skin PNG must be between 1 byte and {MaxPngBytes / 1024} KB.");

        EnsureDirectory();
        var existing = ListSkins();
        using var source = new Bitmap(new MemoryStream(sourceBytes));
        if (source.Width != SkinSize || (source.Height != SkinSize && source.Height != 32))
            throw new InvalidDataException($"Skin must be a {SkinSize}x{SkinSize} PNG. Legacy 64x32 skins are accepted but do not include layers.");

        using var normalized = new Bitmap(SkinSize, SkinSize, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(normalized))
        {
            g.Clear(System.Drawing.Color.Transparent);
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.NearestNeighbor;
            g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.Half;
            g.DrawImage(
                source,
                new System.Drawing.Rectangle(0, 0, source.Width, source.Height),
                new System.Drawing.Rectangle(0, 0, source.Width, source.Height),
                GraphicsUnit.Pixel);
        }
        var hasLayers = source.Height == SkinSize && HasVisibleSecondLayer(normalized);

        byte[] pngBytes;
        using (var ms = new MemoryStream())
        {
            normalized.Save(ms, ImageFormat.Png);
            pngBytes = ms.ToArray();
        }

        if (pngBytes.Length > MaxPngBytes)
            throw new InvalidDataException($"Skin PNG is too large after normalization. Limit is {MaxPngBytes / 1024} KB.");

        var hash = ComputeSha256Hex(pngBytes);
        var pngPath = GetPngPath(hash);
        var lvskinPath = GetLvskinPath(hash);

        var alreadyExists = File.Exists(pngPath);
        displayName = NormalizeDisplayName(displayName, hash);
        if (alreadyExists)
        {
            if (!File.Exists(lvskinPath))
                WriteLvskin(lvskinPath, hash, pngBytes, displayName, source.Height == 32 ? "skin-64x32-converted" : "skin-64x64", hasLayers);

            return BuildEntry(hash, pngPath, ReadActiveHash())!;
        }

        if (!alreadyExists && existing.Count >= MaxLocalSkins)
            throw new InvalidOperationException($"Local skin library is full. Remove a skin before adding another. Limit: {MaxLocalSkins}.");

        File.WriteAllBytes(pngPath, pngBytes);
        WriteLvskin(lvskinPath, hash, pngBytes, displayName, source.Height == 32 ? "skin-64x32-converted" : "skin-64x64", hasLayers);
        return BuildEntry(hash, pngPath, ReadActiveHash())!;
    }

    public static bool EnsureLocalSkinFromCloud(string hash, byte[] pngBytes, string? displayName, bool setActive, out SkinLibraryEntry? entry, out string? error)
    {
        entry = null;
        error = null;

        try
        {
            hash = (hash ?? string.Empty).Trim().ToLowerInvariant();
            if (!IsValidHash(hash))
            {
                error = "Invalid skin hash.";
                return false;
            }

            if (pngBytes == null || pngBytes.Length == 0 || pngBytes.Length > MaxPngBytes)
            {
                error = $"Skin PNG must be between 1 byte and {MaxPngBytes / 1024} KB.";
                return false;
            }

            if (!string.Equals(ComputeSha256Hex(pngBytes), hash, StringComparison.OrdinalIgnoreCase))
            {
                error = "Skin hash did not match payload.";
                return false;
            }

            EnsureDirectory();
            var pngPath = GetPngPath(hash);
            var lvskinPath = GetLvskinPath(hash);
            if (File.Exists(pngPath))
            {
                entry = BuildEntry(hash, pngPath, ReadActiveHash());
                if (entry == null)
                {
                    error = "Existing skin file could not be read.";
                    return false;
                }

                if (!File.Exists(lvskinPath))
                {
                    displayName = NormalizeDisplayName(displayName, hash);
                    WriteLvskin(lvskinPath, hash, pngBytes, displayName, "skin-64x64", entry.HasLayers);
                    entry = BuildEntry(hash, pngPath, ReadActiveHash());
                }

                if (setActive)
                    SetActive(hash);
                return true;
            }

            using (var bitmap = new Bitmap(new MemoryStream(pngBytes)))
            {
                if (bitmap.Width != SkinSize || bitmap.Height != SkinSize)
                {
                    error = $"Cloud skin must be exactly {SkinSize}x{SkinSize} pixels.";
                    return false;
                }

                PruneForCloudSkinImport(hash);
                var hasLayers = HasVisibleSecondLayer(bitmap);
                File.WriteAllBytes(pngPath, pngBytes);
                displayName = NormalizeDisplayName(displayName, hash);
                WriteLvskin(lvskinPath, hash, pngBytes, displayName, "skin-64x64", hasLayers);
            }

            entry = BuildEntry(hash, pngPath, ReadActiveHash());
            if (entry == null)
            {
                error = "Cloud skin file could not be read after download.";
                return false;
            }

            if (setActive)
                SetActive(hash);
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private static void PruneForCloudSkinImport(string incomingHash)
    {
        if (File.Exists(GetPngPath(incomingHash)))
            return;

        var skins = ListSkins();
        if (skins.Count < MaxLocalSkins)
            return;

        var removable = skins
            .Where(entry => !entry.IsActive)
            .OrderBy(entry => entry.UpdatedUtc)
            .FirstOrDefault();
        if (removable == null)
            return;

        Delete(removable.Hash, out _);
    }

    public static bool TryReadActiveSkinBytes(out string hash, out byte[] pngBytes)
    {
        hash = ReadActiveHash();
        pngBytes = Array.Empty<byte>();
        if (string.IsNullOrWhiteSpace(hash))
            return false;

        try
        {
            var path = GetPngPath(hash);
            if (!File.Exists(path))
                return false;

            pngBytes = File.ReadAllBytes(path);
            return pngBytes.Length > 0 && pngBytes.Length <= MaxPngBytes && string.Equals(ComputeSha256Hex(pngBytes), hash, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            pngBytes = Array.Empty<byte>();
            return false;
        }
    }

    public static bool TryWriteRemoteSkin(string hash, byte[] pngBytes, out string path, out string? error)
    {
        path = string.Empty;
        error = null;
        hash = (hash ?? string.Empty).Trim().ToLowerInvariant();
        if (!IsValidHash(hash))
        {
            error = "Invalid skin hash.";
            return false;
        }

        if (pngBytes == null || pngBytes.Length == 0 || pngBytes.Length > MaxPngBytes)
        {
            error = $"Remote skin payload must be between 1 byte and {MaxPngBytes / 1024} KB.";
            return false;
        }

        if (!string.Equals(ComputeSha256Hex(pngBytes), hash, StringComparison.OrdinalIgnoreCase))
        {
            error = "Remote skin hash did not match payload.";
            return false;
        }

        try
        {
            using var bitmap = new Bitmap(new MemoryStream(pngBytes));
            if (bitmap.Width != SkinSize || bitmap.Height != SkinSize)
            {
                error = $"Remote skin must be exactly {SkinSize}x{SkinSize} pixels.";
                return false;
            }

            Directory.CreateDirectory(Paths.RuntimeRemoteSkinsDir);
            path = Path.Combine(Paths.RuntimeRemoteSkinsDir, $"{hash}.png");
            File.WriteAllBytes(path, pngBytes);
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    public static bool SetActive(string hash)
    {
        hash = (hash ?? string.Empty).Trim().ToLowerInvariant();
        if (!IsValidHash(hash) || !File.Exists(GetPngPath(hash)))
            return false;

        EnsureDirectory();
        File.WriteAllText(ActiveHashPath, hash);
        WriteActiveSignal(hash);
        return true;
    }

    public static bool ClearActive()
    {
        try
        {
            if (File.Exists(ActiveHashPath))
                File.Delete(ActiveHashPath);
            WriteActiveSignal(string.Empty);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static bool Delete(string hash, out string? error)
    {
        error = null;
        hash = (hash ?? string.Empty).Trim().ToLowerInvariant();
        if (!IsValidHash(hash))
        {
            error = "Invalid skin hash.";
            return false;
        }

        try
        {
            var wasActive = string.Equals(ReadActiveHash(), hash, StringComparison.OrdinalIgnoreCase);
            if (wasActive)
                ClearActive();

            var pngPath = GetPngPath(hash);
            var lvskinPath = GetLvskinPath(hash);
            if (File.Exists(pngPath))
                File.Delete(pngPath);
            if (File.Exists(lvskinPath))
                File.Delete(lvskinPath);

            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    public static string GetPngPath(string hash) => Path.Combine(SkinsDir, $"{hash}.png");

    public static string GetLvskinPath(string hash) => Path.Combine(SkinsDir, $"{hash}.lvskin");

    public static bool IsValidHash(string? hash)
    {
        if (string.IsNullOrWhiteSpace(hash) || hash.Length != 64)
            return false;

        for (var i = 0; i < hash.Length; i++)
        {
            var c = hash[i];
            if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F')))
                return false;
        }

        return true;
    }

    private static SkinLibraryEntry? BuildEntry(string? hash, string pngPath, string activeHash)
    {
        hash = (hash ?? string.Empty).Trim().ToLowerInvariant();
        if (!IsValidHash(hash) || !File.Exists(pngPath))
            return null;

        return new SkinLibraryEntry
        {
            Hash = hash,
            DisplayName = ReadDisplayName(hash),
            HasLayers = ReadHasLayers(hash),
            PngPath = pngPath,
            LvskinPath = GetLvskinPath(hash),
            UpdatedUtc = File.GetLastWriteTimeUtc(pngPath),
            IsActive = string.Equals(hash, activeHash, StringComparison.OrdinalIgnoreCase)
        };
    }

    private static void WriteLvskin(string path, string hash, byte[] pngBytes, string displayName, string skinFormat, bool hasLayers)
    {
        var payload = new LvskinPayload
        {
            Format = "lvskin",
            Version = 1,
            SkinFormat = skinFormat,
            Sha256 = hash,
            DisplayName = displayName,
            SourceFileName = displayName,
            Width = SkinSize,
            Height = SkinSize,
            HasLayers = hasLayers,
            Mime = "image/png",
            UpdatedAtUtc = DateTime.UtcNow,
            PngBase64 = Convert.ToBase64String(pngBytes)
        };

        File.WriteAllText(path, JsonSerializer.Serialize(payload, JsonOptions));
    }

    private static bool ReadHasLayers(string hash)
    {
        try
        {
            var path = GetLvskinPath(hash);
            if (File.Exists(path))
            {
                var payload = JsonSerializer.Deserialize<LvskinPayload>(File.ReadAllText(path));
                if (payload != null)
                    return payload.HasLayers;
            }

            var pngPath = GetPngPath(hash);
            if (!File.Exists(pngPath))
                return false;

            using var bitmap = new Bitmap(pngPath);
            return bitmap.Width == SkinSize && bitmap.Height == SkinSize && HasVisibleSecondLayer(bitmap);
        }
        catch
        {
            return false;
        }
    }

    private static bool HasVisibleSecondLayer(Bitmap bitmap)
    {
        var regions = new[]
        {
            new Rectangle(32, 0, 32, 16),
            new Rectangle(16, 32, 40, 16),
            new Rectangle(0, 48, 64, 16)
        };

        foreach (var region in regions)
        {
            for (var y = region.Top; y < region.Bottom; y++)
            for (var x = region.Left; x < region.Right; x++)
            {
                if ((uint)x >= bitmap.Width || (uint)y >= bitmap.Height)
                    continue;

                if (bitmap.GetPixel(x, y).A > 8)
                    return true;
            }
        }

        return false;
    }

    private static string ReadDisplayName(string hash)
    {
        try
        {
            var path = GetLvskinPath(hash);
            if (!File.Exists(path))
                return FallbackDisplayName(hash);

            var payload = JsonSerializer.Deserialize<LvskinPayload>(File.ReadAllText(path));
            var raw = !string.IsNullOrWhiteSpace(payload?.DisplayName)
                ? payload.DisplayName
                : payload?.SourceFileName;
            return NormalizeDisplayName(raw, hash);
        }
        catch
        {
            return FallbackDisplayName(hash);
        }
    }

    private static string NormalizeDisplayName(string? value, string hash)
    {
        value = Path.GetFileNameWithoutExtension((value ?? string.Empty).Trim());
        if (string.IsNullOrWhiteSpace(value))
            return FallbackDisplayName(hash);

        var invalid = Path.GetInvalidFileNameChars();
        var chars = value
            .Where(c => !char.IsControl(c) && !invalid.Contains(c))
            .ToArray();
        value = new string(chars).Trim();
        if (string.IsNullOrWhiteSpace(value))
            return FallbackDisplayName(hash);

        return value.Length <= 32 ? value : value.Substring(0, 32);
    }

    private static string FallbackDisplayName(string hash)
    {
        return IsValidHash(hash) ? hash.Substring(0, 12).ToUpperInvariant() : "CUSTOM SKIN";
    }

    public static string ComputeSha256Hex(byte[] bytes)
    {
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static void WriteActiveSignal(string hash)
    {
        try
        {
            EnsureDirectory();
            var payload = new ActiveSkinSignal
            {
                Format = "lvskin-active",
                Version = 1,
                Sha256 = hash ?? string.Empty,
                UpdatedAtUtc = DateTime.UtcNow
            };
            File.WriteAllText(ActiveSignalPath, JsonSerializer.Serialize(payload, JsonOptions));
        }
        catch
        {
            // The active.txt pointer is authoritative; this signal is best-effort for live systems.
        }
    }

    private static void EnsureDirectory()
    {
        Directory.CreateDirectory(SkinsDir);
    }

    private sealed class LvskinPayload
    {
        public string Format { get; set; } = "lvskin";
        public int Version { get; set; }
        public string SkinFormat { get; set; } = "skin-64x64";
        public string Sha256 { get; set; } = string.Empty;
        public string DisplayName { get; set; } = string.Empty;
        public string SourceFileName { get; set; } = string.Empty;
        public int Width { get; set; }
        public int Height { get; set; }
        public bool HasLayers { get; set; }
        public string Mime { get; set; } = "image/png";
        public DateTime UpdatedAtUtc { get; set; }
        public string PngBase64 { get; set; } = string.Empty;
    }

    private sealed class ActiveSkinSignal
    {
        public string Format { get; set; } = "lvskin-active";
        public int Version { get; set; }
        public string Sha256 { get; set; } = string.Empty;
        public DateTime UpdatedAtUtc { get; set; }
    }
}
