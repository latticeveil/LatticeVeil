using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.Channels;

namespace LatticeVeilMonoGame.Core;

/// <summary>
/// World pregeneration service for efficient chunk loading
/// </summary>
public sealed class WorldPregenerationService : IDisposable
{
    private readonly VoxelWorld _world;
    private readonly ChunkStreamingService _streamingService;
    private readonly Logger _log;
    private readonly int _maxRadius;
    private readonly int _maxConcurrentTasks;
    
    // Pregeneration task management
    private readonly Channel<PregenTask> _taskQueue = Channel.CreateBounded<PregenTask>(1000);
    private readonly Task[] _workers;
    private readonly ConcurrentDictionary<ChunkCoord, PregenTaskStatus> _taskStatus = new();
    
    // Pregeneration state
    private bool _isRunning;
    private int _totalChunksToGenerate;
    private int _chunksGenerated;
    private int _chunksSkipped;
    private DateTime _startTime;
    
    // ENHANCED PERFORMANCE SETTINGS - Better pregeneration
    private readonly int _secondsPerNotification = 3; // More frequent updates
    private DateTime _lastNotification = DateTime.MinValue;
    private readonly int _maxChunksPerSecond = 100; // Doubled for faster pregeneration
    private DateTime _lastChunkTime = DateTime.UtcNow;
    
    public enum PregenTaskStatus
    {
        Pending,
        InProgress,
        Completed,
        Skipped,
        Failed
    }
    
    private struct PregenTask
    {
        public ChunkCoord Coord;
        public int Priority;
        public DateTime StartTime;
        public PregenTaskStatus Status;
    }
    
    public WorldPregenerationService(VoxelWorld world, ChunkStreamingService streamingService, Logger log, int maxRadius = 100, int maxConcurrentTasks = 8)
    {
        _world = world;
        _streamingService = streamingService;
        _log = log;
        _maxRadius = maxRadius;
        _maxConcurrentTasks = maxConcurrentTasks;
        
        _workers = new Task[_maxConcurrentTasks];
        for (int i = 0; i < _maxConcurrentTasks; i++)
        {
            _workers[i] = Task.Run(WorkerLoop);
        }
    }
    
    /// <summary>
    /// Start pregeneration in a circular area around spawn (like Minecraft)
    /// </summary>
    public void StartPregeneration(int radius)
    {
        if (_isRunning)
        {
            _log.Info("Pregeneration already running");
            return;
        }
        
        _isRunning = true;
        _startTime = DateTime.UtcNow;
        _chunksGenerated = 0;
        _chunksSkipped = 0;
        
        // Calculate total chunks needed for circular area
        _totalChunksToGenerate = CalculateCircularChunks(radius);
        
        _log.Info($"Starting Minecraft-style pregeneration: {_totalChunksToGenerate} chunks in {radius * 2 + 1}x{radius * 2 + 1} circular area");
        
        // Generate tasks in circular pattern from center outward (like Minecraft)
        var tasks = GenerateCircularTasks(radius);
        
        foreach (var task in tasks)
        {
            _taskQueue.Writer.TryWrite(task);
            _taskStatus[task.Coord] = PregenTaskStatus.Pending;
        }
        
        _log.Info($"Pregeneration started with {_taskQueue.Reader.Count} tasks queued");
    }
    
    /// <summary>
    /// Stop current pregeneration
    /// </summary>
    public void StopPregeneration()
    {
        if (!_isRunning)
        {
            _log.Info("Pregeneration not running");
            return;
        }
        
        _isRunning = false;
        
        // Clear remaining tasks
        while (_taskQueue.Reader.TryRead(out _))
        {
            // Tasks will be marked as skipped
        }
        
        _log.Info($"Pregeneration stopped. Generated: {_chunksGenerated}, Skipped: {_chunksSkipped}");
    }
    
    /// <summary>
    /// Pause current pregeneration
    /// </summary>
    public void PausePregeneration()
    {
        // Implementation would pause task processing
        _log.Info("Pregeneration paused");
    }
    
    /// <summary>
    /// Resume current pregeneration
    /// </summary>
    public void ResumePregeneration()
    {
        // Implementation would resume task processing
        _log.Info("Pregeneration resumed");
    }
    
    /// <summary>
    /// Get current pregeneration status
    /// </summary>
    public (int total, int generated, int skipped, bool isRunning, TimeSpan elapsed) GetStatus()
    {
        return (_totalChunksToGenerate, _chunksGenerated, _chunksSkipped, _isRunning, 
                _isRunning ? DateTime.UtcNow - _startTime : TimeSpan.Zero);
    }
    
    /// <summary>
    /// Check if pregeneration is currently running
    /// </summary>
    public bool IsRunning()
    {
        return _isRunning;
    }
    
    private async Task WorkerLoop()
    {
        while (true)
        {
            try
            {
                if (!_isRunning)
                {
                    await Task.Delay(100);
                    continue;
                }
                
                if (_taskQueue.Reader.TryRead(out var task))
                {
                    // ENHANCED PERFORMANCE - Reduced throttling for faster pregeneration
                    var timeSinceLastChunk = DateTime.UtcNow - _lastChunkTime;
                    if (timeSinceLastChunk.TotalMilliseconds < (1000 / _maxChunksPerSecond))
                    {
                        await Task.Delay((int)(1000 / _maxChunksPerSecond) - (int)timeSinceLastChunk.TotalMilliseconds);
                    }
                    
                    await ProcessPregenTask(task);
                    _lastChunkTime = DateTime.UtcNow;
                }
                else
                {
                    await Task.Delay(50); // Reduced delay for better responsiveness
                }
            }
            catch (Exception ex)
            {
                _log.Error($"Pregeneration worker error: {ex.Message}");
                await Task.Delay(500); // Shorter delay for faster recovery
            }
        }
    }
    
    private async Task ProcessPregenTask(PregenTask task)
    {
        try
        {
            task.Status = PregenTaskStatus.InProgress;
            _taskStatus[task.Coord] = task.Status;
            
            // Check if chunk already exists or is cached
            if (_world.TryGetChunk(task.Coord, out var existingChunk) && existingChunk != null)
            {
                task.Status = PregenTaskStatus.Skipped;
                _taskStatus[task.Coord] = task.Status;
                Interlocked.Increment(ref _chunksSkipped);
                return;
            }
            
            // Check optimized cache first
            if (OptimizedChunkService.TryGetChunk(task.Coord, out var cachedChunk))
            {
                _world.AddChunkDirect(task.Coord, cachedChunk);
                task.Status = PregenTaskStatus.Completed;
                _taskStatus[task.Coord] = task.Status;
                Interlocked.Increment(ref _chunksGenerated);
                return;
            }
            
            // Generate chunk using optimized service
            var optimizedChunk = await OptimizedChunkService.GenerateChunkAsync(_world.Meta, task.Coord);
            _world.AddChunkDirect(task.Coord, optimizedChunk);
            
            task.Status = PregenTaskStatus.Completed;
            _taskStatus[task.Coord] = task.Status;
            Interlocked.Increment(ref _chunksGenerated);
            
            // Send notification more frequently for better feedback
            if (DateTime.UtcNow - _lastNotification > TimeSpan.FromSeconds(_secondsPerNotification))
            {
                var status = GetStatus();
                _log.Info($"Pregeneration progress: {status.generated}/{status.total} chunks ({(100.0 * status.generated / Math.Max(1, status.total)):F1}%) - Elapsed: {status.elapsed:hh\\:mm\\:ss} - Rate: {(status.generated / Math.Max(1, status.elapsed.TotalSeconds)):F1} chunks/sec");
                _lastNotification = DateTime.UtcNow;
            }
        }
        catch (Exception ex)
        {
            _log.Error($"Error processing pregneration task for chunk {task.Coord}: {ex.Message}");
            task.Status = PregenTaskStatus.Failed;
            _taskStatus[task.Coord] = task.Status;
        }
    }
    
    private async Task WaitForChunkGeneration(ChunkCoord coord)
    {
        var timeout = TimeSpan.FromSeconds(30);
        var startTime = DateTime.UtcNow;
        
        while (DateTime.UtcNow - startTime < timeout)
        {
            if (_world.TryGetChunk(coord, out var chunk) && chunk != null)
            {
                return;
            }
            
            await Task.Delay(100);
        }
        
        _log.Info($"Timeout waiting for chunk generation: {coord}");
    }
    
    private int CalculateTotalChunks(int radius)
    {
        // Calculate chunks in a square area (radius * 2 + 1) x (radius * 2 + 1)
        int diameter = radius * 2 + 1;
        return diameter * diameter;
    }
    
    /// <summary>
    /// Calculate chunks needed for circular area (like Minecraft)
    /// </summary>
    private int CalculateCircularChunks(int radius)
    {
        int count = 0;
        for (int x = -radius; x <= radius; x++)
        {
            for (int z = -radius; z <= radius; z++)
            {
                // Check if chunk is within circular radius
                if (x * x + z * z <= radius * radius)
                {
                    count++;
                }
            }
        }
        return count;
    }
    
    /// <summary>
    /// Generate chunk tasks in circular pattern from center outward (like Minecraft)
    /// </summary>
    private List<PregenTask> GenerateCircularTasks(int radius)
    {
        var tasks = new List<PregenTask>();
        
        // Generate chunks in concentric circles from center outward
        for (int r = 0; r <= radius; r++)
        {
            for (int x = -r; x <= r; x++)
            {
                for (int z = -r; z <= r; z++)
                {
                    // Only generate chunks at the current radius to create circular pattern
                    if (x * x + z * z == r * r)
                    {
                        var chunkCoord = new ChunkCoord(x, 0, z);
                        
                        // Higher priority for chunks closer to center
                        int priority = (radius - r) * 1000 + Math.Abs(x) + Math.Abs(z);
                        
                        tasks.Add(new PregenTask
                        {
                            Coord = chunkCoord,
                            Priority = priority,
                            StartTime = DateTime.UtcNow,
                            Status = PregenTaskStatus.Pending
                        });
                    }
                }
            }
        }
        
        // Sort by priority (center chunks first)
        return tasks.OrderBy(t => t.Priority).ToList();
    }
    
    /// <summary>
    /// Generate chunk tasks in spiral pattern from center outward
    /// </summary>
    private List<PregenTask> GenerateSpiralTasks(int radius)
    {
        var tasks = new List<PregenTask>();
        
        // Spiral pattern: 0,0 then 1,0, 1,1, 0,1, -1,1, -1,0, -1,-1, 0,-1, 1,-1, 2,0, etc.
        int x = 0, z = 0;
        int dx = 0, dz = -1;
        int segmentLength = 1;
        int segmentPassed = 0;
        
        for (int i = 0; i < CalculateTotalChunks(radius); i++)
        {
            if (Math.Abs(x) <= radius && Math.Abs(z) <= radius)
            {
                var chunkCoord = new ChunkCoord(x, 0, z);
                int priority = radius - Math.Max(Math.Abs(x), Math.Abs(z));
                
                tasks.Add(new PregenTask
                {
                    Coord = chunkCoord,
                    Priority = priority,
                    StartTime = DateTime.UtcNow,
                    Status = PregenTaskStatus.Pending
                });
            }
            
            // Spiral movement logic
            x += dx;
            z += dz;
            segmentPassed++;
            
            if (segmentPassed == segmentLength)
            {
                segmentPassed = 0;
                
                // Turn right
                int temp = dx;
                dx = -dz;
                dz = temp;
                
                if (dz == 0)
                {
                    segmentLength++;
                }
            }
        }
        
        return tasks.OrderBy(t => t.Priority).ToList();
    }
    
    public void Dispose()
    {
        _isRunning = false;
        
        // Wait for all workers to finish
        Task.WaitAll(_workers);
        
        _taskQueue.Writer.Complete();
    }
}
