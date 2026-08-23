using FitBudsControl.Bluetooth;
using FitBudsControl.Interop;
using FitBudsControl.Models;
using FitBudsControl.Services;
using FitBudsControl.Tray;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;

namespace FitBudsControl.Windows;

public sealed partial class QuickPanelWindow : Window
{
    private const int PanelWidthDip = 420;
    private const int FallbackPanelHeightDip = 590;
    private const int MinimumPanelHeightDip = 560;
    private const int WindowChromeAllowanceDip = 8;

    private enum BatteryChannel { Left, Right, Case }

    private readonly EarbudsService _service;
    private readonly AppWindow _appWindow;
    private CancellationTokenSource? _ambientDebounce;
    private DateTimeOffset _ignoreDeactivateUntil;
    private bool _updatingUi;
    private bool _visible;
    private bool _busy;

    public QuickPanelWindow(EarbudsService service)
    {
        InitializeComponent();
        _service = service;
        _service.StateChanged += Service_StateChanged;

        Title = "FitBuds Turbo";
        SystemBackdrop = new DesktopAcrylicBackdrop();
        _appWindow = NativeMethods.GetAppWindow(this);
        _appWindow.IsShownInSwitchers = false;
        _appWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico"));

        if (_appWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsResizable = false;
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = false;
            presenter.IsAlwaysOnTop = true;
            presenter.SetBorderAndTitleBar(true, false);
        }

        NativeMethods.EnableRoundedCorners(this);
        Activated += QuickPanelWindow_Activated;
        Closed += (_, _) =>
        {
            _ambientDebounce?.Cancel();
            _ambientDebounce?.Dispose();
            _service.StateChanged -= Service_StateChanged;
        };
        UpdateUi();
    }

    public bool IsVisible => _visible;

    internal async void ShowNearTray(TrayIcon trayIcon, bool refreshState = true)
    {
        // Measure the actual content instead of reserving a fixed empty strip at the
        // bottom of the flyout. The regular page padding remains part of DesiredSize.
        UpdateUi();
        var hwnd = NativeMethods.GetWindowHandle(this);
        // Before the window has been shown once, WinUI can report a slightly
        // smaller DesiredSize because some controls have not completed their first
        // layout pass yet. Never let that first measurement make the flyout shorter
        // than the normal safe height.
        var panelHeightDip = Math.Max(MeasurePanelHeightDip(), FallbackPanelHeightDip);

        ApplyPanelBounds(trayIcon, panelHeightDip);

        _visible = true;
        // The taskbar can transiently take activation back while the mouse-up that
        // opened us is still being processed. Ignore that first deactivation so the
        // panel does not flash and immediately hide.
        _ignoreDeactivateUntil = DateTimeOffset.UtcNow.AddMilliseconds(450);

        // AppWindow.Hide() is used when the flyout loses focus. Show it explicitly
        // on every tray invocation; Window.Activate() alone is not a reliable
        // re-show mechanism for an AppWindow that was previously hidden.
        _appWindow.Show(true);
        Activate();
        _ = NativeMethods.SetForegroundWindow(hwnd);

        // 首次显示后，WinUI 才完成所有控件真正的布局。下一轮界面消息里
        // 再量一次并重新贴到任务栏，避免第一次打开比后续略矮。
        DispatcherQueue.TryEnqueue(() =>
        {
            if (!_visible)
            {
                return;
            }

            RootGrid.UpdateLayout();
            var settledHeightDip = Math.Max(MeasurePanelHeightDip(), FallbackPanelHeightDip);
            ApplyPanelBounds(trayIcon, settledHeightDip);
        });

        // 每次打开控制面板都读取这里会显示的状态；如果尚未连接，连接过程本身
        // 会读取一份基础状态，所以不再重复读取第二次。
        if (refreshState && !_busy)
        {
            if (_service.IsConnected)
            {
                SetBusy(true, "正在读取最新状态…");
                try
                {
                    await _service.RefreshQuickAsync();
                    StatusText.Text = "状态已更新";
                }
                catch
                {
                    StatusText.Text = "没有读到最新状态";
                }
                finally
                {
                    SetBusy(false);
                    UpdateUi();
                }
            }
            else if (_service.Settings.AutoConnectOnOpen)
            {
                SetBusy(true, "正在连接耳机…");
                try
                {
                    await _service.ConnectAsync();
                    StatusText.Text = "已连接";
                }
                catch
                {
                    StatusText.Text = "连接失败";
                }
                finally
                {
                    SetBusy(false);
                    UpdateUi();
                }
            }
        }
    }

    public void HidePanel()
    {
        if (!_visible)
        {
            return;
        }
        _visible = false;
        _appWindow.Hide();
    }

    private void QuickPanelWindow_Activated(object sender, WindowActivatedEventArgs args)
    {
        if (!_visible || args.WindowActivationState != WindowActivationState.Deactivated)
        {
            return;
        }

        if (DateTimeOffset.UtcNow < _ignoreDeactivateUntil)
        {
            return;
        }

        HidePanel();
    }

    private void Service_StateChanged(object? sender, EventArgs e) => UpdateUi();

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RefreshAsync();

    private async Task RefreshAsync()
    {
        if (_busy)
        {
            return;
        }

        SetBusy(true, _service.IsConnected ? "正在读取状态…" : "正在连接并读取状态…");
        try
        {
            await _service.RefreshQuickAsync();
            StatusText.Text = "状态已刷新";
        }
        catch
        {
            StatusText.Text = "刷新失败";
        }
        finally
        {
            SetBusy(false);
            UpdateUi();
        }
    }

    private async void NoiseButton_Click(object sender, RoutedEventArgs e)
    {
        if (_updatingUi || _busy || !_service.IsConnected || sender is not ToggleButton button || button.Tag is not string tag ||
            !Enum.TryParse<NoiseMode>(tag, out var mode))
        {
            return;
        }

        SetBusy(true, $"正在切换到 {FitBudsProtocol.NoiseLabels[mode]}…");
        try
        {
            var result = await _service.SetNoiseAsync(mode);
            StatusText.Text = result.Success ? result.Detail : $"失败：{result.Detail}";
        }
        finally
        {
            SetBusy(false);
            UpdateUi();
        }
    }

    private async void SceneButton_Click(object sender, RoutedEventArgs e)
    {
        if (_updatingUi || _busy || !_service.IsConnected || sender is not ToggleButton button || button.Tag is not string tag ||
            !Enum.TryParse<SceneMode>(tag, out var scene))
        {
            return;
        }

        SetBusy(true, $"正在切换到 {FitBudsProtocol.SceneLabels[scene]}…");
        try
        {
            var result = await _service.SetSceneAsync(scene);
            StatusText.Text = result.Success ? result.Detail : $"失败：{result.Detail}";
        }
        finally
        {
            SetBusy(false);
            UpdateUi();
        }
    }

    private async void AmbientSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_updatingUi || !_service.IsConnected)
        {
            return;
        }

        var value = (int)Math.Round(e.NewValue);
        AmbientValueText.Text = $"{value:+0;-0;0}";

        _ambientDebounce?.Cancel();
        _ambientDebounce?.Dispose();
        _ambientDebounce = new CancellationTokenSource();
        var token = _ambientDebounce.Token;

        var ownsBusy = false;
        try
        {
            await Task.Delay(300, token);
            if (_busy || !_service.IsConnected)
            {
                return;
            }

            SetBusy(true, "正在调整环境音…");
            ownsBusy = true;
            var result = await _service.SetAmbientAsync(value);
            StatusText.Text = result.Success ? result.Detail : $"失败：{result.Detail}";
        }
        catch (OperationCanceledException)
        {
            return;
        }
        finally
        {
            if (ownsBusy)
            {
                SetBusy(false);
                UpdateUi();
            }
        }
    }

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        HidePanel();
        ((App)Application.Current).ShowSettingsWindow();
    }

    private void SetBusy(bool busy, string? message = null)
    {
        _busy = busy;
        BusyRing.IsActive = busy;
        RefreshButton.IsEnabled = !busy;
        if (!string.IsNullOrWhiteSpace(message))
        {
            StatusText.Text = message;
        }
        UpdateControlAvailability();
    }

    private void UpdateUi()
    {
        if (!DispatcherQueue.HasThreadAccess)
        {
            DispatcherQueue.TryEnqueue(UpdateUi);
            return;
        }

        _updatingUi = true;
        try
        {
            var state = _service.State;
            var connected = state.IsConnected && _service.IsConnected;
            DeviceNameText.Text = string.IsNullOrWhiteSpace(state.DeviceName) ? "FitBuds Turbo" : state.DeviceName;
            ConnectionText.Text = state.ConnectionDetail;

            UpdateBattery(LeftBatteryIcon, LeftBatteryText, LeftBatteryBar, LeftLowBatteryStatusIcon, null, state.LeftBattery, connected, BatteryChannel.Left, false);
            UpdateBattery(RightBatteryIcon, RightBatteryText, RightBatteryBar, RightLowBatteryStatusIcon, null, state.RightBattery, connected, BatteryChannel.Right, false);
            UpdateBattery(CaseBatteryIcon, CaseBatteryText, CaseBatteryBar, CaseLowBatteryStatusIcon, CaseChargingStatusIcon, state.CaseBattery, connected, BatteryChannel.Case, state.IsCaseCharging);

            DeepButton.IsChecked = connected && state.NoiseMode == NoiseMode.Deep;
            ComfortButton.IsChecked = connected && state.NoiseMode == NoiseMode.Comfort;
            WideButton.IsChecked = connected && state.NoiseMode == NoiseMode.Wide;
            WindButton.IsChecked = connected && state.NoiseMode == NoiseMode.Wind;
            AmbientButton.IsChecked = connected && state.NoiseMode == NoiseMode.Ambient;
            OffButton.IsChecked = connected && state.NoiseMode == NoiseMode.Off;

            var ambient = state.AmbientDb ?? 0;
            AmbientSlider.Value = ambient;
            AmbientValueText.Text = connected && state.AmbientDb is not null
                ? $"{ambient:+0;-0;0}"
                : "—";

            MusicButton.IsChecked = connected && state.SceneMode == SceneMode.Music;
            GameButton.IsChecked = connected && state.SceneMode == SceneMode.Game;
            SpatialButton.IsChecked = connected && state.SceneMode == SceneMode.Spatial;
            UpdateModeIconAppearance(connected);

            BatteryCard.Opacity = connected ? 1.0 : 0.42;
            NoisePanel.Opacity = connected ? 1.0 : 0.42;
            AmbientCard.Opacity = connected ? 1.0 : 0.42;
            ScenePanel.Opacity = connected ? 1.0 : 0.42;

            if (!_busy)
            {
                if (!string.IsNullOrWhiteSpace(state.LastError))
                {
                    StatusText.Text = state.LastError;
                }
                else if (!connected)
                {
                    StatusText.Text = "未连接";
                }
                else if (state.LastRefresh is not null)
                {
                    StatusText.Text = "刚刚更新";
                }
                else
                {
                    StatusText.Text = "已连接";
                }
            }

            UpdateControlAvailability();
        }
        finally
        {
            _updatingUi = false;
        }
    }

    private void ApplyPanelBounds(TrayIcon trayIcon, int panelHeightDip)
    {
        var hwnd = NativeMethods.GetWindowHandle(this);
        if (trayIcon.TryGetIconRect(out var iconRect))
        {
            var bounds = NativeMethods.GetPopupBoundsNearTray(hwnd, iconRect, PanelWidthDip, panelHeightDip);
            _appWindow.MoveAndResize(bounds);
            return;
        }

        _appWindow.Resize(new global::Windows.Graphics.SizeInt32(
            NativeMethods.DipToPixels(hwnd, PanelWidthDip),
            NativeMethods.DipToPixels(hwnd, panelHeightDip)));
    }

    private int MeasurePanelHeightDip()
    {
        try
        {
            // The two card wrappers added around ANC and sound scenes increase the
            // measured content height. Do not cap the result at the old 620-DIP
            // ceiling: that clipped the scene/footer area after v1.0.19. Measure the
            // complete Grid, preserve its normal page padding, and add a small
            // allowance for the native window border because AppWindow sizes the
            // outer window while DesiredSize describes the XAML client content.
            RootGrid.Measure(new global::Windows.Foundation.Size(PanelWidthDip, double.PositiveInfinity));
            var desiredClientHeight = (int)Math.Ceiling(RootGrid.DesiredSize.Height);
            var desiredWindowHeight = desiredClientHeight + WindowChromeAllowanceDip;
            return Math.Max(desiredWindowHeight, MinimumPanelHeightDip);
        }
        catch
        {
            return FallbackPanelHeightDip;
        }
    }

    private void UpdateControlAvailability()
    {
        var connected = _service.State.IsConnected && _service.IsConnected;
        var enabled = connected && !_busy;

        DeepButton.IsEnabled = enabled;
        ComfortButton.IsEnabled = enabled;
        WideButton.IsEnabled = enabled;
        WindButton.IsEnabled = enabled;
        AmbientButton.IsEnabled = enabled;
        OffButton.IsEnabled = enabled;
        MusicButton.IsEnabled = enabled;
        GameButton.IsEnabled = enabled;
        SpatialButton.IsEnabled = enabled;
        AmbientSlider.IsEnabled = enabled && _service.State.NoiseMode == NoiseMode.Ambient;
    }

    private void UpdateBattery(
        PathIcon icon,
        TextBlock text,
        ProgressBar bar,
        FrameworkElement lowIndicator,
        FrameworkElement? chargingIndicator,
        int? value,
        bool connected,
        BatteryChannel channel,
        bool isCaseCharging)
    {
        var available = connected && value is not null;
        text.Text = available ? $"{value}%" : "—";
        text.Opacity = available ? 1.0 : 0.45;
        bar.Value = available ? value!.Value : 0;
        bar.IsEnabled = available;
        bar.Opacity = available ? 1.0 : 0.28;
        bar.ClearValue(Control.ForegroundProperty);

        // Battery/device glyphs always use the normal theme foreground. Charging and
        // low-battery states are shown as a separate status glyph at the far right of
        // the percentage row instead of recoloring the device glyph or progress bar.
        icon.Opacity = connected ? 1.0 : 0.45;

        var charging = available && channel == BatteryChannel.Case && isCaseCharging;
        var low = available && !charging && value!.Value <= GetBatteryThreshold(channel);
        lowIndicator.Visibility = low ? Visibility.Visible : Visibility.Collapsed;
        if (chargingIndicator is not null)
        {
            chargingIndicator.Visibility = charging ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private int GetBatteryThreshold(BatteryChannel channel)
        => channel switch
        {
            BatteryChannel.Left => _service.Settings.LeftLowBatteryThreshold,
            BatteryChannel.Right => _service.Settings.RightLowBatteryThreshold,
            _ => _service.Settings.CaseLowBatteryThreshold
        };

    private void UpdateModeIconAppearance(bool connected)
    {
        SetModeIconAppearance(DeepIcon, DeepButton, connected);
        SetModeIconAppearance(ComfortIcon, ComfortButton, connected);
        SetModeIconAppearance(AmbientIcon, AmbientButton, connected);
        SetModeIconAppearance(WideIcon, WideButton, connected);
        SetModeIconAppearance(WindIcon, WindButton, connected);
        SetModeIconAppearance(OffIcon, OffButton, connected);
        SetModeIconAppearance(MusicIcon, MusicButton, connected);
        SetModeIconAppearance(GameIcon, GameButton, connected);
        SetModeIconAppearance(SpatialIcon, SpatialButton, connected);
    }

    private static void SetModeIconAppearance(PathIcon icon, ToggleButton button, bool connected)
    {
        // The checked tile uses a light accent fill, so its monochrome glyph needs a
        // dark foreground for contrast. Unchecked tiles keep the normal light glyph.
        icon.Foreground = button.IsChecked == true
            ? new SolidColorBrush(global::Windows.UI.Color.FromArgb(0xFF, 0x1F, 0x1F, 0x1F))
            : new SolidColorBrush(global::Windows.UI.Color.FromArgb(0xFF, 0xF3, 0xF3, 0xF3));
        icon.Opacity = !connected ? 0.40 : button.IsChecked == true ? 1.0 : 0.78;
    }


}
