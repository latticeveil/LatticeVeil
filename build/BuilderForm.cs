using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows.Forms;
using System.Drawing;
using System.Linq;
using System.Threading.Tasks;

namespace LatticeVeilBuilder
{
    public partial class BuilderForm : Form
    {
        private readonly string _repoRoot = @"C:\Users\Redacted\Documents\LatticeVeil_project";
        private readonly string _gameProject = @"C:\Users\Redacted\Documents\LatticeVeil_project\LatticeVeilMonoGame";
        private readonly string _devAssetsRoot = @"C:\Users\Redacted\Documents\LatticeVeil_project\LatticeVeilMonoGame\Defaults\Assets";
        private readonly string _releaseAssetsRoot;
        private readonly string _builderDir;
        private readonly string _logsDir;
        private readonly string _zipsDir;
        private readonly AssetManager _assetManager;

        private Button btnLocalRun;
        private Button btnReleaseRun;
        private Button btnMakeZip;
        private Button btnCleanup;
        private Button btnOpenLog;
        private Button btnCheckUpdate;
        private Button btnRepairAssets;
        private Button btnPlay;
        private Label lblStatus;
        private Label lblDevPath;
        private Label lblReleasePath;
        private Label lblDevMode;
        private TextBox txtLog;

        public BuilderForm()
        {
            _releaseAssetsRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "LatticeVeil", "Assets");
            _builderDir = Path.Combine(_repoRoot, ".builder");
            _logsDir = Path.Combine(_builderDir, "logs");
            _zipsDir = Path.Combine(_builderDir, "release_zips");
            _assetManager = new AssetManager(_releaseAssetsRoot);

            InitializeComponent();
            
            // Check for dev mode
            var isDevLocal = Environment.GetEnvironmentVariable("LATTICEVEIL_DEV_LOCAL") == "1";
            if (isDevLocal)
            {
                lblDevMode.Text = "DEV LOCAL ASSETS ENABLED";
                lblDevMode.ForeColor = Color.FromArgb(255, 255, 0);
                lblDevMode.Visible = true;
            }
        }

        private void InitializeComponent()
        {
            Text = "LatticeVeil Builder";
            Size = new Size(700, 600);
            StartPosition = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.FixedSingle;
            BackColor = Color.FromArgb(45, 45, 48);

            // Title
            var lblTitle = new Label
            {
                Text = "LATTICEVEIL BUILDER",
                Font = new Font("Consolas", 16, FontStyle.Bold),
                ForeColor = Color.FromArgb(0, 255, 127),
                AutoSize = true,
                Location = new Point(20, 20)
            };
            Controls.Add(lblTitle);

            // Path labels
            lblDevPath = new Label
            {
                Text = $"DEV Assets: {_devAssetsRoot}",
                Font = new Font("Segoe UI", 8),
                ForeColor = Color.FromArgb(200, 200, 200),
                AutoSize = true,
                Location = new Point(20, 60)
            };
            Controls.Add(lblDevPath);

            lblReleasePath = new Label
            {
                Text = $"RELEASE Assets: {_releaseAssetsRoot}",
                Font = new Font("Segoe UI", 8),
                ForeColor = Color.FromArgb(200, 200, 200),
                AutoSize = true,
                Location = new Point(20, 80)
            };
            Controls.Add(lblReleasePath);

            // Status label
            lblStatus = new Label
            {
                Text = "Ready",
                Font = new Font("Segoe UI", 10, FontStyle.Bold),
                ForeColor = Color.White,
                AutoSize = true,
                Location = new Point(20, 110)
            };
            Controls.Add(lblStatus);

            // Dev mode label (initially hidden)
            lblDevMode = new Label
            {
                Text = "DEV LOCAL ASSETS ENABLED",
                Font = new Font("Segoe UI", 10, FontStyle.Bold),
                ForeColor = Color.FromArgb(255, 255, 0),
                AutoSize = true,
                Location = new Point(20, 135),
                Visible = false
            };
            Controls.Add(lblDevMode);

            // Buttons
            btnLocalRun = new Button
            {
                Text = "LOCAL RUN (Defaults Assets)",
                Size = new Size(200, 50),
                Location = new Point(20, 170),
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(0, 120, 215),
                ForeColor = Color.White,
                Font = new Font("Segoe UI", 9)
            };
            btnLocalRun.Click += BtnLocalRun_Click;
            Controls.Add(btnLocalRun);

            btnReleaseRun = new Button
            {
                Text = "RELEASE RUN (Documents Assets)",
                Size = new Size(200, 50),
                Location = new Point(240, 170),
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(0, 120, 215),
                ForeColor = Color.White,
                Font = new Font("Segoe UI", 9)
            };
            btnReleaseRun.Click += BtnReleaseRun_Click;
            Controls.Add(btnReleaseRun);

            btnMakeZip = new Button
            {
                Text = "MAKE RELEASE ZIP",
                Size = new Size(200, 50),
                Location = new Point(460, 170),
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(0, 120, 215),
                ForeColor = Color.White,
                Font = new Font("Segoe UI", 9)
            };
            btnMakeZip.Click += BtnMakeZip_Click;
            Controls.Add(btnMakeZip);

            btnCleanup = new Button
            {
                Text = "CLEANUP",
                Size = new Size(130, 50),
                Location = new Point(20, 240),
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(0, 120, 215),
                ForeColor = Color.White,
                Font = new Font("Segoe UI", 9)
            };
            btnCleanup.Click += BtnCleanup_Click;
            Controls.Add(btnCleanup);

            btnOpenLog = new Button
            {
                Text = "OPEN LOG",
                Size = new Size(130, 50),
                Location = new Point(170, 240),
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(0, 120, 215),
                ForeColor = Color.White,
                Font = new Font("Segoe UI", 9)
            };
            btnOpenLog.Click += BtnOpenLog_Click;
            Controls.Add(btnOpenLog);

            // GitHub Asset Management Buttons
            btnCheckUpdate = new Button
            {
                Text = "CHECK/UPDATE ASSETS",
                Size = new Size(150, 40),
                Location = new Point(320, 240),
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(0, 150, 0),
                ForeColor = Color.White,
                Font = new Font("Segoe UI", 8)
            };
            btnCheckUpdate.Click += BtnCheckUpdate_Click;
            Controls.Add(btnCheckUpdate);

            btnRepairAssets = new Button
            {
                Text = "REPAIR ASSETS",
                Size = new Size(130, 40),
                Location = new Point(480, 240),
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(150, 100, 0),
                ForeColor = Color.White,
                Font = new Font("Segoe UI", 8)
            };
            btnRepairAssets.Click += BtnRepairAssets_Click;
            Controls.Add(btnRepairAssets);

            btnPlay = new Button
            {
                Text = "PLAY",
                Size = new Size(100, 40),
                Location = new Point(620, 240),
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(0, 200, 0),
                ForeColor = Color.White,
                Font = new Font("Segoe UI", 9, FontStyle.Bold),
                Enabled = false
            };
            btnPlay.Click += BtnPlay_Click;
            Controls.Add(btnPlay);

            // Log display
            txtLog = new TextBox
            {
                Multiline = true,
                ScrollBars = ScrollBars.Vertical,
                ReadOnly = true,
                BackColor = Color.FromArgb(30, 30, 30),
                ForeColor = Color.LightGreen,
                Font = new Font("Consolas", 8),
                Size = new Size(640, 280),
                Location = new Point(20, 310)
            };
            Controls.Add(txtLog);
        }

        private bool RunPreflight()
        {
            Log("Running preflight checks...");

            var paths = new[]
            {
                _repoRoot,
                _gameProject,
                _devAssetsRoot
            };

            foreach (var path in paths)
            {
                if (!Directory.Exists(path))
                {
                    Log($"DEV PREFLIGHT FAIL: Missing {path}");
                    return false;
                }
            }

            // Create release assets folder if missing
            if (!Directory.Exists(_releaseAssetsRoot))
            {
                Log($"Creating release assets folder: {_releaseAssetsRoot}");
                Directory.CreateDirectory(_releaseAssetsRoot);
            }

            // Create builder directories
            Directory.CreateDirectory(_builderDir);
            Directory.CreateDirectory(_logsDir);
            Directory.CreateDirectory(_zipsDir);

            Log("Preflight checks passed");
            return true;
        }

        private void Log(string message)
        {
            var timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            var logLine = $"[{timestamp}] {message}";
            
            txtLog.Invoke((MethodInvoker)delegate {
                txtLog.AppendText(logLine + Environment.NewLine);
            });

            // Also write to log file
            var logFile = Path.Combine(_logsDir, "builder_latest.log");
            File.AppendAllText(logFile, logLine + Environment.NewLine);
        }

        private void SetButtonsEnabled(bool enabled)
        {
            btnLocalRun.Invoke((MethodInvoker)delegate {
                btnLocalRun.Enabled = enabled;
                btnReleaseRun.Enabled = enabled;
                btnMakeZip.Enabled = enabled;
                btnCleanup.Enabled = enabled;
                btnOpenLog.Enabled = enabled;
                btnCheckUpdate.Enabled = enabled;
                btnRepairAssets.Enabled = enabled;
                btnPlay.Enabled = enabled;
            });
        }

        private void BtnLocalRun_Click(object sender, EventArgs e)
        {
            if (!RunPreflight()) return;

            SetButtonsEnabled(false);
            Log("Starting LOCAL RUN build...");

            try
            {
                Log($"ASSET ROOT = {_devAssetsRoot}");
                Log("ASSET MODE = LOCAL");
                Log("REMOTE ASSETS = DISABLED");

                // Build
                Log("Building game...");
                int buildExit;
                using (var buildProcess = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = "dotnet",
                        Arguments = $"build \"{Path.Combine(_gameProject, "LatticeVeilMonoGame.csproj")}\" --configuration Debug",
                        WorkingDirectory = _gameProject,
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        CreateNoWindow = true
                    }
                })
                {
                    buildProcess.Start();
                    var stdout = buildProcess.StandardOutput.ReadToEnd();
                    var stderr = buildProcess.StandardError.ReadToEnd();
                    if (!string.IsNullOrWhiteSpace(stdout)) Log(stdout);
                    if (!string.IsNullOrWhiteSpace(stderr)) Log(stderr);

                    if (!buildProcess.WaitForExit(10 * 60 * 1000))
                    {
                        try { buildProcess.Kill(entireProcessTree: true); } catch { }
                        Log("Build timed out.");
                        SetButtonsEnabled(true);
                        return;
                    }

                    buildExit = buildProcess.ExitCode;
                }

                if (buildExit != 0)
                {
                    Log($"Build failed with exit code: {buildExit}");
                    SetButtonsEnabled(true);
                    return;
                }

                // Copy assets
                Log("Copying local assets to build directory...");
                var buildAssetsPath = Path.Combine(_gameProject, "bin", "Debug", "net8.0-windows", "Defaults", "Assets");
                if (Directory.Exists(buildAssetsPath))
                {
                    Directory.Delete(buildAssetsPath, true);
                }
                Directory.CreateDirectory(buildAssetsPath);
                CopyDirectory(_devAssetsRoot, buildAssetsPath);

                // Run game as independent process
                Log("Starting game with local assets...");
                var gameExe = Path.Combine(_gameProject, "bin", "Debug", "net8.0-windows", "LatticeVeilGame.exe");
                
                if (!File.Exists(gameExe))
                {
                    Log($"ERROR: Game executable not found at {gameExe}");
                    MessageBox.Show($"Game executable not found at:\n{gameExe}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    SetButtonsEnabled(true);
                    return;
                }

                var gameProcess = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = gameExe,
                        WorkingDirectory = Path.GetDirectoryName(gameExe),
                        UseShellExecute = true
                    }
                };
                gameProcess.Start();

                Log("LOCAL RUN complete - Game started as independent process");
            }
            catch (Exception ex)
            {
                Log($"ERROR: {ex.Message}");
                MessageBox.Show($"Failed to start game: {ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                SetButtonsEnabled(true);
            }
        }

        private void BtnReleaseRun_Click(object sender, EventArgs e)
        {
            if (!RunPreflight()) return;

            SetButtonsEnabled(false);
            Log("Starting RELEASE RUN build...");

            try
            {
                Log($"ASSET ROOT = {_releaseAssetsRoot}");
                Log("ASSET MODE = RELEASE");
                Log("REMOTE ASSETS = ENABLED");

                // Build
                Log("Building release...");
                int buildExit;
                using (var buildProcess = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = "dotnet",
                        Arguments = $"build \"{Path.Combine(_gameProject, "LatticeVeilMonoGame.csproj")}\" --configuration Release",
                        WorkingDirectory = _gameProject,
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        CreateNoWindow = true
                    }
                })
                {
                    buildProcess.Start();
                    var stdout = buildProcess.StandardOutput.ReadToEnd();
                    var stderr = buildProcess.StandardError.ReadToEnd();
                    if (!string.IsNullOrWhiteSpace(stdout)) Log(stdout);
                    if (!string.IsNullOrWhiteSpace(stderr)) Log(stderr);

                    if (!buildProcess.WaitForExit(10 * 60 * 1000))
                    {
                        try { buildProcess.Kill(entireProcessTree: true); } catch { }
                        Log("Build timed out.");
                        SetButtonsEnabled(true);
                        return;
                    }

                    buildExit = buildProcess.ExitCode;
                }

                if (buildExit != 0)
                {
                    Log($"Build failed with exit code: {buildExit}");
                    SetButtonsEnabled(true);
                    return;
                }

                // Run game as independent process
                Log("Starting game with release assets...");
                var gameExe = Path.Combine(_gameProject, "bin", "Release", "net8.0-windows", "LatticeVeilGame.exe");
                
                if (!File.Exists(gameExe))
                {
                    Log($"ERROR: Game executable not found at {gameExe}");
                    MessageBox.Show($"Game executable not found at:\n{gameExe}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    SetButtonsEnabled(true);
                    return;
                }

                var gameProcess = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = gameExe,
                        WorkingDirectory = Path.GetDirectoryName(gameExe),
                        UseShellExecute = true
                    }
                };
                gameProcess.Start();

                Log("RELEASE RUN complete - Game started as independent process");
            }
            catch (Exception ex)
            {
                Log($"ERROR: {ex.Message}");
                MessageBox.Show($"Failed to start game: {ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                SetButtonsEnabled(true);
            }
        }

        private void BtnMakeZip_Click(object sender, EventArgs e)
        {
            if (!RunPreflight()) return;

            SetButtonsEnabled(false);
            Log("Creating release ZIP...");

            try
            {
                // Build launcher
                Log("Building launcher...");
                int launcherExit;
                using (var launcherProcess = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = "dotnet",
                        Arguments = $"publish \"{Path.Combine(_repoRoot, "Builder", "Builder.csproj")}\" -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -p:DebugType=None -p:DebugSymbols=false -o \"{Path.Combine(_builderDir, "launcher_publish")}\"",
                        WorkingDirectory = Path.Combine(_repoRoot, "Builder"),
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        CreateNoWindow = true
                    }
                })
                {
                    launcherProcess.Start();
                    var stdout = launcherProcess.StandardOutput.ReadToEnd();
                    var stderr = launcherProcess.StandardError.ReadToEnd();
                    if (!string.IsNullOrWhiteSpace(stdout)) Log(stdout);
                    if (!string.IsNullOrWhiteSpace(stderr)) Log(stderr);

                    if (!launcherProcess.WaitForExit(10 * 60 * 1000))
                    {
                        try { launcherProcess.Kill(entireProcessTree: true); } catch { }
                        Log("Launcher publish timed out.");
                        SetButtonsEnabled(true);
                        return;
                    }

                    launcherExit = launcherProcess.ExitCode;
                }

                if (launcherExit != 0)
                {
                    Log($"Launcher publish failed with exit code: {launcherExit}");
                    SetButtonsEnabled(true);
                    return;
                }

                // Build game
                Log("Building game...");
                int gameExit;
                using (var gameProcess = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = "dotnet",
                        Arguments = $"publish \"{Path.Combine(_gameProject, "LatticeVeilMonoGame.csproj")}\" -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -p:DebugType=None -p:DebugSymbols=false -o \"{Path.Combine(_builderDir, "game_publish")}\"",
                        WorkingDirectory = _gameProject,
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        CreateNoWindow = true
                    }
                })
                {
                    gameProcess.Start();
                    var stdout = gameProcess.StandardOutput.ReadToEnd();
                    var stderr = gameProcess.StandardError.ReadToEnd();
                    if (!string.IsNullOrWhiteSpace(stdout)) Log(stdout);
                    if (!string.IsNullOrWhiteSpace(stderr)) Log(stderr);

                    if (!gameProcess.WaitForExit(10 * 60 * 1000))
                    {
                        try { gameProcess.Kill(entireProcessTree: true); } catch { }
                        Log("Game publish timed out.");
                        SetButtonsEnabled(true);
                        return;
                    }

                    gameExit = gameProcess.ExitCode;
                }

                if (gameExit != 0)
                {
                    Log($"Game publish failed with exit code: {gameExit}");
                    SetButtonsEnabled(true);
                    return;
                }

                // Create combined release folder
                var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmm");
                var releaseDir = Path.Combine(_builderDir, $"Release_{timestamp}");
                Directory.CreateDirectory(releaseDir);

                // Copy launcher
                Log("Copying launcher...");
                var launcherExe = Path.Combine(_builderDir, "launcher_publish", "LatticeVeil.exe");
                if (File.Exists(launcherExe))
                {
                    File.Copy(launcherExe, Path.Combine(releaseDir, "LatticeVeil.exe"));
                }

                // Copy game
                Log("Copying game...");
                var gameExe = Path.Combine(_builderDir, "game_publish", "LatticeVeilGame.exe");
                if (File.Exists(gameExe))
                {
                    File.Copy(gameExe, Path.Combine(releaseDir, "LatticeVeilGame.exe"));
                }

                // Copy Defaults folder if needed
                var defaultsDir = Path.Combine(_gameProject, "Defaults");
                if (Directory.Exists(defaultsDir))
                {
                    var targetDefaults = Path.Combine(releaseDir, "Defaults");
                    CopyDirectory(defaultsDir, targetDefaults);
                }

                // Create ZIP
                var zipName = $"LatticeVeil_Release_{timestamp}.zip";
                var zipPath = Path.Combine(_zipsDir, zipName);

                Log($"Creating ZIP: {zipPath}");
                System.IO.Compression.ZipFile.CreateFromDirectory(releaseDir, zipPath);

                // Create manifest
                var manifest = new StringBuilder();
                manifest.AppendLine($"Build Timestamp: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
                manifest.AppendLine($"Git Commit: {GetGitCommitHash()}");
                manifest.AppendLine("Asset Mode: RELEASE");
                manifest.AppendLine("Includes: Launcher + Game");

                var manifestPath = Path.Combine(_zipsDir, $"manifest_{timestamp}.txt");
                File.WriteAllText(manifestPath, manifest.ToString());

                Log($"Release ZIP created: {zipPath}");
                Log($"Manifest created: {manifestPath}");
            }
            catch (Exception ex)
            {
                Log($"ERROR: {ex.Message}");
            }
            finally
            {
                SetButtonsEnabled(true);
            }
        }

        private void BtnCleanup_Click(object sender, EventArgs e)
        {
            SetButtonsEnabled(false);
            Log("Running cleanup...");

            try
            {
                // Clean build folders
                var dirsToClean = new[]
                {
                    Path.Combine(_gameProject, "bin"),
                    Path.Combine(_gameProject, "obj"),
                    Path.Combine(_repoRoot, ".vs"),
                    Path.Combine(_builderDir, "publish"),
                    Path.Combine(_builderDir, "launcher_publish"),
                    Path.Combine(_builderDir, "game_publish")
                };

                foreach (var dir in dirsToClean)
                {
                    if (Directory.Exists(dir))
                    {
                        Log($"Cleaning: {dir}");
                        Directory.Delete(dir, true);
                    }
                }

                // Clean old zips (keep last 10)
                var zips = Directory.GetFiles(_zipsDir, "*.zip")
                    .Select(f => new FileInfo(f))
                    .OrderByDescending(f => f.CreationTime)
                    .Skip(10);

                foreach (var zip in zips)
                {
                    Log($"Deleting old zip: {zip.Name}");
                    zip.Delete();
                }

                // Clean old logs (keep last 10)
                var logs = Directory.GetFiles(_logsDir, "*.log")
                    .Select(f => new FileInfo(f))
                    .OrderByDescending(f => f.CreationTime)
                    .Skip(10);

                foreach (var log in logs)
                {
                    Log($"Deleting old log: {log.Name}");
                    log.Delete();
                }

                Log("Cleanup complete");
            }
            catch (Exception ex)
            {
                Log($"ERROR: {ex.Message}");
            }
            finally
            {
                SetButtonsEnabled(true);
            }
        }

        private void BtnOpenLog_Click(object sender, EventArgs e)
        {
            var logFile = Path.Combine(_logsDir, "builder_latest.log");
            if (File.Exists(logFile))
            {
                Process.Start("notepad.exe", logFile);
            }
            else
            {
                MessageBox.Show("No log found yet.", "Builder", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
        }

        private async void BtnCheckUpdate_Click(object sender, EventArgs e)
        {
            SetButtonsEnabled(false);
            Log("Loading asset manifest...");

            try
            {
                var manifestUrl = "https://raw.githubusercontent.com/LatticeVeil/LatticeVeil/main/Assets/assets_manifest.json";
                var success = await _assetManager.LoadManifestAsync(manifestUrl);
                
                if (success)
                {
                    Log("Checking for asset updates...");
                    var progress = new Progress<string>(message => Log(message));
                    await _assetManager.CheckAndUpdateAssetsAsync(progress);
                    btnPlay.Enabled = true;
                    Log("Assets ready - PLAY button enabled");
                }
            }
            catch (Exception ex)
            {
                Log($"ERROR: {ex.Message}");
                MessageBox.Show($"Failed to check for updates: {ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                SetButtonsEnabled(true);
            }
        }

        private async void BtnRepairAssets_Click(object sender, EventArgs e)
        {
            SetButtonsEnabled(false);
            Log("Loading asset manifest for repair...");

            try
            {
                var manifestUrl = "https://raw.githubusercontent.com/LatticeVeil/LatticeVeil/main/Assets/assets_manifest.json";
                var success = await _assetManager.LoadManifestAsync(manifestUrl);
                
                if (success)
                {
                    Log("Repairing assets...");
                    var progress = new Progress<string>(message => Log(message));
                    await _assetManager.RepairAssetsAsync(progress);
                    btnPlay.Enabled = true;
                    Log("Assets repaired - PLAY button enabled");
                }
            }
            catch (Exception ex)
            {
                Log($"ERROR: {ex.Message}");
                MessageBox.Show($"Failed to repair assets: {ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                SetButtonsEnabled(true);
            }
        }

        private void BtnPlay_Click(object sender, EventArgs e)
        {
            try
            {
                Log("Starting game...");
                var gameExe = Path.Combine(_gameProject, "bin", "Release", "net8.0-windows", "LatticeVeilGame.exe");
                
                if (!File.Exists(gameExe))
                {
                    Log($"ERROR: Game executable not found at {gameExe}");
                    MessageBox.Show($"Game executable not found at:\n{gameExe}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return;
                }

                var gameProcess = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = gameExe,
                        WorkingDirectory = Path.GetDirectoryName(gameExe),
                        UseShellExecute = true
                    }
                };
                gameProcess.Start();

                Log("Game started");
            }
            catch (Exception ex)
            {
                Log($"ERROR: {ex.Message}");
                MessageBox.Show($"Failed to start game: {ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private string GetGitCommitHash()
        {
            try
            {
                var process = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = "git",
                        Arguments = "rev-parse --short HEAD",
                        WorkingDirectory = _repoRoot,
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        CreateNoWindow = true
                    }
                };

                process.Start();
                process.WaitForExit();
                return process.StandardOutput.ReadToEnd().Trim();
            }
            catch
            {
                return "N/A";
            }
        }

        private void CopyDirectory(string source, string destination)
        {
            foreach (var dir in Directory.GetDirectories(source))
            {
                var dirName = Path.GetFileName(dir);
                var destDir = Path.Combine(destination, dirName);
                Directory.CreateDirectory(destDir);
                CopyDirectory(dir, destDir);
            }

            foreach (var file in Directory.GetFiles(source))
            {
                var destFile = Path.Combine(destination, Path.GetFileName(file));
                File.Copy(file, destFile, true);
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _assetManager?.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
