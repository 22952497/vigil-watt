using VigilWatt.Core.Models;

namespace VigilWatt.Core.Plugins.Rapoo;

/// <summary>0x07 状态帧的解析结果类型。</summary>
internal enum RapooFrameKind
{
    /// <summary>不是 0x07 状态帧（其余字段无意义）。</summary>
    NotStatus,
    /// <summary>状态帧且电量有效。</summary>
    Status,
    /// <summary>状态帧但电量越界（0 / &gt;100，实测为休眠瞬态）：该帧不能用来更新读数，但链路位仍然可信。</summary>
    StatusInvalidPercent,
}

/// <summary>0x07 状态帧的解析结果。Kind 决定其余字段是否有意义。</summary>
internal readonly record struct RapooStatus(
    RapooFrameKind Kind, int Percent, ChargingState Charging,
    byte ChargeFlag, byte RfByte, string Link)
{
    /// <summary><c>[18].bit7</c> = 0 ⟺ RF 链路断开。与电量无关，越界帧上同样可信。</summary>
    public bool LinkDown => RapooCodec.IsLinkDown(RfByte);
}

/// <summary>
/// 雷柏 2.4G 状态帧（report 0x07，19 字节）的**纯帧解析**，不碰句柄、不写日志——便于单测
/// （见 src/tests/VigilWatt.Core.Tests，用真机抓到的原始帧跑）。
///
/// 帧布局（hidraw 含 ID 视角；本机 dongle 24AE:1412 实测）：
/// <code>
/// [0]=0x07  [1]低4位=连接方式(0=2.4G / 2=有线)  [2]=DPI档  [3..4]/[5..6]=DPI XY(le)
/// [7]=充电标志  [8]=电量%  [11]=轮询率码  [13..14]=USB PID(le)  [18]bit7=RF已连接, bit0..6=RSSI
/// </code>
/// 兼容带 / 不带 report ID 前缀两种布局（<c>off ∈ {0,1}</c>）。
///
/// **[18] 的 bit7 是"RF 链路是否连接"**（2026-10-08 实测确认）：鼠标断电瞬间 dongle 会主动吐一帧
/// <c>… 7F</c>（bit7=0 且 RSSI=127，即"断链且无信号"），随后静音 27.9 秒才恢复
/// （日志 21:38:38.564 → 21:39:06.454）。这条帧的价值在于：**设备自己告诉你断链了**，
/// 比"靠静音计时猜"精确得多。
///
/// 但要注意它描述的是 **2.4G 链路**，不是"设备在不在"——关机、移出范围、受干扰都会翻 0。
/// 所以只能当"离线候选"用，交给 <see cref="OfflinePolicy.Confirm"/> 做延迟确认，不能直接判离线。
/// </summary>
internal static class RapooCodec
{
    public const byte StatusReportId = 0x07;
    public const int StatusReportLength = 19;

    /// <summary><c>[18].bit7</c> = "RF 已连接"（协议定义）。bit7=0 ⟺ dongle 与鼠标的 2.4G 链路断开。</summary>
    public static bool IsLinkDown(byte rfByte) => (rfByte & 0x80) == 0;

    /// <summary>
    /// 解析一帧。返回的 <see cref="RapooStatus.Kind"/> 为 <see cref="RapooFrameKind.NotStatus"/>
    /// 时说明这不是 0x07 状态帧（其余字段无意义）。
    /// </summary>
    public static RapooStatus Parse(byte[] input)
    {
        foreach (int off in new[] { 0, 1 })
        {
            if (input.Length < off + StatusReportLength) continue;
            if (input[off] != StatusReportId) continue;

            int linkRaw = input[off + 1] & 0x0F;
            byte chargeFlag = input[off + 7];
            byte pct = input[off + 8];
            byte rfByte = input[off + 18];
            string link = linkRaw switch { 0 => "2.4G", 2 => "有线", _ => $"link{linkRaw}" };

            if (pct is 0 or > 100)
                return new RapooStatus(RapooFrameKind.StatusInvalidPercent, pct,
                    ChargingState.Unknown, chargeFlag, rfByte, link);

            // [7] 充电标志实测：0x01=放电、0x02=充电；充满态的值未采样，100% 一律判充满兜底
            var charging = chargeFlag == 0x02 ? ChargingState.Charging
                : pct >= 100 ? ChargingState.Full
                : ChargingState.Discharging;
            return new RapooStatus(RapooFrameKind.Status, pct, charging, chargeFlag, rfByte, link);
        }
        return new RapooStatus(RapooFrameKind.NotStatus, 0, ChargingState.Unknown, 0, 0, "");
    }
}
