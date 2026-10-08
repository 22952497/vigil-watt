namespace VigilWatt.Core.Plugins;

/// <summary>
/// 插件"离线判定策略"（第③层）参数的集中载体 —— 与协议事实、安全声明分开。
///
/// 插件配置分三层（见 doc/project/code-flow.md §4.2）：
/// ① 协议事实（帧结构 / 有没有查询命令 / 有没有连接状态字段）——**必须 per-brand**，改不了；
/// ② 安全声明（要不要写帧、用户开关、探测冷却）——**必须 per-brand**，代价各不相同；
/// ③ 离线判定策略（本类型）——机制是同一套，只是参数不同，所以收敛到一个对象里，每插件一行。
///
/// 三条判据互相独立，谁先满足谁生效：
/// - <see cref="Silence"/>：接口彻底不说话（兜底）；
/// - <see cref="Confirm"/>：设备自己说断链了，但要延迟确认（滤瞬时抖动）；
/// - <see cref="MaxFailures"/> + <see cref="CountPollFailures"/>：主动查询发过帧却没应答。
/// 另有"立即核实"通道见 <see cref="BatteryPluginBase.RequestConfirm"/>。
/// </summary>
public sealed record OfflinePolicy
{
    /// <summary>基类默认：不按沉默判离线、不延迟确认，但把"轮询空读"当失败（传统查询型插件）。</summary>
    public static readonly OfflinePolicy Default = new();

    /// <summary>
    /// 被动"沉默"兜底：被动监听超过这么久**收不到任何报文** ⇒ 离线。
    /// 判据是"接口有没有在说话"，不是"能否解析出本协议帧"。
    /// <see cref="TimeSpan.Zero"/> = 不按沉默判离线（推送稀疏、或压根没有推流的协议）。
    /// </summary>
    public TimeSpan Silence { get; init; } = TimeSpan.Zero;

    /// <summary>
    /// 延迟确认窗口：插件标记"疑似离线"候选后，等这么久仍没有恢复证据才真判离线。
    /// <see cref="TimeSpan.Zero"/> = 不启用。
    ///
    /// 存在的理由：有些离线证据是**瞬时**的（典型是雷柏状态帧 [18].bit7 = RF 链路是否连接，
    /// 鼠标断电会翻 0，但移出范围 / 受干扰同样会翻）。直接据此判离线会让设备列表闪一下；
    /// 延迟确认让后续的恢复帧有机会把它撤销（宁可漏判不可误判）。
    ///
    /// 检查点是**被动监听的空闲 tick**（约 500ms 一次），不是 60 秒轮询——
    /// 所以本窗口 ≈ 实际响应延迟，不会被轮询相位放大成"窗口 + 最多 60 秒"。
    /// </summary>
    public TimeSpan Confirm { get; init; } = TimeSpan.Zero;

    /// <summary>
    /// 连续多少次"发过帧却没应答" ⇒ 熔断停写 + 标离线。
    /// 只在 <see cref="CountPollFailures"/> 为 true、或插件自己在 <c>PollDevice</c> 里调用基类
    /// 失败记账入口（<c>ReportPollFailure</c>）时才用得上。
    /// </summary>
    public int MaxFailures { get; init; } = 3;

    /// <summary>
    /// 轮询返回 null 是否**直接**计入失败。
    ///
    /// 默认 true = "轮询发了帧、没读数就是失联"（传统查询型插件，如 Elan）。
    /// 覆盖成 false 的两种情况：
    /// (a) 协议根本没有查询命令，轮询空读是**协议形态**而非失联（雷柏）；
    /// (b) null 与"未开写入 / 熔断中 / 纯推送协议"不可区分，只有插件自己知道这一轮发没发帧
    ///     （迈从、北通）——这些插件改用基类的失败记账入口自己报。
    ///
    /// 这一项**必须**与"要不要起被动监听"分开：那是个机制事实（我起后台只读线程），
    /// 这是个策略推断（所以轮询空读不算失败）。曾经被合成一个布尔量复用，导致迈从
    /// 键盘关机后永远不显示离线（2026-10-08 用户复报）。
    /// </summary>
    public bool CountPollFailures { get; init; } = true;

    /// <summary>
    /// 离线候选是否已超过 <see cref="Confirm"/> 窗口。
    /// <paramref name="candidateSince"/> 为 <c>default</c> 表示当前没有候选；
    /// <see cref="Confirm"/> ≤ 0 表示该插件未启用延迟确认。两者都返回 false。
    ///
    /// 边界取**严格大于**：窗口的语义是"至少要等这么久"，窗口内到点的那一次 tick 不算数。
    /// </summary>
    public bool IsConfirmExpired(DateTimeOffset candidateSince, DateTimeOffset now)
        => candidateSince != default
        && Confirm > TimeSpan.Zero
        && now - candidateSince > Confirm;
}
