using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Xna.Framework.Audio;
using Microsoft.Xna.Framework.Input;

namespace LatticeVeilMonoGame.Core;

public sealed class GameSettings
{
    private const int EnumCurrentSettings = -1;
    private const uint EnumDisplaySettingsRawMode = 0x00000002;

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

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern bool EnumDisplaySettings(string? lpszDeviceName, int iModeNum, ref DEVMODE lpDevMode);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern bool EnumDisplaySettingsEx(string? lpszDeviceName, int iModeNum, ref DEVMODE lpDevMode, uint dwFlags);

    // Video
    public bool Fullscreen { get; set; } = false;
    public bool VSync { get; set; } = true;
    public int ResolutionWidth { get; set; } = 1280;
    public int ResolutionHeight { get; set; } = 720;
    public float GuiScale { get; set; } = 1f;
    public string QualityPreset { get; set; } = "MEDIUM";
    public float Brightness { get; set; } = 1f;
    public int FieldOfView { get; set; } = 70;
    public int RenderDistanceChunks { get; set; } = 10;

    // Launcher
    public bool KeepLauncherOpen { get; set; } = true;
    public bool DarkMode { get; set; } = true;
    public string RendererBackend { get; set; } = "OpenGL"; // "OpenGL" or "Vulkan"
    public int LauncherRenderDistance { get; set; } = 16; // Launcher-specific setting
    public bool AdvancedMode { get; set; } = false; // Allow override of safe caps

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
    public string ReticleStyle { get; set; } = "Dot";
    public int ReticleSize { get; set; } = 8;
    public int ReticleThickness { get; set; } = 2;
    public string ReticleColor { get; set; } = "FFFFFFC8";
    public string BlockOutlineColor { get; set; } = DefaultBlockOutlineColor;
    public Dictionary<string, Keys> Keybinds { get; set; } = new()
    {
        ["MoveUp"] = Keys.W,
        ["MoveDown"] = Keys.S,
        ["MoveLeft"] = Keys.A,
        ["MoveRight"] = Keys.D,
        ["Jump"] = Keys.Space,
        ["Crouch"] = Keys.LeftShift,
        ["Inventory"] = Keys.E,
        ["DropItem"] = Keys.Q,
        ["GiveItem"] = Keys.F,
        ["Pause"] = Keys.Escape
    };

    // Packs
    public List<string> EnabledPacks { get; set; } = new();

    public static GameSettings LoadOrCreate(Logger log)
    {
        try
        {
            Directory.CreateDirectory(Paths.RootDir);
            Directory.CreateDirectory(Paths.ConfigDir);

            if (!File.Exists(Paths.SettingsJsonPath))
            {
                var s = new GameSettings();
                s.Save(log);
                return s;
            }

            var json = File.ReadAllText(Paths.SettingsJsonPath);
            var loaded = JsonSerializer.Deserialize<GameSettings>(json) ?? new GameSettings();
            Sanitize(loaded);
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
        s.GuiScale = ClampRange(s.GuiScale, 0.75f, 2.0f);
        s.Brightness = ClampRange(s.Brightness, 0.5f, 1.5f);
        s.FieldOfView = Math.Clamp(s.FieldOfView, 60, 110);
        s.RenderDistanceChunks = Math.Clamp(s.RenderDistanceChunks, 4, 24);
        s.MouseSensitivity = ClampRange(s.MouseSensitivity, 0.0005f, 0.01f);
        s.QualityPreset = NormalizeQuality(s.QualityPreset);
        s.AudioInputDeviceId ??= "";

        s.AudioOutputDeviceId ??= "";
        s.VoiceOutputDeviceId ??= "";
        s.GameOutputDeviceId ??= "";
        s.ReticleStyle = NormalizeReticleStyle(s.ReticleStyle);
        s.ReticleSize = Math.Clamp(s.ReticleSize, ReticleSizeMin, ReticleSizeMax);
        s.ReticleThickness = Math.Clamp(s.ReticleThickness, ReticleThicknessMin, ReticleThicknessMax);
        s.ReticleColor = NormalizeHexColor(s.ReticleColor, DefaultReticleColor);
        s.BlockOutlineColor = NormalizeHexColor(s.BlockOutlineColor, DefaultBlockOutlineColor);

        EnsureKeybinds(s);

        s.EnabledPacks ??= new List<string>();
    }

    private static float Clamp01(float v) => v < 0f ? 0f : (v > 1f ? 1f : v);
    private static float ClampRange(float v, float min, float max) => v < min ? min : (v > max ? max : v);
    private const int ReticleSizeMin = 2;
    private const int ReticleSizeMax = 32;
    private const int ReticleThicknessMin = 1;
    private const int ReticleThicknessMax = 6;
    private const string DefaultReticleColor = "FFFFFFC8";
    private const string DefaultBlockOutlineColor = "C8DCE678";

    private static string NormalizeQuality(string? value)
    {
        var quality = string.IsNullOrWhiteSpace(value) ? "MEDIUM" : value.Trim().ToUpperInvariant();
        return quality is "LOW" or "MEDIUM" or "HIGH" or "ULTRA" ? quality : "MEDIUM";
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
            Directory.CreateDirectory(Paths.ConfigDir);
            var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(Paths.SettingsJsonPath, json);
        }
        catch (Exception ex)
        {
            log.Warn($"Failed to save settings: {ex.Message}");
        }
    }

    public void ApplyGraphics(global::Microsoft.Xna.Framework.GraphicsDeviceManager graphics)
    {
        var adapter = global::Microsoft.Xna.Framework.Graphics.GraphicsAdapter.DefaultAdapter;
        var currentMode = adapter.CurrentDisplayMode;
        var deviceName = GetScreenDeviceName(graphics);
        var desktop = GetDesktopResolution(deviceName, currentMode.Width, currentMode.Height);

        var maxW = desktop.w;
        var maxH = desktop.h;

        var width = Math.Min(ResolutionWidth, maxW);
        var height = Math.Min(ResolutionHeight, maxH);
        if (width < 640) width = 640;
        if (height < 480) height = 480;

        graphics.IsFullScreen = Fullscreen;
        graphics.SynchronizeWithVerticalRetrace = VSync;
        graphics.PreferredBackBufferWidth = width;
        graphics.PreferredBackBufferHeight = height;
        graphics.ApplyChanges();
    }

    private static string? GetScreenDeviceName(global::Microsoft.Xna.Framework.GraphicsDeviceManager graphics)
    {
        try
        {
            var handle = graphics.GraphicsDevice?.PresentationParameters.DeviceWindowHandle ?? IntPtr.Zero;
            if (handle != IntPtr.Zero)
                return System.Windows.Forms.Screen.FromHandle(handle).DeviceName;
        }
        catch
        {
            // Fall back to primary screen.
        }

        try
        {
            return System.Windows.Forms.Screen.PrimaryScreen?.DeviceName;
        }
        catch
        {
            return null;
        }
    }

    private static (int w, int h) GetDesktopResolution(string? deviceName, int fallbackW, int fallbackH)
    {
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
            if (!string.IsNullOrWhiteSpace(deviceName))
            {
                foreach (var screen in System.Windows.Forms.Screen.AllScreens)
                {
                    if (string.Equals(screen.DeviceName, deviceName, StringComparison.OrdinalIgnoreCase))
                        return (screen.Bounds.Width, screen.Bounds.Height);
                }
            }
        }
        catch
        {
            // Fall back to primary screen.
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
        var maxByRenderer = renderer == "Vulkan" ? 16 : 24;
        var safeMax = 16;
        
        if (advancedMode)
            return Math.Clamp(16, 4, maxByRenderer); // Use fixed 16 as recommended for now
        else
            return Math.Clamp(safeMax, 4, maxByRenderer);
    }
}
