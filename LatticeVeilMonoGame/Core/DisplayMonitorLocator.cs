using System;
using System.Runtime.InteropServices;
using Microsoft.Xna.Framework;

namespace LatticeVeilMonoGame.Core;

public readonly record struct DisplayMonitorInfo(
    string DeviceName,
    Rectangle Bounds,
    Rectangle WorkingArea,
    bool IsPrimary);

public static class DisplayMonitorLocator
{
    private const uint MonitorDefaultToNearest = 0x00000002;
    private const uint MonitorInfoPrimary = 0x00000001;

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MONITORINFOEX
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string szDevice;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SDL_Rect
    {
        public int x;
        public int y;
        public int w;
        public int h;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFOEX lpmi);

    [DllImport("SDL2.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int SDL_GetWindowDisplayIndex(IntPtr window);

    [DllImport("SDL2.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int SDL_GetDisplayBounds(int displayIndex, out SDL_Rect rect);

    [DllImport("SDL2.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int SDL_GetDisplayUsableBounds(int displayIndex, out SDL_Rect rect);

    public static DisplayMonitorInfo GetForWindow(IntPtr windowHandle)
    {
        if (windowHandle != IntPtr.Zero)
        {
            if (TryGetSdlDisplayForWindow(windowHandle, out var sdlMonitor))
                return sdlMonitor;

            try
            {
                var hMonitor = MonitorFromWindow(windowHandle, MonitorDefaultToNearest);
                if (hMonitor != IntPtr.Zero)
                {
                    var info = new MONITORINFOEX
                    {
                        cbSize = Marshal.SizeOf<MONITORINFOEX>(),
                        szDevice = string.Empty
                    };

                    if (GetMonitorInfo(hMonitor, ref info))
                    {
                        return new DisplayMonitorInfo(
                            NormalizeDeviceName(info.szDevice),
                            ToRectangle(info.rcMonitor),
                            ToRectangle(info.rcWork),
                            (info.dwFlags & MonitorInfoPrimary) != 0);
                    }
                }
            }
            catch
            {
                // Fall through to Screen-based fallback.
            }

            try
            {
                var screen = System.Windows.Forms.Screen.FromHandle(windowHandle);
                return FromScreen(screen);
            }
            catch
            {
                // Fall through to primary fallback.
            }
        }

        return GetPrimary();
    }

    public static bool IsSdlWindowHandle(IntPtr windowHandle) =>
        TryGetSdlDisplayForWindow(windowHandle, out _);

    public static bool TryGetByDeviceName(string? deviceName, out DisplayMonitorInfo monitor)
    {
        if (TryGetSdlDisplayByName(deviceName, out monitor))
            return true;

        try
        {
            foreach (var screen in System.Windows.Forms.Screen.AllScreens)
            {
                if (!string.Equals(screen.DeviceName, deviceName, StringComparison.OrdinalIgnoreCase))
                    continue;

                monitor = FromScreen(screen);
                return true;
            }
        }
        catch
        {
            // Fallback below.
        }

        monitor = default;
        return false;
    }

    public static DisplayMonitorInfo GetPrimary()
    {
        try
        {
            var primary = System.Windows.Forms.Screen.PrimaryScreen;
            if (primary != null)
                return FromScreen(primary);
        }
        catch
        {
            // Fall through to synthetic fallback.
        }

        return new DisplayMonitorInfo("PRIMARY", new Rectangle(0, 0, 1280, 720), new Rectangle(0, 0, 1280, 720), true);
    }

    private static DisplayMonitorInfo FromScreen(System.Windows.Forms.Screen screen)
    {
        var bounds = screen.Bounds;
        var workArea = screen.WorkingArea;
        return new DisplayMonitorInfo(
            NormalizeDeviceName(screen.DeviceName),
            new Rectangle(bounds.X, bounds.Y, bounds.Width, bounds.Height),
            new Rectangle(workArea.X, workArea.Y, workArea.Width, workArea.Height),
            screen.Primary);
    }

    private static bool TryGetSdlDisplayForWindow(IntPtr windowHandle, out DisplayMonitorInfo monitor)
    {
        try
        {
            var displayIndex = SDL_GetWindowDisplayIndex(windowHandle);
            if (displayIndex < 0)
            {
                monitor = default;
                return false;
            }

            if (SDL_GetDisplayBounds(displayIndex, out var boundsRect) != 0)
            {
                monitor = default;
                return false;
            }

            var usableRect = boundsRect;
            if (SDL_GetDisplayUsableBounds(displayIndex, out var usableBoundsRect) == 0)
                usableRect = usableBoundsRect;

            var isPrimary = displayIndex == 0;
            monitor = new DisplayMonitorInfo(
                $"DISPLAY{displayIndex + 1}",
                new Rectangle(boundsRect.x, boundsRect.y, Math.Max(0, boundsRect.w), Math.Max(0, boundsRect.h)),
                new Rectangle(usableRect.x, usableRect.y, Math.Max(0, usableRect.w), Math.Max(0, usableRect.h)),
                isPrimary);
            return true;
        }
        catch
        {
            monitor = default;
            return false;
        }
    }

    private static bool TryGetSdlDisplayByName(string? deviceName, out DisplayMonitorInfo monitor)
    {
        monitor = default;
        if (string.IsNullOrWhiteSpace(deviceName) || !deviceName.StartsWith("DISPLAY", StringComparison.OrdinalIgnoreCase))
            return false;

        if (!int.TryParse(deviceName["DISPLAY".Length..], out var displayNumber))
            return false;

        var displayIndex = displayNumber - 1;
        if (displayIndex < 0)
            return false;

        try
        {
            if (SDL_GetDisplayBounds(displayIndex, out var boundsRect) != 0)
                return false;

            var usableRect = boundsRect;
            if (SDL_GetDisplayUsableBounds(displayIndex, out var usableBoundsRect) == 0)
                usableRect = usableBoundsRect;

            monitor = new DisplayMonitorInfo(
                $"DISPLAY{displayIndex + 1}",
                new Rectangle(boundsRect.x, boundsRect.y, Math.Max(0, boundsRect.w), Math.Max(0, boundsRect.h)),
                new Rectangle(usableRect.x, usableRect.y, Math.Max(0, usableRect.w), Math.Max(0, usableRect.h)),
                displayIndex == 0);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static Rectangle ToRectangle(RECT rect) =>
        new(rect.Left, rect.Top, Math.Max(0, rect.Right - rect.Left), Math.Max(0, rect.Bottom - rect.Top));

    private static string NormalizeDeviceName(string? value) =>
        string.IsNullOrWhiteSpace(value) ? "UNKNOWN" : value.Trim();
}
