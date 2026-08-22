using FitBudsControl.Models;
using FitBudsControl.Services;
using FitBudsControl.Tray;
using FitBudsControl.Windows;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace FitBudsControl;

public partial class App : Application
{
    private const string SingleInstanceMutexName = @"Local\FitBudsControl-6A4DF32F-9F8E-4EE5-AE06-D1875C762D0A";

    private Mutex? _singleInstanceMutex;
    private TrayIcon? _trayIcon;
    private QuickPanelWindow? _quickPanel;
    private SettingsWindow? _settingsWindow;
    private AppSettingsStore? _settingsStore;
    private EarbudsService? _earbuds;
    private LowBatteryNotificationService? _notifications;
    private DispatcherQueue? _dispatcherQueue;
    private CancellationTokenSource? _updateCheckCts;
    private bool _exiting;

    public bool DeveloperModeUnlocked { get; private set; }
    public UpdateCheckResult? LatestUpdateCheck { get; private set; }

    public App()
    {
        InitializeComponent();
        DispatcherShutdownMode = DispatcherShutdownMode.OnExplicitShutdown;
        UnhandledException += App_UnhandledException;
    }

    public EarbudsService Earbuds => _earbuds ?? throw new InvalidOperationException("程序尚未准备好");

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _singleInstanceMutex = new Mutex(initiallyOwned: false, SingleInstanceMutexName, out var isFirstInstance);
        if (!isFirstInstance)
        {
            _singleInstanceMutex.Dispose();
            _singleInstanceMutex = null;
            Exit();
            return;
        }

        _dispatcherQueue = DispatcherQueue.GetForCurrentThread();
        _settingsStore = new AppSettingsStore();
        _earbuds = new EarbudsService(_dispatcherQueue, _settingsStore);
        _notifications = new LowBatteryNotificationService(_dispatcherQueue, _earbuds);
        _notifications.OpenRequested += Notifications_OpenRequested;
        _notifications.FallbackNotificationRequested += Notifications_FallbackNotificationRequested;

        // Always keep the notification-area icon visible while the process is running.
        // Its background communicates the current state: gray=disconnected,
        // yellow=at least one battery at/below its configured low-battery threshold.
        _earbuds.StateChanged += Earbuds_StateOrSettingsChanged;
        _earbuds.SettingsChanged += Earbuds_StateOrSettingsChanged;
        _earbuds.EarbudsEventOccurred += Earbuds_EarbudsEventOccurred;
        _trayIcon = new TrayIcon(GetTrayIconState());
        _trayIcon.PrimaryInvoked += TrayIcon_PrimaryInvoked;
        _trayIcon.SecondaryInvoked += TrayIcon_SecondaryInvoked;
        _trayIcon.UpdateTooltip("FitBuds Turbo");

        if (!StartupService.TryApply(_earbuds.Settings.StartWithWindows, out var startupError) &&
            !string.IsNullOrWhiteSpace(startupError))
        {
            System.Diagnostics.Debug.WriteLine(startupError);
        }

        if (_earbuds.Settings.AutoUpdateCheckEnabled)
        {
            _updateCheckCts = new CancellationTokenSource();
            _ = CheckForUpdatesAsync(_updateCheckCts.Token);
        }
    }

    private void Earbuds_StateOrSettingsChanged(object? sender, EventArgs e)
        => _trayIcon?.UpdateState(GetTrayIconState());

    private TrayIconState GetTrayIconState()
    {
        if (_earbuds?.Settings.AlwaysUseBlueTrayIcon == true)
        {
            return TrayIconState.Normal;
        }

        if (_earbuds is null || !_earbuds.IsConnected)
        {
            return TrayIconState.Disconnected;
        }

        var state = _earbuds.State;
        var settings = _earbuds.Settings;
        var lowBattery =
            (state.LeftBattery is int left && left <= settings.LeftLowBatteryThreshold) ||
            (state.RightBattery is int right && right <= settings.RightLowBatteryThreshold) ||
            (state.CaseBattery is int caseBattery && caseBattery <= settings.CaseLowBatteryThreshold);

        return lowBattery ? TrayIconState.LowBattery : TrayIconState.Normal;
    }

    private void TrayIcon_PrimaryInvoked(object? sender, EventArgs e)
    {
        if (_trayIcon is null || _earbuds is null)
        {
            return;
        }

        var panel = EnsureQuickPanel();
        if (panel is null)
        {
            return;
        }

        if (panel.IsVisible)
        {
            panel.HidePanel();
        }
        else
        {
            panel.ShowNearTray(_trayIcon);
        }
    }

    private QuickPanelWindow? EnsureQuickPanel()
    {
        if (_earbuds is null)
        {
            return null;
        }

        if (_quickPanel is null)
        {
            var panel = new QuickPanelWindow(_earbuds);
            panel.Closed += (_, _) =>
            {
                if (ReferenceEquals(_quickPanel, panel))
                {
                    _quickPanel = null;
                }
            };
            _quickPanel = panel;
        }

        return _quickPanel;
    }

    private void ShowQuickPanelForEvent()
    {
        if (_trayIcon is null)
        {
            return;
        }

        EnsureQuickPanel()?.ShowNearTray(_trayIcon, refreshState: false);
    }

    private void TrayIcon_SecondaryInvoked(object? sender, EventArgs e)
    {
        _quickPanel?.HidePanel();
        ShowSettingsWindow();
    }

    public void UnlockDeveloperMode()
        => DeveloperModeUnlocked = true;

    private void Earbuds_EarbudsEventOccurred(object? sender, EarbudsEventArgs e)
    {
        if (_exiting || _earbuds is null)
        {
            return;
        }

        var settings = _earbuds.Settings;
        if (!settings.AutoOpenPanelOnEvents)
        {
            return;
        }

        var shouldOpen = e.Kind switch
        {
            EarbudsEventKind.Connected => settings.OpenPanelOnConnected,
            EarbudsEventKind.Disconnected => settings.OpenPanelOnDisconnected,
            EarbudsEventKind.NoiseModeChanged => settings.OpenPanelOnNoiseModeChanged,
            EarbudsEventKind.SoundModeChanged => settings.OpenPanelOnSoundModeChanged,
            EarbudsEventKind.LowBattery => settings.OpenPanelOnLowBattery,
            _ => false,
        };

        if (shouldOpen)
        {
            ShowQuickPanelForEvent();
        }
    }

    public void ShowSettingsWindow()
    {
        if (_earbuds is null || _settingsStore is null)
        {
            return;
        }

        if (_settingsWindow is null)
        {
            _settingsWindow = new SettingsWindow(_earbuds);
            _settingsWindow.Closed += (_, _) => _settingsWindow = null;
        }

        _settingsWindow.Activate();
    }

    public async void ExitApplication()
    {
        if (_exiting)
        {
            return;
        }
        _exiting = true;

        try
        {
            _updateCheckCts?.Cancel();
            _updateCheckCts?.Dispose();
            _updateCheckCts = null;

            if (_earbuds is not null)
            {
                _earbuds.StateChanged -= Earbuds_StateOrSettingsChanged;
                _earbuds.SettingsChanged -= Earbuds_StateOrSettingsChanged;
                _earbuds.EarbudsEventOccurred -= Earbuds_EarbudsEventOccurred;
            }

            if (_trayIcon is not null)
            {
                _trayIcon.PrimaryInvoked -= TrayIcon_PrimaryInvoked;
                _trayIcon.SecondaryInvoked -= TrayIcon_SecondaryInvoked;
                _trayIcon.Dispose();
                _trayIcon = null;
            }

            _settingsWindow?.Close();
            _quickPanel?.Close();

            if (_notifications is not null)
            {
                _notifications.OpenRequested -= Notifications_OpenRequested;
                _notifications.FallbackNotificationRequested -= Notifications_FallbackNotificationRequested;
                _notifications.Dispose();
                _notifications = null;
            }

            if (_earbuds is not null)
            {
                await _earbuds.DisposeAsync();
                _earbuds = null;
            }
            _dispatcherQueue = null;
        }
        finally
        {
            _singleInstanceMutex?.Dispose();
            _singleInstanceMutex = null;
            Current.Exit();
        }
    }

    private void Notifications_OpenRequested(object? sender, EventArgs e)
    {
        _quickPanel?.HidePanel();
        ShowSettingsWindow();
    }

    private void Notifications_FallbackNotificationRequested(string title, string message)
    {
        _trayIcon?.ShowNotification(title, message);
    }

    private async Task CheckForUpdatesAsync(CancellationToken cancellationToken)
    {
        try
        {
            // Let the tray icon and initial device connection settle before using the network.
            await Task.Delay(TimeSpan.FromSeconds(3), cancellationToken).ConfigureAwait(false);
            var result = await UpdateService.CheckAsync(cancellationToken).ConfigureAwait(false);
            LatestUpdateCheck = result;
            if (result.Succeeded && result.IsUpdateAvailable && result.LatestVersion is not null)
            {
                _dispatcherQueue?.TryEnqueue(() =>
                    _trayIcon?.ShowNotification(
                        "FitBuds Turbo 有新版本",
                        $"版本 {result.LatestVersion} 已发布，请在设置中查看下载地址。"));
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            System.Diagnostics.Debug.WriteLine($"FitBudsControl update check failed: {exception}");
        }
    }

    private void App_UnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        System.Diagnostics.Debug.WriteLine(e.Exception);
    }
}
