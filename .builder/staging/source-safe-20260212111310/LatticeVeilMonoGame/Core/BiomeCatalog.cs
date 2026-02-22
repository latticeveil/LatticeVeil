using System;
using System.Collections.Generic;
using System.IO;

namespace LatticeVeilMonoGame.Core;

public enum BiomeId : byte
{
    Unknown = 0,
    Grasslands = 1,
    Desert = 2,
    Ocean = 3
}

public readonly struct BiomeCatalogPoint
{
    public BiomeCatalogPoint(int x, int z)
    {
        X = x;
        Z = z;
    }

    public int X { get; }
    public int Z { get; }
}

public sealed class BiomeCatalog
{
    private const int FormatVersion = 5;
    public const string FileName = "biome_catalog.bin";
    public const int DefaultStride = 8;
    private const int Magic = 0x4C56424D; // LVBM

    private readonly List<BiomeCatalogPoint> _grasslands;
    private readonly List<BiomeCatalogPoint> _desert;
    private readonly List<BiomeCatalogPoint> _ocean;

    private BiomeCatalog(
        int seed,
        int width,
        int depth,
        int stride,
        List<BiomeCatalogPoint> grasslands,
        List<BiomeCatalogPoint> desert,
        List<BiomeCatalogPoint> ocean)
    {
        Seed = seed;
        Width = width;
        Depth = depth;
        Stride = Math.Max(1, stride);
        _grasslands = grasslands ?? new List<BiomeCatalogPoint>();
        _desert = desert ?? new List<BiomeCatalogPoint>();
        _ocean = ocean ?? new List<BiomeCatalogPoint>();
    }

    public int Seed { get; }
    public int Width { get; }
    public int Depth { get; }
    public int Stride { get; }

    public static string GetPath(string worldPath) => Path.Combine(worldPath, FileName);

    public bool IsCompatible(WorldMeta meta)
    {
        if (meta == null || meta.Size == null)
            return false;

        return Seed == meta.Seed
            && Width == meta.Size.Width
            && Depth == meta.Size.Depth
            && Stride >= 1;
    }

    public IReadOnlyList<BiomeCatalogPoint> GetPoints(BiomeId biome)
    {
        return biome switch
        {
            BiomeId.Desert => _desert,
            BiomeId.Ocean => _ocean,
            BiomeId.Grasslands => _grasslands,
            _ => Array.Empty<BiomeCatalogPoint>()
        };
    }

    public bool TryFindNearest(BiomeId biome, int originX, int originZ, int maxRadius, out int foundX, out int foundZ, out float distance)
    {
        foundX = originX;
        foundZ = originZ;
        distance = 0f;
        var points = GetPoints(biome);
        if (points.Count == 0)
            return false;

        var maxDistSq = (long)maxRadius * maxRadius;
        var bestDistSq = long.MaxValue;
        var found = false;
        for (var i = 0; i < points.Count; i++)
        {
            var point = points[i];
            var dx = point.X - originX;
            var dz = point.Z - originZ;
            var distSq = (long)dx * dx + (long)dz * dz;
            if (distSq > maxDistSq || distSq >= bestDistSq)
                continue;

            bestDistSq = distSq;
            foundX = point.X;
            foundZ = point.Z;
            found = true;
        }

        if (!found)
            return false;

        distance = MathF.Sqrt(bestDistSq);
        return true;
    }

    public static BiomeCatalog Build(WorldMeta meta, string worldPath, Logger log, int stride = DefaultStride)
    {
        if (meta == null)
            throw new ArgumentNullException(nameof(meta));

        stride = Math.Clamp(stride, 2, 64);
        var width = Math.Max(1, meta.Size?.Width ?? 512);
        var depth = Math.Max(1, meta.Size?.Depth ?? 512);
        var world = new VoxelWorld(meta, worldPath, log);

        var grasslands = new List<BiomeCatalogPoint>();
        var desert = new List<BiomeCatalogPoint>();
        var ocean = new List<BiomeCatalogPoint>();

        foreach (var z in EnumerateSampleAxis(depth, stride))
        {
            foreach (var x in EnumerateSampleAxis(width, stride))
            {
                var biome = NameToBiomeId(world.GetBiomeNameAt(x, z));
                var point = new BiomeCatalogPoint(x, z);
                switch (biome)
                {
                    case BiomeId.Desert:
                        desert.Add(point);
                        break;
                    case BiomeId.Ocean:
                        ocean.Add(point);
                        break;
                    default:
                        grasslands.Add(point);
                        break;
                }
            }
        }

        // Ensure each biome has at least one deterministic fallback point so lookup never fails
        // purely because coarse sampling missed a sparse region in this world.
        EnsureMinimumBiomePoint(world, BiomeId.Grasslands, width, depth, stride, grasslands);
        EnsureMinimumBiomePoint(world, BiomeId.Desert, width, depth, stride, desert);
        EnsureMinimumBiomePoint(world, BiomeId.Ocean, width, depth, stride, ocean);

        return new BiomeCatalog(meta.Seed, width, depth, stride, grasslands, desert, ocean);
    }

    public static BiomeCatalog BuildAndSave(WorldMeta meta, string worldPath, Logger log, int stride = DefaultStride)
    {
        var catalog = Build(meta, worldPath, log, stride);
        catalog.Save(worldPath, log);
        return catalog;
    }

    public bool Save(string worldPath, Logger log)
    {
        try
        {
            Directory.CreateDirectory(worldPath);
            var path = GetPath(worldPath);
            using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
            using var bw = new BinaryWriter(fs);

            bw.Write(Magic);
            bw.Write(FormatVersion);
            bw.Write(Seed);
            bw.Write(Width);
            bw.Write(Depth);
            bw.Write(Stride);

            WritePoints(bw, _grasslands);
            WritePoints(bw, _desert);
            WritePoints(bw, _ocean);
            return true;
        }
        catch (Exception ex)
        {
            log.Warn($"Failed to save biome catalog: {ex.Message}");
            return false;
        }
    }

    public static BiomeCatalog? Load(string worldPath, Logger log)
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

            var version = br.ReadInt32();
            if (version != FormatVersion)
                return null;

            var seed = br.ReadInt32();
            var width = br.ReadInt32();
            var depth = br.ReadInt32();
            var stride = br.ReadInt32();

            var grasslands = ReadPoints(br);
            var desert = ReadPoints(br);
            var ocean = ReadPoints(br);

            return new BiomeCatalog(seed, width, depth, stride, grasslands, desert, ocean);
        }
        catch (Exception ex)
        {
            log.Warn($"Failed to load biome catalog: {ex.Message}");
            return null;
        }
    }

    public static bool TryParseBiomeToken(string token, out BiomeId biome)
    {
        biome = BiomeId.Unknown;
        if (string.IsNullOrWhiteSpace(token))
            return false;

        switch (token.Trim().ToLowerInvariant())
        {
            case "grasslands":
            case "grassland":
            case "plains":
            case "grassy":
            case "grass":
                biome = BiomeId.Grasslands;
                return true;
            case "desert":
                biome = BiomeId.Desert;
                return true;
            case "ocean":
            case "sea":
            case "water":
                biome = BiomeId.Ocean;
                return true;
            default:
                return false;
        }
    }

    private static BiomeId NameToBiomeId(string name)
    {
        return name.Trim().ToLowerInvariant() switch
        {
            "desert" => BiomeId.Desert,
            "ocean" => BiomeId.Ocean,
            _ => BiomeId.Grasslands
        };
    }

    private static IEnumerable<int> EnumerateSampleAxis(int size, int stride)
    {
        if (size <= 0)
            yield break;

        var value = 0;
        while (value < size)
        {
            yield return value;
            value += stride;
        }

        var edge = size - 1;
        if (edge >= 0 && (edge % stride != 0))
            yield return edge;
    }

    private static void WritePoints(BinaryWriter bw, List<BiomeCatalogPoint> points)
    {
        bw.Write(points.Count);
        for (var i = 0; i < points.Count; i++)
        {
            bw.Write(points[i].X);
            bw.Write(points[i].Z);
        }
    }

    private static List<BiomeCatalogPoint> ReadPoints(BinaryReader br)
    {
        var count = Math.Max(0, br.ReadInt32());
        var points = new List<BiomeCatalogPoint>(count);
        for (var i = 0; i < count; i++)
        {
            var x = br.ReadInt32();
            var z = br.ReadInt32();
            points.Add(new BiomeCatalogPoint(x, z));
        }

        return points;
    }

    private static void EnsureMinimumBiomePoint(
        VoxelWorld world,
        BiomeId biome,
        int width,
        int depth,
        int stride,
        List<BiomeCatalogPoint> points)
    {
        if (points.Count > 0 || width <= 0 || depth <= 0)
            return;

        if (TryFindExactBiomePoint(world, biome, width, depth, stride, out var exact))
        {
            points.Add(exact);
            return;
        }

        // Keep empty when no exact coordinate exists in the world. The caller will rebuild
        // catalogs after biome distribution changes, and command layer has additional fallbacks.
    }

    private static bool TryFindExactBiomePoint(
        VoxelWorld world,
        BiomeId biome,
        int width,
        int depth,
        int stride,
        out BiomeCatalogPoint point)
    {
        point = default;
        var foundPoint = default(BiomeCatalogPoint);
        var maxX = Math.Max(0, width - 1);
        var maxZ = Math.Max(0, depth - 1);
        if (maxX <= 0 || maxZ <= 0)
            return false;

        var targetName = BiomeNameFromId(biome);
        if (string.IsNullOrWhiteSpace(targetName))
            return false;

        bool IsMatch(int x, int z)
        {
            if (x < 0 || z < 0 || x > maxX || z > maxZ)
                return false;
            return string.Equals(world.GetBiomeNameAt(x, z), targetName, StringComparison.OrdinalIgnoreCase);
        }

        bool TryStride(int sampleStride)
        {
            sampleStride = Math.Max(1, sampleStride);
            for (var z = 0; z <= maxZ; z += sampleStride)
            {
                for (var x = 0; x <= maxX; x += sampleStride)
                {
                    if (!IsMatch(x, z))
                        continue;
                    foundPoint = new BiomeCatalogPoint(x, z);
                    return true;
                }

                if (IsMatch(maxX, z))
                {
                    foundPoint = new BiomeCatalogPoint(maxX, z);
                    return true;
                }
            }

            for (var x = 0; x <= maxX; x += sampleStride)
            {
                if (!IsMatch(x, maxZ))
                    continue;
                foundPoint = new BiomeCatalogPoint(x, maxZ);
                return true;
            }

            if (IsMatch(maxX, maxZ))
            {
                foundPoint = new BiomeCatalogPoint(maxX, maxZ);
                return true;
            }

            return false;
        }

        var candidateStrides = new[]
        {
            Math.Max(1, stride),
            Math.Max(1, stride / 2),
            16,
            8,
            4,
            2,
            1
        };
        for (var i = 0; i < candidateStrides.Length; i++)
        {
            if (TryStride(candidateStrides[i]))
            {
                point = foundPoint;
                return true;
            }
        }

        if (TryFindBestClimateCandidate(world, biome, width, depth, stride, out var climatePoint))
        {
            var refineWindow = Math.Max(6, stride);
            for (var z = climatePoint.Z - refineWindow; z <= climatePoint.Z + refineWindow; z++)
            {
                for (var x = climatePoint.X - refineWindow; x <= climatePoint.X + refineWindow; x++)
                {
                    if (!IsMatch(x, z))
                        continue;
                    point = new BiomeCatalogPoint(x, z);
                    return true;
                }
            }
        }

        return false;
    }

    private static bool TryFindBestClimateCandidate(
        VoxelWorld world,
        BiomeId biome,
        int width,
        int depth,
        int stride,
        out BiomeCatalogPoint point)
    {
        point = default;
        var maxX = Math.Max(0, width - 1);
        var maxZ = Math.Max(0, depth - 1);
        var sampleStride = Math.Max(4, stride);
        var bestX = maxX / 2;
        var bestZ = maxZ / 2;
        var bestScore = float.NegativeInfinity;
        var bestDistSq = long.MaxValue;
        var centerX = maxX / 2;
        var centerZ = maxZ / 2;

        void Evaluate(int x, int z)
        {
            if (x < 0 || z < 0 || x > maxX || z > maxZ)
                return;

            var ocean = world.GetOceanWeightAt(x, z);
            var desert = world.GetDesertWeightAt(x, z);
            var score = biome switch
            {
                BiomeId.Ocean => ocean,
                BiomeId.Desert => desert * (1f - ocean * 0.9f),
                _ => Math.Clamp(1f - MathF.Max(ocean, desert), 0f, 1f)
            };

            var dx = x - centerX;
            var dz = z - centerZ;
            var distSq = (long)dx * dx + (long)dz * dz;
            if (score < bestScore)
                return;
            if (MathF.Abs(score - bestScore) < 0.0001f && distSq >= bestDistSq)
                return;

            bestScore = score;
            bestDistSq = distSq;
            bestX = x;
            bestZ = z;
        }

        foreach (var z in EnumerateSampleAxis(depth, sampleStride))
        {
            foreach (var x in EnumerateSampleAxis(width, sampleStride))
                Evaluate(x, z);
        }

        Evaluate(centerX, centerZ);
        Evaluate(0, 0);
        Evaluate(maxX, 0);
        Evaluate(0, maxZ);
        Evaluate(maxX, maxZ);

        if (float.IsNegativeInfinity(bestScore))
            return false;

        point = new BiomeCatalogPoint(bestX, bestZ);
        return true;
    }

    private static string BiomeNameFromId(BiomeId biome)
    {
        return biome switch
        {
            BiomeId.Desert => "Desert",
            BiomeId.Ocean => "Ocean",
            BiomeId.Grasslands => "Grasslands",
            _ => string.Empty
        };
    }

}
