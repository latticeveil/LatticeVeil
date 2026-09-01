using System;
using System.IO;
using System.Text;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace LatticeVeilMonoGame.Core;

public static class HorizonLodCacheStore
{
    private static readonly byte[] Magic = Encoding.ASCII.GetBytes("LVLOD001");
    private const int Version = 1;
    private const int MaxCachedVertices = 2_500_000;

    public static bool TryLoad(
        string worldPath,
        int centerChunkX,
        int centerChunkZ,
        int innerRadiusChunks,
        int radiusChunks,
        string cacheKey,
        out VertexPositionColor[] vertices)
    {
        vertices = Array.Empty<VertexPositionColor>();
        var path = GetPath(worldPath, centerChunkX, centerChunkZ, innerRadiusChunks, radiusChunks, cacheKey);
        if (!File.Exists(path))
            return false;

        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: false);

            var magic = reader.ReadBytes(Magic.Length);
            if (magic.Length != Magic.Length || !magic.AsSpan().SequenceEqual(Magic))
                return false;

            var version = reader.ReadInt32();
            if (version != Version)
                return false;

            if (reader.ReadInt32() != centerChunkX
                || reader.ReadInt32() != centerChunkZ
                || reader.ReadInt32() != innerRadiusChunks
                || reader.ReadInt32() != radiusChunks
                || !string.Equals(reader.ReadString(), cacheKey, StringComparison.Ordinal))
            {
                return false;
            }

            var count = reader.ReadInt32();
            if (count <= 0 || count > MaxCachedVertices || count % 3 != 0)
                return false;

            var loaded = new VertexPositionColor[count];
            for (var i = 0; i < count; i++)
            {
                var x = reader.ReadSingle();
                var y = reader.ReadSingle();
                var z = reader.ReadSingle();
                var r = reader.ReadByte();
                var g = reader.ReadByte();
                var b = reader.ReadByte();
                var a = reader.ReadByte();
                loaded[i] = new VertexPositionColor(new Vector3(x, y, z), new Color(r, g, b, a));
            }

            vertices = loaded;
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static void Save(
        string worldPath,
        int centerChunkX,
        int centerChunkZ,
        int innerRadiusChunks,
        int radiusChunks,
        string cacheKey,
        VertexPositionColor[] vertices)
    {
        if (vertices.Length <= 0 || vertices.Length > MaxCachedVertices || vertices.Length % 3 != 0)
            return;

        try
        {
            var path = GetPath(worldPath, centerChunkX, centerChunkZ, innerRadiusChunks, radiusChunks, cacheKey);
            var dir = Path.GetDirectoryName(path);
            if (string.IsNullOrWhiteSpace(dir))
                return;

            Directory.CreateDirectory(dir);
            var tempPath = path + ".tmp";
            using (var stream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: false))
            {
                writer.Write(Magic);
                writer.Write(Version);
                writer.Write(centerChunkX);
                writer.Write(centerChunkZ);
                writer.Write(innerRadiusChunks);
                writer.Write(radiusChunks);
                writer.Write(cacheKey);
                writer.Write(vertices.Length);

                foreach (var vertex in vertices)
                {
                    writer.Write(vertex.Position.X);
                    writer.Write(vertex.Position.Y);
                    writer.Write(vertex.Position.Z);
                    writer.Write(vertex.Color.R);
                    writer.Write(vertex.Color.G);
                    writer.Write(vertex.Color.B);
                    writer.Write(vertex.Color.A);
                }
            }

            if (File.Exists(path))
                File.Replace(tempPath, path, null);
            else
                File.Move(tempPath, path);
        }
        catch
        {
            // LOD cache is optional. The renderer can regenerate it if this write fails.
        }
    }

    private static string GetPath(
        string worldPath,
        int centerChunkX,
        int centerChunkZ,
        int innerRadiusChunks,
        int radiusChunks,
        string cacheKey)
    {
        var safeKey = Sanitize(cacheKey);
        var fileName = $"h.{centerChunkX}.{centerChunkZ}.i{innerRadiusChunks}.r{radiusChunks}.{safeKey}{FileConventions.LodExtension}";
        return Path.Combine(FileConventions.GetLodCacheDir(worldPath), fileName);
    }

    private static string Sanitize(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "default";

        var chars = value.ToCharArray();
        for (var i = 0; i < chars.Length; i++)
        {
            if (!char.IsLetterOrDigit(chars[i]) && chars[i] != '-' && chars[i] != '_')
                chars[i] = '_';
        }

        return new string(chars);
    }
}
