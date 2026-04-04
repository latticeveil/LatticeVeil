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

    private readonly Action _onClick;

    public Button(string label, Action onClick)
    {
        Label = label;
        _onClick = onClick;
    }

    public void Update(InputState input)
    {
		if (!Visible || !Enabled) return;
        if (!input.IsNewLeftClick()) return;

        var p = input.MousePosition;
        if (Bounds.Contains(p))
            _onClick();
    }

    public bool TryClick(Point p)
    {
		if (!Visible || !Enabled) return false;
        if (!Bounds.Contains(p))
            return false;
        _onClick();
        return true;
    }

    public void Draw(SpriteBatch sb, Texture2D pixel, PixelFont font)
    {
		if (!Visible) return;

        if (Texture is not null)
        {
            sb.Draw(Texture, Bounds, ForceDisabledStyle ? new Color(128, 128, 128) : Color.White);
            // If a texture is provided, we do not draw label text over it (prevents overlap).
            return;
        }

		var effectivelyEnabled = Enabled && !ForceDisabledStyle;
		var bg = BackgroundColor ?? (effectivelyEnabled ? new Color(30, 30, 30) : new Color(20, 20, 20));
		var fg = effectivelyEnabled ? Color.White : new Color(160, 160, 160);
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
}
