using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Text;
using System.Security.Cryptography;
using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Drawing;
using DColor = System.Drawing.Color;
using DFont = System.Drawing.Font;
using DImage = System.Drawing.Image;
using DPoint = System.Drawing.Point;
using DSize = System.Drawing.Size;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using LatticeVeilMonoGame.Core;
using LatticeVeilMonoGame.Online.Eos;
using LatticeVeilMonoGame.Online.Gate;
using MemoryWebTextureLoader = LatticeVeilMonoGame.UI.MemoryWebTextureLoader;

namespace LatticeVeilMonoGame.Launcher;

using DIcon = System.Drawing.Icon;

// Separate WinForms launcher window.
// The game starts ONLY when the user clicks "Launch Game".
// If "Keep launcher open?" is unchecked, the launcher will close after spawning the game,
// and the game will keep running as an independent process.
public sealed class LauncherForm : Form
{
    private enum GameState
    {
        NotRunning,
        RunningExternal,
        RunningOwned
    }

    private enum LaunchReadiness
    {
        Unknown,
        Checking,
        ReadyOnline,
        ReadyOfflineOnly,
        Failed
    }

    // Win32: make a borderless form draggable.
    // Ref: common WinForms approach to dragging a borderless window.
    [DllImport("user32.dll")]
    private static extern bool ReleaseCapture();

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, int wParam, int lParam);

    private const int WM_NCLBUTTONDOWN = 0xA1;
    private const int HTCAPTION = 0x2;

    private readonly Logger _log;
    private readonly PlayerProfile _profile;
    private readonly GameSettings _settings;
    private readonly VeilnetGamePresenceReporter _presenceReporter;
    private const int LogPollIntervalMs = 400;
    private const int LogTailMaxLines = 400;
    private const int LogLineMaxChars = 300;
    private const bool EpicLoginInGameOnly = true;
    private const string DefaultAllowlistUrl = "https://raw.githubusercontent.com/latticeveil/OnlineService/main/allowlist.json";
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private Process? _gameProcess;

    private readonly Label _usernameLabel = new();
    private readonly TextBox _usernameBox = new();
    private readonly Button _changeUsernameBtn = new(); // legacy; kept for compatibility, not shown
    private readonly Label _offlineNameLabel = new();
    private readonly TextBox _offlineNameBox = new();
    private readonly Button _saveOfflineNameBtn = new();
    private readonly Button _claimUsernameBtn = new();
    private readonly SliderToggleCheckBox _keepOpenBox = new();
    private readonly SliderToggleCheckBox _darkModeBox = new();
    private readonly Label _recommendedLabel = new();
    private readonly Button _useRecommendedBtn = new();
    private readonly CheckBox _advancedModeBox = new();
    private readonly Label _onlineHeader = new();
    private readonly Button _hubLoginBtn = new();
    private readonly Button _hubGoogleBtn = new();
    private readonly Button _hubResetBtn = new();
    private readonly Label _hubStatusLabel = new();
    private readonly Panel _hubVeilnetAccountRow = new();
    private readonly Panel _hubVeilnetInfoPanel = new();
    private readonly Label _hubVeilnetUserLabel = new();
    private readonly PictureBox _hubVeilnetAvatarBox = new();
    private readonly Panel _skinLauncherPanel = new();
    private readonly Label _skinLauncherTitle = new();
    private readonly PictureBox _skinLauncherPreviewBox = new();
    private readonly Button _skinsBtn = new();
    private readonly Panel _onlineStatusIndicator = new();
    private readonly Button _launchBtn = new();
    private readonly ComboBox _launchModeBox = new();
    private readonly Button _openLogsBtn = new();
    private readonly Button _openGameFolderBtn = new();
    private readonly Button _saveLogsBtn = new();
    private readonly Button _updateBtn = new();
    private readonly ToolTip _toolTip = new();
    private DImage? _rocketIcon;
    private DImage? _paperIcon;
    private DImage? _folderIcon;
    private DImage? _skullIcon;
    private DImage? _windowIconImage;
    private DImage? _hubVeilnetAvatarImage;
    private DImage? _skinLauncherPreviewImage;
    private Action? _skinManagerPopupRefresh;
    private readonly TextBox _logBox = new();
    private readonly Queue<string> _logTail = new();
    private readonly TableLayoutPanel _logPanel = new();
    private readonly Panel _progressHost = new();
    private readonly Label _progressLabel = new();
    private readonly Panel _progressTrack = new();
    private readonly Panel _progressFill = new();

    private long _logReadPosition;
    private long _skinCloudUploadSequence;
    private string _logFilePath = "";
    private int _launchProgress;
    private bool _launching;
    private DateTime _logSessionDate = DateTime.Today;
    private bool _logSessionDateSet;
    private readonly SemaphoreSlim _skinCloudUploadLock = new(1, 1);

    private readonly AssetPackInstaller _assetInstaller;
    private readonly GameReleaseUpdater _gameReleaseUpdater;
    private CancellationTokenSource? _assetCts;
    private CancellationTokenSource? _gameUpdateCts;
    private bool _assetBusy;
    private bool _assetPendingLaunch;
    private bool _gameUpdateBusy;
    private bool _gameUpdateCheckInProgress;
    private bool _gameUpdateReminderShown;
    private bool _gameUpdateStartQueued;
    private string _gameUpdateStatusDetail = "Checking for updates...";
    private GameReleaseCheckResult? _gameUpdateCheck;

    private readonly Panel _assetPanel = new();
    private readonly Panel _assetCard = new();
    private readonly Label _assetTitle = new();
    private readonly Label _assetStatus = new();
    private readonly Label _assetDetail = new();
    private readonly ProgressBar _assetProgress = new();
    private readonly TextBox _assetErrorBox = new();
    private readonly Button _assetRetryBtn = new();
    private readonly Button _assetCopyBtn = new();
    private readonly Button _assetCancelBtn = new();

    private readonly System.Windows.Forms.Timer _pollTimer = new();

    private readonly TableLayoutPanel _root = new();
    private readonly FlowLayoutPanel _right = new();
    private readonly FlowLayoutPanel _buttons = new();
    private readonly Panel _bottomLeftHost = new();
    private readonly Panel _logsHost = new();
    private readonly Panel _launchModeHost = new();
    private readonly Panel _buttonSpacer = new();

    private readonly Panel _topBar = new();
    private readonly PictureBox _logoBox = new();
    private readonly Label _title = new();
    private readonly Label _releaseTitleLabel = new();
    private readonly Button _minBtn = new();
    private readonly Button _closeBtn = new();
    private NotifyIcon? _trayIcon;
    private ContextMenuStrip? _trayMenu;
    private ToolStripMenuItem? _trayLaunchGameItem;
    private ToolStripMenuItem? _trayVeilnetAuthItem;
    private ToolStripControlHost? _trayVeilnetAccountHost;
    private PictureBox? _trayVeilnetAvatarBox;
    private Button? _trayVeilnetAuthButton;
    private Button? _trayVeilnetProfileButton;
    private Panel? _trayVeilnetDivider;
    private DIcon? _trayIconImage;
    private DImage? _trayVeilnetAvatarImage;
    private bool _allowTaskbarMinimizeOnce;
    private bool _launcherTrayHidden;
    private bool _allowLauncherExit;
    private bool _veilnetLaunchPromptOpen;

    private readonly Label _dirs = new();
    private readonly Label _logfile = new();
    private bool _nameEditInProgress;
    private string _pendingLaunchArgs = "";
    private bool _pendingLaunchRequireHashApproval = true;
    private bool _hubLoggedIn;
    private EosClient? _eosClient;
    private string? _epicProductUserId;
    private string? _epicDisplayNameShown;
    private bool _epicLoginRequested;

    private bool _veilnetAutoLoginAttempted;
    private VeilnetClient? _veilnetClient;
    private string _veilnetFunctionsBaseUrl = string.Empty;
    private readonly string _startupLinkCode;
    private readonly bool _startupSkinImportClipboard;
    private readonly bool _startupSkinLibraryRefresh;
    private readonly OfficialBuildVerifier _officialBuildVerifier;
    private readonly LauncherRuntimeConfig _launcherRuntimeConfig;

    private const string DefaultVeilnetLauncherPageUrl = "https://latticeveil.github.io/veilnet/launcher/";
    private const string DefaultVeilnetFunctionsBaseUrl = "https://lqghurvonrvrxfwjgkuu.supabase.co/functions/v1";
    private const string DefaultGameHashesGetUrl = "https://lqghurvonrvrxfwjgkuu.supabase.co/rest/v1/game_hashes";
    private const string DefaultSupabaseAnonKey = "sb_publishable_oy1En_XHnhp5AiOWruitmQ_sniWHETA";
    private static readonly string VeilnetAuthDir = Paths.SystemStateDir;
    private static readonly string VeilnetAuthPath = Paths.VeilnetLauncherAuthPath;
    private static readonly string LegacyVeilnetAuthPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "LatticeVeil",
        "veilnet_launcher_token.json");
    private bool _onlineFunctional;
    private bool _releaseHashAllowed;
    private bool _officialBuildVerified;
    private bool _onlineServicesReachable;
    private string _onlineStatusDetail = "Checking online services...";
    private readonly object _onlineValidationSync = new();
    private bool _onlineValidationInProgress;
    private LaunchReadiness _launchReadiness = LaunchReadiness.Unknown;
    private bool _officialHashOk;
    private bool _veilnetAuthOk;
    private bool _gateTicketOk;
    private bool _eosReadyOk;
    private string? _authTicket; // Store authentication ticket for claiming
    private bool _queuedLinkCodeConsumeInProgress;

    private sealed class ReleaseAllowlist
    {
        public string[] AllowedClientExeSha256 { get; set; } = Array.Empty<string>();
    }

    private sealed class VeilnetTokenRecord
    {
        public string Username { get; set; } = string.Empty;
        public string Token { get; set; } = string.Empty;
        public string UserId { get; set; } = string.Empty;
        public DateTime SavedAtUtc { get; set; } = DateTime.UtcNow;
    }

    private sealed class BrowserSkinImportEnvelope
    {
        public string Format { get; set; } = string.Empty;
        public int Version { get; set; }
        public List<BrowserSkinImportEntry> Skins { get; set; } = new();
    }

    private sealed class BrowserSkinImportEntry
    {
        public string Hash { get; set; } = string.Empty;
        public string DisplayName { get; set; } = string.Empty;
        public string PngBase64 { get; set; } = string.Empty;
    }

    private sealed class ProtectedVeilnetTokenEnvelope
    {
        public string PayloadBase64 { get; set; } = string.Empty;
    }

    private enum OnlineStartupState
    {
        Verified,
        HashMismatch,
        MisconfiguredEndpoint,
        ServiceUnavailable,
        Unauthorized,
        BadResponse,
        ComputeFailed
    }

    private enum UpdateReminderChoice
    {
        UpdateNow,
        Later,
        IgnoreThisRelease
    }

    public LauncherForm(Logger log, PlayerProfile profile, string? startupLinkCode = null, bool startupSkinImportClipboard = false, bool startupSkinLibraryRefresh = false)
    {
        _log = log;
        _profile = profile;
        _settings = GameSettings.LoadOrCreate(_log);
        _presenceReporter = new VeilnetGamePresenceReporter(_log, "launcher");
        _startupLinkCode = (startupLinkCode ?? string.Empty).Trim();
        _startupSkinImportClipboard = startupSkinImportClipboard;
        _startupSkinLibraryRefresh = startupSkinLibraryRefresh;
        _launcherRuntimeConfig = LauncherRuntimeConfig.Load(_log);
        _officialBuildVerifier = new OfficialBuildVerifier(_log, GetGameHashesGetUrl(), GetSupabaseAnonKey());
        _assetInstaller = new AssetPackInstaller(_log);
        _gameReleaseUpdater = new GameReleaseUpdater(_log);
        _gameReleaseUpdater.CleanupTransientStorage();
        _logFilePath = _log.LogFilePath;
        ResetLogSessionDate(_logFilePath);
        _log.Info($"Auth storage path: {VeilnetAuthPath}");

        if (string.IsNullOrWhiteSpace(_profile.OfflineUsername))
        {
            _profile.OfflineUsername = GenerateOfflineUsername();
            _profile.Save(_log);
        }

        Text = BuildLauncherWindowTitle();

        // Borderless (custom top bar).
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new DSize(1120, 700);
        DoubleBuffered = true;
        TabStop = false;
        ApplyLauncherWindowSizing();

        // Use the app's icon if present.
        try
        {
            var exePath = Environment.ProcessPath;
            if (!string.IsNullOrWhiteSpace(exePath) && File.Exists(exePath))
                Icon = DIcon.ExtractAssociatedIcon(exePath);
        }
        catch { }

        BuildLayout();

        try
        {
            if (Icon != null)
            {
                _windowIconImage?.Dispose();
                _windowIconImage = Icon.ToBitmap();
                _logoBox.Image = _windowIconImage;
            }
        }
        catch { }
        InitializeLauncherTrayIcon();
        ApplyTheme(_settings.DarkMode);

        TryLoadVeilnetAuth();

        FormClosed += (_, _) =>
        {
            SaveProfile();
            _pollTimer.Stop();
            _presenceReporter.Clear();
            DisposeLauncherTrayIcon();

            try { _windowIconImage?.Dispose(); _windowIconImage = null; }
            catch { }
            try { _hubVeilnetAvatarImage?.Dispose(); _hubVeilnetAvatarImage = null; }
            catch { }
            try { _skinLauncherPreviewImage?.Dispose(); _skinLauncherPreviewImage = null; }
            catch { }

            try { _gameProcess?.Dispose(); }
            catch { }

            try { _eosClient?.Dispose(); }
            catch { }

            _log.Info("Launcher closed.");
        };

        FormClosing += (_, e) =>
        {
            if (!_allowLauncherExit && e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                HandleLauncherCloseButtonRequest();
            }
        };

        Resize += (_, _) =>
        {
            if (WindowState == FormWindowState.Minimized && !_launcherTrayHidden)
                HandleLauncherMinimizeRequest();
        };

        _pollTimer.Interval = LogPollIntervalMs;
        _pollTimer.Tick += (_, _) =>
        {
            UpdateLogBox();
            RefreshGameProcessState();
            _eosClient?.Tick();
            UpdateEpicLoginStatus();
            _presenceReporter.ReportLauncher();
            TryConsumePendingLauncherRestoreRequests();
            _ = TryConsumePendingLinkCodesAsync();
            TryConsumePendingSkinImportRequests();
            TryConsumePendingSkinLibraryRefreshRequests();
        };
        _pollTimer.Start();

        // If the game was somehow launched outside the launcher, reflect that.
        RefreshGameProcessState();

        // Kick off startup checks after the window is shown.
        // (Constructors cannot be async, and we want the UI visible before any network-bound checks.)
        Shown += async (_, _) =>
        {
            var startupChecksTask = BeginStartupOnlineChecks();
            var deepLinkTask = TryConsumeStartupLinkCodeAsync();
            TryConsumeStartupSkinImportClipboard();
            TryConsumeStartupSkinLibraryRefresh();
            var gameUpdateTask = BeginGameUpdateCheckAsync();
            await Task.WhenAll(startupChecksTask, deepLinkTask, gameUpdateTask);
        };

        _log.Info("Launcher UI ready.");
    }

    private void InitializeLauncherTrayIcon()
    {
        try
        {
            _trayMenu = new ContextMenuStrip
            {
                ShowImageMargin = false,
                ShowCheckMargin = false
            };
            _trayVeilnetAuthItem = new ToolStripMenuItem("LOGIN WITH VEILNET", null, async (_, _) => await OnVeilnetPrimaryActionClicked());
            _trayVeilnetAuthItem.Visible = false;
            _trayVeilnetAccountHost = CreateTrayVeilnetAccountHost();
            _trayMenu.Items.Add(_trayVeilnetAccountHost);
            _trayMenu.Items.Add(new ToolStripSeparator());
            _trayMenu.Items.Add("Open Launcher", null, (_, _) => RestoreLauncherFromTray());
            _trayLaunchGameItem = new ToolStripMenuItem("LAUNCH LATTICEVEIL", null, (_, _) => LaunchGameFromTray());
            _trayMenu.Items.Add(_trayLaunchGameItem);
            _trayMenu.Items.Add("Open Skin GUI", null, (_, _) => OpenSkinGuiFromTray());
            _trayMenu.Items.Add(new ToolStripSeparator());
            _trayMenu.Items.Add("Exit Launcher", null, (_, _) => ExitLauncherFromTray());

            _trayIconImage = Icon != null
                ? (DIcon)Icon.Clone()
                : (DIcon)SystemIcons.Application.Clone();

            _trayIcon = new NotifyIcon
            {
                Icon = _trayIconImage,
                Text = Paths.IsDevBuild ? "[DEV] Lattice Launcher" : "Lattice Launcher",
                ContextMenuStrip = _trayMenu,
                Visible = false
            };
            _trayIcon.DoubleClick += (_, _) => RestoreLauncherFromTray();
        }
        catch (Exception ex)
        {
            _log.Warn($"Launcher tray icon initialization failed: {ex.Message}");
        }
    }

    private void DisposeLauncherTrayIcon()
    {
        try
        {
            if (_trayIcon != null)
                _trayIcon.Visible = false;
        }
        catch { }

        try { _trayIcon?.Dispose(); } catch { }
        try { _trayMenu?.Dispose(); } catch { }
        try { _trayIconImage?.Dispose(); } catch { }
        try { _trayVeilnetAvatarImage?.Dispose(); } catch { }

        _trayIcon = null;
        _trayMenu = null;
        _trayLaunchGameItem = null;
        _trayVeilnetAuthItem = null;
        _trayVeilnetAccountHost = null;
        _trayVeilnetAvatarBox = null;
        _trayVeilnetAuthButton = null;
        _trayVeilnetProfileButton = null;
        _trayVeilnetDivider = null;
        _trayIconImage = null;
        _trayVeilnetAvatarImage = null;
    }

    private ToolStripControlHost CreateTrayVeilnetAccountHost()
    {
        var panel = new Panel
        {
            Width = 312,
            Height = 50,
            Margin = Padding.Empty,
            Padding = new Padding(6, 6, 6, 6)
        };

        _trayVeilnetAvatarBox = new PictureBox
        {
            Location = new DPoint(6, 7),
            Size = new DSize(36, 36),
            SizeMode = PictureBoxSizeMode.Zoom,
            BorderStyle = BorderStyle.FixedSingle
        };

        _trayVeilnetAuthButton = new Button
        {
            Text = "LOGIN",
            Location = new DPoint(50, 7),
            Size = new DSize(134, 36),
            FlatStyle = FlatStyle.Flat
        };
        _trayVeilnetAuthButton.Click += async (_, _) =>
        {
            _trayMenu?.Close();
            await OnVeilnetPrimaryActionClicked();
        };

        _trayVeilnetDivider = new Panel
        {
            Location = new DPoint(192, 9),
            Size = new DSize(1, 32),
            BackColor = DColor.FromArgb(110, 110, 110)
        };

        _trayVeilnetProfileButton = new Button
        {
            Text = "PROFILE",
            Location = new DPoint(201, 7),
            Size = new DSize(104, 36),
            FlatStyle = FlatStyle.Flat,
            Enabled = false
        };
        _trayVeilnetProfileButton.Click += (_, _) =>
        {
            _trayMenu?.Close();
            OpenVeilnetProfileFromTray();
        };

        panel.Controls.Add(_trayVeilnetAvatarBox);
        panel.Controls.Add(_trayVeilnetAuthButton);
        panel.Controls.Add(_trayVeilnetDivider);
        panel.Controls.Add(_trayVeilnetProfileButton);
        return new ToolStripControlHost(panel)
        {
            AutoSize = false,
            Size = new DSize(panel.Width, panel.Height),
            Margin = Padding.Empty,
            Padding = Padding.Empty
        };
    }

    private void HandleLauncherMinimizeRequest()
    {
        if (_allowTaskbarMinimizeOnce)
        {
            _allowTaskbarMinimizeOnce = false;
            return;
        }

        if (_settings.AlwaysMinimizeLauncherToTray)
        {
            MinimizeLauncherToTray();
            return;
        }

        var choice = ShowLauncherMinimizePrompt();
        if (choice.MinimizeToTray)
        {
            if (choice.Always)
            {
                _settings.AlwaysMinimizeLauncherToTray = true;
                _settings.Save(_log);
            }

            MinimizeLauncherToTray();
            return;
        }

        MinimizeLauncherToTaskbar();
    }

    private void HandleLauncherCloseButtonRequest()
    {
        var savedAction = (_settings.LauncherCloseButtonAction ?? string.Empty).Trim();
        if (string.Equals(savedAction, "Tray", StringComparison.OrdinalIgnoreCase))
        {
            MinimizeLauncherToTray();
            return;
        }

        if (string.Equals(savedAction, "Close", StringComparison.OrdinalIgnoreCase))
        {
            CloseLauncherNormally();
            return;
        }

        var choice = ShowLauncherClosePrompt();
        if (choice.MinimizeToTray)
        {
            if (choice.Always)
            {
                _settings.LauncherCloseButtonAction = "Tray";
                _settings.Save(_log);
            }

            MinimizeLauncherToTray();
            return;
        }

        if (choice.Always)
        {
            _settings.LauncherCloseButtonAction = "Close";
            _settings.Save(_log);
        }

        CloseLauncherNormally();
    }

    private void CloseLauncherNormally()
    {
        _allowLauncherExit = true;
        Close();
    }

    private void MinimizeLauncherToTaskbar()
    {
        _launcherTrayHidden = false;
        if (_trayIcon != null)
            _trayIcon.Visible = false;

        if (WindowState != FormWindowState.Minimized)
        {
            _allowTaskbarMinimizeOnce = true;
            WindowState = FormWindowState.Minimized;
        }
    }

    private void MinimizeLauncherToTray()
    {
        if (_trayIcon == null)
        {
            MinimizeLauncherToTaskbar();
            return;
        }

        _launcherTrayHidden = true;
        _trayIcon.Visible = true;
        _trayIcon.ShowBalloonTip(1500, "LatticeVeil", "Launcher minimized to the system tray.", ToolTipIcon.Info);
        Hide();
        _log.Info("Launcher minimized to system tray.");
    }

    private void RestoreLauncherFromTray()
    {
        _launcherTrayHidden = false;
        if (_trayIcon != null)
            _trayIcon.Visible = false;

        Show();
        WindowState = FormWindowState.Normal;
        Activate();
    }

    private void TryConsumePendingLauncherRestoreRequests()
    {
        var count = LauncherProtocolLinking.DequeuePendingRestoreLauncherRequests(_log);
        if (count <= 0)
            return;

        RestoreLauncherFromTray();
    }

    private void LaunchGameFromTray()
    {
        if (InvokeRequired)
        {
            BeginInvoke(new Action(LaunchGameFromTray));
            return;
        }

        _trayMenu?.Close();
        HandleLaunchButtonAction();
    }

    private void SetTrayLaunchGameItemState(GameState state)
    {
        if (_trayLaunchGameItem == null)
            return;

        var running = state != GameState.NotRunning;
        _trayLaunchGameItem.Text = running ? "KILL LATTICEVEIL" : "LAUNCH LATTICEVEIL";
        _trayLaunchGameItem.Enabled = running || _launchBtn.Enabled;
    }

    private void ShowVeilnetLinkedLaunchPrompt(string? username)
    {
        if (InvokeRequired)
        {
            BeginInvoke(new Action(() => ShowVeilnetLinkedLaunchPrompt(username)));
            return;
        }

        if (_veilnetLaunchPromptOpen)
            return;

        _veilnetLaunchPromptOpen = true;
        try
        {
            if (_launcherTrayHidden || !Visible)
                RestoreLauncherFromTray();

            var displayName = string.IsNullOrWhiteSpace(username) ? "VEILNET" : username.Trim();
            using var prompt = new Form
            {
                Text = "Veilnet Login",
                StartPosition = FormStartPosition.CenterParent,
                FormBorderStyle = FormBorderStyle.FixedDialog,
                MaximizeBox = false,
                MinimizeBox = false,
                ShowInTaskbar = false,
                ClientSize = new DSize(380, 146),
                BackColor = _settings.DarkMode ? DColor.FromArgb(16, 16, 16) : System.Drawing.SystemColors.Control,
                ForeColor = _settings.DarkMode ? DColor.White : System.Drawing.SystemColors.ControlText
            };

            var label = new Label
            {
                AutoSize = false,
                Text = $"LOGGED IN AS: {displayName}",
                TextAlign = System.Drawing.ContentAlignment.MiddleCenter,
                Font = new DFont(Font.FontFamily, 11f, System.Drawing.FontStyle.Bold),
                Bounds = new System.Drawing.Rectangle(16, 18, 348, 42)
            };

            var ok = new Button
            {
                Text = "OK",
                DialogResult = DialogResult.OK,
                Bounds = new System.Drawing.Rectangle(106, 92, 78, 30)
            };

            var launch = new Button
            {
                Text = "LAUNCH",
                DialogResult = DialogResult.Yes,
                Bounds = new System.Drawing.Rectangle(196, 92, 96, 30)
            };

            prompt.Controls.Add(label);
            prompt.Controls.Add(ok);
            prompt.Controls.Add(launch);
            prompt.AcceptButton = launch;
            prompt.CancelButton = ok;

            if (prompt.ShowDialog(this) == DialogResult.Yes)
                _ = LaunchGameFromVeilnetPromptAsync();
        }
        finally
        {
            _veilnetLaunchPromptOpen = false;
        }
    }

    private async Task LaunchGameFromVeilnetPromptAsync()
    {
        if (InvokeRequired)
        {
            BeginInvoke(new Action(async () => await LaunchGameFromVeilnetPromptAsync()));
            return;
        }

        if (GetGameState() == GameState.NotRunning && IsOnlineModeSelected())
        {
            if (!_onlineValidationInProgress && (_launchReadiness != LaunchReadiness.ReadyOnline || !_onlineFunctional || !_releaseHashAllowed))
                _ = BeginStartupOnlineChecks();

            var deadline = DateTime.UtcNow.AddSeconds(12);
            while (_onlineValidationInProgress && DateTime.UtcNow < deadline)
            {
                await Task.Delay(250);
                SetLaunchButtonState(GetGameState());
            }
        }

        SetLaunchButtonState(GetGameState());
        if (!_launchBtn.Enabled && GetGameState() == GameState.NotRunning)
        {
            MessageBox.Show(
                this,
                "Veilnet is still validating online launch. Try Launch again in a moment.",
                "Launch Not Ready",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            return;
        }

        HandleLaunchButtonAction();
    }

    private void HandleLaunchButtonAction()
    {
        SaveProfile();
        var state = GetGameState();
        if (state == GameState.NotRunning)
        {
            if (!_gameUpdateBusy && _gameUpdateCheck?.IsUpdateAvailable == true && !_gameUpdateReminderShown)
            {
                _gameUpdateReminderShown = true;
                var choice = ShowGameUpdateReminderDialog(_gameUpdateCheck.ReleaseTitle);
                if (choice == UpdateReminderChoice.UpdateNow)
                {
                    StartGameUpdate();
                    return;
                }

                if (choice == UpdateReminderChoice.IgnoreThisRelease)
                {
                    _settings.IgnoredGameReleaseTitle = _gameUpdateCheck.ReleaseTitle;
                    SaveLauncherSettings();
                }
            }

            var mode = (_launchModeBox.SelectedItem as string) ?? "Online";
            if (string.Equals(mode, "Offline", StringComparison.OrdinalIgnoreCase))
            {
                LaunchGameProcess("--offline", requireHashApproval: false);
                return;
            }

            if (!HasValidVeilnetSessionForOnline())
            {
                var switchResult = MessageBox.Show(
                    "You're not signed in. Online features are unavailable. Switch to Offline mode?",
                    "Online Unavailable",
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Warning);
                if (switchResult == DialogResult.Yes)
                {
                    _launchModeBox.SelectedItem = "Offline";
                    LaunchGameProcess("--offline", requireHashApproval: false);
                }
                return;
            }

            if (_launchReadiness != LaunchReadiness.ReadyOnline || !_onlineFunctional || !_releaseHashAllowed)
            {
                var reason = _onlineValidationInProgress
                    ? "Validating Veilnet... online launch will unlock when checks finish."
                    : (string.IsNullOrWhiteSpace(_onlineStatusDetail)
                        ? "Online services are unavailable for this build."
                        : _onlineStatusDetail);
                var switchResult = MessageBox.Show(
                    $"{reason}\n\nSwitch to Offline mode?",
                    "Online Unavailable",
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Warning);
                if (switchResult == DialogResult.Yes)
                {
                    _launchModeBox.SelectedItem = "Offline";
                    LaunchGameProcess("--offline", requireHashApproval: false);
                }
                return;
            }

            StartAssetCheckAndLaunch(BuildOnlineLaunchArgs());
            return;
        }

        ConfirmAndKillGame();
    }

    private void OpenSkinGuiFromTray()
    {
        if (InvokeRequired)
        {
            BeginInvoke(new Action(OpenSkinGuiFromTray));
            return;
        }

        RestoreLauncherFromTray();
        _skinsBtn.PerformClick();
    }

    private void ExitLauncherFromTray()
    {
        _allowLauncherExit = true;
        Close();
    }

    private static (bool MinimizeToTray, bool Always) ShowLauncherMinimizePrompt()
    {
        using var prompt = new Form
        {
            Text = "LatticeVeil Launcher",
            StartPosition = FormStartPosition.CenterScreen,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            MaximizeBox = false,
            MinimizeBox = false,
            ShowInTaskbar = false,
            TopMost = true,
            ClientSize = new DSize(400, 150)
        };

        var label = new Label
        {
            AutoSize = false,
            Text = "Minimize LatticeVeil Launcher to the system tray?",
            TextAlign = ContentAlignment.MiddleLeft,
            Bounds = new System.Drawing.Rectangle(18, 16, 364, 36)
        };

        var always = new CheckBox
        {
            Text = "Always do this",
            AutoSize = true,
            Bounds = new System.Drawing.Rectangle(18, 62, 260, 24)
        };

        var tray = new Button
        {
            Text = "Tray",
            DialogResult = DialogResult.Yes,
            Bounds = new System.Drawing.Rectangle(214, 104, 76, 28)
        };

        var taskbar = new Button
        {
            Text = "Taskbar",
            DialogResult = DialogResult.No,
            Bounds = new System.Drawing.Rectangle(306, 104, 76, 28)
        };

        prompt.Controls.Add(label);
        prompt.Controls.Add(always);
        prompt.Controls.Add(tray);
        prompt.Controls.Add(taskbar);
        prompt.AcceptButton = tray;
        prompt.CancelButton = taskbar;

        var result = prompt.ShowDialog();
        return (result == DialogResult.Yes, always.Checked);
    }

    private static (bool MinimizeToTray, bool Always) ShowLauncherClosePrompt()
    {
        using var prompt = new Form
        {
            Text = "LatticeVeil Launcher",
            StartPosition = FormStartPosition.CenterScreen,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            MaximizeBox = false,
            MinimizeBox = false,
            ShowInTaskbar = false,
            TopMost = true,
            ClientSize = new DSize(430, 150)
        };

        var label = new Label
        {
            AutoSize = false,
            Text = "Minimize LatticeVeil Launcher to the system tray instead of closing?",
            TextAlign = ContentAlignment.MiddleLeft,
            Bounds = new System.Drawing.Rectangle(18, 16, 394, 36)
        };

        var always = new CheckBox
        {
            Text = "Always do this",
            AutoSize = true,
            Bounds = new System.Drawing.Rectangle(18, 62, 260, 24)
        };

        var tray = new Button
        {
            Text = "Tray",
            DialogResult = DialogResult.Yes,
            Bounds = new System.Drawing.Rectangle(244, 104, 76, 28)
        };

        var close = new Button
        {
            Text = "Close",
            DialogResult = DialogResult.No,
            Bounds = new System.Drawing.Rectangle(336, 104, 76, 28)
        };

        prompt.Controls.Add(label);
        prompt.Controls.Add(always);
        prompt.Controls.Add(tray);
        prompt.Controls.Add(close);
        prompt.AcceptButton = tray;
        prompt.CancelButton = close;

        var result = prompt.ShowDialog();
        return (result == DialogResult.Yes, always.Checked);
    }

    private void BuildLayout()
    {
        _root.Dock = DockStyle.Fill;
        _root.ColumnCount = 2;
        _root.RowCount = 6;
        _root.Padding = new Padding(12);
        _root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 60));
        _root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 36));
        _root.RowStyles.Add(new RowStyle(SizeType.Absolute, 48)); // top bar
        _root.RowStyles.Add(new RowStyle(SizeType.Absolute, 0)); // assets
        _root.RowStyles.Add(new RowStyle(SizeType.Absolute, 0)); // log file
        _root.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); // body
        _root.RowStyles.Add(new RowStyle(SizeType.Absolute, 124)); // buttons
        _root.RowStyles.Add(new RowStyle(SizeType.Absolute, 6));  // bottom spacing

        BuildTopBar();
        _root.SetColumnSpan(_topBar, 2);
        _root.Controls.Add(_topBar, 0, 0);

        _logPanel.Dock = DockStyle.Fill;
        _logPanel.ColumnCount = 1;
        _logPanel.RowCount = 2;
        _logPanel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        _logPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));

        _logBox.ReadOnly = true;
        _logBox.Multiline = true;
        _logBox.ScrollBars = ScrollBars.Vertical;
        _logBox.Dock = DockStyle.Fill;
        _logBox.Font = new DFont("Consolas", 9F);
        _logPanel.Controls.Add(_logBox, 0, 0);

        _progressHost.Dock = DockStyle.Fill;
        _progressHost.Padding = new Padding(6, 3, 6, 6);

        _progressLabel.AutoSize = false;
        _progressLabel.Dock = DockStyle.Top;
        _progressLabel.Height = 18;
        _progressLabel.Font = new DFont(Font.FontFamily, 9.5f, System.Drawing.FontStyle.Bold);
        _progressLabel.Text = "Idle";

        _progressTrack.Dock = DockStyle.Bottom;
        _progressTrack.Height = 8;
        _progressTrack.Padding = new Padding(1);
        _progressTrack.Controls.Add(_progressFill);
        _progressTrack.Resize += (_, _) => UpdateProgressFill();

        _progressFill.Dock = DockStyle.Left;
        _progressFill.Width = 0;

        _onlineStatusIndicator.Size = new DSize(14, 14);
        _onlineStatusIndicator.BackColor = DColor.Red;
        _onlineStatusIndicator.Location = new DPoint(6, 23);
        _onlineStatusIndicator.Anchor = AnchorStyles.Left | AnchorStyles.Bottom;
        _onlineStatusIndicator.BorderStyle = BorderStyle.None;
        _toolTip.SetToolTip(_onlineStatusIndicator, "Online access unavailable. Offline/LAN only.");

        _onlineHeader.AutoSize = false;
        _onlineHeader.Size = new DSize(230, 18);
        _onlineHeader.Text = "ONLINE ACCESS: OFFLINE/LAN";
        _onlineHeader.Font = new DFont(Font.FontFamily, 9f, System.Drawing.FontStyle.Bold);
        _onlineHeader.Location = new DPoint(26, 20);
        _onlineHeader.Anchor = AnchorStyles.Left | AnchorStyles.Bottom;
        _onlineHeader.TextAlign = System.Drawing.ContentAlignment.MiddleLeft;

        _progressHost.Controls.Add(_progressLabel);
        _progressHost.Controls.Add(_progressTrack);
        _progressHost.Controls.Add(_onlineStatusIndicator);
        _progressHost.Controls.Add(_onlineHeader);
        _logPanel.Controls.Add(_progressHost, 0, 1);

        _root.Controls.Add(_logPanel, 0, 3);

        _toolTip.AutoPopDelay = 10000;
        _toolTip.InitialDelay = 400;
        _toolTip.ReshowDelay = 100;
        _toolTip.ShowAlways = true;

        _right.Dock = DockStyle.Fill;
        _right.FlowDirection = FlowDirection.TopDown;
        _right.WrapContents = false;
        _right.AutoScroll = true;
        _right.Padding = new Padding(18, 16, 18, 16);
        _right.Margin = new Padding(12, 0, 0, 0);
        _right.Paint += (_, e) =>
        {
            var borderRect = new System.Drawing.Rectangle(0, 0, Math.Max(0, _right.ClientSize.Width - 1), Math.Max(0, _right.ClientSize.Height - 1));
            ControlPaint.DrawBorder(e.Graphics, borderRect, DColor.White, ButtonBorderStyle.Solid);
        };

        // Offline identity uses the shared account row instead of a separate top field.
        _offlineNameLabel.Text = "OFFLINE USERNAME";
        _offlineNameLabel.AutoSize = true;
        _offlineNameLabel.Font = new DFont(Font.FontFamily, 9.5f, System.Drawing.FontStyle.Bold);

        _offlineNameBox.Width = 280;
        _offlineNameBox.Height = 36;
        _offlineNameBox.Font = new DFont(Font.FontFamily, 10f, System.Drawing.FontStyle.Bold);
        _offlineNameBox.Text = _profile.OfflineUsername ?? string.Empty;
        _offlineNameBox.Leave += (_, _) => SaveOfflineNameFromUi();

        // Save username button removed per user request

        _claimUsernameBtn.Visible = false;

        // Hide legacy "Change Username" button entirely.
        _changeUsernameBtn.Visible = false;

        UpdateUsernameLabel();
        UpdateOfflineNameEnabled();

        _keepOpenBox.Text = "Keep launcher open?";
        _keepOpenBox.Width = 250;
        _keepOpenBox.Checked = _settings.KeepLauncherOpen;
        _keepOpenBox.CheckedChanged += (_, _) =>
        {
            _settings.KeepLauncherOpen = _keepOpenBox.Checked;
            SaveLauncherSettings();
            _log.Info($"KeepLauncherOpen: {_settings.KeepLauncherOpen}");
        };
        _right.Controls.Add(_keepOpenBox);

        _darkModeBox.Text = "Dark mode";
        _darkModeBox.Width = 250;
        _darkModeBox.Checked = _settings.DarkMode;
        _darkModeBox.CheckedChanged += (_, _) =>
        {
            _settings.DarkMode = _darkModeBox.Checked;
            SaveLauncherSettings();
            ApplyTheme(_settings.DarkMode);
            _log.Info($"DarkMode: {_settings.DarkMode}");
        };
        _right.Controls.Add(_darkModeBox);

        // Recommended label - removed per user request
        // UpdateRecommendedLabel();
        // _right.Controls.Add(_recommendedLabel);

        BuildOnlineSection();
        UpdateIdentityModeVisuals();

        _root.Controls.Add(_right, 1, 3);

        _buttons.Dock = DockStyle.Fill;
        _buttons.FlowDirection = FlowDirection.RightToLeft;
        _buttons.WrapContents = false;
        _buttons.Padding = new Padding(6, 4, 6, 0);
        _buttons.Resize += (_, _) => UpdateBottomButtonLayout();

        const int footerButtonWidth = 118;

        ConfigureIconButton(_launchBtn, "Launch", footerButtonWidth, 64);
        _toolTip.SetToolTip(_launchBtn, "Launch game");
        _launchBtn.Click += (_, _) => HandleLaunchButtonAction();

        _launchModeBox.DropDownStyle = ComboBoxStyle.DropDownList;
        _launchModeBox.Items.AddRange(new object[] { "Online", "Offline" });
        _launchModeBox.SelectedIndex = 0;
        _launchModeBox.Width = 126;
        _launchModeBox.Height = 30;
        _launchModeBox.Font = new DFont(Font.FontFamily, 8.5f, System.Drawing.FontStyle.Bold);
        _launchModeBox.Margin = Padding.Empty;
        _toolTip.SetToolTip(_launchModeBox, "Launch mode");
        _launchModeBox.SelectedIndexChanged += async (_, _) =>
        {
            UpdateIdentityModeVisuals();
            if (IsOnlineModeSelected())
                await BeginStartupOnlineChecks();

            SetLaunchButtonState(GetGameState());
        };

        ConfigureIconButton(_openLogsBtn, "Logs", footerButtonWidth, 64);
        _toolTip.SetToolTip(_openLogsBtn, "Open logs folder");
        _openLogsBtn.Click += (_, _) => OpenLogsFolder();

        ConfigureIconButton(_openGameFolderBtn, "Game Folder", footerButtonWidth, 52);
        _toolTip.SetToolTip(_openGameFolderBtn, "Open game folder");
        _openGameFolderBtn.Click += (_, _) => OpenGameFolder();

        const int footerHostHeight = 98;
        const int footerButtonTop = 30;

        _bottomLeftHost.Width = footerButtonWidth;
        _bottomLeftHost.Height = footerHostHeight;
        _bottomLeftHost.Margin = Padding.Empty;
        _bottomLeftHost.Controls.Add(_openGameFolderBtn);
        _openGameFolderBtn.Location = new DPoint(0, footerButtonTop);

        _logsHost.Width = footerButtonWidth;
        _logsHost.Height = footerHostHeight;
        _logsHost.Margin = Padding.Empty;
        _logsHost.Controls.Add(_openLogsBtn);
        _openLogsBtn.Location = new DPoint(0, footerButtonTop);

        _launchModeHost.Width = footerButtonWidth;
        _launchModeHost.Height = footerHostHeight;
        _launchModeHost.Margin = new Padding(0, 0, 14, 0);
        _launchModeHost.Controls.Add(_launchModeBox);
        _launchModeHost.Controls.Add(_launchBtn);
        _launchModeBox.Width = footerButtonWidth;
        _launchModeBox.Location = new DPoint(0, 0);
        _launchBtn.Location = new DPoint(0, footerButtonTop);

        _buttonSpacer.Width = 0;
        _buttonSpacer.Height = 1;
        _buttonSpacer.Margin = Padding.Empty;

        _buttons.Controls.Add(_launchModeHost);
        _buttons.Controls.Add(_logsHost);
        _buttons.Controls.Add(_buttonSpacer);
        _buttons.Controls.Add(_bottomLeftHost);
        UpdateBottomButtonLayout();

        _root.SetColumnSpan(_buttons, 2);
        _root.Controls.Add(_buttons, 0, 4);

        Controls.Add(_root);
        DisableTabFocus(_root);
        BuildAssetPanel();
        Controls.Add(_assetPanel);
        DisableTabFocus(_assetPanel);
        _assetPanel.BringToFront();
    }

    private static void DisableTabFocus(Control root)
    {
        root.TabStop = false;
        foreach (Control child in root.Controls)
            DisableTabFocus(child);
    }

    private void ConfigureIconButton(Button button, string text, int width, int height)
    {
        button.Text = text;
        button.Width = width;
        button.Height = height;
        button.TextImageRelation = TextImageRelation.ImageAboveText;
        button.ImageAlign = System.Drawing.ContentAlignment.TopCenter;
        button.TextAlign = System.Drawing.ContentAlignment.BottomCenter;
        button.Font = new DFont(Font.FontFamily, 8.5f, System.Drawing.FontStyle.Bold);
        button.UseVisualStyleBackColor = false;
        button.Padding = new Padding(2, 4, 2, 4);
    }

    private void UpdateBottomButtonLayout()
    {
        if (_buttons.ClientSize.Width <= 0)
            return;

        var nonSpacerWidth = _launchModeHost.Width + _launchModeHost.Margin.Horizontal
            + _logsHost.Width + _logsHost.Margin.Horizontal
            + _bottomLeftHost.Width + _bottomLeftHost.Margin.Horizontal;
        var available = _buttons.ClientSize.Width - _buttons.Padding.Horizontal - nonSpacerWidth;
        _buttonSpacer.Width = Math.Max(0, available);
        _buttonSpacer.BackColor = BackColor;
    }

    private void BuildOnlineSection()
    {
        _hubLoginBtn.Text = "LOGIN";
        _hubLoginBtn.Width = 280;
        _hubLoginBtn.Height = 40;
        _hubLoginBtn.TextImageRelation = TextImageRelation.ImageBeforeText;
        _hubLoginBtn.ImageAlign = System.Drawing.ContentAlignment.MiddleLeft;
        _hubLoginBtn.TextAlign = System.Drawing.ContentAlignment.MiddleCenter;
        _hubLoginBtn.Image = LoadProfileButtonImage();
        _hubLoginBtn.Click += async (_, _) => await OnEpicLoginClicked();
        _right.Controls.Add(_hubLoginBtn);

        _hubGoogleBtn.Text = "LOGIN WITH VEILNET";
        _hubGoogleBtn.Width = 280;
        _hubGoogleBtn.Height = 48;
        _hubGoogleBtn.Font = new DFont(Font.FontFamily, 10f, System.Drawing.FontStyle.Bold);
        _hubGoogleBtn.TextImageRelation = TextImageRelation.ImageBeforeText;
        _hubGoogleBtn.ImageAlign = System.Drawing.ContentAlignment.MiddleLeft;
        _hubGoogleBtn.TextAlign = System.Drawing.ContentAlignment.MiddleCenter;
        _hubGoogleBtn.Image = null;
        _hubGoogleBtn.Click += async (_, _) => await OnVeilnetPrimaryActionClicked();

        _hubVeilnetAccountRow.Width = 280;
        _hubVeilnetAccountRow.Height = 72;
        _hubVeilnetAccountRow.Margin = new Padding(0, 4, 0, 8);

        _hubVeilnetAvatarBox.Width = 72;
        _hubVeilnetAvatarBox.Height = 72;
        _hubVeilnetAvatarBox.Location = new DPoint(0, 0);
        _hubVeilnetAvatarBox.SizeMode = PictureBoxSizeMode.Zoom;
        _hubVeilnetAvatarBox.BorderStyle = BorderStyle.FixedSingle;
        _hubVeilnetAccountRow.Controls.Add(_hubVeilnetAvatarBox);

        _hubVeilnetInfoPanel.Location = new DPoint(84, 0);
        _hubVeilnetInfoPanel.Width = 196;
        _hubVeilnetInfoPanel.Height = 72;

        _hubVeilnetUserLabel.Text = "VEILNET ACCOUNT";
        _hubVeilnetUserLabel.AutoSize = false;
        _hubVeilnetUserLabel.Width = 196;
        _hubVeilnetUserLabel.Height = 22;
        _hubVeilnetUserLabel.Location = new DPoint(0, 4);
        _hubVeilnetUserLabel.TextAlign = System.Drawing.ContentAlignment.MiddleLeft;
        _hubVeilnetUserLabel.Font = new DFont(Font.FontFamily, 9f, System.Drawing.FontStyle.Bold);
        _hubVeilnetInfoPanel.Controls.Add(_hubVeilnetUserLabel);

        _hubGoogleBtn.Width = 196;
        _hubGoogleBtn.Location = new DPoint(0, 28);
        _hubVeilnetInfoPanel.Controls.Add(_hubGoogleBtn);

        _offlineNameLabel.AutoSize = false;
        _offlineNameLabel.Width = 196;
        _offlineNameLabel.Height = 22;
        _offlineNameLabel.Location = new DPoint(0, 4);
        _offlineNameLabel.TextAlign = System.Drawing.ContentAlignment.MiddleLeft;
        _hubVeilnetInfoPanel.Controls.Add(_offlineNameLabel);

        _offlineNameBox.Width = 196;
        _offlineNameBox.Height = 36;
        _offlineNameBox.Location = new DPoint(0, 28);
        _hubVeilnetInfoPanel.Controls.Add(_offlineNameBox);

        _hubVeilnetAccountRow.Controls.Add(_hubVeilnetInfoPanel);
        _right.Controls.Add(_hubVeilnetAccountRow);

        _hubResetBtn.Text = "RESET VEILNET LOGIN";
        _hubResetBtn.Width = 280;
        _hubResetBtn.Height = 34;
        _hubResetBtn.Enabled = false;
        _hubResetBtn.Visible = false;
        _hubResetBtn.Click += (_, _) => OnVeilnetResetClicked();
        _right.Controls.Add(_hubResetBtn);

        // Hub status label - hidden per user request
        _hubStatusLabel.Text = _onlineStatusDetail;
        _hubStatusLabel.AutoSize = true;
        _hubStatusLabel.Visible = false; // Hide the status label
        _right.Controls.Add(_hubStatusLabel);

        BuildSkinSection();

        if (EpicLoginInGameOnly)
        {
            _hubLoginBtn.Visible = false;
            _hubResetBtn.Visible = false;
        }
    }

    private void BuildSkinSection()
    {
        _skinLauncherPanel.Width = 280;
        _skinLauncherPanel.Height = 98;
        _skinLauncherPanel.Margin = new Padding(0, 6, 0, 8);

        _skinLauncherTitle.Text = "SKIN";
        _skinLauncherTitle.AutoSize = false;
        _skinLauncherTitle.Width = 280;
        _skinLauncherTitle.Height = 18;
        _skinLauncherTitle.Location = new DPoint(0, 0);
        _skinLauncherTitle.Font = new DFont(Font.FontFamily, 9f, System.Drawing.FontStyle.Bold);
        _skinLauncherTitle.TextAlign = System.Drawing.ContentAlignment.MiddleLeft;
        _skinLauncherPanel.Controls.Add(_skinLauncherTitle);

        _skinLauncherPreviewBox.Width = 72;
        _skinLauncherPreviewBox.Height = 72;
        _skinLauncherPreviewBox.Location = new DPoint(0, 24);
        _skinLauncherPreviewBox.SizeMode = PictureBoxSizeMode.Zoom;
        _skinLauncherPreviewBox.BorderStyle = BorderStyle.FixedSingle;
        _skinLauncherPanel.Controls.Add(_skinLauncherPreviewBox);

        _skinsBtn.Text = "SKINS";
        _skinsBtn.Width = 196;
        _skinsBtn.Height = 48;
        _skinsBtn.Location = new DPoint(84, 36);
        _skinsBtn.Font = new DFont(Font.FontFamily, 10f, System.Drawing.FontStyle.Bold);
        _skinsBtn.Click += (_, _) => ShowSkinManagerPopup();
        _skinLauncherPanel.Controls.Add(_skinsBtn);

        _right.Controls.Add(_skinLauncherPanel);
        RefreshLauncherSkinPreview();
    }

    private void RefreshLauncherSkinPreview()
    {
        var previous = _skinLauncherPreviewImage;
        var activePath = SkinLibrary.TryGetActivePngPath(out var skinPath) ? skinPath : null;
        _skinLauncherPreviewImage = CreateSkinHeadPreview(activePath, 72);
        _skinLauncherPreviewBox.Image = _skinLauncherPreviewImage;
        previous?.Dispose();
    }

    private PlayerSkinCloudClient CreatePlayerSkinCloudClient()
    {
        return new PlayerSkinCloudClient(_log, GetVeilnetFunctionsBaseUrl(), GetSupabaseAnonKey());
    }

    private string GetCurrentVeilnetAccessToken()
    {
        var token = (Environment.GetEnvironmentVariable("LV_VEILNET_ACCESS_TOKEN") ?? string.Empty).Trim();
        if (!string.IsNullOrWhiteSpace(token))
            return token;

        var record = TryReadVeilnetAuth();
        return (record?.Token ?? string.Empty).Trim();
    }

    private void QueueCloudSkinUpload(string reason)
    {
        var token = GetCurrentVeilnetAccessToken();
        if (string.IsNullOrWhiteSpace(token))
        {
            _log.Info($"Cloud skin upload skipped: no Veilnet account session. reason={reason}");
            return;
        }

        var uploadSequence = Interlocked.Increment(ref _skinCloudUploadSequence);
        _ = Task.Run(async () =>
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(12));
                await Task.Delay(450, cts.Token).ConfigureAwait(false);
                if (uploadSequence != Volatile.Read(ref _skinCloudUploadSequence))
                    return;

                await _skinCloudUploadLock.WaitAsync(cts.Token).ConfigureAwait(false);
                try
                {
                    if (uploadSequence != Volatile.Read(ref _skinCloudUploadSequence))
                        return;

                    var result = await CreatePlayerSkinCloudClient().UploadActiveSkinAsync(token, cts.Token).ConfigureAwait(false);
                    if (!result.Ok)
                        _log.Warn($"Cloud skin upload failed: reason={reason}, error={result.Message}");
                }
                finally
                {
                    _skinCloudUploadLock.Release();
                }
            }
            catch (OperationCanceledException)
            {
                _log.Warn($"Cloud skin upload timed out: reason={reason}");
            }
            catch (Exception ex)
            {
                _log.Warn($"Cloud skin upload error: reason={reason}, error={ex.Message}");
            }
        });
    }

    private void QueueCloudSkinFetchAndApply(string reason, Action? afterApplyOnUiThread = null)
    {
        var token = GetCurrentVeilnetAccessToken();
        if (string.IsNullOrWhiteSpace(token))
        {
            _log.Info($"Cloud skin fetch skipped: no Veilnet account session. reason={reason}");
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(12));
                var result = await CreatePlayerSkinCloudClient().FetchAndApplyAccountSkinAsync(token, cts.Token).ConfigureAwait(false);
                if (!result.Ok)
                {
                    _log.Warn($"Cloud skin fetch failed: reason={reason}, error={result.Message}");
                    return;
                }

                if (string.Equals(result.Message, "no_remote_skin", StringComparison.OrdinalIgnoreCase)
                    && !string.IsNullOrWhiteSpace(SkinLibrary.ReadActiveHash()))
                {
                    var uploadResult = await CreatePlayerSkinCloudClient().UploadActiveSkinAsync(token, cts.Token).ConfigureAwait(false);
                    if (!uploadResult.Ok)
                        _log.Warn($"Cloud skin initial upload failed: reason={reason}, error={uploadResult.Message}");
                }

                if (IsHandleCreated)
                {
                    BeginInvoke(new Action(() =>
                    {
                        RefreshLauncherSkinPreview();
                        afterApplyOnUiThread?.Invoke();
                    }));
                }
            }
            catch (Exception ex)
            {
                _log.Warn($"Cloud skin fetch error: reason={reason}, error={ex.Message}");
            }
        });
    }

    private void ShowSkinManagerPopup()
    {
        using var popup = new Form
        {
            FormBorderStyle = FormBorderStyle.None,
            StartPosition = FormStartPosition.Manual,
            ClientSize = new DSize(860, 540),
            BackColor = _settings.DarkMode ? DColor.FromArgb(12, 12, 12) : System.Drawing.SystemColors.Control,
            ForeColor = _settings.DarkMode ? DColor.White : System.Drawing.SystemColors.ControlText,
            ShowInTaskbar = false,
            MinimizeBox = false,
            MaximizeBox = false,
            AllowDrop = true
        };

        var ownerBounds = Bounds;
        popup.Location = new DPoint(
            ownerBounds.Left + Math.Max(0, (ownerBounds.Width - popup.Width) / 2),
            ownerBounds.Top + Math.Max(0, (ownerBounds.Height - popup.Height) / 2));
        popup.Paint += (_, e) =>
        {
            var borderColor = _settings.DarkMode ? DColor.White : DColor.Black;
            var outer = new System.Drawing.Rectangle(0, 0, Math.Max(0, popup.ClientSize.Width - 1), Math.Max(0, popup.ClientSize.Height - 1));
            var inner = new System.Drawing.Rectangle(1, 1, Math.Max(0, popup.ClientSize.Width - 3), Math.Max(0, popup.ClientSize.Height - 3));
            using var pen = new Pen(borderColor, 1f);
            e.Graphics.DrawRectangle(pen, outer);
            e.Graphics.DrawRectangle(pen, inner);
        };

        var title = new Label
        {
            Text = "SKIN",
            AutoSize = false,
            Location = new DPoint(18, 14),
            Size = new DSize(320, 28),
            Font = new DFont(Font.FontFamily, 14f, System.Drawing.FontStyle.Bold),
            ForeColor = popup.ForeColor,
            BackColor = popup.BackColor
        };
        popup.Controls.Add(title);

        var close = new Button
        {
            Text = "X",
            Location = new DPoint(810, 12),
            Size = new DSize(36, 30)
        };
        close.Click += (_, _) => popup.Close();
        ApplyButtonThemeForCurrentMode(close);
        popup.Controls.Add(close);

        var libraryPanel = new FlowLayoutPanel
        {
            Location = new DPoint(18, 54),
            Size = new DSize(360, 390),
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoScroll = true,
            BackColor = popup.BackColor
        };
        popup.Controls.Add(libraryPanel);

        var previewBox = new PictureBox
        {
            Location = new DPoint(426, 56),
            Size = new DSize(360, 320),
            SizeMode = PictureBoxSizeMode.Zoom,
            BorderStyle = BorderStyle.FixedSingle,
            BackColor = _settings.DarkMode ? DColor.FromArgb(20, 20, 20) : DColor.White,
            TabStop = true
        };
        popup.Controls.Add(previewBox);

        var layerToggle = new CheckBox
        {
            Location = new DPoint(426, 386),
            Size = new DSize(92, 26),
            Text = "LAYERS",
            Checked = true,
            ForeColor = popup.ForeColor,
            BackColor = popup.BackColor
        };
        popup.Controls.Add(layerToggle);

        var dropHint = new Label
        {
            Location = new DPoint(18, 456),
            Size = new DSize(360, 18),
            Text = "Drop a 64x64 PNG here to import it.",
            ForeColor = _settings.DarkMode ? DColor.FromArgb(180, 180, 180) : DColor.FromArgb(70, 70, 70),
            BackColor = popup.BackColor,
            Font = new DFont(Font.FontFamily, 8f, System.Drawing.FontStyle.Regular)
        };
        popup.Controls.Add(dropHint);

        var upload = new Button
        {
            Text = "UPLOAD",
            Location = new DPoint(18, 476),
            Size = new DSize(110, 38)
        };
        ApplyButtonThemeForCurrentMode(upload);
        popup.Controls.Add(upload);

        var openFolder = new Button
        {
            Text = "FOLDER",
            Location = new DPoint(140, 476),
            Size = new DSize(110, 38)
        };
        openFolder.Click += (_, _) => OpenSkinFolder();
        ApplyButtonThemeForCurrentMode(openFolder);
        popup.Controls.Add(openFolder);

        var recenter = new Button
        {
            Text = "RECENTER",
            Location = new DPoint(528, 382),
            Size = new DSize(98, 34)
        };
        ApplyButtonThemeForCurrentMode(recenter);
        popup.Controls.Add(recenter);

        var zoomOut = new Button
        {
            Text = "-",
            Location = new DPoint(638, 382),
            Size = new DSize(42, 34)
        };
        ApplyButtonThemeForCurrentMode(zoomOut);
        popup.Controls.Add(zoomOut);

        var zoomIn = new Button
        {
            Text = "+",
            Location = new DPoint(688, 382),
            Size = new DSize(42, 34)
        };
        ApplyButtonThemeForCurrentMode(zoomIn);
        popup.Controls.Add(zoomIn);

        var animationButtons = new[]
        {
            new Button { Text = "IDLE", Location = new DPoint(426, 430), Size = new DSize(72, 32) },
            new Button { Text = "WALK", Location = new DPoint(506, 430), Size = new DSize(72, 32) },
            new Button { Text = "RUN", Location = new DPoint(586, 430), Size = new DSize(72, 32) },
            new Button { Text = "FLY", Location = new DPoint(666, 430), Size = new DSize(72, 32) },
            new Button { Text = "CROUCH", Location = new DPoint(426, 470), Size = new DSize(88, 32) },
            new Button { Text = "PUNCH", Location = new DPoint(522, 470), Size = new DSize(88, 32) }
        };
        foreach (var button in animationButtons)
        {
            ApplyButtonThemeForCurrentMode(button);
            popup.Controls.Add(button);
        }

        var previewYaw = 0.42f;
        var previewZoom = 1.0f;
        var previewAnimation = SkinPreviewAnimation.Idle;
        var previewCrouch = false;
        var previewPunchUntil = 0d;
        var previewDragging = false;
        var previewLastMouseX = 0;
        var previewStartUtc = DateTime.UtcNow;

        void RenderPreview()
        {
            var activePath = SkinLibrary.TryGetActivePngPath(out var activeSkinPath) ? activeSkinPath : null;
            var now = (DateTime.UtcNow - previewStartUtc).TotalSeconds;
            var image = CreateSkinModelPreview(
                activePath,
                previewBox.Width,
                previewBox.Height,
                previewYaw,
                previewZoom,
                previewAnimation,
                previewCrouch,
                now < previewPunchUntil,
                layerToggle.Checked,
                (float)now);
            var previous = previewBox.Image;
            previewBox.Image = image;
            previous?.Dispose();
        }

        void SetMovementAnimation(SkinPreviewAnimation animation)
        {
            previewAnimation = animation;
            RenderPreview();
        }

        animationButtons[0].Click += (_, _) => SetMovementAnimation(SkinPreviewAnimation.Idle);
        animationButtons[1].Click += (_, _) => SetMovementAnimation(SkinPreviewAnimation.Walk);
        animationButtons[2].Click += (_, _) => SetMovementAnimation(SkinPreviewAnimation.Run);
        animationButtons[3].Click += (_, _) => SetMovementAnimation(SkinPreviewAnimation.Fly);
        animationButtons[4].Click += (_, _) =>
        {
            previewCrouch = !previewCrouch;
            RenderPreview();
        };
        animationButtons[5].Click += (_, _) =>
        {
            previewPunchUntil = (DateTime.UtcNow - previewStartUtc).TotalSeconds + 0.42d;
            RenderPreview();
        };
        recenter.Click += (_, _) =>
        {
            previewYaw = 0.42f;
            previewZoom = 1.0f;
            RenderPreview();
        };
        zoomOut.Click += (_, _) =>
        {
            previewZoom = Math.Max(0.72f, previewZoom - 0.12f);
            RenderPreview();
        };
        zoomIn.Click += (_, _) =>
        {
            previewZoom = Math.Min(1.45f, previewZoom + 0.12f);
            RenderPreview();
        };
        layerToggle.CheckedChanged += (_, _) => RenderPreview();
        previewBox.MouseDown += (_, e) =>
        {
            if (e.Button != MouseButtons.Left)
                return;
            previewDragging = true;
            previewLastMouseX = e.X;
        };
        previewBox.MouseMove += (_, e) =>
        {
            if (!previewDragging)
                return;
            previewYaw += (e.X - previewLastMouseX) * 0.014f;
            previewLastMouseX = e.X;
            RenderPreview();
        };
        previewBox.MouseUp += (_, _) => previewDragging = false;
        previewBox.MouseEnter += (_, _) => previewBox.Focus();
        previewBox.MouseWheel += (_, e) =>
        {
            previewZoom = Math.Clamp(previewZoom + Math.Sign(e.Delta) * 0.08f, 0.72f, 1.45f);
            RenderPreview();
        };

        using var previewTimer = new System.Windows.Forms.Timer { Interval = 33 };
        previewTimer.Tick += (_, _) =>
        {
            if (!popup.Visible)
                return;
            if (!previewDragging && previewAnimation == SkinPreviewAnimation.Idle && (DateTime.UtcNow - previewStartUtc).TotalSeconds >= previewPunchUntil)
                previewYaw += 0.006f;
            RenderPreview();
        };
        previewTimer.Start();
        void RefreshPopup()
        {
            libraryPanel.Controls.Clear();
            var entries = SkinLibrary.ListSkins();
            var defaultIsActive = string.IsNullOrWhiteSpace(SkinLibrary.ReadActiveHash());
            RenderPreview();

            if (defaultIsActive)
                libraryPanel.Controls.Add(CreateDefaultSkinLibraryRow(isActive: true, RefreshPopup));

            foreach (var entry in entries.Where(entry => entry.IsActive))
                libraryPanel.Controls.Add(CreateSkinLibraryRow(entry, RefreshPopup));

            if (!defaultIsActive)
                libraryPanel.Controls.Add(CreateDefaultSkinLibraryRow(isActive: false, RefreshPopup));

            if (entries.Count == 0)
            {
                libraryPanel.Controls.Add(new Label
                {
                    Text = "No custom skins yet. Upload a 64x64 PNG.",
                    AutoSize = false,
                    Size = new DSize(330, 42),
                    ForeColor = popup.ForeColor,
                    BackColor = popup.BackColor
                });
                return;
            }

            foreach (var entry in entries.Where(entry => !entry.IsActive))
                libraryPanel.Controls.Add(CreateSkinLibraryRow(entry, RefreshPopup));
        }
        Action popupRefresh = RefreshPopup;

        popup.FormClosed += (_, _) =>
        {
            if (ReferenceEquals(_skinManagerPopupRefresh, popupRefresh))
                _skinManagerPopupRefresh = null;
            previewTimer.Stop();
            previewBox.Image?.Dispose();
            previewBox.Image = null;
        };

        void ImportSkinFromPath(string path, string source)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                    throw new FileNotFoundException("Skin PNG was not found.", path);

                if (!string.Equals(Path.GetExtension(path), ".png", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Skin must be a PNG file.");

                var imported = SkinLibrary.ImportPng(path);
                SkinLibrary.SetActive(imported.Hash);
                _log.Info($"Skin imported and activated via {source}: name={imported.DisplayName}, hash={ShortLogId(imported.Hash)}");
                RefreshLauncherSkinPreview();
                QueueCloudSkinUpload($"import:{source}");
                RefreshPopup();
            }
            catch (Exception ex)
            {
                _log.Warn($"Skin import failed via {source}: {ex.Message}");
                MessageBox.Show(this, ex.Message, "Skin Import Failed", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        void HandleSkinDragEnter(object? sender, DragEventArgs e)
        {
            e.Effect = DragDropEffects.None;
            if (e.Data == null || !e.Data.GetDataPresent(DataFormats.FileDrop))
                return;

            if (e.Data.GetData(DataFormats.FileDrop) is not string[] files || files.Length != 1)
                return;

            if (string.Equals(Path.GetExtension(files[0]), ".png", StringComparison.OrdinalIgnoreCase))
                e.Effect = DragDropEffects.Copy;
        }

        void HandleSkinDragDrop(object? sender, DragEventArgs e)
        {
            if (e.Data == null || !e.Data.GetDataPresent(DataFormats.FileDrop))
                return;

            if (e.Data.GetData(DataFormats.FileDrop) is not string[] files || files.Length == 0)
                return;

            if (files.Length > 1)
            {
                MessageBox.Show(this, "Drop one skin PNG at a time.", "Skin Import Failed", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            ImportSkinFromPath(files[0], "drag-drop");
        }

        popup.DragEnter += HandleSkinDragEnter;
        popup.DragDrop += HandleSkinDragDrop;
        libraryPanel.AllowDrop = true;
        libraryPanel.DragEnter += HandleSkinDragEnter;
        libraryPanel.DragDrop += HandleSkinDragDrop;
        previewBox.AllowDrop = true;
        previewBox.DragEnter += HandleSkinDragEnter;
        previewBox.DragDrop += HandleSkinDragDrop;

        upload.Click += (_, _) =>
        {
            using var dialog = new OpenFileDialog
            {
                Title = "Upload LatticeVeil skin",
                Filter = "PNG skin (*.png)|*.png",
                Multiselect = false
            };

            if (dialog.ShowDialog(this) != DialogResult.OK)
                return;

            ImportSkinFromPath(dialog.FileName, "file-dialog");
        };

        QueueCloudSkinFetchAndApply("skins popup opened", RefreshPopup);
        RefreshPopup();
        _skinManagerPopupRefresh = popupRefresh;
        popup.ShowDialog(this);
        if (ReferenceEquals(_skinManagerPopupRefresh, popupRefresh))
            _skinManagerPopupRefresh = null;
        RefreshLauncherSkinPreview();
    }

    private Control CreateSkinLibraryRow(SkinLibraryEntry entry, Action refresh)
    {
        var row = new Panel
        {
            Width = 330,
            Height = 86,
            Margin = new Padding(0, 0, 0, 8),
            BackColor = _settings.DarkMode ? DColor.FromArgb(18, 18, 18) : DColor.FromArgb(240, 240, 240)
        };

        var thumb = new PictureBox
        {
            Location = new DPoint(8, 8),
            Size = new DSize(64, 64),
            SizeMode = PictureBoxSizeMode.Zoom,
            Image = CreateSkinHeadPreview(entry.PngPath, 64),
            BorderStyle = BorderStyle.FixedSingle
        };
        row.Controls.Add(thumb);

        var nameLabel = new Label
        {
            Text = string.IsNullOrWhiteSpace(entry.DisplayName) ? entry.Hash.Substring(0, 12).ToUpperInvariant() : entry.DisplayName,
            Location = new DPoint(82, 8),
            Size = new DSize(126, 20),
            ForeColor = ForeColor,
            BackColor = row.BackColor,
            Font = new DFont(Font.FontFamily, 8.5f, System.Drawing.FontStyle.Bold),
            AutoEllipsis = true
        };
        row.Controls.Add(nameLabel);

        var hashLabel = new Label
        {
            Text = entry.Hash.Substring(0, 12).ToUpperInvariant(),
            Location = new DPoint(82, 28),
            Size = new DSize(126, 16),
            ForeColor = _settings.DarkMode ? DColor.FromArgb(170, 170, 170) : DColor.FromArgb(80, 80, 80),
            BackColor = row.BackColor,
            Font = new DFont(Font.FontFamily, 7.5f, System.Drawing.FontStyle.Regular),
            AutoEllipsis = true
        };
        row.Controls.Add(hashLabel);

        var activeLabel = new Label
        {
            Text = entry.IsActive ? "ACTIVE" : entry.HasLayers ? "LAYERED" : "",
            Location = new DPoint(82, 50),
            Size = new DSize(90, 20),
            ForeColor = entry.IsActive ? DColor.LimeGreen : DColor.FromArgb(76, 175, 255),
            BackColor = row.BackColor,
            Font = new DFont(Font.FontFamily, 9f, System.Drawing.FontStyle.Bold)
        };
        row.Controls.Add(activeLabel);

        var use = new Button
        {
            Text = entry.IsActive ? "ACTIVE" : "USE",
            Location = new DPoint(214, 8),
            Size = new DSize(96, 30),
            Enabled = !entry.IsActive
        };
        use.Click += (_, _) =>
        {
            if (SkinLibrary.SetActive(entry.Hash))
            {
                _log.Info($"Active skin changed: hash={ShortLogId(entry.Hash)}");
                RefreshLauncherSkinPreview();
                QueueCloudSkinUpload("skin-selected");
                refresh();
            }
        };
        ApplyButtonThemeForCurrentMode(use);
        row.Controls.Add(use);

        var remove = new Button
        {
            Text = "REMOVE",
            Location = new DPoint(214, 46),
            Size = new DSize(96, 30)
        };
        remove.Click += (_, _) =>
        {
            var confirm = MessageBox.Show(this, "Remove this local skin?", "Remove Skin", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            if (confirm != DialogResult.Yes)
                return;

            if (!SkinLibrary.Delete(entry.Hash, out var error))
            {
                MessageBox.Show(this, error ?? "Unable to remove skin.", "Remove Skin Failed", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            _log.Info($"Skin removed: hash={ShortLogId(entry.Hash)}");
            RefreshLauncherSkinPreview();
            refresh();
        };
        ApplyButtonThemeForCurrentMode(remove);
        row.Controls.Add(remove);

        row.Disposed += (_, _) =>
        {
            try { thumb.Image?.Dispose(); }
            catch { }
        };

        return row;
    }

    private Control CreateDefaultSkinLibraryRow(bool isActive, Action refresh)
    {
        var row = new Panel
        {
            Width = 330,
            Height = 86,
            Margin = new Padding(0, 0, 0, 8),
            BackColor = _settings.DarkMode ? DColor.FromArgb(18, 18, 18) : DColor.FromArgb(240, 240, 240)
        };

        var thumb = new PictureBox
        {
            Location = new DPoint(8, 8),
            Size = new DSize(64, 64),
            SizeMode = PictureBoxSizeMode.Zoom,
            Image = CreateSkinHeadPreview(null, 64),
            BorderStyle = BorderStyle.FixedSingle
        };
        row.Controls.Add(thumb);

        var nameLabel = new Label
        {
            Text = "DEFAULT SKIN",
            Location = new DPoint(82, 8),
            Size = new DSize(130, 20),
            ForeColor = ForeColor,
            BackColor = row.BackColor,
            Font = new DFont(Font.FontFamily, 8.5f, System.Drawing.FontStyle.Bold)
        };
        row.Controls.Add(nameLabel);

        var activeLabel = new Label
        {
            Text = isActive ? "ACTIVE" : "",
            Location = new DPoint(82, 32),
            Size = new DSize(90, 20),
            ForeColor = DColor.LimeGreen,
            BackColor = row.BackColor,
            Font = new DFont(Font.FontFamily, 9f, System.Drawing.FontStyle.Bold)
        };
        row.Controls.Add(activeLabel);

        var use = new Button
        {
            Text = isActive ? "ACTIVE" : "USE DEFAULT",
            Location = new DPoint(190, 24),
            Size = new DSize(120, 34),
            Enabled = !isActive
        };
        use.Click += (_, _) =>
        {
            if (SkinLibrary.ClearActive())
            {
                _log.Info("Active skin changed: default baked skin");
                RefreshLauncherSkinPreview();
                QueueCloudSkinUpload("default-selected");
                refresh();
            }
        };
        ApplyButtonThemeForCurrentMode(use);
        row.Controls.Add(use);

        row.Disposed += (_, _) =>
        {
            try { thumb.Image?.Dispose(); }
            catch { }
        };

        return row;
    }

    private void ApplyButtonThemeForCurrentMode(Button button)
    {
        if (_settings.DarkMode)
        {
            ApplyButtonTheme(
                button,
                DColor.FromArgb(38, 38, 38),
                DColor.White,
                DColor.FromArgb(90, 90, 90),
                DColor.FromArgb(52, 52, 52),
                DColor.FromArgb(30, 30, 30));
            return;
        }

        ApplyButtonTheme(
            button,
            System.Drawing.SystemColors.ControlLight,
            System.Drawing.SystemColors.ControlText,
            System.Drawing.SystemColors.ControlDark,
            System.Drawing.SystemColors.Control,
            System.Drawing.SystemColors.ControlDark);
    }

    private void OpenSkinFolder()
    {
        try
        {
            Directory.CreateDirectory(SkinLibrary.SkinsDir);
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"\"{SkinLibrary.SkinsDir}\"",
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            _log.Warn($"Failed to open skin folder: {ex.Message}");
        }
    }

    private enum SkinPreviewAnimation
    {
        Idle,
        Walk,
        Run,
        Fly
    }

    private DImage CreateSkinModelPreview(
        string? skinPath,
        int width,
        int height,
        float yaw,
        float zoom,
        SkinPreviewAnimation animation,
        bool crouch,
        bool punch,
        bool showLayers,
        float timeSeconds)
    {
        var bitmap = new Bitmap(Math.Max(1, width), Math.Max(1, height), PixelFormat.Format32bppArgb);
        using var skin = LoadPreviewSkinBitmap(skinPath);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.InterpolationMode = InterpolationMode.NearestNeighbor;
        graphics.PixelOffsetMode = PixelOffsetMode.Half;
        graphics.Clear(_settings.DarkMode ? DColor.FromArgb(18, 20, 24) : DColor.FromArgb(238, 242, 246));

        using (var gridPen = new Pen(_settings.DarkMode ? DColor.FromArgb(34, 40, 48) : DColor.FromArgb(210, 218, 226), 1f))
        {
            for (var x = 0; x < width; x += 24)
                graphics.DrawLine(gridPen, x, height - 44, width / 2, height - 12);
            for (var x = width; x >= 0; x -= 24)
                graphics.DrawLine(gridPen, x, height - 44, width / 2, height - 12);
        }

        using (var shadowBrush = new SolidBrush(_settings.DarkMode ? DColor.FromArgb(90, 0, 0, 0) : DColor.FromArgb(45, 60, 75, 90)))
            graphics.FillEllipse(shadowBrush, width / 2 - 58 * zoom, height - 58, 116 * zoom, 28);

        var faces = new List<SkinPreviewFace>();
        var fly = animation == SkinPreviewAnimation.Fly;
        var isSprinting = animation == SkinPreviewAnimation.Run;
        var walkAmount = animation == SkinPreviewAnimation.Idle || fly ? 0f : isSprinting ? 1.12f : 0.72f;
        var moveCycle = timeSeconds * (1.6f + (isSprinting ? 8.8f : 6.0f) * walkAmount);
        var legSwing = MathF.Sin(moveCycle) * (0.90f * walkAmount);
        var armSwing = MathF.Sin(moveCycle + MathF.PI) * (0.86f * walkAmount);
        var torsoSway = MathF.Sin(moveCycle * 1.6f) * (0.03f * walkAmount);
        var bodyBob = fly
            ? MathF.Sin(timeSeconds * 2.6f) * 0.06f
            : MathF.Sin(moveCycle * 2f) * (0.03f * walkAmount);
        var hoverSway = MathF.Sin(timeSeconds * 3.3f) * 0.18f;
        var punchBlend = punch ? MathF.Sin(Math.Clamp(((timeSeconds * 2.35f) % 1f), 0f, 1f) * MathF.PI) : 0f;
        var punchAmount = punchBlend * 1.20f;
        var crouchOffset = crouch ? -0.18f : 0f;
        var upperBodyBob = fly ? 0f : bodyBob;
        var rootYaw = MathF.PI / 2f - yaw;
        var rootPosition = new System.Numerics.Vector3(0f, fly ? bodyBob : 0f, 0f);
        var sneakForwardShift = crouch ? 0.05f : 0f;
        var previewPitch = fly ? -0.22f : 0f;
        var previewHeadYaw = MathF.Sin(moveCycle * 0.85f) * (0.04f * walkAmount);

        AddSkinPreviewPart(faces, skin, zoom, width, height, rootPosition, rootYaw, new(0f, 1.07f + crouchOffset + upperBodyBob, sneakForwardShift), new(0.52f, 0.70f, 0.30f), new(fly ? -0.36f + hoverSway * 0.22f : (crouch ? 0.18f : 0f) + (isSprinting ? 0.08f : 0f), 0f, torsoSway), BodyBasePreviewMap, BodyOverlayPreviewMap, showLayers, new(0f, 0f, 0f), SkinPreviewLayer.Body);
        AddSkinPreviewPart(faces, skin, zoom, width, height, rootPosition, rootYaw, new(0f, 1.59f + crouchOffset + upperBodyBob, sneakForwardShift * 0.7f), new(0.42f, 0.42f, 0.42f), new(-previewPitch, previewHeadYaw, fly ? hoverSway * 0.06f : torsoSway * 0.5f), HeadBasePreviewMap, HeadOverlayPreviewMap, showLayers, new(0f, -0.21f + 0.03f, 0f), SkinPreviewLayer.Head);

        var armBaseY = 1.06f + crouchOffset + upperBodyBob;
        var leftArmRot = new System.Numerics.Vector3(
            fly ? -0.34f + hoverSway * 0.45f : (armSwing * 0.78f) - (punchAmount * 1.18f),
            0f,
            fly ? -0.26f : 0.06f + punchAmount * 0.22f);
        var rightArmRot = new System.Numerics.Vector3(
            fly ? -0.34f - hoverSway * 0.45f : -armSwing * 0.58f,
            0f,
            fly ? 0.26f : -0.08f);
        AddSkinPreviewPart(faces, skin, zoom, width, height, rootPosition, rootYaw, new(-0.38f, armBaseY, sneakForwardShift), new(0.24f, 0.72f, 0.24f), leftArmRot, LeftArmBasePreviewMap, LeftArmOverlayPreviewMap, showLayers, new(0f, 0.36f - 0.02f, 0f), SkinPreviewLayer.Limb);
        AddSkinPreviewPart(faces, skin, zoom, width, height, rootPosition, rootYaw, new(0.38f, armBaseY, sneakForwardShift), new(0.24f, 0.72f, 0.24f), rightArmRot, RightArmBasePreviewMap, RightArmOverlayPreviewMap, showLayers, new(0f, 0.36f - 0.02f, 0f), SkinPreviewLayer.Limb);

        var legBaseY = 0.36f + (fly ? 0.03f : 0f);
        var sneakLegBend = crouch ? 0.08f : 0f;
        var leftLegRot = new System.Numerics.Vector3(fly ? 0.20f + hoverSway * 0.24f : (legSwing * 0.90f) + sneakLegBend, 0f, fly ? -0.06f : (crouch ? 0.05f : 0f));
        var rightLegRot = new System.Numerics.Vector3(fly ? 0.20f - hoverSway * 0.24f : (-legSwing * 0.90f) + sneakLegBend, 0f, fly ? 0.06f : (crouch ? -0.05f : 0f));
        var legOffsetX = fly ? 0.17f : 0.13f;
        AddSkinPreviewPart(faces, skin, zoom, width, height, rootPosition, rootYaw, new(-legOffsetX, legBaseY, 0f), new(0.22f, 0.72f, 0.24f), leftLegRot, LeftLegBasePreviewMap, LeftLegOverlayPreviewMap, showLayers, new(0f, 0.36f - 0.01f, 0f), SkinPreviewLayer.Limb);
        AddSkinPreviewPart(faces, skin, zoom, width, height, rootPosition, rootYaw, new(legOffsetX, legBaseY, 0f), new(0.22f, 0.72f, 0.24f), rightLegRot, RightLegBasePreviewMap, RightLegOverlayPreviewMap, showLayers, new(0f, 0.36f - 0.01f, 0f), SkinPreviewLayer.Limb);

        foreach (var face in faces.OrderByDescending(face => face.Depth))
            graphics.DrawImage(skin, face.TexturePoints, face.SourceRect, GraphicsUnit.Pixel);

        return bitmap;
    }

    private Bitmap LoadPreviewSkinBitmap(string? skinPath)
    {
        var bitmap = CreateBakedDefaultSkinBitmap();
        if (string.IsNullOrWhiteSpace(skinPath) || !File.Exists(skinPath))
            return bitmap;

        try
        {
            using var overlay = new Bitmap(skinPath);
            if (overlay.Width != 64 || (overlay.Height != 64 && overlay.Height != 32))
                return bitmap;

            using var graphics = Graphics.FromImage(bitmap);
            graphics.InterpolationMode = InterpolationMode.NearestNeighbor;
            graphics.PixelOffsetMode = PixelOffsetMode.Half;
            graphics.DrawImage(
                overlay,
                new System.Drawing.Rectangle(0, 0, overlay.Width, overlay.Height),
                new System.Drawing.Rectangle(0, 0, overlay.Width, overlay.Height),
                GraphicsUnit.Pixel);
        }
        catch (Exception ex)
        {
            _log.Warn($"Failed to load 3D skin preview texture: {ex.Message}");
        }

        return bitmap;
    }

    private void AddSkinPreviewPart(
        List<SkinPreviewFace> faces,
        Bitmap skin,
        float zoom,
        int width,
        int height,
        System.Numerics.Vector3 rootPosition,
        float rootYaw,
        System.Numerics.Vector3 center,
        System.Numerics.Vector3 size,
        System.Numerics.Vector3 rotation,
        SkinPreviewBoxMap baseMap,
        SkinPreviewBoxMap overlayMap,
        bool showLayers,
        System.Numerics.Vector3 pivot,
        SkinPreviewLayer layer)
    {
        AddSkinPreviewBox(faces, skin, zoom, width, height, rootPosition, rootYaw, center, size, rotation, baseMap, overlay: false, pivot, layer);
        if (showLayers)
            AddSkinPreviewBox(faces, skin, zoom, width, height, rootPosition, rootYaw, center, size + new System.Numerics.Vector3(0.026f), rotation, overlayMap, overlay: true, pivot, layer);
    }

    private void AddSkinPreviewBox(
        List<SkinPreviewFace> faces,
        Bitmap skin,
        float zoom,
        int width,
        int height,
        System.Numerics.Vector3 rootPosition,
        float rootYaw,
        System.Numerics.Vector3 center,
        System.Numerics.Vector3 size,
        System.Numerics.Vector3 rotation,
        SkinPreviewBoxMap map,
        bool overlay,
        System.Numerics.Vector3 pivot,
        SkinPreviewLayer layer)
    {
        var half = size * 0.5f;
        var vertices = new[]
        {
            new System.Numerics.Vector3(-half.X, -half.Y, -half.Z),
            new System.Numerics.Vector3( half.X, -half.Y, -half.Z),
            new System.Numerics.Vector3( half.X,  half.Y, -half.Z),
            new System.Numerics.Vector3(-half.X,  half.Y, -half.Z),
            new System.Numerics.Vector3(-half.X, -half.Y,  half.Z),
            new System.Numerics.Vector3( half.X, -half.Y,  half.Z),
            new System.Numerics.Vector3( half.X,  half.Y,  half.Z),
            new System.Numerics.Vector3(-half.X,  half.Y,  half.Z)
        };

        for (var i = 0; i < vertices.Length; i++)
            vertices[i] = TransformSkinPreviewPoint(vertices[i], rootPosition, rootYaw, center, rotation, pivot);

        AddSkinPreviewFace(faces, skin, overlay, width, height, zoom, vertices, new[] { 4, 5, 6, 7 }, TransformSkinPreviewNormal(new(0f, 0f, 1f), rootYaw, rotation), map.Front, layer);
        AddSkinPreviewFace(faces, skin, overlay, width, height, zoom, vertices, new[] { 1, 0, 3, 2 }, TransformSkinPreviewNormal(new(0f, 0f, -1f), rootYaw, rotation), map.Back, layer);
        AddSkinPreviewFace(faces, skin, overlay, width, height, zoom, vertices, new[] { 0, 4, 7, 3 }, TransformSkinPreviewNormal(new(-1f, 0f, 0f), rootYaw, rotation), map.Left, layer);
        AddSkinPreviewFace(faces, skin, overlay, width, height, zoom, vertices, new[] { 5, 1, 2, 6 }, TransformSkinPreviewNormal(new(1f, 0f, 0f), rootYaw, rotation), map.Right, layer);
        AddSkinPreviewFace(faces, skin, overlay, width, height, zoom, vertices, new[] { 7, 6, 2, 3 }, TransformSkinPreviewNormal(new(0f, 1f, 0f), rootYaw, rotation), map.Top, layer);
        AddSkinPreviewFace(faces, skin, overlay, width, height, zoom, vertices, new[] { 0, 1, 5, 4 }, TransformSkinPreviewNormal(new(0f, -1f, 0f), rootYaw, rotation), map.Bottom, layer);
    }

    private static System.Numerics.Vector3 TransformSkinPreviewPoint(System.Numerics.Vector3 point, System.Numerics.Vector3 rootPosition, float rootYaw, System.Numerics.Vector3 center, System.Numerics.Vector3 rotation, System.Numerics.Vector3 pivot)
    {
        point -= pivot;
        point = RotateSkinPreview(point, rotation.X, rotation.Y, rotation.Z);
        point += pivot;
        point += center;
        point = RotateSkinPreview(point, 0f, rootYaw, 0f);
        point += rootPosition;
        point = RotateSkinPreview(point, -0.20f, 0f, 0f);
        return point;
    }

    private static System.Numerics.Vector3 TransformSkinPreviewNormal(System.Numerics.Vector3 normal, float rootYaw, System.Numerics.Vector3 rotation)
    {
        normal = RotateSkinPreview(normal, rotation.X, rotation.Y, rotation.Z);
        normal = RotateSkinPreview(normal, 0f, rootYaw, 0f);
        normal = RotateSkinPreview(normal, -0.20f, 0f, 0f);
        return normal;
    }

    private static System.Numerics.Vector3 RotateSkinPreview(System.Numerics.Vector3 point, float x, float y, float z)
    {
        if (x != 0f)
        {
            var c = MathF.Cos(x);
            var s = MathF.Sin(x);
            point = new System.Numerics.Vector3(point.X, point.Y * c - point.Z * s, point.Y * s + point.Z * c);
        }
        if (y != 0f)
        {
            var c = MathF.Cos(y);
            var s = MathF.Sin(y);
            point = new System.Numerics.Vector3(point.X * c + point.Z * s, point.Y, -point.X * s + point.Z * c);
        }
        if (z != 0f)
        {
            var c = MathF.Cos(z);
            var s = MathF.Sin(z);
            point = new System.Numerics.Vector3(point.X * c - point.Y * s, point.X * s + point.Y * c, point.Z);
        }

        return point;
    }

    private void AddSkinPreviewFace(
        List<SkinPreviewFace> faces,
        Bitmap skin,
        bool overlay,
        int width,
        int height,
        float zoom,
        System.Numerics.Vector3[] vertices,
        int[] indices,
        System.Numerics.Vector3 normal,
        SkinPreviewRect rect,
        SkinPreviewLayer layer)
    {
        if (normal.Z > 0.35f)
            return;
        if (overlay && !HasVisiblePreviewSkinPixels(skin, rect))
            return;

        var scale = 122f * zoom;
        var points = new System.Drawing.PointF[indices.Length];
        var depth = 0f;
        for (var i = 0; i < indices.Length; i++)
        {
            var vertex = vertices[indices[i]];
            points[i] = new System.Drawing.PointF(
                width * 0.5f + vertex.X * scale,
                height * 0.78f - vertex.Y * scale + vertex.Z * scale * 0.08f);
            depth += vertex.Z;
        }

        var texturePoints = new[] { points[3], points[2], points[0] };
        faces.Add(new SkinPreviewFace(points, texturePoints, depth / indices.Length + ((float)layer * 0.0001f), rect.ToRectangle()));
    }

    private static bool HasVisiblePreviewSkinPixels(Bitmap skin, SkinPreviewRect rect)
    {
        var xMax = Math.Min(skin.Width, rect.X + rect.Width);
        var yMax = Math.Min(skin.Height, rect.Y + rect.Height);
        for (var y = Math.Max(0, rect.Y); y < yMax; y++)
        {
            for (var x = Math.Max(0, rect.X); x < xMax; x++)
            {
                if (skin.GetPixel(x, y).A > 8)
                    return true;
            }
        }

        return false;
    }

    private readonly struct SkinPreviewFace
    {
        public SkinPreviewFace(System.Drawing.PointF[] points, System.Drawing.PointF[] texturePoints, float depth, System.Drawing.Rectangle sourceRect)
        {
            Points = points;
            TexturePoints = texturePoints;
            Depth = depth;
            SourceRect = sourceRect;
        }

        public System.Drawing.PointF[] Points { get; }
        public System.Drawing.PointF[] TexturePoints { get; }
        public float Depth { get; }
        public System.Drawing.Rectangle SourceRect { get; }
    }

    private enum SkinPreviewLayer
    {
        Body,
        Head,
        Limb
    }

    private readonly struct SkinPreviewRect
    {
        public SkinPreviewRect(int x, int y, int width, int height)
        {
            X = x;
            Y = y;
            Width = width;
            Height = height;
        }

        public int X { get; }
        public int Y { get; }
        public int Width { get; }
        public int Height { get; }

        public System.Drawing.Rectangle ToRectangle() => new(X, Y, Width, Height);
    }

    private readonly struct SkinPreviewBoxMap
    {
        public SkinPreviewBoxMap(SkinPreviewRect top, SkinPreviewRect bottom, SkinPreviewRect left, SkinPreviewRect front, SkinPreviewRect right, SkinPreviewRect back)
        {
            Top = top;
            Bottom = bottom;
            Left = left;
            Front = front;
            Right = right;
            Back = back;
        }

        public SkinPreviewRect Top { get; }
        public SkinPreviewRect Bottom { get; }
        public SkinPreviewRect Left { get; }
        public SkinPreviewRect Front { get; }
        public SkinPreviewRect Right { get; }
        public SkinPreviewRect Back { get; }
    }

    private static readonly SkinPreviewBoxMap HeadBasePreviewMap = new(new(8, 0, 8, 8), new(16, 0, 8, 8), new(0, 8, 8, 8), new(8, 8, 8, 8), new(16, 8, 8, 8), new(24, 8, 8, 8));
    private static readonly SkinPreviewBoxMap HeadOverlayPreviewMap = new(new(40, 0, 8, 8), new(48, 0, 8, 8), new(32, 8, 8, 8), new(40, 8, 8, 8), new(48, 8, 8, 8), new(56, 8, 8, 8));
    private static readonly SkinPreviewBoxMap BodyBasePreviewMap = new(new(20, 16, 8, 4), new(28, 16, 8, 4), new(16, 20, 4, 12), new(20, 20, 8, 12), new(28, 20, 4, 12), new(32, 20, 8, 12));
    private static readonly SkinPreviewBoxMap BodyOverlayPreviewMap = new(new(20, 32, 8, 4), new(28, 32, 8, 4), new(16, 36, 4, 12), new(20, 36, 8, 12), new(28, 36, 4, 12), new(32, 36, 8, 12));
    private static readonly SkinPreviewBoxMap RightArmBasePreviewMap = new(new(44, 16, 4, 4), new(48, 16, 4, 4), new(40, 20, 4, 12), new(44, 20, 4, 12), new(48, 20, 4, 12), new(52, 20, 4, 12));
    private static readonly SkinPreviewBoxMap RightArmOverlayPreviewMap = new(new(44, 32, 4, 4), new(48, 32, 4, 4), new(40, 36, 4, 12), new(44, 36, 4, 12), new(48, 36, 4, 12), new(52, 36, 4, 12));
    private static readonly SkinPreviewBoxMap LeftArmBasePreviewMap = new(new(36, 48, 4, 4), new(40, 48, 4, 4), new(32, 52, 4, 12), new(36, 52, 4, 12), new(40, 52, 4, 12), new(44, 52, 4, 12));
    private static readonly SkinPreviewBoxMap LeftArmOverlayPreviewMap = new(new(52, 48, 4, 4), new(56, 48, 4, 4), new(48, 52, 4, 12), new(52, 52, 4, 12), new(56, 52, 4, 12), new(60, 52, 4, 12));
    private static readonly SkinPreviewBoxMap RightLegBasePreviewMap = new(new(4, 16, 4, 4), new(8, 16, 4, 4), new(0, 20, 4, 12), new(4, 20, 4, 12), new(8, 20, 4, 12), new(12, 20, 4, 12));
    private static readonly SkinPreviewBoxMap RightLegOverlayPreviewMap = new(new(4, 32, 4, 4), new(8, 32, 4, 4), new(0, 36, 4, 12), new(4, 36, 4, 12), new(8, 36, 4, 12), new(12, 36, 4, 12));
    private static readonly SkinPreviewBoxMap LeftLegBasePreviewMap = new(new(20, 48, 4, 4), new(24, 48, 4, 4), new(16, 52, 4, 12), new(20, 52, 4, 12), new(24, 52, 4, 12), new(28, 52, 4, 12));
    private static readonly SkinPreviewBoxMap LeftLegOverlayPreviewMap = new(new(4, 48, 4, 4), new(8, 48, 4, 4), new(0, 52, 4, 12), new(4, 52, 4, 12), new(8, 52, 4, 12), new(12, 52, 4, 12));

    private DImage CreateSkinHeadPreview(string? skinPath, int size)
    {
        var bitmap = new Bitmap(size, size);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.InterpolationMode = InterpolationMode.NearestNeighbor;
        graphics.PixelOffsetMode = PixelOffsetMode.Half;
        graphics.Clear(DColor.Transparent);

        try
        {
            using var defaultSkin = CreateBakedDefaultSkinBitmap();
            var head = new System.Drawing.Rectangle(8, 8, 8, 8);
            var headOverlay = new System.Drawing.Rectangle(40, 8, 8, 8);
            var dst = new System.Drawing.Rectangle(0, 0, size, size);
            graphics.DrawImage(defaultSkin, dst, head, GraphicsUnit.Pixel);
            graphics.DrawImage(defaultSkin, dst, headOverlay, GraphicsUnit.Pixel);

            if (!string.IsNullOrWhiteSpace(skinPath) && File.Exists(skinPath))
            {
                using var source = new Bitmap(skinPath);
                graphics.DrawImage(source, dst, head, GraphicsUnit.Pixel);
                graphics.DrawImage(source, dst, headOverlay, GraphicsUnit.Pixel);
            }

            return bitmap;
        }
        catch (Exception ex)
        {
            _log.Warn($"Failed to render skin head preview: {ex.Message}");
        }

        using var fallbackBrush = new SolidBrush(DColor.FromArgb(90, 112, 136));
        graphics.FillRectangle(fallbackBrush, 0, 0, size, size);
        using var borderPen = new Pen(DColor.White, Math.Max(1, size / 32));
        graphics.DrawRectangle(borderPen, 1, 1, Math.Max(0, size - 2), Math.Max(0, size - 2));
        return bitmap;
    }

    private static Bitmap CreateBakedDefaultSkinBitmap()
    {
        var bitmap = new Bitmap(64, 64, PixelFormat.Format32bppArgb);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.Clear(DColor.FromArgb(90, 112, 136));

        var skin = DColor.FromArgb(232, 200, 172);
        var hair = DColor.FromArgb(96, 74, 54);
        var shirt = DColor.FromArgb(98, 158, 196);
        var shirtShade = DColor.FromArgb(74, 126, 166);
        var pants = DColor.FromArgb(64, 72, 86);
        var shoe = DColor.FromArgb(38, 42, 50);
        var eyeWhite = DColor.FromArgb(225, 232, 240);
        var eyePupil = DColor.FromArgb(44, 54, 66);
        var mouth = DColor.FromArgb(166, 100, 98);

        FillDefaultSkin(graphics, 8, 0, 8, 8, hair); FillDefaultSkin(graphics, 16, 0, 8, 8, skin);
        FillDefaultSkin(graphics, 0, 8, 8, 8, skin); FillDefaultSkin(graphics, 8, 8, 8, 8, skin);
        FillDefaultSkin(graphics, 16, 8, 8, 8, skin); FillDefaultSkin(graphics, 24, 8, 8, 8, skin);
        FillDefaultSkin(graphics, 8, 0, 8, 2, hair); FillDefaultSkin(graphics, 8, 8, 8, 2, hair);
        FillDefaultSkin(graphics, 0, 8, 8, 2, hair); FillDefaultSkin(graphics, 16, 8, 8, 2, hair); FillDefaultSkin(graphics, 24, 8, 8, 2, hair);
        DotDefaultSkin(bitmap, 10, 11, eyeWhite); DotDefaultSkin(bitmap, 13, 11, eyeWhite);
        DotDefaultSkin(bitmap, 10, 11, eyePupil); DotDefaultSkin(bitmap, 13, 11, eyePupil);
        DotDefaultSkin(bitmap, 11, 13, mouth); DotDefaultSkin(bitmap, 12, 13, mouth);

        FillDefaultSkin(graphics, 20, 16, 8, 4, shirt); FillDefaultSkin(graphics, 28, 16, 8, 4, shirt);
        FillDefaultSkin(graphics, 16, 20, 4, 12, shirtShade); FillDefaultSkin(graphics, 20, 20, 8, 12, shirt);
        FillDefaultSkin(graphics, 28, 20, 4, 12, shirtShade); FillDefaultSkin(graphics, 32, 20, 8, 12, shirtShade);
        FillDefaultSkin(graphics, 20, 20, 8, 2, shirtShade);

        FillDefaultSkin(graphics, 44, 16, 4, 4, shirt); FillDefaultSkin(graphics, 48, 16, 4, 4, skin);
        FillDefaultSkin(graphics, 40, 20, 4, 8, shirtShade); FillDefaultSkin(graphics, 44, 20, 4, 8, shirt);
        FillDefaultSkin(graphics, 48, 20, 4, 8, shirtShade); FillDefaultSkin(graphics, 52, 20, 4, 8, shirtShade);
        FillDefaultSkin(graphics, 40, 28, 4, 4, skin); FillDefaultSkin(graphics, 44, 28, 4, 4, skin);
        FillDefaultSkin(graphics, 48, 28, 4, 4, skin); FillDefaultSkin(graphics, 52, 28, 4, 4, skin);

        FillDefaultSkin(graphics, 36, 48, 4, 4, shirt); FillDefaultSkin(graphics, 40, 48, 4, 4, skin);
        FillDefaultSkin(graphics, 32, 52, 4, 8, shirtShade); FillDefaultSkin(graphics, 36, 52, 4, 8, shirt);
        FillDefaultSkin(graphics, 40, 52, 4, 8, shirtShade); FillDefaultSkin(graphics, 44, 52, 4, 8, shirtShade);
        FillDefaultSkin(graphics, 32, 60, 4, 4, skin); FillDefaultSkin(graphics, 36, 60, 4, 4, skin);
        FillDefaultSkin(graphics, 40, 60, 4, 4, skin); FillDefaultSkin(graphics, 44, 60, 4, 4, skin);

        FillDefaultSkin(graphics, 4, 16, 4, 4, pants); FillDefaultSkin(graphics, 8, 16, 4, 4, pants);
        FillDefaultSkin(graphics, 0, 20, 4, 10, pants); FillDefaultSkin(graphics, 4, 20, 4, 10, pants);
        FillDefaultSkin(graphics, 8, 20, 4, 10, pants); FillDefaultSkin(graphics, 12, 20, 4, 10, pants);
        FillDefaultSkin(graphics, 0, 30, 4, 2, shoe); FillDefaultSkin(graphics, 4, 30, 4, 2, shoe);
        FillDefaultSkin(graphics, 8, 30, 4, 2, shoe); FillDefaultSkin(graphics, 12, 30, 4, 2, shoe);

        FillDefaultSkin(graphics, 20, 48, 4, 4, pants); FillDefaultSkin(graphics, 24, 48, 4, 4, pants);
        FillDefaultSkin(graphics, 16, 52, 4, 10, pants); FillDefaultSkin(graphics, 20, 52, 4, 10, pants);
        FillDefaultSkin(graphics, 24, 52, 4, 10, pants); FillDefaultSkin(graphics, 28, 52, 4, 10, pants);
        FillDefaultSkin(graphics, 16, 62, 4, 2, shoe); FillDefaultSkin(graphics, 20, 62, 4, 2, shoe);
        FillDefaultSkin(graphics, 24, 62, 4, 2, shoe); FillDefaultSkin(graphics, 28, 62, 4, 2, shoe);

        ClearDefaultSkin(graphics, 32, 0, 32, 16);
        ClearDefaultSkin(graphics, 16, 32, 24, 16);
        ClearDefaultSkin(graphics, 40, 32, 16, 16);
        ClearDefaultSkin(graphics, 48, 48, 16, 16);
        ClearDefaultSkin(graphics, 0, 32, 16, 16);
        ClearDefaultSkin(graphics, 0, 48, 16, 16);

        return bitmap;
    }

    private static void FillDefaultSkin(Graphics graphics, int x, int y, int width, int height, DColor color)
    {
        using var brush = new SolidBrush(color);
        graphics.FillRectangle(brush, x, y, width, height);
    }

    private static void ClearDefaultSkin(Graphics graphics, int x, int y, int width, int height)
    {
        var previous = graphics.CompositingMode;
        graphics.CompositingMode = CompositingMode.SourceCopy;
        using var brush = new SolidBrush(DColor.Transparent);
        graphics.FillRectangle(brush, x, y, width, height);
        graphics.CompositingMode = previous;
    }

    private static void DotDefaultSkin(Bitmap bitmap, int x, int y, DColor color)
    {
        if ((uint)x < 64 && (uint)y < 64)
            bitmap.SetPixel(x, y, color);
    }

    private async Task OnGoogleLoginClicked()
    {
        try
        {
            // Clear any old auth and get ready for a new link flow.
            TryClearVeilnetAuth(
                "Starting Veilnet login flow; clearing stale local auth.",
                clearPendingCodes: true,
                updateStatus: false);

            _hubGoogleBtn.Enabled = false;
            UpdateHubStatus("Veilnet: opening browser to link...");

            // Open the browser to the launcher page. The user will log in,
            // and the website will redirect to a custom protocol URL
            // (latticeveil://link?code=...) which the OS routes back to us.
            OpenUrlInBrowser(BuildVeilnetLauncherAutoStartUrl());

            // The launcher now just waits. The protocol handler will receive the
            // code and call ConsumeVeilnetLinkCodeAsync automatically.
            UpdateHubStatus("Veilnet: check your browser to complete login.");
            
            // The button will be re-enabled by the ConsumeVeilnetLinkCodeAsync method
            // upon success or failure of the link attempt.
        }
        catch (Exception ex)
        {
            // Re-enable the button on failure.
            BeginInvoke(new Action(() =>
            {
                UpdateHubStatus($"Veilnet: error starting login ({ex.Message})");
                _hubGoogleBtn.Enabled = true;
            }));
        }
    }

    private async Task OnVeilnetPrimaryActionClicked()
    {
        var username = (Environment.GetEnvironmentVariable("LV_VEILNET_USERNAME") ?? string.Empty).Trim();
        if (HasValidVeilnetSessionForOnline() || !string.IsNullOrWhiteSpace(username))
        {
            OnVeilnetResetClicked();
            return;
        }

        await OnGoogleLoginClicked();
    }

    private async Task<VeilnetClient.ExchangeResponse> LinkVeilnetWithCodeAsync(string code)
    {
        var exchange = await GetVeilnetClient().ExchangeCodeAsync(code).ConfigureAwait(false);
        var me = await GetVeilnetClient().GetMeAsync(exchange.Token).ConfigureAwait(false);

        var username = string.IsNullOrWhiteSpace(me.Username) ? exchange.Username : me.Username;
        var userId = string.IsNullOrWhiteSpace(me.UserId) ? exchange.UserId : me.UserId;

        Environment.SetEnvironmentVariable("LV_VEILNET_USERNAME", username);
        Environment.SetEnvironmentVariable("LV_VEILNET_ACCESS_TOKEN", exchange.Token);
        TrySaveVeilnetAuth(username, exchange.Token, userId);
        await RefreshVeilnetProfileCacheForLauncherAsync(exchange.Token, username).ConfigureAwait(false);
        QueueCloudSkinFetchAndApply("link-code exchange");
        ClearVeilnetFriendsProfileCache("Clearing cached Veilnet friends before link sync.");
        await SyncVeilnetFriendsToProfileAsync("link-code exchange").ConfigureAwait(false);

        _log.Info("Veilnet link exchange verified via launcher-me (200).");

        BeginInvoke(new Action(() => RefreshVeilnetLoginVisuals(username)));
        _ = BeginStartupOnlineChecks();
        exchange.Username = username;
        exchange.UserId = userId;
        return exchange;
    }

    private async Task TryConsumeStartupLinkCodeAsync()
    {
        if (string.IsNullOrWhiteSpace(_startupLinkCode))
            return;

        await ConsumeVeilnetLinkCodeAsync(_startupLinkCode, "startup protocol callback").ConfigureAwait(false);
    }

    private void TryConsumeStartupSkinImportClipboard()
    {
        if (!_startupSkinImportClipboard)
            return;

        ImportSkinsFromClipboard("startup protocol callback");
    }

    private void TryConsumeStartupSkinLibraryRefresh()
    {
        if (!_startupSkinLibraryRefresh)
            return;

        RefreshSkinLibraryFromProtocol("startup protocol callback");
    }

    private async Task TryConsumePendingLinkCodesAsync()
    {
        if (_queuedLinkCodeConsumeInProgress)
            return;

        var codes = LauncherProtocolLinking.DequeuePendingLinkCodes(_log);
        if (codes.Length == 0)
            return;

        _queuedLinkCodeConsumeInProgress = true;
        try
        {
            for (var i = 0; i < codes.Length; i++)
                await ConsumeVeilnetLinkCodeAsync(codes[i], "queued protocol callback").ConfigureAwait(false);
        }
        finally
        {
            _queuedLinkCodeConsumeInProgress = false;
        }
    }

    private void TryConsumePendingSkinImportRequests()
    {
        var count = LauncherProtocolLinking.DequeuePendingSkinImportClipboardRequests(_log);
        if (count <= 0)
            return;

        ImportSkinsFromClipboard("queued protocol callback");
    }

    private void TryConsumePendingSkinLibraryRefreshRequests()
    {
        var count = LauncherProtocolLinking.DequeuePendingSkinLibraryRefreshRequests(_log);
        if (count <= 0)
            return;

        RefreshSkinLibraryFromProtocol("queued protocol callback");
    }

    private void RefreshSkinLibraryFromProtocol(string source)
    {
        RefreshLauncherSkinPreview();
        _skinManagerPopupRefresh?.Invoke();
        _log.Info($"Skin library refreshed from protocol request. source={source}");
    }

    private void ImportSkinsFromClipboard(string source)
    {
        try
        {
            if (!Clipboard.ContainsText(TextDataFormat.UnicodeText))
            {
                _log.Warn($"Skin import skipped: clipboard has no text. source={source}");
                return;
            }

            var text = Clipboard.GetText(TextDataFormat.UnicodeText);
            if (string.IsNullOrWhiteSpace(text))
            {
                _log.Warn($"Skin import skipped: clipboard text is empty. source={source}");
                return;
            }

            var envelope = JsonSerializer.Deserialize<BrowserSkinImportEnvelope>(text, JsonOptions);
            if (envelope == null
                || !string.Equals(envelope.Format, "latticeveil-browser-skin-library", StringComparison.OrdinalIgnoreCase)
                || envelope.Skins == null
                || envelope.Skins.Count == 0)
            {
                _log.Warn($"Skin import skipped: clipboard payload was not a LatticeVeil skin library. source={source}");
                return;
            }

            var imported = 0;
            var skipped = 0;
            var activeHash = SkinLibrary.ReadActiveHash();
            foreach (var skin in envelope.Skins.Take(24))
            {
                try
                {
                    var pngBase64 = (skin.PngBase64 ?? string.Empty).Trim();
                    if (string.IsNullOrWhiteSpace(pngBase64))
                    {
                        skipped++;
                        continue;
                    }

                    var bytes = Convert.FromBase64String(pngBase64);
                    var payloadHash = SkinLibrary.ComputeSha256Hex(bytes);
                    if (!string.IsNullOrWhiteSpace(activeHash)
                        && string.Equals(payloadHash, activeHash, StringComparison.OrdinalIgnoreCase))
                    {
                        skipped++;
                        continue;
                    }

                    SkinLibrary.ImportPngBytes(bytes, skin.DisplayName);
                    imported++;
                }
                catch (Exception ex)
                {
                    skipped++;
                    _log.Warn($"Browser skin import entry skipped: {ex.Message}");
                }
            }

            RefreshLauncherSkinPreview();
            _skinManagerPopupRefresh?.Invoke();
            _log.Info($"Browser skin library import complete: imported={imported}, skipped={skipped}, source={source}");
            MessageBox.Show(
                this,
                imported > 0
                    ? $"Copied {imported} browser skin{(imported == 1 ? string.Empty : "s")} into the launcher library."
                    : "No browser skins could be imported.",
                "Skin Import",
                MessageBoxButtons.OK,
                imported > 0 ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
        }
        catch (Exception ex)
        {
            _log.Warn($"Browser skin import failed: source={source}, error={ex.Message}");
            MessageBox.Show(this, ex.Message, "Skin Import Failed", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private async Task ConsumeVeilnetLinkCodeAsync(string code, string source)
    {
        if (string.IsNullOrWhiteSpace(code))
            return;

        try
        {
            BeginInvoke(new Action(() =>
            {
                _hubGoogleBtn.Enabled = false;
                UpdateHubStatus($"Veilnet: processing {source}...");
            }));

            var exchange = await LinkVeilnetWithCodeAsync(code).ConfigureAwait(false);
            BeginInvoke(new Action(() =>
            {
                RefreshVeilnetLoginVisuals(exchange.Username);
                UpdateHubStatus($"Veilnet: linked as {exchange.Username}");
                ShowVeilnetLinkedLaunchPrompt(exchange.Username);
            }));
            _log.Info($"Veilnet link code consumed from {source}.");
        }
        catch (Exception ex)
        {
            _log.Warn($"Veilnet link code consume failed ({source}): {ex.Message}");
            BeginInvoke(new Action(() => UpdateHubStatus($"Veilnet: link failed ({ex.Message})")));
        }
        finally
        {
            BeginInvoke(new Action(() => _hubGoogleBtn.Enabled = true));
        }
    }

    private string GetVeilnetLauncherPageUrl()
    {
        var fromEnv = (Environment.GetEnvironmentVariable("LV_VEILNET_LAUNCHER_URL") ?? string.Empty).Trim();
        if (!string.IsNullOrWhiteSpace(fromEnv))
            return fromEnv;

        if (!string.IsNullOrWhiteSpace(_launcherRuntimeConfig.VeilnetLauncherPageUrl))
            return _launcherRuntimeConfig.VeilnetLauncherPageUrl;

        return DefaultVeilnetLauncherPageUrl;
    }

    private string BuildVeilnetLauncherAutoStartUrl()
    {
        var baseUrl = GetVeilnetLauncherPageUrl().Trim();
        if (string.IsNullOrWhiteSpace(baseUrl))
            return baseUrl;

        var separator = baseUrl.Contains('?', StringComparison.Ordinal) ? "&" : "?";
        return $"{baseUrl}{separator}autostart=1";
    }

    private string GetVeilnetFunctionsBaseUrl()
    {
        var fromEnv = (Environment.GetEnvironmentVariable("LV_VEILNET_FUNCTIONS_URL") ?? string.Empty).Trim();
        if (!string.IsNullOrWhiteSpace(fromEnv))
            return fromEnv.TrimEnd('/');

        if (!string.IsNullOrWhiteSpace(_launcherRuntimeConfig.VeilnetFunctionsBaseUrl))
            return _launcherRuntimeConfig.VeilnetFunctionsBaseUrl;

        return DefaultVeilnetFunctionsBaseUrl;
    }

    private string GetSupabaseAnonKey()
    {
        var fromEnv = (Environment.GetEnvironmentVariable("LV_SUPABASE_ANON_KEY") ?? string.Empty).Trim();
        if (!string.IsNullOrWhiteSpace(fromEnv))
            return fromEnv;

        if (!string.IsNullOrWhiteSpace(_launcherRuntimeConfig.SupabaseAnonKey))
            return _launcherRuntimeConfig.SupabaseAnonKey;

        return DefaultSupabaseAnonKey;
    }

    private string GetGameHashesGetUrl()
    {
        var fromEnv = (Environment.GetEnvironmentVariable("LV_GAME_HASHES_GET_URL") ?? string.Empty).Trim();
        if (!string.IsNullOrWhiteSpace(fromEnv))
            return fromEnv;

        var functionsEnv = (Environment.GetEnvironmentVariable("LV_VEILNET_FUNCTIONS_URL") ?? string.Empty).Trim();
        if (!string.IsNullOrWhiteSpace(functionsEnv))
            return $"{functionsEnv.TrimEnd('/')}/game-hashes-get";

        if (!string.IsNullOrWhiteSpace(_launcherRuntimeConfig.GameHashesGetUrl))
            return _launcherRuntimeConfig.GameHashesGetUrl;

        if (!string.IsNullOrWhiteSpace(_launcherRuntimeConfig.VeilnetFunctionsBaseUrl))
            return $"{_launcherRuntimeConfig.VeilnetFunctionsBaseUrl.TrimEnd('/')}/game-hashes-get";

        return DefaultGameHashesGetUrl;
    }

    private string ResolveCurrentExecutablePathForHashing()
    {
        var fromRuntime = Hashing.ResolveCurrentProcessExecutablePath();
        if (!string.IsNullOrWhiteSpace(fromRuntime))
            return fromRuntime;

        var baseDir = AppContext.BaseDirectory;
        return Path.Combine(baseDir, "LatticeVeilMonoGame.exe");
    }

    private bool TryComputeCurrentExecutableHash(out string executablePath, out string hash, out string errorMessage)
    {
        executablePath = ResolveCurrentExecutablePathForHashing();
        hash = string.Empty;
        errorMessage = string.Empty;

        if (string.IsNullOrWhiteSpace(executablePath) || !File.Exists(executablePath))
        {
            errorMessage = $"Build verification executable not found: {executablePath}";
            return false;
        }

        try
        {
            hash = Hashing.Sha256File(executablePath);
        }
        catch (Exception ex)
        {
            errorMessage = $"Failed to compute local SHA256: {ex.Message}";
            return false;
        }

        if (string.IsNullOrWhiteSpace(hash))
        {
            errorMessage = "Computed local SHA256 is empty.";
            return false;
        }

        _log.Info($"Executable path used for hash: {Path.GetFileName(executablePath)}");
        _log.Info($"Computed EXE SHA256: {ShortLogId(hash)}");
        return true;
    }

    private static OnlineStartupState MapVerifierFailureToStartupState(OfficialBuildVerifier.VerifyFailure failure)
    {
        return failure switch
        {
            OfficialBuildVerifier.VerifyFailure.HashMismatch => OnlineStartupState.HashMismatch,
            OfficialBuildVerifier.VerifyFailure.Unauthorized => OnlineStartupState.Unauthorized,
            OfficialBuildVerifier.VerifyFailure.ServiceUnavailable => OnlineStartupState.ServiceUnavailable,
            OfficialBuildVerifier.VerifyFailure.BadResponse => OnlineStartupState.BadResponse,
            OfficialBuildVerifier.VerifyFailure.ComputeFailed => OnlineStartupState.ComputeFailed,
            OfficialBuildVerifier.VerifyFailure.MissingFile => OnlineStartupState.ComputeFailed,
            _ => OnlineStartupState.ServiceUnavailable
        };
    }

    private bool VerifyOfficialBuildForOnline()
    {
        var channel = Paths.IsDevBuild ? "dev" : "release";
        if (!TryComputeCurrentExecutableHash(out var executablePath, out var localHash, out var hashError))
        {
            _log.Warn(hashError);
            MessageBox.Show(
                "Online services unavailable (cannot verify official build). Try again later.\n\nLAN/offline still available.",
                "Build Verification Failed",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
            return false;
        }

        OfficialBuildVerifier.VerifyResult verify;
        try
        {
            verify = _officialBuildVerifier.VerifyHashAsync(channel, localHash).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            _log.Warn($"Official build verification exception: {ex.Message}");
            MessageBox.Show(
                "Online services unavailable. LAN/offline still available.",
                "Build Verification Failed",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
            return false;
        }

        if (verify.Ok)
        {
            _log.Info($"Official hash verified for {channel} channel.");
            return true;
        }

        var message = verify.Failure switch
        {
            OfficialBuildVerifier.VerifyFailure.HashMismatch =>
                "Unofficial build - online disabled. LAN/offline still available.",
            OfficialBuildVerifier.VerifyFailure.ServiceUnavailable =>
                "Online services unavailable (cannot verify official build). Try again later.\n\nLAN/offline still available.",
            OfficialBuildVerifier.VerifyFailure.Unauthorized =>
                "Online services unavailable (verification unauthorized).\n\nLAN/offline still available.",
            OfficialBuildVerifier.VerifyFailure.BadResponse =>
                "Online services unavailable (invalid verification response).\n\nLAN/offline still available.",
            _ => string.IsNullOrWhiteSpace(verify.Message)
                ? "Online launch blocked by build verification. LAN/offline still available."
                : $"{verify.Message}\n\nLAN/offline still available."
        };

        _log.Warn($"Official hash verification failed ({verify.Failure}) channel={channel} exe={Path.GetFileName(executablePath)}: {verify.Message}");
        MessageBox.Show(
            message,
            "Official Build Verification",
            MessageBoxButtons.OK,
            MessageBoxIcon.Warning);
        return false;
    }

    private VeilnetClient GetVeilnetClient()
    {
        var baseUrl = GetVeilnetFunctionsBaseUrl();
        if (_veilnetClient == null || !string.Equals(baseUrl, _veilnetFunctionsBaseUrl, StringComparison.OrdinalIgnoreCase))
        {
            _veilnetClient = new VeilnetClient(baseUrl);
            _veilnetFunctionsBaseUrl = baseUrl;
        }

        return _veilnetClient;
    }

    private void OpenUrlInBrowser(string url)
    {
        url = (url ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(url))
            return;

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            _log.Warn($"Veilnet browser open failed: {ex.Message}");
        }
    }

    private string? PromptForVeilnetCode()
    {
        using var form = new Form
        {
            Text = "Link with Veilnet",
            Width = 520,
            Height = 220,
            StartPosition = FormStartPosition.CenterParent,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            MinimizeBox = false,
            MaximizeBox = false,
            ShowInTaskbar = false
        };

        var label = new Label
        {
            Text = "Paste the Veilnet launcher code from your browser:",
            Left = 16,
            Top = 16,
            Width = 470
        };

        var input = new TextBox
        {
            Left = 16,
            Top = 46,
            Width = 470
        };

        var hint = new Label
        {
            Text = "Code expires in 10 minutes. Example: ABCD1234X",
            Left = 16,
            Top = 76,
            Width = 470
        };

        var okBtn = new Button
        {
            Text = "Link",
            DialogResult = DialogResult.OK,
            Left = 310,
            Top = 120,
            Width = 86
        };

        var cancelBtn = new Button
        {
            Text = "Cancel",
            DialogResult = DialogResult.Cancel,
            Left = 400,
            Top = 120,
            Width = 86
        };

        form.Controls.Add(label);
        form.Controls.Add(input);
        form.Controls.Add(hint);
        form.Controls.Add(okBtn);
        form.Controls.Add(cancelBtn);
        form.AcceptButton = okBtn;
        form.CancelButton = cancelBtn;

        var result = form.ShowDialog(this);
        if (result != DialogResult.OK)
            return null;

        return (input.Text ?? string.Empty).Trim();
    }

    private void TrySaveVeilnetAuth(string username, string token, string userId)
    {
        try
        {
            Directory.CreateDirectory(VeilnetAuthDir);
            var record = new VeilnetTokenRecord
            {
                Username = (username ?? string.Empty).Trim(),
                Token = (token ?? string.Empty).Trim(),
                UserId = (userId ?? string.Empty).Trim(),
                SavedAtUtc = DateTime.UtcNow
            };
            var tempPath = Path.GetTempFileName();
            byte[] bytes;
            try
            {
                LvcSerializer.Write(tempPath, LvcSerializer.SerializeObject(record));
                bytes = File.ReadAllBytes(tempPath);
            }
            finally
            {
                try { File.Delete(tempPath); } catch { }
            }
            var protectedBytes = ProtectedData.Protect(bytes, null, DataProtectionScope.CurrentUser);
            var envelope = new ProtectedVeilnetTokenEnvelope
            {
                PayloadBase64 = Convert.ToBase64String(protectedBytes)
            };
            LvcSerializer.Write(VeilnetAuthPath, LvcSerializer.SerializeObject(envelope));
        }
        catch (Exception ex)
        {
            _log.Warn($"Failed to save Veilnet auth: {ex.Message}");
        }
    }

    private VeilnetTokenRecord? TryReadVeilnetAuth()
    {
        try
        {
            if (!File.Exists(VeilnetAuthPath))
                return TryReadLegacyVeilnetAuth();

            var envelopeData = LvcSerializer.Read(VeilnetAuthPath);
            var envelope = new ProtectedVeilnetTokenEnvelope();
            LvcSerializer.ApplyObject(envelope, envelopeData);
            if (string.IsNullOrWhiteSpace(envelope.PayloadBase64))
                return null;

            var protectedBytes = Convert.FromBase64String(envelope.PayloadBase64);
            var bytes = ProtectedData.Unprotect(protectedBytes, null, DataProtectionScope.CurrentUser);
            var payload = Encoding.UTF8.GetString(bytes);
            var tempPath = Path.GetTempFileName();
            try
            {
                File.WriteAllText(tempPath, payload);
                var recordData = LvcSerializer.Read(tempPath);
                var record = new VeilnetTokenRecord();
                LvcSerializer.ApplyObject(record, recordData);

                record.Username = (record.Username ?? string.Empty).Trim();
                record.Token = (record.Token ?? string.Empty).Trim();
                record.UserId = (record.UserId ?? string.Empty).Trim();

                if (string.IsNullOrWhiteSpace(record.Token))
                    return null;

                return record;
            }
            finally
            {
                try { File.Delete(tempPath); } catch { }
            }
        }
        catch (Exception ex)
        {
            _log.Warn($"Failed to load Veilnet auth: {ex.Message}");
            return null;
        }
    }

    private void TryLoadVeilnetAuth()
    {
        _log.Info("Loaded session: checking persisted Veilnet auth...");
        var record = TryReadVeilnetAuth();
        if (record == null)
        {
            _log.Info("Loaded session: absent.");
            ClearVeilnetOwnProfileCache("No Veilnet session found; clearing cached local profile media.");
            ClearVeilnetFriendsProfileCache("No Veilnet session found; clearing cached friend usernames.");
            RefreshVeilnetLoginVisuals();
            return;
        }

        _log.Info($"Loaded session: present. savedAtUtc={record.SavedAtUtc:o}");
        if (!TryValidateTokenLifetime(record.Token, out var expiresUtc, out var rejectionReason))
        {
            var expText = expiresUtc.HasValue ? expiresUtc.Value.ToString("o") : "n/a";
            _log.Warn($"Loaded session rejected: reason={rejectionReason}; expUtc={expText}; nowUtc={DateTime.UtcNow:o}");
            TryClearVeilnetAuth("Saved Veilnet session was invalid/expired.", clearPendingCodes: false, updateStatus: true);
            return;
        }

        _log.Info($"Loaded session accepted. expUtc={expiresUtc!.Value:o}; nowUtc={DateTime.UtcNow:o}");

        Environment.SetEnvironmentVariable("LV_VEILNET_ACCESS_TOKEN", record.Token);
        if (!string.IsNullOrWhiteSpace(record.Username))
            Environment.SetEnvironmentVariable("LV_VEILNET_USERNAME", record.Username);

        var updateUi = new Action(() =>
        {
            RefreshVeilnetLoginVisuals(record.Username);
            UpdateHubStatus(string.IsNullOrWhiteSpace(record.Username)
                ? "Veilnet: validating saved login..."
                : $"Veilnet: restoring {record.Username}...");
        });

        if (IsHandleCreated)
            BeginInvoke(updateUi);
        else
            updateUi();

        TryAutoLoginVeilnetFromEosPuid();
    }

    private void TryClearVeilnetAuth(string reason, bool clearPendingCodes, bool updateStatus)
    {
        if (!string.IsNullOrWhiteSpace(reason))
            _log.Info(reason);

        try
        {
            if (File.Exists(VeilnetAuthPath))
                File.Delete(VeilnetAuthPath);
            TryDeleteLegacyVeilnetAuth();
        }
        catch (Exception ex)
        {
            _log.Warn($"Failed to clear Veilnet auth: {ex.Message}");
        }
        finally
        {
            Environment.SetEnvironmentVariable("LV_VEILNET_USERNAME", null);
            Environment.SetEnvironmentVariable("LV_VEILNET_ACCESS_TOKEN", null);
            Environment.SetEnvironmentVariable("LV_GATE_TICKET", null);
            Environment.SetEnvironmentVariable("LV_GATE_TICKET_EXPIRES_UTC", null);
            _veilnetAutoLoginAttempted = false;
            _veilnetAuthOk = false;
            _gateTicketOk = false;
            _eosReadyOk = false;
            _launchReadiness = LaunchReadiness.ReadyOfflineOnly;
            ClearVeilnetOwnProfileCache("Veilnet auth cleared; removing cached local profile media.");
            ClearVeilnetFriendsProfileCache("Veilnet auth cleared; removing cached friend usernames.");

            if (clearPendingCodes)
                LauncherProtocolLinking.ClearPendingLinkCodes(_log);

            if (IsHandleCreated)
            {
                BeginInvoke(new Action(() =>
                {
                    RefreshVeilnetLoginVisuals();
                    if (updateStatus)
                        UpdateHubStatus("Veilnet: LOGIN REQUIRED. Click Login.");
                    SetLaunchButtonState(GetGameState());
                }));
            }
            else if (updateStatus)
            {
                _onlineStatusDetail = "Veilnet: LOGIN REQUIRED. Click Login.";
            }
        }
    }

    private void TryClearVeilnetAuth()
    {
        TryClearVeilnetAuth("Clearing Veilnet auth cache.", clearPendingCodes: false, updateStatus: true);
    }

    private void TryDeleteLegacyVeilnetAuth()
    {
        try
        {
            if (File.Exists(LegacyVeilnetAuthPath))
                File.Delete(LegacyVeilnetAuthPath);
        }
        catch (Exception ex)
        {
            _log.Warn($"Failed to delete legacy Veilnet auth: {ex.Message}");
        }
    }

    private void OnVeilnetResetClicked()
    {
        TryClearVeilnetAuth("Veilnet logout requested by user.", clearPendingCodes: true, updateStatus: true);
    }

    private void TryAutoLoginVeilnetFromEosPuid()
    {
        if (_veilnetAutoLoginAttempted)
            return;

        _veilnetAutoLoginAttempted = true;
        var record = TryReadVeilnetAuth();
        if (record == null || string.IsNullOrWhiteSpace(record.Token))
            return;

        _ = Task.Run(async () =>
        {
            try
            {
                var me = await GetVeilnetClient().GetMeAsync(record.Token).ConfigureAwait(false);
                Environment.SetEnvironmentVariable("LV_VEILNET_USERNAME", me.Username);
                Environment.SetEnvironmentVariable("LV_VEILNET_ACCESS_TOKEN", record.Token);
                TrySaveVeilnetAuth(me.Username, record.Token, me.UserId);
                await RefreshVeilnetProfileCacheForLauncherAsync(record.Token, me.Username).ConfigureAwait(false);
                QueueCloudSkinFetchAndApply("auto-login refresh");
                await SyncVeilnetFriendsToProfileAsync("auto-login refresh").ConfigureAwait(false);

                BeginInvoke(new Action(() =>
                {
                    RefreshVeilnetLoginVisuals(me.Username);
                    UpdateHubStatus($"Veilnet: linked as {me.Username}");
                }));
                _ = BeginStartupOnlineChecks();
            }
            catch (Exception ex)
            {
                _log.Warn($"Veilnet token refresh failed: {ex.Message}");
                TryClearVeilnetAuth("Saved Veilnet login was rejected by launcher-me.", clearPendingCodes: false, updateStatus: true);
                BeginInvoke(new Action(() =>
                {
                    RefreshVeilnetLoginVisuals();
                    UpdateHubStatus("Veilnet: saved link expired. Link again.");
                }));
            }
        });
    }

    private async Task SyncVeilnetFriendsToProfileAsync(string trigger)
    {
        try
        {
            if (!HasValidVeilnetSessionForOnline())
                return;

            var result = await OnlineGateClient.GetOrCreate().GetFriendsAsync().ConfigureAwait(false);
            if (!result.Ok)
            {
                _log.Warn($"Veilnet friend sync skipped ({trigger}): {result.Message}");
                return;
            }

            var merged = new List<PlayerProfile.FriendEntry>();
            var seenUserIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var friend in result.Friends)
            {
                var userId = (friend.ProductUserId ?? string.Empty).Trim();
                if (string.IsNullOrWhiteSpace(userId) || !seenUserIds.Add(userId))
                    continue;

                var label = NormalizeVeilnetFriendName(friend.Username, friend.DisplayName);
                if (string.IsNullOrWhiteSpace(label))
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
            _log.Info($"Veilnet friend sync ok ({trigger}): cachedUsernames={merged.Count}");
        }
        catch (Exception ex)
        {
            _log.Warn($"Veilnet friend sync failed ({trigger}): {ex.Message}");
        }
    }

    private void ClearVeilnetFriendsProfileCache(string reason)
    {
        try
        {
            if (_profile.Friends.Count == 0)
                return;

            _profile.Friends.Clear();
            _profile.Save(_log);
            _log.Info(reason);
        }
        catch (Exception ex)
        {
            _log.Warn($"Failed to clear Veilnet friends cache: {ex.Message}");
        }
    }

    private void ClearVeilnetOwnProfileCache(string reason)
    {
        try
        {
            DeleteFileIfExists(Paths.VeilnetProfileCachePath);
            DeleteFileIfExists(Paths.VeilnetAvatarCachePath);
            DeleteFileIfExists(Paths.VeilnetBannerCachePath);
            _log.Info(reason);
        }
        catch (Exception ex)
        {
            _log.Warn($"Failed to clear Veilnet profile cache: {ex.Message}");
        }
    }

    private static void DeleteFileIfExists(string path)
    {
        if (File.Exists(path))
            File.Delete(path);
    }

    private static string NormalizeVeilnetFriendName(string? username, string? displayName)
    {
        var preferred = (username ?? string.Empty).Trim();
        if (!LooksLikeIdentityToken(preferred))
            return preferred;

        var fallback = (displayName ?? string.Empty).Trim();
        if (!LooksLikeIdentityToken(fallback))
            return fallback;

        return string.Empty;
    }

    private static bool LooksLikeIdentityToken(string value)
    {
        var text = (value ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(text))
            return true;

        if (Guid.TryParse(text, out _))
            return true;

        var compact = text.Replace("-", string.Empty);
        if (compact.Length >= 16 && compact.All(Uri.IsHexDigit))
            return true;

        if (text.Contains("...", StringComparison.Ordinal)
            || text.StartsWith("PLAYER-", StringComparison.OrdinalIgnoreCase)
            || text.StartsWith("000", StringComparison.OrdinalIgnoreCase)
            || string.Equals(text, "UNKNOWN", StringComparison.OrdinalIgnoreCase))
            return true;

        return false;
    }

    private static string MaskAccountDisplay(string? value)
    {
        var text = (value ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(text))
            return string.Empty;

        return LooksLikeIdentityToken(text) ? $"Account {ShortLogId(text)}" : text;
    }

    private static string ShortLogId(string? value)
    {
        var text = (value ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(text))
            return "none";

        if (text.Length <= 16)
            return text;

        return $"{text[..8]}...{text[^4..]}";
    }

    private void RefreshVeilnetLoginVisuals(string? usernameOverride = null)
    {
        var username = (usernameOverride ?? (Environment.GetEnvironmentVariable("LV_VEILNET_USERNAME") ?? string.Empty)).Trim();
        var token = (Environment.GetEnvironmentVariable("LV_VEILNET_ACCESS_TOKEN") ?? string.Empty).Trim();
        var hasToken = !string.IsNullOrWhiteSpace(token) && !IsPlaceholderToken(token);
        var hasSession = hasToken || !string.IsNullOrWhiteSpace(username);
        var onlineMode = IsOnlineModeSelected();

        _hubVeilnetUserLabel.Text = string.IsNullOrWhiteSpace(username) ? "VEILNET ACCOUNT" : username.ToUpperInvariant();
        _hubVeilnetAccountRow.Visible = onlineMode;
        _hubVeilnetUserLabel.Visible = onlineMode;
        _hubVeilnetAvatarBox.Visible = true;
        _hubGoogleBtn.Text = hasSession ? "LOGOUT" : "LOGIN WITH VEILNET";
        _hubGoogleBtn.Visible = onlineMode;
        _offlineNameLabel.Visible = false;
        _offlineNameBox.Visible = false;
        _hubResetBtn.Visible = false;
        _hubResetBtn.Enabled = false;

        RefreshVeilnetAvatarVisuals(username, hasSession);
        SetTrayVeilnetAuthItemState(username, hasSession);
    }

    private void SetTrayVeilnetAuthItemState(string username, bool hasSession)
    {
        if (_trayVeilnetAuthItem != null)
        {
            _trayVeilnetAuthItem.Text = hasSession
                ? $"{(string.IsNullOrWhiteSpace(username) ? "VEILNET" : username.ToUpperInvariant())} - LOGOUT"
                : "LOGIN WITH VEILNET";
            _trayVeilnetAuthItem.Enabled = _hubGoogleBtn.Enabled;
        }

        if (_trayVeilnetAuthButton != null)
        {
            _trayVeilnetAuthButton.Text = hasSession ? "LOGOUT" : "LOGIN";
            _trayVeilnetAuthButton.Enabled = _hubGoogleBtn.Enabled;
        }

        if (_trayVeilnetProfileButton != null)
            _trayVeilnetProfileButton.Enabled = hasSession && !string.IsNullOrWhiteSpace(username);

        RefreshTrayVeilnetAvatarVisuals(username, hasSession);
    }

    private void RefreshTrayVeilnetAvatarVisuals(string username, bool hasSession)
    {
        if (_trayVeilnetAvatarBox == null)
            return;

        var image = hasSession ? TryLoadVeilnetAvatarImageFromCache() : null;
        SetTrayVeilnetAvatarImage(image ?? CreateFallbackVeilnetAvatar(username, hasSession));
    }

    private void SetTrayVeilnetAvatarImage(DImage? image)
    {
        var previous = _trayVeilnetAvatarImage;
        _trayVeilnetAvatarImage = image;
        if (_trayVeilnetAvatarBox != null)
            _trayVeilnetAvatarBox.Image = image;
        previous?.Dispose();
    }

    private void OpenVeilnetProfileFromTray()
    {
        var username = (Environment.GetEnvironmentVariable("LV_VEILNET_USERNAME") ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(username))
            return;

        OpenUrlInBrowser(BuildVeilnetProfileUrl(username));
    }

    private string BuildVeilnetProfileUrl(string username)
    {
        var encoded = Uri.EscapeDataString((username ?? string.Empty).Trim());
        var launcherUrl = GetVeilnetLauncherPageUrl();
        if (Uri.TryCreate(launcherUrl, UriKind.Absolute, out var launcherUri))
            return new Uri(launcherUri, $"../profile/?u={encoded}").ToString();

        return $"https://latticeveil.github.io/veilnet/profile/?u={encoded}";
    }

    private void RefreshOfflineIdentityVisuals()
    {
        var offlineName = (_profile.OfflineUsername ?? string.Empty).Trim();
        _hubVeilnetAccountRow.Visible = true;
        _hubVeilnetUserLabel.Visible = false;
        _hubGoogleBtn.Visible = false;
        _hubVeilnetAvatarBox.Visible = true;
        _offlineNameLabel.Visible = true;
        _offlineNameBox.Visible = true;
        _offlineNameLabel.Text = "OFFLINE USERNAME";
        if (!_offlineNameBox.Focused)
            _offlineNameBox.Text = offlineName;
        _hubResetBtn.Visible = false;
        _hubResetBtn.Enabled = false;
        SetVeilnetAvatarImage(CreateFallbackVeilnetAvatar(offlineName, hasSession: false));
    }

    private void RefreshVeilnetAvatarVisuals(string username, bool hasSession)
    {
        var image = hasSession ? TryLoadVeilnetAvatarImageFromCache() : null;
        SetVeilnetAvatarImage(image ?? CreateFallbackVeilnetAvatar(username, hasSession));
    }

    private DImage? TryLoadVeilnetAvatarImageFromCache()
    {
        try
        {
            if (!File.Exists(Paths.VeilnetAvatarCachePath))
                return null;

            var bytes = File.ReadAllBytes(Paths.VeilnetAvatarCachePath);
            if (bytes.Length == 0)
                return null;

            using var ms = new MemoryStream(bytes, writable: false);
            using var loaded = DImage.FromStream(ms);
            return new Bitmap(loaded);
        }
        catch (Exception ex)
        {
            _log.Warn($"Failed to load cached Veilnet avatar: {ex.Message}");
            return null;
        }
    }

    private DImage CreateFallbackVeilnetAvatar(string username, bool hasSession)
    {
        var bitmap = new Bitmap(72, 72);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.SmoothingMode = SmoothingMode.None;
        graphics.Clear(DColor.Transparent);

        var fill = hasSession ? DColor.FromArgb(38, 92, 68) : DColor.FromArgb(64, 64, 64);
        using var brush = new SolidBrush(fill);
        graphics.FillRectangle(brush, 2, 2, 68, 68);

        using var borderPen = new Pen(DColor.FromArgb(220, 220, 220), 2f);
        graphics.DrawRectangle(borderPen, 2, 2, 68, 68);

        var initials = GetAvatarInitials(username);
        using var textBrush = new SolidBrush(DColor.White);
        using var font = new DFont(Font.FontFamily, 18f, System.Drawing.FontStyle.Bold, GraphicsUnit.Pixel);
        var rect = new RectangleF(0, 0, bitmap.Width, bitmap.Height);
        using var format = new StringFormat
        {
            Alignment = StringAlignment.Center,
            LineAlignment = StringAlignment.Center
        };
        graphics.DrawString(initials, font, textBrush, rect, format);
        return bitmap;
    }

    private static string GetAvatarInitials(string username)
    {
        var value = (username ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(value))
            return "?";

        var parts = value.Split(new[] { ' ', '_', '-', '.' }, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length >= 2)
            return string.Concat(parts[0][0], parts[1][0]).ToUpperInvariant();

        return value.Length >= 2 ? value[..2].ToUpperInvariant() : value.ToUpperInvariant();
    }

    private void SetVeilnetAvatarImage(DImage? image)
    {
        var previous = _hubVeilnetAvatarImage;
        _hubVeilnetAvatarImage = image;
        _hubVeilnetAvatarBox.Image = image;
        previous?.Dispose();
    }

    private async Task RefreshVeilnetProfileCacheForLauncherAsync(string accessToken, string? fallbackUsername = null, CancellationToken ct = default)
    {
        try
        {
            var client = new VeilnetProfileClient(_log, GetVeilnetFunctionsBaseUrl(), GetSupabaseAnonKey());
            using var loader = new MemoryWebTextureLoader();
            var result = await client.GetProfileAsync(accessToken, ct).ConfigureAwait(false);
            if (!result.Ok || result.Profile == null)
            {
                _log.Info($"Veilnet profile refresh skipped: {result.Message}");
                return;
            }

            var profile = result.Profile;
            var cache = new VeilnetProfileCacheStore
            {
                Username = string.IsNullOrWhiteSpace(profile.Username) ? (fallbackUsername ?? string.Empty).Trim() : (profile.Username ?? string.Empty).Trim(),
                AboutMe = (profile.AboutMe ?? string.Empty).Replace("\r\n", "\n").Trim(),
                PictureUrl = (profile.PictureUrl ?? string.Empty).Trim(),
                BannerUrl = (profile.BannerUrl ?? string.Empty).Trim(),
                ThemeColor = (profile.ThemeColor ?? string.Empty).Trim(),
                Theme = (profile.Theme ?? string.Empty).Trim(),
                UpdatedAt = (profile.UpdatedAtRaw ?? string.Empty).Trim()
            };
            cache.Save(_log);

            if (!string.IsNullOrWhiteSpace(cache.PictureUrl))
            {
                var avatarBytes = await loader.DownloadImageBytesAsync(cache.PictureUrl, ct).ConfigureAwait(false);
                if (avatarBytes is { Length: > 0 })
                {
                    var dir = Path.GetDirectoryName(Paths.VeilnetAvatarCachePath);
                    if (!string.IsNullOrWhiteSpace(dir))
                        Directory.CreateDirectory(dir);
                    File.WriteAllBytes(Paths.VeilnetAvatarCachePath, avatarBytes);
                }
            }

            if (IsHandleCreated)
                BeginInvoke(new Action(() => RefreshVeilnetLoginVisuals(cache.Username)));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.Warn($"Veilnet profile refresh failed: {ex.Message}");
        }
    }

    private static bool IsPlaceholderToken(string token)
    {
        return string.Equals(token, "null", StringComparison.OrdinalIgnoreCase)
            || string.Equals(token, "undefined", StringComparison.OrdinalIgnoreCase)
            || string.Equals(token, "none", StringComparison.OrdinalIgnoreCase)
            || string.Equals(token, "placeholder", StringComparison.OrdinalIgnoreCase);
    }

    private bool HasValidVeilnetSessionForOnline(bool logFailures = false)
    {
        var username = (Environment.GetEnvironmentVariable("LV_VEILNET_USERNAME") ?? string.Empty).Trim();
        var token = (Environment.GetEnvironmentVariable("LV_VEILNET_ACCESS_TOKEN") ?? string.Empty).Trim();

        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(token))
        {
            if (logFailures)
                _log.Warn($"Loaded session rejected: reason=missing_username_or_token; hasUsername={!string.IsNullOrWhiteSpace(username)}; hasToken={!string.IsNullOrWhiteSpace(token)}");
            return false;
        }

        if (IsPlaceholderToken(token))
        {
            if (logFailures)
                _log.Warn("Loaded session rejected: reason=placeholder_token");
            return false;
        }

        if (!TryValidateTokenLifetime(token, out var expiresUtc, out var rejectionReason))
        {
            if (logFailures)
            {
                var expText = expiresUtc.HasValue ? expiresUtc.Value.ToString("o") : "n/a";
                _log.Warn($"Loaded session rejected: reason={rejectionReason}; expUtc={expText}; nowUtc={DateTime.UtcNow:o}");
            }
            return false;
        }

        return true;
    }

    private static bool TryValidateTokenLifetime(string token, out DateTime? expiresUtc, out string reason)
    {
        expiresUtc = null;
        reason = string.Empty;

        if (string.IsNullOrWhiteSpace(token))
        {
            reason = "missing_token";
            return false;
        }

        if (IsPlaceholderToken(token))
        {
            reason = "placeholder_token";
            return false;
        }

        if (!TryGetJwtExpiryUtc(token, out var expUtc))
        {
            reason = "exp_missing_or_invalid";
            return false;
        }

        expiresUtc = expUtc;
        if (expUtc <= DateTime.UtcNow.AddSeconds(60))
        {
            reason = "expired_or_near_expiry";
            return false;
        }

        reason = "ok";
        return true;
    }

    private static bool TryGetJwtExpiryUtc(string token, out DateTime expiresUtc)
    {
        expiresUtc = DateTime.MinValue;

        try
        {
            var parts = (token ?? string.Empty).Split('.');
            if (parts.Length < 2)
                return false;

            var payloadBytes = DecodeBase64Url(parts[1]);
            using var doc = JsonDocument.Parse(payloadBytes);
            if (!doc.RootElement.TryGetProperty("exp", out var expProp))
                return false;

            long expSeconds;
            if (expProp.ValueKind == JsonValueKind.Number)
            {
                if (!expProp.TryGetInt64(out expSeconds))
                    return false;
            }
            else if (expProp.ValueKind == JsonValueKind.String && long.TryParse(expProp.GetString(), out var parsed))
            {
                expSeconds = parsed;
            }
            else
            {
                return false;
            }

            expiresUtc = DateTimeOffset.FromUnixTimeSeconds(expSeconds).UtcDateTime;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static byte[] DecodeBase64Url(string value)
    {
        var s = (value ?? string.Empty).Trim().Replace('-', '+').Replace('_', '/');
        switch (s.Length % 4)
        {
            case 2:
                s += "==";
                break;
            case 3:
                s += "=";
                break;
        }

        return Convert.FromBase64String(s);
    }

    private bool IsOnlineModeSelected()
    {
        var mode = (_launchModeBox.SelectedItem as string) ?? "Online";
        return !string.Equals(mode, "Offline", StringComparison.OrdinalIgnoreCase);
    }

    private void BuildTopBar()
    {
        _topBar.Dock = DockStyle.Fill;
        _topBar.Padding = new Padding(6, 6, 6, 6);

        _logoBox.Size = new DSize(28, 28);
        _logoBox.SizeMode = PictureBoxSizeMode.Zoom;
        _logoBox.Image = LoadEmbeddedLogo();
        _logoBox.Location = new DPoint(8, 9);

        _title.AutoSize = true;
        _title.Location = new DPoint(42, 12);
        _title.Text = BuildLauncherWindowTitle();
        _title.Font = new DFont(Font.FontFamily, 11.25f, System.Drawing.FontStyle.Bold);

        _releaseTitleLabel.AutoSize = false;
        _releaseTitleLabel.AutoEllipsis = false;
        _releaseTitleLabel.Font = new DFont(Font.FontFamily, 12.5f, System.Drawing.FontStyle.Bold);
        _releaseTitleLabel.TextAlign = System.Drawing.ContentAlignment.MiddleLeft;
        _releaseTitleLabel.Text = "Checking release...";

        _updateBtn.Text = "UPDATE";
        _updateBtn.Width = 88;
        _updateBtn.Height = 26;
        _updateBtn.FlatStyle = FlatStyle.Flat;
        _updateBtn.FlatAppearance.BorderSize = 1;
        _updateBtn.Font = new DFont(Font.FontFamily, 8.5f, System.Drawing.FontStyle.Bold);
        _updateBtn.Click += async (_, _) => await HandleUpdateButtonClickAsync();

        _closeBtn.Text = "X";
        _closeBtn.Width = 40;
        _closeBtn.Height = 28;
        _closeBtn.FlatStyle = FlatStyle.Flat;
        _closeBtn.FlatAppearance.BorderSize = 0;
        _closeBtn.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        _closeBtn.Location = new DPoint(_topBar.Width - 46, 8);
        _closeBtn.Click += (_, _) => HandleLauncherCloseButtonRequest();

        _minBtn.Text = "–";
        _minBtn.Width = 40;
        _minBtn.Height = 28;
        _minBtn.FlatStyle = FlatStyle.Flat;
        _minBtn.FlatAppearance.BorderSize = 0;
        _minBtn.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        _minBtn.Location = new DPoint(_topBar.Width - 92, 8);
        _minBtn.Click += (_, _) => HandleLauncherMinimizeRequest();

        _topBar.Controls.Add(_logoBox);
        _topBar.Controls.Add(_title);
        _topBar.Controls.Add(_releaseTitleLabel);
        _topBar.Controls.Add(_updateBtn);
        _topBar.Controls.Add(_minBtn);
        _topBar.Controls.Add(_closeBtn);

        // Maintain right-side button positions when resized.
        _topBar.Resize += (_, _) =>
        {
            _closeBtn.Location = new DPoint(_topBar.Width - 46, 8);
            _minBtn.Location = new DPoint(_topBar.Width - 92, 8);
            LayoutTopBarControls();
        };

        LayoutTopBarControls();
        UpdateGameReleaseVisuals();

        // Make the borderless window draggable.
        void drag(MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left)
                return;
            ReleaseCapture();
            SendMessage(Handle, WM_NCLBUTTONDOWN, HTCAPTION, 0);
        }

        _topBar.MouseDown += (_, e) => drag(e);
        _title.MouseDown += (_, e) => drag(e);
        _releaseTitleLabel.MouseDown += (_, e) => drag(e);
        _logoBox.MouseDown += (_, e) => drag(e);
    }

    private string BuildLauncherWindowTitle()
    {
        return $"{(Paths.IsDevBuild ? "[DEV] " : string.Empty)}LatticeLauncher";
    }

    private void LayoutTopBarControls()
    {
        var labelX = _title.Right + 12;
        var buttonX = Math.Max(labelX + 16, _topBar.Width - 188);
        var labelWidth = Math.Max(100, buttonX - labelX - 8);

        _releaseTitleLabel.Location = new DPoint(labelX, 8);
        _releaseTitleLabel.Size = new DSize(labelWidth, 28);
        _updateBtn.Location = new DPoint(buttonX, 9);
        FitReleaseTitleLabelFont();
    }

    private void FitReleaseTitleLabelFont()
    {
        if (_releaseTitleLabel.Width <= 0)
            return;

        var text = (_releaseTitleLabel.Text ?? string.Empty).Trim();
        if (text.Length == 0)
            return;

        var candidateSizes = new[] { 12.5f, 12f, 11.5f, 11f, 10.5f, 10f, 9.5f, 9f };
        foreach (var size in candidateSizes)
        {
            using var font = new DFont(Font.FontFamily, size, System.Drawing.FontStyle.Bold);
            var measured = TextRenderer.MeasureText(text, font, new DSize(int.MaxValue, 28), TextFormatFlags.NoPadding);
            if (measured.Width <= _releaseTitleLabel.Width)
            {
                _releaseTitleLabel.Font = new DFont(Font.FontFamily, size, System.Drawing.FontStyle.Bold);
                return;
            }
        }

        _releaseTitleLabel.Font = new DFont(Font.FontFamily, 9f, System.Drawing.FontStyle.Bold);
    }

    private void UpdateGameReleaseVisuals()
    {
        Text = BuildLauncherWindowTitle();
        _title.Text = BuildLauncherWindowTitle();

        if (_gameUpdateBusy)
        {
            _releaseTitleLabel.Text = "Updating...";
            _releaseTitleLabel.ForeColor = DColor.Gold;
            _updateBtn.Enabled = false;
            _toolTip.SetToolTip(_updateBtn, "Update is in progress.");
            LayoutTopBarControls();
            return;
        }

        if (_gameUpdateCheckInProgress)
        {
            _releaseTitleLabel.Text = "Checking release...";
            _releaseTitleLabel.ForeColor = DColor.Gold;
            _updateBtn.Enabled = false;
            _toolTip.SetToolTip(_updateBtn, "Checking GitHub for the latest release.");
            LayoutTopBarControls();
            return;
        }

        if (_gameUpdateCheck?.Release != null)
        {
            var releaseTitle = string.IsNullOrWhiteSpace(_gameUpdateCheck.ReleaseTitle)
                ? (_gameUpdateCheck.Release.tag_name ?? "Latest")
                : _gameUpdateCheck.ReleaseTitle;
            var localVersionText = GameReleaseUpdater.GetVersionLabel(_gameUpdateCheck.LocalVersion);
            var remoteVersionText = _gameUpdateCheck.RemoteVersion == null
                ? releaseTitle
                : GameReleaseUpdater.GetVersionLabel(_gameUpdateCheck.RemoteVersion);
            var localReleaseName = GetDisplayReleaseName(localVersionText, releaseTitle, _gameUpdateCheck.LocalVersion, _gameUpdateCheck.RemoteVersion);

            if (_gameUpdateCheck.Asset == null)
            {
                _releaseTitleLabel.Text = $"{localVersionText}  NO EXE ASSET";
                _releaseTitleLabel.ForeColor = DColor.FromArgb(255, 120, 92);
                _toolTip.SetToolTip(_updateBtn, $"Running {localVersionText}. Latest GitHub release found, but no matching EXE asset was attached: {releaseTitle}");
            }
            else if (_gameUpdateCheck.IsUpdateAvailable)
            {
                _releaseTitleLabel.Text = $"{localVersionText}  NEW VERSION: {releaseTitle}";
                _releaseTitleLabel.ForeColor = DColor.FromArgb(255, 120, 92);
                _toolTip.SetToolTip(_updateBtn, $"Running {localVersionText}. New version: {releaseTitle} ({remoteVersionText}).");
            }
            else
            {
                _releaseTitleLabel.Text = localReleaseName;
                _releaseTitleLabel.ForeColor = DColor.FromArgb(88, 190, 125);
                _toolTip.SetToolTip(_updateBtn, $"Running {localReleaseName}. Check GitHub for the latest release again.");
            }

            _updateBtn.Enabled = true;
            LayoutTopBarControls();
            return;
        }

        _releaseTitleLabel.Text = string.IsNullOrWhiteSpace(_gameUpdateStatusDetail)
            ? "Release check unavailable"
            : _gameUpdateStatusDetail;
        _releaseTitleLabel.ForeColor = DColor.FromArgb(255, 120, 92);
        _updateBtn.Enabled = true;
        _toolTip.SetToolTip(_updateBtn, "Retry GitHub release check.");
        LayoutTopBarControls();
    }

    private static string GetDisplayReleaseName(string localVersionText, string releaseTitle, Version localVersion, Version? remoteVersion)
    {
        if (!string.IsNullOrWhiteSpace(releaseTitle) && remoteVersion != null
            && NormalizeReleaseComparable(localVersion).Equals(NormalizeReleaseComparable(remoteVersion)))
        {
            return releaseTitle.Trim();
        }

        if (NormalizeReleaseComparable(localVersion).Equals(new Version(16, 1, 0, 0)))
            return "V16.1.0 - Veilnet Skinlink";

        return localVersionText;
    }

    private static Version NormalizeReleaseComparable(Version version)
    {
        var build = version.Build < 0 ? 0 : version.Build;
        var revision = version.Revision < 0 ? 0 : version.Revision;
        return new Version(version.Major, version.Minor, build, revision);
    }

    private async Task HandleUpdateButtonClickAsync()
    {
        if (_gameUpdateBusy || _gameUpdateCheckInProgress)
            return;

        if (_gameUpdateCheck?.IsUpdateAvailable == true)
        {
            StartGameUpdate();
            return;
        }

        await BeginGameUpdateCheckAsync(manual: true);
    }

    private async Task BeginGameUpdateCheckAsync(bool manual = false)
    {
        if (_gameUpdateBusy || _gameUpdateCheckInProgress)
            return;

        _gameUpdateCheckInProgress = true;
        _gameUpdateStatusDetail = "Checking GitHub release...";
        UpdateGameReleaseVisuals();

        _gameUpdateCts?.Cancel();
        _gameUpdateCts = new CancellationTokenSource();

        try
        {
            _gameUpdateCheck = await _gameReleaseUpdater.CheckForUpdateAsync(_gameUpdateCts.Token);
            _gameUpdateStatusDetail = _gameUpdateCheck.StatusMessage;
            _log.Info($"Game release check: {_gameUpdateStatusDetail}");

            if (_gameUpdateCheck.IsUpdateAvailable && ShouldShowGameUpdateReminder())
            {
                _gameUpdateReminderShown = true;
                var choice = ShowGameUpdateReminderDialog(_gameUpdateCheck.ReleaseTitle);
                if (choice == UpdateReminderChoice.UpdateNow)
                {
                    _gameUpdateStartQueued = true;
                    return;
                }

                if (choice == UpdateReminderChoice.IgnoreThisRelease)
                {
                    _settings.IgnoredGameReleaseTitle = _gameUpdateCheck.ReleaseTitle;
                    SaveLauncherSettings();
                }
            }
            else if (manual && !_gameUpdateCheck.IsUpdateAvailable)
            {
                if (_gameUpdateCheck.Asset == null && _gameUpdateCheck.Release != null)
                {
                    ShowGameUpdateNoticeDialog(
                        "UPDATE CHECK INCOMPLETE",
                        $"GitHub release found, but no matching game EXE asset was attached.\n\nRelease: {_gameUpdateCheck.ReleaseTitle}",
                        DColor.FromArgb(230, 180, 72));
                }
                else
                {
                    ShowGameUpdateNoticeDialog(
                        "UPDATED",
                        $"You are already on the latest release.\n\n{_gameUpdateCheck.ReleaseTitle}",
                        DColor.FromArgb(88, 190, 125));
                }
            }
        }
        catch (Exception ex)
        {
            _gameUpdateStatusDetail = $"Release check failed: {ex.Message}";
            _gameUpdateCheck = null;
            _log.Warn($"Game release check failed: {ex.Message}");
            if (manual)
            {
                ShowGameUpdateNoticeDialog(
                    "UPDATE CHECK FAILED",
                    _gameUpdateStatusDetail,
                    DColor.FromArgb(255, 120, 92));
            }
        }
        finally
        {
            _gameUpdateCheckInProgress = false;
            UpdateGameReleaseVisuals();
            if (_gameUpdateStartQueued)
            {
                _gameUpdateStartQueued = false;
                BeginInvoke(new Action(StartGameUpdate));
            }
        }
    }

    private VeilnetTokenRecord? TryReadLegacyVeilnetAuth()
    {
        try
        {
            if (!File.Exists(LegacyVeilnetAuthPath))
                return null;

            var protectedBytes = File.ReadAllBytes(LegacyVeilnetAuthPath);
            var bytes = ProtectedData.Unprotect(protectedBytes, null, DataProtectionScope.CurrentUser);
            var json = Encoding.UTF8.GetString(bytes);
            var record = JsonSerializer.Deserialize<VeilnetTokenRecord>(json, JsonOptions);
            if (record == null)
                return null;

            record.Username = (record.Username ?? string.Empty).Trim();
            record.Token = (record.Token ?? string.Empty).Trim();
            record.UserId = (record.UserId ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(record.Token))
                return null;

            TrySaveVeilnetAuth(record.Username, record.Token, record.UserId);
            TryDeleteLegacyVeilnetAuth();
            return record;
        }
        catch (Exception ex)
        {
            _log.Warn($"Failed to migrate legacy Veilnet auth: {ex.Message}");
            return null;
        }
    }

    private bool ShouldShowGameUpdateReminder()
    {
        if (_gameUpdateCheck == null || !_gameUpdateCheck.IsUpdateAvailable)
            return false;

        if (_gameUpdateReminderShown)
            return false;

        return !string.Equals(
            _settings.IgnoredGameReleaseTitle ?? string.Empty,
            _gameUpdateCheck.ReleaseTitle ?? string.Empty,
            StringComparison.OrdinalIgnoreCase);
    }

    private async void StartGameUpdate()
    {
        if (_gameUpdateBusy || _gameUpdateCheckInProgress)
            return;

        if (GetGameState() != GameState.NotRunning)
        {
            ShowGameUpdateNoticeDialog(
                "CLOSE GAME FIRST",
                "Close the running game before applying an update.",
                DColor.FromArgb(230, 180, 72));
            return;
        }

        if (_assetBusy || _launching)
        {
            ShowGameUpdateNoticeDialog(
                "LAUNCHER BUSY",
                "Wait for the current launcher task to finish before updating.",
                DColor.FromArgb(230, 180, 72));
            return;
        }

        if (_gameUpdateCheck == null)
        {
            await BeginGameUpdateCheckAsync(manual: true);
            return;
        }

        if (!_gameUpdateCheck.IsUpdateAvailable)
        {
            await BeginGameUpdateCheckAsync(manual: true);
            return;
        }

        _gameUpdateBusy = true;
        UpdateGameReleaseVisuals();

        try
        {
            var releaseTitle = string.IsNullOrWhiteSpace(_gameUpdateCheck.ReleaseTitle)
                ? "latest release"
                : _gameUpdateCheck.ReleaseTitle;
            ShowAssetPanel("Updating Game...", "Preparing download...", releaseTitle);

            var progress = new Progress<float>(p =>
            {
                var pct = (int)Math.Round(Math.Clamp(p, 0f, 1f) * 100f);
                _assetProgress.Value = pct;
                _assetStatus.Text = $"Downloading update... {pct}%";
            });

            var downloadedExe = await _gameReleaseUpdater.DownloadUpdateAsync(_gameUpdateCheck, progress, CancellationToken.None);
            _assetStatus.Text = "Scheduling replacement...";
            _assetProgress.Value = 100;

            var scriptPath = _gameReleaseUpdater.PrepareDeferredReplacement(downloadedExe, "--launcher");
            _gameReleaseUpdater.LaunchDeferredReplacementScript(scriptPath);
            _log.Info($"Deferred game update scheduled: {releaseTitle}");

            ShowGameUpdateNoticeDialog(
                "YOU MUST RESTART",
                $"The update has been staged for {releaseTitle}.\n\nThe launcher will now close so the new EXE can replace the current one and restart.",
                DColor.FromArgb(88, 190, 125));
            BeginInvoke(new Action(Close));
        }
        catch (Exception ex)
        {
            ShowAssetError("Game update failed.", ex.Message);
            _log.Warn($"Game update failed: {ex.Message}");
        }
        finally
        {
            _gameUpdateBusy = false;
            UpdateGameReleaseVisuals();
        }
    }

    private UpdateReminderChoice ShowGameUpdateReminderDialog(string releaseTitle)
    {
        using var dialog = new Form();
        dialog.Text = "Update Available";
        dialog.FormBorderStyle = FormBorderStyle.FixedDialog;
        dialog.StartPosition = FormStartPosition.CenterParent;
        dialog.ClientSize = new DSize(420, 170);
        dialog.MaximizeBox = false;
        dialog.MinimizeBox = false;
        dialog.ShowInTaskbar = false;
        dialog.BackColor = _settings.DarkMode ? DColor.FromArgb(18, 18, 18) : SystemColors.Control;
        dialog.ForeColor = _settings.DarkMode ? DColor.White : SystemColors.ControlText;

        var title = new Label
        {
            AutoSize = false,
            Bounds = new System.Drawing.Rectangle(16, 14, 388, 22),
            Font = new DFont(Font.FontFamily, 10f, System.Drawing.FontStyle.Bold),
            Text = "A new release is available."
        };

        var body = new Label
        {
            AutoSize = false,
            Bounds = new System.Drawing.Rectangle(16, 44, 388, 52),
            Text = $"Latest GitHub release: {releaseTitle}\n\nDo you want to update now?"
        };

        var updateNow = new Button
        {
            Text = "Update Now",
            Bounds = new System.Drawing.Rectangle(16, 118, 116, 32),
            DialogResult = DialogResult.Yes
        };
        var later = new Button
        {
            Text = "Later",
            Bounds = new System.Drawing.Rectangle(146, 118, 90, 32),
            DialogResult = DialogResult.No
        };
        var ignore = new Button
        {
            Text = "Don't Remind Me",
            Bounds = new System.Drawing.Rectangle(250, 118, 154, 32),
            DialogResult = DialogResult.Ignore
        };

        dialog.Controls.Add(title);
        dialog.Controls.Add(body);
        dialog.Controls.Add(updateNow);
        dialog.Controls.Add(later);
        dialog.Controls.Add(ignore);
        dialog.AcceptButton = updateNow;
        dialog.CancelButton = later;

        if (_settings.DarkMode)
        {
            foreach (Control control in dialog.Controls)
            {
                control.BackColor = dialog.BackColor;
                control.ForeColor = dialog.ForeColor;
            }

            ApplyButtonTheme(updateNow, DColor.FromArgb(88, 190, 125), DColor.White, DColor.FromArgb(110, 220, 150), DColor.FromArgb(76, 175, 110), DColor.FromArgb(60, 150, 95));
            ApplyButtonTheme(later, DColor.FromArgb(38, 38, 38), DColor.White, DColor.FromArgb(90, 90, 90), DColor.FromArgb(52, 52, 52), DColor.FromArgb(30, 30, 30));
            ApplyButtonTheme(ignore, DColor.FromArgb(110, 72, 28), DColor.White, DColor.FromArgb(170, 120, 60), DColor.FromArgb(130, 86, 34), DColor.FromArgb(90, 60, 24));
        }

        var result = dialog.ShowDialog(this);
        return result switch
        {
            DialogResult.Yes => UpdateReminderChoice.UpdateNow,
            DialogResult.Ignore => UpdateReminderChoice.IgnoreThisRelease,
            _ => UpdateReminderChoice.Later
        };
    }

    private void ShowGameUpdateNoticeDialog(string heading, string body, DColor accentColor)
    {
        using var dialog = new Form();
        dialog.Text = heading;
        dialog.FormBorderStyle = FormBorderStyle.FixedDialog;
        dialog.StartPosition = FormStartPosition.CenterParent;
        dialog.ClientSize = new DSize(470, 210);
        dialog.MaximizeBox = false;
        dialog.MinimizeBox = false;
        dialog.ShowInTaskbar = false;
        dialog.BackColor = _settings.DarkMode ? DColor.FromArgb(18, 18, 18) : SystemColors.Control;
        dialog.ForeColor = _settings.DarkMode ? DColor.White : SystemColors.ControlText;

        var headingLabel = new Label
        {
            AutoSize = false,
            Bounds = new System.Drawing.Rectangle(18, 16, 434, 32),
            Font = new DFont(Font.FontFamily, 12f, System.Drawing.FontStyle.Bold),
            ForeColor = accentColor,
            Text = heading
        };

        var bodyLabel = new Label
        {
            AutoSize = false,
            Bounds = new System.Drawing.Rectangle(18, 58, 434, 92),
            Font = new DFont(Font.FontFamily, 9.5f, System.Drawing.FontStyle.Bold),
            Text = body
        };

        var ok = new Button
        {
            Text = "OK",
            Bounds = new System.Drawing.Rectangle(336, 164, 116, 32),
            DialogResult = DialogResult.OK
        };

        dialog.Controls.Add(headingLabel);
        dialog.Controls.Add(bodyLabel);
        dialog.Controls.Add(ok);
        dialog.AcceptButton = ok;
        dialog.CancelButton = ok;

        if (_settings.DarkMode)
        {
            foreach (Control control in dialog.Controls)
            {
                control.BackColor = dialog.BackColor;
                if (!ReferenceEquals(control, headingLabel))
                    control.ForeColor = dialog.ForeColor;
            }

            ApplyButtonTheme(ok, DColor.FromArgb(38, 38, 38), DColor.White, DColor.FromArgb(90, 90, 90), DColor.FromArgb(52, 52, 52), DColor.FromArgb(30, 30, 30));
        }

        dialog.ShowDialog(this);
    }

    private void BuildAssetPanel()
    {
        _assetPanel.Dock = DockStyle.Fill;
        _assetPanel.Visible = false;
        _assetPanel.BackColor = DColor.FromArgb(32, 32, 32);

        _assetCard.Size = new DSize(560, 320);
        _assetCard.Padding = new Padding(16);
        _assetCard.BackColor = DColor.FromArgb(40, 40, 40);
        _assetCard.Anchor = AnchorStyles.None;

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 6,
        };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));  // title
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));  // status
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));  // detail
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 26)); // progress
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); // error
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));  // buttons

        _assetTitle.Text = "Checking Assets...";
        _assetTitle.Font = new DFont("Segoe UI", 12F, System.Drawing.FontStyle.Bold);
        _assetTitle.AutoSize = true;

        _assetStatus.Text = "Idle";
        _assetStatus.AutoSize = true;

        _assetDetail.Text = "";
        _assetDetail.AutoSize = true;

        _assetProgress.Dock = DockStyle.Fill;
        _assetProgress.Minimum = 0;
        _assetProgress.Maximum = 100;
        _assetProgress.Value = 0;
        _assetProgress.Style = ProgressBarStyle.Continuous;

        _assetErrorBox.Multiline = true;
        _assetErrorBox.ReadOnly = true;
        _assetErrorBox.ScrollBars = ScrollBars.Vertical;
        _assetErrorBox.Dock = DockStyle.Fill;
        _assetErrorBox.Visible = false;

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
            AutoSize = true
        };

        _assetCancelBtn.Text = "Cancel";
        _assetCancelBtn.Width = 120;
        _assetCancelBtn.Height = 34;
        _assetCancelBtn.Click += (_, _) => CancelAssetInstall();

        _assetRetryBtn.Text = "Retry";
        _assetRetryBtn.Width = 120;
        _assetRetryBtn.Height = 34;
        _assetRetryBtn.Enabled = false;
        _assetRetryBtn.Click += (_, _) => StartAssetCheckAndLaunch(_pendingLaunchArgs);

        _assetCopyBtn.Text = "Copy";
        _assetCopyBtn.Width = 120;
        _assetCopyBtn.Height = 34;
        _assetCopyBtn.Enabled = false;
        _assetCopyBtn.Click += (_, _) =>
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(_assetErrorBox.Text))
                    Clipboard.SetText(_assetErrorBox.Text);
            }
            catch { }
        };

        buttons.Controls.Add(_assetCancelBtn);
        buttons.Controls.Add(_assetRetryBtn);
        buttons.Controls.Add(_assetCopyBtn);

        layout.Controls.Add(_assetTitle, 0, 0);
        layout.Controls.Add(_assetStatus, 0, 1);
        layout.Controls.Add(_assetDetail, 0, 2);
        layout.Controls.Add(_assetProgress, 0, 3);
        layout.Controls.Add(_assetErrorBox, 0, 4);
        layout.Controls.Add(buttons, 0, 5);

        _assetCard.Controls.Add(layout);
        _assetPanel.Controls.Add(_assetCard);

        _assetPanel.Resize += (_, _) => CenterAssetCard();
        CenterAssetCard();
    }

    private void CenterAssetCard()
    {
        var x = Math.Max(0, (_assetPanel.Width - _assetCard.Width) / 2);
        var y = Math.Max(0, (_assetPanel.Height - _assetCard.Height) / 2);
        _assetCard.Location = new DPoint(x, y);
    }

    private DImage? LoadEmbeddedLogo()
    {
        try
        {
            var asm = Assembly.GetExecutingAssembly();
            // Match the exact manifest resource name (default: <RootNamespace>.<folder>.<file>)
            const string resName = "LatticeVeilMonoGame.Launcher.Resources.LauncherLogo.png";
            using var s = asm.GetManifestResourceStream(resName);
            if (s == null)
                return null;
            return DImage.FromStream(s);
        }
        catch
        {
            return null;
        }
    }




    private void SetHubLoggedOut(string? status = null)
    {
        _hubLoggedIn = false;
        _hubLoginBtn.Text = "LOGIN";
        _hubResetBtn.Enabled = false;
        _hubResetBtn.Visible = false;
        _hubStatusLabel.Text = status ?? "Online: Not logged in";
        RefreshVeilnetLoginVisuals();

        _usernameBox.Text = _profile.Username ?? string.Empty;
        UpdateUsernameLabel();
        UpdateOfflineNameEnabled();
    }
    private DImage? LoadProfileButtonImage()
    {
        try
        {
            var path = Path.Combine(Paths.AssetsDir, "textures", "menu", "buttons", "Profile.png");
            if (File.Exists(path))
                return LoadImageUnlocked(path);
        }
        catch (Exception ex)
        {
            _log.Warn($"Profile button load failed: {ex.Message}");
        }

        return null;
    }

    private DImage? LoadGoogleButtonImage()
    {
        try
        {
            var candidates = new[]
            {
                Path.Combine(Paths.AssetsDir, "textures", "menu", "buttons", "Google.png"),
                Path.Combine(Paths.AssetsDir, "textures", "menu", "buttons", "GoogleLogo.png"),
                Path.Combine(Paths.AssetsDir, "textures", "ui", "Google.png"),
                Path.Combine(Paths.AssetsDir, "textures", "ui", "GoogleLogo.png")
            };

            foreach (var path in candidates)
            {
                if (File.Exists(path))
                    return LoadImageUnlocked(path);
            }
        }
        catch (Exception ex)
        {
            _log.Warn($"Google button load failed: {ex.Message}");
        }

        return null;
    }

    /// <summary>
    /// Image.FromFile keeps the file handle open for the lifetime of the Image.
    /// That prevents the asset installer from replacing images in-place.
    /// Load into memory so we don't lock the underlying file.
    /// </summary>
    private static DImage? LoadImageUnlocked(string path)
    {
        try
        {
            var bytes = File.ReadAllBytes(path);
            using var ms = new MemoryStream(bytes);
            using var img = DImage.FromStream(ms);
            return (DImage)img.Clone();
        }
        catch
        {
            return null;
        }
    }

    private void ApplyIconImages(DColor iconColor, DColor accentColor)
    {
        _rocketIcon?.Dispose();
        _paperIcon?.Dispose();
        _folderIcon?.Dispose();
        _skullIcon?.Dispose();

        _rocketIcon = CreateRocketIcon(28, iconColor, accentColor);
        _paperIcon = CreatePaperIcon(24, iconColor);
        _folderIcon = CreateFolderIcon(24, iconColor);
        _skullIcon = CreateSkullIcon(24, iconColor);

        _openLogsBtn.Image = _paperIcon;
        _saveLogsBtn.Image = _paperIcon;
        _openGameFolderBtn.Image = _folderIcon;
    }

    private static DImage CreateRocketIcon(int size, DColor color, DColor flameColor)
    {
        var bmp = new Bitmap(size, size);
        using var g = System.Drawing.Graphics.FromImage(bmp);
        g.Clear(DColor.Transparent);
        g.SmoothingMode = SmoothingMode.AntiAlias;

        using var bodyBrush = new SolidBrush(color);
        using var flameBrush = new SolidBrush(flameColor);
        using var windowBrush = new SolidBrush(DColor.FromArgb(24, 24, 24));

        var w = size;
        var bodyRect = new RectangleF(w * 0.38f, w * 0.22f, w * 0.24f, w * 0.50f);
        g.FillEllipse(bodyBrush, bodyRect);

        var nose = new[]
        {
            new PointF(w * 0.50f, w * 0.06f),
            new PointF(w * 0.34f, w * 0.26f),
            new PointF(w * 0.66f, w * 0.26f)
        };
        g.FillPolygon(bodyBrush, nose);

        var finLeft = new[]
        {
            new PointF(w * 0.38f, w * 0.48f),
            new PointF(w * 0.22f, w * 0.62f),
            new PointF(w * 0.38f, w * 0.62f)
        };
        var finRight = new[]
        {
            new PointF(w * 0.62f, w * 0.48f),
            new PointF(w * 0.62f, w * 0.62f),
            new PointF(w * 0.78f, w * 0.62f)
        };
        g.FillPolygon(bodyBrush, finLeft);
        g.FillPolygon(bodyBrush, finRight);

        g.FillEllipse(windowBrush, new RectangleF(w * 0.46f, w * 0.36f, w * 0.08f, w * 0.08f));

        var flame = new[]
        {
            new PointF(w * 0.50f, w * 0.88f),
            new PointF(w * 0.42f, w * 0.72f),
            new PointF(w * 0.58f, w * 0.72f)
        };
        g.FillPolygon(flameBrush, flame);

        return bmp;
    }

    private static DImage CreateFolderIcon(int size, DColor color)
    {
        var bmp = new Bitmap(size, size);
        using var g = System.Drawing.Graphics.FromImage(bmp);
        g.Clear(DColor.Transparent);
        g.SmoothingMode = SmoothingMode.AntiAlias;

        using var brush = new SolidBrush(color);
        var w = size;
        var tab = new RectangleF(w * 0.12f, w * 0.22f, w * 0.36f, w * 0.18f);
        var body = new RectangleF(w * 0.08f, w * 0.30f, w * 0.84f, w * 0.50f);
        g.FillRectangle(brush, tab);
        g.FillRectangle(brush, body);

        return bmp;
    }

    private static DImage CreatePaperIcon(int size, DColor color)
    {
        var bmp = new Bitmap(size, size);
        using var g = System.Drawing.Graphics.FromImage(bmp);
        g.Clear(DColor.Transparent);
        g.SmoothingMode = SmoothingMode.AntiAlias;

        using var brush = new SolidBrush(color);
        using var pen = new Pen(DColor.FromArgb(160, color), 2f);
        var w = size;
        var rect = new RectangleF(w * 0.20f, w * 0.10f, w * 0.60f, w * 0.80f);
        g.FillRectangle(brush, rect);

        var foldX = rect.Right - rect.Width * 0.25f;
        var foldY = rect.Top + rect.Height * 0.25f;
        g.DrawLine(pen, rect.Right - 1, rect.Top, foldX, foldY);
        g.DrawLine(pen, foldX, foldY, rect.Right - 1, foldY);

        return bmp;
    }

    private static DImage CreateSkullIcon(int size, DColor color)
    {
        var bmp = new Bitmap(size, size);
        using var g = System.Drawing.Graphics.FromImage(bmp);
        g.Clear(DColor.Transparent);
        g.SmoothingMode = SmoothingMode.AntiAlias;

        using var brush = new SolidBrush(color);
        using var cutout = new SolidBrush(DColor.FromArgb(20, 20, 20));
        var w = size;

        var head = new RectangleF(w * 0.20f, w * 0.10f, w * 0.60f, w * 0.62f);
        g.FillEllipse(brush, head);

        var jaw = new RectangleF(w * 0.30f, w * 0.60f, w * 0.40f, w * 0.26f);
        g.FillRectangle(brush, jaw);

        g.FillEllipse(cutout, new RectangleF(w * 0.34f, w * 0.30f, w * 0.12f, w * 0.12f));
        g.FillEllipse(cutout, new RectangleF(w * 0.54f, w * 0.30f, w * 0.12f, w * 0.12f));
        g.FillRectangle(cutout, new RectangleF(w * 0.46f, w * 0.44f, w * 0.08f, w * 0.10f));

        return bmp;
    }

    private async Task BeginStartupOnlineChecks()
    {
        lock (_onlineValidationSync)
        {
            if (_onlineValidationInProgress)
                return;

            _onlineValidationInProgress = true;
            _launchReadiness = LaunchReadiness.Checking;
        }

        if (IsHandleCreated)
            BeginInvoke(new Action(() => SetLaunchButtonState(GetGameState())));

        try
        {
            await Task.Run(async () =>
            {
                _officialHashOk = false;
                _veilnetAuthOk = false;
                _gateTicketOk = false;
                _eosReadyOk = false;

                if (!HasValidVeilnetSessionForOnline(logFailures: true))
                {
                    TryClearVeilnetAuth(
                        "Startup online check: Veilnet session missing/expired; forcing signed-out state.",
                        clearPendingCodes: false,
                        updateStatus: false);
                    SetProgress(100, "Online auth required");
                    ApplyOnlineStartupState(
                        OnlineStartupState.Unauthorized,
                        "LOGIN REQUIRED: You're not signed in. Online features are unavailable. Switch to Offline mode.",
                        officialBuildVerified: false,
                        onlineServicesReachable: false);
                    return;
                }

                var authToken = (Environment.GetEnvironmentVariable("LV_VEILNET_ACCESS_TOKEN") ?? string.Empty).Trim();
                try
                {
                    var me = await GetVeilnetClient().GetMeAsync(authToken).ConfigureAwait(false);
                    _veilnetAuthOk = true;
                    if (!string.IsNullOrWhiteSpace(me.Username))
                        Environment.SetEnvironmentVariable("LV_VEILNET_USERNAME", me.Username);
                    await SyncVeilnetFriendsToProfileAsync("startup validation").ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _veilnetAuthOk = false;
                    _log.Warn($"Veilnet auth validation failed: {ex.Message}");
                    SetProgress(100, "Online auth failed");
                    ApplyOnlineStartupState(
                        OnlineStartupState.Unauthorized,
                        "Online auth failed. Please re-login. LAN/offline available.",
                        officialBuildVerified: false,
                        onlineServicesReachable: false);
                    return;
                }

                var channel = Paths.IsDevBuild ? "dev" : "release";
                SetProgress(20, "Computing local build hash...");

                if (!TryComputeCurrentExecutableHash(out var executablePath, out var localHash, out var hashError))
                {
                    SetProgress(100, "Online verification unavailable");
                    ApplyOnlineStartupState(
                        OnlineStartupState.ComputeFailed,
                        "Online services unavailable (cannot verify official build). Try again later. LAN/offline still available.",
                        officialBuildVerified: false,
                        onlineServicesReachable: false);
                    _log.Warn($"Startup hash compute failed: {hashError}");
                    return;
                }

                SetProgress(45, $"Verifying official {channel} build...");
                OfficialBuildVerifier.VerifyResult verify;
                try
                {
                    verify = await _officialBuildVerifier.VerifyHashAsync(channel, localHash).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _log.Warn($"Official hash verification exception: {ex.Message}");
                    SetProgress(100, "Online verification unavailable");
                    ApplyOnlineStartupState(
                        OnlineStartupState.ServiceUnavailable,
                        "Online services unavailable (cannot verify official build). Try again later. LAN/offline still available.",
                        officialBuildVerified: false,
                        onlineServicesReachable: false);
                    return;
                }

                if (!verify.Ok)
                {
                    var startupState = MapVerifierFailureToStartupState(verify.Failure);
                    var detail = startupState switch
                    {
                        OnlineStartupState.HashMismatch => "Unofficial build - online disabled. LAN/offline still available.",
                        OnlineStartupState.Unauthorized => "Online services unavailable (verification unauthorized). LAN/offline still available.",
                        OnlineStartupState.BadResponse => "Online services unavailable (invalid verification response). LAN/offline still available.",
                        _ => "Online services unavailable (cannot verify official build). Try again later. LAN/offline still available."
                    };

                    SetProgress(100, startupState == OnlineStartupState.HashMismatch ? "Unofficial build" : "Online verification unavailable");
                    ApplyOnlineStartupState(
                        startupState,
                        detail,
                        officialBuildVerified: false,
                        onlineServicesReachable: false);
                    _log.Warn($"Official hash verification failed ({verify.Failure}) channel={channel} exe={Path.GetFileName(executablePath)}: {verify.Message}");
                    return;
                }

                _officialHashOk = true;
                _log.Info($"Official hash verified for {channel} channel.");
                SetProgress(70, "Official build verified. Checking online services...");

                try
                {
                    var gate = OnlineGateClient.GetOrCreate();

                    var gateResult = gate.EnsureTicketWithStatus(_log, TimeSpan.FromSeconds(20), executablePath);
                    if (!gateResult.Ok)
                    {
                        _gateTicketOk = false;
                        var startupState = MapTicketStatusToState(gateResult.Status);
                        var detail = startupState switch
                        {
                            OnlineStartupState.HashMismatch => "Official build verified, but gate rejected this hash. Online temporarily unavailable. LAN/offline still available.",
                            OnlineStartupState.MisconfiguredEndpoint => "Online not configured (endpoint missing/wrong). LAN/offline available.",
                            OnlineStartupState.Unauthorized => "Online auth failed. Please re-login. LAN/offline available.",
                            OnlineStartupState.BadResponse => "Official build verified, but online services returned an invalid response. LAN/offline still available.",
                            _ => "Official build verified, but online services are unavailable right now. Try again later. LAN/offline still available."
                        };

                        var progressText = startupState == OnlineStartupState.MisconfiguredEndpoint
                            ? "Online endpoint misconfigured"
                            : "Online services unavailable";
                        SetProgress(100, progressText);
                        ApplyOnlineStartupState(
                            startupState,
                            detail,
                            officialBuildVerified: true,
                            onlineServicesReachable: false);
                        _log.Warn($"Gate ticket check failed ({gateResult.Status}): {gateResult.Message}");
                        return;
                    }

                    _gateTicketOk = true;
                    string? ticket = null;
                    DateTime ticketExpiresUtc;
                    if (gate.TryGetValidTicketForChildProcess(out ticket, out ticketExpiresUtc) && !string.IsNullOrWhiteSpace(ticket))
                    {
                        Environment.SetEnvironmentVariable("LV_GATE_TICKET", ticket);
                        Environment.SetEnvironmentVariable("LV_GATE_TICKET_EXPIRES_UTC", ticketExpiresUtc.ToString("o"));
                    }

                    SetProgress(85, "Checking EOS config...");
                    _eosReadyOk = EosConfig.HasPublicConfigSource() && EosConfig.HasSecretSource();
                    if (!_eosReadyOk)
                        _log.Info("EOS config will be hydrated by the launched game process.");

                    SetProgress(100, "Official online ready");
                    ApplyOnlineStartupState(
                        OnlineStartupState.Verified,
                        "Official build verified. Online services ready.",
                        officialBuildVerified: true,
                        onlineServicesReachable: true);
                }
                catch (Exception ex)
                {
                    _log.Error($"Error during startup checks: {ex.Message}");
                    SetProgress(100, "Online services unavailable");
                    ApplyOnlineStartupState(
                        OnlineStartupState.ServiceUnavailable,
                        "Official build verified, but online services are unavailable right now. Try again later. LAN/offline still available.",
                        officialBuildVerified: true,
                        onlineServicesReachable: false);
                }
            });
        }
        finally
        {
            lock (_onlineValidationSync)
                _onlineValidationInProgress = false;

            if (IsHandleCreated)
                BeginInvoke(new Action(() => SetLaunchButtonState(GetGameState())));
            else
                SetLaunchButtonState(GetGameState());
        }
    }

    private void ApplyOnlineStartupState(
        OnlineStartupState state,
        string detail,
        bool officialBuildVerified,
        bool onlineServicesReachable)
    {
        _officialBuildVerified = officialBuildVerified;
        _onlineServicesReachable = onlineServicesReachable;
        _releaseHashAllowed = officialBuildVerified;
        _onlineFunctional = officialBuildVerified && onlineServicesReachable;
        _officialHashOk = officialBuildVerified;
        if (!onlineServicesReachable)
            _gateTicketOk = false;
        if (_onlineFunctional)
            _eosReadyOk = true;
        _launchReadiness = _onlineFunctional
            ? LaunchReadiness.ReadyOnline
            : (state == OnlineStartupState.ComputeFailed ? LaunchReadiness.Failed : LaunchReadiness.ReadyOfflineOnly);
        _onlineStatusDetail = detail;
        _log.Info(
            $"Online startup state={state}; readiness={_launchReadiness}; official={_officialBuildVerified}; " +
            $"veilnetAuth={_veilnetAuthOk}; ticket={_gateTicketOk}; eosReady={_eosReadyOk}; " +
            $"services={_onlineServicesReachable}; functional={_onlineFunctional}; detail={_onlineStatusDetail}");
        ApplyOnlineStatusVisuals();
        BeginInvoke(new Action(() => SetLaunchButtonState(GetGameState())));
    }

    private static OnlineStartupState MapTicketStatusToState(OnlineGateClient.TicketCheckStatus status)
    {
        return status switch
        {
            OnlineGateClient.TicketCheckStatus.MisconfiguredEndpoint => OnlineStartupState.MisconfiguredEndpoint,
            OnlineGateClient.TicketCheckStatus.HashMismatch => OnlineStartupState.HashMismatch,
            OnlineGateClient.TicketCheckStatus.Unauthorized => OnlineStartupState.Unauthorized,
            OnlineGateClient.TicketCheckStatus.BadResponse => OnlineStartupState.BadResponse,
            OnlineGateClient.TicketCheckStatus.ServiceUnavailable => OnlineStartupState.ServiceUnavailable,
            _ => OnlineStartupState.ServiceUnavailable
        };
    }

    private static bool IsGameRunning()
    {
        try
        {
            using var _ = Mutex.OpenExisting(AppMutexes.GameMutexName);
            return true;
        }
        catch (WaitHandleCannotBeOpenedException)
        {
            return false;
        }
    }

    private bool TryLoadAllowedHashes(out List<string> hashes, out string source, out string error)
    {
        hashes = new List<string>();
        source = "none";
        error = string.Empty;

        var pathFromEnv = (Environment.GetEnvironmentVariable("LV_ALLOWLIST_PATH") ?? string.Empty).Trim();
        if (!string.IsNullOrWhiteSpace(pathFromEnv) && TryLoadAllowlistFromFile(pathFromEnv, out hashes, out error))
        {
            source = "LV_ALLOWLIST_PATH";
            return true;
        }

        var projectRoot = TryFindProjectRoot();
        if (!string.IsNullOrWhiteSpace(projectRoot))
        {
            var localRepoAllowlist = Path.Combine(projectRoot, "OnlineService", "allowlist.json");
            if (TryLoadAllowlistFromFile(localRepoAllowlist, out hashes, out error))
            {
                source = "OnlineService/allowlist.json";
                return true;
            }
        }

        var appBaseAllowlist = Path.Combine(AppContext.BaseDirectory, "allowlist.json");
        if (TryLoadAllowlistFromFile(appBaseAllowlist, out hashes, out error))
        {
            source = "AppBase/allowlist.json";
            return true;
        }

        var allowlistUrl = (Environment.GetEnvironmentVariable("LV_ALLOWLIST_URL") ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(allowlistUrl))
            allowlistUrl = DefaultAllowlistUrl;

        if (TryLoadAllowlistFromUrl(allowlistUrl, out hashes, out error))
        {
            source = allowlistUrl;
            return true;
        }

        return false;
    }

    private bool TryLoadAllowlistFromFile(string path, out List<string> hashes, out string error)
    {
        hashes = new List<string>();
        error = string.Empty;

        try
        {
            if (!File.Exists(path))
            {
                error = $"missing file {path}";
                return false;
            }

            var json = File.ReadAllText(path);
            var model = JsonSerializer.Deserialize<ReleaseAllowlist>(json, JsonOptions);
            if (model?.AllowedClientExeSha256 == null || model.AllowedClientExeSha256.Length == 0)
            {
                error = $"no hashes in {path}";
                return false;
            }

            for (var i = 0; i < model.AllowedClientExeSha256.Length; i++)
            {
                var value = (model.AllowedClientExeSha256[i] ?? string.Empty).Trim().ToLowerInvariant();
                if (string.IsNullOrWhiteSpace(value))
                    continue;
                hashes.Add(value);
            }

            if (hashes.Count == 0)
            {
                error = $"no valid hashes in {path}";
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private bool TryLoadAllowlistFromUrl(string url, out List<string> hashes, out string error)
    {
        hashes = new List<string>();
        error = string.Empty;

        if (string.IsNullOrWhiteSpace(url))
        {
            error = "url empty";
            return false;
        }

        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
            var json = client.GetStringAsync(url).GetAwaiter().GetResult();
            var model = JsonSerializer.Deserialize<ReleaseAllowlist>(json, JsonOptions);
            if (model?.AllowedClientExeSha256 == null || model.AllowedClientExeSha256.Length == 0)
            {
                error = "no hashes in url payload";
                return false;
            }

            for (var i = 0; i < model.AllowedClientExeSha256.Length; i++)
            {
                var value = (model.AllowedClientExeSha256[i] ?? string.Empty).Trim().ToLowerInvariant();
                if (string.IsNullOrWhiteSpace(value))
                    continue;
                hashes.Add(value);
            }

            if (hashes.Count == 0)
            {
                error = "no valid hashes in url payload";
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private static string? TryFindProjectRoot()
    {
        static string? FindFrom(string startPath)
        {
            try
            {
                var dir = new DirectoryInfo(startPath);
                for (var i = 0; i < 10 && dir != null; i++)
                {
                    if (File.Exists(Path.Combine(dir.FullName, "LatticeVeil.sln")))
                        return dir.FullName;
                    dir = dir.Parent;
                }
            }
            catch
            {
                // Ignore lookup failure.
            }

            return null;
        }

        string currentDir;
        try
        {
            currentDir = Directory.GetCurrentDirectory();
        }
        catch
        {
            currentDir = string.Empty;
        }

        var cwdRoot = FindFrom(currentDir);
        if (!string.IsNullOrWhiteSpace(cwdRoot))
            return cwdRoot;

        return FindFrom(AppContext.BaseDirectory);
    }

    private void ApplyOnlineStatusVisuals()
    {
        // Ensure UI updates happen on the UI thread
        if (InvokeRequired)
        {
            BeginInvoke(ApplyOnlineStatusVisuals);
            return;
        }

        _log.Info($"ApplyOnlineStatusVisuals: _onlineFunctional={_onlineFunctional}");
        
        _onlineHeader.Text = _onlineFunctional ? "ONLINE ACCESS: READY" : "ONLINE ACCESS: OFFLINE/LAN ONLY";
        _onlineHeader.ForeColor = _onlineFunctional ? DColor.LimeGreen : DColor.FromArgb(255, 110, 110);

        _onlineStatusIndicator.BackColor = _onlineFunctional ? DColor.LimeGreen : DColor.Red;
        _toolTip.SetToolTip(_onlineStatusIndicator, _onlineFunctional ? "Online access is available." : "Online access is unavailable. Offline/LAN only.");
        
        // Update username label based on online status
        UpdateUsernameLabel();
        
        // Veilnet login button must stay visible so users can recover from stale/expired auth.
        _hubGoogleBtn.Visible = true;
        RefreshVeilnetLoginVisuals();

        TryAutoLoginVeilnetFromEosPuid();
        
        _log.Info($"Status indicator color set to: {_onlineStatusIndicator.BackColor}");
    }

    private void UpdateHubStatus(string text)
    {
        // Ensure UI updates happen on the UI thread
        if (InvokeRequired)
        {
            BeginInvoke(() => UpdateHubStatus(text));
            return;
        }

        _onlineStatusDetail = text;
        _progressLabel.Text = string.IsNullOrWhiteSpace(text) ? "Idle" : text;
        ApplyOnlineStatusVisuals();
    }

    private async Task OnEpicLoginClicked()
    {
        if (_eosClient != null && _eosClient.IsLoggedIn)
        {
            await EpicLogoutAsync();
            return;
        }

        await EpicLoginAsync();
    }

    private Task EpicLoginAsync()
    {
        if (_eosClient != null && !string.IsNullOrWhiteSpace(_eosClient.LocalProductUserId))
        {
            var displayName = _eosClient.EpicDisplayName;
            if (string.IsNullOrWhiteSpace(displayName))
                displayName = MaskAccountDisplay(_eosClient.EpicAccountId ?? _eosClient.LocalProductUserId);
            UpdateHubStatus(string.IsNullOrWhiteSpace(displayName) ? "Online: already logged in" : $"Online: logged in as {displayName}");
            return Task.CompletedTask;
        }

        _epicLoginRequested = true;
        _epicDisplayNameShown = null;
        _hubLoginBtn.Text = "LOGIN...";
        _hubResetBtn.Visible = false;
        _hubResetBtn.Enabled = false;
        UpdateHubStatus("Online: opening login...");
        _log.Info("Online login requested from launcher.");

        _eosClient?.Dispose();
        _eosClient = EosClient.TryCreate(_log, "epic");
        if (_eosClient == null)
        {
            _hubLoginBtn.Text = "LOGIN";
            UpdateHubStatus("Online: login unavailable (EOS config missing).");
            _epicLoginRequested = false;
            _hubLoggedIn = false;
            UpdateUsernameLabel();
        }

        return Task.CompletedTask;
    }

    private async Task EpicLogoutAsync()
    {
        if (_eosClient == null)
        {
            SetHubLoggedOut("Online: Not logged in");
            _hubResetBtn.Visible = false;
            return;
        }

        _hubLoginBtn.Text = "LOGGING OUT...";
        _hubResetBtn.Enabled = false;
        UpdateHubStatus("Online: logging out...");

        var result = await _eosClient.LogoutAsync();
        if (!result.Ok && !string.IsNullOrWhiteSpace(result.Error))
            _log.Warn($"Online logout failed: {result.Error}");

        _eosClient.Dispose();
        _eosClient = null;
        _epicProductUserId = null;
        _epicDisplayNameShown = null;
        _epicLoginRequested = false;

        SetHubLoggedOut(result.Ok ? "Online: logged out" : (result.Error ?? "Online: logout failed."));
        _hubResetBtn.Visible = false;
        UpdateUsernameLabel();
    }

    private async Task EpicSwitchUserAsync()
    {
        if (_eosClient == null)
        {
            await EpicLoginAsync();
            return;
        }

        _hubResetBtn.Text = "SWITCHING...";
        _hubResetBtn.Enabled = false;
        UpdateHubStatus("Online: clearing saved login...");

        var clear = await _eosClient.DeletePersistentAuthAsync();
        if (!clear.Ok && !string.IsNullOrWhiteSpace(clear.Error))
            _log.Warn($"Online persistent auth clear failed: {clear.Error}");

        await EpicLogoutAsync();
        _hubResetBtn.Text = "SWITCH USER";
        await EpicLoginAsync();
    }

    private void UpdateEpicLoginStatus()
    {
        if (EpicLoginInGameOnly)
            return;

        if (_eosClient == null)
        {
            if (_epicLoginRequested)
                UpdateHubStatus("Online: waiting for login...");
            return;
        }

        var wasLoggedIn = _hubLoggedIn;
        var userId = _eosClient.LocalProductUserId;
        if (!string.IsNullOrWhiteSpace(userId))
        {
            var displayName = _eosClient.EpicDisplayName;
            if (string.IsNullOrWhiteSpace(displayName))
                displayName = MaskAccountDisplay(_eosClient.EpicAccountId ?? userId);

            _hubLoggedIn = true;
            _hubLoginBtn.Text = "LOGOUT";
            _hubResetBtn.Text = "SWITCH USER";
            _hubResetBtn.Visible = true;
            _hubResetBtn.Enabled = true;

            if (!string.Equals(userId, _epicProductUserId, StringComparison.Ordinal))
            {
                _epicProductUserId = userId;
                _epicDisplayNameShown = null;
            }

            TryUpdateEpicAccountName();
            if (!string.IsNullOrWhiteSpace(displayName) && !string.Equals(displayName, _epicDisplayNameShown, StringComparison.Ordinal))
            {
                _epicDisplayNameShown = displayName;
                UpdateHubStatus($"Online: logged in as {displayName}");
            }
            _epicLoginRequested = false;
            if (wasLoggedIn != _hubLoggedIn)
                UpdateUsernameLabel();
            return;
        }

        _hubLoggedIn = false;
        _hubResetBtn.Visible = false;
        _hubResetBtn.Enabled = false;
        _hubLoginBtn.Text = _epicLoginRequested ? "LOGIN..." : "LOGIN";
        if (_epicLoginRequested)
            UpdateHubStatus("Online: waiting for login...");
        else
            UpdateHubStatus("Online: Not logged in");
        if (wasLoggedIn != _hubLoggedIn)
            UpdateUsernameLabel();
    }

    private void TryUpdateEpicAccountName()
    {
        if (_eosClient == null)
            return;

        var displayName = _eosClient.EpicDisplayName;
        var accountId = MaskAccountDisplay(_eosClient.EpicAccountId ?? _eosClient.LocalProductUserId);
        var desired = !string.IsNullOrWhiteSpace(displayName)
            ? displayName.Trim()
            : accountId ?? string.Empty;

        if (string.IsNullOrWhiteSpace(desired))
            return;

        if (string.Equals(_profile.Username ?? string.Empty, desired, StringComparison.Ordinal))
            return;

        _profile.Username = desired;
        _profile.Save(_log);
        _usernameBox.Text = desired;
        UpdateUsernameLabel();
    }

    private string BuildOnlineLaunchArgs()
    {
        // EOS-only build: the game reads `Config/eos.local.json` directly and does not need hub args.
        return string.Empty;
    }

    private async void StartAssetCheckAndLaunch(string args, bool requireHashApproval = true)
    {
        if (_launching || _assetBusy)
            return;

        _pendingLaunchArgs = args ?? string.Empty;
        _pendingLaunchRequireHashApproval = requireHashApproval;
        _launching = true;
        _assetBusy = true;
        _assetPendingLaunch = true;
        _launchProgress = 0;
        UpdateProgressFill();

        _assetCts?.Cancel();
        _assetCts = new CancellationTokenSource();

        try
        {
            await RunAssetCheckAndLaunchAsync(_assetCts.Token);
        }
        finally
        {
            _launching = false;
        }
    }

    private async Task RunAssetCheckAndLaunchAsync(CancellationToken ct)
    {
        try
        {
            ShowAssetPanel("Checking Assets...", "Checking local files...", "");
            var isDevBuild = Paths.IsDevBuild;
            var targetAssetsDir = isDevBuild ? Paths.LocalAssetsDir : Paths.AssetsDir;
            var devAssetsMissing = false;
            if (isDevBuild && !Directory.Exists(Paths.LocalAssetsDir))
            {
                devAssetsMissing = true;
                targetAssetsDir = Paths.AssetsDir;
            }
            
            _assetInstaller.PreflightWriteAccess(targetAssetsDir);

            var hasAssets = _assetInstaller.CheckLocalAssetsInstalled(out var missing);
            _assetInstaller.EnsureLocalMarkerExists();

            if (isDevBuild)
            {
                if (!devAssetsMissing && hasAssets)
                {
                    HideAssetPanel();
                    _assetBusy = false;
                    _assetPendingLaunch = false;
                    LaunchGameProcess(_pendingLaunchArgs);
                    return;
                }

                // Dev build but local dev assets are missing: fall back to remote fetch/install like release.
                if (devAssetsMissing)
                    _log.Warn($"Dev assets folder missing: {Paths.LocalAssetsDir}. Falling back to remote asset install.");
            }

            AssetPackCheckResult? check = null;
            try
            {
                check = await _assetInstaller.CheckForUpdateAsync(true, ct);
            }
            catch (Exception ex)
            {
                _log.Warn($"Asset update check failed: {ex.Message}");
            }

            var release = check?.Release;
            var needsUpdate = !hasAssets || (release != null && !check!.IsUpToDate);

            if (!needsUpdate)
            {
                HideAssetPanel();
                _assetBusy = false;
                _assetPendingLaunch = false;
                LaunchGameProcess(_pendingLaunchArgs);
                return;
            }

            if (release == null || string.IsNullOrWhiteSpace(release.DownloadUrl))
            {
                var missingList = missing.Length == 0 ? "unknown" : string.Join(", ", missing);
                ShowAssetError("Assets missing and no download URL found.",
                    $"Missing: {missingList}");
                _assetBusy = false;
                _assetPendingLaunch = false;
                return;
            }

            var detail = hasAssets
                ? $"Update available: {release.Tag ?? "latest"}"
                : $"Missing: {(missing.Length == 0 ? "unknown" : string.Join(", ", missing))}";
            ShowAssetPanel(hasAssets ? "Updating Assets..." : "Downloading Assets...", "Preparing download...", detail);

            var progress = new Progress<float>(p =>
            {
                var pct = (int)Math.Round(Math.Clamp(p, 0f, 1f) * 100f);
                _assetProgress.Value = pct;
                _assetStatus.Text = $"Downloading... {pct}%";
            });

            var zipPath = await _assetInstaller.DownloadAssetsZipAsync(release.DownloadUrl, progress, ct);
            _assetStatus.Text = "Extracting...";
            _assetProgress.Value = 0;

            var extractProgress = new Progress<float>(p =>
            {
                var pct = (int)Math.Round(Math.Clamp(p, 0f, 1f) * 100f);
                _assetProgress.Value = pct;
                _assetStatus.Text = $"Extracting... {pct}%";
            });

            await _assetInstaller.ExtractZipAsync(zipPath, AssetPackInstaller.StagingDir, extractProgress, ct);

            _assetStatus.Text = "Installing...";
            _assetProgress.Value = 0;

            await Task.Run(() => _assetInstaller.InstallStagedAssets(AssetPackInstaller.StagingDir, targetAssetsDir), ct);
            _assetInstaller.WriteInstalledMarker(release);

            _assetStatus.Text = "Assets ready.";
            _assetProgress.Value = 100;
            HideAssetPanel();

            _assetBusy = false;
            if (_assetPendingLaunch)
            {
                _assetPendingLaunch = false;
                LaunchGameProcess(_pendingLaunchArgs, requireHashApproval: _pendingLaunchRequireHashApproval);
            }
        }
        catch (OperationCanceledException)
        {
            ShowAssetError("Asset download canceled.", "You can retry from the launcher.");
            _assetBusy = false;
            _assetPendingLaunch = false;
        }
        catch (Exception ex)
        {
            ShowAssetError("Asset update failed.", ex.Message);
            _assetBusy = false;
            _assetPendingLaunch = false;
        }
        finally
        {
            _assetInstaller.CleanupAfterInstall();
        }
    }

    private void LaunchGameProcess(string extraArgs, bool requireHashApproval = true)
    {
        // Verify hash was approved before allowing launch
        if (requireHashApproval && !_releaseHashAllowed)
        {
            var reason = string.IsNullOrWhiteSpace(_onlineStatusDetail)
                ? "Online launch is unavailable. LAN/offline is still available."
                : _onlineStatusDetail;
            MessageBox.Show(reason, "Online Unavailable", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            _log.Warn($"Launch blocked: official build not verified. reason={reason}");
            return;
        }
        
        // Use unified executable with renderer argument
        var exePath = Environment.ProcessPath ?? string.Empty;
        
        if (!File.Exists(exePath))
        {
            MessageBox.Show(
                $"Unable to locate the game executable:\n{exePath}",
                "Launch Failed",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
            return;
        }

        // The single-exe entry point defaults to launcher UI when started with no args.
        // Ensure we pass --game unless the caller already includes it.
        var normalized = (extraArgs ?? string.Empty).Trim();
        var hasGameArg = normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Any(a => string.Equals(a, "--game", StringComparison.OrdinalIgnoreCase));

        var args = hasGameArg
            ? normalized
            : (string.IsNullOrWhiteSpace(normalized) ? "--game" : $"--game {normalized}");

        // Add renderer argument to override settings
        if (!normalized.Contains("--renderer"))
        {
            args += $" --renderer={_settings.RendererBackend.ToLowerInvariant()}";
        }

        // Add render distance argument to override game setting
        if (!normalized.Contains("--render-distance"))
        {
            args += $" --render-distance={_settings.LauncherRenderDistance}";
        }

        var launchingOffline = args.Contains("--offline", StringComparison.OrdinalIgnoreCase);
        if (!launchingOffline && _launchReadiness != LaunchReadiness.ReadyOnline)
        {
            var reason = string.IsNullOrWhiteSpace(_onlineStatusDetail)
                ? "Online launch is locked until Veilnet validation succeeds."
                : _onlineStatusDetail;
            MessageBox.Show(
                reason,
                "Online Unavailable",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
            _log.Warn($"Launch blocked: launch readiness={_launchReadiness}; reason={reason}");
            return;
        }

        if (!launchingOffline && !VerifyOfficialBuildForOnline())
        {
            return;
        }

        try
        {
            if (!launchingOffline)
                WarmSocialSessionCacheBeforeLaunch();

            _log.Info($"Launching game process with renderer: {_settings.RendererBackend}...");
            
            var startInfo = new ProcessStartInfo
            {
                FileName = exePath,
                Arguments = args,
                WorkingDirectory = Path.GetDirectoryName(exePath) ?? string.Empty,
                UseShellExecute = false, // Required for environment variables
                CreateNoWindow = true
            };
            
            // Set environment variables for EOS authorization
            startInfo.EnvironmentVariables["LV_PROCESS_KIND"] = "game";
            startInfo.EnvironmentVariables["LV_LAUNCH_MODE"] = launchingOffline ? "offline" : "online";
            startInfo.EnvironmentVariables["LV_BUILD_CHANNEL"] = Paths.IsDevBuild ? "dev" : "release";
            var officialVerifiedForRun = !launchingOffline && _officialBuildVerified;
            var servicesReachableForRun = !launchingOffline && _onlineServicesReachable;
            startInfo.EnvironmentVariables["LV_OFFICIAL_BUILD_VERIFIED"] = officialVerifiedForRun ? "1" : "0";
            startInfo.EnvironmentVariables["LV_ONLINE_SERVICES_OK"] = servicesReachableForRun ? "1" : "0";
            startInfo.EnvironmentVariables["LV_LAUNCHER_ONLINE_AUTH"] =
                (officialVerifiedForRun && servicesReachableForRun) ? "1" : "0";

            var veilnetFunctionsBase = GetVeilnetFunctionsBaseUrl();
            startInfo.EnvironmentVariables["LV_VEILNET_FUNCTIONS_URL"] = veilnetFunctionsBase;

            var supabaseAnonKey = (Environment.GetEnvironmentVariable("LV_SUPABASE_ANON_KEY") ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(supabaseAnonKey))
                supabaseAnonKey = (Environment.GetEnvironmentVariable("SUPABASE_ANON_KEY") ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(supabaseAnonKey))
                supabaseAnonKey = DefaultSupabaseAnonKey;
            if (!string.IsNullOrWhiteSpace(supabaseAnonKey))
                startInfo.EnvironmentVariables["LV_SUPABASE_ANON_KEY"] = supabaseAnonKey;

            var veilnetUrl = (Environment.GetEnvironmentVariable("LV_VEILNET_URL") ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(veilnetUrl))
                veilnetUrl = "https://latticeveil.github.io/veilnet";
            startInfo.EnvironmentVariables["LV_VEILNET_URL"] = veilnetUrl;

            var veilnetUsername = (Environment.GetEnvironmentVariable("LV_VEILNET_USERNAME") ?? string.Empty).Trim();
            if (!launchingOffline && !string.IsNullOrWhiteSpace(veilnetUsername))
                startInfo.EnvironmentVariables["LV_VEILNET_USERNAME"] = veilnetUsername;
            else
                startInfo.EnvironmentVariables.Remove("LV_VEILNET_USERNAME");

            var veilnetToken = (Environment.GetEnvironmentVariable("LV_VEILNET_ACCESS_TOKEN") ?? string.Empty).Trim();
            if (!launchingOffline && !string.IsNullOrWhiteSpace(veilnetToken))
                startInfo.EnvironmentVariables["LV_VEILNET_ACCESS_TOKEN"] = veilnetToken;
            else
                startInfo.EnvironmentVariables.Remove("LV_VEILNET_ACCESS_TOKEN");

            if (!launchingOffline)
            {
                CopyPublicEosEnvironmentToChildProcess(startInfo);
                startInfo.EnvironmentVariables.Remove("EOS_DISABLED");
                startInfo.EnvironmentVariables.Remove("EOS_DISABLE");
            }
            else
                startInfo.EnvironmentVariables["EOS_DISABLED"] = "1";

            var hasTicketForRun = false;
            DateTime gateTicketExpiresUtc = DateTime.MinValue;
            string gateTicket = string.Empty;

            if (!launchingOffline
                && officialVerifiedForRun
                && servicesReachableForRun
                && OnlineGateClient.GetOrCreate().TryGetValidTicketForChildProcess(out gateTicket, out gateTicketExpiresUtc)
                && !string.IsNullOrWhiteSpace(gateTicket))
            {
                hasTicketForRun = true;
                startInfo.EnvironmentVariables["LV_GATE_TICKET"] = gateTicket;
                startInfo.EnvironmentVariables["LV_GATE_TICKET_EXPIRES_UTC"] = gateTicketExpiresUtc.ToString("o");
            }
            else
            {
                startInfo.EnvironmentVariables.Remove("LV_GATE_TICKET");
                startInfo.EnvironmentVariables.Remove("LV_GATE_TICKET_EXPIRES_UTC");
            }

            var hasVeilnetTokenForRun = !launchingOffline && !string.IsNullOrWhiteSpace(veilnetToken);
            if (!launchingOffline && (!hasVeilnetTokenForRun || !hasTicketForRun))
            {
                MessageBox.Show(
                    "Online auth context is missing. Please link/sign in and launch Online from the launcher.\n\nLAN/offline remains available.",
                    "Online Unavailable",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
                _log.Warn(
                    $"Online launch blocked due to missing auth context: hasVeilnetToken={hasVeilnetTokenForRun}; " +
                    $"hasGateTicket={hasTicketForRun}; official={officialVerifiedForRun}; services={servicesReachableForRun}");
                return;
            }

            _log.Info(
                $"Launch env snapshot: mode={(launchingOffline ? "offline" : "online")}; channel={(Paths.IsDevBuild ? "dev" : "release")}; " +
                $"official={officialVerifiedForRun}; services={servicesReachableForRun}; " +
                $"hasGateTicket={hasTicketForRun}; hasVeilnetToken={hasVeilnetTokenForRun}; hasUsername={!string.IsNullOrWhiteSpace(veilnetUsername)}");
            
            _gameProcess = Process.Start(startInfo);

            // If the user doesn't want the launcher to stay open, close it after a successful spawn.
            // Tray mode keeps the launcher reachable for kill/skin/menu actions.
            if (_gameProcess != null && !_settings.KeepLauncherOpen)
            {
                if (_settings.AlwaysMinimizeLauncherToTray)
                {
                    _log.Info("KeepLauncherOpen is false; minimizing launcher to tray after launching game.");
                    BeginInvoke(new Action(MinimizeLauncherToTray));
                }
                else
                {
                    _log.Info("KeepLauncherOpen is false; closing launcher after launching game.");
                    BeginInvoke(new Action(() =>
                    {
                        _allowLauncherExit = true;
                        Close();
                    }));
                }
            }
        }
        catch (Exception ex)
        {
            _log.Warn($"Failed to start game process: {ex.Message}");
            MessageBox.Show(
                "Failed to start the game process.",
                "Launch Failed",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }

        SetLaunchButtonState(GetGameState());
    }

    private void WarmSocialSessionCacheBeforeLaunch()
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(4));
            RuntimeSessionCache.ResetSocialSession(_log);
            StartupSocialCacheWarmer.WarmAsync(_log, _profile, cts.Token).GetAwaiter().GetResult();
            _log.Info("Launcher prewarmed social session cache.");
        }
        catch (OperationCanceledException)
        {
            _log.Warn("Launcher social session prewarm timed out.");
        }
        catch (Exception ex)
        {
            _log.Warn($"Launcher social session prewarm failed: {ex.Message}");
        }
    }

    private static void CopyPublicEosEnvironmentToChildProcess(ProcessStartInfo startInfo)
    {
        var keys = new[]
        {
            "EOS_PRODUCT_ID",
            "EOS_SANDBOX_ID",
            "EOS_DEPLOYMENT_ID",
            "EOS_CLIENT_ID",
            "EOS_PRODUCT_NAME",
            "EOS_PRODUCT_VERSION"
        };

        for (var i = 0; i < keys.Length; i++)
        {
            var key = keys[i];
            var value = (Environment.GetEnvironmentVariable(key) ?? string.Empty).Trim();
            if (!string.IsNullOrWhiteSpace(value))
                startInfo.EnvironmentVariables[key] = value;
        }
    }

    private void ConfirmAndKillGame()
    {
        var state = GetGameState();
        if (state == GameState.NotRunning)
            return;

        var result = MessageBox.Show(
            "The game is running. Kill it now?\n\nKilling the game may prevent saves from completing.",
            "Kill Game",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Warning);
        if (result != DialogResult.Yes)
            return;

        try
        {
            if (_gameProcess != null && !_gameProcess.HasExited)
            {
                _gameProcess.Kill(true);
                _gameProcess.Dispose();
                _gameProcess = null;
            }
            else
            {
                var pid = TryReadGamePid();
                if (pid.HasValue)
                {
                    var proc = Process.GetProcessById(pid.Value);
                    if (!proc.HasExited)
                        proc.Kill(true);
                    proc.Dispose();
                }
            }
        }
        catch (Exception ex)
        {
            _log.Warn($"Failed to close game: {ex.Message}");
        }

        SetLaunchButtonState(GetGameState());
    }

    private void RefreshGameProcessState()
    {
        SetLaunchButtonState(GetGameState());
    }

    private GameState GetGameState()
    {
        if (_gameProcess != null)
        {
            if (_gameProcess.HasExited)
            {
                _gameProcess.Dispose();
                _gameProcess = null;
                return GameState.NotRunning;
            }

            return GameState.RunningOwned;
        }

        var pid = TryReadGamePid();
        if (pid.HasValue)
        {
            try
            {
                var proc = Process.GetProcessById(pid.Value);
                if (!proc.HasExited)
                    return GameState.RunningExternal;
            }
            catch
            {
                // ignore
            }
        }

        return GameState.NotRunning;
    }

    private static int? TryReadGamePid()
    {
        try
        {
            if (!File.Exists(Paths.GamePidPath))
                return null;

            var text = File.ReadAllText(Paths.GamePidPath).Trim();
            return int.TryParse(text, out var pid) ? pid : null;
        }
        catch
        {
            return null;
        }
    }

    private void SetLaunchButtonState(GameState state)
    {
        switch (state)
        {
            case GameState.NotRunning:
                _launchBtn.Text = "LAUNCH";
                _launchModeBox.Enabled = true;
                var onlineMode = IsOnlineModeSelected();
                if (!onlineMode)
                {
                    _launchBtn.Enabled = true;
                    _toolTip.SetToolTip(_launchBtn, "Launch game");
                }
                else
                {
                    var readyOnline = _launchReadiness == LaunchReadiness.ReadyOnline;
                    _launchBtn.Enabled = readyOnline;
                    if (_onlineValidationInProgress || _launchReadiness == LaunchReadiness.Checking)
                        _toolTip.SetToolTip(_launchBtn, "Validating Veilnet...");
                    else if (!readyOnline)
                        _toolTip.SetToolTip(_launchBtn, "Online launch is locked until Veilnet validation succeeds.");
                    else
                        _toolTip.SetToolTip(_launchBtn, "Launch game");
                }
                if (_rocketIcon != null) { _launchBtn.Image = _rocketIcon; }
                break;
            default:
                _launchBtn.Text = "KILL";
                _launchBtn.Enabled = true;
                _launchModeBox.Enabled = false;
                if (_skullIcon != null) { _launchBtn.Image = _skullIcon; }
                _toolTip.SetToolTip(_launchBtn, "Kill game process");
                _progressLabel.Text = "Game running";
                break;
        }

        SetTrayLaunchGameItemState(state);
    }

    private void UpdateProgressFill()
    {
        var pct = Math.Clamp(_launchProgress, 0, 100);
        var width = _progressTrack.Width;
        var fill = (int)Math.Round(width * (pct / 100f));
        _progressFill.Width = Math.Max(0, Math.Min(width, fill));
    }

    private void SetProgress(int percent, string text)
    {
        if (IsHandleCreated)
        {
            BeginInvoke(new Action(() => {
                _launchProgress = percent;
                UpdateProgressFill();
                _progressLabel.Text = text;
            }));
        }
        else
        {
            // Handle is not created yet, update directly
            _launchProgress = percent;
            UpdateProgressFill();
            _progressLabel.Text = text;
        }
    }

    private void CancelAssetInstall()
    {
        try { _assetCts?.Cancel(); }
        catch { }

        _assetCts = null;
        _assetBusy = false;
        _assetPendingLaunch = false;
        _assetPanel.Visible = false;
    }

    private void ShowAssetPanel(string title, string status, string detail)
    {
        _assetTitle.Text = title;
        _assetStatus.Text = status;
        _assetDetail.Text = detail;
        _assetProgress.Value = 0;
        _assetErrorBox.Visible = false;
        _assetErrorBox.Text = string.Empty;
        _assetRetryBtn.Enabled = false;
        _assetCopyBtn.Enabled = false;
        _assetPanel.Visible = true;
    }

    private void ShowAssetError(string title, string message)
    {
        _assetTitle.Text = title;
        _assetStatus.Text = "Error";
        _assetDetail.Text = "";
        _assetProgress.Value = 0;
        _assetErrorBox.Text = message;
        _assetErrorBox.Visible = true;
        _assetRetryBtn.Enabled = true;
        _assetCopyBtn.Enabled = true;
        _assetPanel.Visible = true;
    }

    private void HideAssetPanel()
    {
        _assetPanel.Visible = false;
        _assetRetryBtn.Enabled = false;
        _assetCopyBtn.Enabled = false;
        _assetErrorBox.Visible = false;
        _assetErrorBox.Text = string.Empty;
    }

    private void UpdateUsernameLabel()
    {
        // Ensure UI updates happen on the UI thread
        if (InvokeRequired)
        {
            BeginInvoke(UpdateUsernameLabel);
            return;
        }

        _usernameLabel.Text = string.Empty;
        _offlineNameLabel.Text = "OFFLINE USERNAME";
    }

    private void UpdateOfflineNameEnabled()
    {
        var offlineMode = !IsOnlineModeSelected();
        _offlineNameBox.Enabled = offlineMode;
        _offlineNameBox.ReadOnly = !offlineMode;
        if (!_offlineNameBox.Focused)
            _offlineNameBox.Text = _profile.OfflineUsername ?? string.Empty;
    }

    private void SaveOfflineNameFromUi()
    {
        if (IsOnlineModeSelected())
        {
            _offlineNameBox.Text = _profile.OfflineUsername ?? string.Empty;
            return;
        }

        var desired = Truncate(_offlineNameBox.Text, 24).Trim();
        if (string.IsNullOrWhiteSpace(desired))
        {
            _offlineNameBox.Text = _profile.OfflineUsername ?? string.Empty;
            return;
        }

        if (string.Equals(_profile.OfflineUsername ?? string.Empty, desired, StringComparison.Ordinal))
            return;

        _profile.OfflineUsername = desired;
        _profile.Save(_log);
        _offlineNameBox.Text = desired;
        _log.Info($"Offline username updated: {desired}");
    }

    private void UpdateIdentityModeVisuals()
    {
        if (InvokeRequired)
        {
            BeginInvoke(UpdateIdentityModeVisuals);
            return;
        }

        var offlineMode = !IsOnlineModeSelected();
        if (offlineMode)
        {
            _hubResetBtn.Visible = false;
            RefreshOfflineIdentityVisuals();
        }
        else
        {
            RefreshVeilnetLoginVisuals();
        }

        UpdateOfflineNameEnabled();
    }

    private static string GenerateOfflineUsername()
    {
        var bytes = new byte[4];
        RandomNumberGenerator.Fill(bytes);
        var suffix = Convert.ToHexString(bytes).ToLowerInvariant();
        return "player_" + suffix;
    }

    private static string Truncate(string? text, int max)
    {
        if (string.IsNullOrWhiteSpace(text))
            return "";

        return text.Length <= max ? text : text.Substring(0, max);
    }

    private void SaveProfile()
    {
        SaveLauncherSettings();
        _profile.Save(_log);
    }

    private void OpenLogsFolder()
    {
        try
        {
            Directory.CreateDirectory(Paths.LogsDir);
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"\"{Paths.LogsDir}\"",
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            _log.Warn($"Failed to open logs folder: {ex.Message}");
        }
    }

    private void OpenGameFolder()
    {
        try
        {
            Directory.CreateDirectory(Paths.RootDir);
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"\"{Paths.RootDir}\"",
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            _log.Error($"Failed to open game folder: {ex.Message}");
        }
    }

    private void UpdateRecommendedLabel()
    {
        // Fixed recommended render distance for OpenGL
        var recommended = 16; // Recommended render distance for OpenGL
        
        _recommendedLabel.Text = $"Recommended: {recommended}";
        _recommendedLabel.ForeColor = System.Drawing.Color.Green;
        _recommendedLabel.AutoSize = true;
    }

    private void SaveLauncherSettings()
    {
        var merged = GameSettings.LoadOrCreate(_log);
        merged.KeepLauncherOpen = _keepOpenBox.Checked;
        merged.DarkMode = _darkModeBox.Checked;
        merged.IgnoredGameReleaseTitle = _settings.IgnoredGameReleaseTitle;
        merged.Save(_log);
        _settings.KeepLauncherOpen = merged.KeepLauncherOpen;
        _settings.DarkMode = merged.DarkMode;
        _settings.IgnoredGameReleaseTitle = merged.IgnoredGameReleaseTitle;
    }

    private void SaveLogsSnapshot()
    {
        if (_log.TrySaveSnapshot(out var savedPath, out var error))
        {
            _log.Info($"Saved log snapshot: {savedPath}");
            MessageBox.Show(
                $"Saved logs to:\n{savedPath}",
                "Logs Saved",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            return;
        }

        var message = string.IsNullOrWhiteSpace(error) ? "Failed to save logs." : error;
        _log.Warn($"Save logs failed: {message}");
        MessageBox.Show(
            message,
            "Save Logs",
            MessageBoxButtons.OK,
            MessageBoxIcon.Warning);
    }

    private void PerformBackup()
    {
        try
        {
            var backupsDir = Paths.BackupsDir;
            Directory.CreateDirectory(backupsDir);

            var timestamp = DateTime.Now.ToString("yyyyMMdd-HHmm");
            var zipPath = Path.Combine(backupsDir, $"backup-{timestamp}.zip");

            // Simple backup: just the Worlds folders if they exist.
            if (File.Exists(zipPath)) File.Delete(zipPath);

            using (var archive = System.IO.Compression.ZipFile.Open(zipPath, System.IO.Compression.ZipArchiveMode.Create))
            {
                void AddDir(string dirPath, string entryPath)
                {
                    if (!Directory.Exists(dirPath)) return;
                    foreach (var file in Directory.GetFiles(dirPath, "*", SearchOption.AllDirectories))
                    {
                        var relative = Path.GetRelativePath(dirPath, file);
                        archive.CreateEntryFromFile(file, Path.Combine(entryPath, relative));
                    }
                }

                AddDir(Paths.WorldsDir, "Worlds");
                AddDir(Paths.MultiplayerWorldsDir, "_OnlineCache");
                
                if (File.Exists(Paths.SettingsJsonPath))
                    archive.CreateEntryFromFile(Paths.SettingsJsonPath, "options.lvc");
                if (File.Exists(Paths.PlayerProfileJsonPath))
                    archive.CreateEntryFromFile(Paths.PlayerProfileJsonPath, "player_profile.lvc");
            }

            // Retention: keep last 10
            var files = Directory.GetFiles(backupsDir, "backup-*.zip")
                .Select(f => new FileInfo(f))
                .OrderByDescending(f => f.CreationTime)
                .Skip(10);
            foreach (var old in files) old.Delete();

            MessageBox.Show($"Backup created successfully:\n{Path.GetFileName(zipPath)}", "Backup", MessageBoxButtons.OK, MessageBoxIcon.Information);
            _log.Info($"World backup created: {zipPath}");
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Backup failed");
            MessageBox.Show($"Backup failed: {ex.Message}", "Backup Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void UpdateLogBox()
    {
        if (!ReadLogUpdates())
            return;

        _logBox.Text = string.Join(Environment.NewLine, _logTail);
        _logBox.SelectionStart = _logBox.TextLength;
        _logBox.ScrollToCaret();
    }

    private bool ReadLogUpdates()
    {
        var changed = false;
        var path = _log.LogFilePath;
        if (!string.Equals(path, _logFilePath, StringComparison.OrdinalIgnoreCase))
        {
            _logFilePath = path;
            _logReadPosition = 0;
            _logTail.Clear();
            changed = true;
            ResetLogSessionDate(path);
        }

        if (!File.Exists(path))
            return changed;

        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);

            if (fs.Length < _logReadPosition)
            {
                _logReadPosition = 0;
                _logTail.Clear();
                changed = true;
                ResetLogSessionDate(path);
            }

            fs.Seek(_logReadPosition, SeekOrigin.Begin);
            using var reader = new StreamReader(fs);

            string? line;
            while ((line = reader.ReadLine()) != null)
            {
                AddLogLine(line);
                changed = true;
            }

            _logReadPosition = fs.Position;
            return changed;
        }
        catch (Exception ex)
        {
            _log.Warn($"Log tail read failed: {ex.Message}");
            return false;
        }
    }

    private void AddLogLine(string line)
    {
        var trimmed = FormatLogLineForDisplay(line);
        if (trimmed.Length > LogLineMaxChars)
            trimmed = trimmed.Substring(0, LogLineMaxChars) + "...";

        _logTail.Enqueue(trimmed);
        while (_logTail.Count > LogTailMaxLines)
            _logTail.Dequeue();
    }

    private string FormatLogLineForDisplay(string line)
    {
        if (!TryParseLogTime(line, out var hour, out var minute, out var second))
            return line.TrimEnd('\r', '\n');

        var rest = line.Length > 9 ? line.Substring(9) : string.Empty;
        var baseDate = _logSessionDateSet ? _logSessionDate : DateTime.Today;
        var localTime = new DateTime(baseDate.Year, baseDate.Month, baseDate.Day, hour, minute, second, DateTimeKind.Unspecified);
        var offset = TimeZoneInfo.Local.GetUtcOffset(localTime);
        var localStamp = new DateTimeOffset(localTime, offset);

        var display24 = localStamp.ToString("yyyy-MM-dd HH:mm:ss zzz");
        var display12 = localStamp.ToString("h:mm:ss tt");
        return $"{display24} ({display12}) {rest}".TrimEnd('\r', '\n');
    }

    private static bool TryParseLogTime(string line, out int hour, out int minute, out int second)
    {
        hour = 0;
        minute = 0;
        second = 0;

        if (line.Length < 9)
            return false;

        if (line[2] != ':' || line[5] != ':' || line[8] != ' ')
            return false;

        if (!int.TryParse(line.Substring(0, 2), out hour))
            return false;
        if (!int.TryParse(line.Substring(3, 2), out minute))
            return false;
        if (!int.TryParse(line.Substring(6, 2), out second))
            return false;

        if (hour < 0 || hour > 23 || minute < 0 || minute > 59 || second < 0 || second > 59)
            return false;

        return true;
    }

    private void ResetLogSessionDate(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                _logSessionDate = File.GetCreationTime(path).Date;
                _logSessionDateSet = true;
                return;
            }
        }
        catch
        {
            // Fall through to default.
        }

        _logSessionDate = DateTime.Today;
        _logSessionDateSet = true;
    }

    private static void ApplyButtonTheme(
        Button button,
        DColor backColor,
        DColor foreColor,
        DColor borderColor,
        DColor hoverColor,
        DColor downColor)
    {
        if (!button.Font.Style.HasFlag(System.Drawing.FontStyle.Bold))
            button.Font = new DFont(button.Font, System.Drawing.FontStyle.Bold);
        button.UseVisualStyleBackColor = false;
        button.FlatStyle = FlatStyle.Flat;
        button.FlatAppearance.BorderSize = 1;
        button.FlatAppearance.BorderColor = borderColor;
        button.FlatAppearance.MouseOverBackColor = hoverColor;
        button.FlatAppearance.MouseDownBackColor = downColor;
        button.BackColor = backColor;
        button.ForeColor = foreColor;
    }

    private void ApplyTrayTheme(bool dark)
    {
        if (_trayMenu == null)
            return;

        var menuBack = dark ? DColor.FromArgb(18, 18, 18) : System.Drawing.SystemColors.Control;
        var menuFore = dark ? DColor.White : System.Drawing.SystemColors.ControlText;
        var hostBack = dark ? DColor.FromArgb(14, 14, 14) : System.Drawing.SystemColors.Control;
        var buttonBack = dark ? DColor.FromArgb(34, 34, 34) : System.Drawing.SystemColors.ControlLight;
        var buttonHover = dark ? DColor.FromArgb(48, 48, 48) : System.Drawing.SystemColors.Control;
        var buttonDown = dark ? DColor.FromArgb(24, 24, 24) : System.Drawing.SystemColors.ControlDark;
        var border = dark ? DColor.FromArgb(84, 84, 84) : System.Drawing.SystemColors.ControlDark;
        var divider = dark ? DColor.FromArgb(92, 92, 92) : System.Drawing.SystemColors.ControlDark;

        _trayMenu.BackColor = menuBack;
        _trayMenu.ForeColor = menuFore;
        foreach (ToolStripItem item in _trayMenu.Items)
        {
            item.BackColor = menuBack;
            item.ForeColor = menuFore;
        }

        if (_trayVeilnetAccountHost?.Control is Control host)
        {
            host.BackColor = hostBack;
            host.ForeColor = menuFore;
        }

        if (_trayVeilnetAvatarBox != null)
        {
            _trayVeilnetAvatarBox.BackColor = dark ? DColor.FromArgb(22, 22, 22) : System.Drawing.SystemColors.ControlDark;
            _trayVeilnetAvatarBox.ForeColor = menuFore;
        }

        if (_trayVeilnetDivider != null)
            _trayVeilnetDivider.BackColor = divider;

        if (_trayVeilnetAuthButton != null)
            ApplyButtonTheme(_trayVeilnetAuthButton, buttonBack, menuFore, border, buttonHover, buttonDown);

        if (_trayVeilnetProfileButton != null)
            ApplyButtonTheme(_trayVeilnetProfileButton, buttonBack, menuFore, border, buttonHover, buttonDown);
    }

    private void ApplyTheme(bool dark)
    {
        if (dark)
        {
            var baseBack = DColor.FromArgb(12, 12, 12);
            var panelBack = DColor.FromArgb(8, 8, 8);
            var inputBack = DColor.FromArgb(16, 16, 16);
            var buttonBack = DColor.FromArgb(38, 38, 38);
            var buttonHover = DColor.FromArgb(52, 52, 52);
            var buttonDown = DColor.FromArgb(30, 30, 30);
            var buttonBorder = DColor.FromArgb(90, 90, 90);
            var textColor = DColor.White;

            BackColor = baseBack;
            ForeColor = textColor;
            _root.BackColor = BackColor;
            _topBar.BackColor = panelBack;
            _title.ForeColor = ForeColor;
            _releaseTitleLabel.BackColor = panelBack;
            foreach (Control c in _right.Controls)
            {
                c.BackColor = BackColor;
                c.ForeColor = ForeColor;
            }
            _hubVeilnetAccountRow.BackColor = BackColor;
            _hubVeilnetInfoPanel.BackColor = BackColor;
            _hubVeilnetUserLabel.BackColor = BackColor;
            _skinLauncherPanel.BackColor = BackColor;
            _skinLauncherTitle.BackColor = BackColor;
            _skinLauncherTitle.ForeColor = ForeColor;
            _offlineNameLabel.BackColor = BackColor;
            _offlineNameLabel.ForeColor = ForeColor;
            _hubVeilnetAvatarBox.BackColor = DColor.FromArgb(20, 20, 20);
            _hubVeilnetAvatarBox.ForeColor = ForeColor;
            _skinLauncherPreviewBox.BackColor = DColor.FromArgb(20, 20, 20);
            _skinLauncherPreviewBox.ForeColor = ForeColor;

            foreach (Control c in _buttons.Controls)
            {
                if (c is Button button)
                    ApplyButtonTheme(button, buttonBack, textColor, buttonBorder, buttonHover, buttonDown);
                else
                    c.BackColor = BackColor;
            }
            _bottomLeftHost.BackColor = BackColor;
            _launchModeHost.BackColor = BackColor;

            _closeBtn.BackColor = DColor.FromArgb(32, 32, 32);
            _closeBtn.ForeColor = ForeColor;
            _minBtn.BackColor = DColor.FromArgb(32, 32, 32);
            _minBtn.ForeColor = ForeColor;
            ApplyButtonTheme(_updateBtn, DColor.FromArgb(38, 38, 38), textColor, buttonBorder, buttonHover, buttonDown);
            ApplyButtonTheme(_launchBtn, buttonBack, textColor, buttonBorder, buttonHover, buttonDown);

            _logBox.BackColor = inputBack;
            _logBox.ForeColor = ForeColor;

            _usernameBox.BackColor = inputBack;
            _usernameBox.ForeColor = ForeColor;
            _offlineNameBox.BackColor = inputBack;
            _offlineNameBox.ForeColor = ForeColor;
            _launchModeBox.BackColor = inputBack;
            _launchModeBox.ForeColor = ForeColor;
            _launchModeBox.FlatStyle = FlatStyle.Flat;
            _progressHost.BackColor = BackColor;
            _progressLabel.ForeColor = DColor.FromArgb(230, 240, 230);
            _progressLabel.BackColor = BackColor;
            _progressTrack.BackColor = DColor.FromArgb(30, 30, 30);
            _progressFill.BackColor = DColor.FromArgb(88, 190, 125);
            _onlineHeader.BackColor = BackColor;
            _onlineStatusIndicator.BackColor = _onlineFunctional ? DColor.LimeGreen : DColor.Red;
            _keepOpenBox.ApplyLauncherTheme(true);
            _darkModeBox.ApplyLauncherTheme(true);

            _assetPanel.BackColor = DColor.FromArgb(10, 10, 10);
            _assetCard.BackColor = DColor.FromArgb(28, 28, 28);
            _assetTitle.ForeColor = ForeColor;
            _assetStatus.ForeColor = ForeColor;
            _assetDetail.ForeColor = ForeColor;
            _assetErrorBox.BackColor = inputBack;
            _assetErrorBox.ForeColor = ForeColor;

            ApplyButtonTheme(_saveOfflineNameBtn, buttonBack, textColor, buttonBorder, buttonHover, buttonDown);
            ApplyButtonTheme(_openGameFolderBtn, buttonBack, textColor, buttonBorder, buttonHover, buttonDown);
            ApplyButtonTheme(_hubLoginBtn, buttonBack, textColor, buttonBorder, buttonHover, buttonDown);
            ApplyButtonTheme(_hubGoogleBtn, DColor.FromArgb(18, 58, 42), textColor, DColor.FromArgb(58, 140, 102), DColor.FromArgb(26, 78, 56), DColor.FromArgb(14, 46, 34));
            ApplyButtonTheme(_hubResetBtn, buttonBack, textColor, buttonBorder, buttonHover, buttonDown);
            ApplyButtonTheme(_skinsBtn, buttonBack, textColor, buttonBorder, buttonHover, buttonDown);
            ApplyButtonTheme(_assetCancelBtn, buttonBack, textColor, buttonBorder, buttonHover, buttonDown);
            ApplyButtonTheme(_assetRetryBtn, buttonBack, textColor, buttonBorder, buttonHover, buttonDown);
            ApplyButtonTheme(_assetCopyBtn, buttonBack, textColor, buttonBorder, buttonHover, buttonDown);
            ApplyIconImages(textColor, DColor.FromArgb(88, 190, 125));
        }
        else
        {
            BackColor = System.Drawing.SystemColors.Control;
            ForeColor = System.Drawing.SystemColors.ControlText;
            _root.BackColor = BackColor;
            _topBar.BackColor = System.Drawing.SystemColors.ControlLight;
            _title.ForeColor = ForeColor;
            _releaseTitleLabel.BackColor = _topBar.BackColor;
            foreach (Control c in _right.Controls)
            {
                c.BackColor = BackColor;
                c.ForeColor = ForeColor;
            }
            _hubVeilnetAccountRow.BackColor = BackColor;
            _hubVeilnetInfoPanel.BackColor = BackColor;
            _hubVeilnetUserLabel.BackColor = BackColor;
            _skinLauncherPanel.BackColor = BackColor;
            _skinLauncherTitle.BackColor = BackColor;
            _skinLauncherTitle.ForeColor = ForeColor;
            _offlineNameLabel.BackColor = BackColor;
            _offlineNameLabel.ForeColor = ForeColor;
            _hubVeilnetAvatarBox.BackColor = System.Drawing.SystemColors.ControlDark;
            _hubVeilnetAvatarBox.ForeColor = ForeColor;
            _skinLauncherPreviewBox.BackColor = System.Drawing.SystemColors.ControlDark;
            _skinLauncherPreviewBox.ForeColor = ForeColor;

            foreach (Control c in _buttons.Controls)
            {
                if (c is Button button)
                    ApplyButtonTheme(
                        button,
                        System.Drawing.SystemColors.ControlLight,
                        System.Drawing.SystemColors.ControlText,
                        System.Drawing.SystemColors.ControlDark,
                        System.Drawing.SystemColors.Control,
                        System.Drawing.SystemColors.ControlDark);
                else
                    c.BackColor = BackColor;
            }
            _bottomLeftHost.BackColor = BackColor;
            _launchModeHost.BackColor = BackColor;

            _closeBtn.BackColor = System.Drawing.SystemColors.Control;
            _closeBtn.ForeColor = System.Drawing.SystemColors.ControlText;
            _minBtn.BackColor = System.Drawing.SystemColors.Control;
            _minBtn.ForeColor = System.Drawing.SystemColors.ControlText;
            ApplyButtonTheme(
                _updateBtn,
                System.Drawing.SystemColors.ControlLight,
                System.Drawing.SystemColors.ControlText,
                System.Drawing.SystemColors.ControlDark,
                System.Drawing.SystemColors.Control,
                System.Drawing.SystemColors.ControlDark);
            ApplyButtonTheme(
                _launchBtn,
                System.Drawing.SystemColors.ControlLight,
                System.Drawing.SystemColors.ControlText,
                System.Drawing.SystemColors.ControlDark,
                System.Drawing.SystemColors.Control,
                System.Drawing.SystemColors.ControlDark);

            _logBox.BackColor = System.Drawing.SystemColors.Window;
            _logBox.ForeColor = System.Drawing.SystemColors.WindowText;

            _usernameBox.BackColor = System.Drawing.SystemColors.Window;
            _usernameBox.ForeColor = System.Drawing.SystemColors.WindowText;
            _offlineNameBox.BackColor = System.Drawing.SystemColors.Window;
            _offlineNameBox.ForeColor = System.Drawing.SystemColors.WindowText;
            _launchModeBox.BackColor = System.Drawing.SystemColors.Window;
            _launchModeBox.ForeColor = System.Drawing.SystemColors.WindowText;
            _launchModeBox.FlatStyle = FlatStyle.Flat;
            _progressHost.BackColor = BackColor;
            _progressLabel.ForeColor = DColor.FromArgb(30, 90, 60);
            _progressLabel.BackColor = BackColor;
            _progressTrack.BackColor = DColor.FromArgb(210, 210, 210);
            _progressFill.BackColor = DColor.FromArgb(86, 170, 120);
            _onlineHeader.BackColor = BackColor;
            _onlineStatusIndicator.BackColor = _onlineFunctional ? DColor.LimeGreen : DColor.Red;
            _keepOpenBox.ApplyLauncherTheme(false);
            _darkModeBox.ApplyLauncherTheme(false);

            _assetPanel.BackColor = DColor.FromArgb(230, 230, 230);
            _assetCard.BackColor = DColor.FromArgb(250, 250, 250);
            _assetTitle.ForeColor = ForeColor;
            _assetStatus.ForeColor = ForeColor;
            _assetDetail.ForeColor = ForeColor;
            _assetErrorBox.BackColor = System.Drawing.SystemColors.Window;
            _assetErrorBox.ForeColor = ForeColor;

            ApplyButtonTheme(
                _saveOfflineNameBtn,
                System.Drawing.SystemColors.ControlLight,
                System.Drawing.SystemColors.ControlText,
                System.Drawing.SystemColors.ControlDark,
                System.Drawing.SystemColors.Control,
                System.Drawing.SystemColors.ControlDark);
            ApplyButtonTheme(
                _openGameFolderBtn,
                System.Drawing.SystemColors.ControlLight,
                System.Drawing.SystemColors.ControlText,
                System.Drawing.SystemColors.ControlDark,
                System.Drawing.SystemColors.Control,
                System.Drawing.SystemColors.ControlDark);
            ApplyButtonTheme(
                _hubLoginBtn,
                System.Drawing.SystemColors.ControlLight,
                System.Drawing.SystemColors.ControlText,
                System.Drawing.SystemColors.ControlDark,
                System.Drawing.SystemColors.Control,
                System.Drawing.SystemColors.ControlDark);
            ApplyButtonTheme(
                _hubGoogleBtn,
                System.Drawing.SystemColors.ControlLight,
                System.Drawing.SystemColors.ControlText,
                System.Drawing.SystemColors.ControlDark,
                System.Drawing.SystemColors.Control,
                System.Drawing.SystemColors.ControlDark);
            ApplyButtonTheme(
                _hubResetBtn,
                System.Drawing.SystemColors.ControlLight,
                System.Drawing.SystemColors.ControlText,
                System.Drawing.SystemColors.ControlDark,
                System.Drawing.SystemColors.Control,
                System.Drawing.SystemColors.ControlDark);
            ApplyButtonTheme(
                _skinsBtn,
                System.Drawing.SystemColors.ControlLight,
                System.Drawing.SystemColors.ControlText,
                System.Drawing.SystemColors.ControlDark,
                System.Drawing.SystemColors.Control,
                System.Drawing.SystemColors.ControlDark);
            ApplyButtonTheme(
                _assetCancelBtn,
                System.Drawing.SystemColors.ControlLight,
                System.Drawing.SystemColors.ControlText,
                System.Drawing.SystemColors.ControlDark,
                System.Drawing.SystemColors.Control,
                System.Drawing.SystemColors.ControlDark);
            ApplyButtonTheme(
                _assetRetryBtn,
                System.Drawing.SystemColors.ControlLight,
                System.Drawing.SystemColors.ControlText,
                System.Drawing.SystemColors.ControlDark,
                System.Drawing.SystemColors.Control,
                System.Drawing.SystemColors.ControlDark);
            ApplyButtonTheme(
                _assetCopyBtn,
                System.Drawing.SystemColors.ControlLight,
                System.Drawing.SystemColors.ControlText,
                System.Drawing.SystemColors.ControlDark,
                System.Drawing.SystemColors.Control,
                System.Drawing.SystemColors.ControlDark);
            ApplyIconImages(DColor.FromArgb(24, 24, 24), DColor.FromArgb(86, 170, 120));
        }

        ApplyOnlineStatusVisuals();
        ApplyTrayTheme(dark);
        SetLaunchButtonState(GetGameState());
    }

    private void ApplyLauncherWindowSizing()
    {
        var working = Screen.PrimaryScreen?.WorkingArea ?? new System.Drawing.Rectangle(0, 0, 1280, 720);
        var width = Math.Min(1120, Math.Max(760, working.Width - 32));
        var height = Math.Min(700, Math.Max(560, working.Height - 48));
        ClientSize = new DSize(width, height);
        MinimumSize = new DSize(Math.Min(980, working.Width), Math.Min(620, working.Height));
        MaximumSize = new DSize(working.Width, working.Height);
    }

    private void ClaimUsernameAsync()
    {
        MessageBox.Show("Username claiming has been removed. Use Veilnet login to link your online username, or edit your offline username locally.", "Not Available", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    protected override bool ProcessCmdKey(ref System.Windows.Forms.Message msg, System.Windows.Forms.Keys keyData)
    {
        if ((keyData & System.Windows.Forms.Keys.KeyCode) == System.Windows.Forms.Keys.Tab)
            return true;

        return base.ProcessCmdKey(ref msg, keyData);
    }




}
