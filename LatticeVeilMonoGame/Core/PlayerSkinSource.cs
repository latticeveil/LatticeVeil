using System;
using System.IO;
using Microsoft.Xna.Framework.Graphics;

namespace LatticeVeilMonoGame.Core;

internal static class PlayerSkinSource
{
    public const string RelativePath = "textures/entities/player_default.png";
#if DEBUG
    public const string DevAbsolutePath = @"C:\Users\Redacted\Documents\LatticeVeil_project\LatticeVeilMonoGame\Defaults\Assets\textures\entities\player_default.png";
#endif

    public static string? ResolvePath()
    {
#if DEBUG
        if (File.Exists(DevAbsolutePath))
            return DevAbsolutePath;
#endif

        if (AssetResolver.TryResolve(RelativePath, out var resolved))
            return resolved;

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
            using var fs = File.OpenRead(resolvedPath);
            texture = Texture2D.FromStream(device, fs);
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
}
