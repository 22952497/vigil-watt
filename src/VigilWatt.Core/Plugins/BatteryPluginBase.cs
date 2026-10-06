using VigilWatt.Core.Hid;
using VigilWatt.Core.Models;
using VigilWatt.Core.Utils;

namespace VigilWatt.Core.Plugins;

/// <summary>
/// "接口认领"型插件的基类：一台设备 = 一个长持有的 HID 接口句柄。
/// 沉淀通用机制（新插件只实现协议本身，安全机制由基类统一保证）：
/// - 探测失败冷却：失败接口 ProbeCooldown 内不再打扰；
/// - 写入熔断：连续 MaxConsecutiveFailures 次查询失败自动 WritesDisabled（PollDevice 内写帧前应检查 CanWrite）；
/// - 中央认领：认领前经 DeviceClaimRegistry.TryClaim，防止与其他插件抢同一接口；
/// - 设备生命周期：消失设备自动关句柄、标离线、释放认领；
/// - 读数异常过滤：短时间涨跌超过 MaxPlausibleStepPercent 的读数判异常、本轮不采用，
///   需持续 AnomalyPersistWindow 才采信（挡休眠/关机瞬间的 0/1 伪读数，防误报低电量），见 BatteryReadingFilter；
/// - 稳定 Id：{name小写}:{路径 FNV-1a 哈希}；
/// - 首扫提速：新认领设备本轮标记 FreshlyClaimed（探测值即最新读数，Poll 本轮跳过不重读）；
/// - 快速发现：纯被动零写入插件可把 SupportsFastDiscovery 置 true，参与 15 秒发现节拍（见该类属性）。
/// 协议差异由子类实现 IsCandidate / Probe / PollDevice。
/// 会话型插件（如罗技经接收器枚举多设备）可不继承本类，直接实现 IBatteryPlugin 并使用 PluginUtils/DeviceClaimRegistry。
/// </summary>
public abstract class BatteryPluginBase : IBatteryPlugin
{
    public abstract string Name { get; }
    public abstract string DisplayName { get; }

    /// <summary>本插件处理的设备类型（UI 选图标用）。</summary>
    public abstract DeviceKind Kind { get; }

    /// <summary>
    /// 见 <see cref="IBatteryPlugin.SupportsFastDiscovery"/>：是否参与 DeviceManager 的 15 秒快速发现节拍。
    /// 默认 false（会写帧的插件保持默认值）；**只有纯被动零写入插件**才该覆盖成 true。
    /// </summary>
    public virtual bool SupportsFastDiscovery => false;

    /// <summary>
    /// 探测失败后的冷却时间（期间不再探测该接口）。
    ///
    /// 取值必须**明显小于轮询周期（60 秒）**：失败发生在轮询开始后几秒处，若冷却等于一整轮，
    /// 下一拍只剩 5x 秒会被判"冷却未过"而白白跳过，实际重试间隔翻倍（实测雷柏/北通都踩过这个相位坑）。
    /// 所以默认 30 秒 = "每一轮都允许重探"，它挡的是同一轮内的重复探测，不是整轮跳过。
    /// 只有代价真的高、且不指望短期恢复的插件才该调大（如 Elan 的 feature 全扫 = 5 分钟）。
    /// </summary>
    protected virtual TimeSpan ProbeCooldown => TimeSpan.FromSeconds(30);

    /// <summary>是否允许向设备写探测/查询帧。有干扰风险的插件覆盖为用户开关（如迈从的 MchoseProbingEnabled）。</summary>
    protected virtual bool WritesEnabled => true;

    /// <summary>连续失败熔断阈值。</summary>
    protected const int MaxConsecutiveFailures = 3;

    protected sealed class TrackedDevice
    {
        public required HidDeviceInfo Info;
        public required HidDeviceHandle Handle;
        public required DeviceStatus Status;
        public required string Protocol;
        public int ConsecutiveFailures;
        public bool WritesDisabled;
        public DateTimeOffset WritesDisabledAt;
        /// <summary>每句柄一次性的动作标记（会话握手等），子类自行使用。</summary>
        public bool HandshakeSent;
        /// <summary>本设备的读数异常过滤器（基准 + 异常持续计时都封在里面）。</summary>
        public required BatteryReadingFilter Filter;
        /// <summary>子类在 PollDevice 里判定"设备已主动告知休眠/关机"时置位，基类随即标离线。</summary>
        public bool OfflineRequested;
        /// <summary>本轮刚由探测建立（探测返回值就是最新读数）：本轮 Poll 跳过、下一轮起正常轮询。</summary>
        public bool FreshlyClaimed;
    }

    /// <summary>单个候选接口的探测产物（探测阶段只做 I/O，不碰非线程安全的字典）。</summary>
    private readonly record struct ProbeOutcome(
        HidDeviceInfo Info,
        HidDeviceHandle? Handle,
        (string Protocol, int? Percent, ChargingState Charging)? Result);

    /// <summary>熔断退避期：设备经常休眠（无线键盘/鼠标离开即睡），永久停写会让休眠唤醒后永远读不到，
    /// 所以停写 10 分钟后允许重试一轮；仍失败则再次熔断。最坏写入频率 = 1 帧/10 分钟，安全框架内。</summary>
    protected static readonly TimeSpan WriteRetryBackoff = TimeSpan.FromMinutes(10);

    /// <summary>
    /// 单次读数的最大合理变化幅度（百分点）：涨跌超过它就先判异常、本轮不采用。
    /// 起因：键盘休眠/关机瞬间 dongle 会推 0/1 之类的伪读数，直接采信会误报低电量。
    /// 真掉电不会一分钟掉 20 个点，所以这个阈值只挡异常、不挡正常放电。
    /// 判定逻辑见 <see cref="BatteryReadingFilter"/>。
    /// </summary>
    protected virtual int MaxPlausibleStepPercent => 20;

    /// <summary>
    /// 异常读数需持续这么久才最终采信（期间读数一直偏离才算数）。
    /// 作用：既挡住休眠瞬间的瞬时伪读数，又不会把真实的深度掉电永久锁死。
    /// </summary>
    protected virtual TimeSpan AnomalyPersistWindow => TimeSpan.FromMinutes(5);

    private readonly Dictionary<string, TrackedDevice> _devices = new();   // key = DevicePath
    private readonly Dictionary<string, DateTimeOffset> _probeFailedAt = new();
    private DeviceClaimRegistry _claims = null!;

    public IReadOnlyList<DeviceStatus> Attach(IReadOnlyList<HidDeviceInfo> availableDevices, DeviceClaimRegistry claims)
    {
        _claims = claims;
        var claimed = new List<DeviceStatus>();

        // 第一遍（快）：已有设备直接返回；筛出本轮需要新探测的接口（冷却中/被他人认领的直接跳过）
        var toProbe = new List<HidDeviceInfo>();
        foreach (var info in availableDevices)
        {
            if (!IsCandidate(info)) continue;

            if (_devices.TryGetValue(info.DevicePath, out var known))
            {
                known.Info = info;   // 重扫后元数据可能更新
                claimed.Add(known.Status);
                continue;
            }

            if (_probeFailedAt.TryGetValue(info.DevicePath, out var failedAt) &&
                DateTimeOffset.Now - failedAt < ProbeCooldown)
                continue;

            if (!claims.TryClaim(info.DevicePath, Name)) continue;   // 已被其他插件认领

            toProbe.Add(info);
        }

        // 第二遍（慢）：逐个探测。探测期间只碰局部变量，字典改动统一放到后面（便于将来并行化）。
        if (toProbe.Count > 0)
        {
            var outcomes = new ProbeOutcome[toProbe.Count];
            for (int i = 0; i < toProbe.Count; i++) outcomes[i] = RunProbe(toProbe[i]);
            CommitProbes(outcomes, claimed);
        }

        // 已拔出的设备
        foreach (var path in _devices.Keys.ToList())
        {
            if (availableDevices.Any(d => d.DevicePath == path)) continue;
            if (_devices.Remove(path, out var gone))
            {
                gone.Handle.Dispose();
                gone.Status.IsOnline = false;
                _claims.Release(path, Name);
                Log.Info($"{DisplayName} 设备已移除: {path}");
            }
        }

        return claimed;
    }

    /// <summary>打开句柄并跑协议探测。成功返回持有句柄 + 探测值；失败/异常返回空句柄并已释放。</summary>
    private ProbeOutcome RunProbe(HidDeviceInfo info)
    {
        HidDeviceHandle? handle = null;
        try
        {
            handle = info.Open();
            var probe = Probe(handle);
            if (probe == null)
            {
                handle.Dispose();
                return new ProbeOutcome(info, null, null);
            }
            return new ProbeOutcome(info, handle, probe);
        }
        catch (UnauthorizedAccessException)
        {
            handle?.Dispose();
            return new ProbeOutcome(info, null, null);
        }
        catch (Exception ex)
        {
            Log.Error($"{DisplayName} 探测异常: {info.DevicePath}", ex);
            handle?.Dispose();
            return new ProbeOutcome(info, null, null);
        }
    }

    /// <summary>落地探测结果（写字典 + 发认领）。</summary>
    private void CommitProbes(ProbeOutcome[] outcomes, List<DeviceStatus> claimed)
    {
        foreach (var o in outcomes)
        {
            if (o.Result is not { } probe)
            {
                _claims.Release(o.Info.DevicePath, Name);
                _probeFailedAt[o.Info.DevicePath] = DateTimeOffset.Now;
                continue;
            }

            var (protocol, percent, charging) = probe;
            var status = new DeviceStatus
            {
                Id = $"{Name.ToLowerInvariant()}:{PluginUtils.StableHash(o.Info.DevicePath)}",
                Name = BuildDisplayName(o.Info),
                Source = DeviceSource.Hid24G,
                Kind = Kind,
                Protocol = protocol,
                BatteryPercent = percent,
                Charging = charging,
                // 认领了却拿不到读数 = 设备在位但不可读（如北通手柄已关机、只剩 dongle 插着），
                // 这时**必须标离线**：假装在线会让设备列表说谎（2026-10-06 用户复报的 bug）。
                // 现有插件探测成功时都带读数，所以这条规则对它们是无操作。
                IsOnline = percent.HasValue,
            };
            _devices[o.Info.DevicePath] = new TrackedDevice
            {
                Info = o.Info, Handle = o.Handle!, Status = status, Protocol = protocol,
                Filter = CreateFilter(percent), FreshlyClaimed = true,
            };
            Log.Info($"认领{DisplayName}设备: {o.Info} proto={protocol} 电量={percent} 充电={charging}");
            claimed.Add(status);
        }
    }

    public IReadOnlyList<DeviceStatus> Poll()
    {
        var updated = new List<DeviceStatus>();
        foreach (var dev in _devices.Values)
        {
            try
            {
                // 本轮刚由探测建立：探测返回值就是最新读数，不再立刻重读一遍（首扫提速）。
                // 只跳过一轮，下一轮起正常轮询。
                if (dev.FreshlyClaimed)
                {
                    dev.FreshlyClaimed = false;
                    continue;
                }

                var read = PollDevice(dev);

                // 设备主动告知休眠/关机（协议层明确帧，如迈从 13 0A … 02）：立即标离线，
                // 不必等 3 次查询失败（那要 3 分钟）。这是设备告知，不计入通信失败。
                if (dev.OfflineRequested)
                {
                    dev.OfflineRequested = false;
                    dev.ConsecutiveFailures = 0;
                    if (dev.Status.IsOnline)
                    {
                        dev.Status.IsOnline = false;
                        dev.Status.LastUpdated = DateTimeOffset.Now;
                        Log.Info($"{DisplayName} {dev.Status.DisplayName} 收到设备休眠/关机通知，立即标记离线");
                        updated.Add(dev.Status);
                    }
                    continue;
                }

                if (read.HasValue)
                {
                    var (percent, charging) = read.Value;

                    // 读数异常过滤：短时间涨跌幅度过大（休眠/关机伪读数、dongle 抽风）先过滤掉，
                    // 本轮不更新 status（UI 与低电量判定都看不到这个值）。
                    if (percent.HasValue)
                    {
                        int baseline = dev.Filter.LastAcceptedPercent;
                        var verdict = dev.Filter.Evaluate(percent.Value, DateTimeOffset.Now);
                        if (verdict is ReadingVerdict.RejectAnomaly or ReadingVerdict.InvalidValue)
                        {
                            Log.Warn($"{DisplayName} {dev.Status.DisplayName} 读数异常已过滤：" +
                                     $"{baseline}%→{percent}%，本轮不采用" +
                                     (verdict == ReadingVerdict.InvalidValue
                                         ? $"（数值无效，低于 {dev.Filter.MinValidPercent}%）"
                                         : $"（变化超过 {dev.Filter.MaxStepPercent} 点，需持续 " +
                                           $"{dev.Filter.PersistWindow.TotalMinutes:0} 分钟才采信）"));
                            continue;
                        }
                        if (verdict == ReadingVerdict.AcceptConfirmed)
                        {
                            Log.Warn($"{DisplayName} {dev.Status.DisplayName} 读数 {baseline}%→{percent}% " +
                                     $"已持续 {dev.Filter.PersistWindow.TotalMinutes:0} 分钟，采信为真实变化");
                        }
                    }

                    dev.Status.BatteryPercent = percent;
                    dev.Status.Charging = charging;
                    dev.Status.LastUpdated = DateTimeOffset.Now;
                    if (!dev.Status.IsOnline)
                    {
                        dev.Status.IsOnline = true;
                        Log.Info($"{DisplayName} 设备重新上线: {dev.Status.DisplayName}");
                    }
                    dev.ConsecutiveFailures = 0;
                    updated.Add(dev.Status);
                }
                else
                {
                    dev.ConsecutiveFailures++;
                    if (dev.ConsecutiveFailures >= MaxConsecutiveFailures && !dev.WritesDisabled)
                    {
                        dev.WritesDisabled = true;
                        dev.WritesDisabledAt = DateTimeOffset.Now;
                        Log.Warn($"{DisplayName} {dev.Status.DisplayName} 连续 {MaxConsecutiveFailures} 次查询失败，熔断停写（{WriteRetryBackoff.TotalMinutes:0} 分钟退避后重试一轮）");
                    }
                    if (dev.ConsecutiveFailures >= MaxConsecutiveFailures && dev.Status.IsOnline)
                    {
                        dev.Status.IsOnline = false;
                        updated.Add(dev.Status);
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Error($"{DisplayName} 轮询异常: {dev.Status.DisplayName}", ex);
            }
        }
        return updated;
    }

    public virtual void Detach()
    {
        foreach (var dev in _devices.Values) dev.Handle.Dispose();
        _devices.Clear();
        _claims?.ReleaseAll(Name);
        Log.Info($"{DisplayName} 插件已卸载");
    }

    /// <summary>该设备当前是否允许写入（总开关 + 熔断退避）。子类发帧前检查；熔断退避期满自动恢复一轮。</summary>
    protected bool CanWrite(TrackedDevice dev)
    {
        if (!WritesEnabled) return false;
        if (!dev.WritesDisabled) return true;
        if (DateTimeOffset.Now - dev.WritesDisabledAt >= WriteRetryBackoff)
        {
            dev.WritesDisabled = false;
            Log.Info($"{DisplayName} {dev.Status.DisplayName} 停写退避期满，恢复查询");
            return true;
        }
        return false;
    }

    /// <summary>
    /// 子类在 <see cref="PollDevice"/> 里判定"设备已进入休眠/关机"时调用：基类立即把设备标离线，
    /// 不等 3 次查询失败（那要 3 分钟）。典型场景见 MchosePlugin 的 `13 0A … 02` 状态通知帧。
    /// </summary>
    protected static void RequestOffline(TrackedDevice dev) => dev.OfflineRequested = true;

    /// <summary>新建一台设备的读数过滤器，并用探测读到的值建立初始基准。</summary>
    private BatteryReadingFilter CreateFilter(int? probedPercent)
    {
        var filter = new BatteryReadingFilter(MaxPlausibleStepPercent, AnomalyPersistWindow);
        if (probedPercent.HasValue) filter.Seed(probedPercent.Value);
        return filter;
    }

    /// <summary>接口是否属于本插件（按 VID/usage/报告长度严格过滤——多滤不会漏设备，少滤可能碰别家硬件）。</summary>
    protected abstract bool IsCandidate(HidDeviceInfo info);

    /// <summary>协议探测：成功返回 (协议名, 电量, 充电状态)，失败返回 null（进入冷却）。</summary>
    protected abstract (string Protocol, int? Percent, ChargingState Charging)? Probe(HidDeviceHandle handle);

    /// <summary>单设备轮询：返回本次读数；null = 失败/无数据（计入熔断）。</summary>
    protected abstract (int? Percent, ChargingState Charging)? PollDevice(TrackedDevice dev);

    /// <summary>默认显示名：产品字符串，空则用品牌名 + VID/PID。</summary>
    protected virtual string BuildDisplayName(HidDeviceInfo info)
        => string.IsNullOrEmpty(info.Product) ? $"{DisplayName} ({info.VidPid})" : info.Product;
}
