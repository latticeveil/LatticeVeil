using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using LatticeVeilMonoGame.Core;
using LatticeVeilMonoGame.Online.Gate;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace LatticeVeilMonoGame.UI;

public sealed class SocialNotificationOverlay
{
    private readonly Queue<SocialIncomingRequestNotification> _queue = new();
    private readonly Queue<SocialWorldInviteNotification> _inviteQueue = new();
    private readonly MemoryWebTextureLoader _webTextureLoader = new();
    private readonly Dictionary<string, AvatarVisualState> _avatarVisuals = new(StringComparer.OrdinalIgnoreCase);
    private OverlayItem? _active;
    private double _scrollClock;

    private const double CardDurationSeconds = 7.0;
    private const double MessageDurationSeconds = 4.0;

    public void EnqueueRequest(SocialIncomingRequestNotification request)
    {
        _queue.Enqueue(request);
    }

    public void EnqueueWorldInvite(SocialWorldInviteNotification invite)
    {
        _inviteQueue.Enqueue(invite);
        QueueAvatarLoad((invite.SenderPictureUrl ?? string.Empty).Trim());
    }

    public bool Update(
        GameTime gameTime,
        InputState input,
        SocialNotificationMode mode,
        Action openRequestsAction,
        Action<SocialWorldInviteNotification> openWorldInviteAction)
    {
        if (mode == SocialNotificationMode.Off)
        {
            _active = null;
            return false;
        }

        var now = gameTime.TotalGameTime.TotalSeconds;
        _scrollClock = now;
        if (_active != null && now >= _active.ExpiresAtSeconds)
            _active = null;

        if (_active == null && _queue.Count > 0)
        {
            var request = _queue.Dequeue();
            _active = BuildItem(request, now, mode);
        }
        else if (_active == null && _inviteQueue.Count > 0)
        {
            var invite = _inviteQueue.Dequeue();
            _active = BuildInviteItem(invite, now, mode);
        }

        if (_active == null || mode != SocialNotificationMode.On)
            return false;

        if (!input.IsNewLeftClick())
            return false;

        var rect = _active.Bounds;
        if (rect.Contains(input.MousePosition))
        {
            if (_active.WorldInvite != null)
                openWorldInviteAction(_active.WorldInvite);
            else
                openRequestsAction();
            _active = null;
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
            var text = _active.WorldInvite != null
                ? $"World invite: {_active.DisplayName}"
                : $"Friend request: {_active.DisplayName}";
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

        ProcessCompletedAvatarLoads(sb.GraphicsDevice);

        sb.Begin(samplerState: SamplerState.PointClamp, transformMatrix: UiLayout.Transform);
        sb.Draw(pixel, item.Bounds, new Color(0, 0, 0, 200));
        DrawBorder(sb, pixel, item.Bounds, new Color(240, 240, 240, 220));
        var title = item.WorldInvite != null ? "WORLD INVITE" : "FRIEND REQUEST";
        var actionText = item.WorldInvite != null ? "Click to join" : "Click to open requests";
        var contentX = item.Bounds.X + 10;
        if (item.WorldInvite != null)
        {
            var avatarRect = new Rectangle(item.Bounds.X + 10, item.Bounds.Y + 32, 48, 48);
            if (TryGetAvatarTexture((item.WorldInvite.SenderPictureUrl ?? string.Empty).Trim(), out var avatarTexture))
                sb.Draw(avatarTexture, avatarRect, Color.White);
            else
                DrawFallbackBadge(sb, pixel, avatarRect, item.DisplayName);
            DrawBorder(sb, pixel, avatarRect, Color.White);
            contentX = avatarRect.Right + 10;
        }

        font.DrawString(sb, title, new Vector2(contentX, item.Bounds.Y + 8), new Color(120, 220, 255));
        font.DrawString(sb, item.DisplayName, new Vector2(contentX, item.Bounds.Y + 8 + font.LineHeight + 4), Color.White);
        if (item.WorldInvite != null)
        {
            var modeText = string.IsNullOrWhiteSpace(item.WorldInvite.GameMode) ? "UNKNOWN MODE" : item.WorldInvite.GameMode.Trim().ToUpperInvariant();
            var worldName = string.IsNullOrWhiteSpace(item.WorldInvite.WorldName) ? "UNKNOWN WORLD" : item.WorldInvite.WorldName.Trim();
            font.DrawString(sb, modeText, new Vector2(contentX, item.Bounds.Y + 8 + (font.LineHeight + 4) * 2), new Color(180, 214, 240));

            var worldRect = new Rectangle(
                contentX,
                item.Bounds.Y + 8 + (font.LineHeight + 4) * 3,
                Math.Max(40, item.Bounds.Right - contentX - 10),
                font.LineHeight);
            DrawContainedScrollingText(sb, pixel, font, worldName, worldRect, new Color(220, 228, 245), _scrollClock, 18);
        }
        font.DrawString(sb, actionText, new Vector2(contentX, item.Bounds.Bottom - font.LineHeight - 8), new Color(200, 200, 200));
        sb.End();
    }

    private static OverlayItem BuildItem(SocialIncomingRequestNotification request, double now, SocialNotificationMode mode)
    {
        var width = mode == SocialNotificationMode.On ? 300 : 220;
        var height = mode == SocialNotificationMode.On ? 96 : 42;
        var rect = new Rectangle(UiLayout.Viewport.X + 16, UiLayout.Viewport.Y + 16, width, height);
        var expires = now + (mode == SocialNotificationMode.On ? CardDurationSeconds : MessageDurationSeconds);
        return new OverlayItem(request.DisplayName, rect, expires, null);
    }

    private static OverlayItem BuildInviteItem(SocialWorldInviteNotification invite, double now, SocialNotificationMode mode)
    {
        var width = mode == SocialNotificationMode.On ? 560 : 240;
        var height = mode == SocialNotificationMode.On ? 132 : 42;
        var rect = new Rectangle(UiLayout.Viewport.X + 16, UiLayout.Viewport.Y + 16, width, height);
        var expires = now + (mode == SocialNotificationMode.On ? CardDurationSeconds : MessageDurationSeconds);
        return new OverlayItem(invite.SenderDisplayName, rect, expires, invite);
    }

    private void QueueAvatarLoad(string avatarUrl)
    {
        if (string.IsNullOrWhiteSpace(avatarUrl))
            return;

        if (!_avatarVisuals.TryGetValue(avatarUrl, out var state))
        {
            state = new AvatarVisualState();
            _avatarVisuals[avatarUrl] = state;
        }

        if (state.Texture == null && state.BytesTask == null)
            state.BytesTask = _webTextureLoader.DownloadImageBytesAsync(avatarUrl);
    }

    private void ProcessCompletedAvatarLoads(GraphicsDevice graphicsDevice)
    {
        foreach (var pair in _avatarVisuals)
        {
            var state = pair.Value;
            if (state.BytesTask == null || !state.BytesTask.IsCompleted)
                continue;

            if (state.BytesTask.Status == TaskStatus.RanToCompletion)
            {
                var bytes = state.BytesTask.Result;
                if (bytes is { Length: > 0 })
                    state.Texture = CreateCircularAvatarTextureFromBytes(graphicsDevice, bytes);
            }

            state.BytesTask = null;
        }
    }

    private bool TryGetAvatarTexture(string avatarUrl, out Texture2D? texture)
    {
        texture = null;
        if (string.IsNullOrWhiteSpace(avatarUrl))
            return false;

        if (_avatarVisuals.TryGetValue(avatarUrl, out var state) && state.Texture is { IsDisposed: false })
        {
            texture = state.Texture;
            return true;
        }

        QueueAvatarLoad(avatarUrl);
        return false;
    }

    private static Texture2D? CreateCircularAvatarTextureFromBytes(GraphicsDevice graphicsDevice, byte[] imageBytes)
    {
        try
        {
            using var stream = new MemoryStream(imageBytes, writable: false);
            var texture = Texture2D.FromStream(graphicsDevice, stream);
            var colorData = new Color[texture.Width * texture.Height];
            texture.GetData(colorData);

            var masked = new Texture2D(graphicsDevice, texture.Width, texture.Height);
            var center = new Vector2(texture.Width / 2f, texture.Height / 2f);
            var radius = Math.Min(texture.Width, texture.Height) / 2f;
            for (var y = 0; y < texture.Height; y++)
            {
                for (var x = 0; x < texture.Width; x++)
                {
                    var dx = x + 0.5f - center.X;
                    var dy = y + 0.5f - center.Y;
                    if ((dx * dx) + (dy * dy) > radius * radius)
                        colorData[(y * texture.Width) + x] = Color.Transparent;
                }
            }

            masked.SetData(colorData);
            texture.Dispose();
            return masked;
        }
        catch
        {
            return null;
        }
    }

    private static void DrawFallbackBadge(SpriteBatch sb, Texture2D pixel, Rectangle rect, string seed)
    {
        var baseColor = ResolveBadgeColor(seed);
        var center = new Vector2(rect.Center.X, rect.Center.Y);
        var radius = rect.Width / 2f;
        for (var y = rect.Top; y < rect.Bottom; y++)
        {
            for (var x = rect.Left; x < rect.Right; x++)
            {
                var dx = x + 0.5f - center.X;
                var dy = y + 0.5f - center.Y;
                if ((dx * dx) + (dy * dy) > radius * radius)
                    continue;

                sb.Draw(pixel, new Rectangle(x, y, 1, 1), baseColor);
            }
        }
    }

    private static Color ResolveBadgeColor(string seed)
    {
        var text = string.IsNullOrWhiteSpace(seed) ? "friend" : seed.Trim().ToLowerInvariant();
        var hash = 17;
        for (var i = 0; i < text.Length; i++)
            hash = (hash * 31) + text[i];

        var palette = new[]
        {
            new Color(88, 114, 214),
            new Color(98, 168, 122),
            new Color(184, 123, 82),
            new Color(147, 102, 196),
            new Color(74, 150, 170)
        };

        return palette[Math.Abs(hash) % palette.Length];
    }

    private static void DrawBorder(SpriteBatch sb, Texture2D pixel, Rectangle r, Color color)
    {
        sb.Draw(pixel, new Rectangle(r.X, r.Y, r.Width, 2), color);
        sb.Draw(pixel, new Rectangle(r.X, r.Bottom - 2, r.Width, 2), color);
        sb.Draw(pixel, new Rectangle(r.X, r.Y, 2, r.Height), color);
        sb.Draw(pixel, new Rectangle(r.Right - 2, r.Y, 2, r.Height), color);
    }

    private static void DrawContainedScrollingText(
        SpriteBatch sb,
        Texture2D pixel,
        PixelFont font,
        string text,
        Rectangle rect,
        Color color,
        double timer,
        int holdChars)
    {
        text = (text ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(text) || rect.Width <= 0)
            return;

        var charWidth = Math.Max(1f, font.MeasureString("W").X);
        var maxChars = Math.Max(1, (int)(rect.Width / charWidth));
        var visible = text;
        if (text.Length > maxChars)
        {
            var padded = text + "   " + text;
            var scrollable = text.Length + 3;
            var step = Math.Max(0, (int)Math.Floor(Math.Max(0d, timer - holdChars / 6d) * 2.25d) % scrollable);
            visible = padded.Substring(step, Math.Min(maxChars, padded.Length - step));
        }

        sb.Draw(pixel, rect, new Color(0, 0, 0, 80));
        font.DrawString(sb, visible, new Vector2(rect.X, rect.Y), color);
    }

    private sealed record OverlayItem(
        string DisplayName,
        Rectangle Bounds,
        double ExpiresAtSeconds,
        SocialWorldInviteNotification? WorldInvite);

    private sealed class AvatarVisualState
    {
        public Task<byte[]?>? BytesTask { get; set; }
        public Texture2D? Texture { get; set; }
    }
}
