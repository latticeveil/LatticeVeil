using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows.Forms;
using LatticeVeilMonoGame.Core;
using LatticeVeilMonoGame.Launcher;
using LatticeVeilMonoGame.Crash;

namespace LatticeVeilMonoGame;

public static class Program
{
    /// <summary>
    /// Single-exe model (EOS-only build):
     /// - No args: start WinForms launcher window (recommended).
     /// - --game (or any --host/--join args): start MonoGame window.
     /// - --launcher: force WinForms launcher window.
     ///
     /// All core social features are EOS-only (no FriendsHub required).
     /// </summary>
    [STAThread]
    public static void Main(string[] args)
    {
        var logPath = GetArgValue(args, "--log");
        var truncateLogs = string.IsNullOrWhiteSpace(logPath) && !IsGameRunning();
        var log = new Logger(logFilePath: logPath, truncateOnStart: truncateLogs);
        var profile = PlayerProfile.LoadOrCreate(log);

        EnsureRequiredFolders(log);

        // Copy default assets from the app directory to Documents\LatticeVeil\Assets if missing.
        AssetInstaller.EnsureDefaultsInstalled(AppDomain.CurrentDomain.BaseDirectory, log);

        var smoke = HasArg(args, "--smoke");
        var smokeAssetsOk = true;
        string[] smokeMissing = Array.Empty<string>();
        if (smoke)
        {
            var assetInstaller = new AssetPackInstaller(log);
            smokeAssetsOk = assetInstaller.CheckLocalAssetsInstalled(out smokeMissing);
            if (smokeAssetsOk)
                log.Info("SMOKE assets check: OK.");
            else
                log.Warn($"SMOKE assets check: missing {string.Join(", ", smokeMissing)}");
        }

        var assetView = HasArg(args, "--assetview");
        var renderer = GetArgValue(args, "--renderer") ?? "OpenGL"; // Default to OpenGL
        var startOptions = new GameStartOptions
        {
            JoinToken = GetArgValue(args, "--join-token"),
            Offline = HasArg(args, "--offline"),
            Smoke = smoke,
            SmokeAssetsOk = smokeAssetsOk,
            SmokeMissingAssets = smokeMissing,
            AssetView = assetView,
            RendererBackend = renderer
        };

        var forceLauncher = HasArg(args, "--launcher");
        var runGame = !forceLauncher && IsGameEntry(args);

        if (runGame)
        {
            using var gameMutex = new Mutex(true, AppMutexes.GameMutexName, out var gameCreatedNew);
            if (!gameCreatedNew)
            {
                MessageBox.Show(
                    "Game is already running.",
                    "LatticeVeil Game",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
                return;
            }

            try
            {
                try
                {
                    File.WriteAllText(Paths.GamePidPath, Environment.ProcessId.ToString());
                }
                catch (Exception ex)
                {
                    log.Warn($"Failed to write game pid file: {ex.Message}");
                }

                log.Info("Starting GAME mode.");
                using var game = new Game1(log, profile, startOptions);
                game.Run();
                log.Info("Game exited.");
            }
            catch (Exception ex)
            {
                log.Fatal(ex, "Game Crashed (Main Loop)");
                // Show the crash report form
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new CrashReportForm(ex, log.LogFilePath));
            }
            finally
            {
                try
                {
                    if (File.Exists(Paths.GamePidPath))
                        File.Delete(Paths.GamePidPath);
                }
                catch (Exception ex)
                {
                    log.Warn($"Failed to delete game pid file: {ex.Message}");
                }

                try { gameMutex.ReleaseMutex(); }
                catch { }
            }
            return;
        }

        using var mutex = new Mutex(true, AppMutexes.LauncherMutexName, out var createdNew);
        if (!createdNew)
        {
            MessageBox.Show(
                "Launcher is already running.",
                Paths.IsDevBuild ? "[DEV] Lattice Launcher" : "Lattice Launcher",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            return;
        }

        log.Info("Starting LAUNCHER mode.");
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.Run(new LauncherForm(log, profile));
    }

    private static bool IsGameEntry(string[] args)
    {
        if (HasArg(args, "--smoke")
            || HasArg(args, "--assetview")
            || HasArg(args, "--game")
            || HasArg(args, "--dedicated")
            || HasArg(args, "--host-lan")
            || HasArg(args, "--join-lan")
            || HasArg(args, "--lan")
            || HasArg(args, "--renderer"))
            return true;

        return args.Any(a => a.StartsWith("--host", StringComparison.OrdinalIgnoreCase)
            || a.StartsWith("--join", StringComparison.OrdinalIgnoreCase));
    }

    private static string? GetArgValue(string[] args, string name)
    {
        for (var i = 0; i < args.Length; i++)
        {
            if (!string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
                continue;

            if (i + 1 < args.Length)
                return args[i + 1];
        }
        return null;
    }

    private static bool HasArg(string[] args, string name)
    {
        return args.Any(a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase));
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

    private static void EnsureRequiredFolders(Logger log)
    {
        try
        {
            Directory.CreateDirectory(Paths.RootDir);
            Directory.CreateDirectory(Paths.LogsDir);
            Directory.CreateDirectory(Paths.ScreenshotsDir);
            Directory.CreateDirectory(Paths.WorldsDir);
            Directory.CreateDirectory(Paths.MultiplayerWorldsDir);
            Directory.CreateDirectory(Paths.ConfigDir);

            Paths.EnsureAssetDirectoriesExist(log);
            Directory.CreateDirectory(Path.Combine(Paths.AssetsDir, "packs"));
        }
        catch (Exception ex)
        {
            log.Warn($"Failed to create required directories: {ex.Message}");
        }
    }
}
