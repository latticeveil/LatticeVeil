using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using LatticeVeilMonoGame.Core;
using LatticeVeilMonoGame.Online.Eos;
using LatticeVeilMonoGame.Online.Gate;
using LatticeVeilMonoGame.UI;
using WinClipboard = System.Windows.Forms.Clipboard;

namespace LatticeVeilMonoGame.UI.Screens;

public sealed class InviteFriendsScreen : IScreen
{
    private const int MaxFriendInputLength = 48;
    private const double CanonicalSyncRetrySeconds = 8.0;
    private const double MissingEndpointRetrySeconds = 45.0;
    private const double AutoRefreshSeconds = 7.0;
    private const double AutoRefreshBackoffSeconds = 25.0;

    private readonly MenuStack _menus;
    private readonly AssetLoader _assets;
    private readonly PixelFont _font;
    private readonly Texture2D _pixel;
    private readonly Logger _log;
    private readonly string _worldName;
    private readonly PlayerProfile _profile;
    private readonly EosIdentityStore _identityStore;
    private readonly OnlineGateClient _gate;
    private readonly OnlineSocialStateService _socialState;
    private EosClient? _eosClient;

    private readonly Button _refreshBtn;
    private readonly Button _inviteBtn;
    private readonly Button _addFriendBtn;
    private readonly Button _backBtn;

    private Texture2D? _bg;
    private Texture2D? _panel;
    private Rectangle _viewport;
    private Rectangle _panelRect;
    private Rectangle _infoRect;
    private Rectangle _listRect;
    private Rectangle _listBodyRect;
    private Rectangle _addFriendInputRect;
    private Rectangle _buttonRowRect;

    private readonly List<FriendEntry> _friends = new();
    private int _selectedFriend = -1;
    private bool _busy;
    private bool _canonicalSyncInProgress;
    private bool _canonicalSeedAttempted;
    private bool _friendsEndpointUnavailable;
    private double _nextCanonicalSyncAttempt;
    private bool _addFriendActive;
    private string _addFriendQuery = string.Empty;
    private bool _addFriendSelectAll;
    private double _now;
    private string _status = string.Empty;
    private double _statusUntil;
    private DateTime _nextAutoRefreshUtc = DateTime.MinValue;
    private int _refreshFailureCount;

    public InviteFriendsScreen(
        MenuStack menus,
        AssetLoader assets,
        PixelFont font,
        Texture2D pixel,
        Logger log,
        PlayerProfile profile,
        EosClient? eosClient,
        string worldName)
    {
        _menus = menus;
        _assets = assets;
        _font = font;
        _pixel = pixel;
        _log = log;
        _profile = profile;
        _worldName = string.IsNullOrWhiteSpace(worldName) ? "World" : worldName.Trim();
        _eosClient = eosClient ?? EosClientProvider.GetOrCreate(_log, "deviceid", allowRetry: true);
        _identityStore = EosIdentityStore.LoadOrCreate(_log);
        _gate = OnlineGateClient.GetOrCreate();
        _socialState = OnlineSocialStateService.GetOrCreate(_log);

        if (_eosClient == null)
            _log.Warn("InviteFriendsScreen: EOS client not available.");

        _refreshBtn = new Button("REFRESH", () => _ = RefreshFriendsAsync(manualTrigger: true)) { BoldText = true };
        _inviteBtn = new Button("INVITE", () => _ = InviteSelectedAsync()) { BoldText = true };
        _addFriendBtn = new Button("ADD FRIEND", () => _ = AddFriendAsync()) { BoldText = true };
        _backBtn = new Button("BACK", () => _menus.Pop()) { BoldText = true };

        try
        {
            _bg = _assets.LoadTexture("textures/menu/backgrounds/InviteFriends_bg.png");
            _panel = _assets.LoadTexture("textures/menu/GUIS/Multiplayer_GUI.png");
        }
        catch { }

        _nextAutoRefreshUtc = DateTime.UtcNow;
        SeedFriendsFromSnapshot();
        _ = RefreshFriendsAsync(manualTrigger: true);
    }

    public void OnResize(Rectangle viewport)
    {
        _viewport = viewport;

        var panelW = Math.Min(980, viewport.Width - 90);
        var panelH = Math.Min(650, viewport.Height - 120);
        var panelX = viewport.X + (viewport.Width - panelW) / 2;
        var panelY = viewport.Y + (viewport.Height - panelH) / 2;
        _panelRect = new Rectangle(panelX, panelY, panelW, panelH);

        var pad = 20;
        var headerH = _font.LineHeight + 8;
        _infoRect = new Rectangle(panelX + pad, panelY + pad + 6, panelW - pad * 2, headerH);

        var buttonRowH = _font.LineHeight + 10;
        _buttonRowRect = new Rectangle(panelX + pad, panelY + panelH - pad - buttonRowH, panelW - pad * 2, buttonRowH);
        _listRect = new Rectangle(panelX + pad, _infoRect.Bottom + 6, panelW - pad * 2, _buttonRowRect.Top - _infoRect.Bottom - 12);

        var listHeaderH = _font.LineHeight + 8;
        var addInputH = _font.LineHeight + 12;
        _addFriendInputRect = new Rectangle(_listRect.X + 8, _listRect.Y + listHeaderH + 4, _listRect.Width - 16, addInputH);
        _listBodyRect = new Rectangle(
            _listRect.X,
            _addFriendInputRect.Bottom + 8,
            _listRect.Width,
            Math.Max(0, _listRect.Bottom - (_addFriendInputRect.Bottom + 8)));

        var gap = 8;
        var backBtnMargin = 20;
        var backBtnBaseW = Math.Max(_backBtn.Texture?.Width ?? 0, 320);
        var backBtnBaseH = Math.Max(_backBtn.Texture?.Height ?? 0, (int)(backBtnBaseW * 0.28f));
        var backBtnScale = Math.Min(1f, Math.Min(240f / backBtnBaseW, 240f / backBtnBaseH));
        var backBtnW = Math.Max(1, (int)Math.Round(backBtnBaseW * backBtnScale));
        var backBtnH = Math.Max(1, (int)Math.Round(backBtnBaseH * backBtnScale));
        _backBtn.Bounds = new Rectangle(
            _viewport.X + backBtnMargin,
            _viewport.Bottom - backBtnMargin - backBtnH,
            backBtnW,
            backBtnH);

        var buttonW = (_buttonRowRect.Width - gap * 2) / 3;
        _refreshBtn.Bounds = new Rectangle(_buttonRowRect.X, _buttonRowRect.Y, buttonW, buttonRowH);
        _inviteBtn.Bounds = new Rectangle(_refreshBtn.Bounds.Right + gap, _buttonRowRect.Y, buttonW, buttonRowH);
        _addFriendBtn.Bounds = new Rectangle(_inviteBtn.Bounds.Right + gap, _buttonRowRect.Y, buttonW, buttonRowH);
    }

    public void Update(GameTime gameTime, InputState input)
    {
        _now = gameTime.TotalGameTime.TotalSeconds;
        if (!string.IsNullOrWhiteSpace(_status) && _now > _statusUntil)
            _status = string.Empty;

        if (input.IsNewKeyPress(Keys.Escape))
        {
            _menus.Pop();
            return;
        }

        if (input.IsNewLeftClick())
            TextFieldVisuals.HandleWholeFieldPointerSelection(input, _addFriendInputRect, ref _addFriendActive, ref _addFriendSelectAll, _addFriendQuery);

        if (_addFriendActive && !_busy)
        {
            HandleTextInput(input, ref _addFriendQuery, MaxFriendInputLength);
            if (input.IsNewKeyPress(Keys.Enter))
                _ = AddFriendAsync();
        }

        HandleListSelection(input);
        EnsureValidSelection();

        var eosReady = EosRuntimeStatus.Evaluate(_eosClient).Reason == EosRuntimeReason.Ready;
        _refreshBtn.Enabled = !_busy;
        _inviteBtn.Enabled = !_busy && eosReady && _friends.Count > 0;
        _addFriendBtn.Enabled = !_busy && eosReady && !string.IsNullOrWhiteSpace(_addFriendQuery);

        _refreshBtn.Update(input);
        _inviteBtn.Update(input);
        _addFriendBtn.Update(input);
        _backBtn.Update(input);

        if (!_busy && DateTime.UtcNow >= _nextAutoRefreshUtc)
            _ = RefreshFriendsAsync(manualTrigger: false);
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

        DrawPanel(sb);
        DrawInfo(sb);
        DrawList(sb);
        DrawButtons(sb);
        DrawStatus(sb);

        sb.End();
    }

    private void DrawPanel(SpriteBatch sb)
    {
        if (_panel is not null)
        {
            sb.Draw(_panel, _panelRect, Color.White);
            return;
        }

        sb.Draw(_pixel, _panelRect, new Color(0, 0, 0, 180));
        DrawBorder(sb, _panelRect, Color.White);
    }

    private void DrawInfo(SpriteBatch sb)
    {
        DrawTextBold(sb, "INVITE FRIENDS", new Vector2(_infoRect.X + 4, _infoRect.Y + 2), Color.White);

        var eosSnapshot = EosRuntimeStatus.Evaluate(_eosClient);
        var rightText = eosSnapshot.Reason == EosRuntimeReason.Ready
            ? $"HOSTING: {_worldName}"
            : eosSnapshot.StatusText;
        var size = _font.MeasureString(rightText);
        var pos = new Vector2(_infoRect.Right - size.X - 4, _infoRect.Y + 2);
        _font.DrawString(sb, rightText, pos, new Color(220, 180, 80));
    }

    private void DrawList(SpriteBatch sb)
    {
        sb.Draw(_pixel, _listRect, new Color(20, 20, 20, 200));
        DrawBorder(sb, _listRect, Color.White);

        _font.DrawString(sb, "FRIENDS", new Vector2(_listRect.X + 4, _listRect.Y + 2), Color.White);
        _font.DrawString(sb, "ADD FRIEND BY USERNAME", new Vector2(_addFriendInputRect.X, _addFriendInputRect.Y - _font.LineHeight - 2), new Color(220, 220, 220));

        sb.Draw(_pixel, _addFriendInputRect, _addFriendActive ? new Color(36, 36, 36, 240) : new Color(24, 24, 24, 240));
        DrawBorder(sb, _addFriendInputRect, Color.White);

        var inputLabel = string.IsNullOrWhiteSpace(_addFriendQuery) ? "(type Veilnet username)" : _addFriendQuery;
        var inputPos = new Vector2(_addFriendInputRect.X + 6, _addFriendInputRect.Y + 4);
        TextFieldVisuals.DrawWholeFieldSelection(sb, _pixel, _font, _addFriendQuery, inputPos, _addFriendInputRect, _addFriendSelectAll, new Color(88, 148, 218, 170));
        _font.DrawString(
            sb,
            inputLabel,
            inputPos,
            string.IsNullOrWhiteSpace(_addFriendQuery) ? new Color(170, 170, 170) : Color.White);
        TextFieldVisuals.DrawCaret(sb, _pixel, _font, _addFriendQuery, inputPos, _addFriendInputRect, _addFriendActive, _addFriendSelectAll, _now, Color.White);

        if (EosRuntimeStatus.Evaluate(_eosClient).Reason != EosRuntimeReason.Ready)
        {
            DrawCenteredMessage(sb, EosRuntimeStatus.Evaluate(_eosClient).StatusText);
            return;
        }

        if (_friends.Count == 0)
        {
            DrawCenteredMessage(sb, "NO FRIENDS SAVED");
            return;
        }

        var rowH = _font.LineHeight + 2;
        var rowY = _listBodyRect.Y + 2;
        for (var i = 0; i < _friends.Count; i++)
        {
            var rowRect = new Rectangle(_listBodyRect.X, rowY - 1, _listBodyRect.Width, rowH + 2);
            if (i == _selectedFriend)
                sb.Draw(_pixel, rowRect, new Color(40, 40, 40, 200));

            var f = _friends[i];
            var name = string.IsNullOrWhiteSpace(f.DisplayName) ? "PLAYER" : f.DisplayName;
            var state = FriendPresenceFormatter.BuildLabel(
                f.Presence,
                f.IsHosting,
                f.IsInWorld,
                f.IsMultiplayer,
                f.WorldName,
                f.GameMode);
            var text = $"{name} | {state}";
            DrawTextBold(sb, Truncate(text, 96), new Vector2(_listBodyRect.X + 4, rowY), Color.White);
            rowY += rowH;
            if (rowY > _listBodyRect.Bottom - rowH)
                break;
        }
    }

    private void DrawCenteredMessage(SpriteBatch sb, string msg)
    {
        var size = _font.MeasureString(msg);
        var pos = new Vector2(_listBodyRect.Center.X - size.X / 2f, _listBodyRect.Center.Y - size.Y / 2f);
        DrawTextBold(sb, msg, pos, Color.White);
    }

    private void DrawButtons(SpriteBatch sb)
    {
        _refreshBtn.Draw(sb, _pixel, _font);
        _inviteBtn.Draw(sb, _pixel, _font);
        _addFriendBtn.Draw(sb, _pixel, _font);
        _backBtn.Draw(sb, _pixel, _font);
    }

    private void DrawStatus(SpriteBatch sb)
    {
        if (string.IsNullOrWhiteSpace(_status))
            return;

        var pos = new Vector2(_panelRect.X + 14, _panelRect.Bottom - _font.LineHeight - 8);
        _font.DrawString(sb, _status, pos, Color.White);
    }

    private void HandleListSelection(InputState input)
    {
        if (!input.IsNewLeftClick())
            return;

        var p = input.MousePosition;
        if (!_listBodyRect.Contains(p))
            return;

        var rowH = _font.LineHeight + 2;
        var index = (p.Y - _listBodyRect.Y) / rowH;
        if (index < 0 || index >= _friends.Count)
            return;

        _selectedFriend = index;
    }

    private async Task RefreshFriendsAsync(bool manualTrigger)
    {
        if (_busy)
            return;

        if (manualTrigger)
        {
            _refreshFailureCount = 0;
            _nextAutoRefreshUtc = DateTime.UtcNow.AddSeconds(AutoRefreshSeconds);
        }

        if (!_gate.CanUseOfficialOnline(_log, out var gateDenied))
        {
            _friends.Clear();
            _selectedFriend = -1;
            SetStatus(gateDenied);
            _refreshFailureCount++;
            _nextAutoRefreshUtc = DateTime.UtcNow.AddSeconds(_refreshFailureCount >= 3 ? AutoRefreshBackoffSeconds : AutoRefreshSeconds);
            return;
        }

        var eos = EnsureEosClient();
        if (eos == null)
        {
            _friends.Clear();
            _selectedFriend = -1;
            SetStatus("EOS CLIENT UNAVAILABLE");
            _refreshFailureCount++;
            _nextAutoRefreshUtc = DateTime.UtcNow.AddSeconds(_refreshFailureCount >= 3 ? AutoRefreshBackoffSeconds : AutoRefreshSeconds);
            return;
        }

        var snapshot = EosRuntimeStatus.Evaluate(eos);
        if (snapshot.Reason != EosRuntimeReason.Ready)
        {
            _friends.Clear();
            _selectedFriend = -1;
            SetStatus(snapshot.StatusText);
            _refreshFailureCount++;
            _nextAutoRefreshUtc = DateTime.UtcNow.AddSeconds(_refreshFailureCount >= 3 ? AutoRefreshBackoffSeconds : AutoRefreshSeconds);
            return;
        }

        var selectedFriendId = (_selectedFriend >= 0 && _selectedFriend < _friends.Count)
            ? (_friends[_selectedFriend].ProductUserId ?? string.Empty).Trim()
            : string.Empty;

        _busy = true;
        try
        {
            RefreshIdentityStore(eos);

            if (!_friendsEndpointUnavailable || _now >= _nextCanonicalSyncAttempt)
                await SyncCanonicalFriendsAsync(seedFromLocal: !_canonicalSeedAttempted);

            var entries = new List<FriendEntry>();
            var ids = _profile.Friends.Select(f => f.UserId).Where(x => !string.IsNullOrWhiteSpace(x)).ToList();
            var presenceResult = await _gate.QueryPresenceAsync(ids);
            var presenceById = new Dictionary<string, GatePresenceEntry>(StringComparer.OrdinalIgnoreCase);
            var socialSnapshot = _socialState.GetSnapshot();
            if (presenceResult.Ok)
            {
                for (var i = 0; i < presenceResult.Entries.Count; i++)
                {
                    var p = presenceResult.Entries[i];
                    if (!string.IsNullOrWhiteSpace(p.ProductUserId))
                        presenceById[p.ProductUserId] = p;
                }
            }

            foreach (var pair in socialSnapshot.PresenceByUserId)
            {
                if (!presenceById.ContainsKey(pair.Key))
                    presenceById[pair.Key] = pair.Value;
            }

            for (var i = 0; i < _profile.Friends.Count; i++)
            {
                var friend = _profile.Friends[i];
                var id = (friend.UserId ?? string.Empty).Trim();
                if (string.IsNullOrWhiteSpace(id))
                    continue;

                if (presenceById.TryGetValue(id, out var presence))
                {
                    if (!string.IsNullOrWhiteSpace(presence.DisplayName))
                        friend.LastKnownDisplayName = presence.DisplayName;
                    friend.LastKnownPresence = FriendPresenceFormatter.BuildLabel(
                        presence.Status,
                        presence.IsHosting,
                        presence.IsInWorld,
                        presence.IsMultiplayer,
                        presence.WorldName,
                        presence.GameMode);

                    entries.Add(new FriendEntry
                    {
                        ProductUserId = id,
                        DisplayName = string.IsNullOrWhiteSpace(presence.DisplayName) ? friend.Label : presence.DisplayName,
                        Presence = presence.Status,
                        IsHosting = presence.IsHosting,
                        IsInWorld = presence.IsInWorld,
                        IsMultiplayer = presence.IsMultiplayer,
                        WorldName = string.IsNullOrWhiteSpace(presence.WorldName) ? "WORLD" : presence.WorldName,
                        GameMode = presence.GameMode ?? string.Empty
                    });
                }
                else
                {
                    var fallbackName = !string.IsNullOrWhiteSpace(friend.LastKnownDisplayName)
                        ? friend.LastKnownDisplayName
                        : (string.IsNullOrWhiteSpace(friend.Label) ? "PLAYER" : friend.Label);
                    const string fallbackPresence = "OFFLINE";
                    friend.LastKnownPresence = fallbackPresence;
                    entries.Add(new FriendEntry
                    {
                        ProductUserId = id,
                        DisplayName = fallbackName,
                        Presence = fallbackPresence,
                        IsHosting = false,
                        IsInWorld = false,
                        IsMultiplayer = false,
                        WorldName = "WORLD",
                        GameMode = string.Empty
                    });
                }
            }

            _profile.Save(_log);
            _friends.Clear();
            _friends.AddRange(entries.OrderByDescending(x => x.IsHosting).ThenBy(x => x.DisplayName, StringComparer.OrdinalIgnoreCase));
            if (!string.IsNullOrWhiteSpace(selectedFriendId))
            {
                _selectedFriend = _friends.FindIndex(f =>
                    string.Equals(f.ProductUserId, selectedFriendId, StringComparison.OrdinalIgnoreCase));
            }

            EnsureValidSelection();
            SetStatus("Friends updated.", 1.5);
            _refreshFailureCount = 0;
            _nextAutoRefreshUtc = DateTime.UtcNow.AddSeconds(AutoRefreshSeconds);
        }
        catch (Exception ex)
        {
            _log.Warn($"Friends refresh failed: {ex.Message}");
            SetStatus("Refresh failed.");
            _refreshFailureCount++;
            _nextAutoRefreshUtc = DateTime.UtcNow.AddSeconds(_refreshFailureCount >= 3 ? AutoRefreshBackoffSeconds : AutoRefreshSeconds);
        }
        finally
        {
            _busy = false;
        }
    }

    private void SeedFriendsFromSnapshot()
    {
        var snapshot = _socialState.GetSnapshot();
        if (snapshot.Friends.Count <= 0)
            return;

        _friends.Clear();
        for (var i = 0; i < snapshot.Friends.Count; i++)
        {
            var friend = snapshot.Friends[i];
            var id = (friend.ProductUserId ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(id))
                continue;

            var display = !string.IsNullOrWhiteSpace(friend.DisplayName)
                ? friend.DisplayName.Trim()
                : !string.IsNullOrWhiteSpace(friend.Username)
                    ? friend.Username.Trim()
                    : "PLAYER";
            snapshot.PresenceByUserId.TryGetValue(id, out var presence);
            _friends.Add(new FriendEntry
            {
                ProductUserId = id,
                DisplayName = display,
                Presence = string.IsNullOrWhiteSpace(presence?.Status)
                    ? "OFFLINE"
                    : presence!.Status.Trim(),
                IsHosting = presence?.IsHosting ?? false,
                IsInWorld = presence?.IsInWorld ?? false,
                IsMultiplayer = presence?.IsMultiplayer ?? false,
                WorldName = string.IsNullOrWhiteSpace(presence?.WorldName) ? "WORLD" : presence.WorldName.Trim(),
                GameMode = (presence?.GameMode ?? string.Empty).Trim()
            });
        }

        _friends.Sort((a, b) =>
        {
            var hosting = b.IsHosting.CompareTo(a.IsHosting);
            return hosting != 0 ? hosting : string.Compare(a.DisplayName, b.DisplayName, StringComparison.OrdinalIgnoreCase);
        });
        EnsureValidSelection();
    }

    private async Task InviteSelectedAsync()
    {
        EnsureValidSelection();
        if (_selectedFriend < 0 || _selectedFriend >= _friends.Count)
        {
            SetStatus("No friend available to invite.");
            return;
        }

        if (_busy)
            return;

        if (!_gate.CanUseOfficialOnline(_log, out var gateDenied))
        {
            SetStatus(gateDenied);
            return;
        }

        var eos = EnsureEosClient();
        var snapshot = EosRuntimeStatus.Evaluate(eos);
        if (snapshot.Reason != EosRuntimeReason.Ready || eos == null)
        {
            SetStatus(snapshot.StatusText);
            return;
        }

        var hostUserId = (eos.LocalProductUserId ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(hostUserId))
        {
            SetStatus("World hosting is not ready yet.");
            return;
        }

        _busy = true;
        try
        {
            var targetUserId = (_friends[_selectedFriend].ProductUserId ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(targetUserId))
            {
                SetStatus("Friend identity unavailable.");
                return;
            }

            var sent = await _gate.SendWorldInviteDetailedAsync(targetUserId, _worldName, hostUserId).ConfigureAwait(false);
            var friendName = _friends[_selectedFriend].DisplayName;
            if (!sent.Ok)
                _log.Warn($"InviteFriendsScreen invite failed: targetUserId={targetUserId} friend={friendName} message={sent.Message}");
            SetStatus(sent.Ok
                ? $"Invite sent to {friendName}."
                : $"Invite failed: {sent.Message}", 3);
        }
        catch (Exception ex)
        {
            _log.Warn($"Invite action failed: {ex.Message}");
            SetStatus("Invite failed.");
        }
        finally
        {
            _busy = false;
        }
    }

    private void EnsureValidSelection()
    {
        if (_friends.Count <= 0)
        {
            _selectedFriend = -1;
            return;
        }

        if (_selectedFriend < 0 || _selectedFriend >= _friends.Count)
            _selectedFriend = 0;
    }

    private async Task AddFriendAsync()
    {
        if (_busy)
            return;

        var query = (_addFriendQuery ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(query))
        {
            SetStatus("Enter a Veilnet username first.");
            return;
        }

        if (!_gate.CanUseOfficialOnline(_log, out var gateDenied))
        {
            SetStatus(gateDenied);
            return;
        }

        var eos = EnsureEosClient();
        var snapshot = EosRuntimeStatus.Evaluate(eos);
        if (snapshot.Reason != EosRuntimeReason.Ready)
        {
            SetStatus(snapshot.StatusText);
            return;
        }

        _busy = true;
        try
        {
            var add = await _gate.AddFriendAsync(query);
            if (!add.Ok)
            {
                SetStatus(string.IsNullOrWhiteSpace(add.Message) ? "Add friend failed." : add.Message, 3);
                return;
            }

            RefreshIdentityStore(eos);
            await SyncCanonicalFriendsAsync(seedFromLocal: false);
            _addFriendQuery = string.Empty;
            _addFriendActive = false;
            SetStatus(string.IsNullOrWhiteSpace(add.Message) ? "Friend added." : add.Message, 3);
            await RefreshFriendsAsync(manualTrigger: true);
        }
        catch (Exception ex)
        {
            _log.Warn($"Add friend failed: {ex.Message}");
            SetStatus("Add friend failed.");
        }
        finally
        {
            _busy = false;
        }
    }

    private EosClient? EnsureEosClient()
    {
        if (_eosClient != null)
            return _eosClient;

        _eosClient = EosClientProvider.GetOrCreate(_log, "deviceid", allowRetry: true);
        if (_eosClient == null)
            _log.Warn("InviteFriendsScreen: EOS client not available.");
        return _eosClient;
    }

    private void RefreshIdentityStore(EosClient? eos)
    {
        if (eos == null)
            return;

        var dirty = false;
        var localPuid = (eos.LocalProductUserId ?? string.Empty).Trim();
        if (!string.IsNullOrWhiteSpace(localPuid)
            && !string.Equals(_identityStore.ProductUserId, localPuid, StringComparison.Ordinal))
        {
            _identityStore.ProductUserId = localPuid;
            dirty = true;
        }

        var displayName = EosIdentityStore.NormalizeDisplayName(_identityStore.DisplayName);
        if (string.IsNullOrWhiteSpace(displayName))
        {
            displayName = EosIdentityStore.NormalizeDisplayName(_profile.GetDisplayUsername());
            if (string.IsNullOrWhiteSpace(displayName))
                displayName = "Player";
            _identityStore.DisplayName = displayName;
            dirty = true;
        }

        if (dirty)
            _identityStore.Save(_log);
    }

    private async Task<bool> SyncCanonicalFriendsAsync(bool seedFromLocal)
    {
        if (_canonicalSyncInProgress)
            return false;
        if (_friendsEndpointUnavailable && _now < _nextCanonicalSyncAttempt)
            return false;

        _canonicalSyncInProgress = true;
        try
        {
            var serverFriends = await _gate.GetFriendsAsync();
            if (!serverFriends.Ok)
            {
                if (IsMissingFriendsEndpointError(serverFriends.Message))
                {
                    if (!_friendsEndpointUnavailable)
                        SetStatus("Server friends endpoint unavailable. Using local friends list.", 4);
                    _friendsEndpointUnavailable = true;
                    _canonicalSeedAttempted = true;
                    _nextCanonicalSyncAttempt = _now + MissingEndpointRetrySeconds;
                    return false;
                }

                _nextCanonicalSyncAttempt = _now + CanonicalSyncRetrySeconds;
                return false;
            }

            _friendsEndpointUnavailable = false;
            _nextCanonicalSyncAttempt = _now + CanonicalSyncRetrySeconds;

            if (seedFromLocal && serverFriends.Friends.Count == 0 && _profile.Friends.Count > 0)
            {
                _canonicalSeedAttempted = true;
                _nextCanonicalSyncAttempt = _now + CanonicalSyncRetrySeconds;
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
                var entry = _profile.Friends.FirstOrDefault(f => string.Equals(f.UserId, id, StringComparison.OrdinalIgnoreCase));
                if (entry == null)
                    continue;

                entry.LastKnownDisplayName = label;
                if (existingPresence.TryGetValue(id, out var presence))
                    entry.LastKnownPresence = presence;
            }

            _profile.Save(_log);
            _nextCanonicalSyncAttempt = _now + CanonicalSyncRetrySeconds;
            return true;
        }
        catch (Exception ex)
        {
            _log.Warn($"InviteFriends canonical sync failed: {ex.Message}");
            _nextCanonicalSyncAttempt = _now + CanonicalSyncRetrySeconds;
            return false;
        }
        finally
        {
            _canonicalSyncInProgress = false;
        }
    }

    private static bool IsMissingFriendsEndpointError(string? message)
    {
        var value = (message ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(value))
            return false;

        return value.Contains("HTTP 404", StringComparison.OrdinalIgnoreCase)
            || value.Contains("Not Found", StringComparison.OrdinalIgnoreCase);
    }

    private void SetStatus(string message, double seconds = 3)
    {
        _status = message;
        _statusUntil = _now + seconds;
    }

    private void HandleTextInput(InputState input, ref string value, int maxLen)
    {
        var ctrl = input.IsKeyDown(Keys.LeftControl) || input.IsKeyDown(Keys.RightControl);
        if (ctrl)
        {
            if (input.IsNewKeyPress(Keys.A))
            {
                _addFriendSelectAll = true;
                return;
            }

            if (input.IsNewKeyPress(Keys.C))
            {
                if (!string.IsNullOrEmpty(value))
                    WinClipboard.SetText(value);
                return;
            }

            if (input.IsNewKeyPress(Keys.X))
            {
                if (!string.IsNullOrEmpty(value))
                    WinClipboard.SetText(value);
                value = string.Empty;
                _addFriendSelectAll = false;
                return;
            }

            if (input.IsNewKeyPress(Keys.V))
            {
                var clip = (WinClipboard.GetText() ?? string.Empty).Replace("\r", "").Replace("\n", "");
                clip = new string(clip.Where(c => char.IsLetterOrDigit(c) || c == ' ' || c == '-' || c == '_' || c == '.').ToArray());
                if (_addFriendSelectAll)
                    value = string.Empty;
                if (value.Length < maxLen)
                    value += clip[..Math.Min(clip.Length, maxLen - value.Length)];
                _addFriendSelectAll = false;
                return;
            }
        }

        var shift = input.IsKeyDown(Keys.LeftShift) || input.IsKeyDown(Keys.RightShift);
        foreach (var key in input.GetTextInputKeys())
        {
            if (key == Keys.Back)
            {
                if (_addFriendSelectAll)
                {
                    value = string.Empty;
                    _addFriendSelectAll = false;
                }
                else if (value.Length > 0)
                    value = value.Substring(0, value.Length - 1);
                continue;
            }

            if (key == Keys.Space)
            {
                if (_addFriendSelectAll) value = string.Empty;
                Append(ref value, ' ', maxLen);
                _addFriendSelectAll = false;
                continue;
            }

            if (key == Keys.OemMinus || key == Keys.Subtract)
            {
                if (_addFriendSelectAll) value = string.Empty;
                Append(ref value, shift ? '_' : '-', maxLen);
                _addFriendSelectAll = false;
                continue;
            }

            if (key == Keys.OemPeriod || key == Keys.Decimal)
            {
                if (_addFriendSelectAll) value = string.Empty;
                Append(ref value, '.', maxLen);
                _addFriendSelectAll = false;
                continue;
            }

            if (key >= Keys.D0 && key <= Keys.D9)
            {
                if (_addFriendSelectAll) value = string.Empty;
                Append(ref value, (char)('0' + (key - Keys.D0)), maxLen);
                _addFriendSelectAll = false;
                continue;
            }

            if (key >= Keys.NumPad0 && key <= Keys.NumPad9)
            {
                if (_addFriendSelectAll) value = string.Empty;
                Append(ref value, (char)('0' + (key - Keys.NumPad0)), maxLen);
                _addFriendSelectAll = false;
                continue;
            }

            if (key >= Keys.A && key <= Keys.Z)
            {
                if (_addFriendSelectAll) value = string.Empty;
                var c = (char)('A' + (key - Keys.A));
                if (!shift)
                    c = char.ToLowerInvariant(c);
                Append(ref value, c, maxLen);
                _addFriendSelectAll = false;
            }
        }
    }

    private static void Append(ref string value, char c, int maxLen)
    {
        if (value.Length >= maxLen)
            return;
        value += c;
    }

    private static string Truncate(string value, int max)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length <= max)
            return value;
        return value.Substring(0, Math.Max(0, max - 3)) + "...";
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

    private sealed class FriendEntry
    {
        public string ProductUserId { get; init; } = string.Empty;
        public string DisplayName { get; init; } = string.Empty;
        public string Presence { get; init; } = string.Empty;
        public bool IsHosting { get; init; }
        public bool IsInWorld { get; init; }
        public bool IsMultiplayer { get; init; }
        public string WorldName { get; init; } = "WORLD";
        public string GameMode { get; init; } = string.Empty;
    }
}
