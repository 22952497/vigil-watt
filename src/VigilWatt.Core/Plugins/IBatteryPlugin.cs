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

    /// <summary>从扫描结果中认领属于自己的设备（幂等：新设备补上，消失的设备标记离线并释放认领）。
    /// 认领前必须经 claims.TryClaim，防止与其他插件抢同一接口。返回认领的设备。</summary>
    IReadOnlyList<DeviceStatus> Attach(IReadOnlyList<HidDeviceInfo> availableDevices, DeviceClaimRegistry claims);

    /// <summary>轮询一轮电量，返回本次有更新的设备状态。</summary>
    IReadOnlyList<DeviceStatus> Poll();

    /// <summary>释放打开的句柄并释放全部认领（插件被禁用或退出时调用）。</summary>
    void Detach();
}
