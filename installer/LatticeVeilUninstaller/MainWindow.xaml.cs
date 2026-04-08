using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
namespace LatticeVeilUninstaller
{
    public partial class MainWindow : Window
    {
        private const string InstallMetadataFile = "latticeveil_install_info.txt";
        private readonly List<WorldInfo> _worlds = new();
        private CancellationTokenSource? _cancellationTokenSource;
        private string _installPath = "";
        private string _documentsPath = "";
        private string _installedVersion = "";
        private string _backupRootPath = "";
        private bool _isInitialized = false;
        private bool _uninstallCompleted = false;
        private bool _cleanupScheduled = false;
        private bool _hasWorldsAvailable = false;
        private int _currentPage = 0;
        private readonly Random _random = new Random();

        private readonly List<string> _splashMessages = new List<string>
        {
            "Sadly saying goodbye to LatticeVeil...",
            "Removing your adventures...",
            "Cleaning up the memories...",
            "Farewell, brave explorer...",
            "Packing away the moonlight...",
            "Sweeping away the stardust...",
            "Closing the chapter on LatticeVeil...",
            "Vanishing into the digital sunset...",
            "Erasing traces of your journey...",
            "Bidding farewell to the void..."
        };

        private void Window_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.LeftButton == MouseButtonState.Pressed)
            {
                this.DragMove();
            }
        }

        public MainWindow()
        {
            InitializeComponent();
            _cancellationTokenSource = new CancellationTokenSource();

            if (DeleteTokenCheckBox != null)
                DeleteTokenCheckBox.IsChecked = false;
            if (DeleteDocumentsCheckBox != null)
                DeleteDocumentsCheckBox.IsChecked = false;

            this.Title = "LatticeVeil Uninstaller - Loading...";
            Debug.WriteLine("MainWindow constructor: Initializing uninstaller");
            Trace.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] MainWindow constructor: Initializing uninstaller");

            if (Keyboard.IsKeyDown(Key.LeftShift) || Keyboard.IsKeyDown(Key.RightShift))
            {
                Debug.WriteLine("MainWindow constructor: Shift key detected, enabling debug mode");
                Trace.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] MainWindow constructor: Shift key detected, enabling debug mode");
                EnableDebugMode();
            }

            this.MouseLeftButtonDown += (s, e) => { if (e.LeftButton == MouseButtonState.Pressed) this.DragMove(); };

            if (NextBtn != null)
                NextBtn.IsEnabled = false;

            LoadInstallInformation();
            _ = Task.Run(async () => { await InitializeUninstallerAsync(); });
        }

        private void EnableDebugMode()
        {
            string exeDir = AppContext.BaseDirectory;
            string logPath = Path.Combine(exeDir, "installer_debug.log");
            Trace.Listeners.Add(new TextWriterTraceListener(logPath));
            Trace.AutoFlush = true;
            Trace.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] === DEBUG LOG STARTED IN {exeDir} ===");

            Debug.WriteLine("=== DEBUG MODE ENABLED ===");
            Trace.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] === DEBUG MODE ENABLED ===");
            Debug.WriteLine($"Uninstaller Version: {Assembly.GetExecutingAssembly().GetName().Version}");
            Trace.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] Uninstaller Version: {Assembly.GetExecutingAssembly().GetName().Version}");
            Debug.WriteLine($"Launch Time: {DateTime.Now}");
            Trace.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] Launch Time: {DateTime.Now}");
            Debug.WriteLine($"Working Directory: {Environment.CurrentDirectory}");
            Trace.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] Working Directory: {Environment.CurrentDirectory}");

            Dispatcher.Invoke(() =>
            {
                MessageBox.Show("Debug Mode Enabled - Detailed logging active", "Debug Info", MessageBoxButton.OK, MessageBoxImage.Information);
                Trace.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] Debug message shown to user");
            });
        }

        private void LoadInstallInformation()
        {
            try
            {
                string fallbackInstallPath = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                string registryInstallPath = fallbackInstallPath;
                string version = "Unknown";
                string releaseTag = "";

                using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\LatticeVeil");
                if (key != null)
                {
                    registryInstallPath = key.GetValue("InstallLocation")?.ToString() ?? fallbackInstallPath;
                    version = key.GetValue("DisplayVersion")?.ToString() ?? "Unknown";
                    releaseTag = key.GetValue("ReleaseTag")?.ToString() ?? "";
                }

                var metadata = ReadInstallMetadata(registryInstallPath);
                if (metadata.TryGetValue("InstallLocation", out var metadataInstallLocation) && !string.IsNullOrWhiteSpace(metadataInstallLocation))
                    registryInstallPath = metadataInstallLocation;
                if (metadata.TryGetValue("DisplayVersion", out var metadataDisplayVersion) && !string.IsNullOrWhiteSpace(metadataDisplayVersion))
                    version = metadataDisplayVersion;
                else if (metadata.TryGetValue("ReleaseTag", out var metadataReleaseTag) && !string.IsNullOrWhiteSpace(metadataReleaseTag))
                    releaseTag = metadataReleaseTag;

                if (version == "Unknown" && !string.IsNullOrWhiteSpace(releaseTag))
                    version = releaseTag;

                _installPath = registryInstallPath;
                _documentsPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "LatticeVeil");
                _backupRootPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "LatticeVeilWorldBackups");
                _installedVersion = version;

                Dispatcher.Invoke(() =>
                {
                    this.Title = $"LatticeVeil Uninstaller - {_installedVersion}";
                    if (VersionIndicator != null)
                        VersionIndicator.Text = _installedVersion == "Unknown" ? "" : _installedVersion;
                    if (BackupPathBox != null)
                        BackupPathBox.Text = _backupRootPath;
                });
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"LoadInstallInformation Error: {ex.Message}");
                Trace.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] LoadInstallInformation Error: {ex.Message}");
                _installPath = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                _documentsPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "LatticeVeil");
                _backupRootPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "LatticeVeilWorldBackups");
                _installedVersion = "Unknown";
            }
        }

        private Dictionary<string, string> ReadInstallMetadata(string installPath)
        {
            var metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            try
            {
                string[] candidatePaths =
                {
                    Path.Combine(installPath, InstallMetadataFile),
                    Path.Combine(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), InstallMetadataFile)
                };

                foreach (var candidatePath in candidatePaths.Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    if (!File.Exists(candidatePath))
                        continue;

                    foreach (var line in File.ReadAllLines(candidatePath))
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

                    break;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"ReadInstallMetadata Error: {ex.Message}");
                Trace.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] ReadInstallMetadata Error: {ex.Message}");
            }

            return metadata;
        }

        private async Task InitializeUninstallerAsync()
        {
            var cancellationToken = _cancellationTokenSource?.Token ?? CancellationToken.None;
            await UpdateSplashMessagesAsync(cancellationToken);
            await UpdateInitializationProgressAsync(25, "Reading installed version...");
            await UpdateInitializationProgressAsync(60, "Loading uninstall options...");
            await UpdateInitializationProgressAsync(100, "Uninstaller ready.");

            Dispatcher.Invoke(() =>
            {
                _isInitialized = true;
                ShowPage("Options");
            });
        }

        private async Task UpdateSplashMessagesAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string splashMessage = _splashMessages[_random.Next(_splashMessages.Count)];
            Dispatcher.Invoke(() =>
            {
                if (SplashMessage != null)
                    SplashMessage.Text = splashMessage;
            });
            await Task.Delay(350, cancellationToken);
        }

        private Task UpdateInitializationProgressAsync(double progressValue, string status)
        {
            Dispatcher.Invoke(() =>
            {
                if (InitProgress != null)
                    InitProgress.Value = progressValue;
                if (InitializationStatus != null)
                    InitializationStatus.Text = status;
            });

            Trace.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] Initialization: {progressValue:F0}% - {status}");
            return Task.Delay(150);
        }

        private void UpdateNavigationButtons()
        {
            if (PreviousBtn == null || NextBtn == null || CancelBtn == null || NavigationBorder == null)
                return;

            PreviousBtn.IsEnabled = false;
            PreviousBtn.Visibility = Visibility.Visible;
            NextBtn.Visibility = Visibility.Visible;
            CancelBtn.Visibility = Visibility.Visible;
            NavigationBorder.Visibility = Visibility.Visible;

            switch (_currentPage)
            {
                case 0:
                    PreviousBtn.Visibility = Visibility.Hidden;
                    NextBtn.IsEnabled = false;
                    CancelBtn.IsEnabled = true;
                    NextBtn.Content = "NEXT";
                    break;
                case 1:
                    PreviousBtn.Visibility = Visibility.Hidden;
                    NextBtn.IsEnabled = _isInitialized;
                    CancelBtn.IsEnabled = true;
                    NextBtn.Content = "NEXT";
                    break;
                case 2:
                    PreviousBtn.IsEnabled = true;
                    NextBtn.IsEnabled = true;
                    CancelBtn.IsEnabled = true;
                    NextBtn.Content = "NEXT";
                    break;
                case 3:
                    PreviousBtn.IsEnabled = true;
                    NextBtn.IsEnabled = true;
                    CancelBtn.IsEnabled = true;
                    NextBtn.Content = "UNINSTALL";
                    break;
                case 4:
                case 5:
                    NavigationBorder.Visibility = Visibility.Collapsed;
                    break;
            }
        }

        private void ShowPage(string pageName)
        {
            if (InitializingPage != null)
                InitializingPage.Visibility = Visibility.Collapsed;
            if (OptionsPage != null)
                OptionsPage.Visibility = Visibility.Collapsed;
            if (WorldBackupPage != null)
                WorldBackupPage.Visibility = Visibility.Collapsed;
            if (ConfirmationPage != null)
                ConfirmationPage.Visibility = Visibility.Collapsed;
            if (ProgressPage != null)
                ProgressPage.Visibility = Visibility.Collapsed;
            if (CompletePage != null)
                CompletePage.Visibility = Visibility.Collapsed;

            switch (pageName)
            {
                case "Initializing":
                    if (InitializingPage != null)
                        InitializingPage.Visibility = Visibility.Visible;
                    _currentPage = 0;
                    break;
                case "Options":
                    if (OptionsPage != null)
                        OptionsPage.Visibility = Visibility.Visible;
                    _currentPage = 1;
                    break;
                case "WorldBackup":
                    if (WorldBackupPage != null)
                        WorldBackupPage.Visibility = Visibility.Visible;
                    _currentPage = 2;
                    break;
                case "Confirmation":
                    if (ConfirmationPage != null)
                        ConfirmationPage.Visibility = Visibility.Visible;
                    _currentPage = 3;
                    break;
                case "Progress":
                    if (ProgressPage != null)
                        ProgressPage.Visibility = Visibility.Visible;
                    _currentPage = 4;
                    break;
                case "Complete":
                    if (CompletePage != null)
                        CompletePage.Visibility = Visibility.Visible;
                    _currentPage = 5;
                    break;
            }

            UpdateNavigationButtons();
        }

        private void PreviousBtn_Click(object sender, RoutedEventArgs e)
        {
            switch (_currentPage)
            {
                case 2:
                    ShowPage("Options");
                    break;
                case 3:
                    if (_hasWorldsAvailable)
                        ShowPage("WorldBackup");
                    else
                        ShowPage("Options");
                    break;
            }
        }

        private void CloseBtn_Click(object sender, RoutedEventArgs e)
        {
            CancelBtn_Click(sender, e);
        }

        private void MinimizeBtn_Click(object sender, RoutedEventArgs e)
        {
            WindowState = WindowState.Minimized;
        }

        private void NextBtn_Click(object sender, RoutedEventArgs e)
        {
            switch (_currentPage)
            {
                case 1:
                    LoadWorlds();
                    if (_hasWorldsAvailable)
                    {
                        ShowPage("WorldBackup");
                    }
                    else
                    {
                        ShowConfirmationSummary();
                        ShowPage("Confirmation");
                    }
                    break;
                case 2:
                    if (_hasWorldsAvailable && !_worlds.Any(w => w.Checked))
                    {
                        var result = MessageBox.Show("ARE YOU SURE YOU DO NOT WANT TO BACKUP WORLDS?",
                            "World Backup", MessageBoxButton.YesNo, MessageBoxImage.Warning);

                        if (result != MessageBoxResult.Yes)
                            return;
                    }

                    ShowConfirmationSummary();
                    ShowPage("Confirmation");
                    break;
                case 3:
                    if (!ValidateBackupPath())
                        return;

                    ShowPage("Progress");
                    bool deleteLoginToken = DeleteTokenCheckBox.IsChecked == true;
                    bool deleteDocumentsFolder = DeleteDocumentsCheckBox.IsChecked == true;
                    string backupRootPath = BackupPathBox?.Text ?? _backupRootPath;
                    var selectedWorlds = _worlds.Where(w => w.Checked).ToList();
                    _ = Task.Run(async () => { await PerformUninstallAsync(deleteLoginToken, deleteDocumentsFolder, backupRootPath, selectedWorlds); });
                    break;
            }
        }

        private void LoadWorlds()
        {
            _worlds.Clear();
            WorldsList.Children.Clear();
            _hasWorldsAvailable = false;

            var worldsPath = Path.Combine(_documentsPath, "Worlds");
            if (!Directory.Exists(worldsPath))
            {
                WorldBackupDescription.Text = "No worlds were found in Documents\\LatticeVeil\\Worlds.";
                BackupAllBtn.IsEnabled = false;
                WorldsList.Children.Add(CreateEmptyWorldMessage("No worlds were found to back up."));
                return;
            }

            foreach (var worldDir in Directory.GetDirectories(worldsPath))
            {
                var worldInfo = LoadWorldInfo(worldDir);
                if (worldInfo != null)
                {
                    _worlds.Add(worldInfo);
                    WorldsList.Children.Add(CreateWorldControl(worldInfo));
                }
            }

            if (_worlds.Count == 0)
            {
                WorldBackupDescription.Text = "No valid worlds were found in Documents\\LatticeVeil\\Worlds.";
                BackupAllBtn.IsEnabled = false;
                WorldsList.Children.Add(CreateEmptyWorldMessage("No valid worlds were found to back up."));
            }
            else
            {
                WorldBackupDescription.Text = "Select any worlds you want backed up before LatticeVeil is removed.";
                BackupAllBtn.IsEnabled = true;
                _hasWorldsAvailable = true;
            }
        }

        private Border CreateEmptyWorldMessage(string message)
        {
            return new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(20, 20, 20)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(90, 90, 90)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(16),
                Child = new TextBlock
                {
                    Text = message,
                    Foreground = new SolidColorBrush(Color.FromRgb(170, 170, 170)),
                    TextWrapping = TextWrapping.Wrap
                }
            };
        }

        private WorldInfo? LoadWorldInfo(string worldDir)
        {
            try
            {
                var worldFile = Path.Combine(worldDir, "world.lvc");
                if (!File.Exists(worldFile))
                    return null;

                var worldInfo = new WorldInfo
                {
                    Directory = worldDir,
                    Checked = false,
                    PreviewPath = Path.Combine(worldDir, "preview.png"),
                    Name = Path.GetFileName(worldDir)
                };

                foreach (var line in File.ReadAllLines(worldFile))
                {
                    if (line.StartsWith("Name="))
                        worldInfo.Name = line.Substring(5);
                    else if (line.StartsWith("GameMode="))
                        worldInfo.GameMode = line.Substring(9);
                    else if (line.StartsWith("Seed="))
                        worldInfo.Seed = line.Substring(5);
                    else if (line.StartsWith("EnableCheats="))
                        worldInfo.EnableCheats = string.Equals(line.Substring(13), "true", StringComparison.OrdinalIgnoreCase);
                    else if (line.StartsWith("WorldType="))
                        worldInfo.WorldType = line.Substring(10);
                }

                return worldInfo;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"LoadWorldInfo Error: {ex.Message}");
                Trace.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] LoadWorldInfo Error: {ex.Message}");
                return null;
            }
        }

        private Border CreateWorldControl(WorldInfo world)
        {
            var border = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(20, 20, 20)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(90, 90, 90)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Margin = new Thickness(0, 0, 0, 12),
                Padding = new Thickness(14)
            };

            var rootGrid = new Grid();
            rootGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            rootGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            rootGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var checkBox = new CheckBox
            {
                IsChecked = world.Checked,
                Style = (Style)FindResource("ModernCheckBox"),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 16, 0)
            };
            checkBox.Checked += (s, e) => world.Checked = true;
            checkBox.Unchecked += (s, e) => world.Checked = false;
            world.SelectionCheckBox = checkBox;
            Grid.SetColumn(checkBox, 0);

            var image = new Image
            {
                Width = 96,
                Height = 96,
                Stretch = Stretch.UniformToFill,
                Margin = new Thickness(0, 0, 16, 0),
                Source = LoadWorldPreview(world)
            };
            Grid.SetColumn(image, 1);

            var textStack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            textStack.Children.Add(new TextBlock { Text = world.Name, Foreground = Brushes.White, FontWeight = FontWeights.Bold, FontSize = 16 });
            textStack.Children.Add(new TextBlock { Text = $"GameMode: {world.GameMode}", Foreground = new SolidColorBrush(Color.FromRgb(200, 200, 200)), Margin = new Thickness(0, 6, 0, 0) });
            textStack.Children.Add(new TextBlock { Text = $"Seed: {world.Seed}", Foreground = new SolidColorBrush(Color.FromRgb(200, 200, 200)), Margin = new Thickness(0, 4, 0, 0) });
            textStack.Children.Add(new TextBlock { Text = $"EnableCheats: {world.EnableCheats}", Foreground = world.EnableCheats ? new SolidColorBrush(Color.FromRgb(255, 165, 0)) : new SolidColorBrush(Color.FromRgb(200, 200, 200)), Margin = new Thickness(0, 4, 0, 0) });
            textStack.Children.Add(new TextBlock { Text = $"WorldType: {world.WorldType}", Foreground = new SolidColorBrush(Color.FromRgb(200, 200, 200)), Margin = new Thickness(0, 4, 0, 0) });
            Grid.SetColumn(textStack, 2);

            rootGrid.Children.Add(checkBox);
            rootGrid.Children.Add(image);
            rootGrid.Children.Add(textStack);
            border.Child = rootGrid;
            return border;
        }

        private ImageSource? LoadWorldPreview(WorldInfo world)
        {
            try
            {
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.UriSource = File.Exists(world.PreviewPath) ? new Uri(world.PreviewPath, UriKind.Absolute) : new Uri("pack://application:,,,/Icon.ico", UriKind.Absolute);
                bitmap.EndInit();
                bitmap.Freeze();
                return bitmap;
            }
            catch
            {
                return null;
            }
        }

        private void BackupAllBtn_Click(object sender, RoutedEventArgs e)
        {
            foreach (var world in _worlds)
            {
                world.Checked = true;
                if (world.SelectionCheckBox != null)
                    world.SelectionCheckBox.IsChecked = true;
            }
        }

        private void ShowConfirmationSummary()
        {
            var selectedWorlds = _worlds.Where(w => w.Checked).ToList();
            var backupRootPath = BackupPathBox?.Text ?? _backupRootPath;
            var summary = "The following actions will be performed:\n\n";
            summary += "• Remove LatticeVeil application files\n";
            summary += "• Remove shortcuts\n";
            summary += "• Remove uninstall registry entries\n";

            if (DeleteTokenCheckBox.IsChecked == true)
                summary += "• Delete account login token\n";

            if (selectedWorlds.Count > 0)
                summary += $"• Backup {selectedWorlds.Count} world(s) as .lvworld\n";
            if (selectedWorlds.Count > 0)
                summary += $"• Save backups to: {backupRootPath}\n";

            if (DeleteDocumentsCheckBox.IsChecked == true)
                summary += "• Delete Documents\\LatticeVeil folder entirely (including all worlds and saves)\n";

            ConfirmationText.Text = summary;
        }

        private async Task PerformUninstallAsync(bool deleteLoginToken, bool deleteDocumentsFolder, string backupRootPath, List<WorldInfo> selectedWorlds)
        {
            try
            {
                await UpdateProgressAsync(5, "Preparing to uninstall...");

                if (deleteLoginToken)
                {
                    await UpdateProgressAsync(15, "Deleting account login token...");
                    DeleteLoginToken();
                }

                if (selectedWorlds.Count > 0)
                {
                    await UpdateProgressAsync(35, "Backing up selected worlds...");
                    BackupSelectedWorlds(selectedWorlds, backupRootPath);
                }

                await UpdateProgressAsync(55, "Removing shortcuts...");
                DeleteShortcuts();

                await UpdateProgressAsync(70, "Removing registry entries...");
                DeleteRegistryEntries();

                await UpdateProgressAsync(85, "Removing installed files...");
                DeleteInstalledFiles();

                if (deleteDocumentsFolder)
                {
                    await UpdateProgressAsync(95, "Deleting Documents\\LatticeVeil...");
                    DeleteDocumentsFolder();
                }

                await UpdateProgressAsync(100, "Uninstallation complete!");
                _uninstallCompleted = true;
                Dispatcher.Invoke(() => ShowPage("Complete"));
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"PerformUninstallAsync Error: {ex.Message}");
                Trace.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] PerformUninstallAsync Error: {ex.Message}");
                Dispatcher.Invoke(() =>
                {
                    MessageBox.Show($"Uninstallation failed: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                    ShowPage("Confirmation");
                });
            }
        }

        private Task UpdateProgressAsync(double value, string status)
        {
            Dispatcher.Invoke(() =>
            {
                UninstallProgressBar.Value = value;
                UninstallProgressText.Text = $"{value:F0}%";
                UninstallStatus.Text = status;
            });

            Trace.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] Progress: {value:F0}% - {status}");
            return Task.Delay(250);
        }

        private void DeleteLoginToken()
        {
            try
            {
                var tokenPath = Path.Combine(_installPath, "login_token.json");
                if (File.Exists(tokenPath))
                {
                    File.Delete(tokenPath);
                    Trace.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] Deleted login token at {tokenPath}");
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"DeleteLoginToken Error: {ex.Message}");
                Trace.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] DeleteLoginToken Error: {ex.Message}");
            }
        }

        private void BackupSelectedWorlds(List<WorldInfo> selectedWorlds, string backupRootPath)
        {
            Directory.CreateDirectory(backupRootPath);

            foreach (var world in selectedWorlds)
            {
                var backupPath = Path.Combine(backupRootPath, $"{SanitizeWorldName(world.Name)}.lvworld");
                if (File.Exists(backupPath))
                    File.Delete(backupPath);

                ZipFile.CreateFromDirectory(world.Directory, backupPath, CompressionLevel.Optimal, false);
                Trace.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] Backed up world to {backupPath}");
            }
        }

        private void BrowseBackupPathBtn_Click(object sender, RoutedEventArgs e)
        {
            using var dialog = new System.Windows.Forms.FolderBrowserDialog
            {
                Description = "Select where world backups should be saved",
                UseDescriptionForTitle = true,
                InitialDirectory = BackupPathBox?.Text ?? _backupRootPath,
                ShowNewFolderButton = true
            };

            if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            {
                _backupRootPath = dialog.SelectedPath;
                if (BackupPathBox != null)
                    BackupPathBox.Text = _backupRootPath;
            }
        }

        private bool ValidateBackupPath()
        {
            if (!_worlds.Any(w => w.Checked))
                return true;

            string backupPath = BackupPathBox?.Text?.Trim() ?? "";
            if (string.IsNullOrWhiteSpace(backupPath))
            {
                MessageBox.Show("Please choose a valid backup folder.", "Invalid Backup Folder", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }

            if (DeleteDocumentsCheckBox.IsChecked == true)
            {
                string normalizedBackupPath = Path.GetFullPath(backupPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                string normalizedDocumentsPath = Path.GetFullPath(_documentsPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

                if (normalizedBackupPath.StartsWith(normalizedDocumentsPath, StringComparison.OrdinalIgnoreCase))
                {
                    MessageBox.Show("Choose a backup folder outside Documents\\LatticeVeil if you plan to delete that folder.", "Invalid Backup Folder", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return false;
                }
            }

            _backupRootPath = backupPath;
            return true;
        }

        private void DeleteRegistryEntries()
        {
            try
            {
                Registry.LocalMachine.DeleteSubKeyTree(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\LatticeVeil", false);
                Trace.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] Deleted uninstall registry entries");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"DeleteRegistryEntries Error: {ex.Message}");
                Trace.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] DeleteRegistryEntries Error: {ex.Message}");
            }
        }

        private void DeleteShortcuts()
        {
            try
            {
                var desktopPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "LatticeVeil.lnk");
                if (File.Exists(desktopPath))
                    File.Delete(desktopPath);

                var startMenuPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "LatticeVeil");
                if (Directory.Exists(startMenuPath))
                    Directory.Delete(startMenuPath, true);

                Trace.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] Deleted shortcuts");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"DeleteShortcuts Error: {ex.Message}");
                Trace.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] DeleteShortcuts Error: {ex.Message}");
            }
        }

        private void DeleteInstalledFiles()
        {
            try
            {
                if (!Directory.Exists(_installPath))
                    return;

                foreach (var file in Directory.GetFiles(_installPath))
                {
                    var fileName = Path.GetFileName(file);
                    if (!string.Equals(fileName, "LatticeVeilUninstaller.exe", StringComparison.OrdinalIgnoreCase))
                        File.Delete(file);
                }

                foreach (var dir in Directory.GetDirectories(_installPath))
                {
                    Directory.Delete(dir, true);
                }

                Trace.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] Deleted installed files from {_installPath}");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"DeleteInstalledFiles Error: {ex.Message}");
                Trace.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] DeleteInstalledFiles Error: {ex.Message}");
            }
        }

        private void DeleteDocumentsFolder()
        {
            try
            {
                if (Directory.Exists(_documentsPath))
                {
                    Directory.Delete(_documentsPath, true);
                    Trace.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] Deleted documents folder {_documentsPath}");
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"DeleteDocumentsFolder Error: {ex.Message}");
                Trace.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] DeleteDocumentsFolder Error: {ex.Message}");
            }
        }

        private void FinishBtn_Click(object sender, RoutedEventArgs e)
        {
            if (_uninstallCompleted && !_cleanupScheduled)
            {
                _cleanupScheduled = true;
                ExtractAndRunCleanupBatch();
            }

            Close();
        }

        private void ExtractAndRunCleanupBatch()
        {
            try
            {
                var workingDirectory = Path.GetDirectoryName(_installPath);
                if (string.IsNullOrWhiteSpace(workingDirectory))
                    workingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Windows);

                string escapedInstallPath = _installPath.Replace("'", "''");
                string cleanupScript =
                    "$target = '" + escapedInstallPath + "'; " +
                    "Start-Sleep -Seconds 2; " +
                    "for ($i = 0; $i -lt 10; $i++) { " +
                    "if (-not (Test-Path -LiteralPath $target)) { break } " +
                    "try { Remove-Item -LiteralPath $target -Recurse -Force -ErrorAction Stop } catch { Start-Sleep -Seconds 1 } " +
                    "}";

                var process = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = "powershell.exe",
                        Arguments = $"-NoProfile -WindowStyle Hidden -ExecutionPolicy Bypass -Command \"{cleanupScript}\"",
                        WorkingDirectory = workingDirectory,
                        CreateNoWindow = true,
                        UseShellExecute = false,
                        WindowStyle = ProcessWindowStyle.Hidden
                    }
                };

                process.Start();
                Trace.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] Started final cleanup for {_installPath}");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"ExtractAndRunCleanupBatch Error: {ex.Message}");
                Trace.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] ExtractAndRunCleanupBatch Error: {ex.Message}");
            }
        }

        private void CancelBtn_Click(object sender, RoutedEventArgs e)
        {
            var result = MessageBox.Show("Are you sure you want to cancel the uninstallation?",
                "Cancel Uninstallation", MessageBoxButton.YesNo, MessageBoxImage.Question);

            if (result == MessageBoxResult.Yes)
            {
                _cancellationTokenSource?.Cancel();
                Close();
            }
        }

        private string SanitizeWorldName(string worldName)
        {
            var invalidChars = Path.GetInvalidFileNameChars();
            var sanitizedName = worldName;

            foreach (var invalidChar in invalidChars)
            {
                sanitizedName = sanitizedName.Replace(invalidChar.ToString(), "_");
            }

            sanitizedName = sanitizedName.Trim('.', ' ');
            if (string.IsNullOrEmpty(sanitizedName))
                sanitizedName = "ImportedWorld";

            return sanitizedName;
        }

        protected override void OnClosed(EventArgs e)
        {
            try
            {
                _cancellationTokenSource?.Cancel();
                _cancellationTokenSource?.Dispose();
            }
            catch
            {
            }
            finally
            {
                base.OnClosed(e);
            }
        }
    }

    public class WorldInfo
    {
        public string Name { get; set; } = "Unknown World";
        public string GameMode { get; set; } = "Survival";
        public string Seed { get; set; } = "0";
        public string WorldType { get; set; } = "Normal";
        public bool EnableCheats { get; set; }
        public string Directory { get; set; } = "";
        public string PreviewPath { get; set; } = "";
        public bool Checked { get; set; } = false;
        public CheckBox? SelectionCheckBox { get; set; }
    }
}
