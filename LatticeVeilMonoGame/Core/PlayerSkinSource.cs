using System;
using System.Drawing;
using System.IO;
using Microsoft.Xna.Framework.Graphics;

namespace LatticeVeilMonoGame.Core;

internal static class PlayerSkinSource
{
    public const string RelativePath = "textures/entities/player_default.png";

    public static string? ResolvePath()
    {
        if (SkinLibrary.TryGetActivePngPath(out var activeSkinPath))
            return activeSkinPath;

        return null;
    }

    public static bool TryLoadTexture(
        GraphicsDevice device,
        out Texture2D texture,
        out string? resolvedPath,
        out DateTime lastWriteUtc)
    {
        texture = null!;
        resolvedPath = ResolvePath();
        lastWriteUtc = DateTime.MinValue;
        if (string.IsNullOrWhiteSpace(resolvedPath))
            return false;

        try
        {
            texture = LoadCompositedTexture(device, File.ReadAllBytes(resolvedPath));
            lastWriteUtc = File.GetLastWriteTimeUtc(resolvedPath);
            return true;
        }
        catch
        {
            if (texture != null)
                texture.Dispose();

            texture = null!;
            return false;
        }
    }

    public static Texture2D LoadCompositedTexture(GraphicsDevice device, byte[] overlayPngBytes)
    {
        using var ms = new MemoryStream(overlayPngBytes);
        using var overlay = Texture2D.FromStream(device, ms);
        var baseData = DefaultPlayerSkinFactory.CreateData();

        if (overlay.Width != 64 || overlay.Height != 64)
        {
            var fallback = new Texture2D(device, 64, 64);
            fallback.SetData(baseData);
            return fallback;
        }

        var overlayData = new Microsoft.Xna.Framework.Color[64 * 64];
        overlay.GetData(overlayData);
        for (var i = 0; i < overlayData.Length; i++)
        {
            var src = overlayData[i];
            if (src.A == 0)
                continue;

            if (src.A == 255)
            {
                baseData[i] = src;
                continue;
            }

            var dst = baseData[i];
            var alpha = src.A / 255f;
            baseData[i] = new Microsoft.Xna.Framework.Color(
                (byte)Math.Round((src.R * alpha) + (dst.R * (1f - alpha))),
                (byte)Math.Round((src.G * alpha) + (dst.G * (1f - alpha))),
                (byte)Math.Round((src.B * alpha) + (dst.B * (1f - alpha))),
                (byte)255);
        }

        var texture = new Texture2D(device, 64, 64);
        texture.SetData(baseData);
        return texture;
    }

    public static bool[] ReadVisibleLayerMask(byte[] overlayPngBytes, byte alphaThreshold = 8)
    {
        var mask = new bool[64 * 64];
        try
        {
            using var ms = new MemoryStream(overlayPngBytes);
            using var overlay = new Bitmap(ms);
            if (overlay.Width != 64 || overlay.Height != 64)
                return mask;

            for (var y = 0; y < 64; y++)
            {
                for (var x = 0; x < 64; x++)
                    mask[(y * 64) + x] = overlay.GetPixel(x, y).A > alphaThreshold;
            }
        }
        catch
        {
        }

        return mask;
    }
}
