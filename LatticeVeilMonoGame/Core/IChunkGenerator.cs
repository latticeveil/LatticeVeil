using System;

namespace LatticeVeilMonoGame.Core;

/// <summary>
/// Minimal interface for deterministic, on-demand chunk generators.
/// </summary>
public interface IChunkGenerator : IDisposable
{
    VoxelChunkData GenerateChunk(ChunkCoord coord);
}
