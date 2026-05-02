using System;
using System.Collections.Generic;

namespace LatticeVeilMonoGame.Core;

public static class BiomeLocateService
{
    private const int UnboundedCoverageRadius = 4096;
    private static readonly BiomeId[] RequiredBiomes =
    {
        BiomeId.Grasslands,
        BiomeId.Desert,
        BiomeId.Ocean,
        BiomeId.Forest,
        BiomeId.Hills
    };

    public static BiomeIndexStore BuildCoverageIndex(WorldMeta meta, int stride = 64, int pointsPerBiomeLimit = 4096)
    {
        if (meta?.Size == null)
            throw new ArgumentNullException(nameof(meta));

        var finiteBounds = meta.HasFiniteWorldBounds();
        var width = finiteBounds ? Math.Max(1, meta.Size.Width) : (UnboundedCoverageRadius * 2) + 1;
        var depth = finiteBounds ? Math.Max(1, meta.Size.Depth) : (UnboundedCoverageRadius * 2) + 1;
        var clampedStride = Math.Clamp(stride, 8, 256);
        var points = new Dictionary<BiomeId, List<BiomeIndexPoint>>(5)
        {
            [BiomeId.Grasslands] = new List<BiomeIndexPoint>(),
            [BiomeId.Desert] = new List<BiomeIndexPoint>(),
            [BiomeId.Ocean] = new List<BiomeIndexPoint>(),
            [BiomeId.Forest] = new List<BiomeIndexPoint>(),
            [BiomeId.Hills] = new List<BiomeIndexPoint>()
        };
        var anchors = new Dictionary<BiomeId, BiomeIndexPoint>(5);

        foreach (var z in EnumerateAxis(depth, clampedStride))
        {
            foreach (var x in EnumerateAxis(width, clampedStride))
            {
                var sampleX = finiteBounds ? x : x - UnboundedCoverageRadius;
                var sampleZ = finiteBounds ? z : z - UnboundedCoverageRadius;
                var biome = BiomeService.GetSampleAt(meta, sampleX, sampleZ).BiomeId;
                if (biome == BiomeId.Unknown)
                    biome = BiomeId.Grasslands;

                if (points.TryGetValue(biome, out var list) && list.Count < pointsPerBiomeLimit)
                    list.Add(new BiomeIndexPoint(sampleX, sampleZ));
            }
        }

        var centerX = finiteBounds ? width / 2 : 0;
        var centerZ = finiteBounds ? depth / 2 : 0;
        foreach (var biome in RequiredBiomes)
        {
            var anchor = FindAnchor(points[biome], centerX, centerZ);
            if (anchor.HasValue)
                anchors[biome] = anchor.Value;
        }

        return new BiomeIndexStore(meta.Seed, width, depth, clampedStride, points, anchors);
    }

    public static BiomeIndexStore BuildCoverageIndexWithReseed(WorldMeta meta, Logger log, int maxAttempts = 48, int stride = 64)
    {
        if (meta?.Size == null)
            throw new ArgumentNullException(nameof(meta));

        BiomeIndexStore? best = null;
        var bestCoverage = -1;
        var attempts = Math.Max(1, maxAttempts);
        var baseSeed = meta.Seed == 0 ? 1337 : meta.Seed;

        for (var attempt = 0; attempt < attempts; attempt++)
        {
            if (attempt == 0)
                meta.Seed = baseSeed;
            else
                meta.Seed = NextSeed(baseSeed, attempt);

            var candidate = BuildCoverageIndex(meta, stride);
            var coverage = CountCoveredBiomes(candidate);
            if (coverage > bestCoverage)
            {
                bestCoverage = coverage;
                best = candidate;
            }

            if (coverage == RequiredBiomes.Length)
            {
                log.Info($"Biome index coverage complete after {attempt + 1} attempt(s) (seed={meta.Seed}).");
                return candidate;
            }
        }

        if (best == null)
            throw new InvalidOperationException("Failed to build biome index.");

        // Safety net: synthesize anchor points for any missing biome from climate maxima.
        var normalized = EnsureAllBiomeAnchors(meta, best, log);
        meta.Seed = normalized.Seed;
        return normalized;
    }

    public static bool TryLocateNearest(
        WorldMeta meta,
        BiomeIndexStore? index,
        BiomeId target,
        int originX,
        int originZ,
        int maxRadius,
        out int foundX,
        out int foundZ)
    {
        foundX = originX;
        foundZ = originZ;

        if (meta?.Size == null)
            return false;

        if (meta.HasFiniteWorldBounds())
        {
            originX = Math.Clamp(originX, 0, Math.Max(0, meta.Size.Width - 1));
            originZ = Math.Clamp(originZ, 0, Math.Max(0, meta.Size.Depth - 1));
        }

        if (BiomeService.GetSampleAt(meta, originX, originZ).BiomeId == target)
        {
            foundX = originX;
            foundZ = originZ;
            return true;
        }

        if (index != null && index.IsCompatible(meta))
        {
            if (index.TryFindNearest(target, originX, originZ, Math.Max(16, maxRadius), out var nearest, out _)
                && TryRefine(meta, target, originX, originZ, nearest.X, nearest.Z, out foundX, out foundZ))
            {
                return true;
            }

            if (index.TryGetAnchor(target, out var anchor)
                && TryRefine(meta, target, originX, originZ, anchor.X, anchor.Z, out foundX, out foundZ))
            {
                return true;
            }
        }

        return TryGlobalFallback(meta, target, originX, originZ, maxRadius, out foundX, out foundZ);
    }

    public static bool TryGetAnchor(BiomeIndexStore? index, BiomeId biome, out BiomeIndexPoint point)
    {
        point = default;
        if (index == null)
            return false;
        return index.TryGetAnchor(biome, out point);
    }

    private static bool TryGlobalFallback(
        WorldMeta meta,
        BiomeId target,
        int originX,
        int originZ,
        int maxRadius,
        out int foundX,
        out int foundZ)
    {
        foundX = originX;
        foundZ = originZ;

        var bestDistSq = long.MaxValue;
        var bestX = originX;
        var bestZ = originZ;
        var found = false;
        var radiusSq = (long)Math.Max(16, maxRadius) * Math.Max(16, maxRadius);

        void Evaluate(int x, int z, bool ignoreRadius)
        {
            if (meta.HasFiniteWorldBounds() && (x < 0 || z < 0 || x >= meta.Size.Width || z >= meta.Size.Depth))
                return;
            if (BiomeService.GetSampleAt(meta, x, z).BiomeId != target)
                return;

            var dx = x - originX;
            var dz = z - originZ;
            var distSq = (long)dx * dx + (long)dz * dz;
            if (!ignoreRadius && distSq > radiusSq)
                return;
            if (distSq >= bestDistSq)
                return;

            bestDistSq = distSq;
            bestX = x;
            bestZ = z;
            found = true;
        }

        var stride = 32;
        if (meta.HasFiniteWorldBounds())
        {
            var maxX = Math.Max(0, meta.Size.Width - 1);
            var maxZ = Math.Max(0, meta.Size.Depth - 1);
            for (var z = 0; z <= maxZ; z += stride)
            {
                for (var x = 0; x <= maxX; x += stride)
                    Evaluate(x, z, ignoreRadius: false);
                Evaluate(maxX, z, ignoreRadius: false);
            }
            for (var x = 0; x <= maxX; x += stride)
                Evaluate(x, maxZ, ignoreRadius: false);
            Evaluate(maxX, maxZ, ignoreRadius: false);
        }
        else
        {
            var searchRadius = Math.Max(512, Math.Max(16, maxRadius));
            for (var z = originZ - searchRadius; z <= originZ + searchRadius; z += stride)
            {
                for (var x = originX - searchRadius; x <= originX + searchRadius; x += stride)
                    Evaluate(x, z, ignoreRadius: false);
            }
        }

        if (!found)
        {
            var minX = meta.HasFiniteWorldBounds() ? 0 : originX - Math.Max(1024, Math.Max(16, maxRadius) * 2);
            var maxX = meta.HasFiniteWorldBounds() ? Math.Max(0, meta.Size.Width - 1) : originX + Math.Max(1024, Math.Max(16, maxRadius) * 2);
            var minZ = meta.HasFiniteWorldBounds() ? 0 : originZ - Math.Max(1024, Math.Max(16, maxRadius) * 2);
            var maxZ = meta.HasFiniteWorldBounds() ? Math.Max(0, meta.Size.Depth - 1) : originZ + Math.Max(1024, Math.Max(16, maxRadius) * 2);
            for (var z = minZ; z <= maxZ; z += 8)
            {
                for (var x = minX; x <= maxX; x += 8)
                    Evaluate(x, z, ignoreRadius: true);
            }
        }

        if (!found)
            return false;

        return TryRefine(meta, target, originX, originZ, bestX, bestZ, out foundX, out foundZ);
    }

    private static bool TryRefine(
        WorldMeta meta,
        BiomeId target,
        int originX,
        int originZ,
        int centerX,
        int centerZ,
        out int foundX,
        out int foundZ)
    {
        foundX = centerX;
        foundZ = centerZ;

        var bestDistSq = long.MaxValue;
        var bestX = centerX;
        var bestZ = centerZ;
        var found = false;

        void Evaluate(int x, int z)
        {
            if (meta.HasFiniteWorldBounds() && (x < 0 || z < 0 || x >= meta.Size.Width || z >= meta.Size.Depth))
                return;
            if (BiomeService.GetSampleAt(meta, x, z).BiomeId != target)
                return;

            var dx = x - originX;
            var dz = z - originZ;
            var distSq = (long)dx * dx + (long)dz * dz;
            if (distSq >= bestDistSq)
                return;

            bestDistSq = distSq;
            bestX = x;
            bestZ = z;
            found = true;
        }

        for (var z = centerZ - 96; z <= centerZ + 96; z += 4)
        {
            for (var x = centerX - 96; x <= centerX + 96; x += 4)
                Evaluate(x, z);
        }

        if (!found)
            return false;

        var coarseX = bestX;
        var coarseZ = bestZ;
        for (var z = coarseZ - 8; z <= coarseZ + 8; z++)
        {
            for (var x = coarseX - 8; x <= coarseX + 8; x++)
                Evaluate(x, z);
        }

        foundX = bestX;
        foundZ = bestZ;

        return true;
    }

    private static BiomeIndexStore EnsureAllBiomeAnchors(WorldMeta meta, BiomeIndexStore index, Logger log)
    {
        var points = new Dictionary<BiomeId, List<BiomeIndexPoint>>(5)
        {
            [BiomeId.Grasslands] = new List<BiomeIndexPoint>(index.GetPoints(BiomeId.Grasslands)),
            [BiomeId.Desert] = new List<BiomeIndexPoint>(index.GetPoints(BiomeId.Desert)),
            [BiomeId.Ocean] = new List<BiomeIndexPoint>(index.GetPoints(BiomeId.Ocean)),
            [BiomeId.Forest] = new List<BiomeIndexPoint>(index.GetPoints(BiomeId.Forest)),
            [BiomeId.Hills] = new List<BiomeIndexPoint>(index.GetPoints(BiomeId.Hills))
        };
        var anchors = new Dictionary<BiomeId, BiomeIndexPoint>(5);
        foreach (var biome in RequiredBiomes)
        {
            if (index.TryGetAnchor(biome, out var anchor))
                anchors[biome] = anchor;
        }

        foreach (var biome in RequiredBiomes)
        {
            if (anchors.ContainsKey(biome))
                continue;
            if (TryFindClimateMaximum(meta, biome, out var synthetic))
            {
                points[biome].Add(synthetic);
                anchors[biome] = synthetic;
                log.Warn($"Biome anchor synthesized for {biome} (seed={meta.Seed}) at {synthetic.X},{synthetic.Z}.");
            }
        }

        return new BiomeIndexStore(meta.Seed, meta.Size.Width, meta.Size.Depth, index.Stride, points, anchors);
    }

    private static bool TryFindClimateMaximum(WorldMeta meta, BiomeId biome, out BiomeIndexPoint point)
    {
        point = default;
        if (meta?.Size == null)
            return false;

        var bestScore = float.NegativeInfinity;
        var bestX = 0;
        var bestZ = 0;
        var minX = meta.HasFiniteWorldBounds() ? 0 : -UnboundedCoverageRadius;
        var maxX = meta.HasFiniteWorldBounds() ? Math.Max(0, meta.Size.Width - 1) : UnboundedCoverageRadius;
        var minZ = meta.HasFiniteWorldBounds() ? 0 : -UnboundedCoverageRadius;
        var maxZ = meta.HasFiniteWorldBounds() ? Math.Max(0, meta.Size.Depth - 1) : UnboundedCoverageRadius;

        for (var z = minZ; z <= maxZ; z += 8)
        {
            for (var x = minX; x <= maxX; x += 8)
            {
                var sample = BiomeService.GetSampleAt(meta, x, z);
                var score = biome switch
                {
                    BiomeId.Ocean => sample.OceanWeight,
                    BiomeId.Desert => sample.EffectiveDesertWeight,
                    BiomeId.Forest => sample.ForestWeight,
                    BiomeId.Hills => sample.HillsWeight,
                    _ => Math.Clamp(1f - MathF.Max(sample.OceanWeight, sample.EffectiveDesertWeight), 0f, 1f)
                };

                if (score <= bestScore)
                    continue;
                bestScore = score;
                bestX = x;
                bestZ = z;
            }
        }

        point = new BiomeIndexPoint(bestX, bestZ);
        return true;
    }

    private static int CountCoveredBiomes(BiomeIndexStore index)
    {
        var covered = 0;
        foreach (var biome in RequiredBiomes)
        {
            if (index.GetPoints(biome).Count > 0 && index.TryGetAnchor(biome, out _))
                covered++;
        }
        return covered;
    }

    private static BiomeIndexPoint? FindAnchor(List<BiomeIndexPoint> points, int centerX, int centerZ)
    {
        if (points == null || points.Count == 0)
            return null;

        var best = points[0];
        var bestDistSq = long.MaxValue;
        for (var i = 0; i < points.Count; i++)
        {
            var p = points[i];
            var dx = p.X - centerX;
            var dz = p.Z - centerZ;
            var distSq = (long)dx * dx + (long)dz * dz;
            if (distSq >= bestDistSq)
                continue;
            best = p;
            bestDistSq = distSq;
        }

        return best;
    }

    private static IEnumerable<int> EnumerateAxis(int size, int stride)
    {
        if (size <= 0)
            yield break;

        var clampedStride = Math.Max(1, stride);
        for (var i = 0; i < size; i += clampedStride)
            yield return i;

        var edge = size - 1;
        if (edge >= 0 && edge % clampedStride != 0)
            yield return edge;
    }

    private static int NextSeed(int seed, int attempt)
    {
        unchecked
        {
            var next = seed * 1664525 + 1013904223 + (attempt * 7919);
            return next == 0 ? 1337 + attempt : next;
        }
    }
}
