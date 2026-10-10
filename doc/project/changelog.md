# 变更历史（changelog）

> 从 [handoff.md](handoff.md) §3 剥离的历史流水账（2026-10-04 → 2026-10-09）。**新会话别读这份**——交接看 [handoff.md](handoff.md)，这里只在「这件事当时为什么这么做」时翻。
>
> 本文件的写法（2026-10-09 精简时定的口径，与 `dev/comment-style.md` 同源）：
> - **只留「改了什么 + 为什么 + 结论」**。协议细节、坑的完整描述、流程时序一律指向专题文档（`protocols` / `runtime-pitfalls` / `code-flow` / `architecture` / `logging` / `build`），不在这里抄第二份。
> - **每轮的「验证：build 0 错误 / test N/N / 发布 HH:MM」全部删掉**——构建结果和时间戳对历史无价值；只保留**真机实测数据**（延迟、字节值、现场对照）。
> - 环境噪音（`taskkill` 粘死 dongle 会话、进程树清理、编辑器文件冲突）不重复记，见 `handoff.md` §5 与 `safety.md` §2。
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

## 2026-10-10

### 第十七轮：建立代码审查机制（标准 + 发现闭环）+ 文档一致性清理

**诉求**："代码质量参差不齐，需要建立系统的代码审查机制"。要求**先出方案再动代码**。

**勘察结论（与直觉相反）**：底子比"参差不齐"暗示的好——全量构建 `0 警告 0 错误`；全仓 0 个 TODO/HACK、0 个 `async void`、0 个空 catch；20 个测试文件；注释占比最高 35%。真问题是五条：① 标准只覆盖注释，**代码本身**（状态模型/线程/资源/异常/硬件写入）无成文标准；② **审查结果不闭环**——10-06 两份报告的四阶段路线图从未落地（`WriteBudget` / `DeviceStateTracker` / `SerialBackgroundQueue` 三个文件至今不存在）；③ 可测性边界把最该测的挡在门外（21 个 Core 文件不在 `<Compile Include>` 清单里）；④ 同类逻辑各写各的（`ReadConnected` 两份且语义分叉）；⑤ 审查频率与深度靠心情。

**做法（用户三问定调：标准+BACKLOG / 按轮次审 / 先立标准后修）**：

- **新增 `doc/dev/review-standard.md`**：严重级 P0–P2；**R1–R8 红线**（R8 = 新增纯逻辑必须零 WinRT 依赖、可链进单测项目，治本条款）；S1–S6 建议；L0/L1/L2 三层闸门 + 按改动类型的 L1 自查表；A/B/C/D 四场景流程；L2 报告固定五段格式（**证据只写 `文件:符号`，不写行号**——行号会漂移，正是 10-06 报告被迫归档的原因）；发现闭环规则。
- **新增 `doc/reviews/BACKLOG.md`**：审查发现的**唯一跟踪载体**，`open → doing → done / wontfix`（wontfix 必须写理由）。与 `handoff.md` §4 分工写死：**BACKLOG = 未修项全集；handoff §4 = 本轮在做的几条**。编号关闭后不重排不复用。
- **改 `AGENTS.md`**：新增 §8「审查（提交前必过三条）」；§3 加 R 清单与 BACKLOG 指针。
- **改 `doc/README.md` / `doc/dev/build.md` / `doc/dev/comment-style.md` / `doc/project/handoff.md`**：导航入口、测试用法、内容归置表、待办分工。
- **未做**：L0 检查脚本（2026-10-09 用户明确不做脚本 / hook / CI）；**本轮未改任何代码**，历史遗留按用户选择进 BACKLOG 排期。

**关键结论：10-06 报告的 P0 今天全部仍然成立**（逐条回代码核对，非照抄）：

- **CR-03 是新发现，比原报告定性更重**：`DeviceManager.Upsert` 用 `GetOrAdd(id, _ => incoming)` 存**调用方本体**，而插件 `RaiseStatusPushed(dev.Status)` 传的就是同一实例 → `changed` 恒为 `false`（推送路径上 `DeviceUpdated` 与历史写入都不触发），`previousPercent` 读到的也是已被就地改写的新值 → **骤降保护（20 点）恒不成立**。UI 之所以看着正常，是因为 `Devices` 暴露的就是同一批可变实例——缺陷被掩盖。附带：`Upsert` 里「（同一对象的情况也成立）」这句注释**是错的**，属 `comment-style.md` §5 第 3 条禁止的"漂亮的谎"（随 CR-03 一并修）。
- CR-01 罗技仍 `: IBatteryPlugin`，`AppSettings` 里根本没有 `LogitechProbingEnabled`；CR-02 北通 `Probe` 以 `dev: null` 调 `Query`、`RunProbe` 不查 `CanWrite` → 每轮写一帧。
- **可关闭**：CR-D1 BLE 三条相反在线判定（已改 `bool? connected` + `Removed` 发布离线）。

**文档一致性问题一并修掉（CR-11 / CR-18）**：

- `safety.md` 规则 2 原文"一切写入受 `MchoseProbingEnabled` 开关控制"**保护不了任何东西**（北通走 `BetopProbingEnabled` 且默认 true、雷柏/Elan 无开关、罗技完全没闸门）→ 重写规则 2，并新增 **§3.1「各插件写入开关现状」**逐插件对照表 + 两条硬约束（基类默认 `WritesEnabled => true`，新插件必须显式覆盖；许可关闭时降级为只被动监听）。
- 规则 7 补上当前违例（Elan `FeatureSweep` 仍在正常路径）。
- **6 处假指针**：`README.md` / `handoff.md` / `experience.md` / `changelog.md` / `debugging.md` 都把 `safety.md` 的会话排他内容引成了一个**不存在的子节 §2.2**（该文件只有 §1–§4）→ 全部改为 `§2`（事故二）。查法：脚本提取全仓 `§N` 引用（含 `` `x.md` §N `` 与裸文件名 `x §N` 两种写法）与各文件实际小节号比对；顺带确认其余指针全部真实。
- `debugging.md` §5 的"过 `MchoseProbingEnabled` 开关"泛化为"过该插件自己的写入开关"；`architecture.md` 补罗技缺口指针。

### 第十八轮：上线前全检 + 全部调整（4 条 P0 / 7 条 P1 / 大部分 P2）

**诉求**："做一次上线前全检：代码审查、安全审计、QA 测试，看冗余、可抽离封装的函数、能优化的也一并报告。**先出方案与我确认**" → 用户确认（4 人全检 / 纳入 BACKLOG 逐条复核 / 静态 + 单测）→ 报告出完后再批"**全部调整吧**"。

**全检（只读，零代码改动）**：4 人协作（安全审计 / QA / 代码审查 / 可维护性）。报告落 `deliverables/gstack/pre-launch-check-2026-10-10.md`。
结论 🟡 条件 Go：机械层全绿（构建 0 警告 0 错误、17 类 177 用例全绿），但带 **4 条 open 的 P0**。17 条旧条目复核 **16 条仍成立**（CR-03 定性加重、CR-09 上调 P0）、1 条转 done（CR-11），新增 **CR-19~CR-33** + 10 条重构候选。

**修复轮（本轮）——按风险从低到高分三批做，每批构建 + 跑相关测试类**：

**Batch 1 · 4 条 P0**

- **CR-03**：`Upsert` 改为**先 `incoming.Clone()` 快照再入库**（`DeviceStatus.Clone()`），`isNew` 改用引用比较；把变化判定与骤降保护抽成 `Devices/DeviceChangeDetector`（纯逻辑，R8）；**补 8 个单测**（含"同一实例比较恒为假"的反面证据）；改掉 `Upsert` 里那句失实注释。
- **CR-09**：小组件三处订阅改**命名方法** + `Closed` 解绑（`OnDeviceUpdated` / `OnDevicesChanged` / `OnThemeChanged` / `OnClosed`）。
- **CR-02**：基类加 `ProbeWrites`（`Probe` 是否会写帧），`RunProbe` 首行加 `if (ProbeWrites && !WritesEnabled) return`；雷柏/Elan 置 `false`（零写入探测不该被写入开关挡住），北通/迈从保持默认 `true`。
- **CR-01**：罗技插件**自建写入闸门**——`HidppIO.WritesAllowed`（唯一写点内检查）+ `LogitechProbingEnabled` 开关（**默认 false**）+ 连续 3 次失败熔断 10 分钟退避 + `PluginEnabled["Logitech"]` 改默认关。删除直通包装 `StableHash`（CR-26/D9）。

**Batch 2 · 7 条 P1**

- **CR-05**：`HistoryStore.EnsureSchema` 加 `journal_mode=WAL` / `busy_timeout=5000` / `synchronous=NORMAL`。
- **CR-06**：历史页与统计页的查询 / 删除 / VACUUM 移进 `Task.Run`；统计页加 `_renderSeq` 代次防过期结果回填。
- **CR-04**：新增历史清理定时任务（启动 30s 首跑 + 每 6 小时），`CleanupHistoryNow` 只在真的删到东西时记日志。
- **CR-07**：`TrayService.Exit()` 在 `Environment.Exit(0)` 前调 `App.Services.Dispose()`；`AppServices.Dispose` 加 `_disposed` 幂等标志 + 逐个 try/catch。
- **CR-08**：`UnhandledException` 加计数熔断（1 分钟内 5 次）。
- **CR-10**：Elan 的只读 feature 扫描**从正常路径剥离**（`Probe` 改为恒返回 null，只保留 `PollDevice` 供日后协议确认后使用），删掉随之变成死字段的 `ClaimEnabled`。
- **CR-12 + D10**：新增 `IBatteryPlugin.WritePermission`（`Plugins/WritePermission.cs`，含 `Description` / `Get` / `Set`），**由插件声明自己的写入许可**；设置页据此自动生成"写入许可"子行（迈从 / 北通 / 罗技各一行，新增品牌自动出现）；基类 `WritesEnabled => WritePermission?.Get() ?? false`（**顺带关掉 CR-19 的 fail-open**：没声明开关的插件一律不许写）。

**Batch 3 · P2 与重构候选**

- **CR-14**：删除死代码 `Hid/HidWriteProbe.cs` 的写入变体矩阵（Elan 不再引用）。
- **CR-15 + CR-23**：低电量提醒的档位表由"精确百分比且永不复位"改为**布尔"已提醒"+ 回到阈值之上/充电时复位**（修掉"20% 提醒后 19% 又提醒"与"充满再放电不提醒"）；表随设备移除 / 插件禁用 / 关掉本机监控时清理。
- **CR-20**：`AppSettings.Save` 改**原子写**（同目录临时文件 + `Flush(flushToDisk)` + rename 覆盖），防截断后静默回落默认值丢设置。
- **CR-21**：`HidDeviceInfo.ReadInput` 的读失败由"完全静默"改为 **Debug 留痕**（不升 Warn，避免拔插噪声）；关停/竞态类空 catch 保留（预期失败，留痕只增噪）。
- **CR-22**：`Detach` 只在真的释放了设备时才打日志（修掉插件禁用期间每 60 秒一条"已卸载"假日志）。
- **CR-24 + D2**：抽出 `Models/DeviceStatusFormatter`（`IsCharging` / `IsChargingOrFull` / `PercentText` / `ChargeSuffix`），列表页 / 小组件 / 托盘 / `DeviceManager` 四处共用；补 `DeviceStatusFormatterTests`。
- **CR-25 + D7**：迈从的日志去重三件套收进 `Log.Once(level, key, message)` / `Log.ResetOnce(key)`。**没有**硬套 `InfoOnChange`——后者是"状态变化才记"，表达不了"恢复正常后再记一条"，语义会变。`Once` 带级别参数，因为"非 QHW 通道"那条原本是 Debug 级。
- **CR-27**：热路径加 `Log.IsEnabled` 短路（雷柏逐帧、迈从会话读循环、`ReadInput` 失败留痕），`Log.Hex` 的插值开销在过滤档位下不再白付。
- **CR-28**：雷柏被动心跳由"每条 Info + `Log.Hex`"（约 2.8 万行/天）改为 `Log.InfoOnChange`（只在链路/充电/电量变化时记），原始帧移出 Info。
- **CR-29**：小组件与设备页的刷新用 `Interlocked` **合并**（与 `TrayService.RequestUpdate` 同一套路），一轮轮询的 N+1 次全量重建压成 1 次。
- **CR-17**：罗技接口打开失败加 **10 分钟退避重试**（`CanRetryOpen` / `MarkOpenFailed`），修掉"接口仍在位但打不开就永不重试"。
- **D8**：抽出 `Models/ChargingStateMap.FromChargingFlag`，北通 / 迈从的"未充电且 100% ⇒ 已充满"收尾规则统一；补 `ChargingStateMapTests`。
- **D1（部分）**：`ReadConnected` + 属性名常量 + 电量解析在两个 watcher 里**逐字节重复**，抽到 `Ble/WatcherProps.cs` 并补 `WatcherPropsTests`（重点锁"属性缺失返回 null 而不是 false"——10-06 P0 的根因形态）。**基类化（`Start()` 探测循环 / `HandleRemoved` / `Snapshot()`）未做**，理由见 BACKLOG。
- **补测（全检报告行动项 5）**：新增 `PluginUtilsTests`（StableHash）、`DeviceClaimRegistryTests`、`LogLevelNamesTests`。

**wontfix（各带理由，见 BACKLOG）**：**CR-30**（句柄开销 ≈0.2% CPU，收益被高估；改只读打开会扩大枚举面、无法真机验证，拿 0.2% CPU 换硬件不确定性不划算）、**CR-31**（个人工具 + 下载前有确认对话框；真正缓解是发布包签名，属发布流程）、**CR-32**（token 未入库，风险仅在"仓库目录被分发"）、**CR-33**（本地互斥够用，改 `Global\` 收益为负）。

**保留 open**：**CR-16** 与 **D1 完整形态 / D3 / D4 / D5 / D6** —— 都是纯结构调整、不修 bug，而本项目**无 UI 自动化测试**，这些拆分（`WidgetWindow` 679 行 / `SettingsPage` 355 行 / `BatteryChartControl` 405 行 / 两个 watcher 550+ 行）只能靠人工点检回归，本轮**不能启动应用、不能碰真机**。建议顺序：D1 完整形态（含 R8 单测）→ D3（纯函数、零 UI 风险）→ D4/D5 → 最后按 D1~D5 的结果一次性处理 CR-16。

**验证**：`dotnet build VigilWatt.sln -c Debug` **0 警告 0 错误**（期间修掉 CR-10 遗留的 `CS0414 未使用字段`）；测试项目 **253 用例全绿 / 0 失败**（基线 177 → 新增 76）。
**未做**：任何 git 操作（提交由用户决定）；未启动应用、未向任何设备发帧。
**需真机确认**：设置页"写入许可"子行的读写一致性；罗技插件默认关闭后已认领会话是否如预期降级为纯被动监听；低电量提醒的实际次数。

**收尾 · 文档一致性与收工 L2 轻审查**：

- **文档一致性**：全局 grep 核对已删除/重命名的符号引用，修掉 **6 处**——`code-flow.md` ×3（`LogOnceByKey`/`ShouldLogOnce`/`ResetLogOnce` → `Log.Once`/`Log.ResetOnce`；`_lastNotifiedPercent` → `_lowBatteryNotified`；流程图 `（ProbingEnabled?）` → `（写入许可?）`）、`logging.md`（降噪入口表补 `Once`/`ResetOnce` 一行 + 迁移说明改写）、`protocols.md`（Elan `ClaimEnabled` → `Probe` 恒返回 null）、`experience.md`（泛指 `ProbingEnabled` → `WritePermission`）。`safety.md` §3.1 复核后**确认准确**（`*ProbingEnabled` 设置项仍在 `AppSettings`，表头已注明经 `WritePermission` 暴露）。`archive/` 历史报告与 BACKLOG 里描述"缺陷原状"的条目**按惯例不动**——那是"发现时的证据"。
- **收工 L2 轻审查**（`review-standard.md` §5 场景 B，限定改动面）：报告落 `doc/reviews/archive/fix-round-2026-10-10.md`。新发现 **CR-34/35/36**——均为"当前不触发"的 P2 潜在陷阱（`SettingsPage.Load()` 不可重入 / 罗技成功打开后未清 `OpenFailed` / `TryEnqueue` 失败时不复位 `_refreshQueued`），**只记录不修**（与本轮对 D1/D3~D6 的处理原则一致：要动 UI/生命周期代码、而本轮不能启动应用验证）。复核确认 **CR-03 快照传递 / CR-09 订阅解绑 / CR-19 fail-safe / CR-29 刷新合并 / CR-15+23 提醒复位** 五处修对；并修正 **CR-07 定级**（P0→P1）、**R8 归因**、**S1 超限清单补漏**（`BatteryChartControl.xaml.cs` 405 行）。
- 构建/测试未变（文档与 BACKLOG 改动不影响产物）：仍 **0 警告 0 错误 / 253 全绿**。

### 第十九轮：取消独立"写入许可"开关（所有插件统一为"插件开关即授权"）

**起因（用户实测）**：运行第十八轮的构建后，**罗技 2.4G 鼠标（MX Master 3S / `VID_046D&PID_C548`）找不到**。
`app.log` 对比给出铁证——10-09（旧版）有"接口打开 → 发现罗技设备 → 电量更新"，10-10（新版）全无。
根因：第十八轮给罗技新增了独立的 `LogitechProbingEnabled`（默认 false），而用户的 `settings.json` 里没有这个字段 →
反序列化取默认 false → `LogitechPlugin.DiscoverDevices` 的 `if (!WritesEnabled) continue;` 直接不认领任何接口。
**插件开关是"开"的，但设备一个也发现不了，界面上毫无提示**——静默功能回归（BACKLOG CR-37）。

**用户决策**："配置不应该动的，对应的配置开了，那么其相关的权限就是授权的" + "不应该是罗技的独立许可，说所有的都应该是这样"
→ **所有插件统一为"插件开关即写入授权"**，不再有第二个开关。

**改法**：

- 删除 `WritePermission` 机制：`Plugins/WritePermission.cs`、`IBatteryPlugin.WritePermission`、`BatteryPluginBase.WritePermission` 虚属性。
- 删除 `AppSettings` 的 `MchoseProbingEnabled` / `BetopProbingEnabled` / `LogitechProbingEnabled`。
- 基类 `WritesEnabled => IsWritesEnabled(Name)` 直读 `AppSettings.PluginEnabled[Name]`；
  新增 `protected static bool IsWritesEnabled(string pluginName)`，供插件的 **static** 辅助方法使用
  （迈从的 `TryQhw` / `TryKeyboardV1` / `PollKeyboardV1` / `PollQhw` 都是 static，原先读 `MchoseProbingEnabled` 的 static 属性）。
- 迈从：删 `ProbingEnabled` static 属性，改用 `IsWritesEnabled(SettingsKey)`；`Name` 改为 `SettingsKey` 常量（与罗技同款写法）。
- 罗技：`WritesEnabled` 直读插件开关；删除 `WritePermission` 与 `LogitechProbingEnabled`。
- 设置页：删 `_writePermissionRows` / `AddWritePermissionRow` / `Save_Click` 回写循环；分区提示文案改为"开启插件即授权它发送工作所需的帧"。
- `safety.md`：规则 2 与 §3.1 表整体重写（"插件开关即写入授权"），三条硬约束更新。

**行为变化（重要）**：**迈从**从"默认只被动监听"变成"插件开即允许写帧"（第十八轮 `MchoseProbingEnabled=false` 的语义消失）。
用户当前 `PluginEnabled.Mchose=true`，故迈从会开始主动写帧——已知风险、用户知情（探测写入有干扰先例，见 safety.md §1）。
**熔断/退避不受影响**（连续 3 次失败自动停写 10 分钟），只是不再由用户手动切"只被动"档。

**顺带修掉**：`LogitechPlugin.Detach` 原先无条件打"罗技插件已卸载"——CR-22 只修了基类，**漏了不继承基类的罗技**，改为只在真释放会话时记。

**验证**：`dotnet build VigilWatt.sln -c Debug` **0 警告 0 错误**；测试项目 **253 用例全绿 / 0 失败**。
**未做**：任何 git 操作；未启动应用、未向任何设备发帧。
**需真机确认**：罗技恢复发现；迈从写帧可接受。



### 第二十轮：新增「任务栏电量贴片」（用户需求，纯 UI + 设置）

**起因（用户需求）**："添加一个配置功能，可以将电量固定到任务栏中显示。显示的样式需要好看简洁"。

**方案确认（UI 改版先出方案，见 AGENTS.md §3）**：先给外观预览图 + 三条路线对比，用户选定
**① 任务栏内贴片 ② 显示内容可在设置里切换 ③ 位置默认托盘左侧、可拖动记住**。

**先排除两条路**（理由见 `dev/runtime-pitfalls.md` §16）：**deskband/Toolbar**（Win11 已移除该机制，
只能靠 COM 注入 explorer，系统一更新就失效）、**AppBar**（`SHAppBarMessage` 会真的预留屏幕空间，
和任务栏自身的 AppBar 布局互相挤压）。**采用**"把普通无边框窗口摆进任务栏矩形"（TrafficMonitor 一类做法）。

**新增**：

- `src/VigilWatt.App/TaskbarBatteryWindow.xaml(.cs)`：任务栏上的一行读数（**不画底色与边框**）。
  每台在线设备 = 设备图形 + 电量%，数字用 `UiPalette.BatteryLevel` 与列表页/小组件/托盘**同一套状态色**
  （≤20 红 / ≤50 琥珀 / >50 绿 / 充电蓝 + ⚡）；设备间用 1px 分隔线；超出「最多显示设备数」折叠成 `+N`；
  无在线设备时显示"暂无在线设备"。
- `Services/DeviceIconFactory.cs`：把小组件里的设备图形绘制（原 `WidgetWindow.BuildIcon`/`Glyph`）
  **抽成共享工厂**，两个 UI 面共用一套图形，避免各画一份而分叉。`WidgetWindow` 改为调用它（行为不变）。
- `Services/TaskbarLayout.cs`：定位数学（托盘左边默认位 + 拖动偏移 + 夹取 + 反推 offset）做成**纯函数**，
  与窗口分开并链进测试项目（R8）。
- `Services/TaskbarChipFactory.cs`：贴片的配色与"一格长什么样"（图形 + 电量 + ⚡ / 分隔线 / `+N` / 空态文案）。
- `Services/TaskbarBatteryMode.cs`：`TaskbarBatteryMode`（All/Lowest）+ `TaskbarBatteryOptions`（1~5，默认 3），
  沿用 `WidgetDisplayMode` 那种"取值收敛只有一处定义"的写法。
- `AppSettings`：`TaskbarBatteryEnabled`（默认 **false**）/ `TaskbarBatteryMode` / `TaskbarBatteryMaxDevices`
  / `TaskbarBatteryOffsetX`（拖动后的水平偏移，物理像素）。
- `Native`：`FindWindowW` / `FindWindowExW` / `GetWindowRect` / `RECT` / `WS_EX_NOACTIVATE` /
  `ShowWindow` / `HWND_TOPMOST` / `SW_HIDE` / `SW_SHOWNOACTIVATE`。
- 设置页新增「任务栏」分节（开关 / 显示内容 / 最多显示设备数），与小组件同一套即时生效逻辑；
  `AppServices.TaskbarBattery` + `App.OnLaunched` 按设置创建。

**几个刻意的决定**：

1. **本机电量恒定排除**（`Source != DeviceSource.System`）：Windows 任务栏自己就有一个电池图标，
   再重复一遍是噪音——与托盘"最低电量"不统计本机是同一个理由。
2. **跟随用 1 秒轮询而非 `SetWinEventHook`**：要监听的事件类型太多（移动/尺寸/显示隐藏/自动隐藏弹出），
   轮询开销可忽略而代码少一大截。
3. **贴片必须每秒重新 `SetWindowPos(HWND_TOPMOST)`**：任务栏本身是 topmost，两者同层，点任务栏它就会浮上来。
   注意这与 §15"别混用 HWND_TOPMOST"不矛盾——那条说的是**小组件**（层级表达三档语义），贴片只有"压住任务栏"一个诉求。
4. **显示/隐藏用原生 `ShowWindow(SW_SHOWNOACTIVATE)`，不用 `AppWindow.Show()`**：后者会尝试激活窗口，
   贴片被"显示"一次就把用户正在打的字打断了。
5. **窗口表面整体透明**（复用小组件 Win10 那条路）：贴片**不画底色与边框**，只留前景浮在任务栏上，
   所以圆角/背板一概不要；代价是文字/图形/分隔线只能按主题硬编码两套、不能走 `ThemeResource`。

**用户反馈迭代（同轮内）**：首版做成"白底圆角胶囊"，用户实测截图反馈"可以显示，但不好看，很突兀，**不需要背景**"
→ 去掉底色与边框（`Skin` 从"表面/边框/前景"五项缩到只有前景三项），`Root` 的 `BorderThickness` 归零、内边距收窄。
随之新增 BACKLOG **CR-42**：文字色跟的是应用主题、不跟"任务栏实际看起来的明暗"，
浅色主题 + 深色壁纸（任务栏半透明透出壁纸）时对比可能不足——**未处理，待真机评估**。

**收工 L2 轻审查**（`review-standard.md` §5 场景 B，限定改动面）：新发现 **CR-38/39/40/41**，全部 `open`——
① 构造期 `Scale` 退化成 1.0（非 100% DPI 下首次 Resize 用错比例）；② 贴片默认位会盖住任务栏按钮；
③ 每秒无条件重设 topmost，Start 菜单打开时会被穿透；④ 竖向任务栏 / 多显示器 / 自动隐藏三条路径**未真机验证**。
本轮**当场修掉的两处**（同属自审产物，故不留 BACKLOG 条目）：位置数学抽成 `TaskbarLayout` 并补 23 条单测
（R8；顺带把 `TaskbarBatteryWindow.xaml.cs` 从 447 行拆到 344 行，**满足 S1 的 400 行上限**）、
删掉 `Native.IsWindow` 死声明（S6）。

**验证**：`dotnet build VigilWatt.sln -c Debug` **0 警告 0 错误**；单测 **276 用例全绿 / 0 失败**
（基线 253 → 本轮新增 23 条：`TaskbarLayoutTests` 定位数学 T1–T9 + `TaskbarBatteryOptionsTests` 取值收敛 T10–T12。
`dev/build.md` §4.1 记的"全量卡住"本轮**未复现**——`LogTests` 单跑与全量都秒回，该现象仍是间歇性的，下次卡住先按 §4.1 用 `--filter` 绕）。
**未做**：任何 git 操作；**未启动应用肉眼确认**——贴片位置/自动隐藏/多显示器/竖向任务栏都需要真机看。
**需真机确认**：先看"贴片是不是空的"（`WS_EX_NOACTIVATE` 下 `Window.Activate()` 能否正常加载内容，**本轮最该先验的一条**），
再看是否稳稳压在任务栏之上、自动隐藏是否跟着一起藏、拖动与位置记忆。

**第二轮用户反馈（同轮内，三条一起）**："效果还是一样，可以考虑背景框隐藏、背景色透明。还有几个bug：
点击任务栏时这个显示会消失一下。还有 第二个设备的电量显示不全 % 不全"。

诊断分三步走，**第一条把前一轮的整个前提推翻了**：

1. **这台机器不是 Win10**。`cmd /c ver` = `10.0.26200.9445`（Win11 25H2）；`§14` 记的"本机 Win10 19045"已过期。
   于是 `WidgetCompat.NeedsTransparentChrome` 为 false → 小组件走 Mica 路径，**`MakeWindowTransparent`
   在本机从未被执行过**，"它有效"这个前提没有依据。
2. **`SetWindowCompositionAttribute` 的 accent 策略在 Win11 上静默失效**：函数照样返回非 0、
   `app.log` 里没有任何失败警告，只是窗口表面照旧铺系统底色——**"日志干净"不能当"通道生效"的证据**。
3. 从用户截图看出残留物是**浅色圆角方块 + 一圈淡描边**，圆角意味着 DWM 原生圆角 → 反证是 Win11。
   查证后确认 **WinUI 3 的窗口背景有两层**：① Win32 窗口背景（accent 只能治这一层、且仅 Win10）；
   ② XAML 内容岛 Visual 的背景。用户看到的是第 ② 层，**所以删 XAML 的 `Background` 永远删不掉它**。

**修法**（新增 `Services/TaskbarTransparency.cs`：透明通道与窗口类分开，窗口只管道具；配套 `Native.ApplyBorderlessChrome`）：

- **Win11 通道 = 内容岛系统背板换成全透明画刷**：`window.As<ICompositionSupportsSystemBackdrop>().SystemBackdrop`
  设成 `Windows.UI.Composition.Compositor` 造的透明 `CompositionColorBrush`。
  **画刷必须出自系统合成器**——用 WinUI 的 `Window.Compositor` 会编译期报 `CS0029`（类型对不上），
  好在是编译期就挡住，不会拖到运行时。合成器有线程亲和性，静态 `Lazy<>` 缓存复用。
- **`Native.ApplyBorderlessChrome`**：清 `GWL_STYLE` 的边框样式位（`WS_CAPTION`/`WS_THICKFRAME`/…）
  + `SWP_FRAMECHANGED`，再设 `DWMWA_WINDOW_CORNER_PREFERENCE = DWMWCP_DONOTROUND`、
  `DWMWA_BORDER_COLOR = DWMWA_COLOR_NONE`，把 Win11 那圈圆角与描边关掉。旧系统上这些属性不存在，
  **失败即忽略**，不让外观收敛把窗口初始化搞挂。
- **两条通道按系统分**（`WidgetCompat.NeedsTransparentChrome`）：Win10 走 accent（**本轮一行没动**），
  Win11 走背板；`Root.Loaded` 里再补设一次（背板要落到窗口真正接上的内容岛上）。
- **最右一格的 `%` 被裁**：窗口宽度原来是"字符数 × 8.5"估出来的，差一两像素就裁掉最后一个 `%`。
  改成 `ChipStack.Measure(无穷大)` 实测 `DesiredSize.Width`（`MeasureContentWidth`），
  估算函数与 `ChargeMarkWidth` 一起删掉（S6）。
- **点任务栏贴片消失一下**：抬 Z 序原来跟几何跟随共用 1 秒节拍，那 1 秒就是空档。拆成两档——
  **抬 Z 序 100ms 一拍**（只是一次 `SetWindowPos`，代价可忽略），**读任务栏矩形仍约 1 秒一拍**。
- 顺带把 `Root.Padding` / `ChipStack.Spacing` 改成由 `.cs` 的 `PadH/PadV/ChipGap` 赋值，XAML 不再写数字
  （此前 `PadV` 是**声明了没人用**的死常量，S6）。

**文档纠错（都是前几轮留下的错前提）**：
- `dev/runtime-pitfalls.md` §14 的"本机 Win10 19045"标注为过期并复核为 Win11 26200；
  同处**证伪**了"`ICompositionSupportsSystemBackdrop` 是 ABI 接口、C# 用不了"这条旧结论
  （WASDK 1.7.260224002 里有 C# 投影，且是 Win11 上唯一的透明通道）。
- 新增 **§17**：两层背景模型、accent 在 Win11 静默失效、画刷必须出自系统合成器、参考实现
  （`cnbluefire/WinUI3TransparentBackground`）与未验证边界。
- §16.2 补"两档拍频"、§16.4 改为"透明是两条通道、只删 XAML 底色不够"。

**BACKLOG**：新增 **CR-43**（Win11 透明通道失效，`done`）、**CR-44**（`WS_EX_LAYERED` 备选未上，`open`）、
**CR-45**（`%` 被裁，`done`）、**CR-46**（点任务栏闪，`done`）；CR-40 补充"空档已从 1 秒压到 100ms，风险仍在"。

**验证**：`dotnet build VigilWatt.sln -c Debug` **0 警告 0 错误**；`dotnet test` **276/276**
（全量，2 秒跑完——`dev/build.md` §4.1 记的"全量卡住"本轮再次未复现）。
**未做**：任何 git 操作；**未启动应用肉眼确认**——白框是否消失、`%` 是否还裁、点任务栏是否还闪，
三条都要用户双击 exe 复看。若白框仍在，下一步是补 `WS_EX_LAYERED`（CR-44）。

### 第三轮用户反馈（同轮内）：白框与闪动都还在 → 前一轮的定性被实测推翻

用户复看后报："**白色的框还在，而且 点击任务栏。信息还好闪一下**"——即第二轮的两处修改**都没修掉**。

**第一步不是改代码，是补上"能看见"的能力。** 前两轮用临时 Python 脚本截屏想看个究竟，
截出来永远是**一片空白**，于是都在凭推理改代码。真因是那个进程**没声明 DPI 感知**：
`GetWindowRect` 返回物理坐标（本机 3840×2160），`GetDC(0)` + `BitBlt` 却按 DPI 虚拟化后的逻辑坐标
（2560×1440）取像素，两边对不上。加上 `SetProcessDpiAwarenessContext(-4)` 后第一张截图就说明了一切：
**贴片内部 90.5% 的像素是纯白 `#FFFFFF`**。

**第二步：把九个候选通道在本机逐个试一遍**（临时脚本直接改运行中窗口的样式/属性，不动代码、秒级出结果）：

| 通道 | 结果 |
|---|---|
| accent `ACCENT_ENABLE_TRANSPARENTGRADIENT` | ❌ 返回 1、日志干净、毫无变化（§17.3 那条"静默失效"由此**从推断变为实测**） |
| 透明 `SystemBackdrop`（上一轮的修法） | ⚠️ 只清掉内容岛那层，可见的那层照旧 |
| `WS_EX_LAYERED` + `LWA_ALPHA(255)` / 只加 `WS_EX_LAYERED` | ❌ 均无变化 |
| 运行期补 `WS_EX_NOREDIRECTIONBITMAP` | ❌ 系统**直接把这一位丢弃**（只能建窗口时带） |
| `DwmEnableBlurBehindWindow(1px)` / `DwmExtendFrameIntoClientArea(-1)` 单独用 | ❌ 均无变化 |
| `LWA_COLORKEY(白)` | ✅ 有效，但**不采用**（深色主题下文字就是白色会被挖掉；键中像素点击穿透，贴片留白拖不动） |

**第三步：定位真正的根因——上一轮认错了层。**
`GetClassLongPtr(hwnd, GCLP_HBRBACKGROUND)` = **`0x6`** = `(HBRUSH)(COLOR_WINDOW + 1)`：
窗口类 `WinUIDesktopWin32WindowClass` 的**类背景刷**就是 `COLOR_WINDOW`，浅色主题下**正是纯白**。
三条证据互相印证：① 颜色与实测方块完全一致；② 用 GDI 往客户区填别的颜色，方块跟着变；
③ 强制 `RedrawWindow(..., RDW_ERASE)` 触发一次擦背景，白框**立刻回来**。
而内容岛那层（`DesktopChildSiteBridge`，`WS_EX_NOREDIRECTIONBITMAP` + 无类背景刷）本来就是透明的。
**结论：可见的是 ① Win32 层，不是 ② 内容岛层**——上一轮 §17 的定性错误，已更正。

**修法**（`Services/TaskbarTransparency.cs`，两步缺一不可）：

1. `DwmExtendFrameIntoClientArea(hwnd, {-1,-1,-1,-1})`——让 DWM 改用窗口表面的逐像素 alpha 合成；
2. 子类化窗口过程，**接管 `WM_ERASEBKGND`（返回 1，认领"已擦"）与 `WM_PAINT`（用 GDI 填 alpha=0 的黑）**——
   GDI 填色不写 alpha 字节，正好留下 0，叠上第 1 步就是真透明。
   只有第 2 步没有第 1 步会剩约 4% 的暗底（实测 `#B2C8D8` vs 任务栏 `#BACFE2`），所以 extend-frame 不是可选项。

两个实现细节（都按仓里既有写法处理）：**子类化必须幂等**（`Apply` 会被调两次，重复装会无限递归；
判据用"当前窗口过程是不是已经是自己"，比记 HWND 稳）；**`WndProcDele` 必须用静态字段压住**（否则被 GC 回收后
窗口一收消息就是访问已释放内存，`TrayService` 里是同一类写法）。
顺带确认 `DwmExtendFrameIntoClientArea` **不会**把客户区变成标题栏：`WM_NCHITTEST` 在中心与四角都仍返回 `HTCLIENT(1)`。

**闪动**：把任务栏抬到 topmost 顶端复现，贴片确实被整个盖住、要等下一拍才抢回来——**空档长度就等于拍频**。
100ms 仍看得见，压到 **16ms**（`DispatcherQueueTimer` 的下限，系统计时器粒度约 15.6ms）后
实测遮挡 **6~25ms、中位 19ms**（约一帧）。

**验证**：

- `dotnet build VigilWatt.sln -c Debug` **0 警告 0 错误**；`dotnet test` **276/276**；
- **像素级实测**（本机 3840×2160 @150%）：修复后贴片内背景像素（`#E4EFFA` / `#E3EEF9`）与紧邻任务栏像素
  **逐字节相同**，仅内容像素（绿 `#34C759`）不同；截图里电池图形 + `93%` 直接浮在任务栏上，无底框；
- **顺带证实 CR-45**：两台设备同时在线时窗口从 89px 自动长到 **186px**（≈124 DIP = 两格 + 分隔线 + 间距），
  两格数字与 `%` 都完整；也证实 `TrayNotifyWnd` 在 Win11 26200 上仍存在（贴片落在托盘左侧）。

**BACKLOG**：**CR-43 重写并 done**（定性更正 + 像素级验证）、**CR-44 → wontfix**（实测无效 + colorkey 变体有硬伤）、
**CR-45 / CR-46 复看确认**、CR-40 补"拍频已到 16ms"；新增 **CR-47**（16ms 拍频的耗电未测）、
**CR-48**（拖动未真机手测）、**CR-49**（`_prevProc` 静态单份的隐含假设）。

**未做**：任何 git 操作；**仍未真机手测拖动**，未验证竖向任务栏 / 多显示器 / 自动隐藏 / 深色主题。
临时排障脚本（`.workbuddy-ai/tmp/*.py`）用完已删。

**用户复看确认**（2026-10-10 21:28）："哇哦，很棒 是我需要的效果"——**白框与整体观感确认无误**，
本轮核心诉求闭环；用户未再提及闪动。CR-47/48/49 与竖向任务栏/多显示器/自动隐藏/深色主题仍待真机。
