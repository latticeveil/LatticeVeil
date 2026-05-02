using System;
using System.Numerics;

namespace LatticeVeilMonoGame.Core
{
    /// <summary>
    /// Cave generator using 3D noise for organic cave systems
    /// Creates caves that are continuous and respect chunk boundaries
    /// </summary>
    public static class CaveGenerator
    {
        private static FastNoiseGenerator? _noiseGenerator;
        private static readonly object _lock = new object();
        
        // Cave parameters
        private const float CaveThreshold = 0.3f;
        private const float CaveScale = 0.02f;
        private const float CaveDetailScale = 0.05f;
        private const float CaveVerticalBias = 0.3f;
        private const float MinCaveDepth = 10.0f;
        
        /// <summary>
        /// Initialize the cave generator with a noise generator
        /// </summary>
        public static void Initialize(FastNoiseGenerator noiseGenerator)
        {
            lock (_lock)
            {
                _noiseGenerator = noiseGenerator;
            }
        }
        
        /// <summary>
        /// Check if position should be a cave (air block)
        /// </summary>
        public static bool IsCave(float x, float y, float z, float surfaceHeight)
        {
            // Only generate caves underground
            if (y >= surfaceHeight - MinCaveDepth)
            {
                return false;
            }
            
            // Get cave noise values
            float caveNoise = GetCaveNoise(x, y, z);
            float caveDetail = GetCaveDetailNoise(x, y, z);
            
            // Combine noise with vertical bias (more caves deeper underground)
            float depthFactor = Math.Clamp((surfaceHeight - y) / 100.0f, 0.0f, 1.0f);
            float combinedNoise = caveNoise * (1.0f - CaveVerticalBias) + caveDetail * CaveVerticalBias;
            float finalNoise = combinedNoise + depthFactor * 0.2f;
            
            return finalNoise > CaveThreshold;
        }
        
        /// <summary>
        /// Get cave density at position (0.0 = no caves, 1.0 = guaranteed cave)
        /// </summary>
        public static float GetCaveDensity(float x, float y, float z, float surfaceHeight)
        {
            if (y >= surfaceHeight - MinCaveDepth)
            {
                return 0.0f;
            }
            
            float caveNoise = GetCaveNoise(x, y, z);
            float caveDetail = GetCaveDetailNoise(x, y, z);
            float depthFactor = Math.Clamp((surfaceHeight - y) / 100.0f, 0.0f, 1.0f);
            
            float combinedNoise = caveNoise * (1.0f - CaveVerticalBias) + caveDetail * CaveVerticalBias;
            float finalNoise = combinedNoise + depthFactor * 0.2f;
            
            return Math.Clamp((finalNoise - CaveThreshold) / (1.0f - CaveThreshold), 0.0f, 1.0f);
        }
        
        /// <summary>
        /// Get cave noise value at position
        /// </summary>
        private static float GetCaveNoise(float x, float y, float z)
        {
            lock (_lock)
            {
                if (_noiseGenerator == null)
                    return 0f; // Default to no caves if not initialized
                    
                return _noiseGenerator.GetCaveDensity(x, y, z);
            }
        }
        
        /// <summary>
        /// Get cave detail noise value at position
        /// </summary>
        private static float GetCaveDetailNoise(float x, float y, float z)
        {
            lock (_lock)
            {
                if (_noiseGenerator == null)
                    return 0f; // Default to no caves if not initialized
                    
                return _noiseGenerator.GetCaveDensity(x * 2.5f, y * 2.5f, z * 2.5f); // Higher frequency for detail
            }
        }
        
        /// <summary>
        /// Generate cave system for a chunk
        /// Returns a boolean array where true = cave (air), false = solid
        /// </summary>
        public static bool[,,] GenerateCaveChunk(Vector3Int chunkPos, float[,] surfaceHeights)
        {
            bool[,,] caves = new bool[WorldCoordinates.ChunkSizeX, WorldCoordinates.ChunkSizeY, WorldCoordinates.ChunkSizeZ];
            
            Vector3 chunkWorldPos = WorldCoordinates.ChunkToWorld(chunkPos);
            
            for (int x = 0; x < WorldCoordinates.ChunkSizeX; x++)
            {
                for (int y = 0; y < WorldCoordinates.ChunkSizeY; y++)
                {
                    for (int z = 0; z < WorldCoordinates.ChunkSizeZ; z++)
                    {
                        float worldX = chunkWorldPos.X + x;
                        float worldY = chunkWorldPos.Y + y;
                        float worldZ = chunkWorldPos.Z + z;
                        
                        // Get surface height for this column
                        int localX = x + chunkPos.X * WorldCoordinates.ChunkSizeX;
                        int localZ = z + chunkPos.Z * WorldCoordinates.ChunkSizeZ;
                        
                        if (localX >= 0 && localX < surfaceHeights.GetLength(0) && 
                            localZ >= 0 && localZ < surfaceHeights.GetLength(1))
                        {
                            float surfaceHeight = surfaceHeights[localX, localZ];
                            caves[x, y, z] = IsCave(worldX, worldY, worldZ, surfaceHeight);
                        }
                        else
                        {
                            caves[x, y, z] = false;
                        }
                    }
                }
            }
            
            return caves;
        }
        
        /// <summary>
        /// Generate cave system with padding for meshing
        /// Includes 1-block border around chunk for proper neighbor sampling
        /// </summary>
        public static bool[,,] GenerateCaveChunkWithPadding(Vector3Int chunkPos, float[,] surfaceHeights)
        {
            int paddedSizeX = WorldCoordinates.ChunkSizeX + 2;
            int paddedSizeY = WorldCoordinates.ChunkSizeY + 2;
            int paddedSizeZ = WorldCoordinates.ChunkSizeZ + 2;
            
            bool[,,] caves = new bool[paddedSizeX, paddedSizeY, paddedSizeZ];
            
            Vector3 chunkWorldPos = WorldCoordinates.ChunkToWorld(chunkPos);
            
            for (int x = 0; x < paddedSizeX; x++)
            {
                for (int y = 0; y < paddedSizeY; y++)
                {
                    for (int z = 0; z < paddedSizeZ; z++)
                    {
                        float worldX = chunkWorldPos.X + x - 1;
                        float worldY = chunkWorldPos.Y + y - 1;
                        float worldZ = chunkWorldPos.Z + z - 1;
                        
                        // Get surface height for this column
                        int localX = (x - 1) + chunkPos.X * WorldCoordinates.ChunkSizeX;
                        int localZ = (z - 1) + chunkPos.Z * WorldCoordinates.ChunkSizeZ;
                        
                        if (localX >= 0 && localX < surfaceHeights.GetLength(0) && 
                            localZ >= 0 && localZ < surfaceHeights.GetLength(1))
                        {
                            float surfaceHeight = surfaceHeights[localX, localZ];
                            caves[x, y, z] = IsCave(worldX, worldY, worldZ, surfaceHeight);
                        }
                        else
                        {
                            caves[x, y, z] = false;
                        }
                    }
                }
            }
            
            return caves;
        }
        
        /// <summary>
        /// Apply caves to existing block array
        /// </summary>
        public static void ApplyCaves(byte[,,] blocks, bool[,,] caves)
        {
            if (blocks.GetLength(0) != caves.GetLength(0) ||
                blocks.GetLength(1) != caves.GetLength(1) ||
                blocks.GetLength(2) != caves.GetLength(2))
            {
                throw new ArgumentException("Block and cave arrays must have same dimensions");
            }
            
            for (int x = 0; x < blocks.GetLength(0); x++)
            {
                for (int y = 0; y < blocks.GetLength(1); y++)
                {
                    for (int z = 0; z < blocks.GetLength(2); z++)
                    {
                        if (caves[x, y, z])
                        {
                            blocks[x, y, z] = BlockIds.Air;
                        }
                    }
                }
            }
        }
        
        /// <summary>
        /// Generate surface heights for a chunk area
        /// </summary>
        public static float[,] GenerateSurfaceHeights(Vector3Int chunkPos, int radius = 1)
        {
            int sizeX = WorldCoordinates.ChunkSizeX * (radius * 2 + 1);
            int sizeZ = WorldCoordinates.ChunkSizeZ * (radius * 2 + 1);
            float[,] heights = new float[sizeX, sizeZ];
            
            Vector3 centerWorldPos = WorldCoordinates.ChunkToWorld(chunkPos);
            int startX = chunkPos.X - radius;
            int startZ = chunkPos.Z - radius;
            
            for (int x = 0; x < sizeX; x++)
            {
                for (int z = 0; z < sizeZ; z++)
                {
                    int chunkX = startX + x / WorldCoordinates.ChunkSizeX;
                    int chunkZ = startZ + z / WorldCoordinates.ChunkSizeZ;
                    float worldX = chunkX * WorldCoordinates.ChunkSizeX + (x % WorldCoordinates.ChunkSizeX);
                    float worldZ = chunkZ * WorldCoordinates.ChunkSizeZ + (z % WorldCoordinates.ChunkSizeZ);
                    
                    heights[x, z] = GetSpawnHeight(worldX, worldZ);
                }
            }
            
            return heights;
        }
        
        /// <summary>
        /// Get spawn height for a world position (simple terrain height)
        /// </summary>
        private static float GetSpawnHeight(float worldX, float worldZ)
        {
            lock (_lock)
            {
                if (_noiseGenerator == null)
                    return 50f; // Default height if not initialized
                
                // Use terrain noise to get height
                var noise = _noiseGenerator.GetTerrainHeight(worldX * 0.01f, worldZ * 0.01f);
                return (noise + 1.0f) * 50f + 10f; // Normalize and scale to reasonable height
            }
        }
        
        /// <summary>
        /// Validate cave generation parameters
        /// </summary>
        public static bool Validate()
        {
            return CaveThreshold >= 0.0f && CaveThreshold <= 1.0f &&
                   CaveScale > 0.0f && CaveDetailScale > 0.0f &&
                   MinCaveDepth >= 0.0f;
        }
        
        /// <summary>
        /// Get cave generation statistics
        /// </summary>
        public static CaveStats GetStats()
        {
            return new CaveStats
            {
                Threshold = CaveThreshold,
                Scale = CaveScale,
                DetailScale = CaveDetailScale,
                VerticalBias = CaveVerticalBias,
                MinDepth = MinCaveDepth
            };
        }
    }
    
    /// <summary>
    /// Cave generation statistics
    /// </summary>
    public class CaveStats
    {
        public float Threshold { get; set; }
        public float Scale { get; set; }
        public float DetailScale { get; set; }
        public float VerticalBias { get; set; }
        public float MinDepth { get; set; }
        
        public override string ToString()
        {
            return $"Caves - Threshold: {Threshold:F3}, Scale: {Scale:F4}, Detail: {DetailScale:F4}, Bias: {VerticalBias:F3}, MinDepth: {MinDepth:F1}";
        }
    }
}
