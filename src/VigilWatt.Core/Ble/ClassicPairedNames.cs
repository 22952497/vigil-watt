using System.Text;
using Microsoft.Win32;

namespace VigilWatt.Core.Ble;

/// <summary>
/// 从 Windows 的经典蓝牙**配对记录**里读设备友好名。
///
/// 位置：<c>HKLM\SYSTEM\CurrentControlSet\Services\BTHPORT\Parameters\Devices\&lt;mac12&gt;\Name</c>
/// （REG_BINARY，UTF-8 字节、尾部带 <c>\0</c>）。真机实测能拿到 "EDIFIER W820NB 双金标版"，
/// 而同一时刻 WinRT 的 AEP 属性只返回设备 ID —— 所以这是最可靠的兜底来源。
///
/// 纯读、零写入、不建立任何连接；整表缓存 30 秒，避免 watcher 事件密集时反复读注册表。
/// </summary>
internal static class ClassicPairedNames
{
    private const string DevicesKey = @"SYSTEM\CurrentControlSet\Services\BTHPORT\Parameters\Devices";
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(30);

    private static readonly object Gate = new();
    private static Dictionary<string, string> _cache = new();
    private static DateTime _cacheAt = DateTime.MinValue;

    /// <summary>按 MAC（12 位十六进制，大小写/分隔符不敏感）取配对名；没有则返回 null。</summary>
    public static string? TryGet(string? mac)
    {
        if (string.IsNullOrEmpty(mac)) return null;
        var key = mac.Replace(":", "").Replace("-", "");
        if (key.Length != 12) return null;

        return GetTable().TryGetValue(key, out var n) ? n : null;
    }

    private static Dictionary<string, string> GetTable()
    {
        lock (Gate)
        {
            if (DateTime.UtcNow - _cacheAt < CacheTtl) return _cache;

            var table = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                using var root = Registry.LocalMachine.OpenSubKey(DevicesKey);
                if (root != null)
                {
                    foreach (var sub in root.GetSubKeyNames())
                    {
                        if (sub.Length != 12) continue;
                        using var dev = root.OpenSubKey(sub);
                        if (dev?.GetValue("Name") is byte[] raw && raw.Length > 0)
                        {
                            var s = Encoding.UTF8.GetString(raw).TrimEnd('\0').Trim();
                            if (s.Length > 0) table[sub] = s;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Utils.Log.Warn($"读经典蓝牙配对名失败（退化为只显示 MAC）: {ex.Message}");
            }

            _cache = table;
            _cacheAt = DateTime.UtcNow;
            return table;
        }
    }
}
