using System.ComponentModel;
using System.Runtime.InteropServices;
using FitBudsControl.Bluetooth;
using FitBudsControl.Models;
using FitBudsControl.Windows;

namespace FitBudsControl.Services;

internal sealed class GlobalShortcutService : IDisposable
{
    private const int WhKeyboardLl = 13;
    private const uint WmKeyDown = 0x0100;
    private const uint WmKeyUp = 0x0101;
    private const uint WmSysKeyDown = 0x0104;
    private const uint WmSysKeyUp = 0x0105;

    private readonly EarbudsService _earbuds;
    private readonly LowLevelKeyboardProc _keyboardProc;
    private readonly HashSet<uint> _pressedKeys = [];
    private readonly HashSet<uint> _suppressedKeys = [];
    private nint _hook;
    private ShortcutOverlayWindow? _overlay;
    private ShortcutAction? _activeAction;
    private ShortcutModifiers _activeModifiers;
    private int _selectedIndex;
    private bool _disposed;

    private ShortcutBinding _openPanel = new();
    private ShortcutBinding _noiseMode = new();
    private ShortcutBinding _soundScene = new();

    private static readonly NoiseMode[] NoiseModes =
    [
        NoiseMode.Deep,
        NoiseMode.Comfort,
        NoiseMode.Wide,
        NoiseMode.Wind,
        NoiseMode.Ambient,
        NoiseMode.Off,
    ];

    private static readonly SceneMode[] SceneModes =
    [
        SceneMode.Music,
        SceneMode.Game,
        SceneMode.Spatial,
    ];

    public GlobalShortcutService(EarbudsService earbuds)
    {
        _earbuds = earbuds;
        _keyboardProc = KeyboardHook;
        ReloadBindings();
        _earbuds.SettingsChanged += Earbuds_SettingsChanged;

        _hook = SetWindowsHookEx(WhKeyboardLl, _keyboardProc, GetModuleHandle(null), 0);
        if (_hook == 0)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "无法启动全局快捷键监听");
        }
    }

    public event EventHandler? OpenPanelRequested;

    private void Earbuds_SettingsChanged(object? sender, EventArgs e)
    {
        CancelPreview();
        ReloadBindings();
    }

    private void ReloadBindings()
    {
        _openPanel = Copy(_earbuds.Settings.OpenPanelShortcut);
        _noiseMode = Copy(_earbuds.Settings.NoiseModeShortcut);
        _soundScene = Copy(_earbuds.Settings.SoundSceneShortcut);
    }

    private static ShortcutBinding Copy(ShortcutBinding? binding)
        => binding is null
            ? new ShortcutBinding { Enabled = false }
            : new ShortcutBinding
            {
                Enabled = binding.Enabled,
                Modifiers = binding.Modifiers,
                Key = binding.Key,
            };

    private nint KeyboardHook(int code, nuint message, nint data)
    {
        if (code < 0 || _disposed)
        {
            return CallNextHookEx(_hook, code, message, data);
        }

        try
        {
            var keyboard = Marshal.PtrToStructure<KbdLlHookStruct>(data);
            var handled = message switch
            {
                WmKeyDown or WmSysKeyDown => HandleKeyDown(keyboard.VirtualKey),
                WmKeyUp or WmSysKeyUp => HandleKeyUp(keyboard.VirtualKey),
                _ => false,
            };

            return handled ? 1 : CallNextHookEx(_hook, code, message, data);
        }
        catch (Exception exception)
        {
            System.Diagnostics.Debug.WriteLine($"FitBudsControl shortcut hook failed: {exception}");
            return CallNextHookEx(_hook, code, message, data);
        }
    }

    private bool HandleKeyDown(uint key)
    {
        var firstPress = _pressedKeys.Add(key);
        if (!firstPress)
        {
            return _suppressedKeys.Contains(key);
        }

        if (Matches(_openPanel, key))
        {
            _suppressedKeys.Add(key);
            CancelPreview();
            OpenPanelRequested?.Invoke(this, EventArgs.Empty);
            return true;
        }

        if (Matches(_noiseMode, key))
        {
            _suppressedKeys.Add(key);
            AdvancePreview(ShortcutAction.NoiseMode, _noiseMode.Modifiers);
            return true;
        }

        if (Matches(_soundScene, key))
        {
            _suppressedKeys.Add(key);
            AdvancePreview(ShortcutAction.SoundScene, _soundScene.Modifiers);
            return true;
        }

        return false;
    }

    private bool HandleKeyUp(uint key)
    {
        _pressedKeys.Remove(key);
        var suppress = _suppressedKeys.Remove(key);

        var releasedModifier = ModifierForKey(key);
        if (_activeAction is not null && (_activeModifiers & releasedModifier) != 0)
        {
            CommitPreview();
        }

        return suppress;
    }

    private bool Matches(ShortcutBinding binding, uint key)
        => binding.Enabled && binding.Key is >= 0x41 and <= 0x5A &&
           key == (uint)binding.Key && GetPressedModifiers() == binding.Modifiers;

    private ShortcutModifiers GetPressedModifiers()
    {
        var modifiers = ShortcutModifiers.None;
        if (_pressedKeys.Any(IsControlKey))
        {
            modifiers |= ShortcutModifiers.Control;
        }
        if (_pressedKeys.Any(IsAltKey))
        {
            modifiers |= ShortcutModifiers.Alt;
        }
        if (_pressedKeys.Any(IsShiftKey))
        {
            modifiers |= ShortcutModifiers.Shift;
        }
        if (_pressedKeys.Any(IsWindowsKey))
        {
            modifiers |= ShortcutModifiers.Windows;
        }
        return modifiers;
    }

    private void AdvancePreview(ShortcutAction action, ShortcutModifiers modifiers)
    {
        if (!_earbuds.IsConnected)
        {
            CancelPreview();
            EnsureOverlay().ShowNotice("耳机未连接", "请先连接 FitBuds Turbo");
            return;
        }

        if (_activeAction == action)
        {
            _selectedIndex = (_selectedIndex + 1) % GetCandidateCount(action);
        }
        else
        {
            CancelPreview();
            _activeAction = action;
            _activeModifiers = modifiers;
            _selectedIndex = GetNextIndex(action);
        }

        var labels = GetDisplayChoices(action);
        EnsureOverlay().ShowChoices(labels, _selectedIndex);
    }

    private int GetNextIndex(ShortcutAction action)
    {
        var currentIndex = action switch
        {
            ShortcutAction.NoiseMode when _earbuds.State.NoiseMode is NoiseMode mode => Array.IndexOf(NoiseModes, mode),
            ShortcutAction.SoundScene when _earbuds.State.SceneMode is SceneMode scene => Array.IndexOf(SceneModes, scene),
            _ => -1,
        };
        return (currentIndex + 1) % GetCandidateCount(action);
    }

    private static int GetCandidateCount(ShortcutAction action)
        => action == ShortcutAction.NoiseMode ? NoiseModes.Length : SceneModes.Length;

    private static IReadOnlyList<string> GetDisplayChoices(ShortcutAction action)
        => action == ShortcutAction.NoiseMode
            ? NoiseModes.Select(mode => FitBudsProtocol.NoiseLabels[mode]).ToArray()
            : SceneModes.Select(mode => FitBudsProtocol.SceneLabels[mode]).ToArray();

    private async void CommitPreview()
    {
        var action = _activeAction;
        var selectedIndex = _selectedIndex;
        CancelPreview();

        try
        {
            if (action == ShortcutAction.NoiseMode)
            {
                await _earbuds.SetNoiseAsync(NoiseModes[selectedIndex]);
            }
            else if (action == ShortcutAction.SoundScene)
            {
                await _earbuds.SetSceneAsync(SceneModes[selectedIndex]);
            }
        }
        catch (Exception exception)
        {
            System.Diagnostics.Debug.WriteLine($"FitBudsControl shortcut action failed: {exception}");
        }
    }

    private void CancelPreview()
    {
        _activeAction = null;
        _activeModifiers = ShortcutModifiers.None;
        _overlay?.HideOverlay();
    }

    private ShortcutOverlayWindow EnsureOverlay()
        => _overlay ??= new ShortcutOverlayWindow();

    private static ShortcutModifiers ModifierForKey(uint key)
    {
        if (IsControlKey(key)) return ShortcutModifiers.Control;
        if (IsAltKey(key)) return ShortcutModifiers.Alt;
        if (IsShiftKey(key)) return ShortcutModifiers.Shift;
        if (IsWindowsKey(key)) return ShortcutModifiers.Windows;
        return ShortcutModifiers.None;
    }

    private static bool IsControlKey(uint key) => key is 0x11 or 0xA2 or 0xA3;
    private static bool IsAltKey(uint key) => key is 0x12 or 0xA4 or 0xA5;
    private static bool IsShiftKey(uint key) => key is 0x10 or 0xA0 or 0xA1;
    private static bool IsWindowsKey(uint key) => key is 0x5B or 0x5C;

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _earbuds.SettingsChanged -= Earbuds_SettingsChanged;
        if (_hook != 0)
        {
            _ = UnhookWindowsHookEx(_hook);
            _hook = 0;
        }
        _overlay?.Close();
        _overlay = null;
        GC.KeepAlive(_keyboardProc);
    }

    private enum ShortcutAction
    {
        NoiseMode,
        SoundScene,
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KbdLlHookStruct
    {
        public uint VirtualKey;
        public uint ScanCode;
        public uint Flags;
        public uint Time;
        public nuint ExtraInfo;
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate nint LowLevelKeyboardProc(int code, nuint message, nint data);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint SetWindowsHookEx(int hookId, LowLevelKeyboardProc callback, nint module, uint threadId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(nint hook);

    [DllImport("user32.dll")]
    private static extern nint CallNextHookEx(nint hook, int code, nuint message, nint data);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern nint GetModuleHandle(string? moduleName);
}
