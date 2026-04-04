using System;
using System.Collections.Generic;
using System.IO;

namespace LatticeVeilMonoGame.Core;

public readonly struct BiomeIndexPoint
{
    public BiomeIndexPoint(int x, int z)
    {
        X = x;
        Z = z;
    }

    public int X { get; }
    public int Z { get; }
}

public sealed class BiomeIndexStore
{
    private const int Magic = 0x4956424C; // LVBI
    private const int FormatVersion = 3;
    public const string FileName = "biome_index.lvbi";

    private readonly Dictionary<BiomeId, List<BiomeIndexPoint>> _points;
    private readonly Dictionary<BiomeId, BiomeIndexPoint> _anchors;

    public BiomeIndexStore(
        int seed,
        int width,
        int depth,
        int stride,
        Dictionary<BiomeId, List<BiomeIndexPoint>> points,
        Dictionary<BiomeId, BiomeIndexPoint> anchors)
    {
        Seed = seed;
        Width = Math.Max(1, width);
        Depth = Math.Max(1, depth);
        Stride = Math.Clamp(stride, 1, 1024);
        _points = points ?? new Dictionary<BiomeId, List<BiomeIndexPoint>>();
        _anchors = anchors ?? new Dictionary<BiomeId, BiomeIndexPoint>();
    }

    public int Seed { get; }
    public int Width { get; }
    public int Depth { get; }
    public int Stride { get; }

    public static string GetPath(string worldPath) => Path.Combine(worldPath, FileName);

    public bool IsCompatible(WorldMeta? meta)
    {
        if (meta?.Size == null)
            return false;

        if (!meta.HasFiniteWorldBounds())
            return Seed == meta.Seed;

        return Seed == meta.Seed
            && Width == Math.Max(1, meta.Size.Width)
            && Depth == Math.Max(1, meta.Size.Depth);
    }

    public IReadOnlyList<BiomeIndexPoint> GetPoints(BiomeId biome)
    {
        return _points.TryGetValue(biome, out var list)
            ? list
            : Array.Empty<BiomeIndexPoint>();
    }

    public bool TryGetAnchor(BiomeId biome, out BiomeIndexPoint point)
    {
        return _anchors.TryGetValue(biome, out point);
    }

    public bool TryFindNearest(BiomeId biome, int originX, int originZ, int maxRadius, out BiomeIndexPoint point, out float distance)
    {
        point = default;
        distance = 0f;

        if (!_points.TryGetValue(biome, out var list) || list.Count == 0)
            return false;

        var boundedRadius = maxRadius > 0 ? maxRadius : int.MaxValue;
        var maxDistSq = boundedRadius == int.MaxValue
            ? long.MaxValue
            : (long)boundedRadius * boundedRadius;

        var bestDistSq = long.MaxValue;
        var found = false;
        for (var i = 0; i < list.Count; i++)
        {
            var p = list[i];
            var dx = p.X - originX;
            var dz = p.Z - originZ;
            var distSq = (long)dx * dx + (long)dz * dz;
            if (distSq > maxDistSq || distSq >= bestDistSq)
                continue;

            point = p;
            bestDistSq = distSq;
            found = true;
        }

        if (!found)
            return false;

        distance = MathF.Sqrt(bestDistSq);
        return true;
    }

    public bool Save(string worldPath, Logger log)
    {
        try
        {
            var path = GetPath(worldPath);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);

            using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
            using var bw = new BinaryWriter(fs);

            bw.Write(Magic);
            bw.Write(FormatVersion);
            bw.Write(Seed);
            bw.Write(Width);
            bw.Write(Depth);
            bw.Write(Stride);
            WriteBiome(bw, BiomeId.Grasslands);
            WriteBiome(bw, BiomeId.Desert);
            WriteBiome(bw, BiomeId.Ocean);
            WriteBiome(bw, BiomeId.Forest);
            WriteBiome(bw, BiomeId.Hills);
            return true;
        }
        catch (Exception ex)
        {
            log.Warn($"Failed to save biome index: {ex.Message}");
            return false;
        }
    }

    public static BiomeIndexStore? Load(string worldPath, Logger log)
    {
        try
        {
            var path = GetPath(worldPath);
            if (!File.Exists(path))
                return null;

            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var br = new BinaryReader(fs);

            if (br.ReadInt32() != Magic)
                return null;
            if (br.ReadInt32() != FormatVersion)
                return null;

            var seed = br.ReadInt32();
            var width = br.ReadInt32();
            var depth = br.ReadInt32();
            var stride = br.ReadInt32();

            var points = new Dictionary<BiomeId, List<BiomeIndexPoint>>(5);
            var anchors = new Dictionary<BiomeId, BiomeIndexPoint>(5);

            ReadBiome(br, BiomeId.Grasslands, points, anchors);
            ReadBiome(br, BiomeId.Desert, points, anchors);
            ReadBiome(br, BiomeId.Ocean, points, anchors);
            ReadBiome(br, BiomeId.Forest, points, anchors);
            ReadBiome(br, BiomeId.Hills, points, anchors);

            return new BiomeIndexStore(seed, width, depth, stride, points, anchors);
        }
        catch (Exception ex)
        {
            log.Warn($"Failed to load biome index: {ex.Message}");
            return null;
        }
    }

    private void WriteBiome(BinaryWriter bw, BiomeId biome)
    {
        if (_anchors.TryGetValue(biome, out var anchor))
        {
            bw.Write(anchor.X);
            bw.Write(anchor.Z);
        }
        else
        {
            bw.Write(-1);
            bw.Write(-1);
        }

        if (_points.TryGetValue(biome, out var list))
        {
            bw.Write(list.Count);
            for (var i = 0; i < list.Count; i++)
            {
                bw.Write(list[i].X);
                bw.Write(list[i].Z);
            }
        }
        else
        {
            bw.Write(0);
        }
    }

    private static void ReadBiome(
        BinaryReader br,
        BiomeId biome,
        Dictionary<BiomeId, List<BiomeIndexPoint>> points,
        Dictionary<BiomeId, BiomeIndexPoint> anchors)
    {
        var anchorX = br.ReadInt32();
        var anchorZ = br.ReadInt32();
        if (anchorX >= 0 && anchorZ >= 0)
            anchors[biome] = new BiomeIndexPoint(anchorX, anchorZ);

        var count = Math.Max(0, br.ReadInt32());
        var list = new List<BiomeIndexPoint>(count);
        for (var i = 0; i < count; i++)
        {
            var x = br.ReadInt32();
            var z = br.ReadInt32();
            list.Add(new BiomeIndexPoint(x, z));
        }

        points[biome] = list;
    }
}
