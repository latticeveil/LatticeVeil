using System;
using System.IO;

namespace RedactedCraftMonoGame.Core;

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
    private readonly int _chunkVolume;
    private int _currentChunkIndex;
    private int _localIndex;
    private int _blocksGenerated;
    private VoxelChunkData? _currentChunk;

    public int TotalBlocks { get; }
    public bool IsComplete { get; private set; }
    public string? OutputPath { get; private set; }

    public float Progress => TotalBlocks == 0 ? 1f : _blocksGenerated / (float)TotalBlocks;

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
        _chunkVolume = VoxelChunkData.BlockCount;

        TotalBlocks = _worldWidth * _worldHeight * _worldDepth;
    }

    public static void GenerateChunk(WorldMeta meta, ChunkCoord coord, VoxelChunkData chunk)
    {
        var height = Math.Max(1, meta.Size.Height);
        var sea = Math.Max(3, height / 3);
        for (var ly = 0; ly < VoxelChunkData.ChunkSizeY; ly++)
        {
            var wy = coord.Y * VoxelChunkData.ChunkSizeY + ly;
            if (wy < 0 || wy >= height)
                continue;

            for (var lz = 0; lz < VoxelChunkData.ChunkSizeZ; lz++)
            {
                var wz = coord.Z * VoxelChunkData.ChunkSizeZ + lz;
                for (var lx = 0; lx < VoxelChunkData.ChunkSizeX; lx++)
                {
                    var wx = coord.X * VoxelChunkData.ChunkSizeX + lx;
                    var surface = GetHeight(wx, wz, meta.Seed, height);
                    byte id = BlockIds.Air;
                    if (wy < surface)
                    {
                        if (wy == surface - 1)
                            id = BlockIds.Grass;
                        else if (wy >= surface - 4)
                            id = BlockIds.Dirt;
                        else
                        {
                            id = BlockIds.Stone;
                            var oreRoll = HashToUnit(wx, wy ^ wz, meta.Seed + 999);
                            if (oreRoll < 0.02f)
                                id = BlockIds.Coal;
                            else if (oreRoll < 0.03f)
                                id = BlockIds.Iron;
                        }

                        if (wy < sea - 2 && id == BlockIds.Grass)
                            id = BlockIds.Sand;
                    }

                    if (wy == 0)
                        id = BlockIds.Nullblock;

                    chunk.SetLocal(lx, ly, lz, id, markDirty: false);
                }
            }
        }
    }

    public void Step(int blocksPerStep)
    {
        if (IsComplete || blocksPerStep <= 0)
            return;

        if (_currentChunk == null)
            BeginChunk();

        var remaining = blocksPerStep;
        while (remaining > 0 && !IsComplete)
        {
            if (_currentChunk == null)
                BeginChunk();

            var toProcess = Math.Min(remaining, _chunkVolume - _localIndex);
            for (var i = 0; i < toProcess; i++)
            {
                var index = _localIndex++;
                var lx = index % VoxelChunkData.ChunkSizeX;
                var lz = (index / VoxelChunkData.ChunkSizeX) % VoxelChunkData.ChunkSizeZ;
                var ly = index / (VoxelChunkData.ChunkSizeX * VoxelChunkData.ChunkSizeZ);

                var wx = _currentChunk!.Coord.X * VoxelChunkData.ChunkSizeX + lx;
                var wy = _currentChunk.Coord.Y * VoxelChunkData.ChunkSizeY + ly;
                var wz = _currentChunk.Coord.Z * VoxelChunkData.ChunkSizeZ + lz;

                byte id = BlockIds.Air;
                if (wx < _worldWidth && wy < _worldHeight && wz < _worldDepth)
                {
                    var surface = GetHeight(wx, wz, _meta.Seed, _worldHeight);
                    if (wy < surface)
                    {
                        if (wy == surface - 1)
                            id = BlockIds.Grass;
                        else if (wy >= surface - 4)
                            id = BlockIds.Dirt;
                        else
                        {
                            id = BlockIds.Stone;
                            var oreRoll = HashToUnit(wx, wy ^ wz, _meta.Seed + 999);
                            if (oreRoll < 0.02f)
                                id = BlockIds.Coal;
                            else if (oreRoll < 0.03f)
                                id = BlockIds.Iron;
                        }
                    }
                    _blocksGenerated++;
                }

                if (wy == 0)
                    id = BlockIds.Nullblock;

                _currentChunk.SetLocal(lx, ly, lz, id, markDirty: false);
            }

            remaining -= toProcess;

            if (_localIndex >= _chunkVolume)
            {
                SaveChunk(_currentChunk!);
                _currentChunk = null;
                _localIndex = 0;
                _currentChunkIndex++;

                if (_currentChunkIndex >= _chunksX * _chunksY * _chunksZ)
                {
                    IsComplete = true;
                    _blocksGenerated = TotalBlocks;
                    return;
                }
            }
        }
    }

    private void BeginChunk()
    {
        if (_currentChunkIndex >= _chunksX * _chunksY * _chunksZ)
        {
            IsComplete = true;
            return;
        }

        var coord = GetChunkCoord(_currentChunkIndex);
        _currentChunk = new VoxelChunkData(coord);
        _localIndex = 0;
    }

    private void SaveChunk(VoxelChunkData chunk)
    {
        OutputPath = Path.Combine(_chunksDir, $"chunk_{chunk.Coord.X}_{chunk.Coord.Y}_{chunk.Coord.Z}.bin");
        chunk.Save(OutputPath);
    }

    private ChunkCoord GetChunkCoord(int index)
    {
        var cx = index % _chunksX;
        var cz = (index / _chunksX) % _chunksZ;
        var cy = index / (_chunksX * _chunksZ);
        return new ChunkCoord(cx, cy, cz);
    }

    private static int GetHeight(int wx, int wz, int seed, int worldHeight)
    {
        var baseH = Math.Max(4, worldHeight / 4);
        var amp = Math.Max(6, worldHeight / 6);

        var n1 = SampleNoise(wx, wz, seed, 64);
        var n2 = SampleNoise(wx, wz, seed + 1337, 24) * 0.5f;
        var n3 = SampleNoise(wx, wz, seed + 7331, 12) * 0.25f;
        var n = (n1 + n2 + n3) / (1f + 0.5f + 0.25f);

        var h = baseH + (int)Math.Round(n * amp);
        return Math.Clamp(h, 1, worldHeight - 1);
    }

    private static float SampleNoise(int wx, int wz, int seed, int scale)
    {
        var x0 = (int)Math.Floor(wx / (float)scale);
        var z0 = (int)Math.Floor(wz / (float)scale);
        var x1 = x0 + 1;
        var z1 = z0 + 1;

        var fx = (wx - x0 * scale) / (float)scale;
        var fz = (wz - z0 * scale) / (float)scale;

        var v00 = HashToUnit(x0, z0, seed);
        var v10 = HashToUnit(x1, z0, seed);
        var v01 = HashToUnit(x0, z1, seed);
        var v11 = HashToUnit(x1, z1, seed);

        var ix0 = Lerp(v00, v10, Smooth(fx));
        var ix1 = Lerp(v01, v11, Smooth(fx));
        return Lerp(ix0, ix1, Smooth(fz));
    }

    private static float HashToUnit(int x, int z, int seed)
    {
        unchecked
        {
            var h = seed;
            h = h * 374761393 + x * 668265263;
            h = h * 374761393 + z * 2147483647;
            h ^= h >> 13;
            h *= 1274126177;
            var v = h & 0x7fffffff;
            return v / (float)int.MaxValue;
        }
    }

    private static float Smooth(float t) => t * t * (3f - 2f * t);

    private static float Lerp(float a, float b, float t) => a + (b - a) * t;
}
