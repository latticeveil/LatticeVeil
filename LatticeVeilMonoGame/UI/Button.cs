using System;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using LatticeVeilMonoGame.Core;

namespace LatticeVeilMonoGame.UI;

public sealed class Button
{
    public Rectangle Bounds { get; set; }
    public Texture2D? Texture { get; set; }
    public string Label { get; set; }
    public bool Visible { get; set; } = true;
    public Color? BackgroundColor { get; set; }

	/// <summary>
	/// When false, the button is drawn in a disabled style and will not fire clicks.
	/// </summary>
	public bool Enabled { get; set; } = true;
    /// <summary>
    /// When true, the button is drawn in a disabled style even if it is enabled.
    /// </summary>
    public bool ForceDisabledStyle { get; set; } = false;
    /// <summary>
    /// When true, draws the label slightly thicker for readability.
    /// </summary>
    public bool BoldText { get; set; } = false;
    public bool IsHovered { get; private set; }

    private readonly Action _onClick;

    public Button(string label, Action onClick)
    {
        Label = label;
        _onClick = onClick;
    }

    public void Update(InputState input)
    {
        if (!Visible || !Enabled)
        {
            IsHovered = false;
            return;
        }

        IsHovered = Bounds.Contains(input.MousePosition);
        if (!input.IsNewLeftClick()) return;

        if (IsHovered)
            _onClick();
    }

    public bool TryClick(Point p)
    {
        if (!Visible || !Enabled)
        {
            IsHovered = false;
            return false;
        }

        IsHovered = Bounds.Contains(p);
        if (!IsHovered)
            return false;
        _onClick();
        return true;
    }

    public void Draw(SpriteBatch sb, Texture2D pixel, PixelFont font)
    {
		if (!Visible) return;

        var effectivelyEnabled = Enabled && !ForceDisabledStyle;

        if (Texture is not null)
        {
            var drawBounds = IsHovered && effectivelyEnabled
                ? new Rectangle(Bounds.X, Bounds.Y - 2, Bounds.Width, Bounds.Height)
                : Bounds;
            if (IsHovered && effectivelyEnabled)
            {
                DrawTextureHoverGlow(sb, Texture, drawBounds, new Color(188, 232, 255, 76));
            }

            sb.Draw(Texture, drawBounds, ForceDisabledStyle ? new Color(128, 128, 128) : Color.White);
            // If a texture is provided, we do not draw label text over it (prevents overlap).
            return;
        }

		var bg = BackgroundColor ?? (effectivelyEnabled ? new Color(30, 30, 30) : new Color(20, 20, 20));
		var fg = effectivelyEnabled ? Color.White : new Color(160, 160, 160);
        if (IsHovered && effectivelyEnabled)
        {
            bg = BackgroundColor.HasValue
                ? LightenColor(bg, 26)
                : new Color(45, 90, 145, 230);
            fg = new Color(240, 248, 255);
        }
		sb.Draw(pixel, Bounds, bg);
		DrawBorder(sb, pixel, Bounds, fg);

        var size = font.MeasureString(Label);
        var pos = new Vector2(Bounds.Center.X - size.X / 2f, Bounds.Center.Y - size.Y / 2f);
		if (BoldText)
        {
			font.DrawString(sb, Label, pos, fg);
			font.DrawString(sb, Label, pos + new Vector2(1, 0), fg);
        }
        else
        {
			font.DrawString(sb, Label, pos, fg);
        }
    }

    private static void DrawBorder(SpriteBatch sb, Texture2D pixel, Rectangle r, Color color)
    {
        sb.Draw(pixel, new Rectangle(r.X, r.Y, r.Width, 2), color);
        sb.Draw(pixel, new Rectangle(r.X, r.Bottom - 2, r.Width, 2), color);
        sb.Draw(pixel, new Rectangle(r.X, r.Y, 2, r.Height), color);
        sb.Draw(pixel, new Rectangle(r.Right - 2, r.Y, 2, r.Height), color);
    }

    private static void DrawTextureHoverGlow(SpriteBatch sb, Texture2D texture, Rectangle bounds, Color glowColor)
    {
        ReadOnlySpan<Point> offsets =
        [
            new Point(-3, 0),
            new Point(3, 0),
            new Point(0, -3),
            new Point(0, 3),
            new Point(-2, -2),
            new Point(2, -2),
            new Point(-2, 2),
            new Point(2, 2),
            new Point(-1, 0),
            new Point(1, 0),
            new Point(0, -1),
            new Point(0, 1)
        ];

        foreach (var offset in offsets)
        {
            var glowBounds = new Rectangle(
                bounds.X + offset.X,
                bounds.Y + offset.Y,
                bounds.Width,
                bounds.Height);
            sb.Draw(texture, glowBounds, glowColor);
        }
    }

    private static Color LightenColor(Color color, int amount)
    {
        return new Color(
            Math.Clamp(color.R + amount, 0, 255),
            Math.Clamp(color.G + amount, 0, 255),
            Math.Clamp(color.B + amount, 0, 255),
            color.A);
    }
}
