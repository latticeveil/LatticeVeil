using System;
using System.Diagnostics;
using System.IO;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using LatticeVeilMonoGame.Core;
using LatticeVeilMonoGame.Online.Eos;
using LatticeVeilMonoGame.UI;
using LatticeVeilMonoGame.UI.Screens;

namespace LatticeVeilMonoGame.UI.Screens;

public sealed class MainMenuScreen : IScreen
{
    private readonly MenuStack _menus;
    private readonly AssetLoader _assets;
    private readonly PixelFont _font;
    private readonly Texture2D _pixel;
    private readonly Logger _log;
    private readonly PlayerProfile _profile;
    private readonly EosClient? _eosClient;
    private readonly bool _isOffline;
    private readonly GameSettings _settings;
    private readonly global::Microsoft.Xna.Framework.GraphicsDeviceManager _graphics;

    private Texture2D? _bg;
    private Button _singleBtn;
    private Button _multiBtn;
    private Button _optionsBtn;
    private Button _quitBtn;
    private Button _profileBtn;
    private Button _screenshotBtn;
    
    // New UI manager for precise positioning
    private UIManager? _uiManager;

    private Rectangle _viewport;
    private bool _assetsMissing;
    private double _lastAssetCheck = double.NegativeInfinity;
    private double _offlineMessageUntil = 0;
    private const double AssetCheckIntervalSeconds = 1.0;
    private static readonly string[] CriticalAssets =
    {
        "textures/menu/backgrounds/MainMenu.png",
        "textures/menu/buttons/Singleplayer.png",
        "textures/menu/buttons/Multiplayer.png",
        "textures/menu/buttons/options.png",
        "textures/menu/buttons/Quit.png",
        "textures/menu/buttons/Profile.png"
    };

    public MainMenuScreen(MenuStack menus, AssetLoader assets, PixelFont font, Texture2D pixel, Logger log, PlayerProfile profile, global::Microsoft.Xna.Framework.GraphicsDeviceManager graphics, EosClient? eosClient, bool isOffline = false)
    {
        _menus = menus;
        _assets = assets;
        _font = font;
        _pixel = pixel;
        _log = log;
        _profile = profile;
        _eosClient = eosClient;
        _isOffline = isOffline;
        _settings = GameSettings.LoadOrCreate(_log);
        _graphics = graphics;

        // Initialize UI manager for precise positioning
        _uiManager = new UIManager(_viewport);
        _uiManager.CreateMainMenuLayout();
        _log.Info("UI Manager initialized with tight button spacing");

        // Create buttons using UI Manager for proper positioning
        _singleBtn = new Button("SINGLEPLAYER", () => _menus.Push(new SingleplayerScreen(_menus, _assets, _font, _pixel, _log, _profile, _graphics), _viewport));
        _singleBtn.Bounds = _uiManager.GetButtonBounds("singleplayer");

        if (_isOffline)
        {
            _multiBtn = new Button("MULTIPLAYER", () => _offlineMessageUntil = 3.0);
            _multiBtn.ForceDisabledStyle = true;
        }
        else
        {
            _multiBtn = new Button("MULTIPLAYER", () => _menus.Push(new MultiplayerScreen(_menus, _assets, _font, _pixel, _log, _profile, _graphics, _eosClient), _viewport));
        }
        _multiBtn.Bounds = _uiManager.GetButtonBounds("multiplayer");

        _optionsBtn = new Button("OPTIONS", () => _menus.Push(new OptionsScreen(_menus, _assets, _font, _pixel, _log, _graphics), _viewport));
        _optionsBtn.Bounds = _uiManager.GetButtonBounds("options");

        _quitBtn = new Button("QUIT", () => _menus.Pop());
        _quitBtn.Bounds = _uiManager.GetButtonBounds("quit");

        _profileBtn = new Button("PROFILE", () => _menus.Push(new ProfileScreen(_menus, _assets, _font, _pixel, _log, _profile, _graphics, _eosClient), _viewport));
        _profileBtn.Bounds = _uiManager.GetButtonBounds("profile");

        _screenshotBtn = new Button("SCREENSHOTS", () => _menus.Push(new ScreenshotsScreen(_menus, _assets, _font, _pixel, _log), _viewport));
        _screenshotBtn.Bounds = _uiManager.GetButtonBounds("screenshots");

        try
        {
            _bg = _assets.LoadTexture("textures/menu/backgrounds/MainMenu.png");
            _singleBtn.Texture = _assets.LoadTexture("textures/menu/buttons/Singleplayer.png");
            _multiBtn.Texture = _assets.LoadTexture("textures/menu/buttons/Multiplayer.png");
            _optionsBtn.Texture = _assets.LoadTexture("textures/menu/buttons/options.png");
            _quitBtn.Texture = _assets.LoadTexture("textures/menu/buttons/Quit.png");
            _profileBtn.Texture = _assets.LoadTexture("textures/menu/buttons/Profile.png");
            _screenshotBtn.Texture = _assets.LoadTexture("textures/menu/buttons/screenshots.png");
        }
        catch (Exception ex)
        {
            _log.Warn($"Main menu asset load: {ex.Message}");
        }

        UpdateAssetStatus(0);
    }

    public void OnResize(Rectangle viewport)
    {
        _viewport = viewport;
        
        // Update virtual resolution system
        VirtualResolution.Update(viewport);
        
        // Update UI manager with new viewport
        if (_uiManager != null)
        {
            _uiManager = new UIManager(_viewport);
            _uiManager.CreateMainMenuLayout();
            
            // Update button bounds with new positions (already converted to screen coords)
            _singleBtn.Bounds = _uiManager.GetButtonBounds("singleplayer");
            _multiBtn.Bounds = _uiManager.GetButtonBounds("multiplayer");
            _optionsBtn.Bounds = _uiManager.GetButtonBounds("options");
            _quitBtn.Bounds = _uiManager.GetButtonBounds("quit");
            _profileBtn.Bounds = _uiManager.GetButtonBounds("profile");
            _screenshotBtn.Bounds = _uiManager.GetButtonBounds("screenshots");
            
            _log.Info($"Virtual Resolution updated: {VirtualResolution.VirtualWidth}x{VirtualResolution.VirtualHeight} -> {viewport.Width}x{viewport.Height}");
            _log.Info($"Scale: {VirtualResolution.ScaleX:F2}x{VirtualResolution.ScaleY:F2}");
            _log.Info(_uiManager.GetLayoutInfo());
        }
    }

    public void Update(GameTime gameTime, InputState input)
    {
        UpdateAssetStatus(gameTime.TotalGameTime.TotalSeconds);

        if (_offlineMessageUntil > 0)
        {
            _offlineMessageUntil -= gameTime.ElapsedGameTime.TotalSeconds;
        }

        if (input.IsNewKeyPress(Keys.Escape))
        {
            // go back to launcher if keep-open is enabled, else exit
            _menus.Pop();
            return;
        }

        _singleBtn.Update(input);
        _multiBtn.Update(input);
        _optionsBtn.Update(input);
        _quitBtn.Update(input);
        _profileBtn.Update(input);
        _screenshotBtn.Update(input);
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

        sb.Begin(samplerState: SamplerState.PointClamp, transformMatrix: VirtualResolution.ScaleMatrix);

        _singleBtn.Draw(sb, _pixel, _font);
        _multiBtn.Draw(sb, _pixel, _font);
        _optionsBtn.Draw(sb, _pixel, _font);
        _quitBtn.Draw(sb, _pixel, _font);
        _profileBtn.Draw(sb, _pixel, _font);
        _screenshotBtn.Draw(sb, _pixel, _font);

        if (_assetsMissing)
            DrawAssetsMissingBanner(sb);
        
        if (_offlineMessageUntil > 0)
            DrawOfflineMessage(sb);

        sb.End();
    }

    private void DrawOfflineMessage(SpriteBatch sb)
    {
        var message = "Offline mode: Multiplayer disabled";
        var size = _font.MeasureString(message);
        var padding = 10;
        var rect = new Rectangle(
            _viewport.X + (_viewport.Width - (int)size.X) / 2 - padding,
            _viewport.Y + 20,
            (int)size.X + padding * 2,
            (int)size.Y + padding * 2);
        
        sb.Draw(_pixel, rect, new Color(0, 0, 0, 200));
        _font.DrawString(sb, message, new Vector2(rect.X + padding, rect.Y + padding), Color.Yellow);
    }

    private void UpdateAssetStatus(double now)
    {
        if (now - _lastAssetCheck < AssetCheckIntervalSeconds)
            return;

        _lastAssetCheck = now;
        _assetsMissing = !CriticalAssetsPresent();
    }

    private static bool CriticalAssetsPresent()
    {
        foreach (var asset in CriticalAssets)
        {
            if (!AssetResolver.TryResolve(asset, out _))
                return false;
        }

        return true;
    }

    private void DrawAssetsMissingBanner(SpriteBatch sb)
    {
        var path = Paths.ToUiPath(AssetResolver.DescribeInstallLocation());
        var message = $"Assets not found. Install assets to: {path}";
        var size = _font.MeasureString(message);

        var padding = 8;
        var rect = new Rectangle(
            _viewport.X + (int)Math.Round((_viewport.Width - size.X) / 2f) - padding,
            _viewport.Bottom - (int)size.Y - padding * 2,
            (int)Math.Round(size.X) + padding * 2,
            (int)Math.Round(size.Y) + padding);

        sb.Draw(_pixel, rect, new Color(0, 0, 0, 190));
        _font.DrawString(sb, message, new Vector2(rect.X + padding, rect.Y + 4), Color.White);
    }
}
