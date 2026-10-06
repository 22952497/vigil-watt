using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace EasyBluetooth.Core.Hid;

/// <summary>
/// Win32 原生声明：CfgMgr32 枚举 HID 接口 + kernel32/hid.dll 设备 I/O。
/// 不用 WinRT 的 Windows.Devices.HumanInterfaceDevice，因为系统级 collection 被排除且私有协议访问受限。
/// </summary>
internal static class NativeHid
{
    public const uint CM_GET_DEVICE_INTERFACE_LIST_PRESENT = 0;

    public const uint GENERIC_READ = 0x80000000;
    public const uint GENERIC_WRITE = 0x40000000;
    public const uint FILE_SHARE_READ = 0x1;
    public const uint FILE_SHARE_WRITE = 0x2;
    public const uint OPEN_EXISTING = 3;
    public const uint FILE_FLAG_OVERLAPPED = 0x40000000;

    public const int ERROR_ACCESS_DENIED = 5;
    public const int ERROR_DEVICE_IN_USE = 2404; // Win32 error code for exclusive HID lock

    public static readonly Guid GuidDeviceInterfaceHid = new("4d1e55b2-f16f-11cf-88cb-001111000030");

    [DllImport("CfgMgr32.dll", CharSet = CharSet.Unicode)]
    public static extern int CM_Get_Device_Interface_List_SizeW(
        out uint size, ref Guid interfaceClass, string? deviceId, uint flags);

    [DllImport("CfgMgr32.dll", CharSet = CharSet.Unicode)]
    public static extern int CM_Get_Device_Interface_ListW(
        ref Guid interfaceClass, string? deviceId, [Out] char[] buffer, uint size, uint flags);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern SafeFileHandle CreateFileW(
        string fileName, uint desiredAccess, uint shareMode, IntPtr securityAttributes,
        uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

    [DllImport("hid.dll", SetLastError = true)]
    public static extern bool HidD_GetAttributes(
        SafeFileHandle handle, ref HIDD_ATTRIBUTES attributes);

    [DllImport("hid.dll", SetLastError = true)]
    public static extern bool HidD_GetPreparsedData(SafeFileHandle handle, out IntPtr preparsedData);

    [DllImport("hid.dll", SetLastError = true)]
    public static extern bool HidD_FreePreparsedData(IntPtr preparsedData);

    [DllImport("hid.dll", SetLastError = true)]
    public static extern int HidP_GetCaps(IntPtr preparsedData, ref HIDP_CAPS caps);

    /// <summary>HidP_* 的成功返回码（NTSTATUS，注意不是 0）。</summary>
    public const int HIDP_STATUS_SUCCESS = 0x00110000;

    // ---------------------------------------------------------------- report ID 读取
    // 用户态**没有**拿原始报告描述符的 API：hid.dll 不导出 HidP_GetReportDescriptor
    // （实测 AttributeError），hidapi 的 get_report_descriptor 其实是拿 preparsed data
    // 自己重建描述符。所以改用 caps 里的 ReportID 字段——它是 HID 规范里 report ID 的
    // 唯一权威来源，且 HidP_GetCaps/HidP_GetButtonCaps 本机实测可用（status=0x00110000）。

    public enum HIDP_REPORT_TYPE { HidP_Input = 0, HidP_Output = 1, HidP_Feature = 2 }

    [DllImport("hid.dll", SetLastError = true)]
    public static extern int HidP_GetButtonCaps(
        HIDP_REPORT_TYPE reportType, IntPtr buttonCaps, ref ushort buttonCapsLength, IntPtr preparsedData);

    [DllImport("hid.dll", SetLastError = true)]
    public static extern int HidP_GetValueCaps(
        HIDP_REPORT_TYPE reportType, IntPtr valueCaps, ref ushort valueCapsLength, IntPtr preparsedData);

    /// <summary>HIDP_BUTTON_CAPS（hidpi.h，pshpack4，共 72 字节）。只用到前 3 个字段，其余按原布局占位。</summary>
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public struct HIDP_BUTTON_CAPS
    {
        public ushort UsagePage;
        public byte ReportID;
        public byte IsAlias;
        public ushort BitField;
        public ushort LinkCollection;
        public ushort LinkUsage;
        public ushort LinkUsagePage;
        public byte IsRange;
        public byte IsStringRange;
        public byte IsDesignatorRange;
        public byte IsAbsolute;
        public uint Reserved1, Reserved2, Reserved3, Reserved4, Reserved5;
        public uint Reserved6, Reserved7, Reserved8, Reserved9, Reserved10;
        public ushort UsageMin, UsageMax, StringMin, StringMax;
        public ushort DesignatorMin, DesignatorMax, DataIndexMin, DataIndexMax;
    }

    /// <summary>HIDP_VALUE_CAPS（hidpi.h，pshpack4，共 72 字节）。同样只取 ReportID。</summary>
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public struct HIDP_VALUE_CAPS
    {
        public ushort UsagePage;
        public byte ReportID;
        public byte IsAlias;
        public ushort BitField;
        public ushort LinkCollection;
        public ushort LinkUsage;
        public ushort LinkUsagePage;
        public byte IsRange;
        public byte IsStringRange;
        public byte IsDesignatorRange;
        public byte IsAbsolute;
        public byte HasNull;
        public byte Reserved;
        public ushort BitSize;
        public ushort ReportCount;
        public ushort Reserved2A, Reserved2B, Reserved2C, Reserved2D, Reserved2E;
        public uint UnitsExp;
        public uint Units;
        public int LogicalMin, LogicalMax, PhysicalMin, PhysicalMax;
        public ushort UsageMin, UsageMax, StringMin, StringMax;
        public ushort DesignatorMin, DesignatorMax, DataIndexMin, DataIndexMax;
    }

    /// <summary>
    /// 取某个报文类型的 report ID。规则：caps 里第一个非 0 的 ReportID 即该接口的 report ID；
    /// caps 存在但全为 0 → 该接口不使用 report ID（报文首字节就是数据）；完全没有 caps → null。
    /// </summary>
    public static byte? ReadReportId(IntPtr preparsedData, HIDP_REPORT_TYPE reportType)
    {
        const int max = 64;
        int btnSize = Marshal.SizeOf<HIDP_BUTTON_CAPS>();
        int valSize = Marshal.SizeOf<HIDP_VALUE_CAPS>();

        IntPtr btnBuf = Marshal.AllocHGlobal(btnSize * max);
        IntPtr valBuf = Marshal.AllocHGlobal(valSize * max);
        try
        {
            bool any = false;

            ushort n = max;
            if (HidP_GetButtonCaps(reportType, btnBuf, ref n, preparsedData) == HIDP_STATUS_SUCCESS)
            {
                for (int i = 0; i < n; i++)
                {
                    var c = Marshal.PtrToStructure<HIDP_BUTTON_CAPS>(btnBuf + i * btnSize);
                    any = true;
                    if (c.ReportID != 0) return c.ReportID;
                }
            }

            n = max;
            if (HidP_GetValueCaps(reportType, valBuf, ref n, preparsedData) == HIDP_STATUS_SUCCESS)
            {
                for (int i = 0; i < n; i++)
                {
                    var c = Marshal.PtrToStructure<HIDP_VALUE_CAPS>(valBuf + i * valSize);
                    any = true;
                    if (c.ReportID != 0) return c.ReportID;
                }
            }

            return any ? (byte)0 : null;
        }
        finally
        {
            Marshal.FreeHGlobal(btnBuf);
            Marshal.FreeHGlobal(valBuf);
        }
    }

    [DllImport("hid.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern bool HidD_GetProductString(SafeFileHandle handle, [Out] char[] buffer, uint bufferLength);

    [DllImport("hid.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern bool HidD_GetManufacturerString(SafeFileHandle handle, [Out] char[] buffer, uint bufferLength);

    [DllImport("hid.dll", SetLastError = true)]
    public static extern bool HidD_SetFeature(SafeFileHandle handle, byte[] reportBuffer, uint reportBufferLength);

    [DllImport("hid.dll", SetLastError = true)]
    public static extern bool HidD_GetFeature(SafeFileHandle handle, byte[] reportBuffer, uint reportBufferLength);

    [DllImport("hid.dll", SetLastError = true)]
    public static extern bool HidD_SetOutputReport(SafeFileHandle handle, byte[] reportBuffer, uint reportBufferLength);

    [StructLayout(LayoutKind.Sequential)]
    public struct HIDD_ATTRIBUTES
    {
        public int Size;
        public ushort VendorID;
        public ushort ProductID;
        public ushort VersionNumber;
    }

    /// <summary>
    /// HIDP_CAPS —— **字段顺序必须与 Windows SDK 的 hidpi.h 一致：Usage 在前，UsagePage 在后**。
    /// 早期版本把 UsagePage 写在前面（错的），于是扫描日志里 usage_page/usage 显示成颠倒的
    /// （例如 Consumer Control 打成 `usage=0x0001/0x000C`），排查协议时被误导过。
    /// 现有插件都按"两个字段里任一个是 0xFF00"来匹配，所以修不修都不影响认领；
    /// 但修掉后日志才是真实的 usage_page/usage。
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct HIDP_CAPS
    {
        public ushort Usage;
        public ushort UsagePage;
        public ushort InputReportByteLength;
        public ushort OutputReportByteLength;
        public ushort FeatureReportByteLength;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)]
        public ushort[] Reserved;
        public ushort NumberLinkCollectionNodes;
        public ushort NumberInputButtonCaps;
        public ushort NumberInputValueCaps;
        public ushort NumberInputDataIndices;
        public ushort NumberOutputButtonCaps;
        public ushort NumberOutputValueCaps;
        public ushort NumberOutputDataIndices;
        public ushort NumberFeatureButtonCaps;
        public ushort NumberFeatureValueCaps;
        public ushort NumberFeatureDataIndices;
    }
}
