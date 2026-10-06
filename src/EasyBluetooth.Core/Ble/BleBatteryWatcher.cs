using System.Collections.Concurrent;
using EasyBluetooth.Core.Models;
using EasyBluetooth.Core.Utils;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Devices.Enumeration;
using Windows.Storage.Streams;

namespace EasyBluetooth.Core.Ble;

/// <summary>
/// BLE 电量监视器，两条数据通道：
/// 1. DeviceWatcher 监听 System.Devices.AepBattery_Level（系统缓存的 GATT 电量，连接时自动更新）
/// 2. 对已连接设备周期性主动走 GATT 读 0x180F/0x2A19（兜底系统属性不更新的情况）
/// </summary>
public sealed class BleBatteryWatcher : IDisposable
{
    private DeviceWatcher? _watcher;
    private bool _started;
    private readonly ConcurrentDictionary<string, DeviceStatus> _devices = new();
    private readonly ConcurrentDictionary<string, DateTimeOffset> _lastGattAttempt = new();
    private readonly SemaphoreSlim _gattGate = new(1, 1);

    public event Action<DeviceStatus>? DeviceUpdated;

    private const string PropName = "System.ItemNameDisplay";
    private const string PropConnected = "System.Devices.Aep.IsConnected";

    /// <summary>不同 Windows 版本的电量属性名不同，逐个尝试，成功的组合记下来。</summary>
    private static readonly string[][] BatteryPropCandidates =
    {
        new[] { PropName, "System.Devices.AepBattery_Level", PropConnected },
        new[] { PropName, "System.Devices.Aep.Battery_Level", PropConnected },
        new[] { PropName, PropConnected },
    };

    private string? _batteryProp;

    public void Start()
    {
        if (_started) return;
        _started = true;

        var selector = BluetoothLEDevice.GetDeviceSelector();
        DeviceWatcher? watcher = null;
        foreach (var props in BatteryPropCandidates)
        {
            try
            {
                watcher = DeviceInformation.CreateWatcher(selector, props, DeviceInformationKind.AssociationEndpoint);
                if (props.Length == 3) _batteryProp = props[1];
                Log.Info($"BLE watcher 属性集可用: {string.Join(", ", props)}");
                break;
            }
            catch (Exception ex)
            {
                Log.Warn($"BLE watcher 属性集不可用 [{string.Join(", ", props)}]: {ex.Message}");
                watcher = null;
            }
        }
        if (watcher == null)
        {
            Log.Error("BLE DeviceWatcher 无法创建，BLE 监控不可用");
            return;
        }

        watcher.Added += (w, info) => HandleDevice(info.Id, info.Properties, info.Name);
        watcher.Updated += (w, update) => HandleDevice(update.Id, update.Properties, null);
        watcher.Removed += (w, update) => _devices.TryRemove(update.Id, out _);
        watcher.Start();
        _watcher = watcher;
        Log.Info("BLE DeviceWatcher 已启动");
    }

    private void HandleDevice(string id, IReadOnlyDictionary<string, object> props, string? name)
    {
        int? percent = null;
        bool connected = false;
        if (_batteryProp != null && props.TryGetValue(_batteryProp, out var batteryObj))
        {
            try
            {
                int v = Convert.ToInt32(batteryObj);
                if (v is > 0 and <= 100) percent = v;
            }
            catch { /* 类型不对就忽略 */ }
        }
        if (props.TryGetValue(PropConnected, out var connObj))
        {
            try { connected = Convert.ToBoolean(connObj); } catch { }
        }

        if (percent == null && !connected)
        {
            // 已跟踪的设备断开了：标离线；没跟踪过（邻居设备）直接忽略
            if (_devices.TryGetValue(id, out var tracked) && tracked.IsOnline)
            {
                tracked.IsOnline = false;
                tracked.LastUpdated = DateTimeOffset.Now;
                DeviceUpdated?.Invoke(tracked);
            }
            return;
        }

        var status = _devices.GetOrAdd(id, _ => new DeviceStatus
        {
            Id = $"ble:{id}",
            Name = name ?? "",
            Source = DeviceSource.Ble,
            Protocol = "BLE GATT",
        });
        if (!string.IsNullOrEmpty(name)) status.Name = name!;
        else if (props.TryGetValue(PropName, out var nameObj))
        {
            var n = nameObj?.ToString();
            if (!string.IsNullOrEmpty(n)) status.Name = n!;
        }

        status.IsOnline = true;
        status.LastUpdated = DateTimeOffset.Now;

        if (percent.HasValue)
        {
            status.BatteryPercent = percent;
            DeviceUpdated?.Invoke(status);
            return;
        }

        // 系统没给电量且设备在线：主动 GATT 读一次（限流 2 分钟）
        if (connected &&
            (!_lastGattAttempt.TryGetValue(id, out var last) ||
             DateTimeOffset.Now - last > TimeSpan.FromMinutes(2)))
        {
            _lastGattAttempt[id] = DateTimeOffset.Now;
            _ = ReadBatteryViaGattAsync(id, status);
        }
    }

    private async Task ReadBatteryViaGattAsync(string aepId, DeviceStatus status)
    {
        await _gattGate.WaitAsync();
        try
        {
            var ble = await OpenDeviceAsync(aepId);
            if (ble == null) return;
            using (ble)
            {
                var svcResult = await ble
                    .GetGattServicesForUuidAsync(GattServiceUuids.Battery, BluetoothCacheMode.Uncached)
                    .AsTask().WaitAsync(TimeSpan.FromSeconds(8));
                if (svcResult.Status != GattCommunicationStatus.Success || svcResult.Services.Count == 0) return;

                var chrResult = await svcResult.Services[0]
                    .GetCharacteristicsForUuidAsync(GattCharacteristicUuids.BatteryLevel, BluetoothCacheMode.Uncached)
                    .AsTask().WaitAsync(TimeSpan.FromSeconds(8));
                if (chrResult.Status != GattCommunicationStatus.Success || chrResult.Characteristics.Count == 0) return;

                var read = await chrResult.Characteristics[0]
                    .ReadValueAsync(BluetoothCacheMode.Uncached)
                    .AsTask().WaitAsync(TimeSpan.FromSeconds(8));
                if (read.Status != GattCommunicationStatus.Success) return;

                var reader = DataReader.FromBuffer(read.Value);
                int percent = reader.ReadByte();
                if (percent is > 0 and <= 100)
                {
                    status.BatteryPercent = percent;
                    status.LastUpdated = DateTimeOffset.Now;
                    DeviceUpdated?.Invoke(status);
                    Log.Info($"BLE GATT 直读: {status.DisplayName} {percent}%");
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"BLE GATT 读取失败 {aepId}: {ex.Message}");
        }
        finally
        {
            _gattGate.Release();
        }
    }

    /// <summary>周期轮询所有已连接设备（兜底通道）。</summary>
    public async Task PollConnectedAsync()
    {
        foreach (var (id, status) in _devices.ToArray())
        {
            if (!status.IsOnline) continue;
            if (!_lastGattAttempt.TryGetValue(id, out var last) ||
                DateTimeOffset.Now - last > TimeSpan.FromMinutes(2))
            {
                _lastGattAttempt[id] = DateTimeOffset.Now;
                await ReadBatteryViaGattAsync(id, status);
            }
        }
    }

    private static async Task<BluetoothLEDevice?> OpenDeviceAsync(string aepId)
    {
        try
        {
            var task = BluetoothLEDevice.FromIdAsync(aepId).AsTask();
            if (await Task.WhenAny(task, Task.Delay(8000)) != task) return null;
            return task.Result;
        }
        catch
        {
            return null;
        }
    }

    public IReadOnlyList<DeviceStatus> Snapshot() => _devices.Values.ToList();

    public void Dispose()
    {
        _watcher?.Stop();
        _gattGate.Dispose();
    }
}
