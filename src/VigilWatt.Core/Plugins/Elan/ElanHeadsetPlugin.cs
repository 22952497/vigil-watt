using VigilWatt.Core.Hid;
using VigilWatt.Core.Models;

namespace VigilWatt.Core.Plugins.Elan;

/// <summary>
/// 功 能： Elan (VID 0x04F3) 2.4G 接收器插件（与本项目的弱水时砂炽天使 38FF:0001 无关，旧记录已修正，见 docs/hardware/protocols.md §3）。
/// 2026/10/06 15:53:36 xliu
/// 电量协议未公开，当前**只占位、不探测**：不认领设备，探测也不做任何 I/O。
/// </summary>
public sealed class ElanHeadsetPlugin : BatteryPluginBase
{
    public const ushort ElanVid = 0x04F3;

    public override string Name => "Elan";
    public override string DisplayName => "Elan 2.4G 耳机接收器";
    public override DeviceKind Kind => DeviceKind.Headset;

    /// <summary>探测不做任何 I/O（含只读 GetFeature），故不受写入开关约束。</summary>
    protected override bool ProbeWrites => false;

    protected override bool IsCandidate(HidDeviceInfo info)
    {
        if (info.VendorId != ElanVid) return false;
        return info.FeatureReportLength is >= 16 and <= 64;
    }

    /// <summary>
    /// 协议未确认，观察模式下**不做任何探测**。
    ///
    /// 历史实现是"只读 feature 扫描 + 电量位启发式"，违反 docs/hardware/safety.md §3 规则 7
    /// （诊断/扫描代码不得留在正常执行路径上，哪怕有开关）。要恢复排查，请按规则 7 在独立脚本里做，
    /// 不要再塞回这里（CR-10）。
    /// </summary>
    protected override (string Protocol, int? Percent, ChargingState Charging)? Probe(HidDeviceHandle handle)
        => null;

    /// <summary>协议未确认，当前不会产生已认领设备（<see cref="Probe"/> 恒返回 null，故本方法不会被调用）；
    /// 日后确认协议后，按 feature 0x{id}[{offset}] 周期 GetFeature 读数。</summary>
    protected override (int? Percent, ChargingState Charging)? PollDevice(TrackedDevice dev)
    {
        var m = FeatureSpec.Match(dev.Protocol);
        if (!m.Success) return null;
        var resp = dev.Handle.GetFeature(byte.Parse(m.Groups[1].Value, System.Globalization.NumberStyles.HexNumber));
        int offset = int.Parse(m.Groups[2].Value);
        if (resp == null || resp.Length <= offset) return null;
        return (resp[offset], ChargingState.Unknown);
    }

    private static readonly System.Text.RegularExpressions.Regex FeatureSpec =
        new(@"^Elan feature 0x([0-9A-F]{2})\[(\d+)\]$", System.Text.RegularExpressions.RegexOptions.Compiled);
}
