using System;
using System.Collections.Generic;
using System.Threading;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace LatticeVeilMonoGame.Core;

public static class HorizonLodPregenerator
{
    private const int CenterChunkStep = 8;
    private const int SeaLevel = 64;
    private const int SkirtDepthBlocks = 48;

    public static int BuildSpawnCache(
        VoxelWorld world,
        int spawnChunkX,
        int spawnChunkZ,
        string? qualityPreset,
        int renderDistanceChunks,
        int lodLevels,
        int pregenAreaRadiusChunks,
        CancellationToken cancellationToken,
        Action<float>? progress = null)
    {
        if (world == null || pregenAreaRadiusChunks <= 0)
        {
            progress?.Invoke(1f);
            return 0;
        }

        var config = GetConfig(qualityPreset, renderDistanceChunks, lodLevels);
        var centerRadius = ResolveCenterRadius(pregenAreaRadiusChunks);
        var centerX = FloorToMultiple(spawnChunkX, CenterChunkStep);
        var centerZ = FloorToMultiple(spawnChunkZ, CenterChunkStep);
        var centers = new List<ChunkCoord>();

        for (var dz = -centerRadius; dz <= centerRadius; dz += CenterChunkStep)
        {
            for (var dx = -centerRadius; dx <= centerRadius; dx += CenterChunkStep)
                centers.Add(new ChunkCoord(centerX + dx, 0, centerZ + dz));
        }

        var innerRadius = Math.Clamp(renderDistanceChunks + 1, 2, GameSettings.EngineRenderDistanceMax + 1);
        var built = 0;
        for (var i = 0; i < centers.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var center = centers[i];
            var centerBaseProgress = i / (float)Math.Max(1, centers.Count);
            var centerProgressSpan = 1f / Math.Max(1, centers.Count);
            progress?.Invoke(centerBaseProgress);
            BuildAndSave(
                world,
                center,
                innerRadius,
                config,
                cancellationToken,
                localProgress => progress?.Invoke(centerBaseProgress + centerProgressSpan * Math.Clamp(localProgress, 0f, 1f)));
            built++;
            progress?.Invoke((i + 1) / (float)Math.Max(1, centers.Count));
        }

        return built;
    }

    private static void BuildAndSave(
        VoxelWorld world,
        ChunkCoord centerChunk,
        int innerRadiusChunks,
        LodConfig config,
        CancellationToken cancellationToken,
        Action<float>? progress)
    {
        if (HorizonLodCacheStore.TryLoad(
                world.WorldPath,
                centerChunk.X,
                centerChunk.Z,
                innerRadiusChunks,
                config.RadiusChunks,
                config.CacheKey,
                out _))
        {
            progress?.Invoke(1f);
            return;
        }

        var vertices = BuildVertices(world, centerChunk, innerRadiusChunks, config, cancellationToken, progress);
        cancellationToken.ThrowIfCancellationRequested();
        HorizonLodCacheStore.Save(
            world.WorldPath,
            centerChunk.X,
            centerChunk.Z,
            innerRadiusChunks,
            config.RadiusChunks,
            config.CacheKey,
            vertices);
    }

    private static VertexPositionColor[] BuildVertices(
        VoxelWorld world,
        ChunkCoord centerChunk,
        int innerRadiusChunks,
        LodConfig config,
        CancellationToken cancellationToken,
        Action<float>? progress)
    {
        var vertices = new List<VertexPositionColor>(config.VertexCapacityHint);
        var chunkSize = VoxelChunkData.ChunkSizeX;
        var centerX = centerChunk.X * chunkSize + chunkSize / 2;
        var centerZ = centerChunk.Z * chunkSize + chunkSize / 2;
        var innerBlocks = Math.Max(chunkSize * 3, innerRadiusChunks * chunkSize - chunkSize / 2);
        var radiusBlocks = Math.Max(innerBlocks + chunkSize * 4, config.RadiusChunks * chunkSize);
        var nearEnd = Math.Min(radiusBlocks, innerBlocks + config.NearBandChunks * chunkSize);
        var midEnd = Math.Min(radiusBlocks, nearEnd + config.MidBandChunks * chunkSize);

        progress?.Invoke(0f);
        AddRing(world, vertices, centerX, centerZ, innerBlocks, nearEnd, config.NearStepBlocks, cancellationToken, p => progress?.Invoke(p * 0.42f));
        AddRing(world, vertices, centerX, centerZ, nearEnd - config.NearStepBlocks, midEnd, config.MidStepBlocks, cancellationToken, p => progress?.Invoke(0.42f + p * 0.34f));
        AddRing(world, vertices, centerX, centerZ, midEnd - config.MidStepBlocks, radiusBlocks, config.FarStepBlocks, cancellationToken, p => progress?.Invoke(0.76f + p * 0.24f));
        progress?.Invoke(1f);
        return vertices.ToArray();
    }

    private static void AddRing(
        VoxelWorld world,
        List<VertexPositionColor> vertices,
        int centerX,
        int centerZ,
        int innerBlocks,
        int outerBlocks,
        int step,
        CancellationToken cancellationToken,
        Action<float>? progress)
    {
        if (outerBlocks <= innerBlocks)
        {
            progress?.Invoke(1f);
            return;
        }

        var minX = FloorToMultiple(centerX - outerBlocks, step);
        var maxX = FloorToMultiple(centerX + outerBlocks, step);
        var minZ = FloorToMultiple(centerZ - outerBlocks, step);
        var maxZ = FloorToMultiple(centerZ + outerBlocks, step);
        var totalX = Math.Max(1, (int)MathF.Ceiling((maxX - minX) / (float)Math.Max(1, step)));
        var totalZ = Math.Max(1, (int)MathF.Ceiling((maxZ - minZ) / (float)Math.Max(1, step)));
        var totalCells = Math.Max(1, totalX * totalZ);
        var processedCells = 0;
        var sampledCells = 0;

        for (var x = minX; x < maxX; x += step)
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (var z = minZ; z < maxZ; z += step)
            {
                processedCells++;
                if ((++sampledCells & 0x1F) == 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    progress?.Invoke(Math.Clamp(processedCells / (float)totalCells, 0f, 1f));
                }

                var cellCenterX = x + step / 2;
                var cellCenterZ = z + step / 2;
                var ringDistance = Math.Max(Math.Abs(cellCenterX - centerX), Math.Abs(cellCenterZ - centerZ));
                if (ringDistance < innerBlocks || ringDistance > outerBlocks)
                    continue;

                var s00 = world.SampleDistantTerrainColumn(x, z);
                var s10 = world.SampleDistantTerrainColumn(x + step, z);
                var s01 = world.SampleDistantTerrainColumn(x, z + step);
                var s11 = world.SampleDistantTerrainColumn(x + step, z + step);

                AddTerrainCell(vertices, x, z, step, s00, s10, s01, s11);
                if (s00.HasWater || s10.HasWater || s01.HasWater || s11.HasWater)
                    AddWaterCell(vertices, x, z, step);
            }

            progress?.Invoke(Math.Clamp(processedCells / (float)totalCells, 0f, 1f));
        }

        progress?.Invoke(1f);
    }

    private static void AddTerrainCell(
        List<VertexPositionColor> vertices,
        int x,
        int z,
        int step,
        VoxelWorld.DistantTerrainSample s00,
        VoxelWorld.DistantTerrainSample s10,
        VoxelWorld.DistantTerrainSample s01,
        VoxelWorld.DistantTerrainSample s11)
    {
        var p00 = new Vector3(x, s00.SurfaceY + 0.02f, z);
        var p10 = new Vector3(x + step, s10.SurfaceY + 0.02f, z);
        var p01 = new Vector3(x, s01.SurfaceY + 0.02f, z + step);
        var p11 = new Vector3(x + step, s11.SurfaceY + 0.02f, z + step);

        var c00 = ResolveTerrainColor(s00);
        var c10 = ResolveTerrainColor(s10);
        var c01 = ResolveTerrainColor(s01);
        var c11 = ResolveTerrainColor(s11);

        vertices.Add(new VertexPositionColor(p00, c00));
        vertices.Add(new VertexPositionColor(p10, c10));
        vertices.Add(new VertexPositionColor(p11, c11));
        vertices.Add(new VertexPositionColor(p00, c00));
        vertices.Add(new VertexPositionColor(p11, c11));
        vertices.Add(new VertexPositionColor(p01, c01));

        AddSkirt(vertices, p00, p10, c00, c10);
        AddSkirt(vertices, p10, p11, c10, c11);
        AddSkirt(vertices, p11, p01, c11, c01);
        AddSkirt(vertices, p01, p00, c01, c00);
    }

    private static void AddSkirt(List<VertexPositionColor> vertices, Vector3 topA, Vector3 topB, Color colorA, Color colorB)
    {
        if (MathF.Abs(topA.Y - topB.Y) < 0.75f)
            return;

        var bottomY = MathF.Max(1f, MathF.Min(topA.Y, topB.Y) - SkirtDepthBlocks);
        var bottomA = new Vector3(topA.X, bottomY, topA.Z);
        var bottomB = new Vector3(topB.X, bottomY, topB.Z);
        var bottomColorA = ShadeColor(colorA, 0.62f);
        var bottomColorB = ShadeColor(colorB, 0.62f);

        vertices.Add(new VertexPositionColor(topA, colorA));
        vertices.Add(new VertexPositionColor(topB, colorB));
        vertices.Add(new VertexPositionColor(bottomB, bottomColorB));
        vertices.Add(new VertexPositionColor(topA, colorA));
        vertices.Add(new VertexPositionColor(bottomB, bottomColorB));
        vertices.Add(new VertexPositionColor(bottomA, bottomColorA));
    }

    private static void AddWaterCell(List<VertexPositionColor> vertices, int x, int z, int step)
    {
        var color = new Color(64, 171, 221);
        var y = SeaLevel + 0.04f;
        var p00 = new Vector3(x, y, z);
        var p10 = new Vector3(x + step, y, z);
        var p01 = new Vector3(x, y, z + step);
        var p11 = new Vector3(x + step, y, z + step);

        vertices.Add(new VertexPositionColor(p00, color));
        vertices.Add(new VertexPositionColor(p10, color));
        vertices.Add(new VertexPositionColor(p11, color));
        vertices.Add(new VertexPositionColor(p00, color));
        vertices.Add(new VertexPositionColor(p11, color));
        vertices.Add(new VertexPositionColor(p01, color));
    }

    private static Color ResolveTerrainColor(VoxelWorld.DistantTerrainSample sample)
    {
        var color = sample.SurfaceBlock switch
        {
            BlockIds.Sand => new Color(196, 181, 119),
            BlockIds.Dirt => new Color(113, 79, 52),
            BlockIds.Stone => new Color(126, 126, 120),
            _ => sample.BiomeId == (byte)BiomeId.Forest
                ? new Color(47, 129, 42)
                : sample.BiomeId == (byte)BiomeId.Hills
                    ? new Color(63, 145, 52)
                    : new Color(66, 162, 39)
        };

        var heightShade = Math.Clamp((sample.SurfaceY - SeaLevel) / 80f, -0.18f, 0.20f);
        return ShadeColor(color, 1f + heightShade);
    }

    private static Color ShadeColor(Color color, float factor)
    {
        factor = Math.Clamp(factor, 0.65f, 1.18f);
        return new Color(
            (byte)Math.Clamp((int)(color.R * factor), 0, 255),
            (byte)Math.Clamp((int)(color.G * factor), 0, 255),
            (byte)Math.Clamp((int)(color.B * factor), 0, 255),
            color.A);
    }

    private static LodConfig GetConfig(string? qualityPreset, int renderDistanceChunks, int lodLevels)
    {
        var quality = string.IsNullOrWhiteSpace(qualityPreset)
            ? "MEDIUM"
            : qualityPreset.Trim().ToUpperInvariant();
        var renderBoost = Math.Clamp(renderDistanceChunks - GameSettings.RenderDistanceMin, 0, 8);
        lodLevels = Math.Clamp(lodLevels, 1, 4);

        var config = quality switch
        {
            "LOW" => new LodConfig("LOW", 56 + renderBoost * 2, 24, 48, 96, 8, 14, 40_000),
            "HIGH" => new LodConfig("HIGH", 72 + renderBoost * 2, 12, 24, 48, 10, 18, 95_000),
            "ULTRA" => new LodConfig("ULTRA", 80 + renderBoost * 2, 8, 16, 32, 12, 22, 180_000),
            _ => new LodConfig("MEDIUM", 64 + renderBoost * 2, 16, 32, 64, 9, 16, 65_000)
        };

        if (lodLevels >= 4)
            return config with
            {
                RadiusChunks = config.RadiusChunks + 12,
                NearStepBlocks = Math.Max(8, config.NearStepBlocks),
                MidStepBlocks = Math.Max(16, config.MidStepBlocks),
                FarStepBlocks = Math.Max(32, config.FarStepBlocks),
                NearBandChunks = config.NearBandChunks + 2,
                MidBandChunks = config.MidBandChunks + 4,
                VertexCapacityHint = config.VertexCapacityHint + 70_000
            };
        if (lodLevels <= 1)
            return config with
            {
                RadiusChunks = Math.Max(32, config.RadiusChunks - 20),
                NearStepBlocks = Math.Max(config.NearStepBlocks, 8),
                MidStepBlocks = Math.Max(config.MidStepBlocks, 16),
                FarStepBlocks = Math.Max(config.FarStepBlocks, 24),
                VertexCapacityHint = Math.Max(40_000, config.VertexCapacityHint / 2)
            };
        if (lodLevels == 2)
            return config with
            {
                RadiusChunks = Math.Max(48, config.RadiusChunks - 12),
                NearStepBlocks = Math.Max(config.NearStepBlocks, 4),
                MidStepBlocks = Math.Max(config.MidStepBlocks, 8),
                FarStepBlocks = Math.Max(config.FarStepBlocks, 16)
            };

        return config;
    }

    private static int ResolveCenterRadius(int pregenAreaRadiusChunks)
    {
        if (pregenAreaRadiusChunks >= 224)
            return 24;
        if (pregenAreaRadiusChunks >= 160)
            return 16;
        if (pregenAreaRadiusChunks >= 96)
            return 8;
        return 0;
    }

    private static int FloorToMultiple(int value, int step)
        => (int)MathF.Floor(value / (float)step) * step;

    private readonly record struct LodConfig(
        string Quality,
        int RadiusChunks,
        int NearStepBlocks,
        int MidStepBlocks,
        int FarStepBlocks,
        int NearBandChunks,
        int MidBandChunks,
        int VertexCapacityHint)
    {
        public string CacheKey => $"blocky-v9-{Quality}-r{RadiusChunks}-n{NearStepBlocks}-m{MidStepBlocks}-f{FarStepBlocks}-nb{NearBandChunks}-mb{MidBandChunks}";
    }
}
