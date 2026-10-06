namespace EasyBluetooth.Core.Plugins;

/// <summary>一次电量读数的判定结果。</summary>
public enum ReadingVerdict
{
    /// <summary>变化在合理范围内，正常采信。</summary>
    Accept,

    /// <summary>变化过大但已持续超过 <see cref="BatteryReadingFilter.PersistWindow"/>，认定为真实变化。</summary>
    AcceptConfirmed,

    /// <summary>变化过大，判为异常读数：本轮不采用（UI 与低电量判定都看不到这个值）。</summary>
    RejectAnomaly,

    /// <summary>数值本身无效（低于 <see cref="BatteryReadingFilter.MinValidPercent"/> 或 &gt;100），永不采信。</summary>
    InvalidValue,
}

/// <summary>
/// 电量读数异常过滤器（每台设备一个实例）。
///
/// **要解决的问题**：设备休眠/关机、或 dongle 偶发抽风时，会吐出一个和真实值差很远的读数
/// （真机 66% 时推 1%），直接采信就会误报低电量告警、把脏数据写进历史曲线。
///
/// **两条规则**：
/// 1. **数值有效性**：低于 <see cref="MinValidPercent"/>（默认 2，即 0/1）或大于 100 的读数直接判
///    <see cref="ReadingVerdict.InvalidValue"/>，永不采信——设备用 0/1 当"休眠占位帧"是行业惯例，
///    而低电量阈值是 20%，真掉到 1% 之前早就提醒过了，所以拒绝它不会漏掉任何真实告警。
/// 2. **变化幅度**：与「上次采信值」相比涨跌超过 <see cref="MaxStepPercent"/> 个百分点的读数先判
///    <see cref="ReadingVerdict.RejectAnomaly"/> 并**过滤掉**（本轮不采用）；只有它**持续出现**超过
///    <see cref="PersistWindow"/> 才认定为真实变化。读数回到基准附近时持续计时自动清零，
///    所以偶发抖动永远过不去。
///
/// 注意：基准 <see cref="LastAcceptedPercent"/> **不随设备离线而清空**——休眠伪读数经常出现在
/// "设备刚要掉线/刚回来"的窗口里，清了基准就等于放它进门。
/// </summary>
public sealed class BatteryReadingFilter
{
    /// <summary>允许的最大单次变化幅度（百分点，双向）。超过即判异常。</summary>
    public int MaxStepPercent { get; }

    /// <summary>异常读数需要持续多久才被采信为真实变化。</summary>
    public TimeSpan PersistWindow { get; }

    /// <summary>有效电量下界（含）。低于它的读数一律判无效——默认 2，即 0/1 都是休眠占位帧。</summary>
    public int MinValidPercent { get; }

    /// <summary>上次被采信的电量；-1 = 尚无基准（首帧直接采信）。</summary>
    public int LastAcceptedPercent { get; private set; } = -1;

    private DateTimeOffset? _anomalySince;

    public BatteryReadingFilter(int maxStepPercent, TimeSpan persistWindow, int minValidPercent = 2)
    {
        MaxStepPercent = Math.Max(1, maxStepPercent);
        PersistWindow = persistWindow > TimeSpan.Zero ? persistWindow : TimeSpan.FromMinutes(1);
        MinValidPercent = Math.Clamp(minValidPercent, 0, 100);
    }

    /// <summary>用探测阶段读到的值建立初始基准（不判定异常）。无效值当作"无基准"。</summary>
    public void Seed(int percent)
    {
        LastAcceptedPercent = IsValid(percent) ? percent : -1;
        _anomalySince = null;
    }

    /// <summary>判定一个读数是否可信；采信时自动把基准推进到该值。</summary>
    public ReadingVerdict Evaluate(int percent, DateTimeOffset now)
    {
        if (!IsValid(percent)) return ReadingVerdict.InvalidValue;

        if (LastAcceptedPercent < 0)
        {
            LastAcceptedPercent = percent;
            return ReadingVerdict.Accept;
        }

        if (Math.Abs(percent - LastAcceptedPercent) <= MaxStepPercent)
        {
            _anomalySince = null;          // 回到基准附近：持续计时清零
            LastAcceptedPercent = percent;
            return ReadingVerdict.Accept;
        }

        _anomalySince ??= now;             // 异常窗口起点（连续异常不重置）
        if (now - _anomalySince.Value < PersistWindow)
            return ReadingVerdict.RejectAnomaly;

        _anomalySince = null;
        LastAcceptedPercent = percent;
        return ReadingVerdict.AcceptConfirmed;
    }

    private bool IsValid(int percent) => percent >= MinValidPercent && percent <= 100;
}
