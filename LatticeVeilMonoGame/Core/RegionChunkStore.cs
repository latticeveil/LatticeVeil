using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace LatticeVeilMonoGame.Core;

/// <summary>
/// Region-based chunk storage using .lvregion files.
/// Stores 32x32 chunks per region file (chunk Y is currently expected to be 0).
///
/// File layout (little-endian):
///   [8]  magic "LVREGION"
///   [4]  int32 version
///   [4]  int32 regionX
///   [4]  int32 regionZ
///   [4]  int32 regionSizeChunks (32)
///   [8]  int64 indexOffset
///   ...  index table: 32*32 entries of (int32 offset, int32 length, int32 flags, uint32 crc32)
///   ...  chunk payloads appended; payload format is currently raw VoxelChunkData.Blocks (65536 bytes)
///
/// Index entry offset==0 means empty.
/// </summary>
public sealed class RegionChunkStore : IChunkStore, IDisposable
{
    private const int RegionSizeChunks = 32;
    private static readonly byte[] MagicBytes = Encoding.ASCII.GetBytes("LVREGION"); // 8 bytes
    private const int RegionVersion = 2; // Updated version for CRC support

    private const int HeaderSize = 8 /*magic*/ + 4 /*ver*/ + 4 /*rx*/ + 4 /*rz*/ + 4 /*size*/ + 8 /*indexOffset*/;
    private const int IndexEntrySize = 4 + 4 + 4 + 4; // offset, length, flags, crc32
    private const int IndexEntryCount = RegionSizeChunks * RegionSizeChunks; // 1024
    private const int IndexTableSize = IndexEntryCount * IndexEntrySize;

    private readonly string _worldPath;
    private readonly bool _enableCompression;
    private bool _disposed;

    private readonly object _gate = new();
    private readonly Dictionary<(int rx, int rz), RegionFile> _openRegions = new();

    private sealed class RegionFile
    {
        public required string FilePath { get; init; }
        public required FileStream Stream { get; init; }
        public required BinaryReader Reader { get; init; }
        public required BinaryWriter Writer { get; init; }
        public required long IndexOffset { get; init; }
        public readonly object Sync = new();

        public Dictionary<(int x, int z), ChunkIndexEntry> Index { get; } = new();
    }

    private readonly struct ChunkIndexEntry
    {
        public ChunkIndexEntry(int offset, int length, int flags, uint crc32)
        {
            Offset = offset;
            Length = length;
            Flags = flags;
            Crc32 = crc32;
        }
        public int Offset { get; }
        public int Length { get; }
        public int Flags { get; }
        public uint Crc32 { get; }
    }

    public RegionChunkStore(string worldPath, bool enableCompression = false)
    {
        _worldPath = worldPath;
        _enableCompression = enableCompression;
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        lock (_gate)
        {
            foreach (var regionFile in _openRegions.Values)
            {
                try
                {
                    lock (regionFile.Sync)
                    {
                        regionFile.Writer?.Dispose();
                        regionFile.Reader?.Dispose();
                        regionFile.Stream?.Dispose();
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error disposing region file {regionFile.FilePath}: {ex.Message}");
                }
            }
            _openRegions.Clear();
        }

        _disposed = true;
    }

    public bool TryLoadChunk(ChunkCoord coord, out VoxelChunkData data)
    {
        data = null;

        var (regionX, regionZ) = WorldToRegionCoords(coord.X, coord.Z);
        var (localX, localZ) = WorldToLocalCoords(coord.X, coord.Z);

        RegionFile regionFile;
        lock (_gate)
        {
            if (!_openRegions.TryGetValue((regionX, regionZ), out regionFile))
            {
                if (!TryOpenRegion(regionX, regionZ, out regionFile))
                    return false;

                _openRegions[(regionX, regionZ)] = regionFile;
            }
        }

        return TryReadChunkFromRegion(regionFile, coord, localX, localZ, out data);
    }

    public void SaveChunk(ChunkCoord coord, VoxelChunkData data)
    {
        var (regionX, regionZ) = WorldToRegionCoords(coord.X, coord.Z);
        var (localX, localZ) = WorldToLocalCoords(coord.X, coord.Z);

        RegionFile regionFile;
        lock (_gate)
        {
            if (!_openRegions.TryGetValue((regionX, regionZ), out regionFile))
            {
                if (!TryOpenRegion(regionX, regionZ, out regionFile))
                    throw new InvalidOperationException($"Failed to open region file for region ({regionX}, {regionZ})");

                _openRegions[(regionX, regionZ)] = regionFile;
            }
        }

        WriteChunkToRegion(regionFile, localX, localZ, data);
    }

    public void DeleteChunk(ChunkCoord coord)
    {
        var (regionX, regionZ) = WorldToRegionCoords(coord.X, coord.Z);
        var (localX, localZ) = WorldToLocalCoords(coord.X, coord.Z);

        RegionFile regionFile;
        lock (_gate)
        {
            if (!_openRegions.TryGetValue((regionX, regionZ), out regionFile))
            {
                if (!TryOpenRegion(regionX, regionZ, out regionFile))
                    return;

                _openRegions[(regionX, regionZ)] = regionFile;
            }
        }

        lock (regionFile.Sync)
        {
            var key = (localX, localZ);
            if (!regionFile.Index.ContainsKey(key))
                return;

            regionFile.Index.Remove(key);
            WriteIndexEntry(regionFile, localX, localZ, offset: 0, length: 0, flags: 0);
            regionFile.Writer.Flush();
            regionFile.Stream.Flush(flushToDisk: true);
        }
    }

    public void Flush()
    {
        lock (_gate)
        {
            foreach (var regionFile in _openRegions.Values)
            {
                lock (regionFile.Sync)
                {
                    try
                    {
                        regionFile.Writer.Flush();
                        regionFile.Stream.Flush(flushToDisk: true);
                    }
                    catch
                    {
                        // ignored
                    }
                }
            }
        }
    }

    // -------------------------
    // Region helpers
    // -------------------------

    private static (int rx, int rz) WorldToRegionCoords(int worldX, int worldZ)
    {
        var rx = (int)Math.Floor(worldX / (double)RegionSizeChunks);
        var rz = (int)Math.Floor(worldZ / (double)RegionSizeChunks);
        return (rx, rz);
    }

    private static (int x, int z) WorldToLocalCoords(int worldX, int worldZ)
    {
        var rx = (int)Math.Floor(worldX / (double)RegionSizeChunks);
        var rz = (int)Math.Floor(worldZ / (double)RegionSizeChunks);
        var localX = worldX - (rx * RegionSizeChunks);
        var localZ = worldZ - (rz * RegionSizeChunks);
        return (localX, localZ);
    }

    private string GetRegionsDir()
    {
        var lower = Path.Combine(_worldPath, FileConventions.RegionsDirName);
        if (Directory.Exists(lower)) return lower;

        var upper = Path.Combine(_worldPath, "Regions");
        if (Directory.Exists(upper)) return upper;

        return lower; // will be created
    }

    private bool TryOpenRegion(int regionX, int regionZ, out RegionFile regionFile)
    {
        var regionsDir = GetRegionsDir();
        var regionFileName = $"r.{regionX}.{regionZ}{FileConventions.RegionExtension}";
        var regionFilePath = Path.Combine(regionsDir, regionFileName);

        // If the previous world screen hasn't fully disposed yet, Windows can keep the file locked briefly.
        // We prefer to WAIT (correctness) rather than fall back to "empty region" behavior (missing blocks on first rejoin).
        const int maxAttempts = 20;
        const int sleepMs = 50;

        for (var attempt = 0; attempt < maxAttempts; attempt++)
        {
            FileStream stream = null;
            BinaryReader reader = null;
            BinaryWriter writer = null;

            try
            {
                Directory.CreateDirectory(regionsDir);

                if (!File.Exists(regionFilePath))
                    CreateNewRegionFile(regionFilePath, regionX, regionZ);

                stream = new FileStream(regionFilePath, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
                reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
                writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);

                var (indexOffset, index) = ReadRegionIndex(stream, reader);

                regionFile = new RegionFile
                {
                    FilePath = regionFilePath,
                    Stream = stream,
                    Reader = reader,
                    Writer = writer,
                    IndexOffset = indexOffset,
                };

                foreach (var kv in index)
                    regionFile.Index[kv.Key] = kv.Value;

                return true;
            }
            catch (IOException)
            {
                // Dispose partially created resources before retrying.
                try { writer?.Dispose(); } catch { }
                try { reader?.Dispose(); } catch { }
                try { stream?.Dispose(); } catch { }

                if (attempt < maxAttempts - 1)
                {
                    Thread.Sleep(sleepMs);
                    continue;
                }

                break;
            }
            catch
            {
                try { writer?.Dispose(); } catch { }
                try { reader?.Dispose(); } catch { }
                try { stream?.Dispose(); } catch { }
                break;
            }
        }

        regionFile = null;
        return false;
    }

    public static void EnsureRegionExists(string worldPath, int regionX, int regionZ)
    {
        var regionsDir = Path.Combine(worldPath, FileConventions.RegionsDirName);
        var regionFileName = $"r.{regionX}.{regionZ}{FileConventions.RegionExtension}";
        var regionFilePath = Path.Combine(regionsDir, regionFileName);

        Directory.CreateDirectory(regionsDir);
        if (!File.Exists(regionFilePath))
            CreateNewRegionFile(regionFilePath, regionX, regionZ);
    }

    private static void CreateNewRegionFile(string regionFilePath, int regionX, int regionZ)
    {
        var dir = Path.GetDirectoryName(regionFilePath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        using var stream = new FileStream(regionFilePath, FileMode.Create, FileAccess.Write, FileShare.None);
        using var writer = new BinaryWriter(stream, Encoding.UTF8);

        // Header
        writer.Write(MagicBytes);
        writer.Write(RegionVersion);
        writer.Write(regionX);
        writer.Write(regionZ);
        writer.Write(RegionSizeChunks);

        var indexOffset = (long)HeaderSize;
        writer.Write(indexOffset);

        // Index table - v2 format includes CRC32
        for (int i = 0; i < IndexEntryCount; i++)
        {
            writer.Write(0); // offset
            writer.Write(0); // length
            writer.Write(0); // flags
            writer.Write(0u); // crc32
        }

        writer.Flush();
        stream.Flush(flushToDisk: true);
    }

    private static (long indexOffset, Dictionary<(int x, int z), ChunkIndexEntry> index) ReadRegionIndex(FileStream stream, BinaryReader reader)
    {
        stream.Seek(0, SeekOrigin.Begin);

        var magic = reader.ReadBytes(8);
        if (magic.Length != 8 || magic[0] != MagicBytes[0])
        {
            // Fallback: treat as empty if unreadable
            return (HeaderSize, new Dictionary<(int x, int z), ChunkIndexEntry>());
        }

        // Strict magic compare
        for (int i = 0; i < 8; i++)
        {
            if (magic[i] != MagicBytes[i])
                return (HeaderSize, new Dictionary<(int x, int z), ChunkIndexEntry>());
        }

        var ver = reader.ReadInt32();
        _ = reader.ReadInt32(); // rx
        _ = reader.ReadInt32(); // rz
        var size = reader.ReadInt32();
        var indexOffset = reader.ReadInt64();

        if (ver != RegionVersion || size != RegionSizeChunks)
        {
            // Unsupported region format version/size
            return (HeaderSize, new Dictionary<(int x, int z), ChunkIndexEntry>());
        }

        if (indexOffset < HeaderSize || indexOffset > stream.Length)
            indexOffset = HeaderSize;

        var index = new Dictionary<(int x, int z), ChunkIndexEntry>();

        stream.Seek(indexOffset, SeekOrigin.Begin);
        for (int i = 0; i < IndexEntryCount; i++)
        {
            var off = reader.ReadInt32();
            var len = reader.ReadInt32();
            var flags = reader.ReadInt32();
            var crc32 = ver >= 2 ? reader.ReadUInt32() : 0u; // CRC only in v2+

            if (off <= 0 || len <= 0)
                continue;

            var localX = i % RegionSizeChunks;
            var localZ = i / RegionSizeChunks;
            index[(localX, localZ)] = new ChunkIndexEntry(off, len, flags, crc32);
        }

        return (indexOffset, index);
    }

    // -------------------------
    // Chunk IO
    // -------------------------

    private static bool TryReadChunkFromRegion(RegionFile regionFile, ChunkCoord coord, int localX, int localZ, out VoxelChunkData data)
    {
        data = null;

        lock (regionFile.Sync)
        {
            if (!regionFile.Index.TryGetValue((localX, localZ), out var entry) || entry.Offset <= 0 || entry.Length <= 0)
                return false;

            if (entry.Offset + entry.Length > regionFile.Stream.Length)
            {
                Console.WriteLine($"Chunk {coord} entry out of bounds: offset={entry.Offset}, length={entry.Length}, fileLength={regionFile.Stream.Length}");
                return false;
            }

            regionFile.Stream.Seek(entry.Offset, SeekOrigin.Begin);

            var payload = new byte[entry.Length];
            if (!ReadExactly(regionFile.Stream, payload))
            {
                Console.WriteLine($"Failed to read complete chunk data for {coord}: expected {entry.Length} bytes");
                return false;
            }

            byte[] bytes = payload;
            if ((entry.Flags & 1) != 0 && !TryDecompressPayload(payload, VoxelChunkData.Volume, out bytes))
            {
                Console.WriteLine($"Failed to decompress chunk payload for {coord}");
                return false;
            }

            // Current payload is raw blocks
            if (bytes.Length != VoxelChunkData.Volume)
            {
                Console.WriteLine($"Invalid chunk payload size for {coord}: expected {VoxelChunkData.Volume}, got {bytes.Length}");
                return false;
            }

            // Validate CRC32 if available (v2+ format)
            if (entry.Crc32 != 0)
            {
                var calculatedCrc = CalculateCrc32(bytes);
                if (calculatedCrc != entry.Crc32)
                {
                    Console.WriteLine($"CRC32 mismatch for chunk {coord}: expected {entry.Crc32:X8}, calculated {calculatedCrc:X8}");
                    return false;
                }
            }

            var chunk = new VoxelChunkData(coord);
            chunk.Load(bytes);
            data = chunk;
            return true;
        }
    }

    private void WriteChunkToRegion(RegionFile regionFile, int localX, int localZ, VoxelChunkData data)
    {
        // Current payload is raw blocks.
        var chunkBytes = data.Blocks;
        if (chunkBytes.Length != VoxelChunkData.Volume)
            throw new InvalidOperationException($"Unexpected chunk payload size: {chunkBytes.Length} (expected {VoxelChunkData.Volume})");

        var payload = chunkBytes;
        var flags = 0;
        if (_enableCompression)
        {
            var compressed = CompressPayload(chunkBytes);
            if (compressed.Length > 0 && compressed.Length < chunkBytes.Length)
            {
                payload = compressed;
                flags |= 1;
            }
        }

        lock (regionFile.Sync)
        {
            regionFile.Stream.Seek(0, SeekOrigin.End);
            var offsetLong = regionFile.Stream.Position;
            if (offsetLong > int.MaxValue)
                throw new IOException("Region file exceeded 2GB (int32 offsets). Implement 64-bit offsets before continuing.");

            // Write chunk data first
            regionFile.Writer.Write(payload);
            regionFile.Writer.Flush();

            var offset = (int)offsetLong;
            var length = payload.Length;

            // Calculate CRC32 for the chunk data
            var crc32 = CalculateCrc32(chunkBytes);
            regionFile.Index[(localX, localZ)] = new ChunkIndexEntry(offset, length, flags, crc32);
            
            // Update index entry with CRC
            WriteIndexEntry(regionFile, localX, localZ, offset, length, flags);
            UpdateIndexEntryCrc(regionFile, localX, localZ, crc32);

            regionFile.Stream.Flush(flushToDisk: true);

            // Mark chunk clean once persisted
            data.MarkClean();
        }
    }

    private static void WriteIndexEntry(RegionFile regionFile, int localX, int localZ, int offset, int length, int flags)
    {
        var entryIndex = (localZ * RegionSizeChunks) + localX;
        var pos = regionFile.IndexOffset + (entryIndex * IndexEntrySize);

        regionFile.Stream.Seek(pos, SeekOrigin.Begin);
        regionFile.Writer.Write(offset);
        regionFile.Writer.Write(length);
        regionFile.Writer.Write(flags);
        regionFile.Writer.Write(0u); // CRC placeholder - will be updated after write
    }

    private static uint CalculateCrc32(byte[] data)
    {
        const uint polynomial = 0xEDB88320;
        uint crc = 0xFFFFFFFF;
        
        for (int i = 0; i < data.Length; i++)
        {
            crc ^= data[i];
            for (int j = 0; j < 8; j++)
            {
                crc = (crc & 1) != 0 ? (crc >> 1) ^ polynomial : crc >> 1;
            }
        }
        
        return crc ^ 0xFFFFFFFF;
    }

    private static void UpdateIndexEntryCrc(RegionFile regionFile, int localX, int localZ, uint crc32)
    {
        var entryIndex = (localZ * RegionSizeChunks) + localX;
        var pos = regionFile.IndexOffset + (entryIndex * IndexEntrySize) + 12; // Offset to CRC field

        regionFile.Stream.Seek(pos, SeekOrigin.Begin);
        regionFile.Writer.Write(crc32);
        regionFile.Writer.Flush();
    }

    private static bool ReadExactly(Stream stream, byte[] buffer)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var read = stream.Read(buffer, total, buffer.Length - total);
            if (read <= 0)
                return false;
            total += read;
        }
        return true;
    }

    private static byte[] CompressPayload(byte[] bytes)
    {
        try
        {
            using var output = new MemoryStream(bytes.Length);
            using (var ds = new DeflateStream(output, CompressionLevel.Fastest, leaveOpen: true))
            {
                ds.Write(bytes, 0, bytes.Length);
            }
            return output.ToArray();
        }
        catch
        {
            return Array.Empty<byte>();
        }
    }

    private static bool TryDecompressPayload(byte[] payload, int expectedSize, out byte[] bytes)
    {
        bytes = Array.Empty<byte>();
        try
        {
            using var source = new MemoryStream(payload, writable: false);
            using var ds = new DeflateStream(source, CompressionMode.Decompress);
            using var output = new MemoryStream(expectedSize);
            ds.CopyTo(output);
            bytes = output.ToArray();
            return bytes.Length == expectedSize;
        }
        catch
        {
            return false;
        }
    }
}
