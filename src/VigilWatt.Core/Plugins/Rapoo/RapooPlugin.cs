using VigilWatt.Core.Hid;
using VigilWatt.Core.Models;
using VigilWatt.Core.Utils;

namespace VigilWatt.Core.Plugins.Rapoo;

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
/// 鼠标休眠时 dongle 转静音，一动鼠标即恢复心跳。此前 HID++ ping / 0xAA 查询
/// 全部无效是正常的——该世代协议里电量根本没有查询命令，只能等心跳。
/// 注意"静音"不等于"设备不在"：休眠/停手是常态，**不据此判离线**（见 <see cref="OfflinePolicy.Silence"/>）。
///
/// 离线判定有两条腿（2026-10-08 补上第二条）：
/// 1. **设备自己说断链**：状态帧 [18].bit7 = "RF 已连接"，鼠标断电瞬间 dongle 会主动吐一帧 bit7=0
///    （实测 `… 7F`，随后静音 27.9 秒）。走 <see cref="OfflinePolicy.Confirm"/> 延迟确认。
/// 2. **接口彻底不说话**：90 秒兜底，见 <see cref="OfflinePolicy.Silence"/>。
/// 两条都是"宁可漏判不可误判"的保守判据，参数集中在本插件的 <c>Offline</c> 里。
/// 帧解析本身在 <see cref="RapooCodec"/>（纯函数，可单测）。
/// </summary>
public sealed class RapooPlugin : BatteryPluginBase
{
    public const ushort RapooVid = 0x24AE;

    public override string Name => "Rapoo";
    public override string DisplayName => "雷柏";
    public override DeviceKind Kind => DeviceKind.Mouse;

    /// <summary>
    /// 纯被动只读、零写入（WritesEnabled=false，本插件不存在任何写入路径），
    /// 所以探测失败**不是错误**，只说明"鼠标睡着了"（休眠时 dongle 全静音，一动即恢复）。
    ///
    /// 2026-10-06 修正：旧值 3 分钟是"键盘出来了、鼠标不出来"的根因。
    /// 用户刚启动应用时鼠标多半在睡 → 首扫探测必然落空 → 之后 3 分钟**不再看一眼**，
    /// 而用户这时候一动鼠标就把它唤醒了，应用却仍在冷却里。日志实测鼠标要
    /// **4 分 09 秒**才进列表（16:13:45 启动，16:17:54 才认领）。
    ///
    /// 现在 5 秒，配合 DeviceManager 的 15 秒快速发现节拍（SupportsFastDiscovery），
    /// 保证每个节拍都允许重探 —— 动一下鼠标，几秒到十几秒内出现在列表里。
    /// </summary>
    protected override TimeSpan ProbeCooldown => TimeSpan.FromSeconds(5);

    /// <summary>纯被动零写入 → 允许 DeviceManager 用 15 秒节拍做发现（不发任何帧，无风险）。</summary>
    public override bool SupportsFastDiscovery => true;

    /// <summary>本插件不存在任何写入路径；置 false 作为纵深防御（CanWrite 恒 false）。</summary>
    protected override bool WritesEnabled => false;

    /// <summary>认领后后台只读心跳帧，插拔电/电量变化立即 StatusPushed。</summary>
    protected override bool SupportsPassiveListen => true;

    /// <summary>
    /// 本插件的离线判定策略——三条判据的参数都在这里，机制由基类统一实现（见 <see cref="OfflinePolicy"/>）。
    ///
    /// <b>Confirm = 6 秒</b>（2026-10-08 用户拍板，从 12 秒收紧）：
    /// 依据是心跳间隔实测 3.04 秒，6 秒 ≈ 2 个周期——足够让"还没死透"的链路把恢复帧吐出来，
    /// 响应又比 12 秒快一倍。断链帧（[18]=0x7F）到"标记离线"的实测延迟：12 秒窗口下是 12.4 / 12.2 秒，
    /// 本窗口预期 ≈ 6 秒。恢复帧一到候选立即被撤销（<c>ClearOfflineCandidate</c>），列表不闪。
    ///
    /// 为什么不更短（用户提过 3 秒）：3 秒只等于 1 个心跳周期，正常丢一帧就会误判；
    /// 而且鼠标停手时 dongle 本来就会静音，"断链后一直没有新帧"与"停手静音"在被动路径上无法区分。
    ///
    /// <b>Silence = 90 秒</b>：**兜底，不是主要判据**。0x07 是活动驱动的心跳，动鼠标时稳定 3.04 秒一帧，
    /// 一停手就静音——4 分钟内实测有 5 次 >10 秒的正常空档（13.3 / 18.2 / 26.1 / 35.3 / 39.5 秒），
    /// 所以 12 秒那种短阈值必然误报。90 秒只留给"鼠标真关机/没电"。
    ///
    /// <b>CountPollFailures = false</b>：本插件零写入（<c>WritesEnabled=false</c>，不存在任何写入路径），
    /// <see cref="BatteryPluginBase.PollDevice"/> 恒返回 null 是**协议形态**（该世代协议没有电量查询命令），
    /// 不是失联，所以不能让基类把它当失败计数。
    /// </summary>
    protected override OfflinePolicy Offline { get; } = new()
    {
        Confirm = TimeSpan.FromSeconds(6),
        Silence = TimeSpan.FromSeconds(90),
        CountPollFailures = false,
    };

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
        return info.InputReportLength == RapooCodec.StatusReportLength;
    }

    /// <summary>
    /// 探测 = 被动听 5 秒心跳，听不到 = 鼠标睡着，返回 null 进冷却（5 秒），下一轮再试。
    /// 窗口取 5 秒的依据：2026-10-06 用 hidapi 实测本机 dongle 心跳间隔 **3.04 秒**（很稳），
    /// 5 秒 = 1.6× 余量，足够容忍丢一条心跳。探测本身不发任何字节。
    /// </summary>
    protected override (string Protocol, int? Percent, ChargingState Charging)? Probe(HidDeviceHandle handle)
    {
        var read = ListenStatus(handle, TimeSpan.FromSeconds(5));
        if (!read.HasValue) return null;
        return ("status-report7", read.Value.Percent, read.Value.Charging);
    }

    /// <summary>
    /// 读数由后台被动监听负责；轮询不再占 ReadInput（避免与监听抢同一悬空读）。
    /// 沉默离线见 <see cref="OfflinePolicy.Silence"/>。
    /// </summary>
    protected override (int? Percent, ChargingState Charging)? PollDevice(TrackedDevice _) => null;

    /// <summary>解析被动 input：0x07 状态帧 → 电量/充电，并维护 RF 断链候选；其它帧忽略。</summary>
    protected override (int? Percent, ChargingState Charging, bool Offline)? TryParsePassiveInput(
        TrackedDevice dev, byte[] input)
    {
        var status = RapooCodec.Parse(input);
        if (status.Kind == RapooFrameKind.NotStatus) return null;

        Log.Info($"雷柏 状态帧: 连接={status.Link} 充电标志={status.ChargeFlag} 电量={status.Percent}% " +
                 $"RF=0x{status.RfByte:X2}(bit7连接={!status.LinkDown} RSSI={status.RfByte & 0x7F}) 原始={Log.Hex(input)}");

        // 链路位与电量无关，先处理——电量越界的帧（休眠瞬态）同样要能标记/撤销候选。
        if (status.LinkDown) MarkOfflineCandidate(dev);
        else ClearOfflineCandidate(dev);

        if (status.Kind == RapooFrameKind.StatusInvalidPercent)
        {
            Log.Debug($"雷柏 电量越界/归零（疑似休眠瞬态），丢弃: {Log.Hex(input)}");
            return null;
        }

        return (status.Percent, status.Charging, Offline: false);
    }

    /// <summary>在截止时间前收 input report，解析 0x07 状态帧。所有听到的帧都记原始日志（协议排错靠它）。</summary>
    private static (int Percent, ChargingState Charging)? ListenStatus(HidDeviceHandle handle, TimeSpan window)
    {
        var deadline = DateTime.UtcNow + window;
        while (DateTime.UtcNow < deadline)
        {
            var input = handle.ReadInput(TimeSpan.FromMilliseconds(400));
            if (input == null) continue;
            Log.Debug($"雷柏 收到报文: {Log.Hex(input)}");

            var status = RapooCodec.Parse(input);
            if (status.Kind == RapooFrameKind.Status) return (status.Percent, status.Charging);
        }
        return null;
    }

    protected override string BuildDisplayName(HidDeviceInfo info)
        => string.IsNullOrEmpty(info.Product) ? $"雷柏 ({info.VidPid})" : $"雷柏 {info.Product}";
}
