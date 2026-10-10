namespace VigilWatt.App.Services;

/// <summary>
/// 窗口外观的 OS 能力判定（纯逻辑，便于单测与集中管理"哪个系统走哪条路"）。**小组件与任务栏贴片共用**。
///
/// 两边都要"无边框 + 圆角 + 阴影 + 半透明"，而两个系统能力差得很多：Win11 有 DWM 原生圆角 + 云母/Acrylic，
/// 交给系统即可；Win10 没有圆角 API，`SetWindowRgn` 硬裁既无抗锯齿、又会让 DWM 不再画投影，
/// 所以必须换成"透明窗口 + XAML 自绘圆角"。
///
/// **同名陷阱**：这里的"透明"是"让窗口表面不画像素"，Win10 靠 accent 策略、Win11 靠透明 SystemBackdrop，
/// 两条通道互不通用——Win11 上 accent 策略静默失效（见 runtime-pitfalls §17）。
///
/// 做成纯函数是为了让两条路径在同一处集中可见、不散落魔法数字。
/// 具体坑与实修记录见 doc/dev/runtime-pitfalls.md §14 / §17。
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
