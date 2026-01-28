using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace LatticeVeilMonoGame.Core;

public sealed class Logger
{
    private static int _globalInstalled;
    private static Logger? _global;

    private readonly object _gate = new();
    private readonly Queue<string> _tail = new();
    private readonly int _tailMaxLines;

    public string LogFilePath { get; }

    public Logger(string? logFilePath = null, int tailMaxLines = 200, bool truncateOnStart = false)
    {
        _tailMaxLines = Math.Max(50, tailMaxLines);

        try
        {
            Directory.CreateDirectory(Paths.RootDir);
            Directory.CreateDirectory(Paths.LogsDir);
        }
        catch { /* Ignore if we can't create dirs yet, might be permission issues handled later */ }

        LogFilePath = ResolveLogFilePath(logFilePath);
        if (truncateOnStart)
            TryTruncate(LogFilePath);

        InstallGlobalHandlers();

        Info("Logger initialized.");
        Info($"AssetsDir: {Paths.AssetsDir}");
        Debug("Debug logging enabled.");
    }

    [Conditional("DEBUG")]
    public void Debug(string message,
        [CallerMemberName] string member = "",
        [CallerFilePath] string file = "",
        [CallerLineNumber] int line = 0)
        => Write("DEBUG", message, member, file, line);

    public void Info(string message,
        [CallerMemberName] string member = "",
        [CallerFilePath] string file = "",
        [CallerLineNumber] int line = 0)
        => Write("INFO", message, member, file, line);

    public void Warn(string message,
        [CallerMemberName] string member = "",
        [CallerFilePath] string file = "",
        [CallerLineNumber] int line = 0)
        => Write("WARN", message, member, file, line);

    public void Error(string message,
        [CallerMemberName] string member = "",
        [CallerFilePath] string file = "",
        [CallerLineNumber] int line = 0)
        => Write("ERROR", message, member, file, line);

    public void Error(Exception ex, string message,
        [CallerMemberName] string member = "",
        [CallerFilePath] string file = "",
        [CallerLineNumber] int line = 0)
        => Write("ERROR", $"{message} | Exception: {ex}", member, file, line);

    public void Fatal(string message,
        [CallerMemberName] string member = "",
        [CallerFilePath] string file = "",
        [CallerLineNumber] int line = 0)
        => Write("FATAL", message, member, file, line);

    public void Fatal(Exception ex, string message,
        [CallerMemberName] string member = "",
        [CallerFilePath] string file = "",
        [CallerLineNumber] int line = 0)
        => Write("FATAL", $"{message} | Exception: {ex}", member, file, line);

    public void OpenLogsFolder()
    {
        try
        {
            Directory.CreateDirectory(Paths.LogsDir);

            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                // Quote the path so spaces are handled correctly.
                Arguments = $"\"{Paths.LogsDir}\"",
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            Error($"Failed to open logs folder: {ex.Message}");
        }
    }

    public string GetTailText()
    {
        lock (_gate)
        {
            var sb = new StringBuilder();
            foreach (var line in _tail)
                sb.AppendLine(line);
            return sb.ToString();
        }
    }

    /// <summary>
    /// Installs global handlers so unhandled exceptions, Console output, and Trace/Debug output are captured.
    /// Call is idempotent.
    /// </summary>
    public void InstallGlobalHandlers()
    {
        _global = this;
        if (Interlocked.Exchange(ref _globalInstalled, 1) == 1)
            return;

        try
        {
            AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            {
                try
                {
                    var ex = e.ExceptionObject as Exception;
                    (_global ?? this).Fatal(ex != null
                        ? ex
                        : new Exception($"Non-Exception object: {e.ExceptionObject}"),
                        "Unhandled exception");
                }
                catch { }
            };

            TaskScheduler.UnobservedTaskException += (_, e) =>
            {
                try
                {
                    e.SetObserved();
                    (_global ?? this).Error(e.Exception, "Unobserved task exception");
                }
                catch { }
            };

            // Capture tracing output.
            Trace.AutoFlush = true;
            Trace.Listeners.Add(new LoggerTraceListener());

            // Capture Console output.
            Console.SetOut(new LoggerTextWriter("INFO"));
            Console.SetError(new LoggerTextWriter("ERROR"));
        }
        catch
        {
            // Best-effort; logging should never crash the app.
        }
    }

    private void Write(string level, string message, string memberName, string sourceFilePath, int sourceLineNumber)
    {
        var threadId = Environment.CurrentManagedThreadId;
        var fileName = string.IsNullOrEmpty(sourceFilePath) ? "?" : Path.GetFileName(sourceFilePath);
        var location = string.IsNullOrEmpty(memberName) ? "" : $" [{fileName}:{sourceLineNumber}]";
        
        // Format: [Time] [Level] [Thread] [Location] Message
        var line = $"{DateTime.Now:HH:mm:ss.fff} [{level}] [T:{threadId}]{location} {message}";

        lock (_gate)
        {
            _tail.Enqueue(line);
            while (_tail.Count > _tailMaxLines)
                _tail.Dequeue();

            try
            {
                // We open/close specifically to ensure data is flushed and to allow other processes to read the file (FileShare.ReadWrite).
                using var stream = new FileStream(LogFilePath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
                using var writer = new StreamWriter(stream);
                writer.WriteLine(line);
            }
            catch
            {
                // If file write fails, we still keep the in-memory tail.
                // Could verify if disk is full or permissions changed.
            }
        }
    }

    private static string ResolveLogFilePath(string? logFilePath)
    {
        var defaultPath = Paths.ActiveLogPath;

        if (string.IsNullOrWhiteSpace(logFilePath))
            return defaultPath;

        try
        {
            var full = Path.GetFullPath(logFilePath);
            var logsRoot = Path.GetFullPath(Paths.LogsDir) + Path.DirectorySeparatorChar;
            if (full.StartsWith(logsRoot, StringComparison.OrdinalIgnoreCase))
                return full;
        }
        catch
        {
            // Fall back to default if validation fails.
        }

        return defaultPath;
    }

    private static void TryTruncate(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite);
        }
        catch
        {
            // Best-effort; logging should never crash the app.
        }
    }

    public bool TrySaveSnapshot(out string? savedPath, out string? error)
    {
        savedPath = null;
        error = null;

        try
        {
            Directory.CreateDirectory(Paths.LogsDir);

            if (!File.Exists(LogFilePath))
            {
                error = "No log file is available to save yet.";
                return false;
            }

            var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            var basePath = Path.Combine(Paths.LogsDir, $"log-{stamp}.txt");
            var target = GetUniqueSnapshotPath(basePath);

            using var src = new FileStream(LogFilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var dst = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
            src.CopyTo(dst);

            savedPath = target;
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private static string GetUniqueSnapshotPath(string basePath)
    {
        if (!File.Exists(basePath))
            return basePath;

        var dir = Path.GetDirectoryName(basePath) ?? Paths.LogsDir;
        var name = Path.GetFileNameWithoutExtension(basePath);
        var ext = Path.GetExtension(basePath);

        for (var i = 1; i <= 100; i++)
        {
            var candidate = Path.Combine(dir, $"{name}-{i}{ext}");
            if (!File.Exists(candidate))
                return candidate;
        }

        return Path.Combine(dir, $"{name}-{Guid.NewGuid():N}{ext}");
    }

    private sealed class LoggerTraceListener : TraceListener
    {
        public override void Write(string? message)
        {
            if (string.IsNullOrEmpty(message))
                return;
            // Trace listeners don't pass caller info easily, so we leave it empty.
            _global?.Info(message);
        }

        public override void WriteLine(string? message)
        {
            if (string.IsNullOrEmpty(message))
                return;
            _global?.Info(message);
        }
    }

    private sealed class LoggerTextWriter : TextWriter
    {
        private readonly string _level;
        public LoggerTextWriter(string level) => _level = level;
        public override Encoding Encoding => Encoding.UTF8;

        public override void WriteLine(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return;
            if (_level == "ERROR") _global?.Error(value);
            else _global?.Info(value);
        }

        public override void Write(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return;
            if (_level == "ERROR") _global?.Error(value.TrimEnd());
            else _global?.Info(value.TrimEnd());
        }
    }
}
