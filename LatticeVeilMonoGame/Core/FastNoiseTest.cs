using System;
using LatticeVeilMonoGame.Core;

namespace LatticeVeilMonoGame.Core;

/// <summary>
/// Simple test class to verify FastNoiseGenerator functionality
/// This can be used to test noise generation before integrating with world generation
/// </summary>
public static class FastNoiseTest
{
    /// <summary>
    /// Test basic noise generation functionality
    /// </summary>
    public static void RunTest(Logger log)
    {
        log.Info("=== FastNoiseGenerator Test Starting ===");
        
        try
        {
            // Create generator with test seed
            using var generator = new FastNoiseGenerator(1337, log);
            
            // Test terrain height generation
            var terrainHeight = generator.GetTerrainHeight(0, 0);
            log.Info($"Terrain height at (0,0): {terrainHeight:F4}");
            
            // Test cave density
            var caveDensity = generator.GetCaveDensity(0, 50, 0);
            log.Info($"Cave density at (0,50,0): {caveDensity:F4}");
            
            // Test temperature and humidity
            var temperature = generator.GetTemperature(0, 0);
            var humidity = generator.GetHumidity(0, 0);
            log.Info($"Temperature at (0,0): {temperature:F4}");
            log.Info($"Humidity at (0,0): {humidity:F4}");
            
            // Test ore density
            var oreDensity = generator.GetOreDensity(0, 20, 0);
            log.Info($"Ore density at (0,20,0): {oreDensity:F4}");
            
            // Test structure value
            var structureValue = generator.GetStructureValue(0, 0);
            log.Info($"Structure value at (0,0): {structureValue:F4}");
            
            // Test array generation
            var noise2D = generator.GenerateNoise2D(0, 0, 16, 16, 0.1f);
            log.Info($"Generated 2D noise array: {noise2D.Length} values");
            log.Info($"Sample 2D noise[0]: {noise2D[0]:F4}");
            
            var noise3D = generator.GenerateNoise3D(0, 0, 0, 8, 8, 8, 0.1f);
            log.Info($"Generated 3D noise array: {noise3D.Length} values");
            log.Info($"Sample 3D noise[0]: {noise3D[0]:F4}");
            
            // Test value ranges
            bool allValid = true;
            foreach (var value in noise2D)
            {
                if (value < -1.0f || value > 1.0f)
                {
                    allValid = false;
                    break;
                }
            }
            
            log.Info($"2D noise values in valid range [-1,1]: {(allValid ? "YES" : "NO")}");
            
            // Test reproducibility
            var terrainHeight2 = generator.GetTerrainHeight(0, 0);
            var reproducible = Math.Abs(terrainHeight - terrainHeight2) < 0.0001f;
            log.Info($"Reproducibility test: {(reproducible ? "PASS" : "FAIL")}");
            
            log.Info("=== FastNoiseGenerator Test Completed Successfully ===");
        }
        catch (Exception ex)
        {
            log.Error($"FastNoiseGenerator test failed: {ex.Message}");
            log.Error($"Stack trace: {ex.StackTrace}");
        }
    }
    
    /// <summary>
    /// Performance test for noise generation
    /// </summary>
    public static void PerformanceTest(Logger log)
    {
        log.Info("=== FastNoiseGenerator Performance Test ===");
        
        try
        {
            using var generator = new FastNoiseGenerator(1337, log);
            
            var startTime = DateTime.UtcNow;
            
            // Generate 1000 terrain height samples
            for (int i = 0; i < 1000; i++)
            {
                var height = generator.GetTerrainHeight(i * 0.1f, i * 0.1f);
            }
            
            var terrainTime = DateTime.UtcNow;
            var terrainDuration = (terrainTime - startTime).TotalMilliseconds;
            
            // Generate 1000 cave density samples
            for (int i = 0; i < 1000; i++)
            {
                var density = generator.GetCaveDensity(i * 0.1f, 50, i * 0.1f);
            }
            
            var caveTime = DateTime.UtcNow;
            var caveDuration = (caveTime - terrainTime).TotalMilliseconds;
            
            // Generate one 32x32x32 chunk
            var chunkNoise = generator.GenerateNoise3D(0, 0, 0, 32, 32, 32, 0.05f);
            
            var endTime = DateTime.UtcNow;
            var chunkDuration = (endTime - caveTime).TotalMilliseconds;
            
            log.Info($"Terrain generation (1000 samples): {terrainDuration:F2} ms");
            log.Info($"Cave generation (1000 samples): {caveDuration:F2} ms");
            log.Info($"Chunk generation (32x32x32): {chunkDuration:F2} ms");
            log.Info($"Total test time: {(endTime - startTime).TotalMilliseconds:F2} ms");
            
            // Performance expectations
            log.Info("Performance expectations:");
            log.Info("- Terrain: < 10ms for 1000 samples");
            log.Info("- Caves: < 15ms for 1000 samples");
            log.Info("- Chunk: < 50ms for 32x32x32");
            
            var terrainGood = terrainDuration < 10;
            var caveGood = caveDuration < 15;
            var chunkGood = chunkDuration < 50;
            
            log.Info($"Performance results: Terrain {(terrainGood ? "GOOD" : "SLOW")}, " +
                     $"Caves {(caveGood ? "GOOD" : "SLOW")}, Chunk {(chunkGood ? "GOOD" : "SLOW")}");
            
            log.Info("=== Performance Test Completed ===");
        }
        catch (Exception ex)
        {
            log.Error($"Performance test failed: {ex.Message}");
        }
    }
}
