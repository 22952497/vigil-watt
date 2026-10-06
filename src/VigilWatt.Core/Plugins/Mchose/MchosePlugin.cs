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

    /// <summary>接口路径 → 已判定协议（进程内学习）。已知 keyboard-v1 的接口下次直接跳过 QHW 的被动空等。</summary>
    private static readonly ConcurrentDictionary<string, string> KnownProtocolByPath = new();

    /// <summary>协议探测：官方 QHW（新世代）→ 键盘 V1 会话协议（本机 G87）。</summary>
    protected override (string Protocol, int? Percent, ChargingState Charging)? Probe(HidDeviceHandle handle)
    {
        // consumer/system 等小接口没有可用的输出通道，直接排除（避免握手帧越界）
        if (handle.Info.OutputReportLength < 20 || handle.Info.InputReportLength < 20) return null;

        var path = handle.Info.DevicePath;

        // 已知协议：只试对应那条通道（跳过另一条的空等）；试不出则清掉记忆、回落完整探测
        if (KnownProtocolByPath.TryGetValue(path, out var known))
        {
            var again = TryKnownProtocol(handle, known);
            if (again != null) return again;
            KnownProtocolByPath.TryRemove(path, out _);
        }

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
        // 熔断期间只走被动监听（CanWrite 在退避期满后自动放行一轮）
        if (!CanWrite(dev))
        {
            return dev.Protocol switch
            {
                "qhw" or "qhw-push" => ListenCzPush(dev.Handle, TimeSpan.FromSeconds(1)) is { } r
                    ? ((int? Percent, ChargingState Charging)?)(r.Percent,
                        r.Charging ? ChargingState.Charging : ChargingState.Discharging)
                    : null,
                "keyboard-v1" => ListenV1Push(dev),
                _ => null,
            };
        }

        return dev.Protocol switch
        {
            "qhw" => PollQhw(dev),
            "qhw-push" => ListenCzPush(dev.Handle, TimeSpan.FromSeconds(1)) is { } r
                ? ((int? Percent, ChargingState Charging)?)(r.Percent,
                    r.Charging ? ChargingState.Charging : ChargingState.Discharging)
                : null,
            "keyboard-v1" => PollKeyboardV1(dev),
            _ => null,
        };
    }

    /// <summary>被动读一小段，解析 V1 自发推送（不写帧，熔断后的降级路径）。</summary>
    private static (int? Percent, ChargingState Charging)? ListenV1Push(TrackedDevice dev)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(1);
        while (DateTime.UtcNow < deadline)
        {
            var input = dev.Handle.ReadInput(TimeSpan.FromMilliseconds(300));
            if (input == null) continue;
            var read = InterpretV1Input(input, out bool stop);
            if (stop) return read;
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
    private static bool SendQhwHandshake(HidDeviceHandle handle)
    {
        var handshake = new byte[handle.Info.OutputReportLength];
        handshake[0] = QhwReportId;
        byte[] data = { 0xAA, 0x55, 0xAA, 0x55, 0xAA, 0x55 };
        Array.Copy(data, 0, handshake, 1, data.Length);
        return handle.WriteOutput(handshake, TimeSpan.FromMilliseconds(500));
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
            Log.Info($"迈从 qhw: 未开启写入许可，跳过主动查询: {handle.Info.DevicePath}");
            return null;
        }

        // 官方时序：先握手。非 QHW dongle（如本机 G87：该接口只声明 report 0x13）握手写会立即以
        // ERROR_INVALID_PARAMETER 失败 → 这里直接返回，省掉旧版"先空等 3 秒被动监听"的浪费。
        // 对照实验已确认：旧时序（先被动监听再握手）在本机 G87 上每次首扫白等 3 秒，结果完全相同。
        if (!SendQhwHandshake(handle))
        {
            Log.Info($"迈从 qhw 握手写失败（非 QHW 通道，快速跳过）: {handle.Info.DevicePath}");
            return null;
        }

        // 握手成功说明是 QHW 通道：先给一小段时间收 CZ 自发电量推送
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
                Log.Info($"迈从 qhw 第{attempt}次写失败: {handle.Info.DevicePath}");
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

                Log.Info($"迈从 qhw 收到非匹配报文: {Log.Hex(input)}");
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
                return read;
            }
            Log.Info($"迈从 会话ACK: {Log.Hex(input)}");
        }

        // 电量查询，最多重试 2 次（键盘休眠时先醒来）
        for (int attempt = 1; attempt <= 2; attempt++)
        {
            if (!SendKeyboardV1Frame(handle, MchoseV1Codec.BatteryQueryCmd, 0x01))
            {
                Log.Info($"迈从 keyboard-v1 第{attempt}次写失败: {handle.Info.DevicePath}");
                continue;
            }

            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(1.2);
            while (DateTime.UtcNow < deadline)
            {
                var input = handle.ReadInput(TimeSpan.FromMilliseconds(300));
                if (input == null) continue;

                var read = InterpretV1Input(input, out bool stop);
                if (stop) return read;
            }
        }
        Log.Info($"迈从 keyboard-v1 两次询问无响应: {handle.Info.DevicePath}");
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
                return (push.Percent, ParseKeyboardV1Charge(push.ChargeFlag, push.Percent));

            case V1PushKind.StateNotify:
                // 0x02 通知不当休眠/关机处理（2026-10-06 复测推翻旧结论）：payload 0x00/0x01 全天交替出现，
                // 且每次通知后 1 分钟内设备都能正常应答电量查询，按休眠标离线只会造成在线↔离线闪烁。
                // 语义未明，仅记录；真正的失联由基类连续 3 次查询失败兜底标离线。
                Log.Info($"迈从 收到设备状态通知（payload=0x{push.Payload:X2}，语义未明仅记录）: {Log.Hex(input)}");
                return null;

            case V1PushKind.BatteryInvalid:
                Log.Info($"迈从 忽略异常电量帧（电量={push.Percent}）: {Log.Hex(input)}");
                return null;

            case V1PushKind.Other:
                Log.Info($"迈从 忽略非电量推送帧（类型=0x{push.Type:X2}）: {Log.Hex(input)}");
                return null;
        }

        switch (MchoseV1Codec.ParseBatteryResponse(input, out int pct, out byte flag))
        {
            case V1ResponseKind.Battery:
                Log.Info($"迈从 keyboard-v1 解析: 电量={pct} 状态字节=0x{flag:X2} 原始={Log.Hex(input)}");
                stop = true;
                return (pct, ParseKeyboardV1Charge(flag, pct));

            case V1ResponseKind.InvalidBattery:
                Log.Info($"迈从 keyboard-v1 电量占位/越界: {Log.Hex(input)}");
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
                Log.Info($"迈从 qhw 轮询握手写失败: {dev.Handle.Info.DevicePath}");
                return null;
            }
            dev.HandshakeSent = true;
        }

        var frame = BuildQhwFrame(dev.Handle, CmdGetBattery);
        if (!dev.Handle.WriteOutput(frame, TimeSpan.FromMilliseconds(500)))
        {
            Log.Info($"迈从 qhw 轮询写失败: {dev.Handle.Info.DevicePath}");
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
        Log.Info($"迈从 qhw 轮询无响应: {dev.Handle.Info.DevicePath}");
        return null;
    }
}
