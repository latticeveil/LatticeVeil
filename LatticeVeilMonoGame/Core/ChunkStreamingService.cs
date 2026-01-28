using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.Channels;

namespace LatticeVeilMonoGame.Core;

/// <summary>
/// Async chunk streaming service with load/generate, mesh, and save pipelines
/// </summary>
public sealed class ChunkStreamingService : IDisposable
{
    private readonly VoxelWorld _world;
    private readonly Logger _log;
    private readonly CubeNetAtlas _atlas;
    private readonly string _chunksDir;
    private readonly int _maxConcurrency;

    // Job queues - using C5 collections for better performance
    private readonly Channel<ChunkLoadJob> _loadQueue = Channel.CreateBounded<ChunkLoadJob>(1000);
    private readonly Channel<MeshBuildJob> _meshQueue = Channel.CreateBounded<MeshBuildJob>(1000);
    private readonly Channel<ChunkSaveJob> _saveQueue = Channel.CreateBounded<ChunkSaveJob>(1000);
    
    // Results queues - using C5 collections
    private readonly Channel<ChunkLoadResult> _loadResults = Channel.CreateUnbounded<ChunkLoadResult>();
    private readonly Channel<MeshBuildResult> _meshResults = Channel.CreateUnbounded<MeshBuildResult>();
    private readonly Channel<ChunkSaveResult> _saveResults = Channel.CreateUnbounded<ChunkSaveResult>();

    // Worker threads
    private readonly Task[] _loadWorkers;
    private readonly Task[] _meshWorkers;
    private readonly Task _saveWorker;

    // Performance tracking
    private int _loadJobsProcessed;
    private int _meshJobsProcessed;
    private int _saveJobsProcessed;
    private DateTime _lastStatsTime = DateTime.UtcNow;

    // Save throttling
    private readonly TimeSpan _saveThrottleInterval = TimeSpan.FromMilliseconds(50); // Max 20 saves/second
    private DateTime _lastSaveTime = DateTime.MinValue;

    // Mesh failure tracking
    private readonly Dictionary<ChunkCoord, int> _meshFailureCounts = new();
    
    // B) Tiered priority system
    private bool _prewarmMode;
    private int _prewarmConcurrency;
    
    // 2) Progress tracking stats
    private int _meshFailCount;
    private readonly object _statsLock = new();
    
    // Timeout detection
    private DateTime _lastProgressTime = DateTime.UtcNow;
    private int _lastProcessedCount;
    
    // Missing fields for stats
    private int _loadJobsInFlight;
    private int _meshJobsInFlight;
    
    // Apply queue for completed results
    private readonly ConcurrentQueue<MeshBuildResult> _applyQueue = new();

    public ChunkStreamingService(VoxelWorld world, CubeNetAtlas atlas, Logger log)
    {
        _world = world;
        _log = log;
        _atlas = atlas;
        _chunksDir = world.ChunksDir;
        _maxConcurrency = Math.Max(1, Environment.ProcessorCount - 1);
        _prewarmConcurrency = _maxConcurrency; // Full CPU for prewarm
        _prewarmMode = false;

        // D) Start with conservative concurrency, will be boosted for prewarm
        var loadWorkerCount = Math.Min(4, _maxConcurrency);
        var meshWorkerCount = Math.Min(2, Math.Max(1, _maxConcurrency / 2));
        
        // Start worker threads
        _loadWorkers = new Task[loadWorkerCount];
        for (int i = 0; i < loadWorkerCount; i++)
        {
            _loadWorkers[i] = Task.Run(LoadWorkerLoop);
        }
        _meshWorkers = new Task[meshWorkerCount];
        for (int i = 0; i < meshWorkerCount; i++)
        {
            _meshWorkers[i] = Task.Run(MeshWorkerLoop);
        }
        _saveWorker = Task.Run(SaveWorkerLoop);
        
        _log.Info($"ChunkStreamingService started: {_maxConcurrency} load workers, {_meshWorkers.Length} mesh workers, 1 save worker - Using C5 Channels for better performance");
    }

    #region Job Submission

    public void EnqueueLoadJob(ChunkCoord coord, int priority = 0)
    {
        _loadQueue.Writer.TryWrite(new ChunkLoadJob { Coord = coord, Priority = priority, Timestamp = DateTime.UtcNow });
    }

    public void EnqueueMeshJob(ChunkCoord coord, VoxelChunkData chunk, int priority = 0)
    {
        _meshQueue.Writer.TryWrite(new MeshBuildJob { Coord = coord, Chunk = chunk, Priority = priority, Timestamp = DateTime.UtcNow });
    }

    public void EnqueueSaveJob(ChunkCoord coord, byte[] blocks, bool urgent = false)
    {
        _saveQueue.Writer.TryWrite(new ChunkSaveJob { Coord = coord, Blocks = blocks, Urgent = urgent, Timestamp = DateTime.UtcNow });
    }

    #endregion

    #region Result Processing (Main Thread)

    public int ProcessLoadResults(Action<ChunkLoadResult> onResult, int maxResults = 10)
    {
        int processed = 0;
        while (processed < maxResults && _loadResults.Reader.TryRead(out var result))
        {
            onResult(result);
            Interlocked.Decrement(ref _loadJobsInFlight);
            processed++;
        }
        
        // Update progress tracking
        if (processed > 0)
        {
            _lastProgressTime = DateTime.UtcNow;
            _lastProcessedCount += processed;
        }
        
        return processed;
    }

    public int ProcessMeshResults(Action<MeshBuildResult> onResult, int maxResults = 10)
    {
        int processed = 0;
        while (processed < maxResults && _meshResults.Reader.TryRead(out var result))
        {
            onResult(result);
            Interlocked.Decrement(ref _meshJobsInFlight);
            processed++;
        }
        
        // Update progress tracking
        if (processed > 0)
        {
            _lastProgressTime = DateTime.UtcNow;
            _lastProcessedCount += processed;
        }
        
        return processed;
    }

    public bool IsStuck(TimeSpan timeout)
    {
        var timeSinceProgress = DateTime.UtcNow - _lastProgressTime;
        var hasWork = _loadQueue.Reader.Count > 0 || _meshQueue.Reader.Count > 0 || 
                     _loadJobsInFlight > 0 || _meshJobsInFlight > 0;
        
        return hasWork && timeSinceProgress > timeout;
    }

    public int ProcessSaveResults(Action<ChunkSaveResult> onResult, int maxResults = 5)
    {
        int processed = 0;
        while (processed < maxResults && _saveResults.Reader.TryRead(out var result))
        {
            onResult(result);
            processed++;
        }
        return processed;
    }

    #endregion

    #region Prewarm Mode Control

    public void SetPrewarmMode(bool enabled)
    {
        if (_prewarmMode == enabled)
            return;

        _prewarmMode = enabled;
        _log.Info($"Prewarm mode {(enabled ? "ENABLED" : "DISABLED")} - concurrency: {(enabled ? _prewarmConcurrency : "conservative")}");
    }

    // 2) Progress tracking stats
    public (int queuedLoads, int queuedMeshes, int queuedApplies, int inflightLoads, int inflightMeshes, int failCount) GetStats()
    {
        lock (_statsLock)
        {
            return (
                _loadQueue.Reader.Count,
                _meshQueue.Reader.Count,
                _applyQueue.Count,
                _loadJobsInFlight,
                _meshJobsInFlight,
                _meshFailCount
            );
        }
    }

    #endregion

    #region Worker Loops

    private async Task LoadWorkerLoop()
    {
        try
        {
            while (!_disposed)
            {
                if (_loadQueue.Reader.TryRead(out var job))
                {
                    Interlocked.Increment(ref _loadJobsInFlight);
                    var result = await ProcessLoadJobAsync(job);
                    if (result != null)
                    {
                        _loadResults.Writer.TryWrite(result);
                        Interlocked.Increment(ref _loadJobsProcessed);
                    }
                    else
                    {
                        Interlocked.Decrement(ref _loadJobsInFlight);
                    }
                }
                else
                {
                    await Task.Delay(1); // Yield when no work
                }
            }
        }
        catch (Exception ex)
        {
            _log.Error($"Load worker crashed: {ex.Message}");
        }
    }

    private async Task MeshWorkerLoop()
    {
        try
        {
            while (!_disposed)
            {
                if (_meshQueue.Reader.TryRead(out var job))
                {
                    Interlocked.Increment(ref _meshJobsInFlight);
                    var result = await ProcessMeshJobAsync(job);
                    if (result != null)
                    {
                        _meshResults.Writer.TryWrite(result);
                        Interlocked.Increment(ref _meshJobsProcessed);
                    }
                    else
                    {
                        Interlocked.Decrement(ref _meshJobsInFlight);
                    }
                }
                else
                {
                    await Task.Delay(1); // Yield when no work
                }
            }
        }
        catch (Exception ex)
        {
            _log.Error($"Mesh worker crashed: {ex.Message}");
        }
    }

    private async Task SaveWorkerLoop()
    {
        try
        {
            while (!_disposed)
            {
                if (_saveQueue.Reader.TryRead(out var job))
                {
                    await ProcessSaveJobAsync(job);
                    Interlocked.Increment(ref _saveJobsProcessed);
                }
                else
                {
                    await Task.Delay(10); // Save worker can sleep longer
                }
            }
        }
        catch (Exception ex)
        {
            _log.Error($"Save worker crashed: {ex.Message}");
        }
    }

    #endregion

    #region Job Processing

    private async Task<ChunkLoadResult?> ProcessLoadJobAsync(ChunkLoadJob job)
    {
        try
        {
            // Try load from disk first
            var chunkPath = Path.Combine(_chunksDir, $"chunk_{job.Coord.X}_{job.Coord.Y}_{job.Coord.Z}.bin");
            
            if (File.Exists(chunkPath))
            {
                var chunk = await Task.Run(() => VoxelChunkData.Load(chunkPath, _log));
                if (chunk != null)
                {
                    return new ChunkLoadResult { Coord = job.Coord, Chunk = chunk, Source = LoadSource.Disk, Success = true };
                }
            }

            // Generate new chunk
            var generatedChunk = await Task.Run(() => {
                var chunk = new VoxelChunkData(job.Coord);
                VoxelWorldGenerator.GenerateChunk(_world.Meta, job.Coord, chunk);
                return chunk;
            });
            return new ChunkLoadResult { Coord = job.Coord, Chunk = generatedChunk, Source = LoadSource.Generated, Success = true };
        }
        catch (Exception ex)
        {
            _log.Error($"Failed to load/generate chunk {job.Coord}: {ex.Message}");
            return new ChunkLoadResult { Coord = job.Coord, Success = false, Error = ex.Message };
        }
    }

    private async Task<MeshBuildResult?> ProcessMeshJobAsync(MeshBuildJob job)
    {
        try
        {
            // B) Add detailed crash logging before calling mesher
            if (job.Chunk == null)
            {
                _log.Error($"Mesh job has null chunk for coord {job.Coord}");
                return new MeshBuildResult { Coord = job.Coord, Success = false, Error = "Chunk is null" };
            }

            if (job.Chunk.Blocks == null)
            {
                _log.Error($"Mesh job has null Blocks array for coord {job.Coord}");
                return new MeshBuildResult { Coord = job.Coord, Success = false, Error = "Chunk.Blocks is null" };
            }

            if (_atlas == null)
            {
                _log.Error($"Atlas is null when building mesh for chunk {job.Coord}");
                return new MeshBuildResult { Coord = job.Coord, Success = false, Error = "Atlas is null" };
            }

            // Track mesh failure count
            var failureCount = _meshFailureCounts.GetValueOrDefault(job.Coord, 0);
            
            // If this chunk has failed too many times, return placeholder to prevent infinite retry
            if (failureCount >= 3)
            {
                _log.Warn($"Chunk {job.Coord} exceeded mesh failure limit ({failureCount}), returning placeholder mesh");
                return new MeshBuildResult { Coord = job.Coord, Mesh = ChunkMesh.Empty, Success = true, IsPlaceholder = true };
            }

            // C) FAST-FIRST MESHING - For Tier 0 chunks ONLY
            bool isTier0 = job.Priority <= -10; // Tier 0 priority
            ChunkMesh mesh;
            
            if (isTier0)
            {
                // Fast mesh path - no greedy merge, no neighbor dependency
                mesh = await Task.Run(() => VoxelMesherGreedy.BuildChunkMeshFast(_world, job.Chunk, _atlas, _log));
            }
            else
            {
                // Normal mesh path with full quality
                mesh = await Task.Run(() => VoxelMesherGreedy.BuildChunkMesh(_world, job.Chunk, _atlas, _log));
            }
            
            // Clear failure count on success
            if (mesh != null)
            {
                _meshFailureCounts.Remove(job.Coord);
                return new MeshBuildResult { Coord = job.Coord, Mesh = mesh, Success = true };
            }
            else
            {
                // Increment failure count
                _meshFailureCounts[job.Coord] = failureCount + 1;
                return new MeshBuildResult { Coord = job.Coord, Success = false, Error = "Mesh building returned null" };
            }
        }
        catch (Exception ex)
        {
            // B) Change catch to log full exception
            _log.Error($"Failed to build mesh for chunk {job.Coord}: {ex.ToString()}");
            
            // Increment failure count
            var failureCount = _meshFailureCounts.GetValueOrDefault(job.Coord, 0);
            _meshFailureCounts[job.Coord] = failureCount + 1;
            
            // 2) Track total failures for stats
            lock (_statsLock)
            {
                _meshFailCount++;
            }
            
            return new MeshBuildResult { Coord = job.Coord, Success = false, Error = ex.Message };
        }
    }

    private async Task ProcessSaveJobAsync(ChunkSaveJob job)
    {
        try
        {
            // Throttle saves
            var now = DateTime.UtcNow;
            if (!job.Urgent && now - _lastSaveTime < _saveThrottleInterval)
            {
                await Task.Delay(_saveThrottleInterval - (now - _lastSaveTime));
            }

            var chunkPath = Path.Combine(_chunksDir, $"chunk_{job.Coord.X}_{job.Coord.Y}_{job.Coord.Z}.bin");
            var tempPath = chunkPath + ".tmp";

            // Write to temp file first
            await Task.Run(() => VoxelChunkData.SaveSnapshot(tempPath, job.Coord, job.Blocks));

            // Atomic replace
            if (File.Exists(chunkPath))
            {
                File.Delete(chunkPath);
            }
            File.Move(tempPath, chunkPath);
        }
        catch (Exception ex)
        {
            _log.Error($"Failed to save chunk {job.Coord}: {ex.Message}");
            var result = new ChunkSaveResult { Coord = job.Coord, Success = false, Error = ex.Message };
            _saveResults.Writer.TryWrite(result);
        }
    }

    
    public (int LoadQueue, int MeshQueue, int SaveQueue) GetQueueSizes()
    {
        return (_loadQueue.Reader.Count, _meshQueue.Reader.Count, _saveQueue.Reader.Count);
    }

    #endregion

    #region IDisposable

    private bool _disposed;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _log.Info("ChunkStreamingService shutting down...");

        // Wait for all workers to finish
        var allTasks = new List<Task>(_loadWorkers);
        allTasks.AddRange(_meshWorkers);
        allTasks.Add(_saveWorker);

        try
        {
            Task.WaitAll(allTasks.ToArray(), TimeSpan.FromSeconds(5));
        }
        catch (Exception ex)
        {
            _log.Error($"Error waiting for streaming workers: {ex.Message}");
        }

        _log.Info($"ChunkStreamingService shutdown complete. Processed: {_loadJobsProcessed} loads, {_meshJobsProcessed} meshes, {_saveJobsProcessed} saves");
    }

    #endregion
}

#region Job and Result Types

public class ChunkLoadJob
{
    public ChunkCoord Coord { get; set; }
    public int Priority { get; set; }
    public DateTime Timestamp { get; set; }
}

public class MeshBuildJob
{
    public ChunkCoord Coord { get; set; }
    public VoxelChunkData Chunk { get; set; }
    public int Priority { get; set; }
    public DateTime Timestamp { get; set; }
}

public class ChunkSaveJob
{
    public ChunkCoord Coord { get; set; }
    public byte[] Blocks { get; set; }
    public bool Urgent { get; set; }
    public DateTime Timestamp { get; set; }
}

public class ChunkLoadResult
{
    public ChunkCoord Coord { get; set; }
    public VoxelChunkData? Chunk { get; set; }
    public LoadSource Source { get; set; }
    public bool Success { get; set; }
    public string? Error { get; set; }
}

public class MeshBuildResult
{
    public ChunkCoord Coord { get; set; }
    public ChunkMesh? Mesh { get; set; }
    public bool Success { get; set; }
    public string? Error { get; set; }
    public bool IsPlaceholder { get; set; }
}

public class ChunkSaveResult
{
    public ChunkCoord Coord { get; set; }
    public bool Success { get; set; }
    public string? Error { get; set; }
}

public enum LoadSource
{
    Disk,
    Generated
}

#endregion
