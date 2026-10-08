using VigilWatt.Core.Hid;
using VigilWatt.Core.Models;

namespace VigilWatt.Core.Plugins;

/// <summary>
/// 2.4G 品牌插件接口。每个品牌一套私有协议，由 DeviceManager 每轮扫描时喂给它 HID 接口列表，
/// 插件认领匹配的设备并负责轮询电量。DeviceManager 通过反射自动发现实现类（新增品牌 = 加一个类文件）。
/// </summary>
public interface IBatteryPlugin
{
    /// <summary>品牌标识，与 AppSettings.PluginEnabled 的 key 对应。</summary>
    string Name { get; }

    string DisplayName { get; }

    /// <summary>本插件处理的设备类型（UI 选图标用）。拿不准就 Unknown，UI 会退回按名字猜。</summary>
    DeviceKind Kind { get; }

    /// <summary>
    /// 是否允许 DeviceManager 用更快的节拍（15 秒）额外跑一轮"只认领、不轮询"的发现。
    /// **只有纯被动零写入的插件可以返回 true**（如雷柏鼠标：设备只在用户动它时才发心跳，
    /// 而 60 秒的主轮询常常正好撞上它在睡觉，探测落空后要等整整一个主周期才发现）。
    /// 会写帧的插件用 <see cref="SupportsDiscoveryProbe"/>，别用这个。
    /// </summary>
    bool SupportsFastDiscovery { get; }

    /// <summary>
    /// 探测要写帧、但仍希望参与 15 秒发现节拍的插件（如迈从键盘：唤醒/开机后最坏要等 60 秒主轮询
    /// 才被认领，用户看到的是"键盘开了半天才显示在线"）。
    ///
    /// 与 <see cref="SupportsFastDiscovery"/> 的区别与**安全前提**：这条路径会真的发探测帧，
    /// 所以**写入频率必须由插件自己的 <c>ProbeCooldown</c> 兜住**——把它设成明显小于 15 秒
    /// （并留出一次探测本身的耗时），保证"每一拍都允许重探"的同时，最坏写入 ≈ 3 帧/次探测 × 4 次/分钟。
    /// 认领成功后 <c>Attach</c> 对已知设备直接返回、**零写入**，所以这个代价只出现在设备不响应期间。
    /// 默认 false；不确定就别开（会写帧的插件默认只吃 60 秒主轮询）。
    /// </summary>
    bool SupportsDiscoveryProbe { get; }

    /// <summary>从扫描结果中认领属于自己的设备（幂等：新设备补上，消失的设备标记离线并释放认领）。
    /// 认领前必须经 claims.TryClaim，防止与其他插件抢同一接口。返回认领的设备。</summary>
    IReadOnlyList<DeviceStatus> Attach(IReadOnlyList<HidDeviceInfo> availableDevices, DeviceClaimRegistry claims);

    /// <summary>轮询一轮电量，返回本次有更新的设备状态。</summary>
    IReadOnlyList<DeviceStatus> Poll();

    /// <summary>
    /// 插件在轮询之外主动推送的状态更新（如罗技 HID++ 电量/充电自发通知）。
    /// 不推送的插件可永不触发；DeviceManager 订阅后走与 Poll 相同的 Upsert。
    /// </summary>
    event Action<DeviceStatus>? StatusPushed;

    /// <summary>释放打开的句柄并释放全部认领（插件被禁用或退出时调用）。</summary>
    void Detach();
}
