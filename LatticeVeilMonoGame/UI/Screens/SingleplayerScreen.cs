using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using LatticeVeilMonoGame.Core;
using LatticeVeilMonoGame.UI;

namespace LatticeVeilMonoGame.UI.Screens;

public sealed class SingleplayerScreen : IScreen
{
    private const int ScrollStep = 40;

    private readonly MenuStack _menus;
    private readonly AssetLoader _assets;
    private readonly PixelFont _font;
    private readonly Texture2D _pixel;
    private readonly Logger _log;
    private readonly global::Microsoft.Xna.Framework.GraphicsDeviceManager _graphics;
    private readonly PlayerProfile _profile;

    private Texture2D? _bg;
    private Texture2D? _panel;

    private readonly Button _createBtn;
    private readonly Button _deleteBtn;
    private readonly Button _backBtn;

    private Rectangle _viewport;
    private Rectangle _panelRect;
    private Rectangle _listRect;
    private float _scroll;
    private int _rowHeight;
    private int _selectedIndex = -1;
    private double _now;
    private const double DoubleClickSeconds = 0.35;
    private double _lastClickTime;
    private int _lastClickIndex = -1;
    private string? _statusMessage;
    private double _statusUntil;

    private List<string> _worlds = new();

    public SingleplayerScreen(MenuStack menus, AssetLoader assets, PixelFont font, Texture2D pixel, Logger log, PlayerProfile profile, global::Microsoft.Xna.Framework.GraphicsDeviceManager graphics)
    {
        _menus = menus;
        _assets = assets;
        _font = font;
        _pixel = pixel;
        _log = log;
        _graphics = graphics;
        _profile = profile;

        _createBtn = new Button("CREATE WORLD", OpenCreateWorld);
        _deleteBtn = new Button("DELETE WORLD", DeleteSelectedWorld);
        _backBtn = new Button("BACK", () => _menus.Pop());

        try
        {
            _bg = _assets.LoadTexture("textures/menu/backgrounds/singleplayer_bg.png");
            _panel = _assets.LoadTexture("textures/menu/GUIS/Singleplayer_GUI.png");
            _createBtn.Texture = _assets.LoadTexture("textures/menu/buttons/CreateWorld.png");
            _deleteBtn.Texture = _assets.LoadTexture("textures/menu/buttons/DeleteWorld.png");
            _backBtn.Texture = _assets.LoadTexture("textures/menu/buttons/SingleplayerBack.png");
        }
        catch (Exception ex)
        {
            _log.Warn($"Singleplayer asset load: {ex.Message}");
        }

        RefreshWorlds();
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

        var margin = 24;
        var headerH = _font.LineHeight * 2 + 12;
        var buttonAreaH = Math.Clamp((int)(panelH * 0.2f), 60, 90);
        _listRect = new Rectangle(
            _panelRect.X + margin,
            _panelRect.Y + headerH,
            _panelRect.Width - margin * 2,
            _panelRect.Height - headerH - buttonAreaH - margin);

        _rowHeight = _font.LineHeight + 8;

        var buttonW = Math.Clamp((int)(_panelRect.Width * 0.28f), 160, 260);
        var buttonH = Math.Clamp((int)(buttonW * 0.28f), 40, 70);
        var gap = 16;
        var available = _panelRect.Width - margin * 2;
        var deleteSize = buttonH;
        var totalW = buttonW * 2 + deleteSize + gap * 2;
        if (totalW > available)
        {
            var scale = available / (float)totalW;
            buttonW = Math.Max(120, (int)(buttonW * scale));
            buttonH = Math.Max(34, (int)(buttonH * scale));
            gap = Math.Max(8, (int)(gap * scale));
            deleteSize = buttonH;
            totalW = buttonW * 2 + deleteSize + gap * 2;
        }

        var buttonY = _panelRect.Bottom - margin - buttonH;
        var deleteY = buttonY - (deleteSize - buttonH);
        var startX = _panelRect.X + (_panelRect.Width - totalW) / 2;
        _createBtn.Bounds = new Rectangle(startX, buttonY, buttonW, buttonH);
        _deleteBtn.Bounds = new Rectangle(startX + buttonW + gap, deleteY, deleteSize, deleteSize);
        _backBtn.Bounds = new Rectangle(startX + buttonW + gap + deleteSize + gap, buttonY, buttonW, buttonH);

        ClampScroll();
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

        _createBtn.Update(input);
        _deleteBtn.Update(input);
        _backBtn.Update(input);
        HandleListInput(input);

        if (input.IsNewKeyPress(Keys.Enter))
            JoinSelectedWorld();
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

        var title = "WORLDS";
        var titleSize = _font.MeasureString(title);
        var titlePos = new Vector2(_panelRect.Center.X - titleSize.X / 2f, _panelRect.Y + 16);
        _font.DrawString(sb, title, titlePos, Color.White);

        DrawWorldList(sb);

        _createBtn.Draw(sb, _pixel, _font);
        _deleteBtn.Draw(sb, _pixel, _font);
        _backBtn.Draw(sb, _pixel, _font);

        if (!string.IsNullOrWhiteSpace(_statusMessage))
        {
            var size = _font.MeasureString(_statusMessage);
            var pos = new Vector2(_panelRect.Center.X - size.X / 2f, _panelRect.Bottom - _font.LineHeight - 8);
            _font.DrawString(sb, _statusMessage, pos, Color.White);
        }

        sb.End();
    }

    private void DrawWorldList(SpriteBatch sb)
    {
        if (_worlds.Count == 0)
        {
            var msg = "NO WORLDS FOUND";
            var size = _font.MeasureString(msg);
            var pos = new Vector2(_listRect.Center.X - size.X / 2f, _listRect.Center.Y - size.Y / 2f);
            _font.DrawString(sb, msg, pos, Color.White);
            return;
        }

        for (int i = 0; i < _worlds.Count; i++)
        {
            var y = _listRect.Y + i * _rowHeight - (int)Math.Round(_scroll);
            var rowRect = new Rectangle(_listRect.X, y, _listRect.Width, _rowHeight);
            if (rowRect.Bottom < _listRect.Y || rowRect.Y > _listRect.Bottom)
                continue;

            var bg = new Color(20, 20, 20, 180);
            if (i == _selectedIndex)
                bg = new Color(70, 70, 70, 220);
            sb.Draw(_pixel, rowRect, bg);
            DrawBorder(sb, rowRect, Color.White);

            var name = _worlds[i];
            var pos = new Vector2(rowRect.X + 10, rowRect.Y + (_rowHeight - _font.LineHeight) / 2f);
            _font.DrawString(sb, name, pos, Color.White);
        }
    }

    private void HandleListInput(InputState input)
    {
        if (_listRect.Contains(input.MousePosition) && input.ScrollDelta != 0)
        {
            var step = Math.Sign(input.ScrollDelta) * ScrollStep;
            _scroll -= step;
            ClampScroll();
        }

        if (input.IsNewLeftClick() && _listRect.Contains(input.MousePosition))
        {
            var idx = (int)((input.MousePosition.Y - _listRect.Y + _scroll) / _rowHeight);
            if (idx >= 0 && idx < _worlds.Count)
            {
                if (idx == _lastClickIndex && (_now - _lastClickTime) <= DoubleClickSeconds)
                {
                    _selectedIndex = idx;
                    _lastClickIndex = -1;
                    _lastClickTime = 0;
                    JoinSelectedWorld();
                    return;
                }

                _selectedIndex = idx;
                _lastClickIndex = idx;
                _lastClickTime = _now;
            }
        }
    }

    private void RefreshWorlds()
    {
        try
        {
            Directory.CreateDirectory(Paths.WorldsDir);
            _worlds = Directory.GetDirectories(Paths.WorldsDir)
                .Select(path => Path.GetFileName(path) ?? string.Empty)
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (Exception ex)
        {
            _log.Warn($"Failed to read worlds: {ex.Message}");
            _worlds = new List<string>();
        }

        if (_worlds.Count == 0)
        {
            _selectedIndex = -1;
            _scroll = 0;
        }
        else
        {
            _selectedIndex = Math.Clamp(_selectedIndex, 0, _worlds.Count - 1);
        }

        ClampScroll();
    }

    private void ClampScroll()
    {
        var contentHeight = _worlds.Count * _rowHeight;
        var max = Math.Max(0, contentHeight - _listRect.Height);
        if (_scroll < 0)
            _scroll = 0;
        else if (_scroll > max)
            _scroll = max;
    }

    private void OpenCreateWorld()
    {
        _menus.Push(new CreateWorldScreen(_menus, _assets, _font, _pixel, _log, _profile, _graphics, OnWorldCreated), _viewport);
    }

    private void OnWorldCreated(string worldName)
    {
        RefreshWorlds();
        if (!string.IsNullOrWhiteSpace(worldName))
        {
            var idx = _worlds.FindIndex(w => string.Equals(w, worldName, StringComparison.OrdinalIgnoreCase));
            if (idx >= 0)
                _selectedIndex = idx;
        }
    }

    private void DeleteSelectedWorld()
    {
        if (_selectedIndex < 0 || _selectedIndex >= _worlds.Count)
        {
            ShowStatus("SELECT A WORLD TO DELETE");
            return;
        }

        var name = _worlds[_selectedIndex];
        var result = System.Windows.Forms.MessageBox.Show(
            $"Delete world?\n\n{name}",
            "Confirm Delete",
            System.Windows.Forms.MessageBoxButtons.YesNo,
            System.Windows.Forms.MessageBoxIcon.Warning);

        if (result != System.Windows.Forms.DialogResult.Yes)
            return;

        try
        {
            var path = Path.Combine(Paths.WorldsDir, name);
            if (Directory.Exists(path))
                Directory.Delete(path, true);

            _log.Info($"Deleted world: {name}");
            RefreshWorlds();
        }
        catch (Exception ex)
        {
            _log.Warn($"Failed to delete world {name}: {ex.Message}");
            ShowStatus("FAILED TO DELETE WORLD");
        }
    }

    private void ShowStatus(string message)
    {
        _statusMessage = message;
        _statusUntil = _now + 2.5;
    }

    private void JoinSelectedWorld()
    {
        if (_selectedIndex < 0 || _selectedIndex >= _worlds.Count)
        {
            ShowStatus("SELECT A WORLD TO JOIN");
            return;
        }

        var name = _worlds[_selectedIndex];
        var worldPath = Path.Combine(Paths.WorldsDir, name);
        var metaPath = Path.Combine(worldPath, "world.json");
        if (!Directory.Exists(worldPath) || !File.Exists(metaPath))
        {
            _log.Warn($"World data missing for '{name}'.");
            ShowStatus("WORLD DATA MISSING");
            return;
        }

        _menus.Push(new GameWorldScreen(_menus, _assets, _font, _pixel, _log, _profile, _graphics, worldPath, metaPath), _viewport);
    }

    private void DrawBorder(SpriteBatch sb, Rectangle rect, Color color)
    {
        sb.Draw(_pixel, new Rectangle(rect.X, rect.Y, rect.Width, 2), color);
        sb.Draw(_pixel, new Rectangle(rect.X, rect.Bottom - 2, rect.Width, 2), color);
        sb.Draw(_pixel, new Rectangle(rect.X, rect.Y, 2, rect.Height), color);
        sb.Draw(_pixel, new Rectangle(rect.Right - 2, rect.Y, 2, rect.Height), color);
    }
}



