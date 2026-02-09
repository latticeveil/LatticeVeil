using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using LatticeVeilMonoGame.Core;
using LatticeVeilMonoGame.Online.Eos;
using LatticeVeilMonoGame.Online.Gate;
using LatticeVeilMonoGame.UI;

namespace LatticeVeilMonoGame.UI.Screens;

public sealed class MultiplayerHostScreen : IScreen
{
    private const int ScrollStep = 40;

    private readonly MenuStack _menus;
    private readonly AssetLoader _assets;
    private readonly PixelFont _font;
    private readonly Texture2D _pixel;
    private readonly Logger _log;
    private readonly global::Microsoft.Xna.Framework.GraphicsDeviceManager _graphics;
    private readonly PlayerProfile _profile;
    private EosClient? _eosClient;
    private readonly bool _hostForFriends;

    private Texture2D? _bg;
    private Texture2D? _panel;

    private readonly Button _createBtn;
    private readonly Button _deleteBtn;
    private readonly Button _hostBtn;
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

    private List<WorldListEntry> _worlds = new();
    private readonly Dictionary<string, PreviewTextureCacheEntry> _previewTextures = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _previewLoadFailures = new(StringComparer.OrdinalIgnoreCase);

    public MultiplayerHostScreen(
        MenuStack menus,
        AssetLoader assets,
        PixelFont font,
        Texture2D pixel,
        Logger log,
        PlayerProfile profile,
        global::Microsoft.Xna.Framework.GraphicsDeviceManager graphics,
        EosClient? eosClient,
        bool hostForFriends)
    {
        _menus = menus;
        _assets = assets;
        _font = font;
        _pixel = pixel;
        _log = log;
        _graphics = graphics;
        _profile = profile;
        _eosClient = eosClient;
        _hostForFriends = hostForFriends;

        _createBtn = new Button("CREATE WORLD", OpenCreateWorld) { BoldText = true };
        _deleteBtn = new Button("DELETE", DeleteSelectedWorld) { BoldText = true };
        _hostBtn = new Button(_hostForFriends ? "HOST ONLINE" : "HOST LAN", HostSelectedWorld) { BoldText = true };
        _backBtn = new Button("BACK", BackToMultiplayer) { BoldText = true };

        try
        {
            _bg = _assets.LoadTexture("textures/menu/backgrounds/MultiplayerHost_bg.png");
            _panel = _assets.LoadTexture("textures/menu/GUIS/MultiplayerHost_GUI.png");
            _createBtn.Texture = _assets.LoadTexture("textures/menu/buttons/CreateWorld.png");
            _backBtn.Texture = _assets.LoadTexture("textures/menu/buttons/Back.png");
        }
        catch (Exception ex)
        {
            _log.Warn($"Multiplayer host asset load: {ex.Message}");
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

        _rowHeight = Math.Max(_font.LineHeight + 12, 72);

        var buttonY = _panelRect.Bottom - margin - 48;
        var gap = 10;
        var btnW = Math.Max(110, (_panelRect.Width - margin * 2 - gap * 3) / 4);
        _createBtn.Bounds = new Rectangle(_panelRect.X + margin, buttonY, btnW, 48);
        _deleteBtn.Bounds = new Rectangle(_createBtn.Bounds.Right + gap, buttonY, btnW, 48);
        _hostBtn.Bounds = new Rectangle(_deleteBtn.Bounds.Right + gap, buttonY, btnW, 48);
        _backBtn.Bounds = new Rectangle(_hostBtn.Bounds.Right + gap, buttonY, btnW, 48);

        ClampScroll();
    }

    public void Update(GameTime gameTime, InputState input)
    {
        _now = gameTime.TotalGameTime.TotalSeconds;
        if (_statusMessage != null && _now > _statusUntil)
            _statusMessage = null;

        if (input.IsNewKeyPress(Keys.Escape))
        {
            BackToMultiplayer();
            return;
        }

        _createBtn.Update(input);
        _deleteBtn.Update(input);
        _hostBtn.Update(input);
        _backBtn.Update(input);

        HandleListInput(input);

        if (input.IsNewKeyPress(Keys.Enter))
            HostSelectedWorld();
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

        var title = _hostForFriends ? "HOST ONLINE" : "HOST LAN";
        var titleSize = _font.MeasureString(title);
        var titlePos = new Vector2(_panelRect.Center.X - titleSize.X / 2f, _panelRect.Y + 16);
        _font.DrawString(sb, title, titlePos, Color.White);

        DrawWorldList(sb);

        _createBtn.Draw(sb, _pixel, _font);
        _deleteBtn.Draw(sb, _pixel, _font);
        _hostBtn.Draw(sb, _pixel, _font);
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
        DrawBorder(sb, _listRect, Color.White);

        if (_worlds.Count == 0)
        {
            var msg = "NO WORLDS FOUND";
            var size = _font.MeasureString(msg);
            var pos = new Vector2(_listRect.Center.X - size.X / 2f, _listRect.Center.Y - size.Y / 2f);
            _font.DrawString(sb, msg, pos, Color.White);
            return;
        }

        var clip = sb.GraphicsDevice.ScissorRectangle;
        sb.End();

        var raster = new RasterizerState { ScissorTestEnable = true };
        sb.Begin(samplerState: SamplerState.PointClamp, transformMatrix: UiLayout.Transform, rasterizerState: raster);
        sb.GraphicsDevice.ScissorRectangle = UiLayout.ToScreenRect(_listRect);

        var y = _listRect.Y + 6 - (int)_scroll;
        for (var i = 0; i < _worlds.Count; i++)
        {
            var rowRect = new Rectangle(_listRect.X + 6, y, _listRect.Width - 12, _rowHeight);
            if (rowRect.Bottom < _listRect.Top)
            {
                y += _rowHeight;
                continue;
            }

            if (rowRect.Top > _listRect.Bottom)
                break;

            var bg = i == _selectedIndex ? new Color(48, 48, 48, 220) : new Color(10, 10, 10, 150);
            sb.Draw(_pixel, rowRect, bg);
            DrawBorder(sb, rowRect, new Color(200, 200, 200));

            var entry = _worlds[i];
            var previewSize = rowRect.Height - 8;
            var previewRect = new Rectangle(rowRect.X + 4, rowRect.Y + 4, previewSize, previewSize);
            DrawWorldPreviewTile(sb, entry, previewRect);

            var textX = previewRect.Right + 10;
            var textWidth = Math.Max(20, rowRect.Right - textX - 8);
            var title = TruncateToWidth(WorldListService.BuildDisplayTitle(entry), textWidth);
            var seedText = TruncateToWidth($"SEED: {entry.Seed}", textWidth);

            _font.DrawString(sb, title, new Vector2(textX, rowRect.Y + 6), Color.White);
            _font.DrawString(sb, seedText, new Vector2(textX, rowRect.Bottom - _font.LineHeight - 6), new Color(210, 210, 210));
            y += _rowHeight;
        }

        sb.End();
        sb.GraphicsDevice.ScissorRectangle = clip;
        sb.Begin(samplerState: SamplerState.PointClamp, transformMatrix: UiLayout.Transform);
    }

    private void HandleListInput(InputState input)
    {
        if (_worlds.Count == 0)
            return;

        var delta = input.ScrollDelta;
        if (delta != 0)
        {
            _scroll -= Math.Sign(delta) * ScrollStep;
            ClampScroll();
        }

        if (!input.IsNewLeftClick())
            return;

        var p = input.MousePosition;
        if (!_listRect.Contains(p))
            return;

        var localY = p.Y - _listRect.Y + (int)_scroll - 6;
        var idx = localY / _rowHeight;
        if (idx < 0 || idx >= _worlds.Count)
            return;

        _selectedIndex = idx;

        if (_lastClickIndex == idx && (_now - _lastClickTime) <= DoubleClickSeconds)
        {
            _lastClickIndex = -1;
            _lastClickTime = 0;
            HostSelectedWorld();
            return;
        }

        _lastClickIndex = idx;
        _lastClickTime = _now;
    }

    private void RefreshWorlds()
    {
        _worlds = WorldListService.LoadSingleplayerWorlds(_log);
        _selectedIndex = _worlds.Count > 0 ? 0 : -1;
        _scroll = 0;
        TrimPreviewCache();
        ClampScroll();
    }

    private void OpenCreateWorld()
    {
        _menus.Push(new CreateWorldScreen(_menus, _assets, _font, _pixel, _log, _profile, _graphics, OnWorldCreated), _viewport);
    }

    private void DeleteSelectedWorld()
    {
        if (_selectedIndex < 0 || _selectedIndex >= _worlds.Count)
        {
            ShowStatus("SELECT A WORLD");
            return;
        }

        var entry = _worlds[_selectedIndex];
        var result = System.Windows.Forms.MessageBox.Show(
            $"Delete world?\n\n{entry.Name}",
            "Confirm Delete",
            System.Windows.Forms.MessageBoxButtons.YesNo,
            System.Windows.Forms.MessageBoxIcon.Warning);

        if (result != System.Windows.Forms.DialogResult.Yes)
            return;

        try
        {
            if (Directory.Exists(entry.WorldPath))
                Directory.Delete(entry.WorldPath, true);
            RefreshWorlds();
        }
        catch (Exception ex)
        {
            _log.Warn($"Failed to delete world {entry.Name}: {ex.Message}");
            ShowStatus("DELETE FAILED");
        }
    }

    private void OnWorldCreated(string worldName)
    {
        RefreshWorlds();
        if (string.IsNullOrWhiteSpace(worldName))
            return;

        var idx = _worlds.FindIndex(w => string.Equals(w.Name, worldName, StringComparison.OrdinalIgnoreCase));
        if (idx >= 0)
            _selectedIndex = idx;
    }

    private void HostSelectedWorld()
    {
        if (_selectedIndex < 0 || _selectedIndex >= _worlds.Count)
        {
            ShowStatus("SELECT A WORLD");
            return;
        }

        var entry = _worlds[_selectedIndex];
        if (!Directory.Exists(entry.WorldPath) || !File.Exists(entry.MetaPath))
        {
            ShowStatus("WORLD DATA MISSING");
            return;
        }

        WorldHostStartResult result;
        if (_hostForFriends)
        {
            var gate = OnlineGateClient.GetOrCreate();
            if (!gate.CanUseOfficialOnline(_log, out var gateDenied))
            {
                ShowStatus(gateDenied);
                return;
            }

            _eosClient = EosClientProvider.GetOrCreate(_log, "deviceid", allowRetry: true);
            if (_eosClient == null)
            {
                ShowStatus("EOS CLIENT UNAVAILABLE");
                return;
            }

            var snapshot = EosRuntimeStatus.Evaluate(_eosClient);
            if (snapshot.Reason != EosRuntimeReason.Ready)
            {
                if (snapshot.Reason == EosRuntimeReason.Connecting)
                    _eosClient.StartLogin();
                ShowStatus(snapshot.StatusText);
                return;
            }

            result = WorldHostBootstrap.TryStartEosHost(_log, _profile, _eosClient, entry.WorldPath, entry.MetaPath);
        }
        else
        {
            result = WorldHostBootstrap.TryStartLanHost(_log, _profile, entry.WorldPath, entry.MetaPath);
        }

        if (!result.Success || result.Session == null || result.World == null)
        {
            ShowStatus(result.Error);
            return;
        }

        _menus.Push(
            new GameWorldScreen(_menus, _assets, _font, _pixel, _log, _profile, _graphics, entry.WorldPath, entry.MetaPath, result.Session, result.World),
            _viewport);
    }

    private void BackToMultiplayer()
    {
        _menus.Pop();
    }

    private void ShowStatus(string message)
    {
        _statusMessage = message;
        _statusUntil = _now + 2.5;
    }

    private void ClampScroll()
    {
        var totalH = _worlds.Count * _rowHeight;
        var max = Math.Max(0, totalH - (_listRect.Height - 12));
        _scroll = Math.Clamp(_scroll, 0, max);
    }

    public void OnClose()
    {
        DisposePreviewTextures();
    }

    private void DrawWorldPreviewTile(SpriteBatch sb, WorldListEntry entry, Rectangle rect)
    {
        var preview = GetPreviewTexture(entry.PreviewPath);
        if (preview != null)
        {
            sb.Draw(preview, rect, Color.White);
        }
        else
        {
            sb.Draw(_pixel, rect, new Color(42, 42, 42, 220));
            var label = "MAP";
            var labelSize = _font.MeasureString(label);
            var labelPos = new Vector2(rect.Center.X - labelSize.X / 2f, rect.Center.Y - labelSize.Y / 2f);
            _font.DrawString(sb, label, labelPos, new Color(200, 200, 200));
        }

        DrawBorder(sb, rect, new Color(220, 220, 220));
    }

    private Texture2D? GetPreviewTexture(string previewPath)
    {
        if (string.IsNullOrWhiteSpace(previewPath))
            return null;
        if (!File.Exists(previewPath))
            return null;

        var stamp = File.GetLastWriteTimeUtc(previewPath);
        if (_previewTextures.TryGetValue(previewPath, out var cached))
        {
            if (cached.LastWriteUtc == stamp && !cached.Texture.IsDisposed)
                return cached.Texture;

            cached.Texture.Dispose();
            _previewTextures.Remove(previewPath);
        }

        try
        {
            using var fs = new FileStream(previewPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var texture = Texture2D.FromStream(_assets.GraphicsDevice, fs);
            _previewTextures[previewPath] = new PreviewTextureCacheEntry(texture, stamp);
            _previewLoadFailures.Remove(previewPath);
            return texture;
        }
        catch (Exception ex)
        {
            if (_previewLoadFailures.Add(previewPath))
                _log.Warn($"Failed to load host-world preview '{previewPath}': {ex.Message}");
            return null;
        }
    }

    private void TrimPreviewCache()
    {
        if (_previewTextures.Count == 0)
            return;

        var keep = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < _worlds.Count; i++)
        {
            var previewPath = _worlds[i].PreviewPath;
            if (!string.IsNullOrWhiteSpace(previewPath))
                keep.Add(previewPath);
        }

        var remove = new List<string>();
        foreach (var key in _previewTextures.Keys)
        {
            if (!keep.Contains(key))
                remove.Add(key);
        }

        for (var i = 0; i < remove.Count; i++)
        {
            if (_previewTextures.TryGetValue(remove[i], out var cached))
                cached.Texture.Dispose();
            _previewTextures.Remove(remove[i]);
            _previewLoadFailures.Remove(remove[i]);
        }
    }

    private void DisposePreviewTextures()
    {
        foreach (var cached in _previewTextures.Values)
            cached.Texture.Dispose();

        _previewTextures.Clear();
        _previewLoadFailures.Clear();
    }

    private string TruncateToWidth(string value, int maxWidth)
    {
        if (string.IsNullOrEmpty(value))
            return string.Empty;
        if (_font.MeasureString(value).X <= maxWidth)
            return value;

        const string ellipsis = "...";
        var maxChars = Math.Max(1, value.Length - 1);
        while (maxChars > 1)
        {
            var candidate = value.Substring(0, maxChars).TrimEnd() + ellipsis;
            if (_font.MeasureString(candidate).X <= maxWidth)
                return candidate;
            maxChars--;
        }

        return ellipsis;
    }

    private void DrawBorder(SpriteBatch sb, Rectangle rect, Color color)
    {
        sb.Draw(_pixel, new Rectangle(rect.X, rect.Y, rect.Width, 2), color);
        sb.Draw(_pixel, new Rectangle(rect.X, rect.Bottom - 2, rect.Width, 2), color);
        sb.Draw(_pixel, new Rectangle(rect.X, rect.Y, 2, rect.Height), color);
        sb.Draw(_pixel, new Rectangle(rect.Right - 2, rect.Y, 2, rect.Height), color);
    }

    private sealed class PreviewTextureCacheEntry
    {
        public PreviewTextureCacheEntry(Texture2D texture, DateTime lastWriteUtc)
        {
            Texture = texture;
            LastWriteUtc = lastWriteUtc;
        }

        public Texture2D Texture { get; }
        public DateTime LastWriteUtc { get; }
    }
}
