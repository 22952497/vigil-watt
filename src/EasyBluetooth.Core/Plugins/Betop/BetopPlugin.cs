using System.Collections.Concurrent;
using EasyBluetooth.Core.Hid;
using EasyBluetooth.Core.Models;
using EasyBluetooth.Core.Settings;
using EasyBluetooth.Core.Utils;

namespace EasyBluetooth.Core.Plugins.Betop;

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

    /// <summary>接收器常在、手柄可能休眠：探测失败后 1 分钟再试，等用户按键唤醒。</summary>
    protected override TimeSpan ProbeCooldown => TimeSpan.FromMinutes(1);

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

    /// <summary>探测 = 发一条状态查询，2 秒窗口内等响应。收不到 = 手柄休眠，返回 null 进冷却。</summary>
    protected override (string Protocol, int? Percent, ChargingState Charging)? Probe(HidDeviceHandle handle)
    {
        var read = Query(handle, TimeSpan.FromSeconds(2));
        if (read == null) return null;
        return ("betop-status15", read.Value.Percent, MapCharging(read.Value.Percent, read.Value.Charging));
    }

    /// <summary>轮询 = 发一条状态查询读电量。写入被开关/熔断挡住时返回 null（计入失败，但不再写）。</summary>
    protected override (int? Percent, ChargingState Charging)? PollDevice(TrackedDevice dev)
    {
        if (!CanWrite(dev)) return null;
        var read = Query(dev.Handle, TimeSpan.FromSeconds(1.5));
        if (read == null) return null;
        return (read.Value.Percent, MapCharging(read.Value.Percent, read.Value.Charging));
    }

    /// <summary>发一条状态查询并读取响应。写一次（长度/report ID 取自接口的报告描述符），窗口内持续收帧直到解析成功。</summary>
    private static (int Percent, bool Charging)? Query(HidDeviceHandle handle, TimeSpan window)
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
                Log.Info($"北通 收到报文: {Log.Hex(input)}");
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
                return parsed;
            }

            // T9 不响应 0x15 查询，改解析 dongle 在 Col04 上持续推的状态流（byte23 = 电量）。
            var t9 = BetopCodec.ParseT9Status(input);
            if (t9.HasValue)
            {
                Log.Info($"北通 T9 状态流: 电量={t9.Value.Percent}% 原始={Log.Hex(input)}");
                return t9;
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
