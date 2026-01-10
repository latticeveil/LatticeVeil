using System;
using System.IO;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using RedactedCraftMonoGame.Core;
using RedactedCraftMonoGame.Online.Eos;
using RedactedCraftMonoGame.UI;
using RedactedCraftMonoGame.UI.Screens;

namespace RedactedCraftMonoGame;

public sealed class Game1 : Game
{
    private readonly GameStartOptions? _startOptions;

    private readonly GraphicsDeviceManager _graphics;
    private SpriteBatch? _spriteBatch;
    private Texture2D? _pixel;
    private AssetLoader? _assets;
    private PixelFont? _font;

    private readonly MenuStack _menus = new();
    private readonly InputState _input = new();
    private readonly Logger _log;
    private readonly PlayerProfile _profile;
    private EosClient? _eosClient;
    private bool _wasActive;
    private GameSettings _settings = new();
    private DateTime _settingsStamp = DateTime.MinValue;
    private bool _mouseCaptured;
    private bool _ignoreNextCaptureDelta;
    private Point _captureCenter;
    private IMouseCaptureScreen? _captureOwner;

    private bool _screenshotRequested;
    private string? _screenshotToast;
    private double _screenshotToastUntil;
    private bool _exitRequested;

	public PlayerProfile Profile => _profile;

    public Game1(Logger log, PlayerProfile profile, GameStartOptions? startOptions = null)
    {
        _log = log;
        _profile = profile;
        _startOptions = startOptions;


        _graphics = new GraphicsDeviceManager(this);
        IsMouseVisible = true;
        Window.AllowUserResizing = false;
        Window.Title = "Redactedcraft";

        Exiting += (s, e) =>
        {
            _log.Info("Game exiting; popping all screens.");
            while (_menus.Count > 0)
                _menus.Pop();
        };

        // No Content pipeline usage.
        Content.RootDirectory = "Content";
    }

    protected override void Initialize()
    {
        base.Initialize();

        Window.ClientSizeChanged += (_, _) =>
        {
            UpdateUiLayout();
        };

        ApplyStartupSettings();

    }

    protected override void LoadContent()
    {
        _spriteBatch = new SpriteBatch(GraphicsDevice);

        _pixel = new Texture2D(GraphicsDevice, 1, 1);
        _pixel.SetData(new[] { Color.White });

        _font = new PixelFont(_pixel, scale: 2);
        _assets = new AssetLoader(GraphicsDevice, _log);


        // EOS Device-ID only (no login UI).
        if (_startOptions?.Offline == true)
        {
            _log.Info("Starting in OFFLINE mode; EOS initialization skipped.");
            _eosClient = null;
        }
        else
        {
            _eosClient = EosClientProvider.GetOrCreate(_log, "device", allowRetry: true);
            if (_eosClient == null)
                _log.Warn("EOS client unavailable; online play disabled.");
        }

        if (_startOptions?.HasJoinToken == true)
        {
            _menus.Push(
                new JoinByCodeScreen(
                    _menus,
                    _assets,
                    _font,
                    _pixel,
                    _log,
                    _profile,
                    _graphics,
                    _eosClient,
                    _startOptions.JoinToken),
                UiLayout.Viewport);
        }
        else
        {
            _menus.Push(
                new MainMenuScreen(_menus, _assets, _font, _pixel, _log, _profile, _graphics, _eosClient, _startOptions?.Offline ?? false),
                UiLayout.Viewport);
        }

        _log.Info("Game initialized.");
    }

    protected override void UnloadContent()
    {
        _assets?.Dispose();
        _pixel?.Dispose();
        _spriteBatch?.Dispose();
        _eosClient?.Dispose();
        base.UnloadContent();
    }

    protected override void Update(GameTime gameTime)
    {
        if (!IsActive)
        {
            ReleaseMouseCapture();
            _input.Reset();
            _wasActive = false;
            base.Update(gameTime);
            return;
        }

        if (!_wasActive)
        {
            _input.Reset();
            _wasActive = true;
            UpdateMouseCapture(computeDelta: false);
            base.Update(gameTime);
            return;
        }

        _input.Update();
        RefreshSettingsIfChanged();
        UpdateUiLayout();
        UpdateMouseCapture(computeDelta: true);
        _eosClient?.Tick();

        if (_input.IsNewKeyPress(Keys.F2))
            RequestScreenshot();

        // Global quit (Alt+F4 is handled by OS; Esc handled in screens)
        _menus.Update(gameTime, _input);
        if (!_exitRequested && _menus.Count == 0)
        {
            _exitRequested = true;
            ReleaseMouseCapture();
            _log.Info("Menu stack empty. Exiting game.");
            Exit();
            return;
        }
        UpdateMouseCapture(computeDelta: false);
        base.Update(gameTime);
    }

    protected override void Draw(GameTime gameTime)
    {
        GraphicsDevice.Clear(Color.Black);

        if (_spriteBatch is null || _pixel is null || _font is null)
        {
            base.Draw(gameTime);
            return;
        }

        _menus.Draw(_spriteBatch, UiLayout.Viewport);
        if (_screenshotRequested)
        {
            TakeScreenshot(gameTime);
            _screenshotRequested = false;
        }
        DrawScreenshotToast(gameTime);

        base.Draw(gameTime);
    }

    private Rectangle ViewportRect =>
        new(0, 0, GraphicsDevice.Viewport.Width, GraphicsDevice.Viewport.Height);

    private void ApplyStartupSettings()
    {
        _settings = GameSettings.LoadOrCreate(_log);
        _settingsStamp = GetSettingsStamp();
        _settings.ApplyGraphics(_graphics);
        _settings.ApplyAudio();
        UpdateUiLayout(forceResize: true);

        try
        {
            var adapter = GraphicsDevice.Adapter;
            var mode = adapter.CurrentDisplayMode;
            _log.Info($"GPU: {adapter.Description}");
            _log.Info($"DISPLAY: {mode.Width}x{mode.Height} ({mode.Format})");
            _log.Info($"PROFILE: {GraphicsDevice.GraphicsProfile}");
        }
        catch (Exception ex)
        {
            _log.Warn($"GPU detection failed: {ex.Message}");
        }
    }

    private void RefreshSettingsIfChanged()
    {
        var stamp = GetSettingsStamp();
        if (stamp == _settingsStamp)
            return;

        _settingsStamp = stamp;
        _settings = GameSettings.LoadOrCreate(_log);
    }

    private DateTime GetSettingsStamp()
    {
        try
        {
            return File.Exists(Paths.SettingsJsonPath)
                ? File.GetLastWriteTimeUtc(Paths.SettingsJsonPath)
                : DateTime.MinValue;
        }
        catch
        {
            return DateTime.MinValue;
        }
    }

    private void UpdateUiLayout(bool forceResize = false)
    {
        var scale = UiLayout.GetEffectiveScale(_settings.GuiScale);
        var changed = UiLayout.Update(ViewportRect, scale);
        _input.SetUiTransform(UiLayout.Scale, UiLayout.Offset);
        if (forceResize || changed)
            _menus.OnResize(UiLayout.Viewport);
    }

    private void UpdateMouseCapture(bool computeDelta)
    {
        var top = _menus.Peek();
        var desired = top as IMouseCaptureScreen;
        var shouldCapture = IsActive && desired?.WantsMouseCapture == true;

        if (!shouldCapture)
        {
            if (_mouseCaptured)
                ReleaseMouseCapture();
        }
        else
        {
            if (!_mouseCaptured)
                EngageMouseCapture(desired!);
            else if (_captureOwner != desired)
            {
                ReleaseMouseCapture();
                EngageMouseCapture(desired!);
            }
        }

        if (!computeDelta)
            return;

        if (_mouseCaptured && IsActive)
        {
            var viewport = GraphicsDevice.Viewport;
            _captureCenter = new Point(viewport.Width / 2, viewport.Height / 2);

            var raw = _input.RawMousePosition;
            var deltaPx = new Point(raw.X - _captureCenter.X, raw.Y - _captureCenter.Y);
            if (_ignoreNextCaptureDelta)
            {
                _input.SetLookDelta(Vector2.Zero);
                _ignoreNextCaptureDelta = false;
            }
            else
            {
                var sensitivity = Math.Clamp(_settings.MouseSensitivity, 0.0005f, 0.01f);
                var delta = new Vector2(deltaPx.X * sensitivity, deltaPx.Y * sensitivity);
                _input.SetLookDelta(delta);
            }
            Mouse.SetPosition(_captureCenter.X, _captureCenter.Y);
        }
        else
        {
            _input.SetLookDelta(Vector2.Zero);
        }
    }

    private void EngageMouseCapture(IMouseCaptureScreen screen)
    {
        _mouseCaptured = true;
        _captureOwner = screen;
        _ignoreNextCaptureDelta = true;
        IsMouseVisible = false;

        var viewport = GraphicsDevice.Viewport;
        _captureCenter = new Point(viewport.Width / 2, viewport.Height / 2);
        Mouse.SetPosition(_captureCenter.X, _captureCenter.Y);
        screen.OnMouseCaptureGained();
    }

    private void ReleaseMouseCapture()
    {
        if (!_mouseCaptured)
            return;

        _mouseCaptured = false;
        IsMouseVisible = true;
        _ignoreNextCaptureDelta = false;
        _input.SetLookDelta(Vector2.Zero);

        try
        {
            _captureOwner?.OnMouseCaptureLost();
        }
        catch
        {
            // Best-effort.
        }
        _captureOwner = null;
    }

    private void RequestScreenshot()
    {
        _screenshotRequested = true;
    }

    private void TakeScreenshot(GameTime gameTime)
    {
        try
        {
            Directory.CreateDirectory(Paths.ScreenshotsDir);

            var width = GraphicsDevice.PresentationParameters.BackBufferWidth;
            var height = GraphicsDevice.PresentationParameters.BackBufferHeight;
            var data = new Color[width * height];
            GraphicsDevice.GetBackBufferData(data);

            using var tex = new Texture2D(GraphicsDevice, width, height);
            tex.SetData(data);

            var path = GetScreenshotPath();
            using var fs = File.Create(path);
            tex.SaveAsPng(fs, width, height);

            _log.Info($"Screenshot saved: {path}");
            ShowScreenshotToast($"SCREENSHOT SAVED\n{Path.GetFileName(path)}", gameTime);
        }
        catch (Exception ex)
        {
            _log.Warn($"Screenshot failed: {ex.Message}");
            ShowScreenshotToast("SCREENSHOT FAILED", gameTime);
        }
    }

    private string GetScreenshotPath()
    {
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        var basePath = Path.Combine(Paths.ScreenshotsDir, $"screenshot-{stamp}.png");
        var path = basePath;
        var index = 1;
        while (File.Exists(path))
        {
            path = Path.Combine(Paths.ScreenshotsDir, $"screenshot-{stamp}-{index}.png");
            index++;
        }
        return path;
    }

    private void ShowScreenshotToast(string message, GameTime gameTime)
    {
        _screenshotToast = message;
        _screenshotToastUntil = gameTime.TotalGameTime.TotalSeconds + 2.0;
    }

    private void DrawScreenshotToast(GameTime gameTime)
    {
        if (_spriteBatch is null || _pixel is null || _font is null)
            return;

        if (string.IsNullOrWhiteSpace(_screenshotToast))
            return;

        var now = gameTime.TotalGameTime.TotalSeconds;
        if (now > _screenshotToastUntil)
        {
            _screenshotToast = null;
            return;
        }

        var padding = 8;
        var size = _font.MeasureString(_screenshotToast);
        var rect = new Rectangle(UiLayout.Viewport.X + 20, UiLayout.Viewport.Y + 20, (int)size.X + padding * 2, (int)size.Y + padding * 2);

        _spriteBatch.Begin(samplerState: SamplerState.PointClamp, transformMatrix: UiLayout.Transform);
        _spriteBatch.Draw(_pixel, rect, new Color(0, 0, 0, 180));
        _spriteBatch.Draw(_pixel, new Rectangle(rect.X, rect.Y, rect.Width, 2), Color.White);
        _spriteBatch.Draw(_pixel, new Rectangle(rect.X, rect.Bottom - 2, rect.Width, 2), Color.White);
        _spriteBatch.Draw(_pixel, new Rectangle(rect.X, rect.Y, 2, rect.Height), Color.White);
        _spriteBatch.Draw(_pixel, new Rectangle(rect.Right - 2, rect.Y, 2, rect.Height), Color.White);
        _font.DrawString(sb: _spriteBatch, text: _screenshotToast, pos: new Vector2(rect.X + padding, rect.Y + padding), color: Color.White);
        _spriteBatch.End();
    }
}
