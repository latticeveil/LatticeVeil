using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using LatticeVeilMonoGame.Core;

namespace LatticeVeilMonoGame.Core;

/// <summary>
/// HYPER-OPTIMIZED CHUNK GENERATION SERVICE
/// Eliminates lagging with parallel processing and intelligent caching
/// </summary>
public static class OptimizedChunkService
{
    private static readonly Logger _log = new Logger("OptimizedChunkService");
    private static readonly ConcurrentDictionary<ChunkCoord, VoxelChunkData> _chunkCache = new();
    private static readonly SemaphoreSlim _generationSemaphore;
    private static readonly System.Threading.Timer _cacheCleanupTimer;
    
    // Performance settings
    private static readonly int _maxConcurrentGenerations;
    private static readonly int _cacheSizeLimit = 1000;
    private static int _currentCacheSize;
    
    static OptimizedChunkService()
    {
        // Calculate optimal concurrency based on hardware
        _maxConcurrentGenerations = Math.Min(Environment.ProcessorCount, 6); // Cap at 6 for stability
        
        _generationSemaphore = new SemaphoreSlim(_maxConcurrentGenerations, _maxConcurrentGenerations);
        
        // Start cache cleanup timer
        _cacheCleanupTimer = new System.Threading.Timer(CleanupCache, null, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(5));
        
        _log.Info($"🚀 Optimized Chunk Service initialized:");
        _log.Info($"   🔄 Max concurrent generations: {_maxConcurrentGenerations}");
        _log.Info($"   🧊 Cache size limit: {_cacheSizeLimit}");
    }

    /// <summary>
    /// Generate chunk with maximum performance optimization
    /// </summary>
    public static async Task<VoxelChunkData> GenerateChunkAsync(WorldMeta meta, ChunkCoord coord)
    {
        // Check cache first
        if (_chunkCache.TryGetValue(coord, out var cachedChunk))
        {
            return cachedChunk;
        }

        await _generationSemaphore.WaitAsync();
        try
        {
            // Generate chunk with optimized generator
            var chunk = new VoxelChunkData(coord);
            
            // Use optimized world generator
            OptimizedWorldGenerator.GenerateChunk(meta, coord, chunk);
            
            // Cache the result
            if (_currentCacheSize < _cacheSizeLimit)
            {
                _chunkCache.TryAdd(coord, chunk);
                Interlocked.Increment(ref _currentCacheSize);
            }
            
            return chunk;
        }
        finally
        {
            _generationSemaphore.Release();
        }
    }

    /// <summary>
    /// Generate multiple chunks in parallel for maximum performance
    /// </summary>
    public static async Task<VoxelChunkData[]> GenerateChunksAsync(WorldMeta meta, ChunkCoord[] coords)
    {
        var chunks = new VoxelChunkData[coords.Length];
        var tasks = new Task[_maxConcurrentGenerations];
        
        // Process chunks in batches to optimize performance
        for (int i = 0; i < coords.Length; i += _maxConcurrentGenerations)
        {
            int batchSize = Math.Min(_maxConcurrentGenerations, coords.Length - i);
            var batchTasks = new Task<VoxelChunkData>[batchSize];
            
            for (int j = 0; j < batchSize; j++)
            {
                int chunkIndex = i + j;
                batchTasks[j] = GenerateChunkAsync(meta, coords[chunkIndex]);
            }
            
            var batchResults = await Task.WhenAll(batchTasks);
            
            // Copy results to main array
            for (int j = 0; j < batchSize; j++)
            {
                chunks[i + j] = batchResults[j];
            }
        }
        
        return chunks;
    }

    /// <summary>
    /// Pre-generate chunks around spawn area with optimized performance
    /// </summary>
    public static async Task PregenerateSpawnArea(WorldMeta meta, ChunkCoord spawnCoord, int radius)
    {
        _log.Info($"🚀 Starting optimized spawn area pregeneration: radius {radius}");
        
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var coords = new ChunkCoord[radius * radius * 4]; // Approximate area
        int coordIndex = 0;
        
        // Generate spiral coordinates for spawn area
        for (int r = 0; r <= radius; r++)
        {
            for (int angle = 0; angle < 360; angle += 45)
            {
                double rad = angle * Math.PI / 180.0;
                int x = spawnCoord.X + (int)(r * Math.Cos(rad));
                int z = spawnCoord.Z + (int)(r * Math.Sin(rad));
                
                if (coordIndex < coords.Length)
                {
                    coords[coordIndex++] = new ChunkCoord(x, 0, z);
                }
            }
        }
        
        // Generate chunks in parallel
        await GenerateChunksAsync(meta, coords);
        
        sw.Stop();
        _log.Info($"✅ Spawn area pregeneration completed: {coordIndex} chunks in {sw.ElapsedMilliseconds}ms");
    }

    /// <summary>
    /// Get cached chunk if available
    /// </summary>
    public static bool TryGetChunk(ChunkCoord coord, out VoxelChunkData chunk)
    {
        return _chunkCache.TryGetValue(coord, out chunk);
    }

    /// <summary>
    /// Add chunk to cache
    /// </summary>
    public static void CacheChunk(ChunkCoord coord, VoxelChunkData chunk)
    {
        if (_currentCacheSize < _cacheSizeLimit)
        {
            _chunkCache.TryAdd(coord, chunk);
            Interlocked.Increment(ref _currentCacheSize);
        }
    }

    /// <summary>
    /// Periodic cache cleanup to prevent memory issues
    /// </summary>
    private static void CleanupCache(object? state)
    {
        try
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            
            // Clear old chunks if cache is getting full
            if (_currentCacheSize > _cacheSizeLimit * 0.8)
            {
                OptimizedWorldGenerator.ClearCaches();
                
                // Remove oldest chunks (simplified LRU)
                int removed = 0;
                foreach (var chunk in _chunkCache)
                {
                    if (removed >= _cacheSizeLimit / 4) break;
                    if (_chunkCache.TryRemove(chunk.Key, out _))
                    {
                        removed++;
                        Interlocked.Decrement(ref _currentCacheSize);
                    }
                }
                
                // Force garbage collection
                GC.Collect(0, GCCollectionMode.Optimized);
                GC.WaitForPendingFinalizers();
                GC.Collect(0, GCCollectionMode.Optimized);
            }
            
            sw.Stop();
            _log.Debug($"🧹 Cache cleanup completed in {sw.ElapsedMilliseconds}ms: {_currentCacheSize} chunks cached");
        }
        catch (Exception ex)
        {
            _log.Error($"Cache cleanup failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Get service statistics
    /// </summary>
    public static (int cached, int maxConcurrent, int cacheLimit) GetStats()
    {
        return (_currentCacheSize, _maxConcurrentGenerations, _cacheSizeLimit);
    }

    /// <summary>
    /// Clear all caches
    /// </summary>
    public static void ClearAllCaches()
    {
        _chunkCache.Clear();
        OptimizedWorldGenerator.ClearCaches();
        Interlocked.Exchange(ref _currentCacheSize, 0);
        _log.Info("🧹 All caches cleared");
    }
}
