using System.Collections.Concurrent;
using VigilWatt.Core.Ble;
using VigilWatt.Core.Hid;
using VigilWatt.Core.Models;
using VigilWatt.Core.Plugins;
using VigilWatt.Core.Settings;
using VigilWatt.Core.Storage;
using VigilWatt.Core.Utils;

namespace VigilWatt.Core.Devices;

/// <summary>
/// 统一设备管理：合并 BLE + 经典蓝牙 + 2.4G 插件的设备状态，写历史、发变更事件。
/// 插件通过反射自动发现（Core 程序集里所有 IBatteryPlugin 实现类），
/// 接口认领经 DeviceClaimRegistry 防止两个插件抢同一设备。
/// </summary>
public sealed class DeviceManager : IDisposable
{
    private readonly AppSettings _settings;
    private readonly HistoryStore _history;
    private readonly BleBatteryWatcher _ble = new();
    private readonly BluetoothClassicWatcher _classic = new();
    private readonly List<IBatteryPlugin> _plugins;
    private readonly DeviceClaimRegistry _claims = new();
    private readonly ConcurrentDictionary<string, DeviceStatus> _devices = new();
    private readonly ConcurrentDictionary<string, int> _lastNotifiedPercent = new();
    /// <summary>由历史记录补出来的离线占位条目（见 <see cref="RestoreOfflineTombstones"/>），
    /// 需要单独记住：过期剔除时只能删这些，不能误删"在位但读不到"的真实设备。</summary>
    private readonly HashSet<string> _tombstoneIds = new();
    private System.Threading.Timer? _pollTimer;
    private System.Threading.Timer? _sampleTimer;
    private System.Threading.Timer? _discoveryTimer;
    private readonly object _pollSync = new();
    /// <summary>所有 pass（整轮轮询 / 快速发现）串行执行——插件内部的字典不是线程安全的。</summary>
    private readonly SemaphoreSlim _passGate = new(1, 1);
    private Task _currentPoll = Task.CompletedTask;
    private Task _currentDiscovery = Task.CompletedTask;

    /// <summary>单台设备状态有更新（含低电量提醒触发）。</summary>
    public event Action<DeviceStatus>? DeviceUpdated;

    /// <summary>设备列表可能增删（全量刷新 UI 用）。</summary>
    public event Action? DevicesChanged;

    /// <summary>低于阈值触发提醒（App 层弹 Toast/托盘变色）。</summary>
    public event Action<DeviceStatus>? LowBatteryDetected;

    /// <summary>已发现的插件（设置页动态生成开关用）。</summary>
    public IReadOnlyList<IBatteryPlugin> Plugins => _plugins;

    public DeviceManager(AppSettings settings, HistoryStore history)
    {
        _settings = settings;
        _history = history;
        _plugins = DiscoverPlugins();
        _ble.DeviceUpdated += d => Upsert(d);
        _classic.DeviceUpdated += Upsert;
    }

    /// <summary>反射发现 Core 程序集里的全部插件。新增品牌 = 加一个 IBatteryPlugin 实现类文件，无需改这里。</summary>
    private static List<IBatteryPlugin> DiscoverPlugins()
    {
        var list = new List<IBatteryPlugin>();
        foreach (var type in typeof(IBatteryPlugin).Assembly.GetTypes())
        {
            if (type.IsAbstract || !typeof(IBatteryPlugin).IsAssignableFrom(type)) continue;
            var ctor = type.GetConstructor(Type.EmptyTypes);
            if (ctor == null)
            {
                Log.Warn($"插件 {type.Name} 缺少无参构造，已跳过");
                continue;
            }
            try
            {
                list.Add((IBatteryPlugin)ctor.Invoke(null));
            }
            catch (Exception ex)
            {
                Log.Error($"插件 {type.Name} 实例化失败", ex);
            }
        }
        list.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        Log.Info($"插件发现完成: {string.Join(", ", list.Select(p => p.Name))}");
        return list;
    }

    public IReadOnlyCollection<DeviceStatus> Devices => _devices.Values
        .OrderByDescending(d => d.IsOnline).ThenBy(d => d.DisplayName).ToList();

    public DeviceStatus? Find(string id) => _devices.TryGetValue(id, out var d) ? d : null;

    public void Start()
    {
        _ble.Start();
        _classic.Start();
        RestoreOfflineTombstones();
        StartTimers();
        Log.Info("DeviceManager 已启动");
    }

    /// <summary>
    /// 离线占位（墓碑）的保留时长。
    ///
    /// 为什么需要：设备"消失"有两条完全不同的路径——
    /// ① 2.4G 设备拔掉/关机：插件把它标离线，条目留在 <c>_devices</c> 里（字典从不移除）；
    /// ② 蓝牙设备或整个 dongle 不被枚举：它**从来没有进过列表**。
    /// 北通就是 ②：手柄关机后接收器可能连 HID 枚举都退出（实测 19:02 之后扫描里再无 VID_20BC），
    /// 此时没有任何数据源，界面上它等于"从未存在过"——「共 N 台」也就永远等于「N 台在线」，
    /// 这个总数恒等、没有信息量。
    /// 用历史库里最近见过的记录补一个离线条目，既让总数有意义，又不凭空造设备（有真实历史为凭）。
    /// </summary>
    private static readonly TimeSpan TombstoneWindow = TimeSpan.FromHours(24);

    /// <summary>
    /// 把"最近 <see cref="TombstoneWindow"/> 内有历史记录、但当前不在位"的设备补成离线条目。
    /// 只在启动后跑一次：进程中设备一旦被认领就常驻 <c>_devices</c>，不需要反复补。
    /// </summary>
    private void RestoreOfflineTombstones()
    {
        try
        {
            var cutoff = DateTimeOffset.Now - TombstoneWindow;
            int added = 0;
            foreach (var stat in _history.GetDeviceStats())
            {
                if (stat.Last < cutoff) continue;
                if (_devices.ContainsKey(stat.DeviceId)) continue;

                // 用户主动关掉的品牌插件，不要靠历史记录把它"复活"成离线条目——
                // 那会让"关了开关却还显示"的老问题换个形式回来。
                var owner = OwnerOf(stat.DeviceId);
                if (owner.Length > 0 && !IsPluginEnabledByName(owner, defaultOn: true)) continue;

                Upsert(new DeviceStatus
                {
                    Id = stat.DeviceId,
                    Name = stat.Name,
                    Source = SourceFromId(stat.DeviceId),
                    Kind = DeviceKindGuesser.Guess(stat.Name),
                    Protocol = "历史记录",
                    BatteryPercent = null,   // 不拿历史值冒充当前电量
                    Charging = ChargingState.Unknown,
                    LastUpdated = stat.Last,
                    IsOnline = false,
                });
                _tombstoneIds.Add(stat.DeviceId);
                added++;
            }
            if (added > 0)
                Log.Info($"已补 {added} 台离线设备占位（最近 {TombstoneWindow.TotalHours:0} 小时内有历史、当前不在位）");
        }
        catch (Exception ex)
        {
            Log.Error("恢复离线设备占位失败", ex);
        }
    }

    private static DeviceSource SourceFromId(string id) =>
        id.StartsWith("btc:", StringComparison.Ordinal) ? DeviceSource.BluetoothClassic
        : id.StartsWith("ble:", StringComparison.Ordinal) ? DeviceSource.Ble
        : DeviceSource.Hid24G;

    /// <summary>剔除超期的离线占位。真实设备（哪怕离线）不在此列——它们由插件继续维护。</summary>
    private void ExpireTombstones()
    {
        if (_tombstoneIds.Count == 0) return;
        var cutoff = DateTimeOffset.Now - TombstoneWindow;
        var expired = new List<string>();
        foreach (var id in _tombstoneIds)
        {
            if (_devices.TryGetValue(id, out var d) && (d.IsOnline || d.LastUpdated > cutoff)) continue;
            expired.Add(id);
        }
        if (expired.Count == 0) return;

        foreach (var id in expired)
        {
            _devices.TryRemove(id, out _);
            _tombstoneIds.Remove(id);
        }
        Log.Info($"离线占位已过期移除 {expired.Count} 台");
        DevicesChanged?.Invoke();
    }

    private void StartTimers()
    {
        _pollTimer?.Dispose();
        _sampleTimer?.Dispose();
        _discoveryTimer?.Dispose();
        // 首扫尽快跑（探测已是并行/短路，不再需要 2 秒缓冲）
        _pollTimer = new Timer(_ => _ = RequestPollAsync(), null,
            TimeSpan.FromMilliseconds(300), TimeSpan.FromMinutes(1));
        // 快速发现节拍：见 RequestDiscoveryAsync。只服务纯被动插件（雷柏鼠标），
        // 目的 = 鼠标一动就能在十几秒内进列表，而不是苦等下一个 60 秒主轮询。
        _discoveryTimer = new Timer(_ => _ = RequestDiscoveryAsync(), null,
            FastDiscoveryInterval, FastDiscoveryInterval);
        _sampleTimer = new Timer(_ => SafeSample(), null,
            TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(Math.Max(1, _settings.SampleIntervalMinutes)));
    }

    /// <summary>快速发现节拍。15 秒 = 雷柏心跳(3.04s)的约 5 倍，发现时延上界 ~15s。</summary>
    private static readonly TimeSpan FastDiscoveryInterval = TimeSpan.FromSeconds(15);

    /// <summary>设置变化后调用（阈值/采样间隔/插件开关）。</summary>
    public void ApplySettings()
    {
        StartTimers();
        // 立刻跑一轮：否则"关掉插件"要等最多 60 秒的下一个轮询才撤掉设备，
        // 期间列表/托盘仍显示着它，用户会以为开关没生效。
        _ = RequestPollAsync();
    }

    /// <summary>
    /// 手动触发一轮立即轮询（UI 刷新按钮）。返回的 Task 在整轮结束后完成——包括 BLE 的异步补读，
    /// 调用方据此显示"刷新中"状态。
    /// 若已有一轮在跑（例如正好撞上每分钟的自动轮询），直接复用那一轮的 Task 让调用方等待，
    /// 不再像以前那样静默丢弃——否则手动刷新会毫无反应。
    /// </summary>
    public Task RequestPollAsync()
    {
        lock (_pollSync)
        {
            if (!_currentPoll.IsCompleted) return _currentPoll;
            _currentPoll = RunExclusiveAsync(PollOnceAsync);
            return _currentPoll;
        }
    }

    /// <summary>
    /// 快速发现：只让"纯被动零写入"插件（<see cref="IBatteryPlugin.SupportsFastDiscovery"/>）跑一遍认领探测，
    /// **不轮询电量、不碰 BLE / 经典蓝牙、不触发任何写入**。
    ///
    /// 存在的理由：雷柏鼠标休眠时 dongle 全静音，只有用户动它才恢复心跳。60 秒的主轮询
    /// 常常正好落在它睡着的窗口里，探测落空后又要等整整一轮；首扫时更是必然落空
    /// （用户刚启动应用、还没碰鼠标）。15 秒节拍把这个"发现"时延从 ~60s 压到 ~15s。
    ///
    /// 主轮询正在跑时直接返回它——那一轮本来就包含完整探测，不必重复。
    /// </summary>
    public Task RequestDiscoveryAsync()
    {
        lock (_pollSync)
        {
            if (!_currentPoll.IsCompleted) return _currentPoll;
            if (!_currentDiscovery.IsCompleted) return _currentDiscovery;
            _currentDiscovery = RunExclusiveAsync(DiscoverOnlyAsync);
            return _currentDiscovery;
        }
    }

    /// <summary>
    /// 把一段 pass 放到线程池上并**串行**执行（闸门保证同一时刻只有一段 pass 在动插件）。
    /// 用 Task.Run 而不是直接调用：调用方可能是 UI 线程（刷新按钮），HID 探测是阻塞 I/O。
    /// </summary>
    private Task RunExclusiveAsync(Func<Task> body) => Task.Run(async () =>
    {
        try
        {
            await _passGate.WaitAsync().ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            return;   // 正在退出，不再执行
        }

        try
        {
            await body().ConfigureAwait(false);
        }
        finally
        {
            try { _passGate.Release(); } catch (ObjectDisposedException) { /* 退出时竞态，忽略 */ }
        }
    });

    private bool IsPluginEnabled(IBatteryPlugin plugin)
        => IsPluginEnabledByName(plugin.Name, defaultOn: false);

    /// <summary>
    /// 按插件名查开关（大小写不敏感）。
    /// <paramref name="defaultOn"/> 是"没有这个开关时"的兜底：插件必须有显式开关（缺 = 关），
    /// 而蓝牙监视器（Id 前缀 ble: / btc:）根本没有对应插件开关，必须放行，否则墓碑恢复会把蓝牙设备全挡掉。
    /// </summary>
    private bool IsPluginEnabledByName(string pluginName, bool defaultOn)
    {
        foreach (var kv in _settings.PluginEnabled)
            if (string.Equals(kv.Key, pluginName, StringComparison.OrdinalIgnoreCase)) return kv.Value;
        return defaultOn;
    }

    /// <summary>设备 Id 的归属前缀（<c>{插件名小写}:</c>）。用它判断条目属于哪个插件，
    /// 省掉一套额外的归属账本。</summary>
    private static string OwnerOf(string deviceId)
    {
        int i = deviceId.IndexOf(':');
        return i <= 0 ? "" : deviceId[..i];
    }

    /// <summary>
    /// 插件被禁用后，把它此前认领的设备从列表里撤掉。
    ///
    /// 为什么必须显式做：`_devices` 从不移除条目（历史/统计还要用），而 <c>Detach()</c>
    /// 只释放句柄与认领、并不上报状态——于是设备带着"最后一帧的电量"继续显示在设备列表、
    /// 托盘和小组件里，用户关了开关却像没生效（2026-10-06 用户复报）。
    /// 撤掉而不是标离线：用户关的是"不再监听这台设备"，列表里就不该再留着它。
    /// 重新启用后插件会重新认领，设备自然回来。
    /// </summary>
    private void DropDevicesOf(string pluginName)
    {
        var prefix = pluginName.ToLowerInvariant() + ":";
        var dropped = _devices.Keys
            .Where(id => id.StartsWith(prefix, StringComparison.Ordinal))
            .ToList();
        if (dropped.Count == 0) return;

        foreach (var id in dropped)
        {
            _devices.TryRemove(id, out _);
            _tombstoneIds.Remove(id);
        }
        Log.Info($"插件 {pluginName} 已禁用，移除其 {dropped.Count} 台设备");
        DevicesChanged?.Invoke();
    }

    /// <summary>只认领、不读电量（见 RequestDiscoveryAsync）。</summary>
    private Task DiscoverOnlyAsync()
    {
        try
        {
            var hidDevices = HidDeviceScanner.Scan();
            foreach (var plugin in _plugins)
            {
                if (!plugin.SupportsFastDiscovery || !IsPluginEnabled(plugin)) continue;
                try
                {
                    foreach (var d in plugin.Attach(hidDevices, _claims))
                        Upsert(d);
                }
                catch (Exception ex)
                {
                    Log.Error($"插件 {plugin.Name} 快速发现失败", ex);
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error("快速发现周期异常", ex);
        }
        return Task.CompletedTask;
    }

    private async Task PollOnceAsync()
    {
        try
        {
            var hidDevices = HidDeviceScanner.Scan();

            foreach (var plugin in _plugins)
            {
                bool enabled = IsPluginEnabled(plugin);
                try
                {
                    if (!enabled)
                    {
                        plugin.Detach();
                        _claims.ReleaseAll(plugin.Name);
                        DropDevicesOf(plugin.Name);
                        continue;
                    }
                    // Attach 的返回值 = 本轮认领的设备（含探测时已读到的电量）。
                    // 必须立即 Upsert：新设备即时进 UI（不再等整轮扫描结束），也避免紧接着的 Poll 白重读一遍
                    // （基类已把本轮新认领的设备标记 FreshlyClaimed、Poll 会跳过）。
                    foreach (var d in plugin.Attach(hidDevices, _claims))
                        Upsert(d);
                    foreach (var d in plugin.Poll())
                        Upsert(d);
                }
                catch (Exception ex)
                {
                    Log.Error($"插件 {plugin.Name} 轮询失败", ex);
                }
            }

            // 必须 await：以前 fire-and-forget 会让"刷新"在 BLE 侧还没跑完就返回，用户看到的列表纹丝不动
            await _ble.PollConnectedAsync();
            _classic.RefreshBattery();
            DevicesChanged?.Invoke();
        }
        catch (Exception ex)
        {
            Log.Error("轮询周期异常", ex);
        }
    }

    /// <summary>立即按保留天数清理历史（设置保存后调用）。</summary>
    public void CleanupHistoryNow()
    {
        try
        {
            var removed = _history.CleanupOlderThan(DateTimeOffset.Now.AddDays(-_settings.HistoryRetentionDays));
            Log.Info($"历史清理完成，删除 {removed} 条");
        }
        catch (Exception ex)
        {
            Log.Error("历史清理失败", ex);
        }
    }

    private void SafeSample()
    {
        try
        {
            foreach (var d in _devices.Values)
            {
                if (d.IsOnline && d.BatteryPercent.HasValue)
                    _history.Insert(d);
            }
            ExpireTombstones();
        }
        catch (Exception ex)
        {
            Log.Error("历史采样失败", ex);
        }
    }

    private void Upsert(DeviceStatus incoming)
    {
        // 新设备立即通知 UI（watcher/插件首轮上报不等下一分钟轮询）
        bool isNew = !_devices.ContainsKey(incoming.Id);
        var status = _devices.GetOrAdd(incoming.Id, _ => incoming);
        if (isNew)
        {
            DevicesChanged?.Invoke();
            DeviceUpdated?.Invoke(status);
        }

        // 覆盖前记下上一次已知电量，供低电量提醒做骤降保护（同一对象的情况也成立）
        int? previousPercent = isNew ? null : status.BatteryPercent;

        // 充电标志必须参与变化判定：设备改口 充电中↔放电 时电量往往不变（如拔线瞬间停在 99%），
        // 漏判会让 UI/托盘/小组件一直显示"充电中"，直到下一次电量跳动才自愈（2026-10-06 用户复报）。
        bool changed = status.BatteryPercent != incoming.BatteryPercent
                    || status.Charging != incoming.Charging
                    || status.IsOnline != incoming.IsOnline;
        // 原地同步（同一对象的情况也统一字段）；Kind/Protocol 的"声明覆盖猜测"规则见 DeviceStatusMerger
        DeviceStatusMerger.Apply(status, incoming);

        // 占位条目一旦被真实来源接管（重新上线），就不再按占位规则过期剔除
        if (status.IsOnline) _tombstoneIds.Remove(status.Id);

        if (changed)
        {
            // 电量变化立即写一条历史（含从离线恢复）
            if (status.BatteryPercent.HasValue)
                _history.Insert(status);
            DeviceUpdated?.Invoke(status);
        }

        CheckLowBattery(status, previousPercent);
    }

    /// <summary>低电量提醒的骤降保护阈值（百分点）：降幅超过它先暂缓提醒，等下次读数确认。</summary>
    private const int ImplausibleDropPoints = 20;

    private void CheckLowBattery(DeviceStatus status, int? previousPercent)
    {
        int threshold = _settings.LowBatteryThreshold;
        if (threshold <= 0 || !status.IsOnline) return;
        if (status.BatteryPercent is not { } percent || percent > threshold) return;
        if (status.Charging == ChargingState.Charging || status.Charging == ChargingState.Full) return;

        // 骤降保护：键盘休眠/关机瞬间的伪读数（1%/0%）会让电量瞬间"掉"到阈值以下。
        // 真掉电不可能一次掉 20 个点，所以这种降幅先不提醒，等下次读数确认。
        if (previousPercent is { } prev && prev - percent > ImplausibleDropPoints)
        {
            Log.Warn($"低电量提醒暂缓: {status.DisplayName} {prev}%→{percent}% 降幅异常，等下次读数确认");
            return;
        }

        // 每跨过阈值只提醒一次：记录上次提醒时的档位
        int slot = percent; // 精确到个位百分比
        int lastSlot = _lastNotifiedPercent.GetValueOrDefault(status.Id, int.MaxValue);
        if (slot < lastSlot)
        {
            _lastNotifiedPercent[status.Id] = slot;
            LowBatteryDetected?.Invoke(status);
            Log.Info($"低电量提醒: {status.DisplayName} {percent}%");
        }
    }

    public void Dispose()
    {
        _pollTimer?.Dispose();
        _sampleTimer?.Dispose();
        _discoveryTimer?.Dispose();
        foreach (var p in _plugins) p.Detach();
        _ble.Dispose();
        _classic.Dispose();
        _passGate.Dispose();
    }
}
