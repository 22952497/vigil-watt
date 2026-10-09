namespace VigilWatt.Core.Models;

/// <summary>
/// 设备来源的展示元数据（设备列表徽标 / 桌面小组件共用，纯逻辑可单测）。
/// 标签是**唯一事实来源**：两个 UI 面都调这里，避免各写一份 switch 而慢慢分叉。
/// 颜色不放这里——Core 不引 WinRT/UI 类型，配色在 App 层 <c>UiPalette.SourceTag</c>。
/// </summary>
public static class DeviceSourceInfo
{
    /// <summary>来源徽标短标签：蓝牙 / 2.4G / 本机。</summary>
    public static string ShortLabel(DeviceSource source) => source switch
    {
        DeviceSource.Ble or DeviceSource.BluetoothClassic => "蓝牙",
        DeviceSource.System => "本机",
        _ => "2.4G",
    };
}
