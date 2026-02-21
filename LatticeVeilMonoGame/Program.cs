using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows.Forms;
using LatticeVeilMonoGame.Core;
using LatticeVeilMonoGame.Launcher;
using LatticeVeilMonoGame.Online.Eos;
using LatticeVeilMonoGame.Online.Gate;
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
        Logger log;
        try
        {
             log = new Logger(logFilePath: logPath, truncateOnStart: truncateLogs);
        }
        catch (Exception ex)
        {
            LogExceptionToEmergencyFile(ex, "Logger initialization failed");
            return;
        }

        PlayerProfile profile;
        try
        {
            profile = PlayerProfile.LoadOrCreate(log);
        }
        catch (Exception ex)
        {
            log.Fatal(ex, "PlayerProfile.LoadOrCreate failed");
            LogExceptionToEmergencyFile(ex, "PlayerProfile initialization failed");
            return;
        }

        // Early debug output to console AND file
        Console.WriteLine($"=== LatticeVeilMonoGame Starting ===");
        Console.WriteLine($"Log path: {logPath}");
        Console.WriteLine($"Args: {string.Join(" ", args)}");
        Console.WriteLine($"Working directory: {AppDomain.CurrentDomain.BaseDirectory}");
        Console.WriteLine($"EOS DLL Present: {EosRuntimeStatus.IsEosLibraryPresent()}");

        log.Info($"Log path: {logPath}");
        log.Info($"Args: {string.Join(" ", args)}");
        log.Info($"Working directory: {AppDomain.CurrentDomain.BaseDirectory}");
        log.Info($"EOS DLL Present: {EosRuntimeStatus.IsEosLibraryPresent()}");

        EnsureRequiredFolders(log);

        // Copy default assets from the app directory to Documents\LatticeVeil\Assets if missing.
        AssetInstaller.EnsureDefaultsInstalled(AppDomain.CurrentDomain.BaseDirectory, log);
        Paths.RemoveDisallowedAssetEntries(log);

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
        var requestedOffline = HasArg(args, "--offline");
        var buildSha = GetArgValue(args, "--build-sha");

        var forceLauncher = HasArg(args, "--launcher");
        var runGame = !forceLauncher && IsGameEntry(args);
        var startupLinkCode = LauncherProtocolLinking.TryExtractLinkCodeFromArgs(args);
        var hasProtocolArg = args.Any(a => !string.IsNullOrWhiteSpace(a) && a.IndexOf("latticeveil://", StringComparison.OrdinalIgnoreCase) >= 0);
        if (!string.IsNullOrWhiteSpace(startupLinkCode))
            log.Info("Launcher deep-link code detected from protocol URL.");
        else if (hasProtocolArg)
            log.Warn("Protocol URL detected but link code parsing failed.");

        if (runGame)
        {
            Environment.SetEnvironmentVariable("LV_PROCESS_KIND", "game");
            Environment.SetEnvironmentVariable("LV_BUILD_CHANNEL", Paths.IsDevBuild ? "dev" : "release");
            var strictStartupGate = ParseBool(Environment.GetEnvironmentVariable("LV_STRICT_STARTUP_GATE"));

            var requireLauncherHandshake = ShouldRequireLauncherHandshake();
            var onlineAuthorizedByLauncher = !requireLauncherHandshake;
            var effectiveOffline = requestedOffline;
            if (!effectiveOffline)
            {
                var launcherPipe = GetArgValue(args, "--launcher-pipe");
                var launcherToken = GetArgValue(args, "--launcher-token");

                if (requireLauncherHandshake)
                {
                    onlineAuthorizedByLauncher = OnlineLaunchHandshakeGuard.ValidateForGameStart(launcherPipe, launcherToken, log);
                    if (!onlineAuthorizedByLauncher)
                    {
                        effectiveOffline = true;
                        log.Warn("Online disabled: this session was not started by the launcher.");
                    }
                }
                else
                {
                    onlineAuthorizedByLauncher = true;
                    log.Info("Launcher handshake enforcement disabled for this build.");
                }
            }

            Environment.SetEnvironmentVariable(
                "LV_LAUNCHER_ONLINE_AUTH",
                (!effectiveOffline && onlineAuthorizedByLauncher) ? "1" : "0");
            Environment.SetEnvironmentVariable("LV_LAUNCH_MODE", effectiveOffline ? "offline" : "online");

            if (!effectiveOffline)
            {
                var veilnetToken = (Environment.GetEnvironmentVariable("LV_VEILNET_ACCESS_TOKEN") ?? string.Empty).Trim();
                var gateTicket = (Environment.GetEnvironmentVariable("LV_GATE_TICKET") ?? string.Empty).Trim();
                var hasVeilnetToken = IsUsableToken(veilnetToken);
                var hasGateTicket = !string.IsNullOrWhiteSpace(gateTicket);
                if (!hasVeilnetToken || !hasGateTicket)
                {
                    effectiveOffline = true;
                    Environment.SetEnvironmentVariable("LV_LAUNCHER_ONLINE_AUTH", "0");
                    Environment.SetEnvironmentVariable("LV_LAUNCH_MODE", "offline");
                    MessageBox.Show(
                        "Online auth context is missing. This run will start in Offline mode.\n\nUse the launcher Online flow to enable EOS multiplayer.",
                        "Online Unavailable",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Information);
                    log.Warn(
                        $"Online startup context missing; forcing offline mode. " +
                        $"hasVeilnetToken={hasVeilnetToken}; hasGateTicket={hasGateTicket}");
                }
            }

            if (!effectiveOffline)
            {
                Environment.SetEnvironmentVariable("EOS_DISABLED", null);
                var gate = OnlineGateClient.GetOrCreate();
                var hashTargetPath = (Environment.GetEnvironmentVariable("LV_GATE_HASH_TARGET") ?? string.Empty).Trim();
                if (string.IsNullOrWhiteSpace(hashTargetPath))
                {
                    hashTargetPath = ResolveCurrentGameExecutablePathForGate() ?? string.Empty;
                    if (!string.IsNullOrWhiteSpace(hashTargetPath))
                        Environment.SetEnvironmentVariable("LV_GATE_HASH_TARGET", hashTargetPath);
                }

                if (!string.IsNullOrWhiteSpace(hashTargetPath))
                    log.Info($"Game startup gate hash target: {hashTargetPath}");

                var precheckOk = gate.EnsureTicket(
                    log,
                    TimeSpan.FromSeconds(20),
                    string.IsNullOrWhiteSpace(hashTargetPath) ? null : hashTargetPath);
                if (precheckOk)
                {
                    log.Info("Game startup gate precheck: verified.");
                    if (!EosConfig.HasPublicConfigSource() || !EosConfig.HasSecretSource())
                    {
                        if (EosRemoteConfigBootstrap.TryBootstrap(log, gate, allowRetry: true))
                            log.Info("Game startup EOS config bootstrap: ready.");
                    }
                }
                else
                {
                    log.Warn($"Game startup gate precheck failed: {gate.DenialReason}");
                    if (gate.IsGateRequired && strictStartupGate)
                    {
                        effectiveOffline = true;
                        Environment.SetEnvironmentVariable("LV_LAUNCHER_ONLINE_AUTH", "0");
                        log.Warn("Switching this run to OFFLINE/LAN because gate precheck failed.");
                    }
                    else if (gate.IsGateRequired)
                    {
                        log.Warn("Startup gate precheck failed, but continuing in ONLINE-capable mode. Gate will be checked again when hosting/joining.");
                    }
                }
            }
            else
            {
                Environment.SetEnvironmentVariable("LV_GATE_TICKET", null);
                Environment.SetEnvironmentVariable("LV_GATE_TICKET_EXPIRES_UTC", null);
                Environment.SetEnvironmentVariable("EOS_DISABLED", "1");
            }

            var startOptions = new GameStartOptions
            {
                JoinToken = GetArgValue(args, "--join-token"),
                Offline = effectiveOffline,
                Smoke = smoke,
                SmokeAssetsOk = smokeAssetsOk,
                SmokeMissingAssets = smokeMissing,
                AssetView = assetView,
                RendererBackend = renderer,
                BuildSha = buildSha
            };

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
            
                // Log build SHA if provided by launcher
                if (!string.IsNullOrEmpty(buildSha))
                {
                    log.Info($"Build SHA from launcher: {buildSha}");
                    Environment.SetEnvironmentVariable("LV_BUILD_SHA", buildSha);
                }
                else
                {
                    log.Info("No build SHA provided by launcher");
                }
            
                try
                {
                    log.Info("Creating Game1 instance...");
                    using var game = new Game1(log, profile, startOptions);
                    log.Info("Game1 instance created, calling Run()...");
                    game.Run();
                    log.Info("Game exited normally.");
                }
                catch (Exception gameEx)
                {
                    log.Fatal(gameEx, "Game failed to start or crashed during Run()");
                    throw;
                }
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

        Environment.SetEnvironmentVariable("LV_PROCESS_KIND", "launcher");
        Environment.SetEnvironmentVariable("LV_LAUNCHER_ONLINE_AUTH", "1");

        log.Info("Launcher: checking mutex...");
        using var mutex = new Mutex(true, AppMutexes.LauncherMutexName, out var createdNew);
        if (!createdNew)
        {
            if (!string.IsNullOrWhiteSpace(startupLinkCode))
            {
                if (LauncherProtocolLinking.TryQueueLinkCodeForRunningLauncher(startupLinkCode, log))
                {
                    MessageBox.Show(
                        "Link code sent to the running launcher instance.",
                        Paths.IsDevBuild ? "[DEV] Lattice Launcher" : "Lattice Launcher",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Information);
                    return;
                }
            }

            log.Warn("Launcher: mutex already held. Another instance is likely running.");
            MessageBox.Show(
                "Launcher is already running.",
                Paths.IsDevBuild ? "[DEV] Lattice Launcher" : "Lattice Launcher",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            return;
        }

        log.Info("Starting LAUNCHER mode.");
        try
        {
            LauncherProtocolLinking.TryEnsureProtocolRegistration(log);
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new LauncherForm(log, profile, startupLinkCode));
            log.Info("Launcher: Application.Run finished.");
        }
        catch (Exception ex)
        {
            log.Fatal(ex, "Launcher Crashed (Main Loop)");
            // Show the crash report form
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new CrashReportForm(ex, log.LogFilePath));
        }
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
            || HasArg(args, "--online")
            || HasArg(args, "--offline")
            || HasArg(args, "--renderer")
            || HasArg(args, "--launcher-pipe")
            || HasArg(args, "--launcher-token")
            || HasArg(args, "--render-distance")
            || HasArg(args, "--build-sha"))
            return true;

        return args.Any(a => a.StartsWith("--host", StringComparison.OrdinalIgnoreCase)
            || a.StartsWith("--join", StringComparison.OrdinalIgnoreCase)
            || a.StartsWith("--renderer=", StringComparison.OrdinalIgnoreCase)
            || a.StartsWith("--launcher-pipe=", StringComparison.OrdinalIgnoreCase)
            || a.StartsWith("--launcher-token=", StringComparison.OrdinalIgnoreCase)
            || a.StartsWith("--render-distance=", StringComparison.OrdinalIgnoreCase)
            || a.StartsWith("--build-sha=", StringComparison.OrdinalIgnoreCase));
    }

        private static string? GetArgValue(string[] args, string name)
    {
        if (args == null || args.Length == 0 || string.IsNullOrWhiteSpace(name))
            return null;

        for (var i = 0; i < args.Length; i++)
        {
            var a = args[i];
            if (string.Equals(a, name, StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 < args.Length)
                    return args[i + 1];
                return null;
            }

            if (a != null && a.StartsWith(name + "=", StringComparison.OrdinalIgnoreCase))
                return a.Substring(name.Length + 1);
        }

        return null;
    }

    private static bool HasArg(string[] args, string name)
    {
        if (args == null || args.Length == 0 || string.IsNullOrWhiteSpace(name))
            return false;

        return args.Any(a =>
            string.Equals(a, name, StringComparison.OrdinalIgnoreCase)
            || (a != null && a.StartsWith(name + "=", StringComparison.OrdinalIgnoreCase)));
    }

    private static bool ShouldRequireLauncherHandshake()
    {
        var configured = Environment.GetEnvironmentVariable("LV_REQUIRE_LAUNCHER_HANDSHAKE");
        if (!string.IsNullOrWhiteSpace(configured))
            return ParseBool(configured);

        // Keep Debug/Release behavior consistent by default.
        // Set LV_REQUIRE_LAUNCHER_HANDSHAKE=1 to force strict launcher-only starts.
        return false;
    }

    private static bool ParseBool(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;

        var normalized = value.Trim();
        return normalized == "1"
            || normalized.Equals("true", StringComparison.OrdinalIgnoreCase)
            || normalized.Equals("yes", StringComparison.OrdinalIgnoreCase)
            || normalized.Equals("on", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsUsableToken(string? token)
    {
        if (string.IsNullOrWhiteSpace(token))
            return false;

        return !string.Equals(token, "null", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(token, "undefined", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(token, "placeholder", StringComparison.OrdinalIgnoreCase);
    }

    private static string? ResolveCurrentGameExecutablePathForGate()
    {
        try
        {
            var processPath = (Environment.ProcessPath ?? string.Empty).Trim();
            if (!string.IsNullOrWhiteSpace(processPath)
                && !IsDotNetHostPath(processPath)
                && File.Exists(processPath))
            {
                return processPath;
            }
        }
        catch
        {
            // Best-effort only.
        }

        try
        {
            var assemblyName = typeof(Program).Assembly.GetName().Name ?? "LatticeVeilMonoGame";
            var candidates = new[]
            {
                Path.Combine(AppContext.BaseDirectory, "LatticeVeilMonoGame.exe"),
                Path.Combine(AppContext.BaseDirectory, assemblyName + ".exe")
            };

            for (var i = 0; i < candidates.Length; i++)
            {
                var candidate = candidates[i];
                if (File.Exists(candidate))
                    return candidate;
            }
        }
        catch
        {
            // Best-effort only.
        }

        return null;
    }

    private static bool IsDotNetHostPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return false;

        var fileName = Path.GetFileName(path);
        return fileName.Equals("dotnet", StringComparison.OrdinalIgnoreCase)
            || fileName.Equals("dotnet.exe", StringComparison.OrdinalIgnoreCase);
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
            DeleteLegacyMultiplayerWorldCaches(log);
            DeleteLegacyConfigFolder(log);

            Paths.EnsureAssetDirectoriesExist(log);
            Paths.RemoveDisallowedAssetEntries(log);
        }
        catch (Exception ex)
        {
            log.Warn($"Failed to create required directories: {ex.Message}");
        }
    }

    private static void DeleteLegacyMultiplayerWorldCaches(Logger log)
    {
        try
        {
            var legacyFolders = new[]
            {
                Path.Combine(Paths.RootDir, "Worlds_Multiplayer"),
                Path.Combine(Paths.WorldsDir, "_OnlineCache")
            };

            for (var i = 0; i < legacyFolders.Length; i++)
            {
                var legacy = legacyFolders[i];
                if (!Directory.Exists(legacy))
                    continue;

                Directory.Delete(legacy, recursive: true);
                log.Info($"Deleted legacy multiplayer cache folder: {legacy}");
            }
        }
        catch (Exception ex)
        {
            log.Warn($"Failed to delete legacy multiplayer cache folder: {ex.Message}");
        }
    }

    private static void DeleteLegacyConfigFolder(Logger log)
    {
        try
        {
            var legacyConfig = Path.Combine(Paths.RootDir, "Config");
            if (!Directory.Exists(legacyConfig))
                return;

            foreach (var file in Directory.GetFiles(legacyConfig, "*", SearchOption.TopDirectoryOnly))
            {
                var target = Path.Combine(Paths.RootDir, Path.GetFileName(file));
                if (File.Exists(target))
                    continue;

                File.Move(file, target);
                log.Info($"Migrated Config file to root: {Path.GetFileName(file)}");
            }

            Directory.Delete(legacyConfig, recursive: true);
            log.Info("Deleted legacy Config folder.");
        }
        catch (Exception ex)
        {
            log.Warn($"Failed to delete legacy Config folder: {ex.Message}");
        }
    }

    private static void LogExceptionToEmergencyFile(Exception ex, string context)
    {
        try
        {
            var path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "emergency_crash.lverror");
            File.AppendAllText(path, $"[{DateTime.Now}] {context}: {ex}\n");
        }
        catch { }
    }
}
