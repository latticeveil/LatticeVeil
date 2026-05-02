using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using LatticeVeilMonoGame.Core;
using LatticeVeilMonoGame.Online.Eos;
using LatticeVeilMonoGame.Online.Gate;
using LatticeVeilMonoGame.UI;

namespace LatticeVeilMonoGame.UI.Screens;

public sealed class ProfileScreen : IScreen
{
public enum ProfileScreenStartTab
    {
        Identity,
        Friends,
        Invites
    }

    public enum ProfileScreenFriendsMode
    {
        Friends,
        Requests,
        Blocked
    }

    private readonly MenuStack _menus;
    private readonly AssetLoader _assets;
    private readonly PixelFont _font;
    private readonly Texture2D _pixel;
    private readonly Logger _log;
    private readonly PlayerProfile _profile;
    private readonly global::Microsoft.Xna.Framework.GraphicsDeviceManager _graphics;
    private readonly EosClient? _eos;
    private readonly EosIdentityStore _identityStore;
    private readonly OnlineGateClient _gate;
    private readonly OnlineSocialStateService _socialState;
    private readonly VeilnetProfileClient _veilnetProfileClient;
    private readonly MemoryWebTextureLoader _webTextureLoader;
    private readonly Dictionary<string, AvatarVisualState> _socialAvatarVisuals = new(StringComparer.OrdinalIgnoreCase);

    private readonly Button _tabIdentityBtn;
    private readonly Button _tabFriendsBtn;
    private readonly Button _tabInvitesBtn;
    private readonly Button _friendsModeFriendsBtn;
    private readonly Button _friendsModeRequestsBtn;
    private readonly Button _friendsModeBlockedBtn;
    private readonly Button _addFriendBtn;
    private readonly Button _removeFriendBtn;
    private readonly Button _acceptRequestBtn;
    private readonly Button _denyRequestBtn;
    private readonly Button _blockUserBtn;
    private readonly Button _unblockUserBtn;
    private readonly Button _viewFriendsBtn;
    private readonly Button _refreshProfileBtn;
    private readonly Button _backBtn;

    private Texture2D? _bg;
    private Texture2D? _panel;

    private Rectangle _viewport;
    private Rectangle _panelRect;
    private Rectangle _tabIdentityRect;
    private Rectangle _tabFriendsRect;
    private Rectangle _tabInvitesRect;
    private Rectangle _usernameRect;
    private Rectangle _iconRect;
    private Rectangle _identityHeaderRect;
    private Rectangle _identityNameplateRect;
    private Rectangle _identityAvatarRect;
    private Rectangle _identityAboutRect;
    private Rectangle _identityStatusRect;
    private Rectangle _friendsModeFriendsRect;
    private Rectangle _friendsModeRequestsRect;
    private Rectangle _friendsModeBlockedRect;
    private Rectangle _friendsRect;
    private Rectangle _friendsListRect;
    private Rectangle _friendsViewerRect;
    private Rectangle _friendsViewerBannerRect;
    private Rectangle _friendsViewerAvatarRect;
    private Rectangle _friendsViewerBodyRect;

    private ProfileScreenStartTab _activeTab = ProfileScreenStartTab.Identity;
    private ProfileScreenFriendsMode _friendsListMode = ProfileScreenFriendsMode.Friends;
    private int _selectedFriend = -1;
    private int _selectedRequest = -1;
    private int _selectedOutgoingRequest = -1;
    private int _selectedInvite = -1;
    private int _selectedBlocked = -1;
    private bool _friendsSyncInProgress;
    private bool _friendsPresenceRefreshInProgress;
    private bool _friendsSeedAttempted;
    private bool _friendsEndpointUnavailable;
    private DateTime _nextFriendsSyncUtc = DateTime.MinValue;
    private DateTime _nextFriendsPresenceRefreshUtc = DateTime.MinValue;
    private readonly List<GateFriendRequest> _incomingRequests = new();
    private readonly List<GateFriendRequest> _outgoingRequests = new();
    private readonly List<GateIdentityUser> _blockedUsers = new();
    private readonly List<GateWorldInviteEntry> _worldInvites = new();
    private readonly List<Rectangle> _inviteAcceptActionRects = new();
    private readonly List<Rectangle> _inviteDeclineActionRects = new();

    private string _status = string.Empty;
    private DateTime _statusExpiryUtc = DateTime.MinValue;
    private Texture2D? _selectedSocialAvatarTexture;
    private Texture2D? _selectedSocialBannerTexture;
    private Task<byte[]?>? _selectedSocialAvatarBytesTask;
    private Task<byte[]?>? _selectedSocialBannerBytesTask;
    private string _selectedSocialAvatarUrl = string.Empty;
    private string _selectedSocialBannerUrl = string.Empty;
    private string _selectedSocialUserKey = string.Empty;
    private Task<GateFriendLookupResult>? _selectedFriendHydrationTask;
    private string _selectedFriendHydrationUserId = string.Empty;
    private float _profilePreviewScrollTimer;

    private Texture2D? _veilnetAvatarTexture;
    private Texture2D? _veilnetBannerTexture;
    private Texture2D? _veilnetAvatarRingTexture;
    private Texture2D? _veilnetAvatarPlaceholderTexture;
    private int _veilnetAvatarDecorSize;
    private Color _veilnetAvatarRingColor = Color.Transparent;
    private Color _veilnetAvatarPlaceholderColor = Color.Transparent;
    private string _veilnetUsername = string.Empty;
    private string _veilnetAboutMe = string.Empty;
    private string _veilnetPictureUrl = string.Empty;
    private string _veilnetBannerUrl = string.Empty;
    private string _veilnetThemeColorRaw = string.Empty;
    private string _veilnetTheme = string.Empty;
    private string _veilnetUpdatedAt = string.Empty;
    private string _veilnetProfileError = string.Empty;
    private Color _veilnetAccentColor = new(124, 92, 255);
    private Color _veilnetAccentTextColor = new(240, 242, 255);
    private DateTime _lastVeilnetProfileUtc = DateTime.MinValue;
    private bool _lastVeilnetSyncOk;
    private string _lastVeilnetSyncError = string.Empty;
    private DateTime _nextVeilnetProfileRefreshUtc = DateTime.MinValue;
    private string _lastVeilnetTokenSnapshot = string.Empty;
    private bool _veilnetProfileRefreshInProgress;
    private Task<VeilnetProfileRefreshPayload>? _veilnetProfileRefreshTask;
    private readonly TimeSpan _veilnetProfileRefreshInterval;
    private static readonly string LegacyVeilnetCacheDir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "LatticeVeil", "VeilnetCache");
    private VeilnetProfileCacheStore? _veilnetProfileCache;

    public ProfileScreen(MenuStack menus, AssetLoader assets, PixelFont font, Texture2D pixel, Logger log, PlayerProfile profile,
        global::Microsoft.Xna.Framework.GraphicsDeviceManager graphics, EosClient? eosClient,
        ProfileScreenStartTab startTab = ProfileScreenStartTab.Identity,
        ProfileScreenFriendsMode startFriendsMode = ProfileScreenFriendsMode.Friends)
    {
        _menus = menus;
        _assets = assets;
        _font = font;
        _pixel = pixel;
        _log = log;
        _profile = profile;
        _graphics = graphics;
        _eos = eosClient;
        _identityStore = EosIdentityStore.LoadOrCreate(_log);
        _gate = OnlineGateClient.GetOrCreate();
        _socialState = OnlineSocialStateService.GetOrCreate(_log);
        _veilnetProfileClient = new VeilnetProfileClient(_log);
        _webTextureLoader = new MemoryWebTextureLoader();
        _veilnetProfileRefreshInterval = ResolveProfileRefreshInterval();

        _activeTab = startTab;
        _friendsListMode = startFriendsMode;
        if (_activeTab == ProfileScreenStartTab.Invites)
            _friendsListMode = ProfileScreenFriendsMode.Requests;

        _tabIdentityBtn = new Button("PROFILE", () => _activeTab = ProfileScreenStartTab.Identity) { BoldText = true };
        _tabFriendsBtn = new Button("FRIENDS", () => _activeTab = ProfileScreenStartTab.Friends) { BoldText = true };
        _tabInvitesBtn = new Button("INVITES", () => _activeTab = ProfileScreenStartTab.Invites) { BoldText = true };
        _friendsModeFriendsBtn = new Button("FRIENDS", () => _friendsListMode = ProfileScreenFriendsMode.Friends) { BoldText = true };
        _friendsModeRequestsBtn = new Button("REQUESTS", () => _friendsListMode = ProfileScreenFriendsMode.Requests) { BoldText = true };
        _friendsModeBlockedBtn = new Button("BLOCKED", () => _friendsListMode = ProfileScreenFriendsMode.Blocked) { BoldText = true };
        _addFriendBtn = new Button("ADD FRIEND", OpenAddFriend) { BoldText = true };
        _removeFriendBtn = new Button("REMOVE FRIEND", () => _ = RemoveFriendAsync()) { BoldText = true };
        _acceptRequestBtn = new Button("ACCEPT", () => _ = HandlePrimaryActionAsync()) { BoldText = true };
        _denyRequestBtn = new Button("DENY", () => _ = HandleSecondaryActionAsync()) { BoldText = true };
        _blockUserBtn = new Button("BLOCK", () => _ = BlockSelectedUserAsync()) { BoldText = true };
        _unblockUserBtn = new Button("UNBLOCK", () => _ = UnblockSelectedUserAsync()) { BoldText = true };
        _viewFriendsBtn = new Button("VIEW FRIENDS", () => _activeTab = ProfileScreenStartTab.Friends) { BoldText = true };
        _refreshProfileBtn = new Button("REFRESH", () => QueueVeilnetProfileRefresh(force: true)) { BoldText = true };
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

        LoadCachedVeilnetProfile();
        SeedFromSocialSnapshot();

        _lastVeilnetTokenSnapshot = ResolveVeilnetToken();
        if (IsUsableToken(_lastVeilnetTokenSnapshot))
            QueueVeilnetProfileRefresh(force: true);
        else if (string.IsNullOrWhiteSpace(_veilnetUsername))
        {
            _veilnetProfileError = "Sign in via launcher to view your Veilnet profile.";
            _lastVeilnetSyncOk = false;
            _lastVeilnetSyncError = "missing_access_token";
        }
    }

    public void OnResize(Rectangle viewport)
    {
        _viewport = viewport;

        var panelW = Math.Min(1300, viewport.Width - 20);
        var panelH = Math.Min(700, viewport.Height - 30);
        var panelX = viewport.X + (viewport.Width - panelW) / 2;
        var panelY = viewport.Y + (viewport.Height - panelH) / 2;
        _panelRect = new Rectangle(panelX, panelY, panelW, panelH);

        // 1 inch inset (approx 96 pixels)
        var margin = 96;
        var contentRect = new Rectangle(_panelRect.X + margin, _panelRect.Y + margin, _panelRect.Width - margin * 2, _panelRect.Height - margin * 2);

        var tabY = contentRect.Y;
        var tabGap = 8;
        var tabW = (contentRect.Width - (tabGap * 2)) / 3;
        var tabH = _font.LineHeight + 12;
        _tabIdentityRect = new Rectangle(contentRect.X, tabY, tabW, tabH);
        _tabFriendsRect = new Rectangle(_tabIdentityRect.Right + tabGap, tabY, tabW, tabH);
        _tabInvitesRect = new Rectangle(_tabFriendsRect.Right + tabGap, tabY, tabW, tabH);
        _tabIdentityBtn.Bounds = _tabIdentityRect;
        _tabFriendsBtn.Bounds = _tabFriendsRect;
        _tabInvitesBtn.Bounds = _tabInvitesRect;

        var iconSize = 108;
        var iconGap = 16;
        _identityHeaderRect = new Rectangle(contentRect.X, _tabIdentityRect.Bottom + 14, contentRect.Width, Math.Min(230, Math.Max(160, contentRect.Height / 3)));
        _iconRect = new Rectangle(_identityHeaderRect.X + 14, _identityHeaderRect.Y + Math.Max(10, (_identityHeaderRect.Height - iconSize) / 2), iconSize, iconSize);
        _identityAvatarRect = _iconRect;
        var nameplateX = _iconRect.Right + iconGap;
        var nameplateWidth = Math.Max(220, _identityHeaderRect.Width - (nameplateX - _identityHeaderRect.X) - 20);
        _identityNameplateRect = new Rectangle(nameplateX, _identityHeaderRect.Y + 24, nameplateWidth, _font.LineHeight + 26);
        _usernameRect = _identityNameplateRect;
        _refreshProfileBtn.Bounds = new Rectangle(_identityNameplateRect.Right - 132, _identityNameplateRect.Bottom + 8, 132, Math.Max(34, _font.LineHeight + 8));
        var aboutTop = _identityHeaderRect.Bottom + 14;
        var bottomButtonsTop = contentRect.Bottom - 60;
        var infoHeight = Math.Max(120, bottomButtonsTop - aboutTop - 10);
        var half = Math.Max(56, infoHeight / 2 - 4);
        _identityAboutRect = new Rectangle(contentRect.X, aboutTop, contentRect.Width, half);
        _identityStatusRect = new Rectangle(contentRect.X, _identityAboutRect.Bottom + 8, contentRect.Width, Math.Max(56, infoHeight - half - 8));
        
        var modeY = _tabIdentityRect.Bottom + 12;
        var modeH = _font.LineHeight + 10;
        var modeGap = 8;
        var modeW = (contentRect.Width - (modeGap * 2)) / 3;
        _friendsModeFriendsRect = new Rectangle(contentRect.X, modeY, modeW, modeH);
        _friendsModeRequestsRect = new Rectangle(_friendsModeFriendsRect.Right + modeGap, modeY, modeW, modeH);
        _friendsModeBlockedRect = new Rectangle(_friendsModeRequestsRect.Right + modeGap, modeY, modeW, modeH);
        _friendsModeFriendsBtn.Bounds = _friendsModeFriendsRect;
        _friendsModeRequestsBtn.Bounds = _friendsModeRequestsRect;
        _friendsModeBlockedBtn.Bounds = _friendsModeBlockedRect;

        var friendsY = _friendsModeFriendsRect.Bottom + 12;
        var actionAreaH = 60;
        _friendsRect = new Rectangle(
            contentRect.X,
            friendsY,
            contentRect.Width,
            contentRect.Bottom - friendsY - actionAreaH - 12);
        var viewerGap = 14;
        var listWidth = Math.Max(320, (_friendsRect.Width * 43) / 100);
        _friendsListRect = new Rectangle(_friendsRect.X, _friendsRect.Y, listWidth, _friendsRect.Height);
        _friendsViewerRect = new Rectangle(_friendsListRect.Right + viewerGap, _friendsRect.Y, _friendsRect.Right - (_friendsListRect.Right + viewerGap), _friendsRect.Height);
        _friendsViewerBannerRect = new Rectangle(_friendsViewerRect.X + 8, _friendsViewerRect.Y + 8, Math.Max(80, _friendsViewerRect.Width - 16), Math.Min(150, Math.Max(92, _friendsViewerRect.Height / 3)));
        var viewerAvatarSize = Math.Min(96, Math.Max(52, _friendsViewerBannerRect.Height - 16));
        _friendsViewerAvatarRect = new Rectangle(_friendsViewerBannerRect.X + 10, _friendsViewerBannerRect.Bottom - (viewerAvatarSize / 2) - 8, viewerAvatarSize, viewerAvatarSize);
        _friendsViewerBodyRect = new Rectangle(_friendsViewerRect.X + 8, _friendsViewerBannerRect.Bottom + 10, Math.Max(80, _friendsViewerRect.Width - 16), Math.Max(72, _friendsViewerRect.Bottom - (_friendsViewerBannerRect.Bottom + 18)));

        var buttonY = contentRect.Bottom - actionAreaH;
        var gap = 8;
        var buttonH = Math.Max(44, _font.LineHeight * 2);
        
        // Identity view actions
        _viewFriendsBtn.Bounds = new Rectangle(contentRect.X, buttonY, contentRect.Width, buttonH);
        
        // Friends view actions
        var friendsButtonW = (contentRect.Width - gap * 2) / 3;
        _addFriendBtn.Bounds = new Rectangle(contentRect.X, buttonY, friendsButtonW, buttonH);
        _removeFriendBtn.Bounds = new Rectangle(_addFriendBtn.Bounds.Right + gap, buttonY, friendsButtonW, buttonH);
        _blockUserBtn.Bounds = new Rectangle(_removeFriendBtn.Bounds.Right + gap, buttonY, friendsButtonW, buttonH);
        
        _acceptRequestBtn.Bounds = _addFriendBtn.Bounds;
        _denyRequestBtn.Bounds = _removeFriendBtn.Bounds;
        _unblockUserBtn.Bounds = _addFriendBtn.Bounds;

        // Back button matches SingleplayerScreen position (outside the main panel, bottom-left of viewport)
        var backBtnMargin = 20;
        var backBtnBaseW = Math.Max(_backBtn.Texture?.Width ?? 0, 320);
        var backBtnBaseH = Math.Max(_backBtn.Texture?.Height ?? 0, (int)(backBtnBaseW * 0.28f));
        var backBtnScale = Math.Min(1f, Math.Min(240f / backBtnBaseW, 240f / backBtnBaseH));
        var backBtnW = Math.Max(1, (int)Math.Round(backBtnBaseW * backBtnScale));
        var backBtnH = Math.Max(1, (int)Math.Round(backBtnBaseH * backBtnScale));
        _backBtn.Bounds = new Rectangle(viewport.X + backBtnMargin, viewport.Bottom - backBtnMargin - backBtnH, backBtnW, backBtnH);
    }

    public void Update(GameTime gameTime, InputState input)
    {
        _profilePreviewScrollTimer += (float)gameTime.ElapsedGameTime.TotalSeconds;
        ProcessSelectedSocialImageLoads();
        ProcessSelectedFriendHydration();

        if (_statusExpiryUtc != DateTime.MinValue && DateTime.UtcNow >= _statusExpiryUtc)
        {
            _status = string.Empty;
            _statusExpiryUtc = DateTime.MinValue;
        }

        HandleVeilnetTokenChange();
        ProcessCompletedVeilnetProfileRefresh();

        if (input.IsNewKeyPress(Keys.Escape))
        {
            _menus.Pop();
            return;
        }

        _tabIdentityBtn.Update(input);
        _tabFriendsBtn.Update(input);
        _tabInvitesBtn.Update(input);
        _backBtn.Update(input);

        if (_activeTab == ProfileScreenStartTab.Identity)
        {
            _viewFriendsBtn.Update(input);
            _refreshProfileBtn.Update(input);
            if (!_veilnetProfileRefreshInProgress && DateTime.UtcNow >= _nextVeilnetProfileRefreshUtc)
                QueueVeilnetProfileRefresh(force: false);
        }
        else if (_activeTab == ProfileScreenStartTab.Friends)
        {
            _friendsModeFriendsBtn.Update(input);
            _friendsModeRequestsBtn.Update(input);
            _friendsModeBlockedBtn.Update(input);

            _addFriendBtn.Enabled = _friendsListMode == ProfileScreenFriendsMode.Friends;
            _removeFriendBtn.Enabled = _friendsListMode == ProfileScreenFriendsMode.Friends
                && _selectedFriend >= 0
                && _selectedFriend < _profile.Friends.Count;
            _blockUserBtn.Enabled = (_friendsListMode == ProfileScreenFriendsMode.Friends
                    && _selectedFriend >= 0
                    && _selectedFriend < _profile.Friends.Count)
                || (_friendsListMode == ProfileScreenFriendsMode.Requests
                    && _selectedRequest >= 0
                    && _selectedRequest < _incomingRequests.Count);
            var hasIncomingRequestSelection = _selectedRequest >= 0 && _selectedRequest < _incomingRequests.Count;
            var hasOutgoingRequestSelection = _selectedOutgoingRequest >= 0 && _selectedOutgoingRequest < _outgoingRequests.Count;
            _acceptRequestBtn.Enabled = _friendsListMode == ProfileScreenFriendsMode.Requests
                && (hasIncomingRequestSelection || hasOutgoingRequestSelection);
            _denyRequestBtn.Enabled = _friendsListMode == ProfileScreenFriendsMode.Requests
                && (hasIncomingRequestSelection || hasOutgoingRequestSelection);
            _acceptRequestBtn.Label = hasOutgoingRequestSelection ? "VIEW SENT" : "ACCEPT";
            _denyRequestBtn.Label = hasOutgoingRequestSelection ? "CANCEL" : "DENY";
            _unblockUserBtn.Enabled = _friendsListMode == ProfileScreenFriendsMode.Blocked
                && _selectedBlocked >= 0
                && _selectedBlocked < _blockedUsers.Count;

            _addFriendBtn.Update(input);
            _removeFriendBtn.Update(input);
            _blockUserBtn.Update(input);
            _acceptRequestBtn.Update(input);
            _denyRequestBtn.Update(input);
            _unblockUserBtn.Update(input);

            HandleListSelection(input);
            SeedFromSocialSnapshot();
            EnsureSocialAvatarLoads();
            EnsureSelectedFriendHydration();
            EnsureSelectedSocialViewerLoads();
            if (!_friendsSyncInProgress
                && (!_friendsEndpointUnavailable || DateTime.UtcNow >= _nextFriendsSyncUtc)
                && DateTime.UtcNow >= _nextFriendsSyncUtc)
                _ = SyncCanonicalFriendsAsync(seedFromLocal: !_friendsSeedAttempted);
            if (!_friendsPresenceRefreshInProgress && DateTime.UtcNow >= _nextFriendsPresenceRefreshUtc)
                _ = RefreshSavedFriendsPresenceAsync();
        }
        else
        {
            _socialState.MarkNotificationsSeen();
            HandleListSelection(input);
            SeedFromSocialSnapshot();
        }

        UpdateTabButtonStyles();
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

        if (_panel is not null) sb.Draw(_panel, _panelRect, Color.White);
        else sb.Draw(_pixel, _panelRect, new Color(0, 0, 0, 200));

        DrawTabs(sb);

        if (_activeTab == ProfileScreenStartTab.Identity)
            DrawIdentityTab(sb);
        else if (_activeTab == ProfileScreenStartTab.Friends)
            DrawFriendsTab(sb);
        else
            DrawInvitesTab(sb);

        if (!string.IsNullOrWhiteSpace(_status))
        {
            var statusPos = new Vector2(_panelRect.X + 18, _panelRect.Bottom - _font.LineHeight - 8);
            _font.DrawString(sb, _status, statusPos, Color.White);
        }

        if (_activeTab == ProfileScreenStartTab.Identity)
        {
            _viewFriendsBtn.Draw(sb, _pixel, _font);
            _refreshProfileBtn.Draw(sb, _pixel, _font);
        }
        else if (_activeTab == ProfileScreenStartTab.Friends)
        {
            _friendsModeFriendsBtn.Draw(sb, _pixel, _font);
            _friendsModeRequestsBtn.Draw(sb, _pixel, _font);
            _friendsModeBlockedBtn.Draw(sb, _pixel, _font);

            if (_friendsListMode == ProfileScreenFriendsMode.Friends)
            {
                _addFriendBtn.Draw(sb, _pixel, _font);
                _removeFriendBtn.Draw(sb, _pixel, _font);
                _blockUserBtn.Draw(sb, _pixel, _font);
            }
            else if (_friendsListMode == ProfileScreenFriendsMode.Requests)
            {
                _acceptRequestBtn.Draw(sb, _pixel, _font);
                _denyRequestBtn.Draw(sb, _pixel, _font);
                _blockUserBtn.Draw(sb, _pixel, _font);
            }
            else
            {
                _unblockUserBtn.Draw(sb, _pixel, _font);
            }
        }
        else
        {
        }

        _tabIdentityBtn.Draw(sb, _pixel, _font);
        _tabFriendsBtn.Draw(sb, _pixel, _font);
        _tabInvitesBtn.Draw(sb, _pixel, _font);
        _backBtn.Draw(sb, _pixel, _font);

        sb.End();
    }

    public void OnClose()
    {
        CancelVeilnetRefreshTask();
        _selectedFriendHydrationTask = null;
        foreach (var state in _socialAvatarVisuals.Values)
            state.Texture?.Dispose();
        _socialAvatarVisuals.Clear();
        ClearSelectedSocialViewerAssets();
        DisposeVeilnetTextures();
        DisposeAvatarDecorTextures();
        _webTextureLoader.Dispose();
        ClearLegacyVeilnetCacheDir();
    }

    private void DrawTabs(SpriteBatch sb)
    {
        UpdateTabButtonStyles();
    }

    private void DrawIdentityTab(SpriteBatch sb)
    {
        var accent = _veilnetAccentColor;
        var accentBorder = WithAlpha(BlendColors(accent, Color.White, 0.12f), 228);
        var sideBarColor = WithAlpha(BlendColors(accent, new Color(18, 22, 36), 0.24f), 212);
        var headerOverlay = new Color(8, 12, 22, 96);
        var nameplateFill = WithAlpha(DarkenColor(accent, 0.3f), 220);
        var nameplateBorder = WithAlpha(BlendColors(accent, Color.White, 0.28f), 245);
        var infoBorder = WithAlpha(BlendColors(accent, Color.White, 0.14f), 216);
        var infoTitle = BlendColors(_veilnetAccentTextColor, Color.White, 0.2f);

        if (_veilnetBannerTexture is { IsDisposed: false })
            DrawTextureCover(sb, _veilnetBannerTexture, _identityHeaderRect, Color.White);
        else
            DrawFallbackBanner(sb, _identityHeaderRect);

        // Keep banner visible; overlay only improves text readability.
        sb.Draw(_pixel, _identityHeaderRect, headerOverlay);
        DrawHeaderSideBars(sb, _identityHeaderRect, sideBarColor);
        DrawBorder(sb, _identityHeaderRect, accentBorder);

        var ringColor = WithAlpha(BlendColors(accent, Color.White, 0.2f), 235);
        var placeholderColor = WithAlpha(BlendColors(accent, new Color(18, 20, 30), 0.28f), 220);
        EnsureAvatarDecorTextures(_identityAvatarRect.Width, ringColor, placeholderColor);

        if (_veilnetAvatarTexture is { IsDisposed: false })
        {
            sb.Draw(_veilnetAvatarTexture, _identityAvatarRect, Color.White);
        }
        else
        {
            if (_veilnetAvatarPlaceholderTexture is { IsDisposed: false })
                sb.Draw(_veilnetAvatarPlaceholderTexture, _identityAvatarRect, Color.White);

            var placeholderGlyph = ResolveDisplayUsername();
            placeholderGlyph = string.IsNullOrWhiteSpace(placeholderGlyph) ? "?" : placeholderGlyph[..1].ToUpperInvariant();
            var glyphX = _identityAvatarRect.X + (_identityAvatarRect.Width / 2f) - (_font.MeasureString(placeholderGlyph).X / 2f);
            var glyphY = _identityAvatarRect.Y + (_identityAvatarRect.Height - _font.LineHeight) / 2f;
            _font.DrawString(sb, placeholderGlyph, new Vector2(glyphX, glyphY), new Color(232, 236, 248));
        }

        if (_veilnetAvatarRingTexture is { IsDisposed: false })
            sb.Draw(_veilnetAvatarRingTexture, _identityAvatarRect, Color.White);

        sb.Draw(_pixel, _identityNameplateRect, nameplateFill);
        DrawBorder(sb, _identityNameplateRect, nameplateBorder);
        var username = ResolveDisplayUsername();
        var usernamePos = new Vector2(_identityNameplateRect.X + 12, _identityNameplateRect.Y + (_identityNameplateRect.Height - _font.LineHeight) / 2f);
        DrawTextBold(sb, username, usernamePos, _veilnetAccentTextColor);
        _font.DrawString(
            sb,
            "VEILNET",
            new Vector2(_identityNameplateRect.Right - 110, _identityNameplateRect.Y + 8),
            WithAlpha(_veilnetAccentTextColor, 220));

        DrawIdentityInfoPanel(
            sb,
            _identityAboutRect,
            "ABOUT ME",
            string.IsNullOrWhiteSpace(_veilnetAboutMe) ? "No about me set." : _veilnetAboutMe,
            infoBorder,
            infoTitle);

        var statusLines = BuildIdentityStatusLines();
        DrawIdentityInfoPanel(sb, _identityStatusRect, "STATUS", statusLines, infoBorder, infoTitle);
    }

    private string BuildIdentityStatusLines()
    {
        var eosSnapshot = EosRuntimeStatus.Evaluate(_eos);
        var profileState = _veilnetProfileRefreshInProgress
            ? "Refreshing..."
            : string.IsNullOrWhiteSpace(_veilnetUsername)
                ? (string.IsNullOrWhiteSpace(_veilnetProfileError) ? "Waiting for launcher sign-in." : _veilnetProfileError)
                : _lastVeilnetSyncOk
                    ? "Synced"
                    : string.IsNullOrWhiteSpace(_veilnetProfileError)
                        ? "Using cached profile"
                        : $"Using cached profile ({_veilnetProfileError})";

        var friendsState = _friendsSyncInProgress
            ? "Refreshing..."
            : _friendsEndpointUnavailable
                ? "Service unavailable"
                : "Ready";

        return $"EOS: {eosSnapshot.StatusText}\nPROFILE: {profileState}\nFRIENDS: {friendsState}";
    }

    private void LoadCachedVeilnetProfile()
    {
        _veilnetProfileCache = VeilnetProfileCacheStore.Load(_log);
        if (_veilnetProfileCache != null)
        {
            _veilnetUsername = (_veilnetProfileCache.Username ?? string.Empty).Trim();
            _veilnetAboutMe = NormalizeMultiline(_veilnetProfileCache.AboutMe);
            _veilnetPictureUrl = (_veilnetProfileCache.PictureUrl ?? string.Empty).Trim();
            _veilnetBannerUrl = (_veilnetProfileCache.BannerUrl ?? string.Empty).Trim();
            _veilnetThemeColorRaw = (_veilnetProfileCache.ThemeColor ?? string.Empty).Trim();
            _veilnetTheme = (_veilnetProfileCache.Theme ?? string.Empty).Trim();
            _veilnetUpdatedAt = (_veilnetProfileCache.UpdatedAt ?? string.Empty).Trim();
            ApplyVeilnetTheme(_veilnetThemeColorRaw, _veilnetTheme);
            if (!string.IsNullOrWhiteSpace(_veilnetUsername))
                _veilnetProfileError = "Using cached profile.";
        }

        TryLoadCachedProfileImages();
    }

    private void SaveCachedVeilnetProfile()
    {
        _veilnetProfileCache ??= new VeilnetProfileCacheStore();
        _veilnetProfileCache.Username = _veilnetUsername;
        _veilnetProfileCache.AboutMe = _veilnetAboutMe;
        _veilnetProfileCache.PictureUrl = _veilnetPictureUrl;
        _veilnetProfileCache.BannerUrl = _veilnetBannerUrl;
        _veilnetProfileCache.ThemeColor = _veilnetThemeColorRaw;
        _veilnetProfileCache.Theme = _veilnetTheme;
        _veilnetProfileCache.UpdatedAt = _veilnetUpdatedAt;
        _veilnetProfileCache.Save(_log);
    }

    private void TryLoadCachedProfileImages()
    {
        if (_graphics?.GraphicsDevice == null)
            return;

        try
        {
            if (_veilnetAvatarTexture == null && File.Exists(Paths.VeilnetAvatarCachePath))
            {
                var avatarBytes = File.ReadAllBytes(Paths.VeilnetAvatarCachePath);
                var avatar = CreateCircularAvatarTextureFromBytes(avatarBytes);
                if (avatar != null)
                    _veilnetAvatarTexture = avatar;
            }

            if (_veilnetBannerTexture == null && File.Exists(Paths.VeilnetBannerCachePath))
            {
                var bannerBytes = File.ReadAllBytes(Paths.VeilnetBannerCachePath);
                var banner = _webTextureLoader.CreateTextureFromBytes(_graphics.GraphicsDevice, bannerBytes);
                if (banner != null)
                    _veilnetBannerTexture = banner;
            }
        }
        catch (Exception ex)
        {
            _log.Warn($"Failed to load cached Veilnet profile images: {ex.Message}");
        }
    }

    private void SaveCachedProfileImage(string path, byte[] bytes)
    {
        try
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(dir))
                Directory.CreateDirectory(dir);
            File.WriteAllBytes(path, bytes);
        }
        catch (Exception ex)
        {
            _log.Warn($"Failed to save cached profile image '{Path.GetFileName(path)}': {ex.Message}");
        }
    }

    private void DeleteCachedProfileImage(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (Exception ex)
        {
            _log.Warn($"Failed to clear cached profile image '{Path.GetFileName(path)}': {ex.Message}");
        }
    }

    private void HandleVeilnetTokenChange()
    {
        var token = ResolveVeilnetToken();
        if (string.Equals(token, _lastVeilnetTokenSnapshot, StringComparison.Ordinal))
            return;

        _lastVeilnetTokenSnapshot = token;
        if (!IsUsableToken(token))
        {
            CancelVeilnetRefreshTask();
            _veilnetProfileError = string.IsNullOrWhiteSpace(_veilnetUsername)
                ? "Sign in via launcher to view your Veilnet profile."
                : "Using cached profile. Sign in via launcher to refresh.";
            _lastVeilnetSyncOk = false;
            _lastVeilnetSyncError = "missing_access_token";
            _nextVeilnetProfileRefreshUtc = DateTime.UtcNow.AddSeconds(5);
            ClearLegacyVeilnetCacheDir();
            return;
        }

        _veilnetProfileError = string.Empty;
        _lastVeilnetSyncOk = false;
        _lastVeilnetSyncError = string.Empty;
        _nextVeilnetProfileRefreshUtc = DateTime.MinValue;
        QueueVeilnetProfileRefresh(force: true);
    }

    private void QueueVeilnetProfileRefresh(bool force)
    {
        if (_veilnetProfileRefreshInProgress)
            return;
        if (!force && DateTime.UtcNow < _nextVeilnetProfileRefreshUtc)
            return;

        var token = ResolveVeilnetToken();
        if (!IsUsableToken(token))
        {
            _veilnetProfileError = string.IsNullOrWhiteSpace(_veilnetUsername)
                ? "Sign in via launcher to view your Veilnet profile."
                : "Using cached profile. Sign in via launcher to refresh.";
            _lastVeilnetSyncOk = false;
            _lastVeilnetSyncError = "missing_access_token";
            _nextVeilnetProfileRefreshUtc = DateTime.UtcNow.AddSeconds(5);
            return;
        }

        _veilnetProfileRefreshInProgress = true;
        _veilnetProfileRefreshTask = FetchVeilnetProfileRefreshPayloadAsync(token, CancellationToken.None);
    }

    private void ProcessCompletedVeilnetProfileRefresh()
    {
        if (_veilnetProfileRefreshTask == null || !_veilnetProfileRefreshTask.IsCompleted)
            return;

        _veilnetProfileRefreshInProgress = false;
        _nextVeilnetProfileRefreshUtc = DateTime.UtcNow.Add(_veilnetProfileRefreshInterval);

        try
        {
            var payload = _veilnetProfileRefreshTask.GetAwaiter().GetResult();
            ApplyVeilnetProfileRefreshPayload(payload);
        }
        catch (Exception ex)
        {
            _veilnetProfileError = "Failed to refresh profile.";
            _lastVeilnetSyncOk = false;
            _lastVeilnetSyncError = "sync_exception";
            _log.Warn($"Veilnet profile refresh failed: {ex.Message}");
        }
        finally
        {
            _veilnetProfileRefreshTask = null;
        }
    }

    private async Task<VeilnetProfileRefreshPayload> FetchVeilnetProfileRefreshPayloadAsync(string token, CancellationToken ct)
    {
        var result = await _veilnetProfileClient.GetProfileAsync(token, ct).ConfigureAwait(false);
        if (!result.Ok || result.Profile == null)
            return VeilnetProfileRefreshPayload.Fail(string.IsNullOrWhiteSpace(result.Message) ? "profile_lookup_failed" : result.Message);

        var profile = result.Profile;
        var payload = VeilnetProfileRefreshPayload.Success(profile);
        var previousPicture = _veilnetPictureUrl;
        var previousBanner = _veilnetBannerUrl;

        if (!string.IsNullOrWhiteSpace(profile.PictureUrl)
            && (!string.Equals(profile.PictureUrl, previousPicture, StringComparison.OrdinalIgnoreCase)
                || _veilnetAvatarTexture == null))
        {
            payload.AvatarBytes = await _webTextureLoader.DownloadImageBytesAsync(profile.PictureUrl, ct).ConfigureAwait(false);
        }

        if (!string.IsNullOrWhiteSpace(profile.BannerUrl)
            && (!string.Equals(profile.BannerUrl, previousBanner, StringComparison.OrdinalIgnoreCase)
                || _veilnetBannerTexture == null))
        {
            payload.BannerBytes = await _webTextureLoader.DownloadImageBytesAsync(profile.BannerUrl, ct).ConfigureAwait(false);
        }

        return payload;
    }

    private void ApplyVeilnetProfileRefreshPayload(VeilnetProfileRefreshPayload payload)
    {
        if (!payload.Ok || payload.Profile == null)
        {
            var error = string.IsNullOrWhiteSpace(payload.ErrorMessage)
                ? "Could not load Veilnet profile."
                : payload.ErrorMessage;
            _veilnetProfileError = error;
            _lastVeilnetSyncOk = false;
            _lastVeilnetSyncError = error;
            SetStatus(string.IsNullOrWhiteSpace(_veilnetUsername) ? "Profile refresh failed." : "Using cached profile.", 1.8);
            return;
        }

        var profile = payload.Profile;
        _veilnetUsername = (profile.Username ?? string.Empty).Trim();
        _veilnetAboutMe = NormalizeMultiline(profile.AboutMe);
        _veilnetThemeColorRaw = (profile.ThemeColor ?? string.Empty).Trim();
        _veilnetTheme = (profile.Theme ?? string.Empty).Trim();
        ApplyVeilnetTheme(_veilnetThemeColorRaw, _veilnetTheme);
        _veilnetUpdatedAt = (profile.UpdatedAtRaw ?? string.Empty).Trim();
        _lastVeilnetProfileUtc = DateTime.UtcNow;
        _lastVeilnetSyncOk = true;
        _lastVeilnetSyncError = string.Empty;
        _veilnetProfileError = string.Empty;

        if (string.IsNullOrWhiteSpace(profile.PictureUrl))
        {
            _veilnetPictureUrl = string.Empty;
            _veilnetAvatarTexture?.Dispose();
            _veilnetAvatarTexture = null;
            DeleteCachedProfileImage(Paths.VeilnetAvatarCachePath);
        }
        else if (payload.AvatarBytes is { Length: > 0 })
        {
            var nextAvatar = CreateCircularAvatarTextureFromBytes(payload.AvatarBytes);
            if (nextAvatar != null)
            {
                _veilnetAvatarTexture?.Dispose();
                _veilnetAvatarTexture = nextAvatar;
                _veilnetPictureUrl = profile.PictureUrl.Trim();
                SaveCachedProfileImage(Paths.VeilnetAvatarCachePath, payload.AvatarBytes);
            }
            else
            {
                _veilnetProfileError = "Avatar image could not be decoded.";
            }
        }

        if (string.IsNullOrWhiteSpace(profile.BannerUrl))
        {
            _veilnetBannerUrl = string.Empty;
            _veilnetBannerTexture?.Dispose();
            _veilnetBannerTexture = null;
            DeleteCachedProfileImage(Paths.VeilnetBannerCachePath);
        }
        else if (payload.BannerBytes is { Length: > 0 })
        {
            var nextBanner = _webTextureLoader.CreateTextureFromBytes(_graphics.GraphicsDevice, payload.BannerBytes);
            if (nextBanner != null)
            {
                _veilnetBannerTexture?.Dispose();
                _veilnetBannerTexture = nextBanner;
                _veilnetBannerUrl = profile.BannerUrl.Trim();
                SaveCachedProfileImage(Paths.VeilnetBannerCachePath, payload.BannerBytes);
            }
            else
            {
                _veilnetProfileError = "Banner image could not be decoded.";
            }
        }

        SaveCachedVeilnetProfile();
        SetStatus("Profile refreshed.", 1.4);
    }

    private Texture2D? CreateCircularAvatarTextureFromBytes(byte[] imageBytes)
    {
        var source = _webTextureLoader.CreateTextureFromBytes(_graphics.GraphicsDevice, imageBytes);
        if (source == null)
            return null;

        try
        {
            return CreateCircularAvatarTexture(source);
        }
        finally
        {
            source.Dispose();
        }
    }

    private Texture2D? CreateCircularAvatarTexture(Texture2D source)
    {
        try
        {
            var sourceWidth = source.Width;
            var sourceHeight = source.Height;
            var side = Math.Min(sourceWidth, sourceHeight);
            if (side <= 1)
                return null;

            var sourcePixels = new Color[sourceWidth * sourceHeight];
            source.GetData(sourcePixels);

            var cropX = (sourceWidth - side) / 2;
            var cropY = (sourceHeight - side) / 2;
            var output = new Color[side * side];
            var center = (side - 1) / 2f;
            var radius = side / 2f;
            var fadeStart = Math.Max(0f, radius - 1.5f);

            for (var y = 0; y < side; y++)
            {
                for (var x = 0; x < side; x++)
                {
                    var srcIndex = (cropY + y) * sourceWidth + (cropX + x);
                    var color = sourcePixels[srcIndex];
                    var dx = x - center;
                    var dy = y - center;
                    var dist = MathF.Sqrt(dx * dx + dy * dy);
                    if (dist >= radius)
                    {
                        color.A = 0;
                    }
                    else if (dist > fadeStart)
                    {
                        var t = 1f - ((dist - fadeStart) / (radius - fadeStart));
                        color.A = (byte)Math.Clamp((int)(color.A * t), 0, 255);
                    }

                    output[y * side + x] = color;
                }
            }

            var texture = new Texture2D(_graphics.GraphicsDevice, side, side, false, SurfaceFormat.Color);
            texture.SetData(output);
            return texture;
        }
        catch (Exception ex)
        {
            _log.Warn($"Circular avatar conversion failed: {ex.Message}");
            return null;
        }
    }

    private void DrawIdentityInfoPanel(SpriteBatch sb, Rectangle rect, string title, string body, Color borderColor, Color titleColor)
    {
        sb.Draw(_pixel, rect, new Color(18, 18, 24, 220));
        var sideBarWidth = Math.Clamp(rect.Width / 200, 3, 6);
        var sideBarRect = new Rectangle(rect.X, rect.Y, sideBarWidth, rect.Height);
        sb.Draw(_pixel, sideBarRect, WithAlpha(borderColor, 168));
        DrawBorder(sb, rect, borderColor);
        _font.DrawString(sb, title, new Vector2(rect.X + 10, rect.Y + 8), titleColor);
        var textRect = new Rectangle(rect.X + 10, rect.Y + _font.LineHeight + 14, rect.Width - 20, rect.Height - (_font.LineHeight + 20));
        DrawWrappedText(sb, body, textRect, new Color(198, 206, 228));
    }

    private void DrawWrappedText(SpriteBatch sb, string text, Rectangle rect, Color color)
    {
        var wrapped = WrapText(text, Math.Max(80, rect.Width));
        var y = rect.Y;
        for (var i = 0; i < wrapped.Count; i++)
        {
            if (y + _font.LineHeight > rect.Bottom)
                break;
            _font.DrawString(sb, wrapped[i], new Vector2(rect.X, y), color);
            y += _font.LineHeight + 2;
        }
    }

    private List<string> WrapText(string text, int maxWidth)
    {
        var lines = new List<string>();
        var content = string.IsNullOrWhiteSpace(text) ? string.Empty : text.Replace("\r\n", "\n").Trim();
        if (string.IsNullOrWhiteSpace(content))
        {
            lines.Add(string.Empty);
            return lines;
        }

        var paragraphs = content.Split('\n');
        for (var p = 0; p < paragraphs.Length; p++)
        {
            var paragraph = (paragraphs[p] ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(paragraph))
            {
                lines.Add(string.Empty);
                continue;
            }

            var words = paragraph.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var current = string.Empty;
            for (var w = 0; w < words.Length; w++)
            {
                var word = words[w];
                var candidate = string.IsNullOrWhiteSpace(current) ? word : $"{current} {word}";
                if (_font.MeasureString(candidate).X <= maxWidth)
                {
                    current = candidate;
                    continue;
                }

                if (!string.IsNullOrWhiteSpace(current))
                    lines.Add(current);

                if (_font.MeasureString(word).X <= maxWidth)
                {
                    current = word;
                    continue;
                }

                var remaining = word;
                while (remaining.Length > 0)
                {
                    var take = remaining.Length;
                    while (take > 1 && _font.MeasureString(remaining.Substring(0, take)).X > maxWidth)
                        take--;
                    lines.Add(remaining.Substring(0, take));
                    remaining = remaining.Substring(take);
                }

                current = string.Empty;
            }

            if (!string.IsNullOrWhiteSpace(current))
                lines.Add(current);
        }

        return lines;
    }

    private string TruncateToWidth(string text, int maxWidth)
    {
        text = (text ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(text) || _font.MeasureString(text).X <= maxWidth)
            return text;

        const string ellipsis = "...";
        var trimmed = text;
        while (trimmed.Length > 1 && _font.MeasureString(trimmed + ellipsis).X > maxWidth)
            trimmed = trimmed[..^1];
        return trimmed.Length <= 0 ? ellipsis : trimmed + ellipsis;
    }

    private string ResolveDisplayUsername()
    {
        var name = (_veilnetUsername ?? string.Empty).Trim();
        if (!string.IsNullOrWhiteSpace(name))
            return name;

        name = (_identityStore.ReservedUsername ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(name))
            name = _profile.GetDisplayUsername();
        if (string.IsNullOrWhiteSpace(name))
            name = "(unclaimed)";
        return name;
    }

    private void ApplyVeilnetTheme(string? themeColorRaw, string? themeId)
    {
        var resolved = new Color(124, 92, 255);
        if (!TryParseHexColor(themeColorRaw, out resolved) && !TryResolveThemeColorFromId(themeId, out resolved))
            resolved = new Color(124, 92, 255);

        if (!_veilnetAccentColor.Equals(resolved))
            DisposeAvatarDecorTextures();

        _veilnetAccentColor = resolved;
        _veilnetAccentTextColor = RelativeLuminance(resolved) >= 0.54f
            ? new Color(26, 30, 40)
            : new Color(240, 242, 255);
    }

    private static bool TryResolveThemeColorFromId(string? themeId, out Color color)
    {
        color = default;
        var key = (themeId ?? string.Empty).Trim().ToLowerInvariant();
        if (key.Length == 0)
            return false;

        return key switch
        {
            "default" => TryParseHexColor("#7c5cff", out color),
            "ember" => TryParseHexColor("#ff4d4d", out color),
            "neon" => TryParseHexColor("#8bff00", out color),
            "ocean" => TryParseHexColor("#00b3ff", out color),
            "rose" => TryParseHexColor("#ff4fd8", out color),
            "mint" => TryParseHexColor("#22c55e", out color),
            "slate" => TryParseHexColor("#94a3b8", out color),
            "gold" => TryParseHexColor("#fbbf24", out color),
            _ => false
        };
    }

    private static bool TryParseHexColor(string? raw, out Color color)
    {
        color = default;
        var value = (raw ?? string.Empty).Trim();
        if (value.StartsWith("#", StringComparison.Ordinal))
            value = value[1..];

        if (value.Length != 6)
            return false;

        if (!int.TryParse(value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var rgb))
            return false;

        var r = (byte)((rgb >> 16) & 0xFF);
        var g = (byte)((rgb >> 8) & 0xFF);
        var b = (byte)(rgb & 0xFF);
        color = new Color(r, g, b, (byte)255);
        return true;
    }

    private static Color BlendColors(Color baseColor, Color mixColor, float mix)
    {
        var t = Math.Clamp(mix, 0f, 1f);
        var r = (byte)Math.Clamp((int)MathF.Round(baseColor.R + ((mixColor.R - baseColor.R) * t)), 0, 255);
        var g = (byte)Math.Clamp((int)MathF.Round(baseColor.G + ((mixColor.G - baseColor.G) * t)), 0, 255);
        var b = (byte)Math.Clamp((int)MathF.Round(baseColor.B + ((mixColor.B - baseColor.B) * t)), 0, 255);
        return new Color(r, g, b, (byte)255);
    }

    private static Color DarkenColor(Color color, float amount)
    {
        var t = Math.Clamp(amount, 0f, 1f);
        var scale = 1f - t;
        var r = (byte)Math.Clamp((int)MathF.Round(color.R * scale), 0, 255);
        var g = (byte)Math.Clamp((int)MathF.Round(color.G * scale), 0, 255);
        var b = (byte)Math.Clamp((int)MathF.Round(color.B * scale), 0, 255);
        return new Color(r, g, b, (byte)255);
    }

    private static Color WithAlpha(Color color, byte alpha)
    {
        return new Color(color.R, color.G, color.B, alpha);
    }

    private static float RelativeLuminance(Color color)
    {
        var r = color.R / 255f;
        var g = color.G / 255f;
        var b = color.B / 255f;
        return (0.2126f * r) + (0.7152f * g) + (0.0722f * b);
    }

    private void DrawTextureCover(SpriteBatch sb, Texture2D texture, Rectangle dest, Color color)
    {
        var src = GetCenterCropSource(texture, dest);
        sb.Draw(texture, dest, src, color);
    }

    private static Rectangle GetCenterCropSource(Texture2D texture, Rectangle dest)
    {
        if (texture.Width <= 0 || texture.Height <= 0 || dest.Width <= 0 || dest.Height <= 0)
            return new Rectangle(0, 0, Math.Max(1, texture.Width), Math.Max(1, texture.Height));

        var srcAspect = texture.Width / (float)texture.Height;
        var destAspect = dest.Width / (float)dest.Height;
        if (srcAspect > destAspect)
        {
            var cropWidth = Math.Max(1, (int)Math.Round(texture.Height * destAspect));
            var x = Math.Max(0, (texture.Width - cropWidth) / 2);
            return new Rectangle(x, 0, cropWidth, texture.Height);
        }

        var cropHeight = Math.Max(1, (int)Math.Round(texture.Width / destAspect));
        var y = Math.Max(0, (texture.Height - cropHeight) / 2);
        return new Rectangle(0, y, texture.Width, cropHeight);
    }

    private void DrawHeaderSideBars(SpriteBatch sb, Rectangle rect, Color color)
    {
        var barWidth = Math.Clamp(rect.Width / 170, 4, 8);
        var left = new Rectangle(rect.X, rect.Y, barWidth, rect.Height);
        var right = new Rectangle(rect.Right - barWidth, rect.Y, barWidth, rect.Height);
        sb.Draw(_pixel, left, color);
        sb.Draw(_pixel, right, color);
    }

    private void DrawFallbackBanner(SpriteBatch sb, Rectangle rect)
    {
        var leftColor = new Color(18, 26, 42, 255);
        var rightColor = new Color(12, 18, 30, 255);
        var left = new Rectangle(rect.X, rect.Y, rect.Width / 2, rect.Height);
        var right = new Rectangle(left.Right, rect.Y, rect.Width - left.Width, rect.Height);
        sb.Draw(_pixel, left, leftColor);
        sb.Draw(_pixel, right, rightColor);
        var horizon = new Rectangle(rect.X, rect.Y + (rect.Height / 2), rect.Width, Math.Max(2, rect.Height / 10));
        sb.Draw(_pixel, horizon, new Color(36, 52, 78, 130));
    }

    private void EnsureAvatarDecorTextures(int size, Color ringColor, Color placeholderColor)
    {
        var side = Math.Max(24, size);
        var needsRing = _veilnetAvatarRingTexture == null
            || _veilnetAvatarRingTexture.IsDisposed
            || _veilnetAvatarDecorSize != side
            || !_veilnetAvatarRingColor.Equals(ringColor);

        var needsPlaceholder = _veilnetAvatarPlaceholderTexture == null
            || _veilnetAvatarPlaceholderTexture.IsDisposed
            || _veilnetAvatarDecorSize != side
            || !_veilnetAvatarPlaceholderColor.Equals(placeholderColor);

        if (needsRing)
        {
            _veilnetAvatarRingTexture?.Dispose();
            _veilnetAvatarRingTexture = CreateAvatarRingTexture(_graphics.GraphicsDevice, side, Math.Max(2, side / 44), ringColor);
            _veilnetAvatarRingColor = ringColor;
        }

        if (needsPlaceholder)
        {
            _veilnetAvatarPlaceholderTexture?.Dispose();
            _veilnetAvatarPlaceholderTexture = CreateAvatarPlaceholderTexture(
                _graphics.GraphicsDevice,
                side,
                placeholderColor,
                BlendColors(placeholderColor, Color.White, 0.3f),
                Math.Max(2, side / 28));
            _veilnetAvatarPlaceholderColor = placeholderColor;
        }

        _veilnetAvatarDecorSize = side;
    }

    private Texture2D CreateAvatarRingTexture(GraphicsDevice graphics, int size, int thickness, Color color)
    {
        var side = Math.Max(8, size);
        var ringThickness = Math.Clamp(thickness, 1, side / 3);
        var data = new Color[side * side];
        var center = (side - 1) / 2f;
        var radius = side / 2f;
        var inner = Math.Max(0f, radius - ringThickness);

        for (var y = 0; y < side; y++)
        {
            for (var x = 0; x < side; x++)
            {
                var dx = x - center;
                var dy = y - center;
                var dist = MathF.Sqrt(dx * dx + dy * dy);
                data[y * side + x] = dist <= radius && dist >= inner ? color : Color.Transparent;
            }
        }

        var tex = new Texture2D(graphics, side, side, false, SurfaceFormat.Color);
        tex.SetData(data);
        return tex;
    }

    private Texture2D CreateAvatarPlaceholderTexture(GraphicsDevice graphics, int size, Color fill, Color edge, int edgeThickness)
    {
        var side = Math.Max(8, size);
        var data = new Color[side * side];
        var center = (side - 1) / 2f;
        var radius = side / 2f;
        var inner = Math.Max(0f, radius - Math.Max(1, edgeThickness));

        for (var y = 0; y < side; y++)
        {
            for (var x = 0; x < side; x++)
            {
                var dx = x - center;
                var dy = y - center;
                var dist = MathF.Sqrt(dx * dx + dy * dy);
                if (dist > radius)
                    data[y * side + x] = Color.Transparent;
                else if (dist >= inner)
                    data[y * side + x] = edge;
                else
                    data[y * side + x] = fill;
            }
        }

        var tex = new Texture2D(graphics, side, side, false, SurfaceFormat.Color);
        tex.SetData(data);
        return tex;
    }

    private void DisposeVeilnetTextures()
    {
        _veilnetAvatarTexture?.Dispose();
        _veilnetAvatarTexture = null;
        _veilnetBannerTexture?.Dispose();
        _veilnetBannerTexture = null;
    }

    private void DisposeAvatarDecorTextures()
    {
        _veilnetAvatarRingTexture?.Dispose();
        _veilnetAvatarRingTexture = null;
        _veilnetAvatarPlaceholderTexture?.Dispose();
        _veilnetAvatarPlaceholderTexture = null;
        _veilnetAvatarDecorSize = 0;
        _veilnetAvatarRingColor = Color.Transparent;
        _veilnetAvatarPlaceholderColor = Color.Transparent;
    }

    private void CancelVeilnetRefreshTask()
    {
        _veilnetProfileRefreshTask = null;
        _veilnetProfileRefreshInProgress = false;
    }

    private static string NormalizeMultiline(string text)
    {
        return (text ?? string.Empty).Replace("\r\n", "\n").Trim();
    }

    private static string TrimStatus(string text)
    {
        var value = (text ?? string.Empty).Replace('\r', ' ').Replace('\n', ' ').Trim();
        if (value.Length <= 90)
            return value;
        return value.Substring(0, 90);
    }

    private static TimeSpan ResolveProfileRefreshInterval()
    {
        var raw = (Environment.GetEnvironmentVariable("LV_VEILNET_PROFILE_REFRESH_SECONDS") ?? string.Empty).Trim();
        if (int.TryParse(raw, out var seconds))
            return TimeSpan.FromSeconds(Math.Clamp(seconds, 20, 60));
        return TimeSpan.FromSeconds(25);
    }

    private static string ResolveVeilnetToken()
    {
        return (Environment.GetEnvironmentVariable("LV_VEILNET_ACCESS_TOKEN") ?? string.Empty).Trim();
    }

    private static bool IsUsableToken(string token)
    {
        if (string.IsNullOrWhiteSpace(token))
            return false;

        return !string.Equals(token, "null", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(token, "undefined", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(token, "placeholder", StringComparison.OrdinalIgnoreCase);
    }

    private struct VeilnetProfileRefreshPayload
    {
        public bool Ok { get; set; }
        public VeilnetProfileDto? Profile { get; set; }
        public byte[]? AvatarBytes { get; set; }
        public byte[]? BannerBytes { get; set; }
        public string ErrorMessage { get; set; }

        public static VeilnetProfileRefreshPayload Success(VeilnetProfileDto profile)
        {
            return new VeilnetProfileRefreshPayload
            {
                Ok = true,
                Profile = profile,
                AvatarBytes = null,
                BannerBytes = null,
                ErrorMessage = string.Empty
            };
        }

        public static VeilnetProfileRefreshPayload Fail(string message)
        {
            return new VeilnetProfileRefreshPayload
            {
                Ok = false,
                Profile = null,
                AvatarBytes = null,
                BannerBytes = null,
                ErrorMessage = (message ?? string.Empty).Trim()
            };
        }
    }

    private void ClearLegacyVeilnetCacheDir()
    {
        try
        {
            if (!System.IO.Directory.Exists(LegacyVeilnetCacheDir))
                return;
            System.IO.Directory.Delete(LegacyVeilnetCacheDir, recursive: true);
        }
        catch
        {
            // Best effort cleanup only.
        }
    }

    private void EnsureSelectedSocialViewerLoads()
    {
        var user = GetSelectedSocialUser();
        var key = (user?.ProductUserId ?? string.Empty).Trim();
        if (!string.Equals(key, _selectedSocialUserKey, StringComparison.OrdinalIgnoreCase))
        {
            ClearSelectedSocialViewerAssets();
            _selectedSocialUserKey = key;
        }

        if (user == null)
            return;

        var nextAvatarUrl = (user.PictureUrl ?? string.Empty).Trim();
        if (!string.Equals(nextAvatarUrl, _selectedSocialAvatarUrl, StringComparison.OrdinalIgnoreCase))
        {
            _selectedSocialAvatarTexture?.Dispose();
            _selectedSocialAvatarTexture = null;
            _selectedSocialAvatarBytesTask = string.IsNullOrWhiteSpace(nextAvatarUrl)
                ? null
                : _webTextureLoader.DownloadImageBytesAsync(nextAvatarUrl);
            _selectedSocialAvatarUrl = nextAvatarUrl;
        }

        var nextBannerUrl = (user.BannerUrl ?? string.Empty).Trim();
        if (!string.Equals(nextBannerUrl, _selectedSocialBannerUrl, StringComparison.OrdinalIgnoreCase))
        {
            _selectedSocialBannerTexture?.Dispose();
            _selectedSocialBannerTexture = null;
            _selectedSocialBannerBytesTask = string.IsNullOrWhiteSpace(nextBannerUrl)
                ? null
                : _webTextureLoader.DownloadImageBytesAsync(nextBannerUrl);
            _selectedSocialBannerUrl = nextBannerUrl;
        }
    }

    private void EnsureSocialAvatarLoads()
    {
        if (_friendsListMode == ProfileScreenFriendsMode.Friends)
        {
            for (var i = 0; i < _profile.Friends.Count; i++)
            {
                var friend = _profile.Friends[i];
                QueueSocialAvatarLoad((friend.UserId ?? string.Empty).Trim(), (friend.PictureUrl ?? string.Empty).Trim());
            }
            return;
        }

        if (_friendsListMode == ProfileScreenFriendsMode.Requests)
        {
            for (var i = 0; i < _incomingRequests.Count; i++)
            {
                var user = _incomingRequests[i].User;
                QueueSocialAvatarLoad((user.ProductUserId ?? string.Empty).Trim(), (user.PictureUrl ?? string.Empty).Trim());
            }

            for (var i = 0; i < _outgoingRequests.Count; i++)
            {
                var user = _outgoingRequests[i].User;
                QueueSocialAvatarLoad((user.ProductUserId ?? string.Empty).Trim(), (user.PictureUrl ?? string.Empty).Trim());
            }
            return;
        }

        for (var i = 0; i < _blockedUsers.Count; i++)
        {
            var user = _blockedUsers[i];
            QueueSocialAvatarLoad((user.ProductUserId ?? string.Empty).Trim(), (user.PictureUrl ?? string.Empty).Trim());
        }
    }

    private void QueueSocialAvatarLoad(string userId, string pictureUrl)
    {
        if (string.IsNullOrWhiteSpace(userId) || string.IsNullOrWhiteSpace(pictureUrl))
            return;

        if (!_socialAvatarVisuals.TryGetValue(userId, out var state))
        {
            state = new AvatarVisualState();
            _socialAvatarVisuals[userId] = state;
        }

        if (state.Texture == null && state.BytesTask == null)
            state.BytesTask = _webTextureLoader.DownloadImageBytesAsync(pictureUrl);
    }

    private void EnsureSelectedFriendHydration()
    {
        if (_friendsListMode != ProfileScreenFriendsMode.Friends)
            return;
        if (_selectedFriend < 0 || _selectedFriend >= _profile.Friends.Count)
            return;
        if (_selectedFriendHydrationTask != null)
            return;
        if (!_gate.CanUseOfficialOnline(_log, out _))
            return;

        var friend = _profile.Friends[_selectedFriend];
        var userId = (friend.UserId ?? string.Empty).Trim();
        var username = ResolveFriendUsername(friend);
        if (string.IsNullOrWhiteSpace(userId) || string.IsNullOrWhiteSpace(username))
            return;

        var missingProfileData = string.IsNullOrWhiteSpace(friend.PictureUrl)
            || string.IsNullOrWhiteSpace(friend.BannerUrl)
            || string.IsNullOrWhiteSpace(friend.AboutMe);
        if (!missingProfileData)
            return;

        _selectedFriendHydrationUserId = userId;
        _selectedFriendHydrationTask = _gate.LookupVeilnetUserAsync(username);
    }

    private void ProcessSelectedFriendHydration()
    {
        if (_selectedFriendHydrationTask == null || !_selectedFriendHydrationTask.IsCompleted)
            return;

        try
        {
            var result = _selectedFriendHydrationTask.GetAwaiter().GetResult();
            if (!result.Ok || !result.Found || result.User == null)
                return;

            var hydrated = result.User;
            var friend = _profile.Friends.FirstOrDefault(entry =>
                string.Equals((entry.UserId ?? string.Empty).Trim(), _selectedFriendHydrationUserId, StringComparison.OrdinalIgnoreCase));
            if (friend == null)
                return;

            var changed = false;
            if (!string.IsNullOrWhiteSpace(hydrated.PictureUrl) && !string.Equals(friend.PictureUrl, hydrated.PictureUrl, StringComparison.Ordinal))
            {
                friend.PictureUrl = hydrated.PictureUrl.Trim();
                changed = true;
            }

            if (!string.IsNullOrWhiteSpace(hydrated.BannerUrl) && !string.Equals(friend.BannerUrl, hydrated.BannerUrl, StringComparison.Ordinal))
            {
                friend.BannerUrl = hydrated.BannerUrl.Trim();
                changed = true;
            }

            var aboutMe = NormalizeMultiline(hydrated.AboutMe);
            if (!string.IsNullOrWhiteSpace(aboutMe) && !string.Equals(friend.AboutMe, aboutMe, StringComparison.Ordinal))
            {
                friend.AboutMe = aboutMe;
                changed = true;
            }

            if (changed)
            {
                _profile.Save(_log);
                QueueSocialAvatarLoad((friend.UserId ?? string.Empty).Trim(), (friend.PictureUrl ?? string.Empty).Trim());
                EnsureSelectedSocialViewerLoads();
            }
        }
        catch (Exception ex)
        {
            _log.Warn($"Saved friend profile hydration failed: {ex.Message}");
        }
        finally
        {
            _selectedFriendHydrationTask = null;
            _selectedFriendHydrationUserId = string.Empty;
        }
    }

    private void ProcessSelectedSocialImageLoads()
    {
        foreach (var pair in _socialAvatarVisuals)
        {
            var state = pair.Value;
            if (state.BytesTask == null || !state.BytesTask.IsCompleted)
                continue;

            try
            {
                if (state.BytesTask.Status == TaskStatus.RanToCompletion)
                {
                    var bytes = state.BytesTask.Result;
                    if (bytes is { Length: > 0 })
                    {
                        var texture = CreateCircularAvatarTextureFromBytes(bytes);
                        if (texture != null)
                        {
                            state.Texture?.Dispose();
                            state.Texture = texture;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _log.Warn($"Social list avatar load failed: {ex.Message}");
            }
            finally
            {
                state.BytesTask = null;
            }
        }

        if (_selectedSocialAvatarBytesTask is { IsCompleted: true })
        {
            try
            {
                var bytes = _selectedSocialAvatarBytesTask.GetAwaiter().GetResult();
                if (bytes is { Length: > 0 })
                {
                    var texture = CreateCircularAvatarTextureFromBytes(bytes);
                    if (texture != null)
                    {
                        _selectedSocialAvatarTexture?.Dispose();
                        _selectedSocialAvatarTexture = texture;
                    }
                }
            }
            catch (Exception ex)
            {
                _log.Warn($"Social avatar load failed: {ex.Message}");
            }
            finally
            {
                _selectedSocialAvatarBytesTask = null;
            }
        }

        if (_selectedSocialBannerBytesTask is { IsCompleted: true })
        {
            try
            {
                var bytes = _selectedSocialBannerBytesTask.GetAwaiter().GetResult();
                if (bytes is { Length: > 0 })
                {
                    var texture = _webTextureLoader.CreateTextureFromBytes(_graphics.GraphicsDevice, bytes);
                    if (texture != null)
                    {
                        _selectedSocialBannerTexture?.Dispose();
                        _selectedSocialBannerTexture = texture;
                    }
                }
            }
            catch (Exception ex)
            {
                _log.Warn($"Social banner load failed: {ex.Message}");
            }
            finally
            {
                _selectedSocialBannerBytesTask = null;
            }
        }
    }

    private Texture2D? TryGetSocialAvatarTexture(string userId)
    {
        userId = (userId ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(userId))
            return null;
        return _socialAvatarVisuals.TryGetValue(userId, out var state) ? state.Texture : null;
    }

    private void ClearSelectedSocialViewerAssets()
    {
        _selectedSocialAvatarTexture?.Dispose();
        _selectedSocialAvatarTexture = null;
        _selectedSocialBannerTexture?.Dispose();
        _selectedSocialBannerTexture = null;
        _selectedSocialAvatarBytesTask = null;
        _selectedSocialBannerBytesTask = null;
        _selectedSocialAvatarUrl = string.Empty;
        _selectedSocialBannerUrl = string.Empty;
    }

    private void DrawFriendsTab(SpriteBatch sb)
    {
        DrawFriendsModeTabs(sb);

        sb.Draw(_pixel, _friendsListRect, new Color(20, 20, 20, 220));
        DrawBorder(sb, _friendsListRect, Color.White);

        var title = _friendsListMode switch
        {
            ProfileScreenFriendsMode.Requests => "INCOMING REQUESTS",
            ProfileScreenFriendsMode.Blocked => "BLOCKED USERS",
            _ => "SAVED FRIENDS"
        };
        _font.DrawString(sb, title, new Vector2(_friendsListRect.X + 8, _friendsListRect.Y + 6), Color.White);

        if (_friendsListMode == ProfileScreenFriendsMode.Requests)
        {
            _font.DrawString(
                sb,
                $"OUTGOING: {_outgoingRequests.Count}",
                new Vector2(_friendsListRect.Right - 160, _friendsListRect.Y + 6),
                new Color(170, 170, 170));
        }

        var rowHeight = Math.Max(54, (_font.LineHeight * 2) + 14);
        var y = _friendsListRect.Y + _font.LineHeight + 12;
        if (_friendsListMode == ProfileScreenFriendsMode.Friends)
        {
            if (_profile.Friends.Count == 0)
            {
                _font.DrawString(sb, "(no friends saved yet)", new Vector2(_friendsListRect.X + 8, _friendsListRect.Y + _font.LineHeight + 10), new Color(180, 180, 180));
                DrawSelectedSocialViewer(sb, null, GetSelectedSocialStatusText(), "Select a friend to preview the profile.");
                return;
            }

            for (var i = 0; i < _profile.Friends.Count; i++)
            {
                var f = _profile.Friends[i];
                var username = ResolveFriendUsername(f);
                var secondary = BuildFriendPresenceText(f);
                var row = new Rectangle(_friendsListRect.X + 6, y, _friendsListRect.Width - 12, rowHeight);
                DrawSocialRow(sb, row, username, secondary, i == _selectedFriend, new Color(60, 90, 130, 220), new Color(200, 230, 255), TryGetSocialAvatarTexture((f.UserId ?? string.Empty).Trim()));

                y += rowHeight + 6;
                if (y + rowHeight > _friendsListRect.Bottom - 8)
                    break;
            }

            DrawSelectedSocialViewer(sb, GetSelectedSocialUser(), GetSelectedSocialStatusText(), "Select a friend to preview the profile.");
            return;
        }

        if (_friendsListMode == ProfileScreenFriendsMode.Requests)
        {
            if (_incomingRequests.Count == 0 && _outgoingRequests.Count == 0)
            {
                _font.DrawString(sb, "(no pending requests)", new Vector2(_friendsListRect.X + 8, _friendsListRect.Y + _font.LineHeight + 10), new Color(180, 180, 180));
                DrawSelectedSocialViewer(sb, null, GetSelectedSocialStatusText(), "Select an incoming or outgoing request to preview the profile.");
                return;
            }

            // Incoming
            if (_incomingRequests.Count > 0)
            {
                _font.DrawString(sb, "INCOMING:", new Vector2(_friendsListRect.X + 8, y - 4), new Color(200, 200, 200));
                y += _font.LineHeight + 4;

                for (var i = 0; i < _incomingRequests.Count; i++)
                {
                    var req = _incomingRequests[i];
                    var user = req.User;
                    var username = ResolveGateUsername(user);
                    var row = new Rectangle(_friendsListRect.X + 6, y, _friendsListRect.Width - 12, rowHeight);
                    DrawSocialRow(sb, row, username, "Incoming request", i == _selectedRequest, new Color(90, 72, 40, 220), new Color(240, 220, 160), TryGetSocialAvatarTexture((user.ProductUserId ?? string.Empty).Trim()));

                    y += rowHeight + 6;
                    if (y + rowHeight > _friendsListRect.Bottom - 8)
                        break;
                }
            }

            // Outgoing
            if (_outgoingRequests.Count > 0 && y + rowHeight + 20 < _friendsListRect.Bottom)
            {
                y += 8;
                _font.DrawString(sb, "OUTGOING:", new Vector2(_friendsListRect.X + 8, y), new Color(200, 200, 200));
                y += _font.LineHeight + 4;

                for (var i = 0; i < _outgoingRequests.Count; i++)
                {
                    var req = _outgoingRequests[i];
                    var user = req.User;
                    var username = ResolveGateUsername(user);
                    var row = new Rectangle(_friendsListRect.X + 6, y, _friendsListRect.Width - 12, rowHeight);
                    DrawSocialRow(sb, row, username, "Outgoing request", i == _selectedOutgoingRequest, new Color(50, 64, 92, 220), new Color(180, 210, 245), TryGetSocialAvatarTexture((user.ProductUserId ?? string.Empty).Trim()));

                    y += rowHeight + 6;
                    if (y + rowHeight > _friendsListRect.Bottom - 8)
                        break;
                }
            }

            DrawSelectedSocialViewer(sb, GetSelectedSocialUser(), GetSelectedSocialStatusText(), "Select an incoming or outgoing request to preview the profile.");
            return;
        }

        if (_blockedUsers.Count == 0)
        {
            _font.DrawString(sb, "(no blocked users)", new Vector2(_friendsListRect.X + 8, _friendsListRect.Y + _font.LineHeight + 10), new Color(180, 180, 180));
            DrawSelectedSocialViewer(sb, null, GetSelectedSocialStatusText(), "Select a blocked user to preview the cached profile.");
            return;
        }

        for (var i = 0; i < _blockedUsers.Count; i++)
        {
            var blockedUser = _blockedUsers[i];
            var username = ResolveGateUsername(blockedUser);
            var row = new Rectangle(_friendsListRect.X + 6, y, _friendsListRect.Width - 12, rowHeight);
            DrawSocialRow(sb, row, username, "Blocked", i == _selectedBlocked, new Color(110, 52, 52, 220), new Color(245, 170, 170), TryGetSocialAvatarTexture((blockedUser.ProductUserId ?? string.Empty).Trim()));

            y += rowHeight + 6;
            if (y + rowHeight > _friendsListRect.Bottom - 8)
                break;
        }

        DrawSelectedSocialViewer(sb, GetSelectedSocialUser(), GetSelectedSocialStatusText(), "Select a blocked user to preview the cached profile.");
    }

    private void DrawSocialRow(SpriteBatch sb, Rectangle row, string title, string subtitle, bool selected, Color selectedFill, Color selectedBorder, Texture2D? avatarTexture = null)
    {
        sb.Draw(_pixel, row, selected ? selectedFill : new Color(26, 26, 26, 200));
        DrawBorder(sb, row, selected ? selectedBorder : new Color(110, 110, 110));

        var iconSize = Math.Max(28, row.Height - 14);
        var iconRect = new Rectangle(row.X + 8, row.Y + (row.Height - iconSize) / 2, iconSize, iconSize);
        if (avatarTexture is { IsDisposed: false })
            sb.Draw(avatarTexture, iconRect, Color.White);
        else
        {
            DrawCircularBadge(sb, iconRect, title);
            var glyph = string.IsNullOrWhiteSpace(title) ? "?" : title[..1].ToUpperInvariant();
            var glyphPos = new Vector2(iconRect.Center.X - (_font.MeasureString(glyph).X / 2f), iconRect.Center.Y - (_font.LineHeight / 2f));
            _font.DrawString(sb, glyph, glyphPos, Color.White);
        }

        var textX = iconRect.Right + 10;
        _font.DrawString(sb, title, new Vector2(textX, row.Y + 7), Color.White);
        var maxTextWidth = Math.Max(40, row.Right - textX - 8);
        _font.DrawString(sb, TruncateToWidth(subtitle, maxTextWidth), new Vector2(textX, row.Y + 7 + _font.LineHeight), new Color(180, 188, 205));
    }

    private void DrawSelectedSocialViewer(SpriteBatch sb, GateIdentityUser? user, string presenceText, string emptyMessage)
    {
        sb.Draw(_pixel, _friendsViewerRect, new Color(20, 20, 20, 190));
        DrawBorder(sb, _friendsViewerRect, new Color(100, 100, 100));

        if (user == null)
        {
            _font.DrawString(sb, "PROFILE VIEWER", new Vector2(_friendsViewerRect.X + 8, _friendsViewerRect.Y + 8), Color.White);
            _font.DrawString(sb, emptyMessage, new Vector2(_friendsViewerRect.X + 8, _friendsViewerRect.Y + 36), new Color(180, 180, 180));
            return;
        }

        if (_selectedSocialBannerTexture is { IsDisposed: false })
            DrawTextureCover(sb, _selectedSocialBannerTexture, _friendsViewerBannerRect, Color.White);
        else
            DrawFallbackBanner(sb, _friendsViewerBannerRect);
        DrawBorder(sb, _friendsViewerBannerRect, new Color(160, 170, 190));
        sb.Draw(_pixel, _friendsViewerBannerRect, new Color(0, 0, 0, _selectedSocialBannerTexture is { IsDisposed: false } ? 70 : 40));

        if (_selectedSocialAvatarTexture is { IsDisposed: false })
            sb.Draw(_selectedSocialAvatarTexture, _friendsViewerAvatarRect, Color.White);
        else
            DrawCircularBadge(sb, _friendsViewerAvatarRect, ResolveGateUsername(user));
        DrawBorder(sb, _friendsViewerAvatarRect, Color.White);

        var namePos = new Vector2(_friendsViewerAvatarRect.Right + 12, _friendsViewerBannerRect.Bottom - _font.LineHeight - 12);
        _font.DrawString(sb, ResolveGateUsername(user), namePos, Color.White);
        var statusPos = new Vector2(_friendsViewerBodyRect.X + 8, _friendsViewerBannerRect.Bottom + 96);
        var statusWidth = Math.Max(40, _friendsViewerBodyRect.Right - (int)statusPos.X - 8);
        if (TryGetSelectedPresenceDetails(out var primaryStatus, out var worldName, out var gameMode)
            && string.Equals(primaryStatus, "IN WORLD", StringComparison.OrdinalIgnoreCase))
        {
            var modeText = string.IsNullOrWhiteSpace(gameMode) ? string.Empty : $" : {gameMode.Trim().ToUpperInvariant()}";
            _font.DrawString(sb, $"ONLINE - IN WORLD{modeText}", statusPos, new Color(190, 220, 245));
            var worldRect = new Rectangle((int)statusPos.X, (int)statusPos.Y + _font.LineHeight + 4, statusWidth, _font.LineHeight);
            DrawContainedScrollingText(sb, string.IsNullOrWhiteSpace(worldName) ? "WORLD" : worldName, worldRect, new Color(220, 228, 245), _profilePreviewScrollTimer);
        }
        else
        {
            _font.DrawString(
                sb,
                TruncateToWidth(string.IsNullOrWhiteSpace(presenceText) ? "OFFLINE" : presenceText, statusWidth),
                statusPos,
                new Color(190, 220, 245));
        }

        var aboutLabel = Math.Max(_friendsViewerBodyRect.Y + 8, (int)statusPos.Y + (_font.LineHeight * 2) + 22);
        _font.DrawString(sb, "ABOUT", new Vector2(_friendsViewerBodyRect.X + 8, aboutLabel), new Color(190, 220, 245));
        var textRect = new Rectangle(_friendsViewerBodyRect.X + 8, aboutLabel + _font.LineHeight + 8, _friendsViewerBodyRect.Width - 16, Math.Max(0, _friendsViewerBodyRect.Bottom - (aboutLabel + _font.LineHeight + 12)));
        var about = string.IsNullOrWhiteSpace(user.AboutMe)
            ? "No profile bio available yet."
            : NormalizeMultiline(user.AboutMe);
        DrawWrappedText(sb, about, textRect, new Color(198, 206, 228));
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

        DrawBorder(sb, rect, WithAlpha(BlendColors(baseColor, Color.White, 0.25f), 220));
    }

    private Color ResolveBadgeColor(string seed)
    {
        var text = string.IsNullOrWhiteSpace(seed) ? "friend" : seed.Trim().ToLowerInvariant();
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

    private string ResolveFriendUsername(PlayerProfile.FriendEntry friend)
    {
        var username = (friend.Label ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(username))
            username = (friend.LastKnownDisplayName ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(username))
            username = "PLAYER";
        return username;
    }

    private static string ResolveGateUsername(GateIdentityUser user)
    {
        var username = (user.Username ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(username))
            username = (user.DisplayName ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(username))
            username = "PLAYER";
        return username;
    }

    private GateIdentityUser? GetSelectedSocialUser()
    {
        if (_friendsListMode == ProfileScreenFriendsMode.Friends)
        {
            if (_selectedFriend < 0 || _selectedFriend >= _profile.Friends.Count)
                return null;

            var friend = _profile.Friends[_selectedFriend];
            return new GateIdentityUser
            {
                ProductUserId = (friend.UserId ?? string.Empty).Trim(),
                Username = ResolveFriendUsername(friend),
                DisplayName = (friend.LastKnownDisplayName ?? string.Empty).Trim(),
                PictureUrl = (friend.PictureUrl ?? string.Empty).Trim(),
                BannerUrl = (friend.BannerUrl ?? string.Empty).Trim(),
                AboutMe = (friend.AboutMe ?? string.Empty).Trim()
            };
        }

        if (_friendsListMode == ProfileScreenFriendsMode.Requests)
        {
            if (_selectedRequest >= 0 && _selectedRequest < _incomingRequests.Count)
                return _incomingRequests[_selectedRequest].User;
            if (_selectedOutgoingRequest >= 0 && _selectedOutgoingRequest < _outgoingRequests.Count)
                return _outgoingRequests[_selectedOutgoingRequest].User;
            return null;
        }

        if (_selectedBlocked < 0 || _selectedBlocked >= _blockedUsers.Count)
            return null;
        return _blockedUsers[_selectedBlocked];
    }

    private static string ResolveInviteUsername(GateWorldInviteEntry invite)
    {
        var username = (invite.SenderDisplayName ?? string.Empty).Trim();
        return string.IsNullOrWhiteSpace(username) ? "PLAYER" : username;
    }

    private string BuildFriendPresenceText(PlayerProfile.FriendEntry friend)
    {
        var id = (friend.UserId ?? string.Empty).Trim();
        if (!string.IsNullOrWhiteSpace(id) && TryGetPresenceDisplayText(id, out var presenceText))
            return presenceText;
        return FriendPresenceFormatter.BuildLabel(friend.LastKnownPresence);
    }

    private string GetSelectedSocialStatusText()
    {
        var user = GetSelectedSocialUser();
        var userId = (user?.ProductUserId ?? string.Empty).Trim();
        if (!string.IsNullOrWhiteSpace(userId) && TryGetPresenceDisplayText(userId, out var presenceText))
            return presenceText;
        return "OFFLINE";
    }

    private bool TryGetPresenceDisplayText(string userId, out string text)
    {
        userId = (userId ?? string.Empty).Trim();
        if (!string.IsNullOrWhiteSpace(userId))
        {
            var snapshot = _socialState.GetSnapshot();
            if (snapshot.PresenceByUserId.TryGetValue(userId, out var presence))
            {
                text = FriendPresenceFormatter.BuildLabel(
                    presence.Status,
                    presence.IsHosting,
                    presence.IsInWorld,
                    presence.IsMultiplayer,
                    presence.WorldName,
                    presence.GameMode);
                return true;
            }

            if (_gate.CanUseOfficialOnline(_log, out _))
            {
                text = "OFFLINE";
                return true;
            }
        }

        text = string.Empty;
        return false;
    }

    private bool TryGetSelectedPresenceDetails(out string primaryStatus, out string worldName, out string gameMode)
    {
        primaryStatus = string.Empty;
        worldName = string.Empty;
        gameMode = string.Empty;

        var user = GetSelectedSocialUser();
        var userId = (user?.ProductUserId ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(userId))
            return false;

        var snapshot = _socialState.GetSnapshot();
        if (snapshot.PresenceByUserId.TryGetValue(userId, out var presence))
        {
            primaryStatus = FriendPresenceFormatter.BuildPrimaryStatus(
                presence.Status,
                presence.IsHosting,
                presence.IsInWorld,
                presence.IsMultiplayer,
                presence.WorldName,
                presence.GameMode);
            worldName = (presence.WorldName ?? string.Empty).Trim();
            gameMode = (presence.GameMode ?? string.Empty).Trim();
            return true;
        }

        if (_friendsListMode == ProfileScreenFriendsMode.Friends
            && _selectedFriend >= 0
            && _selectedFriend < _profile.Friends.Count)
        {
            var known = (_profile.Friends[_selectedFriend].LastKnownPresence ?? string.Empty).Trim();
            if (known.Contains("IN WORLD", StringComparison.OrdinalIgnoreCase))
            {
                primaryStatus = "IN WORLD";
                var firstQuote = known.IndexOf('"');
                var lastQuote = known.LastIndexOf('"');
                if (firstQuote >= 0 && lastQuote > firstQuote)
                    worldName = known.Substring(firstQuote + 1, lastQuote - firstQuote - 1).Trim();
                return true;
            }
        }

        return false;
    }

    private void DrawContainedScrollingText(SpriteBatch sb, string text, Rectangle rect, Color color, float timer)
    {
        text = (text ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(text) || rect.Width <= 0)
            return;

        var charWidth = Math.Max(1f, _font.MeasureString("W").X);
        var maxChars = Math.Max(1, (int)(rect.Width / charWidth));
        var visible = text;
        if (text.Length > maxChars)
        {
            var padded = text + "   " + text;
            var scrollable = text.Length + 3;
            var step = Math.Max(0, (int)MathF.Floor(Math.Max(0f, timer - 1.5f) * 2.25f) % scrollable);
            visible = padded.Substring(step, Math.Min(maxChars, padded.Length - step));
        }

        sb.Draw(_pixel, rect, new Color(0, 0, 0, 80));
        _font.DrawString(sb, visible, new Vector2(rect.X, rect.Y), color);
    }

    private void DrawFriendsModeTabs(SpriteBatch sb)
    {
        var friendsColor = _friendsListMode == ProfileScreenFriendsMode.Friends ? new Color(60, 60, 60, 220) : new Color(30, 30, 30, 220);
        var requestsColor = _friendsListMode == ProfileScreenFriendsMode.Requests ? new Color(60, 60, 60, 220) : new Color(30, 30, 30, 220);
        var blockedColor = _friendsListMode == ProfileScreenFriendsMode.Blocked ? new Color(60, 60, 60, 220) : new Color(30, 30, 30, 220);
        sb.Draw(_pixel, _friendsModeFriendsRect, friendsColor);
        sb.Draw(_pixel, _friendsModeRequestsRect, requestsColor);
        sb.Draw(_pixel, _friendsModeBlockedRect, blockedColor);
        DrawBorder(sb, _friendsModeFriendsRect, Color.White);
        DrawBorder(sb, _friendsModeRequestsRect, Color.White);
        DrawBorder(sb, _friendsModeBlockedRect, Color.White);
    }

    private void HandleListSelection(InputState input)
    {
        if (!input.IsNewLeftClick())
            return;

        var activeListRect = _activeTab == ProfileScreenStartTab.Invites ? _friendsRect : _friendsListRect;
        if (!activeListRect.Contains(input.MousePosition))
            return;

        if (_activeTab == ProfileScreenStartTab.Invites)
        {
            for (var i = 0; i < _inviteAcceptActionRects.Count && i < _worldInvites.Count; i++)
            {
                if (_inviteAcceptActionRects[i].Contains(input.MousePosition))
                {
                    _selectedInvite = i;
                    _ = AcceptWorldInviteAsync(i);
                    return;
                }

                if (_inviteDeclineActionRects[i].Contains(input.MousePosition))
                {
                    _selectedInvite = i;
                    _ = DeclineWorldInviteAsync(i);
                    return;
                }
            }

            var selectedIndex = ResolveFriendsRowIndex(input.MousePosition.Y, _worldInvites.Count);
            if (selectedIndex < 0 || selectedIndex >= _worldInvites.Count)
                return;
            _selectedInvite = selectedIndex;
            return;
        }

        if (_friendsListMode == ProfileScreenFriendsMode.Friends)
        {
            var selectedIndex = ResolveFriendsRowIndex(input.MousePosition.Y, _profile.Friends.Count);
            if (selectedIndex < 0 || selectedIndex >= _profile.Friends.Count)
                return;
            _selectedFriend = selectedIndex;
            return;
        }

        if (_friendsListMode == ProfileScreenFriendsMode.Requests)
        {
            var selectedIndex = ResolveIncomingRequestRowIndex(input.MousePosition.Y);
            if (selectedIndex >= 0 && selectedIndex < _incomingRequests.Count)
            {
                _selectedRequest = selectedIndex;
                _selectedOutgoingRequest = -1;
                return;
            }

            var outgoingIndex = ResolveOutgoingRequestRowIndex(input.MousePosition.Y);
            if (outgoingIndex < 0 || outgoingIndex >= _outgoingRequests.Count)
                return;
            _selectedOutgoingRequest = outgoingIndex;
            _selectedRequest = -1;
            return;
        }

        var blockedIndex = ResolveFriendsRowIndex(input.MousePosition.Y, _blockedUsers.Count);
        if (blockedIndex < 0 || blockedIndex >= _blockedUsers.Count)
            return;
        _selectedBlocked = blockedIndex;
    }

    private int ResolveFriendsRowIndex(int mouseY, int count)
    {
        var rowHeight = Math.Max(54, (_font.LineHeight * 2) + 14);
        var startY = _friendsListRect.Y + _font.LineHeight + 12;
        var relY = mouseY - startY;
        if (relY < 0)
            return -1;

        var stride = rowHeight + 6;
        var index = relY / stride;
        return index >= count ? -1 : index;
    }

    private int ResolveIncomingRequestRowIndex(int mouseY)
    {
        var rowHeight = Math.Max(54, (_font.LineHeight * 2) + 14);
        var y = _friendsListRect.Y + _font.LineHeight + 12;
        if (_incomingRequests.Count > 0)
            y += _font.LineHeight + 4;

        var relY = mouseY - y;
        if (relY < 0)
            return -1;

        var index = relY / (rowHeight + 6);
        return index >= _incomingRequests.Count ? -1 : index;
    }

    private int ResolveOutgoingRequestRowIndex(int mouseY)
    {
        if (_outgoingRequests.Count == 0)
            return -1;

        var rowHeight = Math.Max(54, (_font.LineHeight * 2) + 14);
        var y = _friendsListRect.Y + _font.LineHeight + 12;
        if (_incomingRequests.Count > 0)
        {
            y += _font.LineHeight + 4;
            y += _incomingRequests.Count * (rowHeight + 6);
        }

        y += 8;
        y += _font.LineHeight + 4;

        var relY = mouseY - y;
        if (relY < 0)
            return -1;

        var index = relY / (rowHeight + 6);
        return index >= _outgoingRequests.Count ? -1 : index;
    }

    private void OpenAddFriend()
    {
        _menus.Push(new AddFriendScreen(_menus, _assets, _font, _pixel, _log, _profile, _graphics, _eos), _viewport);
    }

    private async Task RemoveFriendAsync()
    {
        if (_selectedFriend < 0 || _selectedFriend >= _profile.Friends.Count)
        {
            SetStatus("Select a friend first.");
            return;
        }

        var entry = _profile.Friends[_selectedFriend];
        if (_gate.CanUseOfficialOnline(_log, out _))
        {
            var remove = await _gate.RemoveFriendAsync(entry.UserId);
            if (!remove.Ok)
            {
                if (IsMissingFriendsEndpointError(remove.Message))
                {
                    _friendsEndpointUnavailable = true;
                    _nextFriendsSyncUtc = DateTime.UtcNow.AddSeconds(45);
                    _profile.Friends.RemoveAt(_selectedFriend);
                    _selectedFriend = Math.Min(_selectedFriend, _profile.Friends.Count - 1);
                    _profile.Save(_log);
                    SetStatus($"Removed {ResolveFriendUsername(entry)} (local fallback).");
                    return;
                }
                SetStatus(string.IsNullOrWhiteSpace(remove.Message) ? "Failed to remove friend." : remove.Message);
                _nextFriendsSyncUtc = DateTime.UtcNow.AddSeconds(8);
                return;
            }

            await SyncCanonicalFriendsAsync(seedFromLocal: false);
            _selectedFriend = Math.Min(_selectedFriend, _profile.Friends.Count - 1);
            SetStatus($"Removed {ResolveFriendUsername(entry)}.");
            return;
        }

        _profile.Friends.RemoveAt(_selectedFriend);
        _selectedFriend = Math.Min(_selectedFriend, _profile.Friends.Count - 1);
        _profile.Save(_log);
        SetStatus($"Removed {ResolveFriendUsername(entry)}.");
    }

    private async Task AcceptRequestAsync()
    {
        if (_selectedRequest < 0 || _selectedRequest >= _incomingRequests.Count)
        {
            SetStatus("Select a request first.");
            return;
        }

        var req = _incomingRequests[_selectedRequest];
        var result = await _gate.RespondToFriendRequestAsync(requesterProductUserId: req.ProductUserId, accept: true);
        if (!result.Ok)
        {
            SetStatus(result.Message ?? "Failed to accept request.");
            return;
        }

        await SyncCanonicalFriendsAsync(seedFromLocal: false);
        SetStatus($"Accepted {ResolveGateUsername(req.User)}.");
    }

    private async Task DenyRequestAsync()
    {
        if (_selectedRequest < 0 || _selectedRequest >= _incomingRequests.Count)
        {
            SetStatus("Select a request first.");
            return;
        }

        var req = _incomingRequests[_selectedRequest];
        var result = await _gate.RespondToFriendRequestAsync(requesterProductUserId: req.ProductUserId, accept: false);
        if (!result.Ok)
        {
            SetStatus(result.Message ?? "Failed to deny request.");
            return;
        }

        await SyncCanonicalFriendsAsync(seedFromLocal: false);
        SetStatus($"Denied {ResolveGateUsername(req.User)}.");
    }

    private async Task CancelOutgoingRequestAsync()
    {
        if (_selectedOutgoingRequest < 0 || _selectedOutgoingRequest >= _outgoingRequests.Count)
        {
            SetStatus("Select an outgoing request first.");
            return;
        }

        var req = _outgoingRequests[_selectedOutgoingRequest];
        var result = await _gate.CancelOutgoingFriendRequestAsync(req.ProductUserId);
        if (!result.Ok)
        {
            SetStatus(result.Message ?? "Failed to cancel request.");
            return;
        }

        var cancelledName = ResolveGateUsername(req.User);
        _outgoingRequests.RemoveAt(_selectedOutgoingRequest);
        _selectedOutgoingRequest = Math.Min(_selectedOutgoingRequest, _outgoingRequests.Count - 1);
        await SyncCanonicalFriendsAsync(seedFromLocal: false);
        SetStatus($"Cancelled request to {cancelledName}.");
    }

    private async Task BlockSelectedUserAsync()
    {
        string targetId;
        if (_friendsListMode == ProfileScreenFriendsMode.Friends)
        {
            if (_selectedFriend < 0 || _selectedFriend >= _profile.Friends.Count)
            {
                SetStatus("Select a friend to block.");
                return;
            }

            targetId = (_profile.Friends[_selectedFriend].UserId ?? string.Empty).Trim();
        }
        else if (_friendsListMode == ProfileScreenFriendsMode.Requests)
        {
            if (_selectedRequest < 0 || _selectedRequest >= _incomingRequests.Count)
            {
                SetStatus("Select a request to block.");
                return;
            }

            targetId = (_incomingRequests[_selectedRequest].ProductUserId ?? string.Empty).Trim();
        }
        else
        {
            SetStatus("Select a friend or request to block.");
            return;
        }

        if (string.IsNullOrWhiteSpace(targetId))
        {
            SetStatus("Target ID unavailable.");
            return;
        }

        if (!_gate.CanUseOfficialOnline(_log, out var gateDenied))
        {
            SetStatus(gateDenied);
            return;
        }

        var block = await _gate.BlockUserAsync(targetId);
        if (!block.Ok)
        {
            if (IsMissingFriendsEndpointError(block.Message))
            {
                _friendsEndpointUnavailable = true;
                _nextFriendsSyncUtc = DateTime.UtcNow.AddSeconds(45);
            }

            SetStatus(string.IsNullOrWhiteSpace(block.Message) ? "Could not block user." : block.Message);
            return;
        }

        await SyncCanonicalFriendsAsync(seedFromLocal: false);
        SetStatus("User blocked.");
        _friendsListMode = ProfileScreenFriendsMode.Blocked;
    }

    private async Task UnblockSelectedUserAsync()
    {
        if (_selectedBlocked < 0 || _selectedBlocked >= _blockedUsers.Count)
        {
            SetStatus("Select a blocked user first.");
            return;
        }

        if (!_gate.CanUseOfficialOnline(_log, out var gateDenied))
        {
            SetStatus(gateDenied);
            return;
        }

        var targetId = (_blockedUsers[_selectedBlocked].ProductUserId ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(targetId))
        {
            SetStatus("Target ID unavailable.");
            return;
        }

        var unblock = await _gate.UnblockUserAsync(targetId);
        if (!unblock.Ok)
        {
            if (IsMissingFriendsEndpointError(unblock.Message))
            {
                _friendsEndpointUnavailable = true;
                _nextFriendsSyncUtc = DateTime.UtcNow.AddSeconds(45);
            }

            SetStatus(string.IsNullOrWhiteSpace(unblock.Message) ? "Could not unblock user." : unblock.Message);
            return;
        }

        await SyncCanonicalFriendsAsync(seedFromLocal: false);
        SetStatus("User unblocked.");
    }

    private async Task<bool> SyncCanonicalFriendsAsync(bool seedFromLocal)
    {
        if (_friendsSyncInProgress || !_gate.CanUseOfficialOnline(_log, out _))
        {
            _nextFriendsSyncUtc = DateTime.UtcNow.AddSeconds(5);
            return false;
        }

        _friendsSyncInProgress = true;
        try
        {
            var serverFriends = await _gate.GetFriendsAsync();
            if (!serverFriends.Ok)
            {
                if (IsMissingFriendsEndpointError(serverFriends.Message))
                {
                    if (!_friendsEndpointUnavailable)
                        SetStatus(string.IsNullOrWhiteSpace(serverFriends.Message) ? "Server friends endpoint unavailable." : serverFriends.Message);
                    _friendsEndpointUnavailable = true;
                    _friendsSeedAttempted = true;
                    _nextFriendsSyncUtc = DateTime.UtcNow.AddSeconds(45);
                    return false;
                }

                _nextFriendsSyncUtc = DateTime.UtcNow.AddSeconds(8);
                return false;
            }

            _friendsEndpointUnavailable = false;

            if (seedFromLocal && serverFriends.Friends.Count == 0 && _profile.Friends.Count > 0)
            {
                _friendsSeedAttempted = true;
                _incomingRequests.Clear();
                foreach (var request in serverFriends.IncomingRequests)
                {
                    if (request?.User == null)
                        continue;
                    _incomingRequests.Add(request);
                }

                _outgoingRequests.Clear();
                foreach (var request in serverFriends.OutgoingRequests)
                {
                    if (request?.User == null)
                        continue;
                    _outgoingRequests.Add(request);
                }

                _blockedUsers.Clear();
                foreach (var blocked in serverFriends.BlockedUsers)
                {
                    if (blocked == null)
                        continue;
                    _blockedUsers.Add(blocked);
                }

                _selectedRequest = Math.Min(_selectedRequest, _incomingRequests.Count - 1);
                _selectedBlocked = Math.Min(_selectedBlocked, _blockedUsers.Count - 1);
                _nextFriendsSyncUtc = DateTime.UtcNow.AddSeconds(8);
                return true;
            }

            var existingPresence = _profile.Friends
                .Where(f => !string.IsNullOrWhiteSpace(f.UserId))
                .ToDictionary(
                    f => f.UserId,
                    f => f.LastKnownPresence ?? string.Empty,
                    StringComparer.OrdinalIgnoreCase);
            var existingProfiles = _profile.Friends
                .Where(f => !string.IsNullOrWhiteSpace(f.UserId))
                .ToDictionary(
                    f => f.UserId,
                    f => f,
                    StringComparer.OrdinalIgnoreCase);

            _profile.Friends.Clear();
            foreach (var user in serverFriends.Friends)
            {
                var id = (user.ProductUserId ?? string.Empty).Trim();
                if (string.IsNullOrWhiteSpace(id))
                    continue;

                var label = string.IsNullOrWhiteSpace(user.DisplayName) ? user.Username : user.DisplayName;
                _profile.AddOrUpdateFriend(id, label);
                var friend = _profile.Friends.FirstOrDefault(f => string.Equals(f.UserId, id, StringComparison.OrdinalIgnoreCase));
                if (friend == null)
                    continue;
                friend.LastKnownDisplayName = label;
                if (existingPresence.TryGetValue(id, out var presence))
                    friend.LastKnownPresence = presence;
                if (existingProfiles.TryGetValue(id, out var cached))
                {
                    friend.PictureUrl = (cached.PictureUrl ?? string.Empty).Trim();
                    friend.BannerUrl = (cached.BannerUrl ?? string.Empty).Trim();
                    friend.AboutMe = (cached.AboutMe ?? string.Empty).Trim();
                }
                if (!string.IsNullOrWhiteSpace(user.PictureUrl))
                    friend.PictureUrl = user.PictureUrl.Trim();
                if (!string.IsNullOrWhiteSpace(user.BannerUrl))
                    friend.BannerUrl = user.BannerUrl.Trim();
                if (!string.IsNullOrWhiteSpace(user.AboutMe))
                    friend.AboutMe = NormalizeMultiline(user.AboutMe);
            }

            _incomingRequests.Clear();
            foreach (var request in serverFriends.IncomingRequests)
            {
                if (request?.User == null)
                    continue;
                _incomingRequests.Add(request);
            }

            _outgoingRequests.Clear();
            foreach (var request in serverFriends.OutgoingRequests)
            {
                if (request?.User == null)
                    continue;
                _outgoingRequests.Add(request);
            }

            _blockedUsers.Clear();
            foreach (var blocked in serverFriends.BlockedUsers)
            {
                if (blocked == null)
                    continue;
                _blockedUsers.Add(blocked);
            }

            SeedWorldInvitesFromSnapshot();

            _profile.Save(_log);
            _selectedFriend = Math.Min(_selectedFriend, _profile.Friends.Count - 1);
            _selectedRequest = Math.Min(_selectedRequest, _incomingRequests.Count - 1);
            _selectedBlocked = Math.Min(_selectedBlocked, _blockedUsers.Count - 1);
            _nextFriendsSyncUtc = DateTime.UtcNow.AddSeconds(8);
            return true;
        }
        catch (Exception ex)
        {
            _log.Warn($"Profile canonical friends sync failed: {ex.Message}");
            _nextFriendsSyncUtc = DateTime.UtcNow.AddSeconds(8);
            return false;
        }
        finally
        {
            _friendsSyncInProgress = false;
        }
    }

    private async Task RefreshSavedFriendsPresenceAsync()
    {
        if (_friendsPresenceRefreshInProgress)
            return;

        var ids = _profile.Friends
            .Select(friend => (friend.UserId ?? string.Empty).Trim())
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (ids.Length == 0)
        {
            _nextFriendsPresenceRefreshUtc = DateTime.UtcNow.AddSeconds(5);
            return;
        }

        if (!_gate.CanUseOfficialOnline(_log, out _))
        {
            _nextFriendsPresenceRefreshUtc = DateTime.UtcNow.AddSeconds(5);
            return;
        }

        _friendsPresenceRefreshInProgress = true;
        try
        {
            var presenceResult = await _gate.QueryPresenceAsync(ids).ConfigureAwait(false);
            var presenceById = new Dictionary<string, GatePresenceEntry>(StringComparer.OrdinalIgnoreCase);
            if (presenceResult.Ok)
            {
                for (var i = 0; i < presenceResult.Entries.Count; i++)
                {
                    var entry = presenceResult.Entries[i];
                    var id = (entry.ProductUserId ?? string.Empty).Trim();
                    if (!string.IsNullOrWhiteSpace(id))
                        presenceById[id] = entry;
                }
            }

            var socialSnapshot = _socialState.GetSnapshot();
            foreach (var pair in socialSnapshot.PresenceByUserId)
            {
                if (!presenceById.ContainsKey(pair.Key))
                    presenceById[pair.Key] = pair.Value;
            }

            var changed = false;
            for (var i = 0; i < _profile.Friends.Count; i++)
            {
                var friend = _profile.Friends[i];
                var id = (friend.UserId ?? string.Empty).Trim();
                if (string.IsNullOrWhiteSpace(id))
                    continue;

                string resolvedPresence;
                string? resolvedDisplayName = null;
                if (presenceById.TryGetValue(id, out var presence))
                {
                    resolvedDisplayName = string.IsNullOrWhiteSpace(presence.DisplayName)
                        ? null
                        : presence.DisplayName.Trim();
                    resolvedPresence = FriendPresenceFormatter.BuildLabel(
                        presence.Status,
                        presence.IsHosting,
                        presence.IsInWorld,
                        presence.IsMultiplayer,
                        presence.WorldName,
                        presence.GameMode);
                }
                else
                    resolvedPresence = "OFFLINE";

                if (!string.IsNullOrWhiteSpace(resolvedDisplayName)
                    && !string.Equals(friend.LastKnownDisplayName, resolvedDisplayName, StringComparison.Ordinal))
                {
                    friend.LastKnownDisplayName = resolvedDisplayName;
                    changed = true;
                }

                if (!string.Equals(friend.LastKnownPresence, resolvedPresence, StringComparison.Ordinal))
                {
                    friend.LastKnownPresence = resolvedPresence;
                    changed = true;
                }
            }

            if (changed)
                _profile.Save(_log);

            _nextFriendsPresenceRefreshUtc = DateTime.UtcNow.AddSeconds(presenceResult.Ok ? 3 : 6);
        }
        catch (Exception ex)
        {
            _log.Warn($"Profile saved-friends presence refresh failed: {ex.Message}");
            _nextFriendsPresenceRefreshUtc = DateTime.UtcNow.AddSeconds(6);
        }
        finally
        {
            _friendsPresenceRefreshInProgress = false;
        }
    }

    private void SeedFromSocialSnapshot()
    {
        var snapshot = _socialState.GetSnapshot();
        var cachedIncoming = _incomingRequests.ToArray();
        var cachedOutgoing = _outgoingRequests.ToArray();
        var cachedBlocked = _blockedUsers.ToArray();
        _incomingRequests.Clear();
        _incomingRequests.AddRange(MergeRequests(snapshot.IncomingRequests, cachedIncoming));
        _outgoingRequests.Clear();
        _outgoingRequests.AddRange(MergeRequests(snapshot.OutgoingRequests, cachedOutgoing));
        _blockedUsers.Clear();
        _blockedUsers.AddRange(MergeUsers(snapshot.BlockedUsers, cachedBlocked));
        SeedWorldInvitesFromSnapshot(snapshot);
    }

    private List<GateFriendRequest> MergeRequests(IReadOnlyCollection<GateFriendRequest> fresh, IReadOnlyCollection<GateFriendRequest> cached)
    {
        var cachedById = (cached ?? Array.Empty<GateFriendRequest>())
            .Where(entry => entry != null)
            .ToDictionary(
                entry => (entry.ProductUserId ?? entry.User?.ProductUserId ?? string.Empty).Trim(),
                entry => entry,
                StringComparer.OrdinalIgnoreCase);

        var merged = new List<GateFriendRequest>();
        foreach (var entry in fresh ?? Array.Empty<GateFriendRequest>())
        {
            if (entry == null)
                continue;

            var id = (entry.ProductUserId ?? entry.User?.ProductUserId ?? string.Empty).Trim();
            cachedById.TryGetValue(id, out var previous);
            merged.Add(new GateFriendRequest
            {
                ProductUserId = id,
                RequestedUtc = entry.RequestedUtc,
                User = MergeUser(entry.User, previous?.User)
            });
        }

        return merged;
    }

    private List<GateIdentityUser> MergeUsers(IReadOnlyCollection<GateIdentityUser> fresh, IReadOnlyCollection<GateIdentityUser> cached)
    {
        var cachedById = (cached ?? Array.Empty<GateIdentityUser>())
            .Where(user => user != null)
            .ToDictionary(
                user => (user.ProductUserId ?? string.Empty).Trim(),
                user => user,
                StringComparer.OrdinalIgnoreCase);

        var merged = new List<GateIdentityUser>();
        foreach (var user in fresh ?? Array.Empty<GateIdentityUser>())
        {
            if (user == null)
                continue;

            var id = (user.ProductUserId ?? string.Empty).Trim();
            cachedById.TryGetValue(id, out var previous);
            merged.Add(MergeUser(user, previous));
        }

        return merged;
    }

    private GateIdentityUser MergeUser(GateIdentityUser? fresh, GateIdentityUser? cached)
    {
        if (fresh == null && cached == null)
            return new GateIdentityUser();

        var fallback = cached ?? new GateIdentityUser();
        var current = fresh ?? new GateIdentityUser();

        return new GateIdentityUser
        {
            ProductUserId = !string.IsNullOrWhiteSpace(current.ProductUserId) ? current.ProductUserId.Trim() : (fallback.ProductUserId ?? string.Empty).Trim(),
            Username = !string.IsNullOrWhiteSpace(current.Username) ? current.Username.Trim() : (fallback.Username ?? string.Empty).Trim(),
            DisplayName = !string.IsNullOrWhiteSpace(current.DisplayName) ? current.DisplayName.Trim() : (fallback.DisplayName ?? string.Empty).Trim(),
            FriendCode = !string.IsNullOrWhiteSpace(current.FriendCode) ? current.FriendCode.Trim() : (fallback.FriendCode ?? string.Empty).Trim(),
            PictureUrl = !string.IsNullOrWhiteSpace(current.PictureUrl) ? current.PictureUrl.Trim() : (fallback.PictureUrl ?? string.Empty).Trim(),
            BannerUrl = !string.IsNullOrWhiteSpace(current.BannerUrl) ? current.BannerUrl.Trim() : (fallback.BannerUrl ?? string.Empty).Trim(),
            AboutMe = !string.IsNullOrWhiteSpace(current.AboutMe) ? NormalizeMultiline(current.AboutMe) : NormalizeMultiline(fallback.AboutMe)
        };
    }

    private void SeedWorldInvitesFromSnapshot()
        => SeedWorldInvitesFromSnapshot(_socialState.GetSnapshot());

    private void SeedWorldInvitesFromSnapshot(OnlineSocialSnapshot snapshot)
    {
        _worldInvites.Clear();
        _inviteAcceptActionRects.Clear();
        _inviteDeclineActionRects.Clear();
        for (var i = 0; i < snapshot.WorldInvites.Count; i++)
        {
            var invite = snapshot.WorldInvites[i];
            if (string.Equals((invite.Status ?? string.Empty).Trim(), "pending", StringComparison.OrdinalIgnoreCase))
            {
                _worldInvites.Add(invite);
                QueueSocialAvatarLoad((invite.SenderProductUserId ?? string.Empty).Trim(), (invite.SenderPictureUrl ?? string.Empty).Trim());
            }
        }

        _selectedInvite = Math.Min(_selectedInvite, _worldInvites.Count - 1);
    }

    private async Task HandlePrimaryActionAsync()
    {
        if (_activeTab == ProfileScreenStartTab.Invites)
        {
            await AcceptWorldInviteAsync().ConfigureAwait(false);
            return;
        }

        if (_selectedOutgoingRequest >= 0 && _selectedOutgoingRequest < _outgoingRequests.Count)
        {
            SetStatus($"Request already sent to {ResolveGateUsername(_outgoingRequests[_selectedOutgoingRequest].User)}.");
            return;
        }

        await AcceptRequestAsync().ConfigureAwait(false);
    }

    private async Task HandleSecondaryActionAsync()
    {
        if (_activeTab == ProfileScreenStartTab.Invites)
        {
            await DeclineWorldInviteAsync().ConfigureAwait(false);
            return;
        }

        if (_selectedOutgoingRequest >= 0 && _selectedOutgoingRequest < _outgoingRequests.Count)
        {
            await CancelOutgoingRequestAsync().ConfigureAwait(false);
            return;
        }

        await DenyRequestAsync().ConfigureAwait(false);
    }

    private async Task AcceptWorldInviteAsync(int inviteIndex = -1)
    {
        if (inviteIndex >= 0)
            _selectedInvite = inviteIndex;

        if (_selectedInvite < 0 || _selectedInvite >= _worldInvites.Count)
        {
            SetStatus("Select an invite first.");
            return;
        }

        var invite = _worldInvites[_selectedInvite];
        var senderId = (invite.SenderProductUserId ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(senderId))
        {
            SetStatus("Invite sender unavailable.");
            return;
        }

        var accepted = await _gate.RespondToWorldInviteAsync(senderId, "accepted");
        if (!accepted)
        {
            SetStatus("Failed to accept invite.");
            return;
        }

        _worldInvites.RemoveAt(_selectedInvite);
        _selectedInvite = Math.Min(_selectedInvite, _worldInvites.Count - 1);
        _socialState.MarkNotificationsSeen();
        _menus.Push(
            new MultiplayerScreen(
                _menus,
                _assets,
                _font,
                _pixel,
                _log,
                _profile,
                _graphics,
                _eos,
                preferredInviteSenderId: senderId,
                preferredInviteSenderDisplayName: (invite.SenderDisplayName ?? string.Empty).Trim(),
                preferredInviteJoinTarget: (invite.SenderJoinTarget ?? string.Empty).Trim(),
                preferredInviteSenderPictureUrl: (invite.SenderPictureUrl ?? string.Empty).Trim(),
                preferredInviteWorldName: (invite.WorldName ?? string.Empty).Trim(),
                preferredInviteGameMode: (invite.GameMode ?? string.Empty).Trim(),
                autoJoinPreferredInvite: true),
            _viewport);
    }

    private async Task DeclineWorldInviteAsync(int inviteIndex = -1)
    {
        if (inviteIndex >= 0)
            _selectedInvite = inviteIndex;

        if (_selectedInvite < 0 || _selectedInvite >= _worldInvites.Count)
        {
            SetStatus("Select an invite first.");
            return;
        }

        var invite = _worldInvites[_selectedInvite];
        var senderId = (invite.SenderProductUserId ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(senderId))
        {
            SetStatus("Invite sender unavailable.");
            return;
        }

        var declined = await _gate.RespondToWorldInviteAsync(senderId, "declined");
        if (!declined)
        {
            SetStatus("Failed to decline invite.");
            return;
        }

        _worldInvites.RemoveAt(_selectedInvite);
        _selectedInvite = Math.Min(_selectedInvite, _worldInvites.Count - 1);
        SetStatus("Invite declined.");
    }

    private void DrawInvitesTab(SpriteBatch sb)
    {
        sb.Draw(_pixel, _friendsRect, new Color(20, 20, 20, 220));
        DrawBorder(sb, _friendsRect, Color.White);
        _inviteAcceptActionRects.Clear();
        _inviteDeclineActionRects.Clear();

        _font.DrawString(sb, "WORLD INVITES", new Vector2(_friendsRect.X + 8, _friendsRect.Y + 6), Color.White);

        if (_worldInvites.Count == 0)
        {
            _font.DrawString(sb, "(no pending world invites)", new Vector2(_friendsRect.X + 8, _friendsRect.Y + _font.LineHeight + 10), new Color(180, 180, 180));
            return;
        }

        var rowHeight = Math.Max(54, (_font.LineHeight * 2) + 14);
        var y = _friendsRect.Y + _font.LineHeight + 12;
        for (var i = 0; i < _worldInvites.Count; i++)
        {
            var invite = _worldInvites[i];
            var username = ResolveInviteUsername(invite);
            var worldName = string.IsNullOrWhiteSpace(invite.WorldName) ? "World invite" : invite.WorldName.Trim();
            var mode = string.IsNullOrWhiteSpace(invite.GameMode) ? string.Empty : invite.GameMode.Trim();
            var secondary = string.IsNullOrWhiteSpace(mode) ? worldName : $"{worldName} : {mode}";
            var row = new Rectangle(_friendsRect.X + 6, y, _friendsRect.Width - 12, rowHeight);
            DrawSocialRow(sb, row, username, secondary, i == _selectedInvite, new Color(60, 104, 88, 220), new Color(200, 245, 220), TryGetSocialAvatarTexture((invite.SenderProductUserId ?? string.Empty).Trim()));
            var acceptRect = new Rectangle(row.Right - 76, row.Y + 10, 28, 28);
            var declineRect = new Rectangle(row.Right - 38, row.Y + 10, 28, 28);
            _inviteAcceptActionRects.Add(acceptRect);
            _inviteDeclineActionRects.Add(declineRect);
            DrawInviteActionBox(sb, acceptRect, new Color(48, 160, 80), isAccept: true);
            DrawInviteActionBox(sb, declineRect, new Color(176, 58, 58), isAccept: false);

            y += rowHeight + 6;
            if (y + rowHeight > _friendsRect.Bottom - 8)
                break;
        }
    }

    private void UpdateTabButtonStyles()
    {
        var activeTop = new Color(60, 60, 60, 220);
        var inactiveTop = new Color(30, 30, 30, 220);
        _tabIdentityBtn.BackgroundColor = _activeTab == ProfileScreenStartTab.Identity ? activeTop : inactiveTop;
        _tabFriendsBtn.BackgroundColor = _activeTab == ProfileScreenStartTab.Friends ? activeTop : inactiveTop;
        _tabInvitesBtn.BackgroundColor = _activeTab == ProfileScreenStartTab.Invites ? activeTop : inactiveTop;

        var activeSub = new Color(60, 60, 60, 220);
        var inactiveSub = new Color(30, 30, 30, 220);
        _friendsModeFriendsBtn.BackgroundColor = _friendsListMode == ProfileScreenFriendsMode.Friends ? activeSub : inactiveSub;
        _friendsModeRequestsBtn.BackgroundColor = _friendsListMode == ProfileScreenFriendsMode.Requests ? activeSub : inactiveSub;
        _friendsModeBlockedBtn.BackgroundColor = _friendsListMode == ProfileScreenFriendsMode.Blocked ? activeSub : inactiveSub;
    }

    private void ShowFriendsUnavailableError()
    {
        _friendsEndpointUnavailable = true;
        _nextFriendsSyncUtc = DateTime.UtcNow.AddSeconds(45);
    }

    private static bool IsMissingFriendsEndpointError(string? message)
    {
        var value = (message ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(value))
            return false;

        return value.Contains("HTTP 404", StringComparison.OrdinalIgnoreCase)
            || value.Contains("Not Found", StringComparison.OrdinalIgnoreCase);
    }

    private void SetStatus(string msg, double seconds = 3.0)
    {
        _status = msg;
        _statusExpiryUtc = seconds <= 0 ? DateTime.MinValue : DateTime.UtcNow.AddSeconds(seconds);
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

    private void DrawInviteActionBox(SpriteBatch sb, Rectangle rect, Color fill, bool isAccept)
    {
        sb.Draw(_pixel, rect, fill);
        DrawBorder(sb, rect, Color.White);
        if (isAccept)
        {
            DrawLine(sb, rect.X + 6, rect.Y + 14, rect.X + 11, rect.Y + 19, Color.White, 2);
            DrawLine(sb, rect.X + 11, rect.Y + 19, rect.Right - 6, rect.Y + 8, Color.White, 2);
            return;
        }

        DrawLine(sb, rect.X + 7, rect.Y + 7, rect.Right - 7, rect.Bottom - 7, Color.White, 2);
        DrawLine(sb, rect.Right - 7, rect.Y + 7, rect.X + 7, rect.Bottom - 7, Color.White, 2);
    }

    private void DrawLine(SpriteBatch sb, int x0, int y0, int x1, int y1, Color color, int thickness)
    {
        var dx = Math.Abs(x1 - x0);
        var sx = x0 < x1 ? 1 : -1;
        var dy = -Math.Abs(y1 - y0);
        var sy = y0 < y1 ? 1 : -1;
        var err = dx + dy;

        while (true)
        {
            sb.Draw(_pixel, new Rectangle(x0 - (thickness / 2), y0 - (thickness / 2), thickness, thickness), color);
            if (x0 == x1 && y0 == y1)
                break;

            var e2 = 2 * err;
            if (e2 >= dy)
            {
                err += dy;
                x0 += sx;
            }

            if (e2 <= dx)
            {
                err += dx;
                y0 += sy;
            }
        }
    }

    private sealed class AvatarVisualState
    {
        public Task<byte[]?>? BytesTask { get; set; }
        public Texture2D? Texture { get; set; }
    }
}
