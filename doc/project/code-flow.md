# 代码流程

> 运行时的每条数据流，按真实调用顺序写。方法名均可搜索定位。

## 1. 启动序列（App.OnLaunched）

1. **单实例检查**：命名 Mutex `EasyBluetooth_SingleInstance`，已存在则直接退出。
2. `new AppServices()`：加载 AppSettings → 打开 HistoryStore（建库/建表）→ `new DeviceManager(settings, history)`（构造里挂 BLE + 经典蓝牙的 DeviceUpdated → Upsert）。
3. `Manager.Start()` → `BleBatteryWatcher.Start()` + `BluetoothClassicWatcher.Start()`（见 §3）+ 启动两个定时器：
   - 轮询：**300ms 后首跑**（探测已并行/短路，不再需要 2 秒缓冲），之后每 **1 分钟**（SafePoll）
   - 采样：1 分钟后首跑，之后每 `SampleIntervalMinutes` 分钟（SafeSample）
4. `new MainWindow()`（NavigationView 三页），然后 TrayService.Initialize()（Win32 消息窗口 + Shell_NotifyIcon）。
5. `MainWindow.Activate()` → 若 `StartMinimized` 立即 `AppWindow.Hide()`（关窗也是隐藏，见 §7）。
6. 若 `WidgetEnabled` → `new WidgetWindow()` + Activate（DIP×缩放尺寸、置顶、工具窗口样式）。
7. 全程异常兜底：App.UnhandledException / AppDomain.UnhandledException / TaskScheduler.UnobservedTaskException → Log.Error。

## 2. 轮询主循环（DeviceManager.SafePoll，每 1 分钟 + 手动刷新共用）

```
SafePoll（Interlocked 防重入）
├── HidDeviceScanner.Scan()            # CfgMgr32 枚举全部在位 HID 接口，读 VID/PID/usage/报告长度
├── foreach 插件 (反射发现，按 Name 排序)
│   ├── 未启用 → plugin.Detach() + 认领注册表 ReleaseAll
│   ├── plugin.Attach(hidDevices, _claims)  # 幂等认领：先 TryClaim 注册表，再开句柄+探测协议
│   │      → **返回值 = 本轮认领的设备（含探测时已读到的电量），立即逐个 Upsert**
│   └── plugin.Poll()                  # 只查已认领设备，返回有更新的 DeviceStatus
├── _ble.PollConnectedAsync()          # BLE 兜底轮询（异步，不阻塞）
├── _classic.RefreshBattery()          # 经典蓝牙电量重读（HFP 设备节点属性，见 §3）
└── DevicesChanged?.Invoke()           # 收尾再全量刷新一次（增量更新已在上面发生）
```

**首扫提速（2026-10-06）**：`Attach` 的返回值曾被丢弃 → 探测刚读到的电量被丢、紧接着 `Poll` 又完整重读一遍（雷柏白多花 5s）。现在：
- `Attach` 返回值立即 `Upsert` → **新设备探测出来就进 UI**，不等整轮扫描结束；
- 基类给本轮新认领的设备打 `FreshlyClaimed` 标记，**本轮 `Poll` 跳过**（探测值即最新读数），下一轮起正常轮询；
- 雷柏 `IsCandidate` 收敛到"输入长度 ==19"（协议文档里状态报表就是 19 字节那一条）——旧版放行 19/32/33，本机 0x0011/32B 与 0x000E/33B 两条 vendor 通道从不发 0x07，每次首扫各白等一整个窗口；收敛后一个 dongle 只剩唯一候选，探测窗口 7s→5s；
- 迈从 `TryQhw` 改官方时序（**先握手**，非 QHW dongle 写入立即失败即快速退出，省掉旧版先空等 3s 被动监听），并按接口路径缓存已判定协议。

> 曾尝试用 `Parallel.For` 并行探测多个候选接口。实测那条路不必走：把候选收敛到唯一接口后，串行本就只花 1×窗口；而 `ReadInput` 是**阻塞式**实现（内部 `Task.WhenAny(...).GetAwaiter().GetResult()`），把阻塞 I/O 塞进 `Parallel.For` 会引入线程池饥饿风险。要用并行必须先把这层改成真异步，别直接上 `Parallel.For`。
> （排查记录：曾把一次首扫后 41 秒无日志误判为"并行卡死"，实际是**轮询正常完成、只是那一轮雷柏鼠标睡着 + 经典蓝牙电量无变化，因而没有任何日志**——下一次 SafePoll 在 +60s 准点开跑即为证据。日志"安静"≠"卡死"。）

每轮的插件结果逐个 `Upsert(d)`（见 §5）。任何插件抛异常只记日志，不影响其他插件。

## 3. 蓝牙设备流（BleBatteryWatcher + BluetoothClassicWatcher）

**发现**：`BluetoothLEDevice.GetDeviceSelector()` 建 DeviceWatcher，属性候选列表逐个试（本机只有 `System.Devices.Aep.IsConnected` 可用）。Added/Updated 回调 → HandleDevice → 组 DeviceStatus（含名称、连接态）→ DeviceUpdated → Manager.Upsert。

**电量**：系统属性可靠时直接用；否则 `ReadBatteryViaGattAsync`——连接 → GetGattService(0x180F) → 读 characteristic(0x2A19) → 字节[0] 即百分比，8 秒超时，SemaphoreSlim 限流，每设备记录上次尝试时间避免频繁重试。

**离线**：IsConnected=false → 标记离线（保留最后电量显示）。

**经典蓝牙（BluetoothClassicWatcher，2026-10-06 新增）**：`BluetoothLEDevice.GetDeviceSelector()` 只覆盖 BLE 端点（ProtocolId `{BB7BB05E-…}`），经典蓝牙音频设备（BR/EDR，ProtocolId `{E0CBF06C-…}`，如 EDIFIER W820NB）根本不进那个 watcher——这就是"蓝牙耳机已连接但列表没有"的根因。此 watcher 用 `BluetoothDevice.GetDeviceSelector()`（已配对经典设备）单开一路。**纯被动：不打开设备、零写入。** 两个修正 BLE 版的坑：Updated 只带变化属性，IsConnected 缺失时沿用现状不误判离线；Removed 发离线状态而非静默删除。已知取舍：所有已配对且已连接的经典蓝牙设备（含手机）都会显示。

**经典蓝牙电量（ClassicAudioBattery，2026-10-06 打通）**：AEP 属性（`System.Devices.AepBattery_Level` 等）在本机全部无效，但 Windows 设置页显示的耳机电量另有来源——**HFP 免手操作音频网关设备节点的 PnP 属性 `DEVPKEY_Device_BatteryLevel`**（`{104EA319-6EE2-4701-BD47-8DDBF425BBE5},2`，BYTE）。`ClassicAudioBattery` 用 SetupAPI（`SetupDiGetClassDevsW` → `SetupDiEnumDeviceInfo` → `SetupDiGetDeviceInstanceIdW` → `SetupDiGetDevicePropertyW`）枚举 `BTHENUM\*` 节点读该属性，从实例 ID 末段（`…&0CAEBDC6CDA7_C00000000`）取设备 MAC；watcher 侧用 `MacFromAepId`（AEP ID 形如 `Bluetooth#Bluetooth<适配器MAC>-<设备MAC>`，取最后一段）对上号。整表结果缓存 5 秒；`RefreshBattery()` 挂在 SafePoll 上每分钟重读，有变化才通知。实测 EDIFIER W820NB 读数与设置页一致。排查手法见 scripts/probe-devnode-battery.py。

## 4. 2.4G 插件流（以 MchosePlugin 为例，最重要的一条链路）

### 4.1 认领（Attach，每轮调用、幂等）

```
BatteryPluginBase.Attach(hidDevices, claims)
├── IsCandidate(info)                  # vendor usage + in/out≥20，排除 consumer/system 小接口
├── 已认领 → 直接返回缓存状态
├── 1 分钟内探测失败过 → 跳过（冷却，防打扰）
├── claims.TryClaim(路径, "Mchose")    # 中央认领，被别的插件持有则跳过
└── 新设备：
    ├── info.Open() → HidDeviceHandle（长持有，退出/禁用时释放）
    ├── Probe(handle)：
    │   ├── 守卫：输出/输入 < 20 字节 → 放弃
    │   ├── TryQhw：                    # 新世代 dongle 协议（本机不命中）
    │   │   被动监听 CZ 推送 3s →（ProbingEnabled?）握手 0x4D+AA55… → getBattery(0x0904) → 解析
    │   └── TryKeyboardV1：             # 本机 G87 dongle 协议 ✅
    │       SendKeyboardV1Frame(0x05,0x01) 开会话 → 收 ACK/推送
    │       SendKeyboardV1Frame(0x4A,0x01) 查电量 → [5]=% [6]=充电标志
    ├── 失败 → 关句柄 + 释放认领 + 进入冷却
    └── 成功 → 建 DeviceStatus{Id="mchose:"+路径哈希, Protocol="keyboard-v1"} 存表
```

### 4.2 轮询（Poll，基类骨架 + 子类 PollDevice）

`PollDevice` 按 `dev.Protocol` 分派：`keyboard-v1` → `PollKeyboardV1`（发一条 `13 4A 01`，1.2 秒内解析 4A 响应或 `13 0A` 自发推送）。**所有帧解析走纯函数单元 `MchoseV1Codec`**（`BuildFrame`/`ParsePush`/`ParseBatteryResponse`，零依赖，单测在 `src/tests/EasyBluetooth.Core.Tests/`）。关键：0A 推送帧的**类型位（下标 5）必须先认**——0x05=电量（[6]=%、[7]=标志），0x02=设备状态通知（实测键盘休眠，[6] **不是电量**）。失败记账在基类：**连续 3 次 → `WritesDisabled=true` 熔断停写，之后各协议分支只走被动监听；停写 10 分钟退避期满 `CanWrite` 自动放行一轮**（对付键盘休眠唤醒，safety.md §3 规则 3）。`ProbingEnabled=false` 时轮询/探测直接短路，不写任何字节。子类判定"设备已休眠"时调基类 `RequestOffline(dev)` → 立即标离线，不等 3 次失败（省约 3 分钟）。

**读数异常过滤（`BatteryReadingFilter`，每设备一个实例，基类 `BatteryPluginBase.Poll` 调用，2026-10-06 加）**：判定只做两件事——① **数值有效性**：低于 `MinValidPercent`（默认 2，即 0/1 是休眠占位帧）或 >100 → `InvalidValue`，永不采信；② **变化幅度**：与 `LastAcceptedPercent` 相比涨跌超过 `MaxPlausibleStepPercent`（默认 20 点）→ `RejectAnomaly`，本轮**不写入 status**（UI 和低电量判定都看不到），需持续 `AnomalyPersistWindow`（默认 5 分钟）才升级为 `AcceptConfirmed` 采信；读数回到基准附近则持续计时清零。基准**不随离线清空**（伪读数常出现在"刚掉线/刚回来"的窗口）。越界/异常都打 `读数异常已过滤` 日志。阈值均为 `protected virtual`，子类可覆盖。单测见 `src/tests/EasyBluetooth.Core.Tests/`。

### 4.3 读取通道（HidDeviceHandle）

WriteOutput：异步 WriteFile（500ms 超时，失败记 Win32 错误码）。
ReadInput：**单悬空读**——句柄生命周期只有一个挂着的 ReadAsync，超时让它继续挂着，下次复用（runtime-pitfalls §1，改坏这条所有协议集体失灵）。

## 5. 状态汇聚与历史（DeviceManager.Upsert / SafeSample）

```
Upsert(incoming):
  新 Id → 立即 DevicesChanged + DeviceUpdated     # 新设备不等下一轮轮询（2026-10-06 起）
  GetOrAdd(Id) → 原地同步 Name/电量/充电/LastUpdated/IsOnline
  changed?（电量或在线状态变了）
  ├── history.Insert(status)              # 变化即写一条（保证曲线不漏跳变）
  └── DeviceUpdated?.Invoke               # UI 单设备刷新
  CheckLowBattery(status)                 # 见 §6

SafeSample（每 SampleIntervalMinutes 分钟）:
  foreach 在线且有电量的设备 → history.Insert     # 无变化也写点，保证曲线连续
```

## 6. 低电量提醒（DeviceManager.CheckLowBattery → App.AttachTray）

条件：在线 && 电量 ≤ LowBatteryThreshold && 非充电/已充满。**按百分比档位去重**：`_lastNotifiedPercent[Id]` 记录上次提醒档位，只在电量进一步下降时再提醒（避免反复轰炸）。触发 → `LowBatteryDetected` 事件 → App 层 `DispatcherQueue.TryEnqueue` → `Tray.ShowLowBattery(device)`（Win32 NIF_INFO 气泡）。

**骤降保护（2026-10-06 加）**：`Upsert` 在覆盖前记下 `previousPercent`，若本次低电量值比上次已知值低超过 20 点，则**暂缓本轮提醒**并记日志（`低电量提醒暂缓`），等下次读数确认。这是低电量告警的最后一道闸——即使某个通道漏进了休眠伪读数，也不会立刻弹告警。

## 7. UI 更新流

- **DevicesPage**：订阅 DevicesChanged（全量重建行）/ DeviceUpdated（单行刷新），事件都在后台线程来 → `DispatcherQueue.TryEnqueue` 切 UI 线程。行模型 DeviceRowModel 一次性 x:Bind。
- **SettingsPage**：插件开关按 `Manager.Plugins`（反射发现结果）动态生成，保存时遍历开关写回 `PluginEnabled`——新增插件无需改设置页。
- **StatsPage**：设备下拉 + 范围下拉（24h/7d/30d）→ 查 `History.Query(deviceId, from, to)` → BatteryChartControl.SetData 画折线。事件处理器开头判空（XAML 解析期会触发 SelectionChanged）。
- **MainWindow**：关窗被拦截 → `AppWindow.Hide()`（隐藏到托盘而不是退出）；真正退出走托盘菜单 Exit（`Environment.Exit(0)`）。
- **WidgetWindow**：订阅同样的设备事件 → 重建芯片（颜色按电量分档/充电蓝色）→ 按芯片数重算窗口宽度（DIP×RasterizationScale 物理像素）→ ClampToWorkArea 收边。可拖动（GetCursorPos 偏移）。

## 8. 托盘（TrayService，纯 Win32）

Initialize：RegisterClassW 建隐藏消息窗口（类名带 GUID）→ Shell_NotifyIconW NIM_ADD（图标 = System.Drawing 画 32×32 彩色圆+百分比文字 → GetHicon）。消息窗口 WndProc 收到回调消息 0x8C40：左键/双击 → ShowMainWindow；右键 → SetForegroundWindow + TrackPopupMenuEx(TPM_RETURNCMD)（打开主界面 / 退出）。任务栏重建（Explorer 重启）监听 TaskbarCreated 重新添加图标。低电量气泡 = NIF_INFO。

## 9. 设置流（SettingsPage.Save）

收集 UI 值 → AppSettings.Save()（settings.json）→ 按改动项生效：AutostartService 写/删 HKCU Run 键、小组件开/关、Manager.ApplySettings()（重建定时器）、CleanupHistoryNow()（按保留天数清理）。

## 10. 退出流

托盘 Exit → TrayService 删托盘图标（NIM_DELETE）→ Environment.Exit(0)。**不走** MainWindow.Close（那是隐藏）。插件句柄靠进程终止回收（正常路径 AppServices.Dispose 由窗口关闭触发的场景不存在，是已知取舍）。
