namespace VigilWatt.App.Services;

/// <summary>
/// 桌面小组件的 OS 能力判定（纯逻辑，便于单测与集中管理"哪个系统走哪条路"）。
///
/// 背景：小组件是"无边框悬浮卡片"，视觉上要圆角 + 阴影 + 半透明材质。两个系统能力差得很多：
///
/// - **Windows 11 (build >= 22000)**：DWM 原生圆角（带抗锯齿）+ 云母/Acrylic 材质，
///   窗口按普通窗口处理即可，视觉由系统兜底。
/// - **Windows 10 (build < 22000)**：没有 `DWMWA_WINDOW_CORNER_PREFERENCE`，
///   只能 SetWindowRgn 硬裁——而 GDI 区域裁剪**没有抗锯齿**，圆角是大台阶；
///   更要命的是裁剪后 DWM 不再给窗口画投影，卡片"贴死"在桌面上、毫无层次。
///   所以 Win10 必须换一条路：把窗口做成透明分层窗口，圆角与背景**全部交给 XAML 渲染**
///   （WinUI 画圆角自带抗锯齿），材质用 DesktopAcrylicBackdrop 挂在透明窗口上。
///
/// 这里刻意把"要不要走兼容路径"做成纯函数，避免代码里散落魔法数字，也让两个系统
/// 的路径在同一处集中可见——改动时不会误伤另一个系统。
/// </summary>
public static class WidgetCompat
{
    /// <summary>Windows 11 首个正式版的 build 号。达到或超过即视为 Win11 及以上。</summary>
    public const int Windows11Build = 22000;

    /// <summary>
    /// 该 build 号是否需要走"透明窗口 + XAML 自绘圆角"的兼容路径。
    /// 只有 Win10 需要；Win11 及以上交给 DWM 原生处理，保持原有观感。
    /// </summary>
    public static bool NeedsTransparentChrome(int build) => build < Windows11Build;

    /// <summary>是否支持 DWM 原生窗口圆角（Win11+）。</summary>
    public static bool SupportsDwmRoundedCorner(int build) => build >= Windows11Build;
}
