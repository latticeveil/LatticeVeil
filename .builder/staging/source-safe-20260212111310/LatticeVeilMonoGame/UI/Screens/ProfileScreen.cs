using System;
using WinClipboard = System.Windows.Forms.Clipboard;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using LatticeVeilMonoGame.Core;
using LatticeVeilMonoGame.Online.Eos;
using LatticeVeilMonoGame.UI;

namespace LatticeVeilMonoGame.UI.Screens;

public sealed class ProfileScreen : IScreen
{
    private enum ProfileTab
    {
        Identity,
        Friends
    }

    private const int MaxNameLength = 24;

    private readonly MenuStack _menus;
    private readonly AssetLoader _assets;
    private readonly PixelFont _font;
    private readonly Texture2D _pixel;
    private readonly Logger _log;
    private readonly PlayerProfile _profile;
    private readonly global::Microsoft.Xna.Framework.GraphicsDeviceManager _graphics;
    private readonly EosClient? _eos;

    private readonly Button _tabIdentityBtn;
    private readonly Button _tabFriendsBtn;
    private readonly Button _saveBtn;
    private readonly Button _clearBtn;
    private readonly Button _removeFriendBtn;
    private readonly Button _copyIdBtn;
    private readonly Button _backBtn;

    private Texture2D? _bg;
    private Texture2D? _panel;

    private Rectangle _viewport;
    private Rectangle _panelRect;
    private Rectangle _tabIdentityRect;
    private Rectangle _tabFriendsRect;
    private Rectangle _nameRect;
    private Rectangle _friendsRect;

    private string _nameValue;
    private bool _editing;
    private ProfileTab _activeTab = ProfileTab.Identity;
    private int _selectedFriend = -1;

    private string _status = string.Empty;
    private DateTime _statusExpiryUtc = DateTime.MinValue;

    public ProfileScreen(MenuStack menus, AssetLoader assets, PixelFont font, Texture2D pixel, Logger log, PlayerProfile profile,
        global::Microsoft.Xna.Framework.GraphicsDeviceManager graphics, EosClient? eosClient)
    {
        _menus = menus;
        _assets = assets;
        _font = font;
        _pixel = pixel;
        _log = log;
        _profile = profile;
        _graphics = graphics;
        _eos = eosClient;

        _nameValue = _profile.OfflineUsername ?? string.Empty;

        _tabIdentityBtn = new Button("PROFILE", () => _activeTab = ProfileTab.Identity) { BoldText = true };
        _tabFriendsBtn = new Button("FRIENDS", () => _activeTab = ProfileTab.Friends) { BoldText = true };
        _saveBtn = new Button("SAVE", Save) { BoldText = true };
        _clearBtn = new Button("CLEAR", Clear) { BoldText = true };
        _removeFriendBtn = new Button("REMOVE FRIEND", RemoveSelectedFriend) { BoldText = true };
        _copyIdBtn = new Button("COPY MY ID", CopyLocalId) { BoldText = true };
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

        var panelW = Math.Min(860, (int)(viewport.Width * 0.94f));
        var panelH = Math.Min(520, (int)(viewport.Height * 0.86f));
        var panelX = viewport.X + (viewport.Width - panelW) / 2;
        var panelY = viewport.Y + (viewport.Height - panelH) / 2;
        _panelRect = new Rectangle(panelX, panelY, panelW, panelH);

        var pad = 16;
        var tabY = _panelRect.Y + 52;
        var tabW = (_panelRect.Width - (pad * 2) - 8) / 2;
        var tabH = _font.LineHeight + 12;
        _tabIdentityRect = new Rectangle(_panelRect.X + pad, tabY, tabW, tabH);
        _tabFriendsRect = new Rectangle(_tabIdentityRect.Right + 8, tabY, tabW, tabH);
        _tabIdentityBtn.Bounds = _tabIdentityRect;
        _tabFriendsBtn.Bounds = _tabFriendsRect;

        _nameRect = new Rectangle(_panelRect.X + pad, _tabIdentityRect.Bottom + _font.LineHeight + 16, _panelRect.Width - pad * 2, _font.LineHeight + 18);
        _friendsRect = new Rectangle(_panelRect.X + pad, _tabIdentityRect.Bottom + 12, _panelRect.Width - pad * 2, _panelRect.Height - 190);

        var buttonY = _panelRect.Bottom - 58;
        var gap = 8;
        var buttonW = (_panelRect.Width - pad * 2 - gap * 2) / 3;
        var buttonH = Math.Max(44, _font.LineHeight * 2);
        _saveBtn.Bounds = new Rectangle(_panelRect.X + pad, buttonY, buttonW, buttonH);
        _clearBtn.Bounds = new Rectangle(_saveBtn.Bounds.Right + gap, buttonY, buttonW, buttonH);
        _copyIdBtn.Bounds = new Rectangle(_clearBtn.Bounds.Right + gap, buttonY, buttonW, buttonH);
        _removeFriendBtn.Bounds = new Rectangle(_panelRect.X + pad, buttonY, _panelRect.Width - pad * 2, buttonH);

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

        if (input.IsNewKeyPress(Keys.Escape))
        {
            _menus.Pop();
            return;
        }

        _tabIdentityBtn.Update(input);
        _tabFriendsBtn.Update(input);
        _backBtn.Update(input);

        if (_activeTab == ProfileTab.Identity)
        {
            _saveBtn.Update(input);
            _clearBtn.Update(input);
            _copyIdBtn.Update(input);

            if (input.IsNewLeftClick())
                _editing = _nameRect.Contains(input.MousePosition);

            if (_editing)
            {
                HandleTextInput(input, ref _nameValue, MaxNameLength);
                if (input.IsNewKeyPress(Keys.Enter))
                {
                    Save();
                    _editing = false;
                }
            }
        }
        else
        {
            _editing = false;
            _removeFriendBtn.Enabled = _selectedFriend >= 0 && _selectedFriend < _profile.Friends.Count;
            _removeFriendBtn.Update(input);
            HandleFriendSelection(input);
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

        var titlePos = new Vector2(_panelRect.X + 18, _panelRect.Y + 14);
        DrawTextBold(sb, "PROFILE", titlePos, Color.White);

        DrawTabs(sb);

        if (_activeTab == ProfileTab.Identity)
            DrawIdentityTab(sb);
        else
            DrawFriendsTab(sb);

        if (!string.IsNullOrWhiteSpace(_status))
        {
            var statusPos = new Vector2(_panelRect.X + 18, _panelRect.Bottom - _font.LineHeight - 8);
            _font.DrawString(sb, _status, statusPos, Color.White);
        }

        if (_activeTab == ProfileTab.Identity)
        {
            _saveBtn.Draw(sb, _pixel, _font);
            _clearBtn.Draw(sb, _pixel, _font);
            _copyIdBtn.Draw(sb, _pixel, _font);
        }
        else
        {
            _removeFriendBtn.Draw(sb, _pixel, _font);
        }

        _tabIdentityBtn.Draw(sb, _pixel, _font);
        _tabFriendsBtn.Draw(sb, _pixel, _font);
        _backBtn.Draw(sb, _pixel, _font);

        sb.End();
    }

    private void DrawTabs(SpriteBatch sb)
    {
        var identityColor = _activeTab == ProfileTab.Identity ? new Color(60, 60, 60, 220) : new Color(30, 30, 30, 220);
        var friendsColor = _activeTab == ProfileTab.Friends ? new Color(60, 60, 60, 220) : new Color(30, 30, 30, 220);
        sb.Draw(_pixel, _tabIdentityRect, identityColor);
        sb.Draw(_pixel, _tabFriendsRect, friendsColor);
        DrawBorder(sb, _tabIdentityRect, Color.White);
        DrawBorder(sb, _tabFriendsRect, Color.White);
    }

    private void DrawIdentityTab(SpriteBatch sb)
    {
        var x = _panelRect.X + 16;
        var y = _tabIdentityRect.Bottom + 14;

        _font.DrawString(sb, "DISPLAY NAME (IN-GAME)", new Vector2(x, y), Color.White);

        sb.Draw(_pixel, _nameRect, _editing ? new Color(50, 50, 50, 230) : new Color(30, 30, 30, 230));
        DrawBorder(sb, _nameRect, Color.White);
        var name = string.IsNullOrWhiteSpace(_nameValue) ? "(auto)" : _nameValue;
        var npos = new Vector2(_nameRect.X + 8, _nameRect.Y + (_nameRect.Height - _font.LineHeight) / 2f);
        _font.DrawString(sb, name, npos, Color.White);

        var infoY = _nameRect.Bottom + 16;
        var eosSnapshot = EosRuntimeStatus.Evaluate(_eos);
        _font.DrawString(sb, eosSnapshot.StatusText, new Vector2(x, infoY), new Color(220, 180, 80));
        infoY += _font.LineHeight + 4;

        var id = (_eos?.LocalProductUserId ?? string.Empty).Trim();
        var friendCode = string.IsNullOrWhiteSpace(id) ? string.Empty : EosIdentityStore.GenerateFriendCode(id);
        _font.DrawString(sb, $"MY ID: {(string.IsNullOrWhiteSpace(id) ? "(waiting...)" : id)}", new Vector2(x, infoY), Color.White);
        infoY += _font.LineHeight + 2;
        _font.DrawString(sb, $"MY FRIEND CODE: {(string.IsNullOrWhiteSpace(friendCode) ? "(waiting...)" : friendCode)}", new Vector2(x, infoY), Color.White);
        infoY += _font.LineHeight + 2;
        _font.DrawString(sb, $"EOS Config: {EosRuntimeStatus.DescribeConfigSource()}", new Vector2(x, infoY), new Color(180, 180, 180));
    }

    private void DrawFriendsTab(SpriteBatch sb)
    {
        sb.Draw(_pixel, _friendsRect, new Color(20, 20, 20, 220));
        DrawBorder(sb, _friendsRect, Color.White);

        _font.DrawString(sb, "SAVED FRIENDS", new Vector2(_friendsRect.X + 8, _friendsRect.Y + 6), Color.White);

        if (_profile.Friends.Count == 0)
        {
            _font.DrawString(sb, "(no friends saved yet)", new Vector2(_friendsRect.X + 8, _friendsRect.Y + _font.LineHeight + 10), new Color(180, 180, 180));
            return;
        }

        var rowH = _font.LineHeight + 8;
        var y = _friendsRect.Y + _font.LineHeight + 12;
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
    }

    private void HandleFriendSelection(InputState input)
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
        if (index < 0 || index >= _profile.Friends.Count)
            return;

        _selectedFriend = index;
    }

    private void Save()
    {
        _profile.OfflineUsername = (_nameValue ?? string.Empty).Trim();
        _profile.Save(_log);
        SetStatus("Display name saved.");
    }

    private void Clear()
    {
        _nameValue = string.Empty;
        _profile.OfflineUsername = string.Empty;
        _profile.Save(_log);
        SetStatus("Display name cleared.");
    }

    private void RemoveSelectedFriend()
    {
        if (_selectedFriend < 0 || _selectedFriend >= _profile.Friends.Count)
        {
            SetStatus("Select a friend first.");
            return;
        }

        var entry = _profile.Friends[_selectedFriend];
        _profile.Friends.RemoveAt(_selectedFriend);
        _selectedFriend = Math.Min(_selectedFriend, _profile.Friends.Count - 1);
        _profile.Save(_log);
        SetStatus($"Removed {PlayerProfile.ShortId(entry.UserId)}.");
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
