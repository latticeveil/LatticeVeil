using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace LatticeVeilMonoGame.Core;

/// <summary>
/// Region-based chunk storage using .lvregion files
/// Stores 32x32 chunks per region file
/// </summary>
public sealed class RegionChunkStore : IChunkStore
{
    private const int RegionSizeChunks = 32;
    private const string RegionMagic = "LVREGION";
    private const int RegionVersion = 1;
    
    private readonly string _worldPath;
    private readonly Dictionary<(int rx, int rz), RegionFile> _openRegions = new();
    private readonly bool _enableCompression;
    
    /// <summary>
    /// Region file handle for managing open region files
    /// </summary>
    private sealed class RegionFile
    {
        public FileStream Stream { get; set; }
        public BinaryWriter Writer { get; set; }
        public Dictionary<(int x, int z), ChunkIndexEntry> Index { get; set; } = new();
        public string FilePath { get; set; }
    }
    
    /// <summary>
    /// Chunk index entry within a region file
    /// </summary>
    private struct ChunkIndexEntry
    {
        public int Offset { get; set; }
        public int Length { get; set; }
        public int Flags { get; set; } = 0; // bit0 = compressed
        
        public ChunkIndexEntry(int offset, int length, int flags = 0)
        {
            Offset = offset;
            Length = length;
            Flags = flags;
        }
    }
    
    public RegionChunkStore(string worldPath, bool enableCompression = false)
    {
        _worldPath = worldPath;
        _enableCompression = enableCompression;
    }
    
    public bool TryLoadChunk(ChunkCoord coord, out VoxelChunkData data)
    {
        data = null;
        
        var (regionX, regionZ) = WorldToRegionCoords(coord.X, coord.Z);
        var (localX, localZ) = WorldToLocalCoords(coord.X, coord.Z);
        
        if (!_openRegions.TryGetValue((regionX, regionZ), out var regionFile))
        {
            if (!TryOpenRegion(regionX, regionZ, out regionFile))
            {
                return false;
            }
            _openRegions[(regionX, regionZ)] = regionFile;
        }
        
        return TryReadChunkFromRegion(regionFile, localX, localZ, out data);
    }
    
    public void SaveChunk(ChunkCoord coord, VoxelChunkData data)
    {
        var (regionX, regionZ) = WorldToRegionCoords(coord.X, coord.Z);
        var (localX, localZ) = WorldToLocalCoords(coord.X, coord.Z);
        
        if (!_openRegions.TryGetValue((regionX, regionZ), out var regionFile))
        {
            if (!TryOpenRegion(regionX, regionZ, out regionFile))
            {
                throw new InvalidOperationException($"Failed to open region file for region ({regionX}, {regionZ})");
            }
            _openRegions[(regionX, regionZ)] = regionFile;
        }
        
        WriteChunkToRegion(regionFile, localX, localZ, data);
    }
    
    public void DeleteChunk(ChunkCoord coord)
    {
        var (regionX, regionZ) = WorldToRegionCoords(coord.X, coord.Z);
        var (localX, localZ) = WorldToLocalCoords(coord.X, coord.Z);
        
        if (!_openRegions.TryGetValue((regionX, regionZ), out var regionFile))
        {
            if (!TryOpenRegion(regionX, regionZ, out regionFile))
            {
                return; // Silently fail if region doesn't exist
            }
            _openRegions[(regionX, regionZ)] = regionFile;
        }
        
        // Mark chunk as deleted by setting offset=0
        var indexKey = (localX, localZ);
        if (regionFile.Index.ContainsKey(indexKey))
        {
            regionFile.Index[indexKey] = new ChunkIndexEntry { Offset = 0, Length = 0 };
        }
    }
    
    public void Flush()
    {
        foreach (var regionFile in _openRegions.Values)
        {
            try
            {
                regionFile.Writer?.Flush();
                regionFile.Stream?.Flush();
            }
            catch (Exception ex)
            {
                // Log but don't crash - individual region failures shouldn't bring down the world
                Console.WriteLine($"Failed to flush region file {regionFile.FilePath}: {ex.Message}");
            }
        }
    }
    
    /// <summary>
    /// Converts world coordinates to region coordinates
    /// </summary>
    private static (int rx, int rz) WorldToRegionCoords(int worldX, int worldZ)
    {
        var rx = (int)Math.Floor(worldX / (double)RegionSizeChunks);
        var rz = (int)Math.Floor(worldZ / (double)RegionSizeChunks);
        return (rx, rz);
    }
    
    /// <summary>
    /// Converts world coordinates to local coordinates within a region
    /// </summary>
    private static (int x, int z) WorldToLocalCoords(int worldX, int worldZ)
    {
        var rx = (int)Math.Floor(worldX / (double)RegionSizeChunks);
        var rz = (int)Math.Floor(worldZ / (double)RegionSizeChunks);
        var localX = worldX - (rx * RegionSizeChunks);
        var localZ = worldZ - (rz * RegionSizeChunks);
        return (localX, localZ);
    }
    
    /// <summary>
    /// Opens a region file for reading/writing
    /// </summary>
    private bool TryOpenRegion(int regionX, int regionZ, out RegionFile regionFile)
    {
        var regionsDir = Path.Combine(_worldPath, FileConventions.RegionsDirName);
        var regionFileName = $"r.{regionX}.{regionZ}{FileConventions.RegionExtension}";
        var regionFilePath = Path.Combine(regionsDir, regionFileName);
        
        try
        {
            Directory.CreateDirectory(regionsDir);
            
            // Create new region file if it doesn't exist
            if (!File.Exists(regionFilePath))
            {
                CreateNewRegionFile(regionFilePath);
            }
            
            var stream = new FileStream(regionFilePath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read);
            var writer = new BinaryWriter(stream, Encoding.UTF8);
            
            // Read existing index if file has data
            if (stream.Length > 0)
            {
                ReadRegionIndex(stream, writer);
            }
            
            regionFile = new RegionFile
            {
                Stream = stream,
                Writer = writer,
                FilePath = regionFilePath
            };
            
            return true;
        }
        catch (Exception)
        {
            regionFile = null;
            return false;
        }
    }
    
    /// <summary>
    /// Creates a new region file with header and empty index
    /// </summary>
    private static void CreateNewRegionFile(string regionFilePath)
    {
        using var stream = new FileStream(regionFilePath, FileMode.Create, FileAccess.Write, FileShare.None);
        using var writer = new BinaryWriter(stream, Encoding.UTF8);
        
        // Write header
        writer.Write(Encoding.UTF8.GetBytes(RegionMagic));
        writer.Write(RegionVersion);
        writer.Write(0); // RegionX
        writer.Write(0); // RegionZ
        writer.Write(RegionSizeChunks);
        
        // Write index offset (after header)
        var indexOffset = stream.Position;
        writer.Write(indexOffset);
        
        // Write empty index (1024 entries)
        for (int i = 0; i < RegionSizeChunks * RegionSizeChunks; i++)
        {
            writer.Write(0); // offset
            writer.Write(0); // length  
            writer.Write(0); // flags
        }
    }
    
    /// <summary>
    /// Reads the index section of an existing region file
    /// </summary>
    private static void ReadRegionIndex(FileStream stream, BinaryWriter writer)
    {
        // Skip header to find index offset
        stream.Seek(8 + 4 + 4 + 4 + 4, SeekOrigin.Begin); // magic + version + rx + rz + size
        
        var indexOffset = ReadInt32(stream);
        
        // Seek to index and read all entries
        stream.Seek(indexOffset, SeekOrigin.Begin);
        for (int i = 0; i < RegionSizeChunks * RegionSizeChunks; i++)
        {
            var offset = ReadInt32(stream);
            var length = ReadInt32(stream);
            var flags = ReadInt32(stream);
            
            var localX = i % RegionSizeChunks;
            var localZ = i / RegionSizeChunks;
            
            // Store in index (would need to pass this to the RegionFile instance)
            // For now, we'll rebuild the index on each open
        }
    }
    
    /// <summary>
    /// Reads chunk data from a region file
    /// </summary>
    private static bool TryReadChunkFromRegion(RegionFile regionFile, int localX, int localZ, out VoxelChunkData data)
    {
        data = null;
        
        var indexKey = (localX, localZ);
        if (!regionFile.Index.ContainsKey(indexKey) || regionFile.Index[indexKey].Offset == 0)
        {
            return false; // Chunk doesn't exist
        }
        
        try
        {
            var entry = regionFile.Index[indexKey];
            regionFile.Stream.Seek(entry.Offset, SeekOrigin.Begin);
            var chunkBytes = new byte[entry.Length];
            regionFile.Stream.Read(chunkBytes, 0, entry.Length);
            
            // Decompress if needed
            if ((entry.Flags & 1) != 0)
            {
                // TODO: Implement decompression when compression is added
                // For now, assume uncompressed
            }
            
            // TODO: Deserialize chunk bytes to VoxelChunkData
            // This would reuse existing chunk deserialization logic
            // data = ChunkSerializer.Deserialize(chunkBytes);
            
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }
    
    /// <summary>
    /// Writes chunk data to a region file
    /// </summary>
    private void WriteChunkToRegion(RegionFile regionFile, int localX, int localZ, VoxelChunkData data)
    {
        // TODO: Serialize chunk data to bytes
        // var chunkBytes = ChunkSerializer.Serialize(data);
        var chunkBytes = new byte[0]; // Placeholder
        
        // Compress if enabled
        if (_enableCompression)
        {
            // TODO: Implement compression
            // chunkBytes = Compressor.Compress(chunkBytes);
        }
        
        // Find position to write (append to end for simplicity)
        regionFile.Stream.Seek(0, SeekOrigin.End);
        var offset = regionFile.Stream.Position;
        
        // Write chunk data
        regionFile.Writer.Write(chunkBytes);
        
        // Update index
        var indexKey = (localX, localZ);
        var flags = _enableCompression ? 1 : 0;
        regionFile.Index[indexKey] = new ChunkIndexEntry
        {
            Offset = (int)offset,
            Length = chunkBytes.Length,
            Flags = flags
        };
        
        // TODO: Update index in file (rewrite index section)
        // For now, index is only kept in memory
    }
    
    /// <summary>
    /// Helper to read int32 from stream
    /// </summary>
    private static int ReadInt32(Stream stream)
    {
        var buffer = new byte[4];
        stream.Read(buffer, 0, 4);
        return buffer[0] | (buffer[1] << 8) | (buffer[2] << 16) | (buffer[3] << 24);
    }
}
