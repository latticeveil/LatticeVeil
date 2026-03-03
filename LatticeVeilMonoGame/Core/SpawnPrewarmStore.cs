using System;
using System.Collections.Generic;
using System.IO;

namespace LatticeVeilMonoGame.Core;

public sealed class SpawnPrewarmManifest
{
    public int SpawnChunkX { get; set; }
    public int SpawnChunkZ { get; set; }
    public int BakeRadius { get; set; }
    public int GateRadius { get; set; }
    public List<ChunkCoord> BakedChunks { get; set; } = new();
    public List<ChunkCoord> GateChunks { get; set; } = new();
    public List<ChunkCoord> BiomeAnchorChunks { get; set; } = new();
}

public static class SpawnPrewarmStore
{
    private const int Magic = 0x5750534C; // LSPW
    private const int FormatVersion = 1;
    public const string FileName = "Spawn.lvpwarm";

    public static string GetPath(string worldPath) => Path.Combine(worldPath, FileName);

    public static bool Save(string worldPath, SpawnPrewarmManifest manifest, Logger log)
    {
        try
        {
            var path = GetPath(worldPath);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);

            using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
            using var bw = new BinaryWriter(fs);

            bw.Write(Magic);
            bw.Write(FormatVersion);
            bw.Write(manifest.SpawnChunkX);
            bw.Write(manifest.SpawnChunkZ);
            bw.Write(Math.Max(0, manifest.BakeRadius));
            bw.Write(Math.Max(0, manifest.GateRadius));
            WriteCoords(bw, manifest.BakedChunks);
            WriteCoords(bw, manifest.GateChunks);
            WriteCoords(bw, manifest.BiomeAnchorChunks);
            return true;
        }
        catch (Exception ex)
        {
            log.Warn($"Failed to save spawn prewarm manifest: {ex.Message}");
            return false;
        }
    }

    public static bool TryLoad(string worldPath, Logger log, out SpawnPrewarmManifest manifest)
    {
        manifest = new SpawnPrewarmManifest();
        try
        {
            var path = GetPath(worldPath);
            if (!File.Exists(path))
                return false;

            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var br = new BinaryReader(fs);

            if (br.ReadInt32() != Magic)
                return false;
            if (br.ReadInt32() != FormatVersion)
                return false;

            manifest = new SpawnPrewarmManifest
            {
                SpawnChunkX = br.ReadInt32(),
                SpawnChunkZ = br.ReadInt32(),
                BakeRadius = br.ReadInt32(),
                GateRadius = br.ReadInt32(),
                BakedChunks = ReadCoords(br),
                GateChunks = ReadCoords(br),
                BiomeAnchorChunks = ReadCoords(br)
            };
            return true;
        }
        catch (Exception ex)
        {
            log.Warn($"Failed to load spawn prewarm manifest: {ex.Message}");
            return false;
        }
    }

    private static void WriteCoords(BinaryWriter bw, List<ChunkCoord>? coords)
    {
        if (coords == null)
        {
            bw.Write(0);
            return;
        }

        bw.Write(coords.Count);
        for (var i = 0; i < coords.Count; i++)
        {
            bw.Write(coords[i].X);
            bw.Write(coords[i].Y);
            bw.Write(coords[i].Z);
        }
    }

    private static List<ChunkCoord> ReadCoords(BinaryReader br)
    {
        var count = Math.Max(0, br.ReadInt32());
        var result = new List<ChunkCoord>(count);
        for (var i = 0; i < count; i++)
        {
            var x = br.ReadInt32();
            var y = br.ReadInt32();
            var z = br.ReadInt32();
            result.Add(new ChunkCoord(x, y, z));
        }

        return result;
    }
}
