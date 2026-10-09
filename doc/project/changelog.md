# 变更历史（changelog）

> 从 [handoff.md](handoff.md) §3 剥离的历史流水账（2026-10-04 → 2026-10-09）。**新会话别读这份**——交接看 [handoff.md](handoff.md)，这里只在「这件事当时为什么这么做」时翻。
>
> 本文件的写法（2026-10-09 精简时定的口径，与 `dev/comment-style.md` 同源）：
> - **只留「改了什么 + 为什么 + 结论」**。协议细节、坑的完整描述、流程时序一律指向专题文档（`protocols` / `runtime-pitfalls` / `code-flow` / `architecture` / `logging` / `build`），不在这里抄第二份。
> - **每轮的「验证：build 0 错误 / test N/N / 发布 HH:MM」全部删掉**——构建结果和时间戳对历史无价值；只保留**真机实测数据**（延迟、字节值、现场对照）。
> - 环境噪音（`taskkill` 粘死 dongle 会话、进程树清理、编辑器文件冲突）不重复记，见 `handoff.md` §5 与 `safety.md` §2.2。
> - 编号已拉连续（10-04~05 第一轮，10-06 二~九，10-08 十~十四，10-09 十五~十六），**定位优先用日期**。
> - 文内 `src/publish` 是 2026-10-06 当时的记录，现行发布位是仓库根 `publish/`（`dev/build.md` §4）。

---

## 2026-10-04~05（第一轮）

1. **G87 电量协议打通**（最大成果）：抓官方 M HUB WEB 确认 0x13 会话协议——`13 05 01` 开会话（必须，否则 dongle 全静音）→ `13 4A 01` 查电量（[5]=%，[6]=充电标志）。规格见 `hardware/protocols.md` §1。
2. **MchosePlugin 重写**：只发官方帧，删除全部危险扫描代码（两次事故复盘见 `hardware/safety.md`）。
3. **文档体系建立**：doc/ 三目录九份文档（project/dev/hardware）。
4. **插件架构二次演进**：`BatteryPluginBase` 基类（冷却/熔断/生命周期/稳定 Id）+ `DeviceClaimRegistry` 中央认领 + 反射自动发现 + 设置页动态开关。新增品牌 = 一个类文件（详见 `architecture.md` §3）。
5. **语言包裁剪**：WindowsAppSDK 自包含会全量拷 88 种语言 .mui；用官方排除口只留 en-us/zh-CN/zh-TW（`dev/build.md` §6）。
6. **退避式熔断**：连续 3 次失败停写 → 10 分钟退避后重试一轮（键盘休眠唤醒后自愈，最坏 1 帧/10 分钟）。
7. **0% 电量 bug 修复**：键盘休眠/关机瞬间 dongle 推归零帧，解析层把 0 判为无效读数。（**后续已于 2026-10-06 采样并根治——真凶是类型位，见下**）

## 2026-10-06

### 经典蓝牙耳机电量打通

确认 Windows 设置页的耳机电量来自 **PnP 设备属性 `DEVPKEY_Device_BatteryLevel`**（挂在 HFP 免手操作网关节点），与 AEP 属性是**两套体系**。新增 `Ble/ClassicAudioBattery.cs`（SetupAPI P/Invoke 枚举 `BTHENUM\*` 节点 + MAC 匹配），排查脚本 `scripts/probe-devnode-battery.py` 是发现该属性的关键工具。实测 EDIFIER W820NB 60%，与设置页一致。详见 `runtime-pitfalls.md` §3、`code-flow.md` §3。

### 休眠伪读数根治（用户复报的老问题）

不是"电量位变 1"，而是**另一种类型位**的推送帧——休眠时 dongle 推 `13 0A 01 00 04 **02** 01 …`，下标 5 是**类型位**（`0x05`=电量 / `0x02`=设备状态通知），旧代码不认类型位、把 payload 的 `0x01` 当电量 1%，这是误报低电量的根因。三层加固：解析层按类型位识别 + `InvalidPercentFloor=1` 兜底、基类 `BatteryReadingFilter`（数值有效性 + 双向变化幅度）、告警层骤降暂缓一轮；并把 `0x02` 帧当"设备已休眠"信号**立即标离线**。详见 `runtime-pitfalls.md` §12。

### 代码可测化重构

把内联的异常判断抽成 `Plugins/BatteryReadingFilter.cs`，把迈从 V1 帧编解码抽成 `Plugins/Mchose/MchoseV1Codec.cs`（**先认类型位再解析**）。新增 **`src/tests/VigilWatt.Core.Tests/`**（xUnit，18 个用例覆盖原 51 项断言，帧解析全用真机原始帧、含休眠帧回归）。

### 仓库目录重整

`scripts/` 收敛为**只放临时调试脚本**；C# 项目迁出（单测升为 xUnit 项目，诊断工具 `src/tests/VigilWatt.BleScan/`）。

### 首扫提速（用户报"启动扫描有点慢"）

用 app.log 毫秒时间戳定位到**首个 SafePoll 要 ~27.6s**（此前误以为 HID 扫描慢，其实扫描只要 11ms）。四个根因：

- `DeviceManager.SafePoll` 把 `plugin.Attach()` 的返回值丢了（探测刚读到的电量被丢，`Poll` 又完整重读一遍）→ 改成 Attach 返回值立即 `Upsert`。
- 基类新增 `TrackedDevice.FreshlyClaimed`：本轮新认领的设备跳过一轮 `Poll`。
- 雷柏 `IsCandidate` 收敛到 `InputReportLength == 19`（旧版放行 19/32/33，两条 vendor 通道从不发 0x07，每次白等一个窗口）。
- 迈从 `TryQhw` 改官方时序（先握手），非 QHW dongle 握手写立即失败即退出，省掉旧版"先空等 3s"。
- 顺带：`Log` 改常驻 StreamWriter、`AppSettings.Load` 加进程内缓存、首扫延迟 2s→300ms。

**实测**：首个 SafePoll 27.6s → **~5.6s**。详见 `code-flow.md` §2。

> **两条教训**：① 曾试 `Parallel.For` 并行探测——**已放弃**，`ReadInput` 是阻塞实现，并行有线程池饥饿风险，且候选收敛后串行只花 1×窗口。② 曾把"某轮首扫后 41 秒无日志"误判成卡死——实际是该轮正常完成、只是雷柏睡着且经典蓝牙电量无变化。**日志安静 ≠ 卡死**，先看下一轮是否准点。

### （下午）北通手柄电量插件

新增 `BetopPlugin`。协议出处 = 开源实现 [oitsukiii/betop-battery](https://github.com/oitsukiii/betop-battery)（MIT，源自对北通官方 Electron 客户端的互操作性分析），只碰厂商接口（usage_page `0xFF00`）。

**为什么标准 API 读不到**：接收器把自己报成"有线设备"（XInput BatteryType=WIRED/FULL），必须直连厂商接口。

协议与帧格式见 `hardware/protocols.md` §4；纯逻辑抽出 `BetopCodec.cs` + 10 个单测；接入点 `AppSettings` 增 `PluginEnabled["Betop"]` + `BetopProbingEnabled`；安全上只发读类命令 `cmd=0x5`。

### （晚）北通真机验证：修 report ID 硬编码 + 新增 T9 被动流

用户插上 **北通 T9**（dongle `VID_20BC&PID_5073`），列表里不出现北通。三个根因：

1. **report ID 硬编码 `0x02`**：T9 三个厂商接口的真实 report ID 是 **`0x01`/`0x04`/`0x05`**；写接口没有的 report ID，HID 驱动返回 `ERROR_INVALID_FUNCTION`。修：`Hid/NativeHid.cs` 新增 `ReadReportId`（从 caps 读）。
   **教训：report ID / 报文长度一律从接口 caps 读，任何硬编码都会在换型号时静默失败。**
   > 走弯路：一度写了 `HidP_GetReportDescriptor` P/Invoke + 描述符解析器——**该 API 在 hid.dll 里不存在**（抛 `EntryPointNotFoundException` 被 catch 吞掉，表现为"读不到描述符"），那一版必然失败。用户态没有别的方式拿原始描述符。**教训：P/Invoke 一个没验证过存在的导出，异常会被 catch 掩盖成"设备不支持"。**
2. **`HIDP_CAPS` 字段序写反**：Windows SDK 里 `Usage` 在前、`UsagePage` 在后。
3. **`settings.json` 缺 `Betop` 键** → `AppSettings.Load()` 改为按默认表补齐缺失键。

**T9 不响应 `02 15` 查询** → 改走**被动状态流**：dongle 在 Col04 上持续推 `04 … <byte23>`，byte23 = 4-bit 电量等级 × 8（满值 120）。实测三值：`0x50`(80)=67% 休眠占位 → `0x58`(88)=73% 唤醒 → `0x78`(120)=100%（与北通助手满格双重印证）。新增 `BetopCodec.ParseT9Status` + `T9ToPercent`。

**已排除**：PnP `DEVPKEY_Device_BatteryLevel` 对北通无效；`PID_5075` 全零无响应；Col05 是摇杆流、Col01 是配置流。

### （晚）经典蓝牙设备显示名修成原始 ID

**现象**：耳机显示成 `btc:Bluetooth#Bluetooth1c:ce:…` 一长串 AEP 设备 ID。**根因**：WinRT 的 `DeviceInformation.Name` 在友好名还没解析出来时**会把 AEP 设备 ID 当名字返回**，旧代码照单全收。

**修复（三层来源 + 占位名识别）**：`Ble/ClassicDeviceName.cs`（纯逻辑，`IsPlaceholder()` 识别空值 / `Bluetooth#…` ID / 裸 MAC）、`Ble/ClassicPairedNames.cs`（从注册表 `BTHPORT\...\Devices\<mac12>\Name` 读友好名，纯读零写入、缓存 30 秒）、`BluetoothClassicWatcher.ApplyName()` 按 名字 → ItemNameDisplay → 注册表 → 格式化 MAC 降级，并对仍是占位名的设备每轮重试补全。

**可复用结论**：WinRT 拿到的"名字"**必须验真**；注册表 `BTHPORT\...\Devices\<mac>\Name` 是同步、可靠、只读的兜底来源。

### （晚）第二轮真机复测：三个 UI/判定问题

**① 显示名 "BEITONG T9 For Keyboard & Mouse" 该只留型号** → 新增 `BetopCodec.CleanProductName()` → 「北通 T9」。

**② 图标画成键盘** → `DeviceRowModel.From()` 按显示名猜，而 `switch` 里 mouse/keyboard 排在手柄**前面**，北通名字含 "Keyboard & Mouse" 被认成键盘。修：手柄匹配提到最前。顺带修掉潜伏 bug：耳机用的 `E767` 在 Segoe MDL2/Fluent **并不存在**（渲染成方框），正确的是 `E7F6`。

**③ 配对但没连的 W820NB 出现在列表** → `Added` 首帧的 `Aep.IsConnected` 可能缺失，而旧代码拿"读到了电量"当在线依据——**错的**：配对但已断开的耳机，HFP 网关节点上仍留着上次的电量。修：新增设备一律先过 `VerifyConnectionAndAddAsync()`（枚举"已连接" AEP 集合，只读不建连）。详见 `code-flow.md` §3。

### （晚）第三轮：键盘/鼠标/耳机图标仍不对 —— 改"显式声明设备类型"

**根因**：图标按显示名关键字猜，而真机显示名里一个关键字都没有（雷柏 `Rapoo Gaming Device` / 迈从 `MCHOSE 2.4G Wireless` / 耳机 `EDIFIER W820NB 双金标版`），三条全部落到默认蓝牙图标——**"按名字猜"这条路本身不成立**。

**修法**：新增 `Models/DeviceKind.cs`，让**插件显式声明**类型（`IBatteryPlugin.Kind` 设成 abstract，逼每个插件声明，编译器查漏）；UI 侧优先按 Kind 取图标，只有 Unknown 才退回按名字猜。详见 `architecture.md` §6。

**教训**：**能用显式元数据就别用字符串猜**。名字是给人看的、随时会变；类型是给程序看的，应由最清楚它的那一层（插件）声明。

### （晚）第四轮：主界面"刷新"按钮点了没反应

不是"没用"，是三层问题叠在一起把效果全盖住：

| # | 问题 | 位置 |
|---|---|---|
| 1 | **视觉反馈被自己抹掉**：`Refresh_Click` 先 `IsEnabled=false`，紧接着调的 `Refresh()` 第一行就是 `IsEnabled=true` | `DevicesPage.xaml.cs` |
| 2 | **异步半截没等**：`_ = _ble.PollConnectedAsync()` 是 fire-and-forget，`RequestPoll()` 同步返回时 BLE 侧根本没跑 | `DeviceManager.cs` |
| 3 | **重入静默丢弃**：`Interlocked` 标志重入即 `return`，撞上自动轮询时点击什么都不做 | `DeviceManager.cs` |

**修法**：`SafePoll()` → `async Task PollOnceAsync()`（`await` BLE 不再 fire-and-forget）；新增 `RequestPollAsync()`，已有一轮在跑就**复用它的 Task** 返回（替代静默丢弃）；按钮进入"刷新中"态 + **450ms 最小可见时长** + `SummaryText` 加时间戳。

**教训**：**"点了没反应"要先分清是"没做"还是"做了但看不见"**。这次两者都有。**任何用户触发的操作都必须有可见的、跨帧的状态变化**；重入要么排队要么复用，绝不能静默 return。

### （晚）第五轮：历史统计页改版（纯 UI）

**根因不是配色，是信息结构太薄**——整页只有两个裸下拉框 + 一张曲线，想读最高/最低/平均只能从曲线上量。

改动：`StatsPage.xaml` 重排为 4 行（标题栏 → 筛选卡片 → **4 张指标摘要卡** → 曲线卡片），时间范围从 ComboBox 换成 ToggleButton 组；`BatteryChartControl` 加面积填充、**hover 提示**（竖虚线 + 高亮圆点 + 气泡）、Y 网格改 20 档、空状态改图标 + 主副文案，并修了"切换明暗主题后图表颜色不刷新"（要订阅 `ActualThemeChanged`）。

> hover 的关键坑：控件 `Background` 必须设成**非 null 的透明刷**，否则空白处收不到指针事件。

**未做**（方案里列为可选、用户未选）：多设备对比曲线、导出 CSV、充电时段背景标注。

### （傍晚）第六轮：设备发现提速 —— "键盘出来了、鼠标不出来"

翻 app.log 毫秒时间戳，时间线很清楚：

| 时刻 | 事件 |
|---|---|
| 16:13:45.06 | 启动 |
| 16:13:51.72 | 迈从键盘认领（+6.7s） |
| 16:13:52~56 | 雷柏探测窗口落空 → 失败冷却 **3 分钟** |
| 16:16:56 | 冷却到期，但主轮询相位差 11 秒错过 |
| 16:17:54 | 才认领鼠标 —— **启动后 4 分 09 秒** |

三个根因：① **雷柏 `ProbeCooldown` = 3 分钟（致命）**——该插件纯被动零写入，探测失败不是错误、只是"鼠标睡着了"，改成 5 秒并新增 `SupportsFastDiscovery`（15 秒发现节拍）。② **北通 T9 状态流识别回归**：`ParseT9Status` 把 `byte[1]==0x02` 当帧头，而真机帧变成了 `04 **0C** …`（byte[1] 是可变状态位），识别特征改为只取稳定的 report ID `0x04`。③ **冷却与轮询周期的相位坑**：`ProbeCooldown` 若接近 60 秒，失败发生在轮询开始后几秒处，下一拍判"冷却未过"整轮跳过 → 重试间隔翻倍；基类默认 1 分钟 → 30 秒。

顺带：`HidDeviceScanner` 改成**设备集合指纹变化时才 dump**（旧日志 725KB 绝大部分是这些重复行）。

**关键实测数据**：雷柏 dongle 心跳间隔 hidapi 实测 **3.04 秒**（连续 9 帧零抖动）→ 5 秒窗口是 1.6× 余量；修复后北通 +4.7s / 迈从 +5.1s / 雷柏 +5.5s 全部到齐（对照修复前：鼠标 +4m09s、北通缺席）。

### （晚）第七轮：新增「历史管理」页（按设备删除历史）

**Core**：`HistoryStore` 新增 `GetStats` / `GetDeviceStats` / `DeleteDevices`（IN 列表按数量生成占位符 + 逐个绑定参数，绝不拼 SQL）/ `FileBytes` / `Compact`（VACUUM），全部走已有的 `_writeGate` 锁（后台每分钟在并发写库，VACUUM 尤其不能并发）。构造加可选 `dbPath` 供单测指向临时库；`Log.RedirectTo()` 是配套（否则单测往真实 app.log 写脏数据）。

**UI**：新增 `Views/HistoryPage` + 4 张概览卡 + 按设备清理卡片（批量勾选 + ContentDialog 二次确认 + 删除后 `Compact()`）。**HistoryPage 刻意不订阅 `DevicesChanged`**——后台每分钟采样会重建列表、打断用户勾选。

**三个可复用的坑**：① WinUI `CheckBox` 默认 `MinWidth=120`，放进列表行要显式置 0。② **不要用双向绑定读勾选状态**（事件回调与 x:Bind 写回的先后顺序没有保证），直接在事件里读 `CheckBox.IsChecked`。③ 测试造数据别逐条 `Insert`（每条一次隐式事务，200/500 行让套件从 20ms 涨到 53s）。

### （傍晚）第八轮：两个"看起来像功能缺失"的 Bug

**① 程序图标：工程里压根没有图标**（csproj 无 `<ApplicationIcon>`，仓库无 .ico → PE 里没有 `RT_GROUP_ICON`）。新增 `scripts/make-app-icon.py`（纯标准库，不依赖 Pillow）解析式绘制绿色圆角方块 + 白色闪电，直接编码 ICO 九档。接入要**两处**：csproj `<ApplicationIcon>` + `MainWindow` 里 `AppWindow.SetIcon(...)`（非打包 WinUI 有时不继承 exe 图标资源，只做前者任务栏还是空白方块）。
> 弯路：第一版"电池外框 + 电量条"在小尺寸下像便签纸，换成闪电后 16px 依然清晰。验证不靠肉眼：把 ico 各尺寸载荷拿去 `bytes.count()` 在 exe 里数。

**② 托盘提示不刷新：`UpdateIcon()` 只在启动那一刻跑过一次**，首轮扫描还没开始 → 渲染「暂无在线设备」→ 之后再没被调用，提示被永久定格。修：`App.AttachTray` 里挂 `DeviceUpdated` + `DevicesChanged`（**两个都要订**，一个管变化一个管增删）→ `TrayService.RequestUpdate()`（内做合并 + 回 UI 线程）。
> 顺带加"提示变了才记日志"：轮询期全打会冲爆日志，但"提示到底变没变"正是这类问题唯一想知道的信息。

### （晚）第九轮：北通手柄关了还显示在线 —— T9 的 `0x50` 是"未连接"占位帧

**根因**：手柄关机后 dongle **不会停止推流**，而是继续每轮推同一帧、只把电量字节换成固定的 `0x50`；旧 `ParseT9Status` 照单全收（`0x50` 按 `raw/120` = 67%）→ 明明关了还报在线。

**取证**：翻 app.log 全部 Col04 帧，四段状态一次例外都没有——

| 时刻 | byte[1] | byte[23] | 现场 |
|---|---|---|---|
| 15:13~15:18 | `0x02` | `0x58` | 手柄开机 |
| 15:19~15:30 | `0x02` | `0x50` | 手柄休眠 |
| 15:31~17:02 | `0x0C` | `0x58` | 手柄开机（73%） |
| 17:03~17:12+ | `0x04` | `0x50` | **手柄关机，只剩 dongle** |

→ **`byte23 == 0x50` ⟺ 手柄没连上**；`byte[1]` 在 `{0x02,0x04,0x0C}` 之间乱变，不能当判据。

**为什么只认 `0x50`、不按"低位=标志位"猜**：两种自洽读法下 `0x50` 都只可能是"没连上"，认它零风险；而按"低位=标志"判，若实际是等级×8，则所有偶数等级都会被误判离线。**宁可漏判、不可误判**（验证方法见 `handoff.md` §4 第 8 条）。

**修法**：`ParseT9Status` 返回 `Linked` 字段；**探测阶段"算成功但电量留空"**——基类据此认领设备并按 `IsOnline = percent.HasValue` 标**离线**（如实列出「离线」而不是让设备从列表消失）；轮询阶段 `RequestOffline(dev)` 立即标离线。

> **顺手澄清一条此前记错的结论**：北通"每次轮询白等 4~6 秒"的根因**不是**"T9 流没被识别"，而是 `IsCandidate` 放行了 3 个非电量接口（配置流 / 摇杆流 / `5075` 全零帧）。要真正消除需收敛 `IsCandidate` 或给失败路径加负缓存——**本轮为控风险未动**。

## 2026-10-08

### 第十轮：新增「本机电量监控」（可选）

**用户拍板的取舍**（实现按此为准）：

| 决策点 | 结论 |
|---|---|
| 列表 / 小组件 | **进** |
| 托盘"最低电量" | **不参与**（笔记本正常放电 30% 不该染红托盘图标） |
| 历史曲线 | **记**，进历史统计页可选 |
| 低电量提醒 | **参与**（共用同一阈值，走原有气泡通道） |
| 台式机 / 无电池 | **完全不显示**（不出占位行） |
| 设置开关 | `MonitorSystemBattery`，**默认关** |

**实现**（全部只读、零写入）：新增 `Core/Power/SystemPowerReader.cs`（`GetSystemPowerStatus`，翻译逻辑抽成纯函数 `Interpret`：`BatteryFlag` 128/255 → 无电池不显示；`BatteryLifePercent` 255 → `Percent=null` 但仍算有电池——笔记本不该因一次读不到就假装没电池）+ `SystemBatteryWatcher.cs`（事件式数据源，不实现 `IBatteryPlugin`，固定 Id `sys:battery`）。详见 `code-flow.md` §本机电量。

**⚠️ 命名空间不能叫 `VigilWatt.Core.System`**——它会遮蔽 BCL 的 `System`，让 `System.Threading.Timer`、`System.Text.*` 全部编译失败（CS0234）。改用 `Power`。同类教训见 `experience.md` §2。

**实测**：本机电池 100% Full；托盘提示 `Dell WK717Keyboard 76%` 未受本机电量干扰 ✓。

### 第十一轮：本地打包发布 1.0.2（不推 GitHub）+ 修 Release 构建坑

**背景**：用户要"本地发布"，明确不推 GitHub（账号被停用、PAT 失效），只走 `scripts/build-installer.ps1`。

**版本号决策**：csproj 与 `dist/` 里已有 1.0.2 产物，但**那份是旧代码**。判别手法（可复用）：解包 `dist/*-full.nupkg` 里的 `VigilWatt.Core.dll`，`grep -c -a SystemPowerReader` 找符号——旧包没有，说明第十轮的本机电量监控没打进去。用户选择**仍用 1.0.2**（不 bump），直接重打覆盖。

**本轮最大收获：Release 打包 `CS0227 不安全代码` + XamlCompiler `MSB3073`**。WinRT AOT 源生成器生成的 `WinRTGenericInstantiation.g.cs` 含 `unsafe`，工程没开 `<AllowUnsafeBlocks>` → 一屏 CS0227。**表象与 `build.md` §1「XAML 属性名写错」完全相同**，判别法：**先有 CS0227 才是本坑**；Debug 不启用该生成器故从不报错。详见 `build.md` §4。

**重打同版本的注意**：`vpk` 不允许同版本重复 pack，需先删掉 `dist/` 里 1.0.2 的产物、**只留 1.0.1-full.nupkg 作增量基线**再跑脚本，否则拿不到 delta（只能出全量包）。

### 第十二轮：两个"设备在线判定"问题（用户复报）

**① 雷柏"一直在用却显示掉线"——阈值太紧**。日志实测 0x07 是**活动驱动**的心跳：动鼠标 3.04s 一帧，一停手 dongle 就静音，正常空档 13~39 秒很常见；`PassiveSilenceOffline = 12s` 落在空档分布**内部**，任何一次轮询撞进去就标离线（用户看到的那次实际只沉默 13.3 秒）。修：收到**任何报文**都刷新 `LastPassiveAt`，阈值 12s → **90s**（降级为兜底）。

**② 迈从"开了要等一会儿才显示在线"——发现节拍只覆盖零写入插件**。`DiscoverOnlyAsync`（15 秒节拍）原来只跑 `SupportsFastDiscovery`（只有雷柏）；迈从探测要写帧，被 safety.md 规则 4 排除 → 只吃 60 秒主轮询。修（用户批准）：新增 `SupportsDiscoveryProbe`——写入频率由插件自己的 `ProbeCooldown` 兜住的插件也可参与，迈从置 true + `ProbeCooldown = 10s`（一次探测约 3.7s，`10 + 3.7 < 15` 才能每拍都重探）。安全口径已写进 **safety.md 规则 4 的例外**：发帧内容不变，**没有引入任何新命令**。

### 第十三轮：雷柏离线判定改用"设备自己说断链"（用户提问引出）

**起因**：复盘第十二轮时发现断电重连那一帧带 `RF=0x7F`（bit7 连接=False），用户问"不能被动得到这个信息吗"。**考证结论：本来就是被动拿到的，只是被丢掉了**——`RapooPlugin` 早已取出 `[18].bit7` 并打进日志（走的就是被动监听路径），但 `TryParsePassiveInput` 硬编码 `Offline: false`，bit7 到这就断了；基类的"被动帧告知离线"通道**早就铺好**（参照北通 T9 占位帧）。

**方案（用户选 B：延迟确认）**：不直接判离线——`bit7` 是 **RF 链路**状态，比"设备在不在"敏感得多（关机/移出范围/受干扰都会翻 0），直接判会让列表闪。改成"标记候选 + 等确认窗口"：真关机没有恢复帧 → 窗口到点翻离线；抖动 → 下一帧 bit7=1 撤销候选。

**改动**：基类新增 `MarkOfflineCandidate` / `ClearOfflineCandidate`（幂等）+ `TrackedDevice.OfflineCandidateSince`，**检查点放在被动监听循环的空闲 tick（约 500ms）**——放 60 秒轮询会让"12 秒确认"退化成"12 + 最多 60 秒"；新增纯函数 `RapooCodec`。

**真机验证**：两次 `RF=0x7F` → `标记离线` 分别耗时 12.4s / 12.2s，恢复帧一到立刻"重新上线"，列表不闪。

### 第十四轮：迈从"关机后永不显示离线"的真凶 + 离线参数收敛

**① 真凶：`SupportsPassiveListen` 的语义过载**（不是"慢"，是**永远不会**）。一个布尔量被当成两个含义用："我起后台被动监听"（**机制事实**，对）+ "所以轮询返回 null 不算失败"（**策略推断**，错）。后果：迈从走进 `Poll()` 的 `else if (SupportsPassiveListen)` 分支，而 `PassiveSilenceOffline = Zero` 让分支内 if 不成立 → 什么都不做；含 `ConsecutiveFailures++` 的 `else` **永远不可达** → 失败计数恒为 0 → 既不标离线也不熔断。

**修法**：拆成两件事。基类新增 `OfflinePolicy.CountPollFailures`（默认 true）；迈从置 false，改由 `PollDevice` 在**发过帧**的分支自己调 `ReportPollFailure`——只有插件知道这一轮到底发帧了没有（`null` 与"用户关了写入许可 / 熔断停写中"不可区分）。

**② 第③层参数收敛成 `OfflinePolicy`**（用户拍板）。起因是用户问"为什么这些不设置成统一的"。盘出插件配置分三层：① 协议事实 ② 安全声明——这两层**必须** per-brand（物理上就是三种不同东西）；③ **离线判定策略**——机制同一套、只是参数不同，**该收敛**。新增 `Plugins/OfflinePolicy.cs`（`Silence` / `Confirm` / `MaxFailures` / `CountPollFailures` + 纯函数 `IsConfirmExpired`），每插件只写一行；基类新增私有 `MarkOffline(dev, reason)` 作为**所有离线路径的唯一落地点**。

**③ 雷柏断联窗口 12s → 6s**（用户拍板）：心跳实测 3.04s，6s ≈ 2 个周期。用户提过 3 秒被否——3s 只等于 1 个周期，正常丢一帧就误判；且"断链后没新帧"与"停手静音"在被动路径上无法区分。

**④ 迈从"关机后立刻定性"：`0x00` 触发核实**（用户拍板）。`0x0A payload=0x00` 语义至今未定论，**不能当离线证据**——但可以当**触发器**：基类新增 `RequestConfirm(dev)`，在空闲 tick 上跑一次与轮询同款的 `PollDevice` 做核实；怎么知道"插件确实发了帧却没应答"：看 `ConsecutiveFailures` 有没有被插件推高（没发帧就不会报账）——推高就当场判离线，**不等 3 次**；这条路径**不触发熔断停写**。

**真机验证（22:11~22:14，用户确认"看着比较正常了"）**：

| 设备 | 触发 | 判定离线 | 耗时 | 恢复 |
|---|---|---|---|---|
| 迈从 | 22:13:33 收到 `0A payload=0x00` → 触发核实 | 22:13:35 核实查询无应答 | **1.75s**（原 2~3 分钟） | 22:13:46 键盘开机即上线 |
| 雷柏 | 22:13:37 收到 `RF=0x7F` → 标记候选 | 22:13:43 候选持续 6s | **6.12s** | 22:13:49 恢复帧即上线 |

全程 `标记离线` / `判定离线` 各 1 次（都对应真实关机）、`读数异常已过滤` 0 次——**没有误判、没有闪烁**。

> **顺带拿到一条新证据（第一次支持"0x00=链路断"）**：键盘**关机**那一刻来的正是 `0x00`、**开机**那一刻正是 `0x01`，与电源状态严格对应。这与 10-06"全天交替"不矛盾——若那次交替其实是键盘空闲休眠/唤醒，则休眠也算"链路断"，但 dongle 仍能应答缓存帧。详见 `protocols.md` §1.3。

## 2026-10-09

### 第十五轮：日志分级与管控改造

**诉求**：日志没有正确分级和管控——噪音把 app.log 冲爆、写失败静默、没有滚动。

**做法**：先评审+补强已有方案 `doc/dev/logging.md`（修 3 处事实错误 + 补 7 项遗漏 N1–N7），用户确认后按三阶段实施，**完整设计以 `logging.md` 为准**，这里只记阶段与关键决策：

- **阶段 1 级别体系**：`LogLevel`（Debug < Info < Warn < Error + Off 哨兵）+ `Log.MinLevel`（默认 `Info`）+ 设置页下拉框（**重启生效**，不追求热更新）+ `Error` 多行缩进保住"一行一条"。噪音下沉 Info→Debug：逐帧报文 / 探测 / HID 扫描明细；协议解析**结果**仍是 Info（行尾带 `原始=<HEX>`）。
- **[P0] 稳定性四件事**：写失败不再静默（`logs/log-error.txt`，阈值+节流+上限）、失败后 30s 退避（修重试风暴）、滚动改**带序号保留**（`app.log` → `app.1.log` → `app.2.log`）、按字节计量（2MB）+ `FileShare.ReadWrite` + `Log.Shutdown()` 挂**真实退出路径**。
- **阶段 2 注入/时间**：`Sanitize` 转义换行与控制符（防日志注入伪造时间戳）；每个日志文件新建时写一行"**时间基准**"（本机时区 + UTC offset，并声明 `history.db` 的 `ts` 是 UTC 毫秒）。
- **阶段 3 降噪**：`Log.InfoOnChange(key, state, msg)` + `Log.InfoThrottled(key, msg, interval)` 把插件里各自攒的去重字段泛化成公共入口。
- `LogConfig` 收敛**未做**（方案标"可选/可砍"，纯内部搬迁无可感知收益）。

### 第十六轮：经典蓝牙设备类型判定 —— 蓝牙鼠标不再被认成耳机

**症状**：`RAPOO BT3.0 Mouse` 显示成耳机。**根因**：`BluetoothClassicWatcher` 新建条目时把 `Kind` 硬编码成 `Headset`（假设"A2DP/HFP = 耳机"），但 `BluetoothDevice.GetDeviceSelector()` 匹配的是**所有已配对经典蓝牙设备**（含鼠标/键盘/手机）；而 `DeviceStatusMerger` 是"声明覆盖猜测"（非 Unknown 才覆盖），这个错误类型会被**冻结**在进程内——重启多少次都不自愈。

**修法（不预设，按三级信号判定）**：新增 `Models/ClassicDeviceKindResolver.cs`；`ClassicAudioBattery.cs` → 重命名扩展为 `Ble/ClassicDevNodeReader.cs`（一次枚举同时给出电量 + 类别 + 服务 UUID）。

| 优先级 | 信号 | 真机取值 |
|---|---|---|
| ① | Windows 设备功能类别 `DEVPKEY_DeviceDisplay_Category`（**父节点**） | `Input.Mouse` / `Audio.Headphone` |
| ② | 经典蓝牙服务 UUID（**子节点**实例 ID 前缀） | `00001124`=HIDP；`0000111e`+`0000110b`=HFP+A2DP |
| ③ | 显示名兜底 | `RAPOO BT3.0 Mouse` → Mouse |

**猜不出就 `Unknown`，绝不退回 `Headset`**（那正是本次 bug 的根因）。类型只在还没解析出来时重算（名字/类别常后到），非 Unknown 即锁定。详见 `runtime-pitfalls.md` §3.1。

**实测坑**：**设备类别只在父节点、服务 UUID 只在子节点**（必须按 MAC 归并）；实例 ID 取 MAC 不能用"全局找 12 位十六进制"（服务 UUID 里的 `00805F9B34FB` 会被先匹配上）。排查脚本 `scripts/probe-classic-kind.py`。
