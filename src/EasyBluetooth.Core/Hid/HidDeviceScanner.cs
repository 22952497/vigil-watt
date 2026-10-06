using System.Runtime.InteropServices;
using EasyBluetooth.Core.Utils;

namespace EasyBluetooth.Core.Hid;

/// <summary>枚举当前在位的所有 HID 接口，收集元数据供插件匹配。</summary>
public static class HidDeviceScanner
{
    public static List<HidDeviceInfo> Scan()
    {
        var list = new List<HidDeviceInfo>();
        var guid = NativeHid.GuidDeviceInterfaceHid;

        if (NativeHid.CM_Get_Device_Interface_List_SizeW(out uint size, ref guid, null,
                NativeHid.CM_GET_DEVICE_INTERFACE_LIST_PRESENT) != 0 || size == 0)
            return list;

        var buffer = new char[size];
        if (NativeHid.CM_Get_Device_Interface_ListW(ref guid, null, buffer, size,
                NativeHid.CM_GET_DEVICE_INTERFACE_LIST_PRESENT) != 0)
            return list;

        // 多字符串（double-null 结尾）
        var paths = new string(buffer).Split('\0', StringSplitOptions.RemoveEmptyEntries);
        foreach (var path in paths)
        {
            try
            {
                var info = Inspect(path);
                if (info != null) list.Add(info);
            }
            catch
            {
                // 个别接口打不开不影响整体枚举
            }
        }

        Log.Info($"HID 扫描完成，共 {list.Count} 个接口");
        foreach (var d in list) Log.Info("  " + d);
        return list;
    }

    private static HidDeviceInfo? Inspect(string path)
    {
        // 只为了读元数据短暂打开；键盘/鼠标保护 collection 打不开就跳过
        var handle = NativeHid.CreateFileW(
            path,
            NativeHid.GENERIC_READ | NativeHid.GENERIC_WRITE,
            NativeHid.FILE_SHARE_READ | NativeHid.FILE_SHARE_WRITE,
            IntPtr.Zero, NativeHid.OPEN_EXISTING, NativeHid.FILE_FLAG_OVERLAPPED, IntPtr.Zero);

        using var safe = handle;
        if (safe.IsInvalid) return null;

        var attrs = new NativeHid.HIDD_ATTRIBUTES { Size = Marshal.SizeOf<NativeHid.HIDD_ATTRIBUTES>() };
        if (!NativeHid.HidD_GetAttributes(safe, ref attrs)) return null;

        ushort usagePage = 0, usage = 0;
        ushort inLen = 0, outLen = 0, featLen = 0;
        int capsStatus = -1;
        bool preparsedOk = NativeHid.HidD_GetPreparsedData(safe, out var preparsed);
        if (preparsedOk)
        {
            try
            {
                var caps = new NativeHid.HIDP_CAPS();
                // HidP_GetCaps 返回 NTSTATUS，成功码是 0x00110000 而不是 0
                const int HIDP_STATUS_SUCCESS = 0x00110000;
                capsStatus = NativeHid.HidP_GetCaps(preparsed, ref caps);
                if (capsStatus == HIDP_STATUS_SUCCESS)
                {
                    usagePage = caps.UsagePage;
                    usage = caps.Usage;
                    inLen = caps.InputReportByteLength;
                    outLen = caps.OutputReportByteLength;
                    featLen = caps.FeatureReportByteLength;
                }
            }
            finally
            {
                NativeHid.HidD_FreePreparsedData(preparsed);
            }
        }
        Utils.Log.Info($"HID 元数据 {attrs.VendorID:X4}/{attrs.ProductID:X4}: preparsed={preparsedOk} capsStatus=0x{capsStatus:X8}");

        return new HidDeviceInfo
        {
            DevicePath = path,
            VendorId = attrs.VendorID,
            ProductId = attrs.ProductID,
            Version = attrs.VersionNumber,
            UsagePage = usagePage,
            Usage = usage,
            InputReportLength = inLen,
            OutputReportLength = outLen,
            FeatureReportLength = featLen,
            Product = GetString(safe, NativeHid.HidD_GetProductString) ?? "",
            Manufacturer = GetString(safe, NativeHid.HidD_GetManufacturerString) ?? "",
        };
    }

    private static string? GetString(
        Microsoft.Win32.SafeHandles.SafeFileHandle handle,
        Func<Microsoft.Win32.SafeHandles.SafeFileHandle, char[], uint, bool> api)
    {
        var buffer = new char[126];
        return api(handle, buffer, (uint)(buffer.Length * 2)) ? new string(buffer).TrimEnd('\0') : null;
    }
}
