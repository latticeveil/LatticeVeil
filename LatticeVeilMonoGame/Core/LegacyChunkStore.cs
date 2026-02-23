using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;

namespace LatticeVeilMonoGame.Core;

/// <summary>
/// Legacy chunk storage using individual .bin files
/// Used for backward compatibility with existing worlds
/// </summary>
public sealed class LegacyChunkStore : IChunkStore
{
    private static readonly Regex ChunkNameRegex = new(@"chunk_(?<x>-?\d+)_(?<y>-?\d+)_(?<z>-?\d+)\.bin", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    
    private readonly string _worldPath;
    private readonly Dictionary<ChunkCoord, VoxelChunkData> _chunks = new();
    private readonly object _chunksLock = new();

    public LegacyChunkStore(string worldPath)
    {
        _worldPath = worldPath;
    }

    public bool TryLoadChunk(ChunkCoord coord, out VoxelChunkData data)
    {
        data = null;
        
        if (!TryGetChunk(coord, out data))
        {
            return false;
        }
        
        return true;
    }

    public void SaveChunk(ChunkCoord coord, VoxelChunkData data)
    {
        lock (_chunksLock)
        {
            _chunks[coord] = data;
        }
        
        try
        {
            var path = Path.Combine(_worldPath, "chunks", $"chunk_{coord.X}_{coord.Y}_{coord.Z}.bin");
            data.Save(path);
        }
        catch (Exception ex)
        {
            // Log but don't crash
            System.Diagnostics.Debug.WriteLine($"Failed to save chunk {coord}: {ex.Message}");
        }
    }

    public void DeleteChunk(ChunkCoord coord)
    {
        lock (_chunksLock)
        {
            _chunks.Remove(coord);
        }
        
        try
        {
            var path = Path.Combine(_worldPath, "chunks", $"chunk_{coord.X}_{coord.Y}_{coord.Z}.bin");
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex)
        {
            // Log but don't crash
            System.Diagnostics.Debug.WriteLine($"Failed to delete chunk {coord}: {ex.Message}");
        }
    }

    public void Flush()
    {
        // Legacy store writes immediately, no flush needed
    }

    private bool TryGetChunk(ChunkCoord coord, out VoxelChunkData data)
    {
        data = null;
        
        lock (_chunksLock)
        {
            if (_chunks.TryGetValue(coord, out data))
            {
                return true;
            }
        }

        try
        {
            var path = Path.Combine(_worldPath, "chunks", $"chunk_{coord.X}_{coord.Y}_{coord.Z}.bin");
            if (!File.Exists(path))
            {
                return false;
            }

            var chunk = new VoxelChunkData(coord);
            chunk.Load(path);
            
            lock (_chunksLock)
            {
                _chunks[coord] = chunk;
            }
            
            data = chunk;
            return true;
        }
        catch (Exception)
        {
            data = null;
            return false;
        }
    }
}
