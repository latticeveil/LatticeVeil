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
using RedactedCraftMonoGame.Core;
using RedactedCraftMonoGame.Online.Eos;

namespace RedactedCraftMonoGame.Launcher;

using DIcon = System.Drawing.Icon;

// Separate WinForms launcher window.
// The game starts ONLY when the user clicks "Launch Game".
// Closing the launcher does NOT start the game.
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

    private Process? _gameProcess;

    private readonly Label _usernameLabel = new();
    private readonly TextBox _usernameBox = new();
    private readonly Button _changeUsernameBtn = new(); // legacy; kept for compatibility, not shown
    private readonly Label _offlineNameLabel = new();
    private readonly TextBox _offlineNameBox = new();
    private readonly Button _saveOfflineNameBtn = new();
    private readonly CheckBox _keepOpenBox = new();
    private readonly CheckBox _darkModeBox = new();
    private readonly Label _onlineHeader = new();
    private readonly Button _hubLoginBtn = new();
    private readonly Button _hubResetBtn = new();
    private readonly Label _hubStatusLabel = new();
    private readonly Button _launchBtn = new();
    private readonly Button _launchOfflineBtn = new();
    private readonly Button _backupBtn = new();
    private readonly Button _openLogsBtn = new();
    private readonly Button _openGameFolderBtn = new();
    private readonly Button _saveLogsBtn = new();
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
    private bool _hubLoggedIn;
    private EosClient? _eosClient;
    private string? _epicProductUserId;
    private string? _epicDisplayNameShown;
    private bool _epicLoginRequested;

    public LauncherForm(Logger log, PlayerProfile profile)
    {
        _log = log;
        _profile = profile;
        _settings = GameSettings.LoadOrCreate(_log);
        _assetInstaller = new AssetPackInstaller(_log);
        _logFilePath = _log.LogFilePath;
        ResetLogSessionDate(_logFilePath);

        Text = "RedactedCraft Launcher";

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
        ApplyTheme(_settings.DarkMode);

        FormClosed += (_, _) =>
        {
            SaveProfile();
            _pollTimer.Stop();

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
        _root.RowStyles.Add(new RowStyle(SizeType.Absolute, 54)); // buttons
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

        _right.Dock = DockStyle.Fill;
        _right.FlowDirection = FlowDirection.TopDown;
        _right.WrapContents = false;
        _right.AutoScroll = true;

        _usernameLabel.AutoSize = true;
        _right.Controls.Add(_usernameLabel);

        _usernameBox.Width = 240;
        _usernameBox.Text = _profile.Username ?? string.Empty;
        _usernameBox.ReadOnly = true;
        _right.Controls.Add(_usernameBox);

        // In-game name (shown to other players)
        _offlineNameLabel.Text = "In-Game Name";
        _offlineNameLabel.AutoSize = true;
        _right.Controls.Add(_offlineNameLabel);

        _offlineNameBox.Width = 240;
        _offlineNameBox.Text = _profile.OfflineUsername ?? string.Empty;
        _offlineNameBox.Leave += (_, _) => SaveOfflineNameFromUi();
        _right.Controls.Add(_offlineNameBox);

        _saveOfflineNameBtn.Text = "Save In-Game Name";
        _saveOfflineNameBtn.Width = 240;
        _saveOfflineNameBtn.Height = 30;
        _saveOfflineNameBtn.Click += (_, _) => SaveOfflineNameFromUi();
        _right.Controls.Add(_saveOfflineNameBtn);

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

        BuildOnlineSection();

        _right.Controls.Add(new Label { Text = "Player Model (placeholder)", AutoSize = true });
        var modelPlaceholder = new Panel { Width = 240, Height = 140, BorderStyle = BorderStyle.FixedSingle };
        _right.Controls.Add(modelPlaceholder);

        _root.Controls.Add(_right, 1, 3);

        _buttons.Dock = DockStyle.Fill;
        _buttons.FlowDirection = FlowDirection.RightToLeft;
        _buttons.WrapContents = false;

        _launchBtn.Text = "Launch Game";
        _launchBtn.Width = 140;
        _launchBtn.Height = 34;
        _launchBtn.UseVisualStyleBackColor = false;
        _launchBtn.Click += (_, _) =>
        {
            SaveProfile();
            var state = GetGameState();
            if (state == GameState.NotRunning)
                StartAssetCheckAndLaunch(BuildOnlineLaunchArgs());
            else
                ConfirmAndKillGame();
        };

        _launchOfflineBtn.Text = "PLAY OFFLINE";
        _launchOfflineBtn.Width = 140;
        _launchOfflineBtn.Height = 34;
        _launchOfflineBtn.UseVisualStyleBackColor = false;
        _launchOfflineBtn.Click += (_, _) =>
        {
            SaveProfile();
            var state = GetGameState();
            if (state == GameState.NotRunning)
                StartAssetCheckAndLaunch("--game --offline");
            else
                ConfirmAndKillGame();
        };

        _backupBtn.Text = "BACKUP WORLDS";
        _backupBtn.Width = 140;
        _backupBtn.Height = 34;
        _backupBtn.Click += (_, _) => PerformBackup();

        _openLogsBtn.Text = "Open Logs";
        _openLogsBtn.Width = 140;
        _openLogsBtn.Height = 34;
        _openLogsBtn.Click += (_, _) => OpenLogsFolder();

        _openGameFolderBtn.Text = "Game Folder";
        _openGameFolderBtn.Width = 140;
        _openGameFolderBtn.Height = 34;
        _openGameFolderBtn.Click += (_, _) => OpenGameFolder();

        _saveLogsBtn.Text = "Save Logs";
        _saveLogsBtn.Width = 140;
        _saveLogsBtn.Height = 34;
        _saveLogsBtn.Click += (_, _) => SaveLogsSnapshot();

        _buttons.Controls.Add(_launchBtn);
        _buttons.Controls.Add(_launchOfflineBtn);
        _buttons.Controls.Add(_backupBtn);
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


    private void BuildOnlineSection()
    {
        _onlineHeader.Text = "Online";
        _onlineHeader.AutoSize = true;
        _onlineHeader.Font = new DFont(Font, System.Drawing.FontStyle.Bold);
        _right.Controls.Add(_onlineHeader);

        _hubLoginBtn.Text = "LOGIN WITH EPIC";
        _hubLoginBtn.Width = 240;
        _hubLoginBtn.Height = 36;
        _hubLoginBtn.TextImageRelation = TextImageRelation.ImageBeforeText;
        _hubLoginBtn.ImageAlign = System.Drawing.ContentAlignment.MiddleLeft;
        _hubLoginBtn.TextAlign = System.Drawing.ContentAlignment.MiddleCenter;
        _hubLoginBtn.Image = LoadProfileButtonImage();
        _hubLoginBtn.Click += async (_, _) => await OnEpicLoginClicked();
        _right.Controls.Add(_hubLoginBtn);

        _hubResetBtn.Text = "SWITCH USER";
        _hubResetBtn.Width = 240;
        _hubResetBtn.Height = 30;
        _hubResetBtn.Enabled = false;
        _hubResetBtn.Visible = false;
        _hubResetBtn.Click += async (_, _) => await EpicSwitchUserAsync();
        _right.Controls.Add(_hubResetBtn);

        _hubStatusLabel.Text = EpicLoginInGameOnly ? "Epic login is handled in-game." : "Epic: Not logged in";
        _hubStatusLabel.AutoSize = true;
        _right.Controls.Add(_hubStatusLabel);

        if (EpicLoginInGameOnly)
        {
            _hubLoginBtn.Visible = false;
            _hubResetBtn.Visible = false;
        }
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
        _title.Text = "Launcher — closing does not start the game";

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

        _topBar.Controls.Add(_logoBox);
        _topBar.Controls.Add(_title);
        _topBar.Controls.Add(_minBtn);
        _topBar.Controls.Add(_closeBtn);

        // Maintain right-side button positions when resized.
        _topBar.Resize += (_, _) =>
        {
            _closeBtn.Location = new DPoint(_topBar.Width - 46, 8);
            _minBtn.Location = new DPoint(_topBar.Width - 92, 8);
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
        _assetCancelBtn.Height = 30;
        _assetCancelBtn.Click += (_, _) => CancelAssetInstall();

        _assetRetryBtn.Text = "Retry";
        _assetRetryBtn.Width = 120;
        _assetRetryBtn.Height = 30;
        _assetRetryBtn.Enabled = false;
        _assetRetryBtn.Click += (_, _) => StartAssetCheckAndLaunch(_pendingLaunchArgs);

        _assetCopyBtn.Text = "Copy";
        _assetCopyBtn.Width = 120;
        _assetCopyBtn.Height = 30;
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
            const string resName = "RedactedCraftMonoGame.Launcher.Resources.LauncherLogo.png";
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
        catch
        {
            return false;
        }
    }

    private void UpdateHubStatus(string text)
    {
        _hubStatusLabel.Text = text;
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

    private async void StartAssetCheckAndLaunch(string args)
    {
        if (_launching || _assetBusy)
            return;

        _pendingLaunchArgs = args ?? string.Empty;
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
            _assetInstaller.PreflightWriteAccess(Paths.AssetsDir);

            var hasAssets = _assetInstaller.CheckLocalAssetsInstalled(out var missing);
            _assetInstaller.EnsureLocalMarkerExists();

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

            await Task.Run(() => _assetInstaller.InstallStagedAssets(AssetPackInstaller.StagingDir, Paths.AssetsDir), ct);
            _assetInstaller.WriteInstalledMarker(release);

            _assetStatus.Text = "Assets ready.";
            _assetProgress.Value = 100;
            HideAssetPanel();

            _assetBusy = false;
            if (_assetPendingLaunch)
            {
                _assetPendingLaunch = false;
                LaunchGameProcess(_pendingLaunchArgs);
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

    private void LaunchGameProcess(string extraArgs)
    {
        var exePath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(exePath) || !File.Exists(exePath))
        {
            MessageBox.Show(
                "Unable to locate the launcher executable to start the game.",
                "Launch Failed",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
            return;
        }

        var args = string.IsNullOrWhiteSpace(extraArgs) ? "--game" : $"--game {extraArgs}";
        try
        {
            _log.Info("Launching game process...");
            _gameProcess = Process.Start(new ProcessStartInfo
            {
                FileName = exePath,
                Arguments = args,
                WorkingDirectory = Path.GetDirectoryName(exePath) ?? string.Empty,
                UseShellExecute = true
            });
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
            "The game is running. Close it now?",
            "Close Game",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Question);
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
                _launchBtn.Text = "Launch Game";
                _launchBtn.Enabled = true;
                _progressLabel.Text = "Idle";
                break;
            default:
                _launchBtn.Text = "Close Game";
                _launchBtn.Enabled = true;
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
        _usernameLabel.Text = _hubLoggedIn
            ? "Account Username (Epic)"
            : "Account Username (not logged in)";

        _offlineNameLabel.Text = "In-Game Name";
    }

    private void UpdateOfflineNameEnabled()
    {
        _offlineNameBox.Enabled = true;
        _saveOfflineNameBtn.Enabled = true;
        if (!_offlineNameBox.Focused)
            _offlineNameBox.Text = _profile.OfflineUsername ?? string.Empty;
    }

    private void SaveOfflineNameFromUi()
    {
        var desired = (_offlineNameBox.Text ?? string.Empty).Trim();
        if (desired.Length > 24)
            desired = desired.Substring(0, 24);

        // Empty is allowed: game will fall back to a deterministic default.
        _profile.OfflineUsername = desired;
        _profile.Save(_log);
        _offlineNameBox.Text = desired;
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

    private void SaveLauncherSettings()
    {
        var merged = GameSettings.LoadOrCreate(_log);
        merged.KeepLauncherOpen = _keepOpenBox.Checked;
        merged.DarkMode = _darkModeBox.Checked;
        merged.Save(_log);
        _settings.KeepLauncherOpen = merged.KeepLauncherOpen;
        _settings.DarkMode = merged.DarkMode;
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
            _log.Warn($"Failed to open game folder: {ex.Message}");
        }
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

    private void ApplyTheme(bool dark)
    {
        if (dark)
        {
            BackColor = DColor.FromArgb(32, 32, 32);
            ForeColor = DColor.Gainsboro;
            _root.BackColor = BackColor;
            _topBar.BackColor = DColor.FromArgb(24, 24, 24);
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
                c.BackColor = DColor.FromArgb(48, 48, 48);
                c.ForeColor = ForeColor;
            }

            _closeBtn.BackColor = DColor.FromArgb(48, 48, 48);
            _closeBtn.ForeColor = ForeColor;
            _minBtn.BackColor = DColor.FromArgb(48, 48, 48);
            _minBtn.ForeColor = ForeColor;

            _logBox.BackColor = DColor.FromArgb(20, 20, 20);
            _logBox.ForeColor = DColor.Gainsboro;

            _usernameBox.BackColor = DColor.FromArgb(20, 20, 20);
            _usernameBox.ForeColor = DColor.Gainsboro;
            _hubLoginBtn.BackColor = DColor.FromArgb(48, 48, 48);
            _hubLoginBtn.ForeColor = ForeColor;
            _hubResetBtn.BackColor = DColor.FromArgb(48, 48, 48);
            _hubResetBtn.ForeColor = ForeColor;
            _progressHost.BackColor = BackColor;
            _progressLabel.ForeColor = ForeColor;
            _progressLabel.BackColor = BackColor;
            _progressTrack.BackColor = DColor.FromArgb(60, 60, 60);

            _assetPanel.BackColor = DColor.FromArgb(18, 18, 18);
            _assetCard.BackColor = DColor.FromArgb(40, 40, 40);
            _assetTitle.ForeColor = ForeColor;
            _assetStatus.ForeColor = ForeColor;
            _assetDetail.ForeColor = ForeColor;
            _assetErrorBox.BackColor = DColor.FromArgb(20, 20, 20);
            _assetErrorBox.ForeColor = ForeColor;
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
                c.BackColor = System.Drawing.SystemColors.Control;
                c.ForeColor = System.Drawing.SystemColors.ControlText;
            }

            _closeBtn.BackColor = System.Drawing.SystemColors.Control;
            _closeBtn.ForeColor = System.Drawing.SystemColors.ControlText;
            _minBtn.BackColor = System.Drawing.SystemColors.Control;
            _minBtn.ForeColor = System.Drawing.SystemColors.ControlText;

            _logBox.BackColor = System.Drawing.SystemColors.Window;
            _logBox.ForeColor = System.Drawing.SystemColors.WindowText;

            _usernameBox.BackColor = System.Drawing.SystemColors.Window;
            _usernameBox.ForeColor = System.Drawing.SystemColors.WindowText;
            _hubLoginBtn.BackColor = System.Drawing.SystemColors.Control;
            _hubLoginBtn.ForeColor = System.Drawing.SystemColors.ControlText;
            _hubResetBtn.BackColor = System.Drawing.SystemColors.Control;
            _hubResetBtn.ForeColor = System.Drawing.SystemColors.ControlText;
            _progressHost.BackColor = BackColor;
            _progressLabel.ForeColor = ForeColor;
            _progressLabel.BackColor = BackColor;
            _progressTrack.BackColor = DColor.FromArgb(210, 210, 210);

            _assetPanel.BackColor = DColor.FromArgb(230, 230, 230);
            _assetCard.BackColor = DColor.FromArgb(250, 250, 250);
            _assetTitle.ForeColor = ForeColor;
            _assetStatus.ForeColor = ForeColor;
            _assetDetail.ForeColor = ForeColor;
            _assetErrorBox.BackColor = System.Drawing.SystemColors.Window;
            _assetErrorBox.ForeColor = ForeColor;
        }

        SetLaunchButtonState(GetGameState());
    }




}
