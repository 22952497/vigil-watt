using VigilWatt.App.Services;
using VigilWatt.Core.Models;
using VigilWatt.Core.Utils;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;

namespace VigilWatt.App;

/// <summary>
/// 桌面小组件：无边框置顶悬浮条，横向显示各设备电量芯片，可拖动，不进 Alt-Tab/任务栏。
/// 尺寸按 DPI 缩放（AppWindow.Resize 用物理像素），位置始终夹在工作区内。
/// </summary>
public sealed partial class WidgetWindow : Window
{
    private bool _dragging;
    private int _dragOffsetX, _dragOffsetY;

    // 与 XAML Root.Padding 保持一致
    private const double PadH = 14, PadV = 10;
    private const double RowInnerGap = 3;     // 名称行与电量条间距
    private const double BarHeight = 4;
    private const double LineHeight = 18;     // 名称行钉死行高（12 号字实际行高约 18），行高确定就不需要运行时 Measure
    private const double RowHeight = LineHeight + RowInnerGap + BarHeight;
    private const double NameMaxWidth = 200;  // 名称列上限（超出省略号），正常设备名都能放下

    public WidgetWindow()
    {
        InitializeComponent();

        Title = "VigilWatt 小组件";
        AppWindow.Title = Title;

        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.SetBorderAndTitleBar(false, false);
            presenter.IsAlwaysOnTop = true;
            presenter.IsResizable = false;
            presenter.IsMinimizable = false;
            presenter.IsMaximizable = false;
        }

        // WS_EX_TOOLWINDOW：不出现在 Alt-Tab 和任务栏
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        var exStyle = Native.GetWindowLongPtr(hwnd, Native.GWL_EXSTYLE);
        Native.SetWindowLongPtr(hwnd, Native.GWL_EXSTYLE,
            new IntPtr(exStyle.ToInt64() | Native.WS_EX_TOOLWINDOW));

        var s = App.Services.Settings;
        AppWindow.Move(new PointInt32((int)s.WidgetX, (int)s.WidgetY));

        App.Services.Manager.DeviceUpdated += _ => DispatcherQueue.TryEnqueue(RefreshContent);
        App.Services.Manager.DevicesChanged += () => DispatcherQueue.TryEnqueue(RefreshContent);

        Root.Loaded += (_, _) => RefreshContent();
        RefreshContent();
    }

    private double Scale => Root.XamlRoot?.RasterizationScale is > 0 ? Root.XamlRoot.RasterizationScale : 1.0;

    private void RefreshContent()
    {
        // 本方法经 DispatcherQueue.TryEnqueue 调用，回调里未捕获的异常会绕过
        // Application.UnhandledException 直接 fail-fast（combase E_FAIL、进程静默消失），必须整体兜底
        try
        {
            RefreshContentCore();
        }
        catch (Exception ex)
        {
            Log.Warn($"小组件刷新失败: {ex}");
        }
    }

    private void RefreshContentCore()
    {
        ChipStack.Children.Clear();
        // 低电量优先：充电中/已满的排末尾，未知电量殿后，同级按名称保持稳定
        var devices = App.Services.Manager.Devices
            .Where(d => d.IsOnline)
            .OrderBy(d => d.Charging is ChargingState.Charging or ChargingState.Full)
            .ThenBy(d => d.BatteryPercent ?? 999)
            .ThenBy(d => d.DisplayName)
            .ToList();

        if (devices.Count == 0)
        {
            ChipStack.Children.Add(new TextBlock
            {
                Text = "暂无在线设备",
                FontSize = 12,
                Height = LineHeight,
                Width = 96,
                TextAlignment = TextAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = Application.Current.Resources["TextFillColorSecondaryBrush"] as Brush
                             ?? new SolidColorBrush(Colors.Gray) { Opacity = 0.8 },
            });
            ResizeContent(PadH * 2 + 96 + 6, PadV * 2 + RowHeight + 2);
            return;
        }

        // 所有行共用同一行宽（取最宽者），名称左对齐、百分比右对齐
        double nameW = 0, rowW = 0;
        foreach (var d in devices)
        {
            double w = Math.Min(EstimateNameWidth(d.DisplayName), NameMaxWidth);
            if (w > nameW) nameW = w;
        }
        foreach (var d in devices)
        {
            double w = nameW + 6 + (d.Charging == ChargingState.Charging ? 14 : 0) + 6 + PercentWidth(d);
            if (w > rowW) rowW = w;
        }

        foreach (var d in devices)
        {
            ChipStack.Children.Add(BuildRow(d, rowW));
        }

        // 总宽补上 Root Border 的 1 DIP 边框线（两侧共 2）+ DPI 舍入余量，
        // 少算几个 DIP 最右侧的百分比列就会被窗口边缘裁掉
        ResizeContent(PadH * 2 + rowW + 6,
            PadV * 2 + devices.Count * RowHeight + (devices.Count - 1) * 8 + 8);
    }

    private void ResizeContent(double widthDip, double heightDip)
    {
        var scale = Scale;
        // 防御性下限：算出 NaN/负数时保底，避免 AppWindow.Resize 收到非法值
        AppWindow.Resize(new SizeInt32(
            (int)Math.Ceiling(Math.Max(40, widthDip) * scale),
            (int)Math.Ceiling(Math.Max(24, heightDip) * scale)));
        ClampToWorkArea();
    }

    /// <summary>确保窗口完整落在最近显示器的工作区内。</summary>
    private void ClampToWorkArea()
    {
        try
        {
            var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Nearest);
            var pos = AppWindow.Position;
            var size = AppWindow.Size;
            int x = Math.Clamp(pos.X, area.WorkArea.X, Math.Max(area.WorkArea.X, area.WorkArea.X + area.WorkArea.Width - size.Width));
            int y = Math.Clamp(pos.Y, area.WorkArea.Y, Math.Max(area.WorkArea.Y, area.WorkArea.Y + area.WorkArea.Height - size.Height));
            if (x != pos.X || y != pos.Y)
                AppWindow.Move(new PointInt32(x, y));
        }
        catch
        {
            // 显示器信息拿不到就算了
        }
    }

    /// <summary>12 号字下的近似文本宽度：CJK≈13.5px、ASCII≈7.5px（偏宽松，多余宽度变成名称与百分比之间的空隙）。</summary>
    private static double EstimateNameWidth(string text)
    {
        double w = 0;
        foreach (var ch in text) w += ch > 0x2E80 ? 13.5 : 7.5;
        return w;
    }

    /// <summary>13 号加粗下百分比文本的近似宽度（偏宽松：数字≈9、%≈13，估小了会被窗口右缘裁掉）。</summary>
    private static double PercentWidth(DeviceStatus d) =>
        d.BatteryPercent.HasValue ? $"{d.BatteryPercent}%".Length * 10 : 22;

    private static UIElement BuildRow(DeviceStatus d, double width)
    {
        var percent = d.BatteryPercent;
        var color = UiPalette.BatteryLevel(percent, d.Charging == ChargingState.Charging);

        var row = new StackPanel { Spacing = RowInnerGap, Width = width };

        // 名称 * 列自适应收缩（超长省略号），⚡ 与百分比 Auto 列，互不挤占
        var line = new Grid { ColumnSpacing = 6 };
        line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        line.Children.Add(new TextBlock
        {
            Text = d.DisplayName,
            FontSize = 12,
            Height = LineHeight,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
        });
        var bolt = new TextBlock
        {
            Text = "⚡",
            FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center,
            Visibility = d.Charging == ChargingState.Charging ? Visibility.Visible : Visibility.Collapsed,
        };
        Grid.SetColumn(bolt, 1);
        line.Children.Add(bolt);
        var percentText = new TextBlock
        {
            Text = percent.HasValue ? $"{percent}%" : "--",
            FontSize = 13,
            FontWeight = Microsoft.UI.Text.FontWeights.Bold,
            Foreground = new SolidColorBrush(color),
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(percentText, 2);
        line.Children.Add(percentText);
        row.Children.Add(line);

        // 3px 微型电量条用自绘（Grid 轨道 + 定宽填充），ProgressBar 模板在这个高度下不渲染填充。
        // 轨道色不用 Resources 查找：DispatcherQueue 回调里的异常不走 UnhandledException，会直接 fail-fast
        var track = new Grid
        {
            Height = BarHeight,
            CornerRadius = new CornerRadius(BarHeight / 2),
            Background = new SolidColorBrush(Colors.Gray) { Opacity = 0.25 },
        };
        track.Children.Add(new Border
        {
            Width = width * Math.Clamp(percent ?? 0, 0, 100) / 100.0,
            HorizontalAlignment = HorizontalAlignment.Left,
            CornerRadius = new CornerRadius(BarHeight / 2),
            Background = new SolidColorBrush(color),
        });
        row.Children.Add(track);
        return row;
    }

    private void OpenMain_Click(object sender, RoutedEventArgs e)
    {
        // 与托盘左键同一路径：显示并前置主窗口
        TrayService.ShowMainWindow();
    }

    // ---------- 拖动 ----------

    private void Root_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        // 仅左键拖动；右键留给 ContextFlyout（打开主窗口菜单）
        if (!e.GetCurrentPoint(Root).Properties.IsLeftButtonPressed) return;
        _dragging = true;
        Root.CapturePointer(e.Pointer);
        var pos = Native.GetCursorPos();
        _dragOffsetX = pos.X - AppWindow.Position.X;
        _dragOffsetY = pos.Y - AppWindow.Position.Y;
    }

    private void Root_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_dragging) return;
        var pos = Native.GetCursorPos();
        AppWindow.Move(new PointInt32(pos.X - _dragOffsetX, pos.Y - _dragOffsetY));
    }

    private void Root_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_dragging) return;
        _dragging = false;
        Root.ReleasePointerCapture(e.Pointer);

        ClampToWorkArea();
        var s = App.Services.Settings;
        s.WidgetX = AppWindow.Position.X;
        s.WidgetY = AppWindow.Position.Y;
        s.Save();
    }
}
