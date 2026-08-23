using FitBudsControl.Interop;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace FitBudsControl.Windows;

public sealed partial class ShortcutOverlayWindow : Window
{
    private const int TopMarginDip = 18;
    private readonly AppWindow _appWindow;
    private readonly nint _hwnd;
    private readonly Brush _accentBrush;
    private CancellationTokenSource? _noticeHideCts;
    private bool _visible;

    public ShortcutOverlayWindow()
    {
        InitializeComponent();
        _accentBrush = new SolidColorBrush(
            new global::Windows.UI.ViewManagement.UISettings().GetColorValue(
                global::Windows.UI.ViewManagement.UIColorType.Accent));
        Title = "FitBuds Turbo 快捷切换";
        SystemBackdrop = new DesktopAcrylicBackdrop();
        _hwnd = NativeMethods.GetWindowHandle(this);
        _appWindow = NativeMethods.GetAppWindow(this);
        _appWindow.IsShownInSwitchers = false;

        if (_appWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsResizable = false;
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = false;
            presenter.IsAlwaysOnTop = true;
            presenter.SetBorderAndTitleBar(false, false);
        }

        NativeMethods.ConfigureNoActivatePopup(this);
        NativeMethods.EnableRoundedCorners(this);
        NativeMethods.DisableWindowBorder(this);
        Closed += (_, _) =>
        {
            _noticeHideCts?.Cancel();
            _noticeHideCts?.Dispose();
            _noticeHideCts = null;
        };
    }

    public void ShowChoices(IReadOnlyList<string> labels, IReadOnlyList<string> icons, int selectedIndex)
    {
        CancelNoticeHide();
        ChoicesPanel.Visibility = Visibility.Visible;
        ChoiceDivider.Visibility = Visibility.Visible;
        ChoicesPanel.Children.Clear();
        SelectedNameText.Text = labels[selectedIndex];

        for (var index = 0; index < labels.Count; index++)
        {
            var selected = index == selectedIndex;
            var icon = new FontIcon
            {
                Glyph = icons[index],
                FontSize = 21,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = new SolidColorBrush(global::Windows.UI.Color.FromArgb(255, 242, 242, 242)),
            };

            ChoicesPanel.Children.Add(new Border
            {
                Width = 48,
                Height = 44,
                Padding = new Thickness(4),
                CornerRadius = new CornerRadius(8),
                Background = selected
                    ? _accentBrush
                    : new SolidColorBrush(global::Windows.UI.Color.FromArgb(0, 0, 0, 0)),
                Child = icon,
            });
        }

        var widthDip = 24 + labels.Count * 48 + Math.Max(0, labels.Count - 1) * 8;
        ShowAtTopCenter(widthDip, 92);
    }

    public void ShowNotice(string title, string detail)
    {
        CancelNoticeHide();
        ChoicesPanel.Children.Clear();
        ChoicesPanel.Visibility = Visibility.Collapsed;
        ChoiceDivider.Visibility = Visibility.Collapsed;
        SelectedNameText.Text = $"{title} · {detail}";
        ShowAtTopCenter(290, 42);

        _noticeHideCts = new CancellationTokenSource();
        _ = HideNoticeLaterAsync(_noticeHideCts.Token);
    }

    public void HideOverlay()
    {
        CancelNoticeHide();
        if (!_visible)
        {
            return;
        }

        _visible = false;
        _appWindow.Hide();
    }

    private void ShowAtTopCenter(int widthDip, int heightDip)
    {
        var width = NativeMethods.DipToPixels(_hwnd, widthDip);
        var height = NativeMethods.DipToPixels(_hwnd, heightDip);
        var topMargin = NativeMethods.DipToPixels(_hwnd, TopMarginDip);
        var workArea = DisplayArea.Primary.WorkArea;
        var x = workArea.X + Math.Max(0, (workArea.Width - width) / 2);
        var y = workArea.Y + topMargin;

        _appWindow.MoveAndResize(new global::Windows.Graphics.RectInt32(x, y, width, height));
        _visible = true;
        _appWindow.Show(false);
    }

    private async Task HideNoticeLaterAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromMilliseconds(1400), cancellationToken);
            if (!cancellationToken.IsCancellationRequested)
            {
                _visible = false;
                _appWindow.Hide();
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void CancelNoticeHide()
    {
        var cancellation = _noticeHideCts;
        _noticeHideCts = null;
        cancellation?.Cancel();
        cancellation?.Dispose();
    }
}
