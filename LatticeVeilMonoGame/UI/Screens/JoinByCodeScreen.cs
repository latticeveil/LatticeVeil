using System;
using System.IO;
using System.Threading.Tasks;
using WinClipboard = System.Windows.Forms.Clipboard;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using LatticeVeilMonoGame.Core;
using LatticeVeilMonoGame.Online.Lan;
using LatticeVeilMonoGame.Online.Eos;
using LatticeVeilMonoGame.UI;

namespace LatticeVeilMonoGame.UI.Screens;

/// <summary>
/// Join an EOS-hosted game by manually entering the host code (EOS ProductUserId).
/// </summary>
public sealed class JoinByCodeScreen : IScreen
{
    private const int MaxCodeLength = 96;

    private readonly MenuStack _menus;
	private readonly Game1 _game;
    private readonly AssetLoader _assets;
    private readonly PixelFont _font;
    private readonly Texture2D _pixel;
    private readonly Logger _log;
    private readonly PlayerProfile _profile;
    private readonly global::Microsoft.Xna.Framework.GraphicsDeviceManager _graphics;
    private readonly EosClient _eos;

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
    private double _now;
    private string _status = string.Empty;
    private double _statusUntil;

    public JoinByCodeScreen(MenuStack menus, AssetLoader assets, PixelFont font, Texture2D pixel, Logger log, PlayerProfile profile,
        global::Microsoft.Xna.Framework.GraphicsDeviceManager graphics, EosClient eos, string? initialCode)
    {
        _menus = menus;
        _assets = assets;
        _font = font;
        _pixel = pixel;
        _log = log;
        _profile = profile;
        _graphics = graphics;
        _eos = eos;

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

        var panelW = Math.Min(640, (int)(viewport.Width * 0.85f));
		// Slightly taller so we can show a saved friends list.
		var panelH = Math.Min(430, (int)(viewport.Height * 0.75f));
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
		var buttonW = (_buttonRowRect.Width - gap * 3) / 4;
		_joinBtn.Bounds = new Rectangle(_buttonRowRect.X, _buttonRowRect.Y, buttonW, buttonRowH);
		_pasteBtn.Bounds = new Rectangle(_joinBtn.Bounds.Right + gap, _buttonRowRect.Y, buttonW, buttonRowH);
		_saveFriendBtn.Bounds = new Rectangle(_pasteBtn.Bounds.Right + gap, _buttonRowRect.Y, buttonW, buttonRowH);
		_backBtn.Bounds = new Rectangle(_saveFriendBtn.Bounds.Right + gap, _buttonRowRect.Y, buttonW, buttonRowH);

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


		// Enabled/disabled state
		_joinBtn.Enabled = !_busy && _eos.IsLoggedIn && !string.IsNullOrWhiteSpace(_code);
		_pasteBtn.Enabled = !_busy;
		_saveFriendBtn.Enabled = !_busy && _eos.IsLoggedIn && !string.IsNullOrWhiteSpace(_code);
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

        if (!_eos.IsLoggedIn)
        {
            SetStatus("EOS is still logging in...");
            return;
        }

        _busy = true;
        SetStatus("Connecting...");

        try
        {
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
			if (text.Length > 64)
				text = text.Substring(0, 64);

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
		if (!_eos.IsLoggedIn)
		{
			SetStatus("EOS not logged in yet.", 3);
			return;
		}
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

			var label = string.IsNullOrWhiteSpace(f.Label)
				? $"FRIEND {PlayerProfile.ShortId(f.UserId)}"
				: $"{f.Label} ({PlayerProfile.ShortId(f.UserId)})";

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
