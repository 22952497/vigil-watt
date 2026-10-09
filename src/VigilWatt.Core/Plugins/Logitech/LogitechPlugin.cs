using System.Collections.Concurrent;
using VigilWatt.Core.Hid;
using VigilWatt.Core.Models;
using VigilWatt.Core.Utils;

namespace VigilWatt.Core.Plugins.Logitech;

/// <summary>
/// 罗技 2.4G 插件：支持 Unifying / Bolt / Lightspeed / Nano 接收器、USB 直连设备（HID++ 协议）。
/// 电量读取级联：Unified Battery 0x1004 → Battery Status 0x1000 → Battery Voltage 0x1001 → ADC Measurement 0x1F20 → HID++ 1.0 寄存器 0x07/0x0D（协议细节见 doc/hardware/protocols.md §3）。
/// 接收器槽位与报文偏好见嵌入的 logitech-hidpp.json（目录是补充，不是白名单）。雷柏（VID 0x24AE）不是 HID++ 协议，已移交 RapooPlugin（纯被动 status-report7），勿在此认领。
/// </summary>
public sealed class LogitechPlugin : IBatteryPlugin
{
    public const ushort LogitechVid = 0x046D;
    public const ushort HidppUsagePage = 0xFF43;

    /// <summary>HID 规范的厂商自定义页（罗技 HID++ 集合实际多用这个页）。</summary>
    public const ushort VendorUsagePage = 0xFF00;

    /// <summary>发现阶段 ping 超时。本机 Bolt 实测醒着约 1.0s 才回（400ms 会假阴性）；睡着时 7 槽 × 1.5s ≈ 10s（旧版短接口 3s×7 ≈ 21s），可接受。</summary>
    private static readonly TimeSpan DiscoverTimeout = TimeSpan.FromMilliseconds(1500);

    /// <summary>已发现至少一台后，空槽快速扫过的超时。</summary>
    private static readonly TimeSpan FastSlotTimeout = TimeSpan.FromMilliseconds(400);

    public string Name => "Logitech";
    public string DisplayName => "罗技";

    /// <summary>罗技接收器同时管鼠标和键盘，按型号分不出来 → Unknown，UI 退回按名字猜。</summary>
    public DeviceKind Kind => DeviceKind.Unknown;

    /// <summary>HID++ 探测要写帧（ping / GetFeature），不参与 15 秒快速发现节拍，避免写入频率上升。</summary>
    public bool SupportsFastDiscovery => false;

    /// <summary>同理不参与：HID++ 的发现要 ping 7 个槽位（睡着时约 10 秒），每 15 秒跑一遍代价太高；
    /// 罗技靠自发的 HID++ 通知做实时更新，不需要加快发现节拍。</summary>
    public bool SupportsDiscoveryProbe => false;

    /// <summary>HID++ 自发电量/充电通知 → DeviceManager 即时 Upsert（对照浏览器扩展的实时更新）。</summary>
    public event Action<DeviceStatus>? StatusPushed;

    /// <summary>判断接口是否为 HID++ 兼容 vendor 通道。HIDP_CAPS 字段序早前写反过（已修），且各家把 vendor 页放在 usage_page 还是 usage 上不一，故对 0xFF00 / 0xFF43 两个候选值在**两个字段里都查**，避免因字段序差异漏掉设备。</summary>
    public static bool IsHidppInterface(HidDeviceInfo info) =>
        info.VendorId == LogitechVid &&
        (info.UsagePage == HidppUsagePage || info.Usage == HidppUsagePage ||
         info.UsagePage == VendorUsagePage || info.Usage == VendorUsagePage);

    private readonly ConcurrentDictionary<string, ReceiverSession> _sessions = new();

    private sealed class DevEntry
    {
        public required DeviceStatus Status;
        public byte ProtocolMajor = 2;
        public byte? UnifiedBatteryIdx;     // 0x1004 的 feature index
        public byte? BatteryStatusIdx;      // 0x1000 的 feature index
        public byte? BatteryVoltageIdx;     // 0x1001
        public byte? AdcMeasurementIdx;     // 0x1F20
        public byte? UnifiedWorkingFunc;    // 已验证可用的 Unified Battery func（0 或 1）
        public int? RegisterBattery;        // 1.0: 0x07 或 0x0D
        public ushort? WirelessPid;         // 配对寄存器 WPID，供电压曲线覆盖
        public int ConsecutiveFailures;
    }

    private sealed class ReceiverSession
    {
        public required HidDeviceInfo Info;
        public HidppIO? Io;
        public bool OpenFailed;
        public bool DirectDevice;                       // true = 设备本体直连，false = 接收器
        public readonly Dictionary<byte, DevEntry> Devices = new();
        public Action<byte, byte[]>? NotifyHandler;
        public CancellationTokenSource? ListenCts;
        public Task? ListenTask;
    }

    public IReadOnlyList<DeviceStatus> Attach(IReadOnlyList<HidDeviceInfo> availableDevices, DeviceClaimRegistry claims)
    {
        var claimed = new List<DeviceStatus>();

        // 同 VID/PID 上按目录报文偏好跳过冗余接口（Bolt 跳短；Unifying 跳长）
        var hidppPeers = availableDevices
            .Where(IsHidppInterface)
            .Select(d => (d.VendorId, d.ProductId, d.OutputReportLength))
            .ToList();

        foreach (var info in availableDevices)
        {
            if (!IsHidppInterface(info))
                continue;
            var pref = HidppCatalog.ReportPreference(info.ProductId);
            if (HidppCodec.ShouldSkipInterface(
                    info.VendorId, info.ProductId, info.OutputReportLength, hidppPeers, pref))
            {
                if (_sessions.TryRemove(info.DevicePath, out var skipped))
                {
                    StopListening(skipped);
                    skipped.Io?.Dispose();
                    claims.Release(info.DevicePath, Name);
                }
                Log.Info($"罗技跳过冗余接口（偏好={pref}）: {info}");
                continue;
            }
            if (!claims.TryClaim(info.DevicePath, Name))
                continue;   // 已被其他插件认领（注册表兜底，IsHidppInterface 本身也按 VID 过滤）

            var session = _sessions.GetOrAdd(info.DevicePath,
                p => new ReceiverSession { Info = info });

            // 重新扫描后元数据可能更新
            session.Info = info;

            if (session.Io == null && !session.OpenFailed)
            {
                try
                {
                    session.Io = new HidppIO(info.Open());
                    Log.Info($"罗技 HID++ 接口打开: {info} report=0x{session.Io.ReportId:X2}");
                }
                catch (UnauthorizedAccessException ex)
                {
                    session.OpenFailed = true;
                    Log.Warn($"罗技接口受保护无法访问: {info.DevicePath} ({ex.Message})");
                }
                catch (Exception ex)
                {
                    session.OpenFailed = true;
                    Log.Error("罗技接口打开失败", ex);
                }
            }

            // 每次 Attach 按目录槽位数补扫尚未发现的索引（第二台不必等重启）
            if (session.Io != null)
                DiscoverDevices(session);

            if (session.Io != null && session.Devices.Count > 0)
            {
                EnsureListening(session);
                claimed.AddRange(session.Devices.Values.Select(d => d.Status));
            }
        }

        // 掉线的接口：关闭句柄，设备标离线，释放认领
        foreach (var path in _sessions.Keys)
        {
            if (availableDevices.Any(d => d.DevicePath == path)) continue;
            if (_sessions.TryRemove(path, out var gone))
            {
                StopListening(gone);
                gone.Io?.Dispose();
                foreach (var d in gone.Devices.Values) d.Status.IsOnline = false;
                claims.Release(path, Name);
                Log.Info($"罗技接口已移除: {path}");
            }
        }

        return claimed;
    }

    private void DiscoverDevices(ReceiverSession session)
    {
        var io = session.Io!;
        if (!session.DirectDevice)
        {
            int maxSlots = HidppCatalog.MaxSlots(session.Info.ProductId);
            for (byte idx = 1; idx <= maxSlots; idx++)
            {
                if (session.Devices.ContainsKey(idx)) continue;
                var t = session.Devices.Count > 0 ? FastSlotTimeout : DiscoverTimeout;
                if (Ping(io, idx, out var major, t))
                    AddDevice(session, idx, major);
            }
        }

        if (session.Devices.Count == 0)
        {
            // 直连设备模式：广播索引 0xFF
            if (Ping(io, 0xFF, out var major, DiscoverTimeout))
            {
                session.DirectDevice = true;
                AddDevice(session, 0xFF, major);
            }
        }
    }

    /// <summary>通知里出现未知槽位时补发现（设备刚上线）。</summary>
    private void TryDiscoverSlot(ReceiverSession session, byte idx)
    {
        if (session.Io == null || session.DirectDevice) return;
        if (idx is 0 or 0xFF) return;
        if (session.Devices.ContainsKey(idx)) return;
        int maxSlots = HidppCatalog.MaxSlots(session.Info.ProductId);
        if (idx > maxSlots) return;
        if (Ping(session.Io, idx, out var major, FastSlotTimeout))
            AddDevice(session, idx, major);
    }

    private void AddDevice(ReceiverSession session, byte idx, byte protocolMajor)
    {
        var io = session.Io!;
        string name = ReadDeviceName(io, idx) ?? $"罗技设备 {idx}";
        var kind = DeviceKind.Unknown;
        ushort? wpid = null;
        if (!session.DirectDevice &&
            TryReadPairingInfo(io, session.Info.ProductId, idx, out wpid, out var pairedKind))
        {
            kind = pairedKind;
        }
        if (kind == DeviceKind.Unknown)
            kind = DeviceKindGuesser.Guess(name);

        var status = new DeviceStatus
        {
            Id = $"logitech:{StableHash(session.Info.DevicePath)}:{idx}",
            Name = name,
            Source = DeviceSource.Hid24G,
            Kind = kind,
            Protocol = protocolMajor >= 2 ? "Logitech HID++ 2.0" : "Logitech HID++ 1.0",
        };
        var entry = new DevEntry
        {
            Status = status,
            ProtocolMajor = protocolMajor,
            WirelessPid = wpid,
        };
        // 预解析电量 feature 下标，后台通知才能按 feature 匹配（不必等下一轮 Poll）
        if (protocolMajor >= 2)
        {
            entry.UnifiedBatteryIdx = ResolveFeature(io, idx, 0x1004);
            if (entry.UnifiedBatteryIdx == 0)
            {
                entry.BatteryStatusIdx = ResolveFeature(io, idx, 0x1000);
                if (entry.BatteryStatusIdx == 0)
                {
                    entry.BatteryVoltageIdx = ResolveFeature(io, idx, HidppCodec.FeatureBatteryVoltage);
                    if (entry.BatteryVoltageIdx == 0)
                        entry.AdcMeasurementIdx = ResolveFeature(io, idx, HidppCodec.FeatureAdcMeasurement);
                }
            }
        }
        session.Devices[idx] = entry;
        Log.Info($"发现罗技设备 devIdx={idx} proto={protocolMajor} kind={kind} wpid={(wpid is { } w ? w.ToString("X4") : "-")} name={name}");
    }

    /// <summary>后台只读泵：零写入，收 Unified Battery 插拔电自发通知（与 Logi Battery Dashboard 同源）。</summary>
    private void EnsureListening(ReceiverSession session)
    {
        if (session.Io == null || session.ListenTask != null) return;

        var io = session.Io;
        session.NotifyHandler = (devIdx, data) => OnHidppNotification(session, devIdx, data);
        io.NotificationReceived += session.NotifyHandler;
        session.ListenCts = new CancellationTokenSource();
        var cts = session.ListenCts;
        session.ListenTask = Task.Run(() =>
        {
            Log.Info("罗技 HID++ 通知监听已启动（被动，零写入）");
            while (!cts.IsCancellationRequested)
            {
                try
                {
                    io.PumpNotifications(TimeSpan.FromMilliseconds(500));
                }
                catch (Exception ex)
                {
                    if (!cts.IsCancellationRequested)
                        Log.Warn($"罗技通知监听异常: {ex.Message}");
                }
            }
        }, cts.Token);
    }

    private void StopListening(ReceiverSession session)
    {
        try { session.ListenCts?.Cancel(); } catch { /* ignore */ }
        if (session.Io != null && session.NotifyHandler != null)
            session.Io.NotificationReceived -= session.NotifyHandler;
        session.NotifyHandler = null;
        try { session.ListenTask?.Wait(TimeSpan.FromSeconds(1)); } catch { /* ignore */ }
        session.ListenCts?.Dispose();
        session.ListenCts = null;
        session.ListenTask = null;
    }

    private void OnHidppNotification(ReceiverSession session, byte devIdx, byte[] data)
    {
        if (data.Length < 5) return;

        if (!session.Devices.TryGetValue(devIdx, out var dev))
        {
            TryDiscoverSlot(session, devIdx);
            if (!session.Devices.TryGetValue(devIdx, out dev)) return;
        }

        byte feature = data[0];
        bool batteryFeature =
            (dev.UnifiedBatteryIdx is { } u && feature == u) ||
            (dev.BatteryStatusIdx is { } b && feature == b) ||
            (dev.BatteryVoltageIdx is { } v && feature == v) ||
            (dev.AdcMeasurementIdx is { } a && feature == a);
        if (!batteryFeature) return;

        if (dev.BatteryVoltageIdx is { } voltIdx && feature == voltIdx)
        {
            var curve = HidppCatalog.CurveFor(dev.WirelessPid, adcFeature: false);
            if (!HidppCodec.TryParseBatteryVoltage(data, curve, out var pctV, out var chgV, out _))
                return;
            PushIfChanged(dev, pctV, chgV);
            return;
        }

        if (dev.AdcMeasurementIdx is { } adcIdx && feature == adcIdx)
        {
            var curve = HidppCatalog.CurveFor(dev.WirelessPid, adcFeature: true);
            if (!HidppCodec.TryParseAdcMeasurement(data, curve, out var pctA, out var chgA, out _))
                return;
            PushIfChanged(dev, pctA, chgA);
            return;
        }

        if (!HidppCodec.TryParseBattery20(data, out var pct, out var chg)) return;
        PushIfChanged(dev, pct, chg);
    }

    private void PushIfChanged(DevEntry dev, int pct, ChargingState chg)
    {
        bool changed = dev.Status.BatteryPercent != pct
                    || dev.Status.Charging != chg
                    || !dev.Status.IsOnline;
        ApplyBattery(dev.Status, pct, chg);
        if (changed)
            StatusPushed?.Invoke(dev.Status);
    }

    public IReadOnlyList<DeviceStatus> Poll()
    {
        var updated = new List<DeviceStatus>();
        foreach (var session in _sessions.Values)
        {
            if (session.Io == null) continue;

            // 发现只在 Attach / 通知补槽里做；此处再扫会把空接收器的超时翻倍
            foreach (var (idx, dev) in session.Devices)
            {
                try
                {
                    if (RefreshBattery(session, idx, dev))
                    {
                        updated.Add(dev.Status);
                        dev.ConsecutiveFailures = 0;
                    }
                    else
                    {
                        dev.ConsecutiveFailures++;
                        if (dev.ConsecutiveFailures >= 3 && dev.Status.IsOnline)
                        {
                            dev.Status.IsOnline = false;
                            updated.Add(dev.Status);
                        }
                    }
                }
                catch (Exception ex)
                {
                    Log.Error($"罗技电量轮询异常 devIdx={idx}", ex);
                }
            }
        }
        return updated;
    }

    private bool RefreshBattery(ReceiverSession session, byte idx, DevEntry dev)
    {
        var io = session.Io!;
        var status = dev.Status;

        if (dev.ProtocolMajor >= 2)
        {
            // 1) Unified Battery 0x1004
            //    func 1 = GetStatus（电量% + 充电态）—— MX Master 3S 真机：90% / charging=1
            //    func 0 = GetCapabilities（档位能力），**不能当电量**（真机会被误读成 15%）
            //    优先 func 1；仅当 1 无响应时才回退 func 0（极旧固件）
            var unifiedIdx = dev.UnifiedBatteryIdx ??= ResolveFeature(io, idx, 0x1004);
            if (unifiedIdx != 0)
            {
                byte[] funcs = dev.UnifiedWorkingFunc.HasValue
                    ? new byte[] { dev.UnifiedWorkingFunc.Value }
                    : new byte[] { 1, 0 };
                int? fallbackPct = null;
                ChargingState fallbackChg = ChargingState.Unknown;
                byte? fallbackFunc = null;
                foreach (byte func in funcs)
                {
                    var resp = io.Request(idx, HidppCodec.FeatureFunc(unifiedIdx, func), ReadOnlySpan<byte>.Empty);
                    if (resp == null || !HidppCodec.TryParseBattery20(resp, out var pct, out var chg))
                        continue;

                    // 已锁定工作 func：直接用
                    if (dev.UnifiedWorkingFunc.HasValue)
                    {
                        ApplyBattery(status, pct, chg);
                        return true;
                    }

                    // 探测期：func 1 立即采信；func 0 只作回退候选
                    if (func == 1)
                    {
                        dev.UnifiedWorkingFunc = 1;
                        ApplyBattery(status, pct, chg);
                        return true;
                    }

                    fallbackPct = pct;
                    fallbackChg = chg;
                    fallbackFunc = 0;
                }

                if (fallbackPct.HasValue)
                {
                    dev.UnifiedWorkingFunc = fallbackFunc;
                    ApplyBattery(status, fallbackPct.Value, fallbackChg);
                    return true;
                }
            }

            // 2) Battery Status 0x1000
            var statusIdx = dev.BatteryStatusIdx ??= ResolveFeature(io, idx, 0x1000);
            if (statusIdx != 0)
            {
                var resp = io.Request(idx, HidppCodec.FeatureFunc(statusIdx, 0), ReadOnlySpan<byte>.Empty);
                if (resp != null && HidppCodec.TryParseBattery20(resp, out var pct, out var chg))
                {
                    ApplyBattery(status, pct, chg);
                    return true;
                }
            }

            // 3) Battery Voltage 0x1001（G 系等只有毫伏、没有百分比特征）
            var voltIdx = dev.BatteryVoltageIdx ??= ResolveFeature(io, idx, HidppCodec.FeatureBatteryVoltage);
            if (voltIdx != 0)
            {
                var resp = io.Request(idx, HidppCodec.FeatureFunc(voltIdx, 0), ReadOnlySpan<byte>.Empty);
                var curve = HidppCatalog.CurveFor(dev.WirelessPid, adcFeature: false);
                if (resp != null &&
                    HidppCodec.TryParseBatteryVoltage(resp, curve, out var pct, out var chg, out _))
                {
                    ApplyBattery(status, pct, chg);
                    return true;
                }
            }

            // 4) ADC Measurement 0x1F20
            var adcIdx = dev.AdcMeasurementIdx ??= ResolveFeature(io, idx, HidppCodec.FeatureAdcMeasurement);
            if (adcIdx != 0)
            {
                var resp = io.Request(idx, HidppCodec.FeatureFunc(adcIdx, 0), ReadOnlySpan<byte>.Empty);
                var curve = HidppCatalog.CurveFor(dev.WirelessPid, adcFeature: true);
                if (resp != null &&
                    HidppCodec.TryParseAdcMeasurement(resp, curve, out var pct, out var chg, out _))
                {
                    ApplyBattery(status, pct, chg);
                    return true;
                }
            }
        }

        // 5) HID++ 1.0 寄存器（偏移已按参数区 resp[2..] 对齐，保持不动）
        int? register = dev.RegisterBattery;
        foreach (int reg in new[] { 0x07, 0x0D })
        {
            if (register.HasValue && register.Value != reg) continue;
            var resp = io.Request(idx, (ushort)(0x8100 | reg), ReadOnlySpan<byte>.Empty,
                patchSw: false);
            if (resp != null && resp.Length >= 3)
            {
                dev.RegisterBattery = reg;
                if (reg == 0x07)
                {
                    int percent = resp[2];
                    if (percent is > 0 and <= 100)
                    {
                        var state = (resp.Length >= 5 ? resp[4] : (byte)0) switch
                        {
                            0x30 => ChargingState.Discharging,
                            0x50 => ChargingState.Charging,
                            0x90 => ChargingState.Full,
                            _ => ChargingState.Unknown,
                        };
                        ApplyBattery(status, percent, state);
                        return true;
                    }
                }
                else // 0x0D：只有档位
                {
                    var (percent, state) = MapTierBattery(resp[2], resp.Length >= 4 ? resp[3] : (byte)0);
                    ApplyBattery(status, percent, state);
                    return true;
                }
            }
            if (!register.HasValue) break; // 第一次只试 0x07
        }

        return false;
    }

    private static void ApplyBattery(DeviceStatus status, int percent, ChargingState charging)
    {
        bool pctChanged = status.BatteryPercent != percent;
        bool chgChanged = status.Charging != charging;
        status.BatteryPercent = percent;
        status.Charging = charging;
        status.LastUpdated = DateTimeOffset.Now;
        if (!status.IsOnline)
        {
            status.IsOnline = true;
            Log.Info($"罗技设备重新上线: {status.DisplayName} {percent}% {charging}");
        }
        else if (pctChanged || chgChanged)
        {
            Log.Info($"罗技电量更新: {status.DisplayName} {percent}% {charging}");
        }
    }

    /// <summary>读接收器配对信息（HID++ 1.0 长寄存器 0xB5）：设备类型 + wireless PID。Unifying 子寄存器 0x20+(n-1)；Bolt 子寄存器 0x50+n。</summary>
    private static bool TryReadPairingInfo(
        HidppIO io, ushort receiverPid, byte deviceIndex,
        out ushort? wirelessPid, out DeviceKind kind)
    {
        wirelessPid = null;
        kind = DeviceKind.Unknown;
        var family = HidppCatalog.Family(receiverPid);
        byte subReg = family == ReceiverFamily.Bolt
            ? (byte)(0x50 + deviceIndex)
            : (byte)(0x20 + deviceIndex - 1);

        Span<byte> args = stackalloc byte[1] { subReg };
        // 0x83B5 = 长寄存器读 RECEIVER_INFO（0x2B5）
        var resp = io.Request(0xFF, 0x83B5, args, patchSw: false,
            timeout: TimeSpan.FromMilliseconds(800));
        if (resp == null) return false;

        if (family == ReceiverFamily.Bolt)
        {
            if (!HidppCodec.TryParseBoltPairing(resp, out var wpid, out kind))
                return false;
            wirelessPid = wpid == 0 ? null : wpid;
            return true;
        }

        if (HidppCodec.TryParseUnifyingPairing(resp, out var uWpid, out kind))
        {
            wirelessPid = uWpid == 0 ? null : uWpid;
            return true;
        }

        // 未知族：先试 Unifying 布局，再试 Bolt
        if (HidppCodec.TryParseBoltPairing(resp, out var bWpid, out kind))
        {
            wirelessPid = bWpid == 0 ? null : bWpid;
            return true;
        }
        return false;
    }

    /// <summary>Root.GetFeature：查询特性 ID 对应的 feature index，0 = 不支持。</summary>
    private static byte ResolveFeature(HidppIO io, byte devIdx, ushort featureId)
    {
        Span<byte> args = stackalloc byte[2] { (byte)(featureId >> 8), (byte)featureId };
        var resp = io.Request(devIdx, 0x0000, args);
        return resp != null ? HidppCodec.ParseFeatureIndex(resp) : (byte)0;
    }

    /// <summary>Root.Ping（func 1，带随机标记）。major 取响应 data[2]。</summary>
    private static bool Ping(HidppIO io, byte devIdx, out byte protocolMajor, TimeSpan? timeout = null)
    {
        protocolMajor = 0;
        byte mark = (byte)(Random.Shared.Next(1, 255));
        Span<byte> args = stackalloc byte[3] { 0, 0, mark };
        var resp = io.Request(devIdx, 0x0010, args, timeout: timeout); // 0x0010|sw → feature 0x00, func 1
        if (resp != null && resp.Length >= 5 && resp[4] == mark)
        {
            protocolMajor = resp[2];
            return true;
        }
        return false;
    }

    private static string? ReadDeviceName(HidppIO io, byte devIdx)
    {
        // Feature ID 0x0005 = DEVICE NAME；下标因固件而异，必须先 GetFeature，不能写死 0x05
        byte nameIdx = ResolveFeature(io, devIdx, 0x0005);
        if (nameIdx == 0) return null;

        var resp = io.Request(devIdx, HidppCodec.FeatureFunc(nameIdx, 0), ReadOnlySpan<byte>.Empty); // GetCount
        if (resp == null || resp.Length < 3 || resp[2] is 0 or > 32) return null;
        int len = resp[2];
        var chars = new char[len];
        for (byte i = 0; i < len; i++)
        {
            var r = io.Request(devIdx, HidppCodec.FeatureFunc(nameIdx, 1), new byte[] { i }); // GetChar
            if (r == null || r.Length < 3) return null;
            chars[i] = (char)r[2];
        }
        var name = new string(chars).Trim();
        return name.Length == 0 ? null : name;
    }

    private static (int Percent, ChargingState State) MapTierBattery(byte tier, byte chargeStatus)
    {
        var state = (chargeStatus & 0x21) == 0x21 ? ChargingState.Charging
            : (chargeStatus & 0x22) == 0x22 ? ChargingState.Full
            : ChargingState.Discharging;
        return tier switch
        {
            7 => (100, state),
            5 => (60, state),
            3 => (20, state),
            1 => (10, state),
            _ => (0, state),
        };
    }

    public void Detach()
    {
        foreach (var session in _sessions.Values)
        {
            StopListening(session);
            session.Io?.Dispose();
        }
        _sessions.Clear();
        Log.Info("罗技插件已卸载");
    }

    public static string StableHash(string s) => PluginUtils.StableHash(s);
}
