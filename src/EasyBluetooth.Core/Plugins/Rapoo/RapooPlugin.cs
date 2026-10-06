using EasyBluetooth.Core.Hid;
using EasyBluetooth.Core.Models;
using EasyBluetooth.Core.Utils;

namespace EasyBluetooth.Core.Plugins.Rapoo;

/// <summary>
/// 雷柏 (Rapoo) 2.4G 插件 —— 纯被动实现，不向设备写任何字节。
///
/// 协议出处（公开逆向，均 MIT 许可，符合 safety.md "只发有出处的帧"）：
/// - pedro3z0/rapoo-software-linux：逆向自官方驱动 A HUB 1.0.19 + 官方 Web 驱动 hub.rapoo.cn 抓包，VT0 Air Max 真机验证；
/// - D3m0nZOnFire/mousectl：VT3 PRO（dongle 24AE:1231）真机验证。
/// 本机 dongle 24AE:1412 = VT3 二代（V2 世代 Nordic dongle，与 VT3 PRO V2 = 14A5 / VT0 = 1410 同代同族；
/// 设备表未收录 1412 但帧结构吻合：旧日志 GetFeature(0x08)→`02 00…` 正是该协议 feature 8 通道的 ACK 0x02=Busy）。
///
/// 电量来自 dongle 每 ~3 秒自发推送的 input report 0x07（19 字节，hidraw 含 ID 视角）：
///   [0]=0x07  [1]低4位=连接方式(0=2.4G/2=有线)  [2]=DPI档  [3..4]/[5..6]=DPI XY(le)  [7]=充电标志
///   [8]=电量%  [11]=轮询率码  [13..14]=USB PID(le)  [18]bit7=RF已连接、bit0..6=RSSI
/// 充电标志 [7] 本机实测校准（2026-10-05）：0x01=放电、0x02=充电（与 mousectl 在 VT3 PRO 一代记录的
/// 1/2 编码同族）；充满态的值待采样（当前 100% 一律判充满）。
///
/// 鼠标休眠时 dongle 全静音（连鼠标报文都停）→ 表现为离线；一动鼠标即恢复。此前 HID++ ping / 0xAA 查询
/// 全部无效是正常的——该世代协议里电量根本没有查询命令，只能等心跳。
/// </summary>
public sealed class RapooPlugin : BatteryPluginBase
{
    public const ushort RapooVid = 0x24AE;
    private const byte StatusReportId = 0x07;
    private const int StatusReportLength = 19;

    public override string Name => "Rapoo";
    public override string DisplayName => "雷柏";
    public override DeviceKind Kind => DeviceKind.Mouse;

    /// <summary>纯被动只读；鼠标休眠期空转无意义，失败冷却放长。</summary>
    protected override TimeSpan ProbeCooldown => TimeSpan.FromMinutes(3);

    /// <summary>本插件不存在任何写入路径；置 false 作为纵深防御（CanWrite 恒 false）。</summary>
    protected override bool WritesEnabled => false;

    /// <summary>
    /// 候选接口：vendor 通道（雷柏把 0xFF00 放在 UsagePage 或 Usage 字段——hidapi 与本仓库扫描器字段序相反，两边都查），
    /// 且输入长度 **正好 19 字节**（状态报表 0x07 的长度）。
    ///
    /// 2026-10-06 收敛：旧版放行 19/32/33 是"≥19 就试试"的保守猜测，但本机 dongle（24AE:1412）的
    /// 0x0011/32B 与 0x000E/33B 两条 vendor 通道**从不发 0x07**，每次首扫各白等一整个探测窗口（串行 2×5s）。
    /// 逆向协议（pedro3z0 / D3m0nZOnFire）与 protocols.md §3 都写明状态报表是 19 字节，故收敛到 ==19：
    /// 一个 dongle 只剩唯一候选，首扫探测耗时 = 1×窗口。14B（装不下）/513B（DFU 批量）/其它 usage 本就不碰。
    /// </summary>
    protected override bool IsCandidate(HidDeviceInfo info)
    {
        if (info.VendorId != RapooVid) return false;
        if (info.UsagePage != 0xFF00 && info.Usage != 0xFF00) return false;
        return info.InputReportLength == StatusReportLength;
    }

    /// <summary>探测 = 被动听 5 秒心跳（心跳约 3 秒一条，够收一条）。听不到 = 鼠标睡着，返回 null 进冷却，下一轮再试。</summary>
    protected override (string Protocol, int? Percent, ChargingState Charging)? Probe(HidDeviceHandle handle)
    {
        var read = ListenStatus(handle, TimeSpan.FromSeconds(5));
        if (!read.HasValue) return null;
        return ("status-report7", read.Value.Percent, read.Value.Charging);
    }

    /// <summary>轮询 = 被动听 5 秒（心跳 3 秒，鼠标醒着必能听到一条）。 null 计入基类失败计数（标离线）。</summary>
    protected override (int? Percent, ChargingState Charging)? PollDevice(TrackedDevice dev)
    {
        var read = ListenStatus(dev.Handle, TimeSpan.FromSeconds(5));
        if (!read.HasValue) return null;
        return (read.Value.Percent, read.Value.Charging);
    }

    /// <summary>在截止时间前收 input report，解析 0x07 状态帧。所有听到的帧都记原始日志（协议排错靠它）。</summary>
    private static (int Percent, ChargingState Charging)? ListenStatus(HidDeviceHandle handle, TimeSpan window)
    {
        var deadline = DateTime.UtcNow + window;
        while (DateTime.UtcNow < deadline)
        {
            var input = handle.ReadInput(TimeSpan.FromMilliseconds(400));
            if (input == null) continue;
            Log.Info($"雷柏 收到报文: {Log.Hex(input)}");

            var parsed = ParseStatusReport(input);
            if (parsed.HasValue) return parsed;
        }
        return null;
    }

    /// <summary>解析状态帧（兼容带/不带 report ID 前缀两种布局）。电量 0 视为无效读数（休眠/瞬态）。</summary>
    private static (int Percent, ChargingState Charging)? ParseStatusReport(byte[] input)
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

            Log.Info($"雷柏 状态帧: 连接={link} 充电标志={chargeFlag} 电量={pct}% RF=0x{rfByte:X2}(bit7连接={(rfByte & 0x80) != 0} RSSI={rfByte & 0x7F}) 原始={Log.Hex(input)}");

            if (pct is 0 or > 100)
            {
                Log.Info($"雷柏 电量越界/归零（疑似休眠瞬态），丢弃: {Log.Hex(input)}");
                return null;
            }
            // [7] 充电标志实测：0x01=放电、0x02=充电；充满态的值未采样，100% 一律判充满兜底
            var charging = chargeFlag == 0x02 ? ChargingState.Charging
                : pct >= 100 ? ChargingState.Full
                : ChargingState.Discharging;
            return (pct, charging);
        }
        return null;
    }

    protected override string BuildDisplayName(HidDeviceInfo info)
        => string.IsNullOrEmpty(info.Product) ? $"雷柏 ({info.VidPid})" : $"雷柏 {info.Product}";
}
