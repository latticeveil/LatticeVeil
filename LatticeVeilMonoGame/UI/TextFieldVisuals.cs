using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using LatticeVeilMonoGame.Core;

namespace LatticeVeilMonoGame.UI;

public static class TextFieldVisuals
{
    public static void HandleWholeFieldPointerSelection(
        InputState input,
        Rectangle rect,
        ref bool focused,
        ref bool selectAll,
        string text,
        bool enabled = true)
    {
        if (!enabled)
        {
            focused = false;
            selectAll = false;
            return;
        }

        if (input.IsNewLeftClick())
        {
            var containsPointer = rect.Contains(input.MousePosition);
            if (!containsPointer)
            {
                focused = false;
                selectAll = false;
            }
            else
            {
                selectAll = focused && !string.IsNullOrEmpty(text);
                focused = true;
            }
        }

        if (focused && !string.IsNullOrEmpty(text) && rect.Contains(input.MousePosition) && input.IsLeftDragActive())
            selectAll = true;
    }

    public static void DrawWholeFieldSelection(
        SpriteBatch sb,
        Texture2D pixel,
        PixelFont font,
        string text,
        Vector2 textPosition,
        Rectangle clipRect,
        bool selectAll,
        Color fillColor)
    {
        if (!selectAll || string.IsNullOrEmpty(text))
            return;

        var textSize = font.MeasureString(text);
        var width = Math.Min((int)System.Math.Ceiling(textSize.X), clipRect.Width - (int)textPosition.X + clipRect.X);
        var height = System.Math.Min(font.LineHeight + 4, clipRect.Height - 4);
        if (width <= 0 || height <= 0)
            return;

        var selectionRect = new Rectangle(
            (int)System.Math.Floor(textPosition.X) - 1,
            (int)System.Math.Floor(textPosition.Y) - 2,
            width + 2,
            height);

        selectionRect = Rectangle.Intersect(selectionRect, clipRect);
        if (selectionRect.Width > 0 && selectionRect.Height > 0)
            sb.Draw(pixel, selectionRect, fillColor);
    }

    public static void DrawCaret(
        SpriteBatch sb,
        Texture2D pixel,
        PixelFont font,
        string text,
        Vector2 textPosition,
        Rectangle clipRect,
        bool focused,
        bool selectAll,
        double blinkClock,
        Color color)
    {
        if (!focused || selectAll || ((int)(blinkClock * 2.0) % 2) != 0)
            return;

        var caretX = textPosition.X + font.MeasureString(text).X;
        var caretRect = new Rectangle(
            (int)System.Math.Round(caretX),
            (int)System.Math.Floor(textPosition.Y) - 1,
            2,
            font.LineHeight + 2);

        caretRect = Rectangle.Intersect(caretRect, clipRect);
        if (caretRect.Width > 0 && caretRect.Height > 0)
            sb.Draw(pixel, caretRect, color);
    }
}
