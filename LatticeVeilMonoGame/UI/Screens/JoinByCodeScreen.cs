using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using WinClipboard = System.Windows.Forms.Clipboard;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using LatticeVeilMonoGame.Core;
using LatticeVeilMonoGame.Online.Lan;
using LatticeVeilMonoGame.Online.Eos;
using LatticeVeilMonoGame.Online.Gate;
using LatticeVeilMonoGame.UI;

namespace LatticeVeilMonoGame.UI.Screens;

/// <summary>
/// Join an EOS-hosted game by manually entering the host code (EOS ProductUserId).
/// </summary>
public sealed class JoinByCodeScreen : IScreen
{
    private const int MaxCodeLength = 96;
    private const double PresenceRefreshIntervalSeconds = 5.0;

    private readonly MenuStack _menus;
    private readonly AssetLoader _assets;
    private readonly PixelFont _font;
    private readonly Texture2D _pixel;
    private readonly Logger _log;
    private readonly PlayerProfile _profile;
    private readonly global::Microsoft.Xna.Framework.GraphicsDeviceManager _graphics;
    private readonly EosIdentityStore _identityStore;
    private EosClient? _eos;

    private readonly Button _joinBtn;
    private readonly Button _pasteBtn;
	private readonly Button _saveFriendBtn;
    private readonly Button _backBtn;

	private readonly List<Button> _friendJoinBtns = new();
	private readonly List<Button> _friendRemoveBtns = new();

    private Texture2D? _bg;
    private Texture2D? _panel;

    private Rectangle _viewport;
    private Rectangle _panelRect;
    private Rectangle _codeRect;
	private Rectangle _friendsRect;
    private Rectangle _buttonRowRect;

    private bool _codeActive = true;
    private string _code = string.Empty;

    private bool _busy;
    private bool _presenceBusy;
    private double _lastPresenceRefresh = -100;
    private string _localUsername = string.Empty;
    private string _localFriendCode = string.Empty;
    private double _now;
    private string _status = string.Empty;
    private double _statusUntil;
    private readonly Dictionary<string, EosFriendSnapshot> _friendPresenceByJoinCode = new(StringComparer.OrdinalIgnoreCase);

    public JoinByCodeScreen(MenuStack menus, AssetLoader assets, PixelFont font, Texture2D pixel, Logger log, PlayerProfile profile,
        global::Microsoft.Xna.Framework.GraphicsDeviceManager graphics, EosClient? eos, string? initialCode)
    {
        _menus = menus;
        _assets = assets;
        _font = font;
        _pixel = pixel;
        _log = log;
        _profile = profile;
        _graphics = graphics;
        _eos = eos;
        _identityStore = EosIdentityStore.LoadOrCreate(_log);
        RefreshLocalIdentity();

        _code = (initialCode ?? string.Empty).Trim();
        if (_code.Length > MaxCodeLength)
            _code = _code.Substring(0, MaxCodeLength);

        _joinBtn = new Button("JOIN", () => _ = JoinAsync()) { BoldText = true };
        _pasteBtn = new Button("PASTE", PasteFromClipboard) { BoldText = true };
		_saveFriendBtn = new Button("SAVE FRIEND", () => SaveFriend()) { BoldText = true };
        _backBtn = new Button("BACK", () => _menus.Pop()) { BoldText = true };

        try
        {
            _bg = _assets.LoadTexture("textures/menu/backgrounds/JoinByCode_bg.png");
            _panel = _assets.LoadTexture("textures/menu/GUIS/Multiplayer_GUI.png");
            _backBtn.Texture = _assets.LoadTexture("textures/menu/buttons/Back.png");
        }
        catch
        {
            // optional
        }
    }

    public void OnResize(Rectangle viewport)
    {
        _viewport = viewport;

        var panelW = Math.Min(540, (int)(viewport.Width * 0.65f));
		var panelH = Math.Min(430, (int)(viewport.Height * 0.65f));
        var panelX = viewport.X + (viewport.Width - panelW) / 2;
        var panelY = viewport.Y + (viewport.Height - panelH) / 2;
        _panelRect = new Rectangle(panelX, panelY, panelW, panelH);

        var pad = 14;
        var labelH = _font.LineHeight + 4;
        _codeRect = new Rectangle(panelX + pad, panelY + pad + labelH + 10, panelW - pad * 2, _font.LineHeight + 14);

		// Friends list area (below code box, above the bottom buttons)
		var friendsY = _codeRect.Bottom + 12;
		var friendsH = Math.Max(90, _panelRect.Bottom - pad - (_font.LineHeight + 10) - friendsY - 12);
		_friendsRect = new Rectangle(panelX + pad, friendsY, panelW - pad * 2, friendsH);

        var buttonRowH = _font.LineHeight + 10;
        _buttonRowRect = new Rectangle(panelX + pad, _panelRect.Bottom - pad - buttonRowH, panelW - pad * 2, buttonRowH);

        var gap = 10;
		// Position back button in bottom-left corner of full screen with proper aspect ratio
		var backBtnMargin = 20;
		var backBtnBaseW = Math.Max(_backBtn.Texture?.Width ?? 0, 320);
		var backBtnBaseH = Math.Max(_backBtn.Texture?.Height ?? 0, (int)(backBtnBaseW * 0.28f));
		var backBtnScale = Math.Min(1f, Math.Min(240f / backBtnBaseW, 240f / backBtnBaseH));
		var backBtnW = Math.Max(1, (int)Math.Round(backBtnBaseW * backBtnScale));
		var backBtnH = Math.Max(1, (int)Math.Round(backBtnBaseH * backBtnScale));
		_backBtn.Bounds = new Rectangle(
			viewport.X + backBtnMargin, 
			viewport.Bottom - backBtnMargin - backBtnH, 
			backBtnW, 
			backBtnH
		);
		
		// Adjust other buttons to account for back button position
		var buttonW = (_buttonRowRect.Width - gap * 3) / 3;
		_joinBtn.Bounds = new Rectangle(_buttonRowRect.X, _buttonRowRect.Y, buttonW, buttonRowH);
		_pasteBtn.Bounds = new Rectangle(_joinBtn.Bounds.Right + gap, _buttonRowRect.Y, buttonW, buttonRowH);
		_saveFriendBtn.Bounds = new Rectangle(_pasteBtn.Bounds.Right + gap, _buttonRowRect.Y, buttonW, buttonRowH);

		RebuildFriendButtons();
    }

    public void Update(GameTime gameTime, InputState input)
    {
        _now = gameTime.TotalGameTime.TotalSeconds;

        if (input.IsNewKeyPress(Keys.Escape))
        {
            _menus.Pop();
            return;
        }

        if (input.IsNewLeftClick())
        {
            var p = input.MousePosition;
            _codeActive = _codeRect.Contains(p);
        }

        if (_codeActive && !_busy)
        {
            HandleTextInput(input, ref _code, MaxCodeLength);
            if (input.IsNewKeyPress(Keys.Enter))
                _ = JoinAsync();
        }

        RefreshLocalIdentity();
        if (!_busy && !_presenceBusy && _now - _lastPresenceRefresh >= PresenceRefreshIntervalSeconds)
            _ = RefreshFriendPresenceAsync();

		// Enabled/disabled state
        var snapshot = EosRuntimeStatus.Evaluate(_eos);
		_joinBtn.Enabled = !_busy && snapshot.Reason == EosRuntimeReason.Ready && !string.IsNullOrWhiteSpace(_code);
		_pasteBtn.Enabled = !_busy;
		_saveFriendBtn.Enabled = !_busy && !string.IsNullOrWhiteSpace(_code);
		_backBtn.Enabled = true;

		_joinBtn.Update(input);
		_pasteBtn.Update(input);
		_saveFriendBtn.Update(input);
		_backBtn.Update(input);

		foreach (var b in _friendJoinBtns) b.Update(input);
		foreach (var b in _friendRemoveBtns) b.Update(input);

        if (_statusUntil > 0 && _now > _statusUntil)
        {
            _statusUntil = 0;
            _status = string.Empty;
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
        else sb.Draw(_pixel, _panelRect, new Color(0, 0, 0, 180));

        DrawBorder(sb, _panelRect, Color.White);

        var title = "JOIN ONLINE (EOS)";
        var tSize = _font.MeasureString(title);
        var tPos = new Vector2(_panelRect.Center.X - tSize.X / 2f, _panelRect.Y + 12);
        DrawTextBold(sb, title, tPos, Color.White);

        var label = "HOST CODE (ProductUserId)";
        _font.DrawString(sb, label, new Vector2(_codeRect.X, _codeRect.Y - _font.LineHeight - 6), Color.White);

        sb.Draw(_pixel, _codeRect, _codeActive ? new Color(35, 35, 35, 230) : new Color(20, 20, 20, 230));
        DrawBorder(sb, _codeRect, Color.White);

        var codeText = string.IsNullOrWhiteSpace(_code) ? "(paste host code here)" : _code;
        var cColor = string.IsNullOrWhiteSpace(_code) ? new Color(180, 180, 180) : Color.White;
        _font.DrawString(sb, codeText, new Vector2(_codeRect.X + 6, _codeRect.Y + 6), cColor);

        var infoY = _codeRect.Bottom + 8;
        _font.DrawString(sb, $"YOU: {_localUsername}", new Vector2(_codeRect.X, infoY), new Color(220, 220, 220));
        infoY += _font.LineHeight + 2;
        _font.DrawString(sb, $"FRIEND CODE: {(string.IsNullOrWhiteSpace(_localFriendCode) ? "(waiting for EOS login...)" : _localFriendCode)}", new Vector2(_codeRect.X, infoY), new Color(220, 220, 220));
        infoY += _font.LineHeight + 2;
        _font.DrawString(sb, EosRuntimeStatus.Evaluate(_eos).StatusText, new Vector2(_codeRect.X, infoY), new Color(220, 180, 80));

		// Saved friends list
		_font.DrawString(sb, "SAVED FRIENDS", new Vector2(_friendsRect.X, _friendsRect.Y - _font.LineHeight - 6), new Color(220, 220, 220));
		if (_profile.Friends.Count == 0)
		{
			_font.DrawString(sb, "(none yet — paste an ID above and press SAVE FRIEND)", new Vector2(_friendsRect.X + 6, _friendsRect.Y + 6), new Color(180, 180, 180));
		}
		else
		{
			foreach (var b in _friendJoinBtns) b.Draw(sb, _pixel, _font);
			foreach (var b in _friendRemoveBtns) b.Draw(sb, _pixel, _font);
		}

        _joinBtn.Draw(sb, _pixel, _font);
        _pasteBtn.Draw(sb, _pixel, _font);
		_saveFriendBtn.Draw(sb, _pixel, _font);
        _backBtn.Draw(sb, _pixel, _font);

        if (!string.IsNullOrWhiteSpace(_status))
        {
            var pos = new Vector2(_panelRect.X + 14, _panelRect.Bottom - _font.LineHeight - 8);
            _font.DrawString(sb, _status, pos, Color.White);
        }

        sb.End();
    }

    private async Task JoinAsync()
    {
        if (_busy)
            return;

        var code = (_code ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(code))
        {
            SetStatus("Enter a host code.");
            return;
        }

        var snapshot = EosRuntimeStatus.Evaluate(_eos);
        if (snapshot.Reason != EosRuntimeReason.Ready)
        {
            SetStatus(snapshot.StatusText);
            return;
        }

        var gate = OnlineGateClient.GetOrCreate();
        if (!gate.CanUseOfficialOnline(_log, out var gateDenied))
        {
            SetStatus(gateDenied);
            return;
        }

        _busy = true;
        SetStatus("Connecting...");

        try
        {
            if (_eos == null)
            {
                SetStatus("EOS CLIENT UNAVAILABLE");
                return;
            }

			// Relay connections can take longer (especially on first connect).
			var result = await EosP2PClientSession.ConnectAsync(_log, _eos, code, _profile.GetDisplayUsername(), TimeSpan.FromSeconds(25));
            if (!result.Success || result.Session == null)
            {
                SetStatus($"Connect failed: {result.Error}");
                _log.Warn($"EOS join-by-code failed: {result.Error}");
                return;
            }

            var info = result.WorldInfo;
            var worldDir = EnsureJoinedWorld(info);
            var metaPath = Path.Combine(worldDir, "world.json");
            var meta = WorldMeta.CreateFlat(info.WorldName, info.GameMode, info.Width, info.Height, info.Depth, info.Seed);
            meta.PlayerCollision = info.PlayerCollision;
            meta.Save(metaPath, _log);

            _menus.Push(
                new GameWorldScreen(_menus, _assets, _font, _pixel, _log, _profile, _graphics, worldDir, metaPath, result.Session),
                _viewport);
        }
        finally
        {
            _busy = false;
        }
    }

    private void SetStatus(string msg, double seconds = 3.0)
    {
        _status = msg;
        _statusUntil = seconds <= 0 ? 0 : _now + seconds;
    }

	private void PasteFromClipboard()
	{
		try
		{
			var text = WinClipboard.GetText();
			if (string.IsNullOrWhiteSpace(text))
			{
				SetStatus("Clipboard is empty.");
				return;
			}

			// Keep it simple: trim and cap to the same limit the input box uses.
			text = text.Trim();
			if (text.Length > MaxCodeLength)
				text = text.Substring(0, MaxCodeLength);

			_code = text;
			SetStatus("Pasted.", 2);
		}
		catch (Exception ex)
		{
			_log.Warn($"PasteFromClipboard failed: {ex.Message}");
			SetStatus("Could not read clipboard.", 3);
		}
	}

	private void SaveFriend()
	{
		var id = _code.Trim();
		if (string.IsNullOrWhiteSpace(id))
		{
			SetStatus("Nothing to save.", 2);
			return;
		}

		_profile.AddOrUpdateFriend(id);
		_profile.Save(_log);
		SetStatus($"Saved friend {PlayerProfile.ShortId(id)}", 3);
		RebuildFriendButtons();
        _ = RefreshFriendPresenceAsync();
	}

	private void RemoveFriend(string userId)
	{
		_profile.RemoveFriend(userId);
		_profile.Save(_log);
		SetStatus($"Removed friend {PlayerProfile.ShortId(userId)}", 3);
		RebuildFriendButtons();
	}

	private void RebuildFriendButtons()
	{
		_friendJoinBtns.Clear();
		_friendRemoveBtns.Clear();

		// Show up to 6 friends to keep the screen simple.
		var friends = _profile.Friends;
		if (friends == null || friends.Count == 0) return;

		var rowH = 44;
		var gap = 8;
		var maxRows = Math.Min(6, friends.Count);
		var joinW = _friendsRect.Width - 54;
		for (int i = 0; i < maxRows; i++)
		{
			var f = friends[i];
			var y = _friendsRect.Y + 28 + i * (rowH + gap);
			var joinBounds = new Rectangle(_friendsRect.X, y, joinW, rowH);
			var removeBounds = new Rectangle(_friendsRect.Right - 44, y, 44, rowH);

            var label = BuildFriendRowLabel(f);

			var joinBtn = new Button(label, () =>
			{
				_code = f.UserId;
				SetStatus($"Joining {PlayerProfile.ShortId(f.UserId)}...", 2);
				_ = JoinAsync();
			}) { BoldText = true };
			joinBtn.Bounds = joinBounds;
			_friendJoinBtns.Add(joinBtn);

			var removeBtn = new Button("X", () => RemoveFriend(f.UserId)) { BoldText = true };
			removeBtn.Bounds = removeBounds;
			_friendRemoveBtns.Add(removeBtn);
		}
	}

    private string BuildFriendRowLabel(PlayerProfile.FriendEntry friend)
    {
        if (_friendPresenceByJoinCode.TryGetValue(friend.UserId, out var snapshot))
        {
            var name = string.IsNullOrWhiteSpace(snapshot.DisplayName)
                ? (!string.IsNullOrWhiteSpace(friend.Label) ? friend.Label : PlayerProfile.ShortId(friend.UserId))
                : snapshot.DisplayName;
            var friendCode = TryFormatFriendCode(string.IsNullOrWhiteSpace(snapshot.JoinInfo) ? friend.UserId : snapshot.JoinInfo);
            if (!string.IsNullOrWhiteSpace(friendCode))
                name = $"{name} ({friendCode})";
            var state = snapshot.IsHosting
                ? $"HOSTING {snapshot.WorldName ?? "WORLD"}"
                : snapshot.Presence.ToUpperInvariant();
            return $"{name} | {state}";
        }

        var fallbackName = !string.IsNullOrWhiteSpace(friend.LastKnownDisplayName)
            ? friend.LastKnownDisplayName
            : (!string.IsNullOrWhiteSpace(friend.Label) ? friend.Label : $"FRIEND {PlayerProfile.ShortId(friend.UserId)}");
        var fallbackCode = TryFormatFriendCode(friend.UserId);
        if (!string.IsNullOrWhiteSpace(fallbackCode))
            fallbackName = $"{fallbackName} ({fallbackCode})";
        if (!string.IsNullOrWhiteSpace(friend.LastKnownPresence))
            return $"{fallbackName} | {friend.LastKnownPresence.ToUpperInvariant()}";

        if (!string.IsNullOrWhiteSpace(fallbackCode))
            return fallbackName;

        return $"{fallbackName} ({PlayerProfile.ShortId(friend.UserId)})";
    }

    private void RefreshLocalIdentity()
    {
        _eos ??= EosClientProvider.GetOrCreate(_log, "deviceid", allowRetry: true);
        var dirty = false;
        if (!string.IsNullOrWhiteSpace(_eos?.LocalProductUserId)
            && !string.Equals(_identityStore.ProductUserId, _eos.LocalProductUserId, StringComparison.Ordinal))
        {
            _identityStore.ProductUserId = _eos.LocalProductUserId;
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

        _localUsername = displayName;
        _localFriendCode = _identityStore.GetFriendCode();
        if (string.IsNullOrWhiteSpace(_localFriendCode) && !string.IsNullOrWhiteSpace(_eos?.LocalProductUserId))
            _localFriendCode = EosIdentityStore.GenerateFriendCode(_eos.LocalProductUserId);
    }

    private static string TryFormatFriendCode(string? value)
    {
        var trimmed = value?.Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
            return string.Empty;
        return EosIdentityStore.GenerateFriendCode(trimmed);
    }

    private async Task RefreshFriendPresenceAsync()
    {
        _lastPresenceRefresh = _now;
        var gate = OnlineGateClient.GetOrCreate();
        if (!gate.CanUseOfficialOnline(_log, out _))
            return;

        var eos = _eos ?? EosClientProvider.GetOrCreate(_log, "deviceid", allowRetry: true);
        _eos = eos;
        if (EosRuntimeStatus.Evaluate(eos).Reason != EosRuntimeReason.Ready || eos == null)
            return;

        _presenceBusy = true;
        try
        {
            var snapshots = await eos.GetFriendsWithPresenceAsync();
            _friendPresenceByJoinCode.Clear();
            for (var i = 0; i < snapshots.Count; i++)
            {
                var joinCode = snapshots[i].JoinInfo?.Trim();
                if (string.IsNullOrWhiteSpace(joinCode))
                    continue;

                _friendPresenceByJoinCode[joinCode] = snapshots[i];
            }

            var changed = false;
            foreach (var friend in _profile.Friends)
            {
                if (!_friendPresenceByJoinCode.TryGetValue(friend.UserId, out var snap))
                    continue;

                if (string.IsNullOrWhiteSpace(snap.DisplayName))
                    continue;

                if (!string.Equals(friend.LastKnownDisplayName, snap.DisplayName, StringComparison.Ordinal))
                {
                    friend.LastKnownDisplayName = snap.DisplayName;
                    changed = true;
                }

                var presence = snap.IsHosting ? $"hosting {snap.WorldName ?? "world"}" : snap.Presence;
                if (!string.Equals(friend.LastKnownPresence, presence, StringComparison.Ordinal))
                {
                    friend.LastKnownPresence = presence;
                    changed = true;
                }
            }

            if (changed)
                _profile.Save(_log);

            RebuildFriendButtons();
        }
        catch (Exception ex)
        {
            _log.Warn($"JoinByCode friend presence refresh failed: {ex.Message}");
        }
        finally
        {
            _presenceBusy = false;
        }
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
