using System;
using System.IO;
using System.Linq;

namespace LatticeVeilMonoGame.Core;

/// <summary>
/// Lightweight world validation for detecting new vs legacy world formats
/// </summary>
public enum WorldValidationStatus
{
    ValidNew,
    LegacyDetected,
    CorruptedNew,
    NotAWorld
}

/// <summary>
/// World validation result with status and reason
/// </summary>
public sealed class WorldValidationResult
{
    public WorldValidationStatus Status { get; set; }
    public string Reason { get; set; } = string.Empty;
    
    public static WorldValidationResult Valid() => new() { Status = WorldValidationStatus.ValidNew };
    public static WorldValidationResult Legacy(string reason) => new() { Status = WorldValidationStatus.LegacyDetected, Reason = reason };
    public static WorldValidationResult Corrupted(string reason) => new() { Status = WorldValidationStatus.CorruptedNew, Reason = reason };
    public static WorldValidationResult NotAWorld() => new() { Status = WorldValidationStatus.NotAWorld };
}

/// <summary>
/// Validates world folders and detects format type
/// </summary>
public static class WorldValidator
{
    /// <summary>
    /// Validates a world folder and determines its format status
    /// </summary>
    /// <param name="worldPath">Path to world folder</param>
    /// <returns>Validation result with status and reason</returns>
    public static WorldValidationResult ValidateWorldFolder(string worldPath)
    {
        if (string.IsNullOrWhiteSpace(worldPath) || !Directory.Exists(worldPath))
        {
            return WorldValidationResult.NotAWorld();
        }

        var levelPath = Path.Combine(worldPath, FileConventions.LevelFileName);
        
        // Check for new format (level.lvc exists)
        if (File.Exists(levelPath))
        {
            return ValidateNewFormat(worldPath, levelPath);
        }
        
        // Check for legacy format markers
        return CheckLegacyMarkers(worldPath);
    }
    
    /// <summary>
    /// Validates new format world structure
    /// </summary>
    private static WorldValidationResult ValidateNewFormat(string worldPath, string levelPath)
    {
        try
        {
            var levelLines = File.ReadAllLines(levelPath);
            var formatLine = levelLines.FirstOrDefault(line => line.StartsWith("format=", StringComparison.OrdinalIgnoreCase));
            var versionLine = levelLines.FirstOrDefault(line => line.StartsWith("format_version=", StringComparison.OrdinalIgnoreCase));
            
            // Check required keys
            if (string.IsNullOrWhiteSpace(formatLine) || !formatLine.Contains("LVWORLD"))
            {
                return WorldValidationResult.Corrupted("Invalid or missing format key in level.lvc");
            }
            
            if (string.IsNullOrWhiteSpace(versionLine) || !versionLine.Contains("2"))
            {
                return WorldValidationResult.Corrupted("Invalid or missing format_version key in level.lvc");
            }
            
            // Check required directories
            var regionsDir = Path.Combine(worldPath, FileConventions.RegionsDirName);
            if (!Directory.Exists(regionsDir))
            {
                return WorldValidationResult.Corrupted("Missing Regions/ directory");
            }
            
            return WorldValidationResult.Valid();
        }
        catch (Exception ex)
        {
            return WorldValidationResult.Corrupted($"Failed to read level.lvc: {ex.Message}");
        }
    }
    
    /// <summary>
    /// Checks for legacy world format markers
    /// </summary>
    private static WorldValidationResult CheckLegacyMarkers(string worldPath)
    {
        var chunksDir = Path.Combine(worldPath, "chunks");
        var meshcacheDir = Path.Combine(worldPath, "meshcache");
        var biomeCatalog = Path.Combine(worldPath, "biome_catalog.bin");
        
        var hasLegacyMarkers = false;
        var reasons = new System.Text.StringBuilder();
        
        if (Directory.Exists(chunksDir))
        {
            try
            {
                var chunkFiles = Directory.GetFiles(chunksDir, "*.bin", SearchOption.TopDirectoryOnly);
                if (chunkFiles.Length > 0)
                {
                    hasLegacyMarkers = true;
                    reasons.AppendLine("chunks/*.bin files found");
                }
            }
            catch (Exception)
            {
                // Ignore access errors for validation
            }
        }
        
        if (Directory.Exists(meshcacheDir))
        {
            try
            {
                var meshFiles = Directory.GetFiles(meshcacheDir, "*.meshbin", SearchOption.TopDirectoryOnly);
                if (meshFiles.Length > 0)
                {
                    hasLegacyMarkers = true;
                    if (reasons.Length > 0) reasons.Append("; ");
                    reasons.Append("meshcache/*.meshbin files found");
                }
            }
            catch (Exception)
            {
                // Ignore access errors for validation
            }
        }
        
        if (File.Exists(biomeCatalog))
        {
            hasLegacyMarkers = true;
            if (reasons.Length > 0) reasons.Append("; ");
            reasons.Append("biome_catalog.bin found");
        }
        
        if (hasLegacyMarkers)
        {
            return WorldValidationResult.Legacy(reasons.ToString());
        }
        
        return WorldValidationResult.NotAWorld();
    }
}
