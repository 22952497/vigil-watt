using VigilWatt.Core.Hid;
using VigilWatt.Core.Models;
using VigilWatt.Core.Utils;

namespace VigilWatt.Core.Plugins.Elan;

/// <summary>
/// Elan (VID 0x04F3) 2.4G 接收器插件——与本项目的弱水时砂炽天使（38FF:0001）无关，旧记录已修正（见 protocols.md §3）。
/// 接收器带 feature report 通道，电量协议未公开：探测只做只读的 feature 扫描并记日志，
/// 找到可信电量位前保持观察模式（ClaimEnabled=false，不认领设备，避免把版本号当电量）。
/// </summary>
public sealed class ElanHeadsetPlugin : BatteryPluginBase
{
    public const ushort ElanVid = 0x04F3;

    public override string Name => "Elan";
    public override string DisplayName => "Elan 2.4G 耳机接收器";
    public override DeviceKind Kind => DeviceKind.Headset;

    /// <summary>扫描无副作用（只读 GetFeature），失败冷却可以长一些。</summary>
    protected override TimeSpan ProbeCooldown => TimeSpan.FromMinutes(5);

    /// <summary>观察模式：协议确认前置为 true 才会真正认领设备。</summary>
    private static readonly bool ClaimEnabled = false;

    protected override bool IsCandidate(HidDeviceInfo info)
    {
        if (info.VendorId != ElanVid) return false;
        return info.FeatureReportLength is >= 16 and <= 64;
    }

    protected override (string Protocol, int? Percent, ChargingState Charging)? Probe(HidDeviceHandle handle)
    {
        // 只读扫描：找有响应的 feature report ID
        HidWriteProbe.FeatureSweep(handle, 16);
        var found = FindBatteryFeature(handle);
        if (found == null) return null;
        var (featureId, percentOffset, percent) = found.Value;

        if (!ClaimEnabled)
        {
            Log.Info($"[Elan] 找到候选电量位 feature=0x{featureId:X2} offset={percentOffset} 值={percent}，但协议未确认，保持观察模式");
            return null;
        }
        return ($"Elan feature 0x{featureId:X2}[{percentOffset}]", percent, ChargingState.Unknown);
    }

    /// <summary>启发式：找 0-100 且左邻字节像状态标志的电量位。返回 (featureId, offset, percent)。</summary>
    private static (byte, int, int)? FindBatteryFeature(HidDeviceHandle handle)
    {
        for (byte id = 1; id <= 16; id++)
        {
            var resp = handle.GetFeature(id);
            if (resp == null) continue;
            Log.Info($"[Elan 探测] GetFeature(0x{id:X2}) -> {Log.Hex(resp)}");

            for (int i = 1; i < Math.Min(resp.Length, 12); i++)
            {
                if (resp[i] <= 100 && resp[i - 1] <= 4)
                {
                    Log.Info($"[Elan 探测] 候选电量位: id=0x{id:X2} offset={i} 值={resp[i]} 邻字节={resp[i - 1]}");
                    return (id, i, resp[i]);
                }
            }
        }
        return null;
    }

    /// <summary>观察模式下不会产生任何已认领设备；一旦启用认领，按 feature 0x{id}[{offset}] 周期 GetFeature 读数。</summary>
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
