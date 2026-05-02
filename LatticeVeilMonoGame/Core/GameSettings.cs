using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Management;
using Microsoft.Xna.Framework.Audio;
using Microsoft.Xna.Framework.Input;
using LatticeVeilMonoGame.UI;

namespace LatticeVeilMonoGame.Core;

public enum SocialNotificationMode
{
    Off,
    MessageOnly,
    On
}

public sealed class GameSettings
{
    private const int EnumCurrentSettings = -1;
    private const uint EnumDisplaySettingsRawMode = 0x00000002;
    public const int RenderDistanceMin = 4;
    public const int EngineRenderDistanceMax = 10;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct DEVMODE
    {
        private const int CchDeviceName = 32;
        private const int CchFormName = 32;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = CchDeviceName)]
        public string dmDeviceName;
        public short dmSpecVersion;
        public short dmDriverVersion;
        public short dmSize;
        public short dmDriverExtra;
        public int dmFields;
        public int dmPositionX;
        public int dmPositionY;
        public int dmDisplayOrientation;
        public int dmDisplayFixedOutput;
        public short dmColor;
        public short dmDuplex;
        public short dmYResolution;
        public short dmTTOption;
        public short dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = CchFormName)]
        public string dmFormName;
        public short dmLogPixels;
        public int dmBitsPerPel;
        public int dmPelsWidth;
        public int dmPelsHeight;
        public int dmDisplayFlags;
        public int dmDisplayFrequency;
        public int dmICMMethod;
        public int dmICMIntent;
        public int dmMediaType;
        public int dmDitherType;
        public int dmReserved1;
        public int dmReserved2;
        public int dmPanningWidth;
        public int dmPanningHeight;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern bool EnumDisplaySettings(string? lpszDeviceName, int iModeNum, ref DEVMODE lpDevMode);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern bool EnumDisplaySettingsEx(string? lpszDeviceName, int iModeNum, ref DEVMODE lpDevMode, uint dwFlags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool AdjustWindowRectEx(ref RECT lpRect, int dwStyle, bool bMenu, int dwExStyle);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr GetAncestor(IntPtr hWnd, uint gaFlags);

    [DllImport("SDL2.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern void SDL_SetWindowSize(IntPtr window, int w, int h);

    [DllImport("SDL2.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern void SDL_SetWindowPosition(IntPtr window, int x, int y);

    private const int GwlStyle = -16;
    private const int GwlExStyle = -20;
    private const uint GaRoot = 2;

    // Video
    public bool Fullscreen { get; set; } = false;
    public bool VSync { get; set; } = true;
    public int ResolutionWidth { get; set; } = 1280;
    public int ResolutionHeight { get; set; } = 720;
    public string PreferredDisplayDeviceName { get; set; } = "";
    public float GuiScale { get; set; } = 1.5f;
    public string QualityPreset { get; set; } = "MEDIUM";
    public float Brightness { get; set; } = 1f;
    public int FieldOfView { get; set; } = 70;
    public int RenderDistanceChunks { get; set; } = EngineRenderDistanceMax;
    public string ParticlePreset { get; set; } = "AUTO";
    public bool PerformanceDefaultsApplied { get; set; } = false;
    public bool ShowSeedInfoInWorldList { get; set; } = false;
    public int CreateWorldHomesCap { get; set; } = 10;
    public bool EnableInviteLinks { get; set; } = false;
    public string SocialNotifications { get; set; } = nameof(SocialNotificationMode.On);
    public bool IndicatorsEnabled { get; set; } = true;
    public bool SoulDeathMarkerGuidanceEnabled { get; set; } = true;
    public string NametagMode { get; set; } = "Fade";
    public float NametagFadeSeconds { get; set; } = 3f;
    public string UiFontStyle { get; set; } = PixelFont.ClassicStyle;

    // Performance / Derived-data cache
    // Default OFF: meshes are derived and can always be rebuilt; keeping this off avoids stale cache + slow exits.
    public bool PersistMeshCache { get; set; } = false;


    // Launcher
    public bool KeepLauncherOpen { get; set; } = false;
    public bool AlwaysMinimizeLauncherToTray { get; set; } = false;
    public string LauncherCloseButtonAction { get; set; } = ""; // "", "Tray", or "Close"
    public bool DarkMode { get; set; } = true;
    public string RendererBackend { get; set; } = "OpenGL"; // "OpenGL" or "Vulkan"
    public int LauncherRenderDistance { get; set; } = 16; // Launcher-specific setting
    public bool AdvancedMode { get; set; } = false; // Allow override of safe caps
    public string OfficialBuildHashFilePath { get; set; } = ""; // Optional override for official hash verification target file
    public string IgnoredGameReleaseTitle { get; set; } = "";

    // Audio
    public float MasterVolume { get; set; } = 1f;
    public float MusicVolume { get; set; } = 1f;
    public float SfxVolume { get; set; } = 1f;
    public string AudioInputDeviceId { get; set; } = "";
    public string AudioOutputDeviceId { get; set; } = "";
    public string VoiceOutputDeviceId { get; set; } = "";
    public string GameOutputDeviceId { get; set; } = "";
    public bool MultistreamAudio { get; set; } = false;

    // Controls
    public float MouseSensitivity { get; set; } = 0.0035f;
    public bool ReticleEnabled { get; set; } = true;
    public bool ToggleCrouchEnabled { get; set; } = false;
    public bool SprintLatchEnabled { get; set; } = true;
    public string ReticleStyle { get; set; } = "Dot";
    public int ReticleSize { get; set; } = 8;
    public int ReticleThickness { get; set; } = 2;
    public string ReticleColor { get; set; } = "FFFFFFC8";
    public string BlockOutlineColor { get; set; } = DefaultBlockOutlineColor;
    public bool FlyingOutlineEnabled { get; set; } = true;
    public string FlyingOutlineColor { get; set; } = DefaultFlyingOutlineColor;
    public Dictionary<string, Keys> Keybinds { get; set; } = new()
    {
        ["MoveUp"] = Keys.W,
        ["MoveDown"] = Keys.S,
        ["MoveLeft"] = Keys.A,
        ["MoveRight"] = Keys.D,
        ["Jump"] = Keys.Space,
        ["Crouch"] = Keys.LeftShift,
        ["Sprint"] = Keys.LeftControl,
        ["FlyDescend"] = Keys.LeftShift,
        ["Inventory"] = Keys.E,
        ["DropItem"] = Keys.Q,
        ["GiveItem"] = Keys.F,
        ["Pause"] = Keys.Escape,
        ["Chat"] = Keys.T,
        ["Command"] = Keys.OemQuestion,
        ["HomeGui"] = Keys.H,
        ["StructureFinder"] = Keys.B,
        ["GamemodeModifier"] = Keys.LeftAlt,
        ["GamemodeWheel"] = Keys.G,
        ["VeilseerXrayToggle"] = Keys.X,
        ["InviteQuickAction"] = Keys.Y
    };
    public Dictionary<string, string> MouseBinds { get; set; } = new();

    // Packs
    public List<string> EnabledPacks { get; set; } = new();

    public static GameSettings LoadOrCreate(Logger log)
    {
        try
        {
            Directory.CreateDirectory(Paths.RootDir);

            if (!File.Exists(Paths.SettingsJsonPath))
            {
                if (File.Exists(Paths.LegacySettingsLvcPath) || File.Exists(Paths.LegacySettingsJsonPath) || LvcSerializer.IsJsonFormat(Paths.SettingsJsonPath))
                    throw new LvcSerializer.LegacyFormatException($"Legacy settings format detected. Delete/replace: {Paths.ToUiPath(Paths.SettingsJsonPath)}");
            }

            if (!File.Exists(Paths.SettingsJsonPath))
            {
                var s = new GameSettings();
                s.ApplyAutoPerformanceDefaults(log);
                s.Save(log);
                return s;
            }

            var data = LvcSerializer.Read(Paths.SettingsJsonPath);
            var loaded = new GameSettings();
            LvcSerializer.ApplyObject(loaded, data);
            Sanitize(loaded);
            if (!loaded.PerformanceDefaultsApplied)
            {
                loaded.ApplyAutoPerformanceDefaults(log);
                loaded.Save(log);
            }
            return loaded;
}
        catch (Exception ex)
        {
            log.Warn($"Failed to load settings: {ex.Message}");
            return new GameSettings();
        }
    }

    private static void Sanitize(GameSettings s)
    {
        if (s.ResolutionWidth < 640) s.ResolutionWidth = 640;
        if (s.ResolutionHeight < 480) s.ResolutionHeight = 480;

        s.MasterVolume = Clamp01(s.MasterVolume);
        s.MusicVolume = Clamp01(s.MusicVolume);
        s.SfxVolume = Clamp01(s.SfxVolume);
        s.GuiScale = NormalizeGuiScale(s.GuiScale);
        s.Brightness = ClampRange(s.Brightness, 0.5f, 1.5f);
        s.FieldOfView = Math.Clamp(s.FieldOfView, 60, 110);
        s.RenderDistanceChunks = Math.Clamp(s.RenderDistanceChunks, RenderDistanceMin, EngineRenderDistanceMax);
        s.CreateWorldHomesCap = Math.Clamp(s.CreateWorldHomesCap, 1, 64);
        s.ParticlePreset = NormalizeParticlePreset(s.ParticlePreset);
        s.SocialNotifications = NormalizeSocialNotifications(s.SocialNotifications);
        s.NametagMode = NormalizeNametagMode(s.NametagMode);
        s.NametagFadeSeconds = ClampRange(s.NametagFadeSeconds, 0.5f, 12f);
        s.UiFontStyle = NormalizeUiFontStyle(s.UiFontStyle);
        s.SoulDeathMarkerGuidanceEnabled = s.SoulDeathMarkerGuidanceEnabled;
        s.MouseSensitivity = ClampRange(s.MouseSensitivity, 0.0005f, 0.01f);
        s.QualityPreset = NormalizeQuality(s.QualityPreset);
        s.OfficialBuildHashFilePath = (s.OfficialBuildHashFilePath ?? string.Empty).Trim();
        s.IgnoredGameReleaseTitle = (s.IgnoredGameReleaseTitle ?? string.Empty).Trim();
        s.AudioInputDeviceId ??= "";

        s.AudioOutputDeviceId ??= "";
        s.VoiceOutputDeviceId ??= "";
        s.GameOutputDeviceId ??= "";
        s.ReticleStyle = NormalizeReticleStyle(s.ReticleStyle);
        s.ReticleSize = Math.Clamp(s.ReticleSize, ReticleSizeMin, ReticleSizeMax);
        s.ReticleThickness = Math.Clamp(s.ReticleThickness, ReticleThicknessMin, ReticleThicknessMax);
        s.ReticleColor = NormalizeHexColor(s.ReticleColor, DefaultReticleColor);
        s.BlockOutlineColor = NormalizeHexColor(s.BlockOutlineColor, DefaultBlockOutlineColor);
        s.FlyingOutlineColor = NormalizeHexColor(s.FlyingOutlineColor, DefaultFlyingOutlineColor);

        EnsureKeybinds(s);
        s.MouseBinds ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        s.EnabledPacks ??= new List<string>();
    }

    private static float Clamp01(float v) => v < 0f ? 0f : (v > 1f ? 1f : v);
    private static float ClampRange(float v, float min, float max) => v < min ? min : (v > max ? max : v);
    private static float NormalizeGuiScale(float value)
    {
        // Migrate legacy 0.75x-1.0x values into the new 1x-2x user-facing range.
        if (value < 1.0f)
        {
            var legacy = ClampRange(value, 0.75f, 1.0f);
            return 1.0f + ((legacy - 0.75f) / 0.25f);
        }

        return ClampRange(value, 1.0f, 2.0f);
    }
    private const int ReticleSizeMin = 2;
    private const int ReticleSizeMax = 32;
    private const int ReticleThicknessMin = 1;
    private const int ReticleThicknessMax = 6;
    private const string DefaultReticleColor = "FFFFFFC8";
    private const string DefaultBlockOutlineColor = "C8DCE678";
    private const string DefaultFlyingOutlineColor = "FF8A8AC8";

    private static string NormalizeQuality(string? value)
    {
        var quality = string.IsNullOrWhiteSpace(value) ? "MEDIUM" : value.Trim().ToUpperInvariant();
        return quality is "LOW" or "MEDIUM" or "HIGH" or "ULTRA" ? quality : "MEDIUM";
    }

    public static string NormalizeUiFontStyle(string? value) => PixelFont.NormalizeStyle(value);

    private static string NormalizeParticlePreset(string? value)
    {
        var preset = string.IsNullOrWhiteSpace(value) ? "AUTO" : value.Trim().ToUpperInvariant();
        return preset is "AUTO" or "OFF" or "LOW" or "MEDIUM" or "HIGH" or "ULTRA" ? preset : "AUTO";
    }

    private static string NormalizeSocialNotifications(string? value)
    {
        var mode = (value ?? string.Empty).Trim();
        if (mode.Equals("off", StringComparison.OrdinalIgnoreCase))
            return nameof(SocialNotificationMode.Off);
        if (mode.Equals("messageonly", StringComparison.OrdinalIgnoreCase)
            || mode.Equals("message_only", StringComparison.OrdinalIgnoreCase)
            || mode.Equals("message only", StringComparison.OrdinalIgnoreCase))
        {
            return nameof(SocialNotificationMode.MessageOnly);
        }

        return nameof(SocialNotificationMode.On);
    }

    private static string NormalizeNametagMode(string? value)
    {
        var mode = (value ?? string.Empty).Trim();
        if (mode.Equals("off", StringComparison.OrdinalIgnoreCase))
            return "Off";
        if (mode.Equals("fade", StringComparison.OrdinalIgnoreCase))
            return "Fade";
        return "Fade";
    }

    public SocialNotificationMode GetSocialNotificationMode()
    {
        if (SocialNotifications.Equals(nameof(SocialNotificationMode.Off), StringComparison.OrdinalIgnoreCase))
            return SocialNotificationMode.Off;
        if (SocialNotifications.Equals(nameof(SocialNotificationMode.MessageOnly), StringComparison.OrdinalIgnoreCase))
            return SocialNotificationMode.MessageOnly;
        return SocialNotificationMode.On;
    }

    public void SetSocialNotificationMode(SocialNotificationMode mode)
    {
        SocialNotifications = mode switch
        {
            SocialNotificationMode.Off => nameof(SocialNotificationMode.Off),
            SocialNotificationMode.MessageOnly => nameof(SocialNotificationMode.MessageOnly),
            _ => nameof(SocialNotificationMode.On)
        };
    }

    private static string NormalizeReticleStyle(string? value)
    {
        var style = string.IsNullOrWhiteSpace(value) ? "DOT" : value.Trim().ToUpperInvariant();
        return style switch
        {
            "DOT" => "Dot",
            "PLUS" => "Plus",
            "SQUARE" => "Square",
            "CIRCLE" => "Circle",
            _ => "Dot"
        };
    }

    private static string NormalizeHexColor(string? value, string fallback)
    {
        if (string.IsNullOrWhiteSpace(value))
            return fallback;

        var text = value.Trim();
        if (text.StartsWith("#", StringComparison.Ordinal))
            text = text.Substring(1);

        if (text.Length == 6)
            text += "FF";

        if (text.Length != 8)
            return fallback;

        for (int i = 0; i < text.Length; i++)
        {
            var c = text[i];
            var isHex = (c >= '0' && c <= '9')
                || (c >= 'a' && c <= 'f')
                || (c >= 'A' && c <= 'F');
            if (!isHex)
                return fallback;
        }

        return text.ToUpperInvariant();
    }

    private static void EnsureKeybinds(GameSettings s)
    {
        if (s.Keybinds is null || s.Keybinds.Count == 0)
        {
            s.Keybinds = new GameSettings().Keybinds;
            return;
        }

        var defaults = new GameSettings().Keybinds;
        foreach (var pair in defaults)
        {
            if (!s.Keybinds.ContainsKey(pair.Key))
                s.Keybinds[pair.Key] = pair.Value;
        }
    }

    public void Save(Logger log)
    {
        try
        {
            Directory.CreateDirectory(Paths.RootDir);
            var data = LvcSerializer.SerializeObject(this);
            LvcSerializer.Write(Paths.SettingsJsonPath, data);
}
        catch (Exception ex)
        {
            log.Warn($"Failed to save settings: {ex.Message}");
        }
    }

    private static void TryMigrateLegacySettingsFile(Logger log)
    {
        throw new LvcSerializer.LegacyFormatException("Legacy settings format detected (migration disabled).");
    }

    public void ApplyGraphics(global::Microsoft.Xna.Framework.GraphicsDeviceManager graphics)
    {
        var adapter = global::Microsoft.Xna.Framework.Graphics.GraphicsAdapter.DefaultAdapter;
        var currentMode = adapter.CurrentDisplayMode;
        var deviceWindowHandle = graphics.GraphicsDevice?.PresentationParameters.DeviceWindowHandle ?? IntPtr.Zero;
        var useSdlWindow = DisplayMonitorLocator.IsSdlWindowHandle(deviceWindowHandle);
        var windowHandle = useSdlWindow ? deviceWindowHandle : ResolveTopLevelWindowHandle(deviceWindowHandle);
        var monitor = windowHandle != IntPtr.Zero
            ? DisplayMonitorLocator.GetForWindow(windowHandle)
            : ResolvePreferredMonitor();
        PreferredDisplayDeviceName = monitor.DeviceName;
        var deviceName = monitor.DeviceName;
        var desktop = GetDesktopResolution(deviceName, currentMode.Width, currentMode.Height);
        var windowTargetBounds = monitor.IsPrimary && monitor.Bounds.Width <= 0
            ? new Rectangle(0, 0, desktop.w, desktop.h)
            : monitor.Bounds;
        var workArea = monitor.WorkingArea.Width > 0 && monitor.WorkingArea.Height > 0
            ? monitor.WorkingArea
            : windowTargetBounds;

        var width = Math.Min(ResolutionWidth, Math.Max(640, workArea.Width));
        var height = Math.Min(ResolutionHeight, Math.Max(480, workArea.Height));
        if (width < 640) width = 640;
        if (height < 480) height = 480;

        if (Fullscreen)
        {
            width = Math.Max(640, desktop.w);
            height = Math.Max(480, desktop.h);
        }

        graphics.HardwareModeSwitch = false;
        graphics.IsFullScreen = Fullscreen;
        graphics.SynchronizeWithVerticalRetrace = VSync;
        graphics.PreferredBackBufferWidth = width;
        graphics.PreferredBackBufferHeight = height;

        if (Fullscreen && windowHandle != IntPtr.Zero)
        {
            MoveWindowToMonitorOrigin(windowHandle, monitor, useSdlWindow);
        }

        graphics.ApplyChanges();

        if (Fullscreen)
        {
            MoveWindowToMonitorOrigin(windowHandle, monitor, useSdlWindow);
            return;
        }

        PinWindowToMonitor(windowHandle, monitor, width, height, useSdlWindow);
    }

    public void CaptureActiveGraphics(global::Microsoft.Xna.Framework.GraphicsDeviceManager graphics)
    {
        try
        {
            Fullscreen = graphics.IsFullScreen;
            VSync = graphics.SynchronizeWithVerticalRetrace;

            var deviceWindowHandle = graphics.GraphicsDevice?.PresentationParameters.DeviceWindowHandle ?? IntPtr.Zero;
            var useSdlWindow = DisplayMonitorLocator.IsSdlWindowHandle(deviceWindowHandle);
            var windowHandle = useSdlWindow ? deviceWindowHandle : ResolveTopLevelWindowHandle(deviceWindowHandle);
            var monitor = DisplayMonitorLocator.GetForWindow(windowHandle);
            PreferredDisplayDeviceName = monitor.DeviceName;
        }
        catch
        {
            // Best-effort only.
        }
    }

    public void StageStartupGraphics(global::Microsoft.Xna.Framework.GraphicsDeviceManager graphics, global::Microsoft.Xna.Framework.GameWindow window)
    {
        var adapter = global::Microsoft.Xna.Framework.Graphics.GraphicsAdapter.DefaultAdapter;
        var currentMode = adapter.CurrentDisplayMode;
        var monitor = ResolvePreferredMonitor();
        PreferredDisplayDeviceName = monitor.DeviceName;
        var desktop = GetDesktopResolution(monitor.DeviceName, currentMode.Width, currentMode.Height);
        var windowTargetBounds = monitor.IsPrimary && monitor.Bounds.Width <= 0
            ? new Rectangle(0, 0, desktop.w, desktop.h)
            : monitor.Bounds;
        var workArea = monitor.WorkingArea.Width > 0 && monitor.WorkingArea.Height > 0
            ? monitor.WorkingArea
            : windowTargetBounds;

        var width = Math.Min(ResolutionWidth, Math.Max(640, workArea.Width));
        var height = Math.Min(ResolutionHeight, Math.Max(480, workArea.Height));
        if (width < 640) width = 640;
        if (height < 480) height = 480;

        var startupPosition = workArea.Location;
        if (Fullscreen)
        {
            width = Math.Max(640, desktop.w);
            height = Math.Max(480, desktop.h);
            startupPosition = windowTargetBounds.Location;
        }
        else
        {
            var centered = CenterWithin(workArea, width, height);
            startupPosition = centered.Location;
        }

        graphics.HardwareModeSwitch = false;
        graphics.IsFullScreen = Fullscreen;
        graphics.SynchronizeWithVerticalRetrace = VSync;
        graphics.PreferredBackBufferWidth = width;
        graphics.PreferredBackBufferHeight = height;

        try
        {
            window.Position = new Point(startupPosition.X, startupPosition.Y);
        }
        catch
        {
            // Best-effort only; ApplyGraphics will position again once the window exists.
        }
    }

    public void ApplyAutoPerformanceDefaults(Logger log)
    {
        try
        {
            var recommendation = RecommendPerformanceProfile();
            RenderDistanceChunks = recommendation.RenderDistanceChunks;
            QualityPreset = recommendation.QualityPreset;
            ParticlePreset = recommendation.ParticlePreset;
            PerformanceDefaultsApplied = true;
            log.Info(
                $"Auto performance defaults applied: renderDistance={RenderDistanceChunks}, quality={QualityPreset}, particles={ParticlePreset}, tier={recommendation.TierLabel}");
        }
        catch (Exception ex)
        {
            RenderDistanceChunks = 8;
            QualityPreset = "MEDIUM";
            ParticlePreset = "MEDIUM";
            PerformanceDefaultsApplied = true;
            log.Warn($"Auto performance defaults failed; using fallback defaults: {ex.Message}");
        }
    }

    private static (int RenderDistanceChunks, string QualityPreset, string ParticlePreset, string TierLabel) RecommendPerformanceProfile()
    {
        var score = 0;

        var cpuThreads = Environment.ProcessorCount;
        if (cpuThreads >= 12) score += 2;
        else if (cpuThreads >= 8) score += 1;
        else if (cpuThreads <= 4) score -= 1;

        var ramGb = TryGetPhysicalMemoryGb();
        if (ramGb >= 24) score += 2;
        else if (ramGb >= 16) score += 1;
        else if (ramGb > 0 && ramGb <= 8) score -= 1;

        var gpu = (global::Microsoft.Xna.Framework.Graphics.GraphicsAdapter.DefaultAdapter.Description ?? string.Empty).Trim().ToLowerInvariant();
        if (!string.IsNullOrWhiteSpace(gpu))
        {
            if (gpu.Contains("rtx 4090") || gpu.Contains("rtx 4080") || gpu.Contains("rtx 4070")
                || gpu.Contains("rx 7900") || gpu.Contains("rx 7800") || gpu.Contains("rx 7700"))
            {
                score += 3;
            }
            else if (gpu.Contains("rtx 30") || gpu.Contains("rtx 20") || gpu.Contains("rx 6")
                || gpu.Contains("rx 5") || gpu.Contains("gtx 1080") || gpu.Contains("gtx 1070")
                || gpu.Contains("gtx 1660") || gpu.Contains("arc a") || gpu.Contains("arc b"))
            {
                score += 2;
            }
            else if (gpu.Contains("gtx") || gpu.Contains("rx") || gpu.Contains("vega") || gpu.Contains("iris xe"))
            {
                score += 1;
            }
            else if (gpu.Contains("uhd") || gpu.Contains("hd graphics") || gpu.Contains("intel(r) graphics")
                || gpu.Contains("microsoft basic"))
            {
                score -= 2;
            }
        }

        if (score >= 6)
            return (EngineRenderDistanceMax, "ULTRA", "ULTRA", "ultra");
        if (score >= 4)
            return (10, "HIGH", "HIGH", "high");
        if (score >= 1)
            return (8, "MEDIUM", "MEDIUM", "medium");
        return (6, "LOW", "LOW", "low");
    }

    private static int TryGetPhysicalMemoryGb()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT TotalPhysicalMemory FROM Win32_ComputerSystem");
            foreach (var obj in searcher.Get())
            {
                if (obj["TotalPhysicalMemory"] == null)
                    continue;

                if (ulong.TryParse(obj["TotalPhysicalMemory"].ToString(), out var bytes))
                    return (int)Math.Clamp((long)(bytes / (1024UL * 1024UL * 1024UL)), 0L, 512L);
            }
        }
        catch
        {
            // Fall back below.
        }

        return 0;
    }

    private static (int w, int h) GetDesktopResolution(string? deviceName, int fallbackW, int fallbackH)
    {
        if (DisplayMonitorLocator.TryGetByDeviceName(deviceName, out var monitor))
        {
            var bounds = monitor.Bounds;
            if (bounds.Width > 0 && bounds.Height > 0)
                return (bounds.Width, bounds.Height);
        }

        try
        {
            if (!string.IsNullOrWhiteSpace(deviceName))
            {
                foreach (var screen in System.Windows.Forms.Screen.AllScreens)
                {
                    if (!string.Equals(screen.DeviceName, deviceName, StringComparison.OrdinalIgnoreCase))
                        continue;

                    var bounds = screen.Bounds;
                    if (bounds.Width > 0 && bounds.Height > 0)
                        return (bounds.Width, bounds.Height);
                }
            }
        }
        catch
        {
            // Fall through to mode enumeration.
        }

        try
        {
            var mode = CreateDevMode();
            if (TryEnumDisplaySettings(deviceName, EnumCurrentSettings, ref mode))
                return (mode.dmPelsWidth, mode.dmPelsHeight);

            if (!string.IsNullOrWhiteSpace(deviceName))
            {
                mode = CreateDevMode();
                if (TryEnumDisplaySettings(null, EnumCurrentSettings, ref mode))
                    return (mode.dmPelsWidth, mode.dmPelsHeight);
            }
        }
        catch
        {
            // Fall back to screen bounds.
        }

        try
        {
            var primary = System.Windows.Forms.Screen.PrimaryScreen;
            if (primary != null)
                return (primary.Bounds.Width, primary.Bounds.Height);
        }
        catch
        {
            // Fall back to adapter mode size.
        }

        return (fallbackW, fallbackH);
    }

    private static (int w, int h) GetMaxSupportedResolution(string? deviceName, int fallbackW, int fallbackH)
    {
        var bestW = fallbackW;
        var bestH = fallbackH;
        var bestPixels = (long)bestW * bestH;

        TryUpdateMaxSupportedResolution(deviceName, ref bestW, ref bestH, ref bestPixels);
        if (!string.IsNullOrWhiteSpace(deviceName))
            TryUpdateMaxSupportedResolution(null, ref bestW, ref bestH, ref bestPixels);

        return (bestW, bestH);
    }

    private static void TryUpdateMaxSupportedResolution(string? deviceName, ref int bestW, ref int bestH, ref long bestPixels)
    {
        try
        {
            for (var modeNum = 0; ; modeNum++)
            {
                var mode = CreateDevMode();
                if (!TryEnumDisplaySettings(deviceName, modeNum, ref mode))
                    break;

                var pixels = (long)mode.dmPelsWidth * mode.dmPelsHeight;
                if (pixels <= bestPixels)
                    continue;

                bestPixels = pixels;
                bestW = mode.dmPelsWidth;
                bestH = mode.dmPelsHeight;
            }
        }
        catch
        {
            // If adapter modes are unavailable, keep fallback.
        }
    }

    private static DEVMODE CreateDevMode()
    {
        var mode = new DEVMODE
        {
            dmDeviceName = string.Empty,
            dmFormName = string.Empty,
            dmSize = (short)Marshal.SizeOf<DEVMODE>()
        };
        return mode;
    }

    private static bool TryEnumDisplaySettings(string? deviceName, int modeNum, ref DEVMODE mode)
    {
        if (EnumDisplaySettingsEx(deviceName, modeNum, ref mode, EnumDisplaySettingsRawMode))
            return true;

        return EnumDisplaySettings(deviceName, modeNum, ref mode);
    }

    private static void PinWindowToMonitor(IntPtr windowHandle, DisplayMonitorInfo monitor, int width, int height, bool useSdlWindow)
    {
        if (windowHandle == IntPtr.Zero)
            return;

        if (useSdlWindow)
        {
            var workArea = monitor.WorkingArea.Width > 0 && monitor.WorkingArea.Height > 0 ? monitor.WorkingArea : monitor.Bounds;
            var sdlTargetBounds = CenterWithin(workArea, width, height);
            try
            {
                SDL_SetWindowSize(windowHandle, Math.Max(1, sdlTargetBounds.Width), Math.Max(1, sdlTargetBounds.Height));
                SDL_SetWindowPosition(windowHandle, sdlTargetBounds.X, sdlTargetBounds.Y);
                return;
            }
            catch
            {
                // Fall back to Win32 path below if SDL calls fail.
            }
        }

        var targetBounds = CenterWithin(
            monitor.WorkingArea.Width > 0 && monitor.WorkingArea.Height > 0 ? monitor.WorkingArea : monitor.Bounds,
            GetWindowBoundsForClientSize(windowHandle, width, height).Width,
            GetWindowBoundsForClientSize(windowHandle, width, height).Height);

        if (targetBounds.Width <= 0 || targetBounds.Height <= 0)
            return;

        const uint swpNoZOrder = 0x0004;
        const uint swpNoActivate = 0x0010;
        const uint swpShowWindow = 0x0040;
        var adjusted = GetWindowBoundsForClientSize(windowHandle, width, height);
        targetBounds = new Microsoft.Xna.Framework.Rectangle(targetBounds.X, targetBounds.Y, adjusted.Width, adjusted.Height);

        _ = SetWindowPos(windowHandle, IntPtr.Zero, targetBounds.X, targetBounds.Y, targetBounds.Width, targetBounds.Height, swpNoZOrder | swpNoActivate | swpShowWindow);
    }

    private static void MoveWindowToMonitorOrigin(IntPtr windowHandle, DisplayMonitorInfo monitor, bool useSdlWindow)
    {
        if (windowHandle == IntPtr.Zero)
            return;

        var bounds = monitor.Bounds.Width > 0 && monitor.Bounds.Height > 0
            ? monitor.Bounds
            : monitor.WorkingArea;
        if (bounds.Width <= 0 || bounds.Height <= 0)
            return;

        if (useSdlWindow)
        {
            try
            {
                SDL_SetWindowPosition(windowHandle, bounds.X, bounds.Y);
                return;
            }
            catch
            {
                // Fall back to Win32 path below if SDL calls fail.
            }
        }

        const uint swpNoSize = 0x0001;
        const uint swpNoZOrder = 0x0004;
        const uint swpNoActivate = 0x0010;
        const uint swpShowWindow = 0x0040;
        _ = SetWindowPos(windowHandle, IntPtr.Zero, bounds.X, bounds.Y, 0, 0, swpNoSize | swpNoZOrder | swpNoActivate | swpShowWindow);
    }

    private static Microsoft.Xna.Framework.Rectangle CenterWithin(Microsoft.Xna.Framework.Rectangle outer, int width, int height)
    {
        var clampedWidth = outer.Width > 0 ? Math.Min(width, outer.Width) : width;
        var clampedHeight = outer.Height > 0 ? Math.Min(height, outer.Height) : height;

        return new Microsoft.Xna.Framework.Rectangle(
            outer.X + Math.Max(0, (outer.Width - clampedWidth) / 2),
            outer.Y + Math.Max(0, (outer.Height - clampedHeight) / 2),
            clampedWidth,
            clampedHeight);
    }

    private static Microsoft.Xna.Framework.Rectangle GetWindowBoundsForClientSize(IntPtr windowHandle, int clientWidth, int clientHeight)
    {
        var rect = new RECT
        {
            Left = 0,
            Top = 0,
            Right = Math.Max(1, clientWidth),
            Bottom = Math.Max(1, clientHeight)
        };

        try
        {
            var style = GetWindowLong(windowHandle, GwlStyle);
            var exStyle = GetWindowLong(windowHandle, GwlExStyle);
            if (AdjustWindowRectEx(ref rect, style, false, exStyle))
            {
                return new Microsoft.Xna.Framework.Rectangle(
                    0,
                    0,
                    Math.Max(1, rect.Right - rect.Left),
                    Math.Max(1, rect.Bottom - rect.Top));
            }
        }
        catch
        {
            // Fall back to client size.
        }

        return new Microsoft.Xna.Framework.Rectangle(0, 0, Math.Max(1, clientWidth), Math.Max(1, clientHeight));
    }

    private DisplayMonitorInfo ResolvePreferredMonitor()
    {
        if (DisplayMonitorLocator.TryGetByDeviceName(PreferredDisplayDeviceName, out var preferredMonitor))
            return preferredMonitor;

        return DisplayMonitorLocator.GetPrimary();
    }

    private static IntPtr ResolveTopLevelWindowHandle(IntPtr windowHandle)
    {
        if (windowHandle == IntPtr.Zero)
            return IntPtr.Zero;

        try
        {
            var rootHandle = GetAncestor(windowHandle, GaRoot);
            if (rootHandle != IntPtr.Zero)
                return rootHandle;
        }
        catch
        {
            // Fall back to the provided handle.
        }

        return windowHandle;
    }

    public void ApplyAudio()
    {
        SoundEffect.MasterVolume = Clamp01(MasterVolume);
        // MusicVolume / SfxVolume are persisted for later routing when music/sfx systems exist.
    }

    public static IReadOnlyList<(int w, int h)> DefaultResolutions { get; } = new List<(int w, int h)>
    {
        (1280, 720),
        (1920, 1080),
        (2560, 1440),
        (3840, 2160)
    };

    // Render distance utilities
    public static int GetRecommendedRenderDistance(int safeMax, string? gpuName = null, long? vramBytes = null)
    {
        // If no VRAM info, return safeMax.
        if (vramBytes is null || vramBytes <= 0) return safeMax;

        // simple heuristic
        var gb = vramBytes.Value / (1024L * 1024L * 1024L);
        int rec = gb switch
        {
            <= 3 => 8,
            <= 5 => 12,
            <= 7 => 16,
            _    => 20
        };
        return Math.Clamp(rec, 6, safeMax);
    }

    public static int GetSafeRenderDistance(string renderer, bool advancedMode)
    {
        var rendererCap = renderer == "Vulkan" ? 16 : 24;
        var engineCap = Math.Min(EngineRenderDistanceMax, rendererCap);
        if (advancedMode)
            return Math.Clamp(engineCap, RenderDistanceMin, engineCap);
        return Math.Clamp(engineCap, RenderDistanceMin, engineCap);
    }
}
