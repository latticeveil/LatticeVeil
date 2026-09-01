using System;
using System.IO;
using System.Text;

namespace LatticeVeilMonoGame.Core;

public readonly record struct BuildExportBounds(
    int MinX,
    int MinY,
    int MinZ,
    int MaxX,
    int MaxY,
    int MaxZ)
{
    public int SizeX => Math.Max(0, MaxX - MinX + 1);
    public int SizeY => Math.Max(0, MaxY - MinY + 1);
    public int SizeZ => Math.Max(0, MaxZ - MinZ + 1);
    public int Volume => SizeX * SizeY * SizeZ;
}

public sealed class BuildExportData
{
    public string BuildId { get; init; } = Guid.NewGuid().ToString("N");
    public string Name { get; init; } = "Build";
    public string SourceWorldId { get; init; } = string.Empty;
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public BuildExportBounds Bounds { get; init; }
    public byte[] Blocks { get; init; } = Array.Empty<byte>();
}

public static class BuildExportStore
{
    private const string Magic = "LVBUILD1";
    private const int MaxExportBlocks = 2_000_000;

    public static BuildExportData Capture(VoxelWorld world, BuildExportBounds bounds, string name)
    {
        if (world == null)
            throw new ArgumentNullException(nameof(world));
        ValidateBounds(bounds);

        var blocks = new byte[bounds.Volume];
        var index = 0;
        for (var y = bounds.MinY; y <= bounds.MaxY; y++)
        {
            for (var z = bounds.MinZ; z <= bounds.MaxZ; z++)
            {
                for (var x = bounds.MinX; x <= bounds.MaxX; x++)
                    blocks[index++] = world.GetBlock(x, y, z);
            }
        }

        return new BuildExportData
        {
            Name = string.IsNullOrWhiteSpace(name) ? "Build" : name.Trim(),
            SourceWorldId = world.Meta?.WorldId ?? string.Empty,
            Bounds = bounds,
            Blocks = blocks
        };
    }

    public static string Save(string worldPath, BuildExportData data, Logger? log = null)
    {
        if (string.IsNullOrWhiteSpace(worldPath))
            throw new ArgumentException("World path is required.", nameof(worldPath));
        if (data == null)
            throw new ArgumentNullException(nameof(data));
        ValidateData(data);

        var dir = FileConventions.GetBuildExportsDir(worldPath);
        Directory.CreateDirectory(dir);

        var fileName = $"{SanitizeFileName(data.Name)}-{data.BuildId[..Math.Min(8, data.BuildId.Length)]}{FileConventions.BuildExtension}";
        var path = Path.Combine(dir, fileName);

        using var stream = File.Create(path);
        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: false);
        writer.Write(Magic);
        writer.Write(data.BuildId);
        writer.Write(data.Name);
        writer.Write(data.SourceWorldId);
        writer.Write(data.CreatedAt.ToUnixTimeSeconds());
        writer.Write(data.Bounds.MinX);
        writer.Write(data.Bounds.MinY);
        writer.Write(data.Bounds.MinZ);
        writer.Write(data.Bounds.MaxX);
        writer.Write(data.Bounds.MaxY);
        writer.Write(data.Bounds.MaxZ);
        writer.Write(data.Blocks.Length);
        writer.Write(data.Blocks);

        log?.Info($"SavedBuildExport path={Paths.ToUiPath(path)} blocks={data.Blocks.Length}");
        return path;
    }

    public static bool TryLoad(string path, out BuildExportData? data, Logger? log = null)
    {
        data = null;
        try
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                return false;

            using var stream = File.OpenRead(path);
            using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: false);
            if (!string.Equals(reader.ReadString(), Magic, StringComparison.Ordinal))
                return false;

            var buildId = reader.ReadString();
            var name = reader.ReadString();
            var sourceWorldId = reader.ReadString();
            var createdAt = DateTimeOffset.FromUnixTimeSeconds(reader.ReadInt64());
            var bounds = new BuildExportBounds(
                reader.ReadInt32(),
                reader.ReadInt32(),
                reader.ReadInt32(),
                reader.ReadInt32(),
                reader.ReadInt32(),
                reader.ReadInt32());
            ValidateBounds(bounds);

            var length = reader.ReadInt32();
            if (length != bounds.Volume || length > MaxExportBlocks)
                return false;

            var blocks = reader.ReadBytes(length);
            if (blocks.Length != length)
                return false;

            data = new BuildExportData
            {
                BuildId = buildId,
                Name = name,
                SourceWorldId = sourceWorldId,
                CreatedAt = createdAt,
                Bounds = bounds,
                Blocks = blocks
            };
            return true;
        }
        catch (Exception ex)
        {
            log?.Warn($"Failed to load build export {Paths.ToUiPath(path)}: {ex.Message}");
            data = null;
            return false;
        }
    }

    private static void ValidateData(BuildExportData data)
    {
        ValidateBounds(data.Bounds);
        if (data.Blocks.Length != data.Bounds.Volume)
            throw new InvalidDataException("Build export block count does not match its bounds.");
    }

    private static void ValidateBounds(BuildExportBounds bounds)
    {
        if (bounds.MinX > bounds.MaxX || bounds.MinY > bounds.MaxY || bounds.MinZ > bounds.MaxZ)
            throw new ArgumentOutOfRangeException(nameof(bounds), "Build export bounds are inverted.");
        if (bounds.Volume <= 0 || bounds.Volume > MaxExportBlocks)
            throw new ArgumentOutOfRangeException(nameof(bounds), $"Build export must contain 1 to {MaxExportBlocks} blocks.");
    }

    private static string SanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var builder = new StringBuilder(name.Length);
        foreach (var ch in name)
            builder.Append(Array.IndexOf(invalid, ch) >= 0 ? '_' : ch);

        var sanitized = builder.ToString().Trim();
        return sanitized.Length == 0 ? "Build" : sanitized;
    }
}
