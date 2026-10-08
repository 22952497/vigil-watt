namespace VigilWatt.App.Services;

/// <summary>
/// 桌面小组件的窗口层级：取值定义 + 收敛逻辑。
///
/// 三档语义（用户视角）：
/// - <see cref="Topmost"/>：浮在其它窗口上方，任何时候都看得见。
/// - <see cref="Normal"/>：普通窗口，会被之后激活的窗口正常遮挡（历史行为，默认值）。
/// - <see cref="Desktop"/>：压在其它窗口下方，只有桌面露出来时才可见 —— 等同"钉在桌面"。
///
/// 与 WindowBackdrop 一样把"取值判定"做成纯逻辑，避免魔法字符串散落，
/// 也便于设置页显示人话说明。
/// </summary>
public static class WidgetDisplayMode
{
    /// <summary>浮在其它窗口上方（always on top）。</summary>
    public const string Topmost = "Topmost";

    /// <summary>普通窗口层级：会被其它窗口遮挡。</summary>
    public const string Normal = "Normal";

    /// <summary>压在其它窗口下方：钉在桌面。</summary>
    public const string Desktop = "Desktop";

    /// <summary>把任意输入（含 null / 手改坏了的配置）收敛成三个合法取值之一。</summary>
    public static string Normalize(string? value) => value switch
    {
        Topmost => Topmost,
        Desktop => Desktop,
        _ => Normal,
    };

    /// <summary>设置页副标题用的一句话说明。</summary>
    public static string Describe(string? value) => Normalize(value) switch
    {
        Topmost => "置顶显示：始终浮在其它窗口之上",
        Desktop => "钉在桌面：被其它窗口压住，最小化后才看得见",
        _ => "普通窗口：会被其它窗口正常遮挡",
    };
}

/// <summary>
/// 小组件一屏最多显示几个设备。单独抽出来是为了让"合法范围"只有一处定义：
/// 设置页下拉框按它生成档位，AppSettings 按它收敛手改坏的配置。
/// </summary>
public static class WidgetMaxDevicesOptions
{
    public const int Min = 1;
    public const int Max = 5;
    public const int Default = 3;

    /// <summary>下拉框档位（1~5）。</summary>
    public static IReadOnlyList<int> Choices { get; } = Enumerable.Range(Min, Max - Min + 1).ToArray();

    /// <summary>把任意数值收敛到合法范围。</summary>
    public static int Clamp(int value) => Math.Clamp(value == 0 ? Default : value, Min, Max);
}
