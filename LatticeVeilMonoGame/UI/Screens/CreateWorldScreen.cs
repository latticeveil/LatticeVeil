using System;
using System.IO;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using LatticeVeilMonoGame.Core;
using LatticeVeilMonoGame.UI;

namespace LatticeVeilMonoGame.UI.Screens;

public sealed class CreateWorldScreen : IScreen
{
    private const int MaxNameLength = 32;
    private const int DefaultWorldWidth = 64;
    private const int DefaultWorldHeight = 64;
    private const int DefaultWorldDepth = 64;

    private readonly MenuStack _menus;
    private readonly AssetLoader _assets;
    private readonly PixelFont _font;
    private readonly Texture2D _pixel;
    private readonly Logger _log;
    private readonly PlayerProfile _profile;
    private readonly global::Microsoft.Xna.Framework.GraphicsDeviceManager _graphics;
    private readonly Action<string> _onCreated;
    private readonly string _worldsDir;
    private readonly bool _enterWorldAfterCreate;

    private Texture2D? _bg;
    private Texture2D? _panel;
    private Texture2D? _sandboxTex;
    private Texture2D? _sandboxSelTex;
    private Texture2D? _survivalTex;
    private Texture2D? _survivalSelTex;

    private readonly Button _createBtn;
    private readonly Button _backBtn;
    private readonly Button _sandboxBtn;
    private readonly Button _survivalBtn;
    private readonly Button _collisionBtn;

    private Rectangle _viewport;
    private Rectangle _panelRect;
    private Rectangle _nameBox;
    private Rectangle _modeHeaderRect;
    private bool _nameActive = true;
    private bool _sandboxSelected = true;
    private string _worldName = string.Empty;
    private bool _playerCollisionEnabled = true;

    private double _now;
    private string? _statusMessage;
    private double _statusUntil;

    public CreateWorldScreen(MenuStack menus, AssetLoader assets, PixelFont font, Texture2D pixel, Logger log, PlayerProfile profile, global::Microsoft.Xna.Framework.GraphicsDeviceManager graphics, Action<string> onCreated, string? worldsDir = null, bool enterWorldAfterCreate = true)
    {
        _menus = menus;
        _assets = assets;
        _font = font;
        _pixel = pixel;
        _log = log;
        _profile = profile;
        _graphics = graphics;
        _onCreated = onCreated;
        _worldsDir = string.IsNullOrWhiteSpace(worldsDir) ? Paths.WorldsDir : worldsDir;
        _enterWorldAfterCreate = enterWorldAfterCreate;

        _createBtn = new Button("CREATE WORLD", CreateWorld);
        _backBtn = new Button("BACK", () => _menus.Pop());
        _sandboxBtn = new Button("SANDBOX", () => SelectMode(true));
        _survivalBtn = new Button("SURVIVE", () => SelectMode(false));
        _collisionBtn = new Button("PLAYER COLLISION: ON", TogglePlayerCollision) { BoldText = true };

        try
        {
            _bg = _assets.LoadTexture("textures/menu/backgrounds/CreateWorld_bg.png");
            _panel = _assets.LoadTexture("textures/menu/GUIS/CreateWorld_GUI.png");
            _createBtn.Texture = _assets.LoadTexture("textures/menu/buttons/CreateWorld.png");
            _backBtn.Texture = _assets.LoadTexture("textures/menu/buttons/SingleplayerBack.png");
            _sandboxTex = _assets.LoadTexture("textures/menu/buttons/sandbox.png");
            _sandboxSelTex = _assets.LoadTexture("textures/menu/buttons/SandboxSelected.png");
            _survivalTex = _assets.LoadTexture("textures/menu/buttons/Survival.png");
            _survivalSelTex = _assets.LoadTexture("textures/menu/buttons/SurvivalSelected.png");
        }
        catch (Exception ex)
        {
            _log.Warn($"Create world asset load: {ex.Message}");
        }

        ApplyModeTextures();
        UpdateCollisionLabel();
    }

    public void OnResize(Rectangle viewport)
    {
        _viewport = viewport;

        var panelW = Math.Clamp((int)(viewport.Width * 0.7f), 420, viewport.Width - 40);
        var panelH = Math.Clamp((int)(viewport.Height * 0.72f), 300, viewport.Height - 40);
        _panelRect = new Rectangle(
            viewport.X + (viewport.Width - panelW) / 2,
            viewport.Y + (viewport.Height - panelH) / 2,
            panelW,
            panelH);

        var margin = 30;
        var contentX = _panelRect.X + margin;
        var contentW = _panelRect.Width - margin * 2;

        var titleY = _panelRect.Y + 16;
        var nameLabelY = titleY + _font.LineHeight + 12;
        _nameBox = new Rectangle(contentX, nameLabelY + _font.LineHeight + 6, contentW, _font.LineHeight + 14);

        var modeLabelY = _nameBox.Bottom + _font.LineHeight + 16;
        _modeHeaderRect = new Rectangle(contentX, modeLabelY, contentW, _font.LineHeight);

        var modeButtonY = _modeHeaderRect.Bottom + 10;
        var gap = 18;

        var availableButtonsH = Math.Max(0, _panelRect.Bottom - margin - modeButtonY);
        var gapY = 12;
        var desiredRowH = Math.Clamp((int)Math.Round(_panelRect.Height * 0.22f), 70, 110);
        var rowMaxH = availableButtonsH > gapY ? (availableButtonsH - gapY) / 2f : 1f;
        var rowH = Math.Max(1, Math.Min(desiredRowH, rowMaxH));

        var bottomBaseW = Math.Max(_createBtn.Texture?.Width ?? 0, _backBtn.Texture?.Width ?? 0);
        var bottomBaseH = Math.Max(_createBtn.Texture?.Height ?? 0, _backBtn.Texture?.Height ?? 0);
        if (bottomBaseW <= 0 || bottomBaseH <= 0)
        {
            bottomBaseW = Math.Clamp((int)(_panelRect.Width * 0.3f), 160, 260);
            bottomBaseH = Math.Clamp((int)(bottomBaseW * 0.28f), 40, 70);
        }

        var bottomGap = 16;
        var bottomMaxW = (contentW - bottomGap) / 2f;
        var bottomScale = Math.Min(1f, Math.Min(bottomMaxW / bottomBaseW, rowH / bottomBaseH));
        var bottomButtonW = Math.Max(1, (int)Math.Round(bottomBaseW * bottomScale));
        var bottomButtonH = Math.Max(1, (int)Math.Round(bottomBaseH * bottomScale));
        var bottomY = _panelRect.Bottom - margin - bottomButtonH;
        var bottomStartX = _panelRect.X + (_panelRect.Width - (bottomButtonW * 2 + bottomGap)) / 2;
        _createBtn.Bounds = new Rectangle(bottomStartX, bottomY, bottomButtonW, bottomButtonH);
        _backBtn.Bounds = new Rectangle(bottomStartX + bottomButtonW + bottomGap, bottomY, bottomButtonW, bottomButtonH);

        var modeBaseW = Math.Max(_sandboxTex?.Width ?? 0, _survivalTex?.Width ?? 0);
        var modeBaseH = Math.Max(_sandboxTex?.Height ?? 0, _survivalTex?.Height ?? 0);
        if (modeBaseW <= 0 || modeBaseH <= 0)
        {
            modeBaseW = (contentW - gap) / 2;
            modeBaseH = Math.Clamp((int)(modeBaseW * 0.28f), 40, 70);
        }

        var modeMaxW = (contentW - gap) / 2f;
        var modeScale = Math.Min(1f, Math.Min(modeMaxW / modeBaseW, rowH / modeBaseH));
        var buttonW = Math.Max(1, (int)Math.Round(modeBaseW * modeScale));
        var buttonH = Math.Max(1, (int)Math.Round(modeBaseH * modeScale));
        var startX = contentX + (contentW - (buttonW * 2 + gap)) / 2;
        _sandboxBtn.Bounds = new Rectangle(startX, modeButtonY, buttonW, buttonH);
        _survivalBtn.Bounds = new Rectangle(startX + buttonW + gap, modeButtonY, buttonW, buttonH);

        var toggleH = Math.Clamp(_font.LineHeight + 12, 26, 36);
        var toggleY = (int)Math.Max(modeButtonY + rowH + gapY, bottomY - toggleH - 12);
        _collisionBtn.Bounds = new Rectangle(contentX, toggleY, contentW, toggleH);
    }

    public void Update(GameTime gameTime, InputState input)
    {
        _now = gameTime.TotalGameTime.TotalSeconds;
        if (_statusMessage != null && _now > _statusUntil)
            _statusMessage = null;

        if (input.IsNewKeyPress(Keys.Escape))
        {
            _menus.Pop();
            return;
        }

        if (input.IsNewLeftClick())
        {
            if (_nameBox.Contains(input.MousePosition))
                _nameActive = true;
            else
                _nameActive = false;
        }

        HandleNameInput(input);

        _sandboxBtn.Update(input);
        _survivalBtn.Update(input);
        _collisionBtn.Update(input);
        _createBtn.Update(input);
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

        if (_panel is not null)
            sb.Draw(_panel, _panelRect, Color.White);
        else
            sb.Draw(_pixel, _panelRect, new Color(25, 25, 25));

        var title = "CREATE WORLD";
        var titleSize = _font.MeasureString(title);
        _font.DrawString(sb, title, new Vector2(_panelRect.Center.X - titleSize.X / 2f, _panelRect.Y + 16), Color.White);

        _font.DrawString(sb, "WORLD NAME", new Vector2(_nameBox.X, _nameBox.Y - _font.LineHeight - 4), Color.White);
        DrawInputBox(sb);

        _font.DrawString(sb, "GAME MODE", new Vector2(_modeHeaderRect.X, _modeHeaderRect.Y), Color.White);
        _sandboxBtn.Draw(sb, _pixel, _font);
        _survivalBtn.Draw(sb, _pixel, _font);
        DrawModeSelection(sb);
        _collisionBtn.Draw(sb, _pixel, _font);

        _createBtn.Draw(sb, _pixel, _font);
        _backBtn.Draw(sb, _pixel, _font);

        if (!string.IsNullOrWhiteSpace(_statusMessage))
        {
            var size = _font.MeasureString(_statusMessage);
            var pos = new Vector2(_panelRect.Center.X - size.X / 2f, _panelRect.Bottom - _font.LineHeight - 8);
            _font.DrawString(sb, _statusMessage, pos, Color.White);
        }

        sb.End();
    }

    private void DrawInputBox(SpriteBatch sb)
    {
        var bg = _nameActive ? new Color(40, 40, 40) : new Color(25, 25, 25);
        sb.Draw(_pixel, _nameBox, bg);
        DrawBorder(sb, _nameBox, Color.White);

        var text = string.IsNullOrWhiteSpace(_worldName) ? "ENTER NAME..." : _worldName;
        var color = string.IsNullOrWhiteSpace(_worldName) ? new Color(180, 180, 180) : Color.White;
        var pos = new Vector2(_nameBox.X + 8, _nameBox.Y + (_nameBox.Height - _font.LineHeight) / 2f);
        _font.DrawString(sb, text, pos, color);
    }

    private void DrawModeSelection(SpriteBatch sb)
    {
        if (_sandboxSelected)
            DrawBorder(sb, _sandboxBtn.Bounds, new Color(255, 210, 90));
        else
            DrawBorder(sb, _survivalBtn.Bounds, new Color(255, 210, 90));
    }

    private void HandleNameInput(InputState input)
    {
        if (!_nameActive)
            return;

        var shift = input.IsKeyDown(Keys.LeftShift) || input.IsKeyDown(Keys.RightShift);

        foreach (var key in input.GetNewKeys())
        {
            if (key == Keys.Back)
            {
                if (_worldName.Length > 0)
                    _worldName = _worldName.Substring(0, _worldName.Length - 1);
                continue;
            }

            if (key == Keys.Enter)
            {
                CreateWorld();
                return;
            }

            if (key == Keys.Space)
            {
                AppendChar(' ');
                continue;
            }

            if (key == Keys.OemMinus || key == Keys.Subtract)
            {
                AppendChar(shift ? '_' : '-');
                continue;
            }

            if (key == Keys.OemPeriod || key == Keys.Decimal)
            {
                AppendChar('.');
                continue;
            }

            if (key >= Keys.D0 && key <= Keys.D9)
            {
                AppendChar((char)('0' + (key - Keys.D0)));
                continue;
            }

            if (key >= Keys.NumPad0 && key <= Keys.NumPad9)
            {
                AppendChar((char)('0' + (key - Keys.NumPad0)));
                continue;
            }

            if (key >= Keys.A && key <= Keys.Z)
            {
                var c = (char)('A' + (key - Keys.A));
                if (!shift)
                    c = char.ToLowerInvariant(c);
                AppendChar(c);
            }
        }
    }

    private void AppendChar(char c)
    {
        if (_worldName.Length >= MaxNameLength)
            return;
        _worldName += c;
    }

    private void SelectMode(bool creative)
    {
        _sandboxSelected = creative;
        ApplyModeTextures();
    }

    private void ApplyModeTextures()
    {
        _sandboxBtn.Texture = _sandboxSelected ? _sandboxSelTex : _sandboxTex;
        _survivalBtn.Texture = _sandboxSelected ? _survivalTex : _survivalSelTex;
    }

    private void TogglePlayerCollision()
    {
        _playerCollisionEnabled = !_playerCollisionEnabled;
        UpdateCollisionLabel();
    }

    private void UpdateCollisionLabel()
    {
        _collisionBtn.Label = _playerCollisionEnabled ? "PLAYER COLLISION: ON" : "PLAYER COLLISION: OFF";
    }

    private void CreateWorld()
    {
        var name = _worldName.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            ShowStatus("ENTER A WORLD NAME");
            return;
        }

        try
        {
            Directory.CreateDirectory(_worldsDir);
            var worldPath = Path.Combine(_worldsDir, name);
            if (Directory.Exists(worldPath))
            {
                ShowStatus("WORLD ALREADY EXISTS");
                return;
            }

            Directory.CreateDirectory(worldPath);
            var mode = _sandboxSelected ? GameMode.Sandbox : GameMode.Survival;
            var seed = Environment.TickCount;
            var meta = WorldMeta.CreateFlat(name, mode, width: DefaultWorldWidth, height: DefaultWorldHeight, depth: DefaultWorldDepth, seed: seed);
            meta.PlayerCollision = _playerCollisionEnabled;
            var metaPath = Path.Combine(worldPath, "world.json");
            meta.Save(metaPath, _log);

            _log.Info($"Created world data for '{name}' (mode={mode}).");

            var viewport = _viewport;
            _menus.Pop();
            // GO DIRECTLY TO GAME WORLD - NO GENERATION SCREEN
            _menus.Push(
                new GameWorldScreen(_menus, _assets, _font, _pixel, _log, _profile, _graphics, worldPath, metaPath, null),
                viewport);
            _onCreated?.Invoke(name);
        }
        catch (Exception ex)
        {
            _log.Warn($"Create world failed: {ex.Message}");
            ShowStatus("FAILED TO CREATE WORLD");
        }
    }

    private void ShowStatus(string message)
    {
        _statusMessage = message;
        _statusUntil = _now + 2.5;
    }

    private void DrawBorder(SpriteBatch sb, Rectangle rect, Color color)
    {
        sb.Draw(_pixel, new Rectangle(rect.X, rect.Y, rect.Width, 2), color);
        sb.Draw(_pixel, new Rectangle(rect.X, rect.Bottom - 2, rect.Width, 2), color);
        sb.Draw(_pixel, new Rectangle(rect.X, rect.Y, 2, rect.Height), color);
        sb.Draw(_pixel, new Rectangle(rect.Right - 2, rect.Y, 2, rect.Height), color);
    }
}



