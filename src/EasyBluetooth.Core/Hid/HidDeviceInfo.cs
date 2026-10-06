using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace EasyBluetooth.Core.Hid;

/// <summary>一个 HID 接口的静态元数据（枚举时收集，不开句柄）。</summary>
public sealed class HidDeviceInfo
{
    public required string DevicePath { get; init; }
    public ushort VendorId { get; init; }
    public ushort ProductId { get; init; }
    public ushort Version { get; init; }
    public ushort UsagePage { get; init; }
    public ushort Usage { get; init; }
    public ushort InputReportLength { get; init; }
    public ushort OutputReportLength { get; init; }
    public ushort FeatureReportLength { get; init; }
    public string Product { get; init; } = "";
    public string Manufacturer { get; init; } = "";

    public string VidPid => $"VID_{VendorId:X4}&PID_{ProductId:X4}";

    public override string ToString() =>
        $"{Product} [{VidPid}] usage=0x{UsagePage:X4}/0x{Usage:X4} in={InputReportLength} out={OutputReportLength} feat={FeatureReportLength}";

    /// <summary>打开设备。键盘/鼠标等受保护的 collection 会抛 UnauthorizedAccessException。</summary>
    public HidDeviceHandle Open()
    {
        var handle = NativeHid.CreateFileW(
            DevicePath,
            NativeHid.GENERIC_READ | NativeHid.GENERIC_WRITE,
            NativeHid.FILE_SHARE_READ | NativeHid.FILE_SHARE_WRITE,
            IntPtr.Zero,
            NativeHid.OPEN_EXISTING,
            NativeHid.FILE_FLAG_OVERLAPPED,
            IntPtr.Zero);

        int hr = handle.IsInvalid ? Marshal.GetHRForLastWin32Error() : 0;
        if (handle.IsInvalid)
        {
            throw hr switch
            {
                unchecked((int)0x80070005) => new UnauthorizedAccessException($"无权访问 {DevicePath}（系统保护 collection）"),
                _ => new Win32Exception(hr, $"打开 HID 设备失败: {DevicePath}"),
            };
        }

        var stream = new FileStream(handle, FileAccess.ReadWrite, 0, isAsync: true);
        return new HidDeviceHandle(this, stream);
    }
}

/// <summary>已打开的 HID 设备句柄，封装 feature/input/output 报文读写。</summary>
public sealed class HidDeviceHandle : IDisposable
{
    private readonly FileStream _stream;

    public HidDeviceInfo Info { get; }

    internal HidDeviceHandle(HidDeviceInfo info, FileStream stream)
    {
        Info = info;
        _stream = stream;
    }

    /// <summary>
    /// 发送 feature report 并立即读回同 ID 的响应（HidD 是同步调用，自带设备层超时）。
    /// buffer[0] 必须是 report ID。
    /// </summary>
    public byte[]? GetFeature(byte reportId)
    {
        ushort len = Info.FeatureReportLength;
        if (len == 0) return null;
        var buffer = new byte[len];
        buffer[0] = reportId;
        if (!NativeHid.HidD_GetFeature(_stream.SafeFileHandle, buffer, len))
            return null;
        return buffer;
    }

    /// <summary>
    /// 读该接口**输出报文**的 report ID（HID 规范里由 caps 的 ReportID 字段给出）。
    /// 返回 0 = 该接口不使用 report ID（报文首字节即数据，无 ID 前缀）；读不到返回 null。
    ///
    /// **绝不能硬编码**：北通 T9 三个厂商集合分别是 1/4/5、鲲鹏20 是 2。写一个该接口没有的
    /// report ID，HID 驱动直接报 ERROR_INVALID_FUNCTION（"函数不正确"）。
    /// 用户态拿不到原始报告描述符（hid.dll 不导出 HidP_GetReportDescriptor），故走 caps。
    /// </summary>
    public byte? GetOutputReportId()
    {
        if (!NativeHid.HidD_GetPreparsedData(_stream.SafeFileHandle, out var preparsed)) return null;
        try
        {
            // 输出集合优先；个别接口的输出集合没有 caps（纯 padding），退回输入集合——
            // Report ID 是 HID 全局项，同一接口的各报文类型共用同一组 ID。
            var id = NativeHid.ReadReportId(preparsed, NativeHid.HIDP_REPORT_TYPE.HidP_Output);
            id ??= NativeHid.ReadReportId(preparsed, NativeHid.HIDP_REPORT_TYPE.HidP_Input);
            return id;
        }
        catch
        {
            return null;
        }
        finally
        {
            NativeHid.HidD_FreePreparsedData(preparsed);
        }
    }

    /// <summary>发送 feature report（带自定义 payload），成功后可选择立刻读回。</summary>
    public bool SetFeature(byte reportId, byte[] payload)
    {
        ushort len = Info.FeatureReportLength;
        if (len == 0) return false;
        var buffer = new byte[len];
        buffer[0] = reportId;
        Array.Copy(payload, 0, buffer, 1, Math.Min(payload.Length, len - 1));
        return NativeHid.HidD_SetFeature(_stream.SafeFileHandle, buffer, len);
    }

    /// <summary>写 output report（WriteFile，异步带超时）。buffer[0] 是 report ID。</summary>
    public bool WriteOutput(byte[] report, TimeSpan timeout)
    {
        try
        {
            var writeTask = _stream.WriteAsync(report, 0, report.Length);
            writeTask.Wait(timeout);
            if (!writeTask.IsCompletedSuccessfully)
            {
                int err = System.Runtime.InteropServices.Marshal.GetLastWin32Error();
                Utils.Log.Warn($"WriteOutput 失败 len={report.Length} lastWin32Err={err}");
                return false;
            }
            return true;
        }
        catch (Exception ex)
        {
            Utils.Log.Warn($"WriteOutput 异常 len={report.Length}: {ex.Message}");
            return false;
        }
    }

    // 单悬空读：超时后旧 ReadAsync 仍挂在驱动队列上（无法取消），重复开新读会导致
    // 设备响应被旧读吃掉。整个句柄生命周期只保留一个 pending read，数据来了在下次调用时取走。
    private Task<int>? _pendingRead;
    private byte[] _readBuffer = Array.Empty<byte>();

    /// <summary>读一个 input report（ReadFile 异步 + 超时）。返回完整缓冲区，buffer[0] 是 report ID。</summary>
    public byte[]? ReadInput(TimeSpan timeout)
    {
        ushort len = Info.InputReportLength;
        if (len == 0) return null;
        try
        {
            if (_pendingRead == null)
            {
                _readBuffer = new byte[len];
                _pendingRead = _stream.ReadAsync(_readBuffer, 0, len);
            }
            var completed = Task.WhenAny(_pendingRead, Task.Delay(timeout)).GetAwaiter().GetResult();
            if (completed != _pendingRead)
                return null; // 超时：读继续挂着，等下次调用
            int n = _pendingRead.Result;
            _pendingRead = null;
            if (n <= 0) return null;
            var result = new byte[n];
            Array.Copy(_readBuffer, result, n);
            return result;
        }
        catch
        {
            _pendingRead = null; // 读失败（设备拔出等），下次重建
            return null;
        }
    }

    public void Dispose()
    {
        _stream.Dispose();
    }
}
