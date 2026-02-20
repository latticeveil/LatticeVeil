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
    private readonly OnlineSocialStateService _social;

    private readonly Button _refreshBtn;
    private readonly Button _addBtn;
    private readonly Button _hostBtn;
    private readonly Button _joinBtn;
	private readonly Button _yourIdBtn;
    private readonly Button _backBtn;

    private enum SessionType { Lan, Friend, Server }
    
    private sealed class SessionEntry
    {
        public SessionType Type;
        public string Title = "";
        public string HostName = "";
        public string Status = "";
        public string FriendUserId = "";
        public string JoinTarget = "";
        public string LobbyId = "";
        public string WorldName = "";
        public string GameMode = "";
        public bool Cheats;
        public int PlayerCount;
        public int MaxPlayers;
    }
    
    private sealed class IncomingJoinRequest
    {
        public string RequesterPuid = "";
        public string RequesterName = "";
        public string WorldName = "";
        public DateTime RequestTime = DateTime.UtcNow;
        public bool Handled = false;
    }

    private List<SessionEntry> _sessions = new();
    private int _selectedIndex = -1;
    private int _hoveredIndex = -1;
    private Vector2 _lastMousePos = Vector2.Zero;
    private string _hoveredWorldDetails = string.Empty;

    private Dictionary<string, string> _pendingInvitesByFriendUserId = new(StringComparer.OrdinalIgnoreCase);
    private DateTime _nextInvitePollUtc = DateTime.MinValue;
    private DateTime _nextSessionRefreshUtc = DateTime.MinValue;
    private bool _isRefreshing = false;
    private bool _invitePollInProgress;
    private int _invitePollFailures;
    private int _sessionRefreshFailures;
    private float _refreshIconRotation = 0f;
    private bool _justReturnedFromHosting = false;
    private List<IncomingJoinRequest> _incomingJoinRequests = new();

    private bool _joining;
    private string _status = "";

    private Texture2D? _bg;
    private Texture2D? _panel;

    private double _lastLanRefresh;
    private int _lastLoggedLanDiscoveryCount = -1;
    private const double LanRefreshIntervalSeconds = 2.0;
    private const double DefaultListRefreshSeconds = 4.0;
    private const double RefreshBackoffSeconds = 20.0;

    private Rectangle _viewport;
    private Rectangle _panelRect;
    private Rectangle _listRect;
    private Rectangle _listBodyRect;
    private Rectangle _buttonRowRect;
    private Rectangle _infoRect;
    private Rectangle _playerNameInputRect;

    private EosIdentityStore _identityStore;
    private bool _identitySyncBusy;
    private double _lastIdentitySyncAttempt = -100;
    private const double IdentitySyncIntervalSeconds = 8.0;
    private string _reservedUsername = string.Empty;

    private double _now;
    private const double DoubleClickSeconds = 0.35;
    private const string OnlineLoadingStatusPrefix = "Online loading:";
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

        _eosClient = eosClient;
        var launchMode = (Environment.GetEnvironmentVariable("LV_LAUNCH_MODE") ?? string.Empty).Trim();
        var offlineLaunch = string.Equals(launchMode, "offline", StringComparison.OrdinalIgnoreCase);
        if (!offlineLaunch && _eosClient == null)
            _eosClient = EosClientProvider.GetOrCreate(_log, "epic", allowRetry: true);

        if (_eosClient == null)
        {
            if (offlineLaunch)
                _log.Info("MultiplayerScreen: offline launch mode; EOS online features disabled.");
            else
                _log.Warn("MultiplayerScreen: EOS client not available.");
        }
        _onlineGate = OnlineGateClient.GetOrCreate();
        _social = OnlineSocialStateService.GetOrCreate(_log);
        _identityStore = EosIdentityStore.LoadOrCreate(_log);
        _reservedUsername = (_identityStore.ReservedUsername ?? string.Empty).Trim();

        _lanDiscovery = new LanDiscovery(_log);
        _lanDiscovery.StartListening();

        _refreshBtn = new Button("REFRESH", OnRefreshClicked) { BoldText = true };
        _addBtn = new Button("ADD", OnAddClicked) { BoldText = true };
        _hostBtn = new Button("HOST", OpenHostWorlds) { BoldText = true };
        _joinBtn = new Button("JOIN", OnJoinClicked) { BoldText = true };
		_yourIdBtn = new Button("SHARE ID", OnYourIdClicked) { BoldText = true };
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

        RefreshSessions();
        _nextSessionRefreshUtc = DateTime.UtcNow.AddSeconds(DefaultListRefreshSeconds);
        _nextInvitePollUtc = DateTime.UtcNow;
    }

    public void OnResize(Rectangle viewport)
    {
        _viewport = viewport;

        const int PanelMaxWidth = 1300;
        const int PanelMaxHeight = 700;
        
        var panelW = Math.Min(PanelMaxWidth, viewport.Width - 20); // Match SingleplayerScreen
        var panelH = Math.Min(PanelMaxHeight, viewport.Height - 30); // Match SingleplayerScreen
        var panelX = viewport.X + (viewport.Width - panelW) / 2;
        var panelY = viewport.Y + (viewport.Height - panelH) / 2;
        _panelRect = new Rectangle(panelX, panelY, panelW, panelH);

        // Apply scaling to all UI elements and center them
        const float UIScale = 0.80f; // Make UI 20% smaller (80% of original)
        
        // Calculate centered UI area within the GUI box (stretched slightly right)
        var scaledUIWidth = (int)(panelW * UIScale);
        var scaledUIHeight = (int)(panelH * UIScale);
        var uiOffsetX = panelX + (panelW - scaledUIWidth) / 2 + (panelW - scaledUIWidth) / 8; // Shift 1/8 to the right
        var uiOffsetY = panelY + (panelH - scaledUIHeight) / 2;
        
        var pad = (int)(24 * UIScale); // Scaled padding
        var headerH = (int)((_font.LineHeight + 12) * UIScale);
        _infoRect = new Rectangle(uiOffsetX + pad, uiOffsetY + pad, (int)((scaledUIWidth - pad * 2) * UIScale), headerH);

        var buttonRowH = (int)Math.Max(60, (_font.LineHeight * 2 + 18) * UIScale);
        _buttonRowRect = new Rectangle(uiOffsetX + pad, uiOffsetY + scaledUIHeight - pad/2 - buttonRowH, (int)((scaledUIWidth - pad * 2) * UIScale), buttonRowH);
        _listRect = new Rectangle(uiOffsetX + pad, _infoRect.Bottom + (int)(15 * UIScale), (int)((scaledUIWidth - pad * 2) * UIScale) + (int)(240 * UIScale), _buttonRowRect.Top - _infoRect.Bottom - (int)(4 * UIScale));

        var listHeaderH = (int)((_font.LineHeight + 20) * UIScale);
        _listBodyRect = new Rectangle(_listRect.X + (int)(8 * UIScale), _listRect.Y + listHeaderH, (int)((_listRect.Width - 16) * UIScale), Math.Max(0, _listRect.Height - listHeaderH - (int)(8 * UIScale)));
        _playerNameInputRect = new Rectangle(
            _listBodyRect.X + (int)(10 * UIScale),
            _listBodyRect.Y + (int)(_font.LineHeight * UIScale + 24 * UIScale),
            Math.Max(220, Math.Min(420, (int)(_listBodyRect.Width * UIScale - 210 * UIScale))),
            (int)((_font.LineHeight + 12) * UIScale));
        LayoutActionButtons();

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
            _menus.Pop();
            return;
        }

        HandleListInput(input);
        UpdateOnlineActionEnabledState();
        _refreshBtn.Update(input);
        _addBtn.Update(input);
        _hostBtn.Update(input);
        _joinBtn.Update(input);
        _backBtn.Update(input);

        // Update mouse position for hover effects
        _lastMousePos = new Vector2(input.MousePosition.X, input.MousePosition.Y);

        // Update refresh icon rotation
        if (_isRefreshing)
        {
            _refreshIconRotation += 5f; // Rotate when refreshing
            if (_refreshIconRotation >= 360f)
                _refreshIconRotation -= 360f;
        }

        var nowUtc = DateTime.UtcNow;
        if (!_invitePollInProgress && nowUtc >= _nextInvitePollUtc)
            _ = PollInviteStateAsync();

        // Auto-refresh list while screen is active.
        if (_justReturnedFromHosting || nowUtc >= _nextSessionRefreshUtc)
        {
            _isRefreshing = true;
            _social.Tick();
            RefreshSessions();
            var nextSeconds = _sessionRefreshFailures >= 3 ? RefreshBackoffSeconds : DefaultListRefreshSeconds;
            _nextSessionRefreshUtc = DateTime.UtcNow.AddSeconds(nextSeconds);
            _justReturnedFromHosting = false; // Reset flag
        }

        _social.Tick();
        UpdateLanDiscovery(gameTime);
        UpdateHoveredSession();
        RefreshIdentityStateFromEos(EnsureEosClient());
        if (!_identitySyncBusy && _now - _lastIdentitySyncAttempt >= IdentitySyncIntervalSeconds)
        {
            _lastIdentitySyncAttempt = _now;
            _identitySyncBusy = true;
            _ = SyncIdentityStateAsync();
        }
    }

    private void UpdateOnlineActionEnabledState()
    {
        var eos = EnsureEosClient();
        var snapshot = EosRuntimeStatus.Evaluate(eos);
        var onlineAvailable = snapshot.Reason == EosRuntimeReason.Ready;

        _refreshBtn.Enabled = true;
        _hostBtn.Enabled = true;
        _joinBtn.Enabled = true;
        _yourIdBtn.Enabled = onlineAvailable;

        if (_selectedIndex >= 0 && _selectedIndex < _sessions.Count)
        {
            var selected = _sessions[_selectedIndex];
            if (selected.Type == SessionType.Friend)
            {
                if (_pendingInvitesByFriendUserId.TryGetValue(selected.FriendUserId, out var inviteStatus)
                    && string.Equals(inviteStatus, "pending", StringComparison.OrdinalIgnoreCase))
                {
                    _joinBtn.Enabled = false;
                }
                else if (string.IsNullOrWhiteSpace(selected.JoinTarget))
                {
                    _joinBtn.Enabled = false;
                }
            }
        }
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
        // Draw username centered in the info area
        var username = _profile?.Username ?? "Unknown";
        var usernameSize = _font.MeasureString(username);
        var usernamePos = new Vector2(_infoRect.Center.X - usernameSize.X / 2f, _infoRect.Center.Y - usernameSize.Y / 2f);
        _font.DrawString(sb, username, usernamePos, new Color(220, 180, 80));
    }

    private void DrawList(SpriteBatch sb)
    {
        _hoveredWorldDetails = string.Empty;

        sb.Draw(_pixel, _listRect, new Color(20, 20, 20, 200));
        DrawBorder(sb, _listRect, Color.White);

        if (_sessions.Count == 0)
        {
            var msg = "No games found on LAN or with Friends.";
            var size = _font.MeasureString(msg);
            _font.DrawString(sb, msg, new Vector2(_listRect.Center.X - size.X / 2, _listRect.Center.Y - size.Y / 2), Color.Gray);
            return;
        }

        // Draw headers only when there are sessions
        const float UIScale = 0.80f;
        var headerRect = new Rectangle(_listRect.X, _listRect.Y, _listRect.Width, (int)((_font.LineHeight + 20) * UIScale));
        sb.Draw(_pixel, headerRect, new Color(40, 40, 40) * 0.8f);
        DrawBorder(sb, headerRect, new Color(60, 60, 60));

        var pad = 10;
        var iconColW = (int)(40 * 0.80f);
        var userColW = (int)(170 * 0.80f);
        var worldColW = (int)(220 * 0.80f);
        var cheatsColW = (int)(130 * 0.80f);
        var modeColW = (int)(130 * 0.80f);
        var playersColW = (int)(120 * 0.80f);
        var colsTotal = iconColW + userColW + worldColW + cheatsColW + modeColW + playersColW + pad * 2;
        // If columns exceed width, shrink world column
        if (colsTotal > headerRect.Width)
        {
            var shrink = colsTotal - headerRect.Width;
            worldColW = Math.Max(80, worldColW - shrink);
        }

        var colX = headerRect.X + pad + 8; // Shift headers right by 8px
        _font.DrawString(sb, "", new Vector2(colX, headerRect.Y + 4), Color.White);
        colX += iconColW;
        _font.DrawString(sb, "USERNAME", new Vector2(colX + 8, headerRect.Y + 4), new Color(180, 180, 180)); // Shift USERNAME right
        colX += userColW;
        _font.DrawString(sb, "WORLD", new Vector2(colX + 8, headerRect.Y + 4), new Color(180, 180, 180)); // Shift WORLD right
        colX += worldColW;
        _font.DrawString(sb, "CHEATS", new Vector2(colX + 8, headerRect.Y + 4), new Color(180, 180, 180)); // Shift CHEATS right
        colX += cheatsColW;
        _font.DrawString(sb, "MODE", new Vector2(colX + 8, headerRect.Y + 4), new Color(180, 180, 180)); // Shift MODE right
        colX += modeColW;
        _font.DrawString(sb, "PLAYERS", new Vector2(colX + 25, headerRect.Y + 4), new Color(180, 180, 180)); // Shift PLAYERS further right

        // Draw vertical lines between columns (shifted right)
        var lineX = headerRect.X + pad + iconColW + 8;
        sb.Draw(_pixel, new Rectangle(lineX, headerRect.Y, 2, headerRect.Height), new Color(60, 60, 60));
        lineX += userColW;
        sb.Draw(_pixel, new Rectangle(lineX, headerRect.Y, 2, headerRect.Height), new Color(60, 60, 60));
        lineX += worldColW;
        sb.Draw(_pixel, new Rectangle(lineX, headerRect.Y, 2, headerRect.Height), new Color(60, 60, 60));
        lineX += cheatsColW;
        sb.Draw(_pixel, new Rectangle(lineX, headerRect.Y, 2, headerRect.Height), new Color(60, 60, 60));
        lineX += modeColW;
        sb.Draw(_pixel, new Rectangle(lineX, headerRect.Y, 2, headerRect.Height), new Color(60, 60, 60));

        var rowH = (int)(64 * 0.80f);
        var rowY = _listBodyRect.Y + 2;
        for (var i = 0; i < _sessions.Count; i++)
        {
            var s = _sessions[i];
            var rowRect = new Rectangle(_listBodyRect.X, rowY - 1, _listBodyRect.Width, rowH);
            var rowIsHovered = i == _hoveredIndex;
            
            if (i == _selectedIndex)
            {
                sb.Draw(_pixel, rowRect, new Color(100, 100, 100, 200));
                DrawBorder(sb, rowRect, Color.Yellow); // Bright border for selection
            }
            else if (rowIsHovered)
            {
                sb.Draw(_pixel, rowRect, new Color(40, 40, 40, 150));
            }

            var iconSize = (int)(48 * 0.80f);
            var iconRect = new Rectangle(rowRect.X + 8, rowRect.Y + (rowRect.Height - iconSize) / 2, iconSize, iconSize);
            
            // Draw Type Icon (Color coded for now)
            var iconColor = s.Type switch
            {
                SessionType.Lan => Color.Cyan,
                SessionType.Friend => Color.LimeGreen,
                SessionType.Server => Color.Orange,
                _ => Color.White
            };
            sb.Draw(_pixel, iconRect, iconColor * 0.5f);
            DrawBorder(sb, iconRect, iconColor);

            var textX = _listBodyRect.X + pad + iconColW + 8; // Match header shift
            var textY = rowRect.Y + (rowRect.Height - _font.LineHeight) / 2;

            // Username column (shifted right)
            var user = s.HostName;
            var userFit = FitTextToWidth(user, Math.Max(24, userColW - 16), out _);
            _font.DrawString(sb, userFit, new Vector2(textX + 8, textY), Color.White);
            textX += userColW;

            // World column (shifted right)
            var world = s.WorldName;
            var worldFit = FitTextToWidth(world, Math.Max(24, worldColW - 16), out var worldWasTrimmed);
            _font.DrawString(sb, worldFit, new Vector2(textX + 8, textY), Color.White);
            if (worldWasTrimmed && (i == _selectedIndex || rowIsHovered))
                _hoveredWorldDetails = world;
            textX += worldColW;

            // Cheats pill (shifted right)
            var pillPad = 4;
            var pillHeight = _font.LineHeight + pillPad * 2;
            var cheatsPillColor = s.Cheats ? new Color(80, 220, 120) : new Color(220, 180, 80);
            var cheatsText = s.Cheats ? "ON" : "OFF";
            var cheatsSize = _font.MeasureString(cheatsText);
            var cheatsPillRect = new Rectangle(textX + 8, rowRect.Y + (rowRect.Height - pillHeight) / 2, (int)cheatsSize.X + pillPad * 2, pillHeight); // Shift CHEATS right
            sb.Draw(_pixel, cheatsPillRect, cheatsPillColor * 0.3f);
            DrawBorder(sb, cheatsPillRect, cheatsPillColor);
            _font.DrawString(sb, cheatsText, new Vector2(cheatsPillRect.X + pillPad, cheatsPillRect.Y + pillPad), cheatsPillColor);
            textX += cheatsColW;

            // Mode pill (shifted right)
            var modePillColor = s.GameMode?.ToLowerInvariant() switch
            {
                "artificer" => new Color(220, 120, 220),
                "veilwalker" => new Color(120, 180, 220),
                "veilseer" => new Color(220, 220, 120),
                _ => new Color(160, 160, 160)
            };
            var modeText = FitTextToWidth(s.GameMode ?? "", Math.Max(20, modeColW - 24), out _);
            var modeSize = _font.MeasureString(modeText);
            var modePillRect = new Rectangle(textX + 8, rowRect.Y + (rowRect.Height - pillHeight) / 2, (int)modeSize.X + pillPad * 2, pillHeight); // Shift MODE right
            sb.Draw(_pixel, modePillRect, modePillColor * 0.3f);
            DrawBorder(sb, modePillRect, modePillColor);
            _font.DrawString(sb, modeText, new Vector2(modePillRect.X + pillPad, modePillRect.Y + pillPad), modePillColor);
            textX += modeColW;

            // Players column (moved further right, include host in count)
            var activePlayers = Math.Max(1, s.PlayerCount); // Always count at least 1 for host
            var players = (s.MaxPlayers > 0) ? $"{activePlayers} active" : $"{activePlayers} active";
            _font.DrawString(sb, players, new Vector2(textX + 35, textY), Color.White); // Moved further right to match header

            rowY += rowH + 4;
            if (rowY > _listBodyRect.Bottom - rowH)
                break;
        }
    }

    
    private void DrawButtons(SpriteBatch sb)
    {
        var joinText = "JOIN";
        if (_selectedIndex >= 0 && _selectedIndex < _sessions.Count)
        {
            var s = _sessions[_selectedIndex];
            if (s.Type == SessionType.Friend)
            {
                joinText = "REQUEST JOIN";
                if (_pendingInvitesByFriendUserId.TryGetValue(s.FriendUserId, out var status))
                {
                    if (status == "pending") joinText = "REQUESTED";
                    else if (status == "accepted") joinText = "JOIN";
                    else if (status == "rejected") joinText = "REQUEST JOIN";
                }
            }
        }
        _joinBtn.Label = joinText;

        // Draw refresh icon (rotating circle arrow)
        var refreshIconRect = new Rectangle(_refreshBtn.Bounds.X - 25, _refreshBtn.Bounds.Y + (_refreshBtn.Bounds.Height - 20) / 2, 20, 20);
        DrawRefreshIcon(sb, refreshIconRect, _isRefreshing ? Color.Yellow : Color.White);

        _refreshBtn.Draw(sb, _pixel, _font);
        _addBtn.Draw(sb, _pixel, _font);
        _hostBtn.Draw(sb, _pixel, _font);
        _joinBtn.Draw(sb, _pixel, _font);
    }

    private void DrawStatus(SpriteBatch sb)
    {
        // Draw status text
        if (!string.IsNullOrEmpty(_status))
        {
            var pos = new Vector2(_panelRect.X + 14, _panelRect.Bottom - _font.LineHeight - 8);
            _font.DrawString(sb, _status, pos, Color.White);
        }

        if (!string.IsNullOrWhiteSpace(_hoveredWorldDetails))
        {
            var worldDetails = $"WORLD: {_hoveredWorldDetails}";
            var detailsPos = new Vector2(_panelRect.X + 14, _panelRect.Bottom - (_font.LineHeight * 2) - 12);
            _font.DrawString(sb, worldDetails, detailsPos, new Color(220, 220, 220));
        }
        
        // Draw incoming join requests
        if (_incomingJoinRequests.Count > 0)
        {
            var requestY = _panelRect.Y + _panelRect.Height - 100;
            foreach (var request in _incomingJoinRequests.OrderByDescending(r => r.RequestTime))
            {
                var requestText = $"Join Request: {request.RequesterName} wants to join {request.WorldName}";
                var textSize = _font.MeasureString(requestText);
                var textRect = new Rectangle(_panelRect.X + 14, requestY, _panelRect.Width - 28, (int)textSize.Y + 4);
                
                // Draw background
                sb.Draw(_pixel, textRect, new Color(50, 50, 50, 200));
                DrawBorder(sb, textRect, Color.Orange);
                
                // Draw text
                _font.DrawString(sb, requestText, new Vector2(textRect.X + 4, textRect.Y + 2), Color.White);
                
                // Draw accept/deny indicators
                var denyRect = new Rectangle(textRect.X, textRect.Y, textRect.Width / 2, textRect.Height);
                var acceptRect = new Rectangle(textRect.X + textRect.Width / 2, textRect.Y, textRect.Width / 2, textRect.Height);
                
                // Deny button (left half) - red
                sb.Draw(_pixel, denyRect, new Color(150, 50, 50, 200));
                DrawBorder(sb, denyRect, Color.Red);
                var denyText = "DENY";
                var denySize = _font.MeasureString(denyText);
                var denyPos = new Vector2(denyRect.X + (denyRect.Width - denySize.X) / 2, denyRect.Y + (denyRect.Height - denySize.Y) / 2);
                _font.DrawString(sb, denyText, denyPos, Color.White);
                
                // Accept button (right half) - green
                sb.Draw(_pixel, acceptRect, new Color(50, 150, 50, 200));
                DrawBorder(sb, acceptRect, Color.Green);
                var acceptText = "ACCEPT";
                var acceptSize = _font.MeasureString(acceptText);
                var acceptPos = new Vector2(acceptRect.X + (acceptRect.Width - acceptSize.X) / 2, acceptRect.Y + (acceptRect.Height - acceptSize.Y) / 2);
                _font.DrawString(sb, acceptText, acceptPos, Color.White);
                
                requestY += textRect.Height + 4;
            }
        }
    }

    private void OnRefreshClicked()
    {
        _isRefreshing = true;
        _sessionRefreshFailures = 0;
        _invitePollFailures = 0;
        _social.ForceRefresh();
        _social.Tick();
        RefreshSessions();
        _status = "";
        _nextSessionRefreshUtc = DateTime.UtcNow.AddSeconds(DefaultListRefreshSeconds);
        _nextInvitePollUtc = DateTime.UtcNow;
    }

    private void OnAddClicked()
    {
        _status = "Direct IP join not implemented yet.";
    }

    private void OnJoinClicked()
    {
        if (_selectedIndex < 0 || _selectedIndex >= _sessions.Count)
        {
            _status = "Select a game to join.";
            return;
        }

        if (_joining) return;

        var entry = _sessions[_selectedIndex];
        _joining = true;

        if (entry.Type == SessionType.Lan)
        {
            var parts = entry.JoinTarget.Split(':');
            var host = parts[0];
            var port = parts.Length > 1 ? int.Parse(parts[1]) : 27015;
            _status = $"Connecting to LAN {host}:{port}...";
            _ = JoinLanAsync(host, port);
        }
        else if (entry.Type == SessionType.Friend)
        {
            if (_pendingInvitesByFriendUserId.TryGetValue(entry.FriendUserId, out var status))
            {
                if (status == "accepted")
                {
                    if (string.IsNullOrWhiteSpace(entry.JoinTarget))
                    {
                        _status = "Host join target is unavailable. Refresh and try again.";
                        _joining = false;
                        return;
                    }

                    _status = $"Connecting to {entry.HostName} via EOS...";
                    _joining = true;
                    _ = JoinOnlineSessionAsync(entry);
                    return;
                }
                else if (status == "rejected")
                {
                    _status = "Invite was rejected.";
                    _joining = false;
                }
                else
                {
                    _status = "Invite is still pending.";
                    _joining = false;
                }
            }
            else
            {
                _status = $"Sending join request to {entry.HostName}...";
                _joining = true;
                _ = SendInviteAsync(entry.FriendUserId, entry.JoinTarget, entry.WorldName);
            }
        }
        else
        {
            _status = "Joining dedicated servers not implemented yet.";
            _joining = false;
        }
    }

    private void OnYourIdClicked()
    {
        var eos = EnsureEosClient();
        var snapshot = EosRuntimeStatus.Evaluate(eos);
        if (snapshot.Reason is EosRuntimeReason.SdkNotCompiled or EosRuntimeReason.ConfigMissing or EosRuntimeReason.DisabledByEnvironment or EosRuntimeReason.ClientUnavailable)
        {
            _status = "Online services not available.";
            return;
        }

        var reserved = _identityStore.ReservedUsername ?? string.Empty;
        if (string.IsNullOrWhiteSpace(reserved))
        {
            _status = "You must claim a username in the launcher first.";
            return;
        }

        var targetUsername = _profile.GetDisplayUsername();
        if (string.IsNullOrWhiteSpace(targetUsername))
        {
            _status = "You must set a username in the launcher first.";
            return;
        }

        var normalized = EosIdentityStore.NormalizeDisplayName(targetUsername);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            _status = "Invalid username format.";
            return;
        }

        if (normalized != reserved)
        {
            _status = $"Your username ({targetUsername}) doesn't match your reserved name ({reserved}).";
            return;
        }

        _status = $"Your ID: {normalized}";
    }

    private void OnFriendsClicked()
    {
        if (!_onlineGate.CanUseOfficialOnline(_log, out var gateDenied))
        {
            _status = gateDenied;
            return;
        }

        _menus.Push(
            new ProfileScreen(
                _menus,
                _assets,
                _font,
                _pixel,
                _log,
                _profile,
                _graphics,
                EnsureEosClient()),
            _viewport);
    }

    private void OpenHostWorlds()
    {
        var eos = EnsureEosClient();
        var snapshot = EosRuntimeStatus.Evaluate(eos);
        var hostOnline = snapshot.Reason == EosRuntimeReason.Ready;
        
        if (hostOnline && !_onlineGate.CanUseOfficialOnline(_log, out var gateDenied))
        {
            _status = gateDenied;
            return;
        }

        _justReturnedFromHosting = true; // Set flag to refresh immediately on return
        _menus.Push(new MultiplayerHostScreen(_menus, _assets, _font, _pixel, _log, _profile, _graphics, _eosClient, hostOnline), _viewport);
    }

    private void LayoutActionButtons()
    {
        const float UIScale = 0.80f; // Make UI 20% smaller (80% of original)
        const int gap = (int)(12 * UIScale);
        var rowH = (int)Math.Max(36, (_font.LineHeight + 16) * UIScale);
        var rowY = _buttonRowRect.Y + Math.Max(0, (_buttonRowRect.Height - rowH) / 2);

        _hostBtn.Visible = true;
        _joinBtn.Visible = true;
        _refreshBtn.Visible = true;
        _addBtn.Visible = true;
        _yourIdBtn.Visible = false; // Hide YOUR ID button

        var eos = EnsureEosClient();
        var snapshot = EosRuntimeStatus.Evaluate(eos);
        var onlineAvailable = snapshot.Reason == EosRuntimeReason.Ready;

        // Layout 4 buttons in one row (without YOUR ID)
        var buttonCount = 4;
        var minButtonW = (int)(100 * UIScale);
        var canFitOneRow = _buttonRowRect.Width >= (minButtonW * buttonCount + gap * (buttonCount - 1));
        
        if (canFitOneRow)
        {
            var buttonW = (int)((_buttonRowRect.Width - gap * (buttonCount - 1)) / buttonCount);
            var totalWidth = buttonW * buttonCount + gap * (buttonCount - 1);
            var x = _listRect.X + (_listRect.Width - totalWidth) / 2; // Center relative to list box
            
            _refreshBtn.Bounds = new Rectangle(x, rowY, buttonW, rowH);
            x += buttonW + gap;

            _addBtn.Bounds = new Rectangle(x, rowY, buttonW, rowH);
            x += buttonW + gap;
            
            _hostBtn.Bounds = new Rectangle(x, rowY, buttonW, rowH);
            x += buttonW + gap;
            
            _joinBtn.Bounds = new Rectangle(x, rowY, buttonW, rowH);
        }
        else
        {
            // Two rows if needed
            var twoRowH = (int)Math.Max(28, ((_buttonRowRect.Height - gap) / 2) * UIScale);
            var topY = _buttonRowRect.Y;
            var bottomY = topY + twoRowH + gap;
            
            // Top row: REFRESH, ADD, HOST (centered relative to list box)
            var topButtonCount = 3;
            var topButtonW = (int)((_buttonRowRect.Width - gap * (topButtonCount - 1)) / topButtonCount);
            var topTotalWidth = topButtonW * topButtonCount + gap * (topButtonCount - 1);
            var topX = _listRect.X + (_listRect.Width - topTotalWidth) / 2;
            _refreshBtn.Bounds = new Rectangle(topX, topY, topButtonW, twoRowH);
            _addBtn.Bounds = new Rectangle(topX + topButtonW + gap, topY, topButtonW, twoRowH);
            _hostBtn.Bounds = new Rectangle(topX + (topButtonW + gap) * 2, topY, topButtonW, twoRowH);

            // Bottom row: JOIN (centered relative to list box)
            var bottomButtonW = _buttonRowRect.Width;
            var bottomX = _listRect.X + (_listRect.Width - bottomButtonW) / 2;
            _joinBtn.Bounds = new Rectangle(bottomX, bottomY, bottomButtonW, twoRowH);
        }
    }

    private void HandleListInput(InputState input)
    {
        if (!input.IsNewLeftClick())
            return;

        var p = input.MousePosition;
        if (!_listBodyRect.Contains(p))
            return;

        var rowH = (int)(64 * 0.80f) + 4;
        var idx = (int)((p.Y - _listBodyRect.Y) / rowH);
        if (idx >= 0 && idx < _sessions.Count)
        {
            _selectedIndex = idx;

            // Double-click to join
            var dt = _now - _lastClickTime;
            var isDouble = idx == _lastClickIndex && dt >= 0 && dt < DoubleClickSeconds;
            _lastClickIndex = idx;
            _lastClickTime = _now;

            if (isDouble)
                OnJoinClicked();
        }
        
        // Check for clicks on incoming join requests
        if (_incomingJoinRequests.Count > 0)
        {
            var requestY = _panelRect.Y + _panelRect.Height - 100;
            foreach (var request in _incomingJoinRequests.OrderByDescending(r => r.RequestTime))
            {
                var textSize = _font.MeasureString($"Join Request: {request.RequesterName} wants to join {request.WorldName}");
                var textRect = new Rectangle(_panelRect.X + 14, requestY, _panelRect.Width - 28, (int)textSize.Y + 4);
                
                // Check for accept/deny clicks
                if (textRect.Contains(p) && input.IsNewLeftClick())
                {
                    if (p.X < textRect.X + textRect.Width / 2) // Click on left side = deny
                    {
                        _log.Info($"Denied join request from {request.RequesterName}");
                        _incomingJoinRequests.Remove(request);
                        _ = RespondToJoinRequestAsync(request.RequesterPuid, "rejected");
                    }
                    else // Click on right side = accept
                    {
                        _log.Info($"Accepted join request from {request.RequesterName}");
                        _incomingJoinRequests.Remove(request);
                        _ = RespondToJoinRequestAsync(request.RequesterPuid, "accepted");
                    }
                    return;
                }
                
                requestY += textRect.Height + 4;
            }
        }
    }

    private async Task SendInviteAsync(string targetUserId, string targetJoinTarget, string worldName)
    {
        var eos = EnsureEosClient();
        var senderJoinTarget = (eos?.LocalProductUserId ?? string.Empty).Trim();
        _log.Info($"JOIN_REQUEST send targetUserId={targetUserId} world='{worldName}' senderJoinTargetPresent={!string.IsNullOrWhiteSpace(senderJoinTarget)}");
        var ok = await _onlineGate.SendWorldInviteAsync(targetUserId, worldName, senderJoinTarget);
        if (ok)
        {
            _status = "Join request sent. Waiting for host approval...";
            _pendingInvitesByFriendUserId[targetUserId] = "pending";
            _log.Info($"JOIN_REQUEST sent targetUserId={targetUserId}");
            
            // Trigger immediate refresh and polling
            RefreshSessions();
            _nextInvitePollUtc = DateTime.UtcNow;
            _ = PollInviteStateAsync(); // Immediate poll to check response
            
            // Also trigger more frequent polling for the next few seconds
            _nextSessionRefreshUtc = DateTime.UtcNow.AddSeconds(2);
        }
        else
        {
            _status = "Failed to send join request.";
            _log.Warn($"JOIN_REQUEST failed targetUserId={targetUserId} targetJoinTarget={targetJoinTarget}");
        }
        _joining = false;
    }

    private async Task JoinP2PAsync(string hostPuid, string hostName)
    {
        var eos = EnsureEosClient();
        if (eos == null) { _status = "EOS not ready."; _joining = false; return; }

        var result = await EosP2PClientSession.ConnectAsync(_log, eos, hostPuid, _profile.GetDisplayUsername(), TimeSpan.FromSeconds(10));
        if (!result.Success || result.Session == null)
        {
            _status = $"P2P Failed: {result.Error}";
            _joining = false;
            return;
        }

        var info = result.WorldInfo;
        var worldDir = JoinedWorldCache.PrepareJoinedWorldPath(info, _log);
        var metaPath = Path.Combine(worldDir, "world.json");
        var meta = WorldMeta.CreateFlat(info.WorldName, info.GameMode, info.Width, info.Height, info.Depth, info.Seed);
        meta.PlayerCollision = info.PlayerCollision;
        meta.WorldId = JoinedWorldCache.ResolveWorldId(info);
        meta.Save(metaPath, _log);

        _status = $"Connected to {hostName}!";
        _joining = false;

        _menus.Push(new GameWorldScreen(_menus, _assets, _font, _pixel, _log, _profile, _graphics, worldDir, metaPath, result.Session), _viewport);
    }

    private async Task JoinOnlineSessionAsync(SessionEntry entry)
    {
        var eos = EnsureEosClient();
        if (eos == null)
        {
            _status = "EOS not ready.";
            _joining = false;
            return;
        }

        if (!string.IsNullOrWhiteSpace(entry.LobbyId))
        {
            _status = $"Joining lobby for {entry.HostName}...";
            var joinedLobby = await eos.JoinLobbyAsync(entry.LobbyId);
            _log.Info($"EOS_LOBBY_JOIN_ATTEMPT lobbyId={entry.LobbyId} ok={joinedLobby}");
            if (!joinedLobby)
            {
                _status = "Could not join lobby.";
                _joining = false;
                return;
            }
        }

        await JoinP2PAsync(entry.JoinTarget, entry.HostName);
    }

    private Task SyncIdentityStateAsync()
    {
        return Task.CompletedTask;
    }

    private void RefreshIdentityStateFromEos(EosClient? eos)
    {
        // This would refresh identity state from EOS services
        // Implementation would go here
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

    private void RefreshIdentityState(EosClient? eos)
    {
        if (eos == null || string.IsNullOrWhiteSpace(eos.LocalProductUserId))
            return;

        var normalizedName = EosIdentityStore.NormalizeDisplayName(_profile.GetDisplayUsername());
        if (string.IsNullOrWhiteSpace(normalizedName))
            normalizedName = "Player";

        if (!string.Equals(_identityStore.ProductUserId, eos.LocalProductUserId, StringComparison.Ordinal))
        {
            _identityStore.ProductUserId = eos.LocalProductUserId;
            _identityStore.DisplayName = normalizedName;
            _identityStore.Save(_log);
            return;
        }

        if (!string.Equals(_identityStore.DisplayName, normalizedName, StringComparison.Ordinal))
        {
            _identityStore.DisplayName = normalizedName;
            _identityStore.Save(_log);
        }
    }

    private async Task SyncIdentityWithGateAsync()
    {
        if (_identitySyncBusy)
            return;

        _lastIdentitySyncAttempt = _now;
        var eos = EnsureEosClient();
        var snapshot = EosRuntimeStatus.Evaluate(eos);
        if (snapshot.Reason != EosRuntimeReason.Ready || eos == null)
            return;

        var productUserId = (eos.LocalProductUserId ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(productUserId))
            return;

        if (!_onlineGate.CanUseOfficialOnline(_log, out _))
            return;

        _identitySyncBusy = true;
        try
        {
            var me = await _onlineGate.GetMyIdentityAsync();
            if (me.Ok && me.Found && me.User != null)
            {
                _reservedUsername = me.User.Username;
                if (!string.Equals(_identityStore.ReservedUsername, me.User.Username, StringComparison.Ordinal))
                {
                    _identityStore.ReservedUsername = me.User.Username;
                    _identityStore.Save(_log);
                }

                return;
            }
        }
        catch (Exception ex)
        {
            _log.Warn($"Identity sync failed: {ex.Message}");
        }
        finally
        {
            _identitySyncBusy = false;
        }
    }

    private async Task PollInviteStateAsync()
    {
        if (_invitePollInProgress)
            return;

        _invitePollInProgress = true;
        try
        {
            var result = await _onlineGate.GetMyWorldInvitesAsync().ConfigureAwait(false);
            if (!result.Ok)
            {
                _invitePollFailures++;
                _nextInvitePollUtc = DateTime.UtcNow.AddSeconds(_invitePollFailures >= 3 ? RefreshBackoffSeconds : DefaultListRefreshSeconds);
                _sessionRefreshFailures = Math.Max(_sessionRefreshFailures, _invitePollFailures);
                _log.Warn("Failed to poll invites.");
                return;
            }

            _invitePollFailures = 0;
            _nextInvitePollUtc = DateTime.UtcNow.AddSeconds(DefaultListRefreshSeconds);
            _sessionRefreshFailures = 0;

            _log.Info($"Polled {result.Outgoing.Count} outgoing invites");
            var serverInvites = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var invite in result.Outgoing)
            {
                if (string.IsNullOrWhiteSpace(invite.TargetProductUserId))
                    continue;

                serverInvites[invite.TargetProductUserId] = invite.Status;
                _log.Info($"Invite to {invite.TargetProductUserId}: {invite.Status}");
            }

            // Sync server state to local, but keep local "pending" if it's missing from server (just sent).
            foreach (var kvp in serverInvites)
                _pendingInvitesByFriendUserId[kvp.Key] = kvp.Value;

            var toRemove = new List<string>();
            foreach (var localPuid in _pendingInvitesByFriendUserId.Keys)
            {
                if (!serverInvites.ContainsKey(localPuid) && _pendingInvitesByFriendUserId[localPuid] != "pending")
                    toRemove.Add(localPuid);
            }
            foreach (var puid in toRemove)
                _pendingInvitesByFriendUserId.Remove(puid);

            // Check incoming join requests using the same response (single network call).
            foreach (var incoming in result.Incoming)
            {
                if (string.IsNullOrWhiteSpace(incoming.SenderProductUserId))
                    continue;
                if (!_incomingJoinRequests.Any(r => r.RequesterPuid == incoming.SenderProductUserId))
                {
                    _incomingJoinRequests.Add(new IncomingJoinRequest
                    {
                        RequesterPuid = incoming.SenderProductUserId,
                        RequesterName = incoming.SenderDisplayName,
                        WorldName = incoming.WorldName,
                        RequestTime = incoming.CreatedUtc
                    });
                    
                    _log.Info($"Received join request from {incoming.SenderDisplayName} to join {incoming.WorldName}");
                }
            }

            RefreshSessions();
        }
        catch (Exception ex)
        {
            _invitePollFailures++;
            _nextInvitePollUtc = DateTime.UtcNow.AddSeconds(_invitePollFailures >= 3 ? RefreshBackoffSeconds : DefaultListRefreshSeconds);
            _sessionRefreshFailures = Math.Max(_sessionRefreshFailures, _invitePollFailures);
            _log.Warn($"Invite polling failed: {ex.Message}");
        }
        finally
        {
            _invitePollInProgress = false;
        }
    }

    private async Task RespondToJoinRequestAsync(string requesterPuid, string response)
    {
        _log.Info($"Responding to join request from {requesterPuid} with: {response}");
        var ok = await _onlineGate.RespondToWorldInviteAsync(requesterPuid, response);
        if (ok)
        {
            _log.Info($"Successfully responded to join request from {requesterPuid}");
        }
        else
        {
            _log.Warn($"Failed to respond to join request from {requesterPuid}");
        }
    }

    private void RefreshSessions()
    {
        var previousSelectionKey = _selectedIndex >= 0 && _selectedIndex < _sessions.Count
            ? BuildSessionKey(_sessions[_selectedIndex])
            : string.Empty;

        var newSessions = new List<SessionEntry>();

        // 1. LAN
        var lan = _lanDiscovery.GetServers();
        if (lan.Count != _lastLoggedLanDiscoveryCount)
        {
            _lastLoggedLanDiscoveryCount = lan.Count;
            _log.Info($"LAN_DISCOVERY_FOUND n={lan.Count}");
        }
        foreach (var s in lan)
        {
            newSessions.Add(new SessionEntry
            {
                Type = SessionType.Lan,
                Title = s.ServerName,
                HostName = "Local Network",
                Status = "Online",
                FriendUserId = DescribeEndpoint(s),
                JoinTarget = DescribeEndpoint(s),
                WorldName = "LAN World",
                GameMode = s.GameMode,
                Cheats = false, // Default for LAN
                PlayerCount = 0, // Unknown for LAN
                MaxPlayers = 0 // Unknown for LAN
            });
        }

        // 2. Friends
        var social = _social.GetSnapshot();
        foreach (var friend in social.Friends)
        {
            var friendId = (friend.ProductUserId ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(friendId))
                continue;

            var displayName = !string.IsNullOrWhiteSpace(friend.DisplayName)
                ? friend.DisplayName
                : (!string.IsNullOrWhiteSpace(friend.Username) ? friend.Username : PlayerProfile.ShortId(friendId));

            if (!social.PresenceByUserId.TryGetValue(friendId, out var presenceEntry) || presenceEntry == null)
                continue;

            if (!IsPresenceEligibleAsActiveHost(presenceEntry))
                continue;

            var worldName = string.IsNullOrWhiteSpace(presenceEntry.WorldName)
                ? $"{displayName}'s World"
                : presenceEntry.WorldName.Trim();
            var mode = (presenceEntry.GameMode ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(mode))
                mode = "Unknown";
            var status = string.IsNullOrWhiteSpace(presenceEntry.Status)
                ? "Hosting"
                : presenceEntry.Status.Trim();

            newSessions.Add(new SessionEntry
            {
                Type = SessionType.Friend,
                Title = worldName,
                HostName = displayName,
                Status = status,
                FriendUserId = friendId,
                JoinTarget = (presenceEntry.JoinTarget ?? string.Empty).Trim(),
                LobbyId = (presenceEntry.LobbyId ?? string.Empty).Trim(),
                WorldName = worldName,
                GameMode = mode,
                Cheats = presenceEntry.Cheats,
                PlayerCount = Math.Max(1, presenceEntry.PlayerCount),
                MaxPlayers = presenceEntry.MaxPlayers
            });
        }

        // 3. Servers (Placeholder)
        // (Empty for now)

        _sessions = newSessions.OrderByDescending(s => s.Type == SessionType.Friend && _pendingInvitesByFriendUserId.TryGetValue(s.FriendUserId, out var status) && status == "accepted")
                               .ThenBy(s => s.Type)
                               .ToList();

        // Clean up pending invites for hosts that are no longer in the list
        var activeHostPuids = new HashSet<string>(newSessions.Where(s => s.Type == SessionType.Friend).Select(s => s.FriendUserId), StringComparer.OrdinalIgnoreCase);
        var toRemove = _pendingInvitesByFriendUserId.Keys.Where(puid => !activeHostPuids.Contains(puid)).ToList();
        foreach (var puid in toRemove)
        {
            _pendingInvitesByFriendUserId.Remove(puid);
        }

        if (!string.IsNullOrWhiteSpace(previousSelectionKey))
        {
            var restoredIndex = _sessions.FindIndex(s => string.Equals(BuildSessionKey(s), previousSelectionKey, StringComparison.Ordinal));
            if (restoredIndex >= 0)
                _selectedIndex = restoredIndex;
        }

        if (_selectedIndex >= _sessions.Count)
            _selectedIndex = _sessions.Count - 1;
        if (_selectedIndex < 0 && _sessions.Count > 0)
            _selectedIndex = 0;

        _isRefreshing = false; // Stop refreshing after update completes
    }

    private void UpdateLanDiscovery(GameTime gameTime)
    {
        var now = gameTime.TotalGameTime.TotalSeconds;
        if (now - _lastLanRefresh < LanRefreshIntervalSeconds)
            return;

        _lastLanRefresh = now;
        RefreshSessions();
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
        var worldDir = JoinedWorldCache.PrepareJoinedWorldPath(info, _log);
        var metaPath = Path.Combine(worldDir, "world.json");
        var meta = WorldMeta.CreateFlat(info.WorldName, info.GameMode, info.Width, info.Height, info.Depth, info.Seed);
        meta.PlayerCollision = info.PlayerCollision;
        meta.WorldId = JoinedWorldCache.ResolveWorldId(info);
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

    private string FitTextToWidth(string text, int maxWidth, out bool trimmed)
    {
        text = (text ?? string.Empty).Trim();
        trimmed = false;
        if (string.IsNullOrEmpty(text) || maxWidth <= 8)
            return text;

        if (_font.MeasureString(text).X <= maxWidth)
            return text;

        trimmed = true;
        const string Ellipsis = "...";
        var low = 0;
        var high = text.Length;
        var best = Ellipsis;

        while (low <= high)
        {
            var mid = (low + high) / 2;
            var candidate = text.Substring(0, mid) + Ellipsis;
            if (_font.MeasureString(candidate).X <= maxWidth)
            {
                best = candidate;
                low = mid + 1;
            }
            else
            {
                high = mid - 1;
            }
        }

        return best;
    }

    private void UpdateHoveredSession()
    {
        _hoveredIndex = -1;
        if (!_listBodyRect.Contains(_lastMousePos))
            return;

        var rowH = (int)(64 * 0.80f) + 4;
        var idx = (int)((_lastMousePos.Y - _listBodyRect.Y) / rowH);
        if (idx >= 0 && idx < _sessions.Count)
            _hoveredIndex = idx;
    }

    private static string BuildSessionKey(SessionEntry entry)
    {
        var type = entry.Type.ToString();
        var friendUserId = (entry.FriendUserId ?? string.Empty).Trim();
        var joinTarget = (entry.JoinTarget ?? string.Empty).Trim();
        var worldName = (entry.WorldName ?? string.Empty).Trim();
        return $"{type}|{friendUserId}|{joinTarget}|{worldName}";
    }

    private static bool IsPresenceEligibleAsActiveHost(GatePresenceEntry presence)
    {
        if (presence == null)
            return false;

        var nowUtc = DateTime.UtcNow;
        if (presence.ExpiresUtc != default && presence.ExpiresUtc <= nowUtc)
            return false;
        if (presence.UpdatedUtc != default && (nowUtc - presence.UpdatedUtc) > TimeSpan.FromSeconds(30))
            return false;

        var isHosting = presence.IsHosting && presence.IsInWorld;
        if (!isHosting)
            return false;
        return !string.IsNullOrWhiteSpace(presence.JoinTarget);
    }

    private void DrawRefreshIcon(SpriteBatch sb, Rectangle rect, Color color)
    {
        // Draw a simple circular arrow icon
        var centerX = rect.X + rect.Width / 2;
        var centerY = rect.Y + rect.Height / 2;
        var radius = rect.Width / 2 - 2;
        
        // Draw circle
        for (int angle = 0; angle < 360; angle += 10)
        {
            var rad = (angle + _refreshIconRotation) * Math.PI / 180;
            var x = (int)(centerX + radius * Math.Cos(rad));
            var y = (int)(centerY + radius * Math.Sin(rad));
            sb.Draw(_pixel, new Rectangle(x, y, 2, 2), color);
        }
        
        // Draw arrow head
        var arrowRad = (_refreshIconRotation) * Math.PI / 180;
        var arrowX = (int)(centerX + radius * Math.Cos(arrowRad));
        var arrowY = (int)(centerY + radius * Math.Sin(arrowRad));
        
        // Arrow pointing clockwise
        var arrowTipRad = (_refreshIconRotation + 90) * Math.PI / 180;
        var arrowTipX = (int)(arrowX + 6 * Math.Cos(arrowTipRad));
        var arrowTipY = (int)(arrowY + 6 * Math.Sin(arrowTipRad));
        
        sb.Draw(_pixel, new Rectangle(arrowX, arrowY, 2, 2), color);
        sb.Draw(_pixel, new Rectangle(arrowTipX, arrowTipY, 3, 3), color);
    }

    private void DrawBorder(SpriteBatch sb, Rectangle r, Color color)
    {
        sb.Draw(_pixel, new Rectangle(r.X, r.Y, r.Width, 2), color);
        sb.Draw(_pixel, new Rectangle(r.X, r.Bottom - 2, r.Width, 2), color);
        sb.Draw(_pixel, new Rectangle(r.X, r.Y, 2, r.Height), color);
        sb.Draw(_pixel, new Rectangle(r.Right - 2, r.Y, 2, r.Height), color);
    }
}
