using System.Collections.Concurrent;
using VigilWatt.Core.Models;
using VigilWatt.Core.Utils;
using Windows.Devices.Bluetooth;
using Windows.Devices.Enumeration;

namespace VigilWatt.Core.Ble;

/// <summary>
/// 经典蓝牙（BR/EDR）监视器：监听已配对、已连接的 A2DP/HFP 设备（蓝牙耳机等）。
/// BleBatteryWatcher 的 selector 只覆盖 BLE 端点，经典蓝牙音频设备不会进那个 watcher，
/// 所以这里用 BluetoothDevice 的 selector 单独开一路 DeviceWatcher。
///
/// 电量两条通道，都只读系统缓存、不打开设备、不建立连接、零写入：
/// 1. AEP 属性 System.Devices.AepBattery_Level（本机无效，仅作兼容保留）
/// 2. <see cref="ClassicAudioBattery"/>：读 HFP 免手操作网关设备节点的
///    DEVPKEY_Device_BatteryLevel（本机实测可用，与 Windows 设置页显示一致）
/// </summary>
public sealed class BluetoothClassicWatcher : IDisposable
{
    private DeviceWatcher? _watcher;
    private bool _started;
    private readonly ConcurrentDictionary<string, DeviceStatus> _devices = new();

    public event Action<DeviceStatus>? DeviceUpdated;

    private const string PropName = "System.ItemNameDisplay";
    private const string PropConnected = "System.Devices.Aep.IsConnected";

    /// <summary>电量属性候选：AepBattery_Level 在本机 BLE selector 下不可用（runtime-pitfalls §3），
    /// 经典蓝牙 selector 下值得实测一次；抛异常就自动回退到只显示连接状态，无副作用。</summary>
    private static readonly string[][] BatteryPropCandidates =
    {
        new[] { PropName, "System.Devices.AepBattery_Level", PropConnected },
        new[] { PropName, PropConnected },
    };

    private string? _batteryProp;

    public void Start()
    {
        if (_started) return;
        _started = true;

        var selector = BluetoothDevice.GetDeviceSelector();
        DeviceWatcher? watcher = null;
        foreach (var props in BatteryPropCandidates)
        {
            try
            {
                watcher = DeviceInformation.CreateWatcher(selector, props, DeviceInformationKind.AssociationEndpoint);
                if (props.Length == 3) _batteryProp = props[1];
                Log.Info($"经典蓝牙 watcher 属性集可用: {string.Join(", ", props)}");
                break;
            }
            catch (Exception ex)
            {
                Log.Warn($"经典蓝牙 watcher 属性集不可用 [{string.Join(", ", props)}]: {ex.Message}");
                watcher = null;
            }
        }
        if (watcher == null)
        {
            Log.Error("经典蓝牙 DeviceWatcher 无法创建，经典蓝牙监控不可用");
            return;
        }

        watcher.Added += (w, info) => HandleDevice(info.Id, info.Properties, info.Name);
        watcher.Updated += (w, update) => HandleDevice(update.Id, update.Properties, null);
        watcher.Removed += (w, update) => HandleRemoved(update.Id);
        watcher.Start();
        _watcher = watcher;
        Log.Info("经典蓝牙 DeviceWatcher 已启动");
    }

    private void HandleDevice(
        string id, IReadOnlyDictionary<string, object> props, string? name, bool connectionVerified = false)
    {
        // Updated 事件只带变化的属性：IsConnected 缺失时沿用现状，不能当成断开
        bool? connected = ReadConnected(props);

        int? percent = null;
        if (_batteryProp != null && props.TryGetValue(_batteryProp, out var batteryObj))
        {
            try
            {
                int v = Convert.ToInt32(batteryObj);
                if (v is > 0 and <= 100) percent = v;
            }
            catch { /* 类型不对就忽略 */ }
        }

        // AEP 属性拿不到时，走 HFP 网关设备节点的 DEVPKEY_Device_BatteryLevel
        if (!percent.HasValue && TryGetDevNodeBattery(id, out var devNodePercent))
        {
            percent = devNodePercent;
        }

        if (connected == false)
        {
            if (_devices.TryGetValue(id, out var tracked) && tracked.IsOnline)
            {
                tracked.IsOnline = false;
                tracked.LastUpdated = DateTimeOffset.Now;
                Log.Info($"经典蓝牙设备离线: {tracked.DisplayName}");
                DeviceUpdated?.Invoke(tracked);
            }
            return;
        }

        // 新增设备必须**确认真的连着**才进列表。
        // 旧代码拿"读到了电量"当在线依据——那是错的：配对但已断开的耳机，它的 HFP 网关
        // 设备节点上仍留着上次的电量（真机 2026-10-06：W820NB 明明没连，却以 50% 在线出现在列表里）。
        bool isNew = !_devices.TryGetValue(id, out var status);
        if (isNew && !connectionVerified)
        {
            _ = VerifyConnectionAndAddAsync(id, props, name);
            return;
        }

        if (isNew)
        {
            status = new DeviceStatus
            {
                Id = $"btc:{id}",
                Name = "",
                Source = DeviceSource.BluetoothClassic,
                Kind = DeviceKind.Headset,   // A2DP/HFP 音频设备，基本都是耳机/音箱
                Protocol = "经典蓝牙 A2DP/HFP",
            };
            _devices[id] = status;
        }

        ApplyName(status, id, name, props);

        // 只在有意义的变化时通知：新上线 / 从离线恢复 / 电量数值变化
        bool wasOffline = !status.IsOnline;
        bool batteryChanged = percent.HasValue && status.BatteryPercent != percent;
        bool shouldNotify = isNew || wasOffline || batteryChanged;

        status.IsOnline = true;
        status.LastUpdated = DateTimeOffset.Now;
        if (percent.HasValue) status.BatteryPercent = percent;

        if (shouldNotify)
        {
            if (isNew) Log.Info($"经典蓝牙设备发现: {status.DisplayName} 电量={(percent.HasValue ? $"{percent}%" : "--")}");
            else if (wasOffline) Log.Info($"经典蓝牙设备上线: {status.DisplayName}");
            DeviceUpdated?.Invoke(status);
        }
    }

    /// <summary>
    /// 新增设备前的连接确认。
    ///
    /// 为什么不能只看 AEP 的 <c>IsConnected</c>：<c>Added</c> 首帧里这个属性**可能还没填好**
    /// （真机 2026-10-06 实测），而旧代码此时拿"读到了电量"当在线依据 —— 那是错的，
    /// 配对但已断开的耳机，它的 HFP 网关设备节点上仍留着上次的电量（W820NB 没连却以 50% 进列表）。
    /// 所以直接问系统要"当前已连接"的集合（只读枚举，不建立任何连接）。
    /// </summary>
    private async Task VerifyConnectionAndAddAsync(
        string id, IReadOnlyDictionary<string, object> props, string? name)
    {
        bool? aep = ReadConnected(props);
        if (aep == false)   // AEP 明说断开，不用再问
        {
            Log.Info($"经典蓝牙设备未连接，不进列表: {id}");
            return;
        }

        bool connected;
        try
        {
            var list = await DeviceInformation.FindAllAsync(
                BluetoothDevice.GetDeviceSelectorFromConnectionStatus(BluetoothConnectionStatus.Connected),
                new[] { PropConnected }, DeviceInformationKind.AssociationEndpoint);
            connected = list.Any(d => string.Equals(d.Id, id, StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception ex)
        {
            // 枚举失败时退回 AEP 属性；两者都说不出"在线"就不进列表
            Log.Warn($"经典蓝牙连接状态查询失败，退回 AEP 属性: {id} {ex.Message}");
            if (aep != true) return;
            connected = true;
        }

        if (!connected)
        {
            Log.Info($"经典蓝牙设备未连接，不进列表: {id}（AEP IsConnected={aep?.ToString() ?? "缺"}）");
            return;
        }

        HandleDevice(id, props, name, connectionVerified: true);
    }

    private static bool? ReadConnected(IReadOnlyDictionary<string, object> props)
    {
        if (!props.TryGetValue(PropConnected, out var obj)) return null;
        try { return Convert.ToBoolean(obj); } catch { return null; }
    }

    /// <summary>
    /// 取设备显示名。来源按可靠性排序：WinRT 传来的名字 → <c>System.ItemNameDisplay</c> →
    /// 注册表配对记录 → 格式化 MAC。每个候选都要过 <see cref="ClassicDeviceName.IsPlaceholder"/>，
    /// 因为 WinRT 在友好名还没解析出来时会把 **AEP 设备 ID 当名字**返回（真机实测过）。
    /// </summary>
    private static void ApplyName(
        DeviceStatus status, string aepId, string? infoName, IReadOnlyDictionary<string, object> props)
    {
        if (!ClassicDeviceName.IsPlaceholder(infoName))
        {
            status.Name = infoName!.Trim();
            return;
        }

        if (props.TryGetValue(PropName, out var nameObj) &&
            !ClassicDeviceName.IsPlaceholder(nameObj?.ToString()))
        {
            status.Name = nameObj!.ToString()!.Trim();
            return;
        }

        // 已经解析出真名了就别覆盖（Updated 事件会反复进来）
        if (!ClassicDeviceName.IsPlaceholder(status.Name)) return;

        string? mac = ClassicAudioBattery.MacFromAepId(aepId);
        var paired = ClassicPairedNames.TryGet(mac);
        status.Name = string.IsNullOrEmpty(paired) ? ClassicDeviceName.FallbackName(mac) : paired!;
    }

    private void HandleRemoved(string id)
    {
        // 设备不再匹配 selector（断开/取消配对）：发离线而不是静默删除，保持 DeviceManager 里有据可查
        if (_devices.TryRemove(id, out var tracked) && tracked.IsOnline)
        {
            tracked.IsOnline = false;
            tracked.LastUpdated = DateTimeOffset.Now;
            Log.Info($"经典蓝牙设备移除: {tracked.DisplayName}");
            DeviceUpdated?.Invoke(tracked);
        }
    }

    /// <summary>按 AEP ID 里的设备 MAC 去 HFP 网关节点上取电量。</summary>
    private static bool TryGetDevNodeBattery(string aepId, out int percent)
    {
        percent = 0;
        var mac = ClassicAudioBattery.MacFromAepId(aepId);
        if (mac == null) return false;
        if (!ClassicAudioBattery.ReadBatteryByMac().TryGetValue(mac, out var p)) return false;
        if (p is <= 0 or > 100) return false;
        percent = p;
        return true;
    }

    /// <summary>
    /// 周期刷新（DeviceManager 轮询时调用）。watcher 只在设备属性变化时推事件，
    /// 电量变了不一定触发 Updated，所以这里主动重读一次，有变化才通知。
    /// </summary>
    public void RefreshBattery()
    {
        if (!_started) return;
        foreach (var (id, status) in _devices.ToArray())
        {
            if (!status.IsOnline) continue;

            // 名字可能是后来才解析出来的（WinRT 首次 Added 常常只给设备 ID），每轮补一次
            if (ClassicDeviceName.IsPlaceholder(status.Name))
            {
                var mac = ClassicAudioBattery.MacFromAepId(id);
                var paired = ClassicPairedNames.TryGet(mac);
                if (!string.IsNullOrEmpty(paired))
                {
                    status.Name = paired!;
                    status.LastUpdated = DateTimeOffset.Now;
                    Log.Info($"经典蓝牙设备名补全: {status.DisplayName}");
                    DeviceUpdated?.Invoke(status);
                }
            }

            if (!TryGetDevNodeBattery(id, out var percent)) continue;
            if (status.BatteryPercent == percent) continue;

            status.BatteryPercent = percent;
            status.LastUpdated = DateTimeOffset.Now;
            Log.Info($"经典蓝牙电量(HFP 节点): {status.DisplayName} {percent}%");
            DeviceUpdated?.Invoke(status);
        }
    }

    public IReadOnlyList<DeviceStatus> Snapshot() => _devices.Values.ToList();

    public void Dispose()
    {
        _watcher?.Stop();
    }
}
