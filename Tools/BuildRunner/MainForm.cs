using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace BuildRunner;

public sealed class MainForm : Form
{
    private readonly string _root;
    private readonly string _outDir;
    private readonly string _tempDir;
    private readonly string _projectPath;

    private readonly ProgressBar _progress;
    private readonly Label _stepLabel;
    private readonly TextBox _logBox;
    private readonly Button _copyLog;
    private readonly Button _openOutput;
    private readonly Button _close;

    private readonly object _logGate = new();
    private readonly List<string> _logLines = new();
    private bool _isBuilding;
    private int _phaseBase;
    private int _phaseWeight;
    private int _publishHits;
    private double _publishSmooth;
    private DateTime _publishStart;
    private System.Windows.Forms.Timer? _publishTimer;
    private string? _failureMessage;
    private string[] _failureListing = Array.Empty<string>();

    public MainForm(string root)
    {
        _root = Path.GetFullPath(root);
        _outDir = Path.Combine(_root, "Builds");
        _tempDir = Path.Combine(_outDir, "_temp_publish");
        _projectPath = Path.Combine(_root, "RedactedCraftMonoGame", "RedactedCraftMonoGame.csproj");

        Text = "Build Runner";
        Width = 720;
        Height = 520;
        MinimizeBox = true;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;

        _stepLabel = new Label
        {
            AutoSize = true,
            Text = "Idle",
            Dock = DockStyle.Top,
            Padding = new Padding(10, 10, 10, 6)
        };

        _progress = new ProgressBar
        {
            Minimum = 0,
            Maximum = 100,
            Dock = DockStyle.Top,
            Height = 18
        };

        _logBox = new TextBox
        {
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Vertical,
            Dock = DockStyle.Fill,
            Font = new System.Drawing.Font("Consolas", 9f),
            BackColor = System.Drawing.Color.White
        };

        _copyLog = new Button { Text = "Copy Log", Width = 110, Height = 28 };
        _openOutput = new Button { Text = "Open Output Folder", Width = 160, Height = 28 };
        _close = new Button { Text = "Close", Width = 90, Height = 28, Enabled = false };

        _copyLog.Click += (_, _) => CopyLog();
        _openOutput.Click += (_, _) => OpenOutputFolder();
        _close.Click += (_, _) => Close();

        var buttonPanel = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            Height = 44,
            FlowDirection = FlowDirection.RightToLeft,
            Padding = new Padding(10, 6, 10, 6)
        };
        buttonPanel.Controls.Add(_close);
        buttonPanel.Controls.Add(_openOutput);
        buttonPanel.Controls.Add(_copyLog);

        Controls.Add(_logBox);
        Controls.Add(buttonPanel);
        Controls.Add(_progress);
        Controls.Add(_stepLabel);

        Shown += async (_, _) =>
        {
            WindowState = FormWindowState.Normal;
            Activate();
            await StartBuildAsync();
        };

        FormClosing += (_, e) =>
        {
            if (_isBuilding)
                e.Cancel = true;
        };
    }

    private async Task StartBuildAsync()
    {
        _isBuilding = true;
        _close.Enabled = false;
        _failureMessage = null;
        _failureListing = Array.Empty<string>();
        SetProgress(0);
        LogLine($"Repo: {_root}");
        LogLine($"Project: {_projectPath}");

        if (!File.Exists(_projectPath))
        {
            FailBuild($"Main project not found: {_projectPath}");
            return;
        }

        try
        {
            SetPhase("Clean", 0, 10);
            await Task.Run(CleanAsync);
            SetProgress(_phaseBase + _phaseWeight);

            SetPhase("Restore", 10, 15);
            await RunProcessAsync("dotnet", $"restore \"{_projectPath}\"", CancellationToken.None);
            SetProgress(_phaseBase + _phaseWeight);

            SetPhase("Publish", 25, 60);
            _publishHits = 0;
            _publishSmooth = 0;
            _publishStart = DateTime.UtcNow;
            StartPublishTimer();
            // Added /v:n for detailed logging.
            await RunProcessAsync("dotnet",
                $"publish \"{_projectPath}\" -c Release -r win-x64 -o \"{_tempDir}\" /p:PublishSingleFile=true /p:SelfContained=true /p:IncludeNativeLibrariesForSelfExtract=true /p:IncludeAllContentForSelfExtract=true /p:DebugType=None /p:DebugSymbols=false /v:n",
                CancellationToken.None,
                OnPublishOutputLine);
            StopPublishTimer();
            SetProgress(_phaseBase + _phaseWeight);

            SetPhase("Package", 85, 10);
            await Task.Run(PackageAsync);
            SetProgress(_phaseBase + _phaseWeight);

            SetPhase("Cleanup", 95, 5);
            await Task.Run(CleanupAsync);
            SetProgress(100);

            LogLine("Build succeeded.");
            FinishSuccess();
        }
        catch (Exception ex)
        {
            FailBuild(ex.Message);
        }
    }

    private void CleanAsync()
    {
        KillGameProcesses();

        DeleteDirIfExists(Path.Combine(_root, "artifacts"));
        DeleteDirIfExists(Path.Combine(_root, "publish"));
        DeleteDirIfExists(Path.Combine(_root, "out"));
        DeleteBinObj(_root);

        // IMPORTANT: Do not wipe the entire Builds folder.
        // Users may keep other build artifacts in Builds that shouldn't be deleted.
        // Only remove the previous game EXE, and reset the temp publish folder.
        Directory.CreateDirectory(_outDir);
        TryDeleteFile(Path.Combine(_outDir, "RedactedCraft.exe"));

        DeleteDirIfExists(_tempDir);
        Directory.CreateDirectory(_tempDir);
    }

    private void KillGameProcesses()
    {
        try
        {
            var processes = Process.GetProcessesByName("RedactedCraft");
            foreach (var p in processes)
            {
                try
                {
                    LogLine($"Killing running game process: {p.ProcessName} (PID: {p.Id})");
                    p.Kill();
                    p.WaitForExit(3000); // Give it a moment to release locks
                }
                catch (Exception ex)
                {
                    LogLine($"Failed to kill process {p.Id}: {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            LogLine($"Error checking for running processes: {ex.Message}");
        }
    }

    private void PackageAsync()
    {
                    var dlls = Directory.Exists(_tempDir)
                        ? Directory.GetFiles(_tempDir, "*.dll", SearchOption.TopDirectoryOnly)
                            .Where(f => !IsAllowedNativeDll(Path.GetFileName(f))) // Added filter
                            .ToArray()
                        : Array.Empty<string>();
        if (dlls.Length > 0)
        {
            var names = dlls.Select(Path.GetFileName).ToArray();
            _failureMessage = names.Contains("SDL2.dll", StringComparer.OrdinalIgnoreCase)
                ? "Publish produced external native dependency SDL2.dll. Strict single-file output would be broken. Fix bundling or relax output rule."
                : $"Publish produced external native dependency dlls: {string.Join(", ", names)}. Strict single-file output would be broken. Fix bundling or relax output rule.";

            _failureListing = Directory.Exists(_tempDir)
                ? Directory.GetFileSystemEntries(_tempDir).Select(Path.GetFileName).Where(n => !string.IsNullOrWhiteSpace(n)).ToArray()
                : Array.Empty<string>();

            throw new InvalidOperationException("External DLL detected in publish output.");
        }

        var exe = Directory.Exists(_tempDir)
            ? Directory.GetFiles(_tempDir, "RedactedCraftMonoGame.exe", SearchOption.TopDirectoryOnly).FirstOrDefault()
            : null;

        exe ??= Directory.Exists(_tempDir)
            ? Directory.GetFiles(_tempDir, "*.exe", SearchOption.TopDirectoryOnly).FirstOrDefault()
            : null;

        if (string.IsNullOrWhiteSpace(exe))
            throw new FileNotFoundException("No EXE produced in temp publish folder.");

        var target = Path.Combine(_outDir, "RedactedCraft.exe");
        File.Copy(exe, target, true);
    }

    private void CleanupAsync()
    {
        DeleteDirIfExists(_tempDir);
        DeleteBinObj(_root);

        ScheduleSelfCleanup();
    }

    private void FailBuild(string message)
    {
        var msg = _failureMessage ?? message;
        var listing = _failureListing ?? Array.Empty<string>();
        WriteFailureLog(msg, listing);
        LogLine($"FAILED: {message}");
        MessageBox.Show("Build failed. Copy log for support.", "Build Failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
        FinishFailure();
    }

    private void FinishSuccess()
    {
        _isBuilding = false;
        _close.Enabled = true;
        _stepLabel.Text = "Done";
        Environment.ExitCode = 0;
        BeginInvoke(new Action(Close));
    }

    private void FinishFailure()
    {
        _isBuilding = false;
        _close.Enabled = true;
        _stepLabel.Text = "Failed";
        Environment.ExitCode = 1;
        ScheduleSelfCleanup();
    }

    private void SetPhase(string name, int basePercent, int weight)
    {
        _phaseBase = basePercent;
        _phaseWeight = weight;
        UpdateStepLabel($"{name}");
    }

    private void SetProgress(int value)
    {
        var clamped = Math.Max(0, Math.Min(100, value));
        if (InvokeRequired)
        {
            BeginInvoke(new Action<int>(SetProgress), clamped);
            return;
        }
        if (_progress.Value != clamped)
            _progress.Value = clamped;
    }

    private void UpdateStepLabel(string text)
    {
        if (InvokeRequired)
        {
            BeginInvoke(new Action<string>(UpdateStepLabel), text);
            return;
        }
        _stepLabel.Text = $"Step: {text}";
    }

    private void LogLine(string line)
    {
        lock (_logGate)
        {
            _logLines.Add(line);
        }
        if (InvokeRequired)
        {
            BeginInvoke(new Action<string>(AppendLogLine), line);
            return;
        }

        AppendLogLine(line);
    }

    private void AppendLogLine(string line)
    {
        _logBox.AppendText(line + Environment.NewLine);
        _logBox.SelectionStart = _logBox.TextLength;
        _logBox.ScrollToCaret();
    }

    private void CopyLog()
    {
        try
        {
            Clipboard.SetText(_logBox.Text);
        }
        catch
        {
            // Best-effort.
        }
    }

    private void OpenOutputFolder()
    {
        try
        {
            Directory.CreateDirectory(_outDir);
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"\"{_outDir}\"",
                UseShellExecute = true
            });
        }
        catch
        {
            // Best-effort.
        }
    }

    private void WriteFailureLog(string message, string[] listing)
    {
        try
        {
            if (Directory.Exists(_tempDir))
                DeleteDirIfExists(_tempDir);

            DeleteBinObj(_root);
            DeleteDirIfExists(Path.Combine(_root, "artifacts"));
            DeleteDirIfExists(Path.Combine(_root, "publish"));
            DeleteDirIfExists(Path.Combine(_root, "out"));

            // Do not delete the entire Builds folder on failure; preserve other artifacts.
            Directory.CreateDirectory(_outDir);
            var logPath = Path.Combine(_outDir, "Build.log"); // Renamed from build_failed.log to Build.log

            var sb = new StringBuilder();
            if (!string.IsNullOrWhiteSpace(message))
                sb.AppendLine(message);

            lock (_logGate)
            {
                if (_logLines.Count > 0)
                    sb.AppendLine(string.Join(Environment.NewLine, _logLines));
            }

            if (listing.Length > 0)
            {
                sb.AppendLine("Temp publish folder contents:");
                sb.AppendLine(string.Join(Environment.NewLine, listing));
            }

            File.WriteAllText(logPath, sb.ToString());
        }
        catch
        {
            // Best-effort.
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (!File.Exists(path))
                return;

            var attrs = File.GetAttributes(path);
            if ((attrs & FileAttributes.ReadOnly) != 0)
                File.SetAttributes(path, attrs & ~FileAttributes.ReadOnly);

            File.Delete(path);
        }
        catch
        {
            // Best-effort.
        }
    }

    private async Task RunProcessAsync(string fileName, string args, CancellationToken token, Action<string>? onLine = null)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = args,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        var tcs = new TaskCompletionSource<int>();

        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data == null)
                return;
            LogLine(e.Data);
            onLine?.Invoke(e.Data);
        };

        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data == null)
                return;
            LogLine(e.Data);
            onLine?.Invoke(e.Data);
        };

        process.Exited += (_, _) => tcs.TrySetResult(process.ExitCode);

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        var exitCode = await tcs.Task.ConfigureAwait(false);
        if (exitCode != 0)
            throw new InvalidOperationException($"{fileName} {args} failed with exit code {exitCode}");
    }

    private void OnPublishOutputLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
            return;

        if (line.Contains("Compile", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("Copy", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("Generate", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("Publish", StringComparison.OrdinalIgnoreCase))
        {
            _publishHits++;
        }

        var hitRatio = Math.Min(0.95, _publishHits / 50.0);
        var elapsed = (DateTime.UtcNow - _publishStart).TotalSeconds;
        var timeRatio = Math.Min(0.4, elapsed / 30.0);
        var ratio = Math.Max(hitRatio, timeRatio);
        _publishSmooth = Math.Max(_publishSmooth, ratio);

        var value = _phaseBase + (int)Math.Round(_phaseWeight * _publishSmooth);
        SetProgress(value);
    }

    private void StartPublishTimer()
    {
        _publishTimer?.Stop();
        _publishTimer = new System.Windows.Forms.Timer { Interval = 500 };
        _publishTimer.Tick += (_, _) =>
        {
            _publishSmooth = Math.Min(0.95, _publishSmooth + 0.01);
            var value = _phaseBase + (int)Math.Round(_phaseWeight * _publishSmooth);
            SetProgress(value);
        };
        _publishTimer.Start();
    }

    private void StopPublishTimer()
    {
        if (_publishTimer == null)
            return;
        _publishTimer.Stop();
        _publishTimer.Dispose();
        _publishTimer = null;
    }

    private static void DeleteDirIfExists(string path)
    {
        if (Directory.Exists(path))
            Directory.Delete(path, true);
    }

    private static void DeleteDirOrFile(string path)
    {
        if (Directory.Exists(path))
            Directory.Delete(path, true);
        else if (File.Exists(path))
            File.Delete(path);
    }

    private void DeleteBinObj(string root)
    {
        var dirs = Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories)
            .Where(d => !d.Contains(Path.DirectorySeparatorChar + ".git" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            .Where(d =>
            {
                var name = Path.GetFileName(d);
                return string.Equals(name, "bin", StringComparison.OrdinalIgnoreCase) ||
                       string.Equals(name, "obj", StringComparison.OrdinalIgnoreCase);
            })
            .ToList();

        foreach (var dir in dirs)
        {
            try
            {
                Directory.Delete(dir, true);
            }
            catch
            {
                // Best-effort during running exe; try again at the end.
            }
        }
    }

    private void ScheduleSelfCleanup()
    {
        try
        {
            var binDir = Path.Combine(_root, "Tools", "BuildRunner", "bin");
            var objDir = Path.Combine(_root, "Tools", "BuildRunner", "obj");
            Process.Start(new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c ping -n 2 127.0.0.1 >nul & rmdir /s /q \"{binDir}\" & rmdir /s /q \"{objDir}\"",
                CreateNoWindow = true,
                UseShellExecute = false
            });
        }
        catch
        {
            // Best-effort cleanup.
        }
    }

    private static bool IsAllowedNativeDll(string dllName)
    {
        return dllName.Equals("SDL2.dll", StringComparison.OrdinalIgnoreCase) ||
               dllName.Equals("soft_oal.dll", StringComparison.OrdinalIgnoreCase) ||
               dllName.Equals("FAudio.dll", StringComparison.OrdinalIgnoreCase) ||
               dllName.Equals("libEGL.dll", StringComparison.OrdinalIgnoreCase) ||
               dllName.Equals("libGLESv2.dll", StringComparison.OrdinalIgnoreCase) ||
               dllName.Equals("MonoGame.Framework.dll", StringComparison.OrdinalIgnoreCase) || // Main MonoGame DLL
               dllName.Equals("SharpDX.dll", StringComparison.OrdinalIgnoreCase) || // Direct X backend
               dllName.Equals("SharpDX.Direct2D1.dll", StringComparison.OrdinalIgnoreCase) ||
               dllName.Equals("SharpDX.Direct3D11.dll", StringComparison.OrdinalIgnoreCase) ||
               dllName.Equals("SharpDX.DXGI.dll", StringComparison.OrdinalIgnoreCase) ||
               dllName.Equals("SharpDX.MediaFoundation.dll", StringComparison.OrdinalIgnoreCase) ||
               dllName.Equals("SharpDX.XAudio2.dll", StringComparison.OrdinalIgnoreCase) ||
               dllName.Equals("SharpDX.XInput.dll", StringComparison.OrdinalIgnoreCase);
    }
}
