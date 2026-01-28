using System;
using Microsoft.Xna.Framework.Graphics;
using LatticeVeilMonoGame.Core;

namespace LatticeVeilMonoGame.Core;

/// <summary>
/// SIMPLE MEMORY MANAGER
/// Optimizes RAM and VRAM usage for maximum performance
/// </summary>
public static class SimpleMemoryManager
{
    private static readonly Logger _log = new Logger("SimpleMemoryManager");
    
    // Memory tracking
    private static long _totalAllocated;
    private static long _peakUsage;
    private static long _vramUsage;
    private static long _vramLimit;
    
    static SimpleMemoryManager()
    {
        InitializeMemoryManagement();
    }
    
    /// <summary>
    /// Initialize advanced memory management
    /// </summary>
    private static void InitializeMemoryManagement()
    {
        // Detect VRAM limit (simplified)
        _vramLimit = DetectVramLimit();
        
        _log.Info($"💾 Simple Memory Manager initialized:");
        _log.Info($"   🎮 VRAM Limit: {_vramLimit / 1024 / 1024}MB");
    }
    
    /// <summary>
    /// Detect VRAM limit (simplified implementation)
    /// </summary>
    private static long DetectVramLimit()
    {
        // Default to 2GB VRAM limit (would use actual GPU detection in production)
        return 2L * 1024 * 1024 * 1024;
    }
    
    /// <summary>
    /// Allocate VRAM for texture
    /// </summary>
    public static bool AllocateVram(long size)
    {
        if (_vramUsage + size > _vramLimit)
        {
            _log.Warn($"⚠️ VRAM allocation failed: {size / 1024 / 1024}MB requested, {_vramUsage / 1024 / 1024}MB used");
            return false;
        }
        
        Interlocked.Add(ref _vramUsage, size);
        return true;
    }
    
    /// <summary>
    /// Free VRAM
    /// </summary>
    public static void FreeVram(long size)
    {
        Interlocked.Add(ref _vramUsage, -size);
    }
    
    /// <summary>
    /// Get memory statistics
    /// </summary>
    public static (long ramUsed, long ramPeak, long vramUsed, long vramLimit) GetMemoryStats()
    {
        var currentUsage = GC.GetTotalMemory(false);
        if (currentUsage > _peakUsage)
        {
            _peakUsage = currentUsage;
        }
        
        return (currentUsage, _peakUsage, _vramUsage, _vramLimit);
    }
    
    /// <summary>
    /// Optimize memory usage manually
    /// </summary>
    public static void OptimizeNow()
    {
        GC.Collect(0, GCCollectionMode.Optimized);
        GC.WaitForPendingFinalizers();
        GC.Collect(0, GCCollectionMode.Optimized);
        _log.Info("🔧 Manual memory optimization completed");
    }
    
    /// <summary>
    /// Check if memory pressure is high
    /// </summary>
    public static bool IsMemoryPressureHigh()
    {
        var currentUsage = GC.GetTotalMemory(false);
        var ramPressure = (double)currentUsage / (_peakUsage + 1);
        var vramPressure = (double)_vramUsage / (_vramLimit + 1);
        
        return ramPressure > 0.8 || vramPressure > 0.8;
    }
}
