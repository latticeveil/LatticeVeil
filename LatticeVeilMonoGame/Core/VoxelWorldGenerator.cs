using System;
using System.IO;

namespace LatticeVeilMonoGame.Core;

public sealed class VoxelWorldGenerator
{
    private readonly WorldMeta _meta;
    private readonly string _chunksDir;
    private readonly int _worldWidth;
    private readonly int _worldHeight;
    private readonly int _worldDepth;
    private readonly int _chunksX;
    private readonly int _chunksY;
    private readonly int _chunksZ;

    public int TotalBlocks { get; }
    public bool IsComplete { get; private set; }
    public string? OutputPath { get; private set; }

    public float Progress => TotalBlocks == 0 ? 1f : 0f; // Simplified since we use modular system

    public VoxelWorldGenerator(WorldMeta meta, string worldPath)
    {
        _meta = meta;
        _chunksDir = Path.Combine(worldPath, "chunks");
        Directory.CreateDirectory(_chunksDir);

        _worldWidth = Math.Max(1, _meta.Size.Width);
        _worldHeight = Math.Max(1, _meta.Size.Height);
        _worldDepth = Math.Max(1, _meta.Size.Depth);

        _chunksX = (_worldWidth + VoxelChunkData.ChunkSizeX - 1) / VoxelChunkData.ChunkSizeX;
        _chunksY = (_worldHeight + VoxelChunkData.ChunkSizeY - 1) / VoxelChunkData.ChunkSizeY;
        _chunksZ = (_worldDepth + VoxelChunkData.ChunkSizeZ - 1) / VoxelChunkData.ChunkSizeZ;

        TotalBlocks = _worldWidth * _worldHeight * _worldDepth;
        IsComplete = true; // Always complete since we use modular system
    }

    public static void GenerateChunk(WorldMeta meta, ChunkCoord coord, VoxelChunkData chunk)
    {
        // Use HYPER-OPTIMIZED generator to eliminate lagging
        OptimizedWorldGenerator.GenerateChunk(meta, coord, chunk);
    }
}
