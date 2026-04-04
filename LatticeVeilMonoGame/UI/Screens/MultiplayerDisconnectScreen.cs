using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using LatticeVeilMonoGame.Core;
using LatticeVeilMonoGame.UI;

namespace LatticeVeilMonoGame.UI.Screens;

public sealed class MultiplayerDisconnectScreen : IScreen
{
    private readonly MenuStack _menus;
    private readonly AssetLoader _assets;
    private readonly PixelFont _font;
    private readonly Texture2D _pixel;
    private readonly string _message;
    private readonly Button _backBtn;
    private Texture2D? _bg;
    private Rectangle _viewport;
    private Rectangle _panelRect;

    public MultiplayerDisconnectScreen(
        MenuStack menus,
        AssetLoader assets,
        PixelFont font,
        Texture2D pixel,
        string message)
    {
        _menus = menus;
        _assets = assets;
        _font = font;
        _pixel = pixel;
        _message = string.IsNullOrWhiteSpace(message) ? "Disconnected." : message.Trim();
        _backBtn = new Button("BACK", () => _menus.Pop()) { BoldText = true };
        try
        {
            _bg = _assets.LoadTexture("textures/menu/backgrounds/Kicked_background.png");
        }
        catch
        {
            try { _bg = _assets.LoadTexture("textures/missing.png"); }
            catch { _bg = null; }
        }
    }

    public void OnResize(Rectangle viewport)
    {
        _viewport = viewport;
        var panelW = Math.Min(780, (int)(viewport.Width * 0.9f));
        var panelH = Math.Min(280, (int)(viewport.Height * 0.55f));
        var panelX = viewport.X + (viewport.Width - panelW) / 2;
        var panelY = viewport.Y + (viewport.Height - panelH) / 2;
        _panelRect = new Rectangle(panelX, panelY, panelW, panelH);
        var buttonW = Math.Max(150, panelW / 4);
        var buttonH = Math.Max(44, _font.LineHeight * 2);
        _backBtn.Bounds = new Rectangle(_panelRect.Center.X - buttonW / 2, _panelRect.Bottom - buttonH - 18, buttonW, buttonH);
    }

    public void Update(GameTime gameTime, InputState input)
    {
        if (input.IsNewKeyPress(Keys.Escape) || input.IsNewKeyPress(Keys.Enter))
        {
            _menus.Pop();
            return;
        }

        _backBtn.Update(input);
    }

    public void Draw(SpriteBatch sb, Rectangle viewport)
    {
        if (viewport != _viewport)
            OnResize(viewport);

        sb.Begin(samplerState: SamplerState.PointClamp);
        if (_bg is not null)
            sb.Draw(_bg, UiLayout.WindowViewport, Color.White);
        else
            sb.Draw(_pixel, UiLayout.WindowViewport, new Color(0, 0, 0));
        sb.End();

        sb.Begin(samplerState: SamplerState.PointClamp, transformMatrix: UiLayout.Transform);
        sb.Draw(_pixel, _panelRect, new Color(0, 0, 0, 220));
        DrawBorder(sb, _panelRect, new Color(215, 225, 255, 210));

        var title = _message.StartsWith("Kicked by Host", StringComparison.OrdinalIgnoreCase)
            ? "MULTIPLAYER DISCONNECTED"
            : "CONNECTION CLOSED";
        DrawHeading(sb, title, new Vector2(_panelRect.X + 20, _panelRect.Y + 18));

        var lines = WrapText(_message, _panelRect.Width - 40);
        var y = _panelRect.Y + 66;
        for (var i = 0; i < lines.Count; i++)
        {
            _font.DrawString(sb, lines[i], new Vector2(_panelRect.X + 20, y), new Color(235, 235, 245));
            y += _font.LineHeight + 2;
        }

        _backBtn.Draw(sb, _pixel, _font);
        sb.End();
    }

    public void OnClose()
    {
    }

    private void DrawHeading(SpriteBatch sb, string text, Vector2 pos)
    {
        _font.DrawString(sb, text, pos + new Vector2(1, 1), Color.Black);
        _font.DrawString(sb, text, pos, Color.White);
    }

    private void DrawBorder(SpriteBatch sb, Rectangle rect, Color color)
    {
        sb.Draw(_pixel, new Rectangle(rect.X, rect.Y, rect.Width, 2), color);
        sb.Draw(_pixel, new Rectangle(rect.X, rect.Bottom - 2, rect.Width, 2), color);
        sb.Draw(_pixel, new Rectangle(rect.X, rect.Y, 2, rect.Height), color);
        sb.Draw(_pixel, new Rectangle(rect.Right - 2, rect.Y, 2, rect.Height), color);
    }

    private List<string> WrapText(string text, int maxWidth)
    {
        var lines = new List<string>();
        var content = (text ?? string.Empty).Trim();
        if (content.Length == 0)
        {
            lines.Add(string.Empty);
            return lines;
        }

        var words = content.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var current = string.Empty;
        for (var i = 0; i < words.Length; i++)
        {
            var candidate = current.Length == 0 ? words[i] : $"{current} {words[i]}";
            if (_font.MeasureString(candidate).X <= maxWidth)
            {
                current = candidate;
                continue;
            }

            if (current.Length > 0)
                lines.Add(current);
            current = words[i];
        }

        if (current.Length > 0)
            lines.Add(current);
        return lines;
    }
}
