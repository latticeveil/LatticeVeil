using System;
using WinClipboard = System.Windows.Forms.Clipboard;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using LatticeVeilMonoGame.Core;
using LatticeVeilMonoGame.UI;

namespace LatticeVeilMonoGame.UI.Screens;

/// <summary>
/// Simple modal screen that displays a join code for an EOS-hosted session.
/// </summary>
public sealed class ShareJoinCodeScreen : IScreen
{
    private readonly MenuStack _menus;
    private readonly AssetLoader _assets;
    private readonly PixelFont _font;
    private readonly Texture2D _pixel;
    private readonly Logger _log;

    private readonly string _code;
    private readonly string? _hostUserId;

    private readonly Button _copyBtn;
    private readonly Button _copyIdBtn;
    private readonly Button _okBtn;

    private string _toast = string.Empty;
    private float _toastTimer;

    private Texture2D? _bg;
    private Rectangle _viewport;
    private Rectangle _panelRect;

    public ShareJoinCodeScreen(MenuStack menus, AssetLoader assets, PixelFont font, Texture2D pixel, Logger log, string joinCode, string? hostUserId = null)
    {
        _menus = menus;
        _assets = assets;
        _font = font;
        _pixel = pixel;
        _log = log;
        _code = (joinCode ?? string.Empty).Trim();
        _hostUserId = string.IsNullOrWhiteSpace(hostUserId) ? null : hostUserId.Trim();

        _copyBtn = new Button("COPY CODE", CopyCodeToClipboard) { BoldText = true };
        // NOTE: Button in this UI framework does not expose an "Enabled" property.
        // We keep the button clickable and show a toast if the ID isn't available yet.
        // This is the local user's EOS Product User ID (PUID). Useful when the host needs to share their ID directly.
        _copyIdBtn = new Button("COPY MY ID", CopyHostIdToClipboard) { BoldText = true };
        _okBtn = new Button("OK", () => _menus.Pop()) { BoldText = true };

        try
        {
            _bg = _assets.LoadTexture("textures/menu/backgrounds/ShareJoinCode_bg.png");
        }
        catch
        {
            // optional
        }
    }

    public void OnResize(Rectangle viewport)
    {
        _viewport = viewport;

        var panelW = Math.Min(740, (int)(viewport.Width * 0.92f));
        // Extra height so we can show both the join code and the host's user ID.
        var panelH = Math.Min(320, (int)(viewport.Height * 0.7f));
        var panelX = viewport.X + (viewport.Width - panelW) / 2;
        var panelY = viewport.Y + (viewport.Height - panelH) / 2;
        _panelRect = new Rectangle(panelX, panelY, panelW, panelH);

        var buttonW = 160;
        var buttonH = Math.Max(_font.LineHeight * 2, 44);

        var y = _panelRect.Bottom - buttonH - 16;
        var gap = 16;
        var totalW = buttonW * 3 + gap * 2;
        var startX = _panelRect.Center.X - totalW / 2;

        _copyBtn.Bounds = new Rectangle(startX, y, buttonW, buttonH);
        _copyIdBtn.Bounds = new Rectangle(startX + buttonW + gap, y, buttonW, buttonH);
        _okBtn.Bounds = new Rectangle(startX + (buttonW + gap) * 2, y, buttonW, buttonH);
    }

    public void Update(GameTime gameTime, InputState input)
    {
        if (input.IsNewKeyPress(Keys.Escape) || input.IsNewKeyPress(Keys.Enter))
        {
            _menus.Pop();
            return;
        }

        _okBtn.Update(input);
        _copyBtn.Update(input);
        _copyIdBtn.Update(input);

        if (_toastTimer > 0)
        {
            _toastTimer -= (float)gameTime.ElapsedGameTime.TotalSeconds;
            if (_toastTimer <= 0)
            {
                _toastTimer = 0;
                _toast = string.Empty;
            }
        }
    }

    public void Draw(SpriteBatch sb, Rectangle viewport)
    {
        if (viewport != _viewport)
            OnResize(viewport);

        sb.Begin(samplerState: SamplerState.PointClamp);
        if (_bg is not null) sb.Draw(_bg, UiLayout.WindowViewport, Color.White);
        else sb.Draw(_pixel, UiLayout.WindowViewport, new Color(0, 0, 0));
        sb.End();

        sb.Begin(samplerState: SamplerState.PointClamp, transformMatrix: UiLayout.Transform);

        sb.Draw(_pixel, _panelRect, new Color(0, 0, 0, 210));
        DrawBorder(sb, _panelRect, Color.White);

        var x = _panelRect.X + 18;
        var y = _panelRect.Y + 16;

        DrawTextBold(sb, "SHARE HOST CODE", new Vector2(x, y), Color.White);
        y += _font.LineHeight + 10;

        _font.DrawString(sb, "Give this code to your friend.", new Vector2(x, y), Color.White);
        y += _font.LineHeight + 2;
        _font.DrawString(sb, "They can join via Multiplayer > Online > Join.", new Vector2(x, y), Color.White);
        y += _font.LineHeight + 14;

        DrawTextBold(sb, "HOST CODE:", new Vector2(x, y), new Color(220, 180, 80));
        y += _font.LineHeight + 4;

        var codeRect = new Rectangle(x, y, _panelRect.Width - 36, _font.LineHeight + 18);
        sb.Draw(_pixel, codeRect, new Color(30, 30, 30, 230));
        DrawBorder(sb, codeRect, Color.White);
        var codePos = new Vector2(codeRect.X + 8, codeRect.Y + (codeRect.Height - _font.LineHeight) / 2f);
        _font.DrawString(sb, string.IsNullOrWhiteSpace(_code) ? "(missing)" : _code, codePos, Color.White);

        y = codeRect.Bottom + 12;

        DrawTextBold(sb, "YOUR ID:", new Vector2(x, y), new Color(220, 180, 80));
        y += _font.LineHeight + 4;

        var idRect = new Rectangle(x, y, _panelRect.Width - 36, _font.LineHeight + 18);
        sb.Draw(_pixel, idRect, new Color(30, 30, 30, 230));
        DrawBorder(sb, idRect, Color.White);
        var idPos = new Vector2(idRect.X + 8, idRect.Y + (idRect.Height - _font.LineHeight) / 2f);
        _font.DrawString(sb, string.IsNullOrWhiteSpace(_hostUserId) ? "(unknown)" : _hostUserId, idPos, Color.White);

        if (!string.IsNullOrEmpty(_toast))
        {
            var toastPos = new Vector2(x, idRect.Bottom + 10);
            _font.DrawString(sb, _toast, toastPos, new Color(120, 220, 140));
        }

        _copyBtn.Draw(sb, _pixel, _font);
        _copyIdBtn.Draw(sb, _pixel, _font);
        _okBtn.Draw(sb, _pixel, _font);

        sb.End();
    }

    private void DrawTextBold(SpriteBatch sb, string text, Vector2 pos, Color color)
    {
        _font.DrawString(sb, text, pos + new Vector2(1, 1), Color.Black);
        _font.DrawString(sb, text, pos, color);
    }

    private void DrawBorder(SpriteBatch sb, Rectangle r, Color color)
    {
        sb.Draw(_pixel, new Rectangle(r.X, r.Y, r.Width, 2), color);
        sb.Draw(_pixel, new Rectangle(r.X, r.Bottom - 2, r.Width, 2), color);
        sb.Draw(_pixel, new Rectangle(r.X, r.Y, 2, r.Height), color);
        sb.Draw(_pixel, new Rectangle(r.Right - 2, r.Y, 2, r.Height), color);
    }

    private void CopyCodeToClipboard()
    {
        try
        {
            if (string.IsNullOrWhiteSpace(_code))
            {
                _toast = "No code to copy.";
                _toastTimer = 2.0f;
                return;
            }

            WinClipboard.SetText(_code);
            _toast = "Copied to clipboard!";
            _toastTimer = 2.0f;
        }
        catch (Exception ex)
        {
            _log.Warn($"Clipboard copy failed: {ex.Message}");
            _toast = "Clipboard copy failed.";
            _toastTimer = 2.0f;
        }
    }

    private void CopyHostIdToClipboard()
    {
        try
        {
            if (string.IsNullOrWhiteSpace(_hostUserId))
            {
                _toast = "No user ID available to copy.";
                _toastTimer = 2.0f;
                return;
            }

            WinClipboard.SetText(_hostUserId);
            _toast = "User ID copied!";
            _toastTimer = 2.0f;
        }
        catch (Exception ex)
        {
            _log.Warn($"Clipboard copy failed: {ex.Message}");
            _toast = "Clipboard copy failed.";
            _toastTimer = 2.0f;
        }
    }
}
