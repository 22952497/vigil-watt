using System.Collections.Concurrent;
using VigilWatt.Core.Hid;
using VigilWatt.Core.Models;
using VigilWatt.Core.Settings;
using VigilWatt.Core.Utils;

namespace VigilWatt.Core.Plugins.Mchose;

/// <summary>
/// 迈从 (MCHOSE) 2.4G 插件。
/// 本机 G87 键盘 dongle（VID_41E4&amp;PID_2001）实测协议（report ID 0x13，帧校验=前19字节之和）：
/// - 开会话：[13, 05, 01, 0×16, sum]（官方 M HUB WEB 连接时发送，会话没开 dongle 不响应）；
/// - 电量查询：[13, 4A, 01, 0×16, sum] → [5]=电量%、[6]=充电标志；
/// - dongle 自发推送：[13, 0A, 01, 00, 04, **类型**, …]，**类型 0x05 = 电量**（[6]=电量%、[7]=标志），
///   **类型 0x02 = 设备状态通知**，payload 实测 0x00/0x01 交替、设备随后仍可应答查询，语义未明，仅记录。
/// 注意：键盘空闲（未打字）时 dongle 对电量查询返回**逐字节相同的缓存帧**，充电标志可能冻结很久；
/// 键盘被唤醒（按键/会话重建）后缓存才刷新——这是固件行为，主机侧无法提前感知（2026-10-06 实测）。
/// 另实现新一代 QHW 协议（M HUB WEB 前端逆向，report 0x4D + 命令 0x0904），供新 dongle 使用。
/// 安全约束（doc/hardware/safety.md）：只发官方/实测帧；禁止任何命令扫描；写入受
/// MchoseProbingEnabled 开关控制；连续 3 次失败由基类自动停写。
/// 注意：dongle 会话被崩溃进程占住时所有查询静音，重新插拔接收器可恢复。
/// </summary>
public sealed class MchosePlugin : BatteryPluginBase
{
    public override string Name => "Mchose";
    public override string DisplayName => "迈从";
    public override DeviceKind Kind => DeviceKind.Keyboard;

    private const byte QhwReportId = 0x4D;
    private const ushort CmdGetBattery = 0x0904;  // 2308

    /// <summary>迈从全系协议的无效电量下界（0/1 都是设备休眠/关机的占位值），
    /// 单一来源在 <see cref="MchoseV1Codec.InvalidPercentFloor"/>，QHW/CZ 解析也共用。</summary>
    private const byte InvalidPercentFloor = MchoseV1Codec.InvalidPercentFloor;

    /// <summary>探测写入许可（AppSettings.MchoseProbingEnabled）。未开启时只监听不写。</summary>
    public static bool ProbingEnabled => AppSettings.Load().MchoseProbingEnabled;
    protected override bool WritesEnabled => ProbingEnabled;

    /// <summary>认领后后台听 0A/CZ 推送，插拔电不必等 60 秒主动查询。</summary>
    protected override bool SupportsPassiveListen => true;

    /// <summary>
    /// 本插件的离线判定策略（见 <see cref="OfflinePolicy"/>）。
    ///
    /// <b>Silence = 0</b>：0A 推送很稀疏（仅状态变化时才有），沉默在这里毫无信息量，
    /// 不能当离线判据。
    ///
    /// <b>CountPollFailures = false</b>：本插件的轮询**确实会发帧**（<c>13 4A 01</c>），
    /// 但 <c>PollDevice</c> 返回 null 有三种可能——真的没应答 / 用户关了写入许可 / 熔断停写中，
    /// 基类分不出来。所以改成由插件自己在发过帧的分支上报账（<c>ReportPollFailure</c>），
    /// 基类那条"null 就是失败"的自动计数必须关掉，否则会把"用户关了探测"误判成"键盘不在"。
    ///
    /// 注意迈从协议里**根本没有"连接状态"字段**（不像雷柏有 bit7、北通有占位帧），
    /// 所以这里是它唯一能判离线的物理依据——这也正是第②层"要不要被动监听"不能与
    /// "轮询空读算不算失败"合并的原因。
    /// </summary>
    protected override OfflinePolicy Offline { get; } = new()
    {
        Silence = TimeSpan.Zero,
        CountPollFailures = false,
    };

    /// <summary>
    /// 探测要写帧（会话帧 + 最多 2 条电量查询），所以不能走 <see cref="SupportsFastDiscovery"/>
    /// （那条路只给纯被动零写入插件），但**不参与 15 秒发现节拍**的代价是：键盘唤醒/开机后最坏要等
    /// 60 秒主轮询才被认领，用户看到的是"键盘开了半天才显示在线"（2026-10-08 用户复报）。
    /// 改用 SupportsDiscoveryProbe 参与 15 秒节拍，写入频率由下面的 ProbeCooldown 兜住。
    /// </summary>
    public override bool SupportsDiscoveryProbe => true;

    /// <summary>
    /// **10 秒 &lt; 15 秒发现节拍**，保证每一拍都允许重探。
    ///
    /// 为什么必须小于一拍：探测失败发生在拍点之后约 3.7 秒（会话 1.2s + 两次查询各 1.2s），
    /// 若冷却 ≥ 15 秒，下一拍看到的是"才过了 11.3 秒"，会被判冷却未过而白白跳过——
    /// 实际重试间隔翻倍成 30 秒（相位坑见 <see cref="BatteryPluginBase.ProbeCooldown"/> 的注释）。
    ///
    /// 写入代价：只有**设备不响应**时才会重探，每次 ≈ 3 帧（1 会话 + ≤2 查询），最坏 4 拍/分钟 ≈ 12 帧/分钟；
    /// 一旦认领成功，<c>Attach</c> 对已知设备直接返回，这一路**零写入**。
    /// </summary>
    protected override TimeSpan ProbeCooldown => TimeSpan.FromSeconds(10);

    protected override bool IsCandidate(HidDeviceInfo info)
    {
        if (info.VendorId == Logitech.LogitechPlugin.LogitechVid) return false;
        if (info.VendorId == Rapoo.RapooPlugin.RapooVid) return false;
        // 北通手柄接收器也有 usage 0xFF00 的 vendor 接口，但它不是迈从协议，勿发 QHW/0x13 帧
        if (info.VendorId == Betop.BetopPlugin.BetopVid) return false;
        // dongle 上的 consumer/system/标准键盘接口即使产品名含 MCHOSE 也不是配置通道，
        // 必须是 vendor 用法 + 至少 20/20 的 I/O 才有意义
        bool vendor = info.UsagePage >= 0xFF00 || info.Usage >= 0xFF00;
        if (!vendor) return false;
        if (info.OutputReportLength == 20 && info.InputReportLength == 20) return true; // QHW/邮箱通道
        if (info.OutputReportLength == 64 && info.InputReportLength == 64) return true; // 大帧 QHW 通道
        return false;
    }

    /// <summary>
    /// 接口路径 → 已判定协议（进程内学习）。**只在探测成功后写入，失败不抹掉**。
    /// 路径里带 `VID_xxxx&PID_xxxx&MI_xx&Colxx`，同一条路径永远是同一个型号，协议不会变；
    /// 探测失败最常见的原因是"键盘在睡"（dongle 在位、设备不应答），那说明不了协议变了。
    /// </summary>
    private static readonly ConcurrentDictionary<string, string> KnownProtocolByPath = new();

    /// <summary>
    /// "同一条路径只记一次"的日志去重表，key = <c>路径|用途</c>。
    ///
    /// 存在的理由：发现节拍每 15 秒会重探一次，设备睡着时逐次记录会把 app.log 刷满、而且没有新信息。
    /// 配 <see cref="ResetLogOnce"/> 就变成"**状态变化才记**"——恢复正常时清掉标记，
    /// 下次再出问题会重新记一条（与北通占位帧的记法一致）。
    /// </summary>
    private static readonly ConcurrentDictionary<string, bool> LogOnceByKey = new();

    private const string LogKeyQhwSkip = "qhw-skip";          // "这条通道不是 QHW"
    private const string LogKeyQhwNoPermit = "qhw-no-permit"; // "没开写入许可，跳过主动查询"
    private const string LogKeyKbV1Silent = "kbv1-silent";    // "keyboard-v1 问了两次都不应答"

    /// <summary>该路径的这类事件还没记过 → 记一条（返回 true）；记过就跳过（返回 false）。</summary>
    private static bool ShouldLogOnce(HidDeviceHandle handle, string what)
        => LogOnceByKey.TryAdd($"{handle.Info.DevicePath}|{what}", true);

    /// <summary>清掉"记过"标记，让下次再发生时重新记一条（"状态变化才记"的关键一半）。</summary>
    private static void ResetLogOnce(HidDeviceHandle handle, string what)
        => LogOnceByKey.TryRemove($"{handle.Info.DevicePath}|{what}", out _);

    /// <summary>协议探测：官方 QHW（新世代）→ 键盘 V1 会话协议（本机 G87）。</summary>
    protected override (string Protocol, int? Percent, ChargingState Charging)? Probe(HidDeviceHandle handle)
    {
        // consumer/system 等小接口没有可用的输出通道，直接排除（避免握手帧越界）
        if (handle.Info.OutputReportLength < 20 || handle.Info.InputReportLength < 20) return null;

        var path = handle.Info.DevicePath;

        // 已知协议：只试对应那条通道，**失败就返回、不清记忆**。
        //
        // 2026-10-08 实修：旧代码在这里 `TryRemove` 后回落完整探测，代价是每 15 秒的发现节拍
        // 都白写一次 QHW 握手（本机 G87 不是 QHW 通道，必失败 → 每 15 秒一条 WARN 刷屏）
        // 再重试一遍 keyboard-v1，写入量翻倍。缓存只在**探测成功**时写入，本身就是可靠判据，
        // 没有理由因为"设备睡着了"把它丢掉；真要重置，重启应用即可（缓存是进程内的）。
        if (KnownProtocolByPath.TryGetValue(path, out var known))
            return TryKnownProtocol(handle, known);

        var qhw = TryQhw(handle);
        if (qhw != null)
        {
            KnownProtocolByPath[path] = qhw.Value.Protocol;
            return qhw;
        }

        var kbV1 = TryKeyboardV1(handle);
        if (kbV1.HasValue)
        {
            var (p, c) = kbV1.Value;
            KnownProtocolByPath[path] = "keyboard-v1";
            return ("keyboard-v1", p, c);
        }
        return null;
    }

    /// <summary>按已判定的协议只试那一条通道（避免每次探测都先空等 QHW 3 秒）。</summary>
    private static (string Protocol, int? Percent, ChargingState Charging)? TryKnownProtocol(HidDeviceHandle handle, string known)
        => known switch
        {
            "qhw" or "qhw-push" => TryQhw(handle),
            "keyboard-v1" => TryKeyboardV1(handle) is { } kb ? ("keyboard-v1", kb.Percent, kb.Charging) : null,
            _ => null,
        };

    protected override (int? Percent, ChargingState Charging)? PollDevice(TrackedDevice dev)
    {
        // 熔断 / 未开写入 / 纯推送协议：读数交给后台被动监听，轮询不占 ReadInput。
        // 注意这几条**都没发帧**，所以直接返回、不计失败——不能把"用户关了探测"当成"键盘不在"。
        if (!CanWrite(dev) || dev.Protocol == "qhw-push")
            return null;

        var read = dev.Protocol switch
        {
            "qhw" => PollQhw(dev),
            "keyboard-v1" => PollKeyboardV1(dev),
            _ => null,
        };

        // 发过帧：有应答 → 清零；无应答 → 记账（满 3 次 → 标离线 + 熔断停写）。
        //
        // 必须在这里自己报账（2026-10-08 用户复报"键盘关机后一直不显示离线"）：
        // 本插件把 OfflinePolicy.CountPollFailures 置成了 false（因为 null 有歧义，见上面那条注释），
        // 所以基类不会替我们数——只有本插件知道这一轮到底发帧了没有。
        if (read.HasValue) ReportPollAnswered(dev);
        else ReportPollFailure(dev);

        return read;
    }

    /// <summary>
    /// 被动解析：keyboard-v1 的 0A 电量推送，或 QHW CZ 通道 FA FB 07 推送。
    /// 查询应答（4A / QHW getBattery）留给 PollDevice 的写读会话，这里不抢。
    /// </summary>
    protected override (int? Percent, ChargingState Charging, bool Offline)? TryParsePassiveInput(
        TrackedDevice dev, byte[] input)
    {
        if (dev.Protocol is "keyboard-v1")
        {
            var push = MchoseV1Codec.ParsePush(input);
            if (push.Kind == V1PushKind.Battery)
                return (push.Percent, ParseKeyboardV1Charge(push.ChargeFlag, push.Percent), Offline: false);
            if (push.Kind == V1PushKind.StateNotify)
            {
                // payload 0x00/0x01 的语义**至今未定论**（全天交替出现，且每次通知后 1 分钟内设备
                // 都能正常应答电量查询），所以绝不能当离线证据。
                //
                // 但可以当**触发器**：收到 payload=0x00 就让基类立刻补一次核实查询。
                // 键盘真关机时，这条路径把"发现离线"从 60 秒轮询攒 3 次失败（2~3 分钟）
                // 压到秒级；键盘其实好着的话，那次查询会正常应答，什么都不会发生。
                // 只认 0x00（不当证据，所以选哪个值只影响触发时机，不影响判定结果）。
                if (push.Payload == MchoseV1Codec.StatePayloadZero)
                {
                    Log.Info($"迈从 被动收到设备状态通知 payload=0x00，触发一次核实查询: {Log.Hex(input)}");
                    RequestConfirm(dev);
                }
                else
                {
                    Log.Debug($"迈从 被动收到设备状态通知（payload=0x{push.Payload:X2}，仅记录）: {Log.Hex(input)}");
                }
            }
            return null;
        }

        if (dev.Protocol is "qhw" or "qhw-push")
        {
            var cz = ParseCzPush(input);
            if (cz.HasValue)
                return (cz.Value.Percent,
                    cz.Value.Charging ? ChargingState.Charging : ChargingState.Discharging,
                    Offline: false);
        }

        return null;
    }

    private static byte _seq = 1;

    /// <summary>构造官方 QHW 帧：[0x4D, ver=1, flags=3, len, cmdLo, cmdHi, biz=0, seq, payload…, XOR 校验]，补零到接口输出长度。</summary>
    private static byte[] BuildQhwFrame(HidDeviceHandle handle, ushort command, params byte[] payload)
    {
        int outLen = Math.Max(handle.Info.OutputReportLength, payload.Length + 10);
        var frame = new byte[outLen];
        frame[0] = QhwReportId;
        frame[1] = 0x01;                      // version
        frame[2] = 0x03;                      // flags
        frame[3] = (byte)payload.Length;      // length
        frame[4] = (byte)(command & 0xFF);    // cmd lo
        frame[5] = (byte)(command >> 8);      // cmd hi
        frame[6] = 0x00;                      // bizCode
        frame[7] = _seq++;                    // sequence
        for (int i = 0; i < payload.Length; i++) frame[8 + i] = payload[i];
        byte xor = 0;
        for (int i = 1; i < 8 + payload.Length; i++) xor ^= frame[i]; // 官方: 255 & d[1..] 逐字节异或
        frame[8 + payload.Length] = xor;
        return frame;
    }

    /// <summary>解析 QHW 响应。返回 (充电状态字节, 电量%) 或 null（非电量响应）。</summary>
    private static (byte ChargeRaw, int Percent)? ParseQhwBatteryResponse(byte[] input, ushort command)
    {
        // Windows ReadInput 缓冲 [0]=report ID；兼容无 ID 前缀（[0]=0）两种布局
        foreach (int off in new[] { 0, 1 })
        {
            if (input.Length < off + 10) continue;
            if (input[off] != QhwReportId || input[off + 1] != 0x01) continue;
            if (input[off + 4] != (command & 0xFF) || input[off + 5] != (command >> 8)) continue;
            byte charge = input[off + 8];
            byte pct = input[off + 9];
            if (pct <= InvalidPercentFloor || pct > 100 || charge > 2) return null;   // ≤1 = 休眠/关机占位帧，无效
            return (charge, pct);
        }
        return null;
    }

    /// <summary>解析 CZ 通道电量推送 [0xFA, 0xFB, 0x07, 电量%, 充电标志]（官方 handleMessage 分支）。</summary>
    private static (int Percent, bool Charging)? ParseCzPush(byte[] input)
    {
        foreach (int off in new[] { 0, 1 })
        {
            if (input.Length < off + 5) continue;
            if (input[off] != 0xFA || input[off + 1] != 0xFB || input[off + 2] != 0x07) continue;
            byte pct = input[off + 3];
            byte chg = input[off + 4];
            if (pct <= InvalidPercentFloor || pct > 100 || chg > 2) return null;   // ≤1 = 休眠/关机占位帧，无效
            return (pct, chg == 1);
        }
        return null;
    }

    private static ChargingState MapCharging(byte chargeRaw)
    {
        return chargeRaw switch
        {
            1 => ChargingState.Charging,
            2 => ChargingState.Full,
            0 => ChargingState.Discharging,
            _ => ChargingState.Unknown,
        };
    }

    /// <summary>发送官方握手帧（数据 AA 55 AA 55 AA 55，report 0x4D）。</summary>
    /// <param name="quiet">
    /// 写失败时不记 WARN。**只有"把握手当通道判据"的探测路径该传 true**——
    /// 非 QHW 通道的描述符里没有 report 0x4D，写必然失败，这正是"不是这条通道"的判据；
    /// 而在已知的 QHW 设备上（轮询）握手写失败是真故障，要留 WARN。
    /// </param>
    private static bool SendQhwHandshake(HidDeviceHandle handle, bool quiet = false)
    {
        var handshake = new byte[handle.Info.OutputReportLength];
        handshake[0] = QhwReportId;
        byte[] data = { 0xAA, 0x55, 0xAA, 0x55, 0xAA, 0x55 };
        Array.Copy(data, 0, handshake, 1, data.Length);
        return handle.WriteOutput(handshake, TimeSpan.FromMilliseconds(500), quiet);
    }

    /// <summary>
    /// 官方 QHW 电量查询（M HUB WEB 连接时序：握手 → getBattery(0x0904)）。
    /// 每次探测发一次握手 + 最多 2 条 getBattery；同时监听 CZ 通道的自发电量推送。
    /// 注意握手放在最前：非 QHW dongle 会在握手写入处立即失败并快速退出（不再先空等被动监听）。
    /// </summary>
    private static (string Protocol, int? Percent, ChargingState Charging)? TryQhw(HidDeviceHandle handle)
    {
        // 只读模式（未开启写入许可）：不能写握手，只能被动等 dongle 自发推送
        if (!ProbingEnabled)
        {
            var passive = ListenCzPush(handle, TimeSpan.FromSeconds(3));
            if (passive.HasValue)
            {
                var (pct0, chg0) = passive.Value;
                return ("qhw-push", pct0, chg0 ? ChargingState.Charging : ChargingState.Discharging);
            }
            // 同样只在状态变化时记：关掉写入许可后设备永远不会被认领，这条本来会每 15 秒一条、
            // 无限刷下去（比"键盘睡着"那种自限的情况更糟）。重新开启许可会清标记。
            if (ShouldLogOnce(handle, LogKeyQhwNoPermit))
                Log.Info($"迈从 qhw: 未开启写入许可，跳过主动查询: {handle.Info.DevicePath}");
            return null;
        }

        // 许可已开：清掉"没许可"的记过标记，这样下次再关掉时会重新记一条
        ResetLogOnce(handle, LogKeyQhwNoPermit);

        // 官方时序：先握手。非 QHW dongle（如本机 G87：该接口只声明 report 0x13）握手写会立即以
        // ERROR_INVALID_PARAMETER 失败 → 这里直接返回，省掉旧版"先空等 3 秒被动监听"的浪费。
        // 对照实验已确认：旧时序（先被动监听再握手）在本机 G87 上每次首扫白等 3 秒，结果完全相同。
        // quiet: true —— 写失败在这里是**预期结果**（"不是 QHW 通道"的判据），下面那条 INFO 已经
        // 说清楚了；否则发现节拍每 15 秒就会刷一条 WARN，看着像故障（2026-10-08 实修）。
        // INFO 本身也只记第一次：设备一直睡着时它会每 15 秒重复一条（同样刷屏）。
        if (!SendQhwHandshake(handle, quiet: true))
        {
            if (ShouldLogOnce(handle, LogKeyQhwSkip))
                Log.Debug($"迈从 qhw 握手写失败（非 QHW 通道，快速跳过；同路径后续不再重复记录）: {handle.Info.DevicePath}");
            return null;
        }

        // 握手成功说明是 QHW 通道：清掉"不是 QHW"的记过标记（下次真出问题会重新记一条），
        // 先给一小段时间收 CZ 自发电量推送
        ResetLogOnce(handle, LogKeyQhwSkip);
        var result = ListenCzPush(handle, TimeSpan.FromSeconds(1));
        if (result.HasValue)
        {
            var (pct, charging) = result.Value;
            return ("qhw-push", pct, charging ? ChargingState.Charging : ChargingState.Discharging);
        }

        var frame = BuildQhwFrame(handle, CmdGetBattery);
        for (int attempt = 1; attempt <= 2; attempt++)
        {
            if (!handle.WriteOutput(frame, TimeSpan.FromMilliseconds(500)))
            {
                Log.Debug($"迈从 qhw 第{attempt}次写失败: {handle.Info.DevicePath}");
                return null;
            }

            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(2);
            while (DateTime.UtcNow < deadline)
            {
                var input = handle.ReadInput(TimeSpan.FromMilliseconds(300));
                if (input == null) continue;

                var push = ParseCzPush(input);
                if (push.HasValue)
                {
                    Log.Info($"迈从 qhw 收到 CZ 电量推送: 电量={push.Value.Percent} 充电={push.Value.Charging} 原始={Log.Hex(input)}");
                    return ("qhw-push", push.Value.Percent,
                        push.Value.Charging ? ChargingState.Charging : ChargingState.Discharging);
                }

                var parsed = ParseQhwBatteryResponse(input, CmdGetBattery);
                if (parsed.HasValue)
                {
                    Log.Info($"迈从 qhw getBattery 解析: 电量={parsed.Value.Percent} 状态字节={parsed.Value.ChargeRaw} 原始={Log.Hex(input)}");
                    return ("qhw", parsed.Value.Percent, MapCharging(parsed.Value.ChargeRaw));
                }

                Log.Debug($"迈从 qhw 收到非匹配报文: {Log.Hex(input)}");
            }
        }
        Log.Info($"迈从 qhw 无电量响应: {handle.Info.DevicePath}");
        return null;
    }

    /// <summary>纯被动监听 CZ 电量推送，不写任何字节。</summary>
    private static (int Percent, bool Charging)? ListenCzPush(HidDeviceHandle handle, TimeSpan window)
    {
        var deadline = DateTime.UtcNow + window;
        while (DateTime.UtcNow < deadline)
        {
            var input = handle.ReadInput(TimeSpan.FromMilliseconds(300));
            if (input == null) continue;
            var push = ParseCzPush(input);
            if (push.HasValue)
            {
                Log.Info($"迈从 CZ 推送: 电量={push.Value.Percent} 充电={push.Value.Charging} 原始={Log.Hex(input)}");
                return push;
            }
        }
        return null;
    }

    /// <summary>
    /// 键盘 V1 协议（本机 dongle 41E4:2001 实测打通，与官方 M HUB WEB 抓包一致）：
    /// 1. 开会话（官方 HUB 连接时发送）：[0x13, 0x05, 0x01, 0×16, sum] → ACK [13 05 01 00 0A 03 ...]
    /// 2. 电量查询：[0x13, 0x4A, 0x01, 0×16, sum] → [5]=电量%, [6]=充电标志（0x10 充电中）。
    ///    实测 35% 时 [6]=0x01，充电语义待用户插线校准。
    /// 3. dongle 自发推送：[13, 0x0A, 01, 00, 04, 类型, …]，类型 0x05=电量（[6]=%，[7]=标志），
    ///    类型 0x02=设备状态通知（payload 0x00/0x01 交替，语义未明，仅记录不当离线信号）。
    /// 帧格式与解析全部在 <see cref="MchoseV1Codec"/>（纯函数，单测在 src/tests/VigilWatt.Core.Tests/）。
    /// 会话必须先开，否则 dongle 不响应（且被崩溃进程占住会话时所有人都会静音，需重新插拔释放）。
    ///
    /// **"无响应"只在状态变化时记日志**：发现节拍每 15 秒重探一次，键盘睡着时逐次记录会把 app.log
    /// 刷满且没有新信息。成功一次就 <see cref="ResetLogOnce"/>，于是"好了之后又不应答"会重新记一条。
    /// </summary>
    private static (int? Percent, ChargingState Charging)? TryKeyboardV1(HidDeviceHandle handle)
    {
        if (!ProbingEnabled) return null;

        // 开会话（官方时序），失败不影响下一步——实测 dongle 记住会话状态
        SendKeyboardV1Frame(handle, MchoseV1Codec.SessionCmd, 0x01);
        var deadline0 = DateTime.UtcNow + TimeSpan.FromSeconds(1.2);
        while (DateTime.UtcNow < deadline0)
        {
            var input = handle.ReadInput(TimeSpan.FromMilliseconds(300));
            if (input == null) continue;

            var read = InterpretV1Input(input, out bool stop);
            if (stop)
            {
                Log.Info($"迈从 会话期间即收到电量推送，直接采用");
                ResetLogOnce(handle, LogKeyKbV1Silent);
                return read;
            }
            Log.Debug($"迈从 会话ACK: {Log.Hex(input)}");
        }

        // 电量查询，最多重试 2 次（键盘休眠时先醒来）
        for (int attempt = 1; attempt <= 2; attempt++)
        {
            if (!SendKeyboardV1Frame(handle, MchoseV1Codec.BatteryQueryCmd, 0x01))
            {
                Log.Debug($"迈从 keyboard-v1 第{attempt}次写失败: {handle.Info.DevicePath}");
                continue;
            }

            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(1.2);
            while (DateTime.UtcNow < deadline)
            {
                var input = handle.ReadInput(TimeSpan.FromMilliseconds(300));
                if (input == null) continue;

                var read = InterpretV1Input(input, out bool stop);
                if (stop)
                {
                    ResetLogOnce(handle, LogKeyKbV1Silent);
                    return read;
                }
            }
        }

        if (ShouldLogOnce(handle, LogKeyKbV1Silent))
            Log.Debug($"迈从 keyboard-v1 两次询问无响应（键盘多半在睡；恢复应答后会再记一条）: {handle.Info.DevicePath}");
        return null;
    }

    /// <summary>构造并发送 V1 帧：[0x13, cmd, param, 0×16, sum(前19字节)]。</summary>
    private static bool SendKeyboardV1Frame(HidDeviceHandle handle, byte cmd, byte param)
    {
        if (handle.Info.OutputReportLength < MchoseV1Codec.FrameLength) return false;
        return handle.WriteOutput(MchoseV1Codec.BuildFrame(cmd, param), TimeSpan.FromMilliseconds(500));
    }

    /// <summary>
    /// 解读一条输入帧（0A 推送或 4A 响应），并负责日志。
    /// <paramref name="stop"/> = true 表示本轮不必再等（已拿到读数）。
    /// </summary>
    private static (int? Percent, ChargingState Charging)? InterpretV1Input(byte[] input, out bool stop)
    {
        stop = false;

        var push = MchoseV1Codec.ParsePush(input);
        switch (push.Kind)
        {
            case V1PushKind.Battery:
                stop = true;
                // 必须打日志：这条路径原来静默，于是"轮询到底跑没跑、有没有拿到读数"在日志里看不见
                // （2026-10-08 排查时就撞上过：某一拍轮询全无痕迹，其实就是走了这里拿到的读数）。
                Log.Info($"迈从 0A 电量推送: 电量={push.Percent} 标志=0x{push.ChargeFlag:X2} 原始={Log.Hex(input)}");
                return (push.Percent, ParseKeyboardV1Charge(push.ChargeFlag, push.Percent));

            case V1PushKind.StateNotify:
                // 0x02 通知不当休眠/关机处理（2026-10-06 复测推翻旧结论）：payload 0x00/0x01 全天交替出现，
                // 且每次通知后 1 分钟内设备都能正常应答电量查询，按休眠标离线只会造成在线↔离线闪烁。
                // 语义未明，仅记录。**真正的失联靠 PollDevice 里的失败记账**（ReportPollFailure，
                // 满 OfflinePolicy.MaxFailures 次标离线 + 熔断）——本插件把 CountPollFailures 置成了 false，
                // 基类不会替我们数，所以必须自己报账。2026-10-08 修正：旧注释以为基类会自动兜底，是错的。
                //
                // 这里不做"触发核实"：本方法服务于主动路径（探测/轮询），查询刚刚才发过，
                // 立刻再查一次没有新信息。触发核实只放在被动路径（TryParsePassiveInput）。
                Log.Debug($"迈从 收到设备状态通知（payload=0x{push.Payload:X2}，语义未明仅记录）: {Log.Hex(input)}");
                return null;

            case V1PushKind.BatteryInvalid:
                Log.Debug($"迈从 忽略异常电量帧（电量={push.Percent}）: {Log.Hex(input)}");
                return null;

            case V1PushKind.Other:
                Log.Debug($"迈从 忽略非电量推送帧（类型=0x{push.Type:X2}）: {Log.Hex(input)}");
                return null;
        }

        switch (MchoseV1Codec.ParseBatteryResponse(input, out int pct, out byte flag))
        {
            case V1ResponseKind.Battery:
                Log.Info($"迈从 keyboard-v1 解析: 电量={pct} 状态字节=0x{flag:X2} 原始={Log.Hex(input)}");
                stop = true;
                return (pct, ParseKeyboardV1Charge(flag, pct));

            case V1ResponseKind.InvalidBattery:
                Log.Debug($"迈从 keyboard-v1 电量占位/越界: {Log.Hex(input)}");
                return null;

            default:
                return null;
        }
    }

    /// <summary>充电标志：0x10 位=充电中；实测 35% 放电时标志=0x01（语义待插线校准）。</summary>
    private static ChargingState ParseKeyboardV1Charge(byte flag, int percent)
    {
        if ((flag & 0x10) != 0) return ChargingState.Charging;
        if (percent >= 100) return ChargingState.Full;
        return ChargingState.Discharging;
    }

    /// <summary>keyboard-v1 轮询：只发电量查询（会话已建立），解析 4A 响应和 0A 推送。</summary>
    private static (int? Percent, ChargingState Charging)? PollKeyboardV1(TrackedDevice dev)
    {
        if (!ProbingEnabled) return null;

        if (!SendKeyboardV1Frame(dev.Handle, MchoseV1Codec.BatteryQueryCmd, 0x01)) return null;
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(1.2);
        while (DateTime.UtcNow < deadline)
        {
            var input = dev.Handle.ReadInput(TimeSpan.FromMilliseconds(300));
            if (input == null) continue;
            var read = InterpretV1Input(input, out bool stop);
            if (stop) return read;
        }
        return null;
    }

    /// <summary>qhw 轮询：握手一次 → getBattery 一条 → 解析（含 CZ 推送兜底）。</summary>
    private static (int? Percent, ChargingState Charging)? PollQhw(TrackedDevice dev)
    {
        if (!ProbingEnabled) return null;

        if (!dev.HandshakeSent)
        {
            if (!SendQhwHandshake(dev.Handle))
            {
                Log.Debug($"迈从 qhw 轮询握手写失败: {dev.Handle.Info.DevicePath}");
                return null;
            }
            dev.HandshakeSent = true;
        }

        var frame = BuildQhwFrame(dev.Handle, CmdGetBattery);
        if (!dev.Handle.WriteOutput(frame, TimeSpan.FromMilliseconds(500)))
        {
            Log.Debug($"迈从 qhw 轮询写失败: {dev.Handle.Info.DevicePath}");
            return null;
        }

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(2);
        while (DateTime.UtcNow < deadline)
        {
            var input = dev.Handle.ReadInput(TimeSpan.FromMilliseconds(300));
            if (input == null) continue;

            var push = ParseCzPush(input);
            if (push.HasValue)
            {
                Log.Info($"迈从 qhw 轮询收到 CZ 推送: 电量={push.Value.Percent} 充电={push.Value.Charging}");
                return (push.Value.Percent,
                    push.Value.Charging ? ChargingState.Charging : ChargingState.Discharging);
            }

            var parsed = ParseQhwBatteryResponse(input, CmdGetBattery);
            if (parsed.HasValue)
            {
                Log.Info($"迈从 qhw 轮询解析: 电量={parsed.Value.Percent} 状态字节={parsed.Value.ChargeRaw}");
                return (parsed.Value.Percent, MapCharging(parsed.Value.ChargeRaw));
            }
        }
        Log.Debug($"迈从 qhw 轮询无响应: {dev.Handle.Info.DevicePath}");
        return null;
    }
}
