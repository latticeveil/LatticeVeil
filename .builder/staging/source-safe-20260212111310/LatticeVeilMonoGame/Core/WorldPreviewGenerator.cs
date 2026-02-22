using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using DrawingColor = System.Drawing.Color;

namespace LatticeVeilMonoGame.Core;

public static class WorldPreviewGenerator
{
    public const string PreviewFileName = "preview.png";
    private const int DefaultPreviewSize = 96;
    private const int MinPreviewSize = 32;
    private const int MaxPreviewSize = 256;
    private const int SampleRadiusBlocks = 256;

    public static string GetPreviewPath(string worldPath)
    {
        return Path.Combine(worldPath, PreviewFileName);
    }

    public static void GenerateAndSave(WorldMeta meta, string worldPath, Logger log, int size = DefaultPreviewSize)
    {
        var (centerX, centerZ) = GetDefaultCenter(meta);
        GenerateAndSave(meta, worldPath, centerX, centerZ, log, size);
    }

    public static void GenerateAndSave(WorldMeta meta, string worldPath, int centerX, int centerZ, Logger log, int size = DefaultPreviewSize)
    {
        if (meta == null || string.IsNullOrWhiteSpace(worldPath))
            return;

        size = Math.Clamp(size, MinPreviewSize, MaxPreviewSize);
        try
        {
            Directory.CreateDirectory(worldPath);
            var previewPath = GetPreviewPath(worldPath);

            using var bitmap = GdiPlusHelper.SafeCreateBitmap(size, size);
            if (bitmap == null)
            {
                log.Warn($"World preview generation skipped (bitmap allocation failed): {worldPath}");
                return;
            }

            var maxY = Math.Max(1, meta.Size?.Height ?? VoxelChunkData.ChunkSizeY);
            var chunkCache = new Dictionary<ChunkCoord, VoxelChunkData?>();
            var worldForBiomeFallback = new VoxelWorld(meta, worldPath, log);

            for (int py = 0; py < size; py++)
            {
                for (int px = 0; px < size; px++)
                {
                    var wx = SampleWorldCoordinate(centerX, px, size);
                    // Invert Y so north appears at top of preview.
                    var wz = SampleWorldCoordinate(centerZ, size - 1 - py, size);

                    DrawingColor color;
                    if (TryGetTopBlock(worldPath, wx, wz, maxY, chunkCache, out var topBlock, out var topY))
                    {
                        color = ColorForBlock(topBlock);
                        color = ApplyElevationTint(color, topY, maxY);
                    }
                    else
                    {
                        color = ColorForBiome(worldForBiomeFallback.GetBiomeNameAt(wx, wz));
                    }

                    bitmap.SetPixel(px, py, color);
                }
            }

            if (!GdiPlusHelper.SafeSaveImage(bitmap, previewPath))
            {
                log.Warn($"World preview save failed: {previewPath}");
                return;
            }

            log.Info($"World preview updated: {previewPath} ({centerX}, {centerZ})");
        }
        catch (Exception ex)
        {
            log.Warn($"World preview generation failed: {ex.Message}");
        }
    }

    private static (int x, int z) GetDefaultCenter(WorldMeta meta)
    {
        if (meta == null)
            return (0, 0);

        if (meta.HasCustomSpawn)
            return (meta.SpawnX, meta.SpawnZ);

        var width = Math.Max(64, meta.Size?.Width ?? 512);
        var depth = Math.Max(64, meta.Size?.Depth ?? 512);
        var spawnX = width / 4;
        var spawnZ = depth / 4;
        spawnX = Math.Clamp(spawnX, 16, Math.Max(16, width - 16));
        spawnZ = Math.Clamp(spawnZ, 16, Math.Max(16, depth - 16));
        return (spawnX, spawnZ);
    }

    private static int SampleWorldCoordinate(int center, int pixelIndex, int previewSize)
    {
        if (previewSize <= 1)
            return center;

        var t = ((pixelIndex + 0.5f) / previewSize - 0.5f) * 2f;
        return center + (int)MathF.Round(t * SampleRadiusBlocks);
    }

    private static bool TryGetTopBlock(
        string worldPath,
        int wx,
        int wz,
        int maxY,
        Dictionary<ChunkCoord, VoxelChunkData?> chunkCache,
        out byte blockId,
        out int blockY)
    {
        blockId = BlockIds.Air;
        blockY = 0;

        var cx = FloorDiv(wx, VoxelChunkData.ChunkSizeX);
        var lx = Mod(wx, VoxelChunkData.ChunkSizeX);
        var cz = FloorDiv(wz, VoxelChunkData.ChunkSizeZ);
        var lz = Mod(wz, VoxelChunkData.ChunkSizeZ);

        for (int wy = maxY - 1; wy >= 0; wy--)
        {
            var cy = FloorDiv(wy, VoxelChunkData.ChunkSizeY);
            var ly = Mod(wy, VoxelChunkData.ChunkSizeY);
            var coord = new ChunkCoord(cx, cy, cz);
            if (!TryGetChunk(worldPath, coord, chunkCache, out var chunk) || chunk == null)
                continue;

            var id = chunk.GetLocal(lx, ly, lz);
            if (id == BlockIds.Air)
                continue;

            blockId = id;
            blockY = wy;
            return true;
        }

        return false;
    }

    private static bool TryGetChunk(
        string worldPath,
        ChunkCoord coord,
        Dictionary<ChunkCoord, VoxelChunkData?> chunkCache,
        out VoxelChunkData? chunk)
    {
        if (chunkCache.TryGetValue(coord, out chunk))
            return chunk != null;

        var chunkPath = Path.Combine(worldPath, "chunks", $"chunk_{coord.X}_{coord.Y}_{coord.Z}.bin");
        if (!File.Exists(chunkPath))
        {
            chunkCache[coord] = null;
            chunk = null;
            return false;
        }

        try
        {
            var loaded = new VoxelChunkData(coord);
            loaded.Load(chunkPath);
            chunkCache[coord] = loaded;
            chunk = loaded;
            return true;
        }
        catch
        {
            chunkCache[coord] = null;
            chunk = null;
            return false;
        }
    }

    private static DrawingColor ColorForBiome(string biomeName)
    {
        if (string.IsNullOrWhiteSpace(biomeName))
            return DrawingColor.FromArgb(255, 95, 128, 96);

        return biomeName.Trim().ToLowerInvariant() switch
        {
            "desert" => DrawingColor.FromArgb(255, 214, 196, 136),
            "ocean" => DrawingColor.FromArgb(255, 63, 125, 188),
            _ => DrawingColor.FromArgb(255, 96, 156, 92)
        };
    }

    private static DrawingColor ColorForBlock(byte blockId)
    {
        return blockId switch
        {
            BlockIds.Grass => DrawingColor.FromArgb(255, 90, 158, 78),
            BlockIds.Dirt => DrawingColor.FromArgb(255, 126, 96, 66),
            BlockIds.Stone => DrawingColor.FromArgb(255, 122, 124, 128),
            BlockIds.Water => DrawingColor.FromArgb(255, 61, 124, 194),
            BlockIds.Sand => DrawingColor.FromArgb(255, 218, 202, 156),
            BlockIds.Gravel => DrawingColor.FromArgb(255, 136, 136, 132),
            BlockIds.Wood => DrawingColor.FromArgb(255, 118, 84, 52),
            BlockIds.Leaves => DrawingColor.FromArgb(255, 82, 136, 72),
            BlockIds.Nullblock => DrawingColor.FromArgb(255, 84, 84, 88),
            _ => DrawingColor.FromArgb(255, 142, 132, 118)
        };
    }

    private static DrawingColor ApplyElevationTint(DrawingColor baseColor, int y, int maxY)
    {
        var t = maxY <= 1 ? 0.5f : Math.Clamp(y / (float)(maxY - 1), 0f, 1f);
        var shade = 0.78f + t * 0.34f;
        return ScaleColor(baseColor, shade);
    }

    private static DrawingColor ScaleColor(DrawingColor color, float factor)
    {
        var r = (int)Math.Clamp(MathF.Round(color.R * factor), 0f, 255f);
        var g = (int)Math.Clamp(MathF.Round(color.G * factor), 0f, 255f);
        var b = (int)Math.Clamp(MathF.Round(color.B * factor), 0f, 255f);
        return DrawingColor.FromArgb(color.A, r, g, b);
    }

    private static int FloorDiv(int value, int divisor)
    {
        var q = value / divisor;
        var r = value % divisor;
        if (r != 0 && ((r > 0) != (divisor > 0)))
            q--;
        return q;
    }

    private static int Mod(int value, int divisor)
    {
        var m = value % divisor;
        if (m < 0)
            m += Math.Abs(divisor);
        return m;
    }
}
