using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using LatticeVeilMonoGame.Core;
using LatticeVeilMonoGame.Online.Eos;
using LatticeVeilMonoGame.Online.Lan;
using LatticeVeilMonoGame.UI;

namespace LatticeVeilMonoGame.UI.Screens;

public sealed class MultiplayerHostScreen : IScreen
{
    private const int ScrollStep = 40;
    private const int DefaultLanServerPort = 27037;

    private readonly MenuStack _menus;
    private readonly AssetLoader _assets;
    private readonly PixelFont _font;
    private readonly Texture2D _pixel;
    private readonly Logger _log;
    private readonly global::Microsoft.Xna.Framework.GraphicsDeviceManager _graphics;
    private readonly PlayerProfile _profile;
    private EosClient? _eosClient;
    private readonly bool _hostForFriends;

    private readonly LanDiscovery _lanDiscovery;

    private LanHostSession? _lanHostSession;
    private EosP2PHostSession? _eosHostSession;
    private ILanSession? _activeSession;

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

    private List<WorldEntry> _worlds = new();
    private bool _hostingSessionActive;

    public MultiplayerHostScreen(MenuStack menus, AssetLoader assets, PixelFont font, Texture2D pixel, Logger log, PlayerProfile profile,
        global::Microsoft.Xna.Framework.GraphicsDeviceManager graphics, EosClient? eosClient, bool hostForFriends)
    {
        _menus = menus;
        _assets = assets;
        _font = font;
        _pixel = pixel;
        _log = log;
        _graphics = graphics;
        _profile = profile;
        _eosClient = eosClient ?? EosClientProvider.GetOrCreate(_log, "device", allowRetry: true);
        _hostForFriends = hostForFriends;

        _lanDiscovery = new LanDiscovery(_log);

        _createBtn = new Button("CREATE WORLD", OpenCreateWorld) { BoldText = true };
        _deleteBtn = new Button("DELETE", DeleteSelectedWorld) { BoldText = true };
        _hostBtn = new Button(_hostForFriends ? "HOST ONLINE" : "HOST LAN", HostSelectedWorld) { BoldText = true };
        _backBtn = new Button("BACK", BackToMultiplayer) { BoldText = true };

        try
        {
            _bg = _assets.LoadTexture("textures/menu/backgrounds/MultiplayerHost_bg.png");
            _panel = _assets.LoadTexture("textures/menu/GUIS/MultiplayerHost_GUI.png");
            _createBtn.Texture = _assets.LoadTexture("textures/menu/buttons/CreateWorld.png");
            _backBtn.Texture = _assets.LoadTexture("textures/menu/buttons/SingleplayerBack.png");
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

        _rowHeight = _font.LineHeight + 8;

        var buttonW = Math.Clamp((int)(_panelRect.Width * 0.26f), 150, 240);
        var buttonH = Math.Clamp((int)(buttonW * 0.28f), 40, 70);
        var gap = 14;

        // Layout: create | delete | host | back
        var totalW = buttonW * 3 + gap * 3 + buttonH; // delete is square
        var available = _panelRect.Width - margin * 2;
        if (totalW > available)
        {
            var scale = available / (float)totalW;
            buttonW = Math.Max(120, (int)(buttonW * scale));
            buttonH = Math.Max(34, (int)(buttonH * scale));
            gap = Math.Max(8, (int)(gap * scale));
            totalW = buttonW * 3 + gap * 3 + buttonH;
        }

        var buttonY = _panelRect.Bottom - margin - buttonH;
        var startX = _panelRect.X + (_panelRect.Width - totalW) / 2;

        _createBtn.Bounds = new Rectangle(startX, buttonY, buttonW, buttonH);
        _deleteBtn.Bounds = new Rectangle(_createBtn.Bounds.Right + gap, buttonY, buttonH, buttonH);
        _hostBtn.Bounds = new Rectangle(_deleteBtn.Bounds.Right + gap, buttonY, buttonW, buttonH);
        _backBtn.Bounds = new Rectangle(_hostBtn.Bounds.Right + gap, buttonY, buttonW, buttonH);

        ClampScroll();
    }

    public void Update(GameTime gameTime, InputState input)
    {
        _now = gameTime.TotalGameTime.TotalSeconds;
        if (_statusMessage != null && _now > _statusUntil)
            _statusMessage = null;

        // If a hosting session died unexpectedly, stop it.
        if (_hostingSessionActive && _activeSession != null && !_activeSession.IsConnected)
            StopHosting();

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

        var title = _hostForFriends ? "HOST ONLINE (EOS)" : "HOST LAN";
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

        // Re-begin with scissor to clip list area.
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

            if (i == _selectedIndex)
                sb.Draw(_pixel, rowRect, new Color(40, 40, 40, 200));

            var name = _worlds[i].DisplayName;
            _font.DrawString(sb, name, new Vector2(rowRect.X + 8, rowRect.Y + 4), Color.White);
            y += _rowHeight;
        }

        // Restore scissor and end, then begin normally again.
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

        // Double click to host.
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
        try
        {
            _worlds = new List<WorldEntry>();
            LoadWorldEntries(Paths.MultiplayerWorldsDir, isMultiplayer: true);
            LoadWorldEntries(Paths.WorldsDir, isMultiplayer: false);
            _worlds.Sort((a, b) => string.Compare(a.DisplayName, b.DisplayName, StringComparison.OrdinalIgnoreCase));

            _selectedIndex = _worlds.Count > 0 ? 0 : -1;
            _scroll = 0;
        }
        catch (Exception ex)
        {
            _log.Warn($"Failed to list multiplayer worlds: {ex.Message}");
            _worlds = new List<WorldEntry>();
            _selectedIndex = -1;
        }
    }

    private void LoadWorldEntries(string rootDir, bool isMultiplayer)
    {
        Directory.CreateDirectory(rootDir);
        foreach (var dir in Directory.GetDirectories(rootDir))
        {
            var folder = Path.GetFileName(dir);
            if (string.IsNullOrWhiteSpace(folder))
                continue;
            if (string.Equals(folder, "Joined", StringComparison.OrdinalIgnoreCase))
                continue;

            var metaPath = Path.Combine(dir, "world.json");
            WorldMeta? meta = null;
            if (File.Exists(metaPath))
                meta = WorldMeta.Load(metaPath, _log);
            var displayName = !string.IsNullOrWhiteSpace(meta?.Name) ? meta!.Name : folder;
            var prefix = isMultiplayer ? "[MP]" : "[SP]";
            _worlds.Add(new WorldEntry
            {
                DisplayName = $"{prefix} {displayName}",
                WorldPath = dir,
                MetaPath = metaPath,
                IsMultiplayer = isMultiplayer
            });
        }
    }

    private void OpenCreateWorld()
    {
        _menus.Push(new CreateWorldScreen(
            _menus,
            _assets,
            _font,
            _pixel,
            _log,
            _profile,
            _graphics,
            onCreated: _ => RefreshWorlds(),
            worldsDir: Paths.MultiplayerWorldsDir,
            enterWorldAfterCreate: false), _viewport);
    }

    private void DeleteSelectedWorld()
    {
        if (_selectedIndex < 0 || _selectedIndex >= _worlds.Count)
        {
            ShowStatus("SELECT A WORLD");
            return;
        }

        var entry = _worlds[_selectedIndex];
        if (!entry.IsMultiplayer)
        {
            ShowStatus("DELETE ONLY FOR MP WORLDS");
            return;
        }

        var name = entry.DisplayName;
        var result = System.Windows.Forms.MessageBox.Show(
            $"Delete world?\n\n{name}",
            "Confirm Delete",
            System.Windows.Forms.MessageBoxButtons.YesNo,
            System.Windows.Forms.MessageBoxIcon.Warning);

        if (result != System.Windows.Forms.DialogResult.Yes)
            return;

        try
        {
            if (Directory.Exists(entry.WorldPath))
                Directory.Delete(entry.WorldPath, true);
            _log.Info($"Deleted multiplayer world: {entry.DisplayName}");
            RefreshWorlds();
        }
        catch (Exception ex)
        {
            _log.Warn($"Failed to delete multiplayer world {entry.DisplayName}: {ex.Message}");
            ShowStatus("DELETE FAILED");
        }
    }

    private void HostSelectedWorld()
    {
        if (_selectedIndex < 0 || _selectedIndex >= _worlds.Count)
        {
            ShowStatus("SELECT A WORLD");
            return;
        }

        var entry = _worlds[_selectedIndex];
        var worldPath = entry.WorldPath;
        var metaPath = entry.MetaPath;
        if (!Directory.Exists(worldPath) || !File.Exists(metaPath))
        {
            _log.Warn($"World data missing for '{entry.DisplayName}'.");
            ShowStatus("WORLD DATA MISSING");
            return;
        }

        if (_hostForFriends)
        {
            _ = StartEosHostingAsync(worldPath, metaPath);
            return;
        }

        StartLanHosting(worldPath, metaPath);
    }

    private EosClient? EnsureEosClient()
    {
        if (_eosClient != null)
            return _eosClient;

        _eosClient = EosClientProvider.GetOrCreate(_log, "device", allowRetry: true);
        return _eosClient;
    }

    private void StartLanHosting(string worldPath, string metaPath)
    {
        var world = VoxelWorld.Load(worldPath, metaPath, _log);
        if (world == null)
        {
            ShowStatus("WORLD FAILED TO LOAD");
            return;
        }

        var meta = world.Meta;

        StopHosting();

        var hostName = _profile.GetDisplayUsername();
        _lanHostSession = new LanHostSession(_log, hostName, new LanWorldInfo
        {
            WorldName = meta.Name,
            GameMode = meta.GameMode,
            Width = meta.Size.Width,
            Height = meta.Size.Height,
            Depth = meta.Size.Depth,
            Seed = meta.Seed,
            PlayerCollision = meta.PlayerCollision
        }, world);

        _lanHostSession.Start(DefaultLanServerPort);
        _activeSession = _lanHostSession;
        _lanDiscovery.StartBroadcast(meta.Name, GetBuildVersion(), DefaultLanServerPort);
        _hostingSessionActive = true;

        _menus.Push(new GameWorldScreen(_menus, _assets, _font, _pixel, _log, _profile, _graphics, worldPath, metaPath, _activeSession, world), _viewport);
    }

    private Task StartEosHostingAsync(string worldPath, string metaPath)
    {
        var eos = EnsureEosClient();
        if (eos == null || !eos.IsLoggedIn)
        {
            ShowStatus("EOS NOT READY");
            return Task.CompletedTask;
        }

        var world = VoxelWorld.Load(worldPath, metaPath, _log);
        if (world == null)
        {
            ShowStatus("WORLD FAILED TO LOAD");
            return Task.CompletedTask;
        }
        
        var meta = world.Meta;

        try
        {
            StopHosting();

            var hostName = _profile.GetDisplayUsername();
            _eosHostSession = new EosP2PHostSession(_log, eos, hostName, new LanWorldInfo
            {
                WorldName = meta.Name,
                GameMode = meta.GameMode,
                Width = meta.Size.Width,
                Height = meta.Size.Height,
                Depth = meta.Size.Depth,
                Seed = meta.Seed,
                PlayerCollision = meta.PlayerCollision
            }, world);

            _activeSession = _eosHostSession;
            _hostingSessionActive = true;
            _statusMessage = "EOS HOST READY";

            _menus.Push(new GameWorldScreen(_menus, _assets, _font, _pixel, _log, _profile, _graphics, worldPath, metaPath, _activeSession, world), _viewport);
        }
        catch (Exception ex)
        {
            _log.Warn($"EOS host failed: {ex.Message}");
            ShowStatus("EOS HOST FAILED");
        }

        return Task.CompletedTask;
    }

    private void StopHosting()
    {
        if (!_hostingSessionActive && _activeSession == null)
            return;

        _hostingSessionActive = false;
        _lanDiscovery.StopBroadcast();

        _lanHostSession?.Dispose();
        _lanHostSession = null;

        _eosHostSession?.Dispose();
        _eosHostSession = null;

        _activeSession = null;

    }

    private void BackToMultiplayer()
    {
        StopHosting();
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

    private static string GetBuildVersion()
    {
        var version = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString();
        return string.IsNullOrWhiteSpace(version) ? "dev" : version;
    }

    private void DrawBorder(SpriteBatch sb, Rectangle rect, Color color)
    {
        sb.Draw(_pixel, new Rectangle(rect.X, rect.Y, rect.Width, 2), color);
        sb.Draw(_pixel, new Rectangle(rect.X, rect.Bottom - 2, rect.Width, 2), color);
        sb.Draw(_pixel, new Rectangle(rect.X, rect.Y, 2, rect.Height), color);
        sb.Draw(_pixel, new Rectangle(rect.Right - 2, rect.Y, 2, rect.Height), color);
    }

    private sealed class WorldEntry
    {
        public string DisplayName { get; init; } = "";
        public string WorldPath { get; init; } = "";
        public string MetaPath { get; init; } = "";
        public bool IsMultiplayer { get; init; }
    }
}

