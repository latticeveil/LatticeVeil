using System;
using Microsoft.Xna.Framework;

namespace LatticeVeilMonoGame.UI;

public static class UiLayout
{
    public const float BaseScale = 0.8f;
    public const float MinScale = 0.6f;
    public const float MaxScale = 2.0f;

    public static float Scale { get; private set; } = 1f;
    public static Point Offset { get; private set; } = Point.Zero;
    public static Rectangle Viewport { get; private set; } = new(0, 0, 1, 1);
    public static Rectangle WindowViewport { get; private set; } = new(0, 0, 1, 1);
    public static Matrix Transform { get; private set; } = Matrix.Identity;

    public static float GetEffectiveScale(float userScale)
    {
        var scale = Math.Clamp(userScale, MinScale, MaxScale);
        return scale * BaseScale;
    }

    public static bool Update(Rectangle windowViewport, float effectiveScale)
    {
        WindowViewport = windowViewport;
        var scale = Math.Clamp(effectiveScale, MinScale * BaseScale, MaxScale * BaseScale);
        var virtualWidth = Math.Max(1, (int)Math.Round(windowViewport.Width / scale));
        var virtualHeight = Math.Max(1, (int)Math.Round(windowViewport.Height / scale));
        var virtualViewport = new Rectangle(0, 0, virtualWidth, virtualHeight);

        var scaledWidth = (int)Math.Round(virtualWidth * scale);
        var scaledHeight = (int)Math.Round(virtualHeight * scale);
        var offsetX = (int)Math.Round((windowViewport.Width - scaledWidth) / 2f);
        var offsetY = (int)Math.Round((windowViewport.Height - scaledHeight) / 2f);

        var changed = Math.Abs(scale - Scale) > 0.001f
            || offsetX != Offset.X
            || offsetY != Offset.Y
            || virtualViewport != Viewport;

        Scale = scale;
        Offset = new Point(offsetX, offsetY);
        Viewport = virtualViewport;
        Transform = Matrix.CreateScale(Scale, Scale, 1f) * Matrix.CreateTranslation(Offset.X, Offset.Y, 0f);

        return changed;
    }

    public static Rectangle ToScreenRect(Rectangle uiRect)
    {
        var x = (int)Math.Floor(uiRect.X * Scale + Offset.X);
        var y = (int)Math.Floor(uiRect.Y * Scale + Offset.Y);
        var w = (int)Math.Ceiling(uiRect.Width * Scale);
        var h = (int)Math.Ceiling(uiRect.Height * Scale);
        return new Rectangle(x, y, w, h);
    }
}
