using VigilWatt.Core.Utils;

namespace VigilWatt.Core.Hid;

/// <summary>
/// HID vendor 通道探测工具：国产 2.4G 接收器的 output report 是否带 report ID、
/// 用哪个 ID、命令格式如何，各不相同。对候选接口跑一遍矩阵，把所有结果写日志，
/// 据此校准各品牌插件。
/// </summary>
public static class HidWriteProbe
{
    public sealed record ProbeVariant(string Name, byte[] Packet);

    /// <summary>生成一组变体：带/不带 report ID、短/长消息。</summary>
    public static List<ProbeVariant> PingVariants(ushort outLen, byte devIndex = 0xFF)
    {
        var list = new List<ProbeVariant>();

        // HID++ 风格 ping（feature 0x00 func 1，带标记字节）
        byte mark = (byte)Random.Shared.Next(1, 255);
        byte[] shortPing = { 0x10, devIndex, 0x00, 0x1B, 0x00, 0x00, mark };
        byte[] longPing = { 0x11, devIndex, 0x00, 0x1B, 0x00, 0x00, mark };

        if (outLen >= shortPing.Length)
        {
            var a = (byte[])shortPing.Clone();
            list.Add(new ProbeVariant("hidpp-short-id", Pad(a, outLen)));
            var b = new byte[outLen];
            b[0] = 0x00;
            Array.Copy(shortPing, 0, b, 1, shortPing.Length);   // 无 report ID：首字节 0
            list.Add(new ProbeVariant("hidpp-short-noid", b));
        }
        if (outLen >= longPing.Length)
        {
            var a = (byte[])longPing.Clone();
            list.Add(new ProbeVariant("hidpp-long-id", Pad(a, outLen)));
            var b = new byte[outLen];
            b[0] = 0x00;
            Array.Copy(longPing, 0, b, 1, longPing.Length);
            list.Add(new ProbeVariant("hidpp-long-noid", b));
        }
        return list;
    }

    /// <summary>迈从经典协议变体：命令 0x06 经 0xFF 异或 = 0xF9。</summary>
    public static List<ProbeVariant> MchoseClassicVariants(ushort outLen)
    {
        var list = new List<ProbeVariant>();
        if (outLen < 20) return list;
        var body = new byte[outLen];
        body[1] = 0xF9;                       // 无 ID 形式：[0]=0, [1]=0xF9, 其余 0xFF
        Array.Fill(body, (byte)0xFF, 2, outLen - 2);
        list.Add(new ProbeVariant("mchose-noid", body));
        var withId = new byte[outLen];
        withId[0] = 0x11;
        withId[1] = 0xF9;
        Array.Fill(withId, (byte)0xFF, 2, outLen - 2);
        list.Add(new ProbeVariant("mchose-id11", withId));
        return list;
    }

    private static byte[] Pad(byte[] src, int len)
    {
        var r = new byte[len];
        Array.Copy(src, r, src.Length);
        return r;
    }

    /// <summary>feature report 扫描：对 report ID 1..N 发 GetFeature，把有响应的原始字节写日志。
    /// 用于发现未知 vendor 通道（如时砂耳机 Elan 接收器的 32 字节 feature）。</summary>
    public static void FeatureSweep(HidDeviceHandle handle, byte maxId = 8)
    {
        if (handle.Info.FeatureReportLength == 0) return;
        for (byte id = 1; id <= maxId; id++)
        {
            var resp = handle.GetFeature(id);
            if (resp != null)
                Log.Debug($"[探测] {handle.Info.VidPid} usage=0x{handle.Info.Usage:X4} GetFeature(0x{id:X2}) -> {Log.Hex(resp)}");
        }
    }

    /// <summary>对打开的接口跑变体矩阵，读回所有响应并写日志。返回第一个有响应的变体名。</summary>
    public static string? Run(HidDeviceHandle handle, IReadOnlyList<ProbeVariant> variants, int readMs = 600)
    {
        string? responded = null;
        foreach (var v in variants)
        {
            if (!handle.WriteOutput(v.Packet, TimeSpan.FromMilliseconds(400)))
                continue;

            // 写成功：收集 1 秒内所有输入报文
            var deadline = DateTime.UtcNow + TimeSpan.FromMilliseconds(readMs);
            var got = new List<byte[]>();
            while (DateTime.UtcNow < deadline)
            {
                var input = handle.ReadInput(TimeSpan.FromMilliseconds(200));
                if (input != null) got.Add(input);
            }
            if (got.Count > 0)
            {
                responded ??= v.Name;
                Log.Debug($"[探测] {handle.Info.VidPid} usage=0x{handle.Info.Usage:X4} 变体={v.Name} 收到 {got.Count} 个响应报文:");
                foreach (var g in got.Take(4)) Log.Debug($"[探测]   <- {Log.Hex(g)}");
            }
        }
        if (responded == null)
            Log.Debug($"[探测] {handle.Info.VidPid} usage=0x{handle.Info.Usage:X4} 无任何变体响应");
        return responded;
    }
}
