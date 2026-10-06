namespace EasyBluetooth.Core.Plugins.Mchose;

/// <summary>0A 推送帧（[13 0A …]）的解析结果类型。</summary>
internal enum V1PushKind
{
    /// <summary>不是 0A 推送帧。</summary>
    NotPush,
    /// <summary>电量推送，Percent / ChargeFlag 有效。</summary>
    Battery,
    /// <summary>电量帧但电量值无效（占位/越界），Percent 不可用。</summary>
    BatteryInvalid,
    /// <summary>设备状态通知（实测=键盘休眠/关机），**下标 6 不是电量**。</summary>
    StateNotify,
    /// <summary>其他类型的 0A 帧，忽略但值得记日志校准。</summary>
    Other,
}

/// <summary>4A 电量查询响应的解析结果类型。</summary>
internal enum V1ResponseKind
{
    /// <summary>不是 4A 响应。</summary>
    NotResponse,
    /// <summary>正常电量响应。</summary>
    Battery,
    /// <summary>是 4A 响应但电量值无效（占位/越界）。</summary>
    InvalidBattery,
}

/// <summary>0A 推送帧的解析结果。Battery 时看 Percent/ChargeFlag；StateNotify 时看 Payload。</summary>
internal readonly record struct V1Push(V1PushKind Kind, byte Type, byte Payload, int Percent, byte ChargeFlag);

/// <summary>
/// 迈从键盘 V1 协议（report 0x13）的**纯帧编解码**，不碰句柄、不写日志——便于单测
/// （见 src/tests/EasyBluetooth.Core.Tests/，用真机抓到的原始帧跑）。
///
/// 帧布局（全部 20 字节，校验 = 前 19 字节之和 mod 256）：
/// <code>
/// 开会话  [13 05 01 00×16 sum]
/// 查电量  [13 4A 01 00×16 sum]
/// 电量响应 [13 4A 01 00 02 电量% 充电标志 00×11 sum]
/// 推送    [13 0A 01 00 04 类型 …payload… sum]
/// </code>
/// **推送帧的类型位（下标 5）是关键**：0x05 = 电量（payload [6]=电量% [7]=标志），
/// 0x02 = 设备状态通知（实测键盘休眠时发 `13 0A 01 00 04 02 01 …`，payload [6]=0x01）。
/// 旧代码不看类型位、直接读下标 6 当电量，于是把休眠帧的 0x01 显示成 1%，误报低电量。
/// </summary>
internal static class MchoseV1Codec
{
    public const byte ReportId = 0x13;
    public const byte SessionCmd = 0x05;
    public const byte BatteryQueryCmd = 0x4A;
    public const byte PushCmd = 0x0A;

    /// <summary>推送帧类型位：电量。</summary>
    public const byte PushTypeBattery = 0x05;

    /// <summary>推送帧类型位：设备状态通知（实测=休眠/关机）。</summary>
    public const byte PushTypeState = 0x02;

    /// <summary>无效电量下界：≤1 一律无效（真机休眠帧 payload 就是 0x01）。低电量阈值 20%，
    /// 正常放电到 1% 之前早就提醒过了，所以拒绝它不会漏掉任何真实告警。</summary>
    public const byte InvalidPercentFloor = 1;

    /// <summary>帧总长度。</summary>
    public const int FrameLength = 20;

    /// <summary>构造 V1 帧（补零到 20 字节，末字节 = 前 19 字节之和）。</summary>
    public static byte[] BuildFrame(byte cmd, byte param)
    {
        var packet = new byte[FrameLength];
        packet[0] = ReportId;
        packet[1] = cmd;
        packet[2] = param;
        packet[FrameLength - 1] = Checksum(packet);
        return packet;
    }

    /// <summary>校验和 = 前 19 字节之和 mod 256。</summary>
    public static byte Checksum(byte[] frame)
    {
        byte sum = 0;
        for (int i = 0; i < FrameLength - 1 && i < frame.Length; i++) sum += frame[i];
        return sum;
    }

    /// <summary>校验帧尾校验和是否匹配。</summary>
    public static bool IsChecksumValid(byte[] frame)
        => frame.Length >= FrameLength && frame[FrameLength - 1] == Checksum(frame);

    /// <summary>
    /// 解析 0A 推送帧。Windows 的 ReadInput 缓冲有时带 report ID 前缀（下标 0），
    /// 所以偏移 0/1 都试一次。
    /// </summary>
    public static V1Push ParsePush(byte[] input)
    {
        foreach (int off in new[] { 0, 1 })
        {
            if (input.Length < off + 8) continue;
            if (input[off] != ReportId || input[off + 1] != PushCmd) continue;

            byte type = input[off + 5];
            byte payload = input[off + 6];
            if (type == PushTypeState) return new V1Push(V1PushKind.StateNotify, type, payload, 0, 0);
            if (type != PushTypeBattery) return new V1Push(V1PushKind.Other, type, payload, 0, 0);

            byte pct = input[off + 6];
            byte flag = input[off + 7];
            if (pct <= InvalidPercentFloor || pct > 100)
                return new V1Push(V1PushKind.BatteryInvalid, type, payload, pct, flag);

            return new V1Push(V1PushKind.Battery, type, payload, pct, flag);
        }
        return new V1Push(V1PushKind.NotPush, 0, 0, 0, 0);
    }

    /// <summary>解析 4A 电量查询响应。</summary>
    public static V1ResponseKind ParseBatteryResponse(byte[] input, out int percent, out byte chargeFlag)
    {
        percent = 0;
        chargeFlag = 0;
        if (input.Length < FrameLength) return V1ResponseKind.NotResponse;
        if (input[0] != ReportId || input[1] != BatteryQueryCmd) return V1ResponseKind.NotResponse;

        byte pct = input[5];
        chargeFlag = input[6];
        if (pct <= InvalidPercentFloor || pct > 100)
        {
            percent = pct;
            return V1ResponseKind.InvalidBattery;
        }

        percent = pct;
        return V1ResponseKind.Battery;
    }
}
