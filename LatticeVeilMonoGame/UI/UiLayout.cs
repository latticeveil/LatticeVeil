using System;
using Microsoft.Xna.Framework;

namespace LatticeVeilMonoGame.UI;

public static class UiLayout
{
    public const float ReferenceWidth = 1920f;
    public const float ReferenceHeight = 1080f;
    public const float MinUserScale = 1.0f;
    public const float MaxUserScale = 2.0f;

    public static float Scale { get; private set; } = 1f;
    public static Point Offset { get; private set; } = Point.Zero;
    public static Rectangle Viewport { get; private set; } = new(0, 0, 1, 1);
    public static Rectangle WindowViewport { get; private set; } = new(0, 0, 1, 1);
    public static Matrix Transform { get; private set; } = Matrix.Identity;
    public static float GetEffectiveScale(float userScale)
    {
        var normalized = Math.Clamp(userScale, MinUserScale, MaxUserScale);
        var t = (normalized - MinUserScale) / (MaxUserScale - MinUserScale);
        return MathHelper.Lerp(0.92f, 1.14f, t);
    }

    public static bool Update(Rectangle windowViewport, float effectiveScale)
    {
        WindowViewport = windowViewport;
        var referenceScale = GetReferenceResolutionScale(windowViewport);
        var scale = Math.Max(0.0001f, referenceScale * effectiveScale);
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

    private static float GetReferenceResolutionScale(Rectangle windowViewport)
    {
        if (windowViewport.Width <= 0 || windowViewport.Height <= 0)
            return 1f;

        var widthFactor = windowViewport.Width / ReferenceWidth;
        var heightFactor = windowViewport.Height / ReferenceHeight;
        return MathF.Min(widthFactor, heightFactor);
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
