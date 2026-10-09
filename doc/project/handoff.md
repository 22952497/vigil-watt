# 会话交接（给下一个 AI / 未来的自己）

> 快照时间：2026-10-08。**先读本文，再按需查其他文档**；内容冲突时以其他文档为准（本文是快照）。

## 1. 当前状态一句话

应用运行中。本机三台 2.4G 设备**已全部打通且首次发现均在 6 秒内**：
北通 T9 手柄 73%（T9 被动状态流）、迈从 G87 键盘 65%（0x13 会话协议）、雷柏 VT3 二代鼠标 98%（status-report7 被动）；
经典蓝牙耳机 EDIFIER W820NB 走 HFP 网关节点 `DEVPKEY_Device_BatteryLevel`（与 Windows 设置页一致）；
插件架构完成二演化。**本轮（第十轮）新增「本机电量监控」——可选监控笔记本电池（默认关），见下。**

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
| **本机电池（笔记本）** | `GetSystemPowerStatus`（`Power/SystemBatteryWatcher`，Id `sys:battery`） | ✅ 已接入，**默认关闭**（设置页「本机 → 监控本机电量」）。本机实测：有电池、插电、100%/Full |

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
4. **代码可测化重构**（用户要求"异常过滤抽成独立函数"）：把内联的异常判断抽成 `Plugins/BatteryReadingFilter.cs`（`ReadingVerdict` 枚举 + 每设备一实例，零依赖可单测），把迈从 V1 帧编解码抽成 `Plugins/Mchose/MchoseV1Codec.cs`（`V1PushKind`/`V1ResponseKind`，**先认类型位再解析**）。新增 **`src/tests/VigilWatt.Core.Tests/`**（xUnit 标准测试项目，**18 个用例**覆盖原 51 项断言，帧解析全用真机原始帧、含休眠帧回归）——改过滤器或 V1 解析后必须重跑，`dotnet test src/tests/VigilWatt.Core.Tests` 全绿即通过。
5. **仓库目录重整（2026-10-06）**：`scripts/`（原名 `tools/`，后改名）收敛为**只放临时调试脚本**；C# 项目迁出——单测升为 xUnit 项目 `src/tests/VigilWatt.Core.Tests/`，诊断工具 `src/tests/VigilWatt.BleScan/`；发布产物从仓库根 `publish/` 移到 `src/publish/`。两者均已加入 sln。
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
   测试总数 **18 → 28**；`dotnet test` 全绿、`dotnet build VigilWatt.sln -c Debug` 0 错误。
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
> 取证手法：扩展 `src/tests/VigilWatt.BleScan` 打印每个 AEP 的 `IsConnected` /
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

### 2026-10-06（晚）第五轮：历史统计页改版（纯 UI，不碰 Core）

用户反馈"历史统计页面显示不好看"，先出方案确认后动代码。**根因不是配色，是信息结构太薄**——
整页只有两个裸下拉框 + 一张曲线，没有任何摘要数字，想读最高/最低/平均只能用眼睛从曲线上量。

**改动（三个文件，全在 App 层）**：

1. `Views/StatsPage.xaml` 重排为 4 行：标题栏（标题 + 副标题 + 刷新按钮）→ 筛选卡片
   （`设备` 下拉 + **时间范围按钮组**）→ **4 张指标摘要卡** → 曲线卡片。对齐设置页/设备页的卡片语言。
   - 时间范围从 `ComboBox` 换成三个 `ToggleButton`（24 小时 / 7 天 / 30 天），
     `Range_Click` 里 `IsChecked = ReferenceEquals(tb, clicked)` 做互斥（点击本身会取反，强制点回选中态）。
   - **指标卡**：当前电量（带状态色）、区间最高、区间最低、平均电量，各带一行小字（时间 / 掉电点数）。
     "当前电量"优先取 `Manager.Devices` 里**实时在线**读数，取不到才回退最后一条历史采样，小字标"实时/最后记录"。
   - `KpiMax`/`KpiAvg` 用 `ClearValue(TextBlock.ForegroundProperty)` 恢复主题默认色，
     避免"有数据时染过色、没数据时洗不掉"。
2. `Views/BatteryChartControl.xaml.cs` 增强：
   - **面积填充**（折线到基线的闭合 `Polygon`，alpha 38）+ 采样点 ≤40 时补圆点。
   - **hover 提示**：`PointerMoved` 找全局最近采样点 → 竖虚线 + 高亮圆点 + 深色气泡（时间 + 电量%）。
     关键：控件 `Background` 必须设成**非 null 的透明刷**，否则空白处收不到指针事件，hover 只在曲线附近才触发。
     气泡宽度用 `Border.Measure(...)` 实测 `DesiredSize` 决定位置（替代原"每字符约 7px"估算）。
   - Y 网格改 0/20/40/60/80/100（比 25 档细）；X 轴档数按 `_plotW` 动态取 3~6，
     太窄时标签退化成 `HH:mm`；**单系列隐藏图例**（与设备下拉框完全重复）。
   - 空状态改成图标 + 主副文案（对齐设备页 EmptyState 风格）。
   - **修一个潜在 bug**：网格/轴文字色来自 `ThemeResource`，原来只在 `SizeChanged` 重绘 →
     切换明暗主题后图表颜色不刷新。现在订阅 `ActualThemeChanged` 重绘。
3. 刷新按钮复用 `Manager.RequestPollAsync()` + 450ms 最小可见时长（与设备页同一套反馈约定）。

**验证**：`dotnet build VigilWatt.sln -c Debug` 0 错误（2 个警告是原有的 System.Drawing.Common 版本冲突）；
`dotnet test` **50/50**（Core 未动）；`dotnet publish -c Release` 已出 `src/publish`。
XAML 合法性由构建背书（`obj/.../Views/StatsPage.g.cs` 已生成 `Range24`/`KpiCurrent`/`SubtitleText`，
`ToggleButton` 解析到 `Controls.Primitives`）——XamlCompiler 对写错的属性名会静默失败，构建通过即属性名无误。

**未做**（本轮方案里列为"可选"、用户未选）：多设备对比曲线、导出 CSV、充电时段背景标注。

### 2026-10-06（傍晚）第五轮：设备发现提速 —— "键盘出来了、鼠标不出来"的根因是 3 分钟冷却

用户复报：**"设备检测还是有点慢，我识别到了键盘，但鼠标并没有识别到"**。
翻 app.log 的毫秒时间戳，时间线非常清楚：

| 时刻 | 事件 |
|---|---|
| 16:13:45.06 | 启动 |
| 16:13:51.72 | 迈从键盘认领（+6.7s，排在北通 3 个接口 × 2s 的探测后面） |
| 16:13:52~56 | 雷柏探测窗口（被动听 5s）**落空** → 失败冷却 **3 分钟** |
| 16:16:56 | 冷却到期，但主轮询的相位正好差 11 秒错过（16:16:45 那一拍） |
| 16:17:54 | 才认领鼠标 —— **启动后 4 分 09 秒** |

三个根因（按影响排序）：

1. **雷柏 `ProbeCooldown` = 3 分钟（致命）**。该插件纯被动零写入，探测失败**不是错误**，只是"鼠标睡着了"
   （dongle 休眠时全静音）。而用户一动鼠标就唤醒它，应用却在冷却里"不看了"。
   → 改成 **5 秒**；并新增 `SupportsFastDiscovery`，由 DeviceManager 用 **15 秒**节拍单独跑
   "只认领、不轮询、不碰 BLE、不发任何帧"的发现（`DiscoverOnlyAsync`，与主轮询共用信号量闸门）。
2. **北通 T9 状态流识别回归（顺手挖出来的第二个 bug）**。`BetopCodec.ParseT9Status` 把 `byte[1] == 0x02`
   当帧头，而当天下午真机帧变成了 `04 **0C** 80 80 … 58`。统计日志里 81 条 Col04 帧：
   `byte[1] ∈ {0x02, 0x0C}`、`byte[23] ∈ {80, 88}`，其余字节完全一致 —— **byte[1] 是可变状态位**。
   后果有两层：手柄从设备列表消失；而且北通排在最前，每次轮询要为 3 个候选接口白等 6 秒
   （**键盘因此被拖慢 6 秒**，这正是第 1 条时间线里键盘 +6.7s 的来源）。
   → 识别特征只取稳定的 **report ID 0x04**（Col05 是 0x05、Col01 是 0x01，天然排除）；单测加 T7/T8（真机 0x0C 帧）。
3. **冷却与轮询周期的相位坑（普遍性）**。`ProbeCooldown` 若等于/接近 60 秒，失败发生在轮询开始后几秒处，
   下一拍只剩 5x 秒 → 判"冷却未过"整轮跳过 → 重试间隔翻倍。北通的 1 分钟就是这么变成 2 分钟的。
   → 基类默认 1 分钟 → **30 秒**（= "每一轮都允许重探"），规则写进注释与 code-flow §2。

顺带（性能/IO）：`HidDeviceScanner` 每次扫描都 dump 24 个接口 + 24 行元数据（≈50 行），而扫描现在是
每分钟 1 次 + 快速发现每 15 秒 1 次 —— 旧日志 725KB 里绝大部分就是这些重复行。改成
**设备集合指纹变化时才 dump**（插拔/首次枚举的明细照旧完整落盘，排错信息不丢）。

**关键实测数据（留档）**：
- 雷柏 dongle 心跳间隔用 hidapi 实测 = **3.04 秒**（很稳，连续 9 帧零抖动）→ 5 秒探测窗口是 1.6× 余量。
- 修复后（16:28:54 启动）：**北通 T9 73% 于 +4.7s、迈从键盘 65% 于 +5.1s、雷柏鼠标 98% 于 +5.5s**，
  三个设备全部到齐；对照修复前是"键盘 +6.7s、鼠标 +4m09s、北通完全缺席"。
- 稳定性：60 秒主轮询准点运行（16:29:59 / 16:30:59 / 16:31:59 秒不差），三个设备各认领**恰好一次**
  （快速发现节拍不会重复认领/误标离线），0 条 ERR。

**验证**：`dotnet build VigilWatt.sln -c Debug` 0 错误；`dotnet test` **50/50**（新增 T7/T8）；
`dotnet publish src/VigilWatt.App -c Release -p:Platform=x64 -o src/publish` 成功。

> 两个环境提醒：① 首次 Release 发布会失败（`StatsPage.xaml.cs` 报 `RangeCombo` 不存在）——
> 那是**另一个编辑器正在改 `StatsPage.xaml/.xaml.cs`** 造成的瞬时不一致（文件 mtime 就在发布那一刻），
> 不是本轮改动；等文件写稳后重试即通过。② 用 shell 拉起 `VigilWatt.App.exe` 会在该次命令返回后
> 几秒被进程树清理带走（**没有崩溃转储、没有 WER 报告、日志里也没有异常**）——
> 想让它长驻就用后台任务方式启动，或直接双击 `src/publish/VigilWatt.App.exe`。

### 2026-10-06（晚）第七轮：新增「历史管理」页（按设备删除历史）

用户要求"一个可以进行删除历史的管理页面"。方案确认后按 **独立导航项 + 只做按设备删除（含批量勾选）+ 加单测** 实现。

**Core（原来 `HistoryStore` 只有读，没有任何删除入口）**：
- `Storage/HistoryStore.cs` 新增：`HistoryStats GetStats()`（总条数/设备数/最早/最新/文件字节）、
  `List<DeviceHistoryStat> GetDeviceStats()`（每设备条数/名称/最早/最新）、
  `int DeleteDevices(IReadOnlyCollection<string>)`（IN 列表**按数量生成占位符 + 逐个绑定参数**，绝不拼 SQL）、
  `long FileBytes()`、`void Compact()`（VACUUM）。全部走已有的 `_writeGate` 锁——
  后台每分钟 `SafeSample` 在并发写库，`VACUUM` 尤其不能与别的语句并发。
- `HistoryStore` 构造加可选 `dbPath` 参数（null = 默认 `%LOCALAPPDATA%\VigilWatt\history.db`），供单测指向临时库。
- `Utils/Log.cs` 新增 `RedirectTo(string logDir)`：**没有它单测会往真实 `app.log` 写脏数据**。
  `LogDir`/`LogPath` 从 `static readonly` 改成可变静态字段。
- `Utils/ByteSize.cs`（新）：字节 → `"1.24 MB"`，纯函数。

**UI**：
- 新增 `Views/HistoryPage.xaml(.cs)` + `ViewModels/HistoryDeviceRow.cs`。
  布局：标题栏 → InfoBar（操作结果）→ 4 张概览卡（库大小/总条数/设备数/最早记录）→ 按设备清理卡片
  （CheckBox + 名称 + 条数 + 时间范围 + 单行删除；底部"已选 N 台 · 将删除 M 条" + 批量删除按钮）。
- `MainWindow` 加第 4 项导航「历史管理」（Tag=history，图标 E74D）+ 路由。
- 删除流程：ContentDialog 二次确认（写明设备/时间范围/条数/不可恢复）→ `DeleteDevices` → **`Compact()`** → InfoBar 报"删除 N 条，释放 X"。
- **HistoryPage 刻意不订阅 `DevicesChanged`**：后台每分钟采样会重建列表、打断用户勾选（StatsPage 踩过同款坑），数据陈旧靠手动刷新。

**踩到的三个坑（可复用）**：
1. **WinUI `CheckBox` 默认 `MinWidth=120`**，放进列表行会在名字前留一大段空白，必须显式 `MinWidth="0"`。
2. **不要用双向绑定读勾选状态**：`Checked`/`Unchecked` 事件回调与 x:Bind 写回的先后顺序没有保证，
   所以行模型不用 `IsChecked="{x:Bind IsSelected, Mode=TwoWay}"`，而是**在事件里直接读 `CheckBox.IsChecked`** 写回模型。
3. **测试造数据别逐条 `Insert`**：`HistoryStore.Insert` 每条一次隐式事务（一次 fsync），
   H9/H10 原本各插 200/500 行，测试套件从 20ms 涨到 **53s**；行数压到 30/60 后回到 **9s**。

**测试**：测试项目引入 `Microsoft.Data.Sqlite`，用 `<Compile Include>` 链接
`Storage/HistoryStore.cs` + `Utils/AppPaths.cs` + `Utils/ByteSize.cs` + `Utils/Log.cs` + `Models/DeviceStatus.cs`
（这几个文件虽碰 SQLite 但**都不碰 WinRT**，所以纯 net8.0 测试项目仍能编译，且真跑 SQLite）。
新增 `HistoryStoreTests`（H1–H10：空库统计、计数与时间范围、无读数不写库、按设备聚合、只删指定设备、批量删、空集合幂等、删不存在、VACUUM 后数据完好、全清后文件不膨胀）+ `ByteSizeTests`（B1–B5）。
**测试总数 50 → 65**。

**验证**：`dotnet build VigilWatt.sln -c Debug` 0 错误（唯一新增警告是 `BluetoothClassicWatcher.cs:134` 的既有 CS8604，与本次无关）；`dotnet test` **65/65**。

### 2026-10-06（傍晚）第八轮：两个"看起来像功能缺失"的 Bug —— 程序图标 + 托盘提示不刷新

用户报两条：① 没有程序图标；② 最小化后哪怕有设备在线，托盘也显示"无设备在线"。**两条都不是逻辑写错，是"从来没人做/只做了一次"**。

**① 程序图标：工程里压根没有图标。** `VigilWatt.App.csproj` 没有 `<ApplicationIcon>`，仓库里也没有任何 `.ico`、没有 `Assets/` 目录——PE 里就没有 `RT_GROUP_ICON`，所以是默认空白图标。
- 新增 `scripts/make-app-icon.py`（**纯标准库，不依赖 Pillow**）：解析式绘制绿色圆角方块 + 白色闪电（watt），
  每像素 4×4（大尺寸 3×3）超采样抗锯齿，直接编码 ICO（16/20/24/32/40/48/64/128/256 九档；≥64 用 PNG 条目，其余 32 位 DIB）。
  `--preview x.png` 出放大预览图——**判断 16px 档还认不认得出，就靠这张图**。
  > 弯路：第一版画的是"电池外框 + 电量条"，但外框和填充同色、中间那道背景色窗口让它看着像便签纸，
  > 小尺寸更糊。换成闪电后 16px 依然清晰。
- 接入两处：csproj 的 `<ApplicationIcon>`（写进 exe 资源，给资源管理器/任务栏/Alt-Tab 用）
  + `MainWindow` 构造里的 `AppWindow.SetIcon(...)`（**非打包 WinUI 有时不继承 exe 图标资源**，只做前者任务栏还是空白方块）。
- **验证不靠肉眼**：把 ico 每个尺寸的载荷拿去 `bytes.count()` 在 exe 里数——9 档全部命中即证明资源已嵌入。

**② 托盘提示不刷新：`UpdateIcon()` 只在启动那一刻跑过一次。**
启动时 `DeviceManager.Start()` 已经调了但首轮扫描还没开始（`_pollTimer` 首次是 300ms 后），
`Manager.Devices` 是空的 → 渲染出「暂无在线设备」→ 之后**再没有任何地方调用 `UpdateIcon`**，提示被永久定格。
（所以严格说不是"最小化后才错"，是"从启动起就一直是这个值，最小化后才被看见"。）
- 修法：`App.AttachTray` 里挂 `Manager.DeviceUpdated` + `Manager.DevicesChanged` → 新增的 `TrayService.RequestUpdate()`。
  两个事件都要订：`DeviceUpdated` 管电量/在线**变化**，`DevicesChanged` 管设备**增删**，缺一个就漏一半。
- `RequestUpdate()` 内做 **合并**（`Interlocked.Exchange` 标志位——一轮轮询会连打十几次 `Upsert`）
  + **回 UI 线程**（`MainWindow.DispatcherQueue.TryEnqueue`，与其它 UI 更新同线程，也避免和 `Dispose` 抢 `_currentHIcon`）；
  `DispatcherQueue` 还没就绪时就地渲染兜底。
- 顺带在 `UpdateIcon` 里加"**提示变了才记日志**"（`_lastTip` 比对）：轮询期全打会冲爆日志，
  但"提示到底变没变"正是这类问题唯一想知道的信息。**实测**：`17:07:49 暂无在线设备` → `17:07:56 北通 T9 67%`，
  随后迈从 88%、雷柏 98% 认领，提示仍停在 67%（= 三者最低），行为正确。

**验证**：`dotnet build VigilWatt.sln -c Debug` 0 错误、**仅 2 个既有警告**（CS8604 + System.Drawing.Common 的 MSB3277，无新增）；
`dotnet test` **65/65**；发布 `src/publish` 17:07，运行实测托盘随设备认领实时刷新、日志无新增 ERR。

> 操作提醒：`src/publish` 被运行中的实例锁着会导致 publish 失败（MSB3027/3021）——
> 必须先关掉应用（**`taskkill /F` 会粘连迈从 dongle 会话约 2 分钟，属已知现象**，见 safety.md §2.2）。

### 2026-10-06（晚）第九轮：北通手柄关了还显示在线 —— T9 的 `0x50` 是"未连接"占位帧

用户报：**「检查下 北通真的还在线吗？我设备已经关闭了 只有连接器还在电脑上」** ——
手柄关机、只剩 dongle 插着，列表里「北通 T9」仍显示 **67% 在线**。

**根因**：**手柄关机后 dongle 不会停止推流**，而是继续每轮推同一帧、只把电量字节换成固定的 `0x50`。
旧 `ParseT9Status` 照单全收（`0x50` 按 `raw/120` 换算 = 67%）→ 手柄明明关了还报在线。

**取证**：翻 app.log 全部 Col04 帧，四段状态一次例外都没有 ——

| 时刻 | byte[1] | byte[23] | 现场 |
|---|---|---|---|
| 15:13~15:18 | `0x02` | `0x58` | 手柄开机 |
| 15:19~15:30 | `0x02` | `0x50` | 手柄休眠 |
| 15:31~17:02 | `0x0C` | `0x58` | 手柄开机（73%） |
| 17:03~17:12+ | `0x04` | `0x50` | **手柄关机，只剩 dongle** |

→ **`byte23 == 0x50` ⟺ 手柄没连上**；`byte[1]` 在 `{0x02,0x04,0x0C}` 之间乱变，不能当判据。

**为什么只认 `0x50`、不按"低位=标志位"猜**：`0x50`/`0x58` 只差最低位，两种自洽读法（等级×8 / 高 4 位等级+低位标志）
下 `0x50` 都只可能是"没连上"，认它是零风险；而按"低位=标志"判，若实际是等级×8，则所有偶数等级都会被误判离线。
**宁可漏判、不可误判。** 留待验证：充满（`0x78`）后关机若报 `0x70`，则标志位读法成立、可一般化。

**修法**（`BetopCodec` + `BetopPlugin` + 基类 1 行）：
- `ParseT9Status` 返回 `T9Status(Percent, Charging, Linked)`；占位帧 `Linked=false`。
- **探测阶段"算成功但电量留空"**（不是判失败）：基类据此认领设备，并按新增规则
  `IsOnline = percent.HasValue`（`BatteryPluginBase.CommitProbes`）标**离线** → 列表显示「北通 T9 · 离线 · --%」。
- **轮询阶段** `RequestOffline(dev)` 立即标离线（不等 3 次失败 = 3 分钟）；手柄再开机下一轮自动回在线。
- ⚠ **"探测算成功"是为了如实列出「离线」**（而不是让设备从列表消失），**不是为了省时间**：
  基类的 toProbe 列表在任何探测之前就收集完了，同一轮里其它候选接口照旧会被探测。
  实测三个设备仍在 +7s 内全部到齐。
- **顺手澄清一个此前记错的结论**：北通"每次轮询白等 4~6 秒"的根因**不是**"T9 流没被识别"，
  而是 `IsCandidate` 放行了该 dongle 的 3 个非电量接口（`0x0001` 配置流 / `0x0003` 摇杆流 / `5075` 全零帧），
  每个白等一个 2 秒窗口 —— 旧版日志（17:19:30）与新版日志（17:29:40）**同样是 `05 → 00 → 04` 三段**，
  说明它是**既有开销、与本轮改动无关**，上一轮的"已修复"只是让第 4 个接口的探测能立刻命中。
  要真正消除需收敛 `IsCandidate`（电量通道只在 `usage 0x0002/0x0003`）或给失败路径加负缓存 —— **本轮为控风险未动**。
- 单测：T2/T7 改为断言 `Linked=false`，新增 T9（`byte1=0x04` 真机帧）、T10（**只认 `0x50`，其余偶数等级不得误判离线**）。
  **测试总数 65 → 67**，`dotnet test` 全绿；`dotnet build VigilWatt.sln -c Debug` 0 错误、无新增警告。

**实测（17:25 发布版）**：
```
17:25:40 北通 T9 手柄未连接（dongle 占位帧 byte23=0x50，手柄已关机/休眠；dongle 仍在）: 04 04 80 … 50
17:25:44 认领北通手柄设备: … proto=betop-status15 电量= 充电=Unknown      ← 电量空 = 离线认领
17:25:44 认领迈从设备: … 电量=91    17:25:44 认领雷柏设备: … 电量=98
17:25:44 托盘提示已更新: VigilWatt · MCHOSE 2.4G Wireless 91%            ← 托盘只统计在线设备，北通不再计入
```

> **环境教训（重要，上次的记录不准）**：本会话用 shell / 后台任务 / `cmd /c start` 拉起
> `VigilWatt.App.exe` **都会被进程树清理在 ~7 秒内带走**（日志只写到首轮扫描就断），
> 所以**"后台任务方式启动"这条并不总能长驻**。更要命的是：应用被强杀后**单实例互斥量仍由旧实例持有**，
> 新实例会静默退出，而它的日志也写不进去（旧实例以 `FileShare.Read` 占着 app.log）——
> 表现为"日志里根本没有新启动记录"，很容易误判成"新代码没生效"。
> **验证前先 `tasklist` 确认没有旧实例在跑**；要长驻请让用户双击 `src/publish/VigilWatt.App.exe`。
>
> > **2026-10-09 更正**：`app.log` 已改以 `FileShare.ReadWrite` 打开，新实例的日志**能**正常写入
> > （"日志写不进去"这条已不成立）；托盘"退出"也会先 `ReleaseMutex`，正常退出后新实例能立刻启动。
> > 只剩 `taskkill /F` 强杀会留下"互斥量被占"。详见 [hardware/debugging.md §8](../hardware/debugging.md)。

### 2026-10-08 第十轮：新增「本机电量监控」（可选）

**诉求**：把电脑（笔记本）自身的电量也纳入监控。

**用户拍板的取舍**（四问四答，实现按此为准）：
| 决策点 | 结论 |
|---|---|
| 列表 / 小组件 | **进** |
| 托盘"最低电量" | **不参与**（笔记本正常放电 30% 不该染红托盘图标） |
| 历史曲线 | **记**，进历史统计页可选 |
| 低电量提醒 | **参与**（共用同一阈值，走原有气泡通道） |
| 台式机 / 无电池 | **完全不显示**（不出占位行） |
| 类型命名 | `DeviceKind.Computer`，显示名「本机电池」，来源徽标「本机」 |
| 设置开关 | 有，`MonitorSystemBattery`，**默认关** |

**实现**（全部只读，零写入，符合硬件安全红线）：
- **新增 `Core/Power/SystemPowerReader.cs`**：P/Invoke `GetSystemPowerStatus`。**关键翻译**：
  `BatteryFlag` 128（无电池）/ 255（未知）→ `HasBattery=false`（不显示）；
  `BatteryLifePercent` 255（未知）→ `Percent=null` 但**仍算有电池**（笔记本不该因一次读不到就假装没电池）。
  翻译逻辑抽成 `internal static Interpret`，纯函数可单测。
- **新增 `Core/Power/SystemBatteryWatcher.cs`**：仿 `BluetoothClassicWatcher` 的事件式数据源（**不实现 `IBatteryPlugin`**，
  系统电量没有接口可认领）。固定 `DeviceId = "sys:battery"`（历史曲线连续性靠它）。
  充电推导 `DeriveCharging`：接电+100%=`Full`，接电未满=`Charging`，未接电=`Discharging`，读不到=`Unknown`。
- **`DeviceManager`**：构造订阅 `_system.DeviceUpdated += Upsert`；`PollOnceAsync` 尾部 `if (_settings.MonitorSystemBattery) _system.Refresh();`；
  `SourceFromId` 加 `sys:` → `DeviceSource.System`；`ApplySettings` 新增 `ApplySystemBatterySetting()`（关掉时撤条目，同 `DropDevicesOf` 理由）；
  `RestoreOfflineTombstones` 跳过它（开关关着时历史记录不该复活它）。
- **`DeviceSource` 加 `System`，`DeviceKind` 加 `Computer`**；`DeviceRowModel`（徽标「本机」+ 字形 `\uE7F8`）、
  `WidgetWindow.Glyph`（显示器形状）、`SettingsPage`（「本机」分节 + 开关）配套。
- **`TrayService.Render` 加 `if (d.Source == DeviceSource.System) continue;`** —— 这是用户明确要的取舍。

**⚠️ 本轮踩的坑（已写进 architecture.md §6）**：新建的命名空间**不能叫 `VigilWatt.Core.System`**——
它会遮蔽 BCL 的 `System`，让 `DeviceManager.cs` 里的 `System.Threading.Timer` 和 `ElanHeadsetPlugin.cs` 里的
`System.Text.*` 全部编译失败（CS0234）。**改用 `VigilWatt.Core.Power`**（目录也改为 `Power/`）。

**真机验证（本机 = 有电池笔记本，插着电）**：
```
17:52:20 [INFO] 系统电量已接入: 本机电池 100% Full
17:52:19 [INFO] 托盘提示已更新: VigilWatt · Dell WK717Keyboard 76%   ← 托盘未受本机电量干扰 ✓
```
独立探针（Python ctypes 复核）：`ACLineStatus=1 / BatteryFlag=1 / BatteryLifePercent=100`，与上面一致 ✓。
**未能在本机观察到历史采样落库**：应用在此环境活不过 1 分钟（见上方"环境教训"），采样定时器首跑在 1 分钟后。
采样路径本身是既有代码（只多了一个 `IsOnline && BatteryPercent.HasValue` 都满足的条目），**建议下次长驻运行时确认 `history.db` 里 `sys:battery` 有采样**。

**测试 67 → 119**（新增 `SystemPowerReaderTests` 45 个 + `SystemBatteryChargingTests` 7 个；
前者含 `Theory` 展开 5 个）。`dotnet test` 全绿，`dotnet build VigilWatt.sln -c Debug` 0 错误。

### 2026-10-08 第十一轮：本地打包发布 1.0.2（不推 GitHub）+ 修 Release 构建坑

**背景**：用户要"本地发布"，明确**不推 GitHub**（`ACoolOctober` 账号被 GitHub 停用，API 返回
`Sorry. Your account was suspended`，`.vpk-github-token` 里的 PAT 已失效）。所以只走
`scripts/build-installer.ps1` 出本地安装包，**不走** `publish-github.ps1`。

**版本号决策**：csproj 已是 `1.0.2`，`dist/` 里也已有 1.0.2 产物，但——**那份是旧代码**。
判别手法（可复用）：解包 `dist/*-full.nupkg` 里的 `lib/app/VigilWatt.Core.dll`，用
`grep -c -a SystemPowerReader` 找符号；旧包只有 `MchosePlugin`/`BetopPlugin`，
**没有** `SystemPowerReader`/`SystemBatteryWatcher`，即第十轮的「本机电量监控」没打进去。
用户选择**仍用 1.0.2**（不 bump），直接重打覆盖。

**踩到的坑（本轮最大收获）：Release 打包 `CS0227: 不安全代码…` + XamlCompiler `MSB3073`**。
`dotnet publish -c Release` 时 WinRT AOT 源生成器生成的 `WinRTGenericInstantiation.g.cs` 含
`unsafe` 代码，工程没开 `<AllowUnsafeBlocks>` → 一屏 CS0227，XamlCompiler pass1 中间项目编译失败
→ `MSB3073 退出码 1`。**表象与 build.md §1「XAML 属性名写错」完全相同**，别被带偏：
**先有 CS0227 才是本坑**，只有 MSB3073 无 CS 错误才是 §1。Debug 不启用该生成器故从不报错
（`dotnet build -c Debug` 一直 0 错误，极易误判"工程没问题"）。
修法 = `VigilWatt.App.csproj` 加一行 `<AllowUnsafeBlocks>true</AllowUnsafeBlocks>`（已加）。
详见 build.md §4。

**重打同版本的注意**：`vpk` 不允许同版本重复 pack（build.md §8.3），所以先把 `dist/` 里
1.0.2 的产物（full/delta/Setup/Portable/RELEASES/*.json）删掉，**只留 1.0.1-full.nupkg 作增量基线**
再跑脚本——这样 vpk 会自动生成新的 `1.0.1 -> 1.0.2` delta（本次 5.0MB，patch 9 个文件）。
若把整个 dist 清空则拿不到 delta（只能出全量包）。

**产物（`dist/`，2026-10-08 20:52）**：`VigilWattApp-win-Setup.exe`（44.2MB，当前用户安装、无 UAC）、
`VigilWattApp-win-Portable.zip`（36.7MB，免安装）、`VigilWattApp-1.0.2-full.nupkg`（36.7MB）、
`VigilWattApp-1.0.2-delta.nupkg`（5.0MB）、`RELEASES`、`assets.win.json`、`releases.win.json`；
`publish/`（仓库根）出 self-contained 版 `VigilWatt.App.exe`。
**验证**：新 full.nupkg 与 `publish/VigilWatt.Core.dll` 均含 `SystemPowerReader`/`SystemBatteryWatcher` ✓。
**未签名**（SmartScreen 会提示"未知发布者"）。

**顺带记录**：`git status` 显示本地 `main` **领先 origin/main 5 个提交**，仍未推送（账号停用期间搁置）。

### 2026-10-08 第十二轮：两个"设备在线判定"问题（用户复报）

**① 雷柏鼠标"一直在用却显示掉线"** —— 阈值太紧，不是硬件问题。

- 日志实测 0x07 状态帧是**活动驱动**的心跳：动鼠标稳定 3.04s 一帧，一停手 dongle 就静音，
  正常空档里 13~39 秒很常见（4 分钟内出现 5 次 >10s）。`PassiveSilenceOffline = 12s` 落在空档分布
  **内部**，任何一次 60 秒轮询（或手动刷新/改设置触发的补跑）撞进去就把鼠标标离线
  —— 用户 20:59:38 看到的那次，实际只沉默 **13.3 秒**。
- 修法：`BatteryPluginBase.StartPassiveListen` 改成**收到任何报文都刷新 `LastPassiveAt`**
  （不再只认能解析出 0x07 的帧）；`RapooPlugin.PassiveSilenceOffline` **12s → 90s**（降级为兜底）。
- 影响面：只有雷柏会走到沉默分支（北通/迈从的阈值本就是 `TimeSpan.Zero`）。
- 验证：运行会话内出现 2 次 >12s 空档（最大 16.2s），`标记离线` / `重新上线` 各 **0** 次。

**② 迈从键盘"开了要等一会儿才显示在线"** —— 发现节拍只覆盖零写入插件。

- `DiscoverOnlyAsync`（15 秒节拍）原来只跑 `SupportsFastDiscovery` 的插件（只有雷柏，纯被动零写入）；
  迈从探测要写帧，按 safety.md 规则 4 被排除 → **只吃 60 秒主轮询** → 键盘唤醒/开机后最坏等
  ~60s + ~4s 探测。实测 21:16 那次：21:16:25 启动 → 探测 #1/#2 连续无响应 →
  21:18:15 用户点「刷新」才认领成功，**110 秒**（期间列表里显示的是历史补出来的"离线"占位）。
- 修法（用户批准）：新增 `IBatteryPlugin.SupportsDiscoveryProbe` —— 探测写帧、但**写入频率由插件
  自己的 `ProbeCooldown` 兜住**的插件也可参与 15 秒节拍。迈从置 true，`ProbeCooldown` 设 **10 秒**
  （一次探测本身约 3.7s，`10 + 3.7 < 15` 才能每拍都重探，否则被相位吃掉变成 30 秒）。
  已认领后 `Attach` 对已知设备直接返回 → 这条路径**零写入**；代价只出现在**设备不响应**期间
  （最坏 ≈3 帧/次 × 4 次/分钟 ≈ 12 帧/分钟）。
- 安全口径已写进 **safety.md 规则 4 的例外**：发帧内容不变，仍然只有官方/实测的会话帧与 0x4A 电量查询，
  **没有引入任何新命令**。
- **遗留**：21:15 那个实例是被 `cmd start` 拉起后遭进程树清理**强杀**的，按 safety.md §2 可能把
  dongle 会话占死，所以上面那两次"无响应"未必是键盘在睡。**需要一次干净的复现**（拔插 dongle 后再量时延）。

### 2026-10-08 第十三轮：雷柏离线判定改用"设备自己说断链"（用户提问引出）

**起因**：复盘第十二轮时发现断电重连那一帧带 `RF=0x7F(bit7连接=False)`，用户问"不能被动得到这个信息吗"。

**考证结论**：这个信息**本来就是被动拿到的**，只是被丢掉了。
- `RapooPlugin` 的解析早已取出 `[18].bit7` 并打进日志——那行日志走的就是**被动监听路径**（零写入、零探测）；
- 但 `TryParsePassiveInput` 硬编码 `Offline: false`，bit7 到这就断了；
- 基类 `BatteryPluginBase` 的"被动帧告知离线"通道**早就铺好**（`offline=true` → `RequestOffline` +
  立即标离线 + `RaiseStatusPushed`），参照实现是北通 T9 占位帧。

**方案（用户选 B：延迟确认）**——不直接判离线：`bit7` 是 **RF 链路**状态，比"设备在不在"
敏感得多（关机 / 移出范围 / 受干扰都会翻 0），直接判会让列表闪。改成"标记候选 + 等确认窗口"：
真关机没有恢复帧 → 窗口到点后翻离线（比 90 秒兜底快一个数量级）；抖动 → 下一帧 bit7=1 撤销候选。
> 窗口最初定 12 秒，**第十四轮按用户要求收紧为 6 秒**，参数也从散落的虚属性收敛进 `OfflinePolicy`。

**改动**
- **基类**：新增 `PassiveOfflineConfirm`（默认 `TimeSpan.Zero` = 不启用）、`MarkOfflineCandidate` /
  `ClearOfflineCandidate`（幂等）、`TrackedDevice.OfflineCandidateSince`。
  **检查点放在被动监听循环的空闲 tick（约 500ms 一次）**，不放 60 秒轮询——否则"12 秒确认"会退化成
  "12 + 最多 60 秒"。
- **新增** `Plugins/PassiveOfflinePolicy.cs`：纯函数 `IsExpired`（零依赖，可单测）。
- **新增** `Plugins/Rapoo/RapooCodec.cs`：把状态帧解析抽成纯函数编解码（对齐 `MchoseV1Codec` /
  `BetopCodec` 惯例），新增 `RapooFrameKind` 三态（`NotStatus` / `Status` / `StatusInvalidPercent`）。
- **`RapooPlugin`**：`PassiveOfflineConfirm = 12s`；`TryParsePassiveInput` 里 bit7=0 标记候选、bit7=1 撤销
  ——**放在电量越界判断之前**，因为链路位与电量无关（越界帧同样要能标记/撤销）。

**验证**：build **0 错误**（3 个既有警告）；test **139/139**
（+20：`RapooCodecTests` 13 例 + `PassiveOfflinePolicyTests` 7 例，用例帧全部取自真机日志）。

**真机验证（2026-10-08 晚，用户复报后）**：雷柏 ✓ ——"连接很快"，两次 `RF=0x7F` → `标记离线`
分别耗时 **12.4s / 12.2s**，恢复帧一到立刻"设备重新上线"，列表不闪。
迈从 ✗ ——"一直没反应"，查出**结构性根因**，见第十四轮。

**遗留**：断电重连时 `[7] 充电标志` 由 `0x01` 变 `0x00`、电量 94%→95%，`0x00` 的含义待确认
（此前只校准过 `0x01`=放电 / `0x02`=充电）。

### 2026-10-08 第十四轮：迈从"关机后永不显示离线"的真凶 + 离线参数收敛（用户复报 + 讨论后拍板）

**① 真凶：`SupportsPassiveListen` 的语义过载**（不是"慢"，是**永远不会**）

一个布尔量被当成两个含义用："我起后台被动监听"（**机制事实**，正确）+ "所以轮询返回 null 不算失败"
（**策略推断**，错误）。后果：迈从的 `SupportsPassiveListen = true` 让它走进 `Poll()` 的
`else if (SupportsPassiveListen)` 分支，而 `PassiveSilenceOffline = TimeSpan.Zero` 让分支内的 if 不成立
→ **什么都不做**；含 `dev.ConsecutiveFailures++` 的那个 `else` 对迈从**永远不可达**
→ 失败计数恒为 0 → 永远到不了 3 次 → 既不标离线也不熔断。旧代码注释"真正的失联由基类连续 3 次查询失败
兜底标离线"是**错误假设**（第十三轮已修正注释，第十四轮才真正修好）。

日志佐证：键盘关机后状态通知 `0x00`(21:51:52) → `0x01`(21:52:53) 跨 61 秒，全程 **0 次**离线标记。

**修法**：把两件事拆开。基类新增 `OfflinePolicy.CountPollFailures`（默认 true = 传统查询型插件）；
迈从置 false，改由 `PollDevice` 在**发过帧**的分支自己调 `ReportPollFailure` / `ReportPollAnswered`
（只有插件知道这一轮到底发帧了没有——`null` 与"用户关了写入许可 / 熔断停写中"不可区分）。

**② 第③层参数收敛成 `OfflinePolicy`（用户拍板）**

起因是用户问"为什么这些不设置成统一的？你好像把很多东西都弄成了各品牌的专有了"。
盘出插件配置其实分三层：① 协议事实（帧结构 / 有没有查询命令 / 有没有连接状态字段）
② 安全声明（要不要写帧、用户开关、探测冷却）——这两层**必须** per-brand，因为物理上就是三种不同东西
（北通占位帧 / 雷柏 `bit7` / 迈从根本没有连接状态字段）；③ **离线判定策略**——机制同一套，
只是参数不同，**该收敛**。

- **新增** `Plugins/OfflinePolicy.cs`：`record`，四个参数 `Silence` / `Confirm` / `MaxFailures` /
  `CountPollFailures`，外加纯函数 `IsConfirmExpired`。每插件只写一行
  `protected override OfflinePolicy Offline { get; } = new() { … };`。
- **删除** `PassiveOfflinePolicy.cs`（其 `IsExpired` 并入 `OfflinePolicy`）与对应的
  `PassiveSilenceOffline` / `PassiveOfflineConfirm` 两个虚属性、`MaxConsecutiveFailures` 常量。
- 基类新增私有 `MarkOffline(dev, reason)` 作为**所有离线路径的唯一落地点**（幂等：已离线则无操作），
  以及 `ConsumeOfflineRequest`（`RequestOffline` 的消费端，`Poll` 与"立刻核实"共用）。
- 各插件取值：雷柏 `Confirm=6s, Silence=90s, CountPollFailures=false`；
  迈从 `Silence=0, CountPollFailures=false`；北通 `Silence=0, CountPollFailures=false`；Elan 全默认。

**③ 雷柏断联窗口 12s → 6s（用户拍板）**

依据：心跳间隔实测 3.04s，6s ≈ 2 个周期。用户提过 3 秒，被否——3s 只等于 1 个周期，
正常丢一帧就会误判；且鼠标停手时 dongle 本来就会静音，"断链后一直没有新帧"与"停手静音"
在被动路径上无法区分。

**④ 迈从"关机后立刻定性"：`0x00` 触发核实（用户拍板）**

`0x0A payload=0x00` 语义**至今未定论**（与 `0x01` 全天交替、之后设备都能正常应答查询），
所以**不能当离线证据**——但可以当**触发器**：基类新增 `RequestConfirm(dev)`，插件在被动帧里调它，
基类在**空闲 tick（约 500ms）**上跑一次与轮询同款的 `PollDevice` 做核实。
怎么知道"插件确实发了帧却没应答"：看 `ConsecutiveFailures` 有没有被插件推高（插件没发帧就不会报账）
——推高就当场判离线，**不等 3 次**；这条路径**不触发熔断停写**（一次核实失败不足以停写 10 分钟）。
效果：键盘真关机时，发现延迟从"60 秒轮询攒 3 次失败 ≈ 2~3 分钟"压到**秒级**。

**验证**：build **0 错误**；test **143/143**（`OfflinePolicyTests` 11 例，替换原 7 例）。

**真机验证（2026-10-08 22:11~22:14 会话，用户确认"看着比较正常了"）**——两条链路都跑通：

| 设备 | 触发 | 判定离线 | 耗时 | 恢复 |
|---|---|---|---|---|
| 迈从 | 22:13:33.322 收到 `0A payload=0x00` → 触发核实 | 22:13:35.070 核实查询无应答 | **1.75s**（原 2~3 分钟） | 22:13:46.488 键盘开机即上线 |
| 雷柏 | 22:13:37.094 收到 `RF=0x7F`（bit7=0）→ 标记候选 | 22:13:43.211 候选持续 6s | **6.12s**（12s 窗口时实测 12.4/12.2s） | 22:13:49.493 恢复帧 `RF=0xD1` 即上线 |

全程 `标记离线` / `判定离线` 各 **1 次**（都对应真实关机）、`重新上线` **2 次**、
`读数异常已过滤` **0 次**——**没有误判、没有闪烁**。雷柏恢复时候选已被撤销，列表不闪。

**顺带拿到一条新证据（第一次支持"0x00=链路断"）**：键盘**关机**那一刻来的正是 `0x00`，
**开机**那一刻来的正是 `0x01`，与电源状态严格对应。这与 2026-10-06"全天交替"不矛盾——
若那次交替其实是**键盘空闲休眠/唤醒**（无线键盘空闲会自己睡），则休眠也算"链路断"，
但 dongle 仍能应答缓存帧，所以"每次通知后设备都能正常应答查询"。
**待验**：让键盘自然休眠（不关电源），看是否发 `0x00`、以及那时 `4A` 查询是否仍能应答。
即便成立也**仍不该直接判离线**（休眠会闪）——现在"触发器 + 核实"的用法正好绕开这个坑。
详见 protocols.md §1.3。

**未提交；未做发行打包**（`src/publish` 已更新到本轮，应用在跑）。

**遗留**：① ~~迈从 `0x00` 触发的真机效果待验~~ **已验证（22:13，1.75 秒）**；
② `0x00/0x01` 语义仍未定论（但 22:13 那次样本与电源状态严格对应，见上）；
③ 北通"休眠时表现为离线"（类头写的预期行为）**其实不会发生**——它没有像迈从那样自报失败，
需要真机样本再决定要不要补，别盲改（已在 `BetopPlugin.Offline` 注释里留了口子）；
④ 日志噪音（**已修，2026-10-08**）：迈从 dongle 不是 QHW 通道，发现节拍每 15 秒会在 QHW 握手上撞一次
`WriteOutput 异常 … (参数错误。)`（WARN 级）。这是**预期行为**（代码里就叫"快速跳过"），但级别偏高、
看着像故障。三处一起改掉：
   - `HidDeviceHandle.WriteOutput` 新增 `quiet` 参数（预期失败不记 WARN），只给 QHW 握手用；
   - `MchosePlugin.Probe` 不再因"探测失败"清掉 `KnownProtocolByPath`（失败最常见的原因是键盘在睡，
     说明不了协议变了）——**顺带把白写的握手帧也省掉了**，写入量不再翻倍；
   - 探测期日志一律"**状态变化才记**"（`LogOnceByKey` + `ShouldLogOnce`/`ResetLogOnce`）：
     "不是 QHW 通道" / "没开写入许可" / "keyboard-v1 两次询问无响应"各只记第一次，
     恢复后清标记（再出问题会重新记一条，与北通占位帧同一记法）。
     其中"没开写入许可"那条原本是**无限刷**（关掉许可后设备永不被认领，每 15 秒一条）。

### 2026-10-09 第十一轮：日志分级与管控改造（分级 + 滚动 + 兜底 + 降噪）

**诉求**：日志没有正确分级和管控——噪音把 `app.log` 冲爆、写失败静默、没有滚动。

**方案**：先评审+补强已有方案 `doc/dev/logging.md`（修正 3 处事实错误 + 补 7 项遗漏 N1–N7），
用户确认后按三阶段实施。**本轮三阶段全部落地**（方案文档已改写为《日志设计》）。

**阶段 1 —— 级别体系 + 配置 + P0 稳定性**
- 新增 `Utils/LogLevel.cs`：`Debug < Info < Warn < Error` + `Off` 哨兵 + 名称解析（未知值回落 Info）。
- `Log` 加静态 `MinLevel`（默认 `Info`）+ `IsEnabled(level)`；`Write()` 入口先比级别（低于阈值**零 IO、零分配**）。
- `AppSettings.LogLevel` + App 启动时赋值（**重启生效**，不追求热更新）。
- 设置页新增「日志」分区：级别**下拉框**（`Services/LogLevelOptions.cs`：关闭/详细/信息/警告/错误）+ 打开目录 + 清空日志。
- `Error` 改多行友好：堆栈后续行统一加 `       | ` 缩进前缀（保住"一行一条"）。
- **噪音下沉 Info→Debug**：逐帧报文 / `[探测]` / `[Elan 探测]` / Mchose 握手簇 / HID 扫描明细等；
  协议解析**结果**仍是 Info（行尾带 `原始=<HEX>`，默认档照样能排障）。
- **[P0]** 写失败不再静默：连续 3 次失败写 `logs/log-error.txt`（30s 节流 + 64KB 上限 + 兜底自身失败即彻底放弃、不递归）。
- **[P0]** 失败后 30s 退避（修 N2 重试风暴）。
- **[P0]** 滚动改**带序号保留**：`app.log` → `app.1.log` → `app.2.log`（`File.Move(overwrite)`，不整文件删）。
- **[P0]** 按**字节**计量（`MaxBytes = 2MB`）+ `FileShare.ReadWrite` + `Log.Shutdown()` 挂**真实退出路径**。
- `LogTests` 落地（`RedirectTo` 到临时目录）；`AssemblyInfo.cs` 加 `[assembly: CollectionBehavior(DisableTestParallelization = true)]`。

**阶段 2 —— 注入 / 时间 / 测试隔离**
- **N3 注入清洗**：`Sanitize` 统一转义换行 + 控制符（C0/DEL → `\xNN`，tab 保留），无控制符走快路径零分配。
- **N4 时间基准**：每个日志文件**新建时写一行"时间基准"**（本机时区 + UTC offset，并声明 `history.db` 的 `ts` 是 UTC 毫秒），
  使日志与数据库可对齐；行首仍是**本地时间**（不动 §3.4 格式契约）。
- **N6**：测试并行禁用（阶段 1 已落地）。
- **N7**：`App.ShutdownLogging()`（`ReleaseMutex` + `Dispose` + `Log.Shutdown`，**幂等**）——
  托盘退出、单实例退出路径都调它，另注册 `AppDomain.ProcessExit` 兜底。

**阶段 3 —— 降噪下沉 + 可测性**
- `Log.InfoOnChange(key, state, message)`（状态变了才记，返回"是否变化"）+ `Log.InfoThrottled(key, message, interval)`
  ——把 `MchosePlugin.ShouldLogOnce/ResetLogOnce` 的思路泛化成公共入口。
- 迁移**纯日志用途**的两处：`TrayService._lastTip`、`HidDeviceScanner._lastSignature`（都删掉自建去重字段）；
  Betop/Mchose 里**同时承担业务语义**的状态判断**保持不动**。
- `LogConfig` 收敛**未做**：方案里就标"可选/可砍"，纯内部搬迁无可感知收益，徒增回归面（个人工具，克制）。

**验证**：`dotnet build VigilWatt.sln -c Debug` 0 错误；`dotnet test` 全绿（新增 N3/N4/InfoOnChange/InfoThrottled 用例）。
**提交**：阶段 1 = `d68b6d5 日志分级与管控改造`；阶段 2/3 见本轮提交。

## 4. 待办（按优先级，详见 experience.md §5）



1. **充电标志校准**：用户插充电线 → 看 app.log `状态字节` → 补全 `ParseKeyboardV1Charge` 映射（已知 0x10 位=充电中）
2. ~~**休眠推送帧采样**~~ **已完成（2026-10-06）**：休眠帧已实测抓到并解析清楚——`13 0A 01 00 04 **02** 01 …`，下标 5 是**类型位**（0x05=电量 / 0x02=设备状态通知），旧代码没认类型位、把 payload 的 0x01 当电量 1%，这就是误报低电量的根因。现在按类型位识别，并把 0x02 当"设备已休眠"信号**立即标离线**（`RequestOffline`，省掉 3 分钟延迟）。详见 runtime-pitfalls §12、protocols.md §1。
3. **雷柏充满态采样**：充电标志已实测校准（0x01=放电/0x02=充电，2026-10-05 插充电线确认，电量 69%→75% 期间 [7]=0x02 恒定）；**充满时 [7] 的值还没采到**——下次充满看日志 `充电标志` 字节，若是 0x03 之类补进 RapooPlugin 映射
4. ~~**时砂耳机**~~ **已结案（2026-10-06）**：2.4G 无电量通道（protocols.md §3 双重确认），且 RoseLink 逆向确认其协议内也无电量命令 —— 主机侧 + 官方 App 侧均无解，唯一途径是耳机语音播报。不再投入。
5. ~~**经典蓝牙耳机电量**~~ **已解决（2026-10-06）**：连接状态走 BluetoothClassicWatcher（纯被动零写入）；电量改读 **HFP 免手操作网关设备节点的 PnP 属性 `DEVPKEY_Device_BatteryLevel`**（`{104EA319-6EE2-4701-BD47-8DDBF425BBE5},2`），用 SetupAPI 枚举 `BTHENUM\*` 节点 + 按 MAC 对上 AEP 设备。实测 EDIFIER W820NB 60% 与设置页一致。**结论：`System.Devices.AepBattery_Level` / `Aep.Battery_Level` / `System.Devices.BatteryLevel` 都别再试了，PnP 属性是另一套体系**（runtime-pitfalls §3、code-flow §3）。剩余可做：接耳机时观察 HFP 节点电量是否实时刷新（现在是每分钟轮询 + 5 秒缓存）。
6. 新世代迈从 dongle（3837:3033）QHW 协议已实现未验证，遇到即用
7. ~~**北通手柄真机验证**~~ **已真机验证（2026-10-06 晚，北通 T9）**：修掉 report ID 硬编码根因 + 新增 T9 被动流解析（见上节）。
   剩余：**鲲鹏20（`PID_5191`）主动查询路径仍未真机验证**（本机只有 T9）。
8. **T9 `byte23` 低位语义未定论**（第九轮留的口子）：现在是"只认 `0x50` = 未连接"。
   **验证方法**：把手柄充满（`byte23=0x78`）后再关机，看 dongle 报 `0x50` 还是 `0x70` ——
   报 `0x70` 说明低位 `0x08` 是"链路有效"标志，可把判据一般化成 `(byte23 & 0x08) == 0`，
   同时电量换算要改成"高 4 位 / 7"；报 `0x50` 则维持现状（等级×8）。见 protocols.md §4.4.1。

## 5. 给下一个 AI 的注意事项

1. **开工顺序**：doc/README.md（症状速查）→ 本文件 → 按任务查专题文档。repo 文档永远比记忆新。
2. **硬件安全红线**（用户两次事故换来，最高优先级约束）：**绝不盲扫**；只发有出处的帧（官方抓包/公开文档）；写入全过 `MchoseProbingEnabled`；熔断+退避已在基类内建，别在子类绕过。
3. **dongle 静音时先查会话**：本应用 / 浏览器里的 M HUB WEB / 官方桌面 HUB 互斥，谁持有会话谁工作，崩溃进程会把会话粘死（所有人静音），kill 持有者或重插拔恢复（safety.md §2.2）。这是最容易误判成"协议错了"的坑。
4. **调试主力手法**：让官方软件和硬件说话，浏览器 monkey-patch 抓包（debugging.md §1，含选择器弹窗自动化、DPI 截图坑）；验证用 `pip install hidapi` 直测（§2）。
5. **构建**（dev/build.md）：sln 构建、从 `bin\x64\...\win-x64\` 启动；XamlCompiler 静默崩溃查 XAML 属性名；应用运行中先 taskkill。
6. **用户偏好**：中文交流、文档中文；界面只要中英文；个人自用工具，克制功能范围（不做游戏内显示/HTTP API/账号等）。
7. **验证习惯**：改完协议代码看三处——日志认领电量值 vs 键盘 FN+B 指示、history.db 采样、拔插自愈。
8. **雷柏特性**：鼠标停手 = dongle 静音（被动监听空转是预期），动一下鼠标即恢复心跳；**静音 ≠ 离线**——0x07 是活动驱动的心跳，2026-10-08 起存活判据改成"接口有没有在说话"，`OfflinePolicy.Silence = 90s` 只是兜底。**离线主判据是 `[18].bit7`（RF 断链位）**：鼠标断电瞬间 dongle 会主动吐一帧 `[18]=0x7F`，走 **6 秒**延迟确认（`OfflinePolicy.Confirm`）——但 bit7 是链路状态不是"设备在不在"，**别改成直接判离线**，抖动会让列表闪。别把静音误判成协议/会话问题。协议细节与已证伪路径见 protocols.md §3。
9. **脚本/项目存放规则**：临时调试脚本（PowerShell/Python/一次性 P/Invoke 探针）写进 `scripts/`（索引见 scripts/README.md），**用完即删**；**长期存在的 C# 项目放 `src/tests/`**（单测/诊断工具），不放 scripts/；工程源码在 `src/`。**仓库根目录只放工程文件**；临时产物用 `%TEMP%`，用完即删。别在根目录或 src/ 里散落 .ps1/.py。
10. **发现节拍**（2026-10-08）：设备"在位与否"只有探测才知道，而主轮询 60 秒一拍太慢。15 秒快速发现节拍服务两类插件——`SupportsFastDiscovery`（零写入，雷柏）与 `SupportsDiscoveryProbe`（写帧但 `ProbeCooldown` 兜住写入，迈从）。**新增会写帧的插件若要吃 15 秒节拍，必须先过 safety.md 规则 4 的例外口径**，并把 `ProbeCooldown` 压到 15 秒以下（含一次探测的耗时余量）。
11. **本机启动应用的方式**：shell / `nohup` / `cmd //c start` 拉起的进程会被进程树清理带走（约 5 秒后死，日志无退出记录）；WMI `Win32_Process Create` 被安全策略拦。**`explorer.exe "VigilWatt.App.exe"` 可用**（挂在资源管理器下，脱离工具进程树）。强杀实例会按 safety.md §2 把 dongle 会话占死，之后要重新插拔 dongle。
12. **插件配置分三层，别混（2026-10-08 第十四轮）**：① 协议事实 ② 安全声明 —— 这两层**必须** per-brand；③ **离线判定策略**集中在 `OfflinePolicy`（`Plugins/OfflinePolicy.cs`），每插件一行。**尤其注意 `CountPollFailures` 与 `SupportsPassiveListen` 是两件事**：前者回答"轮询返回 null 算不算失联"（策略），后者只说"我起后台只读线程"（机制）。曾经合成一个布尔量复用，导致迈从键盘关机后**永远**不显示离线。新增"要被动监听、但轮询空读确实是失联"的插件时，必须把 `CountPollFailures` 置 false 并在 `PollDevice` 发过帧的分支自己调 `ReportPollFailure`，否则同样的 bug 会再犯一次。
13. **日志（2026-10-09 第十一轮，完整设计见 dev/logging.md）**：`Log` 是**自研极简分级日志，不引 Serilog/NLog**。
    - 默认 `Info`；`Debug` 打逐帧报文/探测明细，需要时在**设置页下拉框**切"详细"（或 `settings.json` 改 `LogLevel`）**并重启应用**。
    - **新增日志前先定级别**：状态变化 / 协议解析结果 = `Info`（结果行尾带 `原始=<HEX>`）；逐帧 / 中间帧 / 明细 = `Debug`；热路径先 `Log.IsEnabled(level)` 短路，省掉 `$"..."` 插值。
    - **别在各调用点自己攒去重字段**：轮询型重复日志用 `Log.InfoOnChange(key, state, message)` 或 `Log.InfoThrottled(key, message, interval)`。
    - 排障时 `app.log` 之外还有 `app.1.log` / `app.2.log`（按大小滚动）；每个文件**首行是"时间基准"**（本地时区 + UTC offset），用来和 `history.db` 的 UTC 毫秒对齐。

## 6. 关键文件速查

| 要动什么 | 去哪 |
|---|---|
| 迈从协议/解析 | `src/VigilWatt.Core/Plugins/Mchose/MchosePlugin.cs`（规格 protocols.md §1） |
| 雷柏协议/解析 | `src/VigilWatt.Core/Plugins/Rapoo/RapooPlugin.cs` + `RapooCodec.cs`（纯函数，有单测；规格 protocols.md §3） |
| 被动离线判定机制 | `Plugins/OfflinePolicy.cs`（第③层参数 + 纯函数 `IsConfirmExpired`，有单测 `OfflinePolicyTests`）+ `BatteryPluginBase`（`MarkOffline` / `MarkOfflineCandidate` / `ClearOfflineCandidate` / `RequestOffline` / `RequestConfirm` / `ReportPollFailure`）。五条路径对照表见 code-flow.md §4.2 |
| 北通协议/解析 | `src/VigilWatt.Core/Plugins/Betop/BetopPlugin.cs` + `BetopCodec.cs`（规格 protocols.md §4；**"手柄未连接"占位帧 §4.4.1**）；report ID 解析见 `Hid/NativeHid.cs` 的 `ReadReportId` |
| 插件"认领但离线"规则 | `BatteryPluginBase.CommitProbes` 的 `IsOnline = percent.HasValue`（探测成功但无读数 = 设备在位不可读，标离线） |
| 经典蓝牙显示名 | `src/VigilWatt.Core/Ble/ClassicDeviceName.cs`（纯逻辑：占位名识别）+ `ClassicPairedNames.cs`（注册表配对名兜底） |
| 蓝牙诊断 | `src/tests/VigilWatt.BleScan/`（`dotnet run --project`）：适配器能力、已配对经典/LE 设备、**每个 AEP 的 `IsConnected`/`DeviceAddress` 与"已连接"集合** |
| 新增品牌插件 | 新建 `Plugins/xx/XxPlugin.cs` 继承 `BatteryPluginBase`，三个方法（architecture.md §3） |
| 轮询/采样/低电量节奏 | `src/VigilWatt.Core/Devices/DeviceManager.cs`（code-flow.md §2/§5/§6） |
| 经典蓝牙发现 + 电量 | `src/VigilWatt.Core/Ble/BluetoothClassicWatcher.cs` + `Ble/ClassicAudioBattery.cs`（code-flow.md §3、runtime-pitfalls §3） |
| 经典蓝牙电量排查 | `scripts/probe-devnode-battery.py`（`--all` dump 节点全属性） |
| 本机电量读数/推导 | `src/VigilWatt.Core/Power/SystemPowerReader.cs`（`GetSystemPowerStatus`）+ `Power/SystemBatteryWatcher.cs`（充电推导、稳定 Id `sys:battery`） |
| 本机电量开关 | `AppSettings.MonitorSystemBattery` + `DeviceManager.ApplySystemBatterySetting()` + `SettingsPage`「本机」分节 |
| 托盘为何不含本机电量 | `src/VigilWatt.App/Services/TrayService.cs` 的 `Render()`：`if (d.Source == DeviceSource.System) continue;` |
| 托盘/小组件/设置页 | `src/VigilWatt.App/Services|Views`（code-flow.md §7/§8/§9） |
| 程序图标 | 生成器 `scripts/make-app-icon.py`（改配色/字形改它后重跑，**别手改 .ico**）→ `src/VigilWatt.App/Assets/VigilWatt.ico`；接入 = csproj `<ApplicationIcon>` + `MainWindow.ApplyWindowIcon()`（两处都要，见 runtime-pitfalls §5.2） |
| 托盘提示/图标刷新 | `TrayService.RequestUpdate()` + `App.AttachTray` 里挂的 `DeviceUpdated`/`DevicesChanged`（见 runtime-pitfalls §5.1） |
| 历史统计页（指标卡/曲线/hover） | `src/VigilWatt.App/Views/StatsPage.xaml(.cs)` + `Views/BatteryChartControl.xaml.cs` |
| 历史删除/统计能力 | `src/VigilWatt.Core/Storage/HistoryStore.cs`（`GetStats`/`GetDeviceStats`/`DeleteDevices`/`Compact`）；页面 `Views/HistoryPage.xaml(.cs)` |
| 历史管理页单测 | `src/tests/VigilWatt.Core.Tests/HistoryStoreTests.cs`（临时库真跑 SQLite） |
| 日志实现/分级/滚动/兜底 | `src/VigilWatt.Core/Utils/Log.cs` + `Utils/LogLevel.cs`；App 侧档位 `Services/LogLevelOptions.cs`；设置页「日志」分区（设计见 dev/logging.md） |
| 日志级别配置与单测 | `AppSettings.LogLevel` + `App.OnLaunched` 里 `Log.MinLevel = LogLevelNames.Parse(...)`；退出收尾 `App.ShutdownLogging()`；单测 `src/tests/VigilWatt.Core.Tests/LogTests.cs` |
| 语言包白名单 | `VigilWatt.App.csproj` 的 `MuiLanguagesToKeep` + `_MuiKeep` 两处（build.md §6） |
