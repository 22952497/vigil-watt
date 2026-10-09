using System.Runtime.InteropServices;

namespace VigilWatt.App.Services;

internal static class Native
{
    public delegate IntPtr WndProcDele(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    public const int GWL_EXSTYLE = -20;
    public const int GWLP_WNDPROC = -4;
    public const int WS_EX_TOOLWINDOW = 0x80;

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

    // ---- 窗口圆角（仅 Win11）----
    // ⚠ Win10 **不要**用 SetWindowRgn 裁圆角：WinUI 3 的内容走 DirectComposition，SetWindowRgn
    // 只裁传统 GDI 区域、对 DComp 内容完全无效（实测"设了区域仍是直角"）。
    // Win10 的圆角靠"透明窗口 + XAML 自绘"，见 WidgetWindow.ApplyWin10Chrome。
    public const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    public const int DWMWCP_ROUND = 2;

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
    /// 其余部分是真正的桌面。仅 Win10 圆角方案使用；Win11 有 DWM 原生圆角，不需要。
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
}
