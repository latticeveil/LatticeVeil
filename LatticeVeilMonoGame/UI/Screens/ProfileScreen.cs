using System;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using LatticeVeilMonoGame.Core;
using LatticeVeilMonoGame.Online.Eos;
using LatticeVeilMonoGame.UI;

namespace LatticeVeilMonoGame.UI.Screens;

/// <summary>
/// Minimal profile screen: set in-game display name and show EOS Device-ID status.
/// (No Epic Account login UI.)
/// </summary>
public sealed class ProfileScreen : IScreen
{
    private const int MaxNameLength = 24;

    private readonly MenuStack _menus;
    private readonly AssetLoader _assets;
    private readonly PixelFont _font;
    private readonly Texture2D _pixel;
    private readonly Logger _log;
    private readonly PlayerProfile _profile;
    private readonly global::Microsoft.Xna.Framework.GraphicsDeviceManager _graphics;
    private readonly EosClient? _eos;

    private readonly Button _saveBtn;
    private readonly Button _clearBtn;
    private readonly Button _backBtn;

    private Texture2D? _bg;
    private Texture2D? _panel;

    private Rectangle _viewport;
    private Rectangle _panelRect;
    private Rectangle _nameRect;

    private string _nameValue;
    private bool _editing;

    private string _status = "";
    private double _statusUntil;

    public ProfileScreen(MenuStack menus, AssetLoader assets, PixelFont font, Texture2D pixel, Logger log, PlayerProfile profile,
        global::Microsoft.Xna.Framework.GraphicsDeviceManager graphics, EosClient? eosClient)
    {
        _menus = menus;
        _assets = assets;
        _font = font;
        _pixel = pixel;
        _log = log;
        _profile = profile;
        _graphics = graphics;

        _eos = eosClient;

        _nameValue = _profile.OfflineUsername ?? string.Empty;

        _saveBtn = new Button("SAVE", Save) { BoldText = true };
        _clearBtn = new Button("CLEAR", Clear) { BoldText = true };
        _backBtn = new Button("BACK", () => _menus.Pop()) { BoldText = true };

        try
        {
            _bg = _assets.LoadTexture("textures/menu/backgrounds/Profile_bg.png");
            _panel = _assets.LoadTexture("textures/menu/GUIS/Profile_GUI.png");
            _backBtn.Texture = _assets.LoadTexture("textures/menu/buttons/Back.png");
        }
        catch
        {
            // optional
        }
    }

    public void OnResize(Rectangle viewport)
    {
        _viewport = viewport;

        var panelW = Math.Min(720, (int)(viewport.Width * 0.9f));
        var panelH = Math.Min(380, (int)(viewport.Height * 0.75f));
        var panelX = viewport.X + (viewport.Width - panelW) / 2;
        var panelY = viewport.Y + (viewport.Height - panelH) / 2;
        _panelRect = new Rectangle(panelX, panelY, panelW, panelH);

        var x = _panelRect.X + 18;
        var y = _panelRect.Y + 54;
        _nameRect = new Rectangle(x, y + _font.LineHeight + 6, _panelRect.Width - 36, _font.LineHeight + 18);

        var buttonRowY = _panelRect.Bottom - 58;
        var buttonW = Math.Min(180, (_panelRect.Width - 36 - 16) / 3);
        var buttonH = Math.Max(44, _font.LineHeight * 2);

        _saveBtn.Bounds = new Rectangle(_panelRect.X + 18, buttonRowY, buttonW, buttonH);
        _clearBtn.Bounds = new Rectangle(_saveBtn.Bounds.Right + 8, buttonRowY, buttonW, buttonH);
        _backBtn.Bounds = new Rectangle(_clearBtn.Bounds.Right + 8, buttonRowY, buttonW, buttonH);
    }

    public void Update(GameTime gameTime, InputState input)
    {
        var now = gameTime.TotalGameTime.TotalSeconds;

        if (input.IsNewKeyPress(Keys.Escape))
        {
            _menus.Pop();
            return;
        }

        if (_statusUntil > 0 && now >= _statusUntil)
            _status = "";

        if (input.IsNewLeftClick())
        {
            var p = input.MousePosition;
            _editing = _nameRect.Contains(p);
        }

        if (_editing)
        {
            HandleTextInput(input, ref _nameValue, MaxNameLength);
            if (input.IsNewKeyPress(Keys.Enter))
            {
                Save();
                _editing = false;
            }
        }

        _saveBtn.Update(input);
        _clearBtn.Update(input);
        _backBtn.Update(input);
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

        if (_panel is not null)
            sb.Draw(_panel, _panelRect, Color.White);
        else
            sb.Draw(_pixel, _panelRect, new Color(0, 0, 0, 200));

        DrawBorder(sb, _panelRect, Color.White);

        var x = _panelRect.X + 18;
        var y = _panelRect.Y + 16;
        DrawTextBold(sb, "PROFILE", new Vector2(x, y), Color.White);

        y = _panelRect.Y + 54;
        _font.DrawString(sb, "DISPLAY NAME (IN-GAME)", new Vector2(x, y), Color.White);

        // Name input
        sb.Draw(_pixel, _nameRect, _editing ? new Color(50, 50, 50, 230) : new Color(30, 30, 30, 230));
        DrawBorder(sb, _nameRect, Color.White);
        var name = string.IsNullOrWhiteSpace(_nameValue) ? "(auto)" : _nameValue;
        var npos = new Vector2(_nameRect.X + 8, _nameRect.Y + (_nameRect.Height - _font.LineHeight) / 2f);
        _font.DrawString(sb, name, npos, Color.White);

        // EOS info
        var infoY = _nameRect.Bottom + 18;
        var eosStatus = _eos == null ? "EOS: disabled" : (_eos.IsLoggedIn ? "EOS: ready" : "EOS: connecting...");
        _font.DrawString(sb, eosStatus, new Vector2(x, infoY), new Color(220, 180, 80));
        infoY += _font.LineHeight + 4;
        _font.DrawString(sb, "HOST CODE (YOUR ID):", new Vector2(x, infoY), Color.White);
        infoY += _font.LineHeight + 2;
        var id = _eos?.LocalProductUserId;
        _font.DrawString(sb, string.IsNullOrWhiteSpace(id) ? "(waiting...)" : id, new Vector2(x, infoY), Color.White);

        // Status
        if (!string.IsNullOrWhiteSpace(_status))
        {
            var statusPos = new Vector2(_panelRect.X + 18, _panelRect.Bottom - _font.LineHeight - 10);
            _font.DrawString(sb, _status, statusPos, Color.White);
        }

        _saveBtn.Draw(sb, _pixel, _font);
        _clearBtn.Draw(sb, _pixel, _font);
        _backBtn.Draw(sb, _pixel, _font);

        sb.End();
    }

    private void Save()
    {
        _profile.OfflineUsername = (_nameValue ?? string.Empty).Trim();
        _profile.Save(_log);
        SetStatus("Saved.");
    }

    private void Clear()
    {
        _nameValue = string.Empty;
        _profile.OfflineUsername = "";
        _profile.Save(_log);
        SetStatus("Cleared.");
    }

    private void SetStatus(string msg)
    {
        _status = msg;
        _statusUntil = 0; // left until changed
    }

    private void HandleTextInput(InputState input, ref string value, int maxLen)
    {
        var shift = input.IsKeyDown(Keys.LeftShift) || input.IsKeyDown(Keys.RightShift);
        foreach (var key in input.GetNewKeys())
        {
            if (key == Keys.Back)
            {
                if (value.Length > 0)
                    value = value.Substring(0, value.Length - 1);
                continue;
            }

            if (key == Keys.Space)
            {
                Append(ref value, ' ', maxLen);
                continue;
            }

            if (key == Keys.OemMinus || key == Keys.Subtract)
            {
                Append(ref value, shift ? '_' : '-', maxLen);
                continue;
            }

            if (key == Keys.OemPeriod || key == Keys.Decimal)
            {
                Append(ref value, '.', maxLen);
                continue;
            }

            if (key >= Keys.D0 && key <= Keys.D9)
            {
                Append(ref value, (char)('0' + (key - Keys.D0)), maxLen);
                continue;
            }

            if (key >= Keys.NumPad0 && key <= Keys.NumPad9)
            {
                Append(ref value, (char)('0' + (key - Keys.NumPad0)), maxLen);
                continue;
            }

            if (key >= Keys.A && key <= Keys.Z)
            {
                var c = (char)('A' + (key - Keys.A));
                if (!shift)
                    c = char.ToLowerInvariant(c);
                Append(ref value, c, maxLen);
            }
        }
    }

    private static void Append(ref string value, char c, int maxLen)
    {
        if (value.Length >= maxLen)
            return;
        value += c;
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
}
