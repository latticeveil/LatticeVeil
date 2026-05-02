using System;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using LatticeVeilMonoGame.Core;
using LatticeVeilMonoGame.Online.Eos;
using LatticeVeilMonoGame.Online.Gate;
using LatticeVeilMonoGame.UI;
using LatticeVeilMonoGame.UI.Screens;

namespace LatticeVeilMonoGame;

public sealed class Game1 : Game
{
    // Optional global time hook for screens that want a stable animation clock without threading GameTime everywhere.
    // (Some experimental UI code references this; keep it lightweight.)
    public static TimeSpan TotalGameTime { get; private set; }
    public static bool WindowIsActive { get; private set; } = true;

    private readonly GameStartOptions? _startOptions;

    private readonly GraphicsDeviceManager _graphics;
    private SpriteBatch? _spriteBatch;
    private Texture2D? _pixel;
    private AssetLoader? _assets;
    private PixelFont? _font;
    private System.Drawing.Icon? _windowIcon;

    private readonly MenuStack _menus = new();
    private readonly InputState _input = new();
    private readonly Logger _log;
    private readonly PlayerProfile _profile;
    private readonly OnlineSocialStateService _socialState;
    private readonly VeilnetGamePresenceReporter _gamePresenceReporter;
    private readonly SocialNotificationOverlay _socialOverlay = new();
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
    private Task<StartupSocialCacheWarmResult>? _startupSocialWarmTask;
    private bool _exitRequested;
    private double _smokeExitAtSeconds = -1;
    private const double SmokeDurationSeconds = 8.0;
    private double _lastUpdateSeconds = -1;
    private double _lastStallLogSeconds = -1;
    private const double StallThresholdSeconds = 2.0;
    private const double StallLogCooldownSeconds = 10.0;
    private const int WmSetIcon = 0x0080;
    private const int IconSmall = 0;
    private const int IconBig = 1;
    private Vector2 _smoothedLookDelta = Vector2.Zero;
    private const float CaptureDeltaSmoothing = 0.70f;
    private const int CaptureDeltaClampPixels = 200;
    private const int CaptureDeltaSpikePixels = 900;
    private const int CaptureDeltaDeadzonePixels = 2;
    private const float CaptureDeltaClampRadians = 0.45f;
    private static readonly Point[] VeilCursorShadowPixels =
    {
        new(0, 0), new(0, 1), new(0, 2), new(0, 3), new(0, 4), new(0, 5),
        new(1, 1), new(1, 2), new(1, 3), new(1, 4), new(1, 5), new(1, 6),
        new(2, 2), new(2, 3), new(2, 4), new(2, 5), new(2, 6), new(2, 7),
        new(3, 4), new(3, 5), new(3, 6), new(3, 7), new(3, 8),
        new(4, 6), new(4, 7), new(4, 8), new(4, 9),
        new(5, 8), new(5, 9), new(5, 10),
        new(6, 10), new(6, 11),
        new(7, 4), new(8, 4), new(9, 4),
        new(7, 5), new(8, 5), new(9, 5),
        new(8, 6), new(8, 7),
        new(9, 6)
    };

    private static readonly Point[] VeilCursorBodyPixels =
    {
        new(0, 0), new(0, 1), new(0, 2), new(0, 3), new(0, 4),
        new(1, 1), new(1, 2), new(1, 3), new(1, 4), new(1, 5),
        new(2, 2), new(2, 3), new(2, 4), new(2, 5), new(2, 6),
        new(3, 4), new(3, 5), new(3, 6), new(3, 7),
        new(4, 6), new(4, 7), new(4, 8),
        new(5, 8), new(5, 9),
        new(6, 10),
        new(7, 4), new(8, 4),
        new(7, 5), new(8, 5),
        new(8, 6)
    };

    private static readonly Point[] VeilCursorAccentPixels =
    {
        new(0, 0), new(0, 1),
        new(1, 1), new(1, 2),
        new(2, 2), new(2, 3),
        new(3, 4), new(4, 6),
        new(7, 4), new(8, 4)
    };

	public PlayerProfile Profile => _profile;

    public Game1(Logger log, PlayerProfile profile, GameStartOptions? startOptions = null)
    {
        _log = log;
        _profile = profile;
        _socialState = OnlineSocialStateService.GetOrCreate(_log);
        _gamePresenceReporter = new VeilnetGamePresenceReporter(_log, "game");
        _startOptions = startOptions;

        // Log build SHA if provided
        if (!string.IsNullOrEmpty(_startOptions?.BuildSha))
        {
            _log.Info($"Game1 constructor received BuildSha: {_startOptions.BuildSha}");
        }

        // Set renderer backend via environment variable before creating GraphicsDeviceManager
        var rendererBackend = _startOptions?.RendererBackend ?? "OpenGL";
        ConfigureRendererBackend(rendererBackend);

        // Create GraphicsDeviceManager with configured backend
        _graphics = new GraphicsDeviceManager(this);
        _settings = GameSettings.LoadOrCreate(_log);
        IsFixedTimeStep = false; // Variable timestep helps avoid visible camera stepping when frames fluctuate.
        IsMouseVisible = false;
        Window.AllowUserResizing = false;
        Window.Title = Paths.IsDevBuild ? "[DEV] LatticeVeil" : "LatticeVeil";
        
        // Prevent throttling when inactive - keep loading running
        InactiveSleepTime = TimeSpan.Zero;

        _settings.StageStartupGraphics(_graphics, Window);

        // Force window to be visible and focused
        Window.IsBorderless = false;
        _log.Info($"Window properties: Title='{Window.Title}', IsBorderless={Window.IsBorderless}, IsMouseVisible={IsMouseVisible}, Position={Window.Position}");

        // Initialize Vulkan backend if requested
        // OpenGL renderer - stable and optimized
        _log.Info("📦 OpenGL renderer configured - using stable DesktopGL backend");

        Exiting += (s, e) =>
        {
            _log.Info("Game exiting; popping all screens.");
            try
            {
                _settings.CaptureActiveGraphics(_graphics);
                _settings.Save(_log);
            }
            catch (Exception ex)
            {
                _log.Warn($"Failed to persist runtime graphics state on exit: {ex.Message}");
            }
            _gamePresenceReporter.ClearBeforeExit();
            RuntimeSessionCache.ClearSocialSession(_log);
            while (_menus.Count > 0)
                _menus.Pop();
        };

        AppDomain.CurrentDomain.ProcessExit += (_, _) => _gamePresenceReporter.ClearBeforeExit();
        AppDomain.CurrentDomain.UnhandledException += (_, _) => _gamePresenceReporter.ClearBeforeExit();

        // No Content pipeline usage.
        Content.RootDirectory = "Content";
    }

    private void ConfigureRendererBackend(string rendererBackend)
    {
        try
        {
            _log.Info($"Configuring renderer backend: {rendererBackend}");
            
            if (string.Equals(rendererBackend, "DirectX", StringComparison.OrdinalIgnoreCase))
            {
                // Configure DirectX backend
                Environment.SetEnvironmentVariable("MONOGAME_GRAPHICS_BACKEND", "DirectX");
                Environment.SetEnvironmentVariable("SDL_VIDEODRIVER", "direct3d11");
                _log.Info("Configured DirectX backend via environment variables");
            }
            else
            {
                // Configure OpenGL backend (default)
                Environment.SetEnvironmentVariable("MONOGAME_GRAPHICS_BACKEND", "OpenGL");
                Environment.SetEnvironmentVariable("SDL_VIDEODRIVER", "opengl");
                _log.Info("Configured OpenGL backend via environment variables");
            }
        }
        catch (Exception ex)
        {
            _log.Warn($"Failed to configure renderer backend {rendererBackend}: {ex.Message}. Using default.");
        }
    }

    protected override void Initialize()
    {
        _log.Info("Game1.Initialize() started");
        ApplyStartupSettings(startupPhase: true);
        base.Initialize();
        _log.Info("base.Initialize() completed");
        ApplyWindowIcon();

        Window.ClientSizeChanged += (_, _) =>
        {
            UpdateUiLayout();
        };

        ApplyStartupSettings(startupPhase: false);
        _log.Info("Game1.Initialize() completed");
    }

    protected override void LoadContent()
    {
        _log.Info("Game1.LoadContent() started");
        _spriteBatch = new SpriteBatch(GraphicsDevice);
        _log.Info("SpriteBatch created");

        _pixel = new Texture2D(GraphicsDevice, 1, 1);
        _pixel.SetData(new[] { Color.White });
        _log.Info("Pixel texture created");

        _font = new PixelFont(_pixel, scale: 2);
        _font.SetStyle(_settings.UiFontStyle);
        _assets = new AssetLoader(GraphicsDevice, _log);
        _log.Info("Font and AssetLoader created");
        QueueAccountSkinBootstrap();

        // EOS initialization is deferred to Update() to avoid blocking load-time hangs.
        // Update() will retry creation when not in offline mode.
        _log.Info("EOS initialization deferred to update loop.");
        _eosClient = null;
        var eosSnapshot = EosRuntimeStatus.Evaluate(_eosClient);
        _log.Info($"EOS SDK compiled: {eosSnapshot.IsSdkCompiled}");
        _log.Info($"EOS config available: {eosSnapshot.HasConfig} ({EosRuntimeStatus.DescribeConfigSource()})");
        _log.Info("EOS login bootstrap mode: deviceid.");

        if (_startOptions?.AssetView == true)
        {
            _menus.Push(
                new AssetViewerScreen(_menus, _assets, _font, _pixel, _log),
                UiLayout.Viewport);
        }
        else
        {
            _menus.Push(
                new MainMenuScreen(_menus, _assets, _font, _pixel, _log, _profile, _graphics, Window, _eosClient, _startOptions?.Offline ?? false),
                UiLayout.Viewport);
        }

        _startupSocialWarmTask = StartupSocialCacheWarmer.WarmAsync(_log, _profile);

        _log.Info("Game initialized.");
    }

    private void QueueAccountSkinBootstrap()
    {
        if (_startOptions?.Offline == true)
            return;

        var token = (Environment.GetEnvironmentVariable("LV_VEILNET_ACCESS_TOKEN") ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(token))
            return;

        _ = Task.Run(async () =>
        {
            try
            {
                using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(12));
                var result = await new PlayerSkinCloudClient(_log).FetchAndApplyAccountSkinAsync(token, cts.Token).ConfigureAwait(false);
                if (!result.Ok)
                    _log.Warn($"Account skin bootstrap failed: {result.Message}");
            }
            catch (Exception ex)
            {
                _log.Warn($"Account skin bootstrap error: {ex.Message}");
            }
        });
    }

    protected override void UnloadContent()
    {
        _gamePresenceReporter.ClearBeforeExit();
        _windowIcon?.Dispose();
        _windowIcon = null;
        _assets?.Dispose();
        _pixel?.Dispose();
        _spriteBatch?.Dispose();
        _eosClient?.Dispose();
        base.UnloadContent();
    }

    protected override void Update(GameTime gameTime)
    {
        TotalGameTime = gameTime.TotalGameTime;
        try
        {
            var exitRequestPath = Path.Combine(Paths.RootDir, "exit.request");
            if (File.Exists(exitRequestPath))
            {
                File.Delete(exitRequestPath);
                Exit();
                return;
            }
        }
        catch { }

        // Keep all game systems ticking even while window focus is lost.
        // Input is suppressed while inactive, but loading/network/refresh continue.
        if (!IsActive)
        {
            WindowIsActive = false;
            if (_wasActive)
            {
                _log.Info("Window lost focus - releasing mouse capture and resetting input");
            }
            ReleaseMouseCapture();
            _input.Reset();
            _wasActive = false;
            _lastUpdateSeconds = gameTime.TotalGameTime.TotalSeconds;
            UpdateGamePresence();
            _menus.Update(gameTime, _input);
            UpdateSocialStateAndOverlay(gameTime);
            base.Update(gameTime);
            return;
        }

        WindowIsActive = true;
        if (!_wasActive)
        {
            _log.Info("Window gained focus - reinitializing input");
            _input.Reset();
            _wasActive = true;
            UpdateMouseCapture(computeDelta: false);
            _lastUpdateSeconds = gameTime.TotalGameTime.TotalSeconds;
            UpdateGamePresence();
            base.Update(gameTime);
            return;
        }

        LogFrameStall(gameTime);
        _input.Update();

        RefreshSettingsIfChanged();
        UpdateUiLayout();
        UpdateMouseCapture(computeDelta: IsActive);
        ProcessStartupSocialWarmTask();
        if (_eosClient == null && _startOptions?.Offline != true)
        {
            var created = EosClientProvider.GetOrCreate(_log, "deviceid", allowRetry: true);
            if (created != null)
            {
                _eosClient = created;
                _log.Info("EOS client initialized.");
            }
        }

        var eos = _eosClient ?? EosClientProvider.Current;
        if (_eosClient == null && eos != null)
            _eosClient = eos;
        eos?.Tick();

        if (_input.IsNewKeyPress(Keys.F2))
            RequestScreenshot();
        if (_input.IsNewKeyPress(Keys.F12))
            ToggleFullscreen(gameTime);

        if (UpdateSocialStateAndOverlay(gameTime))
        {
            UpdateMouseCapture(computeDelta: false);
            base.Update(gameTime);
            return;
        }

        // Global quit (Alt+F4 is handled by OS; Esc handled in screens)
        _menus.Update(gameTime, _input);
        UpdateGamePresence();

        HandleSmoke(gameTime);
        // Do not auto-exit when the menu stack is momentarily empty during startup transitions.
        // Exit should be driven by an explicit request (Quit button / OS close).
        UpdateMouseCapture(computeDelta: false);
        base.Update(gameTime);
    }

    private void UpdateGamePresence()
    {
        var worldScreen = _menus.FindTopMost<GameWorldScreen>();
        if (worldScreen != null && worldScreen.TryGetPresenceState(out var worldName, out var gameMode, out var isMultiplayer))
        {
            _gamePresenceReporter.ReportInGame(worldName, gameMode, isMultiplayer);
            return;
        }

        _gamePresenceReporter.ReportOnline();
    }

    private bool UpdateSocialStateAndOverlay(GameTime gameTime)
    {
        _socialState.Tick();
        while (_socialState.TryDequeueNewRequestNotification(out var request))
            _socialOverlay.EnqueueRequest(request);
        while (_socialState.TryDequeueWorldInviteNotification(out var invite))
        {
            if (_menus.Peek() is GameWorldScreen worldScreen)
                worldScreen.NotifyWorldInvite(invite);
            else
                _socialOverlay.EnqueueWorldInvite(invite);
        }

        return _socialOverlay.Update(
            gameTime,
            _input,
            _settings.GetSocialNotificationMode(),
            OpenProfileRequestsFromOverlay,
            OpenMultiplayerFromInviteOverlay);
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
        _socialOverlay.Draw(_spriteBatch, _font, _pixel, UiLayout.Viewport, _settings.GetSocialNotificationMode());
        DrawCustomCursor(gameTime);

        base.Draw(gameTime);
    }

    private Rectangle ViewportRect =>
        new(0, 0, GraphicsDevice.Viewport.Width, GraphicsDevice.Viewport.Height);

    private void ApplyStartupSettings(bool startupPhase)
    {
        _settingsStamp = GetSettingsStamp();
        
        // Override renderer backend from command line if provided
        if (_startOptions?.RendererBackend != null)
        {
            var requestedBackend = _startOptions.RendererBackend;
            _settings.RendererBackend = requestedBackend;
            _log.Info($"Using renderer backend from command line: {requestedBackend}");
        }
        
        _settings.ApplyGraphics(_graphics);

        if (startupPhase)
            return;

        _settings.ApplyAudio();
        UpdateUiLayout(forceResize: true);

        try
        {
            var adapter = GraphicsDevice.Adapter;
            var mode = adapter.CurrentDisplayMode;
            _log.Info($"GPU: {adapter.Description}");
            _log.Info($"DISPLAY: {mode.Width}x{mode.Height} ({mode.Format})");
            _log.Info($"PROFILE: {GraphicsDevice.GraphicsProfile}");
            
            // Show OpenGL renderer status
            _log.Info($"🎮 RENDERER: OpenGL (DesktopGL) - Stable and optimized");
            _log.Info("🚀 OpenGL rendering is ACTIVE - maximum compatibility and performance!");
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
        _font?.SetStyle(_settings.UiFontStyle);
    }

    private void ProcessStartupSocialWarmTask()
    {
        if (_startupSocialWarmTask == null || !_startupSocialWarmTask.IsCompleted)
            return;

        try
        {
            var warmed = _startupSocialWarmTask.GetAwaiter().GetResult();
            if (warmed.HasFriendUpdates)
            {
                _profile.Friends = warmed.Friends;
                _profile.Save(_log);
            }
        }
        catch (Exception ex)
        {
            _log.Warn($"Startup social warm apply failed: {ex.Message}");
        }
        finally
        {
            _startupSocialWarmTask = null;
        }
    }

    private void ApplyWindowIcon()
    {
        try
        {
            var iconPath = Path.Combine(AppContext.BaseDirectory, "Icon.ico");
            if (!File.Exists(iconPath))
                iconPath = Path.Combine(AppContext.BaseDirectory, "LatticeVeilMonoGame", "Icon.ico");
            if (!File.Exists(iconPath))
                iconPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Icon.ico");
            if (!File.Exists(iconPath))
                return;

            _windowIcon?.Dispose();
            _windowIcon = new System.Drawing.Icon(iconPath);
            var handle = Window.Handle;
            if (handle == IntPtr.Zero)
                return;

            SendMessage(handle, WmSetIcon, (IntPtr)IconBig, _windowIcon.Handle);
            SendMessage(handle, WmSetIcon, (IntPtr)IconSmall, _windowIcon.Handle);
        }
        catch (Exception ex)
        {
            _log.Warn($"Window icon apply failed: {ex.Message}");
        }
    }

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

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
            
            // Immediately set previous state to center to avoid accumulating deltas
            _input.SetLookDelta(Vector2.Zero);
            
            if (_ignoreNextCaptureDelta)
            {
                _smoothedLookDelta = Vector2.Zero;
                _ignoreNextCaptureDelta = false;
            }
            else
            {
                var spikeThreshold = Math.Max(CaptureDeltaSpikePixels, Math.Max(viewport.Width, viewport.Height) / 2);
                if (Math.Abs(deltaPx.X) > spikeThreshold || Math.Abs(deltaPx.Y) > spikeThreshold)
                {
                    _smoothedLookDelta = Vector2.Zero;
                    _input.SetLookDelta(Vector2.Zero);
                    Mouse.SetPosition(_captureCenter.X, _captureCenter.Y);
                    return;
                }

                var sensitivity = Math.Clamp(_settings.MouseSensitivity, 0.0005f, 0.01f);
                if (!float.IsFinite(sensitivity))
                    sensitivity = 0.0035f;
                var clampedPxX = Math.Clamp(deltaPx.X, -CaptureDeltaClampPixels, CaptureDeltaClampPixels);
                var clampedPxY = Math.Clamp(deltaPx.Y, -CaptureDeltaClampPixels, CaptureDeltaClampPixels);
                
                if (Math.Abs(clampedPxX) <= CaptureDeltaDeadzonePixels) clampedPxX = 0;
                if (Math.Abs(clampedPxY) <= CaptureDeltaDeadzonePixels) clampedPxY = 0;

                if (clampedPxX == 0 && clampedPxY == 0)
                {
                    _smoothedLookDelta = Vector2.Zero;
                    _input.SetLookDelta(Vector2.Zero);
                    Mouse.SetPosition(_captureCenter.X, _captureCenter.Y);
                    return;
                }

                var delta = new Vector2(clampedPxX * sensitivity, clampedPxY * sensitivity);
                delta.X = Math.Clamp(delta.X, -CaptureDeltaClampRadians, CaptureDeltaClampRadians);
                delta.Y = Math.Clamp(delta.Y, -CaptureDeltaClampRadians, CaptureDeltaClampRadians);

                _smoothedLookDelta = Vector2.Lerp(_smoothedLookDelta, delta, CaptureDeltaSmoothing);
                _input.SetLookDelta(_smoothedLookDelta);
            }

            Mouse.SetPosition(_captureCenter.X, _captureCenter.Y);
        }
        else
        {
            _smoothedLookDelta = Vector2.Zero;
            _input.SetLookDelta(Vector2.Zero);
        }
    }

    private void EngageMouseCapture(IMouseCaptureScreen screen)
    {
        _mouseCaptured = true;
        _captureOwner = screen;
        _ignoreNextCaptureDelta = true;
        _smoothedLookDelta = Vector2.Zero;
        IsMouseVisible = false;

        var viewport = GraphicsDevice.Viewport;
        _captureCenter = new Point(viewport.Width / 2, viewport.Height / 2);
        Mouse.SetPosition(_captureCenter.X, _captureCenter.Y);
        
        _log.Info($"Mouse capture ENGAGED for screen: {screen.GetType().Name}, center: {_captureCenter}");
        screen.OnMouseCaptureGained();
    }

    private void ReleaseMouseCapture()
    {
        if (!_mouseCaptured)
            return;

        _mouseCaptured = false;
        IsMouseVisible = false;
        _ignoreNextCaptureDelta = false;
        _smoothedLookDelta = Vector2.Zero;
        _input.SetLookDelta(Vector2.Zero);

        var screenName = _captureOwner?.GetType().Name ?? "Unknown";
        _log.Info($"Mouse capture RELEASED for screen: {screenName}");

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

    private void DrawCustomCursor(GameTime gameTime)
    {
        if (_spriteBatch is null || _pixel is null || _mouseCaptured || !IsActive)
            return;

        var mouse = Mouse.GetState();
        var pulse = 0.94f + (float)((Math.Sin(gameTime.TotalGameTime.TotalSeconds * 3.15) + 1d) * 0.035d);
        var shadowColor = new Color(4, 8, 18, 210);
        var outlineColor = new Color(8, 16, 28, 255);
        var bodyColor = new Color(
            Math.Clamp((int)MathF.Round(214f * pulse), 0, 255),
            Math.Clamp((int)MathF.Round(231f * pulse), 0, 255),
            Math.Clamp((int)MathF.Round(242f * pulse), 0, 255),
            245);
        var accentColor = new Color(
            Math.Clamp((int)MathF.Round(96f * pulse), 0, 255),
            Math.Clamp((int)MathF.Round(218f * pulse), 0, 255),
            255,
            255);

        const int scale = 3;
        var origin = new Point(mouse.X, mouse.Y);

        _spriteBatch.Begin(samplerState: SamplerState.PointClamp, blendState: BlendState.AlphaBlend);
        DrawCursorPixels(origin + new Point(scale, scale), scale, VeilCursorShadowPixels, shadowColor);
        DrawCursorOutline(origin, scale, outlineColor);
        DrawCursorPixels(origin, scale, VeilCursorBodyPixels, bodyColor);
        DrawCursorPixels(origin, scale, VeilCursorAccentPixels, accentColor);

        var runeGlow = new Rectangle(origin.X + 20, origin.Y + 20, 9, 9);
        _spriteBatch.Draw(_pixel, runeGlow, new Color(70, 196, 225, 110));
        _spriteBatch.Draw(_pixel, new Rectangle(runeGlow.X + 3, runeGlow.Y, 3, 9), accentColor);
        _spriteBatch.Draw(_pixel, new Rectangle(runeGlow.X, runeGlow.Y + 3, 9, 3), accentColor);
        _spriteBatch.End();
    }

    private void DrawCursorPixels(Point origin, int scale, Point[] pixels, Color color)
    {
        if (_spriteBatch is null || _pixel is null)
            return;

        for (var i = 0; i < pixels.Length; i++)
        {
            var cell = pixels[i];
            _spriteBatch.Draw(
                _pixel,
                new Rectangle(origin.X + cell.X * scale, origin.Y + cell.Y * scale, scale, scale),
                color);
        }
    }

    private void DrawCursorOutline(Point origin, int scale, Color color)
    {
        DrawCursorPixels(origin + new Point(-scale, 0), scale, VeilCursorBodyPixels, color);
        DrawCursorPixels(origin + new Point(scale, 0), scale, VeilCursorBodyPixels, color);
        DrawCursorPixels(origin + new Point(0, -scale), scale, VeilCursorBodyPixels, color);
        DrawCursorPixels(origin + new Point(0, scale), scale, VeilCursorBodyPixels, color);
        DrawCursorPixels(origin + new Point(-scale, -scale), scale, VeilCursorBodyPixels, color);
        DrawCursorPixels(origin + new Point(scale, -scale), scale, VeilCursorBodyPixels, color);
        DrawCursorPixels(origin + new Point(-scale, scale), scale, VeilCursorBodyPixels, color);
        DrawCursorPixels(origin + new Point(scale, scale), scale, VeilCursorBodyPixels, color);
    }

    private void RequestScreenshot()
    {
        _screenshotRequested = true;
    }

    private void ToggleFullscreen(GameTime gameTime)
    {
        try
        {
            _settings.Fullscreen = !_settings.Fullscreen;
            _settings.ApplyGraphics(_graphics);
            _settings.CaptureActiveGraphics(_graphics);
            _settings.Save(_log);
            _settingsStamp = GetSettingsStamp();
            UpdateUiLayout(forceResize: true);
            ApplyWindowIcon();
            if (_menus.Peek() is OptionsScreen optionsScreen)
                optionsScreen.SyncExternalFullscreenState(_settings.Fullscreen);
            ShowScreenshotToast(_settings.Fullscreen ? "FULLSCREEN ON" : "FULLSCREEN OFF", gameTime);
            _log.Info($"Fullscreen toggled: {(_settings.Fullscreen ? "ON" : "OFF")}");
        }
        catch (Exception ex)
        {
            _log.Warn($"Fullscreen toggle failed: {ex.Message}");
            ShowScreenshotToast("FULLSCREEN FAILED", gameTime);
        }
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

    private void HandleSmoke(GameTime gameTime)
    {
        if (_startOptions?.Smoke != true)
            return;

        if (_smokeExitAtSeconds < 0)
        {
            _smokeExitAtSeconds = gameTime.TotalGameTime.TotalSeconds + SmokeDurationSeconds;
            _log.Info($"SMOKE mode active. Exiting in {SmokeDurationSeconds:0.0}s.");
        }

        if (gameTime.TotalGameTime.TotalSeconds < _smokeExitAtSeconds || _exitRequested)
            return;

        _exitRequested = true;
        if (_startOptions.SmokeAssetsOk)
        {
            _log.Info("SMOKE PASS");
        }
        else
        {
            var missing = _startOptions.SmokeMissingAssets ?? Array.Empty<string>();
            _log.Warn($"SMOKE FAIL: missing assets: {string.Join(", ", missing)}");
        }

        Exit();
    }

    private void LogFrameStall(GameTime gameTime)
    {
        var now = gameTime.TotalGameTime.TotalSeconds;
        if (_lastUpdateSeconds >= 0)
        {
            var delta = now - _lastUpdateSeconds;
            if (delta >= StallThresholdSeconds &&
                (_lastStallLogSeconds < 0 || now - _lastStallLogSeconds >= StallLogCooldownSeconds))
            {
                _lastStallLogSeconds = now;
                _log.Warn($"Frame stall detected: {delta:0.00}s (possible hang).");
            }
        }

        _lastUpdateSeconds = now;
    }

    private void OpenProfileRequestsFromOverlay()
    {
        if (_assets is null || _font is null || _pixel is null)
            return;

        _menus.Push(
            new ProfileScreen(
                _menus,
                _assets,
                _font,
                _pixel,
                _log,
                _profile,
                _graphics,
                _eosClient,
                startTab: ProfileScreen.ProfileScreenStartTab.Friends,
                startFriendsMode: ProfileScreen.ProfileScreenFriendsMode.Requests),
            UiLayout.Viewport);
    }

    private void OpenMultiplayerFromInviteOverlay(SocialWorldInviteNotification invite)
    {
        if (_assets is null || _font is null || _pixel is null)
            return;

        _menus.Push(
            new MultiplayerScreen(
                _menus,
                _assets,
                _font,
                _pixel,
                _log,
                _profile,
                _graphics,
                _eosClient,
                preferredInviteSenderId: invite.SenderProductUserId,
                preferredInviteSenderDisplayName: invite.SenderDisplayName,
                preferredInviteJoinTarget: invite.SenderJoinTarget,
                preferredInviteSenderPictureUrl: invite.SenderPictureUrl,
                preferredInviteWorldName: invite.WorldName,
                preferredInviteGameMode: invite.GameMode,
                autoJoinPreferredInvite: true),
            UiLayout.Viewport);
    }
}
