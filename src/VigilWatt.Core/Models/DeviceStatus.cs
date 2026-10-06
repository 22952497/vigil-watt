namespace VigilWatt.Core.Models;

public enum DeviceSource
{
    Ble,
    Hid24G,
    BluetoothClassic,
}

public enum ChargingState
{
    Unknown,
    Discharging,
    Charging,
    Full,
}

/// <summary>一台被监控设备的当前状态快照。</summary>
public class DeviceStatus
{
    /// <summary>稳定唯一 ID：BLE 用蓝牙地址，2.4G 用 HID 设备路径哈希 + 设备索引。</summary>
    public required string Id { get; init; }

    public required string Name { get; set; }

    public required DeviceSource Source { get; init; }

    /// <summary>设备类型（选图标用）。插件/监视器声明；未知时 UI 退回按名字猜。</summary>
    public DeviceKind Kind { get; set; } = DeviceKind.Unknown;

    /// <summary>0-100，未知时为 null（比如设备暂不可读）。</summary>
    public int? BatteryPercent { get; set; }

    public ChargingState Charging { get; set; } = ChargingState.Unknown;

    /// <summary>协议来源描述，如 "BLE GATT"、"Logitech HID++"、"MCHOSE V2"，方便排错。</summary>
    public string Protocol { get; set; } = "";

    public DateTimeOffset LastUpdated { get; set; } = DateTimeOffset.Now;

    public bool IsOnline { get; set; } = true;

    /// <summary>参与统计曲线时用于图例显示的短名。</summary>
    public string DisplayName => string.IsNullOrEmpty(Name) ? Id : Name;

    public override string ToString() => $"{DisplayName} {BatteryPercent?.ToString() ?? "?"}%";
}
