using System;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using LatticeVeilMonoGame.Core;

namespace LatticeVeilMonoGame.UI;

public sealed class Checkbox
{
    public Rectangle Bounds { get; set; }
    public string Label { get; set; }
    public bool Value { get; set; }

    private readonly Action<bool>? _onChanged;

    public Checkbox(string label, bool initial, Action<bool>? onChanged = null)
    {
        Label = label;
        Value = initial;
        _onChanged = onChanged;
    }

    public void Update(InputState input)
    {
        if (!input.IsNewLeftClick()) return;
        var p = input.MousePosition;
        if (Bounds.Contains(p))
        {
            Value = !Value;
            _onChanged?.Invoke(Value);
        }
    }

    public void Draw(SpriteBatch sb, Texture2D pixel, PixelFont font)
    {
        // checkbox square
        var box = new Rectangle(Bounds.X, Bounds.Y, Bounds.Height, Bounds.Height);
        sb.Draw(pixel, box, new Color(20, 20, 20));
        sb.Draw(pixel, new Rectangle(box.X, box.Y, box.Width, 2), Color.White);
        sb.Draw(pixel, new Rectangle(box.X, box.Bottom - 2, box.Width, 2), Color.White);
        sb.Draw(pixel, new Rectangle(box.X, box.Y, 2, box.Height), Color.White);
        sb.Draw(pixel, new Rectangle(box.Right - 2, box.Y, 2, box.Height), Color.White);

        if (Value)
        {
            // MonoGame Rectangle.Inflate(...) is an instance method (mutates the rectangle).
            var inner = box;
            inner.Inflate(-6, -6);
            sb.Draw(pixel, inner, Color.White);
        }

        // label to the right
        font.DrawString(sb, Label, new Vector2(box.Right + 10, box.Y + 2), Color.White);
    }
}
