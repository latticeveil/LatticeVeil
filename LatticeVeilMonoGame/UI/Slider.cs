using System;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using LatticeVeilMonoGame.Core;

namespace LatticeVeilMonoGame.UI;

public sealed class Slider
{
    public Rectangle Bounds { get; set; }
    public string Label { get; set; }
    public float Value { get; set; } // 0..1

    private bool _dragging;
    private readonly Action<float>? _onChanged;
    private readonly Action<float>? _onCommitted;

    public Slider(string label, float initial, Action<float>? onChanged = null, Action<float>? onCommitted = null)
    {
        Label = label;
        Value = Clamp01(initial);
        _onChanged = onChanged;
        _onCommitted = onCommitted;
    }

    public void Update(InputState input)
    {
        var p = input.MousePosition;

        var wasDragging = _dragging;

        if (input.IsNewLeftClick() && Bounds.Contains(p))
            _dragging = true;

        if (!input.IsLeftDown())
            _dragging = false;

        if (_dragging)
        {
            var t = (p.X - Bounds.X) / (float)Bounds.Width;
            Value = Clamp01(t);
            _onChanged?.Invoke(Value);
        }

        if (wasDragging && !_dragging)
            _onCommitted?.Invoke(Value);
    }

    public void Draw(SpriteBatch sb, Texture2D pixel, PixelFont font)
    {
        // label above
        font.DrawString(sb, Label, new Vector2(Bounds.X, Bounds.Y - font.LineHeight), Color.White);

        // track
        sb.Draw(pixel, Bounds, new Color(20, 20, 20));
        sb.Draw(pixel, new Rectangle(Bounds.X, Bounds.Y, (int)(Bounds.Width * Value), Bounds.Height), new Color(200, 200, 200));

        // border
        sb.Draw(pixel, new Rectangle(Bounds.X, Bounds.Y, Bounds.Width, 2), Color.White);
        sb.Draw(pixel, new Rectangle(Bounds.X, Bounds.Bottom - 2, Bounds.Width, 2), Color.White);
        sb.Draw(pixel, new Rectangle(Bounds.X, Bounds.Y, 2, Bounds.Height), Color.White);
        sb.Draw(pixel, new Rectangle(Bounds.Right - 2, Bounds.Y, 2, Bounds.Height), Color.White);
    }

    private static float Clamp01(float v) => v < 0f ? 0f : (v > 1f ? 1f : v);
}
