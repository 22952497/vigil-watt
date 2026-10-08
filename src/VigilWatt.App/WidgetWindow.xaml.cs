using VigilWatt.App.Services;
using VigilWatt.Core.Models;
using VigilWatt.Core.Utils;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Graphics;
using Windows.UI;

namespace VigilWatt.App;

/// <summary>
/// 桌面小组件：无边框悬浮卡片，纵向显示各设备电量，可拖动，不进 Alt-Tab/任务栏。
/// 普通窗口层级（不置顶），会被其他窗口正常遮挡。
/// 尺寸按 DPI 缩放（AppWindow.Resize 用物理像素），位置始终夹在工作区内。
///
/// 两套外壳（按 OS 分流，判定见 Services/WidgetCompat.cs）：
/// - **Win11**：DWM 原生圆角（抗锯齿），窗口矩形即可，保持既有观感不动。
/// - **Win10**：DWM 没有圆角 API；而 `SetWindowRgn` 对 WinUI 3 **完全无效**
///   （见下方 ApplyWin10Chrome 注释），所以唯一的出路是**透明窗口 + 圆角交给 XAML 画**：
///   窗口表面透明（XAML 根节点 `Background="Transparent"`）、不画任何东西，
///   WinUI 的抗锯齿圆角就是最终轮廓。另外恢复 DWM 投影以免卡片"贴死"桌面。
/// </summary>
public sealed partial class WidgetWindow : Window
{
    private bool _dragging;
    private int _dragOffsetX, _dragOffsetY;

    // 与 XAML 的 Root.Padding / ChipStack.Spacing 保持一致
    private const double PadH = 16, PadV = 14;
    private const double RowGap = 16;           // 行与行间距（= ChipStack.Spacing）
    private const double IconSize = 22;         // 设备类型图标方块边长
    private const double IconGap = 10;          // 图标与名称间距
    private const double RowInnerGap = 9;       // 名称行与电量条间距
    private const double BarHeight = 6;
    private const double LineHeight = IconSize; // 名称行行高由图标方块决定，行高确定就不需要运行时 Measure
    private const double RowHeight = IconSize + RowInnerGap + BarHeight;
    private const double NameMaxWidth = 200;    // 名称列上限（超出省略号），正常设备名都能放下

    /// <summary>n 行内容（含行间距）的总高度。</summary>
    private static double RowsHeight(int rows) => rows <= 0 ? 0 : rows * RowHeight + (rows - 1) * RowGap;

    /// <summary>
    /// Win10 圆角半径（DIP）。比 Win11 大一圈是刻意的：
    /// Win10 的圆角完全由 XAML 绘制（抗锯齿、干净），大一点的弧线观感更接近现代磁贴。
    /// </summary>
    private const double Win10CornerRadiusDip = 16;

    /// <summary>Win10 兼容外壳是否生效（决定是否走透明窗口 + XAML 自绘圆角）。</summary>
    private readonly bool _win10Chrome;

    public WidgetWindow()
    {
        InitializeComponent();

        Title = "VigilWatt 小组件";
        AppWindow.Title = Title;

        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.SetBorderAndTitleBar(false, false);
            // 层级先按"普通窗口"起步，随后 ApplyDisplayMode 按设置覆盖（置顶 / 桌面）
            presenter.IsAlwaysOnTop = false;
            presenter.IsResizable = false;
            presenter.IsMinimizable = false;
            presenter.IsMaximizable = false;
        }

        // WS_EX_TOOLWINDOW：不出现在 Alt-Tab 和任务栏
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        var exStyle = Native.GetWindowLongPtr(hwnd, Native.GWL_EXSTYLE);
        Native.SetWindowLongPtr(hwnd, Native.GWL_EXSTYLE,
            new IntPtr(exStyle.ToInt64() | Native.WS_EX_TOOLWINDOW));

        // Win10 走兼容外壳（透明窗口 + 恢复投影 + 大一号圆角）；Win11 保持原样
        _win10Chrome = WidgetCompat.NeedsTransparentChrome(Environment.OSVersion.Version.Build);
        if (_win10Chrome)
        {
            // XAML 里写死的 12 是 Win11 档；Win10 靠 XAML 自绘轮廓，半径大一圈弧线更立得住
            Root.CornerRadius = new CornerRadius(Win10CornerRadiusDip);
            ApplyWin10Chrome();
        }

        var s = App.Services.Settings;
        AppWindow.Move(new PointInt32((int)s.WidgetX, (int)s.WidgetY));
        // 层级：置顶 / 普通 / 桌面（窗口句柄已就绪，构造期就能应用）
        ApplyDisplayMode(s.WidgetDisplayMode);

        App.Services.Manager.DeviceUpdated += _ => DispatcherQueue.TryEnqueue(RefreshContent);
        App.Services.Manager.DevicesChanged += () => DispatcherQueue.TryEnqueue(RefreshContent);
        // 硬编码色值按主题分套，切主题要重画一遍
        Root.ActualThemeChanged += (_, _) => RefreshContent();

        Root.Loaded += (_, _) => RefreshContent();
        RefreshContent();
    }

    private double Scale => Root.XamlRoot?.RasterizationScale is > 0 ? Root.XamlRoot.RasterizationScale : 1.0;

    /// <summary>
    /// Win10 专用外壳：把窗口做成**完全透明的"玻璃框"**，圆角与卡片全部交给 XAML 渲染。
    ///
    /// 为什么不裁窗口形状（重要，上一版就栽在这里）：
    /// WinUI 3 的窗口内容走 **DirectComposition**，由合成器直接画到窗口表面上；
    /// `SetWindowRgn` 裁的是**传统 GDI 绘制区域**，对 DComp 合成的内容**完全无效** ——
    /// 表现就是"设了圆角区域，但窗口看起来仍是直角"（实测截图确认）。
    /// 所以 Win10 上想让卡片是圆角，只能让窗口本身透明、由 XAML 画圆角。
    ///
    /// 透明从哪来（两条路都试过，只有第二条能跑）：
    /// - XAML `<Window Background="Transparent">`：**WinUI 3 的 `Window` 没有 `Background`
    ///   属性**，XAML 编译器直接失败（`Window` 也没有对应 C# 属性，写了 CS0103）。
    /// - 自定义 `SystemBackdrop` 子类：**不可实现** —— `OnTargetConnected` 的参数类型
    ///   `ICompositionSupportsSystemBackdrop` 是 ABI 接口，没有投影到 C#，找不到类型。
    /// - ✅ 最终采用 `SetWindowCompositionAttribute(WCA_ACCENT_POLICY, 透明渐变)`：
    ///   把窗口表面整体镂空，只剩 XAML 画的内容（见 Native.MakeWindowTransparent）。
    ///
    /// 另外恢复 DWM 投影：无边框窗口（SetBorderAndTitleBar(false,false)）在 Win10 上会连
    /// 投影一起失去，卡片"贴死"桌面、毫无层次 —— 这是"平淡"的主因。
    /// </summary>
    private void ApplyWin10Chrome()
    {
        try
        {
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);

            // 1) 窗口表面全透明：卡片圆角之外透出真正的桌面
            if (!Native.MakeWindowTransparent(hwnd))
            {
                Log.Warn("小组件 Win10 透明化失败：窗口将保持不透明底色");
            }

            // 2) 恢复 DWM 投影（透明窗口同样能拿到投影）
            int ncPolicy = Native.DWMNCRP_ENABLED;
            Native.DwmSetWindowAttribute(hwnd, Native.DWMWA_NCRENDERING_POLICY, ref ncPolicy, sizeof(int));
            Native.SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0,
                Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOZORDER | Native.SWP_FRAMECHANGED);
        }
        catch (Exception ex)
        {
            // 外壳失败不能影响功能：窗口仍可用，只是没有投影/透明
            Log.Warn($"小组件 Win10 外壳应用失败: {ex}");
        }
    }

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
        var skin = SkinFor(Root.ActualTheme);

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
                FontSize = 13,
                Height = LineHeight,
                Width = 100,
                TextAlignment = TextAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = new SolidColorBrush(skin.Text) { Opacity = 0.55 },
            });
            ResizeContent(PadH * 2 + 100 + 6, PadV * 2 + LineHeight + 2);
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
            double w = IconSize + IconGap + nameW + 10 + PercentWidth(d);
            if (w > rowW) rowW = w;
        }

        foreach (var d in devices)
        {
            ChipStack.Children.Add(BuildRow(d, rowW, skin));
        }

        // 一屏最多显示 MaxVisibleDevices 条（设置项，1~5，默认 3），多出来的靠滚轮查看。
        // 高度按 min(实际条数, 上限) 算：窗口尺寸因此**封顶**，设备增减不会让卡片一跳一跳。
        // 上限的 DIP 高度向上取整，避免滚动容器把最后一行切掉半个像素。
        var maxVisible = WidgetMaxDevicesOptions.Clamp(App.Services.Settings.WidgetMaxDevices);
        var visibleRows = Math.Min(devices.Count, maxVisible);
        double visibleRowsH = Math.Ceiling(RowsHeight(visibleRows));

        // 总宽补上 Root Border 的 1 DIP 边框线（两侧共 2）+ DPI 舍入余量，
        // 少算几个 DIP 最右侧的百分比列就会被窗口边缘裁掉
        ResizeContent(PadH * 2 + rowW + 6, PadV * 2 + visibleRowsH + 8);
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

    /// <summary>
    /// 让窗口本身是圆角，只对 Win11 有效。
    ///
    /// Border 的 CornerRadius 只作用于卡片那一层绘制，窗口矩形背板会在四个角把它糊住，
    /// 所以需要窗口本身也圆角。Win11 有 DWM 原生圆角（带抗锯齿），一句话搞定。
    ///
    /// **Win10 不能走这里**：那个系统没有圆角 API，而唯一能裁窗口形状的 `SetWindowRgn`
    /// 对 WinUI 3（DirectComposition 渲染）**完全无效**，实测就是"设了但看着仍是直角"。
    /// Win10 改由"透明窗口 + XAML 自绘圆角"解决，见 ApplyWin10Chrome，这里什么都不做。
    /// </summary>
    private void ApplyRoundedCorner()
    {
        if (_win10Chrome) return;   // Win10 的圆角由 XAML 画，不碰窗口

        try
        {
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            int pref = Native.DWMWCP_ROUND;
            Native.DwmSetWindowAttribute(hwnd, Native.DWMWA_WINDOW_CORNER_PREFERENCE, ref pref, sizeof(int));
        }
        catch (Exception ex)
        {
            Log.Warn($"小组件圆角设置失败: {ex}");
        }
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

    /// <summary>13 号字下的近似文本宽度：CJK≈15px、ASCII≈8.5px（偏宽松，多余宽度变成名称与百分比之间的空隙）。</summary>
    private static double EstimateNameWidth(string text)
    {
        double w = 0;
        foreach (var ch in text) w += ch > 0x2E80 ? 15 : 8.5;
        return w;
    }

    /// <summary>16 号加粗下百分比文本的近似宽度（偏宽松：估小了会被窗口右缘裁掉）。</summary>
    private static double PercentWidth(DeviceStatus d) =>
        d.BatteryPercent.HasValue ? $"{d.BatteryPercent}%".Length * 11 : 22;

    private static UIElement BuildRow(DeviceStatus d, double width, Skin skin)
    {
        var percent = d.BatteryPercent;
        var charging = d.Charging == ChargingState.Charging;
        // 进度条与百分比数字共用这一个状态色，两者永远同色（>50% 绿 / ≤50% 琥珀 / ≤20% 红 / 充电蓝）
        var stateColor = UiPalette.BatteryLevel(percent, charging);

        var row = new StackPanel { Spacing = RowInnerGap, Width = width };

        // 图标 Auto 列、名称 * 列自适应收缩（超长省略号）、百分比 Auto 列，互不挤占
        var line = new Grid { ColumnSpacing = 8 };
        line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        line.Children.Add(BuildIcon(d.Kind, skin));

        var name = new TextBlock
        {
            Text = d.DisplayName,
            FontSize = 13,
            Height = LineHeight,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = new SolidColorBrush(skin.Text),
        };
        Grid.SetColumn(name, 1);
        line.Children.Add(name);

        var percentText = new TextBlock
        {
            Text = percent.HasValue ? $"{percent}%" : "--",
            FontSize = 16,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = new SolidColorBrush(stateColor),
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(percentText, 2);
        line.Children.Add(percentText);
        row.Children.Add(line);

        // 电量条用自绘（Grid 轨道 + 定宽填充），ProgressBar 模板在这个高度下不渲染填充。
        // 轨道色不用 Resources 查找：DispatcherQueue 回调里的异常不走 UnhandledException，会直接 fail-fast
        var track = new Grid
        {
            Height = BarHeight,
            CornerRadius = new CornerRadius(BarHeight / 2),
            Background = new SolidColorBrush(skin.Track),
        };
        track.Children.Add(new Border
        {
            Width = width * Math.Clamp(percent ?? 0, 0, 100) / 100.0,
            HorizontalAlignment = HorizontalAlignment.Left,
            CornerRadius = new CornerRadius(BarHeight / 2),
            Background = new SolidColorBrush(stateColor),
        });
        row.Children.Add(track);
        return row;
    }

    /// <summary>设备类型图标：圆角方块底 + 纯形状拼装的图形（不用字体图标，避免字形缺失渲染成方块）。</summary>
    private static UIElement BuildIcon(DeviceKind kind, Skin skin)
    {
        var host = new Grid
        {
            Width = IconSize,
            Height = IconSize,
            CornerRadius = new CornerRadius(6),
            Background = new SolidColorBrush(skin.IconFill),
            VerticalAlignment = VerticalAlignment.Center,
        };
        host.Children.Add(Glyph(kind, skin.IconStroke));
        return host;
    }

    private static UIElement Glyph(DeviceKind kind, Color stroke)
    {
        var brush = new SolidColorBrush(stroke);

        switch (kind)
        {
            case DeviceKind.Keyboard:
            {
                var box = new Grid { Width = 13, Height = 9 };
                box.Children.Add(new Border
                {
                    BorderThickness = new Thickness(1.2),
                    CornerRadius = new CornerRadius(2.5),
                    BorderBrush = brush,
                });
                box.Children.Add(new Border
                {
                    Width = 7,
                    Height = 1.4,
                    Background = brush,
                    CornerRadius = new CornerRadius(0.7),
                    VerticalAlignment = VerticalAlignment.Bottom,
                    Margin = new Thickness(0, 0, 0, 1.8),
                });
                return box;
            }

            case DeviceKind.Mouse:
            {
                var box = new Grid { Width = 9, Height = 13 };
                box.Children.Add(new Border
                {
                    BorderThickness = new Thickness(1.2),
                    CornerRadius = new CornerRadius(4.5),
                    BorderBrush = brush,
                });
                box.Children.Add(new Border
                {
                    Width = 1.2,
                    Height = 3.5,
                    Background = brush,
                    VerticalAlignment = VerticalAlignment.Top,
                    Margin = new Thickness(0, 2, 0, 0),
                });
                return box;
            }

            case DeviceKind.Headset:
            {
                var box = new Grid { Width = 13, Height = 12 };
                box.Children.Add(new Border
                {
                    Width = 11,
                    Height = 7,
                    CornerRadius = new CornerRadius(5.5, 5.5, 0, 0),
                    BorderThickness = new Thickness(1.2, 1.2, 0, 0),
                    BorderBrush = brush,
                    VerticalAlignment = VerticalAlignment.Top,
                });
                foreach (var h in new[] { HorizontalAlignment.Left, HorizontalAlignment.Right })
                {
                    box.Children.Add(new Border
                    {
                        Width = 2.6,
                        Height = 4.5,
                        CornerRadius = new CornerRadius(1.3),
                        Background = brush,
                        HorizontalAlignment = h,
                        VerticalAlignment = VerticalAlignment.Bottom,
                    });
                }
                return box;
            }

            case DeviceKind.Speaker:
            {
                var box = new Grid { Width = 9.5, Height = 13 };
                box.Children.Add(new Border
                {
                    BorderThickness = new Thickness(1.2),
                    CornerRadius = new CornerRadius(2.5),
                    BorderBrush = brush,
                });
                box.Children.Add(new Ellipse
                {
                    Width = 3,
                    Height = 3,
                    Fill = brush,
                    VerticalAlignment = VerticalAlignment.Top,
                    Margin = new Thickness(0, 2.2, 0, 0),
                });
                box.Children.Add(new Ellipse
                {
                    Width = 4.4,
                    Height = 4.4,
                    Fill = brush,
                    VerticalAlignment = VerticalAlignment.Bottom,
                    Margin = new Thickness(0, 0, 0, 2),
                });
                return box;
            }

            case DeviceKind.Gamepad:
            {
                var box = new Grid { Width = 14, Height = 9 };
                box.Children.Add(new Border
                {
                    BorderThickness = new Thickness(1.2),
                    CornerRadius = new CornerRadius(4.5),
                    BorderBrush = brush,
                });
                foreach (var h in new[] { HorizontalAlignment.Left, HorizontalAlignment.Right })
                {
                    box.Children.Add(new Ellipse
                    {
                        Width = 2.6,
                        Height = 2.6,
                        Fill = brush,
                        HorizontalAlignment = h,
                        Margin = new Thickness(2, 0, 2, 0),
                        VerticalAlignment = VerticalAlignment.Center,
                    });
                }
                return box;
            }

            case DeviceKind.Computer:
            {
                // 显示器造型：屏幕 + 支架。与设置/列表里的 \uE7F8 语义一致。
                var box = new Grid { Width = 14, Height = 11 };
                box.Children.Add(new Border
                {
                    Width = 14,
                    Height = 9.5,
                    BorderThickness = new Thickness(1.2),
                    CornerRadius = new CornerRadius(1.8),
                    BorderBrush = brush,
                    VerticalAlignment = VerticalAlignment.Top,
                });
                box.Children.Add(new Border
                {
                    Width = 5,
                    Height = 1.4,
                    Background = brush,
                    CornerRadius = new CornerRadius(0.7),
                    VerticalAlignment = VerticalAlignment.Bottom,
                });
                return box;
            }

            default:
                return new Ellipse
                {
                    Width = 9,
                    Height = 9,
                    Stroke = brush,
                    StrokeThickness = 1.4,
                };
        }
    }

    /// <summary>
    /// 小组件配色。全部硬编码、按主题分两套：ResizeContent 路径由 DispatcherQueue 回调驱动，
    /// 那里查 Resources 抛异常会直接 fail-fast，所以状态色一律不走 ThemeResource。
    /// </summary>
    private readonly record struct Skin(
        Color Track,
        Color IconFill,
        Color IconStroke,
        Color Text);

    private static Skin SkinFor(ElementTheme theme) => theme == ElementTheme.Dark
        ? new Skin(
            Track: Color.FromArgb(0x1F, 0xFF, 0xFF, 0xFF),
            IconFill: Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF),
            IconStroke: Color.FromArgb(0xFF, 0xD2, 0xD2, 0xD2),
            Text: Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF))
        : new Skin(
            Track: Color.FromArgb(0x16, 0x00, 0x00, 0x00),
            IconFill: Color.FromArgb(0x0F, 0x00, 0x00, 0x00),
            IconStroke: Color.FromArgb(0xFF, 0x5F, 0x5E, 0x5A),
            Text: Color.FromArgb(0xFF, 0x1F, 0x1F, 0x1F));

    private void OpenMain_Click(object sender, RoutedEventArgs e)
    {
        // 与托盘左键同一路径：显示并前置主窗口
        TrayService.ShowMainWindow();
    }

    private void Exit_Click(object sender, RoutedEventArgs e)
    {
        // 与托盘右键的"退出"完全一致：清理托盘图标后结束进程
        TrayService.ExitApplication();
    }

    // ---------- 显示模式 ----------

    /// <summary>
    /// 设置保存后的即时生效入口：重新应用层级 + 重算窗口高度（最多显示条数可能变了）。
    /// 由设置页在已存在小组件时调用。
    /// </summary>
    public void RefreshFromSettings()
    {
        var s = App.Services.Settings;
        ApplyDisplayMode(s.WidgetDisplayMode);
        RefreshContent();
    }

    /// <summary>
    /// 应用窗口层级。可重复调用（构造期一次、设置页改完再调一次），每次都会先复位再设，
    /// 避免从"置顶"切到"桌面"时残留 TOPMOST 标记。
    ///
    /// 三档对应关系：
    /// - Topmost：`OverlappedPresenter.IsAlwaysOnTop = true`，浮在其它窗口上方。
    /// - Normal ：`IsAlwaysOnTop = false`，普通层级，被后激活的窗口遮挡。
    /// - Desktop：同样不置顶，再用 `SetWindowPos(HWND_BOTTOM)` 把窗口压到 Z 序最底，
    ///   只有桌面露出时才可见 —— 即"钉在桌面"。
    ///
    /// ⚠ Desktop 档不能用 `HWND_TOPMOST` 的反面来做：`IsAlwaysOnTop` 只认识"置顶/非置顶"，
    /// 表达不了"沉到最底"，必须走 SetWindowPos。
    /// </summary>
    public void ApplyDisplayMode(string? mode)
    {
        var resolved = WidgetDisplayMode.Normalize(mode);
        try
        {
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            if (hwnd == IntPtr.Zero) return;

            if (AppWindow.Presenter is OverlappedPresenter presenter)
                presenter.IsAlwaysOnTop = resolved == WidgetDisplayMode.Topmost;

            if (resolved == WidgetDisplayMode.Desktop)
            {
                Native.SetWindowPos(hwnd, Native.HWND_BOTTOM, 0, 0, 0, 0,
                    Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
            }
            else
            {
                // 从桌面档切回来时要显式脱离 bottom：HWND_TOP + NOACTIVATE 把它抬回普通层，
                // 既不抢焦点，也不会在 Topmost 档下和 IsAlwaysOnTop 打架。
                Native.SetWindowPos(hwnd, Native.HWND_TOP, 0, 0, 0, 0,
                    Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"小组件显示模式应用失败({resolved}): {ex.Message}");
        }
    }

    // ---------- 拖动 ----------

    private void Root_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        // 仅左键拖动；右键留给 ContextFlyout（打开主窗口 / 退出菜单）
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
        // 拖到另一块显示器时 DPI 会变，圆角半径的物理像素值要跟着重算
        ApplyRoundedCorner();
        var s = App.Services.Settings;
        s.WidgetX = AppWindow.Position.X;
        s.WidgetY = AppWindow.Position.Y;
        s.Save();
    }
}
