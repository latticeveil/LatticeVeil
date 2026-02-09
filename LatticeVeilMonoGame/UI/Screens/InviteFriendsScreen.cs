using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using LatticeVeilMonoGame.Core;
using LatticeVeilMonoGame.Online.Eos;
using LatticeVeilMonoGame.Online.Gate;
using LatticeVeilMonoGame.UI;

namespace LatticeVeilMonoGame.UI.Screens;

public sealed class InviteFriendsScreen : IScreen
{
    private readonly MenuStack _menus;
    private readonly AssetLoader _assets;
    private readonly PixelFont _font;
    private readonly Texture2D _pixel;
    private readonly Logger _log;
    private readonly string _worldName;
    private EosClient? _eosClient;
    private readonly FriendLabelsStore _friendLabels;

    private readonly Button _refreshBtn;
    private readonly Button _inviteBtn;
    private readonly Button _backBtn;

    private Texture2D? _bg;
    private Rectangle _viewport;
    private Rectangle _panelRect;
    private Rectangle _infoRect;
    private Rectangle _listRect;
    private Rectangle _listBodyRect;
    private Rectangle _buttonRowRect;

    private readonly List<FriendEntry> _friends = new();
    private int _selectedFriend = -1;
    private bool _busy;
    private double _now;
    private string _status = string.Empty;
    private double _statusUntil;

    public InviteFriendsScreen(MenuStack menus, AssetLoader assets, PixelFont font, Texture2D pixel, Logger log,
        EosClient? eosClient, string worldName)
    {
        _menus = menus;
        _assets = assets;
        _font = font;
        _pixel = pixel;
        _log = log;
        _worldName = string.IsNullOrWhiteSpace(worldName) ? "World" : worldName.Trim();
        _eosClient = eosClient ?? EosClientProvider.GetOrCreate(_log, "deviceid", allowRetry: true);
        if (_eosClient == null)
            _log.Warn("InviteFriendsScreen: EOS client not available.");
        _friendLabels = FriendLabelsStore.LoadOrCreate(log);

        _refreshBtn = new Button("REFRESH", () => _ = RefreshFriendsAsync()) { BoldText = true };
        _inviteBtn = new Button("INVITE", () => _ = InviteSelectedAsync()) { BoldText = true };
        _backBtn = new Button("BACK", () => _menus.Pop()) { BoldText = true };

        try { _bg = _assets.LoadTexture("textures/menu/backgrounds/InviteFriends_bg.png"); }
        catch { /* optional */ }

        _ = RefreshFriendsAsync();
    }

    public void OnResize(Rectangle viewport)
    {
        _viewport = viewport;

        var panelW = Math.Min(640, (int)(viewport.Width * 0.85f));
        var panelH = Math.Min(360, (int)(viewport.Height * 0.75f));
        var panelX = viewport.X + (viewport.Width - panelW) / 2;
        var panelY = viewport.Y + (viewport.Height - panelH) / 2;
        _panelRect = new Rectangle(panelX, panelY, panelW, panelH);

        var pad = 12;
        var headerH = _font.LineHeight + 8;
        _infoRect = new Rectangle(panelX + pad, panelY + pad, panelW - pad * 2, headerH);

        var buttonRowH = _font.LineHeight + 10;
        _buttonRowRect = new Rectangle(panelX + pad, panelY + panelH - pad - buttonRowH, panelW - pad * 2, buttonRowH);
        _listRect = new Rectangle(panelX + pad, _infoRect.Bottom + 6, panelW - pad * 2, _buttonRowRect.Top - _infoRect.Bottom - 12);

        var listHeaderH = _font.LineHeight + 8;
        _listBodyRect = new Rectangle(_listRect.X, _listRect.Y + listHeaderH, _listRect.Width, Math.Max(0, _listRect.Height - listHeaderH));

        var gap = 8;
        // Position back button in bottom-left corner of full screen with proper aspect ratio
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
            backBtnH
        );
        
        // Adjust other buttons to account for back button position
        var buttonW = (_buttonRowRect.Width - gap * 2) / 3;
        _refreshBtn.Bounds = new Rectangle(_buttonRowRect.X, _buttonRowRect.Y, buttonW, buttonRowH);
        _inviteBtn.Bounds = new Rectangle(_refreshBtn.Bounds.Right + gap, _buttonRowRect.Y, buttonW, buttonRowH);
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

        _refreshBtn.Update(input);
        _inviteBtn.Update(input);
        _backBtn.Update(input);

        HandleListSelection(input);
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
        sb.Draw(_pixel, _panelRect, new Color(0, 0, 0, 180));
        DrawBorder(sb, _panelRect, Color.White);
    }

    private void DrawInfo(SpriteBatch sb)
    {
        var title = "INVITE FRIENDS";
        var left = new Vector2(_infoRect.X + 4, _infoRect.Y + 2);
        DrawTextBold(sb, title, left, Color.White);

        var eosSnapshot = EosRuntimeStatus.Evaluate(_eosClient);
        var eos = eosSnapshot.Reason == EosRuntimeReason.Ready
            ? $"HOSTING: {_worldName}"
            : eosSnapshot.StatusText;
        var size = _font.MeasureString(eos);
        var pos = new Vector2(_infoRect.Right - size.X - 4, _infoRect.Y + 2);
        _font.DrawString(sb, eos, pos, new Color(220, 180, 80));
    }

    private void DrawList(SpriteBatch sb)
    {
        sb.Draw(_pixel, _listRect, new Color(20, 20, 20, 200));
        DrawBorder(sb, _listRect, Color.White);

        _font.DrawString(sb, "FRIENDS (EOS)", new Vector2(_listRect.X + 4, _listRect.Y + 2), Color.White);

        if (EosRuntimeStatus.Evaluate(_eosClient).Reason != EosRuntimeReason.Ready)
        {
            DrawCenteredMessage(sb, EosRuntimeStatus.Evaluate(_eosClient).StatusText);
            return;
        }

        if (_friends.Count == 0)
        {
            DrawCenteredMessage(sb, "NO FRIENDS FOUND");
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
            var tag = f.IsPinned ? "RC" : "EP";
            var state = f.IsHosting
                ? $"HOSTING {f.WorldName ?? "WORLD"}"
                : f.Presence.ToUpperInvariant();
            var text = $"{tag}: {f.DisplayName} | {state}";
            DrawTextBold(sb, text, new Vector2(_listBodyRect.X + 4, rowY), Color.White);
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

    private async Task RefreshFriendsAsync()
    {
        if (_busy)
            return;

        var gate = OnlineGateClient.GetOrCreate();
        if (!gate.CanUseOfficialOnline(_log, out var gateDenied))
        {
            _friends.Clear();
            _selectedFriend = -1;
            SetStatus(gateDenied);
            return;
        }

        var eos = EnsureEosClient();
        if (eos == null)
        {
            _friends.Clear();
            _selectedFriend = -1;
            SetStatus("EOS CLIENT UNAVAILABLE");
            return;
        }
        var snapshot = EosRuntimeStatus.Evaluate(eos);
        if (snapshot.Reason != EosRuntimeReason.Ready)
        {
            _friends.Clear();
            _selectedFriend = -1;
            SetStatus(snapshot.StatusText);
            return;
        }

        _busy = true;
        try
        {
            var list = await eos.GetFriendsWithPresenceAsync();
            _friends.Clear();
            foreach (var f in list)
            {
                var alias = _friendLabels.GetNickname(f.AccountId);
                var display = string.IsNullOrWhiteSpace(alias) ? f.DisplayName : $"{alias} ({f.DisplayName})";
                _friends.Add(new FriendEntry
                {
                    AccountId = f.AccountId,
                    DisplayName = display,
                    RawDisplayName = f.DisplayName,
                    Status = f.Status,
                    Presence = f.Presence,
                    IsHosting = f.IsHosting,
                    WorldName = f.WorldName,
                    IsPinned = _friendLabels.IsPinned(f.AccountId) || !string.IsNullOrWhiteSpace(alias)
                });
            }

            _friends.Sort((a, b) =>
            {
                if (a.IsPinned != b.IsPinned)
                    return a.IsPinned ? -1 : 1;
                return string.Compare(a.DisplayName, b.DisplayName, StringComparison.OrdinalIgnoreCase);
            });

            _selectedFriend = _friends.Count > 0 ? 0 : -1;
            SetStatus("Friends updated.", 1.5);
        }
        catch (Exception ex)
        {
            _log.Warn($"EOS friends refresh failed: {ex.Message}");
            SetStatus("Refresh failed.");
        }
        finally
        {
            _busy = false;
        }
    }

    private async Task InviteSelectedAsync()
    {
        var gate = OnlineGateClient.GetOrCreate();
        if (!gate.CanUseOfficialOnline(_log, out var gateDenied))
        {
            SetStatus(gateDenied);
            return;
        }

        var eos = EnsureEosClient();
        if (eos == null)
        {
            SetStatus("EOS CLIENT UNAVAILABLE");
            return;
        }
        var snapshot = EosRuntimeStatus.Evaluate(eos);
        if (snapshot.Reason != EosRuntimeReason.Ready)
        {
            SetStatus(snapshot.StatusText);
            return;
        }

        if (_selectedFriend < 0 || _selectedFriend >= _friends.Count)
        {
            SetStatus("Select a friend.");
            return;
        }

        var f = _friends[_selectedFriend];
        if (!string.Equals(f.Status, "friends", StringComparison.OrdinalIgnoreCase))
        {
            SetStatus("Invite requires a friend.");
            return;
        }

        if (_busy)
            return;

        _busy = true;
        try
        {
            var ok = await eos.SetHostingPresenceAsync(_worldName, true);
            if (!ok)
            {
                SetStatus("Invite failed (presence).");
                return;
            }

            SetStatus($"Invite ready for {f.DisplayName}. They can join from Friends tab.");
        }
        catch (Exception ex)
        {
            _log.Warn($"Invite failed: {ex.Message}");
            SetStatus("Invite failed.");
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

    private void SetStatus(string message, double seconds = 3)
    {
        _status = message;
        _statusUntil = _now + seconds;
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
        public string AccountId { get; init; } = "";
        public string DisplayName { get; init; } = "";
        public string RawDisplayName { get; init; } = "";
        public string Status { get; init; } = "";
        public string Presence { get; init; } = "";
        public bool IsHosting { get; init; }
        public string? WorldName { get; init; }
        public bool IsPinned { get; init; }
    }
}
