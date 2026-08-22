using System.Collections.ObjectModel;
using System.Globalization;
using System.Runtime.InteropServices;
using Windows.ApplicationModel.DataTransfer;
using Windows.UI.Text;
using FitBudsControl.Bluetooth;
using FitBudsControl.Interop;
using FitBudsControl.Models;
using FitBudsControl.Services;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace FitBudsControl.Windows;

public sealed class ProtocolLogRow
{
    public required string TimeText { get; init; }
    public required string Arrow { get; init; }
    public required string FrameText { get; init; }
    public required string Description { get; init; }
    public required string DirectionLabel { get; init; }
    public required string CopyText { get; init; }
    public required Brush DirectionBrush { get; init; }
    public required Brush DirectionBackgroundBrush { get; init; }
    public required FontWeight EmphasisWeight { get; init; }
}

public sealed class LegacyCommandRow
{
    public required LegacyEdifierCommandPreset Preset { get; init; }
    public required Brush StatusBrush { get; init; }
    public string DisplayTitle => Preset.DisplayTitle;
    public string CommandNumber => ((Preset.Category >> 8) & 0xFF).ToString("X2", CultureInfo.InvariantCulture);
}

public sealed partial class SettingsWindow : Window
{
    private const int MinimumWidthDip = 780;
    private const int MinimumHeightDip = 540;

    private enum BatteryChannel { Left, Right, Case }
    private enum DeveloperSubPage { Traffic, Extractor, LegacyCommands }

    private readonly EarbudsService _service;
    private readonly Dictionary<byte, ComboBox> _touchCombos;
    private readonly CheckBox[] _cycleChecks;
    private readonly NumberBox[] _equalizerFrequencyBoxes;
    private readonly NumberBox[] _equalizerGainBoxes;
    private readonly NumberBox[] _equalizerQBoxes;
    private readonly NativeMethods.WindowProc _sizeConstraintProc;
    private readonly nint _previousWindowProc;
    private readonly ObservableCollection<ProtocolLogRow> _liveTrafficRows = new();
    private readonly ObservableCollection<ProtocolLogRow> _extractedTrafficRows = new();
    private readonly ObservableCollection<ProtocolLogRow> _legacyResponseRows = new();
    private readonly Brush _toDeviceLogBrush = new SolidColorBrush(global::Windows.UI.Color.FromArgb(255, 226, 88, 34));
    private readonly Brush _fromDeviceLogBrush = new SolidColorBrush(global::Windows.UI.Color.FromArgb(255, 0, 120, 212));
    private readonly Brush _toDeviceLogBackgroundBrush = new SolidColorBrush(global::Windows.UI.Color.FromArgb(40, 226, 88, 34));
    private readonly Brush _fromDeviceLogBackgroundBrush = new SolidColorBrush(global::Windows.UI.Color.FromArgb(40, 0, 120, 212));
    private readonly Brush _legacyConfirmedBrush = new SolidColorBrush(global::Windows.UI.Color.FromArgb(255, 16, 124, 16));
    private readonly Brush _legacyIntegratedBrush = new SolidColorBrush(global::Windows.UI.Color.FromArgb(255, 202, 80, 16));
    private readonly Brush _legacyExistingFamilyBrush = new SolidColorBrush(global::Windows.UI.Color.FromArgb(255, 0, 120, 212));
    private readonly Brush _legacyCandidateBrush = new SolidColorBrush(global::Windows.UI.Color.FromArgb(255, 118, 118, 118));
    private CancellationTokenSource? _infoBarHideCts;
    private bool _initialized;
    private bool _updatingUi;
    private bool _busy;
    private bool _promptDirty;
    private bool _soundQualityDirty;
    private bool _equalizerDirty;
    private bool _timerDirty;
    private bool _touchDirty;
    private bool _cycleDirty;
    private bool _legacyEditorLoading;
    private bool _legacyListening;
    private int _developerVersionTapCount;

    public SettingsWindow(EarbudsService service)
    {
        InitializeComponent();
        var app = (App)Application.Current;
        DeveloperNavigationItem.Visibility = app.DeveloperModeUnlocked ? Visibility.Visible : Visibility.Collapsed;
        VersionTextBlock.Text = $"版本 {GetDisplayVersion()}";
        LiveTrafficListView.ItemsSource = _liveTrafficRows;
        ExtractedTrafficListView.ItemsSource = _extractedTrafficRows;
        LegacyResponseListView.ItemsSource = _legacyResponseRows;
        _service = service;
        _service.StateChanged += Service_StateChanged;
        _service.TrafficObserved += Service_TrafficObserved;
        LegacyCommandListView.ItemsSource = LegacyEdifierCommandCatalog.All
            .OrderBy(preset => preset.Category)
            .Select(preset => new LegacyCommandRow
            {
                Preset = preset,
                StatusBrush = GetLegacyStatusBrush(preset.Confidence),
            })
            .ToArray();
        LegacyCommandListView.SelectedIndex = LegacyEdifierCommandCatalog.All.Count > 0 ? 0 : -1;

        _touchCombos = new Dictionary<byte, ComboBox>
        {
            [0x01] = Touch01,
            [0x03] = Touch03,
            [0x08] = Touch08,
            [0x02] = Touch02,
            [0x04] = Touch04,
            [0x09] = Touch09,
        };
        _cycleChecks = [CycleDeep, CycleComfort, CycleWide, CycleWind, CycleAmbient, CycleOff];
        _equalizerFrequencyBoxes = [Eq0Frequency, Eq1Frequency, Eq2Frequency, Eq3Frequency];
        _equalizerGainBoxes = [Eq0Gain, Eq1Gain, Eq2Gain, Eq3Gain];
        _equalizerQBoxes = [Eq0Q, Eq1Q, Eq2Q, Eq3Q];

        Title = "FitBuds Turbo 设置";
        SystemBackdrop = new MicaBackdrop { Kind = MicaKind.BaseAlt };
        var hwnd = NativeMethods.GetWindowHandle(this);
        var appWindow = NativeMethods.GetAppWindow(this);
        appWindow.Resize(new global::Windows.Graphics.SizeInt32(
            NativeMethods.DipToPixels(hwnd, 920),
            NativeMethods.DipToPixels(hwnd, 680)));
        appWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico"));
        NativeMethods.EnableRoundedCorners(this);

        _sizeConstraintProc = SizeConstraintWindowProc;
        _previousWindowProc = NativeMethods.SetWindowLongPtr(
            hwnd,
            NativeMethods.GwlpWndProc,
            Marshal.GetFunctionPointerForDelegate(_sizeConstraintProc));

        Closed += (_, _) =>
        {
            var infoBarHide = _infoBarHideCts;
            _infoBarHideCts = null;
            infoBarHide?.Cancel();
            _service.StateChanged -= Service_StateChanged;
            _service.TrafficObserved -= Service_TrafficObserved;
            GC.KeepAlive(_sizeConstraintProc);
        };
    }

    private nint SizeConstraintWindowProc(nint hwnd, uint message, nuint wParam, nint lParam)
    {
        var result = _previousWindowProc != 0
            ? NativeMethods.CallWindowProc(_previousWindowProc, hwnd, message, wParam, lParam)
            : 0;

        if (message == NativeMethods.WmGetMinMaxInfo && lParam != 0)
        {
            var info = Marshal.PtrToStructure<NativeMethods.MinMaxInfo>(lParam);
            info.MinTrackSize.X = Math.Max(info.MinTrackSize.X, NativeMethods.DipToPixels(hwnd, MinimumWidthDip));
            info.MinTrackSize.Y = Math.Max(info.MinTrackSize.Y, NativeMethods.DipToPixels(hwnd, MinimumHeightDip));
            Marshal.StructureToPtr(info, lParam, false);
        }

        return result;
    }

    private async void Root_Loaded(object sender, RoutedEventArgs e)
    {
        if (_initialized)
        {
            return;
        }

        PopulateOptions();
        LoadSettingsIntoUi();

        if (Navigation.MenuItems.FirstOrDefault() is NavigationViewItem first)
        {
            Navigation.SelectedItem = first;
        }

        LoadTrafficHistory();
        UpdateUi();
        _initialized = true;
        await RefreshPageOnOpenAsync("device");
    }

    private void PopulateOptions()
    {
        var soundQualities = Enum.GetValues<SoundQualityMode>()
            .Select(value => new SoundQualityOption(value, FitBudsProtocol.SoundQualityLabels[value]))
            .ToArray();
        SoundQualityComboBox.DisplayMemberPath = nameof(SoundQualityOption.Label);
        SoundQualityComboBox.ItemsSource = soundQualities;

        var functions = Enum.GetValues<TouchFunction>()
            .Select(value => new TouchFunctionOption(value, FitBudsProtocol.TouchFunctionLabels[value]))
            .ToArray();

        foreach (var combo in _touchCombos.Values)
        {
            combo.DisplayMemberPath = nameof(TouchFunctionOption.Label);
            combo.ItemsSource = functions;
        }
    }

    private void LoadSettingsIntoUi()
    {
        var settings = _service.Settings;
        _updatingUi = true;
        try
        {
            MacTextBox.Text = BluetoothAddress.TryParse(settings.BluetoothMac, out var address)
                ? BluetoothAddress.Format(address)
                : settings.BluetoothMac.ToUpperInvariant();
            ChannelNumberBox.Value = settings.RfcommChannel;
            ConnectTimeoutNumberBox.Value = settings.ConnectTimeoutMs;
            TimeoutNumberBox.Value = settings.RequestTimeoutMs;
            AutoConnectToggle.IsOn = settings.AutoConnectOnOpen;
            ReconnectIntervalNumberBox.Value = settings.ReconnectIntervalSeconds;
            LowBatteryNotificationsToggle.IsOn = settings.LowBatteryNotificationsEnabled;
            LeftLowBatteryNumberBox.Value = settings.LeftLowBatteryThreshold;
            RightLowBatteryNumberBox.Value = settings.RightLowBatteryThreshold;
            CaseLowBatteryNumberBox.Value = settings.CaseLowBatteryThreshold;
            SetLowBatteryOptionsEnabled(settings.LowBatteryNotificationsEnabled);

            StartWithWindowsToggle.IsOn = settings.StartWithWindows;
            AlwaysBlueTrayIconToggle.IsOn = settings.AlwaysUseBlueTrayIcon;
            AutoOpenEventsToggle.IsOn = settings.AutoOpenPanelOnEvents;
            OpenOnConnectedCheckBox.IsChecked = settings.OpenPanelOnConnected;
            OpenOnDisconnectedCheckBox.IsChecked = settings.OpenPanelOnDisconnected;
            OpenOnNoiseModeChangedCheckBox.IsChecked = settings.OpenPanelOnNoiseModeChanged;
            OpenOnSoundModeChangedCheckBox.IsChecked = settings.OpenPanelOnSoundModeChanged;
            OpenOnLowBatteryCheckBox.IsChecked = settings.OpenPanelOnLowBattery;
            SetAutoOpenEventOptionsEnabled(settings.AutoOpenPanelOnEvents);
        }
        finally
        {
            _updatingUi = false;
        }
    }

    private async void Navigation_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        var tag = (args.SelectedItem as NavigationViewItem)?.Tag as string ?? "device";
        if (tag == "exit")
        {
            ((App)Application.Current).ExitApplication();
            return;
        }

        if (tag == "developer" && !((App)Application.Current).DeveloperModeUnlocked)
        {
            return;
        }

        DevicePanel.Visibility = tag == "device" ? Visibility.Visible : Visibility.Collapsed;
        SoundPanel.Visibility = tag == "sound" ? Visibility.Visible : Visibility.Collapsed;
        ApplicationPanel.Visibility = tag == "application" ? Visibility.Visible : Visibility.Collapsed;
        DeveloperPanel.Visibility = tag == "developer" ? Visibility.Visible : Visibility.Collapsed;
        AboutPanel.Visibility = tag == "about" ? Visibility.Visible : Visibility.Collapsed;

        if (tag == "developer")
        {
            SetDeveloperSubPage(DeveloperSubPage.Traffic);
            ScrollLiveTrafficToBottom();
        }

        if (_initialized && _service.IsConnected && tag is "device" or "sound")
        {
            await RefreshPageOnOpenAsync(tag);
        }
    }

    private async Task RefreshPageOnOpenAsync(string tag)
    {
        if (_busy || !_service.IsConnected)
        {
            return;
        }

        SetBusy(true);
        try
        {
            if (tag == "device")
            {
                await _service.RefreshDevicePageAsync();
            }
            else if (tag == "sound")
            {
                await _service.RefreshSoundPageAsync();
            }
        }
        catch
        {
            ShowInfo("没有读到最新状态，请稍后再试", InfoBarSeverity.Warning);
        }
        finally
        {
            SetBusy(false);
            UpdateUi();
        }
    }

    private void LoadTrafficHistory()
    {
        _liveTrafficRows.Clear();
        foreach (var entry in _service.GetTrafficHistory())
        {
            _liveTrafficRows.Add(CreateLogRow(ProtocolLogFormatter.BuildLiveDisplayLine(entry)));
        }
        ScrollLiveTrafficToBottom();
    }

    private void Service_TrafficObserved(ProtocolTrafficEntry entry)
    {
        if (!DispatcherQueue.HasThreadAccess)
        {
            DispatcherQueue.TryEnqueue(() => Service_TrafficObserved(entry));
            return;
        }

        var row = CreateLogRow(ProtocolLogFormatter.BuildLiveDisplayLine(entry));
        _liveTrafficRows.Add(row);
        if (_legacyListening && entry.Direction == ProtocolTrafficDirection.FromDevice)
        {
            _legacyResponseRows.Add(row);
            ScrollLegacyResponsesToBottom();
        }

        if (DeveloperPanel.Visibility == Visibility.Visible && DeveloperTrafficSubPanel.Visibility == Visibility.Visible)
        {
            ScrollLiveTrafficToBottom();
        }
    }

    private ProtocolLogRow CreateLogRow(ProtocolLogDisplayLine line)
    {
        var toDevice = line.Direction == ProtocolTrafficDirection.ToDevice;
        var unknown = string.IsNullOrWhiteSpace(line.Description) ||
            line.Description.Contains("未识别", StringComparison.Ordinal);
        var description = unknown && string.IsNullOrWhiteSpace(line.Description)
            ? "未识别的数据"
            : line.Description;
        var copyText = string.Join(
            " ",
            new[] { line.TimeText, line.Arrow, line.FrameText, description }
                .Where(value => !string.IsNullOrWhiteSpace(value)));

        return new ProtocolLogRow
        {
            TimeText = line.TimeText,
            Arrow = line.Arrow,
            FrameText = line.FrameText,
            Description = description,
            DirectionLabel = toDevice ? "发送" : "接收",
            CopyText = copyText,
            DirectionBrush = toDevice ? _toDeviceLogBrush : _fromDeviceLogBrush,
            DirectionBackgroundBrush = toDevice ? _toDeviceLogBackgroundBrush : _fromDeviceLogBackgroundBrush,
            EmphasisWeight = unknown ? Microsoft.UI.Text.FontWeights.Bold : Microsoft.UI.Text.FontWeights.Normal,
        };
    }

    private void ScrollLiveTrafficToBottom()
    {
        if (_liveTrafficRows.Count == 0)
        {
            return;
        }

        var last = _liveTrafficRows[_liveTrafficRows.Count - 1];
        DispatcherQueue.TryEnqueue(() => LiveTrafficListView.ScrollIntoView(last, ScrollIntoViewAlignment.Leading));
    }

    private void SetDeveloperSubPage(DeveloperSubPage page)
    {
        var showTraffic = page == DeveloperSubPage.Traffic;
        var showExtractor = page == DeveloperSubPage.Extractor;
        var showLegacy = page == DeveloperSubPage.LegacyCommands;

        DeveloperTrafficTabButton.IsChecked = showTraffic;
        DeveloperExtractorTabButton.IsChecked = showExtractor;
        DeveloperLegacyTabButton.IsChecked = showLegacy;
        DeveloperTrafficSubPanel.Visibility = showTraffic ? Visibility.Visible : Visibility.Collapsed;
        DeveloperExtractorSubPanel.Visibility = showExtractor ? Visibility.Visible : Visibility.Collapsed;
        DeveloperLegacySubPanel.Visibility = showLegacy ? Visibility.Visible : Visibility.Collapsed;

        if (showTraffic)
        {
            ScrollLiveTrafficToBottom();
        }
    }

    private void DeveloperTrafficTab_Click(object sender, RoutedEventArgs e)
        => SetDeveloperSubPage(DeveloperSubPage.Traffic);

    private void DeveloperExtractorTab_Click(object sender, RoutedEventArgs e)
        => SetDeveloperSubPage(DeveloperSubPage.Extractor);

    private void DeveloperLegacyTab_Click(object sender, RoutedEventArgs e)
        => SetDeveloperSubPage(DeveloperSubPage.LegacyCommands);

    private void LegacyCommandListView_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_legacyListening)
        {
            StopLegacyListening("已停止记录，因为你切换了指令");
        }
        LoadSelectedLegacyCommand();
    }

    private Brush GetLegacyStatusBrush(LegacyCommandConfidence confidence)
        => confidence switch
        {
            LegacyCommandConfidence.Confirmed => _legacyConfirmedBrush,
            LegacyCommandConfidence.Integrated => _legacyIntegratedBrush,
            LegacyCommandConfidence.ExistingFamily => _legacyExistingFamilyBrush,
            _ => _legacyCandidateBrush,
        };

    private LegacyEdifierCommandPreset? GetSelectedLegacyPreset()
        => (LegacyCommandListView.SelectedItem as LegacyCommandRow)?.Preset;

    private void LoadSelectedLegacyCommand()
    {
        if (GetSelectedLegacyPreset() is not LegacyEdifierCommandPreset preset)
        {
            return;
        }

        _legacyEditorLoading = true;
        try
        {
            LegacyCommandTitleText.Text = preset.DisplayTitle;
            LegacyCommandTitleText.Foreground = GetLegacyStatusBrush(preset.Confidence);
            LegacyCategoryTextBox.Text = ((preset.Category >> 8) & 0xFF).ToString("X2", CultureInfo.InvariantCulture);
            LegacySubcommandTextBox.Text = (preset.Category & 0xFF).ToString("X2", CultureInfo.InvariantCulture);
            LegacyPayloadTextBox.Text = preset.PayloadHex;
            LegacyCommandDescriptionText.Text = string.Join(
                Environment.NewLine,
                new[] { preset.Description, "第二个值通常保持 00。", preset.PayloadHint }
                    .Where(value => !string.IsNullOrWhiteSpace(value)));
            LegacyDangerousCommandToggle.IsOn = false;
            LegacyDangerousCommandToggle.Visibility = preset.IsDangerous ? Visibility.Visible : Visibility.Collapsed;
        }
        finally
        {
            _legacyEditorLoading = false;
        }

        UpdateLegacyCommandPreview();
    }

    private void LegacyCommandEditor_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_legacyEditorLoading)
        {
            UpdateLegacyCommandPreview();
        }
    }

    private void LegacyDangerousCommandToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (!_legacyEditorLoading)
        {
            UpdateLegacyCommandPreview();
        }
    }

    private void ResetLegacyCommand_Click(object sender, RoutedEventArgs e)
        => LoadSelectedLegacyCommand();

    private void UpdateLegacyCommandPreview()
    {
        var preset = GetSelectedLegacyPreset();
        if (!TryBuildLegacyCommand(out _, out _, out var frame, out var error))
        {
            LegacyFramePreviewTextBox.Text = string.Empty;
            LegacyValidationText.Text = error;
            SendLegacyCommandButton.IsEnabled = false;
            return;
        }

        LegacyFramePreviewTextBox.Text = FormatHex(frame);
        if (_legacyListening)
        {
            LegacyValidationText.Text = "正在记录耳机发来的数据；点击“停止”结束。";
            SendLegacyCommandButton.IsEnabled = true;
            return;
        }

        var locked = preset?.IsDangerous == true && !LegacyDangerousCommandToggle.IsOn;
        LegacyValidationText.Text = locked
            ? "这项操作可能让设备断开、重置或重新配对，请先打开上方的允许开关。"
            : "内容可以发送。耳机有返回并不一定代表这项功能有效，请结合实际变化判断。";
        SendLegacyCommandButton.IsEnabled = !locked;
    }

    private bool TryBuildLegacyCommand(
        out ushort category,
        out byte[] payload,
        out byte[] frame,
        out string error)
    {
        category = 0;
        payload = Array.Empty<byte>();
        frame = Array.Empty<byte>();
        error = string.Empty;

        var commandText = LegacyCategoryTextBox.Text.Trim();
        var secondText = LegacySubcommandTextBox.Text.Trim();
        if (commandText.Length != 2 ||
            !byte.TryParse(commandText, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var command))
        {
            error = "指令编号填写 2 个字符，例如 D5。";
            return false;
        }
        if (secondText.Length != 2 ||
            !byte.TryParse(secondText, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var second))
        {
            error = "第二个值填写 2 个字符；普通指令通常是 00。";
            return false;
        }
        category = (ushort)((command << 8) | second);

        try
        {
            payload = ParseHexBytes(LegacyPayloadTextBox.Text);
            frame = FitBudsFrameCodec.BuildPacket(category, payload);
            return true;
        }
        catch (FormatException ex)
        {
            error = ex.Message;
            return false;
        }
        catch (ArgumentOutOfRangeException ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private static byte[] ParseHexBytes(string text)
    {
        var normalized = text
            .Replace("0x", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace(" ", string.Empty)
            .Replace("\t", string.Empty)
            .Replace("\r", string.Empty)
            .Replace("\n", string.Empty)
            .Replace("-", string.Empty)
            .Replace(":", string.Empty)
            .Replace(",", string.Empty);

        if (normalized.Length == 0)
        {
            return Array.Empty<byte>();
        }

        if ((normalized.Length & 1) != 0 || normalized.Any(ch => !Uri.IsHexDigit(ch)))
        {
            throw new FormatException("要发送的内容需要按两个字符一组填写，例如 02 或 00 1E。");
        }

        return Convert.FromHexString(normalized);
    }

    private static string FormatHex(IEnumerable<byte> bytes)
        => string.Join(" ", bytes.Select(value => value.ToString("X2", CultureInfo.InvariantCulture)));

    private void CopyLegacyFrame_Click(object sender, RoutedEventArgs e)
    {
        if (!TryBuildLegacyCommand(out _, out _, out var frame, out var error))
        {
            ShowInfo(error, InfoBarSeverity.Warning);
            return;
        }

        var package = new DataPackage();
        package.SetText(FormatHex(frame));
        Clipboard.SetContent(package);
        ShowInfo("发送内容已复制", InfoBarSeverity.Success);
    }

    private async void SendLegacyCommand_Click(object sender, RoutedEventArgs e)
    {
        if (_legacyListening)
        {
            StopLegacyListening("已停止记录");
            return;
        }

        var preset = GetSelectedLegacyPreset();
        if (preset?.IsDangerous == true && !LegacyDangerousCommandToggle.IsOn)
        {
            ShowInfo("请先允许发送这项设备操作", InfoBarSeverity.Warning);
            return;
        }

        if (!TryBuildLegacyCommand(out var category, out var payload, out _, out var error))
        {
            ShowInfo(error, InfoBarSeverity.Warning);
            return;
        }

        StartLegacyListening();
        SendLegacyCommandButton.IsEnabled = false;
        try
        {
            var result = await _service.SendDeveloperCommandAsync(category, payload);
            if (!result.Success)
            {
                StopLegacyListening("发送失败，没有继续记录");
                ShowInfo(result.Detail, InfoBarSeverity.Error);
                return;
            }

            LegacyResponseStateText.Text = "已发送，正在记录耳机返回和主动发来的数据；点击“停止”结束";
            LegacyValidationText.Text = "正在记录耳机发来的数据；点击“停止”结束。";
        }
        catch
        {
            StopLegacyListening("发送失败，没有继续记录");
            ShowInfo("发送失败，请检查连接后重试", InfoBarSeverity.Error);
        }
        finally
        {
            SendLegacyCommandButton.IsEnabled = true;
        }
    }

    private void StartLegacyListening()
    {
        _legacyResponseRows.Clear();
        _legacyListening = true;
        SendLegacyCommandButton.Content = "停止";
        LegacyResponseStateText.Text = "正在等待耳机返回数据…";
        LegacyCommandListView.IsEnabled = false;
        LegacyCategoryTextBox.IsEnabled = false;
        LegacySubcommandTextBox.IsEnabled = false;
        LegacyPayloadTextBox.IsEnabled = false;
        LegacyDangerousCommandToggle.IsEnabled = false;
        ResetLegacyCommandButton.IsEnabled = false;
        LegacyValidationText.Text = "正在记录耳机发来的数据；点击“停止”结束。";
    }

    private void StopLegacyListening(string message)
    {
        _legacyListening = false;
        SendLegacyCommandButton.Content = "发送";
        LegacyResponseStateText.Text = message;
        LegacyCommandListView.IsEnabled = true;
        LegacyCategoryTextBox.IsEnabled = true;
        LegacySubcommandTextBox.IsEnabled = true;
        LegacyPayloadTextBox.IsEnabled = true;
        LegacyDangerousCommandToggle.IsEnabled = true;
        ResetLegacyCommandButton.IsEnabled = true;
        UpdateLegacyCommandPreview();
    }

    private void ScrollLegacyResponsesToBottom()
    {
        if (_legacyResponseRows.Count == 0)
        {
            return;
        }

        var last = _legacyResponseRows[_legacyResponseRows.Count - 1];
        DispatcherQueue.TryEnqueue(() => LegacyResponseListView.ScrollIntoView(last, ScrollIntoViewAlignment.Leading));
    }

    private void CopySelectedLegacyResponses_Click(object sender, RoutedEventArgs e)
        => CopySelectedLogRows(LegacyResponseListView);

    private void ClearLegacyResponses_Click(object sender, RoutedEventArgs e)
    {
        _legacyResponseRows.Clear();
        LegacyResponseStateText.Text = _legacyListening
            ? "正在记录耳机返回和主动发来的数据…"
            : "记录已清空；再次发送后会从头记录";
    }

    private void ClearTrafficLog_Click(object sender, RoutedEventArgs e)
    {
        _service.ClearTrafficHistory();
        _liveTrafficRows.Clear();
        ShowInfo("通讯日志已清空", InfoBarSeverity.Success);
    }

    private void SelectAllTraffic_Click(object sender, RoutedEventArgs e)
        => LiveTrafficListView.SelectAll();

    private void CopySelectedTraffic_Click(object sender, RoutedEventArgs e)
        => CopySelectedLogRows(LiveTrafficListView);

    private void SelectAllExtractedTraffic_Click(object sender, RoutedEventArgs e)
        => ExtractedTrafficListView.SelectAll();

    private void CopySelectedExtractedTraffic_Click(object sender, RoutedEventArgs e)
        => CopySelectedLogRows(ExtractedTrafficListView);

    private void CopySelectedLogRows(ListView listView)
    {
        var rows = listView.SelectedItems.Cast<ProtocolLogRow>().ToArray();
        if (rows.Length == 0)
        {
            ShowInfo("请先选择要复制的日志", InfoBarSeverity.Warning);
            return;
        }

        var package = new DataPackage();
        package.SetText(string.Join(Environment.NewLine, rows.Select(row => row.CopyText)));
        Clipboard.SetContent(package);
        ShowInfo($"已复制 {rows.Length} 条日志", InfoBarSeverity.Success);
    }

    private void ExtractTrafficLog_Click(object sender, RoutedEventArgs e)
        => ExtractTrafficFromText(RawLogInputTextBox.Text);

    private async void ExtractTrafficFromClipboard_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var content = Clipboard.GetContent();
            if (!content.Contains(StandardDataFormats.Text))
            {
                ShowInfo("剪贴板里没有文字", InfoBarSeverity.Warning);
                return;
            }

            var text = await content.GetTextAsync();
            RawLogInputTextBox.Text = text;
            ExtractTrafficFromText(text);
        }
        catch
        {
            ShowInfo("无法读取剪贴板，请稍后重试", InfoBarSeverity.Error);
        }
    }

    private void ExtractTrafficFromText(string text)
    {
        var frames = ProtocolLogFormatter.ExtractFrames(text);
        _extractedTrafficRows.Clear();
        foreach (var frame in frames)
        {
            _extractedTrafficRows.Add(CreateLogRow(ProtocolLogFormatter.BuildExtractedDisplayLine(frame)));
        }

        ShowInfo(
            frames.Count == 0 ? "没有找到可提取的数据" : $"已提取 {frames.Count} 条数据",
            frames.Count == 0 ? InfoBarSeverity.Warning : InfoBarSeverity.Success);
    }

    private void LowBatteryNotifications_Toggled(object sender, RoutedEventArgs e)
    {
        if (!_initialized || _updatingUi)
        {
            return;
        }

        SetLowBatteryOptionsEnabled(LowBatteryNotificationsToggle.IsOn);
    }

    private void SetLowBatteryOptionsEnabled(bool enabled)
    {
        LeftLowBatteryNumberBox.IsEnabled = enabled;
        RightLowBatteryNumberBox.IsEnabled = enabled;
        CaseLowBatteryNumberBox.IsEnabled = enabled;
        LowBatteryOptionsPanel.Opacity = enabled ? 1.0 : 0.45;
    }

    private static int ReadThreshold(NumberBox box, int fallback)
        => Math.Clamp((int)Math.Round(double.IsNaN(box.Value) ? fallback : box.Value), 1, 100);

    private void SaveLowBattery_Click(object sender, RoutedEventArgs e)
    {
        var settings = _service.Settings;
        settings.LowBatteryNotificationsEnabled = LowBatteryNotificationsToggle.IsOn;
        settings.LeftLowBatteryThreshold = ReadThreshold(LeftLowBatteryNumberBox, settings.LeftLowBatteryThreshold);
        settings.RightLowBatteryThreshold = ReadThreshold(RightLowBatteryNumberBox, settings.RightLowBatteryThreshold);
        settings.CaseLowBatteryThreshold = ReadThreshold(CaseLowBatteryNumberBox, settings.CaseLowBatteryThreshold);
        _service.SaveSettings(settings);
        SetLowBatteryOptionsEnabled(settings.LowBatteryNotificationsEnabled);
        ShowInfo("低电量提醒已保存", InfoBarSeverity.Success);
    }

    private void SaveConnectionPreferences_Click(object sender, RoutedEventArgs e)
    {
        var settings = _service.Settings;
        settings.AutoConnectOnOpen = AutoConnectToggle.IsOn;
        settings.ReconnectIntervalSeconds = Math.Clamp(
            (int)Math.Round(double.IsNaN(ReconnectIntervalNumberBox.Value)
                ? settings.ReconnectIntervalSeconds
                : ReconnectIntervalNumberBox.Value),
            1,
            3600);
        ReconnectIntervalNumberBox.Value = settings.ReconnectIntervalSeconds;
        _service.SaveSettings(settings);
        _service.RescheduleReconnect();
        ShowInfo("连接偏好已保存", InfoBarSeverity.Success);
    }

    private void AutoOpenEventsToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_updatingUi)
        {
            return;
        }

        SetAutoOpenEventOptionsEnabled(AutoOpenEventsToggle.IsOn);
    }

    private void SetAutoOpenEventOptionsEnabled(bool enabled)
    {
        foreach (var checkBox in new[]
                 {
                     OpenOnConnectedCheckBox,
                     OpenOnDisconnectedCheckBox,
                     OpenOnNoiseModeChangedCheckBox,
                     OpenOnSoundModeChangedCheckBox,
                     OpenOnLowBatteryCheckBox,
                 })
        {
            checkBox.IsEnabled = enabled;
        }

        AutoOpenEventOptionsPanel.Opacity = enabled ? 1.0 : 0.45;
    }

    private void SaveStartup_Click(object sender, RoutedEventArgs e)
    {
        if (!StartupService.TryApply(StartWithWindowsToggle.IsOn, out var error))
        {
            StartWithWindowsToggle.IsOn = _service.Settings.StartWithWindows;
            ShowInfo(error, InfoBarSeverity.Error);
            return;
        }

        var settings = _service.Settings;
        settings.StartWithWindows = StartWithWindowsToggle.IsOn;
        _service.SaveSettings(settings);
        ShowInfo(settings.StartWithWindows ? "开机自动启动已开启" : "开机自动启动已关闭", InfoBarSeverity.Success);
    }

    private void SaveTrayIcon_Click(object sender, RoutedEventArgs e)
    {
        var settings = _service.Settings;
        settings.AlwaysUseBlueTrayIcon = AlwaysBlueTrayIconToggle.IsOn;
        _service.SaveSettings(settings);
        ShowInfo(settings.AlwaysUseBlueTrayIcon ? "任务栏图标将始终保持蓝色" : "任务栏图标将继续显示设备状态", InfoBarSeverity.Success);
    }

    private void SaveAutoOpenEvents_Click(object sender, RoutedEventArgs e)
    {
        var anySelected = OpenOnConnectedCheckBox.IsChecked == true ||
                          OpenOnDisconnectedCheckBox.IsChecked == true ||
                          OpenOnNoiseModeChangedCheckBox.IsChecked == true ||
                          OpenOnSoundModeChangedCheckBox.IsChecked == true ||
                          OpenOnLowBatteryCheckBox.IsChecked == true;
        if (AutoOpenEventsToggle.IsOn && !anySelected)
        {
            ShowInfo("请至少选择一个自动打开菜单的事件", InfoBarSeverity.Warning);
            return;
        }

        var settings = _service.Settings;
        settings.AutoOpenPanelOnEvents = AutoOpenEventsToggle.IsOn;
        settings.OpenPanelOnConnected = OpenOnConnectedCheckBox.IsChecked == true;
        settings.OpenPanelOnDisconnected = OpenOnDisconnectedCheckBox.IsChecked == true;
        settings.OpenPanelOnNoiseModeChanged = OpenOnNoiseModeChangedCheckBox.IsChecked == true;
        settings.OpenPanelOnSoundModeChanged = OpenOnSoundModeChangedCheckBox.IsChecked == true;
        settings.OpenPanelOnLowBattery = OpenOnLowBatteryCheckBox.IsChecked == true;
        _service.SaveSettings(settings);
        ShowInfo(settings.AutoOpenPanelOnEvents ? "自动打开菜单已保存" : "自动打开菜单已关闭", InfoBarSeverity.Success);
    }

    private async void SaveConnection_Click(object sender, RoutedEventArgs e)
    {
        if (!BluetoothAddress.TryParse(MacTextBox.Text, out var address))
        {
            ShowInfo("耳机地址格式不正确", InfoBarSeverity.Error);
            return;
        }

        var channel = (int)Math.Round(double.IsNaN(ChannelNumberBox.Value) ? 10 : ChannelNumberBox.Value);
        if (channel is < 1 or > 30)
        {
            ShowInfo("连接通道设置不正确", InfoBarSeverity.Error);
            return;
        }

        var connectTimeout = (int)Math.Round(double.IsNaN(ConnectTimeoutNumberBox.Value) ? 2000 : ConnectTimeoutNumberBox.Value);
        var responseTimeout = (int)Math.Round(double.IsNaN(TimeoutNumberBox.Value) ? 450 : TimeoutNumberBox.Value);
        var settings = _service.Settings;
        settings.BluetoothMac = BluetoothAddress.Format(address);
        settings.RfcommChannel = channel;
        settings.ConnectTimeoutMs = Math.Clamp(connectTimeout, 500, 10000);
        settings.RequestTimeoutMs = Math.Clamp(responseTimeout, 100, 5000);
        _service.SaveSettings(settings);

        MacTextBox.Text = settings.BluetoothMac;
        ConnectTimeoutNumberBox.Value = settings.ConnectTimeoutMs;
        TimeoutNumberBox.Value = settings.RequestTimeoutMs;

        if (!_service.IsConnected)
        {
            ShowInfo("连接设置已保存", InfoBarSeverity.Success);
            return;
        }

        SetBusy(true);
        try
        {
            await _service.DisconnectAsync();
            await _service.ConnectAsync();
            await _service.EnsureFullSnapshotAsync();
            ShowInfo("连接设置已保存并重新连接", InfoBarSeverity.Success);
        }
        catch
        {
            ShowInfo("连接设置已保存 重新连接失败", InfoBarSeverity.Error);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async Task<bool> ConfirmDeviceOperationAsync(string title, string message, string primaryButtonText)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = Root.XamlRoot,
            Title = title,
            Content = message,
            PrimaryButtonText = primaryButtonText,
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    private async void SaveDeviceName_Click(object sender, RoutedEventArgs e)
    {
        if (!_service.IsConnected)
        {
            ShowInfo("请先连接耳机", InfoBarSeverity.Warning);
            return;
        }

        var name = DeviceNameTextBox.Text.Trim();
        SetBusy(true);
        try
        {
            var result = await _service.SetDeviceNameAsync(name);
            ShowInfo(result.Detail, result.Success ? InfoBarSeverity.Success : InfoBarSeverity.Error);
        }
        finally
        {
            SetBusy(false);
            UpdateUi();
        }
    }

    private async void PowerOffDevice_Click(object sender, RoutedEventArgs e)
    {
        if (!await ConfirmDeviceOperationAsync("关闭耳机", "耳机会关机，并与程序断开连接。确定继续吗？", "关机"))
        {
            return;
        }
        await RunDeviceDepartureFromUiAsync(() => _service.PowerOffDeviceAsync());
    }

    private async void DisconnectDeviceCommand_Click(object sender, RoutedEventArgs e)
    {
        if (!await ConfirmDeviceOperationAsync("断开设备", "耳机会主动断开连接，程序不会马上自动连回去。确定继续吗？", "断开"))
        {
            return;
        }
        await RunDeviceDepartureFromUiAsync(() => _service.DisconnectDeviceCommandAsync());
    }

    private async void EnterPairing_Click(object sender, RoutedEventArgs e)
    {
        if (!await ConfirmDeviceOperationAsync("进入配对模式", "耳机会断开当前连接并进入配对状态。确定继续吗？", "进入配对"))
        {
            return;
        }
        await RunDeviceDepartureFromUiAsync(() => _service.EnterPairingAsync());
    }

    private async void ResetDevice_Click(object sender, RoutedEventArgs e)
    {
        if (!await ConfirmDeviceOperationAsync("重置设备", "重置可能恢复部分设备设置，并可能影响当前配对。确定继续吗？", "重置"))
        {
            return;
        }
        await RunDeviceDepartureFromUiAsync(() => _service.ResetDeviceAsync());
    }

    private async Task RunDeviceDepartureFromUiAsync(Func<Task<OperationResult>> action)
    {
        if (!_service.IsConnected)
        {
            ShowInfo("请先连接耳机", InfoBarSeverity.Warning);
            return;
        }

        SetBusy(true);
        try
        {
            var result = await action();
            ShowInfo(result.Detail, result.Success ? InfoBarSeverity.Success : InfoBarSeverity.Error);
        }
        finally
        {
            SetBusy(false);
            UpdateUi();
        }
    }

    private async void RefreshAll_Click(object sender, RoutedEventArgs e)
    {
        SetBusy(true);
        try
        {
            await _service.RefreshDevicePageAsync();
            UpdateUi();
            ShowInfo("设备信息已刷新", InfoBarSeverity.Success);
        }
        catch (TimeoutException)
        {
            ShowInfo("没有读到最新状态，请稍后再试", InfoBarSeverity.Error);
        }
        catch
        {
            ShowInfo("设备信息刷新失败", InfoBarSeverity.Error);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void PromptSetting_Changed(object sender, RoutedEventArgs e)
    {
        if (_initialized && !_updatingUi)
        {
            _promptDirty = true;
        }
    }

    private void PromptVolumeSlider_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        PromptVolumeText.Text = ((int)Math.Round(e.NewValue)).ToString();
        if (_initialized && !_updatingUi)
        {
            _promptDirty = true;
        }
    }

    private async void SavePrompt_Click(object sender, RoutedEventArgs e)
    {
        if (!_service.IsConnected)
        {
            ShowInfo("请先连接耳机", InfoBarSeverity.Warning);
            return;
        }

        var volume = Math.Clamp((int)Math.Round(PromptVolumeSlider.Value), 0, 15);
        SetBusy(true);
        try
        {
            var enabledResult = await _service.SetPromptEnabledAsync(PromptSwitch.IsOn);
            if (!enabledResult.Success)
            {
                ShowInfo("提示音设置未保存", InfoBarSeverity.Error);
                return;
            }

            var volumeResult = await _service.SetPromptVolumeAsync(volume);
            if (!volumeResult.Success)
            {
                ShowInfo("提示音设置未保存", InfoBarSeverity.Error);
                return;
            }

            _promptDirty = false;
            ShowInfo("提示音设置已保存", InfoBarSeverity.Success);
        }
        catch (TimeoutException)
        {
            ShowInfo("操作超时", InfoBarSeverity.Error);
        }
        catch
        {
            ShowInfo("提示音设置未保存", InfoBarSeverity.Error);
        }
        finally
        {
            SetBusy(false);
            UpdateUi();
        }
    }

    private void EqualizerSetting_Changed(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (!double.IsNaN(args.NewValue))
        {
            if (sender.Name is "Eq0Q" or "Eq1Q" or "Eq2Q" or "Eq3Q")
            {
                var exactQ = decimal.Round((decimal)args.NewValue, 1, MidpointRounding.AwayFromZero);
                var snappedQ = (double)exactQ;
                if (args.NewValue != snappedQ)
                {
                    sender.Value = snappedQ;
                    return;
                }
            }
            else if (sender.Name is "Eq0Frequency" or "Eq1Frequency" or "Eq2Frequency" or "Eq3Frequency")
            {
                var snappedFrequency = Math.Round(args.NewValue, MidpointRounding.AwayFromZero);
                if (args.NewValue != snappedFrequency)
                {
                    sender.Value = snappedFrequency;
                    return;
                }
            }
        }

        if (_initialized && !_updatingUi)
        {
            _equalizerDirty = true;
        }
    }

    private void SoundQuality_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_initialized && !_updatingUi)
        {
            _soundQualityDirty = true;
        }
    }

    private async void SaveSoundQuality_Click(object sender, RoutedEventArgs e)
    {
        if (!_service.IsConnected)
        {
            ShowInfo("请先连接耳机", InfoBarSeverity.Warning);
            return;
        }

        if (SoundQualityComboBox.SelectedItem is not SoundQualityOption selected)
        {
            ShowInfo("请选择音质模式", InfoBarSeverity.Warning);
            return;
        }

        SetBusy(true);
        try
        {
            var result = await _service.SetSoundQualityAsync(selected.Value);
            if (!result.Success)
            {
                ShowInfo(result.Detail, InfoBarSeverity.Error);
                return;
            }

            _soundQualityDirty = false;
            if (selected.Value != SoundQualityMode.Custom)
            {
                _equalizerDirty = false;
            }
            ShowInfo("音质设置已保存", InfoBarSeverity.Success);
        }
        catch (TimeoutException)
        {
            ShowInfo("操作超时", InfoBarSeverity.Error);
        }
        catch
        {
            ShowInfo("音质设置未保存", InfoBarSeverity.Error);
        }
        finally
        {
            SetBusy(false);
            UpdateUi();
        }
    }

    private async void SaveEqualizer_Click(object sender, RoutedEventArgs e)
    {
        if (!_service.IsConnected)
        {
            ShowInfo("请先连接耳机", InfoBarSeverity.Warning);
            return;
        }

        if (_service.State.SoundQualityMode != SoundQualityMode.Custom)
        {
            ShowInfo("请先将音质设置为自定义", InfoBarSeverity.Warning);
            return;
        }

        var bands = ReadEqualizerFromUi();
        SetBusy(true);
        try
        {
            var result = await _service.SetEqualizerAsync(bands);
            if (!result.Success)
            {
                ShowInfo(result.Detail, InfoBarSeverity.Error);
                return;
            }

            _equalizerDirty = false;
            ShowInfo("均衡器设置已保存", InfoBarSeverity.Success);
        }
        catch (TimeoutException)
        {
            ShowInfo("操作超时", InfoBarSeverity.Error);
        }
        catch
        {
            ShowInfo("均衡器设置未保存", InfoBarSeverity.Error);
        }
        finally
        {
            SetBusy(false);
            UpdateUi();
        }
    }

    private IReadOnlyList<EqualizerBand> ReadEqualizerFromUi()
    {
        int[] fallbackFrequencies = [100, 1000, 3000, 5000];
        var bands = new EqualizerBand[FitBudsProtocol.EqualizerBandCount];

        for (var index = 0; index < FitBudsProtocol.EqualizerBandCount; index++)
        {
            var frequencyValue = _equalizerFrequencyBoxes[index].Value;
            var gainValue = _equalizerGainBoxes[index].Value;
            var qValue = _equalizerQBoxes[index].Value;
            var (minFrequencyHz, maxFrequencyHz) = FitBudsProtocol.GetEqualizerFrequencyRange((byte)index);

            var frequency = Math.Clamp(
                (int)Math.Round(double.IsNaN(frequencyValue) ? fallbackFrequencies[index] : frequencyValue),
                minFrequencyHz,
                maxFrequencyHz);
            var gain = Math.Clamp(
                Math.Round((double.IsNaN(gainValue) ? 0.0 : gainValue) * 2.0, MidpointRounding.AwayFromZero) / 2.0,
                FitBudsProtocol.EqualizerMinGainDb,
                FitBudsProtocol.EqualizerMaxGainDb);
            var qInput = double.IsNaN(qValue) ? FitBudsProtocol.EqualizerMinQ : (decimal)qValue;
            var q = Math.Clamp(
                decimal.Round(qInput, 1, MidpointRounding.AwayFromZero),
                FitBudsProtocol.EqualizerMinQ,
                FitBudsProtocol.EqualizerMaxQ);

            _equalizerFrequencyBoxes[index].Value = frequency;
            _equalizerGainBoxes[index].Value = gain;
            _equalizerQBoxes[index].Value = (double)q;
            bands[index] = new EqualizerBand((byte)index, frequency, gain, q);
        }

        return bands;
    }

    private void TimerSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (_initialized && !_updatingUi)
        {
            _timerDirty = true;
        }
        UpdateTimerInputEnabled();
    }

    private void UpdateTimerInputEnabled()
        => TimerNumberBox.IsEnabled = TimerSwitch.IsOn;

    private async void SaveTimer_Click(object sender, RoutedEventArgs e)
    {
        if (!_service.IsConnected)
        {
            ShowInfo("请先连接耳机", InfoBarSeverity.Warning);
            return;
        }

        var minutes = Math.Clamp(
            (int)Math.Round(double.IsNaN(TimerNumberBox.Value) ? 30 : TimerNumberBox.Value),
            1,
            90);
        TimerNumberBox.Value = minutes;

        SetBusy(true);
        try
        {
            var result = TimerSwitch.IsOn
                ? await _service.SetTimerAsync(minutes)
                : await _service.CancelTimerAsync();

            if (!result.Success)
            {
                ShowInfo("定时设置未保存", InfoBarSeverity.Error);
                return;
            }

            _timerDirty = false;
            ShowInfo(TimerSwitch.IsOn ? "定时已保存" : "定时已关闭", InfoBarSeverity.Success);
        }
        catch (TimeoutException)
        {
            ShowInfo("操作超时", InfoBarSeverity.Error);
        }
        catch
        {
            ShowInfo("定时设置未保存", InfoBarSeverity.Error);
        }
        finally
        {
            SetBusy(false);
            UpdateUi();
        }
    }

    private void TouchSelection_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_initialized && !_updatingUi)
        {
            _touchDirty = true;
        }
    }

    private async void ApplyTouch_Click(object sender, RoutedEventArgs e)
    {
        if (!_service.IsConnected)
        {
            ShowInfo("请先连接耳机", InfoBarSeverity.Warning);
            return;
        }

        SetBusy(true);
        try
        {
            foreach (var gestureId in FitBudsProtocol.TouchReadOrder)
            {
                if (_touchCombos[gestureId].SelectedItem is not TouchFunctionOption selected)
                {
                    continue;
                }

                var current = _service.State.TouchAssignments.FirstOrDefault(t => t.GestureId == gestureId);
                if (current?.Function == selected.Value)
                {
                    continue;
                }

                var result = await _service.SetTouchAsync(gestureId, selected.Value);
                if (!result.Success)
                {
                    ShowInfo("触控设置未保存", InfoBarSeverity.Error);
                    return;
                }
            }

            _touchDirty = false;
            ShowInfo("触控设置已保存", InfoBarSeverity.Success);
        }
        catch (TimeoutException)
        {
            ShowInfo("操作超时", InfoBarSeverity.Error);
        }
        catch
        {
            ShowInfo("触控设置未保存", InfoBarSeverity.Error);
        }
        finally
        {
            SetBusy(false);
            UpdateUi();
        }
    }

    private void CycleSetting_Changed(object sender, RoutedEventArgs e)
    {
        if (_initialized && !_updatingUi)
        {
            _cycleDirty = true;
        }
    }

    private async void ApplyCycle_Click(object sender, RoutedEventArgs e)
    {
        if (!_service.IsConnected)
        {
            ShowInfo("请先连接耳机", InfoBarSeverity.Warning);
            return;
        }

        var modes = new List<NoiseMode>();
        foreach (var check in _cycleChecks.Where(c => c.IsChecked == true))
        {
            if (check.Tag is string tag && Enum.TryParse<NoiseMode>(tag, out var mode))
            {
                modes.Add(mode);
            }
        }

        if (modes.Count is < 2 or > 3)
        {
            ShowInfo("请选择 2 到 3 个降噪模式", InfoBarSeverity.Warning);
            return;
        }

        SetBusy(true);
        try
        {
            var result = await _service.SetCycleMaskAsync(modes);
            if (!result.Success)
            {
                ShowInfo(result.Detail, InfoBarSeverity.Error);
                return;
            }

            _cycleDirty = false;
            ShowInfo("轮换设置已保存", InfoBarSeverity.Success);
        }
        catch (TimeoutException)
        {
            ShowInfo("操作超时", InfoBarSeverity.Error);
        }
        catch
        {
            ShowInfo("轮换设置未保存", InfoBarSeverity.Error);
        }
        finally
        {
            SetBusy(false);
            UpdateUi();
        }
    }

    private async Task EnsureFullSnapshotIfConnectedAsync()
    {
        if (!_service.IsConnected)
        {
            return;
        }

        SetBusy(true);
        try
        {
            await _service.EnsureFullSnapshotAsync();
        }
        catch
        {
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void Service_StateChanged(object? sender, EventArgs e) => UpdateUi();

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

            DeviceConnectionText.Text = state.ConnectionDetail;
            DeviceNameValue.Text = string.IsNullOrWhiteSpace(state.DeviceName) ? "FitBuds Turbo" : state.DeviceName;
            if (DeviceNameTextBox.FocusState == FocusState.Unfocused)
            {
                DeviceNameTextBox.Text = state.DeviceName;
            }
            FirmwareVersionValue.Text = string.IsNullOrWhiteSpace(state.FirmwareVersion) ? "—" : state.FirmwareVersion;
            SetBatteryValue(DeviceLeftBatteryIcon, DeviceLeftBatteryValue, DeviceLeftLowBatteryStatusIcon, null, state.LeftBattery, connected, BatteryChannel.Left, false);
            SetBatteryValue(DeviceRightBatteryIcon, DeviceRightBatteryValue, DeviceRightLowBatteryStatusIcon, null, state.RightBattery, connected, BatteryChannel.Right, false);
            SetBatteryValue(DeviceCaseBatteryIcon, DeviceCaseBatteryValue, DeviceCaseLowBatteryStatusIcon, DeviceCaseChargingStatusIcon, state.CaseBattery, connected, BatteryChannel.Case, state.IsCaseCharging);

            SoundPanel.IsHitTestVisible = connected;
            SoundPanel.Opacity = connected ? 1.0 : 0.45;

            if (!_promptDirty)
            {
                PromptSwitch.IsOn = state.PromptEnabled ?? false;
                PromptVolumeSlider.Value = state.PromptVolume ?? 0;
                PromptVolumeText.Text = state.PromptVolume?.ToString() ?? "—";
            }

            if (!_soundQualityDirty && SoundQualityComboBox.ItemsSource is IEnumerable<SoundQualityOption> soundQualityOptions)
            {
                var options = soundQualityOptions.ToArray();
                SoundQualityComboBox.SelectedItem = state.SoundQualityMode is { } soundQuality
                    ? options.FirstOrDefault(option => option.Value == soundQuality)
                    : null;
            }

            var equalizerEnabled = connected && state.SoundQualityMode == SoundQualityMode.Custom;
            foreach (var input in _equalizerFrequencyBoxes.Concat(_equalizerGainBoxes).Concat(_equalizerQBoxes))
            {
                input.IsEnabled = equalizerEnabled;
            }
            EqualizerEditorGrid.Opacity = equalizerEnabled ? 1.0 : 0.45;
            SaveEqualizerButton.IsEnabled = equalizerEnabled;

            if (!_equalizerDirty && state.EqualizerBands.Count == FitBudsProtocol.EqualizerBandCount)
            {
                foreach (var band in state.EqualizerBands)
                {
                    if (band.BandIndex >= FitBudsProtocol.EqualizerBandCount)
                    {
                        continue;
                    }

                    _equalizerFrequencyBoxes[band.BandIndex].Value = band.FrequencyHz;
                    _equalizerGainBoxes[band.BandIndex].Value = band.GainDb;
                    _equalizerQBoxes[band.BandIndex].Value = (double)band.Q;
                }
            }

            if (!_timerDirty)
            {
                var timerActive = state.TimerMinutes is > 0;
                TimerSwitch.IsOn = timerActive;
                if (timerActive)
                {
                    TimerNumberBox.Value = Math.Clamp(state.TimerMinutes!.Value, 1, 90);
                }
                else if (double.IsNaN(TimerNumberBox.Value) || TimerNumberBox.Value < 1)
                {
                    TimerNumberBox.Value = 30;
                }
                UpdateTimerInputEnabled();
            }

            if (!_touchDirty && _touchCombos.Values.FirstOrDefault()?.ItemsSource is IEnumerable<TouchFunctionOption> functionOptions)
            {
                var functions = functionOptions.ToArray();
                foreach (var (gestureId, combo) in _touchCombos)
                {
                    var assignment = state.TouchAssignments.FirstOrDefault(t => t.GestureId == gestureId);
                    combo.SelectedItem = assignment?.Function is { } function
                        ? functions.FirstOrDefault(f => f.Value == function)
                        : null;
                }
            }

            var sharedMask = state.SharedAncCycleMask;
            if (!_cycleDirty)
            {
                foreach (var check in _cycleChecks)
                {
                    if (check.Tag is string tag && Enum.TryParse<NoiseMode>(tag, out var mode))
                    {
                        check.IsChecked = sharedMask is not null && (sharedMask.Value & FitBudsProtocol.AncCycleBits[mode]) != 0;
                    }
                }
            }

        }
        finally
        {
            _updatingUi = false;
        }
    }

    private void SetBusy(bool value)
    {
        _busy = value;
        SettingsBusyRing.IsActive = value;
        Navigation.IsHitTestVisible = !value;
        SettingsBusyRing.IsHitTestVisible = true;
    }

    private void VersionButton_Click(object sender, RoutedEventArgs e)
    {
        var app = (App)Application.Current;
        if (app.DeveloperModeUnlocked)
        {
            return;
        }

        _developerVersionTapCount++;
        const int tapsRequired = 7;
        if (_developerVersionTapCount >= tapsRequired)
        {
            app.UnlockDeveloperMode();
            DeveloperNavigationItem.Visibility = Visibility.Visible;
            Navigation.SelectedItem = DeveloperNavigationItem;
            _developerVersionTapCount = 0;
            ShowInfo("开发者模式已开启，本次运行期间保持有效", InfoBarSeverity.Success);
            return;
        }

        if (_developerVersionTapCount >= 4)
        {
            ShowInfo($"再点击 {tapsRequired - _developerVersionTapCount} 次即可开启开发者模式", InfoBarSeverity.Informational);
        }
    }

    private static string GetDisplayVersion()
    {
        var version = typeof(App).Assembly.GetName().Version;
        if (version is null)
        {
            return "1.0.48";
        }

        return version.Build >= 0
            ? $"{version.Major}.{version.Minor}.{version.Build}"
            : $"{version.Major}.{version.Minor}";
    }

    private void ShowInfo(string message, InfoBarSeverity severity)
    {
        var safeMessage = CleanUserMessage(message);
        SettingsInfoBar.Message = safeMessage;
        SettingsInfoBar.Severity = severity;
        SettingsInfoBar.IsOpen = true;

        _infoBarHideCts?.Cancel();
        var owner = new CancellationTokenSource();
        _infoBarHideCts = owner;
        _ = HideInfoBarLaterAsync(owner);
    }

    private async Task HideInfoBarLaterAsync(CancellationTokenSource owner)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(4), owner.Token).ConfigureAwait(false);
            if (!owner.IsCancellationRequested)
            {
                DispatcherQueue.TryEnqueue(() =>
                {
                    if (ReferenceEquals(_infoBarHideCts, owner))
                    {
                        SettingsInfoBar.IsOpen = false;
                    }
                });
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            if (ReferenceEquals(_infoBarHideCts, owner))
            {
                _infoBarHideCts = null;
            }
            owner.Dispose();
        }
    }

    private static string CleanUserMessage(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return "操作完成";
        }

        return message.Trim().TrimEnd('。');
    }

    private void SetBatteryValue(
        PathIcon icon,
        TextBlock text,
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

    private sealed record SoundQualityOption(SoundQualityMode Value, string Label);
    private sealed record TouchFunctionOption(TouchFunction Value, string Label);
}
