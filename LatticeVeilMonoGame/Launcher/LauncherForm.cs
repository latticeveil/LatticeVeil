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
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using System.Drawing.Drawing2D;
using LatticeVeilMonoGame.Core;
using LatticeVeilMonoGame.Online.Eos;
using LatticeVeilMonoGame.Online.Gate;

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
    private readonly CheckBox _keepOpenBox = new();
    private readonly CheckBox _darkModeBox = new();
    private readonly Label _recommendedLabel = new();
    private readonly Button _useRecommendedBtn = new();
    private readonly CheckBox _advancedModeBox = new();
    private readonly Label _onlineHeader = new();
    private readonly Button _hubLoginBtn = new();
    private readonly Button _hubGoogleBtn = new();
    private readonly Button _hubResetBtn = new();
    private readonly Label _hubStatusLabel = new();
    private readonly Panel _onlineStatusIndicator = new();
    private readonly Button _launchBtn = new();
    private readonly ComboBox _launchModeBox = new();
    private readonly Button _openLogsBtn = new();
    private readonly Button _openGameFolderBtn = new();
    private readonly Button _saveLogsBtn = new();
    private readonly ToolTip _toolTip = new();
    private DImage? _rocketIcon;
    private DImage? _paperIcon;
    private DImage? _folderIcon;
    private DImage? _skullIcon;
    private DImage? _windowIconImage;
    private readonly TextBox _logBox = new();
    private readonly Queue<string> _logTail = new();
    private readonly TableLayoutPanel _logPanel = new();
    private readonly Panel _progressHost = new();
    private readonly Label _progressLabel = new();
    private readonly Panel _progressTrack = new();
    private readonly Panel _progressFill = new();

    private long _logReadPosition;
    private string _logFilePath = "";
    private int _launchProgress;
    private bool _launching;
    private DateTime _logSessionDate = DateTime.Today;
    private bool _logSessionDateSet;

    private readonly AssetPackInstaller _assetInstaller;
    private CancellationTokenSource? _assetCts;
    private bool _assetBusy;
    private bool _assetPendingLaunch;

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

    private readonly Panel _topBar = new();
    private readonly PictureBox _logoBox = new();
    private readonly Label _title = new();
    private readonly Button _minBtn = new();
    private readonly Button _closeBtn = new();

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
    private readonly OfficialBuildVerifier _officialBuildVerifier;
    private readonly LauncherRuntimeConfig _launcherRuntimeConfig;

    private const string DefaultVeilnetLauncherPageUrl = "https://latticeveil.github.io/veilnet/launcher/";
    private const string DefaultVeilnetFunctionsBaseUrl = "https://lqghurvonrvrxfwjgkuu.supabase.co/functions/v1";
    private const string DefaultGameHashesGetUrl = "https://lqghurvonrvrxfwjgkuu.supabase.co/functions/v1/game-hashes-get";
    private static readonly string VeilnetAuthDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "RedactedCraft");
    private static readonly string VeilnetAuthPath = Path.Combine(VeilnetAuthDir, "veilnet_launcher_token.json");
    private bool _onlineFunctional;
    private bool _releaseHashAllowed;
    private string _onlineStatusDetail = "Checking online services...";
    private string? _authTicket; // Store authentication ticket for claiming

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

    private readonly struct OnlineStartupStatus
    {
        public bool OnlineFunctional { get; init; }
        public bool ReleaseHashAllowed { get; init; }
        public string Detail { get; init; }
    }

    public LauncherForm(Logger log, PlayerProfile profile)
    {
        _log = log;
        _profile = profile;
        _settings = GameSettings.LoadOrCreate(_log);
        _launcherRuntimeConfig = LauncherRuntimeConfig.Load(_log);
        _officialBuildVerifier = new OfficialBuildVerifier(_log, GetGameHashesGetUrl());
        _assetInstaller = new AssetPackInstaller(_log);
        _logFilePath = _log.LogFilePath;
        ResetLogSessionDate(_logFilePath);

        if (string.IsNullOrWhiteSpace(_profile.OfflineUsername))
        {
            _profile.OfflineUsername = GenerateOfflineUsername();
            _profile.Save(_log);
        }

        Text = Paths.IsDevBuild ? "[DEV] Lattice Launcher" : "Lattice Launcher";

        // Borderless (custom top bar).
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new DSize(820, 520);
        DoubleBuffered = true;

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
        ApplyTheme(_settings.DarkMode);

        TryLoadVeilnetAuth();

        FormClosed += (_, _) =>
        {
            SaveProfile();
            _pollTimer.Stop();

            try { _windowIconImage?.Dispose(); _windowIconImage = null; }
            catch { }

            try { _gameProcess?.Dispose(); }
            catch { }

            try { _eosClient?.Dispose(); }
            catch { }

            _log.Info("Launcher closed.");
        };

        _pollTimer.Interval = LogPollIntervalMs;
        _pollTimer.Tick += (_, _) =>
        {
            UpdateLogBox();
            RefreshGameProcessState();
            _eosClient?.Tick();
            UpdateEpicLoginStatus();
        };
        _pollTimer.Start();

        // If the game was somehow launched outside the launcher, reflect that.
        RefreshGameProcessState();

        // Kick off startup checks after the window is shown.
        // (Constructors cannot be async, and we want the UI visible before any network-bound checks.)
        Shown += async (_, _) => await BeginStartupOnlineChecks();

        _log.Info("Launcher UI ready.");
    }

    private void BuildLayout()
    {
        _root.Dock = DockStyle.Fill;
        _root.ColumnCount = 2;
        _root.RowCount = 6;
        _root.Padding = new Padding(12);
        _root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 60));
        _root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 40));
        _root.RowStyles.Add(new RowStyle(SizeType.Absolute, 44)); // top bar
        _root.RowStyles.Add(new RowStyle(SizeType.Absolute, 28)); // assets
        _root.RowStyles.Add(new RowStyle(SizeType.Absolute, 28)); // log file
        _root.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); // body
        _root.RowStyles.Add(new RowStyle(SizeType.Absolute, 84)); // buttons
        _root.RowStyles.Add(new RowStyle(SizeType.Absolute, 6));  // bottom spacing

        BuildTopBar();
        _root.SetColumnSpan(_topBar, 2);
        _root.Controls.Add(_topBar, 0, 0);

        _dirs.Dock = DockStyle.Fill;
        _dirs.TextAlign = System.Drawing.ContentAlignment.MiddleLeft;
        _dirs.Text = $"Assets: {Paths.AssetsDir}";
        _root.SetColumnSpan(_dirs, 2);
        _root.Controls.Add(_dirs, 0, 1);

        _logfile.Dock = DockStyle.Fill;
        _logfile.TextAlign = System.Drawing.ContentAlignment.MiddleLeft;
        _logfile.Text = $"Log: {_log.LogFilePath}";
        _root.SetColumnSpan(_logfile, 2);
        _root.Controls.Add(_logfile, 0, 2);

        _logPanel.Dock = DockStyle.Fill;
        _logPanel.ColumnCount = 1;
        _logPanel.RowCount = 2;
        _logPanel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        _logPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));

        _logBox.ReadOnly = true;
        _logBox.Multiline = true;
        _logBox.ScrollBars = ScrollBars.Vertical;
        _logBox.Dock = DockStyle.Fill;
        _logBox.Font = new DFont("Consolas", 9F);
        _logPanel.Controls.Add(_logBox, 0, 0);

        _progressHost.Dock = DockStyle.Fill;
        _progressHost.Padding = new Padding(6, 4, 6, 6);

        _progressLabel.AutoSize = false;
        _progressLabel.Dock = DockStyle.Top;
        _progressLabel.Height = 12;
        _progressLabel.Text = "Idle";

        _progressTrack.Dock = DockStyle.Bottom;
        _progressTrack.Height = 10;
        _progressTrack.Padding = new Padding(1);
        _progressTrack.Controls.Add(_progressFill);
        _progressTrack.Resize += (_, _) => UpdateProgressFill();

        _progressFill.Dock = DockStyle.Left;
        _progressFill.Width = 0;

        _progressHost.Controls.Add(_progressLabel);
        _progressHost.Controls.Add(_progressTrack);
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

        // In-game name (shown to other players)
        _offlineNameLabel.Text = "Username";
        _offlineNameLabel.AutoSize = true;
        _right.Controls.Add(_offlineNameLabel);

        _offlineNameBox.Width = 240;
        _offlineNameBox.Text = _profile.OfflineUsername ?? string.Empty;
        _offlineNameBox.Leave += (_, _) => SaveOfflineNameFromUi();
        _right.Controls.Add(_offlineNameBox);

        // Save username button removed per user request

        _claimUsernameBtn.Visible = false;

        // Hide legacy "Change Username" button entirely.
        _changeUsernameBtn.Visible = false;

        UpdateUsernameLabel();
        UpdateOfflineNameEnabled();

        _keepOpenBox.Text = "Keep launcher open?";
        _keepOpenBox.AutoSize = true;
        _keepOpenBox.Checked = _settings.KeepLauncherOpen;
        _keepOpenBox.CheckedChanged += (_, _) =>
        {
            _settings.KeepLauncherOpen = _keepOpenBox.Checked;
            SaveLauncherSettings();
            _log.Info($"KeepLauncherOpen: {_settings.KeepLauncherOpen}");
        };
        _right.Controls.Add(_keepOpenBox);

        _darkModeBox.Text = "Dark mode";
        _darkModeBox.AutoSize = true;
        _darkModeBox.Checked = _settings.DarkMode;
        _darkModeBox.CheckedChanged += (_, _) =>
        {
            _settings.DarkMode = _darkModeBox.Checked;
            SaveLauncherSettings();
            ApplyTheme(_settings.DarkMode);
            _log.Info($"DarkMode: {_settings.DarkMode}");
        };
        _right.Controls.Add(_darkModeBox);

        // Recommended button
        _useRecommendedBtn.Text = "Use Recommended";
        _useRecommendedBtn.AutoSize = true;
        _useRecommendedBtn.Click += (_, _) => {
            // Set recommended OpenGL settings
            _settings.RendererBackend = "OpenGL";
            _settings.LauncherRenderDistance = 16; // Recommended render distance
            UpdateRecommendedLabel();
            SaveLauncherSettings();
            _log.Info($"Applied recommended settings: OpenGL renderer, 16 render distance");
        };
        _right.Controls.Add(_useRecommendedBtn);

        // Advanced mode checkbox
        _advancedModeBox.Text = "Advanced Mode";
        _advancedModeBox.AutoSize = true;
        _advancedModeBox.Checked = _settings.AdvancedMode;
        _advancedModeBox.CheckedChanged += (_, _) => {
            _settings.AdvancedMode = _advancedModeBox.Checked;
            SaveLauncherSettings();
            _log.Info($"AdvancedMode: {_settings.AdvancedMode}");
        };
        _right.Controls.Add(_advancedModeBox);

        // Recommended label - removed per user request
        // UpdateRecommendedLabel();
        // _right.Controls.Add(_recommendedLabel);

        BuildOnlineSection();

        _right.Controls.Add(new Label { Text = "Player Model (placeholder)", AutoSize = true });
        var modelPlaceholder = new Panel { Width = 240, Height = 140, BorderStyle = BorderStyle.FixedSingle };
        _right.Controls.Add(modelPlaceholder);

        _root.Controls.Add(_right, 1, 3);

        _buttons.Dock = DockStyle.Fill;
        _buttons.FlowDirection = FlowDirection.RightToLeft;
        _buttons.WrapContents = false;

        _buttons.Padding = new Padding(6, 4, 6, 4);

        ConfigureIconButton(_launchBtn, "Launch", 110, 64);
        _toolTip.SetToolTip(_launchBtn, "Launch game");
        _launchBtn.Click += (_, _) =>
        {
            SaveProfile();
            var state = GetGameState();
            if (state == GameState.NotRunning)
            {
                var mode = (_launchModeBox.SelectedItem as string) ?? "Online";
                if (string.Equals(mode, "Offline", StringComparison.OrdinalIgnoreCase))
                {
                    LaunchGameProcess("--offline", requireHashApproval: false);
                    return;
                }
                else
                {
                    if (!_onlineFunctional || !_releaseHashAllowed)
                    {
                        var reason = string.IsNullOrWhiteSpace(_onlineStatusDetail)
                            ? "Online services are unavailable for this build."
                            : _onlineStatusDetail;
                        MessageBox.Show(
                            reason,
                            "Online Unavailable",
                            MessageBoxButtons.OK,
                            MessageBoxIcon.Warning);
                        return;
                    }
                    StartAssetCheckAndLaunch(BuildOnlineLaunchArgs());
                }
            }
            else
                ConfirmAndKillGame();
        };

        _launchModeBox.DropDownStyle = ComboBoxStyle.DropDownList;
        _launchModeBox.Items.AddRange(new object[] { "Online", "Offline" });
        _launchModeBox.SelectedIndex = 0;
        _launchModeBox.Width = 110;
        _launchModeBox.Height = 32;
        _launchModeBox.Font = new DFont(Font.FontFamily, 8.5f, System.Drawing.FontStyle.Bold);
        _launchModeBox.Margin = new Padding(6, 18, 6, 6);
        _toolTip.SetToolTip(_launchModeBox, "Launch mode");

        ConfigureIconButton(_openLogsBtn, "Logs", 96, 64);
        _toolTip.SetToolTip(_openLogsBtn, "Open logs folder");
        _openLogsBtn.Click += (_, _) => OpenLogsFolder();

        ConfigureIconButton(_openGameFolderBtn, "Game Folder", 110, 64);
        _toolTip.SetToolTip(_openGameFolderBtn, "Open game folder");
        _openGameFolderBtn.Click += (_, _) => OpenGameFolder();

        ConfigureIconButton(_saveLogsBtn, "Save Logs", 96, 64);
        _toolTip.SetToolTip(_saveLogsBtn, "Save logs snapshot");
        _saveLogsBtn.Click += (_, _) => SaveLogsSnapshot();

        _buttons.Controls.Add(_launchBtn);
        _buttons.Controls.Add(_launchModeBox);
        _buttons.Controls.Add(_saveLogsBtn);
        _buttons.Controls.Add(_openLogsBtn);
        _buttons.Controls.Add(_openGameFolderBtn);

        _root.SetColumnSpan(_buttons, 2);
        _root.Controls.Add(_buttons, 0, 4);

        Controls.Add(_root);
        BuildAssetPanel();
        Controls.Add(_assetPanel);
        _assetPanel.BringToFront();
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

    private void BuildOnlineSection()
    {
        // Hide the online header text - we're using the top bar indicator now
        _onlineHeader.Text = "OFFLINE/LAN";
        _onlineHeader.AutoSize = true;
        _onlineHeader.Font = new DFont(Font.FontFamily, 10f, System.Drawing.FontStyle.Bold);
        _onlineHeader.ForeColor = DColor.Red;
        _onlineHeader.Visible = false; // Hide the text
        _right.Controls.Add(_onlineHeader);

        _hubLoginBtn.Text = "LOGIN WITH EPIC";
        _hubLoginBtn.Width = 240;
        _hubLoginBtn.Height = 40;
        _hubLoginBtn.TextImageRelation = TextImageRelation.ImageBeforeText;
        _hubLoginBtn.ImageAlign = System.Drawing.ContentAlignment.MiddleLeft;
        _hubLoginBtn.TextAlign = System.Drawing.ContentAlignment.MiddleCenter;
        _hubLoginBtn.Image = LoadProfileButtonImage();
        _hubLoginBtn.Click += async (_, _) => await OnEpicLoginClicked();
        _right.Controls.Add(_hubLoginBtn);

        _hubGoogleBtn.Text = "LOGIN WITH VEILNET";
        _hubGoogleBtn.Width = 240;
        _hubGoogleBtn.Height = 40;
        _hubGoogleBtn.TextImageRelation = TextImageRelation.ImageBeforeText;
        _hubGoogleBtn.ImageAlign = System.Drawing.ContentAlignment.MiddleLeft;
        _hubGoogleBtn.TextAlign = System.Drawing.ContentAlignment.MiddleCenter;
        _hubGoogleBtn.Image = null;
        _hubGoogleBtn.Click += async (_, _) => await OnGoogleLoginClicked();
        _right.Controls.Add(_hubGoogleBtn);

        _hubResetBtn.Text = "SWITCH USER";
        _hubResetBtn.Width = 240;
        _hubResetBtn.Height = 34;
        _hubResetBtn.Enabled = false;
        _hubResetBtn.Visible = false;
        _hubResetBtn.Click += async (_, _) => await EpicSwitchUserAsync();
        _right.Controls.Add(_hubResetBtn);

        // Hub status label - hidden per user request
        _hubStatusLabel.Text = _onlineStatusDetail;
        _hubStatusLabel.AutoSize = true;
        _hubStatusLabel.Visible = false; // Hide the status label
        _right.Controls.Add(_hubStatusLabel);

        if (EpicLoginInGameOnly)
        {
            _hubLoginBtn.Visible = false;
            _hubResetBtn.Visible = false;
        }
    }

    private async Task OnGoogleLoginClicked()
    {
        try
        {
            var existingToken = (Environment.GetEnvironmentVariable("LV_VEILNET_ACCESS_TOKEN") ?? string.Empty).Trim();
            if (!string.IsNullOrWhiteSpace(existingToken))
            {
                TryClearVeilnetAuth();
                BeginInvoke(new Action(() =>
                {
                    RefreshVeilnetLoginVisuals();
                    UpdateHubStatus("Veilnet: unlinked");
                }));
                return;
            }

            _hubGoogleBtn.Enabled = false;
            UpdateHubStatus("Veilnet: open browser to link...");

            OpenUrlInBrowser(GetVeilnetLauncherPageUrl());
            UpdateHubStatus("Veilnet: paste your link code from the browser page.");

            string? code = null;
            if (InvokeRequired)
            {
                var tcs = new TaskCompletionSource<string?>();
                BeginInvoke(new Action(() =>
                {
                    try { tcs.SetResult(PromptForVeilnetCode()); }
                    catch (Exception ex) { tcs.SetException(ex); }
                }));
                code = await tcs.Task.ConfigureAwait(false);
            }
            else
            {
                code = PromptForVeilnetCode();
            }

            if (string.IsNullOrWhiteSpace(code))
            {
                BeginInvoke(new Action(() => UpdateHubStatus("Veilnet: link canceled")));
                return;
            }

            var exchange = await GetVeilnetClient().ExchangeCodeAsync(code).ConfigureAwait(false);

            Environment.SetEnvironmentVariable("LV_VEILNET_USERNAME", exchange.Username);
            Environment.SetEnvironmentVariable("LV_VEILNET_ACCESS_TOKEN", exchange.Token);
            TrySaveVeilnetAuth(exchange.Username, exchange.Token, exchange.UserId);

            BeginInvoke(new Action(() =>
            {
                RefreshVeilnetLoginVisuals(exchange.Username);
                UpdateHubStatus($"Veilnet: linked as {exchange.Username}");
            }));
        }
        catch (Exception ex)
        {
            BeginInvoke(new Action(() => UpdateHubStatus($"Veilnet: {ex.Message}")));
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

    private string GetVeilnetFunctionsBaseUrl()
    {
        var fromEnv = (Environment.GetEnvironmentVariable("LV_VEILNET_FUNCTIONS_URL") ?? string.Empty).Trim();
        if (!string.IsNullOrWhiteSpace(fromEnv))
            return fromEnv.TrimEnd('/');

        var legacy = (Environment.GetEnvironmentVariable("LV_VEILNET_URL") ?? string.Empty).Trim();
        if (!string.IsNullOrWhiteSpace(legacy)
            && legacy.Contains("supabase.co", StringComparison.OrdinalIgnoreCase)
            && legacy.Contains("/functions/v1", StringComparison.OrdinalIgnoreCase))
        {
            return legacy.TrimEnd('/');
        }

        if (!string.IsNullOrWhiteSpace(_launcherRuntimeConfig.VeilnetFunctionsBaseUrl))
            return _launcherRuntimeConfig.VeilnetFunctionsBaseUrl;

        return DefaultVeilnetFunctionsBaseUrl;
    }

    private string GetGameHashesGetUrl()
    {
        var fromEnv = (Environment.GetEnvironmentVariable("LV_GAME_HASHES_GET_URL") ?? string.Empty).Trim();
        if (!string.IsNullOrWhiteSpace(fromEnv))
            return fromEnv;

        var functionsEnv = (Environment.GetEnvironmentVariable("LV_VEILNET_FUNCTIONS_URL") ?? string.Empty).Trim();
        if (!string.IsNullOrWhiteSpace(functionsEnv))
            return $"{functionsEnv.TrimEnd('/')}/game-hashes-get";

        var legacy = (Environment.GetEnvironmentVariable("LV_VEILNET_URL") ?? string.Empty).Trim();
        if (!string.IsNullOrWhiteSpace(legacy)
            && legacy.Contains("supabase.co", StringComparison.OrdinalIgnoreCase)
            && legacy.Contains("/functions/v1", StringComparison.OrdinalIgnoreCase))
        {
            return $"{legacy.TrimEnd('/')}/game-hashes-get";
        }

        if (!string.IsNullOrWhiteSpace(_launcherRuntimeConfig.GameHashesGetUrl))
            return _launcherRuntimeConfig.GameHashesGetUrl;

        if (!string.IsNullOrWhiteSpace(_launcherRuntimeConfig.VeilnetFunctionsBaseUrl))
            return $"{_launcherRuntimeConfig.VeilnetFunctionsBaseUrl.TrimEnd('/')}/game-hashes-get";

        return DefaultGameHashesGetUrl;
    }

    private string ResolveOfficialHashTargetPath()
    {
        var fromSettings = (_settings.OfficialBuildHashFilePath ?? string.Empty).Trim();
        if (!string.IsNullOrWhiteSpace(fromSettings))
            return fromSettings;

        var baseDir = AppContext.BaseDirectory;
        var candidates = new[]
        {
            Path.Combine(baseDir, "LatticeVeilMonoGame.exe"),
            Path.Combine(baseDir, "LatticeVeil.exe")
        };

        for (var i = 0; i < candidates.Length; i++)
        {
            if (File.Exists(candidates[i]))
                return candidates[i];
        }

        var processPath = (Environment.ProcessPath ?? string.Empty).Trim();
        if (!string.IsNullOrWhiteSpace(processPath))
            return processPath;

        return Path.Combine(baseDir, "LatticeVeilMonoGame.exe");
    }

    private bool VerifyOfficialBuildForOnline()
    {
        var channel = Paths.IsDevBuild ? "dev" : "release";
        var hashTargetPath = ResolveOfficialHashTargetPath();

        OfficialBuildVerifier.VerifyResult verify;
        try
        {
            verify = _officialBuildVerifier.VerifyAsync(channel, hashTargetPath).GetAwaiter().GetResult();
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
            OfficialBuildVerifier.VerifyFailure.FetchFailed =>
                "Online services unavailable. LAN/offline still available.",
            OfficialBuildVerifier.VerifyFailure.InvalidRemotePayload =>
                "Online services unavailable. LAN/offline still available.",
            _ => string.IsNullOrWhiteSpace(verify.Message)
                ? "Online launch blocked by build verification. LAN/offline still available."
                : $"{verify.Message}\n\nLAN/offline still available."
        };

        _log.Warn($"Official hash verification failed ({verify.Failure}) channel={channel}: {verify.Message}");
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
            var payload = JsonSerializer.Serialize(new VeilnetTokenRecord
            {
                Username = (username ?? string.Empty).Trim(),
                Token = (token ?? string.Empty).Trim(),
                UserId = (userId ?? string.Empty).Trim(),
                SavedAtUtc = DateTime.UtcNow
            });
            var bytes = Encoding.UTF8.GetBytes(payload);
            var protectedBytes = ProtectedData.Protect(bytes, null, DataProtectionScope.CurrentUser);
            File.WriteAllBytes(VeilnetAuthPath, protectedBytes);
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
                return null;

            var protectedBytes = File.ReadAllBytes(VeilnetAuthPath);
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

            return record;
        }
        catch (Exception ex)
        {
            _log.Warn($"Failed to load Veilnet auth: {ex.Message}");
            return null;
        }
    }

    private void TryLoadVeilnetAuth()
    {
        var record = TryReadVeilnetAuth();
        if (record == null)
            return;

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

    private void TryClearVeilnetAuth()
    {
        try
        {
            if (File.Exists(VeilnetAuthPath))
                File.Delete(VeilnetAuthPath);
        }
        catch (Exception ex)
        {
            _log.Warn($"Failed to clear Veilnet auth: {ex.Message}");
        }
        finally
        {
            Environment.SetEnvironmentVariable("LV_VEILNET_USERNAME", null);
            Environment.SetEnvironmentVariable("LV_VEILNET_ACCESS_TOKEN", null);
            _veilnetAutoLoginAttempted = false;
        }
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

                BeginInvoke(new Action(() =>
                {
                    RefreshVeilnetLoginVisuals(me.Username);
                    UpdateHubStatus($"Veilnet: linked as {me.Username}");
                }));
            }
            catch (Exception ex)
            {
                _log.Warn($"Veilnet token refresh failed: {ex.Message}");
                TryClearVeilnetAuth();
                BeginInvoke(new Action(() =>
                {
                    RefreshVeilnetLoginVisuals();
                    UpdateHubStatus("Veilnet: saved link expired. Link again.");
                }));
            }
        });
    }

    private void RefreshVeilnetLoginVisuals(string? usernameOverride = null)
    {
        var username = (usernameOverride ?? (Environment.GetEnvironmentVariable("LV_VEILNET_USERNAME") ?? string.Empty)).Trim();
        _hubGoogleBtn.Text = string.IsNullOrWhiteSpace(username) ? "LOGIN WITH VEILNET" : $"LINKED: {username}";
    }

    private void BuildTopBar()
    {
        _topBar.Dock = DockStyle.Fill;
        _topBar.Padding = new Padding(6, 6, 6, 6);

        _logoBox.Size = new DSize(32, 32);
        _logoBox.SizeMode = PictureBoxSizeMode.Zoom;
        _logoBox.Image = LoadEmbeddedLogo();
        _logoBox.Location = new DPoint(6, 6);

        _title.AutoSize = true;
        _title.Location = new DPoint(44, 12);
        _title.Text = (Paths.IsDevBuild ? "[DEV] " : string.Empty) + "Lattice Launcher";
        _title.Font = new DFont(Font.FontFamily, 12f, System.Drawing.FontStyle.Bold);

        _closeBtn.Text = "X";
        _closeBtn.Width = 40;
        _closeBtn.Height = 28;
        _closeBtn.FlatStyle = FlatStyle.Flat;
        _closeBtn.FlatAppearance.BorderSize = 0;
        _closeBtn.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        _closeBtn.Location = new DPoint(_topBar.Width - 46, 8);
        _closeBtn.Click += (_, _) => Close();

        _minBtn.Text = "–";
        _minBtn.Width = 40;
        _minBtn.Height = 28;
        _minBtn.FlatStyle = FlatStyle.Flat;
        _minBtn.FlatAppearance.BorderSize = 0;
        _minBtn.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        _minBtn.Location = new DPoint(_topBar.Width - 92, 8);
        _minBtn.Click += (_, _) => WindowState = FormWindowState.Minimized;

        // Online status indicator
        _onlineStatusIndicator.Size = new DSize(12, 12);
        _onlineStatusIndicator.BackColor = DColor.Red; // Start with offline (red)
        _onlineStatusIndicator.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        _onlineStatusIndicator.Location = new DPoint(_topBar.Width - 138, 12);
        _onlineStatusIndicator.BorderStyle = BorderStyle.None;
        _toolTip.SetToolTip(_onlineStatusIndicator, "Offline/LAN");

        // Online status text label
        _onlineHeader.Size = new DSize(60, 20);
        _onlineHeader.Text = "OFFLINE";
        _onlineHeader.ForeColor = DColor.Red;
        _onlineHeader.Font = new DFont(Font.FontFamily, 8f, System.Drawing.FontStyle.Bold);
        _onlineHeader.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        _onlineHeader.Location = new DPoint(_topBar.Width - 120, 10);
        _onlineHeader.TextAlign = System.Drawing.ContentAlignment.MiddleLeft;
        _topBar.Controls.Add(_onlineHeader);

        _topBar.Controls.Add(_logoBox);
        _topBar.Controls.Add(_title);
        _topBar.Controls.Add(_onlineStatusIndicator);
        _topBar.Controls.Add(_minBtn);
        _topBar.Controls.Add(_closeBtn);

        // Maintain right-side button positions when resized.
        _topBar.Resize += (_, _) =>
        {
            _closeBtn.Location = new DPoint(_topBar.Width - 46, 8);
            _minBtn.Location = new DPoint(_topBar.Width - 92, 8);
            _onlineStatusIndicator.Location = new DPoint(_topBar.Width - 138, 12);
            _onlineHeader.Location = new DPoint(_topBar.Width - 120, 10);
        };

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
        _logoBox.MouseDown += (_, e) => drag(e);
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
        _hubLoginBtn.Text = "LOGIN WITH EPIC";
        _hubResetBtn.Enabled = false;
        _hubResetBtn.Visible = false;
        _hubStatusLabel.Text = status ?? "Epic: Not logged in";

        _hubGoogleBtn.Text = "LOGIN WITH VEILNET";

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
        await Task.Run(async () =>
        {
            var getExecutableHash = () =>
            {
                // Hash the running executable (single-EXE model). Do not rely on a fixed filename.
                var exePath = Environment.ProcessPath ?? "";

                if (string.IsNullOrWhiteSpace(exePath) || !File.Exists(exePath))
                    return "";
                
                using var stream = File.OpenRead(exePath);
                using var sha256 = System.Security.Cryptography.SHA256.Create();
                return Convert.ToHexString(sha256.ComputeHash(stream)).ToLowerInvariant();
            };
            
            SetProgress(30, "Submitting hash for approval...");
            var ourHash = getExecutableHash();
            _log.Info($"Our hash: {ourHash}");
            
            try
            {
                var gate = OnlineGateClient.GetOrCreate();
                
                // Admin-only: auto-submit hash if we have an admin token; otherwise, just verify.
                var adminToken = (Environment.GetEnvironmentVariable("GATE_ADMIN_TOKEN")
                                  ?? Environment.GetEnvironmentVariable("LV_GATE_ADMIN_TOKEN")
                                  ?? "").Trim();

                dynamic submitResult;
                if (!string.IsNullOrWhiteSpace(adminToken))
                {
                    submitResult = await gate.SubmitHashForApprovalAsync(ourHash).ConfigureAwait(false);
                }
                else
                {
                    var verified = await gate.VerifyExecutableHashAsync(_log, target: null).ConfigureAwait(false);
                    submitResult = new { Ok = verified, Message = verified ? "Verified" : "Not verified" };
                }

                var ok = false;
                string message = "";
                try
                {
                    ok = submitResult?.Ok == true;
                    message = submitResult?.Message ?? "";
                }
                catch
                {
                    // If the response shape is unexpected, treat it as failure.
                    ok = false;
                }

                if (ok)
                {
                    _log.Info(string.IsNullOrWhiteSpace(message) ? "Hash approved by gate!" : $"Hash approved by gate: {message}");
                    SetProgress(50, "Hash approved by gate!");

                    SetProgress(60, "Fetching EOS config...");
                    string? ticket = null;
                    gate.TryGetValidTicketForChildProcess(out ticket, out _);
                    if (!EosRemoteConfigBootstrap.TryBootstrap(_log, gate, allowRetry: false, ticket))
                    {
                        throw new Exception("Failed to fetch EOS config from gate");
                    }

                    SetProgress(100, "Ready");
                    _onlineFunctional = true;
                    _releaseHashAllowed = true;
                    _onlineStatusDetail = "Online ready";
                    _log.Info("Online status: Functional and hash allowed - marking as ONLINE");
                    ApplyOnlineStatusVisuals();
                    BeginInvoke(new Action(() => SetLaunchButtonState(GetGameState())));
                }
                else
                {
                    _log.Error($"Hash rejected: {message}");
                    SetProgress(100, "Hash rejected");
                    _onlineFunctional = false;
                    _releaseHashAllowed = false;
                    _onlineStatusDetail = string.IsNullOrWhiteSpace(message) ? "Hash rejected" : message;
                    _log.Info("Online status: Not functional - hash rejected - marking as OFFLINE");
                    ApplyOnlineStatusVisuals();
                    BeginInvoke(new Action(() => SetLaunchButtonState(GetGameState())));
                }
            }
            catch (Exception ex)
            {
                _log.Error($"Error during startup checks: {ex.Message}");
                SetProgress(100, "Error during startup checks");
                _onlineFunctional = false;
                _releaseHashAllowed = false;
                _onlineStatusDetail = ex.Message;
                _log.Info("Online status: Not functional - exception during checks - marking as OFFLINE");
                ApplyOnlineStatusVisuals();
                BeginInvoke(new Action(() => SetLaunchButtonState(GetGameState())));
            }
        });
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
        
        _onlineHeader.Text = _onlineFunctional ? "ONLINE" : "OFFLINE/LAN";
        _onlineHeader.ForeColor = _onlineFunctional ? DColor.LimeGreen : DColor.Red;
        
        // Update top bar indicator
        _onlineStatusIndicator.BackColor = _onlineFunctional ? DColor.LimeGreen : DColor.Red;
        _toolTip.SetToolTip(_onlineStatusIndicator, _onlineFunctional ? "Online" : "Offline/LAN");
        
        // Update username label based on online status
        UpdateUsernameLabel();
        
        // Only allow Veilnet login button to appear when online is functional AND build is verified/allowed.
        _hubGoogleBtn.Visible = _onlineFunctional && _releaseHashAllowed;

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
                displayName = _eosClient.EpicAccountId ?? _eosClient.LocalProductUserId;
            UpdateHubStatus(string.IsNullOrWhiteSpace(displayName) ? "Epic: already logged in" : $"Epic: logged in as {displayName}");
            return Task.CompletedTask;
        }

        _epicLoginRequested = true;
        _epicDisplayNameShown = null;
        _hubLoginBtn.Text = "EPIC LOGIN...";
        _hubResetBtn.Visible = false;
        _hubResetBtn.Enabled = false;
        UpdateHubStatus("Epic: opening login...");
        _log.Info("Epic login requested from launcher.");

        _eosClient?.Dispose();
        _eosClient = EosClient.TryCreate(_log, "epic");
        if (_eosClient == null)
        {
            _hubLoginBtn.Text = "LOGIN WITH EPIC";
            UpdateHubStatus("Epic: login unavailable (EOS config missing).");
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
            SetHubLoggedOut("Epic: Not logged in");
            _hubResetBtn.Visible = false;
            return;
        }

        _hubLoginBtn.Text = "LOGGING OUT...";
        _hubResetBtn.Enabled = false;
        UpdateHubStatus("Epic: logging out...");

        var result = await _eosClient.LogoutAsync();
        if (!result.Ok && !string.IsNullOrWhiteSpace(result.Error))
            _log.Warn($"Epic logout failed: {result.Error}");

        _eosClient.Dispose();
        _eosClient = null;
        _epicProductUserId = null;
        _epicDisplayNameShown = null;
        _epicLoginRequested = false;

        SetHubLoggedOut(result.Ok ? "Epic: logged out" : (result.Error ?? "Epic: logout failed."));
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
        UpdateHubStatus("Epic: clearing saved login...");

        var clear = await _eosClient.DeletePersistentAuthAsync();
        if (!clear.Ok && !string.IsNullOrWhiteSpace(clear.Error))
            _log.Warn($"Epic persistent auth clear failed: {clear.Error}");

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
                UpdateHubStatus("Epic: waiting for login...");
            return;
        }

        var wasLoggedIn = _hubLoggedIn;
        var userId = _eosClient.LocalProductUserId;
        if (!string.IsNullOrWhiteSpace(userId))
        {
            var displayName = _eosClient.EpicDisplayName;
            if (string.IsNullOrWhiteSpace(displayName))
                displayName = _eosClient.EpicAccountId ?? userId;

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
                UpdateHubStatus($"Epic: logged in as {displayName}");
            }
            _epicLoginRequested = false;
            if (wasLoggedIn != _hubLoggedIn)
                UpdateUsernameLabel();
            return;
        }

        _hubLoggedIn = false;
        _hubResetBtn.Visible = false;
        _hubResetBtn.Enabled = false;
        _hubLoginBtn.Text = _epicLoginRequested ? "EPIC LOGIN..." : "LOGIN WITH EPIC";
        if (_epicLoginRequested)
            UpdateHubStatus("Epic: waiting for login...");
        else
            UpdateHubStatus("Epic: Not logged in");
        if (wasLoggedIn != _hubLoggedIn)
            UpdateUsernameLabel();
    }

    private void TryUpdateEpicAccountName()
    {
        if (_eosClient == null)
            return;

        var displayName = _eosClient.EpicDisplayName;
        var accountId = _eosClient.EpicAccountId ?? _eosClient.LocalProductUserId;
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
            MessageBox.Show("Cannot launch game: Hash verification failed. Please ensure your build is approved.", "Hash Verification Failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
            _log.Warn("Launch blocked: Hash not approved");
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
        if (!launchingOffline && !VerifyOfficialBuildForOnline())
        {
            return;
        }

        try
        {
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
            if (_onlineFunctional && _releaseHashAllowed)
            {
                startInfo.EnvironmentVariables["LV_LAUNCHER_ONLINE_AUTH"] = "1";
                _log.Info("Set EOS authorization environment variables for game process");
            }
            else
            {
                startInfo.EnvironmentVariables["LV_LAUNCHER_ONLINE_AUTH"] = "0";
            }

            var veilnetUrl = (Environment.GetEnvironmentVariable("LV_VEILNET_URL") ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(veilnetUrl))
                veilnetUrl = "https://veilnet.onrender.com";
            startInfo.EnvironmentVariables["LV_VEILNET_URL"] = veilnetUrl;

            var veilnetUsername = (Environment.GetEnvironmentVariable("LV_VEILNET_USERNAME") ?? string.Empty).Trim();
            if (!launchingOffline && !string.IsNullOrWhiteSpace(veilnetUsername))
                startInfo.EnvironmentVariables["LV_VEILNET_USERNAME"] = veilnetUsername;

            var veilnetToken = (Environment.GetEnvironmentVariable("LV_VEILNET_ACCESS_TOKEN") ?? string.Empty).Trim();
            if (!launchingOffline && !string.IsNullOrWhiteSpace(veilnetToken))
                startInfo.EnvironmentVariables["LV_VEILNET_ACCESS_TOKEN"] = veilnetToken;
            
            _gameProcess = Process.Start(startInfo);

            // If the user doesn't want the launcher to stay open, close it after a successful spawn.
            if (_gameProcess != null && !_settings.KeepLauncherOpen)
            {
                _log.Info("KeepLauncherOpen is false; closing launcher after launching game.");
                BeginInvoke(new Action(Close));
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
                _launchBtn.Enabled = true;
                _launchModeBox.Enabled = true;
                if (_rocketIcon != null) { _launchBtn.Image = _rocketIcon; }
                _toolTip.SetToolTip(_launchBtn, "Launch game");
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
        _offlineNameLabel.Text = _onlineFunctional ? "Username" : "OFFLINE USERNAME";
    }

    private void UpdateOfflineNameEnabled()
    {
        _offlineNameBox.Enabled = false;
        _offlineNameBox.ReadOnly = true;
        if (!_offlineNameBox.Focused)
            _offlineNameBox.Text = _profile.OfflineUsername ?? string.Empty;
    }

    private void SaveOfflineNameFromUi()
    {
        _offlineNameBox.Text = _profile.OfflineUsername ?? string.Empty;
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
        merged.Save(_log);
        _settings.KeepLauncherOpen = merged.KeepLauncherOpen;
        _settings.DarkMode = merged.DarkMode;
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
                AddDir(Paths.MultiplayerWorldsDir, "Worlds_Multiplayer");
                
                if (File.Exists(Paths.SettingsJsonPath))
                    archive.CreateEntryFromFile(Paths.SettingsJsonPath, "settings.json");
                if (File.Exists(Paths.PlayerProfileJsonPath))
                    archive.CreateEntryFromFile(Paths.PlayerProfileJsonPath, "player_profile.json");
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
            _dirs.ForeColor = ForeColor;
            _logfile.ForeColor = ForeColor;

            foreach (Control c in _right.Controls)
            {
                c.BackColor = BackColor;
                c.ForeColor = ForeColor;
            }

            foreach (Control c in _buttons.Controls)
            {
                if (c is Button button)
                    ApplyButtonTheme(button, buttonBack, textColor, buttonBorder, buttonHover, buttonDown);
                else
                    c.BackColor = BackColor;
            }

            _closeBtn.BackColor = DColor.FromArgb(32, 32, 32);
            _closeBtn.ForeColor = ForeColor;
            _minBtn.BackColor = DColor.FromArgb(32, 32, 32);
            _minBtn.ForeColor = ForeColor;

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
            _progressLabel.ForeColor = ForeColor;
            _progressLabel.BackColor = BackColor;
            _progressTrack.BackColor = DColor.FromArgb(30, 30, 30);
            _progressFill.BackColor = DColor.FromArgb(88, 190, 125);

            _assetPanel.BackColor = DColor.FromArgb(10, 10, 10);
            _assetCard.BackColor = DColor.FromArgb(28, 28, 28);
            _assetTitle.ForeColor = ForeColor;
            _assetStatus.ForeColor = ForeColor;
            _assetDetail.ForeColor = ForeColor;
            _assetErrorBox.BackColor = inputBack;
            _assetErrorBox.ForeColor = ForeColor;

            ApplyButtonTheme(_saveOfflineNameBtn, buttonBack, textColor, buttonBorder, buttonHover, buttonDown);
            ApplyButtonTheme(_hubLoginBtn, buttonBack, textColor, buttonBorder, buttonHover, buttonDown);
            ApplyButtonTheme(_hubResetBtn, buttonBack, textColor, buttonBorder, buttonHover, buttonDown);
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
            _dirs.ForeColor = ForeColor;
            _logfile.ForeColor = ForeColor;

            foreach (Control c in _right.Controls)
            {
                c.BackColor = BackColor;
                c.ForeColor = ForeColor;
            }

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

            _closeBtn.BackColor = System.Drawing.SystemColors.Control;
            _closeBtn.ForeColor = System.Drawing.SystemColors.ControlText;
            _minBtn.BackColor = System.Drawing.SystemColors.Control;
            _minBtn.ForeColor = System.Drawing.SystemColors.ControlText;

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
            _progressLabel.ForeColor = ForeColor;
            _progressLabel.BackColor = BackColor;
            _progressTrack.BackColor = DColor.FromArgb(210, 210, 210);
            _progressFill.BackColor = DColor.FromArgb(86, 170, 120);

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
                _hubLoginBtn,
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
        SetLaunchButtonState(GetGameState());
    }

    private void ClaimUsernameAsync()
    {
        MessageBox.Show("Username claiming has been removed. Use Veilnet login to link your online username, or edit your offline username locally.", "Not Available", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }




}
