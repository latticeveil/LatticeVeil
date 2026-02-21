using System;
using System.IO;
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

/// <summary>
/// Join an online-hosted game by host code, friend code, or reserved username.
/// Friend management lives in Profile > Friends.
/// </summary>
public sealed class JoinByCodeScreen : IScreen
{
    private const int MaxCodeLength = 96;

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
    private readonly Button _backBtn;

    private Texture2D? _bg;
    private Texture2D? _panel;

    private Rectangle _viewport;
    private Rectangle _panelRect;
    private Rectangle _codeRect;
    private Rectangle _infoRect;
    private Rectangle _buttonRowRect;

    private bool _codeActive = true;
    private bool _busy;
    private bool _autoJoinPending;

    private string _code = string.Empty;
    private string _status = string.Empty;
    private string _localUsername = string.Empty;

    private double _now;
    private double _statusUntil;

    public JoinByCodeScreen(
        MenuStack menus,
        AssetLoader assets,
        PixelFont font,
        Texture2D pixel,
        Logger log,
        PlayerProfile profile,
        global::Microsoft.Xna.Framework.GraphicsDeviceManager graphics,
        EosClient? eos,
        string? initialCode,
        bool autoJoin = false)
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

        _code = (initialCode ?? string.Empty).Trim();
        if (_code.Length > MaxCodeLength)
            _code = _code.Substring(0, MaxCodeLength);
        _autoJoinPending = autoJoin && !string.IsNullOrWhiteSpace(_code);

        _joinBtn = new Button("JOIN", () => _ = JoinAsync()) { BoldText = true };
        _pasteBtn = new Button("PASTE", PasteFromClipboard) { BoldText = true };
        _backBtn = new Button("BACK", () => _menus.Pop()) { BoldText = true };

        try
        {
            _bg = _assets.LoadTexture("textures/menu/backgrounds/JoinByCode_bg.png");
            _panel = _assets.LoadTexture("textures/menu/GUIS/Multiplayer_GUI.png");
            _backBtn.Texture = _assets.LoadTexture("textures/menu/buttons/Back.png");
        }
        catch
        {
            // Optional cosmetics.
        }
    }

    public void OnResize(Rectangle viewport)
    {
        _viewport = viewport;

        var panelW = Math.Min(980, viewport.Width - 90);
        var panelH = Math.Min(560, viewport.Height - 120);
        var panelX = viewport.X + (viewport.Width - panelW) / 2;
        var panelY = viewport.Y + (viewport.Height - panelH) / 2;
        _panelRect = new Rectangle(panelX, panelY, panelW, panelH);

        var pad = 20;
        var titleH = _font.LineHeight + 12;
        var labelH = _font.LineHeight + 6;
        var inputH = _font.LineHeight + 14;

        var codeY = panelY + pad + titleH + labelH + 6;
        _codeRect = new Rectangle(panelX + pad, codeY, panelW - pad * 2, inputH);

        var infoY = _codeRect.Bottom + 10;
        var infoH = Math.Max(130, _panelRect.Bottom - (pad + 72) - infoY);
        _infoRect = new Rectangle(panelX + pad, infoY, panelW - pad * 2, infoH);

        var rowH = Math.Max(36, _font.LineHeight + 14);
        _buttonRowRect = new Rectangle(panelX + pad, _panelRect.Bottom - pad - rowH, panelW - pad * 2, rowH);

        var gap = 10;
        var buttonW = (_buttonRowRect.Width - gap) / 2;
        _joinBtn.Bounds = new Rectangle(_buttonRowRect.X, _buttonRowRect.Y, buttonW, rowH);
        _pasteBtn.Bounds = new Rectangle(_joinBtn.Bounds.Right + gap, _buttonRowRect.Y, buttonW, rowH);

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
            backBtnH);
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
            _codeActive = _codeRect.Contains(input.MousePosition);

        if (_codeActive && !_busy)
        {
            HandleTextInput(input, ref _code, MaxCodeLength);
            if (input.IsNewKeyPress(Keys.Enter))
                _ = JoinAsync();
        }

        if (_autoJoinPending && !_busy)
        {
            _autoJoinPending = false;
            _ = JoinAsync();
        }

        RefreshLocalIdentity();
        var snapshot = EosRuntimeStatus.Evaluate(_eos);

        _joinBtn.Enabled = !_busy
            && snapshot.Reason == EosRuntimeReason.Ready
            && !string.IsNullOrWhiteSpace(_code);
        _pasteBtn.Enabled = !_busy;
        _backBtn.Enabled = true;

        _joinBtn.Update(input);
        _pasteBtn.Update(input);
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

        var title = "JOIN ONLINE";
        var tSize = _font.MeasureString(title);
        var tPos = new Vector2(_panelRect.Center.X - tSize.X / 2f, _panelRect.Y + 12);
        DrawTextBold(sb, title, tPos, Color.White);

        _font.DrawString(sb,
            "USERNAME (CODE FALLBACK SUPPORTED)",
            new Vector2(_codeRect.X, _codeRect.Y - _font.LineHeight - 6),
            Color.White);

        sb.Draw(_pixel, _codeRect, _codeActive ? new Color(35, 35, 35, 230) : new Color(20, 20, 20, 230));
        DrawBorder(sb, _codeRect, Color.White);

        var codeText = string.IsNullOrWhiteSpace(_code)
            ? "(enter reserved username)"
            : _code;
        var codeColor = string.IsNullOrWhiteSpace(_code) ? new Color(180, 180, 180) : Color.White;
        _font.DrawString(sb, codeText, new Vector2(_codeRect.X + 6, _codeRect.Y + 6), codeColor);

        sb.Draw(_pixel, _infoRect, new Color(20, 20, 20, 190));
        DrawBorder(sb, _infoRect, new Color(180, 180, 180));

        var lineY = _infoRect.Y + 8;
        _font.DrawString(sb, $"PROFILE: {_localUsername}", new Vector2(_infoRect.X + 8, lineY), new Color(220, 220, 220));
        lineY += _font.LineHeight + 2;
        _font.DrawString(sb, "Join by username. If host shared a code, paste it here.", new Vector2(_infoRect.X + 8, lineY), new Color(180, 180, 180));
        lineY += _font.LineHeight + 2;
        _font.DrawString(sb, EosRuntimeStatus.Evaluate(_eos).StatusText, new Vector2(_infoRect.X + 8, lineY), new Color(220, 180, 80));
        lineY += _font.LineHeight + 8;
        _font.DrawString(sb, "Manage friends in PROFILE > FRIENDS.", new Vector2(_infoRect.X + 8, lineY), new Color(180, 180, 180));

        _joinBtn.Draw(sb, _pixel, _font);
        _pasteBtn.Draw(sb, _pixel, _font);
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

        var rawCode = (_code ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(rawCode))
        {
            SetStatus("Enter a host code.");
            return;
        }

        if (!TryNormalizeHostCode(rawCode, out var code, out var normalizeMessage))
        {
            SetStatus(normalizeMessage);
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

        var resolvedCode = await ResolveHostCodeAsync(code, gate);
        if (!resolvedCode.Ok)
        {
            SetStatus(resolvedCode.Error);
            return;
        }

        code = resolvedCode.HostCode;
        _code = code;

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
            var result = await EosP2PClientSession.ConnectAsync(
                _log,
                _eos,
                code,
                _profile.GetDisplayUsername(),
                TimeSpan.FromSeconds(25));

            if (!result.Success || result.Session == null)
            {
                SetStatus($"Connect failed: {result.Error}");
                _log.Warn($"EOS join-by-code failed: {result.Error}");
                return;
            }

            var info = result.WorldInfo;
            var worldDir = JoinedWorldCache.PrepareJoinedWorldPath(info, _log);
            var metaPath = Paths.GetWorldMetaPath(worldDir);
            var meta = WorldMeta.CreateFlat(
                info.WorldName,
                info.GameMode,
                info.Width,
                info.Height,
                info.Depth,
                info.Seed);

            meta.PlayerCollision = info.PlayerCollision;
            meta.WorldId = JoinedWorldCache.ResolveWorldId(info);
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

            text = text.Trim();
            if (text.Length > MaxCodeLength)
                text = text.Substring(0, MaxCodeLength);

            _code = text;
            if (TryNormalizeHostCode(_code, out var normalizedCode, out _))
                _code = normalizedCode;
            SetStatus("Pasted.", 2);
        }
        catch (Exception ex)
        {
            _log.Warn($"PasteFromClipboard failed: {ex.Message}");
            SetStatus("Could not read clipboard.", 3);
        }
    }

    private bool TryNormalizeHostCode(string rawInput, out string hostCode, out string error)
    {
        hostCode = string.Empty;
        error = string.Empty;

        var value = (rawInput ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(value))
        {
            error = "Host code is empty.";
            return false;
        }

        var hostCodeMarker = "HOST CODE:";
        var markerIndex = value.IndexOf(hostCodeMarker, StringComparison.OrdinalIgnoreCase);
        if (markerIndex >= 0)
            value = value.Substring(markerIndex + hostCodeMarker.Length).Trim();

        var hostLinkMarker = "HOST LINK:";
        markerIndex = value.IndexOf(hostLinkMarker, StringComparison.OrdinalIgnoreCase);
        if (markerIndex >= 0)
            value = value.Substring(markerIndex + hostLinkMarker.Length).Trim();

        var embeddedLinkIndex = value.IndexOf("latticeveil://join/", StringComparison.OrdinalIgnoreCase);
        if (embeddedLinkIndex >= 0)
            value = value.Substring(embeddedLinkIndex);

        if (value.StartsWith("latticeveil://join/", StringComparison.OrdinalIgnoreCase))
            value = value.Substring("latticeveil://join/".Length).Trim();

        if (Uri.TryCreate(value, UriKind.Absolute, out var uri))
        {
            if (string.Equals(uri.Scheme, "latticeveil", StringComparison.OrdinalIgnoreCase)
                && string.Equals(uri.Host, "join", StringComparison.OrdinalIgnoreCase))
            {
                var path = uri.AbsolutePath?.Trim('/') ?? string.Empty;
                value = !string.IsNullOrWhiteSpace(path)
                    ? path
                    : (uri.Query ?? string.Empty).Replace("?code=", string.Empty, StringComparison.OrdinalIgnoreCase).Trim();
            }
            else
            {
                var query = uri.Query ?? string.Empty;
                var codeIndex = query.IndexOf("code=", StringComparison.OrdinalIgnoreCase);
                if (codeIndex >= 0)
                {
                    var code = query.Substring(codeIndex + 5);
                    var amp = code.IndexOf('&');
                    if (amp >= 0)
                        code = code.Substring(0, amp);
                    value = Uri.UnescapeDataString(code).Trim();
                }
            }
        }

        if (value.StartsWith("puid=", StringComparison.OrdinalIgnoreCase))
            value = value.Substring(5).Trim();

        if (value.Length > MaxCodeLength)
            value = value.Substring(0, MaxCodeLength);

        if (string.IsNullOrWhiteSpace(value))
        {
            error = "Host code is empty.";
            return false;
        }

        hostCode = value;
        return true;
    }

    private async Task<(bool Ok, string HostCode, string DisplayName, string Error)> ResolveHostCodeAsync(
        string normalizedCode,
        OnlineGateClient gate)
    {
        var value = (normalizedCode ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(value))
            return (false, string.Empty, string.Empty, "Host code is empty.");

        var resolve = await gate.ResolveIdentityAsync(value);
        if (resolve.Found && resolve.User != null)
        {
            var user = resolve.User;
            var display = string.IsNullOrWhiteSpace(user.DisplayName) ? user.Username : user.DisplayName;
            return (true, user.ProductUserId, display, string.Empty);
        }

        if (value.StartsWith("RC-", StringComparison.OrdinalIgnoreCase))
        {
            var reason = string.IsNullOrWhiteSpace(resolve.Reason) ? "Friend code not found." : resolve.Reason;
            return (false, string.Empty, string.Empty, reason);
        }

        if (LooksLikeReservedUsername(value))
        {
            var reason = string.IsNullOrWhiteSpace(resolve.Reason) ? "Username not found." : resolve.Reason;
            return (false, string.Empty, string.Empty, reason);
        }

        return (true, value, string.Empty, string.Empty);
    }

    private static bool LooksLikeReservedUsername(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;

        // Reserved usernames are short; long values are likely direct host IDs.
        if (value.Length > 16)
            return false;

        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            if (char.IsLetterOrDigit(c) || c == '_')
                continue;
            return false;
        }

        return true;
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
