using System;

namespace LatticeVeilMonoGame.Core;

public readonly struct BiomeSample
{
    public BiomeSample(
        BiomeId biomeId,
        float oceanWeight,
        float desertWeight,
        float hillsWeight,
        float forestWeight,
        float effectiveDesertWeight,
        float temperature,
        float humidity,
        float continentalness)
    {
        BiomeId = biomeId;
        OceanWeight = Math.Clamp(oceanWeight, 0f, 1f);
        DesertWeight = Math.Clamp(desertWeight, 0f, 1f);
        HillsWeight = Math.Clamp(hillsWeight, 0f, 1f);
        ForestWeight = Math.Clamp(forestWeight, 0f, 1f);
        EffectiveDesertWeight = Math.Clamp(effectiveDesertWeight, 0f, 1f);
        Temperature = Math.Clamp(temperature, 0f, 1f);
        Humidity = Math.Clamp(humidity, 0f, 1f);
        Continentalness = Math.Clamp(continentalness, 0f, 1f);
    }

    public BiomeId BiomeId { get; }
    public float OceanWeight { get; }
    public float DesertWeight { get; }
    public float HillsWeight { get; }
    public float ForestWeight { get; }
    public float EffectiveDesertWeight { get; }
    public float Temperature { get; }
    public float Humidity { get; }
    public float Continentalness { get; }
}

/// <summary>
/// Deterministic biome generator for v2 worlds
/// Provides biome lookup without requiring biome_catalog.bin
/// </summary>
public static class BiomeService
{
    // Biome thresholds - these must match the terrain generation logic
    private const float OceanColumnThreshold = 0.78f;
    private const float DesertBiomeThreshold = 0.46f;
    private const float HillsBiomeThreshold = 0.52f;
    private const float ForestBiomeThreshold = 0.50f;

    public static BiomeSample GetSampleAt(WorldMeta meta, int worldX, int worldZ)
    {
        var seed = meta?.Seed ?? 0;
        if (seed == 0)
            seed = 1337;
        return GetSampleAt(seed, worldX, worldZ);
    }

    public static BiomeSample GetSampleAt(int seed, int worldX, int worldZ)
    {
        var desertWeight = ComputeDesertWeight(worldX, worldZ, seed);
        var oceanWeight = ComputeOceanWeight(worldX, worldZ, seed);
        var effectiveDesertWeight = Math.Clamp(desertWeight * (1f - oceanWeight * 0.9f), 0f, 1f);
        var temperature = Normalize01(FractalValueNoise2D(worldX * 0.0016f, worldZ * 0.0016f, seed + 5011, octaves: 3, lacunarity: 2.0f, persistence: 0.5f));
        var humidity = Normalize01(FractalValueNoise2D((worldX + 2048) * 0.0014f, (worldZ - 2048) * 0.0014f, seed + 6011, octaves: 3, lacunarity: 2.0f, persistence: 0.52f));
        var hillsWeight = ComputeHillsWeight(worldX, worldZ, seed, temperature, humidity, oceanWeight, effectiveDesertWeight);
        var forestWeight = ComputeForestWeight(temperature, humidity, oceanWeight, effectiveDesertWeight);

        var biome = BiomeId.Grasslands;
        if (oceanWeight >= OceanColumnThreshold)
            biome = BiomeId.Ocean;
        else if (effectiveDesertWeight >= DesertBiomeThreshold)
            biome = BiomeId.Desert;
        else if (hillsWeight >= HillsBiomeThreshold)
            biome = BiomeId.Hills;
        else if (forestWeight >= ForestBiomeThreshold)
            biome = BiomeId.Forest;

        var continentalness = Math.Clamp(1f - oceanWeight, 0f, 1f);

        return new BiomeSample(
            biomeId: biome,
            oceanWeight: oceanWeight,
            desertWeight: desertWeight,
            hillsWeight: hillsWeight,
            forestWeight: forestWeight,
            effectiveDesertWeight: effectiveDesertWeight,
            temperature: temperature,
            humidity: humidity,
            continentalness: continentalness);
    }

    /// <summary>
    /// Get biome ID at world XZ coordinates deterministically
    /// </summary>
    public static BiomeId GetBiomeIdAtWorldXZ(WorldMeta meta, int worldX, int worldZ)
    {
        return GetSampleAt(meta, worldX, worldZ).BiomeId;
    }

    /// <summary>
    /// Get precipitation type for a biome
    /// </summary>
    public static PrecipitationType GetPrecipitationType(BiomeId biome)
    {
        return biome switch
        {
            BiomeId.Desert => PrecipitationType.None,
            BiomeId.Ocean => PrecipitationType.Rain,
            BiomeId.Hills => PrecipitationType.Rain,
            BiomeId.Forest => PrecipitationType.Rain,
            _ => PrecipitationType.Rain
        };
    }

    /// <summary>
    /// Check if rain is allowed at world coordinates
    /// </summary>
    public static bool IsRainAllowedAt(WorldMeta meta, int worldX, int worldZ)
    {
        var biome = GetSampleAt(meta, worldX, worldZ).BiomeId;
        return GetPrecipitationType(biome) != PrecipitationType.None;
    }

    /// <summary>
    /// Parse biome token string to BiomeId
    /// </summary>
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
            case "forest":
            case "woods":
            case "woodland":
            case "trees":
                biome = BiomeId.Forest;
                return true;
            case "hill":
            case "hills":
            case "highlands":
                biome = BiomeId.Hills;
                return true;
            default:
                return false;
        }
    }

    /// <summary>
    /// Get biome name from BiomeId
    /// </summary>
    public static string GetBiomeName(BiomeId biome)
    {
        return biome switch
        {
            BiomeId.Desert => "Desert",
            BiomeId.Ocean => "Ocean",
            BiomeId.Hills => "Hills",
            BiomeId.Forest => "Forest",
            BiomeId.Grasslands => "Grasslands",
            _ => "Unknown"
        };
    }

    private static float ComputeForestWeight(float temperature, float humidity, float oceanWeight, float effectiveDesertWeight)
    {
        var humidityBand = ComputeBandWeight(humidity, center: 0.62f, halfWidth: 0.28f);
        var temperatureBand = ComputeBandWeight(temperature, center: 0.58f, halfWidth: 0.26f);
        var inlandGate = Math.Clamp(1f - oceanWeight * 1.35f, 0f, 1f);
        var desertGate = Math.Clamp(1f - effectiveDesertWeight * 1.8f, 0f, 1f);
        var combined = humidityBand * 0.62f + temperatureBand * 0.38f;
        return Math.Clamp(combined * inlandGate * desertGate, 0f, 1f);
    }

    private static float ComputeHillsWeight(
        int wx,
        int wz,
        int seed,
        float temperature,
        float humidity,
        float oceanWeight,
        float effectiveDesertWeight)
    {
        var macro = Normalize01(FractalValueNoise2D(wx * 0.00105f, wz * 0.00105f, seed + 7011, octaves: 3, lacunarity: 2.0f, persistence: 0.54f));
        var medium = Normalize01(FractalValueNoise2D((wx + 4096) * 0.0028f, (wz - 4096) * 0.0028f, seed + 7021, octaves: 2, lacunarity: 2.05f, persistence: 0.52f));
        var micro = Normalize01(FractalValueNoise2D(wx * 0.0067f, wz * 0.0067f, seed + 7031, octaves: 2, lacunarity: 2.0f, persistence: 0.5f));

        var ridgeSignal = macro * 0.57f + medium * 0.30f + micro * 0.13f;
        var ridgeBand = ComputeBandWeight(ridgeSignal, center: 0.68f, halfWidth: 0.28f);

        var inlandGate = Math.Clamp(1f - oceanWeight * 1.75f, 0f, 1f);
        var desertGate = Math.Clamp(1f - effectiveDesertWeight * 1.45f, 0f, 1f);
        var humidityGate = ComputeBandWeight(humidity, center: 0.52f, halfWidth: 0.42f);
        var temperatureGate = ComputeBandWeight(temperature, center: 0.56f, halfWidth: 0.40f);

        var climateGate = humidityGate * 0.6f + temperatureGate * 0.4f;
        var combined = ridgeBand * 0.76f + macro * 0.16f + medium * 0.08f;
        var boosted = combined * 1.28f + ridgeBand * 0.10f;
        return Math.Clamp(boosted * inlandGate * desertGate * climateGate, 0f, 1f);
    }

    private static float ComputeDesertWeight(int wx, int wz, int seed)
    {
        // Blend wide climate regions with controlled pocketing so deserts spawn consistently.
        const int smoothOffset = 24;
        var center = ComputeRawDesertClimateSignal(wx, wz, seed);
        var north = ComputeRawDesertClimateSignal(wx, wz - smoothOffset, seed);
        var south = ComputeRawDesertClimateSignal(wx, wz + smoothOffset, seed);
        var east = ComputeRawDesertClimateSignal(wx + smoothOffset, wz, seed);
        var west = ComputeRawDesertClimateSignal(wx - smoothOffset, wz, seed);
        var climate = center * 0.44f + (north + south + east + west) * 0.14f;

        // Macro bands are the primary source of desert regions.
        var macroWeight = ComputeBandWeight(climate, center: 0.30f, halfWidth: 0.15f);

        // Medium pockets add variation while preserving larger biome identity.
        var mediumPocketNoise = FractalValueNoise2D(wx * 0.0026f, wz * 0.0026f, seed + 941, octaves: 2, lacunarity: 2.0f, persistence: 0.5f);
        var mediumPocketWeight = ComputeBandWeight(mediumPocketNoise, center: 0.67f, halfWidth: 0.09f) * 0.38f;

        // Small pockets remain possible but do not dominate.
        var smallPocketNoise = FractalValueNoise2D(wx * 0.0064f, wz * 0.0064f, seed + 2141, octaves: 2, lacunarity: 2.0f, persistence: 0.53f);
        var smallPocketWeight = ComputeBandWeight(smallPocketNoise, center: 0.76f, halfWidth: 0.06f) * 0.18f;

        // Wet climates suppress pockets so grasslands still remain dominant overall.
        var pocketClimateGate = ComputeBandWeight(climate, center: 0.18f, halfWidth: 0.34f);
        mediumPocketWeight *= Lerp(0.45f, 1f, pocketClimateGate);
        smallPocketWeight *= Lerp(0.35f, 0.9f, pocketClimateGate);

        var combined = macroWeight * 0.92f + mediumPocketWeight * 0.24f + smallPocketWeight * 0.08f;
        combined = MathF.Max(combined, macroWeight * 0.92f);
        return Math.Clamp(combined, 0f, 1f);
    }

    private static float ComputeOceanWeight(int wx, int wz, int seed)
    {
        // Ocean macro signal tuned for medium-size basins with clear coastlines.
        const int smoothOffset = 20;
        var center = ComputeRawOceanClimateSignal(wx, wz, seed);
        var north = ComputeRawOceanClimateSignal(wx, wz - smoothOffset, seed);
        var south = ComputeRawOceanClimateSignal(wx, wz + smoothOffset, seed);
        var east = ComputeRawOceanClimateSignal(wx + smoothOffset, wz, seed);
        var west = ComputeRawOceanClimateSignal(wx - smoothOffset, wz, seed);
        var continental = center * 0.46f + (north + south + east + west) * 0.135f;

        // Convert to [0..1], then derive ocean primarily from low continental values.
        var continental01 = Math.Clamp((continental + 1f) * 0.5f, 0f, 1f);
        var macroOcean = ComputeBandWeight(1f - continental01, center: 0.68f, halfWidth: 0.21f);

        // Add medium-scale variation for coastline irregularity while keeping basins recognizable.
        var mediumNoise = FractalValueNoise2D(wx * 0.0043f, wz * 0.0043f, seed + 727, octaves: 2, lacunarity: 2.0f, persistence: 0.5f);
        var mediumOcean = ComputeBandWeight(mediumNoise, center: 0.61f, halfWidth: 0.12f) * 0.31f;

        // Small-scale detail for interesting beaches and coves.
        var smallNoise = FractalValueNoise2D(wx * 0.017f, wz * 0.017f, seed + 1087, octaves: 1, lacunarity: 2.0f, persistence: 0.5f);
        var smallOcean = ComputeBandWeight(smallNoise, center: 0.55f, halfWidth: 0.08f) * 0.13f;

        var combined = macroOcean * 0.91f + mediumOcean * 0.34f + smallOcean * 0.11f;
        combined = MathF.Max(combined, macroOcean * 0.91f);
        return Math.Clamp(combined, 0f, 1f);
    }

    private static float ComputeRawDesertClimateSignal(int x, int z, int seed)
    {
        var large = ValueNoise2D(x * 0.0007f, z * 0.0007f, seed + 301);
        var medium = ValueNoise2D(x * 0.0031f, z * 0.0031f, seed + 302);
        var small = ValueNoise2D(x * 0.0089f, z * 0.0089f, seed + 303);
        return large * 0.62f + medium * 0.27f + small * 0.11f;
    }

    private static float ComputeRawOceanClimateSignal(int x, int z, int seed)
    {
        var large = ValueNoise2D(x * 0.0009f, z * 0.0009f, seed + 401);
        var medium = ValueNoise2D(x * 0.0041f, z * 0.0041f, seed + 402);
        var small = ValueNoise2D(x * 0.0123f, z * 0.0123f, seed + 403);
        return large * 0.58f + medium * 0.31f + small * 0.11f;
    }

    private static float ComputeBandWeight(float signal, float center, float halfWidth)
    {
        var distance = Math.Abs(signal - center);
        return Math.Clamp(1f - (distance / halfWidth), 0f, 1f);
    }

    private static float Normalize01(float v) => Math.Clamp((v + 1f) * 0.5f, 0f, 1f);

    private static float Lerp(float a, float b, float t) => a + (b - a) * Math.Clamp(t, 0f, 1f);

    // Noise functions - these should match the ones in VoxelWorld
    private static float ValueNoise2D(float x, float y, int seed)
    {
        // Simple hash-based noise - this should match the implementation in VoxelWorld
        int xi = (int)MathF.Floor(x) & 255;
        int yi = (int)MathF.Floor(y) & 255;
        
        float xf = x - MathF.Floor(x);
        float yf = y - MathF.Floor(y);
        
        float u = Fade(xf);
        float v = Fade(yf);
        
        int a = Hash(xi, yi) + seed;
        int b = Hash(xi + 1, yi) + seed;
        int c = Hash(xi, yi + 1) + seed;
        int d = Hash(xi + 1, yi + 1) + seed;
        
        float x1 = Lerp(Grad(a, xf, yf), Grad(b, xf - 1f, yf), u);
        float x2 = Lerp(Grad(c, xf, yf - 1f), Grad(d, xf - 1f, yf - 1f), u);
        
        return Lerp(x1, x2, v);
    }

    private static float FractalValueNoise2D(float x, float y, int seed, int octaves, float lacunarity, float persistence)
    {
        float value = 0f;
        float amplitude = 1f;
        float frequency = 1f;
        float maxValue = 0f;

        for (int i = 0; i < octaves; i++)
        {
            value += ValueNoise2D(x * frequency, y * frequency, seed + i) * amplitude;
            maxValue += amplitude;
            amplitude *= persistence;
            frequency *= lacunarity;
        }

        return value / maxValue;
    }

    private static float Fade(float t) => t * t * t * (t * (t * 6f - 15f) + 10f);

    private static int Hash(int x, int y)
    {
        int h = x * 374761393 + y * 668265263;
        h = (h ^ (h >> 13)) * 1274126177;
        return h;
    }

    private static float Grad(int hash, float x, float y)
    {
        int h = hash & 3;
        float u = h < 2 ? x : y;
        float v = h < 2 ? y : x;
        return ((h & 1) == 0 ? u : -u) + ((h & 2) == 0 ? v : -v);
    }

    private static int FloorDiv(int a, int b)
    {
        return Math.DivRem(a, b, out var rem) - (rem < 0 ? 1 : 0);
    }
}

/// <summary>
/// Precipitation types for weather system
/// </summary>
public enum PrecipitationType
{
    None,
    Rain,
    Snow
}
