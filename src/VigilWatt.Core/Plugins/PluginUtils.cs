namespace VigilWatt.Core.Plugins;

/// <summary>插件间共享的小工具。</summary>
public static class PluginUtils
{
    /// <summary>FNV-1a 64：把设备路径映射成稳定短 ID（重启、重扫后不变，用于 DeviceStatus.Id）。</summary>
    public static string StableHash(string s)
    {
        ulong h = 1469598103934665603;
        foreach (var c in s) { h ^= c; h *= 1099511628211; }
        return h.ToString("X16");
    }
}
