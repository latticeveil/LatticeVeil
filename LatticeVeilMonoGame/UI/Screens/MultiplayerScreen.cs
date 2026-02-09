using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using LatticeVeilMonoGame.Core;
using LatticeVeilMonoGame.Online.Eos;
using LatticeVeilMonoGame.Online.Gate;
using LatticeVeilMonoGame.Online.Lan;
using LatticeVeilMonoGame.UI;

namespace LatticeVeilMonoGame.UI.Screens;

public sealed class MultiplayerScreen : IScreen
{
    private enum MultiplayerTab
    {
        Lan,
        Online
    }

    private readonly MenuStack _menus;
    private readonly AssetLoader _assets;
    private readonly PixelFont _font;
    private readonly Texture2D _pixel;
    private readonly Logger _log;

    private readonly PlayerProfile _profile;
    private readonly global::Microsoft.Xna.Framework.GraphicsDeviceManager _graphics;

    private readonly LanDiscovery _lanDiscovery;
    private EosClient? _eosClient;
    private readonly OnlineGateClient _onlineGate;

    private readonly Button _refreshBtn;
    private readonly Button _hostBtn;
    private readonly Button _joinBtn;
	private readonly Button _yourIdBtn;
    private readonly Button _friendsBtn;
    private readonly Button _saveNameBtn;
    private readonly Button _backBtn;

    private List<LanServerEntry> _lanServers = new();
    private int _selectedLan = -1;

    private MultiplayerTab _tab = MultiplayerTab.Lan;
    private string _status = "";
    private bool _joining;

    private Texture2D? _bg;
    private Texture2D? _panel;

    private double _lastLanRefresh;
    private const double LanRefreshIntervalSeconds = 1.0;

    private Rectangle _viewport;
    private Rectangle _panelRect;
    private Rectangle _listRect;
    private Rectangle _listBodyRect;
    private Rectangle _tabLanRect;
    private Rectangle _tabOnlineRect;
    private Rectangle _buttonRowRect;
    private Rectangle _infoRect;
    private Rectangle _playerNameInputRect;

    private EosIdentityStore _identityStore;
    private string _playerNameInput = string.Empty;
    private bool _playerNameActive;

    private double _now;
    private const double DoubleClickSeconds = 0.35;
    private double _lastClickTime;
    private int _lastClickIndex = -1;

    public MultiplayerScreen(MenuStack menus, AssetLoader assets, PixelFont font, Texture2D pixel, Logger log, PlayerProfile profile,
        global::Microsoft.Xna.Framework.GraphicsDeviceManager graphics, EosClient? eosClient)
    {
        _menus = menus;
        _assets = assets;
        _font = font;
        _pixel = pixel;
        _log = log;
        _profile = profile;
        _graphics = graphics;

        _eosClient = eosClient ?? EosClientProvider.GetOrCreate(_log, "deviceid", allowRetry: true);
        if (_eosClient == null)
            _log.Warn("MultiplayerScreen: EOS client not available.");
        _onlineGate = OnlineGateClient.GetOrCreate();
        _identityStore = EosIdentityStore.LoadOrCreate(_log);
        _playerNameInput = _identityStore.GetDisplayNameOrDefault("Player");

        _lanDiscovery = new LanDiscovery(_log);
        _lanDiscovery.StartListening();

        _refreshBtn = new Button("REFRESH", OnRefreshClicked) { BoldText = true };
        _hostBtn = new Button("HOST", OpenHostWorlds) { BoldText = true };
        _joinBtn = new Button("JOIN", OnJoinClicked) { BoldText = true };
		_yourIdBtn = new Button("YOUR ID", OnYourIdClicked) { BoldText = true };
        _friendsBtn = new Button("FRIENDS", OnFriendsClicked) { BoldText = true };
        _saveNameBtn = new Button("SAVE NAME", SavePlayerName) { BoldText = true };
        _backBtn = new Button("BACK", () => { Cleanup(); _menus.Pop(); }) { BoldText = true };

        try
        {
            _bg = _assets.LoadTexture("textures/menu/backgrounds/Multiplayer_bg.png");
            _panel = _assets.LoadTexture("textures/menu/GUIS/Multiplayer_GUI.png");
            _backBtn.Texture = _assets.LoadTexture("textures/menu/buttons/Back.png");
        }
        catch
        {
            // optional
        }

        RefreshLanServers();
    }

    public void OnResize(Rectangle viewport)
    {
        _viewport = viewport;

        var panelW = Math.Min(1300 - 60, viewport.Width - 20); // Shrink by 2 inches (60px)
        var panelH = Math.Min(700 - 60, viewport.Height - 30); // Shrink by 2 inches (60px)
        var panelX = viewport.X + (viewport.Width - panelW) / 2;
        var panelY = viewport.Y + (viewport.Height - panelH) / 2;
        _panelRect = new Rectangle(panelX, panelY, panelW, panelH);

        var pad = 12 + 30; // Move content inward by 1 inch (30px)
        var headerH = _font.LineHeight + 8;
        _infoRect = new Rectangle(panelX + pad, panelY + pad, panelW - pad * 2, headerH);

        var buttonRowH = _font.LineHeight + 10;
        _buttonRowRect = new Rectangle(panelX + pad, panelY + panelH - pad - buttonRowH, panelW - pad * 2, buttonRowH);
        _listRect = new Rectangle(panelX + pad, _infoRect.Bottom + 6, panelW - pad * 2, _buttonRowRect.Top - _infoRect.Bottom - 12);

        var listHeaderH = _font.LineHeight + 8;
        _tabLanRect = new Rectangle(_listRect.X + 4, _listRect.Y + 4, (_listRect.Width - 12) / 2, _font.LineHeight + 2);
        _tabOnlineRect = new Rectangle(_tabLanRect.Right + 4, _tabLanRect.Y, (_listRect.Width - 12) / 2, _tabLanRect.Height);
        _listBodyRect = new Rectangle(_listRect.X, _listRect.Y + listHeaderH, _listRect.Width, Math.Max(0, _listRect.Height - listHeaderH));
        _playerNameInputRect = new Rectangle(
            _listBodyRect.X + 10,
            _listBodyRect.Y + _font.LineHeight + 36,
            Math.Max(180, Math.Min(360, _listBodyRect.Width - 180)),
            _font.LineHeight + 12);
        _saveNameBtn.Bounds = new Rectangle(_playerNameInputRect.Right + 8, _playerNameInputRect.Y, 120, _playerNameInputRect.Height);

		var gap = 20; // Increased gap to prevent button overlap
		if (_tab == MultiplayerTab.Online)
		{
			_refreshBtn.Visible = false;
			_yourIdBtn.Visible = true;
            _friendsBtn.Visible = true;
            _saveNameBtn.Visible = true;

			// Match button bounds to their textures with proper spacing
			_hostBtn.Bounds = new Rectangle(_buttonRowRect.X, _buttonRowRect.Y, _hostBtn.Texture?.Width ?? 0, _hostBtn.Texture?.Height ?? 0);
			_joinBtn.Bounds = new Rectangle(_hostBtn.Bounds.Right + gap, _buttonRowRect.Y, _joinBtn.Texture?.Width ?? 0, _joinBtn.Texture?.Height ?? 0);
			_yourIdBtn.Bounds = new Rectangle(_joinBtn.Bounds.Right + gap, _buttonRowRect.Y, _yourIdBtn.Texture?.Width ?? 0, _yourIdBtn.Texture?.Height ?? 0);
            _friendsBtn.Bounds = new Rectangle(_yourIdBtn.Bounds.Right + gap, _buttonRowRect.Y, _friendsBtn.Texture?.Width ?? 140, _friendsBtn.Texture?.Height ?? _buttonRowRect.Height);
		}
		else
		{
			_refreshBtn.Visible = true;
			_yourIdBtn.Visible = false;
            _friendsBtn.Visible = false;
            _saveNameBtn.Visible = false;
            _playerNameActive = false;

			// Match button bounds to their textures with proper spacing
			_refreshBtn.Bounds = new Rectangle(_buttonRowRect.X, _buttonRowRect.Y, _refreshBtn.Texture?.Width ?? 0, _refreshBtn.Texture?.Height ?? 0);
			_hostBtn.Bounds = new Rectangle(_refreshBtn.Bounds.Right + gap, _buttonRowRect.Y, _hostBtn.Texture?.Width ?? 0, _hostBtn.Texture?.Height ?? 0);
			_joinBtn.Bounds = new Rectangle(_hostBtn.Bounds.Right + gap, _buttonRowRect.Y, _joinBtn.Texture?.Width ?? 0, _joinBtn.Texture?.Height ?? 0);
		}

        // Standard back button with proper aspect ratio positioned in bottom-left corner
        var backBtnMargin = 20;
        var backBtnBaseW = Math.Max(_backBtn.Texture?.Width ?? 0, 320);
        var backBtnBaseH = Math.Max(_backBtn.Texture?.Height ?? 0, (int)(backBtnBaseW * 0.28f));
        var backBtnScale = Math.Min(1f, Math.Min(240f / backBtnBaseW, 240f / backBtnBaseH)); // Match SettingsScreen
        var backBtnW = Math.Max(1, (int)Math.Round(backBtnBaseW * backBtnScale));
        var backBtnH = Math.Max(1, (int)Math.Round(backBtnBaseH * backBtnScale));
        _backBtn.Bounds = new Rectangle(viewport.X + backBtnMargin, viewport.Bottom - backBtnMargin - backBtnH, backBtnW, backBtnH);
    }

    public void Update(GameTime gameTime, InputState input)
    {
        _now = gameTime.TotalGameTime.TotalSeconds;

        if (input.IsNewKeyPress(Keys.Escape))
        {
            Cleanup();
            _menus.Pop();
            return;
        }

        if (input.IsNewLeftClick())
        {
            var p = input.MousePosition;
            if (_tabLanRect.Contains(p))
                SetTab(MultiplayerTab.Lan);
            else if (_tabOnlineRect.Contains(p))
                SetTab(MultiplayerTab.Online);
            else if (_tab == MultiplayerTab.Online)
                _playerNameActive = _playerNameInputRect.Contains(p);
        }

        if (_tab == MultiplayerTab.Online && _playerNameActive)
        {
            HandleTextInput(input, ref _playerNameInput, EosIdentityStore.MaxDisplayNameLength);
            if (input.IsNewKeyPress(Keys.Enter))
                SavePlayerName();
        }

        _refreshBtn.Update(input);
        _hostBtn.Update(input);
        _joinBtn.Update(input);
		_yourIdBtn.Update(input);
        _friendsBtn.Update(input);
        _saveNameBtn.Enabled = _tab == MultiplayerTab.Online;
        _saveNameBtn.Update(input);
        _backBtn.Update(input);

        if (_tab == MultiplayerTab.Lan)
        {
            HandleLanListSelection(input);
            UpdateLanDiscovery(gameTime);
        }

        RefreshIdentityState(EnsureEosClient());
    }

    public void Draw(SpriteBatch sb, Rectangle viewport)
    {
        if (viewport != _viewport)
            OnResize(viewport);

        sb.Begin(samplerState: SamplerState.PointClamp);

        if (_bg is not null) sb.Draw(_bg, UiLayout.WindowViewport, Color.White);

        sb.End();

        sb.Begin(samplerState: SamplerState.PointClamp, transformMatrix: UiLayout.Transform);

        DrawPanel(sb);
        DrawInfo(sb);
        DrawList(sb);
        DrawButtons(sb);
        DrawStatus(sb);
        _backBtn.Draw(sb, _pixel, _font);

        sb.End();
    }

    private void DrawNinePatch(SpriteBatch sb, Texture2D texture, Rectangle destination)
    {
        if (texture == null) return;
        
        // Define the border sizes (adjust these based on your GUI texture)
        const int borderSize = 16;
        
        var source = new Rectangle(0, 0, texture.Width, texture.Height);
        
        // Create the 9 patches
        var patches = new[]
        {
            // Corners
            new Rectangle(source.X, source.Y, borderSize, borderSize), // top-left
            new Rectangle(source.X + borderSize, source.Y, source.Width - borderSize * 2, borderSize), // top-middle
            new Rectangle(source.Right - borderSize, source.Y, borderSize, borderSize), // top-right
            new Rectangle(source.X, source.Y + borderSize, borderSize, source.Height - borderSize * 2), // left-middle
            new Rectangle(source.X + borderSize, source.Y + borderSize, source.Width - borderSize * 2, source.Height - borderSize * 2), // center
            new Rectangle(source.Right - borderSize, source.Y + borderSize, borderSize, source.Height - borderSize * 2), // right-middle
            new Rectangle(source.X, source.Bottom - borderSize, borderSize, borderSize), // bottom-left
            new Rectangle(source.X + borderSize, source.Bottom - borderSize, source.Width - borderSize * 2, borderSize), // bottom-middle
            new Rectangle(source.Right - borderSize, source.Bottom - borderSize, borderSize, borderSize) // bottom-right
        };
        
        var destPatches = new[]
        {
            // Corners
            new Rectangle(destination.X, destination.Y, borderSize, borderSize), // top-left
            new Rectangle(destination.X + borderSize, destination.Y, destination.Width - borderSize * 2, borderSize), // top-middle
            new Rectangle(destination.Right - borderSize, destination.Y, borderSize, borderSize), // top-right
            new Rectangle(destination.X, destination.Y + borderSize, borderSize, destination.Height - borderSize * 2), // left-middle
            new Rectangle(destination.X + borderSize, destination.Y + borderSize, destination.Width - borderSize * 2, destination.Height - borderSize * 2), // center
            new Rectangle(destination.Right - borderSize, destination.Y + borderSize, borderSize, destination.Height - borderSize * 2), // right-middle
            new Rectangle(destination.X, destination.Bottom - borderSize, borderSize, borderSize), // bottom-left
            new Rectangle(destination.X + borderSize, destination.Bottom - borderSize, destination.Width - borderSize * 2, borderSize), // bottom-middle
            new Rectangle(destination.Right - borderSize, destination.Bottom - borderSize, borderSize, borderSize) // bottom-right
        };
        
        // Draw all patches
        for (int i = 0; i < 9; i++)
        {
            if (destPatches[i].Width > 0 && destPatches[i].Height > 0)
                sb.Draw(texture, destPatches[i], patches[i], Color.White);
        }
    }

    private void DrawPanel(SpriteBatch sb)
    {
        if (_panel is not null)
            DrawNinePatch(sb, _panel, _panelRect);
        else
            sb.Draw(_pixel, _panelRect, new Color(0, 0, 0, 180));
    }

    private void DrawInfo(SpriteBatch sb)
    {
        var title = _tab == MultiplayerTab.Lan ? "LAN MULTIPLAYER" : "ONLINE (EOS)";
        var left = new Vector2(_infoRect.X + 4, _infoRect.Y + 2);
        DrawTextBold(sb, title, left, Color.White);

        if (_tab == MultiplayerTab.Online)
        {
            var snapshot = EosRuntimeStatus.Evaluate(EnsureEosClient());
            var label = snapshot.StatusText;
            var size = _font.MeasureString(label);
            var pos = new Vector2(_infoRect.Right - size.X - 4, _infoRect.Y + 2);
            _font.DrawString(sb, label, pos, new Color(220, 180, 80));
        }
    }

    private void DrawList(SpriteBatch sb)
    {
        sb.Draw(_pixel, _listRect, new Color(20, 20, 20, 200));
        DrawBorder(sb, _listRect, Color.White);

        DrawTabs(sb);

        if (_tab == MultiplayerTab.Lan)
            DrawLanList(sb);
        else
            DrawOnlineInfo(sb);
    }

    private void DrawTabs(SpriteBatch sb)
    {
        var lanActive = _tab == MultiplayerTab.Lan;
        var onlineActive = _tab == MultiplayerTab.Online;

        sb.Draw(_pixel, _tabLanRect, lanActive ? new Color(60, 60, 60, 220) : new Color(30, 30, 30, 220));
        sb.Draw(_pixel, _tabOnlineRect, onlineActive ? new Color(60, 60, 60, 220) : new Color(30, 30, 30, 220));
        DrawBorder(sb, _tabLanRect, Color.White);
        DrawBorder(sb, _tabOnlineRect, Color.White);

        var lanText = "LAN";
        var onText = "ONLINE";
        var lanSize = _font.MeasureString(lanText);
        var onSize = _font.MeasureString(onText);

        _font.DrawString(sb, lanText, new Vector2(_tabLanRect.Center.X - lanSize.X / 2f, _tabLanRect.Y), Color.White);
        _font.DrawString(sb, onText, new Vector2(_tabOnlineRect.Center.X - onSize.X / 2f, _tabOnlineRect.Y), Color.White);
    }

    private void DrawLanList(SpriteBatch sb)
    {
        if (_lanServers.Count == 0)
        {
            var msg = "NO LAN SERVERS";
            var size = _font.MeasureString(msg);
            var pos = new Vector2(_listBodyRect.Center.X - size.X / 2f, _listBodyRect.Center.Y - size.Y / 2f);
            DrawTextBold(sb, msg, pos, Color.White);
            return;
        }

        var rowH = _font.LineHeight + 2;
        var rowY = _listBodyRect.Y + 2;
        for (var i = 0; i < _lanServers.Count; i++)
        {
            var rowRect = new Rectangle(_listBodyRect.X, rowY - 1, _listBodyRect.Width, rowH + 2);
            if (i == _selectedLan)
                sb.Draw(_pixel, rowRect, new Color(40, 40, 40, 200));

            var s = _lanServers[i];
            var host = DescribeEndpoint(s);
            var text = $"{s.ServerName} | {host}";
            DrawTextBold(sb, text, new Vector2(_listBodyRect.X + 4, rowY), Color.White);
            rowY += rowH;
            if (rowY > _listBodyRect.Bottom - rowH)
                break;
        }
    }

    private void DrawOnlineInfo(SpriteBatch sb)
    {
        var eos = EnsureEosClient();
        var snapshot = EosRuntimeStatus.Evaluate(eos);
        var x = _listBodyRect.X + 10;
        var y = _listBodyRect.Y + 10;

        var header = "ONLINE PLAY (NO PORT FORWARDING)";
        DrawTextBold(sb, header, new Vector2(x, y), Color.White);
        y += _font.LineHeight + 10;

        _font.DrawString(sb, "PLAYER NAME:", new Vector2(x, y), new Color(220, 220, 220));
        y += _font.LineHeight + 2;
        sb.Draw(_pixel, _playerNameInputRect, _playerNameActive ? new Color(36, 36, 36, 240) : new Color(26, 26, 26, 240));
        DrawBorder(sb, _playerNameInputRect, Color.White);
        var nameText = string.IsNullOrWhiteSpace(_playerNameInput) ? "Player" : _playerNameInput;
        _font.DrawString(sb, nameText, new Vector2(_playerNameInputRect.X + 6, _playerNameInputRect.Y + 4), Color.White);
        y = _playerNameInputRect.Bottom + 6;

        if (snapshot.Reason is EosRuntimeReason.SdkNotCompiled or EosRuntimeReason.ConfigMissing or EosRuntimeReason.DisabledByEnvironment or EosRuntimeReason.ClientUnavailable)
        {
            var msg = snapshot.StatusText;
            _font.DrawString(sb, msg, new Vector2(x, y), Color.White);
            y += _font.LineHeight + 6;
            _font.DrawString(sb, $"Assets: {Paths.ToUiPath(Paths.GetAssetsDir())}", new Vector2(x, y), new Color(180, 180, 180));
            y += _font.LineHeight + 2;
            _font.DrawString(sb, $"EOS Config: {EosRuntimeStatus.DescribeConfigSource()}", new Vector2(x, y), new Color(180, 180, 180));
            return;
        }

        _font.DrawString(sb, "Host: click HOST (Online tab).", new Vector2(x, y), Color.White);
        y += _font.LineHeight + 2;
        _font.DrawString(sb, "Join: click JOIN and enter the host code.", new Vector2(x, y), Color.White);
        y += _font.LineHeight + 10;

        var id = eos?.LocalProductUserId;
        var idLabel = string.IsNullOrWhiteSpace(id) ? "(waiting for EOS login...)" : id;
        var friendCode = _identityStore.GetFriendCode();
        if (string.IsNullOrWhiteSpace(friendCode) && !string.IsNullOrWhiteSpace(id))
            friendCode = EosIdentityStore.GenerateFriendCode(id);
        var normalizedName = EosIdentityStore.NormalizeDisplayName(_playerNameInput);
        if (string.IsNullOrWhiteSpace(normalizedName))
            normalizedName = _identityStore.GetDisplayNameOrDefault("Player");
        var identityLabel = BuildIdentityLabel(normalizedName, friendCode, id);
        _font.DrawString(sb, $"YOU: {identityLabel}", new Vector2(x, y), new Color(220, 220, 220));
        y += _font.LineHeight + 2;
        _font.DrawString(sb, $"YOUR FRIEND CODE: {(string.IsNullOrWhiteSpace(friendCode) ? "(waiting for EOS login...)" : friendCode)}", new Vector2(x, y), new Color(220, 220, 220));
        y += _font.LineHeight + 6;
        DrawTextBold(sb, "YOUR HOST CODE:", new Vector2(x, y), new Color(220, 180, 80));
        y += _font.LineHeight + 2;
        _font.DrawString(sb, idLabel, new Vector2(x, y), Color.White);
        y += _font.LineHeight + 8;
        _font.DrawString(sb, $"Assets: {Paths.ToUiPath(Paths.GetAssetsDir())}", new Vector2(x, y), new Color(180, 180, 180));
        y += _font.LineHeight + 2;
        _font.DrawString(sb, $"EOS Config: {EosRuntimeStatus.DescribeConfigSource()}", new Vector2(x, y), new Color(180, 180, 180));
        y += _font.LineHeight + 2;
        var gateLabel = _onlineGate.HasValidTicket ? "GATE: VERIFIED" : $"GATE: {_onlineGate.StatusText}";
        _font.DrawString(sb, gateLabel, new Vector2(x, y), new Color(180, 180, 180));
    }

    private void DrawButtons(SpriteBatch sb)
    {
        _refreshBtn.Draw(sb, _pixel, _font);
        _hostBtn.Draw(sb, _pixel, _font);
        _joinBtn.Draw(sb, _pixel, _font);
		_yourIdBtn.Draw(sb, _pixel, _font);
        _friendsBtn.Draw(sb, _pixel, _font);
        _saveNameBtn.Draw(sb, _pixel, _font);
    }

    private void DrawStatus(SpriteBatch sb)
    {
        if (string.IsNullOrWhiteSpace(_status))
            return;

        var pos = new Vector2(_panelRect.X + 14, _panelRect.Bottom - _font.LineHeight - 8);
        _font.DrawString(sb, _status, pos, Color.White);
    }

    private void OnRefreshClicked()
    {
        if (_tab == MultiplayerTab.Lan)
            RefreshLanServers();
        else
            _status = "";
    }

    private void OnJoinClicked()
    {
        if (_tab == MultiplayerTab.Lan)
        {
            JoinLanGame();
            return;
        }

        if (_joining)
            return;

        var eos = EnsureEosClient();
        var snapshot = EosRuntimeStatus.Evaluate(eos);
        if (snapshot.Reason is EosRuntimeReason.SdkNotCompiled or EosRuntimeReason.ConfigMissing or EosRuntimeReason.DisabledByEnvironment or EosRuntimeReason.ClientUnavailable)
        {
            _status = snapshot.StatusText;
            return;
        }
        if (!_onlineGate.CanUseOfficialOnline(_log, out var gateDenied))
        {
            _status = gateDenied;
            return;
        }

        _menus.Push(new JoinByCodeScreen(_menus, _assets, _font, _pixel, _log, _profile, _graphics, eos, null), _viewport);
    }

    private void OnYourIdClicked()
    {
        if (_tab != MultiplayerTab.Online)
            return;

        var eos = EnsureEosClient();
        var snapshot = EosRuntimeStatus.Evaluate(eos);
        if (snapshot.Reason is EosRuntimeReason.SdkNotCompiled or EosRuntimeReason.ConfigMissing or EosRuntimeReason.DisabledByEnvironment or EosRuntimeReason.ClientUnavailable)
        {
            _status = snapshot.StatusText;
            return;
        }

        if (eos == null || !eos.IsLoggedIn)
        {
            _status = "Signing into EOS...";
            return;
        }
        if (!_onlineGate.CanUseOfficialOnline(_log, out var gateDenied))
        {
            _status = gateDenied;
            return;
        }

		_menus.Push(new ShareJoinCodeScreen(_menus, _assets, _font, _pixel, _log, eos?.LocalProductUserId ?? string.Empty, eos?.LocalProductUserId), _viewport);
    }

    private void OnFriendsClicked()
    {
        if (_tab != MultiplayerTab.Online)
            return;

        if (!_onlineGate.CanUseOfficialOnline(_log, out var gateDenied))
        {
            _status = gateDenied;
            return;
        }

        var eos = EnsureEosClient();
        _menus.Push(new InviteFriendsScreen(_menus, _assets, _font, _pixel, _log, eos, "Online"), _viewport);
    }

    private void OpenHostWorlds()
    {
        var hostOnline = _tab == MultiplayerTab.Online;
        if (hostOnline && !_onlineGate.CanUseOfficialOnline(_log, out var gateDenied))
        {
            _status = gateDenied;
            return;
        }

        _menus.Push(new MultiplayerHostScreen(_menus, _assets, _font, _pixel, _log, _profile, _graphics, _eosClient, hostOnline), _viewport);
    }

    private void JoinLanGame()
    {
        if (_selectedLan < 0 || _selectedLan >= _lanServers.Count)
        {
            _status = "Select a LAN server.";
            return;
        }

        var entry = _lanServers[_selectedLan];
        if (entry.Endpoint == null)
        {
            _status = "Selected LAN server missing endpoint.";
            return;
        }

        var host = entry.Endpoint.Address.ToString();
        if (_joining)
            return;

        _joining = true;
        _status = $"Connecting to {host}:{entry.ServerPort}...";
        _ = JoinLanAsync(host, entry.ServerPort);
    }

    private void SetTab(MultiplayerTab tab)
    {
        if (_tab == tab)
            return;

        _tab = tab;
        _status = "";
    }

    private void HandleLanListSelection(InputState input)
    {
        if (!input.IsNewLeftClick())
            return;

        var p = input.MousePosition;
        if (!_listBodyRect.Contains(p))
            return;

        var rowH = _font.LineHeight + 2;
        var index = (p.Y - _listBodyRect.Y) / rowH;
        if (index < 0 || index >= _lanServers.Count)
            return;

        _selectedLan = index;

        // Double-click to join.
        var dt = _now - _lastClickTime;
        var isDouble = index == _lastClickIndex && dt >= 0 && dt < DoubleClickSeconds;
        _lastClickIndex = index;
        _lastClickTime = _now;
        if (isDouble)
            JoinLanGame();
    }

    private EosClient? EnsureEosClient()
    {
        if (_eosClient != null)
            return _eosClient;

        _eosClient = EosClientProvider.GetOrCreate(_log, "deviceid", allowRetry: true);
        if (_eosClient == null)
            _log.Warn("MultiplayerScreen: EOS client not available.");
        return _eosClient;
    }

    private void SavePlayerName()
    {
        if (_tab != MultiplayerTab.Online)
            return;

        var normalized = EosIdentityStore.NormalizeDisplayName(_playerNameInput);
        if (string.IsNullOrWhiteSpace(normalized))
            normalized = "Player";

        var eos = EnsureEosClient();
        if (eos != null && !string.IsNullOrWhiteSpace(eos.LocalProductUserId))
            _identityStore.ProductUserId = eos.LocalProductUserId;

        _identityStore.DisplayName = normalized;
        _identityStore.Save(_log);
        _playerNameInput = normalized;
        _status = $"Player name saved: {normalized}";
    }

    private void RefreshIdentityState(EosClient? eos)
    {
        if (eos == null || string.IsNullOrWhiteSpace(eos.LocalProductUserId))
            return;

        if (!string.Equals(_identityStore.ProductUserId, eos.LocalProductUserId, StringComparison.Ordinal))
        {
            _identityStore.ProductUserId = eos.LocalProductUserId;
            if (string.IsNullOrWhiteSpace(_identityStore.DisplayName))
                _identityStore.DisplayName = EosIdentityStore.NormalizeDisplayName(_playerNameInput);
            if (string.IsNullOrWhiteSpace(_identityStore.DisplayName))
                _identityStore.DisplayName = "Player";
            _identityStore.Save(_log);
        }
    }

    private static string BuildIdentityLabel(string displayName, string friendCode, string? puid)
    {
        if (!string.IsNullOrWhiteSpace(friendCode))
            return $"{displayName} ({friendCode})";

        var suffix = "----";
        if (!string.IsNullOrWhiteSpace(puid))
        {
            var trimmed = puid.Trim();
            suffix = trimmed.Length <= 4 ? trimmed : trimmed.Substring(trimmed.Length - 4);
        }

        return $"{displayName}#{suffix}";
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

    private void RefreshLanServers()
    {
        _lanServers = new List<LanServerEntry>(_lanDiscovery.GetServers());
        _selectedLan = _lanServers.Count > 0 ? 0 : -1;
    }

    private void UpdateLanDiscovery(GameTime gameTime)
    {
        var now = gameTime.TotalGameTime.TotalSeconds;
        if (now - _lastLanRefresh < LanRefreshIntervalSeconds)
            return;

        _lastLanRefresh = now;
        RefreshLanServers();
    }

    private async Task JoinLanAsync(string host, int port)
    {
        var result = await LanClientSession.ConnectAsync(_log, host, port, _profile.GetDisplayUsername(), TimeSpan.FromSeconds(4));
        if (!result.Success || result.Session == null)
        {
            _status = $"Connect failed: {result.Error}";
            _log.Warn($"LAN join failed: {result.Error}");
            _joining = false;
            return;
        }

        var info = result.WorldInfo;
        var worldDir = EnsureJoinedWorld(info);
        var metaPath = Path.Combine(worldDir, "world.json");
        var meta = WorldMeta.CreateFlat(info.WorldName, info.GameMode, info.Width, info.Height, info.Depth, info.Seed);
        meta.PlayerCollision = info.PlayerCollision;
        meta.Save(metaPath, _log);

        _status = $"Connected to {host}.";
        _joining = false;

        // GO DIRECTLY TO GAME WORLD - NO GENERATION SCREEN
        _menus.Push(
            new GameWorldScreen(_menus, _assets, _font, _pixel, _log, _profile, _graphics, worldDir, metaPath, result.Session),
            _viewport);
    }

    private void Cleanup()
    {
        _lanDiscovery.StopListening();
    }

    private static string DescribeEndpoint(LanServerEntry entry)
    {
        if (entry.Endpoint == null)
            return "";
        return $"{entry.Endpoint.Address}:{entry.ServerPort}";
    }

    private void DrawTextBold(SpriteBatch sb, string text, Vector2 pos, Color color)
    {
        // Cheap bold effect (shadow)
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

    private static string EnsureJoinedWorld(LanWorldInfo info)
    {
        var root = Path.Combine(Paths.MultiplayerWorldsDir, "Joined");
        Directory.CreateDirectory(root);
        var safeName = SanitizeFolderName(info.WorldName);
        var path = Path.Combine(root, safeName);
        Directory.CreateDirectory(path);
        return path;
    }

    private static string SanitizeFolderName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return "World";

        foreach (var c in Path.GetInvalidFileNameChars())
            name = name.Replace(c, '_');
        return name.Trim();
    }
}
