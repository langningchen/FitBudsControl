using System.Runtime.InteropServices;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Windows.Graphics;
using WinRT.Interop;

namespace FitBudsControl.Interop;

internal static class NativeMethods
{
    internal const uint WmGetMinMaxInfo = 0x0024;
    internal const uint WmApp = 0x8000;
    internal const int GwlpWndProc = -4;
    internal const int SwHide = 0;
    internal const int DwmwaWindowCornerPreference = 33;
    internal const int DwmwcpRound = 2;

    [StructLayout(LayoutKind.Sequential)]
    internal struct Point
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MinMaxInfo
    {
        public Point Reserved;
        public Point MaxSize;
        public Point MaxPosition;
        public Point MinTrackSize;
        public Point MaxTrackSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;

        public int Width => Right - Left;
        public int Height => Bottom - Top;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct MonitorInfo
    {
        public uint Size;
        public Rect Monitor;
        public Rect Work;
        public uint Flags;
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    internal delegate nint WindowProc(nint hwnd, uint msg, nuint wParam, nint lParam);

    internal static nint SetWindowLongPtr(nint hwnd, int index, nint newLong)
    {
        if (IntPtr.Size == 8)
        {
            return SetWindowLongPtr64(hwnd, index, newLong);
        }
        return new nint(SetWindowLong32(hwnd, index, newLong.ToInt32()));
    }

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern nint SetWindowLongPtr64(nint hwnd, int index, nint newLong);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW", SetLastError = true)]
    private static extern int SetWindowLong32(nint hwnd, int index, int newLong);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    internal static extern nint CallWindowProc(nint previousProc, nint hwnd, uint msg, nuint wParam, nint lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    internal static extern uint RegisterWindowMessage(string message);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetForegroundWindow(nint hwnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool ShowWindow(nint hwnd, int command);

    [DllImport("user32.dll")]
    internal static extern uint GetDpiForWindow(nint hwnd);

    [DllImport("user32.dll")]
    internal static extern nint MonitorFromRect(ref Rect rect, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo info);

    [DllImport("dwmapi.dll")]
    internal static extern int DwmSetWindowAttribute(nint hwnd, int attribute, ref int value, int valueSize);

    internal static nint GetWindowHandle(Window window) => WindowNative.GetWindowHandle(window);

    internal static AppWindow GetAppWindow(Window window)
    {
        var hwnd = GetWindowHandle(window);
        var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd);
        return AppWindow.GetFromWindowId(windowId);
    }

    internal static int DipToPixels(nint hwnd, int dip)
    {
        var dpi = GetDpiForWindow(hwnd);
        if (dpi == 0)
        {
            dpi = 96;
        }
        return (int)Math.Round(dip * dpi / 96.0);
    }

    internal static RectInt32 GetPopupBoundsNearTray(nint popupHwnd, Rect iconRect, int widthDip, int heightDip)
    {
        const uint monitorDefaultToNearest = 2;
        var monitorHandle = MonitorFromRect(ref iconRect, monitorDefaultToNearest);
        var info = new MonitorInfo { Size = (uint)Marshal.SizeOf<MonitorInfo>() };
        if (monitorHandle == 0 || !GetMonitorInfo(monitorHandle, ref info))
        {
            info.Monitor = new Rect { Left = 0, Top = 0, Right = 1920, Bottom = 1080 };
            info.Work = new Rect { Left = 0, Top = 0, Right = 1920, Bottom = 1040 };
        }

        var width = DipToPixels(popupHwnd, widthDip);
        var height = DipToPixels(popupHwnd, heightDip);
        var gap = DipToPixels(popupHwnd, 8);

        var monitor = info.Monitor;
        var work = info.Work;
        var distLeft = Math.Abs(iconRect.Left - monitor.Left);
        var distRight = Math.Abs(monitor.Right - iconRect.Right);
        var distTop = Math.Abs(iconRect.Top - monitor.Top);
        var distBottom = Math.Abs(monitor.Bottom - iconRect.Bottom);
        var nearest = Math.Min(Math.Min(distLeft, distRight), Math.Min(distTop, distBottom));

        int x;
        int y;

        if (nearest == distTop)
        {
            x = iconRect.Left;
            y = iconRect.Bottom + gap;
        }
        else if (nearest == distLeft)
        {
            x = iconRect.Right + gap;
            y = iconRect.Bottom - height;
        }
        else if (nearest == distRight)
        {
            x = iconRect.Left - gap - width;
            y = iconRect.Bottom - height;
        }
        else
        {
            x = iconRect.Right - width;
            y = iconRect.Top - gap - height;
        }

        x = Math.Clamp(x, work.Left + gap, Math.Max(work.Left + gap, work.Right - width - gap));
        y = Math.Clamp(y, work.Top + gap, Math.Max(work.Top + gap, work.Bottom - height - gap));

        return new RectInt32(x, y, width, height);
    }

    internal static void EnableRoundedCorners(Window window)
    {
        try
        {
            var hwnd = GetWindowHandle(window);
            var preference = DwmwcpRound;
            _ = DwmSetWindowAttribute(hwnd, DwmwaWindowCornerPreference, ref preference, sizeof(int));
        }
        catch
        {
            // Windows 10 or unsupported DWM: no-op.
        }
    }
}
