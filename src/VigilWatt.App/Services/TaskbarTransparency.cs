using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using VigilWatt.Core.Utils;
using WinRT;
using System.Runtime.InteropServices;
using SystemCompositor = Windows.UI.Composition.Compositor;

namespace VigilWatt.App.Services;

/// <summary>
/// 把任务栏贴片的窗口表面做成"什么都不画"，只留 XAML 画的前景。WinUI3 的窗口有**两层**背景：
/// 内容岛 Visual 的底色（换透明 SystemBackdrop 即可）和 Win32 窗口自己擦出来的底色（才是"白框"）。
/// 后者要先用 <c>DwmExtendFrameIntoClientArea</c> 让 DWM 改按逐像素 alpha 合成，再自己接管
/// WM_ERASEBKGND/WM_PAINT 把客户区擦成 alpha=0。光删 XAML 里的 Background 两层都去不掉。
/// 实测过的失败通道与实修记录见 docs/dev/runtime-pitfalls.md §17。
/// </summary>
public static class TaskbarTransparency
{
    /// <summary>系统合成器（内容岛系统背板专用）。有线程亲和性，建一次复用（贴片可反复开关）。</summary>
    private static readonly Lazy<SystemCompositor> BackdropCompositor = new(() => new());

    /// <summary>必须用静态字段压住：交给非托管代码的回调一旦被 GC 回收，窗口再来消息就是访问已释放内存。</summary>
    private static readonly Native.WndProcDele SurfaceProc = SurfaceWndProc;

    /// <summary>子类化前窗口原本的窗口过程。贴片同一时刻只有一个，所以存单份即可。</summary>
    private static IntPtr _prevProc;

    /// <summary>可重复调用：构造函数里一次，`Root.Loaded` 再一次——背板要落到窗口真正接上的内容岛上，构造期那一次不保证生效。</summary>
    public static void Apply(Window window)
    {
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
        if (hwnd == IntPtr.Zero) return;

        if (WidgetCompat.NeedsTransparentChrome(Environment.OSVersion.Version.Build))
        {
            if (!Native.MakeWindowTransparent(hwnd))
                Log.Warn("任务栏贴片透明化失败（Win10 accent 通道）：窗口将带不透明底色");
            return;
        }

        Native.ApplyBorderlessChrome(hwnd);
        ClearWin32Background(hwnd);

        try
        {
            // 画刷必须出自**系统合成器**：系统背板由系统合成器在内容岛后面绘制，
            // 拿 WinUI 自己的 Window.Compositor 造的画刷类型对不上（编译期 CS0029，好在不会拖到运行时）。
            var brush = BackdropCompositor.Value.CreateColorBrush(Windows.UI.Color.FromArgb(0x00, 0x00, 0x00, 0x00));
            window.As<ICompositionSupportsSystemBackdrop>().SystemBackdrop = brush;
        }
        catch (Exception ex)
        {
            Log.Warn($"任务栏贴片透明背板设置失败：{ex.Message}");
        }
    }

    /// <summary>去掉 Win32 那一层底色。**按窗口过程是否已是自己来判断装没装过**：重复装会把我们自己的过程再包一层，消息一进来就无限递归。</summary>
    private static void ClearWin32Background(IntPtr hwnd)
    {
        var ours = Marshal.GetFunctionPointerForDelegate(SurfaceProc);
        if (Native.GetWindowLongPtr(hwnd, Native.GWLP_WNDPROC) == ours) return;

        var margins = new Native.MARGINS { Left = -1, Right = -1, Top = -1, Bottom = -1 };
        Native.DwmExtendFrameIntoClientArea(hwnd, ref margins);

        _prevProc = Native.SetWindowLongPtr(hwnd, Native.GWLP_WNDPROC, ours);
    }

    /// <summary>接管擦背景与重画：默认处理会用类背景刷（COLOR_WINDOW，浅色主题纯白）把客户区铺满。</summary>
    private static IntPtr SurfaceWndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == Native.WM_ERASEBKGND) return 1;

        if (msg == Native.WM_PAINT)
        {
            var hdc = Native.BeginPaint(hwnd, out var ps);
            if (hdc != IntPtr.Zero)
            {
                Native.GetClientRect(hwnd, out var rc);
                Native.FillRect(hdc, ref rc, Native.GetStockObject(Native.BLACK_BRUSH));
                Native.EndPaint(hwnd, ref ps);
            }
            return 0;
        }

        return Native.CallWindowProcW(_prevProc, hwnd, msg, wParam, lParam);
    }
}
