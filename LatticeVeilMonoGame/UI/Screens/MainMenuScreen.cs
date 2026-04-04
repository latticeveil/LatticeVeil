using System;
using System.Diagnostics;
using System.IO;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using LatticeVeilMonoGame.Core;
using LatticeVeilMonoGame.Online.Eos;
using LatticeVeilMonoGame.Online.Gate;
using LatticeVeilMonoGame.UI;
using LatticeVeilMonoGame.UI.Screens;
using WinForms = System.Windows.Forms;

namespace LatticeVeilMonoGame.UI.Screens;

public sealed class MainMenuScreen : IScreen
{
    private enum MultiplayerDialogAction
    {
        Retry,
        GoOffline,
        Close
    }

    private readonly MenuStack _menus;
    private readonly AssetLoader _assets;
    private readonly PixelFont _font;
    private readonly Texture2D _pixel;
    private readonly Logger _log;
    private readonly PlayerProfile _profile;
    private EosClient? _eosClient;
    private readonly bool _isOffline;
    private readonly GameSettings _settings;
    private readonly OnlineSocialStateService _socialState;
    private readonly GraphicsDeviceManager _graphics;
    private readonly GameWindow _window;

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
    private int _pendingIncomingCount;
    private double _lastAssetCheck = double.NegativeInfinity;
    private bool _multiplayerAvailable;
    private string _multiplayerDisabledReason = string.Empty;
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

    public MainMenuScreen(MenuStack menus, AssetLoader assets, PixelFont font, Texture2D pixel, Logger log, PlayerProfile profile, GraphicsDeviceManager graphics, GameWindow window, EosClient? eosClient, bool isOffline = false)
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
        _socialState = OnlineSocialStateService.GetOrCreate(_log);
        _graphics = graphics;
        _window = window;

        var veilnetName = (Environment.GetEnvironmentVariable("LV_VEILNET_USERNAME") ?? string.Empty).Trim();
        if (!string.IsNullOrWhiteSpace(veilnetName))
            _profile.Username = veilnetName;

        // Initialize UI manager for precise positioning
        _uiManager = new UIManager(_viewport);
        _uiManager.CreateMainMenuLayout();
        _log.Info("UI Manager initialized with tight button spacing");

        // Create buttons using UI Manager for proper positioning
        _singleBtn = new Button("SINGLEPLAYER", () => _menus.Push(new SingleplayerScreen(_menus, _assets, _font, _pixel, _log, _profile, _graphics), _viewport));
        _singleBtn.Bounds = _uiManager.GetButtonBounds("singleplayer");

        _multiBtn = new Button("MULTIPLAYER", OpenMultiplayer);
        _multiBtn.Bounds = _uiManager.GetButtonBounds("multiplayer");

        _optionsBtn = new Button("OPTIONS", () => _menus.Push(new OptionsScreen(_menus, _assets, _font, _pixel, _log, _graphics), _viewport));
        _optionsBtn.Bounds = _uiManager.GetButtonBounds("options");

        _quitBtn = new Button("QUIT", RequestQuit);
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

        RefreshMultiplayerAvailability(forceLog: true);
        UpdateAssetStatus(0);
    }

    private void RequestQuit()
    {
        try
        {
            _log.Info("Main menu: Quit requested.");
            File.WriteAllText(Path.Combine(Paths.RootDir, "exit.request"), DateTime.UtcNow.ToString("O"));
        }
        catch (Exception ex)
        {
            _log.Warn($"Quit failed: {ex.Message}");
            _menus.Pop();
        }
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
        RefreshMultiplayerAvailability(forceLog: false);

        if (input.IsNewKeyPress(Keys.Escape))
        {
            // go back to launcher if keep-open is enabled, else exit
            RequestQuit();
            return;
        }

        _singleBtn.Update(input);
        _multiBtn.Update(input);
        _optionsBtn.Update(input);
        _quitBtn.Update(input);
        _profileBtn.Update(input);
        _screenshotBtn.Update(input);

        _pendingIncomingCount = Math.Max(0, _socialState.GetSnapshot().PendingIncomingCount);
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
        DrawProfilePendingBadge(sb);

        if (_assetsMissing)
            DrawAssetsMissingBanner(sb);

        if (!_multiplayerAvailable && !string.IsNullOrWhiteSpace(_multiplayerDisabledReason))
            DrawMultiplayerDisabledMessage(sb, _multiplayerDisabledReason);

        sb.End();
    }

    private void DrawProfilePendingBadge(SpriteBatch sb)
    {
        if (_pendingIncomingCount <= 0)
            return;

        var label = _pendingIncomingCount > 99 ? "99+" : _pendingIncomingCount.ToString();
        var badgeW = label.Length > 2 ? 36 : 28;
        var badgeH = 22;
        var badgeRect = new Rectangle(
            _profileBtn.Bounds.Right - badgeW / 2,
            _profileBtn.Bounds.Y - 8,
            badgeW,
            badgeH);

        sb.Draw(_pixel, badgeRect, new Color(170, 24, 24, 245));
        sb.Draw(_pixel, new Rectangle(badgeRect.X, badgeRect.Y, badgeRect.Width, 2), Color.White);
        sb.Draw(_pixel, new Rectangle(badgeRect.X, badgeRect.Bottom - 2, badgeRect.Width, 2), Color.White);
        sb.Draw(_pixel, new Rectangle(badgeRect.X, badgeRect.Y, 2, badgeRect.Height), Color.White);
        sb.Draw(_pixel, new Rectangle(badgeRect.Right - 2, badgeRect.Y, 2, badgeRect.Height), Color.White);

        var textSize = _font.MeasureString(label);
        var textPos = new Vector2(
            badgeRect.Center.X - textSize.X / 2f,
            badgeRect.Center.Y - textSize.Y / 2f);
        _font.DrawString(sb, label, textPos, Color.White);
    }

    private void DrawMultiplayerDisabledMessage(SpriteBatch sb, string message)
    {
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

    private void OpenMultiplayer()
    {
        if (_isOffline)
        {
            _menus.Push(new MultiplayerScreen(_menus, _assets, _font, _pixel, _log, _profile, _graphics, null), _viewport);
            return;
        }

        while (true)
        {
            // Allow the button to be clickable while EOS is still connecting.
            _eosClient ??= EosClientProvider.Current;
            var providerReturnedNull = false;
            if (_eosClient == null)
            {
                _eosClient = EosClientProvider.GetOrCreate(_log, "deviceid", allowRetry: true);
                providerReturnedNull = _eosClient == null;
            }

            _eosClient?.Tick();
            var snapshot = EosRuntimeStatus.Evaluate(_eosClient);

            _log.Info(
                $"Main menu multiplayer open: reason={snapshot.Reason}; status={snapshot.StatusText}; " +
                $"configSource={EosRuntimeStatus.DescribeConfigSource()}; providerNull={providerReturnedNull}; clientNull={_eosClient == null}");

            if (snapshot.Reason is EosRuntimeReason.Ready or EosRuntimeReason.Connecting)
            {
                _menus.Push(new MultiplayerScreen(_menus, _assets, _font, _pixel, _log, _profile, _graphics, _eosClient), _viewport);
                return;
            }

            RefreshMultiplayerAvailability(forceLog: false);
            var action = ShowMultiplayerUnavailableDialog(snapshot, providerReturnedNull);
            if (action == MultiplayerDialogAction.Retry)
            {
                _eosClient = EosClientProvider.GetOrCreate(_log, "deviceid", allowRetry: true);
                _eosClient?.Tick();
                continue;
            }

            if (action == MultiplayerDialogAction.GoOffline)
            {
                Environment.SetEnvironmentVariable("EOS_DISABLED", "1");
                Environment.SetEnvironmentVariable("LV_LAUNCHER_ONLINE_AUTH", "0");
                Environment.SetEnvironmentVariable("LV_OFFICIAL_BUILD_VERIFIED", "0");
                Environment.SetEnvironmentVariable("LV_ONLINE_SERVICES_OK", "0");
                _log.Warn("Main menu: user switched to offline multiplayer fallback.");
                _menus.Push(new MultiplayerScreen(_menus, _assets, _font, _pixel, _log, _profile, _graphics, null), _viewport);
            }

            return;
        }
    }

    private void RefreshMultiplayerAvailability(bool forceLog)
    {
        var available = ComputeMultiplayerAvailability(out var reason, out var snapshot, out var providerReturnedNull);
        if (!forceLog
            && available == _multiplayerAvailable
            && string.Equals(reason, _multiplayerDisabledReason, StringComparison.Ordinal))
        {
            return;
        }

        _multiplayerAvailable = available;
        _multiplayerDisabledReason = reason;
        _multiBtn.Enabled = true;
        _multiBtn.ForceDisabledStyle = false;

        if (forceLog || !available || snapshot.Reason != EosRuntimeReason.Ready || providerReturnedNull)
        {
            _log.Info(
                $"Main menu multiplayer state: available={available}; reason={snapshot.Reason}; status={snapshot.StatusText}; " +
                $"configSource={EosRuntimeStatus.DescribeConfigSource()}; providerNull={providerReturnedNull}; " +
                $"clientNull={_eosClient == null}; disabledReason={reason}");
        }
    }

    private bool ComputeMultiplayerAvailability(out string reason, out EosRuntimeSnapshot snapshot, out bool providerReturnedNull)
    {
        providerReturnedNull = false;
        snapshot = EosRuntimeStatus.Evaluate(_eosClient);

        if (_isOffline)
        {
            reason = "Offline mode: LAN only";
            return true;
        }

        _eosClient ??= EosClientProvider.Current;
        if (_eosClient == null)
        {
            _eosClient = EosClientProvider.GetOrCreate(_log, "deviceid", allowRetry: true);
            providerReturnedNull = _eosClient == null;
        }

        _eosClient?.Tick();
        snapshot = EosRuntimeStatus.Evaluate(_eosClient);
        if (snapshot.Reason == EosRuntimeReason.Ready)
        {
            reason = string.Empty;
            return true;
        }

        // Option A UX: enable the multiplayer button once EOS exists, even if it's still connecting.
        // MultiplayerScreen itself is only entered once EOS becomes Ready (see OpenMultiplayer).
        if (_eosClient != null && snapshot.Reason == EosRuntimeReason.Connecting)
        {
            reason = "EOS loading: Multiplayer will unlock when login completes";
            return true;
        }

        reason = snapshot.Reason switch
        {
            EosRuntimeReason.Connecting => "EOS loading: Multiplayer will unlock when login completes",
            EosRuntimeReason.ConfigMissing => "EOS config missing: Multiplayer disabled",
            EosRuntimeReason.DisabledByEnvironment => "EOS disabled by environment: Multiplayer disabled",
            EosRuntimeReason.SdkNotCompiled => "EOS SDK not compiled: Multiplayer disabled",
            EosRuntimeReason.ClientUnavailable => BuildClientUnavailableReason(providerReturnedNull),
            _ => "EOS unavailable: Multiplayer disabled"
        };
        return false;
    }

    private static bool IsTruthy(string? value)
    {
        return string.Equals(value, "1", StringComparison.Ordinal)
            || string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
    }

    private static string BuildClientUnavailableReason(bool providerReturnedNull)
    {
        if (providerReturnedNull)
        {
            var processKind = (Environment.GetEnvironmentVariable("LV_PROCESS_KIND") ?? string.Empty).Trim();
            if (string.Equals(processKind, "game", StringComparison.OrdinalIgnoreCase))
            {
                var launcherAuth = IsTruthy(Environment.GetEnvironmentVariable("LV_LAUNCHER_ONLINE_AUTH"));
                var official = IsTruthy(Environment.GetEnvironmentVariable("LV_OFFICIAL_BUILD_VERIFIED"));
                var servicesOk = IsTruthy(Environment.GetEnvironmentVariable("LV_ONLINE_SERVICES_OK"));
                var gateTicket = (Environment.GetEnvironmentVariable("LV_GATE_TICKET") ?? string.Empty).Trim();
                var accessToken = (Environment.GetEnvironmentVariable("LV_VEILNET_ACCESS_TOKEN") ?? string.Empty).Trim();
                var missing = new System.Collections.Generic.List<string>();

                if (!launcherAuth)
                    missing.Add("LV_LAUNCHER_ONLINE_AUTH");
                if (!official)
                    missing.Add("LV_OFFICIAL_BUILD_VERIFIED");
                if (!servicesOk)
                    missing.Add("LV_ONLINE_SERVICES_OK");
                if (string.IsNullOrWhiteSpace(gateTicket))
                    missing.Add("LV_GATE_TICKET");
                if (string.IsNullOrWhiteSpace(accessToken))
                    missing.Add("LV_VEILNET_ACCESS_TOKEN");

                if (missing.Count > 0)
                    return $"Online auth missing. Please launch Online from launcher.\nMissing: {string.Join(", ", missing)}";
            }

            return "EOS client unavailable. Retry initialization from launcher.";
        }

        return "EOS client unavailable: Multiplayer disabled";
    }

    private MultiplayerDialogAction ShowMultiplayerUnavailableDialog(EosRuntimeSnapshot snapshot, bool providerReturnedNull)
    {
        var message = snapshot.Reason switch
        {
            EosRuntimeReason.ConfigMissing =>
                $"EOS config is missing.\n\nConfig source: {EosRuntimeStatus.DescribeConfigSource()}\n" +
                "Ensure eos.public.json is present or EOS_* public env vars are set.",
            EosRuntimeReason.SdkNotCompiled =>
                "EOS SDK is not compiled into this build.\nRebuild with EOS_SDK enabled and EOSSDK-Win64-Shipping.dll present.",
            EosRuntimeReason.DisabledByEnvironment =>
                "EOS is disabled by environment flags (EOS_DISABLED / EOS_DISABLE).",
            EosRuntimeReason.ClientUnavailable =>
                BuildClientUnavailableReason(providerReturnedNull),
            _ =>
                $"EOS is not ready.\nStatus: {snapshot.StatusText}\nReason: {snapshot.Reason}"
        };

        using var form = new WinForms.Form();
        form.Text = "Multiplayer Unavailable";
        form.StartPosition = WinForms.FormStartPosition.CenterScreen;
        form.FormBorderStyle = WinForms.FormBorderStyle.FixedDialog;
        form.MinimizeBox = false;
        form.MaximizeBox = false;
        form.ShowInTaskbar = false;
        form.Width = 560;
        form.Height = 280;

        var label = new WinForms.Label
        {
            Left = 16,
            Top = 16,
            Width = 520,
            Height = 160,
            Text = message,
            AutoSize = false
        };

        var retryButton = new WinForms.Button
        {
            Text = "Retry",
            Left = 110,
            Top = 190,
            Width = 100,
            DialogResult = WinForms.DialogResult.Retry
        };

        var offlineButton = new WinForms.Button
        {
            Text = "Go Offline",
            Left = 225,
            Top = 190,
            Width = 120,
            DialogResult = WinForms.DialogResult.Yes
        };

        var closeButton = new WinForms.Button
        {
            Text = "Close",
            Left = 360,
            Top = 190,
            Width = 100,
            DialogResult = WinForms.DialogResult.Cancel
        };

        form.Controls.Add(label);
        form.Controls.Add(retryButton);
        form.Controls.Add(offlineButton);
        form.Controls.Add(closeButton);
        form.AcceptButton = retryButton;
        form.CancelButton = closeButton;

        var result = form.ShowDialog();
        return result switch
        {
            WinForms.DialogResult.Retry => MultiplayerDialogAction.Retry,
            WinForms.DialogResult.Yes => MultiplayerDialogAction.GoOffline,
            _ => MultiplayerDialogAction.Close
        };
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
