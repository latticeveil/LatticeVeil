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
    IncompleteGeneration,
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
    public static WorldValidationResult Incomplete(string reason) => new() { Status = WorldValidationStatus.IncompleteGeneration, Reason = reason };
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

        var worldFile = Path.Combine(worldPath, "world.lvc");

        // Fail-fast: old layout files present => legacy (no auto-convert)
        var legacyLevel = Path.Combine(worldPath, "level.lvc");
        var legacyCfg = Path.Combine(worldPath, "world_config.lvc");
        if (File.Exists(legacyLevel) || File.Exists(legacyCfg))
        {
            return WorldValidationResult.Legacy("Legacy world layout (level.lvc/world_config.lvc) present");
        }

        // New format uses world.lvc only
        if (File.Exists(worldFile))
        {
            if (WorldGenerationStateStore.TryLoadRecoverableState(worldPath, TimeSpan.FromSeconds(90), out var worldgenState)
                && worldgenState != null
                && !worldgenState.IsCompleted)
            {
                var stage = string.IsNullOrWhiteSpace(worldgenState.Stage) ? "PREPARING" : worldgenState.Stage;
                var reason = string.IsNullOrWhiteSpace(worldgenState.ErrorReason)
                    ? $"Generation state is {worldgenState.Status} at stage {stage}."
                    : worldgenState.ErrorReason;
                return WorldValidationResult.Incomplete(reason);
            }

            return ValidateNewFormat(worldPath, worldFile);
        }

        // Check for legacy format markers
        return CheckLegacyMarkers(worldPath);
}
    
    /// <summary>
    /// Validates new format world structure
    /// </summary>
    private static WorldValidationResult ValidateNewFormat(string worldPath, string worldFile)
    {
        try
        {
            var levelLines = File.ReadAllLines(worldFile);

            // Accept both snake_case (format_version) and the current serializer's PascalCase (FormatVersion)
            // so worlds don't get flagged as corrupted due to key naming differences.
            var formatLine = levelLines.FirstOrDefault(line =>
                line.StartsWith("format=", StringComparison.OrdinalIgnoreCase) ||
                line.StartsWith("Format=", StringComparison.OrdinalIgnoreCase));

            var versionLine = levelLines.FirstOrDefault(line =>
                line.StartsWith("format_version=", StringComparison.OrdinalIgnoreCase) ||
                line.StartsWith("FormatVersion=", StringComparison.OrdinalIgnoreCase));
            
            // Check required keys
            if (string.IsNullOrWhiteSpace(formatLine) || !formatLine.Contains("LVWORLD"))
            {
                return WorldValidationResult.Corrupted("Invalid or missing format key in world.lvc");
            }
            
            if (string.IsNullOrWhiteSpace(versionLine))
                return WorldValidationResult.Corrupted("Invalid or missing format_version key in world.lvc");

            // Parse the version after '=' and require >= 2.
            var eq = versionLine.IndexOf('=');
            if (eq < 0 || !int.TryParse(versionLine.Substring(eq + 1).Trim().Trim('"'), out var ver) || ver < 2)
                return WorldValidationResult.Corrupted("Invalid or missing format_version key in world.lvc");
            
            // If legacy storage exists alongside a v2 marker, treat as legacy/invalid (no auto-convert).
            var legacyChunksDir = Path.Combine(worldPath, "chunks");
            if (Directory.Exists(legacyChunksDir))
            {
                try
                {
                    if (Directory.GetFiles(legacyChunksDir, "chunk_*.bin", SearchOption.TopDirectoryOnly).Length > 0)
                        return WorldValidationResult.Legacy("Legacy chunk storage (chunks/*.bin) present");
                }
                catch { }
            }

            // Check required directories
            var regionsDir = Path.Combine(worldPath, "regions");
            var legacyRegionsDir = Path.Combine(worldPath, "Regions");
            if (!Directory.Exists(regionsDir) && Directory.Exists(legacyRegionsDir))
            {
                return WorldValidationResult.Corrupted("Regions directory casing invalid (expected regions/)");
            }
            if (!Directory.Exists(regionsDir))
            {
                return WorldValidationResult.Corrupted("Missing regions/ directory");
            }

            // vNext optional artifacts are valid if present:
            // - biome_index.lvbi
            // - Spawn.lvpwarm
            // - worldgen_state.lvc
            // - playerdata/*.lvplayer

            // Must contain at least one region file for valid new format
            try
            {
                if (Directory.GetFiles(regionsDir, "*.lvregion", SearchOption.TopDirectoryOnly).Length == 0)
                    return WorldValidationResult.Corrupted("No .lvregion files found in regions/");
            }
            catch (Exception ex)
            {
                return WorldValidationResult.Corrupted($"Failed to enumerate regions/: {ex.Message}");
            }

            return WorldValidationResult.Valid();
        }
        catch (Exception ex)
        {
            return WorldValidationResult.Corrupted($"Failed to read world.lvc: {ex.Message}");
        }
    }
    
    /// <summary>
    /// Checks for legacy world format markers
    /// </summary>
    private static WorldValidationResult CheckLegacyMarkers(string worldPath)
    {
        var chunksDir = Path.Combine(worldPath, "chunks");
        var meshcacheDir = Path.Combine(worldPath, "meshcache");
        var spawnMeshcacheDir = Path.Combine(worldPath, "spawn_meshcache");
        var legacySpawnPrewarm = Path.Combine(worldPath, "spawn_prewarm.bin");
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

        if (Directory.Exists(spawnMeshcacheDir))
        {
            hasLegacyMarkers = true;
            if (reasons.Length > 0) reasons.Append("; ");
            reasons.Append("spawn_meshcache/ found");
        }
        
        if (File.Exists(biomeCatalog))
        {
            hasLegacyMarkers = true;
            if (reasons.Length > 0) reasons.Append("; ");
            reasons.Append("biome_catalog.bin found");
        }

        if (File.Exists(legacySpawnPrewarm))
        {
            hasLegacyMarkers = true;
            if (reasons.Length > 0) reasons.Append("; ");
            reasons.Append("spawn_prewarm.bin found");
        }
        
        if (hasLegacyMarkers)
        {
            return WorldValidationResult.Legacy(reasons.ToString());
        }
        
        return WorldValidationResult.NotAWorld();
    }
}
