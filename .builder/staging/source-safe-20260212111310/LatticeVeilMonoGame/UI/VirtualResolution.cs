using Microsoft.Xna.Framework;

namespace LatticeVeilMonoGame.UI;

public static class VirtualResolution
{
    // Virtual resolution for UI design (1920x1080)
    public const int VirtualWidth = 1920;
    public const int VirtualHeight = 1080;
    
    public static Matrix ScaleMatrix { get; private set; } = Matrix.Identity;
    public static float ScaleX { get; private set; } = 1f;
    public static float ScaleY { get; private set; } = 1f;
    
    public static void Update(Rectangle actualViewport)
    {
        ScaleX = (float)actualViewport.Width / VirtualWidth;
        ScaleY = (float)actualViewport.Height / VirtualHeight;
        ScaleMatrix = Matrix.CreateScale(ScaleX, ScaleY, 1f);
    }
    
    public static Rectangle ToVirtual(Rectangle screenRect)
    {
        return new Rectangle(
            (int)(screenRect.X / ScaleX),
            (int)(screenRect.Y / ScaleY),
            (int)(screenRect.Width / ScaleX),
            (int)(screenRect.Height / ScaleY)
        );
    }
    
    public static Rectangle ToScreen(Rectangle virtualRect)
    {
        return new Rectangle(
            (int)(virtualRect.X * ScaleX),
            (int)(virtualRect.Y * ScaleY),
            (int)(virtualRect.Width * ScaleX),
            (int)(virtualRect.Height * ScaleY)
        );
    }
    
    public static Vector2 ToVirtual(Vector2 screenPos)
    {
        return new Vector2(screenPos.X / ScaleX, screenPos.Y / ScaleY);
    }
    
    public static Vector2 ToScreen(Vector2 virtualPos)
    {
        return new Vector2(virtualPos.X * ScaleX, virtualPos.Y * ScaleY);
    }
}
