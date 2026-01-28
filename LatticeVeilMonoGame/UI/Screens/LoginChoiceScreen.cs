using System;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using LatticeVeilMonoGame.Core;
using LatticeVeilMonoGame.Online.Eos;
using LatticeVeilMonoGame.UI;

namespace LatticeVeilMonoGame.UI.Screens;

public sealed class LoginChoiceScreen : IScreen
{
    private readonly MenuStack _menus;
    private readonly AssetLoader _assets;
    private readonly PixelFont _font;
    private readonly Texture2D _pixel;
    private readonly Logger _log;
    private readonly PlayerProfile _profile;
    private readonly global::Microsoft.Xna.Framework.GraphicsDeviceManager _graphics;
    private readonly GameStartOptions? _startOptions;
    private readonly Action<EosClient?> _setEosClient;

    private Texture2D? _bg;
    private Rectangle _viewport;
    private Rectangle _panelRect;

    private readonly Button _epicBtn;
    private readonly Button _localBtn;

    private string _status = string.Empty;
    private Color _statusColor = Color.White;
    private bool _complete;
    private EosClient? _autoLoginClient;
    private double _autoLoginDeadline = -1;
    private bool _autoLoginChecked;

    private const double AutoLoginTimeoutSeconds = 2.0;

    public LoginChoiceScreen(
        MenuStack menus,
        AssetLoader assets,
        PixelFont font,
        Texture2D pixel,
        Logger log,
        PlayerProfile profile,
        global::Microsoft.Xna.Framework.GraphicsDeviceManager graphics,
        GameStartOptions? startOptions,
        Action<EosClient?> setEosClient)
    {
        _menus = menus;
        _assets = assets;
        _font = font;
        _pixel = pixel;
        _log = log;
        _profile = profile;
        _graphics = graphics;
        _startOptions = startOptions;
        _setEosClient = setEosClient;

        _epicBtn = new Button("LOGIN WITH EPIC", OnEpicLogin);
        _localBtn = new Button("LOCAL ONLY", OnLocalOnly);

        try
        {
            _bg = _assets.LoadTexture("textures/menu/backgrounds/MainMenu.png");
        }
        catch (Exception ex)
        {
            _log.Warn($"LoginChoiceScreen background load: {ex.Message}");
        }
    }

    public void OnResize(Rectangle viewport)
    {
        _viewport = viewport;

        var buttonW = Math.Min(360, (int)(viewport.Width * 0.5f));
        var buttonH = (int)(buttonW * 0.18f);
        var gap = 14;
        var padding = 18;

        var textHeight = _font.LineHeight * 2 + 6;
        var statusHeight = _font.LineHeight + 6;
        var panelH = padding * 2 + textHeight + statusHeight + buttonH * 2 + gap * 2;
        var panelW = Math.Min(520, (int)(viewport.Width * 0.7f));

        _panelRect = new Rectangle(
            viewport.X + (viewport.Width - panelW) / 2,
            viewport.Y + (viewport.Height - panelH) / 2,
            panelW,
            panelH);

        var buttonX = _panelRect.X + (_panelRect.Width - buttonW) / 2;
        var firstY = _panelRect.Y + padding + textHeight + gap;

        _epicBtn.Bounds = new Rectangle(buttonX, firstY, buttonW, buttonH);
        _localBtn.Bounds = new Rectangle(buttonX, firstY + buttonH + gap, buttonW, buttonH);
    }

    public void Update(GameTime gameTime, InputState input)
    {
        if (_complete)
            return;

        var now = gameTime.TotalGameTime.TotalSeconds;
        if (!_autoLoginChecked)
            BeginAutoLogin(now);

        if (_autoLoginClient != null)
        {
            if (_autoLoginClient.IsLoggedIn || _autoLoginClient.IsEpicLoggedIn)
            {
                _setEosClient(_autoLoginClient);
                Continue(_autoLoginClient);
                return;
            }

            if (_autoLoginClient.SilentLoginFailed || (_autoLoginDeadline > 0 && now >= _autoLoginDeadline))
            {
                _autoLoginClient = null;
                _status = string.Empty;
            }
        }

        if (input.IsNewKeyPress(Keys.Escape))
        {
            OnLocalOnly();
            return;
        }

        var autoLoginInProgress = _autoLoginClient != null
            && !_autoLoginClient.SilentLoginFailed
            && (_autoLoginDeadline < 0 || now < _autoLoginDeadline);

        if (!autoLoginInProgress)
        {
            _epicBtn.Update(input);
            _localBtn.Update(input);
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
            sb.Draw(_pixel, UiLayout.WindowViewport, new Color(0, 0, 0));

        sb.End();

        sb.Begin(samplerState: SamplerState.PointClamp, transformMatrix: UiLayout.Transform);

        DrawPanel(sb);
        _epicBtn.Draw(sb, _pixel, _font);
        _localBtn.Draw(sb, _pixel, _font);

        sb.End();
    }

    private void DrawPanel(SpriteBatch sb)
    {
        sb.Draw(_pixel, _panelRect, new Color(0, 0, 0, 190));
        DrawBorder(sb, _panelRect, Color.White);

        var title = "LOGIN MODE";
        var subtitle = "CHOOSE HOW YOU WANT TO PLAY";
        var titleSize = _font.MeasureString(title);
        var subtitleSize = _font.MeasureString(subtitle);

        var titleX = _panelRect.Center.X - (int)(titleSize.X / 2f);
        var titleY = _panelRect.Y + 12;
        _font.DrawString(sb, title, new Vector2(titleX, titleY), Color.White);

        var subtitleX = _panelRect.Center.X - (int)(subtitleSize.X / 2f);
        var subtitleY = titleY + _font.LineHeight + 4;
        _font.DrawString(sb, subtitle, new Vector2(subtitleX, subtitleY), Color.White);

        if (!string.IsNullOrWhiteSpace(_status))
        {
            var statusSize = _font.MeasureString(_status);
            var statusX = _panelRect.Center.X - (int)(statusSize.X / 2f);
            var statusY = _panelRect.Bottom - _font.LineHeight - 10;
            _font.DrawString(sb, _status, new Vector2(statusX, statusY), _statusColor);
        }
    }

    private void OnEpicLogin()
    {
        if (_complete)
            return;

        var client = EosClientProvider.GetOrCreate(_log, "epic", allowRetry: true);
        if (client == null)
        {
            if (EosConfig.IsRemoteFetchPending)
            {
                SetStatus("EOS CONFIG LOADING");
                return;
            }

            var cfg = EosConfig.Load(_log);
            SetStatus(cfg == null ? "EOS CONFIG MISSING" : "EOS SDK UNAVAILABLE");
            return;
        }

        client.StartLogin();
        _setEosClient(client);
        SetStatus("EPIC LOGIN REQUESTED", success: true);
        Continue(client);
    }

    private void OnLocalOnly()
    {
        if (_complete)
            return;

        if (_startOptions?.HasJoinToken == true)
        {
            SetStatus("EPIC LOGIN REQUIRED");
            return;
        }

        _setEosClient(null);
        SetStatus("LOCAL MODE ENABLED", success: true);
        Continue(null);
    }

    private void Continue(EosClient? client)
    {
        if (_complete)
            return;

        _complete = true;

        _menus.Pop();
        if (_startOptions?.HasJoinToken == true)
        {
            _menus.Push(new MultiplayerScreen(_menus, _assets, _font, _pixel, _log, _profile, _graphics, client), _viewport);
        }
        else
        {
            _menus.Push(new MainMenuScreen(_menus, _assets, _font, _pixel, _log, _profile, _graphics, client), _viewport);
        }
    }

    private void SetStatus(string message, bool success = false)
    {
        _status = message;
        _statusColor = success ? Color.LimeGreen : Color.OrangeRed;
    }

    private void BeginAutoLogin(double now)
    {
        _autoLoginChecked = true;

        var client = EosClientProvider.GetOrCreate(_log, "epic", allowRetry: true, autoLogin: false);
        if (client == null)
        {
            if (EosConfig.IsRemoteFetchPending)
                SetStatus("EOS CONFIG LOADING");
            return;
        }

        _autoLoginClient = client;
        _autoLoginDeadline = now + AutoLoginTimeoutSeconds;
        _status = "CHECKING LOGIN...";
        _statusColor = Color.LightGray;
        client.StartSilentLogin();
    }

    private void DrawBorder(SpriteBatch sb, Rectangle rect, Color color)
    {
        sb.Draw(_pixel, new Rectangle(rect.X, rect.Y, rect.Width, 2), color);
        sb.Draw(_pixel, new Rectangle(rect.X, rect.Bottom - 2, rect.Width, 2), color);
        sb.Draw(_pixel, new Rectangle(rect.X, rect.Y, 2, rect.Height), color);
        sb.Draw(_pixel, new Rectangle(rect.Right - 2, rect.Y, 2, rect.Height), color);
    }
}
