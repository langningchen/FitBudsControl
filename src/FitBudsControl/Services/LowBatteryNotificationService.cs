using System.Diagnostics;
using FitBudsControl.Models;
using Microsoft.UI.Dispatching;
using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;

namespace FitBudsControl.Services;

/// <summary>
/// Bridges passive F200 battery-state updates to native Windows app notifications.
/// Each battery source has an independent low threshold and notification latch.
/// </summary>
public sealed class LowBatteryNotificationService : IDisposable
{
    private readonly DispatcherQueue _dispatcher;
    private readonly EarbudsService _earbuds;
    private readonly AppNotificationManager _manager = AppNotificationManager.Default;

    private bool _registered;
    private bool _leftLatched;
    private bool _rightLatched;
    private bool _caseLatched;
    private bool _disposed;

    private bool _lastNotificationsEnabled;
    private int _lastLeftThreshold;
    private int _lastRightThreshold;
    private int _lastCaseThreshold;

    public LowBatteryNotificationService(DispatcherQueue dispatcher, EarbudsService earbuds)
    {
        _dispatcher = dispatcher;
        _earbuds = earbuds;

        var settings = earbuds.Settings;
        _lastNotificationsEnabled = settings.LowBatteryNotificationsEnabled;
        _lastLeftThreshold = settings.LeftLowBatteryThreshold;
        _lastRightThreshold = settings.RightLowBatteryThreshold;
        _lastCaseThreshold = settings.CaseLowBatteryThreshold;

        _earbuds.StateChanged += Earbuds_StateChanged;
        _earbuds.SettingsChanged += Earbuds_SettingsChanged;

        TryRegister();
    }

    public event EventHandler? OpenRequested;
    public event Action<string, string>? FallbackNotificationRequested;

    public bool IsAvailable => _registered;

    private void TryRegister()
    {
        try
        {
            if (!AppNotificationManager.IsSupported())
            {
                Debug.WriteLine("FitBudsControl: Windows App Notifications are not supported for this deployment.");
                _registered = false;
                return;
            }

            // For unpackaged Windows App SDK apps, Register() is the recommended path:
            // it creates the COM activation registration and obtains shell identity data.
            _manager.NotificationInvoked += Manager_NotificationInvoked;
            _manager.Register();
            _registered = true;

            if (_manager.Setting != AppNotificationSetting.Enabled)
            {
                Debug.WriteLine($"FitBudsControl: notifications are registered but blocked: {_manager.Setting}");
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"FitBudsControl: notification registration failed: {ex}");
            try
            {
                _manager.NotificationInvoked -= Manager_NotificationInvoked;
            }
            catch
            {
            }
            _registered = false;
        }
    }

    private void Manager_NotificationInvoked(AppNotificationManager sender, AppNotificationActivatedEventArgs args)
    {
        if (_dispatcher.HasThreadAccess)
        {
            OpenRequested?.Invoke(this, EventArgs.Empty);
        }
        else
        {
            _dispatcher.TryEnqueue(() => OpenRequested?.Invoke(this, EventArgs.Empty));
        }
    }

    private void Earbuds_SettingsChanged(object? sender, EventArgs e)
    {
        var settings = _earbuds.Settings;
        var state = _earbuds.State;

        if (!settings.LowBatteryNotificationsEnabled)
        {
            _leftLatched = false;
            _rightLatched = false;
            _caseLatched = false;
            RememberSettings(settings);
            return;
        }

        if (!_lastNotificationsEnabled)
        {
            // Turning notifications back on should immediately evaluate the current
            // confirmed battery state instead of silently latching it away.
            _leftLatched = false;
            _rightLatched = false;
            _caseLatched = false;
        }
        else
        {
            RearmForThresholdChange(state.LeftBattery, _lastLeftThreshold, settings.LeftLowBatteryThreshold, ref _leftLatched);
            RearmForThresholdChange(state.RightBattery, _lastRightThreshold, settings.RightLowBatteryThreshold, ref _rightLatched);
            RearmForThresholdChange(state.CaseBattery, _lastCaseThreshold, settings.CaseLowBatteryThreshold, ref _caseLatched);
        }

        // While the case is charging we suppress its low alert, but keep it re-armed
        // so unplugging a still-low case can notify immediately.
        if (state.IsCaseCharging)
        {
            _caseLatched = false;
        }

        RememberSettings(settings);
        EvaluateCurrentState();
    }

    private void RememberSettings(AppSettings settings)
    {
        _lastNotificationsEnabled = settings.LowBatteryNotificationsEnabled;
        _lastLeftThreshold = settings.LeftLowBatteryThreshold;
        _lastRightThreshold = settings.RightLowBatteryThreshold;
        _lastCaseThreshold = settings.CaseLowBatteryThreshold;
    }

    private static void RearmForThresholdChange(int? value, int oldThreshold, int newThreshold, ref bool latched)
    {
        if (oldThreshold == newThreshold)
        {
            return;
        }

        var wasLow = IsLow(value, oldThreshold);
        var isLow = IsLow(value, newThreshold);

        if (!isLow || (!wasLow && isLow))
        {
            latched = false;
        }
    }

    private void Earbuds_StateChanged(object? sender, EventArgs e) => EvaluateCurrentState();

    private void EvaluateCurrentState()
    {
        if (_disposed)
        {
            return;
        }

        var settings = _earbuds.Settings;
        var state = _earbuds.State;
        if (!settings.LowBatteryNotificationsEnabled || !state.IsConnected)
        {
            _leftLatched = false;
            _rightLatched = false;
            _caseLatched = false;
            return;
        }

        EvaluateBattery(
            sourceKey: "left",
            label: "左耳",
            value: state.LeftBattery,
            threshold: settings.LeftLowBatteryThreshold,
            ref _leftLatched);

        EvaluateBattery(
            sourceKey: "right",
            label: "右耳",
            value: state.RightBattery,
            threshold: settings.RightLowBatteryThreshold,
            ref _rightLatched);

        if (state.IsCaseCharging)
        {
            _caseLatched = false;
        }
        else
        {
            EvaluateBattery(
                sourceKey: "case",
                label: "耳机盒",
                value: state.CaseBattery,
                threshold: settings.CaseLowBatteryThreshold,
                ref _caseLatched);
        }
    }

    private void EvaluateBattery(
        string sourceKey,
        string label,
        int? value,
        int threshold,
        ref bool latched)
    {
        threshold = Math.Clamp(threshold, 1, 100);
        if (value is null)
        {
            return;
        }

        if (value.Value > threshold)
        {
            latched = false;
            return;
        }

        if (latched)
        {
            return;
        }

        if (ShowLowBatteryNotification(sourceKey, label, value.Value, threshold))
        {
            latched = true;
        }
    }

    private bool ShowLowBatteryNotification(string sourceKey, string label, int value, int threshold)
    {
        var title = "FitBuds Turbo 电量提醒";
        var message = $"{label}电量不足 请及时充电";

        if (!_registered)
        {
            // AppNotificationManager can be unsupported in a fully self-contained
            // Windows App SDK deployment when the Singleton package is absent. Fall
            // back to a native Shell notification-area message instead of silently
            // dropping the user's low-battery alert.
            // Keep this as a plain best-effort fallback only when App Notifications
            // are unavailable for the current deployment.
            FallbackNotificationRequested?.Invoke(title, message);
            return true;
        }

        try
        {
            // Re-check the OS-level notification switch in case the user changed it
            // after app startup. Respect an explicit Windows notification block.
            if (_manager.Setting != AppNotificationSetting.Enabled)
            {
                Debug.WriteLine($"FitBudsControl: notification suppressed by OS setting: {_manager.Setting}");
                return false;
            }

            var builder = new AppNotificationBuilder()
                .AddArgument("source", sourceKey)
                .AddText(title)
                .AddText(message);


            var notification = builder.BuildNotification();
            notification.Priority = AppNotificationPriority.Default;

            _manager.Show(notification);
            return true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"FitBudsControl: failed to show low-battery notification: {ex}");
            return false;
        }
    }

    private static bool IsLow(int? value, int threshold)
        => value is not null && value.Value <= Math.Clamp(threshold, 1, 100);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;

        _earbuds.StateChanged -= Earbuds_StateChanged;
        _earbuds.SettingsChanged -= Earbuds_SettingsChanged;

        if (_registered)
        {
            try
            {
                _manager.NotificationInvoked -= Manager_NotificationInvoked;
                _manager.Unregister();
            }
            catch
            {
            }
            _registered = false;
        }
    }
}
