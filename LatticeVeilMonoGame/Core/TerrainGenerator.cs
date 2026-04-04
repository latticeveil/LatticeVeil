using System;
using System.Collections.Generic;
using LatticeVeilMonoGame.Core;

namespace LatticeVeilMonoGame.Core;

/// <summary>
/// Terrain generator for normal terrain worlds.
/// Uses explicit terrain zones so oceans stay submerged and trees can be stamped across chunk seams.
/// </summary>
public sealed class TerrainGenerator : IChunkGenerator
{
    private readonly FastNoiseGenerator _noiseGenerator;
    private readonly Logger _log;
    private readonly int _seed;
    private readonly TerrainSettings _settings;
    private readonly Dictionary<long, RawTerrainSample> _rawTerrainCache = new();
    private readonly Dictionary<long, TerrainColumnSample> _columnCache = new();
    private bool _disposed;

    private const byte BLOCK_AIR = BlockIds.Air;
    private const byte BLOCK_STONE = BlockIds.Stone;
    private const byte BLOCK_DIRT = BlockIds.Dirt;
    private const byte BLOCK_GRASS = BlockIds.Grass;
    private const byte BLOCK_SAND = BlockIds.Sand;
    private const byte BLOCK_WATER = BlockIds.Water;
    private const byte BLOCK_WOOD = BlockIds.Wood;
    private const byte BLOCK_LEAVES = BlockIds.Leaves;
    private const byte BLOCK_COAL_ORE = BlockIds.CoalOre;
    private const byte BLOCK_IRON_ORE = BlockIds.IronOre;
    private const byte BLOCK_GOLD_ORE = BlockIds.GoldOre;
    private const byte BLOCK_DIAMOND = BlockIds.Diamond;
    private const byte BLOCK_NULLROCK = BlockIds.Nullrock;

    private const int TreeCellSize = 7;
    private const int MaxTreeRadius = 3;
    private const int MaxTallTreeHeight = 8;

    public struct TerrainSettings
    {
        public int ChunkSize { get; set; }
        public int WorldHeight { get; set; }
        public float BaseFrequency { get; set; }
        public float DetailFrequency { get; set; }
        public int Octaves { get; set; }
        public float Persistence { get; set; }
        public float ErosionStrength { get; set; }
        public float SeaLevel { get; set; }
        public bool GenerateCaves { get; set; }
        public bool GenerateOres { get; set; }
        public bool GenerateStructures { get; set; }
        public bool GenerateTrees { get; set; }

        public TerrainSettings(int chunkSize, int worldHeight)
        {
            ChunkSize = chunkSize;
            WorldHeight = worldHeight;
            BaseFrequency = 0.02f;
            DetailFrequency = 0.10f;
            Octaves = 6;
            Persistence = 0.5f;
            ErosionStrength = 0.3f;
            SeaLevel = 64f;
            GenerateCaves = true;
            GenerateOres = true;
            GenerateStructures = true;
            GenerateTrees = true;
        }
    }

    internal enum TerrainZone
    {
        DeepOcean,
        ShallowOcean,
        Coast,
        InlandLowlands,
        InlandHighlands
    }

    internal readonly record struct TerrainColumnSample(
        int SurfaceY,
        TerrainZone Zone,
        BiomeSample Biome,
        byte SurfaceBlock,
        int SurfaceDepth,
        float LandSignal,
        float ReliefSignal,
        float DetailSignal);

    private readonly record struct RawTerrainSample(
        int SurfaceY,
        TerrainZone Zone,
        BiomeSample Biome,
        float OceanBias,
        float LandSignal,
        float ReliefSignal,
        float DetailSignal);

    internal readonly record struct DebugTreeDescriptor(
        int OriginX,
        int OriginY,
        int OriginZ,
        int TrunkHeight,
        string Family,
        int Variant);

    private readonly record struct TreeDescriptor(
        int OriginX,
        int OriginY,
        int OriginZ,
        int TrunkHeight,
        TreeFamily Family,
        int Variant);

    private readonly record struct OreVeinSpec(
        byte BlockId,
        int MinWorldY,
        int MaxWorldY,
        int CellSizeXZ,
        int CellSizeY,
        float SpawnChance,
        int MinVeinSize,
        int MaxVeinSize,
        float LargeVeinChance);

    private enum TreeFamily
    {
        StandardCompact,
        TallForest
    }

    private static readonly OreVeinSpec CoalVeinSpec = new(
        BlockId: BLOCK_COAL_ORE,
        MinWorldY: 24,
        MaxWorldY: 118,
        CellSizeXZ: 10,
        CellSizeY: 12,
        SpawnChance: 0.44f,
        MinVeinSize: 5,
        MaxVeinSize: 11,
        LargeVeinChance: 0.24f);

    private static readonly OreVeinSpec IronVeinSpec = new(
        BlockId: BLOCK_IRON_ORE,
        MinWorldY: 10,
        MaxWorldY: 72,
        CellSizeXZ: 11,
        CellSizeY: 12,
        SpawnChance: 0.28f,
        MinVeinSize: 4,
        MaxVeinSize: 7,
        LargeVeinChance: 0.10f);

    private static readonly OreVeinSpec GoldVeinSpec = new(
        BlockId: BLOCK_GOLD_ORE,
        MinWorldY: 4,
        MaxWorldY: 28,
        CellSizeXZ: 12,
        CellSizeY: 10,
        SpawnChance: 0.18f,
        MinVeinSize: 3,
        MaxVeinSize: 6,
        LargeVeinChance: 0.08f);

    private static readonly OreVeinSpec DiamondVeinSpec = new(
        BlockId: BLOCK_DIAMOND,
        MinWorldY: 1,
        MaxWorldY: 12,
        CellSizeXZ: 13,
        CellSizeY: 9,
        SpawnChance: 0.14f,
        MinVeinSize: 2,
        MaxVeinSize: 10,
        LargeVeinChance: 0.05f);

    public TerrainGenerator(int seed, TerrainSettings settings, Logger log)
    {
        _seed = seed;
        _settings = settings;
        _log = log;
        _noiseGenerator = new FastNoiseGenerator(seed, log);

        _log.Info($"TerrainGenerator initialized with seed: {seed}");
        _log.Info($"Settings: ChunkSize={_settings.ChunkSize}, WorldHeight={_settings.WorldHeight}, SeaLevel={_settings.SeaLevel}");
    }

    public VoxelChunkData GenerateChunk(ChunkCoord coord)
    {
        _rawTerrainCache.Clear();
        _columnCache.Clear();

        try
        {
            var chunk = new VoxelChunkData(coord);
            var sizeX = VoxelChunkData.ChunkSizeX;
            var sizeY = VoxelChunkData.ChunkSizeY;
            var sizeZ = VoxelChunkData.ChunkSizeZ;

            var originX = coord.X * sizeX;
            var originY = coord.Y * sizeY;
            var originZ = coord.Z * sizeZ;

            var columns = GenerateTerrain(chunk, originX, originY, originZ, sizeX, sizeY, sizeZ);

            if (_settings.GenerateCaves)
                GenerateCaves(chunk, columns, originX, originY, originZ, sizeX, sizeY, sizeZ);

            if (_settings.GenerateOres)
                GenerateOres(chunk, originX, originY, originZ, sizeX, sizeY, sizeZ);

            if (_settings.GenerateStructures)
                GenerateStructures(chunk, originX, originY, originZ, sizeX, sizeY, sizeZ);

            if (_settings.GenerateTrees)
                GenerateTrees(chunk, originX, originY, originZ, sizeX, sizeY, sizeZ);

            _log.Debug($"Generated default terrain chunk at {coord}");
            return chunk;
        }
        finally
        {
            _rawTerrainCache.Clear();
            _columnCache.Clear();
        }
    }

    internal TerrainColumnSample SampleDebugColumn(int worldX, int worldZ)
        => SampleTerrainColumn(worldX, worldZ);

    internal bool TryGetDebugTreeDescriptorAtWorld(int worldX, int worldZ, out DebugTreeDescriptor descriptor)
    {
        if (TryCreateTreeDescriptorAt(worldX, worldZ, out var tree))
        {
            descriptor = new DebugTreeDescriptor(
                tree.OriginX,
                tree.OriginY,
                tree.OriginZ,
                tree.TrunkHeight,
                tree.Family.ToString(),
                tree.Variant);
            return true;
        }

        descriptor = default;
        return false;
    }

    private TerrainColumnSample[,] GenerateTerrain(VoxelChunkData chunk, int originX, int originY, int originZ, int sizeX, int sizeY, int sizeZ)
    {
        var columns = new TerrainColumnSample[sizeX, sizeZ];

        for (var x = 0; x < sizeX; x++)
        {
            for (var z = 0; z < sizeZ; z++)
            {
                var worldX = originX + x;
                var worldZ = originZ + z;
                var column = SampleTerrainColumn(worldX, worldZ);
                columns[x, z] = column;
                chunk.SetBiomeLocal(x, z, column.Biome.BiomeId);

                FillColumn(chunk, x, originY, z, sizeY, column);
            }
        }

        return columns;
    }

    private TerrainColumnSample SampleTerrainColumn(int worldX, int worldZ)
    {
        var key = PackColumnKey(worldX, worldZ);
        if (_columnCache.TryGetValue(key, out var cached))
            return cached;

        var raw = SampleRawTerrain(worldX, worldZ);
        var biome = raw.Biome;
        var seaLevel = (int)_settings.SeaLevel;
        var surfaceY = SmoothSurfaceHeight(worldX, worldZ, raw);

        surfaceY = Math.Clamp(surfaceY, 1, _settings.WorldHeight - 8);
        var surfaceBlock = ResolveSurfaceBlock(raw.Zone, biome, surfaceY, seaLevel, raw.OceanBias);
        var surfaceDepth = raw.Zone == TerrainZone.Coast || raw.Zone == TerrainZone.ShallowOcean || raw.Zone == TerrainZone.DeepOcean
            ? 4
            : 3;

        var column = new TerrainColumnSample(surfaceY, raw.Zone, biome, surfaceBlock, surfaceDepth, raw.LandSignal, raw.ReliefSignal, raw.DetailSignal);
        _columnCache[key] = column;
        return column;
    }

    private RawTerrainSample SampleRawTerrain(int worldX, int worldZ)
    {
        var key = PackColumnKey(worldX, worldZ);
        if (_rawTerrainCache.TryGetValue(key, out var cached))
            return cached;

        var biome = BiomeService.GetSampleAt(_seed, worldX, worldZ);
        var seaLevel = (int)_settings.SeaLevel;

        var continentalBase = _noiseGenerator.GetTerrainHeight(worldX * 0.18f + 137f, worldZ * 0.18f - 211f);
        var continentalWarp = _noiseGenerator.GetTerrainHeight(worldX * 0.42f - 521f, worldZ * 0.42f + 317f);
        var biomeLandBias = biome.Continentalness * 2f - 1f;
        var landSignal = biomeLandBias * 0.72f + continentalBase * 0.18f + continentalWarp * 0.10f;

        var reliefPrimary = _noiseGenerator.GetTerrainHeight(worldX * 0.46f, worldZ * 0.46f);
        var reliefSecondary = _noiseGenerator.GetTerrainHeight(worldX * 0.92f + 601f, worldZ * 0.92f - 487f);
        var ridgeNoise = MathF.Abs(_noiseGenerator.GetTerrainHeight(worldX * 0.63f - 907f, worldZ * 0.63f + 311f));
        var reliefSignal = reliefPrimary * 0.55f + reliefSecondary * 0.25f + ridgeNoise * 0.20f;

        var detailWarp = _noiseGenerator.GetTerrainHeight(worldX * 1.8f, worldZ * 1.8f);
        var detailSignal = _noiseGenerator.GetTerrainHeight(
            (worldX + detailWarp * 14f) * 2.2f,
            (worldZ - detailWarp * 14f) * 2.2f);

        var oceanBias = Math.Max(biome.OceanWeight, biome.BiomeId == BiomeId.Ocean ? 0.90f : 0f);
        var terrainZone = ClassifyZone(landSignal, oceanBias, biome.BiomeId);
        var surfaceY = ComputeRawSurfaceHeight(terrainZone, biome, seaLevel, oceanBias, landSignal, reliefSignal, detailSignal);

        var sample = new RawTerrainSample(surfaceY, terrainZone, biome, oceanBias, landSignal, reliefSignal, detailSignal);
        _rawTerrainCache[key] = sample;
        return sample;
    }

    private int ComputeRawSurfaceHeight(TerrainZone terrainZone, BiomeSample biome, int seaLevel, float oceanBias, float landSignal, float reliefSignal, float detailSignal)
    {
        var hillsBias = biome.BiomeId == BiomeId.Hills ? 1f : biome.HillsWeight;
        var forestBias = biome.BiomeId == BiomeId.Forest ? 1f : biome.ForestWeight;
        var reliefStrength = 0.5f + hillsBias * 0.6f + forestBias * 0.15f;
        var reliefHeight = reliefSignal * (terrainZone == TerrainZone.InlandHighlands ? 16f : 10f) * reliefStrength;
        var detailHeight = detailSignal * (terrainZone == TerrainZone.Coast ? 1.8f : 2.6f);

        int surfaceY;
        switch (terrainZone)
        {
            case TerrainZone.DeepOcean:
            {
                var basinDepth = 14f + oceanBias * 9f + Math.Max(0f, -landSignal) * 10f;
                var basinDetail = Math.Min(detailSignal * 1.2f, 1f);
                surfaceY = seaLevel - (int)MathF.Round(basinDepth + basinDetail);
                surfaceY = Math.Min(surfaceY, seaLevel - 6);
                break;
            }
            case TerrainZone.ShallowOcean:
            {
                var shelfDepth = 5f + oceanBias * 4f + Math.Max(0f, 0.15f - landSignal) * 6f;
                surfaceY = seaLevel - (int)MathF.Round(shelfDepth - reliefSignal * 0.8f);
                surfaceY = Math.Clamp(surfaceY, seaLevel - 5, seaLevel - 2);
                break;
            }
            case TerrainZone.Coast:
            {
                var coastLift = reliefSignal * 1.6f + detailSignal * 0.9f;
                surfaceY = seaLevel + (int)MathF.Round(coastLift);
                surfaceY = Math.Clamp(surfaceY, seaLevel - 1, seaLevel + 4);
                break;
            }
            case TerrainZone.InlandHighlands:
            {
                var baseHeight = 18f + hillsBias * 10f + forestBias * 2f;
                surfaceY = seaLevel + (int)MathF.Round(baseHeight + reliefHeight + detailHeight);
                break;
            }
            default:
            {
                var baseHeight = 11f + forestBias * 3f + Math.Max(0f, landSignal) * 5f;
                surfaceY = seaLevel + (int)MathF.Round(baseHeight + reliefHeight * 0.72f + detailHeight);
                break;
            }
        }

        if (biome.BiomeId != BiomeId.Ocean && oceanBias < 0.60f)
        {
            var inlandFloor = biome.BiomeId == BiomeId.Forest ? seaLevel + 4 : seaLevel + 3;
            surfaceY = Math.Max(surfaceY, inlandFloor);
        }

        return surfaceY;
    }

    private int SmoothSurfaceHeight(int worldX, int worldZ, RawTerrainSample center)
    {
        var north = SampleRawTerrain(worldX, worldZ - 1).SurfaceY;
        var south = SampleRawTerrain(worldX, worldZ + 1).SurfaceY;
        var east = SampleRawTerrain(worldX + 1, worldZ).SurfaceY;
        var west = SampleRawTerrain(worldX - 1, worldZ).SurfaceY;
        var northEast = SampleRawTerrain(worldX + 1, worldZ - 1).SurfaceY;
        var northWest = SampleRawTerrain(worldX - 1, worldZ - 1).SurfaceY;
        var southEast = SampleRawTerrain(worldX + 1, worldZ + 1).SurfaceY;
        var southWest = SampleRawTerrain(worldX - 1, worldZ + 1).SurfaceY;

        var weightedAverage =
            center.SurfaceY * 0.34f
            + (north + south + east + west) * 0.11f
            + (northEast + northWest + southEast + southWest) * 0.055f;

        var hillsBias = center.Biome.BiomeId == BiomeId.Hills ? 1f : center.Biome.HillsWeight;
        var ruggedness = Math.Clamp(MathF.Abs(center.ReliefSignal) * 0.75f + hillsBias * 0.35f, 0f, 1f);
        var smoothingStrength = center.Zone switch
        {
            TerrainZone.DeepOcean => 0.25f,
            TerrainZone.ShallowOcean => 0.45f,
            TerrainZone.Coast => 0.65f,
            TerrainZone.InlandLowlands => 0.58f,
            TerrainZone.InlandHighlands => 0.34f,
            _ => 0.45f
        };
        smoothingStrength *= 1f - ruggedness * 0.45f;

        var smoothed = (int)MathF.Round(Lerp(center.SurfaceY, weightedAverage, smoothingStrength));
        var minNeighbor = Math.Min(Math.Min(north, south), Math.Min(east, west));
        var maxNeighbor = Math.Max(Math.Max(north, south), Math.Max(east, west));

        var maxRise = center.Zone switch
        {
            TerrainZone.Coast => 3,
            TerrainZone.InlandLowlands => 4,
            TerrainZone.InlandHighlands => hillsBias >= 0.75f ? 7 : 6,
            _ => 5
        };

        if (smoothed > minNeighbor + maxRise)
            smoothed = minNeighbor + maxRise;
        if (smoothed < maxNeighbor - maxRise)
            smoothed = maxNeighbor - maxRise;

        return smoothed;
    }

    private static TerrainZone ClassifyZone(float landSignal, float oceanBias, BiomeId biomeId)
    {
        if (biomeId == BiomeId.Ocean || oceanBias >= 0.78f)
            return landSignal < -0.18f ? TerrainZone.DeepOcean : TerrainZone.ShallowOcean;

        if (oceanBias >= 0.64f)
        {
            if (landSignal < -0.10f)
                return TerrainZone.DeepOcean;
            if (landSignal < 0.16f)
                return TerrainZone.ShallowOcean;
            return TerrainZone.Coast;
        }

        if (landSignal < -0.30f)
            return TerrainZone.DeepOcean;
        if (landSignal < -0.04f)
            return TerrainZone.ShallowOcean;
        if (landSignal < 0.12f)
            return TerrainZone.Coast;
        if (landSignal > 0.44f || biomeId == BiomeId.Hills)
            return TerrainZone.InlandHighlands;

        return TerrainZone.InlandLowlands;
    }

    private static byte ResolveSurfaceBlock(TerrainZone zone, BiomeSample biome, int surfaceY, int seaLevel, float oceanBias)
    {
        if (zone == TerrainZone.DeepOcean || zone == TerrainZone.ShallowOcean)
            return BLOCK_SAND;

        if (zone == TerrainZone.Coast)
        {
            var shoreline = surfaceY <= seaLevel + 1;
            var oceanFacing = biome.BiomeId == BiomeId.Ocean || oceanBias >= 0.58f;
            if (shoreline || oceanFacing)
                return BLOCK_SAND;

            return biome.BiomeId == BiomeId.Desert ? BLOCK_SAND : BLOCK_GRASS;
        }

        return biome.BiomeId == BiomeId.Desert ? BLOCK_SAND : BLOCK_GRASS;
    }

    private void FillColumn(VoxelChunkData chunk, int localX, int originY, int localZ, int sizeY, TerrainColumnSample column)
    {
        var seaLevel = (int)_settings.SeaLevel;

        for (var localY = 0; localY < sizeY; localY++)
        {
            var worldY = originY + localY;
            if (worldY < 0 || worldY >= _settings.WorldHeight)
                continue;

            byte block;
            if (worldY <= 0)
            {
                block = BLOCK_NULLROCK;
            }
            else if (worldY > column.SurfaceY)
            {
                block = worldY <= seaLevel ? BLOCK_WATER : BLOCK_AIR;
            }
            else if (worldY == column.SurfaceY)
            {
                block = column.SurfaceBlock;
            }
            else if (worldY >= column.SurfaceY - column.SurfaceDepth)
            {
                block = column.SurfaceBlock == BLOCK_SAND ? BLOCK_SAND : BLOCK_DIRT;
            }
            else
            {
                block = BLOCK_STONE;
            }

            chunk.SetBlockRaw(localX, localY, localZ, block);
        }
    }

    private void GenerateCaves(VoxelChunkData chunk, TerrainColumnSample[,] columns, int originX, int originY, int originZ, int sizeX, int sizeY, int sizeZ)
    {
        for (var x = 0; x < sizeX; x++)
        {
            for (var z = 0; z < sizeZ; z++)
            {
                var surfaceY = columns[x, z].SurfaceY;
                var carveLimit = Math.Min(surfaceY - 4, (int)_settings.SeaLevel + 14);

                for (var y = 1; y < sizeY; y++)
                {
                    var worldY = originY + y;
                    if (worldY >= carveLimit)
                        continue;

                    var currentBlock = chunk.GetBlockRaw(x, y, z);
                    if (currentBlock != BLOCK_STONE && currentBlock != BLOCK_DIRT && currentBlock != BLOCK_SAND)
                        continue;

                    var caveDensity = _noiseGenerator.GetCaveDensity(originX + x, worldY, originZ + z);
                    if (caveDensity < 0.13f)
                        chunk.SetBlockRaw(x, y, z, BLOCK_AIR);
                }
            }
        }
    }

    private void GenerateOres(VoxelChunkData chunk, int originX, int originY, int originZ, int sizeX, int sizeY, int sizeZ)
    {
        StampOreVeins(chunk, originX, originY, originZ, sizeX, sizeY, sizeZ, CoalVeinSpec, _seed + 0x3412ABCD);
        StampOreVeins(chunk, originX, originY, originZ, sizeX, sizeY, sizeZ, IronVeinSpec, _seed + 0x19AC02EF);
        StampOreVeins(chunk, originX, originY, originZ, sizeX, sizeY, sizeZ, GoldVeinSpec, _seed + 0x51D22091);
        StampOreVeins(chunk, originX, originY, originZ, sizeX, sizeY, sizeZ, DiamondVeinSpec, _seed + 0x71EE44AF);
    }

    private static bool IsExposedToAirOrWaterAbove(VoxelChunkData chunk, int x, int y, int z, int sizeY)
    {
        for (var dy = 1; dy <= 3; dy++)
        {
            var ny = y + dy;
            if (ny >= sizeY)
                return true;

            var above = chunk.GetBlockRaw(x, ny, z);
            if (above == BLOCK_AIR || above == BLOCK_WATER)
                return true;
        }

        return false;
    }

    private void GenerateStructures(VoxelChunkData chunk, int originX, int originY, int originZ, int sizeX, int sizeY, int sizeZ)
    {
        // Reserved for future structure work.
    }

    private void GenerateTrees(VoxelChunkData chunk, int originX, int originY, int originZ, int sizeX, int sizeY, int sizeZ)
    {
        var minWorldX = originX - MaxTreeRadius;
        var maxWorldX = originX + sizeX - 1 + MaxTreeRadius;
        var minWorldZ = originZ - MaxTreeRadius;
        var maxWorldZ = originZ + sizeZ - 1 + MaxTreeRadius;

        var minCellX = FloorDiv(minWorldX, TreeCellSize);
        var maxCellX = FloorDiv(maxWorldX, TreeCellSize);
        var minCellZ = FloorDiv(minWorldZ, TreeCellSize);
        var maxCellZ = FloorDiv(maxWorldZ, TreeCellSize);

        for (var cellX = minCellX; cellX <= maxCellX; cellX++)
        {
            for (var cellZ = minCellZ; cellZ <= maxCellZ; cellZ++)
            {
                var candidate = GetTreeCandidateOrigin(cellX, cellZ);
                if (!TryCreateTreeDescriptorAt(candidate.originX, candidate.originZ, out var tree))
                    continue;

                if (!IntersectsChunk(tree, originX, originY, originZ, sizeX, sizeY, sizeZ))
                    continue;

                StampTree(chunk, tree, originX, originY, originZ, sizeX, sizeY, sizeZ);
            }
        }
    }

    private static (int originX, int originZ) GetTreeCandidateOrigin(int cellX, int cellZ)
    {
        var hash = Hash2D(cellX, cellZ, 0x51A38F5D);
        var innerSpan = Math.Max(1, TreeCellSize - 2);
        var originX = cellX * TreeCellSize + 1 + (int)(hash % (uint)innerSpan);
        var originZ = cellZ * TreeCellSize + 1 + (int)((hash >> 8) % (uint)innerSpan);
        return (originX, originZ);
    }

    private bool TryCreateTreeDescriptorAt(int worldX, int worldZ, out TreeDescriptor descriptor)
    {
        if (!TryBuildTreeDescriptorAt(worldX, worldZ, out descriptor, out var currentPriority))
            return false;

        var currentSpacing = GetTreeSpacing(descriptor);
        var cellX = FloorDiv(worldX, TreeCellSize);
        var cellZ = FloorDiv(worldZ, TreeCellSize);
        for (var neighborCellX = cellX - 1; neighborCellX <= cellX + 1; neighborCellX++)
        {
            for (var neighborCellZ = cellZ - 1; neighborCellZ <= cellZ + 1; neighborCellZ++)
            {
                if (neighborCellX == cellX && neighborCellZ == cellZ)
                    continue;

                var neighborCandidate = GetTreeCandidateOrigin(neighborCellX, neighborCellZ);
                if (!TryBuildTreeDescriptorAt(neighborCandidate.originX, neighborCandidate.originZ, out var neighbor, out var neighborPriority))
                    continue;

                var spacing = Math.Max(currentSpacing, GetTreeSpacing(neighbor));
                var dx = neighbor.OriginX - descriptor.OriginX;
                var dz = neighbor.OriginZ - descriptor.OriginZ;
                if ((dx * dx) + (dz * dz) >= spacing * spacing)
                    continue;

                if (neighborPriority < currentPriority
                    || (neighborPriority == currentPriority && IsTreeEarlier(neighbor, descriptor)))
                {
                    descriptor = default;
                    return false;
                }
            }
        }

        return true;
    }

    private bool TryBuildTreeDescriptorAt(int worldX, int worldZ, out TreeDescriptor descriptor, out uint priority)
    {
        var cellX = FloorDiv(worldX, TreeCellSize);
        var cellZ = FloorDiv(worldZ, TreeCellSize);
        var candidate = GetTreeCandidateOrigin(cellX, cellZ);
        if (candidate.originX != worldX || candidate.originZ != worldZ)
        {
            descriptor = default;
            priority = uint.MaxValue;
            return false;
        }

        var column = SampleTerrainColumn(worldX, worldZ);
        if (!TryGetTreePlacementRules(column, out var spawnChance, out var tallChance))
        {
            descriptor = default;
            priority = uint.MaxValue;
            return false;
        }

        if (column.SurfaceBlock != BLOCK_GRASS || column.SurfaceY <= _settings.SeaLevel + 1)
        {
            descriptor = default;
            priority = uint.MaxValue;
            return false;
        }

        var treeHash = Hash2D(worldX, worldZ, _seed + 0x1F2E3D4C);
        var roll = ((treeHash >> 16) & 0xFFFFu) / 65535f;
        if (roll >= spawnChance)
        {
            descriptor = default;
            priority = uint.MaxValue;
            return false;
        }

        var family = TreeFamily.StandardCompact;
        var trunkHeight = 4;
        var variant = (int)((treeHash >> 24) & 0x1u);

        var tallRoll = ((treeHash >> 20) & 0x0Fu) / 15f;
        if (column.Biome.BiomeId == BiomeId.Forest && tallRoll < tallChance)
        {
            family = TreeFamily.TallForest;
            trunkHeight = 6 + (int)((treeHash >> 28) % 3u);
            variant = (int)((treeHash >> 26) & 0x1u);
        }

        var maxTreeTop = column.SurfaceY + trunkHeight + (family == TreeFamily.TallForest ? 4 : 3);
        if (maxTreeTop >= _settings.WorldHeight - 1)
        {
            descriptor = default;
            priority = uint.MaxValue;
            return false;
        }

        descriptor = new TreeDescriptor(worldX, column.SurfaceY + 1, worldZ, trunkHeight, family, variant);
        priority = treeHash;
        return true;
    }

    private static bool TryGetTreePlacementRules(TerrainColumnSample column, out float spawnChance, out float tallChance)
    {
        spawnChance = 0f;
        tallChance = 0f;

        if (column.Zone != TerrainZone.InlandLowlands && column.Zone != TerrainZone.InlandHighlands)
            return false;

        switch (column.Biome.BiomeId)
        {
            case BiomeId.Forest:
                spawnChance = 0.74f;
                tallChance = 0.30f;
                return true;
            case BiomeId.Hills:
                spawnChance = 0.26f;
                return true;
            case BiomeId.Grasslands:
                spawnChance = 0.18f;
                return true;
            default:
                return false;
        }
    }

    private static int GetTreeSpacing(TreeDescriptor tree)
        => tree.Family == TreeFamily.TallForest ? 6 : 5;

    private static bool IsTreeEarlier(TreeDescriptor left, TreeDescriptor right)
    {
        if (left.OriginX != right.OriginX)
            return left.OriginX < right.OriginX;
        return left.OriginZ < right.OriginZ;
    }

    private static bool IntersectsChunk(TreeDescriptor tree, int originX, int originY, int originZ, int sizeX, int sizeY, int sizeZ)
    {
        var minTreeY = tree.OriginY;
        var maxTreeY = tree.OriginY + tree.TrunkHeight + 4;
        var radius = tree.Family == TreeFamily.TallForest ? 2 : 3;

        var intersectsX = tree.OriginX + radius >= originX && tree.OriginX - radius < originX + sizeX;
        var intersectsZ = tree.OriginZ + radius >= originZ && tree.OriginZ - radius < originZ + sizeZ;
        var intersectsY = maxTreeY >= originY && minTreeY < originY + sizeY;
        return intersectsX && intersectsY && intersectsZ;
    }

    private void StampTree(VoxelChunkData chunk, TreeDescriptor tree, int originX, int originY, int originZ, int sizeX, int sizeY, int sizeZ)
    {
        for (var i = 0; i < tree.TrunkHeight; i++)
            TrySetTrunkBlock(chunk, originX, originY, originZ, sizeX, sizeY, sizeZ, tree.OriginX, tree.OriginY + i, tree.OriginZ);

        if (tree.Family == TreeFamily.TallForest)
        {
            StampTallCanopy(chunk, tree, originX, originY, originZ, sizeX, sizeY, sizeZ);
            return;
        }

        StampCompactCanopy(chunk, tree, originX, originY, originZ, sizeX, sizeY, sizeZ);
    }

    private void StampCompactCanopy(VoxelChunkData chunk, TreeDescriptor tree, int originX, int originY, int originZ, int sizeX, int sizeY, int sizeZ)
    {
        var crownBaseY = tree.OriginY + tree.TrunkHeight - 2;
        if (tree.Variant == 0)
        {
            PlaceRoundedLeafLayer(chunk, originX, originY, originZ, sizeX, sizeY, sizeZ, tree.OriginX, crownBaseY, tree.OriginZ, 2, includeCorners: false);
            PlaceRoundedLeafLayer(chunk, originX, originY, originZ, sizeX, sizeY, sizeZ, tree.OriginX, crownBaseY + 1, tree.OriginZ, 2, includeCorners: true);
            PlaceRoundedLeafLayer(chunk, originX, originY, originZ, sizeX, sizeY, sizeZ, tree.OriginX, crownBaseY + 2, tree.OriginZ, 1, includeCorners: true);
        }
        else
        {
            PlaceRoundedLeafLayer(chunk, originX, originY, originZ, sizeX, sizeY, sizeZ, tree.OriginX, crownBaseY, tree.OriginZ, 2, includeCorners: false);
            PlaceCrossLeafLayer(chunk, originX, originY, originZ, sizeX, sizeY, sizeZ, tree.OriginX, crownBaseY + 1, tree.OriginZ, 2);
            PlaceRoundedLeafLayer(chunk, originX, originY, originZ, sizeX, sizeY, sizeZ, tree.OriginX, crownBaseY + 2, tree.OriginZ, 1, includeCorners: true);
        }

        PlaceCrossLeafLayer(chunk, originX, originY, originZ, sizeX, sizeY, sizeZ, tree.OriginX, crownBaseY + 3, tree.OriginZ, 1);
        TrySetLeafBlock(chunk, originX, originY, originZ, sizeX, sizeY, sizeZ, tree.OriginX, crownBaseY + 4, tree.OriginZ);
    }

    private void StampTallCanopy(VoxelChunkData chunk, TreeDescriptor tree, int originX, int originY, int originZ, int sizeX, int sizeY, int sizeZ)
    {
        var crownBaseY = tree.OriginY + tree.TrunkHeight - 2;
        if (tree.Variant == 0)
        {
            PlaceCrossLeafLayer(chunk, originX, originY, originZ, sizeX, sizeY, sizeZ, tree.OriginX, crownBaseY, tree.OriginZ, 2);
            PlaceRoundedLeafLayer(chunk, originX, originY, originZ, sizeX, sizeY, sizeZ, tree.OriginX, crownBaseY + 1, tree.OriginZ, 1, includeCorners: true);
            PlaceCrossLeafLayer(chunk, originX, originY, originZ, sizeX, sizeY, sizeZ, tree.OriginX, crownBaseY + 2, tree.OriginZ, 1);
        }
        else
        {
            PlaceRoundedLeafLayer(chunk, originX, originY, originZ, sizeX, sizeY, sizeZ, tree.OriginX, crownBaseY, tree.OriginZ, 1, includeCorners: true);
            PlaceCrossLeafLayer(chunk, originX, originY, originZ, sizeX, sizeY, sizeZ, tree.OriginX, crownBaseY + 1, tree.OriginZ, 2);
            PlaceRoundedLeafLayer(chunk, originX, originY, originZ, sizeX, sizeY, sizeZ, tree.OriginX, crownBaseY + 2, tree.OriginZ, 1, includeCorners: false);
        }

        TrySetLeafBlock(chunk, originX, originY, originZ, sizeX, sizeY, sizeZ, tree.OriginX, crownBaseY + 3, tree.OriginZ);
        TrySetLeafBlock(chunk, originX, originY, originZ, sizeX, sizeY, sizeZ, tree.OriginX, crownBaseY + 4, tree.OriginZ);
    }

    private static void PlaceRoundedLeafLayer(VoxelChunkData chunk, int originX, int originY, int originZ, int sizeX, int sizeY, int sizeZ, int centerX, int y, int centerZ, int radius, bool includeCorners)
    {
        for (var dx = -radius; dx <= radius; dx++)
        {
            for (var dz = -radius; dz <= radius; dz++)
            {
                if (!includeCorners && Math.Abs(dx) == radius && Math.Abs(dz) == radius)
                    continue;

                TrySetLeafBlock(chunk, originX, originY, originZ, sizeX, sizeY, sizeZ, centerX + dx, y, centerZ + dz);
            }
        }
    }

    private static void PlaceCrossLeafLayer(VoxelChunkData chunk, int originX, int originY, int originZ, int sizeX, int sizeY, int sizeZ, int centerX, int y, int centerZ, int radius)
    {
        TrySetLeafBlock(chunk, originX, originY, originZ, sizeX, sizeY, sizeZ, centerX, y, centerZ);
        for (var d = 1; d <= radius; d++)
        {
            TrySetLeafBlock(chunk, originX, originY, originZ, sizeX, sizeY, sizeZ, centerX + d, y, centerZ);
            TrySetLeafBlock(chunk, originX, originY, originZ, sizeX, sizeY, sizeZ, centerX - d, y, centerZ);
            TrySetLeafBlock(chunk, originX, originY, originZ, sizeX, sizeY, sizeZ, centerX, y, centerZ + d);
            TrySetLeafBlock(chunk, originX, originY, originZ, sizeX, sizeY, sizeZ, centerX, y, centerZ - d);
        }
    }

    private static void TrySetTrunkBlock(VoxelChunkData chunk, int originX, int originY, int originZ, int sizeX, int sizeY, int sizeZ, int worldX, int worldY, int worldZ)
    {
        if (!TryWorldToLocal(originX, originY, originZ, sizeX, sizeY, sizeZ, worldX, worldY, worldZ, out var localX, out var localY, out var localZ))
            return;

        var existing = chunk.GetBlockRaw(localX, localY, localZ);
        if (existing == BLOCK_AIR || existing == BLOCK_LEAVES)
            chunk.SetBlockRaw(localX, localY, localZ, BLOCK_WOOD);
    }

    private static void TrySetLeafBlock(VoxelChunkData chunk, int originX, int originY, int originZ, int sizeX, int sizeY, int sizeZ, int worldX, int worldY, int worldZ)
    {
        if (!TryWorldToLocal(originX, originY, originZ, sizeX, sizeY, sizeZ, worldX, worldY, worldZ, out var localX, out var localY, out var localZ))
            return;

        var existing = chunk.GetBlockRaw(localX, localY, localZ);
        if (existing == BLOCK_AIR || existing == BLOCK_LEAVES)
            chunk.SetBlockRaw(localX, localY, localZ, BLOCK_LEAVES);
    }

    private static bool TryWorldToLocal(int originX, int originY, int originZ, int sizeX, int sizeY, int sizeZ, int worldX, int worldY, int worldZ, out int localX, out int localY, out int localZ)
    {
        localX = worldX - originX;
        localY = worldY - originY;
        localZ = worldZ - originZ;
        return localX >= 0 && localX < sizeX
            && localY >= 0 && localY < sizeY
            && localZ >= 0 && localZ < sizeZ;
    }

    private static int FloorDiv(int value, int divisor)
    {
        var q = value / divisor;
        var r = value % divisor;
        if (r != 0 && ((r > 0) != (divisor > 0)))
            q--;
        return q;
    }

    private static uint Hash2D(int x, int z, int seed)
    {
        unchecked
        {
            uint h = (uint)seed;
            h ^= (uint)x * 0x9E3779B9u;
            h ^= (uint)z * 0xC2B2AE35u;
            h ^= (h << 13) | (h >> 19);
            h *= 0x85EBCA6Bu;
            h ^= h >> 16;
            return h;
        }
    }

    private void StampOreVeins(VoxelChunkData chunk, int originX, int originY, int originZ, int sizeX, int sizeY, int sizeZ, OreVeinSpec spec, int seedOffset)
    {
        var minWorldX = originX - 4;
        var maxWorldX = originX + sizeX + 4;
        var minWorldY = Math.Max(spec.MinWorldY, originY - 4);
        var maxWorldY = Math.Min(spec.MaxWorldY, originY + sizeY + 4);
        var minWorldZ = originZ - 4;
        var maxWorldZ = originZ + sizeZ + 4;

        var minCellX = FloorDiv(minWorldX, spec.CellSizeXZ);
        var maxCellX = FloorDiv(maxWorldX, spec.CellSizeXZ);
        var minCellY = FloorDiv(minWorldY, spec.CellSizeY);
        var maxCellY = FloorDiv(maxWorldY, spec.CellSizeY);
        var minCellZ = FloorDiv(minWorldZ, spec.CellSizeXZ);
        var maxCellZ = FloorDiv(maxWorldZ, spec.CellSizeXZ);

        for (var cellX = minCellX; cellX <= maxCellX; cellX++)
        {
            for (var cellY = minCellY; cellY <= maxCellY; cellY++)
            {
                for (var cellZ = minCellZ; cellZ <= maxCellZ; cellZ++)
                {
                    if (!TryCreateOreVein(spec, cellX, cellY, cellZ, seedOffset, out var centerX, out var centerY, out var centerZ, out var veinSize))
                        continue;

                    StampOreVeinBlocks(chunk, originX, originY, originZ, sizeX, sizeY, sizeZ, spec.BlockId, centerX, centerY, centerZ, veinSize, seedOffset ^ (cellX * 73856093) ^ (cellY * 19349663) ^ (cellZ * 83492791));
                }
            }
        }
    }

    private bool TryCreateOreVein(OreVeinSpec spec, int cellX, int cellY, int cellZ, int seedOffset, out int centerX, out int centerY, out int centerZ, out int veinSize)
    {
        var hash = Hash3D(cellX, cellY, cellZ, seedOffset);
        var roll = ((hash >> 8) & 0xFFFFu) / 65535f;
        if (roll >= spec.SpawnChance)
        {
            centerX = centerY = centerZ = veinSize = 0;
            return false;
        }

        centerX = cellX * spec.CellSizeXZ + (int)(hash % (uint)spec.CellSizeXZ);
        centerY = cellY * spec.CellSizeY + (int)((hash >> 16) % (uint)spec.CellSizeY);
        centerZ = cellZ * spec.CellSizeXZ + (int)((hash >> 24) % (uint)spec.CellSizeXZ);

        if (centerY < spec.MinWorldY || centerY > spec.MaxWorldY)
        {
            centerX = centerY = centerZ = veinSize = 0;
            return false;
        }

        veinSize = ResolveVeinSize(spec, hash);
        return true;
    }

    private static int ResolveVeinSize(OreVeinSpec spec, uint hash)
    {
        var range = Math.Max(1, spec.MaxVeinSize - spec.MinVeinSize + 1);
        var smallRange = Math.Max(1, range - 2);
        var baseSize = spec.MinVeinSize + (int)((hash >> 4) % (uint)smallRange);
        var largeRoll = ((hash >> 20) & 0x0FFFu) / 4095f;
        if (largeRoll < spec.LargeVeinChance)
            return spec.MaxVeinSize;

        return Math.Min(spec.MaxVeinSize, baseSize);
    }

    private void StampOreVeinBlocks(VoxelChunkData chunk, int originX, int originY, int originZ, int sizeX, int sizeY, int sizeZ, byte oreBlock, int centerX, int centerY, int centerZ, int veinSize, int jitterSeed)
    {
        var radius = oreBlock == BLOCK_DIAMOND ? 2 : 3;
        var candidates = new List<(float score, int wx, int wy, int wz)>();

        for (var dx = -radius; dx <= radius; dx++)
        {
            for (var dy = -radius; dy <= radius; dy++)
            {
                for (var dz = -radius; dz <= radius; dz++)
                {
                    var distanceSq = (dx * dx) + (dy * dy) + (dz * dz);
                    if (distanceSq > radius * radius + 1)
                        continue;

                    var worldX = centerX + dx;
                    var worldY = centerY + dy;
                    var worldZ = centerZ + dz;
                    if (!TryWorldToLocal(originX, originY, originZ, sizeX, sizeY, sizeZ, worldX, worldY, worldZ, out var localX, out var localY, out var localZ))
                        continue;

                    if (chunk.GetBlockRaw(localX, localY, localZ) != BLOCK_STONE)
                        continue;

                    if (IsExposedToAirOrWaterAbove(chunk, localX, localY, localZ, sizeY))
                        continue;

                    var jitter = (Hash3D(worldX, worldY, worldZ, jitterSeed) & 0xFFu) / 255f;
                    var score = distanceSq + jitter * 0.65f;
                    candidates.Add((score, worldX, worldY, worldZ));
                }
            }
        }

        candidates.Sort(static (a, b) => a.score.CompareTo(b.score));
        var placed = 0;
        for (var i = 0; i < candidates.Count && placed < veinSize; i++)
        {
            var candidate = candidates[i];
            if (!TryWorldToLocal(originX, originY, originZ, sizeX, sizeY, sizeZ, candidate.wx, candidate.wy, candidate.wz, out var localX, out var localY, out var localZ))
                continue;

            if (chunk.GetBlockRaw(localX, localY, localZ) != BLOCK_STONE)
                continue;

            chunk.SetBlockRaw(localX, localY, localZ, oreBlock);
            placed++;
        }
    }

    private static uint Hash3D(int x, int y, int z, int seed)
    {
        unchecked
        {
            uint h = (uint)seed;
            h ^= (uint)x * 0x9E3779B9u;
            h ^= (uint)y * 0x85EBCA6Bu;
            h ^= (uint)z * 0xC2B2AE35u;
            h ^= (h << 13) | (h >> 19);
            h *= 0x27D4EB2Fu;
            h ^= h >> 15;
            return h;
        }
    }

    private static long PackColumnKey(int worldX, int worldZ)
        => ((long)worldX << 32) ^ (uint)worldZ;

    private static float Lerp(float a, float b, float t) => a + ((b - a) * t);

    public void Dispose()
    {
        if (_disposed)
            return;

        _noiseGenerator.Dispose();
        _disposed = true;
    }
}
