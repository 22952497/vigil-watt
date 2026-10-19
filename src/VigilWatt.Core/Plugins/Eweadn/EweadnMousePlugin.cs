using VigilWatt.Core.Hid;
using VigilWatt.Core.Models;
using VigilWatt.Core.Utils;

namespace VigilWatt.Core.Plugins.Eweadn;

/// <summary>
/// 功 能： 前行者 (EWEADN) S9 鼠标插件——走厂商接口 feature report 0x03 收发贝盈协议帧。
/// 2026/10/18 13:40:00 Admin
/// 帧结构、接口选择与实测依据见 docs/hardware/protocols.md §7.2。
/// </summary>
public sealed class EweadnMousePlugin : BatteryPluginBase
{
    /// <summary>插件开关的键，与 <c>AppSettings.PluginEnabled</c> 对应（插件开关即写入授权）。</summary>
    public const string SettingsKey = "EweadnMouse";

    /// <summary>前行者（EWEADN）鼠标的 USB 厂商 ID（S9 / S9 Pro 共用）。</summary>
    public const ushort EweadnVid = 0x372E;

    /// <summary>S9 / S9 Pro 2.4G 接收器的产品 ID。</summary>
    public const ushort MousePid = 0x1026;

    /// <summary>厂商通道的 usage_page / usage（官方 by.json 只认这一对，实测 IF2 命中）。</summary>
    public const ushort VendorUsagePage = 0xFF00;

    public const ushort VendorUsage = 0x0001;

    public override string Name => SettingsKey;
    public override string DisplayName => "前行者鼠标";
    public override DeviceKind Kind => DeviceKind.Mouse;

    /// <summary>官方 Hub 每次进页面才查一次；这里跟着主轮询节拍，不必更快。</summary>
    protected override TimeSpan ProbeCooldown => TimeSpan.FromSeconds(15);

    /// <summary>
    /// 沉默不是离线判据：鼠标休眠时 feature 读会返回旧值而非报错，靠沉默判离线会误报。
    /// 与北通同理，只用连续失败 + 熔断表达离线。
    /// </summary>
    protected override OfflinePolicy Offline { get; } = new()
    {
        Silence = TimeSpan.Zero,
        CountPollFailures = false,
    };

    /// <summary>候选接口：前行者 VID + S9 PID + 厂商 usage 0xFF00/0x0001（该接口才有 feature report 0x03）。</summary>
    protected override bool IsCandidate(HidDeviceInfo info)
    {
        if (info.VendorId != EweadnVid || info.ProductId != MousePid) return false;
        return info.UsagePage == VendorUsagePage && info.Usage == VendorUsage;
    }

    /// <summary>探测 = 发 BASICS 查询读一次；收不到返回 null 进冷却。</summary>
    protected override (string Protocol, int? Percent, ChargingState Charging)? Probe(HidDeviceHandle handle)
    {
        var read = Query(handle);
        if (read == null) return null;
        return (ProtocolName, read.Value.Percent, ChargingStateMap.FromChargingFlag(read.Value.Charging, read.Value.Percent));
    }

    /// <summary>轮询 = 发 BASICS 查询读电量。写入被开关/熔断挡住时返回 null（计入失败，但不再写）。</summary>
    protected override (int? Percent, ChargingState Charging)? PollDevice(TrackedDevice dev)
    {
        if (!CanWrite(dev)) return null;
        var read = Query(dev.Handle);
        if (read == null) return null;
        return (read.Value.Percent, ChargingStateMap.FromChargingFlag(read.Value.Charging, read.Value.Percent));
    }

    /// <summary>协议名（电量通道本身；在线/离线由 IsOnline + 电量是否为空表达）。</summary>
    private const string ProtocolName = "eweadn-s9-feature03";

    /// <summary>
    /// 发一帧 BASICS 查询并读回同 ID 的 feature 响应。
    /// 该通道是「先写 feature、再读 feature」的邮箱式交互，两步缺一不可（实测写后读到 16 字节应答）。
    /// </summary>
    private static (int Percent, bool Charging)? Query(HidDeviceHandle handle)
    {
        if (handle.Info.FeatureReportLength < EweadnMouseCodec.FrameLength + 1)
        {
            Log.Info($"前行者鼠标 feature 报文长度异常: {handle.Info.FeatureReportLength}，跳过: {handle.Info.DevicePath}");
            return null;
        }

        var frame = EweadnMouseCodec.BuildBasicsQuery();
        if (!handle.SetFeature(EweadnMouseCodec.FeatureReportId, frame))
        {
            Log.Info($"前行者鼠标 查询写失败: {handle.Info.DevicePath}");
            return null;
        }

        // 设备侧处理需要几毫秒；官方 Hub 也留了延迟再读（transport delay）。
        Thread.Sleep(20);
        var response = handle.GetFeature(EweadnMouseCodec.FeatureReportId);
        if (response == null)
        {
            Log.Info($"前行者鼠标 读 feature 失败: {handle.Info.DevicePath}");
            return null;
        }

        var parsed = EweadnMouseCodec.ParseBasics(response);
        if (parsed == null)
        {
            Log.Debug($"前行者鼠标 响应无法解析: {Log.Hex(response)}");
            return null;
        }
        return parsed;
    }

    /// <summary>显示名：产品串是「EWEADN S9」，去掉厂商前缀后拼成「前行者 S9」。</summary>
    protected override string BuildDisplayName(HidDeviceInfo info)
    {
        var product = info.Product;
        if (string.IsNullOrWhiteSpace(product)) return "前行者 S9";
        var name = product.Replace("EWEADN", "", StringComparison.OrdinalIgnoreCase).Trim();
        return string.IsNullOrEmpty(name) ? "前行者 S9" : $"前行者 {name}";
    }
}
