using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;

namespace LatticeVeilMonoGame.Core;

public sealed class VoxelWorld
{
    private static readonly Regex ChunkNameRegex = new(@"chunk_(?<x>-?\d+)_(?<y>-?\d+)_(?<z>-?\d+)\.bin", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly Dictionary<ChunkCoord, VoxelChunkData> _chunks = new();
    private readonly object _chunksLock = new();
    private readonly Logger _log;
    private readonly int _maxChunkY;

    public WorldMeta Meta { get; }
    public string WorldPath { get; }
    public string ChunksDir { get; }

    private VoxelWorld(WorldMeta meta, string worldPath, Logger log)
    {
        Meta = meta;
        WorldPath = worldPath;
        ChunksDir = Path.Combine(worldPath, "chunks");
        _log = log;
        var height = Math.Max(1, Meta.Size.Height);
        _maxChunkY = (height - 1) / VoxelChunkData.ChunkSizeY;
    }

    public static VoxelWorld? Load(string worldPath, string metaPath, Logger log)
    {
        var meta = WorldMeta.Load(metaPath, log);
        if (meta == null)
            return null;

        var world = new VoxelWorld(meta, worldPath, log);
        world.LoadChunks();
        return world;
    }

    public List<VoxelChunkData> AllChunks() 
    { 
        lock (_chunksLock) 
            return _chunks.Values.ToList(); 
    }

    public bool TryGetChunk(ChunkCoord coord, out VoxelChunkData? chunk) 
    { 
        lock (_chunksLock) 
            return _chunks.TryGetValue(coord, out chunk); 
    }

    public void AddChunkDirect(ChunkCoord coord, VoxelChunkData chunk)
    {
        lock (_chunksLock)
            _chunks[coord] = chunk;
    }

    public int MaxChunkY => _maxChunkY;

    public byte GetBlock(int wx, int wy, int wz)
    {
        if (!IsWithinWorld(wx, wy, wz))
            return BlockIds.Air;

        var coord = WorldToChunk(wx, wy, wz, out var lx, out var ly, out var lz);
        if (!IsChunkYInRange(coord.Y))
            return BlockIds.Air;

        var chunk = GetOrCreateChunk(coord);
        return chunk.GetLocal(lx, ly, lz);
    }

    public bool SetBlock(int wx, int wy, int wz, byte id)
    {
        if (!IsWithinWorld(wx, wy, wz))
            return false;

        var coord = WorldToChunk(wx, wy, wz, out var lx, out var ly, out var lz);
        if (!IsChunkYInRange(coord.Y))
            return false;

        var chunk = GetOrCreateChunk(coord);
        chunk.SetLocal(lx, ly, lz, id);
        MarkNeighborDirty(coord, lx, ly, lz);
        return true;
    }

    private void MarkNeighborDirty(ChunkCoord coord, int lx, int ly, int lz)
    {
        if (lx == 0) MarkDirty(new ChunkCoord(coord.X - 1, coord.Y, coord.Z));
        if (lx == VoxelChunkData.ChunkSizeX - 1) MarkDirty(new ChunkCoord(coord.X + 1, coord.Y, coord.Z));
        if (ly == 0) MarkDirty(new ChunkCoord(coord.X, coord.Y - 1, coord.Z));
        if (ly == VoxelChunkData.ChunkSizeY - 1) MarkDirty(new ChunkCoord(coord.X, coord.Y + 1, coord.Z));
        if (lz == 0) MarkDirty(new ChunkCoord(coord.X, coord.Y, coord.Z - 1));
        if (lz == VoxelChunkData.ChunkSizeZ - 1) MarkDirty(new ChunkCoord(coord.X, coord.Y, coord.Z + 1));
    }

    private void MarkDirty(ChunkCoord coord)
    {
        lock (_chunksLock)
        {
            if (_chunks.TryGetValue(coord, out var chunk))
                chunk.IsDirty = true;
        }
    }

    public void MarkAllChunksDirty()
    {
        lock (_chunksLock)
        {
            foreach (var chunk in _chunks.Values)
                chunk.IsDirty = true;
        }
    }

    public void SaveModifiedChunks()
    {
        if (!Directory.Exists(ChunksDir))
            Directory.CreateDirectory(ChunksDir);

        lock (_chunksLock)
        {
            foreach (var chunk in _chunks.Values)
            {
                if (chunk.NeedsSave)
                {
                    var path = Path.Combine(ChunksDir, $"chunk_{chunk.Coord.X}_{chunk.Coord.Y}_{chunk.Coord.Z}.bin");
                    chunk.Save(path);
                }
            }
        }
    }

    public void SaveChunk(ChunkCoord coord)
    {
        VoxelChunkData? chunk;
        lock (_chunksLock)
        {
            if (!_chunks.TryGetValue(coord, out chunk))
                return;
        }
        
        var path = Path.Combine(ChunksDir, $"chunk_{coord.X}_{coord.Y}_{coord.Z}.bin");
        chunk.Save(path);
    }

    public bool IsWithinWorld(int wx, int wy, int wz)
    {
        if (wy < 0)
            return false;
        return wy < Meta.Size.Height;
    }

    public VoxelChunkData GetOrCreateChunk(ChunkCoord coord)
    {
        lock (_chunksLock)
        {
            if (_chunks.TryGetValue(coord, out var existing))
                return existing;
        }

        var loaded = TryLoadChunk(coord);
        if (loaded != null)
        {
            lock (_chunksLock)
                _chunks[coord] = loaded;
            return loaded;
        }

        var chunk = new VoxelChunkData(coord);
        VoxelWorldGenerator.GenerateChunk(Meta, coord, chunk);
        chunk.IsDirty = true;
        
        lock (_chunksLock)
            _chunks[coord] = chunk;
        return chunk;
    }

    public void UnloadChunks(HashSet<ChunkCoord> keep, Action<ChunkCoord, byte[]>? onSaveRequest = null)
    {
        List<(ChunkCoord coord, byte[]? blocks)> toSave;
        List<ChunkCoord> toRemove;
        
        lock (_chunksLock)
        {
            // Create snapshot of keys to avoid modification during enumeration
            var keys = _chunks.Keys.ToArray();
            toRemove = new List<ChunkCoord>();
            
            foreach (var coord in keys)
            {
                if (!keep.Contains(coord))
                    toRemove.Add(coord);
            }

            // Collect save data while still under lock
            toSave = new List<(ChunkCoord, byte[]?)>();
            foreach (var coord in toRemove)
            {
                if (_chunks.TryGetValue(coord, out var chunk) && chunk.NeedsSave)
                {
                    // Create a copy of blocks for async save
                    var blocksCopy = new byte[chunk.Blocks.Length];
                    Array.Copy(chunk.Blocks, blocksCopy, blocksCopy.Length);
                    toSave.Add((coord, blocksCopy));
                }
            }

            // Remove chunks from memory while still under lock
            foreach (var coord in toRemove)
                _chunks.Remove(coord);
        }

        // Call save requests OUTSIDE the lock (prefer pattern A)
        if (onSaveRequest != null)
        {
            foreach (var (coord, blocks) in toSave)
            {
                if (blocks != null)
                    onSaveRequest(coord, blocks);
            }
        }
    }

    public static ChunkCoord WorldToChunk(int wx, int wy, int wz, out int lx, out int ly, out int lz)
    {
        var cx = FloorDiv(wx, VoxelChunkData.ChunkSizeX);
        var cy = FloorDiv(wy, VoxelChunkData.ChunkSizeY);
        var cz = FloorDiv(wz, VoxelChunkData.ChunkSizeZ);

        lx = Mod(wx, VoxelChunkData.ChunkSizeX);
        ly = Mod(wy, VoxelChunkData.ChunkSizeY);
        lz = Mod(wz, VoxelChunkData.ChunkSizeZ);

        return new ChunkCoord(cx, cy, cz);
    }

    private void LoadChunks()
    {
        if (!Directory.Exists(ChunksDir))
        {
            _log.Warn($"Chunks folder missing: {ChunksDir}");
            return;
        }

        var files = Directory.GetFiles(ChunksDir, "chunk_*.bin");
        foreach (var file in files)
        {
            var fileName = Path.GetFileName(file);
            ChunkCoord? coord = null;
            if (fileName != null)
                coord = TryParseCoord(fileName);

            var chunk = VoxelChunkData.Load(file, _log, coord);
            if (chunk == null)
                continue;

            if (_chunks.ContainsKey(chunk.Coord))
            {
                _log.Warn($"Duplicate chunk at {chunk.Coord} in {file}");
                continue;
            }

            _chunks[chunk.Coord] = chunk;
        }

        if (_chunks.Count == 0)
            _log.Warn($"No chunks loaded from {ChunksDir}");
    }

    private VoxelChunkData? TryLoadChunk(ChunkCoord coord)
    {
        var path = Path.Combine(ChunksDir, $"chunk_{coord.X}_{coord.Y}_{coord.Z}.bin");
        if (!File.Exists(path))
            return null;
        return VoxelChunkData.Load(path, _log, coord);
    }

    private bool IsChunkYInRange(int cy) => cy >= 0 && cy <= _maxChunkY;

    private static ChunkCoord? TryParseCoord(string fileName)
    {
        var match = ChunkNameRegex.Match(fileName);
        if (!match.Success)
            return null;

        if (!int.TryParse(match.Groups["x"].Value, out var x))
            return null;
        if (!int.TryParse(match.Groups["y"].Value, out var y))
            return null;
        if (!int.TryParse(match.Groups["z"].Value, out var z))
            return null;

        return new ChunkCoord(x, y, z);
    }

    private static int FloorDiv(int value, int divisor)
    {
        var q = value / divisor;
        var r = value % divisor;
        if (r != 0 && ((r > 0) != (divisor > 0)))
            q--;
        return q;
    }

    private static int Mod(int value, int divisor)
    {
        var m = value % divisor;
        if (m < 0)
            m += Math.Abs(divisor);
        return m;
    }
}
