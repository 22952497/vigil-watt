# 会话交接（给下一个 AI / 未来的自己）

> 快照时间：2026-10-06 下午。**先读本文，再按需查其他文档**；内容冲突时以其他文档为准（本文是快照）。

## 1. 当前状态一句话

应用正在运行（`bin\x64\...` 新构建），迈从 G87 键盘 0x13 会话协议 + 雷柏 VT3 二代 status-report7 被动协议**均已打通**（雷柏本会话录得 69% 真实读数并写入 history.db）；**经典蓝牙耳机电量也已打通**（EDIFIER W820NB 读 HFP 网关节点 `DEVPKEY_Device_BatteryLevel`，60% 与 Windows 设置页一致）；插件架构完成二演化。

## 2. 用户设备清单（勿对号入座错）

| 设备 | 通道 | 状态 |
|---|---|---|
| 迈从 G87 键盘（dongle VID_41E4&PID_2001） | 0x13 会话协议 | ✅ **已打通**（keyboard-v1） |
| 雷柏 VT3 二代鼠标（dongle VID_24AE&PID_1412，"Rapoo Gaming Device"） | status-report7 被动心跳（~3s 自发，19 字节） | ✅ **已打通**（RapooPlugin 纯被动，零写入） |
| 北通手柄 BETOP（鲲鹏20 `PID_5191` / T9 `PID_5073`） | 厂商接口 usage_page 0xFF00；鲲鹏20 = report 0x02 + header 0x15 查询；T9 = 不响应查询、解析 Col04 被动流（byte23/120） | ✅ **已实现**，T9 已真机验证（2026-10-06）；鲲鹏20 待真机确认 |
| 弱水时砂耳机 ROSE FIRST ANGEL（**2.4G dongle = 38FF:0001**，USB 声卡+HID） | 2.4G dongle | ❌ **已双重确认无电量通道**（2026-10-05 勘察 + 2026-10-06 主机侧复检 + RoseLink 逆向确认无电量命令，protocols.md §3）；唯一途径=耳机语音播报。**不再投入** |
| Elan dongle (04F3:026F) | feature 通道 | ❌ 与弱水时砂无关（旧记录已修正），纯观察模式 |
| EDIFIER W820NB 双金标版（经典蓝牙 A2DP/AVRCP/HFP） | 系统蓝牙 AEP watcher（BluetoothClassicWatcher）+ HFP 设备节点电量 | ✅ **连接状态 + 电量均已打通**（2026-10-06）；电量读 HFP 网关节点 `DEVPKEY_Device_BatteryLevel`，实测与设置页一致（60%） |
| 华硕 AURA LED (0B05:19AF)、Virtual Multitouch (00FF:BACC)、ROSE FIRST ANGEL (38FF:0001)、lifeme 音箱 | — | 与电量无关，**勿探测** |

未见真正的罗技（VID_046D）设备；罗技 HID++ 代码已备好未真机验证。雷柏不是 HID++，勿再让 Logitech 插件认领雷柏接口。

## 3. 本会话（2026-10-04~05）完成的事，按时间线

1. **G87 电量协议打通**（最大成果）：抓官方 M HUB WEB（www.mchose.com.cn）确认 0x13 会话协议——`13 05 01` 开会话（必须，否则 dongle 全静音）→ `13 4A 01` 查电量（[5]=%，[6]=充电标志）。规格见 hardware/protocols.md §1。
2. **MchosePlugin 重写**：只发官方帧，删除全部危险扫描代码（两次事故复盘见 hardware/safety.md）。
3. **文档体系建立**：doc/ 三目录九份文档（project/dev/hardware）。
4. **插件架构二次演进**：BatteryPluginBase 基类（冷却/熔断/生命周期/稳定 Id）+ DeviceClaimRegistry 中央认领 + 反射自动发现 + 设置页动态开关。新增品牌 = 一个类文件。
5. **语言包裁剪**：WindowsAppSDK 自包含会全量拷 88 种语言 .mui；用官方排除口 `MicrosoftWindowsAppSDKFilesExcluded` 只留 en-us/zh-CN/zh-TW（dev/build.md §6，两个坑：属性无尾反斜杠、PreserveNewest 只增不减）。
6. **退避式熔断**：连续 3 次失败停写 → 10 分钟退避后重试一轮（键盘休眠唤醒后自愈，最坏 1 帧/10 分钟）。
7. **0% 电量 bug 修复**：键盘休眠/关机瞬间 dongle 推归零帧，解析层把 0 判为无效读数（三处解析全加守卫），被拒帧记日志待采样。（**后续已于 2026-10-06 采样并根治——真凶是类型位，见下 §2026-10-06 第 3、4 条**）

### 2026-10-06（本次会话）

1. **经典蓝牙耳机电量打通**：确认 Windows 设置页的耳机电量来自 **PnP 设备属性 `DEVPKEY_Device_BatteryLevel`**（挂在 HFP 免手操作网关节点），与 AEP 属性是两套体系。新增 `Ble/ClassicAudioBattery.cs`（SetupAPI P/Invoke 枚举 `BTHENUM\*` 节点 + MAC 匹配，5 秒缓存），`BluetoothClassicWatcher` 接入并在 SafePoll 里每分钟 `RefreshBattery()`。实测 EDIFIER W820NB 读到 60%，与设置页一致。
2. **新增排查脚本** `scripts/probe-devnode-battery.py`（纯 ctypes，`--all` 可 dump 任意设备节点全属性）——这是发现该属性的关键工具。
3. **休眠伪读数根治**（用户复报的老问题，本会话挖到真凶）：不是"电量位变 1"，而是**另一种类型位**的推送帧——休眠时 dongle 推 `13 0A 01 00 04 **02** 01 …`，下标 5 是**类型位**（`0x05`=电量 / `0x02`=设备状态通知），旧代码不认类型位、把 payload 的 `0x01` 当电量 1%，这就是误报低电量的根因。三层加固：解析层按类型位识别 + `InvalidPercentFloor=1` 兜底、基类 `BatteryReadingFilter`（数值有效性 + 双向变化幅度，>20 点需持续 5 分钟才采信）、告警层骤降暂缓一轮；并把 `0x02` 帧当"设备已休眠"信号**立即标离线**（`RequestOffline`，省掉 3 分钟延迟）。详见 runtime-pitfalls §12、protocols.md §1。
4. **代码可测化重构**（用户要求"异常过滤抽成独立函数"）：把内联的异常判断抽成 `Plugins/BatteryReadingFilter.cs`（`ReadingVerdict` 枚举 + 每设备一实例，零依赖可单测），把迈从 V1 帧编解码抽成 `Plugins/Mchose/MchoseV1Codec.cs`（`V1PushKind`/`V1ResponseKind`，**先认类型位再解析**）。新增 **`src/tests/EasyBluetooth.Core.Tests/`**（xUnit 标准测试项目，**18 个用例**覆盖原 51 项断言，帧解析全用真机原始帧、含休眠帧回归）——改过滤器或 V1 解析后必须重跑，`dotnet test src/tests/EasyBluetooth.Core.Tests` 全绿即通过。
5. **仓库目录重整（2026-10-06）**：`scripts/`（原名 `tools/`，后改名）收敛为**只放临时调试脚本**；C# 项目迁出——单测升为 xUnit 项目 `src/tests/EasyBluetooth.Core.Tests/`，诊断工具 `src/tests/EasyBluetooth.BleScan/`；发布产物从仓库根 `publish/` 移到 `src/publish/`。两者均已加入 sln。
6. **首扫提速（2026-10-06，用户报"启动扫描有点慢"）**：用 app.log 毫秒时间戳定位到**首个 SafePoll 要 ~27.6s 才走完**（此前误以为 HID 扫描慢，其实扫描只要 11ms）。四个根因与修法：
   - **`DeviceManager.SafePoll` 把 `plugin.Attach()` 的返回值丢了**（探测刚读到的电量被丢，紧接着 `Poll` 又完整重读一遍 → 雷柏白多花 5s）。现在 Attach 返回值立即 `Upsert`（新设备探测出来就进 UI，不等整轮结束）。
   - 基类新增 `TrackedDevice.FreshlyClaimed`：本轮新认领的设备 **`Poll` 跳过一轮**（探测值即最新读数）。
   - **雷柏 `IsCandidate` 收敛到 `InputReportLength == 19`**：旧版放行 19/32/33，本机 dongle 的 0x0011/32B 与 0x000E/33B 两条 vendor 通道从不发 0x07，每次首扫各白等一整个窗口；探测窗口 7s→5s。
   - **迈从 `TryQhw` 改官方时序（先握手）**：非 QHW dongle 握手写立即以 ERROR_INVALID_PARAMETER 失败即快速退出，省掉旧版"先空等 3s 被动监听"；另按接口路径缓存已判定协议。
   - 顺带：`Log` 改常驻 StreamWriter（旧版每行 4 次系统调用 + 开关文件）、`AppSettings.Load` 加进程内缓存（插件每轮轮询都在读磁盘）、首扫延迟 2s→300ms。
   - **实测**：首个 SafePoll 27.6s → **~5.6s**，雷柏鼠标从 +15.1s 提前到 **+5.6s** 出现；`dotnet build` 0 错误、单测 18/18。详见 code-flow.md §2。
   - **教训（重要）**：① 曾试 `Parallel.For` 并行探测多候选接口——**已放弃**，因为 `ReadInput` 是阻塞式实现（`Task.WhenAny(...).GetAwaiter().GetResult()`），把阻塞 I/O 塞进 `Parallel.For` 有线程池饥饿风险；且候选收敛到唯一接口后串行本就只花 1×窗口，不需要并行。② 曾把"某轮首扫后 41 秒无日志"误判成并行卡死——实际是**该轮轮询正常完成，只是雷柏鼠标睡着 + 经典蓝牙电量无变化所以没有日志**（下一次 SafePoll 在 +60s 准点开跑即为反证）。**日志安静 ≠ 卡死**，先看下一轮是否准点。
   - **环境提醒**：本会话为验证反复 `taskkill /F`，把迈从 dongle 会话粘死了（`13 05 01` 开会话后连 dongle 级 ACK 都没有，报"两次询问无响应"）。对照实验（临时还原旧时序重跑）证明**与本次改动无关**。恢复方式 = **重新插拔接收器**（safety.md §2.2 的已知现象）。

### 2026-10-06（下午）新增：北通手柄电量插件

1. **新增 `BetopPlugin`（北通手柄 BETOP）**：协议出处 = 开源实现 [oitsukiii/betop-battery](https://github.com/oitsukiii/betop-battery)（MIT），
   其协议来自对**北通官方 Electron 客户端**的互操作性分析，鲲鹏20（BTP-KP20EB，接收器 `VID_20BC&PID_5191`）真机验证。
   - 接收器复合设备 4 接口，**只碰厂商接口**（usage_page `0xFF00` / usage `0x0003`），鼠标/键盘/手柄接口天然排除。
   - 查询：report `0x02`，帧 `02 15`（header 0x15 = cmd 0x5 状态报告 / subcmd 0x1），补零到接口输出长度写入；
     响应 `02 15 <电量%> <保留> <充电标志低4位> …`（实测 `02 15 64 00 51`=100% 充电、`02 15 5D 00 50`=93% 放电）。
   - **为什么标准 API 读不到**：接收器把自己报成"有线设备"（XInput BatteryType=WIRED/FULL），必须直连厂商接口。
2. **纯逻辑抽出 `BetopCodec.cs`**（沿用 MchoseV1Codec 的可测化约定），单测 **10 个用例**（`BetopCodecTests.cs`，真机帧回归），
   测试总数 **18 → 28**；`dotnet test` 全绿、`dotnet build EasyBluetooth.sln -c Debug` 0 错误。
3. **接入点**：`AppSettings` 增 `PluginEnabled["Betop"]`（默认开）+ `BetopProbingEnabled`（默认开，只读查询、风险低）；
   `MchosePlugin.IsCandidate` 按 VID 排除北通接口（防 QHW/0x13 帧误发）；设置页开关自动生成（反射发现，零改动）。
4. **安全**：只发读类命令 `cmd=0x5`，不碰写配置（`cmd=0x2`）/固件/灯效；连续 3 次失败由基类熔断退避（最坏 1 帧/10 分钟）。
5. **未验证项**：本机无北通手柄，**待用户真机确认**（插上接收器 → 看 app.log `北通 状态报告` 日志与手柄实际电量对照）。

### 2026-10-06（晚）北通真机验证：修 report ID 硬编码 + 新增 T9 被动流

用户插上**北通 T9 手柄**（dongle `USB\VID_20BC&PID_5073`）真机验证，发现**设备列表里不出现北通**。逐层排查：

1. **根因 A：report ID 硬编码 `0x02`**。T9 三个厂商接口的真实 report ID 是 **`0x01`/`0x04`/`0x05`**。
   写一个该接口没有的 report ID，HID 驱动直接返回 `ERROR_INVALID_FUNCTION`（"函数不正确"）。
   **修复**：`Hid/NativeHid.cs` 新增 `ReadReportId`（`HidP_GetButtonCaps`/`HidP_GetValueCaps` → `HIDP_*_CAPS.ReportID`），
   `HidDeviceHandle.GetOutputReportId()` 暴露给插件，`ResolvePlan` 用它 + `HIDP_CAPS.OutputReportByteLength`（按接口路径缓存）。
   **教训：report ID / 报文长度一律从接口 caps 读，任何硬编码都会在换型号时静默失败。**
   > 走弯路记录：一度写了 `HidP_GetReportDescriptor` P/Invoke + 纯逻辑描述符解析器——**该 API 在 hid.dll 里不存在**
   > （实测 `AttributeError`，调用抛 `EntryPointNotFoundException` 被 catch 吞掉，表现为"读不到描述符"），
   > 于是那一版**必然失败**。用户态也没有别的方式拿原始描述符（hidapi 是重建 preparsed data）。
   > 该解析器已删除，改走 caps。**教训：P/Invoke 一个没验证过存在的导出，异常会被 catch 掩盖成"设备不支持"。**
2. **根因 B：`HIDP_CAPS` 字段序写反**。Windows SDK 里 `Usage` 在前、`UsagePage` 在后（与直觉相反）——写反导致厂商接口识别失败。
3. **根因 C：`settings.json` 缺 `Betop` 键**。`AppSettings.Load()` 改为按默认表**补齐缺失键**。
4. **T9 不响应 `02 15` 查询**（含"写一个接口、读全部三个"的路由测试，全程无 `0x15` 帧）→ 改走**被动状态流**：
   dongle 在 Col04 上持续推 `04 02 80 80 80 80 … 00 <byte23>`，**byte23 = 4-bit 电量等级 × 8，满值 120**。
   实测三值：`0x50`(80)=67% 休眠占位 → `0x58`(88)=73% 唤醒 → `0x78`(120)=**100%**（北通助手满格绿色，与"充电完成"双重印证）。
   新增 `BetopCodec.ParseT9Status` + `T9ToPercent`；`BetopPlugin.Query` 在 `ParseStatus` 未命中时回退 T9。
5. **单测**：`BetopCodecTests.cs` 加 T1–T6（T9 真机帧 + 换算覆盖），总数 **40 → 42**，`dotnet test` 全绿。
6. **已排除**：PnP `DEVPKEY_Device_BatteryLevel` 对北通无效；`PID_5075`（"T9 For Wireless"）无 report ID、64 字节全 0、无响应；Col05 是摇杆流；Col01 是配置流（`01 0b …`）。
7. **待用户最终确认**：发布后看设备列表出现「北通 BEITONG T9」及电量显示（应 ~100%）。规格见 protocols.md §4.4。

### 2026-10-06（晚）经典蓝牙设备显示名修成原始 ID

**现象**（用户截图）：设备列表里那台耳机显示成 `btc:Bluetooth#Bluetooth1c:ce:51:23:28:7b-0c:ae:bd:c6:cd:a7`
（一长串 AEP 设备 ID），而不是 "EDIFIER W820NB 双金标版"。

**根因**：WinRT 的 `DeviceInformation.Name` / `System.ItemNameDisplay` 在**友好名还没解析出来时会把 AEP 设备 ID 当名字返回**。
旧代码 `if (!string.IsNullOrEmpty(name)) status.Name = name;` 照单全收，于是显示成一长串 ID。
日志可证：13:04 那次解析正常（"EDIFIER W820NB 双金标版"），14:16 之后就变成 ID 了——同一台设备，只是名字缓存丢了。

**修复**（三层来源 + 占位名识别）：
1. `Ble/ClassicDeviceName.cs`（**纯逻辑，可单测**）：`IsPlaceholder()` 识别空值 / `Bluetooth#…` AEP ID /
   设备接口路径 / 裸 MAC；`MacText()` / `FallbackName()`。
2. `Ble/ClassicPairedNames.cs`：从 **注册表配对记录**
   `HKLM\SYSTEM\CurrentControlSet\Services\BTHPORT\Parameters\Devices\<mac12>\Name`（REG_BINARY UTF-8）
   读友好名——真机实测能拿到 "EDIFIER W820NB 双金标版"，纯读零写入，整表缓存 30 秒。
3. `BluetoothClassicWatcher`：`ApplyName()` 按 名字 → ItemNameDisplay → 注册表 → 格式化 MAC 降级；
   `RefreshBattery()` 每轮对仍是占位名的设备**重试补全**（名字可能是后来才解析出来的）。
4. 单测 `ClassicDeviceNameTests.cs` N1–N7；测试总数 **41**（删掉已无数据源的 `HidReportDescriptor` + 其 8 个用例，净 -1）。

**可复用结论**：WinRT 设备属性拿到的"名字"**必须验真**——AEP 的 `Name` 在未解析时等于设备 ID，
直接显示就是这种一长串 ID；注册表 `BTHPORT\...\Devices\<mac>\Name` 是同步、可靠、只读的兜底来源。

### 2026-10-06（晚）第二轮真机复测：三个 UI/判定问题

用户复测（北通已上线 73% ✓、耳机名已修好 ✓）又提了 3 条：

**① 北通显示名 "北通 BEITONG T9 For Keyboard & Mouse" —— 该只留型号。**
Windows 的 USB product string 是 `"BEITONG T9 For Keyboard & Mouse"`（厂商前缀 + `For …` 用途说明）。
新增纯逻辑 `BetopCodec.CleanProductName()` 收拾成 `"T9"` → 显示 **「北通 T9」**；单测 P1–P3。

**② 北通图标画成了键盘图标。**
`DeviceRowModel.From()` 按**显示名**猜图标，`switch` 里 mouse/keyboard 在手柄**前面**——
而北通名字里含 "Keyboard & Mouse"，于是被认成键盘。修：**手柄匹配提到最前**
（`手柄/gamepad/controller/joystick/北通/betop/xbox` → `E7FC` = Game）。
顺带修掉一个潜伏 bug：耳机用的 `E767` **在 Segoe MDL2/Fluent 里并不存在**（会渲染成方框），
正确的 Headphone 是 `E7F6`。

**③ 配对但没连的 W820NB 出现在列表里（显示 50% 在线）。**
根因：`Added` 首帧的 `System.Devices.Aep.IsConnected` **可能还没填好（缺失）**，
而旧代码此时拿"读到了电量"当在线依据——**那是错的**：配对但已断开的耳机，它的 HFP 网关
设备节点上**仍留着上次的电量**，于是没连也进列表。
修：新增设备一律先过 `VerifyConnectionAndAddAsync()` —— 枚举
`BluetoothDevice.GetDeviceSelectorFromConnectionStatus(Connected)` 的 AEP 集合，
**只有真在里面才进列表**（只读枚举，不建立连接）。`RefreshBattery` 仍负责电量刷新。
> 取证手法：扩展 `src/tests/EasyBluetooth.BleScan` 打印每个 AEP 的 `IsConnected` /
> `DeviceAddress` 与"已连接"集合——实测该耳机 `IsConnected=False`、"已连接"集合 0 个，
> 一句话定案，比读代码猜快得多。

**④（环境，非代码）雷柏/迈从一度不在列表里。**
迈从是我 `taskkill /F` 关应用**粘死了 dongle 会话**（本项目已知现象），**2 分钟后自愈**
（15:14:01 失败 → 15:15:57 重新认领成功，无需重插）；雷柏是**鼠标睡着 = dongle 全静音**，
动一下鼠标即恢复（ProbeCooldown 3 分钟）。**两者都不是代码回归。**
→ 今后验证请优先优雅关闭；`taskkill /F` 只在必须时用，并预期迈从 2 分钟内自愈。

**验证**：`dotnet build` 0 错误；`dotnet test` **44/44**（新增 P1–P3 产品名用例）；
发布 `src/publish` 15:21:07。

### 2026-10-06（晚）第三轮：键盘/鼠标/耳机图标仍不对 —— 改"显式声明设备类型"

用户反馈：手柄图标好了，但**键盘、鼠标、耳机的图标还是不对**（都退化成通用蓝牙图标）。

**根因**：图标是按**显示名关键字**猜的，而真机显示名里一个关键字都没有：

| 设备 | 实际显示名 | 含 mouse/keyboard/耳？ |
|---|---|---|
| 雷柏鼠标 | `雷柏 Rapoo Gaming Device` | ❌ |
| 迈从键盘 | `MCHOSE 2.4G Wireless` | ❌ |
| 蓝牙耳机 | `EDIFIER W820NB 双金标版` | ❌ |

三条全部落到默认 `E702`（蓝牙）。**"按名字猜"这条路本身就不成立**，只在名字恰好带关键字时管用。

**修法：让插件显式声明设备类型**（新增 `Models/DeviceKind.cs`）：
- `DeviceKind { Unknown, Mouse, Keyboard, Headset, Speaker, Gamepad }`；
  `DeviceStatus.Kind` 承载；`IBatteryPlugin.Kind` / `BatteryPluginBase.Kind`（**abstract**，
  逼每个插件声明，编译器帮你查漏）。
- 各插件声明：迈从 = Keyboard、雷柏 = Mouse、北通 = Gamepad、Elan = Headset、罗技 = Unknown
  （一个接收器同时管鼠标和键盘，分不出来）；经典蓝牙监视器 = Headset（A2DP/HFP 音频）。
- UI 侧 `DeviceRowModel.GlyphFor()`：**优先按 Kind 取图标**，只有 Unknown 才退回
  `DeviceKindGuesser.Guess(名字)`（纯逻辑，放 Core 便于单测）。
- 字形（Segoe MDL2 / Fluent）：`E7FC` Game / `E962` Mouse / `E765` KeyboardClassic /
  `E7F6` Headphone / `E7F5` Speakers / `E702` Bluetooth（兜底）。
- 单测 `DeviceKindGuesserTests.cs` K1–K4，**其中 K1 固化"手柄优先于键盘鼠标"这条顺序规则**
  （北通产品串含 "Keyboard & Mouse"，顺序错了就认成键盘——这坑踩了两次）。

**教训**：**能用显式元数据就别用字符串猜**。名字是给人看的，随时会变；
类型是给程序看的，应该由最清楚它的那一层（插件）声明。

**验证**：`dotnet build` 0 错误；`dotnet test` **48/48**；发布 `src/publish` 15:26:18。

### 2026-10-06（晚）第四轮：主界面"刷新"按钮点了没反应

用户反馈：**"主界面的刷新按钮是不是没什么用，我发现我点击没什么效果"**。

排查发现**不是"没用"，是三层问题叠在一起把效果全盖住了**：

| # | 问题 | 位置 |
|---|---|---|
| 1 | **视觉反馈被自己抹掉**：`Refresh_Click` 先 `IsEnabled = false`，紧接着调的 `Refresh()` 第一行就是 `IsEnabled = true` —— 同帧内禁用状态被还原，按钮连闪都不闪 | `DevicesPage.xaml.cs` |
| 2 | **异步半截没等**：`SafePoll` 里 `_ = _ble.PollConnectedAsync();` 是 fire-and-forget，`RequestPoll()` 同步返回时 BLE 侧（耳机等）根本没跑，列表纹丝不动 | `DeviceManager.cs` |
| 3 | **重入静默丢弃**：`if (Interlocked.Exchange(ref _polling, 1) == 1) return;` —— 正好撞上每分钟的自动轮询时，点击**什么都不做**，且没有任何提示 | `DeviceManager.cs` |

也就是说 `RequestPoll()` 本身确实会真扫描（HID + 插件 + BLE），功能是有的；**用户"看不出效果"是真实且合理的**——按钮零反馈、BLE 侧延迟、偶尔彻底空转。

**修法**：
- `DeviceManager`：`SafePoll()` → `async Task PollOnceAsync()`，**`await _ble.PollConnectedAsync()`**（不再 fire-and-forget）；
  新增 `Task RequestPollAsync()`，用 `lock (_pollSync) + Task _currentPoll` 串行化：
  **若已有一轮在跑就复用它的 Task 返回**，调用方等到该轮结束即可 —— 替代原来的 `_polling` 标志静默丢弃。
  定时器改 `new Timer(_ => _ = RequestPollAsync(), ...)`。`Interlocked`/`_polling` 字段删除。
- `DevicesPage`：`Refresh_Click` 改 `async void`，进入"刷新中"态（按钮禁用 + `ProgressRing` 转圈 +
  图标隐藏 + 文案变"刷新中…"），`await RequestPollAsync()` 后再恢复；加 **450ms 最小可见时长**，
  避免本地扫描几十毫秒就结束、闪一下反而像没反应。`Refresh()` 里那句 `RefreshButton.IsEnabled = true`
  删除（就是它抹掉的反馈），改由 `SetRefreshing()` 统一管。
- `SummaryText` 追加时间戳 `· HH:mm:ss 更新`：即使设备数没变，用户也能一眼看出这次刷新确实跑过了。
- `_refreshing` 标志挡重复点击。

**教训**：**"点了没反应"要先分清是"没做"还是"做了但看不见"**。这次两者都有——
反馈被自己抹掉（最致命，因为它让正确的功能看起来完全坏掉）、异步没等、重入静默失败。
**任何"用户触发的操作"都必须有可见的、跨帧的状态变化**，且**重入要么排队要么复用，绝不能静默 return**。

**验证**：`dotnet build` 0 错误；`dotnet test` **48/48**；发布 `src/publish` 15:32:06。
**另记**：`TrayService.Exit()` 的实现就是 `Dispose()`（只删托盘图标）+ `Environment.Exit(0)`，
不做任何 HID 解绑 —— 所以**强杀与托盘"退出"对 dongle 会话的影响完全等价**，那 ~2 分钟的粘连绕不开。

## 4. 待办（按优先级，详见 experience.md §5）

1. **充电标志校准**：用户插充电线 → 看 app.log `状态字节` → 补全 `ParseKeyboardV1Charge` 映射（已知 0x10 位=充电中）
2. ~~**休眠推送帧采样**~~ **已完成（2026-10-06）**：休眠帧已实测抓到并解析清楚——`13 0A 01 00 04 **02** 01 …`，下标 5 是**类型位**（0x05=电量 / 0x02=设备状态通知），旧代码没认类型位、把 payload 的 0x01 当电量 1%，这就是误报低电量的根因。现在按类型位识别，并把 0x02 当"设备已休眠"信号**立即标离线**（`RequestOffline`，省掉 3 分钟延迟）。详见 runtime-pitfalls §12、protocols.md §1。
3. **雷柏充满态采样**：充电标志已实测校准（0x01=放电/0x02=充电，2026-10-05 插充电线确认，电量 69%→75% 期间 [7]=0x02 恒定）；**充满时 [7] 的值还没采到**——下次充满看日志 `充电标志` 字节，若是 0x03 之类补进 RapooPlugin 映射
4. ~~**时砂耳机**~~ **已结案（2026-10-06）**：2.4G 无电量通道（protocols.md §3 双重确认），且 RoseLink 逆向确认其协议内也无电量命令 —— 主机侧 + 官方 App 侧均无解，唯一途径是耳机语音播报。不再投入。
5. ~~**经典蓝牙耳机电量**~~ **已解决（2026-10-06）**：连接状态走 BluetoothClassicWatcher（纯被动零写入）；电量改读 **HFP 免手操作网关设备节点的 PnP 属性 `DEVPKEY_Device_BatteryLevel`**（`{104EA319-6EE2-4701-BD47-8DDBF425BBE5},2`），用 SetupAPI 枚举 `BTHENUM\*` 节点 + 按 MAC 对上 AEP 设备。实测 EDIFIER W820NB 60% 与设置页一致。**结论：`System.Devices.AepBattery_Level` / `Aep.Battery_Level` / `System.Devices.BatteryLevel` 都别再试了，PnP 属性是另一套体系**（runtime-pitfalls §3、code-flow §3）。剩余可做：接耳机时观察 HFP 节点电量是否实时刷新（现在是每分钟轮询 + 5 秒缓存）。
6. 新世代迈从 dongle（3837:3033）QHW 协议已实现未验证，遇到即用
7. ~~**北通手柄真机验证**~~ **已真机验证（2026-10-06 晚，北通 T9）**：修掉 report ID 硬编码根因 + 新增 T9 被动流解析（见上节）。
   剩余：**发布后由用户确认**设备列表出现「北通 BEITONG T9」及电量 ~100%；另**鲲鹏20（`PID_5191`）主动查询路径仍未真机验证**（本机只有 T9）。

## 5. 给下一个 AI 的注意事项

1. **开工顺序**：doc/README.md（症状速查）→ 本文件 → 按任务查专题文档。repo 文档永远比记忆新。
2. **硬件安全红线**（用户两次事故换来，最高优先级约束）：**绝不盲扫**；只发有出处的帧（官方抓包/公开文档）；写入全过 `MchoseProbingEnabled`；熔断+退避已在基类内建，别在子类绕过。
3. **dongle 静音时先查会话**：本应用 / 浏览器里的 M HUB WEB / 官方桌面 HUB 互斥，谁持有会话谁工作，崩溃进程会把会话粘死（所有人静音），kill 持有者或重插拔恢复（safety.md §2.2）。这是最容易误判成"协议错了"的坑。
4. **调试主力手法**：让官方软件和硬件说话，浏览器 monkey-patch 抓包（debugging.md §1，含选择器弹窗自动化、DPI 截图坑）；验证用 `pip install hidapi` 直测（§2）。
5. **构建**（dev/build.md）：sln 构建、从 `bin\x64\...\win-x64\` 启动；XamlCompiler 静默崩溃查 XAML 属性名；应用运行中先 taskkill。
6. **用户偏好**：中文交流、文档中文；界面只要中英文；个人自用工具，克制功能范围（不做游戏内显示/HTTP API/账号等）。
7. **验证习惯**：改完协议代码看三处——日志认领电量值 vs 键盘 FN+B 指示、history.db 采样、拔插自愈。
8. **雷柏特性**：鼠标睡着 = dongle 全静音（被动监听空转、显示离线是预期），动一下鼠标即恢复；别把静音误判成协议/会话问题。协议细节与已证伪路径见 protocols.md §3。
9. **脚本/项目存放规则**：临时调试脚本（PowerShell/Python/一次性 P/Invoke 探针）写进 `scripts/`（索引见 scripts/README.md），**用完即删**；**长期存在的 C# 项目放 `src/tests/`**（单测/诊断工具），不放 scripts/；工程源码在 `src/`。**仓库根目录只放工程文件**；临时产物用 `%TEMP%`，用完即删。别在根目录或 src/ 里散落 .ps1/.py。

## 6. 关键文件速查

| 要动什么 | 去哪 |
|---|---|
| 迈从协议/解析 | `src/EasyBluetooth.Core/Plugins/Mchose/MchosePlugin.cs`（规格 protocols.md §1） |
| 雷柏协议/解析 | `src/EasyBluetooth.Core/Plugins/Rapoo/RapooPlugin.cs`（规格 protocols.md §3） |
| 北通协议/解析 | `src/EasyBluetooth.Core/Plugins/Betop/BetopPlugin.cs` + `BetopCodec.cs`（规格 protocols.md §4）；report ID 解析见 `Hid/NativeHid.cs` 的 `ReadReportId` |
| 经典蓝牙显示名 | `src/EasyBluetooth.Core/Ble/ClassicDeviceName.cs`（纯逻辑：占位名识别）+ `ClassicPairedNames.cs`（注册表配对名兜底） |
| 蓝牙诊断 | `src/tests/EasyBluetooth.BleScan/`（`dotnet run --project`）：适配器能力、已配对经典/LE 设备、**每个 AEP 的 `IsConnected`/`DeviceAddress` 与"已连接"集合** |
| 新增品牌插件 | 新建 `Plugins/xx/XxPlugin.cs` 继承 `BatteryPluginBase`，三个方法（architecture.md §3） |
| 轮询/采样/低电量节奏 | `src/EasyBluetooth.Core/Devices/DeviceManager.cs`（code-flow.md §2/§5/§6） |
| 经典蓝牙发现 + 电量 | `src/EasyBluetooth.Core/Ble/BluetoothClassicWatcher.cs` + `Ble/ClassicAudioBattery.cs`（code-flow.md §3、runtime-pitfalls §3） |
| 经典蓝牙电量排查 | `scripts/probe-devnode-battery.py`（`--all` dump 节点全属性） |
| 托盘/小组件/设置页 | `src/EasyBluetooth.App/Services|Views`（code-flow.md §7/§8/§9） |
| 语言包白名单 | `EasyBluetooth.App.csproj` 的 `MuiLanguagesToKeep` + `_MuiKeep` 两处（build.md §6） |
