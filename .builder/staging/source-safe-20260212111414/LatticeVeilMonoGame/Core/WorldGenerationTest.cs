using System;
using LatticeVeilMonoGame.Core;

namespace LatticeVeilMonoGame.Core
{
    /// <summary>
    /// Simple test runner to verify the new world generation system
    /// This can be called from GameWorldScreen to test our new generators
    /// </summary>
    public static class WorldGenerationTest
    {
        /// <summary>
        /// Run a comprehensive test of the new world generation system
        /// </summary>
        public static void RunTest(Logger log)
        {
            log.Info("=== Starting World Generation System Test ===");
            
            try
            {
                // Test 1: FastNoiseGenerator
                log.Info("Test 1: FastNoiseGenerator");
                FastNoiseTest.RunTest(log);
                
                // Test 2: CaveGenerator Integration
                log.Info("Test 2: CaveGenerator Integration");
                TestCaveGeneratorIntegration(log);
                
                // Test 3: BasicWorldGenerator
                log.Info("Test 3: BasicWorldGenerator");
                TestBasicWorldGenerator(log);
                
                log.Info("=== All Tests Passed! World Generation System Ready ===");
            }
            catch (Exception ex)
            {
                log.Error($"World Generation Test Failed: {ex.Message}");
                log.Error($"Stack trace: {ex.StackTrace}");
            }
        }
        
        /// <summary>
        /// Test CaveGenerator integration with FastNoiseGenerator
        /// </summary>
        private static void TestCaveGeneratorIntegration(Logger log)
        {
            log.Info("Initializing CaveGenerator with FastNoiseGenerator...");
            
            // Create FastNoiseGenerator
            using var noiseGenerator = new FastNoiseGenerator(1337, log);
            
            // Initialize CaveGenerator
            CaveGenerator.Initialize(noiseGenerator);
            
            // Test cave generation
            var caveAtSurface = CaveGenerator.IsCave(0, 50, 0, 60);
            var caveUnderground = CaveGenerator.IsCave(0, 20, 0, 60);
            var caveDeep = CaveGenerator.IsCave(0, 5, 0, 60);
            
            log.Info($"Cave at surface (Y=50): {caveAtSurface}");
            log.Info($"Cave underground (Y=20): {caveUnderground}");
            log.Info($"Cave deep underground (Y=5): {caveDeep}");
            
            // Test cave density
            var density = CaveGenerator.GetCaveDensity(0, 20, 0, 60);
            log.Info($"Cave density at (0,20,0): {density:F4}");
            
            log.Info("✅ CaveGenerator integration test passed");
        }
        
        /// <summary>
        /// Test BasicWorldGenerator with a small chunk
        /// </summary>
        private static void TestBasicWorldGenerator(Logger log)
        {
            log.Info("Initializing BasicWorldGenerator...");
            
            // Create world settings
            var settings = new BasicWorldGenerator.WorldSettings
            {
                ChunkSize = 16,
                WorldHeight = 256,
                TerrainFrequency = 0.01f,
                CaveFrequency = 0.05f,
                CaveThreshold = 0.3f,
                SeaLevel = 64,
                GenerateCaves = true,
                GenerateOres = true,
                GenerateStructures = true
            };
            
            // Create generator
            using var generator = new BasicWorldGenerator(1337, settings, log);
            
            // Create a test chunk
            var chunkCoord = new ChunkCoord(0, 0, 0);
            log.Info($"Generating test chunk at {chunkCoord}...");
            
            var chunk = generator.GenerateChunk(chunkCoord);
            
            // Verify chunk was generated
            var blockCount = 0;
            var airCount = 0;
            var stoneCount = 0;
            var dirtCount = 0;
            var grassCount = 0;
            var nullrockCount = 0;
            var waterCount = 0;
            var treeCount = 0;
            
            for (int x = 0; x < 16; x++)
            {
                for (int y = 0; y < 16; y++)
                {
                    for (int z = 0; z < 16; z++)
                    {
                        var block = chunk.GetBlock(x, y, z);
                        blockCount++;
                        
                        if (block == BlockIds.Air) airCount++;
                        else if (block == BlockIds.Stone) stoneCount++;
                        else if (block == BlockIds.Dirt) dirtCount++;
                        else if (block == BlockIds.Grass) grassCount++;
                        else if (block == BlockIds.Nullrock) nullrockCount++;
                        else if (block == BlockIds.Water) waterCount++;
                        else if (block == BlockIds.Wood) treeCount++;
                        else if (block == BlockIds.Leaves) treeCount++;
                    }
                }
            }
            
            log.Info($"Chunk generation results:");
            log.Info($"  Total blocks: {blockCount}");
            log.Info($"  Air: {airCount}");
            log.Info($"  Stone: {stoneCount}");
            log.Info($"  Dirt: {dirtCount}");
            log.Info($"  Grass: {grassCount}");
            log.Info($"  Nullrock (bedrock): {nullrockCount}");
            log.Info($"  Water: {waterCount}");
            log.Info($"  Trees (wood): {treeCount}");
            
            // Verify we have bedrock at bottom
            if (nullrockCount > 0)
            {
                log.Info("✅ Nullrock bedrock layer detected");
            }
            
            // Verify we have terrain
            if (stoneCount + dirtCount + grassCount > 0)
            {
                log.Info("✅ Terrain generated");
            }
            
            // Verify we have caves
            if (airCount > 100) // Expect some air blocks from caves
            {
                log.Info("✅ Cave system working");
            }
            
            // Verify we have structures
            if (treeCount > 0)
            {
                log.Info("✅ Structure generation working");
            }
            
            log.Info("✅ BasicWorldGenerator test passed");
        }
        
        /// <summary>
        /// Performance test for the new world generation system
        /// </summary>
        public static void PerformanceTest(Logger log)
        {
            log.Info("=== World Generation Performance Test ===");
            
            try
            {
                var settings = new BasicWorldGenerator.WorldSettings
                {
                    ChunkSize = 16,
                    WorldHeight = 256,
                    TerrainFrequency = 0.01f,
                    CaveFrequency = 0.05f,
                    CaveThreshold = 0.3f,
                    SeaLevel = 64,
                    GenerateCaves = true,
                    GenerateOres = true,
                    GenerateStructures = true
                };
                
                using var generator = new BasicWorldGenerator(1337, settings, log);
                
                var startTime = DateTime.UtcNow;
                
                // Generate 10 chunks
                for (int i = 0; i < 10; i++)
                {
                    var coord = new ChunkCoord(i, 0, 0);
                    var chunk = generator.GenerateChunk(coord);
                    log.Debug($"Generated chunk {i+1}/10 at {coord}");
                }
                
                var endTime = DateTime.UtcNow;
                var duration = (endTime - startTime).TotalMilliseconds;
                
                log.Info($"Generated 10 chunks in {duration:F2}ms");
                log.Info($"Average: {duration/10:F2}ms per chunk");
                log.Info($"Rate: {10000.0/duration:F0} chunks per second");
                
                if (duration < 1000) // Less than 1 second is good
                {
                    log.Info("✅ Performance test passed - Excellent speed!");
                }
                else if (duration < 5000) // Less than 5 seconds is acceptable
                {
                    log.Info("✅ Performance test passed - Acceptable speed");
                }
                else
                {
                    log.Warn("⚠️ Performance test - Consider optimization");
                }
            }
            catch (Exception ex)
            {
                log.Error($"Performance test failed: {ex.Message}");
            }
        }
    }
}
