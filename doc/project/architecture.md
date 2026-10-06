# 项目结构与架构

> 配套阅读：[code-flow.md](code-flow.md)（运行时数据流）、[protocols.md](../hardware/protocols.md)（协议细节）。
> 以下所有路径相对仓库根，行号会漂移，引用到方法级为止。

## 1. 总览

两个项目、一条依赖方向：`VigilWatt.App → VigilWatt.Core`。Core 不依赖任何 UI（WinRT 例外：BLE 用 WinRT API，属平台能力不算 UI），App 不写协议、只消费 Core 的事件。

```
VigilWatt.sln
├── src/VigilWatt.Core/            # 无 UI 核心库（net8.0-windows10.0.19041.0）
│   ├── Ble/BleBatteryWatcher          # BLE 设备发现 + GATT 电量直读
│   ├── Devices/DeviceManager          # 中枢：合并 BLE+插件状态、定时器、历史、低电量
│   ├── Hid/
│   │   ├── NativeHid                  # Win32 P/Invoke（CfgMgr32 / hid.dll / CreateFileW）
│   │   ├── HidDeviceScanner           # 枚举在位 HID 接口 + 读元数据
│   │   ├── HidDeviceInfo              # 元数据 + HidDeviceHandle（读写通道，单悬空读）
│   │   └── HidWriteProbe              # 【已退役】旧探测矩阵，仅存档勿调用
│   ├── Plugins/
│   │   ├── IBatteryPlugin             # 插件契约：Attach / Poll / Detach
│   │   ├── BatteryPluginBase          # "接口认领"型基类：冷却/熔断/生命周期/稳定Id
│   │   ├── DeviceClaimRegistry        # 中央认领注册表（同一接口同时只归一个插件）
│   │   ├── PluginUtils                # 共享工具（路径 FNV-1a 稳定哈希）
│   │   ├── Logitech/                  # HID++ 1.0/2.0（接收器会话型，含雷柏实验支持）
│   │   ├── Mchose/MchosePlugin        # 0x13 会话协议（在用）+ QHW 0x4D（备用）
│   │   ├── Betop/BetopPlugin          # 北通手柄：厂商接口 0x15 状态报告 + T9 被动流 + BetopCodec（纯逻辑）
│   │   └── Elan/ElanHeadsetPlugin     # 纯观察模式（不认领设备）
│   ├── Models/DeviceStatus            # 统一设备状态快照（Id/电量/充电/在线）
│   ├── Settings/AppSettings           # settings.json 读写
│   ├── Storage/HistoryStore           # SQLite（Microsoft.Data.Sqlite）
│   └── Utils/Log                      # 文本日志 %LOCALAPPDATA%\VigilWatt\logs\app.log
└── src/VigilWatt.App/             # WinUI 3 前端（net8.0-windows10.0.19041.0，非打包）
    ├── App.xaml.cs                    # AppServices 容器 + 启动序列 + 全局异常兜底
    ├── MainWindow(.xaml)              # NavigationView 三页导航；关窗=隐藏到托盘
    ├── Views/
    │   ├── DevicesPage                # 设备列表（一次性 x:Bind 行模型）
    │   ├── StatsPage                  # 历史曲线（设备/时间范围选择）
    │   ├── BatteryChartControl        # 自绘 Canvas/Polyline 折线图（无第三方图表库）
    │   └── SettingsPage               # 设置项 + 应用按钮
    ├── WidgetWindow(.xaml)            # 桌面小组件（无边框悬浮条，普通层级不置顶，纯代码构建芯片）
    ├── ViewModels/DeviceRowModel      # 设备行的绑定模型
    └── Services/
        ├── TrayService                # 托盘（纯 Win32 Shell_NotifyIcon）
        ├── Native                     # App 侧 Win32 声明（托盘/菜单/光标/窗口属性）
        └── AutostartService           # HKCU\...\Run 注册表自启
```

产物：`src\publish\VigilWatt.App.exe`（self-contained 单目录发布，102MB，已 gitignore）。

配套目录：`src/tests/VigilWatt.Core.Tests/`（xUnit 单测，`<Compile Include>` 链接 Core 的纯逻辑真源文件）、`src/tests/VigilWatt.BleScan/`（诊断小工具）、`scripts/`（临时调试脚本，用完即删）。

## 2. 关键类型与关系

```
AppServices ──持有──> Settings / History / Manager / Tray / Widget / MainWindow
DeviceManager ──持有──> BleBatteryWatcher + List<IBatteryPlugin> + ConcurrentDictionary<string,DeviceStatus>
IBatteryPlugin 实现 ──认领──> HidDeviceInfo（扫描结果）──打开──> HidDeviceHandle（长持有句柄）
DeviceManager.Upsert() 是唯一的状态汇聚点（BLE 和插件都汇到它）
事件方向：Core ──DeviceUpdated/DevicesChanged/LowBatteryDetected──> App（DispatcherQueue.TryEnqueue 切 UI 线程）
```

## 3. 设计决策与理由

| 决策 | 理由 |
|---|---|
| **插件制（IBatteryPlugin）+ 反射自动发现** | 每家 2.4G 协议完全私有，隔离在一个类里；DeviceManager 反射扫描 Core 程序集实例化全部实现类。新增品牌 = 加一个类文件，DeviceManager/设置页零改动 |
| **BatteryPluginBase 基类** | 冷却/熔断/设备生命周期/稳定 Id 是安全机制而非可选样板，沉到基类后新插件想漏都漏不掉；会话型插件（罗技）可不继承，直接实现接口 + 共享工具 |
| **DeviceClaimRegistry 中央认领** | 按接口路径记账，同一接口同一时刻只归一个插件，从结构上杜绝两个插件同时开句柄/抢会话（此前靠各插件手动排除对方 VID） |
| **插件每轮收到全量 HID 接口列表（Attach 幂等）** | 设备插拔天然由扫描驱动，插件无需自己监听硬件事件；消失的设备在 Attach 里标记离线 |
| **Core 无 UI 依赖** | 协议调试可以脱离 WinUI（控制台/脚本复用 Core）；WinUI 编译器崩溃也不影响核心逻辑 |
| **BLE 不依赖系统电量属性** | `System.Devices.AepBattery_Level` 在本机 Windows 上不可用（runtime-pitfalls §3），直接 GATT 读标准 Battery Service，任何实现 0x180F 的设备都通用 |
| **自绘折线图（BatteryChartControl）** | 最初计划用 LiveCharts2，后放弃：只画一条电量曲线，自绘 100 行可控、无依赖、无 SkiaSharp 体积 |
| **托盘纯 Win32** | H.NotifyIcon 在 WinUI 3 代码创建场景下事件失灵（runtime-pitfalls §5） |
| **历史写入双通道** | 定时采样（SampleIntervalMinutes，无变化也写点）+ 电量变化立即写（Upsert 里），保证曲线既平滑又不漏跳变 |
| **统一 DeviceStatus.Id** | BLE=蓝牙地址、2.4G=设备路径哈希（`mchose:{StableHash}`）。改协议后设备 ID 会变（旧曲线对不上新 ID）——历史库里同时存在 `mchose:D78E…`（错误时代）和 `mchose:0F60…`（现在）即此原因 |
| **非打包部署（WindowsPackageType=None + SelfContained）** | 免安装免签名，双击就跑；代价是要自己带 Windows App SDK 运行时（`src/publish` 已含） |

## 4. 线程模型

- **定时器线程**（System.Threading.Timer ×2）：轮询 1 分钟（SafePoll，`Interlocked` 防重入）、采样 SampleIntervalMinutes。全部硬件 I/O 在这两个线程上。
- **UI 线程**：只收事件（DispatcherQueue.TryEnqueue）和发起手动刷新（RequestPoll 也走 SafePoll 防重入）。
- **BLE**：DeviceWatcher 回调线程 + GATT 异步（SemaphoreSlim 限流，8 秒超时）。
- **托盘**：Shell 消息窗口线程回调 → 显示气泡前切 UI 线程。

## 5. 数据存储

| 位置 | 内容 |
|---|---|
| `%LOCALAPPDATA%\VigilWatt\settings.json` | AppSettings（阈值/采样/保留天数/自启/小组件/插件开关/MchoseProbingEnabled） |
| `%LOCALAPPDATA%\VigilWatt\history.db` | SQLite `samples(device_id, name, percent, charging, ts)`，索引 (device_id, ts) |
| `%LOCALAPPDATA%\VigilWatt\logs\app.log` | 全部运行日志（协议原始报文也在） |

## 6. 约定（改代码前先看）

- Core 里的日志用中文描述 + 原始报文 hex（`Log.Hex`），协议排错全靠它——**新协议解析代码必须打原始帧日志**。
- 插件所有硬件写入必须能被熔断/开关控制：继承 `BatteryPluginBase` 时覆盖 `WritesEnabled`（如迈从的 MchoseProbingEnabled、北通的 BetopProbingEnabled），发帧前检查 `CanWrite(dev)`；熔断（连续 3 次失败停写）由基类自动执行，见 safety.md §3。
- 新插件清单：加一个 `IBatteryPlugin` 实现类（最简：继承 `BatteryPluginBase`，实现 IsCandidate/Probe/PollDevice）→ 设置页自动出现开关，注册/发现/熔断/认领全自动。
  - ⚠️ **别忘了在 `AppSettings.PluginEnabled` 默认表里加该插件的键**（`AppSettings.Load` 会给老 settings.json 补缺失键，但默认表是唯一来源）。
    不加的话 `TryGetValue` 判成"关闭"，新插件默认不工作——反序列化会用 JSON 的字典整体替换默认字典，缺键就是缺键。
  - ⚠️ **必须声明 `Kind`**（`DeviceKind`，基类是 abstract 的，编译器会逼你写）：UI 靠它选图标。
    **不要靠显示名猜类型**——真机显示名（`Rapoo Gaming Device` / `MCHOSE 2.4G Wireless` /
    `EDIFIER W820NB 双金标版`）里一个关键字都没有，猜的结果是全退化成通用蓝牙图标。
    分不出来时才写 `Unknown`，由 `DeviceKindGuesser` 兜底。
- UI 侧 XAML 事件处理器开头判空（dev/runtime-pitfalls.md §4）；新增 XAML 属性先确认控件真有（dev/build.md §1）。
