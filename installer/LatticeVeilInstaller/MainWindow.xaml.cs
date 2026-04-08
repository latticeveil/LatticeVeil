using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Shapes;
using System.Windows.Threading;
using System.Net.Http;
using Microsoft.Win32;

namespace LatticeVeilInstaller
{
    [System.Runtime.InteropServices.ComVisible(true)]
    public class ScrollHelper
    {
        private readonly Action<double> _onScrollCallback;
        
        public ScrollHelper(Action<double> onScrollCallback)
        {
            _onScrollCallback = onScrollCallback;
        }
        
        public void OnScroll(double percentage)
        {
            try
            {
                _onScrollCallback?.Invoke(percentage);
                System.Diagnostics.Debug.WriteLine($"JavaScript scroll callback: {percentage:F2}%");
                System.Diagnostics.Trace.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] JavaScript scroll callback: {percentage:F2}%");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Scroll callback error: {ex.Message}");
                System.Diagnostics.Trace.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] Scroll callback error: {ex.Message}");
            }
        }
    }

    public partial class MainWindow : Window
    {
        private const string GameExe = "LatticeVeilMonoGame.exe";
        private const string InstallMetadataFile = "latticeveil_install_info.txt";
        private const string UninstallRegistryPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\LatticeVeil";
        private string _latestVersionName = "";
        private string _latestVersionTag = "";
        private string _installedVersionName = "";
        private string _installedVersionTag = "";
        private string _installedPath = "";
        private string _tosHtml = "";
        private string _privacyHtml = "";
        private bool _tosScrolledToBottom = false;
        private bool _privacyScrolledToBottom = false;
        private bool _isInitialized = false;
        private bool _isUpdateMode = false;
        private bool _isOutdatedInstall = false;
        private int _currentPage = 0;
        private readonly Random _random = new Random();
        private CancellationTokenSource? _cancellationTokenSource;
        private readonly HttpClient _httpClient = new HttpClient();
        private readonly List<string> _selectedLvWorldFiles = new List<string>();

        // Window dragging handler
        private void Window_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.LeftButton == MouseButtonState.Pressed)
            {
                this.DragMove();
            }
        }

        private readonly List<string> _splashMessages = new()
        {
            "Preparing your LatticeVeil experience...",
            "Downloading latest version information...",
            "Checking system requirements...",
            "Initializing installation wizard...",
            "Almost ready to begin..."
        };

        public MainWindow()
        {
            InitializeComponent();
            _cancellationTokenSource = new CancellationTokenSource();
            
            this.Title = "LatticeVeil Installer - Loading...";
            System.Diagnostics.Debug.WriteLine("MainWindow constructor: Initializing installer");
            System.Diagnostics.Trace.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] MainWindow constructor: Initializing installer");
            System.Diagnostics.Trace.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] MainWindow constructor: Title set to 'LatticeVeil Installer - Loading...'");
            
            // Check for debug mode (Shift key held during launch)
            if (Keyboard.IsKeyDown(Key.LeftShift) || Keyboard.IsKeyDown(Key.RightShift))
            {
                System.Diagnostics.Debug.WriteLine("MainWindow constructor: Shift key detected, enabling debug mode");
                System.Diagnostics.Trace.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] MainWindow constructor: Shift key detected, enabling debug mode");
                EnableDebugMode();
            }
            
            // Make window draggable
            this.MouseLeftButtonDown += (s, e) => { if(e.LeftButton == MouseButtonState.Pressed) this.DragMove(); };
            if (NextBtn != null)
                NextBtn.IsEnabled = false;
            
            // Start initialization
            System.Diagnostics.Debug.WriteLine("MainWindow constructor: Starting initialization tasks");
            System.Diagnostics.Trace.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] MainWindow constructor: Starting initialization tasks");
            
            _ = Task.Run(async () =>
            {
                await InitializeInstallerAsync();
            });
        }

        private void EnableDebugMode()
        {
            // Add file logging setup
            string exeDir = AppContext.BaseDirectory;
            string logPath = System.IO.Path.Combine(exeDir, "installer_debug.log");
            System.Diagnostics.Trace.Listeners.Add(new System.Diagnostics.TextWriterTraceListener(logPath));
            System.Diagnostics.Trace.AutoFlush = true;
            System.Diagnostics.Trace.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] === DEBUG LOG STARTED IN {exeDir} ===");
            
            // Add debug logging to console
            System.Diagnostics.Debug.WriteLine("=== DEBUG MODE ENABLED ===");
            System.Diagnostics.Trace.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] === DEBUG MODE ENABLED ===");
            System.Diagnostics.Debug.WriteLine($"Installer Version: {System.Reflection.Assembly.GetExecutingAssembly().GetName().Version}");
            System.Diagnostics.Trace.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] Installer Version: {System.Reflection.Assembly.GetExecutingAssembly().GetName().Version}");
            System.Diagnostics.Debug.WriteLine($"Launch Time: {DateTime.Now}");
            System.Diagnostics.Trace.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] Launch Time: {DateTime.Now}");
            System.Diagnostics.Debug.WriteLine($"Working Directory: {Environment.CurrentDirectory}");
            System.Diagnostics.Trace.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] Working Directory: {Environment.CurrentDirectory}");
            
            // Show debug message
            Dispatcher.Invoke(() =>
            {
                MessageBox.Show("Debug Mode Enabled - Detailed logging active", "Debug Info", MessageBoxButton.OK, MessageBoxImage.Information);
                System.Diagnostics.Trace.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] Debug message shown to user");
            });
        }

        private async Task InitializeInstallerAsync()
        {
            var cancellationToken = _cancellationTokenSource?.Token ?? CancellationToken.None;
            var splashTask = UpdateSplashMessagesAsync(cancellationToken);
            var versionTask = DownloadLatestVersionInfoAsync();
            var contentTask = DownloadTosPrivacyContentAsync();
            await Task.WhenAll(versionTask, contentTask);
            LoadExistingInstallInformation();
            _cancellationTokenSource?.Cancel();
            
            Dispatcher.Invoke(() =>
            {
                if (InitializingPage != null)
                    InitializingPage.Visibility = Visibility.Collapsed;
                if (TosPage != null)
                    TosPage.Visibility = Visibility.Visible;
                
                ShowTosPrivacyScreen();
                ApplyInstallerMode();
            });
        }

        private async Task UpdateSplashMessagesAsync(CancellationToken cancellationToken)
        {
            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    await Task.Delay(2000, cancellationToken);
                    
                    if (!cancellationToken.IsCancellationRequested)
                    {
                        Dispatcher.Invoke(() =>
                        {
                            if (SplashMessage != null)
                            {
                                var message = _splashMessages[_random.Next(_splashMessages.Count)];
                                SplashMessage.Text = message;
                            }
                        });
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Expected when cancellation is requested
            }
        }

        private async Task DownloadLatestVersionInfoAsync()
        {
            try
            {
                System.Diagnostics.Debug.WriteLine("DownloadLatestVersionInfoAsync: Starting GitHub API fetch");
                System.Diagnostics.Trace.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] DownloadLatestVersionInfoAsync: Starting GitHub API fetch");
                
                using var httpClient = new HttpClient();
                httpClient.DefaultRequestHeaders.Add("User-Agent", "LatticeVeilInstaller");
                
                var response = await httpClient.GetStringAsync("https://api.github.com/repos/latticeveil/LatticeVeil/releases/latest");
                System.Diagnostics.Debug.WriteLine("DownloadLatestVersionInfoAsync: GitHub API response received");
                System.Diagnostics.Trace.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] DownloadLatestVersionInfoAsync: GitHub API response received");
                
                var releaseInfo = Newtonsoft.Json.JsonConvert.DeserializeObject<dynamic>(response);
                ApplyLatestReleaseInfo(releaseInfo);
                
                System.Diagnostics.Debug.WriteLine($"DownloadLatestVersionInfoAsync: Latest version = {GetLatestReleaseDisplayName()}");
                System.Diagnostics.Trace.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] DownloadLatestVersionInfoAsync: Latest version = {GetLatestReleaseDisplayName()}");
                
                // Update UI on UI thread
                Dispatcher.Invoke(() =>
                {
                    System.Diagnostics.Trace.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] DownloadLatestVersionInfoAsync: About to update title to 'LatticeVeil Installer - {GetLatestReleaseDisplayName()}'");
                    UpdateReleaseDisplay();
                    System.Diagnostics.Trace.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] DownloadLatestVersionInfoAsync: Title updated to 'LatticeVeil Installer - {GetLatestReleaseDisplayName()}'");
                    
                    System.Diagnostics.Debug.WriteLine($"DownloadLatestVersionInfoAsync: UI updated with version {GetLatestReleaseDisplayName()}");
                    System.Diagnostics.Trace.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] DownloadLatestVersionInfoAsync: UI updated with version {GetLatestReleaseDisplayName()}");
                });
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"DownloadLatestVersionInfoAsync: Error - {ex.Message}");
                System.Diagnostics.Trace.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] DownloadLatestVersionInfoAsync: Error - {ex.Message}");
                
                // Fallback to default version
                _latestVersionName = "Unknown";
                _latestVersionTag = "Unknown";
                Dispatcher.Invoke(() =>
                {
                    UpdateReleaseDisplay();
                });
            }
        }

        private void ApplyLatestReleaseInfo(dynamic releaseInfo)
        {
            _latestVersionName = releaseInfo["name"]?.ToString() ?? "";
            _latestVersionTag = releaseInfo["tag_name"]?.ToString() ?? "";

            if (string.IsNullOrWhiteSpace(_latestVersionName))
            {
                _latestVersionName = string.IsNullOrWhiteSpace(_latestVersionTag) ? "Unknown" : _latestVersionTag;
            }

            if (string.IsNullOrWhiteSpace(_latestVersionTag))
            {
                _latestVersionTag = _latestVersionName;
            }
        }

        private void UpdateReleaseDisplay()
        {
            string latestReleaseDisplayName = GetLatestReleaseDisplayName();
            this.Title = $"LatticeVeil Installer - {latestReleaseDisplayName}";

            if (VersionIndicator != null)
                VersionIndicator.Text = latestReleaseDisplayName;

            if (SplashTitle != null)
                SplashTitle.Text = latestReleaseDisplayName;
        }

        private string GetLatestReleaseDisplayName()
        {
            if (!string.IsNullOrWhiteSpace(_latestVersionName))
                return _latestVersionName;

            if (!string.IsNullOrWhiteSpace(_latestVersionTag))
                return _latestVersionTag;

            return "Unknown";
        }

        private string GetInstalledReleaseDisplayName()
        {
            if (!string.IsNullOrWhiteSpace(_installedVersionName))
                return _installedVersionName;

            if (!string.IsNullOrWhiteSpace(_installedVersionTag))
                return _installedVersionTag;

            return "Unknown";
        }

        private void LoadExistingInstallInformation()
        {
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(UninstallRegistryPath);
                if (key == null)
                    return;

                string installLocation = key.GetValue("InstallLocation")?.ToString() ?? "";
                string displayVersion = key.GetValue("DisplayVersion")?.ToString() ?? "";
                string releaseTag = key.GetValue("ReleaseTag")?.ToString() ?? "";

                if (string.IsNullOrWhiteSpace(installLocation))
                    return;

                var metadata = ReadInstallMetadata(installLocation);
                if (metadata.TryGetValue("DisplayVersion", out var metadataDisplayVersion) && !string.IsNullOrWhiteSpace(metadataDisplayVersion))
                    displayVersion = metadataDisplayVersion;
                if (metadata.TryGetValue("ReleaseTag", out var metadataReleaseTag) && !string.IsNullOrWhiteSpace(metadataReleaseTag))
                    releaseTag = metadataReleaseTag;

                bool hasInstalledFiles = Directory.Exists(installLocation) ||
                    File.Exists(System.IO.Path.Combine(installLocation, GameExe)) ||
                    File.Exists(System.IO.Path.Combine(installLocation, "LatticeVeilUninstaller.exe"));

                if (!hasInstalledFiles)
                    return;

                _installedPath = installLocation;
                _installedVersionName = displayVersion;
                _installedVersionTag = releaseTag;
                _isUpdateMode = true;
                _isOutdatedInstall = IsOlderThanLatest(displayVersion, releaseTag);

                System.Diagnostics.Debug.WriteLine($"LoadExistingInstallInformation: Installed path detected at {_installedPath}");
                System.Diagnostics.Trace.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] LoadExistingInstallInformation: Installed path detected at {_installedPath}");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"LoadExistingInstallInformation Error: {ex.Message}");
                System.Diagnostics.Trace.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] LoadExistingInstallInformation Error: {ex.Message}");
            }
        }

        private Dictionary<string, string> ReadInstallMetadata(string installPath)
        {
            var metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            try
            {
                string metadataPath = System.IO.Path.Combine(installPath, InstallMetadataFile);
                if (!File.Exists(metadataPath))
                    return metadata;

                foreach (var line in File.ReadAllLines(metadataPath))
                {
                    if (string.IsNullOrWhiteSpace(line))
                        continue;

                    int separatorIndex = line.IndexOf('=');
                    if (separatorIndex <= 0)
                        continue;

                    var key = line.Substring(0, separatorIndex).Trim();
                    var value = line.Substring(separatorIndex + 1).Trim();
                    metadata[key] = value;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"ReadInstallMetadata Error: {ex.Message}");
                System.Diagnostics.Trace.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] ReadInstallMetadata Error: {ex.Message}");
            }

            return metadata;
        }

        private void WriteInstallMetadata(string installPath)
        {
            try
            {
                string metadataPath = System.IO.Path.Combine(installPath, InstallMetadataFile);
                string metadataContent =
                    $"DisplayVersion={GetLatestReleaseDisplayName()}{Environment.NewLine}" +
                    $"ReleaseTag={_latestVersionTag}{Environment.NewLine}" +
                    $"InstallLocation={installPath}{Environment.NewLine}" +
                    $"UpdatedAt={DateTime.UtcNow:O}{Environment.NewLine}";

                File.WriteAllText(metadataPath, metadataContent, Encoding.UTF8);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"WriteInstallMetadata Error: {ex.Message}");
                System.Diagnostics.Trace.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] WriteInstallMetadata Error: {ex.Message}");
            }
        }

        private bool IsOlderThanLatest(string installedVersionName, string installedVersionTag)
        {
            var latestVersion = ParseVersionValue(_latestVersionTag) ?? ParseVersionValue(_latestVersionName);
            var installedVersion = ParseVersionValue(installedVersionTag) ?? ParseVersionValue(installedVersionName);

            if (latestVersion == null || installedVersion == null)
                return false;

            return installedVersion < latestVersion;
        }

        private Version? ParseVersionValue(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return null;

            var match = System.Text.RegularExpressions.Regex.Match(value, @"(?i)\bv?(\d+(?:\.\d+){0,3})\b");
            if (!match.Success)
                return null;

            var parts = match.Groups[1].Value.Split('.').ToList();
            while (parts.Count < 4)
            {
                parts.Add("0");
            }

            if (parts.Count > 4)
            {
                parts = parts.Take(4).ToList();
            }

            return Version.TryParse(string.Join(".", parts), out var parsedVersion) ? parsedVersion : null;
        }

        private void ApplyInstallerMode()
        {
            UpdateReleaseDisplay();

            if (!_isUpdateMode)
            {
                if (DestinationTitleText != null)
                    DestinationTitleText.Text = "Installation Destination";
                if (DestinationStatusText != null)
                    DestinationStatusText.Visibility = Visibility.Collapsed;
                if (CurrentInstalledVersionText != null)
                    CurrentInstalledVersionText.Visibility = Visibility.Collapsed;
                if (DestinationDescriptionText != null)
                    DestinationDescriptionText.Text = "Select folder where LatticeVeil will be installed";
                if (InstallPageTitleText != null)
                    InstallPageTitleText.Text = "Installing LatticeVeil";
                if (InstallStatus != null)
                    InstallStatus.Text = "Ready to install...";
                if (InstallBtn != null)
                    InstallBtn.Content = "INSTALL";
                if (FinishTitleText != null)
                    FinishTitleText.Text = "THANKS FOR INSTALLING ❤️";
                if (FinishDescriptionText != null)
                    FinishDescriptionText.Text = "Click NEXT to finish installation";
                if (BrowseBtn != null)
                    BrowseBtn.IsEnabled = true;
                if (InstallPathBox != null)
                    InstallPathBox.IsReadOnly = false;
                if (ImportWorldsCheckBox != null)
                {
                    ImportWorldsCheckBox.IsEnabled = true;
                    ImportWorldsCheckBox.Visibility = Visibility.Visible;
                }
                if (SplashPageMessage != null)
                    SplashPageMessage.Text = "Welcome to LatticeVeil!";
                return;
            }

            if (InstallPathBox != null && !string.IsNullOrWhiteSpace(_installedPath))
            {
                InstallPathBox.Text = _installedPath;
                InstallPathBox.IsReadOnly = true;
            }

            if (BrowseBtn != null)
                BrowseBtn.IsEnabled = false;

            if (ImportWorldsCheckBox != null)
            {
                ImportWorldsCheckBox.IsChecked = false;
                ImportWorldsCheckBox.IsEnabled = false;
                ImportWorldsCheckBox.Visibility = Visibility.Collapsed;
            }

            _selectedLvWorldFiles.Clear();

            if (DestinationTitleText != null)
                DestinationTitleText.Text = "Update Destination";

            if (DestinationStatusText != null)
            {
                DestinationStatusText.Text = _isOutdatedInstall ? "UPDATE DETECTED" : "ALREADY INSTALLED";
                DestinationStatusText.Visibility = Visibility.Visible;
            }

            if (CurrentInstalledVersionText != null)
            {
                CurrentInstalledVersionText.Text = $"Currently installed: {GetInstalledReleaseDisplayName()} -> {GetLatestReleaseDisplayName()}";
                CurrentInstalledVersionText.Visibility = Visibility.Visible;
            }

            if (DestinationDescriptionText != null)
                DestinationDescriptionText.Text = "LatticeVeil will be updated in the existing installation folder.";

            if (InstallPageTitleText != null)
                InstallPageTitleText.Text = "Updating LatticeVeil";

            if (InstallStatus != null)
                InstallStatus.Text = "Ready to update...";

            if (InstallBtn != null)
                InstallBtn.Content = "UPDATE";

            if (FinishTitleText != null)
                FinishTitleText.Text = "THANKS FOR UPDATING ❤️";

            if (FinishDescriptionText != null)
                FinishDescriptionText.Text = "Click NEXT to finish update";

            if (SplashPageMessage != null)
            {
                SplashPageMessage.Text = _isOutdatedInstall
                    ? $"Update detected. {GetInstalledReleaseDisplayName()} will be updated to {GetLatestReleaseDisplayName()}."
                    : "An existing LatticeVeil installation was detected. Click NEXT to continue.";
            }
        }

        private async Task DownloadTosPrivacyContentAsync()
        {
            try
            {
                System.Diagnostics.Debug.WriteLine("=== Starting TOS/Privacy Download ===");
                
                // Use the actual website URLs
                var tosUrl = "https://latticeveil.github.io/terms.html";
                var privacyUrl = "https://latticeveil.github.io/privacy.html";
                
                System.Diagnostics.Debug.WriteLine($"TOS URL: {tosUrl}");
                System.Diagnostics.Debug.WriteLine($"Privacy URL: {privacyUrl}");
                
                // Download TOS content
                System.Diagnostics.Debug.WriteLine("Downloading TOS content...");
                var tosTask = _httpClient.GetStringAsync(tosUrl);
                
                // Download Privacy content
                System.Diagnostics.Debug.WriteLine("Downloading Privacy content...");
                var privacyTask = _httpClient.GetStringAsync(privacyUrl);
                
                // Wait for both downloads
                System.Diagnostics.Debug.WriteLine("Waiting for downloads to complete...");
                var results = await Task.WhenAll(tosTask, privacyTask);
                
                System.Diagnostics.Debug.WriteLine($"TOS download completed. Length: {results[0].Length}");
                System.Diagnostics.Debug.WriteLine($"Privacy download completed. Length: {results[1].Length}");
                
                // Extract clean content and preserve formatting
                System.Diagnostics.Debug.WriteLine("Processing TOS content...");
                _tosHtml = WrapCleanContent(results[0]);
                System.Diagnostics.Debug.WriteLine($"TOS processed. Length: {_tosHtml.Length}");
                
                System.Diagnostics.Debug.WriteLine("Processing Privacy content...");
                _privacyHtml = WrapCleanContent(results[1]);
                System.Diagnostics.Debug.WriteLine($"Privacy processed. Length: {_privacyHtml.Length}");
                
                System.Diagnostics.Debug.WriteLine("=== TOS/Privacy Download Complete ===");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Download failed: {ex.Message}");
                System.Diagnostics.Debug.WriteLine($"Stack trace: {ex.StackTrace}");
                
                // Graceful fallback with formatted content
                System.Diagnostics.Debug.WriteLine("Using fallback content...");
                _tosHtml = GetFallbackTosHtml();
                _privacyHtml = GetFallbackPrivacyHtml();
            }
        }

        private string WrapCleanContent(string html)
        {
            try
            {
                // Remove unwanted elements: headers, navigation, scripts, etc.
                var content = html;
                
                // Remove navigation and unwanted elements
                content = System.Text.RegularExpressions.Regex.Replace(content, 
                    @"<a[^>]*class=""back-to-home""[^>]*>.*?</a>", "", 
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Singleline);
                
                content = System.Text.RegularExpressions.Regex.Replace(content, 
                    @"<head>.*?</head>", "", 
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Singleline);
                
                content = System.Text.RegularExpressions.Regex.Replace(content, 
                    @"<script[^>]*>.*?</script>", "", 
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Singleline);
                
                content = System.Text.RegularExpressions.Regex.Replace(content, 
                    @"<link[^>]*>", "", 
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                
                content = System.Text.RegularExpressions.Regex.Replace(content, 
                    @"<meta[^>]*>", "", 
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                
                content = System.Text.RegularExpressions.Regex.Replace(content, 
                    @"<title[^>]*>.*?</title>", "", 
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Singleline);
                
                content = System.Text.RegularExpressions.Regex.Replace(content, 
                    @"<header[^>]*>.*?</header>", "", 
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Singleline);
                
                content = System.Text.RegularExpressions.Regex.Replace(content, 
                    @"<footer[^>]*>.*?</footer>", "", 
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Singleline);
                
                content = System.Text.RegularExpressions.Regex.Replace(content, 
                    @"<nav[^>]*>.*?</nav>", "", 
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Singleline);
                
                // Keep only content inside main/article/container div
                var containerMatch = System.Text.RegularExpressions.Regex.Match(content, 
                    @"<(?:main|article|div)[^>]*class=""(?:container|content|main)""[^>]*>(.*?)</(?:main|article|div)>", 
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Singleline);
                
                if (containerMatch.Success)
                {
                    content = containerMatch.Groups[1].Value;
                }
                
                // Wrap in proper HTML structure for WebBrowser with proper background
                var cleanHtml = $@"
<!DOCTYPE html>
<html>
<head>
    <meta charset=""UTF-8"">
    <style>
        body {{
            font-family: 'Segoe UI', Tahoma, Geneva, Verdana, sans-serif;
            font-size: 14px;
            line-height: 1.6;
            color: white;
            background-color: #1A1A1A;
            margin: 15px;
            padding: 0;
            min-height: 100vh;
        }}
        h1 {{ font-size: 20px; font-weight: bold; margin: 20px 0 10px 0; color: #00FF00; }}
        h2 {{ font-size: 16px; font-weight: bold; margin: 15px 0 8px 0; color: #00FF00; }}
        h3 {{ font-size: 14px; font-weight: bold; margin: 10px 0 5px 0; color: #00FF00; }}
        p {{ margin: 8px 0; }}
        ul {{ margin: 8px 0; padding-left: 20px; }}
        li {{ margin: 4px 0; }}
        strong {{ font-weight: bold; }}
        a {{ color: #00FF00; text-decoration: underline; cursor: pointer; }}
        a:hover {{ color: #00CC00; text-decoration: underline; }}
    </style>
</head>
<body>
{content}
</body>
</html>";
                
                return cleanHtml.Trim();
            }
            catch
            {
                return GetFallbackTosHtml(); // Return fallback if parsing fails
            }
        }

        private string GetFallbackTosHtml()
        {
            return @"
<!DOCTYPE html>
<html>
<head>
    <meta charset=""UTF-8"">
    <style>
        body { font-family: 'Segoe UI', sans-serif; font-size: 14px; line-height: 1.6; color: white; background-color: #1A1A1A; margin: 15px; padding: 0; }
        h1 { font-size: 20px; font-weight: bold; margin: 20px 0 10px 0; color: #00FF00; }
        h2 { font-size: 16px; font-weight: bold; margin: 15px 0 8px 0; color: #00FF00; }
        h3 { font-size: 14px; font-weight: bold; margin: 10px 0 5px 0; color: #00FF00; }
        p { margin: 8px 0; }
        ul { margin: 8px 0; padding-left: 20px; }
        li { margin: 4px 0; }
        strong { font-weight: bold; }
        a { color: #00FF00; text-decoration: underline; cursor: pointer; }
        a:hover { color: #00CC00; text-decoration: underline; }
    </style>
</head>
<body>
    <h1>Terms of Service</h1>
    <p><strong>Effective Date:</strong> March 21, 2026</p>
    <p><strong>Last Updated:</strong> March 21, 2026</p>

    <h2>1. Acceptance of Terms</h2>
    <p>By accessing, downloading, or using LatticeVeil (the ""Game""), you acknowledge that you have read, understood, and agree to be bound by these Terms of Service (""Terms""). If you do not agree to these Terms, please do not use Game or any related services.</p>

    <h2>2. License Grant</h2>
    <p>LatticeVeil grants you a limited, non-exclusive, non-transferable, revocable license to use Game for personal, non-commercial purposes in accordance with these Terms.</p>

    <h3>Allowed Uses</h3>
    <ul>
        <li>Play Game on devices you own or control</li>
        <li>Create and share user-generated content within Game</li>
        <li>Stream and create videos of gameplay for non-commercial purposes</li>
        <li>Take screenshots and capture gameplay footage</li>
    </ul>

    <h3>Restrictions</h3>
    <ul>
        <li>You may not reverse engineer, decompile, or attempt to extract source code from Game</li>
        <li>You may not use the Game for any commercial purposes without explicit permission</li>
        <li>You may not distribute, sell, or license the Game or any modified versions</li>
    </ul>

    <h2>3. User Accounts</h2>
    <p>Some features of the Game may require you to create an account. You are responsible for maintaining the confidentiality of your account credentials and for all activities that occur under your account.</p>

    <h2>4. Contact</h2>
    <p>For questions about these Terms, please contact us.</p>
    
    <p><em>Scroll to the bottom to enable the checkbox and continue.</em></p>
</body>
</html>";
        }

        private string GetFallbackPrivacyHtml()
        {
            return @"
<!DOCTYPE html>
<html>
<head>
    <meta charset=""UTF-8"">
    <style>
        body { font-family: 'Segoe UI', sans-serif; font-size: 14px; line-height: 1.6; color: white; background-color: #1A1A1A; margin: 15px; padding: 0; }
        h1 { font-size: 20px; font-weight: bold; margin: 20px 0 10px 0; color: #00FF00; }
        h2 { font-size: 16px; font-weight: bold; margin: 15px 0 8px 0; color: #00FF00; }
        h3 { font-size: 14px; font-weight: bold; margin: 10px 0 5px 0; color: #00FF00; }
        p { margin: 8px 0; }
        ul { margin: 8px 0; padding-left: 20px; }
        li { margin: 4px 0; }
        strong { font-weight: bold; }
        a { color: #00FF00; text-decoration: underline; cursor: pointer; }
        a:hover { color: #00CC00; text-decoration: underline; }
    </style>
</head>
<body>
    <h1>Privacy Policy</h1>
    <p><strong>Effective Date:</strong> March 21, 2026</p>
    <p><strong>Last Updated:</strong> March 21, 2026</p>

    <h2>1. Introduction</h2>
    <p>LatticeVeil is an independently developed game project committed to protecting your privacy. This Privacy Policy explains how we handle your information when you use our Game and related services.</p>

    <h2>2. Information We DO NOT Collect</h2>
    <ul>
        <li><strong>No location tracking</strong> - We do not track, store, or access your physical location</li>
        <li><strong>No personal identifiers</strong> - We do not collect your name, address, phone number, or other personal contact information</li>
        <li><strong>No usage analytics</strong> - We do not track how you play, what features you use, or your gameplay patterns</li>
        <li><strong>No device fingerprinting</strong> - We do not create profiles based on your device characteristics</li>
    </ul>

    <h2>3. Information We MAY Collect</h2>
    <ul>
        <li><strong>Account Information</strong> - If you choose to create an account, we only store your email address and username</li>
        <li><strong>Game Progress</strong> - Local save files stored on your device (we do not access or upload these)</li>
        <li><strong>Technical Information</strong> - Basic device information needed to ensure compatibility and performance</li>
    </ul>

    <h2>4. Contact</h2>
    <p>For privacy-related questions, please contact us.</p>
    
    <p><em>Scroll to the bottom to enable the checkbox and continue.</em></p>
</body>
</html>";
        }

        private void ShowTosPrivacyScreen()
        {
            try
            {
                System.Diagnostics.Debug.WriteLine("=== ShowTosPrivacyScreen Called ===");
                System.Diagnostics.Trace.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] === ShowTosPrivacyScreen Called ===");
                
                // Reset scroll states
                _tosScrolledToBottom = false;
                _privacyScrolledToBottom = false;
                
                // Disable checkboxes initially - they'll be enabled after scrolling
                if (TosCheckBox != null)
                {
                    TosCheckBox.IsEnabled = false;
                    TosCheckBox.IsChecked = false;
                    System.Diagnostics.Debug.WriteLine("TOS Checkbox: Disabled and unchecked");
                    System.Diagnostics.Trace.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] TOS Checkbox: Disabled and unchecked");
                }
                if (PrivacyCheckBox != null)
                {
                    PrivacyCheckBox.IsEnabled = false;
                    PrivacyCheckBox.IsChecked = false;
                    System.Diagnostics.Debug.WriteLine("Privacy Checkbox: Disabled and unchecked");
                    System.Diagnostics.Trace.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] Privacy Checkbox: Disabled and unchecked");
                }

                // Load content into WebBrowser controls
                Dispatcher.Invoke(() =>
                {
                    System.Diagnostics.Debug.WriteLine("Loading TOS content into WebBrowser...");
                    System.Diagnostics.Trace.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] Loading TOS content into WebBrowser...");
                    LoadContentToWebBrowser(TosWebBrowser, _tosHtml);
                    System.Diagnostics.Debug.WriteLine("Loading Privacy content into WebBrowser...");
                    System.Diagnostics.Trace.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] Loading Privacy content into WebBrowser...");
                    LoadContentToWebBrowser(PrivacyWebBrowser, _privacyHtml);
                });

                // Update Next button state
                CheckNextButtonState();
                System.Diagnostics.Debug.WriteLine("=== ShowTosPrivacyScreen Complete ===");
                System.Diagnostics.Trace.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] === ShowTosPrivacyScreen Complete ===");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"ShowTosPrivacyScreen Error: {ex.Message}");
                System.Diagnostics.Trace.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] ShowTosPrivacyScreen Error: {ex.Message}");
                Dispatcher.Invoke(() =>
                {
                    MessageBox.Show($"Error loading terms: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                });
            }
        }

        private void LoadContentToWebBrowser(System.Windows.Controls.WebBrowser webBrowser, string htmlContent)
        {
            try
            {
                if (webBrowser == null) return;
                
                System.Diagnostics.Debug.WriteLine("LoadContentToWebBrowser: Starting content load");
                System.Diagnostics.Trace.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] LoadContentToWebBrowser: Starting content load");
                
                // Create scroll helper for this WebBrowser
                var scrollHelper = new ScrollHelper((percentage) => {
                    // This will be called from JavaScript when user scrolls
                    Dispatcher.Invoke(() => {
                        // Determine which WebBrowser this is and update accordingly
                        if (webBrowser == TosWebBrowser && TosProgressBar != null)
                        {
                            var clampedPercentage = Math.Max(0, Math.Min(100, percentage));
                            TosProgressBar.Value = clampedPercentage;
                            TosProgressBar.Foreground = new SolidColorBrush(Color.FromRgb(0, 255, 0)); // Green color
                            
                            if (TosCheckBox != null)
                            {
                                var shouldEnable = clampedPercentage >= 95;
                                if (TosCheckBox.IsEnabled != shouldEnable)
                                {
                                    TosCheckBox.IsEnabled = shouldEnable;
                                    System.Diagnostics.Debug.WriteLine($"TOS Checkbox enabled: {shouldEnable} at {clampedPercentage:F2}%");
                                    System.Diagnostics.Trace.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] TOS Checkbox enabled: {shouldEnable} at {clampedPercentage:F2}%");
                                }
                                
                                if (shouldEnable)
                                {
                                    _tosScrolledToBottom = true;
                                    CheckNextButtonState();
                                }
                            }
                        }
                        else if (webBrowser == PrivacyWebBrowser && PrivacyProgressBar != null)
                        {
                            var clampedPercentage = Math.Max(0, Math.Min(100, percentage));
                            PrivacyProgressBar.Value = clampedPercentage;
                            PrivacyProgressBar.Foreground = new SolidColorBrush(Color.FromRgb(0, 255, 0)); // Green color
                            
                            if (PrivacyCheckBox != null)
                            {
                                var shouldEnable = clampedPercentage >= 95;
                                if (PrivacyCheckBox.IsEnabled != shouldEnable)
                                {
                                    PrivacyCheckBox.IsEnabled = shouldEnable;
                                    System.Diagnostics.Debug.WriteLine($"Privacy Checkbox enabled: {shouldEnable} at {clampedPercentage:F2}%");
                                    System.Diagnostics.Trace.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] Privacy Checkbox enabled: {shouldEnable} at {clampedPercentage:F2}%");
                                }
                                
                                if (shouldEnable)
                                {
                                    _privacyScrolledToBottom = true;
                                    CheckNextButtonState();
                                }
                            }
                        }
                    });
                });
                
                // Register the object for scripting
                webBrowser.ObjectForScripting = scrollHelper;
                System.Diagnostics.Debug.WriteLine("LoadContentToWebBrowser: ScrollHelper registered for scripting");
                System.Diagnostics.Trace.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] LoadContentToWebBrowser: ScrollHelper registered for scripting");
                
                // Inject JavaScript scroll listener into the HTML (IE-compatible, accurate calculation)
                var htmlWithScript = htmlContent.Replace("</body>", @"
<script>
    var lastScrollTime = 0;
    // Use IE-compatible scroll event attachment with accurate calculation
    if (window.attachEvent) {
        window.attachEvent('onscroll', function() {
            var now = Date.now();
            // Throttle to 10ms for responsive updates
            if (now - lastScrollTime > 10) {
                lastScrollTime = now;
                var docEl = document.documentElement;
                var body = document.body;
                var scrollTop = docEl.scrollTop || body.scrollTop || window.pageYOffset || 0;
                var scrollHeight = docEl.scrollHeight || body.scrollHeight || 0;
                var clientHeight = docEl.clientHeight || body.clientHeight || window.innerHeight || 0;
                var percentage = 0;
                
                if (scrollHeight > clientHeight) {
                    percentage = (scrollTop / (scrollHeight - clientHeight)) * 100;
                } else {
                    percentage = 100;
                }
                
                // Call C# method with accurate percentage
                window.external.OnScroll(percentage);
            }
        });
    } else if (window.addEventListener) {
        // Fallback for modern browsers
        window.addEventListener('scroll', function() {
            var now = Date.now();
            if (now - lastScrollTime > 10) {
                lastScrollTime = now;
                var docEl = document.documentElement;
                var body = document.body;
                var scrollTop = docEl.scrollTop || body.scrollTop || window.pageYOffset || 0;
                var scrollHeight = docEl.scrollHeight || body.scrollHeight || 0;
                var clientHeight = docEl.clientHeight || body.clientHeight || window.innerHeight || 0;
                var percentage = 0;
                
                if (scrollHeight > clientHeight) {
                    percentage = (scrollTop / (scrollHeight - clientHeight)) * 100;
                } else {
                    percentage = 100;
                }
                
                window.external.OnScroll(percentage);
            }
        });
    }
</script>
</body>");
                
                // Navigate to HTML content with injected script
                webBrowser.NavigateToString(htmlWithScript);
                System.Diagnostics.Debug.WriteLine("LoadContentToWebBrowser: Navigation completed");
                System.Diagnostics.Trace.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] LoadContentToWebBrowser: Navigation completed");
            }
            catch
            {
                // Fallback: show error message
                System.Diagnostics.Debug.WriteLine("LoadContentToWebBrowser: Error occurred, showing fallback");
                System.Diagnostics.Trace.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] LoadContentToWebBrowser: Error occurred, showing fallback");
                
                var errorHtml = @"
<!DOCTYPE html>
<html>
<head>
    <meta charset=""UTF-8"">
    <style>
        body { font-family: 'Segoe UI', sans-serif; font-size: 14px; color: white; background-color: #1A1A1A; margin: 15px; padding: 0; min-height: 100vh; }
        .error { color: #FF6B6B; font-weight: bold; }
    </style>
</head>
<body>
    <p class=""error"">Unable to load content. Please check your internet connection.</p>
    <p>The installer will continue with fallback terms.</p>
</body>
</html>";
                webBrowser.NavigateToString(errorHtml);
            }
        }

        private void SetupWebBrowserScrollHandler(System.Windows.Controls.WebBrowser webBrowser, ProgressBar progressBar, CheckBox checkBox, Action<bool> onScrollComplete)
        {
            // This method is now handled by JavaScript scroll listener via ObjectForScripting
            // No timer polling needed anymore
            System.Diagnostics.Debug.WriteLine("Scroll handler setup complete - using JavaScript callbacks");
        }

        private void TosWebBrowser_LoadCompleted(object sender, NavigationEventArgs e)
        {
            System.Diagnostics.Debug.WriteLine("TOS WebBrowser LoadCompleted - JavaScript scroll listener active");
            // Scroll handling is now done via ObjectForScripting JavaScript callbacks
        }

        private void PrivacyWebBrowser_LoadCompleted(object sender, NavigationEventArgs e)
        {
            System.Diagnostics.Debug.WriteLine("Privacy WebBrowser LoadCompleted - JavaScript scroll listener active");
            // Scroll handling is now done via ObjectForScripting JavaScript callbacks
            
            // Set initialization complete after both WebBrowsers are loaded
            _isInitialized = true;
            CheckNextButtonState();
        }

        private void CheckNextButtonState()
        {
            try
            {
                Dispatcher.Invoke(() =>
                {
                    if (NextBtn != null && TosCheckBox != null && PrivacyCheckBox != null)
                    {
                        // Enable Next button ONLY when both checkboxes are checked AND initialized
                        NextBtn.IsEnabled = TosCheckBox.IsChecked == true && PrivacyCheckBox.IsChecked == true && _isInitialized;
                        
                        System.Diagnostics.Debug.WriteLine($"Next button state: TOS checked={TosCheckBox.IsChecked}, Privacy checked={PrivacyCheckBox.IsChecked}, initialized={_isInitialized}, enabled={NextBtn.IsEnabled}");
                        System.Diagnostics.Trace.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] CheckNextButtonState: TOS checked={TosCheckBox.IsChecked}, Privacy checked={PrivacyCheckBox.IsChecked}, initialized={_isInitialized}, enabled={NextBtn.IsEnabled}");
                    }
                });
            }
            catch
            {
                // Ignore UI update errors
            }
        }

        private void TosCheckBox_Checked(object sender, RoutedEventArgs e)
        {
            CheckNextButtonState();
        }

        private void TosCheckBox_Unchecked(object sender, RoutedEventArgs e)
        {
            CheckNextButtonState();
        }

        private void PrivacyCheckBox_Checked(object sender, RoutedEventArgs e)
        {
            CheckNextButtonState();
        }

        private void PrivacyCheckBox_Unchecked(object sender, RoutedEventArgs e)
        {
            CheckNextButtonState();
        }

        private void ShowPage(string pageName)
        {
            try
            {
                System.Diagnostics.Debug.WriteLine($"ShowPage: Requested page = {pageName}");
                System.Diagnostics.Trace.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] ShowPage: Requested page = {pageName}");
                
                Dispatcher.Invoke(() =>
                {
                    // Hide all pages
                    if (InitializingPage != null)
                        InitializingPage.Visibility = Visibility.Collapsed;
                    if (SplashPage != null)
                        SplashPage.Visibility = Visibility.Collapsed;
                    if (DestinationPage != null)
                        DestinationPage.Visibility = Visibility.Collapsed;
                    if (TosPage != null)
                        TosPage.Visibility = Visibility.Collapsed;
                    if (InstallPage != null)
                        InstallPage.Visibility = Visibility.Collapsed;
                    if (FinishPage != null)
                        FinishPage.Visibility = Visibility.Collapsed;
                    
                    // Show the requested page
                    switch (pageName)
                    {
                        case "Initializing":
                            if (InitializingPage != null)
                                InitializingPage.Visibility = Visibility.Visible;
                            _currentPage = 0;
                            break;
                        case "Splash":
                            if (SplashPage != null)
                                SplashPage.Visibility = Visibility.Visible;
                            _currentPage = 1;
                            break;
                        case "Destination":
                            if (DestinationPage != null)
                                DestinationPage.Visibility = Visibility.Visible;
                            _currentPage = 2;
                            break;
                        case "Tos":
                            if (TosPage != null)
                                TosPage.Visibility = Visibility.Visible;
                            _currentPage = 3;
                            break;
                        case "Install":
                            if (InstallPage != null)
                                InstallPage.Visibility = Visibility.Visible;
                            _currentPage = 4;
                            if (NextBtn != null)
                            {
                                NextBtn.Content = "NEXT";
                                NextBtn.IsEnabled = false;
                            }
                            break;
                        case "Finish":
                            if (FinishPage != null)
                                FinishPage.Visibility = Visibility.Visible;
                            _currentPage = 5;
                            if (NextBtn != null)
                                NextBtn.Content = "FINISH";
                            break;
                    }
                    
                    // Update Previous button state - hide on TOS page and Finish page, show only after Next click
                    if (PreviousBtn != null)
                    {
                        PreviousBtn.IsEnabled = _currentPage > 3 && _currentPage < 5;
                        System.Diagnostics.Debug.WriteLine($"ShowPage: Previous button enabled={PreviousBtn.IsEnabled} for page {_currentPage}");
                        System.Diagnostics.Trace.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] ShowPage: Previous button enabled={PreviousBtn.IsEnabled} for page {_currentPage}");
                    }

                    if (NextBtn != null && _currentPage != 4 && _currentPage != 5)
                    {
                        NextBtn.Content = "NEXT";
                    }
                });
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"ShowPage Error: {ex.Message}");
                System.Diagnostics.Trace.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] ShowPage Error: {ex.Message}");
            }
        }

        private void CloseBtn_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                _cancellationTokenSource?.Cancel();
                Close();
            }
            catch
            {
                // Ignore cleanup errors
            }
        }

        private void BrowseBtn_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var dialog = new OpenFileDialog
                {
                    Filter = "Executable Files (*.exe)|*.exe|All Files (*.*)|*.*",
                    Title = "Select LatticeVeil Executable"
                };
                
                if (dialog.ShowDialog() == true)
                {
                    var directory = System.IO.Path.GetDirectoryName(dialog.FileName);
                    if (InstallPathBox != null)
                        InstallPathBox.Text = directory;
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error browsing for file: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void InstallBtn_Click(object sender, RoutedEventArgs e)
        {
            _cancellationTokenSource = new CancellationTokenSource();
            
            Dispatcher.Invoke(() =>
            {
                if (NextBtn != null) NextBtn.IsEnabled = false;
                if (PreviousBtn != null) PreviousBtn.IsEnabled = false;
                if (InstallBtn != null) InstallBtn.IsEnabled = false;
            });
            
            Task.Run(async () =>
            {
                try
                {
                    Dispatcher.Invoke(() =>
                    {
                        if (InstallStatus != null)
                            InstallStatus.Text = _isUpdateMode ? "Updating LatticeVeil..." : "Installing LatticeVeil...";
                        if (InstallProgressBar != null)
                            InstallProgressBar.Value = 0;
                        if (InstallProgressText != null)
                            InstallProgressText.Text = "0%";
                    });

                    System.Diagnostics.Debug.WriteLine($"InstallBtn_Click: Starting real {(_isUpdateMode ? "update" : "installation")}");
                    System.Diagnostics.Trace.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] InstallBtn_Click: Starting real {(_isUpdateMode ? "update" : "installation")}");

                    _httpClient.DefaultRequestHeaders.Add("User-Agent", "LatticeVeilInstaller");
                    
                    // Get latest release info to find download URL
                    var response = await _httpClient.GetStringAsync("https://api.github.com/repos/latticeveil/LatticeVeil/releases/latest", _cancellationTokenSource.Token);
                    var releaseInfo = Newtonsoft.Json.JsonConvert.DeserializeObject<dynamic>(response);
                    ApplyLatestReleaseInfo(releaseInfo);
                    Dispatcher.Invoke(() => UpdateReleaseDisplay());
                    
                    // Find the LatticeVeilMonoGame.exe asset
                    string? downloadUrl = null;
                    long totalBytes = 0;
                    var assets = releaseInfo?.assets;
                    if (assets == null)
                    {
                        throw new Exception("Latest release assets could not be loaded");
                    }

                    foreach (var asset in assets)
                    {
                        if (asset.name.ToString() == "LatticeVeilMonoGame.exe")
                        {
                            downloadUrl = asset.browser_download_url.ToString();
                            totalBytes = (long)asset.size;
                            break;
                        }
                    }
                    
                    if (string.IsNullOrEmpty(downloadUrl))
                    {
                        throw new Exception("LatticeVeilMonoGame.exe not found in latest release");
                    }

                    System.Diagnostics.Debug.WriteLine($"InstallBtn_Click: Downloading from {downloadUrl}");
                    System.Diagnostics.Trace.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] InstallBtn_Click: Downloading from {downloadUrl}");

                    string installPath = "C:\\Program Files\\LatticeVeil";
                    Dispatcher.Invoke(() =>
                    {
                        installPath = InstallPathBox?.Text ?? "C:\\Program Files\\LatticeVeil";
                    });
                    var exePath = System.IO.Path.Combine(installPath, GameExe);
                    
                    // Ensure directory exists
                    System.IO.Directory.CreateDirectory(installPath);
                    
                    // Download with progress tracking
                    using var responseMessage = await _httpClient.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead, _cancellationTokenSource.Token);
                    responseMessage.EnsureSuccessStatusCode();
                    
                    using var contentStream = await responseMessage.Content.ReadAsStreamAsync(_cancellationTokenSource.Token);
                    using var fileStream = new System.IO.FileStream(exePath, System.IO.FileMode.Create, System.IO.FileAccess.Write, System.IO.FileShare.None, bufferSize: 8192, useAsync: true);
                    
                    var buffer = new byte[8192];
                    long totalBytesRead = 0;
                    int bytesRead;
                    
                    while ((bytesRead = await contentStream.ReadAsync(buffer, 0, buffer.Length, _cancellationTokenSource.Token)) > 0)
                    {
                        await fileStream.WriteAsync(buffer, 0, bytesRead, _cancellationTokenSource.Token);
                        totalBytesRead += bytesRead;
                        
                        // Update progress in real-time
                        var progress = totalBytes > 0 ? (double)totalBytesRead / totalBytes * 100 : 0;
                        Dispatcher.Invoke(() =>
                        {
                            if (InstallProgressBar != null)
                                InstallProgressBar.Value = progress;
                            if (InstallProgressText != null)
                                InstallProgressText.Text = $"{progress:F1}%";
                        });
                    }
                    
                    System.Diagnostics.Debug.WriteLine($"InstallBtn_Click: Downloaded {totalBytesRead} bytes to {exePath}");
                    System.Diagnostics.Trace.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] InstallBtn_Click: Downloaded {totalBytesRead} bytes to {exePath}");

                    // Extract embedded uninstaller
                    Dispatcher.Invoke(() => InstallStatus.Text = "Extracting uninstaller...");
                    var assembly = System.Reflection.Assembly.GetExecutingAssembly();
                    var resourceName = "LatticeVeilInstaller.LatticeVeilUninstaller.exe";
                    var uninstallerPath = System.IO.Path.Combine(installPath, "LatticeVeilUninstaller.exe");
                    
                    using (var stream = assembly.GetManifestResourceStream(resourceName))
                    {
                        if (stream != null)
                        {
                            using (var uninstallerFileStream = File.Create(uninstallerPath))
                            {
                                stream.CopyTo(uninstallerFileStream);
                            }
                        }
                    }
                    
                    System.Diagnostics.Debug.WriteLine($"InstallBtn_Click: Extracted uninstaller to {uninstallerPath}");
                    System.Diagnostics.Trace.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] InstallBtn_Click: Extracted uninstaller to {uninstallerPath}");

                    // Create Desktop shortcut
                    var desktopPath = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
                    var shortcutPath = System.IO.Path.Combine(desktopPath, "LatticeVeil.lnk");
                    CreateShortcut(exePath, shortcutPath, "LatticeVeil");
                    
                    System.Diagnostics.Debug.WriteLine($"InstallBtn_Click: Created Desktop shortcut at {shortcutPath}");
                    System.Diagnostics.Trace.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] InstallBtn_Click: Created Desktop shortcut at {shortcutPath}");

                    // Create Start Menu shortcut
                    var startMenuPath = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "LatticeVeil");
                    System.IO.Directory.CreateDirectory(startMenuPath);
                    var startMenuShortcut = System.IO.Path.Combine(startMenuPath, "LatticeVeil.lnk");
                    CreateShortcut(exePath, startMenuShortcut, "LatticeVeil");
                    
                    System.Diagnostics.Debug.WriteLine($"InstallBtn_Click: Created Start Menu shortcut at {startMenuShortcut}");
                    System.Diagnostics.Trace.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] InstallBtn_Click: Created Start Menu shortcut at {startMenuShortcut}");
                    
                    // Import selected .lvworld files
                    if (_selectedLvWorldFiles.Count > 0)
                    {
                        Dispatcher.Invoke(() => InstallStatus.Text = "Importing world backups...");
                        await ImportWorldFilesAsync();
                    }

                    // Create registry entries
                    var uninstallString = $"\"{System.IO.Path.Combine(installPath, "LatticeVeilUninstaller.exe")}\"";
                    using var key = Microsoft.Win32.Registry.LocalMachine.CreateSubKey(UninstallRegistryPath);
                    key.SetValue("DisplayName", "LatticeVeil");
                    key.SetValue("UninstallString", uninstallString);
                    key.SetValue("DisplayVersion", GetLatestReleaseDisplayName());
                    key.SetValue("ReleaseTag", _latestVersionTag);
                    key.SetValue("InstallLocation", installPath);
                    key.SetValue("Publisher", "LatticeVeil");
                    key.SetValue("DisplayIcon", exePath);

                    WriteInstallMetadata(installPath);
                    
                    System.Diagnostics.Debug.WriteLine("InstallBtn_Click: Created registry entries");
                    System.Diagnostics.Trace.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] InstallBtn_Click: Created registry entries");

                    Dispatcher.Invoke(() =>
                    {
                        if (InstallProgressBar != null)
                            InstallProgressBar.Value = 100;
                        if (InstallProgressText != null)
                            InstallProgressText.Text = "100%";
                        if (InstallStatus != null)
                            InstallStatus.Text = _isUpdateMode ? "Update complete!" : "Installation complete!";
                    });

                    System.Diagnostics.Debug.WriteLine($"InstallBtn_Click: {(_isUpdateMode ? "Update" : "Installation")} completed successfully");
                    System.Diagnostics.Trace.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] InstallBtn_Click: {(_isUpdateMode ? "Update" : "Installation")} completed successfully");
                    
                    // Show finish page after delay
                    await Task.Delay(1000);
                    Dispatcher.Invoke(() => ShowPage("Finish"));
                }
                catch (OperationCanceledException)
                {
                    System.Diagnostics.Debug.WriteLine("InstallBtn_Click: Installation cancelled");
                    System.Diagnostics.Trace.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] InstallBtn_Click: Installation cancelled");
                    
                    Dispatcher.Invoke(() =>
                    {
                        if (InstallStatus != null)
                            InstallStatus.Text = "Installation cancelled.";
                    });
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"InstallBtn_Click Error: {ex.Message}");
                    System.Diagnostics.Trace.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] InstallBtn_Click Error: {ex.Message}");
                    
                    Dispatcher.Invoke(() =>
                    {
                        MessageBox.Show($"Installation failed: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                        if (InstallStatus != null)
                            InstallStatus.Text = $"Installation failed: {ex.Message}";
                    });
                }
                finally
                {
                    Dispatcher.Invoke(() =>
                    {
                        if (NextBtn != null)
                            NextBtn.IsEnabled = _currentPage == 5;
                        if (PreviousBtn != null) PreviousBtn.IsEnabled = true;
                        if (InstallBtn != null) InstallBtn.IsEnabled = true;
                    });
                    
                    _cancellationTokenSource?.Dispose();
                    _cancellationTokenSource = null;
                }
            });
        }

        private void CreateShortcut(string targetPath, string shortcutPath, string description)
        {
            try
            {
                Type? shellType = Type.GetTypeFromProgID("WScript.Shell");
                if (shellType == null)
                    return;

                dynamic? shell = Activator.CreateInstance(shellType);
                if (shell == null)
                    return;

                var shortcut = shell.CreateShortcut(shortcutPath);
                shortcut.TargetPath = targetPath;
                shortcut.Description = description;
                shortcut.Save();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"CreateShortcut Error: {ex.Message}");
                System.Diagnostics.Trace.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] CreateShortcut Error: {ex.Message}");
            }
        }

        private void PreviousBtn_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var previousPage = Math.Max(0, _currentPage - 1);
                switch (previousPage)
                {
                    case 0:
                        ShowPage("Initializing");
                        break;
                    case 1:
                        ShowPage("Splash");
                        break;
                    case 2:
                        ShowPage("Splash");
                        break;
                    case 3:
                        ShowPage("Destination");
                        break;
                    case 4:
                        ShowPage("Tos");
                        break;
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Navigation failed: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void CancelBtn_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var result = MessageBox.Show("Are you sure you want to cancel the installation?", 
                    "Cancel Installation", MessageBoxButton.YesNo, MessageBoxImage.Question);
                
                if (result == MessageBoxResult.Yes)
                {
                    _cancellationTokenSource?.Cancel();
                    
                    // Clean up partial download
                    string installPath = "C:\\Program Files\\LatticeVeil";
                    Dispatcher.Invoke(() =>
                    {
                        installPath = InstallPathBox?.Text ?? "C:\\Program Files\\LatticeVeil";
                    });
                    
                    var exePath = System.IO.Path.Combine(installPath, GameExe);
                    if (System.IO.File.Exists(exePath))
                    {
                        try
                        {
                            System.IO.File.Delete(exePath);
                            System.Diagnostics.Debug.WriteLine($"CancelBtn_Click: Deleted partial file {exePath}");
                            System.Diagnostics.Trace.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] CancelBtn_Click: Deleted partial file {exePath}");
                        }
                        catch (Exception deleteEx)
                        {
                            System.Diagnostics.Debug.WriteLine($"CancelBtn_Click: Failed to delete partial file: {deleteEx.Message}");
                            System.Diagnostics.Trace.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] CancelBtn_Click: Failed to delete partial file: {deleteEx.Message}");
                        }
                    }
                    
                    Close();
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"CancelBtn_Click Error: {ex.Message}");
                System.Diagnostics.Trace.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] CancelBtn_Click Error: {ex.Message}");
            }
        }

        private void NextBtn_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                System.Diagnostics.Debug.WriteLine($"NextBtn_Click: Current page = {_currentPage}");
                System.Diagnostics.Trace.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] NextBtn_Click: Current page = {_currentPage}");
                
                switch (_currentPage)
                {
                    case 3: // TOS page
                        System.Diagnostics.Debug.WriteLine($"NextBtn_Click: TOS page - TOS checked={TosCheckBox?.IsChecked}, Privacy checked={PrivacyCheckBox?.IsChecked}, initialized={_isInitialized}");
                        System.Diagnostics.Trace.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] NextBtn_Click: TOS page - TOS checked={TosCheckBox?.IsChecked}, Privacy checked={PrivacyCheckBox?.IsChecked}, initialized={_isInitialized}");
                        
                        if (TosCheckBox?.IsChecked == true && PrivacyCheckBox?.IsChecked == true && _isInitialized)
                        {
                            System.Diagnostics.Debug.WriteLine("NextBtn_Click: Advancing to Destination page");
                            System.Diagnostics.Trace.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] NextBtn_Click: Advancing to Destination page");
                            ShowPage("Destination");
                        }
                        break;
                    case 2: // Destination page
                        if (string.IsNullOrEmpty(InstallPathBox?.Text))
                        {
                            System.Diagnostics.Debug.WriteLine("NextBtn_Click: Destination page - Path is empty");
                            System.Diagnostics.Trace.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] NextBtn_Click: Destination page - Path is empty");
                            MessageBox.Show("Please select a valid installation path.", "Invalid Path", MessageBoxButton.OK, MessageBoxImage.Warning);
                        }
                        else
                        {
                            // Check for world import
                            var importWorldsCheckBox = this.FindName("ImportWorldsCheckBox") as CheckBox;
                            if (!_isUpdateMode && importWorldsCheckBox?.IsChecked == true)
                            {
                                var openFileDialog = new Microsoft.Win32.OpenFileDialog
                                {
                                    Title = "Select World Backups",
                                    Filter = "LatticeVeil World Files (*.lvworld)|*.lvworld",
                                    Multiselect = true
                                };

                                if (openFileDialog.ShowDialog() == true)
                                {
                                    _selectedLvWorldFiles.Clear();
                                    _selectedLvWorldFiles.AddRange(openFileDialog.FileNames);
                                    
                                    System.Diagnostics.Debug.WriteLine($"NextBtn_Click: Selected {_selectedLvWorldFiles.Count} .lvworld files for import");
                                    System.Diagnostics.Trace.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] NextBtn_Click: Selected {_selectedLvWorldFiles.Count} .lvworld files for import");
                                    
                                    foreach (var file in _selectedLvWorldFiles)
                                    {
                                        System.Diagnostics.Trace.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] NextBtn_Click: Selected world file: {file}");
                                    }
                                }
                            }
                            else if (_isUpdateMode)
                            {
                                _selectedLvWorldFiles.Clear();
                            }
                            
                            System.Diagnostics.Debug.WriteLine("NextBtn_Click: Advancing to Install page");
                            System.Diagnostics.Trace.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] NextBtn_Click: Advancing to Install page");
                            ShowPage("Install");
                        }
                        break;
                    case 4: // Install page
                        System.Diagnostics.Debug.WriteLine("NextBtn_Click: Ignored on Install page while installation is still in progress");
                        System.Diagnostics.Trace.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] NextBtn_Click: Ignored on Install page while installation is still in progress");
                        break;
                    case 5: // Finish page
                        System.Diagnostics.Debug.WriteLine("NextBtn_Click: Finish page - Launching game");
                        System.Diagnostics.Trace.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] NextBtn_Click: Finish page - Launching game");
                        // Launch game if checkbox is checked
                        if (LaunchGameCheckBox?.IsChecked == true)
                        {
                            try
                            {
                                var gamePath = System.IO.Path.Combine(InstallPathBox?.Text ?? "C:\\Program Files\\LatticeVeil", GameExe);
                                if (System.IO.File.Exists(gamePath))
                                {
                                    System.Diagnostics.Process.Start(gamePath);
                                }
                            }
                            catch (Exception ex)
                            {
                                System.Diagnostics.Debug.WriteLine($"NextBtn_Click: Error launching game: {ex.Message}");
                                System.Diagnostics.Trace.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] NextBtn_Click: Error launching game: {ex.Message}");
                                MessageBox.Show($"Failed to launch game: {ex.Message}", "Launch Error", MessageBoxButton.OK, MessageBoxImage.Error);
                            }
                        }
                        Close();
                        break;
                    default:
                        // Default behavior - go to next page
                        System.Diagnostics.Debug.WriteLine("NextBtn_Click: Default behavior - going to next page");
                        System.Diagnostics.Trace.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] NextBtn_Click: Default behavior - going to next page");
                        ShowPage("Destination");
                        break;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"NextBtn_Click Error: {ex.Message}");
                System.Diagnostics.Trace.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] NextBtn_Click Error: {ex.Message}");
                MessageBox.Show($"An error occurred: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        protected override void OnClosed(EventArgs e)
        {
            try
            {
                _cancellationTokenSource?.Cancel();
                _cancellationTokenSource?.Dispose();
                _httpClient?.Dispose();
            }
            catch
            {
                // Ignore cleanup errors
            }
            finally
            {
                base.OnClosed(e);
            }
        }

        private async Task ImportWorldFilesAsync()
        {
            try
            {
                var worldsPath = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "LatticeVeil", "Worlds");
                
                System.Diagnostics.Debug.WriteLine($"ImportWorldFilesAsync: Creating Worlds directory at {worldsPath}");
                System.Diagnostics.Trace.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] ImportWorldFilesAsync: Creating Worlds directory at {worldsPath}");
                
                if (!System.IO.Directory.Exists(worldsPath))
                {
                    System.IO.Directory.CreateDirectory(worldsPath);
                    System.Diagnostics.Trace.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] ImportWorldFilesAsync: Created Worlds directory");
                }

                for (int i = 0; i < _selectedLvWorldFiles.Count; i++)
                {
                    var lvWorldFile = _selectedLvWorldFiles[i];
                    var fileName = System.IO.Path.GetFileNameWithoutExtension(lvWorldFile);
                    var sanitizedWorldName = SanitizeWorldName(fileName);
                    var worldFolderPath = System.IO.Path.Combine(worldsPath, sanitizedWorldName);

                    System.Diagnostics.Debug.WriteLine($"ImportWorldFilesAsync: Processing {lvWorldFile} -> {worldFolderPath}");
                    System.Diagnostics.Trace.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] ImportWorldFilesAsync: Processing {lvWorldFile} -> {worldFolderPath}");

                    // Extract .lvworld file (treat as zip)
                    System.IO.Compression.ZipFile.ExtractToDirectory(lvWorldFile, worldFolderPath);

                    var progress = 80 + (int)((double)(i + 1) / _selectedLvWorldFiles.Count * 20);
                    Dispatcher.Invoke(() => 
                    {
                        InstallProgressBar.Value = progress;
                        InstallProgressText.Text = $"{progress}%";
                        InstallStatus.Text = $"Importing world {i + 1} of {_selectedLvWorldFiles.Count}: {sanitizedWorldName}";
                    });

                    System.Diagnostics.Debug.WriteLine($"ImportWorldFilesAsync: Successfully imported {sanitizedWorldName}");
                    System.Diagnostics.Trace.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] ImportWorldFilesAsync: Successfully imported {sanitizedWorldName}");
                    
                    await Task.Delay(200);
                }

                System.Diagnostics.Debug.WriteLine($"ImportWorldFilesAsync: Completed importing {_selectedLvWorldFiles.Count} world files");
                System.Diagnostics.Trace.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] ImportWorldFilesAsync: Completed importing {_selectedLvWorldFiles.Count} world files");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"ImportWorldFilesAsync Error: {ex.Message}");
                System.Diagnostics.Trace.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] ImportWorldFilesAsync Error: {ex.Message}");
                
                Dispatcher.Invoke(() => 
                {
                    MessageBox.Show($"Failed to import world files: {ex.Message}", "Import Error", MessageBoxButton.OK, MessageBoxImage.Error);
                });
            }
        }

        private string SanitizeWorldName(string worldName)
        {
            var invalidChars = System.IO.Path.GetInvalidFileNameChars();
            var sanitizedName = worldName;
            
            foreach (var invalidChar in invalidChars)
            {
                sanitizedName = sanitizedName.Replace(invalidChar.ToString(), "_");
            }
            
            // Remove any leading/trailing spaces and dots
            sanitizedName = sanitizedName.Trim('.', ' ');
            
            // Ensure name is not empty
            if (string.IsNullOrEmpty(sanitizedName))
            {
                sanitizedName = "ImportedWorld";
            }
            
            return sanitizedName;
        }
    }
}
