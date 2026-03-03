using System;

namespace LatticeVeilMonoGame.Core;

/// <summary>
/// Interface for chunk storage operations
/// </summary>
public interface IChunkStore
{
    /// <summary>
    /// Attempts to load chunk data for the given coordinates
    /// </summary>
    /// <param name="coord">Chunk coordinates</param>
    /// <param name="data">Loaded chunk data</param>
    /// <returns>True if chunk was loaded successfully</returns>
    bool TryLoadChunk(ChunkCoord coord, out VoxelChunkData data);
    
    /// <summary>
    /// Saves chunk data for the given coordinates
    /// </summary>
    /// <param name="coord">Chunk coordinates</param>
    /// <param name="data">Chunk data to save</param>
    void SaveChunk(ChunkCoord coord, VoxelChunkData data);
    
    /// <summary>
    /// Deletes chunk data for the given coordinates
    /// </summary>
    /// <param name="coord">Chunk coordinates</param>
    void DeleteChunk(ChunkCoord coord);
    
    /// <summary>
    /// Flushes any pending writes to disk
    /// </summary>
    void Flush();
}
