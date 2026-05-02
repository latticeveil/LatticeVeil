using System;
using System.Numerics;

namespace LatticeVeilMonoGame.Core
{
    /// <summary>
    /// World coordinate system for continuous voxel world
    /// Uses world-space coordinates for all operations to prevent chunk gaps
    /// </summary>
    public static class WorldCoordinates
    {
        // Chunk dimensions
        public const int ChunkSizeX = 16;
        public const int ChunkSizeY = 256;
        public const int ChunkSizeZ = 16;
        
        // World limits
        public const int WorldMinX = -1000000;
        public const int WorldMaxX = 1000000;
        public const int WorldMinY = 0;
        public const int WorldMaxY = 256;
        public const int WorldMinZ = -1000000;
        public const int WorldMaxZ = 1000000;
        
        /// <summary>
        /// Convert world coordinates to chunk coordinates
        /// </summary>
        public static Vector3Int WorldToChunk(Vector3 worldPos)
        {
            return new Vector3Int(
                (int)MathF.Floor(worldPos.X / (float)ChunkSizeX),
                (int)MathF.Floor(worldPos.Y / (float)ChunkSizeY),
                (int)MathF.Floor(worldPos.Z / (float)ChunkSizeZ)
            );
        }
        
        /// <summary>
        /// Convert chunk coordinates to world coordinates (chunk origin)
        /// </summary>
        public static Vector3 ChunkToWorld(Vector3Int chunkPos)
        {
            return new Vector3(
                chunkPos.X * ChunkSizeX,
                chunkPos.Y * ChunkSizeY,
                chunkPos.Z * ChunkSizeZ
            );
        }
        
        /// <summary>
        /// Convert world coordinates to local chunk coordinates
        /// </summary>
        public static Vector3Int WorldToLocal(Vector3 worldPos)
        {
            Vector3Int chunkPos = WorldToChunk(worldPos);
            Vector3 chunkOrigin = ChunkToWorld(chunkPos);
            
            return new Vector3Int(
                (int)MathF.Floor(worldPos.X - chunkOrigin.X),
                (int)MathF.Floor(worldPos.Y - chunkOrigin.Y),
                (int)MathF.Floor(worldPos.Z - chunkOrigin.Z)
            );
        }
        
        /// <summary>
        /// Check if world coordinates are within valid bounds
        /// </summary>
        public static bool IsValidWorldPosition(Vector3 worldPos)
        {
            return worldPos.X >= WorldMinX && worldPos.X < WorldMaxX &&
                   worldPos.Y >= WorldMinY && worldPos.Y < WorldMaxY &&
                   worldPos.Z >= WorldMinZ && worldPos.Z < WorldMaxZ;
        }
        
        /// <summary>
        /// Check if local chunk coordinates are within chunk bounds
        /// </summary>
        public static bool IsValidLocalPosition(Vector3Int localPos)
        {
            return localPos.X >= 0 && localPos.X < ChunkSizeX &&
                   localPos.Y >= 0 && localPos.Y < ChunkSizeY &&
                   localPos.Z >= 0 && localPos.Z < ChunkSizeZ;
        }
        
        /// <summary>
        /// Get all neighboring chunk coordinates
        /// </summary>
        public static Vector3Int[] GetNeighborChunks(Vector3Int chunkPos)
        {
            return new Vector3Int[]
            {
                new Vector3Int(chunkPos.X - 1, chunkPos.Y, chunkPos.Z - 1),
                new Vector3Int(chunkPos.X - 1, chunkPos.Y, chunkPos.Z),
                new Vector3Int(chunkPos.X - 1, chunkPos.Y, chunkPos.Z + 1),
                new Vector3Int(chunkPos.X, chunkPos.Y, chunkPos.Z - 1),
                new Vector3Int(chunkPos.X, chunkPos.Y, chunkPos.Z + 1),
                new Vector3Int(chunkPos.X + 1, chunkPos.Y, chunkPos.Z - 1),
                new Vector3Int(chunkPos.X + 1, chunkPos.Y, chunkPos.Z),
                new Vector3Int(chunkPos.X + 1, chunkPos.Y, chunkPos.Z + 1)
            };
        }
        
        /// <summary>
        /// Calculate distance between two world positions
        /// </summary>
        public static float Distance(Vector3 pos1, Vector3 pos2)
        {
            return Vector3.Distance(pos1, pos2);
        }
        
        /// <summary>
        /// Calculate squared distance (faster for comparisons)
        /// </summary>
        public static float DistanceSquared(Vector3 pos1, Vector3 pos2)
        {
            return Vector3.DistanceSquared(pos1, pos2);
        }
        
        /// <summary>
        /// Get chunk hash for dictionary keys
        /// </summary>
        public static int GetChunkHash(Vector3Int chunkPos)
        {
            // Simple hash function for chunk coordinates
            return chunkPos.X * 73856093 ^ chunkPos.Y * 19349663 ^ chunkPos.Z * 83492791;
        }
        
        /// <summary>
        /// Clamp world position to valid bounds
        /// </summary>
        public static Vector3 ClampToWorld(Vector3 worldPos)
        {
            return new Vector3(
                Math.Clamp(worldPos.X, WorldMinX, WorldMaxX - 1),
                Math.Clamp(worldPos.Y, WorldMinY, WorldMaxY - 1),
                Math.Clamp(worldPos.Z, WorldMinZ, WorldMaxZ - 1)
            );
        }
    }
    
    /// <summary>
    /// 3D integer vector for chunk and block coordinates
    /// </summary>
    public struct Vector3Int : IEquatable<Vector3Int>
    {
        public int X;
        public int Y;
        public int Z;
        
        public Vector3Int(int x, int y, int z)
        {
            X = x;
            Y = y;
            Z = z;
        }
        
        public static Vector3Int operator +(Vector3Int a, Vector3Int b)
        {
            return new Vector3Int(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
        }
        
        public static Vector3Int operator -(Vector3Int a, Vector3Int b)
        {
            return new Vector3Int(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
        }
        
        public static Vector3Int operator *(Vector3Int a, int scalar)
        {
            return new Vector3Int(a.X * scalar, a.Y * scalar, a.Z * scalar);
        }
        
        public static bool operator ==(Vector3Int a, Vector3Int b)
        {
            return a.X == b.X && a.Y == b.Y && a.Z == b.Z;
        }
        
        public static bool operator !=(Vector3Int a, Vector3Int b)
        {
            return !(a == b);
        }
        
        public override bool Equals(object? obj)
        {
            return obj is Vector3Int other && Equals(other);
        }
        
        public bool Equals(Vector3Int other)
        {
            return X == other.X && Y == other.Y && Z == other.Z;
        }
        
        public override int GetHashCode()
        {
            return HashCode.Combine(X, Y, Z);
        }
        
        public override string ToString()
        {
            return $"({X}, {Y}, {Z})";
        }
        
        public static Vector3Int Zero => new Vector3Int(0, 0, 0);
        public static Vector3Int One => new Vector3Int(1, 1, 1);
        public static Vector3Int Up => new Vector3Int(0, 1, 0);
        public static Vector3Int Down => new Vector3Int(0, -1, 0);
        public static Vector3Int Left => new Vector3Int(-1, 0, 0);
        public static Vector3Int Right => new Vector3Int(1, 0, 0);
        public static Vector3Int Forward => new Vector3Int(0, 0, 1);
        public static Vector3Int Back => new Vector3Int(0, 0, -1);
    }
}
