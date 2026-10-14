using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using VigilWatt.Core.Models;
using VigilWatt.Core.Utils;
using Gdi = System.Drawing;
using GdiImaging = System.Drawing.Imaging;

namespace VigilWatt.App.Services;

/// <summary>
/// 功 能： 任务栏电量贴片：**真嵌进任务栏**——一个原生分层窗口，<c>SetParent</c> 挂成任务栏 （<c>Shell_TrayWnd</c>）的子窗口，内容用 <c>UpdateLayeredWindow</c> 提交 32 位 ARGB 位图。
/// 2026/10/12 17:20:14 xliu
/// 三道锁（SetParent / WS_EX_LAYERED / UpdateLayeredWindow）见 docs/dev/runtime-pitfalls.md §16。
/// </summary>
public sealed class TaskbarPillWindow : IDisposable
{
    private const string ClassName = "VigilWattTaskbarPill";
    private const string WindowTitle = "VigilWatt 任务栏电量";
    private const int FollowIntervalMs = 1000;

    private const double MinHeightDip = 22, MaxHeightDip = 40;
    private const double TaskbarMarginDip = 6;
    private const double EdgeGapDip = 6;

    /// <summary>交给非托管代码的回调必须静态压住，否则被 GC 回收后窗口再来消息就是访问已释放内存。</summary>
    private static readonly Native.WndProcDele StaticProc = WndProc;

    private static bool _classRegistered;

    /// <summary>同一时刻只允许一个贴片；窗口过程是静态的，靠它转回实例。</summary>
    private static TaskbarPillWindow? _current;

    private readonly DispatcherQueue _dispatcher = DispatcherQueue.GetForCurrentThread();
    private readonly DispatcherQueueTimer _timer;

    private IntPtr _hwnd, _taskbar;
    private IntPtr _memDc, _dib, _oldDib, _dibBits;
    private int _dibW, _dibH;
    private Gdi.Bitmap? _bitmap;
    private int _pillW, _pillH;
    private int _offsetPx;
    private int _offsetDpi;
    private uint _windowDpi;
    private bool _dragging;
    private int _dragStartCursor, _dragStartOffset;
    private bool _taskbarHorizontal = true;
    private PixelRect _taskbarRect, _trayRect;
    private int _refreshQueued;
    private int _rebuildQueued;
    private bool _needRender = true;
    private bool _disposed;

    public TaskbarPillWindow()
    {
        _current = this;
        LoadOffsetFromSettings();

        EnsureWindow();

        // 可反复开/关（设置页每次保存都可能 Dispose 后置 null），订阅必须用命名方法并在 Dispose 里解绑
        App.Services.Manager.DeviceUpdated += OnDeviceUpdated;
        App.Services.Manager.DevicesChanged += OnDevicesChanged;

        _timer = _dispatcher.CreateTimer();
        _timer.Interval = TimeSpan.FromMilliseconds(FollowIntervalMs);
        _timer.Tick += (_, _) => Update();
        _timer.Start();

        Update();
    }

    private void LoadOffsetFromSettings()
    {
        var s = App.Services.Settings;
        _offsetPx = s.TaskbarBatteryOffsetX;
        _offsetDpi = s.TaskbarBatteryOffsetDpi;
    }

    private void OnDeviceUpdated(DeviceStatus _) => RequestRefresh();

    private void OnDevicesChanged() => RequestRefresh();

    /// <summary>合并刷新请求：一轮轮询会对 N 台设备各发一次事件，逐个排队就是 N 次全量重建。</summary>
    private void RequestRefresh()
    {
        if (Interlocked.Exchange(ref _refreshQueued, 1) == 1) return;
        _dispatcher.TryEnqueue(() =>
        {
            Interlocked.Exchange(ref _refreshQueued, 0);
            _needRender = true;
            Update();
        });
    }

    /// <summary>设置保存后的即时生效入口（开关/显示内容/最多台数可能都变了）。</summary>
    public void RefreshFromSettings()
    {
        LoadOffsetFromSettings();
        _needRender = true;
        Update();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _timer.Stop();
        App.Services.Manager.DeviceUpdated -= OnDeviceUpdated;
        App.Services.Manager.DevicesChanged -= OnDevicesChanged;
        ReleaseWindow();

        if (ReferenceEquals(_current, this)) _current = null;
    }

    // ---------- 窗口 ----------

    private static void RegisterClassOnce()
    {
        if (_classRegistered) return;
        _classRegistered = true;

        var wc = new Native.WNDCLASSW
        {
            style = Native.CS_DBLCLKS,
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(StaticProc),
            hInstance = Native.GetModuleHandleW(null),
            hCursor = Native.LoadCursorW(IntPtr.Zero, (IntPtr)Native.IDC_ARROW),
            hbrBackground = IntPtr.Zero,
            lpszClassName = ClassName,
        };
        Native.RegisterClassW(ref wc);
    }

    /// <summary>建窗口并挂进任务栏。窗口被父窗口带走（Explorer 重启）后由调用方重新调它。</summary>
    private void EnsureWindow()
    {
        if (_hwnd != IntPtr.Zero && Native.IsWindow(_hwnd)) return;
        ReleaseWindow();

        RegisterClassOnce();
        var hInstance = Native.GetModuleHandleW(null);
        _hwnd = Native.CreateWindowExW(
            (uint)(Native.WS_EX_LAYERED | Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE),
            ClassName, WindowTitle, Native.WS_POPUP,
            0, 0, 1, 1, IntPtr.Zero, IntPtr.Zero, hInstance, IntPtr.Zero);
        if (_hwnd == IntPtr.Zero)
        {
            Log.Warn($"任务栏贴片：窗口创建失败，err={Marshal.GetLastWin32Error()}");
            return;
        }

        AttachToTaskbar();
        _windowDpi = TaskbarDpi();
        Native.ShowWindow(_hwnd, Native.SW_SHOWNOACTIVATE);
    }

    /// <summary>
    /// 挂成任务栏的子窗口。**SetParent 不会自动改 WS_CHILD/WS_POPUP**（MSDN 明确要求自己同步），
    /// 不同步的话坐标仍按桌面算、裁剪也不对。
    /// </summary>
    private void AttachToTaskbar()
    {
        _taskbar = Native.FindWindowW(Native.TaskbarClassName, null);
        if (_taskbar == IntPtr.Zero || _hwnd == IntPtr.Zero) return;

        Native.SetParent(_hwnd, _taskbar);
        var style = Native.GetWindowLongPtr(_hwnd, Native.GWL_STYLE).ToInt64();
        style = (style & ~(long)Native.WS_POPUP) | (long)Native.WS_CHILD;
        Native.SetWindowLongPtr(_hwnd, Native.GWL_STYLE, new IntPtr(style));
    }

    /// <summary>DPI/显示配置变了要 Destroy+重建；不能在 WndProc 里 DestroyWindow（§16.8）。</summary>
    private void RequestRebuild()
    {
        if (Interlocked.Exchange(ref _rebuildQueued, 1) == 1) return;
        _dispatcher.TryEnqueue(() =>
        {
            Interlocked.Exchange(ref _rebuildQueued, 0);
            if (_disposed) return;
            ReleaseWindow();
            _taskbarRect = default;
            _trayRect = default;
            EnsureWindow();
            _needRender = true;
            Update();
        });
    }

    private void ReleaseWindow()
    {
        if (_hwnd != IntPtr.Zero)
        {
            if (Native.IsWindow(_hwnd)) Native.DestroyWindow(_hwnd);
            _hwnd = IntPtr.Zero;
        }
        _taskbar = IntPtr.Zero;
        _windowDpi = 0;
        ReleaseDib();
        _bitmap?.Dispose();
        _bitmap = null;
        _pillW = _pillH = 0;
        _needRender = true;
    }

    // ---------- 更新 ----------

    private void Update()
    {
        if (_disposed) return;
        // 本方法经 DispatcherQueue 回调调用，回调里未捕获的异常会绕过 Application.UnhandledException
        // 直接 fail-fast，必须整体兜底
        try
        {
            UpdateCore();
        }
        catch (Exception ex)
        {
            Log.Warn($"任务栏贴片更新失败: {ex.Message}");
        }
    }

    private void UpdateCore()
    {
        EnsureWindow();
        if (_hwnd == IntPtr.Zero) return;

        // Explorer 重启会换一个任务栏窗口，旧 HWND 失效，要重新挂
        var taskbar = Native.FindWindowW(Native.TaskbarClassName, null);
        if (taskbar == IntPtr.Zero) return;
        if (taskbar != _taskbar || Native.GetParent(_hwnd) != taskbar)
            AttachToTaskbar();

        // 任务栏 DPI 变了：分层子窗口坐标空间跟创建时绑死，只重算矩形不够
        uint dpi = TaskbarDpi();
        if (_windowDpi != 0 && dpi != 0 && dpi != _windowDpi)
        {
            RequestRebuild();
            return;
        }

        if (!TryGetTaskbar(out var rect, out var tray, out bool hidden)) return;
        // 任务栏自动隐藏时只剩约 2px 厚的边：子窗口会自己跟着父窗口走，别按 2px 重算尺寸位置
        if (hidden && _pillH > 0) return;

        if (rect != _taskbarRect || tray != _trayRect)
        {
            _taskbarRect = rect;
            _trayRect = tray;
            _taskbarHorizontal = TaskbarLayout.IsHorizontal(rect);
            int h = TaskbarLayout.PillHeight(rect, Scale, TaskbarMarginDip, MinHeightDip, MaxHeightDip);
            if (h != _pillH)
            {
                _pillH = h;
                _needRender = true;
            }
        }

        if (_needRender || _bitmap == null) Render();
        ApplyPosition();
    }

    /// <summary>找任务栏与托盘矩形（物理像素、屏幕坐标）；<paramref name="hidden"/> 表示自动隐藏已收起。</summary>
    private static bool TryGetTaskbar(out PixelRect taskbar, out PixelRect tray, out bool hidden)
    {
        taskbar = default;
        tray = default;
        hidden = false;

        var hwnd = Native.FindWindowW(Native.TaskbarClassName, null);
        if (hwnd == IntPtr.Zero || !Native.GetWindowRect(hwnd, out var rect)) return false;

        taskbar = new PixelRect(rect.Left, rect.Top, rect.Width, rect.Height);
        if (TaskbarLayout.IsTaskbarHidden(taskbar))
        {
            hidden = true;
            return true;
        }

        // 托盘找不到不算失败：退回用任务栏自身边缘定位，贴片照样显示
        var trayHwnd = Native.FindWindowExW(hwnd, IntPtr.Zero, Native.TrayNotifyClassName, null);
        if (trayHwnd != IntPtr.Zero && Native.GetWindowRect(trayHwnd, out var trayRect))
            tray = new PixelRect(trayRect.Left, trayRect.Top, trayRect.Width, trayRect.Height);
        return true;
    }

    private void Render()
    {
        if (_pillH <= 0) return;   // 还没算出任务栏厚度，等下一拍

        var devices = SelectDevices(out int overflow);
        var bmp = TaskbarPillRenderer.Render(devices, overflow, TaskbarPillRenderer.SkinFor(IsLightTheme()), _pillH, Scale, out int width);

        _bitmap?.Dispose();
        _bitmap = bmp;
        _pillW = width;
        _needRender = false;
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

        int max = TaskbarBatteryOptions.Clamp(App.Services.Settings.TaskbarBatteryMaxDevices);
        if (all.Count <= max) return all;
        overflow = all.Count - max;
        return all.GetRange(0, max);
    }

    private void ApplyPosition()
    {
        if (_bitmap == null || _pillW == 0 || _pillH == 0) return;

        int gap = (int)Math.Round(EdgeGapDip * Scale);
        int placeOffset = OffsetForCurrentDpi(out _);
        var (x, y) = TaskbarLayout.Place(_taskbarRect, _trayRect, _pillW, _pillH, placeOffset, gap, out int applied);
        // 过渡帧（拔插显示器时任务栏/托盘矩形短暂错位）也会夹紧；只有拖动中才把夹紧结果写回记忆，
        // 否则分辨率往返后用户偏移会被污染。反推写回语义见 docs/dev/runtime-pitfalls.md §16.5。
        if (_dragging) _offsetPx = applied;

        // 子窗口的坐标是**相对父窗口客户区**的，定位数学算出来的是屏幕坐标，这里换算一次
        var pt = new Native.POINT { X = x, Y = y };
        Native.ScreenToClient(_taskbar, ref pt);
        Upload(pt.X, pt.Y);
    }

    /// <summary>把记下的偏移换算成当前任务栏 DPI 下的物理像素；旧配置 Dpi=0 时首次只绑定、不改像素。</summary>
    private int OffsetForCurrentDpi(out uint currentDpi)
    {
        currentDpi = TaskbarDpi();
        if (currentDpi == 0) currentDpi = 96;
        if (_offsetDpi <= 0)
        {
            _offsetDpi = (int)currentDpi;
            return _offsetPx;
        }
        if (_dragging) return _offsetPx;
        return TaskbarLayout.ScaleOffset(_offsetPx, (uint)_offsetDpi, currentDpi);
    }

    // ---------- 位图提交 ----------

    private void EnsureDib(int w, int h)
    {
        if (_dib != IntPtr.Zero && _dibW == w && _dibH == h) return;
        ReleaseDib();

        var bmi = new Native.BITMAPINFO
        {
            bmiHeader = new Native.BITMAPINFOHEADER
            {
                biSize = Marshal.SizeOf<Native.BITMAPINFOHEADER>(),
                biWidth = w,
                biHeight = -h,   // 负高度 = 自上而下，与 Bitmap 的行序一致
                biPlanes = 1,
                biBitCount = 32,
                biCompression = 0,
            },
        };
        _memDc = Native.CreateCompatibleDC(IntPtr.Zero);
        _dib = Native.CreateDIBSection(IntPtr.Zero, ref bmi, 0, out _dibBits, IntPtr.Zero, 0);
        _oldDib = Native.SelectObject(_memDc, _dib);
        _dibW = w;
        _dibH = h;
    }

    private void ReleaseDib()
    {
        if (_memDc != IntPtr.Zero)
        {
            if (_oldDib != IntPtr.Zero) Native.SelectObject(_memDc, _oldDib);
            Native.DeleteDC(_memDc);
            _memDc = IntPtr.Zero;
        }
        if (_dib != IntPtr.Zero)
        {
            Native.DeleteObject(_dib);
            _dib = IntPtr.Zero;
        }
        _oldDib = IntPtr.Zero;
        _dibBits = IntPtr.Zero;
        _dibW = _dibH = 0;
    }

    private void Upload(int x, int y)
    {
        if (_bitmap == null) return;
        EnsureDib(_pillW, _pillH);
        if (_dib == IntPtr.Zero) return;

        CopyToDib(_bitmap, _pillW, _pillH);

        var dst = new Native.POINT { X = x, Y = y };
        var size = new Native.SIZE { cx = _pillW, cy = _pillH };
        var src = new Native.POINT { X = 0, Y = 0 };
        var blend = new Native.BLENDFUNCTION
        {
            BlendOp = Native.AC_SRC_OVER,
            BlendFlags = 0,
            SourceConstantAlpha = 255,
            AlphaFormat = Native.AC_SRC_ALPHA,
        };
        if (!Native.UpdateLayeredWindow(_hwnd, IntPtr.Zero, ref dst, ref size, _memDc, ref src, 0, ref blend, Native.ULW_ALPHA))
            Log.Warn($"任务栏贴片：UpdateLayeredWindow 失败，err={Marshal.GetLastWin32Error()}");
    }

    private void CopyToDib(Gdi.Bitmap bmp, int w, int h)
    {
        var data = bmp.LockBits(new Gdi.Rectangle(0, 0, w, h), GdiImaging.ImageLockMode.ReadOnly, GdiImaging.PixelFormat.Format32bppPArgb);
        try
        {
            var row = new byte[w * 4];
            for (int y = 0; y < h; y++)
            {
                Marshal.Copy(data.Scan0 + y * data.Stride, row, 0, row.Length);
                Marshal.Copy(row, 0, _dibBits + y * w * 4, row.Length);
            }
        }
        finally
        {
            bmp.UnlockBits(data);
        }
    }

    // ---------- 交互 ----------

    private static IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        var self = _current;
        if (self != null)
        {
            switch (msg)
            {
                case Native.WM_LBUTTONDOWN:
                    self.OnLButtonDown();
                    return IntPtr.Zero;
                case Native.WM_LBUTTONDBLCLK:
                    self.OnDoubleClick();
                    return IntPtr.Zero;
                case Native.WM_MOUSEMOVE:
                    self.OnMouseMove();
                    return IntPtr.Zero;
                case Native.WM_LBUTTONUP:
                    self.OnLButtonUp();
                    return IntPtr.Zero;
                case Native.WM_RBUTTONUP:
                    self.OnRightClick();
                    return IntPtr.Zero;
                case Native.WM_DISPLAYCHANGE:
                case Native.WM_DPICHANGED:
                    // 重建可能 DestroyWindow，绝不能在本过程里同步做——推迟到队列
                    self.RequestRebuild();
                    return IntPtr.Zero;
                case Native.WM_SETTINGCHANGE:
                    self._needRender = true;
                    self._dispatcher.TryEnqueue(self.Update);
                    return IntPtr.Zero;
            }
        }
        return Native.DefWindowProcW(hwnd, msg, wParam, lParam);
    }

    private void OnLButtonDown()
    {
        // 用当帧可见（已夹紧）的偏移做起点：屏幕变窄贴片贴边时，一拖不会跳回越界前的记忆值
        int gap = (int)Math.Round(EdgeGapDip * Scale);
        int placeOffset = OffsetForCurrentDpi(out uint dpi);
        TaskbarLayout.Place(_taskbarRect, _trayRect, _pillW, _pillH, placeOffset, gap, out int visible);
        _offsetPx = visible;
        _offsetDpi = (int)dpi;

        _dragging = true;
        Native.SetCapture(_hwnd);
        var p = Native.GetCursorPos();
        _dragStartCursor = _taskbarHorizontal ? p.X : p.Y;
        _dragStartOffset = visible;
    }

    private void OnMouseMove()
    {
        if (!_dragging) return;
        var p = Native.GetCursorPos();
        // 只沿任务栏方向拖：横向任务栏走 X，竖向走 Y（斜着拖不会把贴片拽离任务栏）
        _offsetPx = _dragStartOffset + ((_taskbarHorizontal ? p.X : p.Y) - _dragStartCursor);
        ApplyPosition();
    }

    private void OnLButtonUp()
    {
        if (!_dragging) return;
        _dragging = false;
        Native.ReleaseCapture();

        var s = App.Services.Settings;
        s.TaskbarBatteryOffsetX = _offsetPx;
        s.TaskbarBatteryOffsetDpi = _offsetDpi;
        s.Save();
    }

    private void OnDoubleClick()
    {
        if (_dragging) OnLButtonUp();
        TrayService.ShowMainWindow();
    }

    private void OnRightClick()
    {
        var p = Native.GetCursorPos();
        AppPopupMenu.Show(_hwnd, p.X, p.Y);
    }

    // ---------- 环境 ----------

    /// <summary>任务栏所在显示器的缩放。贴片是它的子窗口，按任务栏的 DPI 而不是自己的。</summary>
    private float Scale
    {
        get
        {
            uint dpi = TaskbarDpi();
            return dpi == 0 ? 1f : dpi / 96f;
        }
    }

    private uint TaskbarDpi() =>
        _taskbar != IntPtr.Zero ? Native.GetDpiForWindow(_taskbar) : 0;

    /// <summary>应用主题跟随系统，原生窗口读注册表比拿 WinUI 的 ActualTheme 更直接。</summary>
    private static bool IsLightTheme()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") as int? != 0;
        }
        catch
        {
            return true;
        }
    }
}
