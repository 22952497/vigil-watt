using System.Text.RegularExpressions;

namespace EasyBluetooth.Core.Ble;

/// <summary>
/// 经典蓝牙设备显示名的**纯逻辑**（可单测，见 src/tests/EasyBluetooth.Core.Tests/）。
///
/// 背景（真机 2026-10-06 实测）：WinRT 的 <c>DeviceInformation.Name</c> /
/// <c>System.ItemNameDisplay</c> 在系统还没解析出设备友好名时，会**直接把 AEP 的设备 ID 当名字返回**，
/// 形如 <c>Bluetooth#Bluetooth1c:ce:51:23:28:7b-0c:ae:bd:c6:cd:a7</c>。
/// 旧代码照单全收，于是列表里那台耳机就显示成一长串 ID。
/// 这里负责识别这种"假名字"，让调用方有机会降级到注册表配对名 / 格式化 MAC。
/// </summary>
internal static class ClassicDeviceName
{
    /// <summary>裸 MAC 形态：<c>0c:ae:bd:c6:cd:a7</c> 或 <c>0C-AE-BD-C6-CD-A7</c>。</summary>
    private static readonly Regex MacLike = new(
        @"^[0-9a-fA-F]{2}([:-][0-9a-fA-F]{2}){5}$", RegexOptions.Compiled);

    /// <summary>是不是"占位名"（空、AEP 设备 ID、设备接口路径、裸 MAC）——这些都不能当显示名用。</summary>
    public static bool IsPlaceholder(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return true;
        string n = name.Trim();

        if (n.Contains("Bluetooth#", StringComparison.OrdinalIgnoreCase)) return true; // AEP 设备 ID
        if (n.StartsWith(@"\\?\", StringComparison.Ordinal)) return true;              // 设备接口路径
        if (n.Contains("&0&", StringComparison.Ordinal)) return true;                  // 接口路径片段
        if (MacLike.IsMatch(n)) return true;                                          // 裸 MAC

        return false;
    }

    /// <summary>MAC 文本（大写、冒号分隔）：<c>0c:ae:bd:c6:cd:a7</c> → <c>0C:AE:BD:C6:CD:A7</c>。</summary>
    public static string MacText(string mac) => mac.ToUpperInvariant().Replace('-', ':');

    /// <summary>任何名字都拿不到时的兜底显示名（总比一长串 AEP ID 强）。</summary>
    public static string FallbackName(string? mac)
        => string.IsNullOrEmpty(mac) ? "经典蓝牙设备" : $"经典蓝牙设备 {MacText(mac)}";
}
