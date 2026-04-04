using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using WinClipboard = System.Windows.Forms.Clipboard;
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
        Friends
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
    private readonly VeilnetProfileClient _veilnetProfileClient;
    private readonly MemoryWebTextureLoader _webTextureLoader;

    private readonly Button _tabIdentityBtn;
    private readonly Button _tabFriendsBtn;
    private readonly Button _friendsModeFriendsBtn;
    private readonly Button _friendsModeRequestsBtn;
    private readonly Button _friendsModeBlockedBtn;
    private readonly Button _addFriendBtn;
    private readonly Button _removeFriendBtn;
    private readonly Button _acceptRequestBtn;
    private readonly Button _denyRequestBtn;
    private readonly Button _blockUserBtn;
    private readonly Button _unblockUserBtn;
    private readonly Button _copyIdBtn;
    private readonly Button _refreshProfileBtn;
    private readonly Button _backBtn;

    private Texture2D? _bg;
    private Texture2D? _panel;

    private Rectangle _viewport;
    private Rectangle _panelRect;
    private Rectangle _tabIdentityRect;
    private Rectangle _tabFriendsRect;
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

    private ProfileScreenStartTab _activeTab = ProfileScreenStartTab.Identity;
    private ProfileScreenFriendsMode _friendsListMode = ProfileScreenFriendsMode.Friends;
    private int _selectedFriend = -1;
    private int _selectedRequest = -1;
    private int _selectedBlocked = -1;
    private bool _friendsSyncInProgress;
    private bool _friendsSeedAttempted;
    private bool _friendsEndpointUnavailable;
    private DateTime _nextFriendsSyncUtc = DateTime.MinValue;
    private readonly List<GateFriendRequest> _incomingRequests = new();
    private readonly List<GateFriendRequest> _outgoingRequests = new();
    private readonly List<GateIdentityUser> _blockedUsers = new();

    private string _status = string.Empty;
    private DateTime _statusExpiryUtc = DateTime.MinValue;

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
        _veilnetProfileClient = new VeilnetProfileClient(_log);
        _webTextureLoader = new MemoryWebTextureLoader();
        _veilnetProfileRefreshInterval = ResolveProfileRefreshInterval();

        _activeTab = startTab;
        _friendsListMode = startFriendsMode;

        _tabIdentityBtn = new Button("PROFILE", () => _activeTab = ProfileScreenStartTab.Identity) { BoldText = true };
        _tabFriendsBtn = new Button("FRIENDS", () => _activeTab = ProfileScreenStartTab.Friends) { BoldText = true };
        _friendsModeFriendsBtn = new Button("FRIENDS", () => _friendsListMode = ProfileScreenFriendsMode.Friends) { BoldText = true };
        _friendsModeRequestsBtn = new Button("REQUESTS", () => _friendsListMode = ProfileScreenFriendsMode.Requests) { BoldText = true };
        _friendsModeBlockedBtn = new Button("BLOCKED", () => _friendsListMode = ProfileScreenFriendsMode.Blocked) { BoldText = true };
        _addFriendBtn = new Button("ADD FRIEND", OpenAddFriend) { BoldText = true };
        _removeFriendBtn = new Button("REMOVE FRIEND", () => _ = RemoveFriendAsync()) { BoldText = true };
        _acceptRequestBtn = new Button("ACCEPT", () => _ = AcceptRequestAsync()) { BoldText = true };
        _denyRequestBtn = new Button("DENY", () => _ = DenyRequestAsync()) { BoldText = true };
        _blockUserBtn = new Button("BLOCK", () => _ = BlockSelectedUserAsync()) { BoldText = true };
        _unblockUserBtn = new Button("UNBLOCK", () => _ = UnblockSelectedUserAsync()) { BoldText = true };
        _copyIdBtn = new Button("COPY MY ID", CopyLocalId) { BoldText = true };
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

        _lastVeilnetTokenSnapshot = ResolveVeilnetToken();
        if (IsUsableToken(_lastVeilnetTokenSnapshot))
            QueueVeilnetProfileRefresh(force: true);
        else
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
        var tabW = 200; 
        var tabH = _font.LineHeight + 12;
        _tabIdentityRect = new Rectangle(contentRect.X, tabY, tabW, tabH);
        _tabFriendsRect = new Rectangle(_tabIdentityRect.Right + 8, tabY, tabW, tabH);
        _tabIdentityBtn.Bounds = _tabIdentityRect;
        _tabFriendsBtn.Bounds = _tabFriendsRect;

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
        var modeW = 150; 
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

        var buttonY = contentRect.Bottom - actionAreaH;
        var gap = 8;
        var buttonH = Math.Max(44, _font.LineHeight * 2);
        
        // Identity view actions
        _copyIdBtn.Bounds = new Rectangle(contentRect.X, buttonY, contentRect.Width, buttonH);
        
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
        _backBtn.Update(input);

        if (_activeTab == ProfileScreenStartTab.Identity)
        {
            _copyIdBtn.Update(input);
            _refreshProfileBtn.Update(input);
            if (!_veilnetProfileRefreshInProgress && DateTime.UtcNow >= _nextVeilnetProfileRefreshUtc)
                QueueVeilnetProfileRefresh(force: false);
        }
        else
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
            _acceptRequestBtn.Enabled = _friendsListMode == ProfileScreenFriendsMode.Requests
                && _selectedRequest >= 0
                && _selectedRequest < _incomingRequests.Count;
            _denyRequestBtn.Enabled = _friendsListMode == ProfileScreenFriendsMode.Requests
                && _selectedRequest >= 0
                && _selectedRequest < _incomingRequests.Count;
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
            if (!_friendsSyncInProgress
                && (!_friendsEndpointUnavailable || DateTime.UtcNow >= _nextFriendsSyncUtc)
                && DateTime.UtcNow >= _nextFriendsSyncUtc)
                _ = SyncCanonicalFriendsAsync(seedFromLocal: !_friendsSeedAttempted);
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

        if (_panel is not null) sb.Draw(_panel, _panelRect, Color.White);
        else sb.Draw(_pixel, _panelRect, new Color(0, 0, 0, 200));

        DrawTabs(sb);

        if (_activeTab == ProfileScreenStartTab.Identity)
            DrawIdentityTab(sb);
        else
            DrawFriendsTab(sb);

        if (!string.IsNullOrWhiteSpace(_status))
        {
            var statusPos = new Vector2(_panelRect.X + 18, _panelRect.Bottom - _font.LineHeight - 8);
            _font.DrawString(sb, _status, statusPos, Color.White);
        }

        if (_activeTab == ProfileScreenStartTab.Identity)
        {
            _copyIdBtn.Draw(sb, _pixel, _font);
            _refreshProfileBtn.Draw(sb, _pixel, _font);
        }
        else
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

        _tabIdentityBtn.Draw(sb, _pixel, _font);
        _tabFriendsBtn.Draw(sb, _pixel, _font);
        _backBtn.Draw(sb, _pixel, _font);

        sb.End();
    }

    public void OnClose()
    {
        CancelVeilnetRefreshTask();
        DisposeVeilnetTextures();
        DisposeAvatarDecorTextures();
        _webTextureLoader.Dispose();
        ClearLegacyVeilnetCacheDir();
    }

    private void DrawTabs(SpriteBatch sb)
    {
        var identityColor = _activeTab == ProfileScreenStartTab.Identity ? new Color(60, 60, 60, 220) : new Color(30, 30, 30, 220);
        var friendsColor = _activeTab == ProfileScreenStartTab.Friends ? new Color(60, 60, 60, 220) : new Color(30, 30, 30, 220);
        sb.Draw(_pixel, _tabIdentityRect, identityColor);
        sb.Draw(_pixel, _tabFriendsRect, friendsColor);
        DrawBorder(sb, _tabIdentityRect, Color.White);
        DrawBorder(sb, _tabFriendsRect, Color.White);
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

            var noPicX = _identityAvatarRect.X + (_identityAvatarRect.Width / 2f) - (_font.MeasureString("NO PIC").X / 2f);
            var noPicY = _identityAvatarRect.Y + (_identityAvatarRect.Height - _font.LineHeight) / 2f;
            _font.DrawString(sb, "NO PIC", new Vector2(noPicX, noPicY), new Color(232, 236, 248));
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

        var eosSnapshot = EosRuntimeStatus.Evaluate(_eos);
        var id = (_eos?.LocalProductUserId ?? _identityStore.ProductUserId ?? string.Empty).Trim();
        var statusLines = $"EOS: {eosSnapshot.StatusText}\nMY ID: {(string.IsNullOrWhiteSpace(id) ? "(waiting...)" : id)}\nCONFIG: {EosRuntimeStatus.DescribeConfigSource()}";
        DrawIdentityInfoPanel(sb, _identityStatusRect, "STATUS", statusLines, infoBorder, infoTitle);
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
            DisposeVeilnetTextures();
            DisposeAvatarDecorTextures();
            _veilnetUsername = string.Empty;
            _veilnetAboutMe = string.Empty;
            _veilnetPictureUrl = string.Empty;
            _veilnetBannerUrl = string.Empty;
            _veilnetThemeColorRaw = string.Empty;
            _veilnetTheme = string.Empty;
            _veilnetUpdatedAt = string.Empty;
            _veilnetProfileError = "You are not signed in. Open launcher online login.";
            _lastVeilnetSyncOk = false;
            _lastVeilnetSyncError = "missing_access_token";
            _lastVeilnetProfileUtc = DateTime.MinValue;
            ApplyVeilnetTheme(string.Empty, string.Empty);
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
            _veilnetProfileError = "You are not signed in. Open launcher online login.";
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
            && !string.Equals(profile.PictureUrl, previousPicture, StringComparison.OrdinalIgnoreCase))
        {
            payload.AvatarBytes = await _webTextureLoader.DownloadImageBytesAsync(profile.PictureUrl, ct).ConfigureAwait(false);
        }

        if (!string.IsNullOrWhiteSpace(profile.BannerUrl)
            && !string.Equals(profile.BannerUrl, previousBanner, StringComparison.OrdinalIgnoreCase))
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
            SetStatus("Profile refresh failed.", 1.8);
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
        }
        else if (payload.AvatarBytes is { Length: > 0 })
        {
            var nextAvatar = CreateCircularAvatarTextureFromBytes(payload.AvatarBytes);
            if (nextAvatar != null)
            {
                _veilnetAvatarTexture?.Dispose();
                _veilnetAvatarTexture = nextAvatar;
                _veilnetPictureUrl = profile.PictureUrl.Trim();
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
        }
        else if (payload.BannerBytes is { Length: > 0 })
        {
            var nextBanner = _webTextureLoader.CreateTextureFromBytes(_graphics.GraphicsDevice, payload.BannerBytes);
            if (nextBanner != null)
            {
                _veilnetBannerTexture?.Dispose();
                _veilnetBannerTexture = nextBanner;
                _veilnetBannerUrl = profile.BannerUrl.Trim();
            }
            else
            {
                _veilnetProfileError = "Banner image could not be decoded.";
            }
        }

        SetStatus("Refreshed.", 1.4);
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

    private void DrawFriendsTab(SpriteBatch sb)
    {
        DrawFriendsModeTabs(sb);

        sb.Draw(_pixel, _friendsRect, new Color(20, 20, 20, 220));
        DrawBorder(sb, _friendsRect, Color.White);

        var title = _friendsListMode switch
        {
            ProfileScreenFriendsMode.Requests => "INCOMING REQUESTS",
            ProfileScreenFriendsMode.Blocked => "BLOCKED USERS",
            _ => "SAVED FRIENDS"
        };
        _font.DrawString(sb, title, new Vector2(_friendsRect.X + 8, _friendsRect.Y + 6), Color.White);

        if (_friendsListMode == ProfileScreenFriendsMode.Requests)
        {
            _font.DrawString(
                sb,
                $"OUTGOING: {_outgoingRequests.Count}",
                new Vector2(_friendsRect.Right - 160, _friendsRect.Y + 6),
                new Color(170, 170, 170));
        }

        var rowH = _font.LineHeight + 8;
        var y = _friendsRect.Y + _font.LineHeight + 12;
        if (_friendsListMode == ProfileScreenFriendsMode.Friends)
        {
            if (_profile.Friends.Count == 0)
            {
                _font.DrawString(sb, "(no friends saved yet)", new Vector2(_friendsRect.X + 8, _friendsRect.Y + _font.LineHeight + 10), new Color(180, 180, 180));
                return;
            }

            for (var i = 0; i < _profile.Friends.Count; i++)
            {
                var row = new Rectangle(_friendsRect.X + 6, y, _friendsRect.Width - 12, rowH);
                var selected = i == _selectedFriend;
                sb.Draw(_pixel, row, selected ? new Color(60, 90, 130, 220) : new Color(26, 26, 26, 200));
                DrawBorder(sb, row, selected ? new Color(200, 230, 255) : new Color(110, 110, 110));

                var f = _profile.Friends[i];
                var friendCode = EosIdentityStore.GenerateFriendCode(f.UserId);
                var displayName = !string.IsNullOrWhiteSpace(f.LastKnownDisplayName)
                    ? f.LastKnownDisplayName
                    : (!string.IsNullOrWhiteSpace(f.Label) ? f.Label : PlayerProfile.ShortId(f.UserId));
                var line = $"{displayName} ({friendCode})";
                _font.DrawString(sb, line, new Vector2(row.X + 8, row.Y + 3), Color.White);

                y += rowH + 4;
                if (y + rowH > _friendsRect.Bottom - 8)
                    break;
            }

            return;
        }

        if (_friendsListMode == ProfileScreenFriendsMode.Requests)
        {
            if (_incomingRequests.Count == 0 && _outgoingRequests.Count == 0)
            {
                _font.DrawString(sb, "(no pending requests)", new Vector2(_friendsRect.X + 8, _friendsRect.Y + _font.LineHeight + 10), new Color(180, 180, 180));
                return;
            }

            // Incoming
            if (_incomingRequests.Count > 0)
            {
                _font.DrawString(sb, "INCOMING:", new Vector2(_friendsRect.X + 8, y - 4), new Color(200, 200, 200));
                y += _font.LineHeight + 4;

                for (var i = 0; i < _incomingRequests.Count; i++)
                {
                    var row = new Rectangle(_friendsRect.X + 6, y, _friendsRect.Width - 12, rowH);
                    var selected = i == _selectedRequest;
                    sb.Draw(_pixel, row, selected ? new Color(90, 72, 40, 220) : new Color(26, 26, 26, 200));
                    DrawBorder(sb, row, selected ? new Color(240, 220, 160) : new Color(110, 110, 110));

                    var req = _incomingRequests[i];
                    var user = req.User;
                    var displayName = !string.IsNullOrWhiteSpace(user.DisplayName) ? user.DisplayName : PlayerProfile.ShortId(user.ProductUserId);
                    var line = $"{displayName} ({user.FriendCode})";
                    _font.DrawString(sb, line, new Vector2(row.X + 8, row.Y + 3), Color.White);

                    y += rowH + 4;
                    if (y + rowH > _friendsRect.Bottom - 8)
                        break;
                }
            }

            // Outgoing
            if (_outgoingRequests.Count > 0 && y + rowH + 20 < _friendsRect.Bottom)
            {
                y += 8;
                _font.DrawString(sb, "OUTGOING:", new Vector2(_friendsRect.X + 8, y), new Color(200, 200, 200));
                y += _font.LineHeight + 4;

                for (var i = 0; i < _outgoingRequests.Count; i++)
                {
                    var row = new Rectangle(_friendsRect.X + 6, y, _friendsRect.Width - 12, rowH);
                    sb.Draw(_pixel, row, new Color(20, 20, 20, 180));
                    DrawBorder(sb, row, new Color(80, 80, 80));

                    var req = _outgoingRequests[i];
                    var user = req.User;
                    var displayName = !string.IsNullOrWhiteSpace(user.DisplayName) ? user.DisplayName : PlayerProfile.ShortId(user.ProductUserId);
                    var line = $"{displayName} ({user.FriendCode}) [PENDING]";
                    _font.DrawString(sb, line, new Vector2(row.X + 8, row.Y + 3), new Color(180, 180, 180));

                    y += rowH + 4;
                    if (y + rowH > _friendsRect.Bottom - 8)
                        break;
                }
            }

            return;
        }

        if (_blockedUsers.Count == 0)
        {
            _font.DrawString(sb, "(no blocked users)", new Vector2(_friendsRect.X + 8, _friendsRect.Y + _font.LineHeight + 10), new Color(180, 180, 180));
            return;
        }

        for (var i = 0; i < _blockedUsers.Count; i++)
        {
            var row = new Rectangle(_friendsRect.X + 6, y, _friendsRect.Width - 12, rowH);
            var selected = i == _selectedBlocked;
            sb.Draw(_pixel, row, selected ? new Color(110, 52, 52, 220) : new Color(26, 26, 26, 200));
            DrawBorder(sb, row, selected ? new Color(245, 170, 170) : new Color(110, 110, 110));

            var blockedUser = _blockedUsers[i];
            var displayName = !string.IsNullOrWhiteSpace(blockedUser.DisplayName)
                ? blockedUser.DisplayName
                : PlayerProfile.ShortId(blockedUser.ProductUserId);
            var line = $"{displayName} ({blockedUser.FriendCode})";
            _font.DrawString(sb, line, new Vector2(row.X + 8, row.Y + 3), Color.White);

            y += rowH + 4;
            if (y + rowH > _friendsRect.Bottom - 8)
                break;
        }
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

        if (!_friendsRect.Contains(input.MousePosition))
            return;

        var rowH = _font.LineHeight + 8;
        var startY = _friendsRect.Y + _font.LineHeight + 12;
        var relY = input.MousePosition.Y - startY;
        if (relY < 0)
            return;

        var index = relY / (rowH + 4);
        if (_friendsListMode == ProfileScreenFriendsMode.Friends)
        {
            if (index < 0 || index >= _profile.Friends.Count)
                return;
            _selectedFriend = index;
            return;
        }

        if (_friendsListMode == ProfileScreenFriendsMode.Requests)
        {
            if (index < 0 || index >= _incomingRequests.Count)
                return;
            _selectedRequest = index;
            return;
        }

        if (index < 0 || index >= _blockedUsers.Count)
            return;
        _selectedBlocked = index;
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
                    SetStatus($"Removed {PlayerProfile.ShortId(entry.UserId)} (local fallback).");
                    return;
                }
                SetStatus(string.IsNullOrWhiteSpace(remove.Message) ? "Failed to remove friend." : remove.Message);
                _nextFriendsSyncUtc = DateTime.UtcNow.AddSeconds(8);
                return;
            }

            await SyncCanonicalFriendsAsync(seedFromLocal: false);
            _selectedFriend = Math.Min(_selectedFriend, _profile.Friends.Count - 1);
            SetStatus($"Removed {PlayerProfile.ShortId(entry.UserId)}.");
            return;
        }

        _profile.Friends.RemoveAt(_selectedFriend);
        _selectedFriend = Math.Min(_selectedFriend, _profile.Friends.Count - 1);
        _profile.Save(_log);
        SetStatus($"Removed {PlayerProfile.ShortId(entry.UserId)}.");
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
        SetStatus($"Accepted {req.User.DisplayName}.");
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
        SetStatus($"Denied {req.User.DisplayName}.");
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

    private void ShowFriendsUnavailableError()
    {
        _friendsEndpointUnavailable = true;
        _nextFriendsSyncUtc = DateTime.UtcNow.AddSeconds(45);
    }

    private async Task RefreshProfileDataAsync()
    {
        await SyncCanonicalFriendsAsync(seedFromLocal: false);
    }

    private static bool IsMissingFriendsEndpointError(string? message)
    {
        var value = (message ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(value))
            return false;

        return value.Contains("HTTP 404", StringComparison.OrdinalIgnoreCase)
            || value.Contains("Not Found", StringComparison.OrdinalIgnoreCase);
    }

    private void CopyLocalId()
    {
        var id = (_eos?.LocalProductUserId ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(id))
        {
            SetStatus("ID unavailable right now.");
            return;
        }

        try
        {
            WinClipboard.SetText(id);
            SetStatus("Copied ID to clipboard.");
        }
        catch (Exception ex)
        {
            _log.Warn($"ProfileScreen copy ID failed: {ex.Message}");
            SetStatus("Clipboard copy failed.");
        }
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
}
