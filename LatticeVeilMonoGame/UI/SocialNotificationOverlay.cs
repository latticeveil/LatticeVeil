using System;
using System.Collections.Generic;
using LatticeVeilMonoGame.Core;
using LatticeVeilMonoGame.Online.Gate;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace LatticeVeilMonoGame.UI;

public sealed class SocialNotificationOverlay
{
    private readonly Queue<SocialIncomingRequestNotification> _queue = new();
    private OverlayItem? _active;

    private const double CardDurationSeconds = 7.0;
    private const double MessageDurationSeconds = 4.0;

    public void EnqueueRequest(SocialIncomingRequestNotification request)
    {
        _queue.Enqueue(request);
    }

    public bool Update(
        GameTime gameTime,
        InputState input,
        SocialNotificationMode mode,
        Action openRequestsAction)
    {
        if (mode == SocialNotificationMode.Off)
        {
            _active = null;
            return false;
        }

        var now = gameTime.TotalGameTime.TotalSeconds;
        if (_active != null && now >= _active.ExpiresAtSeconds)
            _active = null;

        if (_active == null && _queue.Count > 0)
        {
            var request = _queue.Dequeue();
            _active = BuildItem(request, now, mode);
        }

        if (_active == null || mode != SocialNotificationMode.On)
            return false;

        if (!input.IsNewLeftClick())
            return false;

        var rect = _active.Bounds;
        if (rect.Contains(input.MousePosition))
        {
            _active = null;
            openRequestsAction();
            return true;
        }

        return false;
    }

    public void Draw(
        SpriteBatch sb,
        PixelFont font,
        Texture2D pixel,
        Rectangle viewport,
        SocialNotificationMode mode)
    {
        if (_active == null || mode == SocialNotificationMode.Off)
            return;

        if (mode == SocialNotificationMode.MessageOnly)
        {
            var text = $"Friend request: {_active.DisplayName}";
            var textSize = font.MeasureString(text);
            var rect = new Rectangle(
                viewport.X + 16,
                viewport.Y + 16,
                (int)textSize.X + 16,
                (int)textSize.Y + 12);

            sb.Begin(samplerState: SamplerState.PointClamp, transformMatrix: UiLayout.Transform);
            sb.Draw(pixel, rect, new Color(0, 0, 0, 170));
            DrawBorder(sb, pixel, rect, new Color(200, 200, 200, 220));
            font.DrawString(sb, text, new Vector2(rect.X + 8, rect.Y + 6), new Color(240, 240, 240));
            sb.End();
            return;
        }

        var item = _active;
        if (item == null)
            return;

        sb.Begin(samplerState: SamplerState.PointClamp, transformMatrix: UiLayout.Transform);
        sb.Draw(pixel, item.Bounds, new Color(0, 0, 0, 200));
        DrawBorder(sb, pixel, item.Bounds, new Color(240, 240, 240, 220));
        font.DrawString(sb, "FRIEND REQUEST", new Vector2(item.Bounds.X + 10, item.Bounds.Y + 8), new Color(120, 220, 255));
        font.DrawString(sb, item.DisplayName, new Vector2(item.Bounds.X + 10, item.Bounds.Y + 8 + font.LineHeight + 4), Color.White);
        font.DrawString(sb, "Click to open requests", new Vector2(item.Bounds.X + 10, item.Bounds.Bottom - font.LineHeight - 8), new Color(200, 200, 200));
        sb.End();
    }

    private static OverlayItem BuildItem(SocialIncomingRequestNotification request, double now, SocialNotificationMode mode)
    {
        var width = mode == SocialNotificationMode.On ? 300 : 220;
        var height = mode == SocialNotificationMode.On ? 96 : 42;
        var rect = new Rectangle(UiLayout.Viewport.X + 16, UiLayout.Viewport.Y + 16, width, height);
        var expires = now + (mode == SocialNotificationMode.On ? CardDurationSeconds : MessageDurationSeconds);
        return new OverlayItem(request.DisplayName, rect, expires);
    }

    private static void DrawBorder(SpriteBatch sb, Texture2D pixel, Rectangle r, Color color)
    {
        sb.Draw(pixel, new Rectangle(r.X, r.Y, r.Width, 2), color);
        sb.Draw(pixel, new Rectangle(r.X, r.Bottom - 2, r.Width, 2), color);
        sb.Draw(pixel, new Rectangle(r.X, r.Y, 2, r.Height), color);
        sb.Draw(pixel, new Rectangle(r.Right - 2, r.Y, 2, r.Height), color);
    }

    private sealed record OverlayItem(
        string DisplayName,
        Rectangle Bounds,
        double ExpiresAtSeconds);
}
