using VigilWatt.Core.Models;
using VigilWatt.Core.Utils;

namespace VigilWatt.Core.Power;

/// <summary>
/// 本机电源（系统电池）监视器，仿 <see cref="Ble.BluetoothClassicWatcher"/> 的事件式数据源范式：
/// 不实现 <see cref="Plugins.IBatteryPlugin"/>（那套是为"认领 HID 接口"设计的，
/// 系统电量没有接口可认领、也不需要探测），由 <see cref="Devices.DeviceManager"/> 直接订阅。
///
/// 设备 Id 固定为 <see cref="DeviceId"/>，**永不变化**——历史曲线、托盘记账、
/// 低电量提醒的"每档只提醒一次"都靠它做键。
///
/// 充电状态（<see cref="ChargingState"/>）的推导是这里唯一的"逻辑"：
/// <c>GetSystemPowerStatus</c> 只告诉我们"是否接着交流电"，不告诉我们"是否正在充电"。
/// 于是用 <see cref="IsPluggedIn"/> 区分"在充电"和"已充满"：
/// 接电且未充满 = Charging，接电且已满 = Full，未接电 = Discharging。
/// </summary>
public sealed class SystemBatteryWatcher : IDisposable
{
    /// <summary>本机电源在设备体系里的稳定 Id。</summary>
    public const string DeviceId = "sys:battery";

    /// <summary>充满判定：接电状态下读到 100% 就当作已充满。</summary>
    private const int FullPercent = 100;

    private readonly object _sync = new();
    private DeviceStatus? _status;
    private bool _started;

    /// <summary>状态有实质变化时推送（新增/上线/电量变/充电标志变）。</summary>
    public event Action<DeviceStatus>? DeviceUpdated;

    public void Start()
    {
        lock (_sync)
        {
            if (_started) return;
            _started = true;
        }
        Log.Info("系统电量监视器已启动");
    }

    /// <summary>
    /// 读一次并推送变化。由 DeviceManager 的轮询尾部调用（和 <c>_classic.RefreshBattery()</c> 并排）。
    /// 冷却由外部轮询周期保证，这里不做节流。
    /// </summary>
    public void Refresh()
    {
        lock (_sync)
        {
            if (!_started) return;
        }

        var snapshot = SystemPowerReader.Read();
        Apply(snapshot);
    }

    /// <summary>
    /// 停止监控并撤下设备条目（用户在设置里关掉开关时调用）。
    ///
    /// 落点说明：DeviceManager 的插件禁用路径靠 <c>DropDevicesOf(插件名)</c> 按 Id 前缀撤条目，
    /// 系统电量不是插件、Id 前缀是 <c>sys:</c>，所以由 DeviceManager 直接按
    /// <see cref="DeviceId"/> 撤除。这里只负责把自己标记为"不再推送"，
    /// 否则 Refresh 会在关掉开关后把条目又加回来。
    /// </summary>
    public void Stop()
    {
        lock (_sync)
        {
            _started = false;
        }
        Log.Info("系统电量监视器已停止");
    }

    private void Apply(SystemPowerSnapshot snapshot)
    {
        // 无电池（台式机 / 电池被禁用）：完全不进列表。
        // 用户明确要的是"检测不到就不显示"，不要占位行。
        if (!snapshot.HasBattery)
        {
            if (_status != null)
            {
                _status.IsOnline = false;
                _status.LastUpdated = DateTimeOffset.Now;
                var offline = _status;
                _status = null;
                DeviceUpdated?.Invoke(offline);
            }
            return;
        }

        bool isNew = _status == null;
        var status = _status ??= new DeviceStatus
        {
            Id = DeviceId,
            Name = "本机电池",
            Source = DeviceSource.System,
            Kind = DeviceKind.Computer,
            Protocol = "系统电源 (GetSystemPowerStatus)",
        };

        var charging = DeriveCharging(snapshot);

        bool changed = isNew
                    || status.BatteryPercent != snapshot.Percent
                    || status.Charging != charging
                    || !status.IsOnline;

        // 无电池分支刚判断过，所以这里 Percent 为 null 只可能是"有电池但读数未知"，
        // 该情况保留上一次的百分比，不要用 null 覆盖掉已有读数。
        if (snapshot.Percent.HasValue) status.BatteryPercent = snapshot.Percent;
        status.Charging = charging;
        status.IsOnline = true;
        status.LastUpdated = DateTimeOffset.Now;

        if (changed)
        {
            if (isNew)
                Log.Info($"系统电量已接入: {status.DisplayName} {status.BatteryPercent?.ToString() ?? "--"}% {status.Charging}");
            DeviceUpdated?.Invoke(status);
        }
    }

    /// <summary>接电 + 满电 = 已充满；接电未满 = 充电中；未接电 = 放电中。
    /// 抽成 internal 纯函数是为了可单测——它是本类唯一的推导逻辑，
    /// 而真机上没法随时切换"接电/断电/充满"这些组合。</summary>
    internal static ChargingState DeriveCharging(SystemPowerSnapshot snapshot)
    {
        if (!snapshot.AcOnline)
            return snapshot.Percent is null ? ChargingState.Unknown : ChargingState.Discharging;

        if (snapshot.Percent == FullPercent) return ChargingState.Full;
        return snapshot.Percent is null ? ChargingState.Unknown : ChargingState.Charging;
    }

    /// <summary>当前快照（测试/诊断用）。</summary>
    public DeviceStatus? Current
    {
        get
        {
            lock (_sync) return _status;
        }
    }

    public void Dispose() => Stop();
}
