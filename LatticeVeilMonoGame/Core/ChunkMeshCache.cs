using System;
using System.IO;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace LatticeVeilMonoGame.Core;

public static class ChunkMeshCache
{
    private const int FormatVersion = 2;

    public static string GetCacheDirectory(string worldPath)
    {
        return Path.Combine(worldPath, "meshcache");
    }

    public static string GetCachePath(string worldPath, ChunkCoord coord)
    {
        return Path.Combine(GetCacheDirectory(worldPath), $"chunk_{coord.X}_{coord.Y}_{coord.Z}.meshbin");
    }

    public static void Save(string worldPath, ChunkMesh mesh)
    {
        var path = GetCachePath(worldPath, mesh.Coord);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        using var stream = File.Create(path);
        using var writer = new BinaryWriter(stream);

        writer.Write(FormatVersion);
        writer.Write(mesh.Coord.X);
        writer.Write(mesh.Coord.Y);
        writer.Write(mesh.Coord.Z);

        writer.Write(mesh.Bounds.Min.X);
        writer.Write(mesh.Bounds.Min.Y);
        writer.Write(mesh.Bounds.Min.Z);
        writer.Write(mesh.Bounds.Max.X);
        writer.Write(mesh.Bounds.Max.Y);
        writer.Write(mesh.Bounds.Max.Z);

        WriteVertices(writer, mesh.OpaqueVertices);
        WriteVertices(writer, mesh.TransparentVertices);
        WriteVertices(writer, mesh.WaterVertices);
    }

    public static bool TryLoadFresh(string worldPath, string chunksDir, ChunkCoord coord, out ChunkMesh mesh)
    {
        mesh = ChunkMesh.Empty;
        var meshPath = GetCachePath(worldPath, coord);
        if (!File.Exists(meshPath))
            return false;

        // For new format worlds, we need to get source write time differently
        var chunkPath = Path.Combine(chunksDir, $"chunk_{coord.X}_{coord.Y}_{coord.Z}.bin");
        DateTime? sourceWriteUtc = null;
        
        if (File.Exists(chunkPath))
        {
            sourceWriteUtc = File.GetLastWriteTimeUtc(chunkPath);
        }
        else
        {
            // For new format worlds, try to get region file write time
            sourceWriteUtc = GetRegionFileWriteTime(worldPath, coord);
        }

        if (sourceWriteUtc.HasValue)
        {
            var meshWrite = File.GetLastWriteTimeUtc(meshPath);
            if (meshWrite < sourceWriteUtc.Value)
                return false;
        }
        else
        {
            // No source file found, treat as stale
            return false;
        }

        try
        {
            using var stream = File.OpenRead(meshPath);
            using var reader = new BinaryReader(stream);

            var version = reader.ReadInt32();
            if (version != FormatVersion)
                return false;

            var x = reader.ReadInt32();
            var y = reader.ReadInt32();
            var z = reader.ReadInt32();
            if (x != coord.X || y != coord.Y || z != coord.Z)
                return false;

            var min = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
            var max = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());

            var opaque = ReadVertices(reader);
            var transparent = ReadVertices(reader);
            var water = ReadVertices(reader);
            mesh = new ChunkMesh(coord, opaque, transparent, water, new BoundingBox(min, max));
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static DateTime? GetRegionFileWriteTime(string worldPath, ChunkCoord coord)
    {
        // Calculate region coordinates (32x32 chunks per region)
        var regionX = FloorDiv(coord.X, 32);
        var regionZ = FloorDiv(coord.Z, 32);
        var regionPath = Path.Combine(worldPath, "Regions", $"r.{regionX}.{regionZ}.lvregion");
        
        if (File.Exists(regionPath))
            return File.GetLastWriteTimeUtc(regionPath);
        
        return null;
    }

    private static int FloorDiv(int value, int divisor)
    {
        var q = value / divisor;
        var r = value % divisor;
        if (r != 0 && ((r > 0) != (divisor > 0)))
            q--;
        return q;
    }

    private static void WriteVertices(BinaryWriter writer, VertexPositionTexture[] vertices)
    {
        writer.Write(vertices.Length);
        for (var i = 0; i < vertices.Length; i++)
        {
            var v = vertices[i];
            writer.Write(v.Position.X);
            writer.Write(v.Position.Y);
            writer.Write(v.Position.Z);
            writer.Write(v.TextureCoordinate.X);
            writer.Write(v.TextureCoordinate.Y);
        }
    }

    private static VertexPositionTexture[] ReadVertices(BinaryReader reader)
    {
        var count = reader.ReadInt32();
        if (count < 0)
            throw new InvalidDataException("Invalid vertex count in mesh cache.");

        var result = new VertexPositionTexture[count];
        for (var i = 0; i < count; i++)
        {
            var pos = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
            var uv = new Vector2(reader.ReadSingle(), reader.ReadSingle());
            result[i] = new VertexPositionTexture(pos, uv);
        }

        return result;
    }
}
