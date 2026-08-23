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
    private CancellationTokenSource? _noticeHideCts;
    private bool _visible;

    public ShortcutOverlayWindow()
    {
        InitializeComponent();
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

        NativeMethods.EnableRoundedCorners(this);
        NativeMethods.DisableWindowBorder(this);
        Closed += (_, _) =>
        {
            _noticeHideCts?.Cancel();
            _noticeHideCts?.Dispose();
            _noticeHideCts = null;
        };
    }

    public void ShowChoices(IReadOnlyList<string> labels, int selectedIndex)
    {
        CancelNoticeHide();
        ChoicesPanel.Children.Clear();

        var itemWidthDip = labels.Count > 4 ? 104 : 112;
        for (var index = 0; index < labels.Count; index++)
        {
            var selected = index == selectedIndex;
            var text = new TextBlock
            {
                Text = labels[index],
                FontSize = 14,
                FontWeight = selected ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                TextWrapping = TextWrapping.NoWrap,
            };

            if (selected)
            {
                text.Foreground = new SolidColorBrush(global::Windows.UI.Color.FromArgb(255, 255, 255, 255));
            }

            ChoicesPanel.Children.Add(new Border
            {
                Width = itemWidthDip,
                Height = 40,
                Padding = new Thickness(8, 0, 8, 0),
                CornerRadius = new CornerRadius(8),
                Background = selected
                    ? new SolidColorBrush(global::Windows.UI.Color.FromArgb(255, 0, 120, 212))
                    : new SolidColorBrush(global::Windows.UI.Color.FromArgb(18, 128, 128, 128)),
                BorderBrush = selected
                    ? new SolidColorBrush(global::Windows.UI.Color.FromArgb(255, 96, 205, 255))
                    : new SolidColorBrush(global::Windows.UI.Color.FromArgb(32, 128, 128, 128)),
                BorderThickness = new Thickness(1),
                Child = text,
            });
        }

        // Include extra room beyond the XAML padding for the invisible Win32 frame.
        // Without it, the first/last choices and the lower edge are clipped.
        var widthDip = 48 + labels.Count * itemWidthDip + Math.Max(0, labels.Count - 1) * 8;
        ShowAtTopCenter(widthDip, 80);
    }

    public void ShowNotice(string title, string detail)
    {
        CancelNoticeHide();
        ChoicesPanel.Children.Clear();
        ChoicesPanel.Children.Add(new TextBlock
        {
            Text = $"{title} · {detail}",
            FontSize = 14,
            Opacity = 0.82,
            HorizontalAlignment = HorizontalAlignment.Center,
        });
        ShowAtTopCenter(320, 72);

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
