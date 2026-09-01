using System;

namespace LatticeVeilMonoGame.Core;

public static class OreRetrofitService
{
    private const int PocketCellSize = 4;
    private const int SeaLevel = 64;

    public static bool ApplyToLoadedStone(VoxelChunkData chunk, WorldMeta meta)
    {
        if (chunk == null || meta == null || meta.WorldGeneration?.GenerateOres == false)
            return false;

        var seed = meta.Seed;
        var changed = false;
        var originX = chunk.Coord.X * VoxelChunkData.ChunkSizeX;
        var originY = chunk.Coord.Y * VoxelChunkData.ChunkSizeY;
        var originZ = chunk.Coord.Z * VoxelChunkData.ChunkSizeZ;

        for (var y = 0; y < VoxelChunkData.ChunkSizeY; y++)
        {
            var worldY = originY + y;
            if (worldY < 1)
                continue;

            for (var z = 0; z < VoxelChunkData.ChunkSizeZ; z++)
            {
                var worldZ = originZ + z;
                for (var x = 0; x < VoxelChunkData.ChunkSizeX; x++)
                {
                    if (chunk.GetBlockRaw(x, y, z) != BlockIds.Stone)
                        continue;

                    var worldX = originX + x;
                    var biome = chunk.GetBiomeLocal(x, z);
                    if (biome == BiomeId.Unknown)
                        biome = BiomeService.GetSampleAt(meta, worldX, worldZ).BiomeId;

                    if (!TryChooseOre(seed, worldX, worldY, worldZ, biome, out var ore))
                        continue;

                    chunk.SetBlockRaw(x, y, z, ore);
                    changed = true;
                }
            }
        }

        if (changed)
            chunk.MarkDirty();

        return changed;
    }

    private static bool TryChooseOre(int seed, int worldX, int worldY, int worldZ, BiomeId biome, out byte ore)
    {
        ore = BlockIds.Air;

        for (var i = 0; i < Rules.Length; i++)
        {
            var rule = Rules[i];
            if (worldY < rule.MinY || worldY > rule.MaxY)
                continue;

            var chance = rule.BaseChancePerTenThousand * DepthWeight(worldY, rule.MinY, rule.MaxY);
            if (worldY > SeaLevel + 8)
                chance *= 0.18f;
            else if (worldY > SeaLevel)
                chance *= 0.45f;

            if (biome == rule.BonusBiome)
                chance *= rule.BiomeMultiplier;
            else if (biome == BiomeId.Ocean && rule.Ore != BlockIds.SapphireOre)
                chance *= 0.72f;

            if (IsInPocket(seed, worldX, worldY, worldZ, rule.Ore, chance))
            {
                ore = rule.Ore;
                return true;
            }
        }

        return false;
    }

    private static float DepthWeight(int y, int minY, int maxY)
    {
        var mid = (minY + maxY) * 0.5f;
        var half = Math.Max(1f, (maxY - minY) * 0.5f);
        var normalized = Math.Abs(y - mid) / half;
        return 0.35f + (1f - Math.Clamp(normalized, 0f, 1f)) * 0.85f;
    }

    private static bool IsInPocket(int seed, int worldX, int worldY, int worldZ, byte ore, float chancePerTenThousand)
    {
        var cellX = FloorDiv(worldX, PocketCellSize);
        var cellY = FloorDiv(worldY, PocketCellSize);
        var cellZ = FloorDiv(worldZ, PocketCellSize);
        var roll = Hash3D(cellX, cellY, cellZ, seed ^ (ore * 7919)) % 10000;
        if (roll >= chancePerTenThousand)
            return false;

        var centerX = cellX * PocketCellSize + (int)(Hash3D(cellX, cellY, cellZ, seed + 11) % PocketCellSize);
        var centerY = cellY * PocketCellSize + (int)(Hash3D(cellX, cellY, cellZ, seed + 23) % PocketCellSize);
        var centerZ = cellZ * PocketCellSize + (int)(Hash3D(cellX, cellY, cellZ, seed + 37) % PocketCellSize);
        var radius = 1 + (int)(Hash3D(cellX, cellY, cellZ, seed + ore + 53) % 2);
        var dx = worldX - centerX;
        var dy = worldY - centerY;
        var dz = worldZ - centerZ;
        return dx * dx + dy * dy + dz * dz <= radius * radius + 1;
    }

    private static int FloorDiv(int value, int divisor)
    {
        var result = value / divisor;
        if ((value ^ divisor) < 0 && value % divisor != 0)
            result--;
        return result;
    }

    private static uint Hash3D(int x, int y, int z, int seed)
    {
        unchecked
        {
            uint h = (uint)seed;
            h ^= (uint)x * 374761393u;
            h = (h << 13) | (h >> 19);
            h ^= (uint)y * 668265263u;
            h = (h << 17) | (h >> 15);
            h ^= (uint)z * 2246822519u;
            h *= 3266489917u;
            h ^= h >> 16;
            return h;
        }
    }

    private readonly record struct OreRule(byte Ore, int MinY, int MaxY, float BaseChancePerTenThousand, BiomeId BonusBiome, float BiomeMultiplier);

    private static readonly OreRule[] Rules =
    {
        new(BlockIds.Diamond, 1, 16, 5, BiomeId.Hills, 1.15f),
        new(BlockIds.EmeraldOre, 6, 34, 7, BiomeId.Hills, 1.55f),
        new(BlockIds.RubyOre, 4, 32, 8, BiomeId.Hills, 1.35f),
        new(BlockIds.SapphireOre, 4, 36, 8, BiomeId.Ocean, 1.25f),
        new(BlockIds.GoldOre, 4, 34, 10, BiomeId.Desert, 1.30f),
        new(BlockIds.AmethystOre, 10, 46, 12, BiomeId.Forest, 1.20f),
        new(BlockIds.IronOre, 8, 66, 24, BiomeId.Hills, 1.20f),
        new(BlockIds.CopperOre, 12, 82, 30, BiomeId.Grasslands, 1.10f),
        new(BlockIds.CoalOre, 16, 112, 38, BiomeId.Hills, 1.08f)
    };
}
