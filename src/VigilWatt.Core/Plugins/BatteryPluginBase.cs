using VigilWatt.Core.Hid;
using VigilWatt.Core.Models;
using VigilWatt.Core.Utils;

namespace VigilWatt.Core.Plugins;

/// <summary>
/// "接口认领"型插件的基类：一台设备 = 一个长持有的 HID 接口句柄。
/// 沉淀通用机制（新插件只实现协议本身，安全机制由基类统一保证）：
/// - 探测失败冷却 / 写入熔断 / 中央认领 / 设备生命周期 / 读数异常过滤 / 稳定 Id / 首扫提速；
/// - 快速发现：纯被动零写入插件可把 SupportsFastDiscovery 置 true；
/// - <b>被动实时监听</b>：SupportsPassiveListen 的插件认领后后台只读不写，
///   收到心跳/推送立即 StatusPushed（与罗技 HID++ 通知同思路，插拔电不必等 60 秒轮询）；
/// - <b>离线判定策略</b>：三条判据（沉默兜底 / 延迟确认 / 查询失败熔断）的参数集中在
///   <see cref="Offline"/>（<see cref="OfflinePolicy"/>）里，与协议事实、安全声明分开；
///   延迟确认与"立刻核实"的检查点都在被动监听的空闲 tick（约 500ms），不走 60 秒轮询。
/// 协议差异由子类实现 IsCandidate / Probe / PollDevice（及可选 TryParsePassiveInput）。
/// 会话型插件（罗技）直接实现 IBatteryPlugin，自建通知泵。
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
    /// 见 <see cref="IBatteryPlugin.SupportsDiscoveryProbe"/>：探测要写帧、但仍要参与 15 秒发现节拍。
    /// 覆盖成 true 时**必须同时把 <see cref="ProbeCooldown"/> 压到 15 秒以下**（并留出一次探测本身的
    /// 耗时余量），否则冷却会让实际重试间隔翻倍——每两拍才轮到一次探测。
    /// </summary>
    public virtual bool SupportsDiscoveryProbe => false;

    /// <summary>基类默认不推送；被动监听与子类实时事件走 <see cref="RaiseStatusPushed"/>。</summary>
    public event Action<DeviceStatus>? StatusPushed;

    /// <summary>供子类/基类被动监听推送轮询外的状态更新。</summary>
    protected void RaiseStatusPushed(DeviceStatus status) => StatusPushed?.Invoke(status);

    /// <summary>
    /// 认领成功后是否启动后台被动监听（只读不写）。
    /// 雷柏心跳 / 迈从 0A 推送 / 北通 T9 流 等覆盖为 true，并实现 <see cref="TryParsePassiveInput"/>。
    ///
    /// 它只描述**机制事实**（我起一个后台只读线程），<b>不</b>隐含"轮询空读不算通信失败"——
    /// 那是 <see cref="OfflinePolicy.CountPollFailures"/> 的事。曾经把两件事合成这一个布尔量，
    /// 于是迈从（要被动监听、但轮询空读确实是失联）键盘关机后永远不显示离线（2026-10-08 用户复报）。
    /// </summary>
    protected virtual bool SupportsPassiveListen => false;

    /// <summary>
    /// 本插件的离线判定策略（三条判据的参数都在里面，见 <see cref="OfflinePolicy"/>）。
    ///
    /// 这是插件的第③层配置——机制由基类统一实现，插件只需要填参数，**每插件一行**。
    /// 第①层（协议事实）和第②层（安全声明）仍然必须 per-brand，不在这里。
    /// </summary>
    protected virtual OfflinePolicy Offline { get; } = OfflinePolicy.Default;

    /// <summary>
    /// 解析一条被动 input report。
    /// 返回 null = 不是本协议帧；Offline=true = 设备明确离线（如北通未连接占位）。
    /// <b>必须零写入</b>。
    /// </summary>
    protected virtual (int? Percent, ChargingState Charging, bool Offline)? TryParsePassiveInput(
        TrackedDevice dev, byte[] input) => null;

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
        /// <summary>与后台被动监听互斥，保护单悬空 ReadInput / 写读会话。</summary>
        public readonly object IoGate = new();
        /// <summary>最近一次被动监听收到**任何**报文的时间（用于沉默离线；不是"解析成功"的时间）。</summary>
        public DateTimeOffset LastPassiveAt;
        /// <summary>
        /// 插件标记的"离线候选"起始时刻（<c>default</c> = 无候选）。
        /// 由 <see cref="MarkOfflineCandidate"/> / <see cref="ClearOfflineCandidate"/> 维护，
        /// 候选持续超过 <see cref="OfflinePolicy.Confirm"/> 才真正判离线。
        /// </summary>
        public DateTimeOffset OfflineCandidateSince;
        /// <summary>
        /// 插件在被动帧里请求"立刻做一次核实查询"（见 <see cref="RequestConfirm"/>）。
        /// 基类在被动监听的空闲 tick 消费它，把响应从 60 秒轮询的相位里解耦出来。
        /// </summary>
        public bool ConfirmRequested;
        public CancellationTokenSource? ListenCts;
        public Task? ListenTask;
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
                StopPassiveListen(gone);
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
            var dev = new TrackedDevice
            {
                Info = o.Info, Handle = o.Handle!, Status = status, Protocol = protocol,
                Filter = CreateFilter(percent), FreshlyClaimed = true,
                LastPassiveAt = percent.HasValue ? DateTimeOffset.Now : default,
            };
            _devices[o.Info.DevicePath] = dev;
            if (SupportsPassiveListen)
                StartPassiveListen(dev);
            Log.Info($"认领{DisplayName}设备: {o.Info} proto={protocol} 电量={percent} 充电={charging}");
            claimed.Add(status);
        }
    }

    private void StartPassiveListen(TrackedDevice dev)
    {
        if (dev.ListenTask != null) return;
        dev.ListenCts = new CancellationTokenSource();
        var cts = dev.ListenCts;
        dev.ListenTask = Task.Run(() =>
        {
            Log.Info($"{DisplayName} 被动监听已启动: {dev.Status.DisplayName}");
            while (!cts.IsCancellationRequested)
            {
                try
                {
                    byte[]? input;
                    lock (dev.IoGate)
                        input = dev.Handle.ReadInput(TimeSpan.FromMilliseconds(500));
                    if (input == null)
                    {
                        // 静音期不是无事可做：延迟离线确认与"立刻核实"靠的都是这里的节拍（约 500ms 一次）。
                        // 放到 60 秒轮询里做的话，"6 秒确认"会退化成"6 + 最多 60 秒"。
                        CheckOfflineCandidate(dev);
                        RunPendingConfirm(dev);
                        continue;
                    }

                    // 收到**任何**报文都算"设备还活着"，不只是本协议能解析出来的帧。
                    // 起因（2026-10-08 实修）：雷柏的 0x07 状态帧是活动驱动的心跳（动鼠标 3.04s 一帧，
                    // 一停手 dongle 就静音），而同一个 vendor 接口上还跑着大量 0x00/0x04/0x05 报文。
                    // 旧代码只把解析成功的帧算存活，于是"沉默"判据把正常空档当成了掉线。
                    dev.LastPassiveAt = DateTimeOffset.Now;

                    var parsed = TryParsePassiveInput(dev, input);
                    if (parsed == null) continue;

                    var (percent, charging, offline) = parsed.Value;
                    if (offline)
                    {
                        // 零歧义证据（如北通 T9 占位帧）：直接标离线，不必等延迟确认窗口。
                        // RequestOffline 顺带让下一轮 Poll 清一次失败计数，避免离线期间把失败攒到熔断。
                        RequestOffline(dev);
                        MarkOffline(dev, "被动帧告知离线");
                        continue;
                    }

                    if (TryApplyReading(dev, percent, charging, push: true))
                        Log.Info($"{DisplayName} 被动更新: {dev.Status.DisplayName} {percent}% {charging}");
                }
                catch (Exception ex)
                {
                    if (!cts.IsCancellationRequested)
                        Log.Warn($"{DisplayName} 被动监听异常: {ex.Message}");
                }
            }
        }, cts.Token);
    }

    private void StopPassiveListen(TrackedDevice dev)
    {
        try { dev.ListenCts?.Cancel(); } catch { /* ignore */ }
        try { dev.ListenTask?.Wait(TimeSpan.FromSeconds(1)); } catch { /* ignore */ }
        dev.ListenCts?.Dispose();
        dev.ListenCts = null;
        dev.ListenTask = null;
    }

    /// <summary>
    /// 把设备标为离线——**所有离线路径的唯一落地点**：翻 IsOnline、盖时间戳、记日志、立即推送。
    /// <b>幂等</b>：已经离线就什么都不做并返回 false（避免重复推送与重复日志）。
    ///
    /// 返回值只表示"本次真的翻转了"，供 <see cref="Poll"/> 决定要不要同时把设备放进返回值里。
    /// </summary>
    private bool MarkOffline(TrackedDevice dev, string reason)
    {
        if (!dev.Status.IsOnline) return false;

        dev.Status.IsOnline = false;
        dev.Status.LastUpdated = DateTimeOffset.Now;
        Log.Info($"{DisplayName} {dev.Status.DisplayName} {reason}");
        RaiseStatusPushed(dev.Status);
        return true;
    }

    /// <summary>
    /// 被动监听空闲 tick 的延迟离线确认：候选持续超过 <see cref="OfflinePolicy.Confirm"/> 就标离线。
    ///
    /// 与"沉默兜底"（<see cref="OfflinePolicy.Silence"/>）是两条**独立**判据，谁先满足谁生效：
    /// 前者靠"设备自己说断链了"，后者靠"接口彻底不说话了"。
    /// 恢复在线不必在这里做——设备回来后 <see cref="TryApplyReading"/> 会自己把 IsOnline 翻回来。
    /// </summary>
    private void CheckOfflineCandidate(TrackedDevice dev)
    {
        if (!Offline.IsConfirmExpired(dev.OfflineCandidateSince, DateTimeOffset.Now)) return;
        MarkOffline(dev, $"被动离线候选持续 {Offline.Confirm.TotalSeconds:0}s，标记离线");
    }

    /// <summary>
    /// 消费插件在被动帧里提交的"立刻核实"请求（见 <see cref="RequestConfirm"/>）：
    /// 空闲 tick 上跑一次与轮询同款的查询，插件照常自己报账。
    ///
    /// <b>为什么必须在这条路径上做</b>：只有它是秒级节拍。放进 60 秒轮询的话，"触发器"等于没加
    /// ——核实最早也要等下一拍，离线延迟还是分钟级。
    ///
    /// <b>怎么知道"插件确实发了帧却没应答"</b>：看 <c>ConsecutiveFailures</c> 有没有被插件推高。
    /// 基类不知道插件这一轮发没发帧，只有插件知道——它没发帧就不会报账，计数不动，于是这里不会误判。
    /// 计数被推高 ⇒ 是**核实失败**（不是"没开写入 / 熔断中"）⇒ 不等 3 次，当场定性为离线。
    ///
    /// <b>这条路径不触发熔断停写</b>：一次核实失败的信息量不足以停写 10 分钟；真是失联的话，
    /// 后面几轮轮询自然会把阈值攒满并熔断。
    /// </summary>
    private void RunPendingConfirm(TrackedDevice dev)
    {
        if (!dev.ConfirmRequested) return;
        dev.ConfirmRequested = false;

        int failuresBefore = dev.ConsecutiveFailures;
        (int? Percent, ChargingState Charging)? read;
        lock (dev.IoGate)
            read = PollDevice(dev);

        if (read.HasValue)
        {
            if (TryApplyReading(dev, read.Value.Percent, read.Value.Charging, push: true))
                Log.Info($"{DisplayName} 核实查询确认在线: {dev.Status.DisplayName}");
        }
        else if (dev.ConsecutiveFailures > failuresBefore)
        {
            MarkOffline(dev, "核实查询无应答，判定离线");
        }

        ConsumeOfflineRequest(dev);
    }

    /// <summary>
    /// 消费插件通过 <see cref="RequestOffline"/> 提交的"设备已休眠/关机"标记：立即标离线。
    /// 这是零歧义证据（如北通 T9 占位帧），**不计入通信失败**。
    /// 返回本轮是否消费过，<see cref="Poll"/> 据此跳过后续判定。
    /// </summary>
    private bool ConsumeOfflineRequest(TrackedDevice dev)
    {
        if (!dev.OfflineRequested) return false;
        dev.OfflineRequested = false;
        dev.ConsecutiveFailures = 0;
        MarkOffline(dev, "收到设备休眠/关机通知，立即标记离线");
        return true;
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

                (int? Percent, ChargingState Charging)? read;
                lock (dev.IoGate)
                    read = PollDevice(dev);

                // 设备主动告知休眠/关机（协议层明确帧，如北通 T9 占位）：立即标离线，
                // 不必等 3 次查询失败（那要 3 分钟）。这是设备告知，不计入通信失败。
                if (ConsumeOfflineRequest(dev)) continue;

                if (read.HasValue)
                {
                    if (TryApplyReading(dev, read.Value.Percent, read.Value.Charging, push: false))
                        updated.Add(dev.Status);
                }
                else
                {
                    // 判据一：沉默兜底。心跳类协议（雷柏）接口彻底静音过久 → 离线（正常停手不算）。
                    // 注意这一条**与 SupportsPassiveListen 无关**了：它是个策略参数，不是机制开关。
                    if (Offline.Silence > TimeSpan.Zero &&
                        dev.LastPassiveAt != default &&
                        DateTimeOffset.Now - dev.LastPassiveAt > Offline.Silence)
                    {
                        if (MarkOffline(dev, $"被动沉默超过 {Offline.Silence.TotalSeconds:0}s，标记离线"))
                            updated.Add(dev.Status);
                    }

                    // 判据二：查询失败熔断。只给"轮询确实会发帧、且 null 就是失联"的插件开；
                    // 雷柏没有查询命令、迈从/北通的 null 与"没开写入/熔断中"不可区分，
                    // 它们把 CountPollFailures 置 false 并在 PollDevice 里自己报账。
                    if (Offline.CountPollFailures)
                    {
                        dev.ConsecutiveFailures++;
                        if (dev.ConsecutiveFailures >= Offline.MaxFailures && !dev.WritesDisabled)
                        {
                            dev.WritesDisabled = true;
                            dev.WritesDisabledAt = DateTimeOffset.Now;
                            Log.Warn($"{DisplayName} {dev.Status.DisplayName} 连续 {Offline.MaxFailures} 次查询失败，熔断停写（{WriteRetryBackoff.TotalMinutes:0} 分钟退避后重试一轮）");
                        }
                        if (dev.ConsecutiveFailures >= Offline.MaxFailures &&
                            MarkOffline(dev, "连续查询失败，标记离线"))
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

    /// <summary>
    /// 统一应用读数（过滤 + 更新字段）。
    /// <paramref name="push"/> = true 时若有变化则 RaiseStatusPushed；false 时由 Poll 收集返回值。
    /// 返回是否产生了应对外可见的状态变化。
    /// </summary>
    private bool TryApplyReading(TrackedDevice dev, int? percent, ChargingState charging, bool push)
    {
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
                return false;
            }
            if (verdict == ReadingVerdict.AcceptConfirmed)
            {
                Log.Warn($"{DisplayName} {dev.Status.DisplayName} 读数 {baseline}%→{percent}% " +
                         $"已持续 {dev.Filter.PersistWindow.TotalMinutes:0} 分钟，采信为真实变化");
            }
        }

        bool changed = dev.Status.BatteryPercent != percent
                    || dev.Status.Charging != charging
                    || !dev.Status.IsOnline;

        dev.Status.BatteryPercent = percent;
        dev.Status.Charging = charging;
        dev.Status.LastUpdated = DateTimeOffset.Now;
        if (!dev.Status.IsOnline)
        {
            dev.Status.IsOnline = true;
            Log.Info($"{DisplayName} 设备重新上线: {dev.Status.DisplayName}");
            changed = true;
        }
        dev.ConsecutiveFailures = 0;

        if (changed && push)
            RaiseStatusPushed(dev.Status);
        return changed;
    }

    public virtual void Detach()
    {
        foreach (var dev in _devices.Values)
        {
            StopPassiveListen(dev);
            dev.Handle.Dispose();
        }
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
    /// 不等 <see cref="OfflinePolicy.MaxFailures"/> 次查询失败（那要几分钟）。
    /// 典型场景见 BetopPlugin 的 T9 占位帧（byte23=0x50 ⟺ 手柄未连接）。
    /// 只认协议层"零歧义"的离线证据（宁可漏判不可误判）：迈从 0x02 状态通知帧就因语义存疑被降级为仅记录。
    /// </summary>
    protected static void RequestOffline(TrackedDevice dev) => dev.OfflineRequested = true;

    /// <summary>
    /// 插件在被动帧里发现"值得立刻核实"的信号时调用。注意它是**触发器不是证据**：
    /// 基类不会因此标离线，只会立刻跑一次 <see cref="PollDevice"/> 去问设备——
    /// 有应答就照常更新读数，没应答才由插件自己的失败记账定性（见 <see cref="RunPendingConfirm"/>）。
    ///
    /// 典型用法（迈从）：状态通知 payload=0x00 语义未定论（与 0x01 全天交替、之后设备都能正常应答查询），
    /// 不能当离线证据，但可以用它把"键盘真关机了"的发现从分钟级压到秒级。
    /// </summary>
    protected static void RequestConfirm(TrackedDevice dev) => dev.ConfirmRequested = true;

    /// <summary>
    /// 标记"疑似离线"，交给 <see cref="OfflinePolicy.Confirm"/> 做延迟确认。
    /// <b>幂等</b>：候选已经开始就不刷新起始时刻——否则持续抖动会把判定无限往后推。
    ///
    /// 与 <see cref="RequestOffline"/> 的分工：那个是"零歧义证据，立即生效"（北通 T9 占位帧），
    /// 这个是"有歧义的证据，等一等看它会不会自己恢复"（雷柏 RF 断链位）。
    /// </summary>
    protected static void MarkOfflineCandidate(TrackedDevice dev)
    {
        if (dev.OfflineCandidateSince == default)
            dev.OfflineCandidateSince = DateTimeOffset.Now;
    }

    /// <summary>撤销"疑似离线"标记（收到恢复正常的证据时调用）。</summary>
    protected static void ClearOfflineCandidate(TrackedDevice dev)
        => dev.OfflineCandidateSince = default;

    /// <summary>
    /// 主动查询型插件在 <see cref="PollDevice"/> 里"**发过帧**但设备没应答"时调用：
    /// 计入连续失败，满 <see cref="OfflinePolicy.MaxFailures"/> 次则熔断停写 + 标离线。
    ///
    /// <b>为什么需要这个入口</b>：基类只在 <see cref="OfflinePolicy.CountPollFailures"/> 为 true 时
    /// 自动计轮询失败。而"轮询空读是不是失联"对有些协议根本没有答案——
    /// 雷柏**根本没有查询命令**，PollDevice 恒返回 null 是协议形态；迈从的 null 又与
    /// "没开写入 / 熔断中"不可区分。这些插件把 CountPollFailures 置 false 后，改由本入口自己报账，
    /// 否则 `ConsecutiveFailures` 永远是 0，于是既不会标离线、也不会熔断 ——
    /// 键盘关机后列表永远显示在线（2026-10-08 用户复报）。
    ///
    /// <b>为什么由插件自己报、而不是基类自动判</b>：只有插件知道"这一轮到底发帧了没有"。
    /// 未开写入 / 熔断中 / 纯推送协议都没发帧，不能算失联；插件在这些分支提前 return 即可。
    /// </summary>
    protected void ReportPollFailure(TrackedDevice dev)
    {
        if (++dev.ConsecutiveFailures < Offline.MaxFailures) return;

        if (!dev.WritesDisabled)
        {
            dev.WritesDisabled = true;
            dev.WritesDisabledAt = DateTimeOffset.Now;
            Log.Warn($"{DisplayName} {dev.Status.DisplayName} 连续 {Offline.MaxFailures} 次查询无应答，熔断停写（{WriteRetryBackoff.TotalMinutes:0} 分钟退避后重试一轮）");
        }
        RequestOffline(dev);
    }

    /// <summary>主动查询拿到应答 → 清零失败计数。</summary>
    protected static void ReportPollAnswered(TrackedDevice dev) => dev.ConsecutiveFailures = 0;

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

    /// <summary>单设备轮询：返回本次读数；null = 失败/无数据。
    /// 基类已在 <see cref="TrackedDevice.IoGate"/> 内调用——子类写读同一句柄时不必再加锁。
    /// 开启被动监听的纯心跳协议可返回 null（由后台负责读数）。</summary>
    protected abstract (int? Percent, ChargingState Charging)? PollDevice(TrackedDevice dev);

    /// <summary>默认显示名：产品字符串，空则用品牌名 + VID/PID。</summary>
    protected virtual string BuildDisplayName(HidDeviceInfo info)
        => string.IsNullOrEmpty(info.Product) ? $"{DisplayName} ({info.VidPid})" : info.Product;
}
