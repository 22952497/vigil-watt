using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using VigilWatt.App.Services;
using VigilWatt.Core.Models;
using VigilWatt.Core.Utils;
using Windows.Graphics;

namespace VigilWatt.App;

/// <summary>
/// 任务栏电量贴片：**一行无底框的读数**（不画底色/边框，直接浮在任务栏上），钉在任务栏内（系统托盘左边），
/// 逐台显示在线设备电量。
/// **不是 deskband**（Win11 已移除该机制），而是"把自己摆进任务栏矩形"的普通窗口——TrafficMonitor 一类做法。
/// 不进 Alt-Tab、不抢焦点；高频把自己抬到任务栏之上（两者同为 topmost，谁后设谁在上）、约每秒跟随一次
/// 任务栏的位置/厚度/自动隐藏。定位数学在 <see cref="TaskbarLayout"/>、格子外观在
/// <see cref="TaskbarChipFactory"/>（都是独立可测/单一职责的纯逻辑），窗口表面透明在
/// <see cref="TransparentWindowSurface"/>，完整论证见 docs/dev/runtime-pitfalls.md §16 / §17。
/// </summary>
public sealed partial class TaskbarBatteryWindow : Window
{
    // 构造函数里赋给 Root.Padding / ChipStack.Spacing，XAML 侧不写数字
    private const double PadH = 6, PadV = 5;
    private const double ChipGap = 9;

    private const double MinWidthDip = 56;
    private const double MinHeightDip = 22, MaxHeightDip = 40;
    private const double TaskbarMarginDip = 6;  // 贴片上下各留的空隙（按任务栏厚度算）
    private const double EdgeGapDip = 6;        // 贴片与托盘之间留的缝

    private const int FollowTickMs = 16;        // 拍频：每拍只做"抬回最上层"这件便宜事
    private const int GeometryEveryTicks = 60;  // 每 60 拍（约 1 秒）读一次任务栏矩形

    private int _offsetPx;
    private bool _shown;
    private bool _dragging;
    private int _dragStartCursor, _dragStartOffset;
    private bool _taskbarHorizontal = true;
    private PixelRect _taskbarRect, _trayRect;
    private int _pillW, _pillH;
    private double _contentWidthDip = MinWidthDip;
    private int _refreshQueued;
    private int _geometryTick = GeometryEveryTicks;   // 首拍就做几何跟随，别等满一个周期
    private DispatcherQueueTimer? _followTimer;

    public TaskbarBatteryWindow()
    {
        InitializeComponent();

        Title = "VigilWatt 任务栏电量";
        AppWindow.Title = Title;

        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.SetBorderAndTitleBar(false, false);
            presenter.IsAlwaysOnTop = true;
            presenter.IsResizable = false;
            presenter.IsMinimizable = false;
            presenter.IsMaximizable = false;
        }

        // 内边距与格间距只有一处定义：XAML 里不再写死数字，免得改一边忘一边
        Root.Padding = new Thickness(PadH, PadV, PadH, PadV);
        ChipStack.Spacing = ChipGap;

        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        var exStyle = Native.GetWindowLongPtr(hwnd, Native.GWL_EXSTYLE);
        Native.SetWindowLongPtr(hwnd, Native.GWL_EXSTYLE, new IntPtr(
            exStyle.ToInt64() | Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE));

        TransparentWindowSurface.Apply(this);

        _offsetPx = App.Services.Settings.TaskbarBatteryOffsetX;

        // 可反复开/关（设置页每次保存都可能 Close 后置 null），订阅必须用命名方法并在 Closed 里解绑
        App.Services.Manager.DeviceUpdated += OnDeviceUpdated;
        App.Services.Manager.DevicesChanged += OnDevicesChanged;
        Root.ActualThemeChanged += OnThemeChanged;
        Closed += OnClosed;

        // Activate 之后窗口才真正显示，Loaded 里补一次定位并同步 _shown 状态；
        // 透明背板也再补一次——它要落到窗口真正接上的内容岛上，构造期这一次不保证生效
        Root.Loaded += (_, _) => { SetShown(true); TransparentWindowSurface.Apply(this); RefreshContent(); };
        RefreshContent();
        StartFollowTimer();
    }

    private void OnDeviceUpdated(DeviceStatus _) => RequestRefresh();
    private void OnDevicesChanged() => RequestRefresh();
    private void OnThemeChanged(FrameworkElement sender, object args) => RefreshContent();

    /// <summary>合并刷新请求：一轮轮询会对 N 台设备各发一次事件，逐个排队就是 N 次全量重建（同 TrayService.RequestUpdate）。</summary>
    private void RequestRefresh()
    {
        if (Interlocked.Exchange(ref _refreshQueued, 1) == 1) return;
        DispatcherQueue.TryEnqueue(() =>
        {
            Interlocked.Exchange(ref _refreshQueued, 0);
            RefreshContent();
        });
    }

    private void OnClosed(object sender, WindowEventArgs args)
    {
        _followTimer?.Stop();
        _followTimer = null;
        App.Services.Manager.DeviceUpdated -= OnDeviceUpdated;
        App.Services.Manager.DevicesChanged -= OnDevicesChanged;
        Root.ActualThemeChanged -= OnThemeChanged;
    }

    private double Scale => Root.XamlRoot?.RasterizationScale is > 0 ? Root.XamlRoot.RasterizationScale : 1.0;

    /// <summary>设置保存后的即时生效入口（开关/显示内容/最多台数可能都变了）。</summary>
    public void RefreshFromSettings()
    {
        _offsetPx = App.Services.Settings.TaskbarBatteryOffsetX;
        RefreshContent();
    }

    // ---------- 内容 ----------

    private void RefreshContent()
    {
        // 本方法经 DispatcherQueue.TryEnqueue 调用，回调里未捕获的异常会绕过 Application.UnhandledException
        // 直接 fail-fast，必须整体兜底
        try
        {
            RefreshContentCore();
        }
        catch (Exception ex)
        {
            Log.Warn($"任务栏贴片刷新失败: {ex}");
        }
    }

    private void RefreshContentCore()
    {
        ChipStack.Children.Clear();
        var skin = TaskbarChipFactory.SkinFor(Root.ActualTheme);

        var devices = SelectDevices(out int overflow);

        if (devices.Count == 0)
        {
            ChipStack.Children.Add(TaskbarChipFactory.EmptyText(skin));
        }
        else
        {
            for (int i = 0; i < devices.Count; i++)
            {
                if (i > 0) ChipStack.Children.Add(TaskbarChipFactory.Divider(skin));
                ChipStack.Children.Add(TaskbarChipFactory.Chip(devices[i], skin));
            }
            if (overflow > 0)
            {
                ChipStack.Children.Add(TaskbarChipFactory.Divider(skin));
                ChipStack.Children.Add(TaskbarChipFactory.Overflow(overflow, skin));
            }
        }

        Relayout(PadH * 2 + MeasureContentWidth());
    }

    /// <summary>
    /// 量出内容的真实宽度（DIP）。**不要退回"字符数 × 估算字宽"**：真实字宽差一两个像素，
    /// 累到最右一格就把那个 "%" 挤出窗口边缘（用户 2026-10-10 反馈"第二个设备的 % 不全"）。
    /// 交给布局系统实测，再多给 2 DIP 吃掉 DPI 取整。
    /// </summary>
    private double MeasureContentWidth()
    {
        ChipStack.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
        return ChipStack.DesiredSize.Width + 2;
    }

    /// <summary>
    /// 取要显示的设备。**本机电量恒定排除**：Windows 任务栏自己已经有一个电池图标，
    /// 再在贴片上重复一遍只是噪音（与托盘"最低电量"不统计本机是同一个理由）。
    /// </summary>
    private static List<DeviceStatus> SelectDevices(out int overflow)
    {
        overflow = 0;
        var all = App.Services.Manager.Devices
            .Where(d => d.IsOnline && d.Source != DeviceSource.System)
            .OrderBy(d => DeviceStatusFormatter.IsChargingOrFull(d.Charging))
            .ThenBy(d => d.BatteryPercent ?? 999)
            .ThenBy(d => d.DisplayName)
            .ToList();

        if (TaskbarBatteryMode.Normalize(App.Services.Settings.TaskbarBatteryMode) == TaskbarBatteryMode.Lowest)
            return all.Count > 0 ? [all[0]] : [];

        var max = TaskbarBatteryOptions.Clamp(App.Services.Settings.TaskbarBatteryMaxDevices);
        if (all.Count <= max) return all;
        overflow = all.Count - max;
        return all.GetRange(0, max);
    }

    // ---------- 定位与跟随 ----------

    private void StartFollowTimer()
    {
        _followTimer = DispatcherQueue.CreateTimer();
        _followTimer.Interval = TimeSpan.FromMilliseconds(FollowTickMs);
        _followTimer.Tick += (_, _) => Follow();
        _followTimer.Start();
    }

    /// <summary>
    /// 跟随。**拍频 16ms 而不是 1 秒**：贴片与任务栏同为 topmost，点一下任务栏它就会浮到贴片之上
    /// 把贴片整个盖住（用户 2026-10-10 两次反馈"点任务栏会闪一下"）——实测把任务栏抬到顶端后，
    /// 贴片确实被它整个占住、要等下一拍才抢回来，所以空档长度就等于拍频。
    /// 100ms 拍频下那是一整块看得见的闪，压到 16ms（一帧）人眼就分辨不出了；
    /// 抬一次只是一次 <c>SetWindowPos</c>，已经在最上层时还是空操作，代价可忽略。
    /// 要读任务栏矩形、可能触发窗口改尺寸的几何跟随仍然约 1 秒一次。
    ///
    /// 用轮询而不是 WinEventHook——要监听的事件类型太多（移动、尺寸、显示隐藏、Z 序变化），
    /// 轮询的开销可以忽略，代码却少一大截。
    /// </summary>
    private void Follow()
    {
        try
        {
            EnsureAbove();

            if (++_geometryTick < GeometryEveryTicks) return;
            _geometryTick = 0;

            if (!TryGetTaskbar(out var taskbar, out var tray))
            {
                SetShown(false);
                return;
            }

            if (taskbar != _taskbarRect || tray != _trayRect)
                RelayoutCore(_contentWidthDip, taskbar, tray);

            SetShown(true);
        }
        catch (Exception ex)
        {
            Log.Warn($"任务栏贴片跟随失败: {ex.Message}");
        }
    }

    /// <summary>
    /// 找任务栏与托盘矩形（物理像素、屏幕坐标）。返回 false 表示任务栏当前看不见——
    /// 自动隐藏收起时它只剩约 2px 厚的边（<see cref="TaskbarLayout.IsTaskbarHidden"/>），
    /// 贴片必须跟着一起藏，否则会孤零零挂在屏幕边缘。
    /// </summary>
    private static bool TryGetTaskbar(out PixelRect taskbar, out PixelRect tray)
    {
        taskbar = default;
        tray = default;

        var hwnd = Native.FindWindowW(Native.TaskbarClassName, null);
        if (hwnd == IntPtr.Zero || !Native.GetWindowRect(hwnd, out var taskbarRect)) return false;
        taskbar = new PixelRect(taskbarRect.Left, taskbarRect.Top, taskbarRect.Width, taskbarRect.Height);
        if (TaskbarLayout.IsTaskbarHidden(taskbar)) return false;

        // 托盘找不到不算失败：退回用任务栏自身边缘定位，贴片照样显示
        var trayHwnd = Native.FindWindowExW(hwnd, IntPtr.Zero, Native.TrayNotifyClassName, null);
        if (trayHwnd != IntPtr.Zero && Native.GetWindowRect(trayHwnd, out var trayRect))
            tray = new PixelRect(trayRect.Left, trayRect.Top, trayRect.Width, trayRect.Height);
        return true;
    }

    private void Relayout(double widthDip)
    {
        if (!TryGetTaskbar(out var taskbar, out var tray))
        {
            SetShown(false);
            return;
        }
        RelayoutCore(widthDip, taskbar, tray);
    }

    private void RelayoutCore(double widthDip, PixelRect taskbar, PixelRect tray)
    {
        var scale = Scale;
        _contentWidthDip = Math.Max(MinWidthDip, widthDip);
        _pillW = (int)Math.Ceiling(_contentWidthDip * scale);

        _taskbarRect = taskbar;
        _trayRect = tray;
        _taskbarHorizontal = TaskbarLayout.IsHorizontal(taskbar);
        _pillH = TaskbarLayout.PillHeight(taskbar, scale, TaskbarMarginDip, MinHeightDip, MaxHeightDip);

        // 按**客户区**定尺寸：AppWindow.Resize 设的是外框，窗口若留了非客户区边框就会把客户区挤窄、
        // 裁掉最右一格的 "%"（Win10 上实测到）
        AppWindow.ResizeClient(new SizeInt32(_pillW, _pillH));
        ApplyPosition();
    }

    private void ApplyPosition()
    {
        if (_pillW == 0 || _pillH == 0) return;
        int gap = (int)Math.Round(EdgeGapDip * Scale);
        var (x, y) = TaskbarLayout.Place(_taskbarRect, _trayRect, _pillW, _pillH, _offsetPx, gap, out int applied);
        _offsetPx = applied;
        AppWindow.Move(new PointInt32(x, y));
        EnsureAbove();
    }

    /// <summary>把自己抬到任务栏之上：两者同为 topmost，点击任务栏会让它浮上来，所以每秒重设一次。</summary>
    private void EnsureAbove()
    {
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        if (hwnd == IntPtr.Zero) return;
        Native.SetWindowPos(hwnd, Native.HWND_TOPMOST, 0, 0, 0, 0,
            Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
    }

    /// <summary>用原生 ShowWindow 而不是 AppWindow.Show()：后者会尝试激活窗口，而贴片绝不能抢焦点。</summary>
    private void SetShown(bool shown)
    {
        if (_shown == shown) return;
        _shown = shown;
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        if (hwnd != IntPtr.Zero)
            Native.ShowWindow(hwnd, shown ? Native.SW_SHOWNOACTIVATE : Native.SW_HIDE);
    }

    // ---------- 拖动与菜单 ----------

    private void Root_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        // 仅左键拖动；右键留给 ContextFlyout（打开主窗口 / 退出菜单）
        if (!e.GetCurrentPoint(Root).Properties.IsLeftButtonPressed) return;
        _dragging = true;
        Root.CapturePointer(e.Pointer);
        var p = Native.GetCursorPos();
        _dragStartCursor = _taskbarHorizontal ? p.X : p.Y;
        _dragStartOffset = _offsetPx;
    }

    private void Root_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_dragging) return;
        var p = Native.GetCursorPos();
        // 只沿任务栏方向拖：横向任务栏走 X，竖向走 Y（斜着拖不会把贴片拽离任务栏）
        _offsetPx = _dragStartOffset + ((_taskbarHorizontal ? p.X : p.Y) - _dragStartCursor);
        ApplyPosition();
    }

    private void Root_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_dragging) return;
        _dragging = false;
        Root.ReleasePointerCapture(e.Pointer);

        var s = App.Services.Settings;
        s.TaskbarBatteryOffsetX = _offsetPx;
        s.Save();
    }

    private void OpenMain_Click(object sender, RoutedEventArgs e) => TrayService.ShowMainWindow();

    private void Root_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        TrayService.ShowMainWindow();
        e.Handled = true;
    }

    private void Exit_Click(object sender, RoutedEventArgs e) => TrayService.ExitApplication();
}
