using VigilWatt.Core.Hid;
using VigilWatt.Core.Models;
using VigilWatt.Core.Utils;

namespace VigilWatt.Core.Plugins.Eweadn;

/// <summary>
/// 功 能： 前行者 (EWEADN) X87 键盘 2.4G 接收器插件——发官方原样电量查询帧。
/// 2026/10/18 13:40:00 Admin
/// 帧结构、接口选择与实测依据见 docs/hardware/protocols.md §7.1。
/// </summary>
public sealed class EweadnKeyboardPlugin : BatteryPluginBase
{
    /// <summary>插件开关的键，与 <c>AppSettings.PluginEnabled</c> 对应（插件开关即写入授权）。</summary>
    public const string SettingsKey = "EweadnKeyboard";

    /// <summary>前行者（EWEADN）USB 厂商 ID。</summary>
    public const ushort EweadnVid = 0x0C45;

    /// <summary>2.4G 接收器的产品 ID（官方 config.xml 的 2.4G 模式）。</summary>
    public const ushort ReceiverPid = 0xFEFE;

    /// <summary>厂商通道的 usage_page / usage：官方 config.xml 只认 MI_03，实测其 caps 为 0xFF60/0x0061。</summary>
    public const ushort VendorUsagePage = 0xFF60;

    public const ushort VendorUsage = 0x0061;

    public override string Name => SettingsKey;
    public override string DisplayName => "前行者键盘";
    public override DeviceKind Kind => DeviceKind.Keyboard;

    /// <summary>
    /// 键盘休眠时查询无应答，只探一次会永久漏掉——参与 15 秒快速发现节拍。
    /// 冷却必须小于该节拍，否则节拍被冷却吃掉（基类注释同此约束）。
    /// </summary>
    public override bool SupportsDiscoveryProbe => true;

    /// <summary>略小于快速发现节拍，保证每拍都能重探休眠后唤醒的键盘。</summary>
    protected override TimeSpan ProbeCooldown => TimeSpan.FromSeconds(12);

    /// <summary>
    /// 沉默不是本设备的离线判据：键盘休眠时查询确实无应答，但 dongle 常驻。
    /// 与北通同理——靠连续失败 + 熔断表达离线，不额外加沉默窗口。
    /// </summary>
    protected override OfflinePolicy Offline { get; } = new()
    {
        Silence = TimeSpan.Zero,
        CountPollFailures = false,
    };

    /// <summary>
    /// 候选接口：前行者 VID + 2.4G PID + 厂商 usage 0xFF60/0x61。
    /// MI_03 与 MI_04 同页同 usage，只有报文长度能区分——官方实发 33 字节，MI_04 是 65 字节（写它必失败）。
    /// </summary>
    protected override bool IsCandidate(HidDeviceInfo info)
    {
        if (info.VendorId != EweadnVid || info.ProductId != ReceiverPid) return false;
        if (info.UsagePage != VendorUsagePage || info.Usage != VendorUsage) return false;
        return info.OutputReportLength == EweadnKeyboardCodec.FrameLength;
    }

    /// <summary>探测 = 发一条官方电量查询，等响应；收不到返回 null 进冷却。</summary>
    protected override (string Protocol, int? Percent, ChargingState Charging)? Probe(HidDeviceHandle handle)
    {
        var percent = Query(handle, TimeSpan.FromSeconds(2));
        if (percent == null) return null;
        // 官方驱动把充电位固定传 0（不做充电判定），这里跟随官方：只给电量，充电态交给收尾规则。
        return (ProtocolName, percent, ChargingStateMap.FromChargingFlag(false, percent.Value));
    }

    /// <summary>轮询 = 发一条电量查询。写入被开关/熔断挡住时返回 null（计入失败，但不再写）。</summary>
    protected override (int? Percent, ChargingState Charging)? PollDevice(TrackedDevice dev)
    {
        if (!CanWrite(dev)) return null;
        var percent = Query(dev.Handle, TimeSpan.FromSeconds(1.5));
        if (percent == null) return null;
        return (percent, ChargingStateMap.FromChargingFlag(false, percent.Value));
    }

    /// <summary>协议名（电量通道本身；在线/离线由 IsOnline + 电量是否为空表达）。</summary>
    private const string ProtocolName = "eweadn-x87-20";

    /// <summary>
    /// 发一帧查询并读响应。写入长度必须等于接口声明的输出报文长度（官方实发 33 = MI_03 的 caps），
    /// 且该接口走 overlapped 异步 IO，同步读会永久阻塞。
    /// </summary>
    private static int? Query(HidDeviceHandle handle, TimeSpan window)
    {
        int length = handle.Info.OutputReportLength;
        if (length < EweadnKeyboardCodec.FrameLength)
        {
            Log.Info($"前行者键盘 输出报文长度异常: {length}，跳过: {handle.Info.DevicePath}");
            return null;
        }

        var frame = EweadnKeyboardCodec.BuildQuery();
        if (!handle.WriteOutput(frame, TimeSpan.FromMilliseconds(500)))
        {
            Log.Info($"前行者键盘 查询写失败: {handle.Info.DevicePath}");
            return null;
        }

        var deadline = DateTime.UtcNow + window;
        int logged = 0;
        while (DateTime.UtcNow < deadline)
        {
            var input = handle.ReadInput(TimeSpan.FromMilliseconds(300));
            if (input == null) continue;

            if (logged++ == 0)
                Log.Debug($"前行者键盘 收到报文: {Log.Hex(input)}");

            var percent = EweadnKeyboardCodec.ParseBattery(input);
            if (percent != null) return percent;
        }
        return null;
    }

    /// <summary>显示名：产品串是「2.4G Dongle」，对用户没意义，直接拼成「前行者 X87」。</summary>
    protected override string BuildDisplayName(HidDeviceInfo info) => "前行者 X87";
}
