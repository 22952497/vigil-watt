using System.Runtime.InteropServices;

namespace VigilWatt.App.Services;

internal static class Native
{
    public delegate IntPtr WndProcDele(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    public const int GWL_EXSTYLE = -20;
    public const int GWLP_WNDPROC = -4;
    public const int WS_EX_TOOLWINDOW = 0x80;

    /// <summary>点击不激活窗口：任务栏贴片必须带它，否则点一下就把焦点从用户正在用的程序抢走。</summary>
    public const int WS_EX_NOACTIVATE = 0x08000000;

    // ---- 无边框窗口的标准样式位（GWL_STYLE）----
    // 光调 AppWindow 的 SetBorderAndTitleBar(false,false) 只关掉标题栏；这几位留着，
    // Win11 仍会给窗口画系统描边 + 圆角外轮廓（贴片没底色，这一圈直接暴露"这里有个窗口"）。
    public const int GWL_STYLE = -16;

    /// <summary>WS_CAPTION | WS_THICKFRAME | WS_MINIMIZEBOX | WS_MAXIMIZEBOX | WS_SYSMENU。</summary>
    private const long BorderlessStyleMask = 0x00CF0000;

    // ---- 托盘 ----
    public const uint NIM_ADD = 0;
    public const uint NIM_MODIFY = 1;
    public const uint NIM_DELETE = 2;

    public const uint NIF_MESSAGE = 0x1;
    public const uint NIF_ICON = 0x2;
    public const uint NIF_TIP = 0x4;
    public const uint NIF_INFO = 0x10;

    public const uint NIIF_WARNING = 0x2;

    public const uint WM_LBUTTONUP = 0x0202;
    public const uint WM_LBUTTONDBLCLK = 0x0203;
    public const uint WM_RBUTTONUP = 0x0205;

    // ---- 菜单 ----
    public const uint MF_STRING = 0x0;
    public const uint MF_SEPARATOR = 0x800;
    public const uint MF_GRAYED = 0x1;
    public const uint TPM_RETURNCMD = 0x100;
    public const uint TPM_RIGHTBUTTON = 0x2;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct NOTIFYICONDATAW
    {
        public uint cbSize;
        public IntPtr hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public uint dwState;
        public uint dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public uint uVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public uint dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct WNDCLASSW
    {
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        public string? lpszMenuName;
        public string lpszClassName;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT
    {
        public int X;
        public int Y;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern bool Shell_NotifyIconW(uint message, ref NOTIFYICONDATAW data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern ushort RegisterClassW(ref WNDCLASSW wndClass);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr CreateWindowExW(
        uint exStyle, string className, string windowName, uint style,
        int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr DefWindowProcW(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr CallWindowProcW(IntPtr prevProc, IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr64(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr64(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    public static IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex) => GetWindowLongPtr64(hWnd, nIndex);

    public static IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr value) =>
        SetWindowLongPtr64(hWnd, nIndex, value);

    [DllImport("user32.dll")]
    public static extern bool DestroyIcon(IntPtr hIcon);

    [DllImport("user32.dll")]
    public static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern bool AppendMenuW(IntPtr menu, uint flags, IntPtr id, string? text);

    [DllImport("user32.dll")]
    public static extern bool DestroyMenu(IntPtr menu);

    [DllImport("user32.dll")]
    public static extern int TrackPopupMenuEx(IntPtr menu, uint flags, int x, int y, IntPtr hwnd, IntPtr tpm);

    [DllImport("user32.dll", EntryPoint = "GetCursorPos")]
    private static extern bool GetCursorPosRaw(out POINT point);

    public static POINT GetCursorPos()
    {
        POINT p = default;
        GetCursorPosRaw(out p);
        return p;
    }

    [DllImport("user32.dll")]
    public static extern bool SetForegroundWindow(IntPtr hwnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern uint RegisterWindowMessageW(string message);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr GetModuleHandleW(string? name);

    // ---- 任务栏定位（任务栏电量贴片用）----
    // 贴片窗口靠"找任务栏窗口 → 读它的矩形 → 把自己摆进去"来定位，不是 deskband（Win11 已移除该机制）。
    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;

        public readonly int Width => Right - Left;

        public readonly int Height => Bottom - Top;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr FindWindowW(string? className, string? windowName);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr FindWindowExW(IntPtr parent, IntPtr childAfter, string? className, string? windowName);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);

    /// <summary>任务栏主窗口类名（主显示器那块；任务栏被拖到副屏时它也跟过去）。</summary>
    public const string TaskbarClassName = "Shell_TrayWnd";

    /// <summary>任务栏内的通知区域（图标 + 时钟）类名，贴片就贴在它左边。</summary>
    public const string TrayNotifyClassName = "TrayNotifyWnd";

    // ---- 窗口圆角（仅 Win11）----
    // ⚠ Win10 **不要**用 SetWindowRgn 裁圆角：WinUI 3 的内容走 DirectComposition，SetWindowRgn
    // 只裁传统 GDI 区域、对 DComp 内容完全无效（实测"设了区域仍是直角"）。
    // Win10 的圆角靠"透明窗口 + XAML 自绘"，见 WidgetWindow.ApplyWin10Chrome。
    public const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    public const int DWMWCP_ROUND = 2;

    /// <summary>圆角偏好：不圆。贴片没有底色，圆角只会留下一圈空弧线（Win11 专用）。</summary>
    public const int DWMWCP_DONOTROUND = 1;

    /// <summary>Win11 的窗口描边颜色属性；取值 <see cref="DWMWA_COLOR_NONE"/> 表示不画描边。</summary>
    public const int DWMWA_BORDER_COLOR = 34;
    public const int DWMWA_COLOR_NONE = unchecked((int)0xFFFFFFFE);

    [DllImport("dwmapi.dll")]
    public static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    // ---- DWM 窗口边框/阴影策略 ----
    // 无边框窗口在 Win10 上会连 DWM 投影一起失去（卡片"贴死"桌面，是"平淡"的根因）。
    // 把 NC 渲染策略显式设回 ENABLED 即可恢复投影；Win11 本就不受影响。
    public const int DWMWA_NCRENDERING_POLICY = 2;
    public const int DWMNCRP_ENABLED = 2;

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool SetWindowPos(IntPtr hwnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint flags);

    // SetWindowPos 的 Z 序插入位：钉桌面档靠 HWND_BOTTOM，切回普通/置顶档靠 HWND_TOP。
    // HWND_TOPMOST 留给 IsAlwaysOnTop 管，别混用。
    public static readonly IntPtr HWND_TOP = IntPtr.Zero;
    public static readonly IntPtr HWND_BOTTOM = new(1);

    /// <summary>任务栏贴片专用：把自己抬到任务栏之上（两者都是 topmost，后设的在上）。</summary>
    public static readonly IntPtr HWND_TOPMOST = new(-1);

    public const int SW_HIDE = 0;

    /// <summary>显示但不激活——贴片不能被点击/显示动作抢走焦点。</summary>
    public const int SW_SHOWNOACTIVATE = 4;

    [DllImport("user32.dll")]
    public static extern bool ShowWindow(IntPtr hwnd, int nCmdShow);

    public const uint SWP_NOSIZE = 0x1;
    public const uint SWP_NOMOVE = 0x2;
    public const uint SWP_NOZORDER = 0x4;
    public const uint SWP_NOACTIVATE = 0x10;
    public const uint SWP_FRAMECHANGED = 0x20;

    // ---- 窗口整体透明（仅 Win10 的圆角方案用）----
    // 唯一能让窗口"镂空"的通道：`Window` 没有 Background 属性，自定义 SystemBackdrop 在 C# 里
    // 又不可实现（ABI 接口未投影）。把 accent 策略设为全透明渐变即可，是 WinUI 3 做异形窗口的通行做法。
    public const int WCA_ACCENT_POLICY = 19;
    public const int ACCENT_ENABLE_TRANSPARENTGRADIENT = 2;

    [StructLayout(LayoutKind.Sequential)]
    public struct AccentPolicy
    {
        public int AccentState;
        public int AccentFlags;
        public int GradientColor;   // 0x00RRGGBB（高位 alpha 这里不要，用 AccentState 控透明）
        public int AnimationId;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct WindowCompositionAttributeData
    {
        public int Attribute;
        public IntPtr Data;
        public int SizeOfData;
    }

    [DllImport("user32.dll")]
    private static extern int SetWindowCompositionAttribute(IntPtr hwnd, ref WindowCompositionAttributeData data);

    /// <summary>
    /// 把窗口表面设为**完全透明**（不画任何像素）。调用后窗口只会显示 XAML 画出来的内容，
    /// 其余部分是真正的桌面。**只在 Win10 上有效**：Win11 起该 accent 策略不再改变窗口表面，
    /// 调用照样返回非 0 却毫无视觉效果，Win11 必须改走"透明 SystemBackdrop"，见 runtime-pitfalls §17。
    /// </summary>
    public static bool MakeWindowTransparent(IntPtr hwnd)
    {
        var accent = new AccentPolicy
        {
            AccentState = ACCENT_ENABLE_TRANSPARENTGRADIENT,
            AccentFlags = 0,
            GradientColor = 0,
            AnimationId = 0,
        };

        int size = Marshal.SizeOf<AccentPolicy>();
        IntPtr ptr = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(accent, ptr, false);
            var data = new WindowCompositionAttributeData
            {
                Attribute = WCA_ACCENT_POLICY,
                Data = ptr,
                SizeOfData = size,
            };
            return SetWindowCompositionAttribute(hwnd, ref data) != 0;
        }
        finally
        {
            Marshal.FreeHGlobal(ptr);
        }
    }

    /// <summary>
    /// 收敛 Win11 的窗口外观：清掉 <see cref="BorderlessStyleMask"/> 里的边框样式位（改完必须补
    /// <c>SWP_FRAMECHANGED</c>，否则样式改动不一定立刻反映到外观），并关掉 DWM 的圆角与外描边。
    /// 这些属性在 Win10 上不存在，失败即忽略——调用点不判返回值，别让外观收敛把窗口初始化搞挂。
    /// </summary>
    public static void ApplyBorderlessChrome(IntPtr hwnd)
    {
        var style = GetWindowLongPtr(hwnd, GWL_STYLE).ToInt64();
        SetWindowLongPtr(hwnd, GWL_STYLE, new IntPtr(style & ~BorderlessStyleMask));
        SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0,
            SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED);

        int noRound = DWMWCP_DONOTROUND;
        DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref noRound, sizeof(int));
        int noBorder = DWMWA_COLOR_NONE;
        DwmSetWindowAttribute(hwnd, DWMWA_BORDER_COLOR, ref noBorder, sizeof(int));
    }

    // ---- Win11 窗口表面透明（任务栏贴片用）----
    // WinUI3 的窗口有**两层**背景：① Win32 窗口自己用类背景刷擦出来的底色，② 内容岛 Visual 的底色。
    // 用户看到的"白框"是 ①：类背景刷是 COLOR_WINDOW，浅色主题下就是纯白（实测 GCLP_HBRBACKGROUND=0x6）。
    // 去掉它两步走——先让 DWM 改用窗口表面的逐像素 alpha，再让客户区被擦成 alpha=0（GDI 不写 alpha 字节）。
    // 实测结论与失败过的通道见 doc/dev/runtime-pitfalls.md §17。
    public const uint WM_PAINT = 0x000F;
    public const uint WM_ERASEBKGND = 0x0014;

    /// <summary>stock object 里的黑色画刷。GDI 填色不写 alpha 字节，正好留下 0。</summary>
    public const int BLACK_BRUSH = 4;

    [StructLayout(LayoutKind.Sequential)]
    public struct MARGINS
    {
        public int Left;
        public int Right;
        public int Top;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct PAINTSTRUCT
    {
        public IntPtr hdc;
        public int fErase;
        public RECT rcPaint;
        public int fRestore;
        public int fIncUpdate;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)] public byte[] rgbReserved;
    }

    /// <summary>把整个窗口都算作"边框"（margins 全 -1）：DWM 从此按窗口表面的 alpha 合成，而不是当成不透明。</summary>
    [DllImport("dwmapi.dll")]
    public static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd, ref MARGINS margins);

    [DllImport("gdi32.dll")]
    public static extern IntPtr GetStockObject(int index);

    [DllImport("user32.dll")]
    public static extern IntPtr BeginPaint(IntPtr hwnd, out PAINTSTRUCT ps);

    [DllImport("user32.dll")]
    public static extern bool EndPaint(IntPtr hwnd, ref PAINTSTRUCT ps);

    [DllImport("user32.dll")]
    public static extern int FillRect(IntPtr hdc, ref RECT rect, IntPtr brush);

    [DllImport("user32.dll")]
    public static extern bool GetClientRect(IntPtr hwnd, out RECT rect);
}
