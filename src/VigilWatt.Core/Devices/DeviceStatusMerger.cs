using VigilWatt.Core.Models;

namespace VigilWatt.Core.Devices;

/// <summary>
/// DeviceStatus 的原地合并规则（纯逻辑，可单测；DeviceManager.Upsert 与"墓碑接管"都走这里）。
/// </summary>
public static class DeviceStatusMerger
{
    /// <summary>
    /// 把 <paramref name="incoming"/> 的字段同步进已存在的 <paramref name="status"/>（原地改）。
    ///
    /// ⚠️ Kind / Protocol 必须允许"声明覆盖猜测"：离线墓碑（RestoreOfflineTombstones）先于插件建条目，
    /// Kind 只能按历史名字猜——雷柏/迈从/漫步者的显示名一个关键字都没有，猜出来全是 Unknown。
    /// 设备真正上线后插件会带着自己声明的 Kind（雷柏=鼠标、迈从=键盘）再来 Upsert，
    /// 若这里不同步 Kind，墓碑的错误猜测会在进程内冻结到永远，图标就丢了（2026-10-06 真机踩过）。
    /// 反向不成立：不声明类型的来源（BLE 监视器上报 Unknown）不得把已知类型洗掉，所以非 Unknown 才覆盖；
    /// Protocol 同理，空串视为"本次未声明"。
    /// </summary>
    public static void Apply(DeviceStatus status, DeviceStatus incoming)
    {
        status.Name = incoming.Name;
        if (incoming.Kind != DeviceKind.Unknown) status.Kind = incoming.Kind;
        status.BatteryPercent = incoming.BatteryPercent;
        status.Charging = incoming.Charging;
        if (incoming.Protocol.Length > 0) status.Protocol = incoming.Protocol;
        status.LastUpdated = incoming.LastUpdated;
        status.IsOnline = incoming.IsOnline;
    }
}
