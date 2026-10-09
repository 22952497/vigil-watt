using System.Collections.Concurrent;
using VigilWatt.Core.Hid;
using VigilWatt.Core.Models;
using VigilWatt.Core.Settings;
using VigilWatt.Core.Utils;

namespace VigilWatt.Core.Plugins.Betop;

/// <summary>
/// 北通 (BETOP) 手柄 2.4G 接收器插件。
///
/// 协议出处（符合 safety.md "只发有出处的帧"）：开源实现 oitsukiii/betop-battery（MIT）——
/// 其协议来自对北通官方 Electron 客户端的**互操作性分析**，鲲鹏20（BTP-KP20EB，
/// 接收器 USB\VID_20BC&amp;PID_5191）在 Windows 11 + 2.4G 接收器上真机验证。
///
/// 接收器是复合设备，4 个接口靠 usage 区分：<c>0x03=厂商(电量通道)</c>、<c>0x02=鼠标</c>、
/// <c>0x06=键盘</c>、<c>0x05=手柄(XInput 呈现)</c>。本插件**只碰厂商接口**（usage_page 0xFF00）。
///
/// 电量读法（状态报告，见 <see cref="BetopCodec"/>）：
/// - 查询：帧 <c>[report_id, 0x15, 0…]</c>（header 0x15 = cmd 0x5 状态报告 / subcmd 0x1），
///   补零到接口输出长度后写入。**report ID 从接口的 HID caps 读**（见 <see cref="ResolvePlan"/>）——
///   它是设备/型号自己的描述符决定的（鲲鹏20 厂商集合 = 0x02，T9 = 0x01/0x04/0x05），硬编码必错；
/// - 响应：<c>[report_id, 0x15, &lt;电量%&gt;, &lt;保留&gt;, &lt;充电标志(低4位)&gt;, …]</c>，
///   实测鲲鹏20 <c>02 15 64 00 51</c> = 100% 充电、<c>02 15 5D 00 50</c> = 93% 放电。
///
/// 为什么标准 API 读不到：接收器在与 Windows 的标准通道上把自己报告成"有线设备"
/// （XInputGetBatteryInformation → BatteryType=WIRED / BatteryLevel=FULL），所以必须直接问厂商接口。
/// 充电时手柄自己上报 100%（BMS 行为，非解析错误），据充电标志判"充电中"。
///
/// 安全：只发官方客户端自己发的这一条读命令（cmd=0x5），不碰写配置命令（cmd=0x2）、不刷固件、不改灯效；
/// 写入受 <see cref="ProbingEnabled"/>（AppSettings.BetopProbingEnabled）控制；连续 3 次失败由基类自动熔断。
/// 手柄休眠时无响应（按一下任意按键即恢复），此时本插件表现为离线，属预期。
///
/// **手柄关机 / 只插着 dongle 时**：dongle 不会停止推流，而是改推"未连接占位帧"
/// （byte23 = <see cref="BetopCodec.T9NotLinkedRaw"/>）。此时必须判**离线**而不是把占位值当电量——
/// 否则手柄关了列表里还显示"北通 T9 67% 在线"（2026-10-06 晚用户复报的 bug）。
/// 表现为设备列表里「北通 T9 · 离线 · --%」；手柄再开机后下一轮自动回到在线。
/// 详见 <see cref="BetopCodec.ParseT9Status"/> 上方的真机对照表。
/// </summary>
public sealed class BetopPlugin : BatteryPluginBase
{
    /// <summary>北通（BETOP）USB 厂商 ID。</summary>
    public const ushort BetopVid = 0x20BC;

    /// <summary>厂商自定义接口的 usage_page（HID 规范的厂商页 0xFF00）。</summary>
    public const ushort VendorUsagePage = 0xFF00;

    /// <summary>厂商接口的 usage（接收器 4 个接口里 0x03 = 厂商通道）。</summary>
    public const ushort VendorUsage = 0x0003;

    /// <summary>回退写入长度（接口未声明输出长度时使用；鲲鹏20 厂商接口为 64）。</summary>
    private const int FallbackWriteLength = 64;

    public override string Name => "Betop";
    public override string DisplayName => "北通手柄";
    public override DeviceKind Kind => DeviceKind.Gamepad;

    /// <summary>探测写入许可（AppSettings.BetopProbingEnabled）。默认开启——查询是官方客户端同款只读命令。</summary>
    public static bool ProbingEnabled => AppSettings.Load().BetopProbingEnabled;

    /// <summary>查询需要写帧；用户关掉开关即完全静默（不认领、不写入）。</summary>
    protected override bool WritesEnabled => ProbingEnabled;

    /// <summary>
    /// T9 等型号在 Col04 上持续推状态流；认领后后台只读即可实时更新电量/离线，
    /// 鲲鹏等仍靠 Poll 写 0x15 查询（被动听不到时无影响）。
    /// </summary>
    protected override bool SupportsPassiveListen => true;

    /// <summary>
    /// 本插件的离线判定策略（见 <see cref="OfflinePolicy"/>）。
    ///
    /// <b>Silence = 0</b>：T9 在推流（约 250Hz），但沉默在这里不是可靠判据——鲲鹏无推流，
    /// 而 T9 "停流"也可能是句柄异常。手柄"没连上"有**明确占位帧**，走 <c>RequestOffline</c>
    /// 立即生效，不需要沉默兜底。
    ///
    /// <b>CountPollFailures = false</b>：与迈从同理——<c>PollDevice</c> 返回 null 有三种可能
    /// （真的没应答 / 用户关了探测 / 熔断停写中），基类分不出来，所以不能把"用户关了探测"
    /// 当成"手柄不在"。本插件当前的离线只由占位帧（<c>RequestOffline</c>）与拔插触发。
    ///
    /// 待确认（本轮不改）：类头写"手柄休眠时无响应，此时本插件表现为离线，属预期"，
    /// 但本插件并没有像迈从那样在发过帧的分支自报失败，所以"休眠翻离线"其实不会发生。
    /// 要不要补上得先有真机样本（鲲鹏20 / T9 各一次休眠），避免改成误报。
    /// </summary>
    protected override OfflinePolicy Offline { get; } = new()
    {
        Silence = TimeSpan.Zero,
        CountPollFailures = false,
    };

    /// <summary>
    /// 接收器常在、手柄可能休眠：探测失败后 30 秒再试，等用户按键唤醒。
    /// 为什么不是 1 分钟：主轮询周期就是 60 秒，失败发生在轮询开始后 ~6 秒处，
    /// 下一拍只剩 54 秒 → `54s &lt; 60s` 判冷却未过，白白再等一整轮（实测首次发现要 2 分钟）。
    /// 30 秒小于任何可能的轮询间隔，保证"每一轮都允许重探"。
    /// </summary>
    protected override TimeSpan ProbeCooldown => TimeSpan.FromSeconds(30);

    /// <summary>
    /// 候选接口：北通 VID + 厂商页 usage_page 0xFF00。
    /// 接收器的鼠标/键盘/手柄接口都在 usage_page 0x0001，据此天然排除；
    /// 兼容个别扫描器把 usage_page/usage 字段序颠倒的情况（两边都查，与 RapooPlugin 同法）。
    /// </summary>
    protected override bool IsCandidate(HidDeviceInfo info)
    {
        if (info.VendorId != BetopVid) return false;
        return info.UsagePage == VendorUsagePage || info.Usage == VendorUsagePage;
    }

    /// <summary>
    /// 探测 = 发一条状态查询，2 秒窗口内等响应。收不到 = 手柄休眠，返回 null 进冷却。
    ///
    /// dongle 报"未连接占位帧"时**也算探测成功**（协议名 + 电量 null）——基类据此认领设备、
    /// 但按"无读数 = 离线"把设备标离线，列表显示「北通 T9 · 离线 · --%」，
    /// 而不是手柄关了还显示"67% 在线"（2026-10-06 晚用户复报的 bug）。
    /// 认领后该接口进入 `_devices`，后续轮询不再重复探测它。
    ///
    /// 注意**不要**指望"探测成功"能省掉同族接口的探测：基类的 toProbe 列表在任何探测之前
    /// 就已收集完毕，同一轮里其它候选接口照旧会被探测（那 ~4 秒是既有开销，与本改动无关）。
    /// </summary>
    protected override (string Protocol, int? Percent, ChargingState Charging)? Probe(HidDeviceHandle handle)
    {
        var read = Query(handle, TimeSpan.FromSeconds(2), dev: null);
        if (read == null) return null;
        if (!read.Value.Linked) return (ProtocolName, null, ChargingState.Unknown);
        return (ProtocolName, read.Value.Percent, MapCharging(read.Value.Percent, read.Value.Charging));
    }

    /// <summary>轮询 = 发一条状态查询读电量。写入被开关/熔断挡住时返回 null（计入失败，但不再写）。</summary>
    protected override (int? Percent, ChargingState Charging)? PollDevice(TrackedDevice dev)
    {
        if (!CanWrite(dev)) return null;
        var read = Query(dev.Handle, TimeSpan.FromSeconds(1.5), dev);
        if (read == null) return null;

        // dongle 明确告知"手柄没连上"：立即标离线（省掉 3 次失败 = 3 分钟），本轮不产生读数。
        // 手柄再开机时下一轮就能读到真实读数，基类会自动把它标回在线。
        if (!read.Value.Linked)
        {
            RequestOffline(dev);
            return null;
        }

        return (read.Value.Percent, MapCharging(read.Value.Percent, read.Value.Charging));
    }

    /// <summary>
    /// 被动解析 T9 状态流 / 偶发 0x15 状态报告。未连接占位帧 → Offline=true。
    /// </summary>
    protected override (int? Percent, ChargingState Charging, bool Offline)? TryParsePassiveInput(
        TrackedDevice _, byte[] input)
    {
        var t9 = BetopCodec.ParseT9Status(input);
        if (t9.HasValue)
        {
            if (!t9.Value.Linked)
                return (null, ChargingState.Unknown, Offline: true);
            return (t9.Value.Percent, MapCharging(t9.Value.Percent, t9.Value.Charging), Offline: false);
        }

        var parsed = BetopCodec.ParseStatus(input);
        if (parsed.HasValue)
            return (parsed.Value.Percent, MapCharging(parsed.Value.Percent, parsed.Value.Charging), Offline: false);

        return null;
    }

    /// <summary>协议名（电量通道本身；在线/离线由 IsOnline + 电量是否为空表达）。</summary>
    private const string ProtocolName = "betop-status15";

    /// <summary>一条查询窗口的结果：读数 / dongle 说"手柄未连接" / 什么都没读到。</summary>
    /// <param name="Percent">电量百分比；<paramref name="Linked"/> 为 false 时为 0（无意义）。</param>
    /// <param name="Charging">是否充电中。</param>
    /// <param name="Linked">手柄是否真的连着（false = dongle 推的是"未连接占位帧"）。</param>
    private readonly record struct QueryResult(int Percent, bool Charging, bool Linked);

    /// <summary>
    /// 发一条状态查询并读取响应。写一次（长度/report ID 取自接口的报告描述符），窗口内持续收帧直到解析成功。
    /// 返回 null = 窗口内没读到任何可识别帧。<paramref name="dev"/> 只用于决定"未连接"日志要不要打（避免每分钟刷屏）。
    /// </summary>
    private static QueryResult? Query(HidDeviceHandle handle, TimeSpan window, TrackedDevice? dev)
    {
        var plan = ResolvePlan(handle);
        if (plan == null) return null;
        var (reportId, length, hasReportId) = plan.Value;

        var frame = hasReportId
            ? BetopCodec.BuildQuery(length, reportId)
            : BetopCodec.BuildQueryWithoutReportId(length);

        if (!handle.WriteOutput(frame, TimeSpan.FromMilliseconds(500)))
        {
            Log.Info($"北通 查询写失败（report_id=0x{reportId:X2} len={length}）: {handle.Info.DevicePath}");
            return null;
        }

        var deadline = DateTime.UtcNow + window;
        int logged = 0;
        while (DateTime.UtcNow < deadline)
        {
            var input = handle.ReadInput(TimeSpan.FromMilliseconds(300));
            if (input == null) continue;

            // 有些厂商集合是 250Hz 的持续流（手柄状态），逐帧打日志会刷爆 app.log——只留前几帧。
            if (logged < 4)
            {
                Log.Debug($"北通 收到报文: {Log.Hex(input)}");
                logged++;
            }
            else if (logged == 4)
            {
                Log.Info("北通 该接口在持续推流（非状态响应），后续报文不再逐条记录");
                logged++;
            }

            var parsed = BetopCodec.ParseStatus(input);
            if (parsed.HasValue)
            {
                Log.Info($"北通 状态报告: 电量={parsed.Value.Percent}% 充电={parsed.Value.Charging} 原始={Log.Hex(input)}");
                return new QueryResult(parsed.Value.Percent, parsed.Value.Charging, Linked: true);
            }

            // T9 不响应 0x15 查询，改解析 dongle 在 Col04 上持续推的状态流（byte23 = 电量）。
            var t9 = BetopCodec.ParseT9Status(input);
            if (t9.HasValue)
            {
                if (!t9.Value.Linked)
                {
                    // dongle 还插着、手柄已关机/休眠：推的是占位帧，**手柄并不在线**。
                    // 只在"第一次发现"或"从在线掉到离线"时记一行，避免每分钟刷屏。
                    if (dev == null || dev.Status.IsOnline)
                        Log.Info($"北通 T9 手柄未连接（dongle 占位帧 byte23=0x{BetopCodec.T9NotLinkedRaw:X2}，" +
                                 $"手柄已关机/休眠；dongle 仍在）: {Log.Hex(input)}");
                    return new QueryResult(0, Charging: false, Linked: false);
                }

                Log.Info($"北通 T9 状态流: 电量={t9.Value.Percent}% 原始={Log.Hex(input)}");
                return new QueryResult(t9.Value.Percent, t9.Value.Charging, Linked: true);
            }
        }
        return null;
    }

    /// <summary>查询参数（report ID / 写长度 / 是否带 report ID 前缀），按接口路径缓存。</summary>
    private readonly record struct QueryPlan(byte ReportId, int Length, bool HasReportId);

    private static readonly ConcurrentDictionary<string, QueryPlan> PlanByPath = new();

    /// <summary>
    /// 从接口的 HID caps 解析出真实 report ID 与写入长度。
    /// **report ID 是设备/型号自己的描述符决定的**（鲲鹏20 厂商集合是 0x02，T9 是 0x01/0x04/0x05），
    /// 硬编码必然写失败——HID 驱动对该接口没有的 report ID 直接报 ERROR_INVALID_FUNCTION。
    /// </summary>
    private static QueryPlan? ResolvePlan(HidDeviceHandle handle)
    {
        var path = handle.Info.DevicePath;
        if (PlanByPath.TryGetValue(path, out var cached)) return cached;

        var rid = handle.GetOutputReportId();
        if (rid == null)
        {
            Log.Info($"北通 读不到输出 report ID: {path}");
            return null;
        }

        // HidP_GetCaps 的 OutputReportByteLength **已含 report ID 那一字节**（T9 Col04 实测 16 = 15+1），
        // 所以它就是写入长度。
        int length = handle.Info.OutputReportLength >= 2 ? handle.Info.OutputReportLength : FallbackWriteLength;

        bool hasReportId = rid.Value != 0;   // 0 = 该接口不使用 report ID，报文首字节即数据
        var plan = new QueryPlan(rid.Value, length, hasReportId);
        Log.Info($"北通 接口参数: report_id=0x{rid.Value:X2} 写长度={length} 带ID={hasReportId}");
        PlanByPath[path] = plan;
        return plan;
    }

    /// <summary>充电标志非 0 = 充电中；否则 100% 判充满、其余放电。充电时手柄自报 100%，故先看标志。</summary>
    private static ChargingState MapCharging(int percent, bool charging)
    {
        if (charging) return ChargingState.Charging;
        if (percent >= 100) return ChargingState.Full;
        return ChargingState.Discharging;
    }

    /// <summary>显示名：产品串形如 "BEITONG T9 For Keyboard & Mouse"，收拾成型号后拼成 "北通 T9"。</summary>
    protected override string BuildDisplayName(HidDeviceInfo info)
    {
        var model = BetopCodec.CleanProductName(info.Product);
        return string.IsNullOrEmpty(model) ? $"北通手柄 ({info.VidPid})" : $"北通 {model}";
    }
}
