using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace VigilWatt.Core.Ble;

/// <summary>
/// 经典蓝牙音频设备（A2DP/HFP 耳机、音箱）的电量读取。
///
/// Windows 11 设置页给耳机/音箱显示的电量，来自 PnP 设备属性
///   DEVPKEY_Device_BatteryLevel = {104EA319-6EE2-4701-BD47-8DDBF425BBE5}, 2 （DEVPROP_TYPE_BYTE）
/// 该属性挂在 **HFP 免手操作音频网关（Hands-Free AG）** 设备节点上，实例 ID 形如
///   BTHENUM\{0000111E-0000-1000-8000-00805F9B34FB}_LOCALMFG&amp;005D\7&amp;1D3D8B&amp;0&amp;0CAEBDC6CDA7_C00000000
/// 尾部 12 位十六进制就是设备 MAC（这里 0C:AE:BD:C6:CD:A7）。
///
/// 只能用 SetupAPI 读设备节点属性；DeviceInformation 的 AEP 属性
/// （System.Devices.AepBattery_Level / System.Devices.Aep.Battery_Level）在本机全部无效
/// （见 doc/dev/runtime-pitfalls.md §3）。本类**纯读**：只枚举设备节点、读系统缓存属性，
/// 不打开设备、不建立连接、零写入，也不依赖耳机是否支持任何私有协议。
/// </summary>
internal static class ClassicAudioBattery
{
    private const uint DIGCF_PRESENT = 0x2;
    private const uint DIGCF_ALLCLASSES = 0x4;
    private const uint DEVPROP_TYPE_BYTE = 0x03;
    private const uint DEVPROP_TYPE_UINT16 = 0x05;
    private const uint DEVPROP_TYPE_UINT32 = 0x07;

    private static readonly IntPtr InvalidHandle = new(-1);

    /// <summary>经典蓝牙（BR/EDR）设备节点前缀，用来把 BLE 节点排除在外。</summary>
    private const string BthEnumPrefix = @"BTHENUM\";

    /// <summary>电量属性在 5 秒内复用，避免 watcher 事件密集时反复枚举整棵设备树。</summary>
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(5);
    private static readonly object CacheLock = new();
    private static Dictionary<string, int>? _cache;
    private static DateTimeOffset _cacheAt;

    private static readonly Regex AepMacRegex = new(
        @"(?:[0-9A-Fa-f]{2}:){5}[0-9A-Fa-f]{2}", RegexOptions.Compiled);

    /// <summary>实例 ID 末段里 <c>&amp;0CAEBDC6CDA7_C00000000</c> 形式的 12 位 MAC。</summary>
    private static readonly Regex DevNodeMacRegex = new(
        @"&([0-9A-Fa-f]{12})", RegexOptions.Compiled);

    [StructLayout(LayoutKind.Sequential)]
    private struct DevPropKey
    {
        public Guid FmtId;
        public uint Pid;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SP_DEVINFO_DATA
    {
        public uint cbSize;
        public Guid ClassGuid;
        public uint DevInst;
        public IntPtr Reserved;
    }

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern IntPtr SetupDiGetClassDevsW(IntPtr classGuid, string? enumerator, IntPtr hwndParent, uint flags);

    [DllImport("setupapi.dll", ExactSpelling = true, SetLastError = true)]
    private static extern bool SetupDiEnumDeviceInfo(IntPtr deviceInfoSet, uint memberIndex, ref SP_DEVINFO_DATA deviceInfoData);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern bool SetupDiGetDeviceInstanceIdW(IntPtr deviceInfoSet, ref SP_DEVINFO_DATA deviceInfoData,
        StringBuilder deviceInstanceId, uint deviceInstanceIdSize, out uint requiredSize);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern bool SetupDiGetDevicePropertyW(IntPtr deviceInfoSet, ref SP_DEVINFO_DATA deviceInfoData,
        ref DevPropKey propertyKey, out uint propertyType, byte[]? propertyBuffer, uint propertyBufferSize,
        out uint requiredSize, uint flags);

    [DllImport("setupapi.dll", ExactSpelling = true, SetLastError = true)]
    private static extern bool SetupDiDestroyDeviceInfoList(IntPtr deviceInfoSet);

    /// <summary>读本机所有经典蓝牙设备节点的电量，返回 归一化MAC(12位大写) → 百分比。</summary>
    public static IReadOnlyDictionary<string, int> ReadBatteryByMac()
    {
        lock (CacheLock)
        {
            if (_cache != null && DateTimeOffset.Now - _cacheAt < CacheTtl) return _cache;
            _cache = ReadUncached();
            _cacheAt = DateTimeOffset.Now;
            return _cache;
        }
    }

    private static Dictionary<string, int> ReadUncached()
    {
        var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        IntPtr set = SetupDiGetClassDevsW(IntPtr.Zero, null, IntPtr.Zero, DIGCF_PRESENT | DIGCF_ALLCLASSES);
        if (set == IntPtr.Zero || set == InvalidHandle) return map;

        try
        {
            var did = new SP_DEVINFO_DATA { cbSize = (uint)Marshal.SizeOf<SP_DEVINFO_DATA>() };
            var sb = new StringBuilder(512);

            for (uint i = 0; SetupDiEnumDeviceInfo(set, i, ref did); i++)
            {
                sb.Clear();
                if (!SetupDiGetDeviceInstanceIdW(set, ref did, sb, (uint)sb.Capacity, out _)) continue;

                string instanceId = sb.ToString();
                if (!instanceId.StartsWith(BthEnumPrefix, StringComparison.OrdinalIgnoreCase)) continue;

                string? mac = MacFromInstanceId(instanceId);
                if (mac == null) continue;

                int? percent = ReadBatteryPercent(set, ref did);
                if (percent is > 0 and <= 100) map[mac] = percent.Value;
            }
        }
        catch
        {
            // 枚举失败按"没读到"处理，绝不影响其他通道
        }
        finally
        {
            SetupDiDestroyDeviceInfoList(set);
        }

        return map;
    }

    private static int? ReadBatteryPercent(IntPtr set, ref SP_DEVINFO_DATA did)
    {
        var key = new DevPropKey
        {
            FmtId = new Guid("104EA319-6EE2-4701-BD47-8DDBF425BBE5"),
            Pid = 2,
        };

        // 第一次调用拿长度（返回 ERROR_INSUFFICIENT_BUFFER 属正常）
        SetupDiGetDevicePropertyW(set, ref did, ref key, out _, null, 0, out uint required, 0);
        if (required == 0) return null;

        var buf = new byte[required];
        if (!SetupDiGetDevicePropertyW(set, ref did, ref key, out uint type, buf, required, out _, 0)) return null;

        return type switch
        {
            DEVPROP_TYPE_BYTE => buf[0],
            DEVPROP_TYPE_UINT16 => BitConverter.ToUInt16(buf, 0),
            DEVPROP_TYPE_UINT32 => (int)BitConverter.ToUInt32(buf, 0),
            _ => null,
        };
    }

    /// <summary>从实例 ID 里取设备 MAC（末段的 <c>&amp;&lt;12位&gt;</c>）。</summary>
    private static string? MacFromInstanceId(string instanceId)
    {
        string? last = null;
        foreach (Match m in DevNodeMacRegex.Matches(instanceId)) last = m.Groups[1].Value;
        return last?.ToUpperInvariant();
    }

    /// <summary>
    /// 从 AEP 设备 ID 里取设备 MAC。经典蓝牙 AEP ID 形如
    /// <c>Bluetooth#Bluetooth1c:ce:51:23:28:7b-0c:ae:bd:c6:cd:a7</c>（前半是适配器，后半才是设备）。
    /// </summary>
    public static string? MacFromAepId(string aepId)
    {
        string? last = null;
        foreach (Match m in AepMacRegex.Matches(aepId)) last = m.Value;
        return last?.Replace(":", "").Replace("-", "").ToUpperInvariant();
    }
}
