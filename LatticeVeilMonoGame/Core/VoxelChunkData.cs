using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;

namespace LatticeVeilMonoGame.Core;

public sealed class VoxelChunkData
{
    public const int ChunkSizeX = 16;
    public const int ChunkSizeY = 16;
    public const int ChunkSizeZ = 16;
    public const int BlockCount = ChunkSizeX * ChunkSizeY * ChunkSizeZ;
    public const int Volume = BlockCount;
    public const int FormatVersion = 3;

    public ChunkCoord Coord { get; }
    public byte[] Blocks { get; }
    public bool IsDirty { get; set; }
    public bool NeedsSave { get; set; }

    public VoxelChunkData(ChunkCoord coord)
    {
        Coord = coord;
        Blocks = new byte[BlockCount];
    }

    public byte GetLocal(int x, int y, int z)
    {
        if (x < 0 || y < 0 || z < 0 || x >= ChunkSizeX || y >= ChunkSizeY || z >= ChunkSizeZ)
            return BlockIds.Air;
        return Blocks[GetIndex(x, y, z)];
    }

    public void SetLocal(int x, int y, int z, byte id)
    {
        SetLocal(x, y, z, id, markDirty: true);
    }

    public void SetLocal(int x, int y, int z, byte id, bool markDirty)
    {
        if (x < 0 || y < 0 || z < 0 || x >= ChunkSizeX || y >= ChunkSizeY || z >= ChunkSizeZ)
            return;
        var idx = GetIndex(x, y, z);
        if (Blocks[idx] == id)
            return;
        Blocks[idx] = id;
        if (markDirty)
        {
            IsDirty = true;
            NeedsSave = true;
        }
    }

    public void Save(string path)
    {
        SaveSnapshot(path, Coord, Blocks);
        IsDirty = false;
        NeedsSave = false;
    }

    public static void SaveSnapshot(string path, ChunkCoord coord, byte[] blocks)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");

        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        using var bw = new BinaryWriter(fs);

        bw.Write(FormatVersion);
        bw.Write(ChunkSizeX);
        bw.Write(ChunkSizeY);
        bw.Write(ChunkSizeZ);
        bw.Write(coord.X);
        bw.Write(coord.Y);
        bw.Write(coord.Z);

        var palette = new List<byte>();
        var paletteIndex = new Dictionary<byte, byte>();
        var indices = new byte[blocks.Length];

        for (var i = 0; i < blocks.Length; i++)
        {
            var id = blocks[i];
            if (!paletteIndex.TryGetValue(id, out var idx))
            {
                idx = (byte)palette.Count;
                paletteIndex[id] = idx;
                palette.Add(id);
            }
            indices[i] = idx;
        }

        bw.Write((byte)palette.Count);
        bw.Write(palette.ToArray());

        using var ms = new MemoryStream();
        using (var ds = new DeflateStream(ms, CompressionLevel.Fastest, leaveOpen: true))
        using (var rle = new BinaryWriter(ds))
        {
            var i = 0;
            while (i < indices.Length)
            {
                var value = indices[i];
                var run = 1;
                while (i + run < indices.Length && indices[i + run] == value && run < ushort.MaxValue)
                    run++;
                rle.Write(value);
                rle.Write((ushort)run);
                i += run;
            }
        }

        var payload = ms.ToArray();
        bw.Write(payload.Length);
        bw.Write(payload);
    }

    public static VoxelChunkData? Load(string path, Logger log, ChunkCoord? fallbackCoord = null)
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
            int sizeX;
            int sizeY;
            int sizeZ;
            ChunkCoord coord;

            if (version == FormatVersion)
            {
                sizeX = br.ReadInt32();
                sizeY = br.ReadInt32();
                sizeZ = br.ReadInt32();
                coord = new ChunkCoord(br.ReadInt32(), br.ReadInt32(), br.ReadInt32());
                if (!TryReadCompressed(br, sizeX, sizeY, sizeZ, coord, out var loadedChunk))
                {
                    log.Warn($"Chunk load failed (compressed data) in {path}");
                    return null;
                }
                loadedChunk.IsDirty = false;
                loadedChunk.NeedsSave = false;
                return loadedChunk;
            }
            else if (version == 2 || version == 1)
            {
                sizeX = br.ReadInt32();
                sizeY = br.ReadInt32();
                sizeZ = br.ReadInt32();
                coord = fallbackCoord ?? new ChunkCoord(0, 0, 0);
            }
            else
            {
                log.Warn($"Unsupported chunk version {version} in {path}");
                return null;
            }

            var length = br.ReadInt32();
            if (sizeX <= 0 || sizeY <= 0 || sizeZ <= 0 || length <= 0)
            {
                log.Warn($"Invalid chunk header in {path}");
                return null;
            }

            var expected = sizeX * sizeY * sizeZ;
            if (length != expected)
                log.Warn($"Chunk length mismatch ({length} != {expected}) in {path}");

            var data = br.ReadBytes(length);
            if (data.Length != length)
                log.Warn($"Chunk read incomplete ({data.Length}/{length}) in {path}");

            if (sizeX != ChunkSizeX || sizeY != ChunkSizeY || sizeZ != ChunkSizeZ)
                log.Warn($"Chunk size {sizeX}x{sizeY}x{sizeZ} differs from expected {ChunkSizeX}x{ChunkSizeY}x{ChunkSizeZ} in {path}");

            var chunk = new VoxelChunkData(coord);
            var copyX = Math.Min(sizeX, ChunkSizeX);
            var copyY = Math.Min(sizeY, ChunkSizeY);
            var copyZ = Math.Min(sizeZ, ChunkSizeZ);

            for (var y = 0; y < copyY; y++)
            {
                for (var z = 0; z < copyZ; z++)
                {
                    for (var x = 0; x < copyX; x++)
                    {
                        var srcIndex = (y * sizeZ + z) * sizeX + x;
                        if (srcIndex < data.Length)
                            chunk.Blocks[GetIndex(x, y, z)] = data[srcIndex];
                    }
                }
            }

            chunk.IsDirty = false;
            chunk.NeedsSave = false;
            return chunk;
        }
        catch (Exception ex)
        {
            log.Warn($"Chunk load failed: {ex.Message}");
            return null;
        }
    }

    private static bool TryReadCompressed(BinaryReader br, int sizeX, int sizeY, int sizeZ, ChunkCoord coord, out VoxelChunkData chunk)
    {
        chunk = new VoxelChunkData(coord);
        var paletteCount = br.ReadByte();
        if (paletteCount == 0)
            return false;

        var palette = br.ReadBytes(paletteCount);
        var payloadLen = br.ReadInt32();
        if (payloadLen <= 0)
            return false;

        var payload = br.ReadBytes(payloadLen);
        var expected = sizeX * sizeY * sizeZ;
        var indices = new byte[expected];

        using var ms = new MemoryStream(payload);
        using var ds = new DeflateStream(ms, CompressionMode.Decompress);
        using var rle = new BinaryReader(ds);

        var i = 0;
        try
        {
            while (i < expected)
            {
                var value = rle.ReadByte();
                var run = rle.ReadUInt16();
                for (var r = 0; r < run && i < expected; r++)
                    indices[i++] = value;
            }
        }
        catch (EndOfStreamException)
        {
            return false;
        }

        if (i != expected)
            return false;

        var copyX = Math.Min(sizeX, ChunkSizeX);
        var copyY = Math.Min(sizeY, ChunkSizeY);
        var copyZ = Math.Min(sizeZ, ChunkSizeZ);

        for (var y = 0; y < copyY; y++)
        {
            for (var z = 0; z < copyZ; z++)
            {
                for (var x = 0; x < copyX; x++)
                {
                    var srcIndex = (y * sizeZ + z) * sizeX + x;
                    var paletteIndexValue = indices[srcIndex];
                    var id = paletteIndexValue < palette.Length ? palette[paletteIndexValue] : BlockIds.Air;
                    chunk.Blocks[GetIndex(x, y, z)] = id;
                }
            }
        }

        return true;
    }

    private static int GetIndex(int x, int y, int z) => (y * ChunkSizeZ + z) * ChunkSizeX + x;
}
