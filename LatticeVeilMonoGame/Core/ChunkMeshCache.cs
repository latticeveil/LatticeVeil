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

        var chunkPath = Path.Combine(chunksDir, $"chunk_{coord.X}_{coord.Y}_{coord.Z}.bin");
        if (File.Exists(chunkPath))
        {
            var chunkWrite = File.GetLastWriteTimeUtc(chunkPath);
            var meshWrite = File.GetLastWriteTimeUtc(meshPath);
            if (meshWrite < chunkWrite)
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
