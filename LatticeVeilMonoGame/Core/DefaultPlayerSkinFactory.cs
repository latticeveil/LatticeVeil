using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace LatticeVeilMonoGame.Core;

internal static class DefaultPlayerSkinFactory
{
    public static Texture2D Create(GraphicsDevice device)
    {
        var tex = new Texture2D(device, 64, 64);
        var data = new Color[64 * 64];

        // Opaque fallback across the atlas avoids black rendering if any UV drifts.
        var fallback = new Color(90, 112, 136);
        for (var i = 0; i < data.Length; i++)
            data[i] = fallback;

        var skin = new Color(232, 200, 172);
        var hair = new Color(96, 74, 54);
        var shirt = new Color(98, 158, 196);
        var shirtShade = new Color(74, 126, 166);
        var pants = new Color(64, 72, 86);
        var shoe = new Color(38, 42, 50);
        var eyeWhite = new Color(225, 232, 240);
        var eyePupil = new Color(44, 54, 66);
        var mouth = new Color(166, 100, 98);

        // Head base
        Fill(data, 8, 0, 8, 8, hair); Fill(data, 16, 0, 8, 8, skin);
        Fill(data, 0, 8, 8, 8, skin); Fill(data, 8, 8, 8, 8, skin);
        Fill(data, 16, 8, 8, 8, skin); Fill(data, 24, 8, 8, 8, skin);
        Fill(data, 8, 0, 8, 2, hair); Fill(data, 8, 8, 8, 2, hair);
        Fill(data, 0, 8, 8, 2, hair); Fill(data, 16, 8, 8, 2, hair); Fill(data, 24, 8, 8, 2, hair);
        Dot(data, 10, 11, eyeWhite); Dot(data, 13, 11, eyeWhite);
        Dot(data, 10, 11, eyePupil); Dot(data, 13, 11, eyePupil);
        Dot(data, 11, 13, mouth); Dot(data, 12, 13, mouth);

        // Torso base
        Fill(data, 20, 16, 8, 4, shirt); Fill(data, 28, 16, 8, 4, shirt);
        Fill(data, 16, 20, 4, 12, shirtShade); Fill(data, 20, 20, 8, 12, shirt);
        Fill(data, 28, 20, 4, 12, shirtShade); Fill(data, 32, 20, 8, 12, shirtShade);
        Fill(data, 20, 20, 8, 2, shirtShade);

        // Right arm base
        Fill(data, 44, 16, 4, 4, shirt); Fill(data, 48, 16, 4, 4, skin);
        Fill(data, 40, 20, 4, 8, shirtShade); Fill(data, 44, 20, 4, 8, shirt);
        Fill(data, 48, 20, 4, 8, shirtShade); Fill(data, 52, 20, 4, 8, shirtShade);
        Fill(data, 40, 28, 4, 4, skin); Fill(data, 44, 28, 4, 4, skin);
        Fill(data, 48, 28, 4, 4, skin); Fill(data, 52, 28, 4, 4, skin);

        // Left arm base
        Fill(data, 36, 48, 4, 4, shirt); Fill(data, 40, 48, 4, 4, skin);
        Fill(data, 32, 52, 4, 8, shirtShade); Fill(data, 36, 52, 4, 8, shirt);
        Fill(data, 40, 52, 4, 8, shirtShade); Fill(data, 44, 52, 4, 8, shirtShade);
        Fill(data, 32, 60, 4, 4, skin); Fill(data, 36, 60, 4, 4, skin);
        Fill(data, 40, 60, 4, 4, skin); Fill(data, 44, 60, 4, 4, skin);

        // Right leg base
        Fill(data, 4, 16, 4, 4, pants); Fill(data, 8, 16, 4, 4, pants);
        Fill(data, 0, 20, 4, 10, pants); Fill(data, 4, 20, 4, 10, pants);
        Fill(data, 8, 20, 4, 10, pants); Fill(data, 12, 20, 4, 10, pants);
        Fill(data, 0, 30, 4, 2, shoe); Fill(data, 4, 30, 4, 2, shoe);
        Fill(data, 8, 30, 4, 2, shoe); Fill(data, 12, 30, 4, 2, shoe);

        // Left leg base
        Fill(data, 20, 48, 4, 4, pants); Fill(data, 24, 48, 4, 4, pants);
        Fill(data, 16, 52, 4, 10, pants); Fill(data, 20, 52, 4, 10, pants);
        Fill(data, 24, 52, 4, 10, pants); Fill(data, 28, 52, 4, 10, pants);
        Fill(data, 16, 62, 4, 2, shoe); Fill(data, 20, 62, 4, 2, shoe);
        Fill(data, 24, 62, 4, 2, shoe); Fill(data, 28, 62, 4, 2, shoe);

        // Overlay regions remain transparent by default for a clean default avatar.
        Clear(data, 32, 0, 32, 16);  // head overlay
        Clear(data, 16, 32, 24, 16); // body overlay
        Clear(data, 40, 32, 16, 16); // right arm overlay
        Clear(data, 48, 48, 16, 16); // left arm overlay
        Clear(data, 0, 32, 16, 16);  // right leg overlay
        Clear(data, 0, 48, 16, 16);  // left leg overlay

        tex.SetData(data);
        return tex;
    }

    private static void Fill(Color[] px, int x0, int y0, int w, int h, Color color)
    {
        for (var y = y0; y < y0 + h; y++)
        for (var x = x0; x < x0 + w; x++)
            if ((uint)x < 64 && (uint)y < 64)
                px[y * 64 + x] = color;
    }

    private static void Clear(Color[] px, int x0, int y0, int w, int h)
    {
        for (var y = y0; y < y0 + h; y++)
        for (var x = x0; x < x0 + w; x++)
            if ((uint)x < 64 && (uint)y < 64)
                px[y * 64 + x] = Color.Transparent;
    }

    private static void Dot(Color[] px, int x, int y, Color color)
    {
        if ((uint)x < 64 && (uint)y < 64)
            px[y * 64 + x] = color;
    }
}
