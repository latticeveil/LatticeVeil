using System;
using System.Collections.Concurrent;
using System.Collections.Generic;

namespace LatticeVeilMonoGame.Core
{
    /// <summary>
    /// Legacy compatibility layer for ChunkSeamRegistry
    /// Manages chunk surface profiles for seamless terrain generation
    /// </summary>
    public static class ChunkSeamRegistry
    {
        // Storage for chunk surface profiles
        private static readonly ConcurrentDictionary<ChunkCoord, ChunkSurfaceProfile> _profiles;
        
        static ChunkSeamRegistry()
        {
            _profiles = new ConcurrentDictionary<ChunkCoord, ChunkSurfaceProfile>();
        }
        
        /// <summary>
        /// Register a chunk surface profile
        /// </summary>
        public static void Register(ChunkCoord coord, ChunkSurfaceProfile profile)
        {
            _profiles.TryAdd(coord, profile);
        }
        
        /// <summary>
        /// Try to get a chunk surface profile
        /// </summary>
        public static bool TryGet(ChunkCoord coord, out ChunkSurfaceProfile? profile)
        {
            return _profiles.TryGetValue(coord, out profile);
        }
        
        /// <summary>
        /// Unregister a chunk surface profile
        /// </summary>
        public static void Unregister(ChunkCoord coord)
        {
            _profiles.TryRemove(coord, out _);
        }
        
        /// <summary>
        /// Clear all registered profiles
        /// </summary>
        public static void Clear()
        {
            _profiles.Clear();
        }
        
        /// <summary>
        /// Get all registered profiles
        /// </summary>
        public static IEnumerable<KeyValuePair<ChunkCoord, ChunkSurfaceProfile>> GetAll()
        {
            return _profiles;
        }
        
        /// <summary>
        /// Get profile count
        /// </summary>
        public static int GetCount()
        {
            return _profiles.Count;
        }
        
        /// <summary>
        /// Check if profile exists
        /// </summary>
        public static bool Contains(ChunkCoord coord)
        {
            return _profiles.ContainsKey(coord);
        }
    }
    
    /// <summary>
    /// Chunk surface profile for seamless terrain generation
    /// </summary>
    public class ChunkSurfaceProfile
    {
        // Surface heights for each column
        public int[,] SurfaceHeights { get; private set; }
        
        // Edge heights for seamless blending
        public int[] NorthEdge { get; private set; } = new int[16];
        public int[] SouthEdge { get; private set; } = new int[16];
        public int[] EastEdge { get; private set; } = new int[16];
        public int[] WestEdge { get; private set; } = new int[16];
        
        // Chunk coordinates
        public ChunkCoord Coord { get; private set; }
        
        public ChunkSurfaceProfile(ChunkCoord coord, int[,] surfaceHeights)
        {
            Coord = coord;
            SurfaceHeights = surfaceHeights;
            
            // Extract edge heights
            ExtractEdgeHeights();
        }
        
        /// <summary>
        /// Create surface profile from chunk data
        /// </summary>
        public static ChunkSurfaceProfile FromChunk(VoxelChunkData chunk)
        {
            var surfaceHeights = new int[VoxelChunkData.ChunkSizeX, VoxelChunkData.ChunkSizeZ];
            var worldOrigin = chunk.GetWorldOrigin();
            
            for (int x = 0; x < VoxelChunkData.ChunkSizeX; x++)
            {
                for (int z = 0; z < VoxelChunkData.ChunkSizeZ; z++)
                {
                    // Find surface height for this column
                    for (int y = VoxelChunkData.ChunkSizeY - 1; y >= 0; y--)
                    {
                        var block = chunk.GetBlock(x, y, z);
                        if (block != BlockIds.Air && block != BlockIds.Nullblock)
                        {
                            surfaceHeights[x, z] = (int)worldOrigin.Y + y;
                            break;
                        }
                    }
                }
            }
            
            return new ChunkSurfaceProfile(chunk.Coord, surfaceHeights);
        }
        
        /// <summary>
        /// Create surface profile from height array
        /// </summary>
        public static ChunkSurfaceProfile FromHeights(int[,] heights)
        {
            // This would need a coordinate - for now use origin
            var coord = new ChunkCoord(0, 0, 0);
            return new ChunkSurfaceProfile(coord, heights);
        }
        
        /// <summary>
        /// Extract edge heights from surface heights
        /// </summary>
        private void ExtractEdgeHeights()
        {
            var sizeX = SurfaceHeights.GetLength(0);
            var sizeZ = SurfaceHeights.GetLength(1);
            
            // North edge (Z = 0)
            NorthEdge = new int[sizeX];
            for (int x = 0; x < sizeX; x++)
            {
                NorthEdge[x] = SurfaceHeights[x, 0];
            }
            
            // South edge (Z = sizeZ - 1)
            SouthEdge = new int[sizeX];
            for (int x = 0; x < sizeX; x++)
            {
                SouthEdge[x] = SurfaceHeights[x, sizeZ - 1];
            }
            
            // West edge (X = 0)
            WestEdge = new int[sizeZ];
            for (int z = 0; z < sizeZ; z++)
            {
                WestEdge[z] = SurfaceHeights[0, z];
            }
            
            // East edge (X = sizeX - 1)
            EastEdge = new int[sizeZ];
            for (int z = 0; z < sizeZ; z++)
            {
                EastEdge[z] = SurfaceHeights[sizeX - 1, z];
            }
        }
        
        /// <summary>
        /// Get surface height at local coordinates
        /// </summary>
        public int GetSurfaceHeight(int x, int z)
        {
            if (x < 0 || x >= SurfaceHeights.GetLength(0) ||
                z < 0 || z >= SurfaceHeights.GetLength(1))
            {
                return int.MinValue;
            }
            
            return SurfaceHeights[x, z];
        }
        
        /// <summary>
        /// Set surface height at local coordinates
        /// </summary>
        public void SetSurfaceHeight(int x, int z, int height)
        {
            if (x < 0 || x >= SurfaceHeights.GetLength(0) ||
                z < 0 || z >= SurfaceHeights.GetLength(1))
            {
                return;
            }
            
            SurfaceHeights[x, z] = height;
            
            // Update edge arrays if necessary
            if (z == 0 && NorthEdge != null)
            {
                NorthEdge[x] = height;
            }
            else if (z == SurfaceHeights.GetLength(1) - 1 && SouthEdge != null)
            {
                SouthEdge[x] = height;
            }
            
            if (x == 0 && WestEdge != null)
            {
                WestEdge[z] = height;
            }
            else if (x == SurfaceHeights.GetLength(0) - 1 && EastEdge != null)
            {
                EastEdge[z] = height;
            }
        }
        
        /// <summary>
        /// Blend with neighboring profiles for seamless terrain
        /// </summary>
        public void BlendWithNeighbors(ChunkSurfaceProfile? north, ChunkSurfaceProfile? south, 
                                      ChunkSurfaceProfile? east, ChunkSurfaceProfile? west)
        {
            // Blend north edge
            if (north != null && SouthEdge != null && north.NorthEdge != null)
            {
                for (int x = 0; x < Math.Min(SouthEdge.Length, north.NorthEdge.Length); x++)
                {
                    var blended = (SouthEdge[x] + north.NorthEdge[x]) / 2;
                    SouthEdge[x] = blended;
                    north.NorthEdge[x] = blended;
                }
            }
            
            // Blend south edge
            if (south != null && NorthEdge != null && south.SouthEdge != null)
            {
                for (int x = 0; x < Math.Min(NorthEdge.Length, south.SouthEdge.Length); x++)
                {
                    var blended = (NorthEdge[x] + south.SouthEdge[x]) / 2;
                    NorthEdge[x] = blended;
                    south.SouthEdge[x] = blended;
                }
            }
            
            // Blend east edge
            if (east != null && WestEdge != null && east.WestEdge != null)
            {
                for (int z = 0; z < Math.Min(WestEdge.Length, east.WestEdge.Length); z++)
                {
                    var blended = (WestEdge[z] + east.WestEdge[z]) / 2;
                    WestEdge[z] = blended;
                    east.WestEdge[z] = blended;
                }
            }
            
            // Blend west edge
            if (west != null && EastEdge != null && west.EastEdge != null)
            {
                for (int z = 0; z < Math.Min(EastEdge.Length, west.EastEdge.Length); z++)
                {
                    var blended = (EastEdge[z] + west.EastEdge[z]) / 2;
                    EastEdge[z] = blended;
                    west.EastEdge[z] = blended;
                }
            }
        }
        
        /// <summary>
        /// Get profile statistics
        /// </summary>
        public ProfileStats GetStats()
        {
            var minHeight = int.MaxValue;
            var maxHeight = int.MinValue;
            var totalHeight = 0;
            var count = 0;
            
            for (int x = 0; x < SurfaceHeights.GetLength(0); x++)
            {
                for (int z = 0; z < SurfaceHeights.GetLength(1); z++)
                {
                    var height = SurfaceHeights[x, z];
                    if (height > int.MinValue) // Valid height
                    {
                        minHeight = Math.Min(minHeight, height);
                        maxHeight = Math.Max(maxHeight, height);
                        totalHeight += height;
                        count++;
                    }
                }
            }
            
            return new ProfileStats
            {
                Coord = Coord,
                MinHeight = minHeight == int.MaxValue ? 0 : minHeight,
                MaxHeight = maxHeight == int.MinValue ? 0 : maxHeight,
                AverageHeight = count > 0 ? totalHeight / count : 0,
                ValidColumns = count
            };
        }
    }
    
    /// <summary>
    /// Profile statistics
    /// </summary>
    public class ProfileStats
    {
        public ChunkCoord Coord { get; set; }
        public int MinHeight { get; set; }
        public int MaxHeight { get; set; }
        public int AverageHeight { get; set; }
        public int ValidColumns { get; set; }
        
        public override string ToString()
        {
            return $"Profile {Coord} - Min: {MinHeight}, Max: {MaxHeight}, Avg: {AverageHeight}, Valid: {ValidColumns}";
        }
    }
}
