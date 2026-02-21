using System;
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

public sealed class AddFriendScreen : IScreen
{
    private const int MaxQueryLength = 64;

    private readonly MenuStack _menus;
    private readonly AssetLoader _assets;
    private readonly PixelFont _font;
    private readonly Texture2D _pixel;
    private readonly Logger _log;
    private readonly PlayerProfile _profile;
    private readonly global::Microsoft.Xna.Framework.GraphicsDeviceManager _graphics;
    private readonly EosClient? _eos;

    private readonly Button _addBtn;
    private readonly Button _backBtn;

    private Texture2D? _bg;
    private Texture2D? _panel;

    private Rectangle _viewport;
    private Rectangle _panelRect;
    private Rectangle _inputRect;
    private Rectangle _infoRect;

    private bool _inputActive = true;
    private bool _busy;
    private string _query = string.Empty;
    private string _status = string.Empty;
    private Color _statusColor = Color.White;
    private GateIdentityUser? _lastLookupUser;
    private bool _lastLookupFound;
    private double _now;
    private double _statusUntil;

    public AddFriendScreen(MenuStack menus, AssetLoader assets, PixelFont font, Texture2D pixel, Logger log, PlayerProfile profile,
        global::Microsoft.Xna.Framework.GraphicsDeviceManager graphics, EosClient? eos)
    {
        _menus = menus;
        _assets = assets;
        _font = font;
        _pixel = pixel;
        _log = log;
        _profile = profile;
        _graphics = graphics;
        _eos = eos;

        _addBtn = new Button("ADD", () => _ = SendRequestAsync()) { BoldText = true };
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
    }

    public void OnResize(Rectangle viewport)
    {
        _viewport = viewport;

        // Same size as current screen (ProfileScreen match)
        var panelW = Math.Min(1300, viewport.Width - 20);
        var panelH = Math.Min(700, viewport.Height - 30);
        var panelX = viewport.X + (viewport.Width - panelW) / 2;
        var panelY = viewport.Y + (viewport.Height - panelH) / 2;
        _panelRect = new Rectangle(panelX, panelY, panelW, panelH);

        // 1 inch inset (approx 96 pixels)
        var margin = 96;
        var contentRect = new Rectangle(_panelRect.X + margin, _panelRect.Y + margin, _panelRect.Width - margin * 2, _panelRect.Height - margin * 2);

        var titleH = _font.LineHeight + 12;
        var inputH = _font.LineHeight + 14;

        _inputRect = new Rectangle(contentRect.X, contentRect.Y + titleH + 20, contentRect.Width, inputH);
        
        var infoY = _inputRect.Bottom + 20;
        _infoRect = new Rectangle(contentRect.X, infoY, contentRect.Width, 120);

        var buttonH = Math.Max(44, _font.LineHeight * 2);
        var buttonW = 200;
        _addBtn.Bounds = new Rectangle(contentRect.X, contentRect.Bottom - buttonH, buttonW, buttonH);

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
        _now = gameTime.TotalGameTime.TotalSeconds;

        if (input.IsNewKeyPress(Keys.Escape))
        {
            _menus.Pop();
            return;
        }

        if (input.IsNewLeftClick())
            _inputActive = _inputRect.Contains(input.MousePosition);

        if (_inputActive && !_busy)
        {
            HandleTextInput(input, ref _query, MaxQueryLength);
            if (input.IsNewKeyPress(Keys.Enter))
                _ = SendRequestAsync();
        }

        _addBtn.Enabled = !_busy && !string.IsNullOrWhiteSpace(_query);
        _addBtn.Update(input);
        _backBtn.Update(input);

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
        if (_bg is not null)
            sb.Draw(_bg, UiLayout.WindowViewport, Color.White);
        else
            sb.Draw(_pixel, UiLayout.WindowViewport, Color.Black);
        sb.End();

        sb.Begin(samplerState: SamplerState.PointClamp, transformMatrix: UiLayout.Transform);

        if (_panel is not null)
            sb.Draw(_panel, _panelRect, Color.White);
        else
        {
            sb.Draw(_pixel, _panelRect, new Color(0, 0, 0, 180));
            DrawBorder(sb, _panelRect, Color.White);
        }

        var title = "ADD FRIEND";
        var tSize = _font.MeasureString(title);
        var tPos = new Vector2(_panelRect.Center.X - tSize.X / 2f, _panelRect.Y + 96);
        _font.DrawString(sb, title, tPos, Color.White);

        var labelPos = new Vector2(_inputRect.X, _inputRect.Y - _font.LineHeight - 6);
        _font.DrawString(sb, "ENTER USERNAME", labelPos, Color.White);

        sb.Draw(_pixel, _inputRect, _inputActive ? new Color(35, 35, 35, 230) : new Color(20, 20, 20, 230));
        DrawBorder(sb, _inputRect, Color.White);

        var queryText = string.IsNullOrWhiteSpace(_query) ? "(type here...)" : _query;
        var queryColor = string.IsNullOrWhiteSpace(_query) ? new Color(120, 120, 120) : Color.White;
        _font.DrawString(sb, queryText, new Vector2(_inputRect.X + 8, _inputRect.Y + 6), queryColor);

        sb.Draw(_pixel, _infoRect, new Color(20, 20, 20, 190));
        DrawBorder(sb, _infoRect, new Color(100, 100, 100));
        if (_lastLookupFound && _lastLookupUser != null)
        {
            var lookupName = string.IsNullOrWhiteSpace(_lastLookupUser.DisplayName)
                ? (_lastLookupUser.Username ?? string.Empty)
                : _lastLookupUser.DisplayName;
            _font.DrawString(sb, "FOUND USER", new Vector2(_infoRect.X + 8, _infoRect.Y + 8), new Color(130, 230, 160));
            _font.DrawString(sb, lookupName, new Vector2(_infoRect.X + 8, _infoRect.Y + 8 + _font.LineHeight + 2), Color.White);
            var id = (_lastLookupUser.ProductUserId ?? string.Empty).Trim();
            if (!string.IsNullOrWhiteSpace(id))
            {
                var shortId = id.Length > 12 ? id[..12] : id;
                _font.DrawString(sb, $"ID: {shortId}", new Vector2(_infoRect.X + 8, _infoRect.Y + 8 + ((_font.LineHeight + 2) * 2)), new Color(170, 178, 196));
            }
        }
        else
        {
            var infoText = "Search for players by their username.";
            _font.DrawString(sb, infoText, new Vector2(_infoRect.X + 8, _infoRect.Y + 8), new Color(180, 180, 180));
        }

        _addBtn.Draw(sb, _pixel, _font);
        _backBtn.Draw(sb, _pixel, _font);

        if (!string.IsNullOrWhiteSpace(_status))
        {
            var pos = new Vector2(_panelRect.X + 96, _panelRect.Bottom - 96 - _font.LineHeight);
            _font.DrawString(sb, _status, pos, _statusColor);
        }

        sb.End();
    }

    private async Task SendRequestAsync()
    {
        if (_busy || string.IsNullOrWhiteSpace(_query))
            return;

        var gate = OnlineGateClient.GetOrCreate();

        _busy = true;
        SetStatus("Checking username...");
        _lastLookupFound = false;
        _lastLookupUser = null;

        try
        {
            var username = _query.Trim();
            var lookup = await gate.LookupVeilnetUserAsync(username).ConfigureAwait(false);
            if (!lookup.Ok)
            {
                SetStatus(string.IsNullOrWhiteSpace(lookup.Message) ? "Could not check username." : lookup.Message);
                return;
            }

            if (!lookup.Found || lookup.User == null)
            {
                SetStatus("USER DOES NOT EXIST", seconds: 5.0, color: new Color(220, 60, 60));
                return;
            }

            _lastLookupFound = true;
            _lastLookupUser = lookup.User;
            SetStatus("Sending request...");
            var result = await gate.AddFriendAsync(username).ConfigureAwait(false);
            if (result.Ok)
            {
                await SyncLocalFriendsCacheAsync(gate).ConfigureAwait(false);
                SetStatus("REQUEST SENT", seconds: 5.0, color: new Color(80, 220, 120));
                _query = string.Empty;
                return;
            }

            SetStatus(string.IsNullOrWhiteSpace(result.Message) ? "Failed to send request." : result.Message);
        }
        catch (Exception ex)
        {
            _log.Error($"AddFriendScreen error: {ex.Message}");
            SetStatus("An error occurred.");
        }
        finally
        {
            _busy = false;
        }
    }

    private void SetStatus(string msg, double seconds = 4.0, Color? color = null)
    {
        _status = msg;
        _statusColor = color ?? Color.White;
        _statusUntil = _now + seconds;
    }

    private async Task SyncLocalFriendsCacheAsync(OnlineGateClient gate)
    {
        try
        {
            var friendsResult = await gate.GetFriendsAsync().ConfigureAwait(false);
            if (!friendsResult.Ok)
                return;

            var merged = new System.Collections.Generic.List<PlayerProfile.FriendEntry>();
            var seen = new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var friend in friendsResult.Friends)
            {
                var userId = (friend.ProductUserId ?? string.Empty).Trim();
                if (string.IsNullOrWhiteSpace(userId) || !seen.Add(userId))
                    continue;

                var label = (friend.Username ?? string.Empty).Trim();
                if (string.IsNullOrWhiteSpace(label))
                    label = (friend.DisplayName ?? string.Empty).Trim();
                if (string.IsNullOrWhiteSpace(label))
                    continue;

                if (Guid.TryParse(label, out _))
                    continue;

                merged.Add(new PlayerProfile.FriendEntry
                {
                    UserId = userId,
                    Label = label,
                    LastKnownDisplayName = label,
                    LastKnownPresence = string.Empty
                });
            }

            _profile.Friends = merged;
            _profile.Save(_log);
        }
        catch (Exception ex)
        {
            _log.Warn($"AddFriendScreen: friend cache sync failed: {ex.Message}");
        }
    }

    private void HandleTextInput(InputState input, ref string value, int maxLen)
    {
        var shift = input.IsKeyDown(Keys.LeftShift) || input.IsKeyDown(Keys.RightShift);
        foreach (var key in input.GetTextInputKeys())
        {
            if (key == Keys.Back)
            {
                if (value.Length > 0)
                    value = value.Substring(0, value.Length - 1);
                continue;
            }

            if (key == Keys.OemMinus || key == Keys.Subtract)
            {
                if (value.Length < maxLen) value += shift ? '_' : '-';
                continue;
            }

            if (key >= Keys.D0 && key <= Keys.D9)
            {
                if (value.Length < maxLen) value += (char)('0' + (key - Keys.D0));
                continue;
            }

            if (key >= Keys.NumPad0 && key <= Keys.NumPad9)
            {
                if (value.Length < maxLen) value += (char)('0' + (key - Keys.NumPad0));
                continue;
            }

            if (key >= Keys.A && key <= Keys.Z)
            {
                var c = (char)('A' + (key - Keys.A));
                if (!shift) c = char.ToLowerInvariant(c);
                if (value.Length < maxLen) value += c;
            }
        }
    }

    private void DrawBorder(SpriteBatch sb, Rectangle r, Color color)
    {
        sb.Draw(_pixel, new Rectangle(r.X, r.Y, r.Width, 2), color);
        sb.Draw(_pixel, new Rectangle(r.X, r.Bottom - 2, r.Width, 2), color);
        sb.Draw(_pixel, new Rectangle(r.X, r.Y, 2, r.Height), color);
        sb.Draw(_pixel, new Rectangle(r.Right - 2, r.Y, 2, r.Height), color);
    }
}
