# 会话交接（给下一个 AI / 未来的自己）

> 快照时间：2026-10-10。**先读本文，再按需查其他文档**；内容冲突时以专题文档为准（本文是快照）。
> 逐轮历史（2026-10-04 起每一轮做了什么、为什么这么做）已剥离到 **[changelog.md](changelog.md)**，本文只留"当前状态 + 待办 + 注意事项 + 文件速查"。
> **维护约定**：本会话收工后——本轮记录追加进 `changelog.md`，本文件只更新 §1 状态与 §4 待办。**流水账不要写回这里**。

## 1. 当前状态一句话

应用运行中。本机三台 2.4G 设备**已全部打通且首次发现均在 6 秒内**：
北通 T9 手柄 73%（T9 被动状态流）、迈从 G87 键盘 65%（0x13 会话协议）、雷柏 VT3 二代鼠标 98%（status-report7 被动）；
经典蓝牙耳机 EDIFIER W820NB 走 HFP 网关节点 `DEVPKEY_Device_BatteryLevel`（与 Windows 设置页一致）；
插件架构完成二演化；本机电量监控（10-08）、日志分级（10-09）、经典蓝牙设备类型判定（10-09）均已落地。
**代码审查机制（2026-10-10）已建立**：标准在 `docs/dev/review-standard.md`，发现跟踪在 `docs/reviews/BACKLOG.md`。
**2026-10-10 又做了"上线前全检 + 全部调整"**：4 条 P0 + 7 条 P1 + 大部分 P2 已修并验证（构建 0 警告 0 错误、测试 253 用例全绿）。
BACKLOG 现在**未修项 = CR-13（部分）/ CR-16 + 重构候选 D1 完整形态 / D3 / D4 / D5 / D6**，其余全部 `done` 或 `wontfix`（各带理由）。
**2026-10-10 第二十轮新增「任务栏电量贴片」**（可选开关、默认关）：把在线设备电量做成一条胶囊钉在任务栏托盘左侧，
可沿任务栏拖动并记住位置；定位/跟随原理见 `dev/runtime-pitfalls.md` §16，文件速查见 §6。
**同轮第二、三轮反馈共修掉三件事**（第二轮的判断被第三轮实测**推翻并更正**，以 §17 为准）：
① 贴片"白框"——可见的是 **Win32 窗口自己用类背景刷擦出来的底色**（`COLOR_WINDOW`，浅色主题=纯白），
不是内容岛那层；修法是 `DwmExtendFrameIntoClientArea(-1)` + 接管 `WM_ERASEBKGND`/`WM_PAINT` 把客户区擦成 alpha=0；
② 最右一格 `%` 被裁——窗口宽度改为布局实测；③ 点任务栏闪一下——抬 Z 序拍频 1 秒 → **16ms**（实测遮挡约一帧）。
**用户已复看确认外观无误**（2026-10-10 21:28："是我需要的效果"）。
**2026-10-10 第二十一轮：日志改为按日分文件**（`logs\app-日期.log`，跨天自动切、默认保留 14 天，可在设置页改；旧 `app.log` 废弃，见 §5.13）。
**2026-10-11 第二十三轮：设置页新增搜索与定位**，可按标题、说明、分区搜索普通设置项，也可按插件名/设备类型查动态插件开关；点击结果滚动定位并短暂高亮。
⚠ **`dev/runtime-pitfalls.md` §14 里"本机 Win10 19045"的旧标注已过期**，以 §17 的复核为准。

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

## 3. 最近变更

完整历史见 **[changelog.md](changelog.md)**，这里只留最近变更：

- **2026-10-11 第二十三轮 · 设置页搜索与定位**：新增实时搜索建议，匹配普通设置标题/说明/分区及插件显示名/内部名/设备类型；点击后把目标行放在视口约四分之一高度处，名称短暂切换为主题强调色并加粗后渐隐。回顶控件为页面右下角悬浮圆形上箭头，滚动一屏后显示。`dotnet build VigilWatt.sln -c Debug` 0 警告 0 错误。

- **2026-10-10 第十九轮 · 取消独立"写入许可"开关**：罗技设备发现不了（第十八轮把罗技默认关 + 新增 `LogitechProbingEnabled`，用户开了插件却找不到设备）→ 按用户要求**所有插件统一为"插件开关即写入授权"**：删除 `WritePermission` 机制与 `MchoseProbingEnabled`/`BetopProbingEnabled`/`LogitechProbingEnabled` 三个设置项，基类 `WritesEnabled => IsWritesEnabled(Name)` 直读插件开关，设置页不再有「写入许可」子行。熔断/退避不受影响。详见 changelog.md 第十九轮 + BACKLOG CR-37。
- **2026-10-10 第十八轮 · 上线前全检 + 全部调整**：4 人全检（报告 `deliverables/gstack/pre-launch-check-2026-10-10.md`）→ 用户批"全部调整"→ 修掉 **4 条 P0**（共享可变 `DeviceStatus` 让变化判定恒假 / 罗技绕过基类安全机制 / 北通探测路径无写入闸门 / 小组件订阅泄漏）+ **7 条 P1** + 大部分 P2，并落地 D2/D7/D8/D9/D10。新增"插件声明写入许可"机制（`IBatteryPlugin.WritePermission` + 设置页自动生成子行，**已于第十九轮取消**），基类写入默认从 fail-open 改为 fail-safe。详见 changelog.md 第十八轮。

## 4. 待办（**唯一来源**：改待办请改这里，别在 experience.md 抄第二份）

> **与 `docs/reviews/BACKLOG.md` 的分工（2026-10-10 定）**：本节只放**当前这一轮在做的那几条**；
> **审查发现的未修项全集在 BACKLOG**（open/doing/done/wontfix），不要在这里抄一份。

1. **充电标志校准**：用户插充电线 → 看日志 `状态字节` → 补全 `ParseKeyboardV1Charge` 映射（已知 0x10 位=充电中）
2. ~~**休眠推送帧采样**~~ **已完成（2026-10-06）**：休眠帧已实测抓到并解析清楚——`13 0A 01 00 04 **02** 01 …`，下标 5 是**类型位**（0x05=电量 / 0x02=设备状态通知），旧代码没认类型位、把 payload 的 0x01 当电量 1%，这就是误报低电量的根因。现在按类型位识别，并把 0x02 当"设备已休眠"信号**立即标离线**（`RequestOffline`，省掉 3 分钟延迟）。详见 runtime-pitfalls §12、protocols.md §1。
3. **雷柏充满态采样**：充电标志已实测校准（0x01=放电/0x02=充电，2026-10-05 插充电线确认，电量 69%→75% 期间 [7]=0x02 恒定）；**充满时 [7] 的值还没采到**——下次充满看日志 `充电标志` 字节，若是 0x03 之类补进 RapooPlugin 映射
4. ~~**时砂耳机**~~ **已结案（2026-10-06）**：2.4G 无电量通道（protocols.md §3 双重确认），且 RoseLink 逆向确认其协议内也无电量命令 —— 主机侧 + 官方 App 侧均无解，唯一途径是耳机语音播报。不再投入。
5. ~~**经典蓝牙耳机电量**~~ **已解决（2026-10-06）**：连接状态走 BluetoothClassicWatcher（纯被动零写入）；电量改读 **HFP 免手操作网关设备节点的 PnP 属性 `DEVPKEY_Device_BatteryLevel`**（`{104EA319-6EE2-4701-BD47-8DDBF425BBE5},2`），用 SetupAPI 枚举 `BTHENUM\*` 节点 + 按 MAC 对上 AEP 设备。实测 EDIFIER W820NB 60% 与设置页一致。**结论：`System.Devices.AepBattery_Level` / `Aep.Battery_Level` / `System.Devices.BatteryLevel` 都别再试了，PnP 属性是另一套体系**（runtime-pitfalls §3、code-flow §3）。剩余可做：接耳机时观察 HFP 节点电量是否实时刷新（现在是每分钟轮询 + 5 秒缓存）。
6. 新世代迈从 dongle（3837:3033）QHW 协议已实现未验证，遇到即用
7. ~~**北通手柄真机验证**~~ **已真机验证（2026-10-06 晚，北通 T9）**：修掉 report ID 硬编码根因 + 新增 T9 被动流解析（见 changelog.md 2026-10-06）。
   剩余：**鲲鹏20（`PID_5191`）主动查询路径仍未真机验证**（本机只有 T9）。
8. **T9 `byte23` 低位语义未定论**（2026-10-06 第九轮留的口子）：现在是"只认 `0x50` = 未连接"。
   **验证方法**：把手柄充满（`byte23=0x78`）后再关机，看 dongle 报 `0x50` 还是 `0x70` ——
   报 `0x70` 说明低位 `0x08` 是"链路有效"标志，可把判据一般化成 `(byte23 & 0x08) == 0`，
   同时电量换算要改成"高 4 位 / 7"；报 `0x50` 则维持现状（等级×8）。见 protocols.md §4.4.1。
9. **任务栏电量贴片（2026-10-10 第二十轮新增）**：开关在设置页「任务栏」，默认关。
   **已由第三轮实测 + 用户复看确认**：白框已消除（贴片内外背景像素逐字节相同，用户确认观感无误）、
   双设备宽度自适应正确（89px → 186px）、点任务栏的遮挡时长压到约一帧（16ms 拍频）、
   `TrayNotifyWnd` 在 Win11 26200 上仍存在。
   **仍需人手确认**：① 沿任务栏**拖动与位置记忆**的手感（`WM_NCHITTEST` 已确认不受影响，但没真拖过）；
   ② 任务栏**自动隐藏**收起时贴片是否跟着藏；③ **竖向任务栏 / 多显示器**（代码写了分支但完全未验证）；
   ④ **深色主题**下文字对比是否够（CR-42）；⑤ 贴片是否压住任务栏按钮（CR-39）。
   原理与已证伪路径见 `dev/runtime-pitfalls.md` §16 / §17（**§17 是最容易重复踩的一条**：
   窗口两层背景 + 九种透明通道的实测结果表）。

## 5. 给下一个 AI 的注意事项

1. **开工顺序**：docs/README.md（症状速查）→ 本文件 → 按任务查专题文档。repo 文档永远比记忆新。
2. **硬件安全红线**（用户两次事故换来，最高优先级约束）：**绝不盲扫**；只发有出处的帧（官方抓包/公开文档）；**插件开关本身就是写入授权**（`AppSettings.PluginEnabled[Name]` → 基类 `WritesEnabled => IsWritesEnabled(Name)`；会写帧的插件默认关闭）；熔断+退避已在基类内建，别在子类绕过。
3. **dongle 静音时先查会话**：本应用 / 浏览器里的 M HUB WEB / 官方桌面 HUB 互斥，谁持有会话谁工作，崩溃进程会把会话粘死（所有人静音），kill 持有者或重插拔恢复（safety.md §2 事故二）。这是最容易误判成"协议错了"的坑。
4. **调试主力手法**：让官方软件和硬件说话，浏览器 monkey-patch 抓包（debugging.md §1，含选择器弹窗自动化、DPI 截图坑）；验证用 `pip install hidapi` 直测（§2）。
5. **构建**（dev/build.md）：sln 构建、从 `bin\x64\...\win-x64\` 启动；XamlCompiler 静默崩溃查 XAML 属性名；应用运行中先 taskkill。
6. **用户偏好**：中文交流、文档中文；界面只要中英文；个人自用工具，克制功能范围（不做游戏内显示/HTTP API/账号等）。
7. **验证习惯**：改完协议代码看三处——日志认领电量值 vs 键盘 FN+B 指示、history.db 采样、拔插自愈。
8. **雷柏特性**：鼠标停手 = dongle 静音（被动监听空转是预期），动一下鼠标即恢复心跳；**静音 ≠ 离线**——0x07 是活动驱动的心跳，2026-10-08 起存活判据改成"接口有没有在说话"，`OfflinePolicy.Silence = 90s` 只是兜底。**离线主判据是 `[18].bit7`（RF 断链位）**：鼠标断电瞬间 dongle 会主动吐一帧 `[18]=0x7F`，走 **6 秒**延迟确认（`OfflinePolicy.Confirm`）——但 bit7 是链路状态不是"设备在不在"，**别改成直接判离线**，抖动会让列表闪。别把静音误判成协议/会话问题。协议细节与已证伪路径见 protocols.md §3。
9. **脚本/项目存放规则**：临时调试脚本（PowerShell/Python/一次性 P/Invoke 探针）写进 `scripts/`（索引见 scripts/README.md），**用完即删**；**长期存在的 C# 项目放 `src/tests/`**（单测/诊断工具），不放 scripts/；工程源码在 `src/`。**仓库根目录只放工程文件**；临时产物用 `%TEMP%`，用完即删。别在根目录或 src/ 里散落 .ps1/.py。
10. **发现节拍**（2026-10-08）：设备"在位与否"只有探测才知道，而主轮询 60 秒一拍太慢。15 秒快速发现节拍服务两类插件——`SupportsFastDiscovery`（零写入，雷柏）与 `SupportsDiscoveryProbe`（写帧但 `ProbeCooldown` 兜住写入，迈从）。**新增会写帧的插件若要吃 15 秒节拍，必须先过 safety.md 规则 4 的例外口径**，并把 `ProbeCooldown` 压到 15 秒以下（含一次探测的耗时余量）。
11. **本机启动应用的方式**：shell / `nohup` / `cmd //c start` 拉起的进程会被进程树清理带走（约 5 秒后死，日志无退出记录）；WMI `Win32_Process Create` 被安全策略拦。**`explorer.exe "VigilWatt.App.exe"` 可用**（挂在资源管理器下，脱离工具进程树）。强杀实例会按 safety.md §2 把 dongle 会话占死，之后要重新插拔 dongle。
12. **插件配置分三层，别混（2026-10-08 第十四轮）**：① 协议事实 ② 安全声明 —— 这两层**必须** per-brand；③ **离线判定策略**集中在 `OfflinePolicy`（`Plugins/OfflinePolicy.cs`），每插件一行。**尤其注意 `CountPollFailures` 与 `SupportsPassiveListen` 是两件事**：前者回答"轮询返回 null 算不算失联"（策略），后者只说"我起后台只读线程"（机制）。曾经合成一个布尔量复用，导致迈从键盘关机后**永远**不显示离线。新增"要被动监听、但轮询空读确实是失联"的插件时，必须把 `CountPollFailures` 置 false 并在 `PollDevice` 发过帧的分支自己调 `ReportPollFailure`，否则同样的 bug 会再犯一次。
13. **日志（2026-10-09 第十五轮，完整设计见 dev/logging.md）**：`Log` 是**自研极简分级日志，不引 Serilog/NLog**。
    - 默认 `Info`；`Debug` 打逐帧报文/探测明细，需要时在**设置页下拉框**切"详细"（或 `settings.json` 改 `LogLevel`）**并重启应用**。
    - **新增日志前先定级别**：状态变化 / 协议解析结果 = `Info`（结果行尾带 `原始=<HEX>`）；逐帧 / 中间帧 / 明细 = `Debug`；热路径先 `Log.IsEnabled(level)` 短路，省掉 `$"..."` 插值。
    - **别在各调用点自己攒去重字段**：轮询型重复日志用 `Log.InfoOnChange(key, state, message)`（状态变了才记）或 `Log.InfoThrottled(key, message, interval)`（同 key 限频）；**"同一 key 只记一条、恢复正常后再记一条"** 用 `Log.Once(level, key, message)` / `Log.ResetOnce(key)`。
    - 日志**按日分文件**：`logs\app-日期.log`（本地日期），跨天自动切；默认保留 **14 天**（`AppSettings.LogRetentionDays`，设置页「日志」可改，重启生效）。每个文件**首行是"时间基准"**（本地时区 + UTC offset），用来和 `history.db` 的 UTC 毫秒对齐。
    - 旧版 `app.log` / `app.1.log` / `app.2.log` 已废弃（2026-10-10 第二十一轮改按日），启动/跨天清理时一并删；docs 里 `grep ... app.log` 已改为 `grep ... app-*.log`。

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
| 新增品牌插件 | 新建 `Plugins/xx/XxPlugin.cs` 继承 `BatteryPluginBase`，三个方法（architecture.md §3）；**会写帧的插件必须在 `AppSettings.PluginEnabled` 里默认关闭**（插件开关即写入授权） |
| 插件写入授权（开关 + 闸门） | `AppSettings.PluginEnabled`（唯一入口，插件开关即授权）；基类 `WritesEnabled => IsWritesEnabled(Name)` / `CanWrite` / `RunProbe` 的 `ProbeWrites` 检查；设置页 `Views/SettingsPage.xaml.cs` 的 `AddPluginRow`（每插件一行） |
| 设备状态文案/充电判定（三处共用） | `src/VigilWatt.Core/Models/DeviceStatusFormatter.cs`（`IsCharging` / `IsChargingOrFull` / `PercentText` / `ChargeSuffix`，有单测）+ `Models/ChargingStateMap.cs`（`FromChargingFlag`）；**色值在 App 层** `Services/UiPalette.cs` |
| 变化判定 / 骤降保护 | `src/VigilWatt.Core/Devices/DeviceChangeDetector.cs`（纯逻辑，有单测）+ `Models/DeviceStatus.Clone()`（`Upsert` 靠它快照，别再传共享可变实例） |
| 两个 watcher 的公共属性读取 | `src/VigilWatt.Core/Ble/WatcherProps.cs`（`ReadConnected` / `ReadBattery`，有单测；**属性缺失必须返回 null，不能当 false**） |
| 轮询/采样/低电量节奏 | `src/VigilWatt.Core/Devices/DeviceManager.cs`（code-flow.md §2/§5/§6） |
| 经典蓝牙发现 + 电量 | `src/VigilWatt.Core/Ble/BluetoothClassicWatcher.cs` + `Ble/ClassicAudioBattery.cs`（code-flow.md §3、runtime-pitfalls §3） |
| 经典蓝牙电量排查 | `scripts/probe-devnode-battery.py`（`--all` dump 节点全属性） |
| 本机电量读数/推导 | `src/VigilWatt.Core/Power/SystemPowerReader.cs`（`GetSystemPowerStatus`）+ `Power/SystemBatteryWatcher.cs`（充电推导、稳定 Id `sys:battery`） |
| 本机电量开关 | `AppSettings.MonitorSystemBattery` + `DeviceManager.ApplySystemBatterySetting()` + `SettingsPage`「本机」分节 |
| 托盘为何不含本机电量 | `src/VigilWatt.App/Services/TrayService.cs` 的 `Render()`：`if (d.Source == DeviceSource.System) continue;` |
| 托盘/小组件/设置页 | `src/VigilWatt.App/Services` 与 `Views`（code-flow.md §7/§8/§9） |
| 任务栏电量贴片 | `src/VigilWatt.App/TaskbarBatteryWindow.xaml(.cs)`（定位/跟随/不抢焦点见 runtime-pitfalls.md §16） |
| 贴片窗口表面透明（两层背景 + 按系统分通道） | `src/VigilWatt.App/Services/TaskbarTransparency.cs`（Win11 = extend-frame + 自接管 `WM_ERASEBKGND`/`WM_PAINT`；**最容易重复踩**，见 runtime-pitfalls.md §17） |
| 任务栏贴片配置与开关 | `AppSettings.TaskbarBatteryEnabled/Mode/MaxDevices/OffsetX` + `Services/TaskbarBatteryMode.cs` + 设置页「任务栏」分节 |
| 设备类型图标（小组件与贴片共用） | `src/VigilWatt.App/Services/DeviceIconFactory.cs`（纯形状拼装，**别用字体图标**） |
| 程序图标 | 生成器 `scripts/make-app-icon.py`（改配色/字形改它后重跑，**别手改 .ico**）→ `src/VigilWatt.App/Assets/VigilWatt.ico`；接入 = csproj `<ApplicationIcon>` + `MainWindow.ApplyWindowIcon()`（两处都要，见 runtime-pitfalls §5.2） |
| 托盘提示/图标刷新 | `TrayService.RequestUpdate()` + `App.AttachTray` 里挂的 `DeviceUpdated`/`DevicesChanged`（见 runtime-pitfalls §5.1） |
| 历史统计页（指标卡/曲线/hover） | `src/VigilWatt.App/Views/StatsPage.xaml(.cs)` + `Views/BatteryChartControl.xaml.cs` |
| 历史删除/统计能力 | `src/VigilWatt.Core/Storage/HistoryStore.cs`（`GetStats`/`GetDeviceStats`/`DeleteDevices`/`Compact`）；页面 `Views/HistoryPage.xaml(.cs)` |
| 历史管理页单测 | `src/tests/VigilWatt.Core.Tests/HistoryStoreTests.cs`（临时库真跑 SQLite） |
| 日志实现/分级/按日分文件/保留/兜底 | `src/VigilWatt.Core/Utils/Log.cs` + `Utils/LogLevel.cs`；App 侧档位 `Services/LogLevelOptions.cs`；设置页「日志」分区（设计见 dev/logging.md） |
| 日志级别/保留天数配置与单测 | `AppSettings.LogLevel` / `AppSettings.LogRetentionDays` + `App.OnLaunched` 里 `Log.MinLevel = ...` / `Log.RetentionDays = ...`；退出收尾 `App.ShutdownLogging()`；单测 `src/tests/VigilWatt.Core.Tests/LogTests.cs` |
| 语言包白名单 | `VigilWatt.App.csproj` 的 `MuiLanguagesToKeep` + `_MuiKeep` 两处（build.md §6） |
| 某轮改动为什么这么做 | `docs/project/changelog.md`（本文件不记历史，只给指针） |
| 审查发现还剩什么没修 | `docs/reviews/BACKLOG.md`（**唯一载体**：open/doing/done/wontfix） |
| 代码该守的红线、审查流程 | `docs/dev/review-standard.md` |
