using System.Collections.Concurrent;
using VigilWatt.Core.Hid;
using VigilWatt.Core.Models;
using VigilWatt.Core.Utils;

namespace VigilWatt.Core.Plugins.Logitech;

/// <summary>
/// 罗技 2.4G 插件：支持 Unifying / Bolt / Lightspeed 接收器、USB 直连设备（HID++ 协议）。
/// 电量读取级联：Unified Battery 0x1004 → Battery Status 0x1000 → HID++ 1.0 寄存器 0x07/0x0D。
/// 雷柏（VID 0x24AE）不是 HID++ 协议，已移交给 RapooPlugin（纯被动 status-report7），勿在此认领。
/// </summary>
public sealed class LogitechPlugin : IBatteryPlugin
{
    public const ushort LogitechVid = 0x046D;
    public const ushort HidppUsagePage = 0xFF43;

    /// <summary>HID 规范的厂商自定义页（罗技 HID++ 集合实际多用这个页）。</summary>
    public const ushort VendorUsagePage = 0xFF00;

    public string Name => "Logitech";
    public string DisplayName => "罗技";

    /// <summary>罗技接收器同时管鼠标和键盘，按型号分不出来 → Unknown，UI 退回按名字猜。</summary>
    public DeviceKind Kind => DeviceKind.Unknown;

    /// <summary>判断接口是否为 HID++ 兼容 vendor 通道。
    /// 注意：HIDP_CAPS 早前字段序写反过（已修），且各家把 vendor 页放在 usage_page 还是 usage 上不一，
    /// 所以这里对 0xFF00 / 0xFF43 两个候选值在**两个字段里都查**，避免因字段序差异漏掉设备。</summary>
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
        public bool UnifiedTriedFunc0, UnifiedTriedFunc1;
        public int? RegisterBattery;        // 1.0: 0x07 或 0x0D
        public int ConsecutiveFailures;
    }

    private sealed class ReceiverSession
    {
        public required HidDeviceInfo Info;
        public HidppIO? Io;
        public bool OpenFailed;
        public bool DirectDevice;                       // true = 设备本体直连，false = 接收器
        public readonly Dictionary<byte, DevEntry> Devices = new();
    }

    public IReadOnlyList<DeviceStatus> Attach(IReadOnlyList<HidDeviceInfo> availableDevices, DeviceClaimRegistry claims)
    {
        var claimed = new List<DeviceStatus>();

        foreach (var info in availableDevices)
        {
            if (!IsHidppInterface(info))
                continue;
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
                    Log.Info($"罗技 HID++ 接口打开: {info}");

                    DiscoverDevices(session);
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

            if (session.Io != null)
                claimed.AddRange(session.Devices.Values.Select(d => d.Status));
        }

        // 掉线的接口：关闭句柄，设备标离线，释放认领
        foreach (var path in _sessions.Keys)
        {
            if (availableDevices.Any(d => d.DevicePath == path)) continue;
            if (_sessions.TryRemove(path, out var gone))
            {
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
        if (!session.DirectDevice && session.Devices.Count == 0)
        {
            // 接收器模式：ping 设备索引 1..6（Unifying 最多 6 台）
            for (byte idx = 1; idx <= 6; idx++)
            {
                if (Ping(io, idx, out var major))
                    AddDevice(session, idx, major);
            }
        }

        if (session.Devices.Count == 0)
        {
            // 直连设备模式：广播索引 0xFF
            if (Ping(io, 0xFF, out var major))
            {
                session.DirectDevice = true;
                AddDevice(session, 0xFF, major);
            }
        }
    }

    private void AddDevice(ReceiverSession session, byte idx, byte protocolMajor)
    {
        var io = session.Io!;
        string name = ReadDeviceName(io, idx) ?? $"罗技设备 {idx}";
        var status = new DeviceStatus
        {
            Id = $"logitech:{StableHash(session.Info.DevicePath)}:{idx}",
            Name = name,
            Source = DeviceSource.Hid24G,
            Protocol = protocolMajor >= 2 ? "Logitech HID++ 2.0" : "Logitech HID++ 1.0",
        };
        session.Devices[idx] = new DevEntry { Status = status, ProtocolMajor = protocolMajor };
        Log.Info($"发现罗技设备 devIdx={idx} proto={protocolMajor} name={name}");
    }

    public IReadOnlyList<DeviceStatus> Poll()
    {
        var updated = new List<DeviceStatus>();
        foreach (var session in _sessions.Values)
        {
            if (session.Io == null) continue;
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
            // 1) Unified Battery 0x1004（官方 func 0，个别固件 func 1，都试一遍）
            var unifiedIdx = dev.UnifiedBatteryIdx ??= ResolveFeature(io, idx, 0x1004);
            if (unifiedIdx != 0)
            {
                foreach (byte func in new byte[] { 0, 1 })
                {
                    if ((func == 0 && dev.UnifiedTriedFunc0) || (func == 1 && dev.UnifiedTriedFunc1)) continue;
                    if (func == 0) dev.UnifiedTriedFunc0 = true; else dev.UnifiedTriedFunc1 = true;
                    var resp = io.Request(idx, (ushort)((unifiedIdx << 8) | func), ReadOnlySpan<byte>.Empty);
                    if (resp != null && resp.Length >= 3 && resp[0] <= 100)
                    {
                        ApplyBattery(status, resp[0], MapUnifiedCharging(resp[2]));
                        return true;
                    }
                }
            }

            // 2) Battery Status 0x1000
            var statusIdx = dev.BatteryStatusIdx ??= ResolveFeature(io, idx, 0x1000);
            if (statusIdx != 0)
            {
                var resp = io.Request(idx, (ushort)((statusIdx << 8) | 0), ReadOnlySpan<byte>.Empty);
                if (resp != null && resp.Length >= 3 && resp[0] > 0)
                {
                    ApplyBattery(status, resp[0], MapStatusCharging(resp[2]));
                    return true;
                }
            }
        }

        // 3) HID++ 1.0 寄存器
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
                        var state = (resp[4] & 0xF0) switch
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
                    var (percent, state) = MapTierBattery(resp[2], resp[3]);
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
        status.BatteryPercent = percent;
        status.Charging = charging;
        status.LastUpdated = DateTimeOffset.Now;
        if (!status.IsOnline)
        {
            status.IsOnline = true;
            Log.Info($"罗技设备重新上线: {status.DisplayName}");
        }
    }

    /// <summary>Root.GetFeature：查询特性 ID 对应的 feature index，0 = 不支持。</summary>
    private static byte ResolveFeature(HidppIO io, byte devIdx, ushort featureId)
    {
        Span<byte> args = stackalloc byte[2] { (byte)(featureId >> 8), (byte)featureId };
        var resp = io.Request(devIdx, 0x0000, args);
        return resp != null && resp.Length >= 1 ? resp[0] : (byte)0;
    }

    /// <summary>Root.Ping（func 1，带随机标记）。major 取响应 data[2]。</summary>
    private static bool Ping(HidppIO io, byte devIdx, out byte protocolMajor)
    {
        protocolMajor = 0;
        byte mark = (byte)(Random.Shared.Next(1, 255));
        Span<byte> args = stackalloc byte[3] { 0, 0, mark };
        var resp = io.Request(devIdx, 0x0010, args); // 0x0010|sw → feature 0x00, func 1
        if (resp != null && resp.Length >= 5 && resp[4] == mark)
        {
            protocolMajor = resp[2];
            return true;
        }
        return false;
    }

    private static string? ReadDeviceName(HidppIO io, byte devIdx)
    {
        var resp = io.Request(devIdx, 0x0500, ReadOnlySpan<byte>.Empty); // Device Name func 0: GetCount
        if (resp == null || resp.Length < 1 || resp[0] is 0 or > 32) return null;
        int len = resp[0];
        var chars = new char[len];
        for (byte i = 0; i < len; i++)
        {
            var r = io.Request(devIdx, 0x0501, new byte[] { i }); // func 1: GetChar(index)
            if (r == null || r.Length < 1) return null;
            chars[i] = (char)r[0];
        }
        var name = new string(chars).Trim();
        return name.Length == 0 ? null : name;
    }

    private static ChargingState MapUnifiedCharging(byte s) => s switch
    {
        1 => ChargingState.Charging,
        2 => ChargingState.Full,
        _ => ChargingState.Discharging,
    };

    private static ChargingState MapStatusCharging(byte s) => s switch
    {
        1 => ChargingState.Charging,
        2 => ChargingState.Full,
        4 or 5 => ChargingState.Charging,
        _ => ChargingState.Discharging,
    };

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
        foreach (var session in _sessions.Values) session.Io?.Dispose();
        _sessions.Clear();
        Log.Info("罗技插件已卸载");
    }

    public static string StableHash(string s) => PluginUtils.StableHash(s);
}
