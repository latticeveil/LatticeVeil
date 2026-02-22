using System;
using System.Numerics;
using System.Threading.Tasks;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.IO;

namespace LatticeVeilMonoGame.Core
{
    /// <summary>
    /// Complete VoxelChunkData implementation with all required members
    /// Thread-safe chunk storage with 3D block arrays, water storage, and metadata
    /// </summary>
    public class VoxelChunkData
    {
        // Chunk dimensions
        public const int ChunkSizeX = 16;
        public const int ChunkSizeY = 256;
        public const int ChunkSizeZ = 16;
        public const int ChunkVolume = ChunkSizeX * ChunkSizeY * ChunkSizeZ;
        
        // Storage arrays
        private readonly byte[,,] _blocks;
        private readonly byte[,,] _water;
        private readonly byte[,,] _metadata;
        
        // Thread safety
        private readonly ReaderWriterLockSlim _lock = new ReaderWriterLockSlim();
        
        // Chunk metadata - PUBLIC SETTERS FOR COMPATIBILITY
        public ChunkCoord Coord { get; set; }
        public bool IsDirty { get; set; }
        public bool NeedsSave { get; set; }
        public DateTime LastModified { get; set; }
        public int Version { get; set; }
        
        // Volume property - READONLY FOR COMPATIBILITY
        public static int Volume => ChunkVolume;
        
        // Performance tracking
        private int _blockAccessCount;
        private int _waterAccessCount;
        
        // Legacy API compatibility - expose blocks as flat array
        public byte[] Blocks
        {
            get
            {
                _lock.EnterReadLock();
                try
                {
                    // Convert 3D array to flat array for legacy compatibility
                    var flat = new byte[ChunkVolume];
                    int index = 0;
                    for (int x = 0; x < ChunkSizeX; x++)
                    {
                        for (int y = 0; y < ChunkSizeY; y++)
                        {
                            for (int z = 0; z < ChunkSizeZ; z++)
                            {
                                flat[index++] = _blocks[x, y, z];
                            }
                        }
                    }
                    return flat;
                }
                finally
                {
                    _lock.ExitReadLock();
                }
            }
        }
        
        // Default constructor
        public VoxelChunkData()
        {
            Coord = new ChunkCoord(0, 0, 0);
            _blocks = new byte[ChunkSizeX, ChunkSizeY, ChunkSizeZ];
            _water = new byte[ChunkSizeX, ChunkSizeY, ChunkSizeZ];
            _metadata = new byte[ChunkSizeX, ChunkSizeY, ChunkSizeZ];
            LastModified = DateTime.UtcNow;
            Version = 1;
        }
        
        public VoxelChunkData(ChunkCoord coord)
        {
            Coord = coord;
            _blocks = new byte[ChunkSizeX, ChunkSizeY, ChunkSizeZ];
            _water = new byte[ChunkSizeX, ChunkSizeY, ChunkSizeZ];
            _metadata = new byte[ChunkSizeX, ChunkSizeY, ChunkSizeZ];
            LastModified = DateTime.UtcNow;
            Version = 1;
        }
        
        /// <summary>
        /// Legacy API compatibility - get block at local coordinates
        /// </summary>
        public byte GetLocal(int x, int y, int z)
        {
            return GetBlock(x, y, z);
        }
        
        /// <summary>
        /// Legacy API compatibility - set block at local coordinates
        /// </summary>
        public void SetLocal(int x, int y, int z, byte blockId)
        {
            SetBlock(x, y, z, blockId);
        }
        
        /// <summary>
        /// Load chunk data from file
        /// </summary>
        public void Load(string filePath)
        {
            if (!File.Exists(filePath))
                return;
            
            try
            {
                var data = File.ReadAllBytes(filePath);
                if (data.Length == ChunkVolume)
                {
                    _lock.EnterWriteLock();
                    try
                    {
                        int index = 0;
                        for (int x = 0; x < ChunkSizeX; x++)
                        {
                            for (int y = 0; y < ChunkSizeY; y++)
                            {
                                for (int z = 0; z < ChunkSizeZ; z++)
                                {
                                    _blocks[x, y, z] = data[index++];
                                }
                            }
                        }
                        MarkDirty();
                    }
                    finally
                    {
                        _lock.ExitWriteLock();
                    }
                }
            }
            catch (Exception ex)
            {
                // Log error but don't crash
                Console.WriteLine($"Failed to load chunk from {filePath}: {ex.Message}");
            }
        }
        
        /// <summary>
        /// Load chunk data from byte array
        /// </summary>
        public void Load(byte[] data)
        {
            if (data == null || data.Length != ChunkVolume)
                return;
            
            _lock.EnterWriteLock();
            try
            {
                int index = 0;
                for (int x = 0; x < ChunkSizeX; x++)
                {
                    for (int y = 0; y < ChunkSizeY; y++)
                    {
                        for (int z = 0; z < ChunkSizeZ; z++)
                        {
                            _blocks[x, y, z] = data[index++];
                        }
                    }
                }
                MarkDirty();
            }
            finally
            {
                _lock.ExitWriteLock();
            }
        }
        
        /// <summary>
        /// Save chunk data to file
        /// </summary>
        public void Save(string filePath)
        {
            try
            {
                var flat = Blocks;
                File.WriteAllBytes(filePath, flat);
                MarkClean();
            }
            catch (Exception ex)
            {
                // Log error but don't crash
                Console.WriteLine($"Failed to save chunk to {filePath}: {ex.Message}");
            }
        }
        
        /// <summary>
        /// Save chunk snapshot to file (static method for ChunkStreamingService)
        /// </summary>
        public static void SaveSnapshot(string filePath, ChunkCoord coord, byte[] blocks)
        {
            try
            {
                // Ensure directory exists
                var directory = Path.GetDirectoryName(filePath);
                if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                {
                    Directory.CreateDirectory(directory);
                }
                
                File.WriteAllBytes(filePath, blocks);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to save chunk snapshot to {filePath}: {ex.Message}");
            }
        }
        
        /// <summary>
        /// Get block at local coordinates (thread-safe)
        /// </summary>
        public byte GetBlock(int x, int y, int z)
        {
            if (!IsValidLocalPosition(x, y, z))
                return BlockIds.Air;
            
            _lock.EnterReadLock();
            try
            {
                Interlocked.Increment(ref _blockAccessCount);
                return _blocks[x, y, z];
            }
            finally
            {
                _lock.ExitReadLock();
            }
        }

        /// <summary>
        /// Copy all local block ids into the provided destination array under a single read lock.
        /// </summary>
        public void CopyBlocksTo(byte[,,] destination)
        {
            if (destination.GetLength(0) != ChunkSizeX
                || destination.GetLength(1) != ChunkSizeY
                || destination.GetLength(2) != ChunkSizeZ)
            {
                throw new ArgumentException("Destination array must match chunk dimensions.");
            }

            _lock.EnterReadLock();
            try
            {
                for (int x = 0; x < ChunkSizeX; x++)
                {
                    for (int y = 0; y < ChunkSizeY; y++)
                    {
                        for (int z = 0; z < ChunkSizeZ; z++)
                        {
                            destination[x, y, z] = _blocks[x, y, z];
                        }
                    }
                }
            }
            finally
            {
                _lock.ExitReadLock();
            }
        }
        
        /// <summary>
        /// Set block at local coordinates (thread-safe)
        /// </summary>
        public void SetBlock(int x, int y, int z, byte blockId)
        {
            if (!IsValidLocalPosition(x, y, z))
                return;
            
            _lock.EnterWriteLock();
            try
            {
                if (_blocks[x, y, z] != blockId)
                {
                    _blocks[x, y, z] = blockId;
                    MarkDirty();
                }
            }
            finally
            {
                _lock.ExitWriteLock();
            }
        }
        
        /// <summary>
        /// Get water level at local coordinates (thread-safe)
        /// </summary>
        public byte GetWater(int x, int y, int z)
        {
            if (!IsValidLocalPosition(x, y, z))
                return 0;
            
            _lock.EnterReadLock();
            try
            {
                Interlocked.Increment(ref _waterAccessCount);
                return _water[x, y, z];
            }
            finally
            {
                _lock.ExitReadLock();
            }
        }
        
        /// <summary>
        /// Set water level at local coordinates (thread-safe)
        /// </summary>
        public void SetWater(int x, int y, int z, byte waterLevel)
        {
            if (!IsValidLocalPosition(x, y, z))
                return;
            
            _lock.EnterWriteLock();
            try
            {
                if (_water[x, y, z] != waterLevel)
                {
                    _water[x, y, z] = waterLevel;
                    MarkDirty();
                }
            }
            finally
            {
                _lock.ExitWriteLock();
            }
        }
        
        /// <summary>
        /// Get metadata at local coordinates (thread-safe)
        /// </summary>
        public byte GetMetadata(int x, int y, int z)
        {
            if (!IsValidLocalPosition(x, y, z))
                return 0;
            
            _lock.EnterReadLock();
            try
            {
                return _metadata[x, y, z];
            }
            finally
            {
                _lock.ExitReadLock();
            }
        }
        
        /// <summary>
        /// Set metadata at local coordinates (thread-safe)
        /// </summary>
        public void SetMetadata(int x, int y, int z, byte metadata)
        {
            if (!IsValidLocalPosition(x, y, z))
                return;
            
            _lock.EnterWriteLock();
            try
            {
                if (_metadata[x, y, z] != metadata)
                {
                    _metadata[x, y, z] = metadata;
                    MarkDirty();
                }
            }
            finally
            {
                _lock.ExitWriteLock();
            }
        }
        
        /// <summary>
        /// Get block at world coordinates (thread-safe)
        /// </summary>
        public byte GetBlockWorld(int worldX, int worldY, int worldZ)
        {
            WorldToChunkLocal(worldX, worldY, worldZ, out int lx, out int ly, out int lz);
            return GetBlock(lx, ly, lz);
        }
        
        /// <summary>
        /// Set block at world coordinates (thread-safe)
        /// </summary>
        public void SetBlockWorld(int worldX, int worldY, int worldZ, byte blockId)
        {
            WorldToChunkLocal(worldX, worldY, worldZ, out int lx, out int ly, out int lz);
            SetBlock(lx, ly, lz, blockId);
        }
        
        /// <summary>
        /// Check if position is valid for this chunk
        /// </summary>
        public bool IsValidLocalPosition(int x, int y, int z)
        {
            return x >= 0 && x < ChunkSizeX &&
                   y >= 0 && y < ChunkSizeY &&
                   z >= 0 && z < ChunkSizeZ;
        }
        
        /// <summary>
        /// Check if world coordinates belong to this chunk
        /// </summary>
        public bool ContainsWorldPosition(int worldX, int worldY, int worldZ)
        {
            WorldToChunkLocal(worldX, worldY, worldZ, out int lx, out int ly, out int lz);
            return IsValidLocalPosition(lx, ly, lz);
        }
        
        /// <summary>
        /// Get neighbor block safely (thread-safe)
        /// </summary>
        public byte GetNeighborBlock(int x, int y, int z, int dx, int dy, int dz)
        {
            int nx = x + dx;
            int ny = y + dy;
            int nz = z + dz;
            
            if (IsValidLocalPosition(nx, ny, nz))
            {
                return GetBlock(nx, ny, nz);
            }
            
            // World coordinates for neighbor chunk
            int worldX = Coord.X * ChunkSizeX + nx;
            int worldY = Coord.Y * ChunkSizeY + ny;
            int worldZ = Coord.Z * ChunkSizeZ + nz;
            
            // This would require access to world manager to get neighbor chunk
            // For now, return air for out-of-bounds
            return BlockIds.Air;
        }
        
        /// <summary>
        /// Fill chunk with specific block type
        /// </summary>
        public void Fill(byte blockId)
        {
            _lock.EnterWriteLock();
            try
            {
                for (int x = 0; x < ChunkSizeX; x++)
                {
                    for (int y = 0; y < ChunkSizeY; y++)
                    {
                        for (int z = 0; z < ChunkSizeZ; z++)
                        {
                            _blocks[x, y, z] = blockId;
                        }
                    }
                }
                MarkDirty();
            }
            finally
            {
                _lock.ExitWriteLock();
            }
        }
        
        /// <summary>
        /// Clear chunk (set all blocks to air)
        /// </summary>
        public void Clear()
        {
            Fill(BlockIds.Air);
            
            _lock.EnterWriteLock();
            try
            {
                // Clear water and metadata too
                for (int x = 0; x < ChunkSizeX; x++)
                {
                    for (int y = 0; y < ChunkSizeY; y++)
                    {
                        for (int z = 0; z < ChunkSizeZ; z++)
                        {
                            _water[x, y, z] = 0;
                            _metadata[x, y, z] = 0;
                        }
                    }
                }
                MarkDirty();
            }
            finally
            {
                _lock.ExitWriteLock();
            }
        }
        
        /// <summary>
        /// Get chunk statistics
        /// </summary>
        public VoxelChunkStats GetStats()
        {
            _lock.EnterReadLock();
            try
            {
                var blockCounts = new ConcurrentDictionary<byte, int>();
                var waterCounts = new ConcurrentDictionary<byte, int>();
                
                for (int x = 0; x < ChunkSizeX; x++)
                {
                    for (int y = 0; y < ChunkSizeY; y++)
                    {
                        for (int z = 0; z < ChunkSizeZ; z++)
                        {
                            byte block = _blocks[x, y, z];
                            byte water = _water[x, y, z];
                            
                            blockCounts.AddOrUpdate(block, 1, (k, v) => v + 1);
                            if (water > 0)
                            {
                                waterCounts.AddOrUpdate(water, 1, (k, v) => v + 1);
                            }
                        }
                    }
                }
                
                return new VoxelChunkStats
                {
                    Coord = Coord,
                    BlockCounts = blockCounts,
                    WaterCounts = waterCounts,
                    IsDirty = IsDirty,
                    NeedsSave = NeedsSave,
                    LastModified = LastModified,
                    Version = Version,
                    BlockAccessCount = _blockAccessCount,
                    WaterAccessCount = _waterAccessCount
                };
            }
            finally
            {
                _lock.ExitReadLock();
            }
        }
        
        /// <summary>
        /// Mark chunk as dirty and needing save
        /// </summary>
        public void MarkDirty()
        {
            IsDirty = true;
            NeedsSave = true;
            LastModified = DateTime.UtcNow;
            Version++;
        }
        
        /// <summary>
        /// Mark chunk as clean (after save)
        /// </summary>
        public void MarkClean()
        {
            _lock.EnterWriteLock();
            try
            {
                IsDirty = false;
                NeedsSave = false;
            }
            finally
            {
                _lock.ExitWriteLock();
            }
        }
        
        /// <summary>
        /// Convert world coordinates to chunk local coordinates
        /// </summary>
        private void WorldToChunkLocal(int worldX, int worldY, int worldZ, out int lx, out int ly, out int lz)
        {
            lx = worldX - Coord.X * ChunkSizeX;
            ly = worldY - Coord.Y * ChunkSizeY;
            lz = worldZ - Coord.Z * ChunkSizeZ;
        }
        
        /// <summary>
        /// Convert chunk local coordinates to world coordinates
        /// </summary>
        public void LocalToWorld(int lx, int ly, int lz, out int worldX, out int worldY, out int worldZ)
        {
            worldX = Coord.X * ChunkSizeX + lx;
            worldY = Coord.Y * ChunkSizeY + ly;
            worldZ = Coord.Z * ChunkSizeZ + lz;
        }
        
        /// <summary>
        /// Get world position of chunk origin
        /// </summary>
        public Vector3 GetWorldOrigin()
        {
            return new Vector3(Coord.X * ChunkSizeX, Coord.Y * ChunkSizeY, Coord.Z * ChunkSizeZ);
        }
        
        /// <summary>
        /// Clone chunk data (deep copy)
        /// </summary>
        public VoxelChunkData Clone()
        {
            var clone = new VoxelChunkData(Coord);
            
            _lock.EnterReadLock();
            try
            {
                // Copy block data
                for (int x = 0; x < ChunkSizeX; x++)
                {
                    for (int y = 0; y < ChunkSizeY; y++)
                    {
                        for (int z = 0; z < ChunkSizeZ; z++)
                        {
                            clone._blocks[x, y, z] = _blocks[x, y, z];
                            clone._water[x, y, z] = _water[x, y, z];
                            clone._metadata[x, y, z] = _metadata[x, y, z];
                        }
                    }
                }
                
                // Copy metadata
                clone.IsDirty = IsDirty;
                clone.NeedsSave = NeedsSave;
                clone.LastModified = LastModified;
                clone.Version = Version;
            }
            finally
            {
                _lock.ExitReadLock();
            }
            
            return clone;
        }
        
        /// <summary>
        /// Dispose resources
        /// </summary>
        public void Dispose()
        {
            _lock?.Dispose();
        }
    }
    
    /// <summary>
    /// Voxel chunk statistics
    /// </summary>
    public class VoxelChunkStats
    {
        public ChunkCoord Coord { get; set; }
        public ConcurrentDictionary<byte, int> BlockCounts { get; set; } = new();
        public ConcurrentDictionary<byte, int> WaterCounts { get; set; } = new();
        public bool IsDirty { get; set; }
        public bool NeedsSave { get; set; }
        public DateTime LastModified { get; set; }
        public int Version { get; set; }
        public int BlockAccessCount { get; set; }
        public int WaterAccessCount { get; set; }
        
        public override string ToString()
        {
            return $"Chunk {Coord} - Blocks: {BlockCounts.Count}, Water: {WaterCounts.Count}, Dirty: {IsDirty}, Version: {Version}";
        }
    }
}
