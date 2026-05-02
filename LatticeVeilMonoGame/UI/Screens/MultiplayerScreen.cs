using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
    private readonly MemoryWebTextureLoader _webTextureLoader;

    private readonly LanDiscovery _lanDiscovery;
    private EosClient? _eosClient;
    private readonly OnlineGateClient _onlineGate;

    private readonly Button _refreshBtn;
    private readonly Button _addBtn;
    private readonly Button _hostBtn;
    private readonly Button _joinBtn;
	private readonly Button _yourIdBtn;
    private readonly Button _filterLanBtn;
    private readonly Button _filterOnlineBtn;
    private readonly Button _backBtn;

    private enum SessionType { Lan, Online }
    private enum BrowserFilter { Lan, Online }
    private enum JoinRequestStateKind { None, Requested, Approved, Declined }

    private sealed class JoinRequestState
    {
        public JoinRequestStateKind State;
        public DateTime UpdatedUtc;
    }
    
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
        public bool IsFriendHost;
        public bool HasInvite;
        public bool IsLocalUserWhitelisted;
        public string WorldId = "";
        public string HostPictureUrl = "";
        public DateTime LastSeenUtc;
    }
    
    private List<SessionEntry> _sessions = new();
    private List<SessionEntry> _onlineSessions = new();
    private readonly Dictionary<string, JoinRequestState> _joinRequestStates = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _hostDisplayNameCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, PreviewTextureCacheEntry> _previewTextures = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, AvatarVisualState> _hostAvatarVisuals = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Task<GateIdentityUser?>> _hostProfileHydrationTasks = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _previewLoadFailures = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _invitedHostIds = new(StringComparer.OrdinalIgnoreCase);
    private int _selectedIndex = -1;
    private int _hoveredIndex = -1;
    private Vector2 _lastMousePos = Vector2.Zero;
    private string _hoveredWorldDetails = string.Empty;

    private DateTime _nextOnlineRefreshUtc = DateTime.MinValue;
    private DateTime _nextSessionRefreshUtc = DateTime.MinValue;
    private DateTime _lastUpdateUtc = DateTime.MinValue;
    private bool _onlineRefreshInProgress;
    private bool _onlineRefreshQueued;
    private bool _isRefreshing = false;
    private int _onlineRefreshFailures;
    private int _sessionRefreshFailures;
    private int _emptyOnlineRefreshStreak;
    private float _refreshIconRotation = 0f;
    private bool _justReturnedFromHosting = false;

    private bool _joining;
    private string _status = "";
    private BrowserFilter _activeFilter = BrowserFilter.Online;
    private readonly bool _onlineUiAvailable;
    private readonly string _preferredInviteSenderId;
    private readonly string _preferredInviteSenderDisplayName;
    private readonly string _preferredInviteJoinTarget;
    private readonly string _preferredInviteSenderPictureUrl;
    private readonly string _preferredInviteWorldName;
    private readonly string _preferredInviteGameMode;
    private readonly bool _autoJoinPreferredInvite;
    private bool _autoJoinPreferredInvitePending;
    private DateTime _preferredInviteAutoJoinDeadlineUtc = DateTime.MinValue;

    private Texture2D? _bg;
    private Texture2D? _panel;

    private double _lastLanRefresh;
    private int _lastLoggedLanDiscoveryCount = -1;
    private const double LanRefreshIntervalSeconds = 3.0;
    private const double DefaultListRefreshSeconds = 2.0;
    private const double RefreshBackoffSeconds = 20.0;
    private const int MaxWorldNameChars = 48;
    private const int MaxUsernameChars = 24;

    private Rectangle _viewport;
    private Rectangle _panelRect;
    private Rectangle _listRect;
    private Rectangle _listBodyRect;
    private Rectangle _buttonRowRect;
    private Rectangle _filterRowRect;
    private Rectangle _infoRect;
    private Rectangle _playerNameInputRect;

    private EosIdentityStore _identityStore;
    private bool _identitySyncBusy;
    private double _lastIdentitySyncAttempt = -100;
    private const double IdentitySyncIntervalSeconds = 8.0;
    private string _reservedUsername = string.Empty;

    private double _now;
    private const double DoubleClickSeconds = 0.35;
    private const double JoinRequestCooldownSeconds = 2.0;
    private const double JoinRequestApprovalTimeoutSeconds = 20.0;
    private const double InviteAutoJoinTimeoutSeconds = 20.0;
    private const double HostedLobbyHeartbeatGraceSeconds = 30.0;
    private const double OnlineSessionVisualGraceSeconds = 90.0;
    private const string OnlineLoadingStatusPrefix = "Online loading:";
    private double _lastClickTime;
    private int _lastClickIndex = -1;

    private sealed record PreviewTextureCacheEntry(Texture2D Texture, DateTime StampUtc);

    private sealed class AvatarVisualState
    {
        public string SourceUrl { get; set; } = string.Empty;
        public Task<byte[]?>? BytesTask { get; set; }
        public Texture2D? Texture { get; set; }
    }

    private static bool LooksLikeRateLimitFailure(string message)
    {
        var text = (message ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(text))
            return false;

        return text.Contains("429", StringComparison.OrdinalIgnoreCase)
            || text.Contains("too many", StringComparison.OrdinalIgnoreCase)
            || text.Contains("rate", StringComparison.OrdinalIgnoreCase)
            || text.Contains("throttle", StringComparison.OrdinalIgnoreCase);
    }

    private void PreserveCachedOnlineSessions(
        HashSet<string> invitedHostIds,
        HashSet<string> friendIds,
        string? statusText)
    {
        if (_onlineSessions.Count == 0)
            return;

        var nowUtc = DateTime.UtcNow;
        var retainedSessions = new List<SessionEntry>(_onlineSessions.Count);
        for (var i = 0; i < _onlineSessions.Count; i++)
        {
            var previous = _onlineSessions[i];
            if ((nowUtc - previous.LastSeenUtc).TotalSeconds > OnlineSessionVisualGraceSeconds)
                continue;

            previous.Status = "Refreshing";
            previous.HasInvite = previous.HasInvite || invitedHostIds.Contains((previous.FriendUserId ?? string.Empty).Trim());
            previous.IsFriendHost = previous.IsFriendHost || friendIds.Contains((previous.FriendUserId ?? string.Empty).Trim());
            retainedSessions.Add(previous);
        }

        if (retainedSessions.Count == 0)
            return;

        _onlineSessions = retainedSessions
            .OrderBy(s => s.HostName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(s => s.WorldName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        _invitedHostIds.Clear();
        foreach (var invitedHostId in invitedHostIds)
            _invitedHostIds.Add(invitedHostId);

        if (!_joining && !string.IsNullOrWhiteSpace(statusText))
            _status = statusText;

        RefreshSessions();
    }

    public MultiplayerScreen(MenuStack menus, AssetLoader assets, PixelFont font, Texture2D pixel, Logger log, PlayerProfile profile,
        global::Microsoft.Xna.Framework.GraphicsDeviceManager graphics, EosClient? eosClient, string? preferredInviteSenderId = null, string? preferredInviteSenderDisplayName = null, string? preferredInviteJoinTarget = null, string? preferredInviteSenderPictureUrl = null, string? preferredInviteWorldName = null, string? preferredInviteGameMode = null, bool autoJoinPreferredInvite = false)
    {
        _menus = menus;
        _assets = assets;
        _font = font;
        _pixel = pixel;
        _log = log;
        _profile = profile;
        _graphics = graphics;
        _webTextureLoader = new MemoryWebTextureLoader();

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
        _onlineUiAvailable = !offlineLaunch && _onlineGate.CanUseOfficialOnline(_log, out _);
        _preferredInviteSenderId = (preferredInviteSenderId ?? string.Empty).Trim();
        _preferredInviteSenderDisplayName = (preferredInviteSenderDisplayName ?? string.Empty).Trim();
        _preferredInviteJoinTarget = (preferredInviteJoinTarget ?? string.Empty).Trim();
        _preferredInviteSenderPictureUrl = (preferredInviteSenderPictureUrl ?? string.Empty).Trim();
        _preferredInviteWorldName = (preferredInviteWorldName ?? string.Empty).Trim();
        _preferredInviteGameMode = NormalizeModeLabel(preferredInviteGameMode);
        _autoJoinPreferredInvite = autoJoinPreferredInvite && !string.IsNullOrWhiteSpace(_preferredInviteSenderId);
        _autoJoinPreferredInvitePending = _autoJoinPreferredInvite;
        _preferredInviteAutoJoinDeadlineUtc = _autoJoinPreferredInvite
            ? DateTime.UtcNow.AddSeconds(InviteAutoJoinTimeoutSeconds)
            : DateTime.MinValue;
        _identityStore = EosIdentityStore.LoadOrCreate(_log);
        _reservedUsername = (_identityStore.ReservedUsername ?? string.Empty).Trim();

        _lanDiscovery = new LanDiscovery(_log);
        _lanDiscovery.StartListening();

        _refreshBtn = new Button("REFRESH", OnRefreshClicked) { BoldText = true };
        _addBtn = new Button("ADD", OnAddClicked) { BoldText = true };
        _hostBtn = new Button("HOST", OpenHostWorlds) { BoldText = true };
        _joinBtn = new Button("JOIN", OnJoinClicked) { BoldText = true };
		_yourIdBtn = new Button("PROFILE", OnYourIdClicked) { BoldText = true };
        _filterLanBtn = new Button("LAN", () => SetBrowserFilter(BrowserFilter.Lan)) { BoldText = true };
        _filterOnlineBtn = new Button("ONLINE", () => SetBrowserFilter(BrowserFilter.Online)) { BoldText = true };
        _backBtn = new Button("BACK", () => { Cleanup(); _menus.Pop(); }) { BoldText = true };
        _activeFilter = _onlineUiAvailable ? BrowserFilter.Online : BrowserFilter.Lan;

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
        _nextOnlineRefreshUtc = DateTime.MinValue;
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
        var filterRowH = (int)((_font.LineHeight + 14) * UIScale);
        _filterRowRect = new Rectangle(_infoRect.X, _infoRect.Bottom + (int)(6 * UIScale), _infoRect.Width, filterRowH);
        LayoutFilterButtons();

        var buttonRowH = (int)Math.Max(60, (_font.LineHeight * 2 + 18) * UIScale);
        _buttonRowRect = new Rectangle(uiOffsetX + pad, uiOffsetY + scaledUIHeight - pad/2 - buttonRowH, (int)((scaledUIWidth - pad * 2) * UIScale), buttonRowH);
        _listRect = new Rectangle(uiOffsetX + pad, _filterRowRect.Bottom + (int)(8 * UIScale), (int)((scaledUIWidth - pad * 2) * UIScale) + (int)(240 * UIScale), _buttonRowRect.Top - _filterRowRect.Bottom - (int)(10 * UIScale));

        var listHeaderH = (int)((_font.LineHeight + 20) * UIScale);
        _listBodyRect = new Rectangle(_listRect.X + (int)(8 * UIScale), _listRect.Y + listHeaderH, Math.Max(0, _listRect.Width - (int)(16 * UIScale)), Math.Max(0, _listRect.Height - listHeaderH - (int)(8 * UIScale)));
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
        var nowUtc = DateTime.UtcNow;
        if (_lastUpdateUtc != DateTime.MinValue && (nowUtc - _lastUpdateUtc).TotalSeconds >= 1.5)
            HandleScreenResume();
        _lastUpdateUtc = nowUtc;

        _now = gameTime.TotalGameTime.TotalSeconds;
        ProcessCompletedHostAvatarLoads();
        ProcessCompletedHostProfileHydration();

        if (input.IsNewKeyPress(Keys.Escape))
        {
            Cleanup();
            _menus.Pop();
            return;
        }

        HandleListInput(input);
        UpdateOnlineActionEnabledState();
        _refreshBtn.Update(input);
        _addBtn.Update(input);
        _hostBtn.Update(input);
        _joinBtn.Update(input);
        _filterLanBtn.Update(input);
        _filterOnlineBtn.Update(input);
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

        if (_activeFilter != BrowserFilter.Lan && !_onlineRefreshInProgress && nowUtc >= _nextOnlineRefreshUtc)
            _ = RefreshOnlineSessionsAsync();

        // Auto-refresh list while screen is active.
        if (_activeFilter == BrowserFilter.Lan && _justReturnedFromHosting)
        {
            _isRefreshing = true;
            _joinRequestStates.Clear();
            RefreshSessions();
            _lastLanRefresh = _now;
            _nextSessionRefreshUtc = DateTime.UtcNow.AddSeconds(LanRefreshIntervalSeconds);
            _justReturnedFromHosting = false; // Reset flag
        }
        else if (_activeFilter != BrowserFilter.Lan && _justReturnedFromHosting)
        {
            _joinRequestStates.Clear();
            _justReturnedFromHosting = false;
            _nextOnlineRefreshUtc = DateTime.MinValue;
            _ = RefreshOnlineSessionsAsync();
        }

        TryDirectJoinPreferredInvite();
        UpdateLanDiscovery(gameTime);
        UpdateHoveredSession();
        EnsureHostAvatarHydration();
        RefreshIdentityStateFromEos(EnsureEosClient());
        if (!_identitySyncBusy && _now - _lastIdentitySyncAttempt >= IdentitySyncIntervalSeconds)
        {
            _lastIdentitySyncAttempt = _now;
            _ = SyncIdentityWithGateAsync();
        }

        if (_autoJoinPreferredInvitePending && DateTime.UtcNow >= _preferredInviteAutoJoinDeadlineUtc)
        {
            _autoJoinPreferredInvitePending = false;
            if (string.IsNullOrWhiteSpace(_status))
                _status = "Invite host not visible yet. Refreshing...";
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
            if (selected.Type == SessionType.Lan)
            {
                _joinBtn.Enabled = !string.IsNullOrWhiteSpace(selected.JoinTarget);
            }
            else if (selected.Type == SessionType.Online)
            {
                var state = GetJoinRequestState(selected);
                _joinBtn.Enabled = !string.IsNullOrWhiteSpace(selected.JoinTarget)
                    && (CanJoinDirect(selected) || state != JoinRequestStateKind.Requested || CanRetryJoinRequest(selected));
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

        if (ShouldShowInviteConnectingOverlay())
        {
            DrawInviteConnectingOverlay(sb);
            _backBtn.Draw(sb, _pixel, _font);
            sb.End();
            return;
        }

        DrawPanel(sb);
        DrawInfo(sb);
        DrawFilters(sb);
        DrawList(sb);
        DrawButtons(sb);
        DrawStatus(sb);
        _backBtn.Draw(sb, _pixel, _font);

        sb.End();
    }

    private bool ShouldShowInviteConnectingOverlay()
    {
        if (string.IsNullOrWhiteSpace(_preferredInviteSenderId))
            return false;

        return _autoJoinPreferredInvitePending || _joining;
    }

    private string GetPreferredInviteDisplayName()
    {
        if (!string.IsNullOrWhiteSpace(_preferredInviteSenderDisplayName))
            return _preferredInviteSenderDisplayName;

        if (!string.IsNullOrWhiteSpace(_preferredInviteSenderId)
            && _hostDisplayNameCache.TryGetValue(_preferredInviteSenderId, out var cached)
            && !string.IsNullOrWhiteSpace(cached))
        {
            return cached;
        }

        return "PLAYER";
    }

    private bool TryGetPreferredInviteAvatarTexture(out Texture2D? texture)
    {
        texture = null;
        if (string.IsNullOrWhiteSpace(_preferredInviteSenderId))
            return false;

        if (!string.IsNullOrWhiteSpace(_preferredInviteSenderPictureUrl))
            QueueHostAvatarLoad(_preferredInviteSenderId, _preferredInviteSenderPictureUrl);

        if (_hostAvatarVisuals.TryGetValue(_preferredInviteSenderId, out var state) && state.Texture is { IsDisposed: false })
        {
            texture = state.Texture;
            return true;
        }

        return false;
    }

    private void DrawInviteConnectingOverlay(SpriteBatch sb)
    {
        DrawPanel(sb);

        var boxWidth = Math.Min(_panelRect.Width - 120, 620);
        var boxHeight = 176;
        var box = new Rectangle(
            _panelRect.Center.X - boxWidth / 2,
            _panelRect.Center.Y - boxHeight / 2,
            boxWidth,
            boxHeight);

        sb.Draw(_pixel, box, new Color(14, 18, 26, 230));
        DrawBorder(sb, box, new Color(210, 216, 228));

        var avatarRect = new Rectangle(box.X + 18, box.Y + 44, 64, 64);
        if (TryGetPreferredInviteAvatarTexture(out var avatarTexture))
            sb.Draw(avatarTexture, avatarRect, Color.White);
        else
            DrawCircularBadge(sb, avatarRect, GetPreferredInviteDisplayName());
        DrawBorder(sb, avatarRect, Color.White);

        var playerName = GetPreferredInviteDisplayName();
        var title = $"CONNECTING TO {playerName.ToUpperInvariant()}";
        DrawCenteredContainedText(
            sb,
            title,
            new Rectangle(box.X + 18, box.Y + 34, box.Width - 36, _font.LineHeight),
            Color.White,
            _now,
            18);

        var worldName = string.IsNullOrWhiteSpace(_preferredInviteWorldName) ? "WORLD" : _preferredInviteWorldName.Trim();
        var modeText = string.IsNullOrWhiteSpace(_preferredInviteGameMode) ? "UNKNOWN" : _preferredInviteGameMode.Trim();
        var inviteText = $"\"{playerName}\" invited you to \"{worldName}\" : \"{modeText}\"";
        DrawContainedText(
            sb,
            inviteText,
            new Rectangle(avatarRect.Right + 14, avatarRect.Y + 14, box.Right - avatarRect.Right - 32, _font.LineHeight),
            new Color(220, 220, 220),
            _now,
            18);

        var statusText = string.IsNullOrWhiteSpace(_status) ? "Please wait..." : _status;
        DrawCenteredContainedText(
            sb,
            statusText,
            new Rectangle(box.X + 18, box.Bottom - 42, box.Width - 36, _font.LineHeight),
            new Color(186, 194, 208),
            _now,
            18);
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

    private void DrawFilters(SpriteBatch sb)
    {
        _filterLanBtn.BackgroundColor = _activeFilter == BrowserFilter.Lan ? new Color(80, 100, 150) : null;
        _filterOnlineBtn.BackgroundColor = _onlineUiAvailable && _activeFilter == BrowserFilter.Online ? new Color(80, 100, 150) : null;

        _filterLanBtn.Draw(sb, _pixel, _font);
        if (_onlineUiAvailable)
            _filterOnlineBtn.Draw(sb, _pixel, _font);
    }

    private void DrawList(SpriteBatch sb)
    {
        _hoveredWorldDetails = string.Empty;

        sb.Draw(_pixel, _listRect, new Color(20, 20, 20, 200));
        DrawBorder(sb, _listRect, Color.White);

        if (_sessions.Count == 0)
        {
            var msg = _activeFilter switch
            {
                BrowserFilter.Lan => "No LAN hosts found.",
                BrowserFilter.Online => "No online EOS lobbies found.",
                _ => "No sessions found."
            };
            var size = _font.MeasureString(msg);
            _font.DrawString(sb, msg, new Vector2(_listRect.Center.X - size.X / 2, _listRect.Center.Y - size.Y / 2), Color.Gray);
            return;
        }

        // Draw headers only when there are sessions
        const float UIScale = 0.80f;
        var headerRect = new Rectangle(_listRect.X, _listRect.Y, _listRect.Width, (int)((_font.LineHeight + 20) * UIScale));
        sb.Draw(_pixel, headerRect, new Color(40, 40, 40) * 0.8f);
        DrawBorder(sb, headerRect, new Color(60, 60, 60));
        sb.Draw(_pixel, new Rectangle(headerRect.X, headerRect.Bottom - 3, headerRect.Width, 3), new Color(220, 180, 80, 220));

        var pad = 10;
        var contentWidth = Math.Max(240, headerRect.Width - pad * 2);
        var iconColW = Math.Clamp((int)(contentWidth * 0.06f), 24, 44);
        var cheatsColW = Math.Clamp((int)(contentWidth * 0.11f), 70, 118);
        var modeColW = Math.Clamp((int)(contentWidth * 0.15f), 90, 150);
        var playersColW = Math.Clamp((int)(contentWidth * 0.18f), 100, 170);
        var remainingForNames = Math.Max(220, contentWidth - (iconColW + cheatsColW + modeColW + playersColW));

        var maxUserWidth = (int)_font.MeasureString("USERNAME").X;
        var maxWorldWidth = (int)_font.MeasureString("WORLD").X;
        for (var i = 0; i < _sessions.Count; i++)
        {
            var userDisplay = LimitDisplayText(_sessions[i].HostName, MaxUsernameChars);
            var worldDisplay = LimitDisplayText(_sessions[i].WorldName, MaxWorldNameChars);
            maxUserWidth = Math.Max(maxUserWidth, (int)_font.MeasureString(userDisplay).X);
            maxWorldWidth = Math.Max(maxWorldWidth, (int)_font.MeasureString(worldDisplay).X);
        }

        var desiredUserWidth = Math.Clamp(maxUserWidth + 26, 105, Math.Max(110, remainingForNames - 120));
        var desiredWorldWidth = Math.Clamp(maxWorldWidth + 26, 120, Math.Max(120, remainingForNames - 105));
        var desiredTotal = desiredUserWidth + desiredWorldWidth;
        if (desiredTotal > remainingForNames)
        {
            var scale = (double)remainingForNames / desiredTotal;
            desiredUserWidth = Math.Max(100, (int)Math.Floor(desiredUserWidth * scale));
            desiredWorldWidth = Math.Max(120, remainingForNames - desiredUserWidth);
        }
        else
        {
            var extra = remainingForNames - desiredTotal;
            desiredWorldWidth += (int)Math.Floor(extra * 0.65);
            desiredUserWidth = remainingForNames - desiredWorldWidth;
        }

        var userColW = Math.Max(100, desiredUserWidth);
        var worldColW = Math.Max(120, remainingForNames - userColW);

        var colX = headerRect.X + pad + 8; // Shift headers right by 8px
        _font.DrawString(sb, "", new Vector2(colX, headerRect.Y + 4), Color.White);
        colX += iconColW;
        _font.DrawString(sb, FitTextToWidth("USERNAME", userColW - 12, out _), new Vector2(colX + 8, headerRect.Y + 4), new Color(180, 180, 180));
        colX += userColW;
        _font.DrawString(sb, FitTextToWidth("WORLD", worldColW - 12, out _), new Vector2(colX + 8, headerRect.Y + 4), new Color(180, 180, 180));
        colX += worldColW;
        _font.DrawString(sb, FitTextToWidth("CHEATS", cheatsColW - 12, out _), new Vector2(colX + 8, headerRect.Y + 4), new Color(180, 180, 180));
        colX += cheatsColW;
        _font.DrawString(sb, FitTextToWidth("MODE", modeColW - 12, out _), new Vector2(colX + 8, headerRect.Y + 4), new Color(180, 180, 180));
        colX += modeColW;
        _font.DrawString(sb, FitTextToWidth("PLAYERS", playersColW - 12, out _), new Vector2(colX + 8, headerRect.Y + 4), new Color(180, 180, 180));

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
            
            if (s.Type == SessionType.Online)
            {
                DrawHostAvatarBadge(sb, s, iconRect);
            }
            else
            {
                DrawWorldPreviewTile(sb, s, iconRect);
            }

            var textX = _listBodyRect.X + pad + iconColW + 8; // Match header shift
            var textY = rowRect.Y + (rowRect.Height - _font.LineHeight) / 2;

            // Username column (shifted right)
            var user = LimitDisplayText(s.HostName, MaxUsernameChars);
            var userFit = FitTextToWidth(user, Math.Max(24, userColW - 16), out _);
            _font.DrawString(sb, userFit, new Vector2(textX + 8, textY), Color.White);
            textX += userColW;

            // World column (shifted right)
            var world = LimitDisplayText(s.WorldName, MaxWorldNameChars);
            var worldTextRect = new Rectangle(textX + 8, textY, Math.Max(24, worldColW - 16), _font.LineHeight);
            DrawContainedText(sb, world, worldTextRect, Color.White, _now, 18);
            if (_font.MeasureString(world).X > worldTextRect.Width && (i == _selectedIndex || rowIsHovered))
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
            var players = $"{activePlayers} ACTIVE";
            var playersFit = FitTextToWidth(players, Math.Max(24, playersColW - 12), out _);
            _font.DrawString(sb, playersFit, new Vector2(textX + 8, textY), Color.White);

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
            if (s.Type == SessionType.Online)
            {
                var state = GetJoinRequestState(s);
                joinText = CanJoinDirect(s) || state == JoinRequestStateKind.Approved
                    ? "JOIN"
                    : state == JoinRequestStateKind.Requested
                        ? "JOIN"
                        : "REQUEST JOIN";
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
        if (!string.IsNullOrEmpty(_status)
            && !_status.Contains("refresh", StringComparison.OrdinalIgnoreCase))
        {
            var statusText = _status;
            var statusSize = _font.MeasureString(statusText);
            var statusCenterX = (_addBtn.Bounds.X + _hostBtn.Bounds.Right) / 2f;
            var pos = new Vector2(
                statusCenterX - (statusSize.X / 2f),
                _buttonRowRect.Y - _font.LineHeight - 8);
            _font.DrawString(sb, _status, pos, Color.White);
        }
    }

    private void OnRefreshClicked()
    {
        _isRefreshing = true;
        _sessionRefreshFailures = 0;
        _onlineRefreshFailures = 0;
        _nextOnlineRefreshUtc = DateTime.MinValue;
        if (_activeFilter != BrowserFilter.Lan)
        {
            if (_onlineRefreshInProgress)
            {
                _onlineRefreshQueued = true;
                return;
            }

            _ = RefreshOnlineSessionsAsync();
        }
        else
        {
            RefreshSessions();
            _lastLanRefresh = _now;
        }
        _nextSessionRefreshUtc = DateTime.UtcNow.AddSeconds(LanRefreshIntervalSeconds);
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
        else if (entry.Type == SessionType.Online)
        {
            if (string.IsNullOrWhiteSpace(entry.JoinTarget))
            {
                _status = "Host join target is unavailable. Refresh and try again.";
                _joining = false;
                return;
            }

            var joinState = GetJoinRequestState(entry);
            if (CanJoinDirect(entry) || joinState == JoinRequestStateKind.Approved)
            {
                _status = $"Joining {entry.HostName} via EOS...";
                _ = JoinOnlineSessionAsync(entry);
                return;
            }

            if (joinState == JoinRequestStateKind.Requested)
            {
                _status = $"Joining {entry.HostName} via EOS...";
                _ = JoinOnlineSessionAsync(entry);
                return;
            }

            _status = $"Sending join request to {entry.HostName}...";
            _ = RequestJoinApprovalAsync(entry);
        }
        else
        {
            _status = "Joining dedicated servers not implemented yet.";
            _joining = false;
        }
    }

    private void OnYourIdClicked()
    {
        _menus.Push(new ProfileScreen(_menus, _assets, _font, _pixel, _log, _profile, _graphics, EnsureEosClient()), _viewport);
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
        var wantsOnlineHost = _onlineUiAvailable && _activeFilter != BrowserFilter.Lan;
        if (wantsOnlineHost)
        {
            if (!_onlineGate.CanUseOfficialOnline(_log, out var gateDenied))
            {
                _status = gateDenied;
                return;
            }

            var eos = EnsureEosClient();
            var snapshot = EosRuntimeStatus.Evaluate(eos);
            if (snapshot.Reason != EosRuntimeReason.Ready)
            {
                if (snapshot.Reason == EosRuntimeReason.Connecting)
                    _status = "EOS connecting... please wait a moment.";
                else
                    _status = snapshot.StatusText;
                return;
            }
        }

        _justReturnedFromHosting = true; // Set flag to refresh immediately on return
        _menus.Push(new MultiplayerHostScreen(_menus, _assets, _font, _pixel, _log, _profile, _graphics, _eosClient, wantsOnlineHost), _viewport);
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
    }

    private void LayoutFilterButtons()
    {
        if (!_onlineUiAvailable)
        {
            _filterLanBtn.Bounds = _filterRowRect;
            _filterOnlineBtn.Bounds = Rectangle.Empty;
            return;
        }

        var gap = 8;
        var buttonCount = 2;
        var buttonW = Math.Max(90, (_filterRowRect.Width - gap * (buttonCount - 1)) / buttonCount);
        var totalW = buttonW * buttonCount + gap * (buttonCount - 1);
        var startX = _filterRowRect.X + Math.Max(0, (_filterRowRect.Width - totalW) / 2);
        var y = _filterRowRect.Y;

        _filterLanBtn.Bounds = new Rectangle(startX, y, buttonW, _filterRowRect.Height);
        _filterOnlineBtn.Bounds = new Rectangle(_filterLanBtn.Bounds.Right + gap, y, buttonW, _filterRowRect.Height);
    }

    private void SetBrowserFilter(BrowserFilter filter)
    {
        if (filter == BrowserFilter.Online && !_onlineUiAvailable)
            filter = BrowserFilter.Lan;

        if (_activeFilter == filter)
            return;

        _activeFilter = filter;
        _status = string.Empty;
        _nextSessionRefreshUtc = DateTime.MinValue;
        _nextOnlineRefreshUtc = DateTime.MinValue;
        _sessionRefreshFailures = 0;
        _onlineRefreshFailures = 0;
        _isRefreshing = true;
        if (_activeFilter != BrowserFilter.Lan)
            _ = RefreshOnlineSessionsAsync();
        RefreshSessions();
    }

    private async Task JoinP2PAsync(string hostPuid, string hostName)
    {
        var eos = EnsureEosClient();
        if (eos == null) { _status = "EOS not ready."; _joining = false; return; }

        var outboundDisplayName = GetOutboundJoinDisplayName();
        var result = await EosP2PClientSession.ConnectAsync(_log, eos, hostPuid, outboundDisplayName, TimeSpan.FromSeconds(10));
        if (!result.Success || result.Session == null)
        {
            _status = $"P2P Failed: {result.Error}";
            _joining = false;
            return;
        }

        var info = result.WorldInfo;
        var worldDir = JoinedWorldCache.PrepareJoinedWorldPath(info, _log);
        var metaPath = Paths.GetWorldMetaPath(worldDir);
        var meta = WorldMeta.CreateFlat(info.WorldName, info.GameMode, info.Width, info.Height, info.Depth, info.Seed);
        meta.PlayerCollision = info.PlayerCollision;
        meta.Player.PlayerCollision = info.PlayerCollision;
        meta.EnableMultipleHomes = info.EnableMultipleHomes;
        meta.MaxHomesPerPlayer = Math.Clamp(info.MaxHomesPerPlayer, 1, 32);
        meta.Gameplay.SaveInventory = info.SaveInventory;
        meta.Gameplay.SharedSoulAccess = info.SharedSoulAccess;
        meta.Gameplay.EnablePvp = info.PvpEnabled;
        meta.Gameplay.DeathType = string.IsNullOrWhiteSpace(info.DeathType) ? GameplaySettings.DefaultDeathType : info.DeathType;
        meta.TimeCycleEnabled = info.TimeCycleEnabled;
        meta.WeatherCycleEnabled = info.WeatherCycleEnabled;
        meta.TimeOfDayTicks = WorldMeta.CanonicalTimeTicks(info.TimeOfDayTicks);
        meta.WeatherState = WorldMeta.CanonicalWeatherState(info.WeatherState);
        meta.Gameplay.EnableMultipleHomes = meta.EnableMultipleHomes;
        meta.Gameplay.MaxHomesPerPlayer = meta.MaxHomesPerPlayer;
        meta.Gameplay.SaveInventory = info.SaveInventory;
        meta.Gameplay.SharedSoulAccess = info.SharedSoulAccess;
        meta.Gameplay.DeathType = string.IsNullOrWhiteSpace(info.DeathType) ? GameplaySettings.DefaultDeathType : info.DeathType;
        meta.Gameplay.TimeCycleEnabled = meta.TimeCycleEnabled;
        meta.Gameplay.WeatherCycleEnabled = meta.WeatherCycleEnabled;
        meta.Gameplay.TimeOfDayTicks = meta.TimeOfDayTicks;
        meta.Gameplay.WeatherState = meta.WeatherState;
        meta.WorldId = JoinedWorldCache.ResolveWorldId(info);
        meta.WorldGeneration.WorldType = WorldMeta.CanonicalWorldType(info.WorldType);
        meta.Generator = WorldMeta.CanonicalGeneratorForWorldType(meta.WorldGeneration.WorldType);
        meta.Save(metaPath, _log);

        if (!string.IsNullOrWhiteSpace(_preferredInviteSenderId))
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    await _onlineGate.RespondToWorldInviteAsync(_preferredInviteSenderId, "accepted").ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _log.Warn($"WORLD_INVITE_ACCEPT_AFTER_JOIN_FAILED sender={_preferredInviteSenderId} error={ex.Message}");
                }
            });
        }

        _status = $"Connected to {hostName}!";
        _joining = false;

        _menus.Push(new GameWorldScreen(_menus, _assets, _font, _pixel, _log, _profile, _graphics, worldDir, metaPath, result.Session), _viewport);
    }

    private void TryDirectJoinPreferredInvite()
    {
        if (!_autoJoinPreferredInvitePending || _joining)
            return;

        var joinTarget = !string.IsNullOrWhiteSpace(_preferredInviteJoinTarget)
            ? _preferredInviteJoinTarget
            : _preferredInviteSenderId;
        if (string.IsNullOrWhiteSpace(joinTarget))
            return;

        _autoJoinPreferredInvitePending = false;
        _joining = true;
        var preferredName = GetPreferredInviteDisplayName();
        _status = $"Connecting to {preferredName}...";
        _ = JoinP2PAsync(joinTarget, preferredName);
    }

    private async Task JoinOnlineSessionAsync(SessionEntry entry)
    {
        var joinStateKey = BuildJoinStateKey(entry);
        ClearJoinRequestState(joinStateKey);

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

    private async Task RequestJoinApprovalAsync(SessionEntry entry)
    {
        var eos = EnsureEosClient();
        if (eos == null)
        {
            _status = "EOS not ready.";
            _joining = false;
            return;
        }

        var joinKey = BuildJoinStateKey(entry);
        if (string.IsNullOrWhiteSpace(joinKey))
        {
            _status = "Host join target is unavailable.";
            _joining = false;
            return;
        }

        try
        {
            var result = await EosP2PClientSession.RequestJoinApprovalAsync(
                _log,
                eos,
                entry.JoinTarget,
                GetOutboundJoinDisplayName(),
                TimeSpan.FromSeconds(JoinRequestApprovalTimeoutSeconds));

            switch (result.Status)
            {
                case EosJoinApprovalStatus.Approved:
                    // If the host approves quickly, proceed immediately so JOIN doesn't feel like "every other click".
                    SetJoinRequestState(joinKey, JoinRequestStateKind.Approved);
                    _status = $"Join approved by {entry.HostName}. Joining...";
                    await JoinOnlineSessionAsync(entry);
                    break;
                case EosJoinApprovalStatus.Pending:
                    SetJoinRequestState(joinKey, JoinRequestStateKind.Requested);
                    _status = "Join request pending. Waiting for host approval...";
                    break;
                case EosJoinApprovalStatus.Declined:
                    SetJoinRequestState(joinKey, JoinRequestStateKind.Declined);
                    _status = string.IsNullOrWhiteSpace(result.Message)
                        ? "Join request declined by host."
                        : result.Message;
                    break;
                default:
                    SetJoinRequestState(joinKey, JoinRequestStateKind.None);
                    _status = string.IsNullOrWhiteSpace(result.Message)
                        ? "Join request failed."
                        : result.Message;
                    break;
            }
        }
        catch (Exception ex)
        {
            _log.Warn($"EOS_JOIN_REQUEST_FAILED host={entry.HostName} error={ex.Message}");
            _status = $"Join request failed: {ex.Message}";
        }
        finally
        {
            _joining = false;
        }
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

    private async Task RefreshOnlineSessionsAsync()
    {
        if (_onlineRefreshInProgress)
        {
            _onlineRefreshQueued = true;
            return;
        }

        _onlineRefreshInProgress = true;
        _isRefreshing = true;
        try
        {
            var eos = EnsureEosClient();
            var snapshot = EosRuntimeStatus.Evaluate(eos);
            if (snapshot.Reason != EosRuntimeReason.Ready || eos == null)
            {
                _onlineRefreshFailures++;
                _nextOnlineRefreshUtc = DateTime.UtcNow.AddSeconds(_onlineRefreshFailures >= 3 ? RefreshBackoffSeconds : DefaultListRefreshSeconds);
                if (!_joining && _onlineRefreshFailures >= 3)
                    _status = snapshot.StatusText;
                return;
            }

            var invitedHostIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                var inviteResult = await _onlineGate.GetMyWorldInvitesAsync().ConfigureAwait(false);
                if (inviteResult.Ok)
                {
                    for (var i = 0; i < inviteResult.Incoming.Count; i++)
                    {
                        var invite = inviteResult.Incoming[i];
                        var senderId = (invite.SenderProductUserId ?? string.Empty).Trim();
                        var status = (invite.Status ?? string.Empty).Trim();
                        if (string.IsNullOrWhiteSpace(senderId))
                            continue;
                        if (string.Equals(status, "pending", StringComparison.OrdinalIgnoreCase)
                            || string.Equals(status, "accepted", StringComparison.OrdinalIgnoreCase))
                        {
                            invitedHostIds.Add(senderId);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _log.Warn($"WORLD_INVITE_REFRESH_FAILED error={ex.Message}");
            }

            var friendIds = _profile.Friends
                .Select(f => (f.UserId ?? string.Empty).Trim())
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            IReadOnlyList<EosHostedLobbyEntry> lobbies = await eos.FindHostedLobbiesAsync().ConfigureAwait(false);
            _log.Info($"EOS_LOBBY_SEARCH_RAW count={lobbies.Count} filter={_activeFilter}");
            var refreshedOnlineSessions = new List<SessionEntry>(lobbies.Count);

            for (var i = 0; i < lobbies.Count; i++)
            {
                var lobby = lobbies[i];
                var hostId = (lobby.HostProductUserId ?? string.Empty).Trim();
                if (string.IsNullOrWhiteSpace(hostId))
                    continue;

                var isActive = lobby.IsHosting && lobby.IsInWorld;
                if (!isActive)
                    continue;
                if ((DateTime.UtcNow - lobby.LastHeartbeatUtc).TotalSeconds > HostedLobbyHeartbeatGraceSeconds)
                    continue;

                var hostName = await ResolveHostDisplayNameAsync(hostId, lobby.HostUsername).ConfigureAwait(false);

                var worldName = NormalizeLobbyWorldName(lobby.WorldName, hostName);
                worldName = LimitDisplayText(worldName, MaxWorldNameChars);

                var mode = NormalizeModeLabel(lobby.GameMode);
                var hostPictureUrl = ResolveCachedHostPictureUrl(hostId, hostName);

                refreshedOnlineSessions.Add(new SessionEntry
                {
                    Type = SessionType.Online,
                    Title = worldName,
                    HostName = hostName,
                    Status = "Hosting",
                    FriendUserId = hostId,
                    JoinTarget = hostId,
                    LobbyId = (lobby.LobbyId ?? string.Empty).Trim(),
                    WorldName = worldName,
                    GameMode = mode,
                    HostPictureUrl = hostPictureUrl,
                    Cheats = lobby.Cheats,
                    PlayerCount = Math.Max(1, lobby.PlayerCount),
                    MaxPlayers = Math.Max(Math.Max(1, lobby.PlayerCount), lobby.MaxPlayers),
                    IsFriendHost = friendIds.Contains(hostId),
                    HasInvite = invitedHostIds.Contains(hostId),
                    IsLocalUserWhitelisted = IsLocalProfileWhitelistedByLobby(lobby.WhitelistNames),
                    WorldId = (lobby.WorldId ?? string.Empty).Trim(),
                    LastSeenUtc = DateTime.UtcNow
                });

                if (!string.IsNullOrWhiteSpace(hostPictureUrl))
                    QueueHostAvatarLoad(hostId, hostPictureUrl);
            }

            var dedupedSessions = refreshedOnlineSessions
                .GroupBy(s => BuildOnlineHostKey(s), StringComparer.OrdinalIgnoreCase)
                .Select(g => g
                    .OrderByDescending(ScoreSessionCompleteness)
                    .OrderByDescending(s => s.PlayerCount)
                    .ThenByDescending(s => s.MaxPlayers)
                    .ThenBy(s => s.LobbyId, StringComparer.OrdinalIgnoreCase)
                    .First())
                .ToList();

            var mergedSessions = new Dictionary<string, SessionEntry>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < dedupedSessions.Count; i++)
            {
                var session = dedupedSessions[i];
                mergedSessions[BuildOnlineHostKey(session)] = session;
            }

            dedupedSessions = mergedSessions.Values
                .OrderBy(s => s.HostName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(s => s.WorldName, StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (dedupedSessions.Count == 0 && _onlineSessions.Count > 0)
            {
                _invitedHostIds.Clear();
                foreach (var invitedHostId in invitedHostIds)
                    _invitedHostIds.Add(invitedHostId);
                _onlineRefreshFailures = 0;
                _nextOnlineRefreshUtc = DateTime.UtcNow.AddSeconds(DefaultListRefreshSeconds);
                _sessionRefreshFailures = 0;
                _emptyOnlineRefreshStreak++;
                _log.Info($"EOS_LOBBY_SEARCH_OK count=0 emptyStreak={_emptyOnlineRefreshStreak}");
                if (_emptyOnlineRefreshStreak < 2)
                {
                    PreserveCachedOnlineSessions(invitedHostIds, friendIds, null);
                }
                else
                {
                    _onlineSessions.Clear();
                    if (!_joining && !string.IsNullOrWhiteSpace(_status) && _status.Contains("Refreshing", StringComparison.OrdinalIgnoreCase))
                        _status = string.Empty;
                    RefreshSessions();
                }
                TryAutoJoinPreferredInvite();
                return;
            }

            _emptyOnlineRefreshStreak = 0;
            _onlineSessions = dedupedSessions;
            _invitedHostIds.Clear();
            foreach (var invitedHostId in invitedHostIds)
                _invitedHostIds.Add(invitedHostId);
            _onlineRefreshFailures = 0;
            _nextOnlineRefreshUtc = DateTime.UtcNow.AddSeconds(DefaultListRefreshSeconds);
            _sessionRefreshFailures = 0;
            _log.Info($"EOS_LOBBY_SEARCH_OK count={dedupedSessions.Count} raw={refreshedOnlineSessions.Count}");
            if (!_joining && !string.IsNullOrWhiteSpace(_status) && _status.Contains("Refreshing", StringComparison.OrdinalIgnoreCase))
                _status = string.Empty;
            RefreshSessions();
            TryAutoJoinPreferredInvite();
        }
        catch (Exception ex)
        {
            _onlineRefreshFailures++;
            _sessionRefreshFailures = Math.Max(_sessionRefreshFailures, _onlineRefreshFailures);
            _nextOnlineRefreshUtc = DateTime.UtcNow.AddSeconds(_onlineRefreshFailures >= 3 ? RefreshBackoffSeconds : DefaultListRefreshSeconds);
            _log.Warn($"EOS_LOBBY_SEARCH_FAILED error={ex.Message}");
            var friendIds = _profile.Friends
                .Select(f => (f.UserId ?? string.Empty).Trim())
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            PreserveCachedOnlineSessions(
                new HashSet<string>(_invitedHostIds, StringComparer.OrdinalIgnoreCase),
                friendIds,
                LooksLikeRateLimitFailure(ex.Message)
                    ? "Online refresh hit rate limits. Showing cached sessions."
                    : "Online refresh failed. Showing cached sessions.");
            if (!_joining && _onlineRefreshFailures >= 3 && string.IsNullOrWhiteSpace(_status))
                _status = "Online refresh failed. Retrying...";
        }
        finally
        {
            _onlineRefreshInProgress = false;
            _isRefreshing = false;
            if (_onlineRefreshQueued)
            {
                _onlineRefreshQueued = false;
                _nextOnlineRefreshUtc = DateTime.MinValue;
                _ = RefreshOnlineSessionsAsync();
            }
        }
    }

    private void RefreshSessions()
    {
        var previousSelectionKey = _selectedIndex >= 0 && _selectedIndex < _sessions.Count
            ? BuildSessionKey(_sessions[_selectedIndex])
            : string.Empty;

        var newSessions = new List<SessionEntry>();

        if (_activeFilter == BrowserFilter.Lan)
        {
            var lan = _lanDiscovery.GetServers();
            if (lan.Count != _lastLoggedLanDiscoveryCount)
            {
                _lastLoggedLanDiscoveryCount = lan.Count;
                _log.Info($"LAN_DISCOVERY_FOUND n={lan.Count}");
            }
            var dedupedLan = new Dictionary<string, SessionEntry>(StringComparer.OrdinalIgnoreCase);
            foreach (var s in lan)
            {
                var joinTarget = DescribeEndpoint(s);
                var worldName = (s.ServerName ?? string.Empty).Trim();
                if (string.IsNullOrWhiteSpace(worldName))
                    worldName = "LAN WORLD";
                worldName = LimitDisplayText(worldName, MaxWorldNameChars);
                var mode = NormalizeModeLabel(s.GameMode);
                var dedupeKey = string.IsNullOrWhiteSpace(joinTarget)
                    ? $"{(s.Host ?? string.Empty).Trim()}|{worldName}|{mode}"
                    : joinTarget;

                var lanSession = new SessionEntry
                {
                    Type = SessionType.Lan,
                    Title = worldName,
                    HostName = "LAN HOST",
                    Status = "Online",
                    FriendUserId = joinTarget,
                    JoinTarget = joinTarget,
                    WorldName = worldName,
                    GameMode = mode,
                    Cheats = false,
                    PlayerCount = 1,
                    MaxPlayers = 8
                };

                if (!dedupedLan.TryGetValue(dedupeKey, out var existing) || ScoreSessionCompleteness(lanSession) > ScoreSessionCompleteness(existing))
                    dedupedLan[dedupeKey] = lanSession;
            }

            newSessions.AddRange(dedupedLan.Values);
        }
        else if (_activeFilter == BrowserFilter.Online)
        {
            newSessions.AddRange(_onlineSessions);
        }

        // 3. Servers (Placeholder)
        // (Empty for now)

        _sessions = newSessions
            .OrderBy(s => s.HostName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(s => s.WorldName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        CleanupJoinRequestStateForVisibleSessions();

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

        _isRefreshing = _activeFilter != BrowserFilter.Lan && _onlineRefreshInProgress;
    }

    private void UpdateLanDiscovery(GameTime gameTime)
    {
        if (_activeFilter != BrowserFilter.Lan)
            return;

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
        var metaPath = Paths.GetWorldMetaPath(worldDir);
        var meta = WorldMeta.CreateFlat(info.WorldName, info.GameMode, info.Width, info.Height, info.Depth, info.Seed);
        meta.PlayerCollision = info.PlayerCollision;
        meta.Player.PlayerCollision = info.PlayerCollision;
        meta.EnableMultipleHomes = info.EnableMultipleHomes;
        meta.MaxHomesPerPlayer = Math.Clamp(info.MaxHomesPerPlayer, 1, 32);
        meta.Gameplay.SaveInventory = info.SaveInventory;
        meta.Gameplay.SharedSoulAccess = info.SharedSoulAccess;
        meta.Gameplay.EnablePvp = info.PvpEnabled;
        meta.Gameplay.DeathType = string.IsNullOrWhiteSpace(info.DeathType) ? GameplaySettings.DefaultDeathType : info.DeathType;
        meta.TimeCycleEnabled = info.TimeCycleEnabled;
        meta.WeatherCycleEnabled = info.WeatherCycleEnabled;
        meta.TimeOfDayTicks = WorldMeta.CanonicalTimeTicks(info.TimeOfDayTicks);
        meta.WeatherState = WorldMeta.CanonicalWeatherState(info.WeatherState);
        meta.Gameplay.EnableMultipleHomes = meta.EnableMultipleHomes;
        meta.Gameplay.MaxHomesPerPlayer = meta.MaxHomesPerPlayer;
        meta.Gameplay.SaveInventory = info.SaveInventory;
        meta.Gameplay.SharedSoulAccess = info.SharedSoulAccess;
        meta.Gameplay.DeathType = string.IsNullOrWhiteSpace(info.DeathType) ? GameplaySettings.DefaultDeathType : info.DeathType;
        meta.Gameplay.TimeCycleEnabled = meta.TimeCycleEnabled;
        meta.Gameplay.WeatherCycleEnabled = meta.WeatherCycleEnabled;
        meta.Gameplay.TimeOfDayTicks = meta.TimeOfDayTicks;
        meta.Gameplay.WeatherState = meta.WeatherState;
        meta.WorldId = JoinedWorldCache.ResolveWorldId(info);
        meta.WorldGeneration.WorldType = WorldMeta.CanonicalWorldType(info.WorldType);
        meta.Generator = WorldMeta.CanonicalGeneratorForWorldType(meta.WorldGeneration.WorldType);
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
        foreach (var cached in _previewTextures.Values)
            cached.Texture.Dispose();
        _previewTextures.Clear();
        foreach (var avatar in _hostAvatarVisuals.Values)
            avatar.Texture?.Dispose();
        _hostAvatarVisuals.Clear();
        _hostProfileHydrationTasks.Clear();
        _previewLoadFailures.Clear();
        _webTextureLoader.Dispose();
    }

    private bool CanJoinDirect(SessionEntry entry)
    {
        if (entry.Type != SessionType.Online)
            return true;

        return entry.HasInvite || entry.IsLocalUserWhitelisted;
    }

    private bool IsLocalProfileWhitelistedByLobby(string whitelistNames)
    {
        var localName = (_profile?.GetDisplayUsername() ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(localName) || string.IsNullOrWhiteSpace(whitelistNames))
            return false;

        var tokens = whitelistNames
            .Split(new[] { '|', ',', ';', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        for (var i = 0; i < tokens.Length; i++)
        {
            if (string.Equals(tokens[i], localName, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private void TryAutoJoinPreferredInvite()
    {
        if (!_autoJoinPreferredInvitePending || _joining || string.IsNullOrWhiteSpace(_preferredInviteSenderId))
            return;

        var index = _sessions.FindIndex(s =>
            s.Type == SessionType.Online
            && string.Equals((s.FriendUserId ?? string.Empty).Trim(), _preferredInviteSenderId, StringComparison.OrdinalIgnoreCase)
            && CanJoinDirect(s));
        if (index < 0)
            return;

        _selectedIndex = index;
        _autoJoinPreferredInvitePending = false;
        OnJoinClicked();
    }

    private void DrawWorldPreviewTile(SpriteBatch sb, SessionEntry entry, Rectangle rect)
    {
        var preview = GetPreviewTexture(entry.WorldId);
        if (preview != null)
        {
            sb.Draw(preview, rect, Color.White);
            DrawBorder(sb, rect, entry.IsFriendHost ? new Color(120, 220, 120) : new Color(220, 180, 80));
            return;
        }

        var iconColor = entry.Type switch
        {
            SessionType.Lan => Color.Cyan,
            SessionType.Online when entry.HasInvite => new Color(120, 220, 255),
            SessionType.Online when entry.IsFriendHost => Color.LimeGreen,
            SessionType.Online => Color.Orange,
            _ => Color.White
        };
        sb.Draw(_pixel, rect, iconColor * 0.35f);
        DrawBorder(sb, rect, iconColor);
    }

    private Texture2D? GetPreviewTexture(string worldId)
    {
        var normalizedWorldId = (worldId ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(normalizedWorldId))
            return null;

        var worldPath = Path.Combine(Paths.MultiplayerWorldsDir, "Joined", $"world_{normalizedWorldId}");
        var previewPath = WorldPreviewGenerator.GetPreviewPath(worldPath);
        if (!File.Exists(previewPath))
            return null;

        var stampUtc = File.GetLastWriteTimeUtc(previewPath);
        if (_previewTextures.TryGetValue(previewPath, out var cached))
        {
            if (cached.StampUtc == stampUtc)
                return cached.Texture;

            cached.Texture.Dispose();
            _previewTextures.Remove(previewPath);
        }

        try
        {
            using var fs = new FileStream(previewPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var texture = Texture2D.FromStream(_graphics.GraphicsDevice, fs);
            var entry = new PreviewTextureCacheEntry(texture, stampUtc);
            _previewTextures[previewPath] = entry;
            _previewLoadFailures.Remove(previewPath);
            return entry.Texture;
        }
        catch (Exception ex)
        {
            if (_previewLoadFailures.Add(previewPath))
                _log.Warn($"Failed to load multiplayer preview '{previewPath}': {ex.Message}");
            return null;
        }
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

    private static string BuildOnlineHostKey(SessionEntry entry)
    {
        var hostId = (entry.FriendUserId ?? string.Empty).Trim();
        if (!string.IsNullOrWhiteSpace(hostId))
            return hostId;

        var joinTarget = (entry.JoinTarget ?? string.Empty).Trim();
        if (!string.IsNullOrWhiteSpace(joinTarget))
            return joinTarget;

        return BuildSessionKey(entry);
    }

    private static int ScoreSessionCompleteness(SessionEntry session)
    {
        var score = 0;
        if (!string.IsNullOrWhiteSpace(session.LobbyId))
            score += 4;
        if (!string.IsNullOrWhiteSpace(session.WorldName) && !string.Equals(session.WorldName, "HOSTED WORLD", StringComparison.OrdinalIgnoreCase))
            score += 8;
        if (!string.IsNullOrWhiteSpace(session.GameMode) && !string.Equals(session.GameMode, "UNKNOWN", StringComparison.OrdinalIgnoreCase))
            score += 4;
        if (!string.IsNullOrWhiteSpace(session.HostName) && !LooksLikeIdentityToken(session.HostName))
            score += 3;
        if (session.PlayerCount > 0)
            score += 2;
        if (session.MaxPlayers > 0)
            score += 1;
        return score;
    }

    private async Task<string> ResolveHostDisplayNameAsync(string hostProductUserId, string? lobbyHostUsername)
    {
        var hostId = (hostProductUserId ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(hostId))
            return "PLAYER";

        var fromLobby = (lobbyHostUsername ?? string.Empty).Trim();
        if (!LooksLikeIdentityToken(fromLobby))
        {
            _hostDisplayNameCache[hostId] = fromLobby;
            return fromLobby;
        }

        if (_hostDisplayNameCache.TryGetValue(hostId, out var cached) && !LooksLikeIdentityToken(cached))
            return cached;

        var localPuid = (EosClientProvider.Current?.LocalProductUserId ?? string.Empty).Trim();
        var localName = (_profile.GetDisplayUsername() ?? string.Empty).Trim();
        if (!string.IsNullOrWhiteSpace(localName) && string.Equals(localPuid, hostId, StringComparison.OrdinalIgnoreCase))
        {
            _hostDisplayNameCache[hostId] = localName;
            return localName;
        }

        for (var i = 0; i < _profile.Friends.Count; i++)
        {
            var friend = _profile.Friends[i];
            var friendId = (friend.UserId ?? string.Empty).Trim();
            if (!string.Equals(friendId, hostId, StringComparison.OrdinalIgnoreCase))
                continue;

            var label = (friend.Label ?? string.Empty).Trim();
            if (!LooksLikeIdentityToken(label))
            {
                _hostDisplayNameCache[hostId] = label;
                return label;
            }

            var displayName = (friend.LastKnownDisplayName ?? string.Empty).Trim();
            if (!LooksLikeIdentityToken(displayName))
            {
                _hostDisplayNameCache[hostId] = displayName;
                return displayName;
            }
        }

        try
        {
            var resolved = await _onlineGate.ResolveIdentityAsync(hostId).ConfigureAwait(false);
            var username = (resolved.User?.Username ?? string.Empty).Trim();
            if (resolved.Found && !LooksLikeIdentityToken(username))
            {
                _hostDisplayNameCache[hostId] = username;
                return username;
            }
        }
        catch (Exception ex)
        {
            _log.Warn($"Failed to resolve lobby host username for {PlayerProfile.ShortId(hostId)}: {ex.Message}");
        }

        return "PLAYER";
    }

    private string ResolveCachedHostPictureUrl(string hostProductUserId, string hostName)
    {
        var hostId = (hostProductUserId ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(hostId))
            return string.Empty;

        for (var i = 0; i < _profile.Friends.Count; i++)
        {
            var friend = _profile.Friends[i];
            if (!string.Equals((friend.UserId ?? string.Empty).Trim(), hostId, StringComparison.OrdinalIgnoreCase))
                continue;

            if (!string.IsNullOrWhiteSpace(friend.PictureUrl))
                return friend.PictureUrl.Trim();
        }

        return string.Empty;
    }

    private void EnsureHostAvatarHydration()
    {
        for (var i = 0; i < _onlineSessions.Count; i++)
        {
            var session = _onlineSessions[i];
            if (session.Type != SessionType.Online)
                continue;

            var hostId = (session.FriendUserId ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(hostId))
                continue;

            if (!string.IsNullOrWhiteSpace(session.HostPictureUrl))
            {
                QueueHostAvatarLoad(hostId, session.HostPictureUrl);
                continue;
            }

            if (_hostProfileHydrationTasks.ContainsKey(hostId))
                continue;

            var hostName = (session.HostName ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(hostName) || LooksLikeIdentityToken(hostName))
                continue;

            _hostProfileHydrationTasks[hostId] = HydrateOnlineHostProfileAsync(hostId, hostName);
        }

        if (!string.IsNullOrWhiteSpace(_preferredInviteSenderId) && !string.IsNullOrWhiteSpace(_preferredInviteSenderPictureUrl))
            QueueHostAvatarLoad(_preferredInviteSenderId, _preferredInviteSenderPictureUrl);
    }

    private async Task<GateIdentityUser?> HydrateOnlineHostProfileAsync(string hostId, string hostName)
    {
        try
        {
            var search = await _onlineGate.SearchVeilnetUsersAsync(hostName, 4).ConfigureAwait(false);
            if (search.Ok)
            {
                for (var i = 0; i < search.Users.Count; i++)
                {
                    var user = search.Users[i];
                    var username = (user.Username ?? string.Empty).Trim();
                    var displayName = (user.DisplayName ?? string.Empty).Trim();
                    if (string.Equals(username, hostName, StringComparison.OrdinalIgnoreCase)
                        || string.Equals(displayName, hostName, StringComparison.OrdinalIgnoreCase))
                    {
                        return user;
                    }
                }
            }

            var lookup = await _onlineGate.LookupVeilnetUserAsync(hostName).ConfigureAwait(false);
            return lookup.Ok ? lookup.User : null;
        }
        catch (Exception ex)
        {
            _log.Warn($"Failed to hydrate host profile for {PlayerProfile.ShortId(hostId)}: {ex.Message}");
            return null;
        }
    }

    private void ProcessCompletedHostProfileHydration()
    {
        if (_hostProfileHydrationTasks.Count == 0)
            return;

        var completed = new List<string>();
        foreach (var pair in _hostProfileHydrationTasks)
        {
            if (!pair.Value.IsCompleted)
                continue;

            completed.Add(pair.Key);
            if (pair.Value.Status != TaskStatus.RanToCompletion || pair.Value.Result == null)
                continue;

            var user = pair.Value.Result;
            if (string.IsNullOrWhiteSpace(user.PictureUrl))
                continue;

            var hostId = pair.Key;
            var pictureUrl = user.PictureUrl.Trim();
            for (var i = 0; i < _onlineSessions.Count; i++)
            {
                if (string.Equals((_onlineSessions[i].FriendUserId ?? string.Empty).Trim(), hostId, StringComparison.OrdinalIgnoreCase))
                    _onlineSessions[i].HostPictureUrl = pictureUrl;
            }

            QueueHostAvatarLoad(hostId, pictureUrl);
        }

        for (var i = 0; i < completed.Count; i++)
            _hostProfileHydrationTasks.Remove(completed[i]);
    }

    private void QueueHostAvatarLoad(string hostId, string pictureUrl)
    {
        hostId = (hostId ?? string.Empty).Trim();
        pictureUrl = (pictureUrl ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(hostId) || string.IsNullOrWhiteSpace(pictureUrl))
            return;

        if (!_hostAvatarVisuals.TryGetValue(hostId, out var state))
        {
            state = new AvatarVisualState();
            _hostAvatarVisuals[hostId] = state;
        }

        if (!string.Equals(state.SourceUrl, pictureUrl, StringComparison.OrdinalIgnoreCase))
        {
            state.Texture?.Dispose();
            state.Texture = null;
            state.BytesTask = null;
            state.SourceUrl = pictureUrl;
        }

        if (state.Texture == null && state.BytesTask == null)
            state.BytesTask = _webTextureLoader.DownloadImageBytesAsync(pictureUrl);
    }

    private void ProcessCompletedHostAvatarLoads()
    {
        if (_graphics?.GraphicsDevice == null)
            return;

        foreach (var pair in _hostAvatarVisuals)
        {
            var state = pair.Value;
            if (state.BytesTask == null || !state.BytesTask.IsCompleted)
                continue;

            if (state.BytesTask.Status == TaskStatus.RanToCompletion)
            {
                var bytes = state.BytesTask.Result;
                if (bytes is { Length: > 0 })
                    state.Texture = CreateCircularAvatarTextureFromBytes(bytes);
            }

            state.BytesTask = null;
        }
    }

    private bool TryGetHostAvatarTexture(SessionEntry entry, out Texture2D? texture)
    {
        texture = null;
        var hostId = (entry.FriendUserId ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(hostId))
            return false;

        if (_hostAvatarVisuals.TryGetValue(hostId, out var state) && state.Texture is { IsDisposed: false })
        {
            texture = state.Texture;
            return true;
        }

        if (!string.IsNullOrWhiteSpace(entry.HostPictureUrl))
            QueueHostAvatarLoad(hostId, entry.HostPictureUrl);
        return false;
    }

    private void DrawHostAvatarBadge(SpriteBatch sb, SessionEntry entry, Rectangle rect)
    {
        if (TryGetHostAvatarTexture(entry, out var texture))
            sb.Draw(texture!, rect, Color.White);
        else
            DrawCircularBadge(sb, rect, entry.HostName);
        DrawBorder(sb, rect, Color.White);
    }

    private Texture2D? CreateCircularAvatarTextureFromBytes(byte[] imageBytes)
    {
        try
        {
            using var stream = new MemoryStream(imageBytes, writable: false);
            var texture = Texture2D.FromStream(_graphics.GraphicsDevice, stream);
            var colorData = new Color[texture.Width * texture.Height];
            texture.GetData(colorData);

            var masked = new Texture2D(_graphics.GraphicsDevice, texture.Width, texture.Height);
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

    private void DrawCircularBadge(SpriteBatch sb, Rectangle rect, string seed)
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

                sb.Draw(_pixel, new Rectangle(x, y, 1, 1), baseColor);
            }
        }
    }

    private static Color ResolveBadgeColor(string seed)
    {
        var text = string.IsNullOrWhiteSpace(seed) ? "host" : seed.Trim().ToLowerInvariant();
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

    private string GetOutboundJoinDisplayName()
    {
        var reserved = (_reservedUsername ?? string.Empty).Trim();
        if (!LooksLikeIdentityToken(reserved))
            return reserved;

        var envName = (Environment.GetEnvironmentVariable("LV_VEILNET_USERNAME") ?? string.Empty).Trim();
        if (!LooksLikeIdentityToken(envName))
            return envName;

        var profileName = (_profile.GetDisplayUsername() ?? string.Empty).Trim();
        if (!LooksLikeIdentityToken(profileName))
            return profileName;

        return "PLAYER";
    }

    private static string NormalizeModeLabel(string? mode)
    {
        var normalized = (mode ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(normalized) || string.Equals(normalized, "Unknown", StringComparison.OrdinalIgnoreCase))
            return "ARTIFICER";
        return normalized.ToUpperInvariant();
    }

    private static string NormalizeLobbyWorldName(string? worldName, string hostName)
    {
        var normalized = (worldName ?? string.Empty).Trim();
        if (LooksLikeIdentityToken(normalized)
            || string.Equals(normalized, "WORLD", StringComparison.OrdinalIgnoreCase)
            || string.Equals(normalized, "HOSTED WORLD", StringComparison.OrdinalIgnoreCase))
        {
            normalized = string.Empty;
        }

        if (string.IsNullOrWhiteSpace(normalized) && !LooksLikeIdentityToken(hostName))
            normalized = $"{hostName}'s World";

        if (string.IsNullOrWhiteSpace(normalized))
            normalized = "WORLD";

        return normalized;
    }

    private static bool LooksLikeIdentityToken(string value)
    {
        var text = (value ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(text))
            return true;

        if (string.Equals(text, "UNKNOWN", StringComparison.OrdinalIgnoreCase)
            || string.Equals(text, "PLAYER", StringComparison.OrdinalIgnoreCase)
            || string.Equals(text, "HOST", StringComparison.OrdinalIgnoreCase))
            return true;

        if (Guid.TryParse(text, out _))
            return true;

        var compact = text.Replace("-", string.Empty);
        if (compact.Length >= 16 && compact.All(Uri.IsHexDigit))
            return true;

        if (text.Contains("...", StringComparison.Ordinal))
            return true;

        if (text.StartsWith("PLAYER-", StringComparison.OrdinalIgnoreCase))
            return true;

        return false;
    }

    private static string LimitDisplayText(string? value, int maxChars)
    {
        var text = (value ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(text) || maxChars <= 0)
            return string.Empty;
        if (text.Length <= maxChars)
            return text;
        if (maxChars <= 3)
            return text.Substring(0, maxChars);
        return text.Substring(0, maxChars - 3) + "...";
    }

    private string BuildJoinStateKey(SessionEntry entry)
    {
        var hostId = (entry.FriendUserId ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(hostId))
            hostId = (entry.JoinTarget ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(hostId))
            return string.Empty;

        if (entry.Type != SessionType.Online)
            return hostId;

        var lobbyId = (entry.LobbyId ?? string.Empty).Trim();
        var worldName = (entry.WorldName ?? string.Empty).Trim().ToUpperInvariant();
        if (!string.IsNullOrWhiteSpace(lobbyId))
            return $"{hostId}|{lobbyId}";

        if (!string.IsNullOrWhiteSpace(worldName))
            return $"{hostId}|{worldName}";

        return hostId;
    }

    private JoinRequestStateKind GetJoinRequestState(SessionEntry entry)
    {
        var key = BuildJoinStateKey(entry);
        if (string.IsNullOrWhiteSpace(key))
            return JoinRequestStateKind.None;

        if (_joinRequestStates.TryGetValue(key, out var state))
            return state.State;
        return JoinRequestStateKind.None;
    }

    private bool CanRetryJoinRequest(SessionEntry entry)
    {
        var key = BuildJoinStateKey(entry);
        if (string.IsNullOrWhiteSpace(key))
            return true;
        if (!_joinRequestStates.TryGetValue(key, out var state))
            return true;
        return (DateTime.UtcNow - state.UpdatedUtc).TotalSeconds >= JoinRequestCooldownSeconds;
    }

    private void SetJoinRequestState(string key, JoinRequestStateKind state)
    {
        if (string.IsNullOrWhiteSpace(key))
            return;

        _joinRequestStates[key] = new JoinRequestState
        {
            State = state,
            UpdatedUtc = DateTime.UtcNow
        };
    }

    private void ClearJoinRequestState(string key)
    {
        if (string.IsNullOrWhiteSpace(key))
            return;

        _joinRequestStates.Remove(key);
    }

    private void CleanupJoinRequestStateForVisibleSessions()
    {
        if (_joinRequestStates.Count == 0)
            return;

        var visibleHostKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < _onlineSessions.Count; i++)
        {
            var key = BuildJoinStateKey(_onlineSessions[i]);
            if (!string.IsNullOrWhiteSpace(key))
                visibleHostKeys.Add(key);
        }

        var keysToDrop = _joinRequestStates.Keys
            .Where(k => !visibleHostKeys.Contains(k))
            .ToArray();

        for (var i = 0; i < keysToDrop.Length; i++)
            _joinRequestStates.Remove(keysToDrop[i]);
    }

    private void HandleScreenResume()
    {
        _joinRequestStates.Clear();
        _sessionRefreshFailures = 0;
        _onlineRefreshFailures = 0;
        _nextSessionRefreshUtc = DateTime.MinValue;
        _nextOnlineRefreshUtc = DateTime.MinValue;

        if (_activeFilter == BrowserFilter.Lan)
        {
            RefreshSessions();
            _status = "Refreshing LAN hosts...";
            return;
        }

        _status = "Refreshing online lobbies...";
        _ = RefreshOnlineSessionsAsync();
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

    private void DrawRefreshFeedback(SpriteBatch sb)
    {
    }

    private void DrawBorder(SpriteBatch sb, Rectangle r, Color color)
    {
        sb.Draw(_pixel, new Rectangle(r.X, r.Y, r.Width, 2), color);
        sb.Draw(_pixel, new Rectangle(r.X, r.Bottom - 2, r.Width, 2), color);
        sb.Draw(_pixel, new Rectangle(r.X, r.Y, 2, r.Height), color);
        sb.Draw(_pixel, new Rectangle(r.Right - 2, r.Y, 2, r.Height), color);
    }

    private void DrawCenteredContainedText(SpriteBatch sb, string text, Rectangle rect, Color color, double timer, int holdChars)
    {
        var visible = ResolveContainedText(text, rect.Width, timer, holdChars);
        if (string.IsNullOrWhiteSpace(visible))
            return;

        var size = _font.MeasureString(visible);
        var x = rect.X + Math.Max(0f, (rect.Width - size.X) / 2f);
        _font.DrawString(sb, visible, new Vector2(x, rect.Y), color);
    }

    private void DrawContainedText(SpriteBatch sb, string text, Rectangle rect, Color color, double timer, int holdChars)
    {
        var visible = ResolveContainedText(text, rect.Width, timer, holdChars);
        if (string.IsNullOrWhiteSpace(visible))
            return;

        _font.DrawString(sb, visible, new Vector2(rect.X, rect.Y), color);
    }

    private string ResolveContainedText(string text, int width, double timer, int holdChars)
    {
        text = (text ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(text) || width <= 0)
            return string.Empty;

        var charWidth = Math.Max(1f, _font.MeasureString("W").X);
        var maxChars = Math.Max(1, (int)(width / charWidth));
        if (text.Length <= maxChars)
            return text;

        var padded = text + "   " + text;
        var scrollable = text.Length + 3;
        var step = Math.Max(0, (int)Math.Floor(Math.Max(0d, timer - holdChars / 6d) * 2.25d) % scrollable);
        return padded.Substring(step, Math.Min(maxChars, padded.Length - step));
    }
}
