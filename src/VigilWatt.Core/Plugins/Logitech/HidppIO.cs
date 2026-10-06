using System.Collections.Concurrent;
using VigilWatt.Core.Hid;
using VigilWatt.Core.Utils;

namespace VigilWatt.Core.Plugins.Logitech;

/// <summary>
/// HID++ 底层 I/O：通过 HID++ vendor 接口（usage page 0xFF43）写 output report 发请求，
/// 读 input report 匹配响应。报文格式对照 Solaar lib/logitech_receiver/base.py：
/// - 短报文 7 字节 [0x10, devIdx, feature, (func<<4)|sw, p0, p1, p2]
/// - 长报文 20 字节 [0x11, devIdx, feature, (func<<4)|sw, p0..p15]
/// - 错误响应：data[0]=0x8F (1.0) / 0xFF (2.0)，data[1..2] 回显请求前两字节，data[3] 错误码
/// </summary>
internal sealed class HidppIO : IDisposable
{
    public const byte ShortMessage = 0x10;
    public const byte LongMessage = 0x11;
    public const byte SwId = 0x0B; // Solaar 约定，避开 G HUB(0x0D)/OpenRGB(0x07)/LGSTray(0x0A)

    private readonly HidDeviceHandle _handle;
    private readonly object _ioGate = new();

    /// <summary>底层句柄（供探测工具使用）。</summary>
    public HidDeviceHandle Handle => _handle;

    public HidppIO(HidDeviceHandle handle) => _handle = handle;

    /// <summary>
    /// 发送一条 HID++ 请求并等待匹配的响应。返回响应 data 部分（不含 report id 和 devIdx）。
    /// 2.0 错误响应返回 null（调用方按“特性不存在/调用失败”处理）；1.0 错误同样返回 null。
    /// patchSw=false 用于 HID++ 1.0 寄存器访问（0x8100|reg 等，低 4 位是寄存器号本身，不能替换）。
    /// </summary>
    public byte[]? Request(byte devIndex, ushort featureIdAndFunc, ReadOnlySpan<byte> args,
        bool longMessage = false, bool patchSw = true)
    {
        ushort requestId = patchSw
            ? (ushort)((featureIdAndFunc & 0xFFF0) | SwId)   // (feature<<8)|(func<<4)|sw
            : featureIdAndFunc;
        int dataLen = longMessage ? 18 : 5;
        var data = new byte[dataLen];
        data[0] = (byte)(requestId >> 8);
        data[1] = (byte)requestId;
        for (int i = 0; i < args.Length && 2 + i < dataLen; i++)
            data[2 + i] = args[i];

        byte reportId = longMessage ? LongMessage : ShortMessage;
        var packet = new byte[dataLen + 2];
        packet[0] = reportId;
        packet[1] = devIndex;
        Array.Copy(data, 0, packet, 2, dataLen);

        // HID WriteFile 要求缓冲区与接口的 output report 长度完全一致，不足则补零
        if (packet.Length < _handle.Info.OutputReportLength)
            Array.Resize(ref packet, _handle.Info.OutputReportLength);

        lock (_ioGate)
        {
            if (!_handle.WriteOutput(packet, TimeSpan.FromMilliseconds(500)))
            {
                Log.Warn($"HID++ 写失败 dev={devIndex} req=0x{requestId:X4}");
                return null;
            }

            return ReadMatching(devIndex, data, TimeSpan.FromSeconds(3));
        }
    }

    /// <summary>读输入流直到出现前两字节与请求匹配的响应；其他报文（通知）丢弃。</summary>
    private byte[]? ReadMatching(byte devIndex, byte[] requestData, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var remaining = deadline - DateTime.UtcNow;
            var input = _handle.ReadInput(remaining > TimeSpan.FromMilliseconds(100)
                ? remaining
                : TimeSpan.FromMilliseconds(100));
            if (input == null) break;

            byte reportId = input[0];
            if (reportId != ShortMessage && reportId != LongMessage) continue;

            // input: [reportId, devIdx, data...]
            byte replyDev = input[1];
            bool devMatch = replyDev == devIndex || replyDev == (byte)~devIndex;
            if (!devMatch) continue;

            int dataStart = 2;
            if (input.Length < dataStart + 2) continue;
            if (input[dataStart] != requestData[0] || input[dataStart + 1] != requestData[1]) continue;

            int dataLen = input.Length - dataStart;
            var reply = new byte[dataLen];
            Array.Copy(input, dataStart, reply, 0, dataLen);

            // 错误识别：data[0]=0x8F(HID++ 1.0) 或 0xFF(HID++ 2.0)，且 data[1..2] 回显请求
            if (reply[0] == 0x8F || reply[0] == 0xFF)
                return null;
            return reply;
        }
        Log.Warn($"HID++ 响应超时 dev={devIndex} req=0x{requestData[0]:X2}{requestData[1]:X2}");
        return null;
    }

    public void Dispose() => _handle.Dispose();
}
