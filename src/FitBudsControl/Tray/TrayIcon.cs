using System.Runtime.InteropServices;
using FitBudsControl.Interop;

namespace FitBudsControl.Tray;

internal enum TrayIconState
{
    Normal,
    Disconnected,
    LowBattery,
}

/// <summary>
/// Notification-area icon backed by a native hidden HWND rather than a WinUI Window.
/// This keeps the tray plumbing out of the taskbar/Alt+Tab window list entirely.
/// </summary>
internal sealed class TrayIcon : IDisposable
{
    private const uint NimAdd = 0x00000000;
    private const uint NimModify = 0x00000001;
    private const uint NimDelete = 0x00000002;
    private const uint NimSetVersion = 0x00000004;

    private const uint NifMessage = 0x00000001;
    private const uint NifIcon = 0x00000002;
    private const uint NifTip = 0x00000004;
    private const uint NifInfo = 0x00000010;
    private const uint NifGuid = 0x00000020;
    private const uint NifShowTip = 0x00000080;

    private const uint NotifyIconVersion4 = 4;
    private const uint NiifInfo = 0x00000001;
    private const uint WmLButtonUp = 0x0202;
    private const uint WmRButtonUp = 0x0205;
    private const uint WmContextMenu = 0x007B;
    private const uint NinSelect = 0x0400;
    private const uint NinKeySelect = 0x0401;

    private const uint CallbackMessage = NativeMethods.WmApp + 0x31;
    private const uint WsExToolWindow = 0x00000080;
    private const uint WsExNoActivate = 0x08000000;

    private const uint ImageIcon = 1;
    private const uint LrLoadFromFile = 0x00000010;
    private const uint LrDefaultSize = 0x00000040;

    private static readonly Guid IconGuid = new("6A4DF32F-9F8E-4EE5-AE06-D1875C762D0A");

    private readonly NativeMethods.WindowProc _windowProc;
    private readonly string _windowClassName;
    private readonly nint _moduleHandle;
    private readonly uint _taskbarCreatedMessage;
    private ushort _windowClassAtom;
    private nint _hwnd;
    private nint _icon;
    private bool _ownsIcon;
    private bool _added;
    private bool _disposed;
    private TrayIconState _state;
    private long _lastPrimaryInvokeTick;
    private long _lastSecondaryInvokeTick;
    private string _tooltip = "FitBuds Turbo";

    public TrayIcon(TrayIconState initialState = TrayIconState.Normal)
    {
        _windowProc = WindowProc;
        _windowClassName = $"FitBudsControl.Tray.{Guid.NewGuid():N}";
        _moduleHandle = GetModuleHandle(null);
        _taskbarCreatedMessage = NativeMethods.RegisterWindowMessage("TaskbarCreated");
        _state = initialState;

        CreateHiddenMessageWindow();
        (_icon, _ownsIcon) = LoadApplicationIcon(_state);
        AddIcon();
    }

    public event EventHandler? PrimaryInvoked;
    public event EventHandler? SecondaryInvoked;

    public void UpdateState(TrayIconState state)
    {
        if (_disposed || state == _state)
        {
            return;
        }

        var (newIcon, ownsNewIcon) = LoadApplicationIcon(state);
        if (newIcon == 0)
        {
            return;
        }

        var oldIcon = _icon;
        var ownedOldIcon = _ownsIcon;
        _icon = newIcon;
        _ownsIcon = ownsNewIcon;
        _state = state;

        if (_added)
        {
            var data = CreateData();
            data.Flags = NifIcon | NifGuid;
            data.IconHandle = _icon;
            _ = Shell_NotifyIcon(NimModify, ref data);
        }

        if (oldIcon != 0 && ownedOldIcon)
        {
            _ = DestroyIcon(oldIcon);
        }
    }

    public void UpdateTooltip(string text)
    {
        if (_disposed)
        {
            return;
        }

        _tooltip = string.IsNullOrWhiteSpace(text) ? "FitBuds Turbo" : text;
        if (!_added)
        {
            return;
        }

        var data = CreateData();
        data.Flags = NifTip | NifGuid | NifShowTip;
        data.Tip = Truncate(_tooltip, 127);
        _ = Shell_NotifyIcon(NimModify, ref data);
    }

    public void ShowNotification(string title, string message)
    {
        if (_disposed || !_added)
        {
            return;
        }

        var data = CreateData();
        data.Flags = NifInfo | NifGuid;
        data.InfoTitle = Truncate(title, 63);
        data.Info = Truncate(message, 255);
        data.InfoFlags = NiifInfo;
        _ = Shell_NotifyIcon(NimModify, ref data);
    }

    public bool TryGetIconRect(out NativeMethods.Rect rect)
    {
        if (_disposed || !_added)
        {
            rect = default;
            return false;
        }

        var id = new NotifyIconIdentifier
        {
            Size = (uint)Marshal.SizeOf<NotifyIconIdentifier>(),
            WindowHandle = _hwnd,
            Id = 1,
            GuidItem = IconGuid,
        };
        return Shell_NotifyIconGetRect(ref id, out rect) >= 0;
    }

    private void CreateHiddenMessageWindow()
    {
        var windowClass = new WndClassEx
        {
            Size = (uint)Marshal.SizeOf<WndClassEx>(),
            Instance = _moduleHandle,
            WindowProc = Marshal.GetFunctionPointerForDelegate(_windowProc),
            ClassName = _windowClassName,
        };

        _windowClassAtom = RegisterClassEx(ref windowClass);
        if (_windowClassAtom == 0)
        {
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastPInvokeError(), "无法启动任务栏图标");
        }

        // This is a real native top-level HWND, but it is never shown. WS_EX_TOOLWINDOW
        // keeps it out of the taskbar and Alt+Tab while still allowing TaskbarCreated
        // broadcasts to reach it after Explorer restarts.
        _hwnd = CreateWindowEx(
            WsExToolWindow | WsExNoActivate,
            _windowClassName,
            string.Empty,
            0,
            0,
            0,
            0,
            0,
            0,
            0,
            _moduleHandle,
            0);

        if (_hwnd == 0)
        {
            var error = Marshal.GetLastPInvokeError();
            _ = UnregisterClass(_windowClassName, _moduleHandle);
            _windowClassAtom = 0;
            throw new System.ComponentModel.Win32Exception(error, "无法启动任务栏图标");
        }
    }

    private void AddIcon()
    {
        var data = CreateData();
        data.Flags = NifMessage | NifIcon | NifTip | NifGuid | NifShowTip;
        data.CallbackMessage = CallbackMessage;
        data.IconHandle = _icon;
        data.Tip = Truncate(_tooltip, 127);

        _added = Shell_NotifyIcon(NimAdd, ref data);
        if (_added)
        {
            data.VersionOrTimeout = NotifyIconVersion4;
            _ = Shell_NotifyIcon(NimSetVersion, ref data);
        }
    }

    private NotifyIconData CreateData()
        => new()
        {
            Size = (uint)Marshal.SizeOf<NotifyIconData>(),
            WindowHandle = _hwnd,
            Id = 1,
            GuidItem = IconGuid,
            Tip = string.Empty,
            Info = string.Empty,
            InfoTitle = string.Empty,
        };

    private nint WindowProc(nint hwnd, uint message, nuint wParam, nint lParam)
    {
        if (message == _taskbarCreatedMessage)
        {
            _added = false;
            AddIcon();
            return 0;
        }

        if (message == CallbackMessage)
        {
            // Shell versions can deliver both a mouse-up style notification and the
            // NOTIFYICON_VERSION_4 NIN_SELECT/NIN_KEYSELECT notification for one user
            // action. Accept both families but debounce them so one click maps to one
            // application action instead of show -> hide.
            var notification = (uint)((nuint)lParam & 0xFFFF);
            switch (notification)
            {
                case WmLButtonUp:
                case NinSelect:
                case NinKeySelect:
                    InvokePrimaryDebounced();
                    return 0;
                case WmRButtonUp:
                case WmContextMenu:
                    InvokeSecondaryDebounced();
                    return 0;
            }
        }

        return DefWindowProc(hwnd, message, wParam, lParam);
    }


    private void InvokePrimaryDebounced()
    {
        var now = Environment.TickCount64;
        if (now - _lastPrimaryInvokeTick < 250)
        {
            return;
        }
        _lastPrimaryInvokeTick = now;
        PrimaryInvoked?.Invoke(this, EventArgs.Empty);
    }

    private void InvokeSecondaryDebounced()
    {
        var now = Environment.TickCount64;
        if (now - _lastSecondaryInvokeTick < 250)
        {
            return;
        }
        _lastSecondaryInvokeTick = now;
        SecondaryInvoked?.Invoke(this, EventArgs.Empty);
    }

    private static (nint Handle, bool Owned) LoadApplicationIcon(TrayIconState state)
    {
        var fileName = state switch
        {
            TrayIconState.Disconnected => "AppIconDisconnected.ico",
            TrayIconState.LowBattery => "AppIconLowBattery.ico",
            _ => "AppIcon.ico",
        };
        var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", fileName);
        var icon = LoadImage(0, iconPath, ImageIcon, 0, 0, LrLoadFromFile | LrDefaultSize);
        if (icon != 0)
        {
            return (icon, true);
        }

        // If a status variant is unavailable, fall back to the normal application icon.
        if (!string.Equals(fileName, "AppIcon.ico", StringComparison.OrdinalIgnoreCase))
        {
            var normalPath = Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico");
            icon = LoadImage(0, normalPath, ImageIcon, 0, 0, LrLoadFromFile | LrDefaultSize);
            if (icon != 0)
            {
                return (icon, true);
            }
        }

        // Shared Windows fallback; do not destroy it.
        return (LoadIcon(0, (nint)32512), false); // IDI_APPLICATION
    }

    private static string Truncate(string text, int max)
        => text.Length <= max ? text : text[..max];

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;

        if (_added)
        {
            var data = CreateData();
            _ = Shell_NotifyIcon(NimDelete, ref data);
            _added = false;
        }

        if (_icon != 0 && _ownsIcon)
        {
            _ = DestroyIcon(_icon);
        }
        _icon = 0;
        _ownsIcon = false;

        if (_hwnd != 0)
        {
            _ = DestroyWindow(_hwnd);
            _hwnd = 0;
        }

        if (_windowClassAtom != 0)
        {
            _ = UnregisterClass(_windowClassName, _moduleHandle);
            _windowClassAtom = 0;
        }

        GC.KeepAlive(_windowProc);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyIconData
    {
        public uint Size;
        public nint WindowHandle;
        public uint Id;
        public uint Flags;
        public uint CallbackMessage;
        public nint IconHandle;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string Tip;
        public uint State;
        public uint StateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string Info;
        public uint VersionOrTimeout;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string InfoTitle;
        public uint InfoFlags;
        public Guid GuidItem;
        public nint BalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NotifyIconIdentifier
    {
        public uint Size;
        public nint WindowHandle;
        public uint Id;
        public Guid GuidItem;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WndClassEx
    {
        public uint Size;
        public uint Style;
        public nint WindowProc;
        public int ClassExtra;
        public int WindowExtra;
        public nint Instance;
        public nint Icon;
        public nint Cursor;
        public nint Background;
        public string? MenuName;
        public string ClassName;
        public nint SmallIcon;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Shell_NotifyIcon(uint message, ref NotifyIconData data);

    [DllImport("shell32.dll")]
    private static extern int Shell_NotifyIconGetRect(ref NotifyIconIdentifier identifier, out NativeMethods.Rect iconLocation);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern nint GetModuleHandle(string? moduleName);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClassEx(ref WndClassEx windowClass);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint CreateWindowEx(
        uint exStyle,
        string className,
        string windowName,
        uint style,
        int x,
        int y,
        int width,
        int height,
        nint parent,
        nint menu,
        nint instance,
        nint parameter);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyWindow(nint hwnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterClass(string className, nint instance);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint DefWindowProc(nint hwnd, uint message, nuint wParam, nint lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint LoadImage(nint instance, string name, uint type, int cx, int cy, uint load);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint LoadIcon(nint instance, nint iconName);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(nint icon);
}
