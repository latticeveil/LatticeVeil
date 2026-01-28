using System;
using System.IO;

namespace LatticeVeilMonoGame.Core;

public sealed class VoxelChunk
{
    public int Version { get; }
    public int Width { get; }
    public int Height { get; }
    public int Depth { get; }
    public byte[] Blocks { get; }

    private VoxelChunk(int version, int width, int height, int depth, byte[] blocks)
    {
        Version = version;
        Width = width;
        Height = height;
        Depth = depth;
        Blocks = blocks;
    }

    public byte GetBlock(int x, int y, int z)
    {
        if (x < 0 || y < 0 || z < 0 || x >= Width || y >= Height || z >= Depth)
            return BlockIds.Air;

        var index = (y * Depth + z) * Width + x;
        return Blocks[index];
    }

    public static VoxelChunk? Load(string path, Logger log)
    {
        try
        {
            if (!File.Exists(path))
            {
                log.Warn($"Chunk file missing: {path}");
                return null;
            }

            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var br = new BinaryReader(fs);

            var version = br.ReadInt32();
            var width = br.ReadInt32();
            var height = br.ReadInt32();
            var depth = br.ReadInt32();
            var length = br.ReadInt32();

            if (width <= 0 || height <= 0 || depth <= 0)
            {
                log.Warn($"Invalid chunk dimensions in {path}");
                return null;
            }

            var expected = width * height * depth;
            if (length != expected)
            {
                log.Warn($"Chunk length mismatch ({length} != {expected}) in {path}");
                return null;
            }

            var blocks = br.ReadBytes(length);
            if (blocks.Length != length)
            {
                log.Warn($"Chunk read incomplete ({blocks.Length}/{length}) in {path}");
                return null;
            }

            return new VoxelChunk(version, width, height, depth, blocks);
        }
        catch (Exception ex)
        {
            log.Warn($"Chunk load failed: {ex.Message}");
            return null;
        }
    }
}
