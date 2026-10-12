# 审查发现 BACKLOG（唯一跟踪载体）

> **本文件是审查发现的唯一载体**：审查报告只承载证据与推理，条目本体在这里，走完 `open → doing → done / wontfix`。
> **与 `docs/project/handoff.md` §4 的分工**：这里 = 审查发现的**未修项全集**；handoff §4 = **当前这一轮在做的那几条**。
> 同一件事只在这里留条目，handoff 只放指针——**不要抄第二份**。
>
> 标准与流程见 `docs/dev/review-standard.md`。证据一律写 `文件:符号`，**不写行号**（行号会漂移）。
> **编号规则：关闭后不重排、不复用**——旧编号永远查得到。
>
> 首次建库：2026-10-10，来源是 `docs/reviews/archive/` 两份 2026-10-06 报告（每条回到当前代码重新核对过）。
> **2026-10-10 上线前全检**（4 人协作：安全审计 / QA / 代码审查 / 可维护性专项）对 CR-01~CR-17 逐条复核，
> 并新增 CR-19~CR-33 与"重构/去重候选"分区。全检证据与推理见 `deliverables/gstack/pre-launch-check-2026-10-10.md`。
> **2026-10-10 修复轮**（用户批准"全部调整"）：4 条 P0 + 7 条 P1 + 大部分 P2 已修并验证，见 `docs/project/changelog.md`。

## 状态图例

| 状态 | 含义 |
|---|---|
| `open` | 已确认，未开始修 |
| `doing` | 正在修（应同时在 handoff §4 出现指针） |
| `done` | 已修并验证（"怎么验证的"记进 `docs/project/changelog.md`） |
| `wontfix` | 决定不修，**必须写理由** |

> **2026-10-10 起的一条约定**：修复轮关闭的条目**就地标 `done`**、不搬运到"已关闭"表——
> 搬运会让"证据 / 来源"两列与条目分离，查起来反而费劲。下方"已关闭"表只保留更早轮次关闭的条目。

## 变更记录

- **2026-10-10（首次建库）**：从两份 2026-10-06 报告逐条回到代码核对建库（CR-01~CR-17 open + CR-D1~D3 done）。
- **2026-10-10（第十七轮 · 文档一致性）**：CR-11、CR-18 修掉并关闭（见 `docs/project/changelog.md` 第十七轮）。
- **2026-10-10（上线前全检）**：
  - CR-01/02/03/04/05/06/08/12/13/14/15/16/17 → 复核后**仍成立**（CR-03 定性加重、CR-05/06 加重、CR-17 表述收紧）。
  - **CR-09 → 上调 P0**（运行时真泄漏：反复开关小组件累积）。
  - **CR-07 维持 P1**（退出路径，进程终止后 OS 回收句柄，不累积；与 §1「资源泄漏=P0」的字面存在张力，见条目内说明）。
  - **CR-10 → 重新定性**为"扫描代码未从正常执行路径剥离"（只读 `GetFeature`，非硬件误写），级别维持 P1。
  - **CR-11 → done**（复核确认第十七轮已修）。
  - 新增 **CR-19~CR-33**；新增「重构/去重候选」分区（D1~D10）。
- **2026-10-10（修复轮 · 全部调整）**：本轮关闭 **CR-01/02/03/04/05/06/07/08/09/10/12/14/15/17/19/20/21/22/23/24/25/26/27/28/29** 与
  **D2/D7/D8/D9/D10**（就地标 done）；**CR-13 部分关闭**（逐字节相同的属性读取已抽离，基类化待做）；
  **CR-30/31/32/33 → wontfix（各带理由）**；**CR-16 与 D1/D3/D4/D5/D6 保留 open**（理由见条目）。
  验证方式：`dotnet build` 0 警告 0 错误；测试项目 **253 用例全绿**（基线 177 → 新增 76）。
- **2026-10-10（修复轮 · 收工 L2 轻审查）**：对本轮改动面做轻审查（报告 `docs/reviews/archive/fix-round-2026-10-10.md`），
  新增 **CR-34/35/36**——均为"当前不触发"的 P2 潜在陷阱，保留 `open`，未修理由见报告 §5。
- **2026-10-10（第十九轮 · 取消独立写入许可）**：用户实测罗技 2.4G 鼠标找不到 → **CR-37**（罗技被当新插件默认关，静默功能回归）。
  按用户要求把所有插件统一为"**插件开关即写入授权**"：
  - 删除 `WritePermission` 机制（`Plugins/WritePermission.cs`、`IBatteryPlugin.WritePermission`、基类虚属性）；
  - 删除 `MchoseProbingEnabled` / `BetopProbingEnabled` / `LogitechProbingEnabled` 三个设置项；
  - 基类 `WritesEnabled => IsWritesEnabled(Name)` 直读 `PluginEnabled[Name]`；新增 `protected static IsWritesEnabled(string)`，供插件的 static 辅助方法（迈从的 `TryQhw` / `TryKeyboardV1` 等）使用；
  - 设置页不再生成「写入许可」子行（`AddWritePermissionRow` / `_writePermissionRows` 已删）；
  - **D10 随之作废**（其"插件声明写入许可"方案被更彻底的"插件开关即授权"取代）；**CR-12 的 UI 交付物（写入许可子行）已回退**，但 CR-12 的根因（`MchoseProbingEnabled` 无控件）已由"取消该设置项"解决；
  - 顺带补掉 **CR-22 的漏项**：`LogitechPlugin.Detach` 原先无条件打"罗技插件已卸载"（不继承基类故没被 CR-22 覆盖），改为只在真释放会话时记。
  验证：`dotnet build` 0 警告 0 错误；测试 **253 用例全绿**。
- **2026-10-10（第二十轮 · 新增任务栏电量贴片）**：收工 L2 轻审查（限定改动面）新增 **CR-38/39/40/41**，
  全部 `open`——四条都是"静态可见、**运行时不可验**"的一类（贴片能不能正常压在任务栏上、位置对不对，
  只能真机肉眼确认，本轮不能启动应用）。同时把该轮**已经修掉的**两处记录在此：位置数学抽成
  `Services/TaskbarLayout.cs` 并补 23 条单测（R8/S1，原本 `TaskbarBatteryWindow.xaml.cs` 447 行超限 → 拆到 344 行）、
  删掉 `Native.IsWindow` 死声明（S6）。  验证：`dotnet build` 0 警告 0 错误；测试 **276 用例全绿**（基线 253 → 新增 23）。
- **2026-10-10（第二十轮 · 用户反馈去底框）**：贴片首版是"白底圆角胶囊"，用户实测"能显示，但很突兀，**不需要背景**"
  → 去掉底色与边框，只留文字与设备图形浮在任务栏上（窗口表面透明本来就是必需的，现在更没有可画的东西了）。
  随之新增 **CR-42**（文字色跟应用主题、不跟任务栏实际明暗），`open`。
- **2026-10-10（第二十轮 · 第二轮反馈：白框仍在 + 两个 bug）**：用户复看后报"效果还是一样"+ 点任务栏闪 + 第二个设备 `%` 不全。
  查出**根因是前一轮的判断前提错了**：这台机器早已不是 Win10 而是 **Win11 26200**，
  于是 `SetWindowCompositionAttribute` 的 accent 策略**静默失效**（返回成功、无警告、就是不透明），
  而"删 XAML 底色"根本治不到那层——WinUI 3 的窗口背景分 Win32 层与内容岛层两层，看到的是**内容岛那一层**。
  修法见 `dev/runtime-pitfalls.md` §17。本轮新增 **CR-43（done）/ CR-44（open）/ CR-45（done）/ CR-46（done）**，
  并更新 **CR-40**（Z 序空档从 1 秒压到 100ms，风险等级下降但未消除）。
  验证：`dotnet build` 0 警告 0 错误；测试 **276 用例全绿**。
- **2026-10-10（第二十轮 · 第三轮反馈：白框与闪动都还在）**：用户复看后报"白色的框还在，而且点击任务栏信息还闪一下"
  ——即第二轮的两处修改**都没修掉**。本轮先补上"能看见"的能力（临时截屏脚本没声明 DPI 感知，前两轮截出来的都是
  不相干区域，等于在盲改），再逐通道实测：
  - **可见的那层被认错了**：是 **Win32 窗口自己用类背景刷擦出来的底色**（`GCLP_HBRBACKGROUND = 0x6` = `COLOR_WINDOW`，
    浅色主题就是纯白），不是内容岛那层 → **CR-43 的定性被推翻并更正**，修法改为
    `DwmExtendFrameIntoClientArea(-1)` + 自己接管 `WM_ERASEBKGND`/`WM_PAINT` 把客户区擦成 alpha=0（§17.5）。
  - **闪动是 Z 序空档**：抬 Z 序拍频 100ms → **16ms**，实测遮挡 **6~25ms（中位 19ms）**。
  本轮 **CR-43 重写并 done（像素级实测）**、**CR-44 → wontfix（实测无效 + colorkey 变体有硬伤）**、
  **CR-45 / CR-46 复看确认**、更新 **CR-40**，新增 **CR-47/48/49**。
  验证：`dotnet build` 0 警告 0 错误；测试 **276 用例全绿**；贴片内外背景像素**逐字节相同**。
- **2026-10-11（全项目架构评估）**：新增 **CR-52**（P2）：`App.xaml.cs:ShutdownLogging` 的注释仍称 `AppServices.Dispose` 全仓无人调用，
  但 `TrayService.Exit` 已在调用；代码行为正确，注释已过期。
- **2026-10-11（日常开发 · 修 CR-53）**：用户复报"迈从休眠后唤醒，程序长时间没识别到" → 当日建 **CR-53** 并**就地修掉（标 `done`）**：
  根因是**固定 10 分钟熔断退避把"唤醒检测"一起挡死**（日志实测盲区 12 分 52 秒，链路见条目）。
  新增纯逻辑 `Plugins/WriteBackoff.cs`（递增退避，封顶 5 分钟）+ 事件驱动放行 + 半开重试；`LogitechPlugin` 同步。
  验证：build 0 警告 0 错误；除 `LogTests` 外 **278 用例全绿**（新增 `WriteBackoffTests` 9 条）。**未真机验证**。
- **2026-10-12（修 Win10 贴片白框 + 末尾 `%` 被裁）**：用户报"贴片在 Win11 正常、Win10 上背景透明丢失（白框）且末尾 `%` 被裁"。
  **本机就是 Win10 19045，bug 可复现**。第一轮按 §17.1 只补「清 Win32 那层」→ **白框照旧**；
  判别实验（GDI 往客户区填色，可见像素纹丝不动）证明 Win10 上可见的是**内容岛那层**
  （不设透明 `SystemBackdrop` 时回退成纯色主题底，浅色=纯白）。最终把「清两层」收进
  `Services/TransparentWindowSurface.cs`（`Apply` = 清边框样式 + 清 Win32 层 + 清内容岛层），**不做 OS 分支**；
  `Services/TaskbarTransparency.cs` 与 `Native.MakeWindowTransparent` 删除；`RelayoutCore` 改 `ResizeClient`。
  连带查出**小组件整个白方块**（`DWMWA_NCRENDERING_POLICY` 与 extend-frame 叠加所致），一并修掉。
  顺带修 **CR-49**（旧窗口过程改按 HWND 存）。新增 **CR-54/55/56（`done`）+ CR-57（`open`：Win10 上投影与透明互斥）**。
  验证：`dotnet build` 0 警告 0 错误；`dotnet test`（除 `LogTests`）**278/278**；**像素级实测**——
  贴片内背景与任务栏同色且末尾 `%` 完整，小组件四角与真桌面**逐字节相同**。
- **2026-10-12（修贴片/小组件右键菜单关不掉）**：用户报"右键菜单弹出后点别处不消失"。
  用合成鼠标 + `EnumWindows` 取证（**这一步动了用户的真实输入，用户当场制止**，教训已写进
  `docs/dev/runtime-pitfalls.md` §18）：flyout 宿主窗口 `Microsoft.UI.Content.PopupWindowSiteBridge`
  带 `WS_EX_NOACTIVATE`，而 WinUI 的 light-dismiss 靠**窗口去激活**触发 → 贴片永不激活 ⇒ 菜单关不掉。
  **修法**：`ContextFlyout` 换成 Win32 `TrackPopupMenuEx`（新增 `Services/AppPopupMenu.cs`），
  两个窗口改挂 `RightTapped`。新增 **CR-58（`done`）**。验证：`dotnet build` 0 警告 0 错误；**未真机点检**。

- **2026-10-12（贴片改「SetParent 真子窗口」，替换覆盖窗方案）**：用户指出 TrafficMonitor 能真嵌任务栏，
  查证其与 TrayStats 走的是 **`SetParent(自己的窗口, Shell_TrayWnd)`**——`runtime-pitfalls.md` §16 此前
  **漏了这条路**、错误断言"deskband 是唯一真任务栏方案"（本轮补上并更正，见 **CR-59**）。
  评估结论：**WinUI 3 窗口不能 reparent**（内容岛 DWM 合成链会断成灰屏），故**新建一个原生分层窗口**
  （`Services/TaskbarPillWindow.cs` + `Services/TaskbarPillRenderer.cs`），删掉 `TaskbarBatteryWindow.xaml(.cs)`
  与 `Services/TaskbarChipFactory.cs`。三道锁：`SetParent` + `WS_EX_LAYERED` + `UpdateLayeredWindow`（32 位 ARGB）。
  **随之作废/取代的条目**：CR-38（构造函数里 Scale 退化——新实现用 `GetDpiForWindow(任务栏)`，无 XamlRoot 依赖）、
  CR-40（无条件抬 Z 序——子窗口天然在父之上，**不再抢 Z 序**）、CR-43（Win11 透明通道静默失效——贴片已不是 WinUI 窗口）、
  CR-45（`%` 被裁——`TaskbarPillRenderer.ContentRight` 直接按位图实测宽度裁剪）、
  CR-46（点任务栏闪一下——抢 Z 序的产物，成因消失）、CR-47（16ms 拍频耗电——16ms 定时器整块删除）、
  CR-54/CR-55（Win10 白框 / `%` 被裁——旧 WinUI 贴片的病，新贴片无第二层背景）。
  **仍成立、更新证据指针**：CR-39（贴片覆盖区左键被拖动吃掉）、CR-41（竖向/多屏/自动隐藏未真机验证）、
  CR-42（文字色跟应用主题，证据改 `TaskbarPillRenderer.SkinFor`）、CR-48（拖动手感未真机手测）。
  新增 **CR-60**（右键菜单 owner 现为任务栏子窗口，未真机点检）。验证：`dotnet build` 0 警告 0 错误；
  `dotnet test src/tests/VigilWatt.Core.Tests --no-build` **292/292**；枚举窗口确认
  `GetParent(贴片) == Shell_TrayWnd`、`GWL_STYLE` 含 `WS_CHILD` 不含 `WS_POPUP`；贴片内背景与任务栏
  **只差 1 个色阶**（alpha=1 命中层）。

## P0 · 安全 / 数据损坏

| 编号 | 一句话 | 证据（`文件:符号`） | 状态 | 来源 |
|---|---|---|---|---|
| **CR-01** | `LogitechPlugin` 直接实现 `IBatteryPlugin`，绕过基类全部安全机制：无 `WritesEnabled`、无 `CanWrite`、无熔断退避；`HidppIO` 的唯一写入点**无条件**发帧；插件默认启用（`PluginEnabled["Logitech"]`），且 `AppSettings` 里**根本不存在** `LogitechProbingEnabled`。连续失败只标离线、永不停止写入 | `Plugins/Logitech/LogitechPlugin.cs`（`: IBatteryPlugin`、`Poll`）、`Plugins/Logitech/HidppIO.cs`（唯一 `WriteOutput` 调用点）、`Settings/AppSettings.cs`（无 Logitech 开关项） | `done` | 10-06 复核 §4.1；10-10 全检复核仍成立 |
| **CR-02** | 北通**探测路径**没有写入闸门：`BetopPlugin.Probe` 以 `dev: null` 调 `Query` 直接发帧，`BatteryPluginBase.RunProbe` 不检查 `CanWrite`（闸门只在 `PollDevice` 首行）。因 `ProbeCooldown`(30s) < 轮询周期(60s)，探测失败后**每轮必重探、每轮必写一帧**。**精确条件**：候选接口完全静默（`Query` 超时返 null）时成立 | `Plugins/Betop/BetopPlugin.cs:Probe` / `:Query`、`Plugins/BatteryPluginBase.cs:RunProbe` / `CanWrite` | `done` | 10-06 复核 §3.1；10-10 全检复核仍成立（条件收紧） |
| **CR-03** | **共享可变 `DeviceStatus` 让"变化判定"恒为假**（定性**加重**：不止插件推送路径，**所有数据源**都持同一实例）。链路：数据源（BLE/经典/本机电源/插件）原地改**同一个** `DeviceStatus` 后传给 `Upsert`；首建时 `GetOrAdd(id, _ => incoming)` 存的就是这个实例 → `changed = status.BatteryPercent != incoming.BatteryPercent` 比较同一对象**恒 false**。后果：① 历史"变化即写"对已有设备永不触发（曲线只靠 `SafeSample` 定时采样，跳变丢点）；② `DeviceUpdated` 永不触发（UI 只靠 `DevicesChanged` 全量刷新）；③ 骤降保护 `previousPercent` 读到已改写的新值 → **恒不成立**。**例外**：设备若先由墓碑建立条目，则与数据源是不同实例，判定正常——表现取决于"启动时是否有 24h 内历史"，更隐蔽。**根因即 R3** | `Devices/DeviceManager.cs:Upsert`（`GetOrAdd` + `changed` + `previousPercent`）与 `p.StatusPushed += Upsert`、`Plugins/BatteryPluginBase.cs:TryApplyReading` / `RaiseStatusPushed`、`Ble/BleBatteryWatcher.cs:HandleDevice`、`Ble/BluetoothClassicWatcher.cs:HandleDevice`、`Power/SystemBatteryWatcher.cs:Apply` | `done` | 10-06 复核 §2.1；10-10 全检复核**加重**（波及面更广、更隐蔽） |
| **CR-09** | 小组件的事件订阅是三个捕获 `this` 的匿名 lambda，**全文件无解绑**，`WidgetWindow` 非 `IDisposable` → 窗口关闭后仍被管理器强引用。**反复开关小组件会累积泄漏 + 回调叠加**（运行时真泄漏，非退出路径） | `WidgetWindow.xaml.cs`（`DeviceUpdated` / `DevicesChanged` / `Root.ActualThemeChanged` 订阅）、`Views/SettingsPage.xaml.cs:Save_Click`（`Widget.Close(); Widget=null` 后旧实例仍被引用） | `done` | 10-06 §3.3；10-10 全检**上调 P0** |

> CR-03 附带一条注释失实：`Upsert` 里写着「覆盖前记下上一次已知电量……（**同一对象的情况也成立**）」，
> 而恰恰是同一对象的情况**不成立**。属 `docs/dev/comment-style.md` §5 第 3 条禁止的"漂亮的谎"——**已随 CR-03 一并改掉**。

## P1 · 用户可感知的功能错误

| 编号 | 一句话 | 证据（`文件:符号`） | 状态 | 来源 |
|---|---|---|---|---|
| **CR-04** | 历史保留策略只在手动保存设置时执行，**没有定时任务**；UI 文案却写"自动清理"。超期记录从不自行过期 | `Devices/DeviceManager.cs:CleanupHistoryNow`（全仓唯一调用点在 `Views/SettingsPage.xaml.cs` 的保存流程）、`Devices/DeviceManager.cs:StartTimers`（三个 Timer 无清理） | `done` | 10-06 §2.4；10-10 复核仍成立 |
| **CR-05** | `HistoryStore` 未开 WAL、未设 `busy_timeout`，读写共用一把 `_writeGate` → UI 查询会阻塞在写锁上，写入被放大成每条一次隐式事务。**加重**：`Views/HistoryPage.xaml.cs:DeleteAsync` 还在 UI 线程同步跑 `History.Compact()`（VACUUM 全表重写） | `Storage/HistoryStore.cs`（全文件无任何 `PRAGMA`） | `done` | 10-06 §4.4；10-10 复核**加重** |
| **CR-06** | 历史页与统计页在 **UI 线程同步**跑 SQLite 全表统计，无 `Task.Run` | `Views/HistoryPage.xaml.cs:Reload` / `:DeleteAsync`、`Views/StatsPage.xaml.cs:ReloadDeviceList` / `:RenderChart` | `done` | 10-06 §4.4；10-10 复核**加重**（含 VACUUM） |
| **CR-07** | 退出路径不释放资源：`TrayService.Exit()` 只摘托盘图标 + `ShutdownLogging()` + `Environment.Exit(0)`，**`AppServices.Dispose`（含 `Manager.Dispose` / `History.Dispose`）全仓无人调用** → HID 句柄、3 个 Timer、DB 都不确定性释放。**级别说明**：维持 P1 而非 P0——发生在进程终止路径，OS 会回收句柄、不累积；`review-standard.md` §1「资源泄漏=P0」的字面与"退出路径不释放"存在张力，建议后续修订 §1 措辞（区分"累积型泄漏"与"退出型未释放"） | `Services/TrayService.cs:Exit`、`App.xaml.cs:AppServices.Dispose` | `done` | 10-06 §4.4；10-10 复核仍成立（级别经裁决维持 P1） |
| **CR-08** | 全局异常一律 `e.Handled = true` 吞掉，无计数熔断 → 应用会带病继续运行，用户只看到"点了没反应" | `App.xaml.cs` 的 `UnhandledException` 处理器 | `done` | 10-06 §4.4；10-10 复核仍成立 |
| **CR-10** | Elan 插件的只读 feature 扫描**留在正常执行路径上**，违反 `docs/hardware/safety.md` §3 规则 7（"任何诊断/扫描代码不得留在正常执行路径上，哪怕有开关"）。**重新定性**：`FeatureSweep` / `FindBatteryFeature` 均为 `GetFeature`（**只读，不写帧**），故**不是硬件误写**，违规点是"扫描代码未剥离"。`ProbeCooldown` 5 分钟，插件默认启用 | `Plugins/Elan/ElanHeadsetPlugin.cs:Probe` 的 `HidWriteProbe.FeatureSweep` / `FindBatteryFeature` | `done` | 10-06 §1.3 + 复核 §4.1；10-10 重新定性 |
| **CR-12** | 写入许可开关没有 UI：设置页只有 4 个手写 `ToggleSwitch` + 动态生成的**品牌级** `PluginEnabled` 行，`MchoseProbingEnabled` 与 `BetopProbingEnabled` 无控件，只能手改 `settings.json`。**加重**：`BetopProbingEnabled` 默认 true → 写入闸门默认敞开且用户关不掉 | `Views/SettingsPage.xaml`（`PluginPanel`）、`Views/SettingsPage.xaml.cs:AddPluginRow` / `Save_Click`、`Settings/AppSettings.cs` | `done` | 10-06 §3.1；10-10 复核**加重** |
| **CR-37** | **罗技插件被当成"新插件"默认关闭，导致静默功能回归**：第十八轮给罗技新增独立的 `LogitechProbingEnabled`（默认 false），而罗技的发现/读数**全靠写帧、没有可用的"只被动"模式** → "插件开着但写入许可关着"这一组合让罗技**完全发现不了设备**，UI 上还看不出任何异常。根因：`safety.md` 规则 2 的"**新**插件默认关闭"被误用到**既有**功能插件上。**修法**：取消独立写入许可，所有插件统一"**插件开关即写入授权**"（见变更记录第十九轮） | `Plugins/Logitech/LogitechPlugin.cs:DiscoverDevices`（`if (!WritesEnabled) continue;`）、`Settings/AppSettings.cs:LogitechProbingEnabled` | `done` | 10-10 用户实测（罗技 2.4G 鼠标找不到） |
| **CR-53** | **键盘休眠触发熔断后，10 分钟退避把"唤醒检测"也一起挡死**——唤醒后最长约 11 分钟才被重新认领（日志实测盲区 **12 分 52 秒**）。五个环节：① 休眠 → `ReportPollFailure` 攒满 3 次 → `WritesDisabled=true`；② 熔断期内 `MchosePlugin.PollDevice` 首行 `CanWrite` 直接 return null（不写帧、不记账）；③ 键盘唤醒时 dongle 推的 `0x02` 通知按设计调 `RequestConfirm`，但 `RunPendingConfirm` 里 `ConfirmRequested` **已在方法开头被消费**、`PollDevice` 又被熔断挡住 → `ConsecutiveFailures` 不变 → `else if (dev.ConsecutiveFailures > failuresBefore)` 为假 → **唤醒信号被静默丢弃**（熔断期内还零日志，看不出是被挡住）；④ 15 秒发现节拍救不了——`Attach` 对 `_devices` 里已有的路径直接 `continue`，而 dongle 一直在位（该节拍只能发现"新接口"，覆盖不到"已认领但离线"）；⑤ 被动监听也救不了——dongle 不在唤醒时自发推 `0A` 电量帧（`0A` 只在开会话/状态变化时来，会话帧本身是写入）。**同类风险**：北通结构同款但当前未自报失败（现表现为"不标离线"，补齐一致性改动后走的是**已修**的基类路径）；罗技自建了同构熔断，已同步同一张退避表。**已修（2026-10-11）**：① 新增纯逻辑 `Plugins/WriteBackoff.cs`（30s→1m→2m→4m→**封顶 5 分钟**，用户定的上限）；② `CanWrite` 改用递增退避；③ `RunPendingConfirm` 熔断期内也放行设备自发信号触发的核实（≤1 帧/30 秒，未到间隔**不消费请求**），成功即提前解除熔断；④ 被动空闲 tick 新增 `MaybeHalfOpenBreaker`，期满**立刻**半开重试、不再等 60 秒轮询拍；⑤ 熔断落地收进 `TripBreaker`（基类自动记账那条只在首次跨阈值时熔断，否则退避被每轮重置、永不到期）；⑥ `LogitechPlugin` 同步。**验证**：build 0 警告 0 错误；除 `LogTests`（本机已知会卡住）外 **278 用例全绿**（新增 `WriteBackoffTests` 9 条）；**未真机验证**——需人手让键盘睡够 3 分钟再唤醒 | `Plugins/Mchose/MchosePlugin.cs:PollDevice`、`Plugins/BatteryPluginBase.cs:ReportPollFailure` / `CanWrite` / `RunPendingConfirm` / `Attach`、`Plugins/WriteBackoff.cs`、`Plugins/Logitech/LogitechPlugin.cs:Poll` | `done` | 10-11 日常开发（用户复报"迈从休眠后唤醒，程序长时间没识别到"；日志 `app-2026-10-11.log` 20:23–20:36） |
| **CR-54** | **Win10 上任务栏贴片出现不透明白框**：`TaskbarTransparency.Apply` 的 Win10 分支只调 accent（`MakeWindowTransparent`）就 `return`，透明处理整段缺失。**修法两轮**：第一轮按 §17.1 只补「清 Win32 那层」→ **白框照旧**；判别实验（GDI 往客户区填色，可见像素纹丝不动）证明 Win10 上可见的是**内容岛那层**——不显式设透明 `SystemBackdrop` 时它回退成"纯色主题底"（浅色=纯白），与 §13 的 Mica 回退同源。最终把「清两层」收进 `TransparentWindowSurface.Apply`（`ApplyBorderlessChrome` + `ClearWin32Background` + `ClearContentIslandBackground`），**不做 OS 分支**；`TaskbarTransparency.cs` 与 `Native.MakeWindowTransparent` 删除。**已像素级验证**。**⚠ 2026-10-12：贴片已改原生子窗口，不再走这条路**（无第二层背景）；本条修法**现只服务小组件** | ~~`Services/TransparentWindowSurface.cs:Apply` / `:ClearContentIslandBackground`、`TaskbarBatteryWindow.xaml.cs`~~ | `done` | 10-12 用户反馈（贴片 Win10 显示异常）；10-12 方案替换 |
| **CR-55** | **Win10 上贴片最右一格的 `%` 被窗口边缘裁掉**：Win10 分支同样漏了 `ApplyBorderlessChrome` → 窗口残留 `WS_CAPTION|WS_THICKFRAME|…` 非客户区；而 `AppWindow.Resize` 设的是**外框**尺寸 → 客户区比实测内容窄。**已修**：两条分支都跑 `ApplyBorderlessChrome`，并把 `RelayoutCore` 的 `AppWindow.Resize` 改为 `ResizeClient`（按**客户区**定尺寸，双保险）。**实测**：窗口矩形与客户区同尺寸（边框差 0），内容右侧留白 9px。**⚠ 2026-10-12：贴片改原生子窗口后无"外框/客户区"之分**（`UpdateLayeredWindow` 直接按位图尺寸定位），本条不再适用于贴片 | ~~`TaskbarBatteryWindow.xaml.cs:RelayoutCore`~~ | `done` | 10-12 用户反馈（同一截图）；10-12 方案替换 |
| **CR-56** | **桌面小组件在 Win10 上整个变成白方块**（本轮连带发现，用户未报）：`ApplyWin10Chrome` 里的 `DWMWA_NCRENDERING_POLICY = DWMNCRP_ENABLED`（§14 为找回 DWM 投影而加）与透明所需的 `DwmExtendFrameIntoClientArea(-1)`（**整窗算边框**）叠加后，DWM 把这层边框填成不透明底 → 四角 `#FFFFFF`、卡片被包在白底里。**已修**：删掉该调用，小组件改调 `TransparentWindowSurface.Apply`。**代价见 CR-57** | `WidgetWindow.xaml.cs:ApplyWin10Chrome` | `done` | 10-12 连带排查（用户未报） |
| **CR-57** | **Win10 上小组件的"透明"与"DWM 投影"互斥**：逐组合实测（margins × NCRENDERING × accent × `WS_EX_LAYERED`）无解——要投影就得让 DWM 渲染非客户区，而整窗已被 `DwmExtendFrameIntoClientArea(-1)` 算作边框 → 填成白底。本轮取**透明优先**，代价是小组件在 Win10 上失去 DWM 投影（§14 曾专门为"平淡"补过它）。补法方向：**在 XAML 里自绘投影**（窗口四周留余量 + 卡片视觉加 `DropShadow`），**未做** | `WidgetWindow.xaml.cs:ApplyWin10Chrome`、`WidgetWindow.xaml:Root` | `open` | 10-12 实测（`dev/runtime-pitfalls.md` §17.8） |
| **CR-58** | **贴片与小组件的右键菜单点别处不消失（ESC 也无效）**：WinUI 的 light-dismiss 靠**宿主窗口去激活**触发，而贴片为"绝不抢焦点"带 `WS_EX_NOACTIVATE`（§16.3）→ **永不激活、也就永不"去激活"**，菜单弹出后点哪都关不掉；菜单 popup 自己也是 `WS_EX_NOACTIVATE`，键盘焦点不在它身上，ESC 同样送不进去。**已修**：两个窗口的 `Border.ContextFlyout` 删除，改挂 `RightTapped` → 走 **Win32 `TrackPopupMenuEx`**（`Services/AppPopupMenu.cs`，与托盘菜单同一条路，自带"点别处即关"）；弹菜单前 `SetForegroundWindow`、后补 `PostMessageW(WM_NULL)`。**2026-10-12 更新**：贴片改原生窗口后右键由 `WndProc` 的 `WM_RBUTTONUP` → `AppPopupMenu.Show`，**"菜单期间停跟随定时器"那条已删**（子窗口不再抬 Z 序、菜单天然在上）；**新增待验点见 CR-60**。**未真机点检** | `Services/AppPopupMenu.cs`、`Services/TaskbarPillWindow.cs:OnRightClick`、`WidgetWindow.xaml(.cs):Root_RightTapped` | `done` | 10-12 用户反馈（`dev/runtime-pitfalls.md` §18） |
| **CR-59** | **`runtime-pitfalls.md` §16 的结论漏了一条真方案**：10-10 那版断言"**deskband 是唯一真任务栏方案**"，只对比了 deskband / AppBar / 覆盖窗三条，**漏了 `SetParent` 子窗口**——而 TrafficMonitor、TrayStats、多个开源"任务栏歌词"走的正是这条。该错误直接导致贴片走了两个月的覆盖窗弯路（要 16ms 抢 Z 序、自动隐藏靠猜、点任务栏会闪）。**已修（2026-10-12）**：§16 重写为"四条路"，补 §16.1 三道锁、§16.2「不需要抢 Z 序」、§16.7 坐标换算，并在正文留"本节结论曾写错过一次"的更正说明 | `docs/dev/runtime-pitfalls.md` §16 | `done` | 10-12 用户指出 TrafficMonitor 可真嵌任务栏后查证 |
| **CR-60** | **新贴片的右键菜单 owner 是"任务栏的子窗口"，未真机验证**：`AppPopupMenu.Show(owner, ...)` 里 `owner = 贴片 HWND`，而贴片现在是 `Shell_TrayWnd` 的 child；`TrackPopupMenuEx` 的 owner 惯例是顶层窗口，`SetForegroundWindow(子窗口)` 的行为也未实测。菜单能否正常弹出、点别处能否关闭、`TPM_RETURNCMD` 返回值是否正常，**都要人手复看**。若失败，退路是 `GetAncestor(GA_ROOT)` 或改用任务栏 HWND 作 owner | `Services/TaskbarPillWindow.cs:OnRightClick`、`Services/AppPopupMenu.cs:Show` | `open` | 10-12 贴片迁移连带引入 |

## P2 · 可维护性

| 编号 | 一句话 | 证据（`文件:符号`） | 状态 | 来源 |
|---|---|---|---|---|
| **CR-13** | `ReadConnected` 在 BLE 与经典蓝牙两个 watcher 里各写了一份（**逐字节相同**，均 `bool?`）；`Start()` 的属性探测循环 / `HandleRemoved` / `Snapshot()` 也高度重复。判定逻辑已修（CR-D1），**重复本身还在，且面比原条目更大** | `Ble/BleBatteryWatcher.cs:ReadConnected`、`Ble/BluetoothClassicWatcher.cs:ReadConnected` | `open` | 10-06 §2.2；10-10 复核（面扩大） |
| **CR-14** | `HidWriteProbe` 的写入变体矩阵（`Run` / `PingVariants` / `MchoseClassicVariants`）**无任何调用点**（死代码），但仍是潜在写入路径；该类目前只有 `FeatureSweep` 被 Elan 使用 | `Hid/HidWriteProbe.cs`（全仓仅 `ElanHeadsetPlugin` 引用该类） | `done` | 10-06 复核 §4.1；10-10 复核仍成立 |
| **CR-15** | 低电量提醒的档位只降不升：`_lastNotifiedPercent` 记录精确百分比且**永不复位**，判据是 `slot < lastSlot`。阈值 20 时在 20% 提醒一次后，19% 会**再提醒一次**（逐 1% 刷屏），而充满到 100% 再放电到 20% **不会提醒**。**附带 CR-23**：该表不随设备移除/禁用清理 | `Devices/DeviceManager.cs:CheckLowBattery` 的 `_lastNotifiedPercent`、`DropDevicesOf` / `ApplySystemBatterySetting` | `done` | 10-06 §4.2；10-10 复核仍成立（需真机确认影响面） |
| **CR-16** | 文件超出 400 行建议上限（S1）。**实测精确吻合**：`WidgetWindow.xaml.cs` 679 / `Plugins/Logitech/LogitechPlugin.cs` 634 / `Plugins/BatteryPluginBase.cs` 599 / `Plugins/Mchose/MchosePlugin.cs` 584 / `Devices/DeviceManager.cs` 499；**清单补漏**：`Views/BatteryChartControl.xaml.cs` 405 亦超限 | 见左 | `open` | 10-06（S1）；10-10 复核 + 补漏 |
| **CR-17** | 罗技会话一旦打开失败就**永不重试**：`OpenFailed` 置位后不清、`DiscoverDevices` 受 `Io != null` 门控、`HidppIO` 无重连/冷却。**表述收紧**：仅对"接口仍在位但打开失败"（会话被占/权限）成立；**物理拔出接收器再插回**会因 session 被 `TryRemove` 而重建、可重试 | `Plugins/Logitech/LogitechPlugin.cs:Attach` / `DiscoverDevices`、`Plugins/Logitech/HidppIO.cs` | `done` | 10-06 §3.2；10-10 复核（表述收紧，仍需真机验证） |
| **CR-19** | 基类写入开关**默认 true**（`WritesEnabled => true`），新插件不显式覆盖即默认开启写入——与 `safety.md` R1"新插件默认关闭"方向相反，属 fail-open 设计隐患 | `Plugins/BatteryPluginBase.cs:WritesEnabled` | `done` | 10-10 全检（安全） |
| **CR-20** | `settings.json` 用 `File.WriteAllText` **整文件覆盖、非原子写**（无临时文件 + rename），且文件写入在 `lock (CacheGate)` **之外**。进程写一半崩溃/断电 → 文件截断，下次 `Load` 反序列化失败**静默回落默认值**，用户设置丢失 | `Settings/AppSettings.cs:Save` | `done` | 10-10 全检（安全） |
| **CR-21** | 多处 `catch { /* 忽略 */ }` 无日志留痕。多数是关停/竞态/预期失败可接受；但 `ReadInput` 吞掉读失败会掩盖"设备拔出/句柄异常" | `Hid/HidDeviceInfo.cs:ReadInput`、`Plugins/BatteryPluginBase.cs:StopPassiveListen`、`Plugins/Logitech/LogitechPlugin.cs:StopListening`、`Ble/BleBatteryWatcher.cs`、`Ble/ClassicDevNodeReader.cs` | `done` | 10-10 全检（安全） |
| **CR-22** | `Detach` 末尾**无条件**打 `Log.Info("...插件已卸载")`；插件禁用期间 `PollOnceAsync` 每轮调 `Detach()` → **每 60s 刷一条"已卸载"假日志** | `Plugins/BatteryPluginBase.cs:Detach` | `done` | 10-10 全检（代码审查） |
| **CR-23** | 低电量提醒档位表 `_lastNotifiedPercent` 在设备移除/禁用时不清理，条目常驻（与 CR-15 一并修） | `Devices/DeviceManager.cs`（`DropDevicesOf` / `ApplySystemBatterySetting` 未清 `_lastNotifiedPercent`） | `done` | 10-10 全检（代码审查） |
| **CR-24** | 设备状态文案/配色格式化三处各写一份：充电/离线文案、`--%`、"主题色 vs 状态色"判断 | `ViewModels/DeviceRowModel.cs:From`、`WidgetWindow.xaml.cs:BuildRow`、`Services/TrayService.cs:ShowContextMenu` | `done` | 10-10 全检（代码审查） |
| **CR-25** | `MchosePlugin` 自建日志去重三件套（`LogOnceByKey` / `ShouldLogOnce` / `ResetLogOnce`），与 `Utils/Log.cs:InfoOnChange`（自认"通用版"）重复 | `Plugins/Mchose/MchosePlugin.cs`、`Utils/Log.cs:InfoOnChange` | `done` | 10-10 全检（代码审查） |
| **CR-26** | `LogitechPlugin.StableHash` 是 `=> PluginUtils.StableHash(s)` 的无谓直通包装（死代码，S6） | `Plugins/Logitech/LogitechPlugin.cs:StableHash` | `done` | 10-10 全检（代码审查） |
| **CR-27** | `docs/dev/logging.md` §2.1 要求热路径先 `Log.IsEnabled(level)` 短路省 `$"..."` 插值，但 `Log.IsEnabled` **全仓 0 个调用点**；被过滤时仍先做插值 | `Utils/Log.cs:IsEnabled`（0 调用点） | `done` | 10-10 全检（可维护性） |
| **CR-28** | 雷柏被动心跳路径每条状态帧打 Info + `Log.Hex`，活动期约 3s 一条 → 单设备可达约 2.8 万行/天，配合 2MB 滚动快速冲掉历史 | `Plugins/Rapoo/RapooPlugin.cs:TryParsePassiveInput` | `done` | 10-10 全检（可维护性） |
| **CR-29** | 小组件与设备页**无合并/防抖**：每个 `DeviceUpdated` 触发一次 `TryEnqueue` 全量重建（`ChipStack.Children.Clear()` / 重建 `ItemsSource`）；一轮轮询 N 次 `Upsert` → N+1 次全量重建。对照 `TrayService.RequestUpdate` 已用 `Interlocked` 合并 | `WidgetWindow.xaml.cs` 订阅、`Views/DevicesPage.xaml.cs:Refresh` / `OnDevicesChanged` | `done` | 10-10 全检（可维护性） |
| **CR-30** | `HidDeviceScanner.Inspect` 每次扫描对**每个** HID 接口以 `GENERIC_READ\|GENERIC_WRITE` 开句柄只为读元数据；15s 发现节拍 + 60s 主轮询 → 约 96+ 次打开/分。无需写权限时应用只读打开或缓存接口元数据 | `Hid/HidDeviceScanner.cs:Inspect` | `wontfix` | 10-10 全检（可维护性） |
| **CR-31** | 自动更新走 Velopack，更新源由**用户可编辑的** `settings.json.UpdateUrl` 决定，**无签名验证、无来源白名单**。本地可写 settings.json 者可指向恶意 feed → 以当前用户身份任意代码执行 | `Services/UpdateService.cs`、`Settings/AppSettings.cs:UpdateUrl`、`Views/SettingsPage.xaml.cs:CheckUpdate_Click` | `wontfix` | 10-10 全检（安全） |
| **CR-34** | `SettingsPage.Load()` **不可重入**：`PluginPanel.Children` / `_pluginToggles` / `_writePermissionRows` 只增不清（`WidgetMaxCombo.Items` / `LogLevelCombo.Items` 同）。当前仅构造函数调用一次故**不触发**；隐患是 `_pluginToggles` 用字典索引器（重复 key 覆盖、不累积）而 `_writePermissionRows` 用 `List.Add`（累积）——**两者行为不一致**，一旦有人加"重新加载"就会同时出现"UI 重复行 + 保存回写走旧行" | `Views/SettingsPage.xaml.cs:Load` / `:AddPluginRow` / `:_writePermissionRows` | `open` | 10-10 修复轮（收工自审） |
| **CR-35** | `LogitechPlugin` 成功打开接口后**只清 `Io`、不清 `OpenFailed`**。因 `CanRetryOpen` 仅在 `Io == null` 时求值、且 `OpenFailedAt` 过期后恒真，当前**无实际后果**；残留语义会让后续改这段的人误判会话仍处打开失败态 | `Plugins/Logitech/LogitechPlugin.cs:MarkOpenFailed` / `CanRetryOpen` / `Attach` | `open` | 10-10 修复轮（收工自审） |
| **CR-36** | `RequestRefresh` 的 `_refreshQueued` 只在 `TryEnqueue` 回调里复位；`TryEnqueue` 返回 false（窗口/队列已关）时永久卡在 1。当前 `Closed` 已解绑事件、关闭后不再收请求，**无实际后果** | `WidgetWindow.xaml.cs:RequestRefresh`、`Views/DevicesPage.xaml.cs:RequestRefresh` | `open` | 10-10 修复轮（收工自审） |
| **CR-38** | 任务栏贴片在**构造函数**里就跑了一次 `Relayout`，此时 `Root.XamlRoot` 尚未就绪 → `Scale` 退化成 1.0。**非 100% DPI 下首次 `AppWindow.Resize` 用错比例**，窗口尺寸偏小（`Loaded` 后第二次刷新才修正）→ 启动瞬间贴片可能闪一下/先小后大。**2026-10-12 随贴片改原生子窗口而作废**：新实现（`Services/TaskbarPillWindow.cs:Scale`）用 `GetDpiForWindow(任务栏)` 取比例，**不依赖 XamlRoot**，构造期即可算对 | ~~`TaskbarBatteryWindow.xaml.cs:Scale`~~ | `done` | 10-10 第二十轮（收工自审）；10-12 方案替换后消失 |
| **CR-39** | 贴片默认位**紧贴托盘左侧**，而 Win11 该区域正是任务栏应用按钮/溢出区 → 会盖住它们；且贴片覆盖范围内的**左键被拖动逻辑吃掉**（点不到底下的任务栏按钮）。是"钉进任务栏"这一形态的固有代价，不是 bug，但需真机评估是否可接受。**2026-10-12 迁移后仍成立**（子窗口照样盖住那块、照样吃左键），证据改 `Services/TaskbarPillWindow.cs:OnLButtonDown` / `Services/TaskbarLayout.cs:Place` | `Services/TaskbarPillWindow.cs:OnLButtonDown`、`Services/TaskbarLayout.cs:Place` | `open` | 10-10 第二十轮（收工自审） |
| **CR-40** | 贴片**无条件反复 `SetWindowPos(HWND_TOPMOST)`**：Start 菜单 / 任务栏飞出面板打开时（它们同为 topmost），贴片会被抬到它们**之上**，视觉上"穿透"到菜单前面。**2026-10-12 随贴片改原生子窗口而作废**：子窗口天然在父窗口之上、**不再抢 Z 序**，`SetWindowPos(HWND_TOPMOST)` 与 16ms 定时器整块删除（见 `runtime-pitfalls.md` §16.2） | ~~`TaskbarBatteryWindow.xaml.cs:EnsureAbove` / `:Follow`~~ | `done` | 10-10 第二十轮（收工自审）；10-12 方案替换后消失 |
| **CR-41** | 贴片的**竖向任务栏 / 多显示器 / 自动隐藏**三条路径全部**未经真机验证**（本机是底部任务栏、单屏）。代码里写了兜底（竖向时胶囊挂到"桌面那一侧"、`TrayNotifyWnd` 找不到就退回任务栏边缘、厚度 ≤4px 判隐藏），但兜底本身也是静态推断；另有 `TrayNotifyWnd` 在 Win11 上是否仍存在未确认。**2026-10-12 迁移后仍成立**（子窗口方案的自动隐藏更简单：父窗口收起时子窗口跟着走，代码里对"厚度 ≤4px 判隐藏"直接跳过重算），证据改 `Services/TaskbarPillWindow.cs:TryGetTaskbar` / `Services/TaskbarLayout.cs:Place` | `Services/TaskbarPillWindow.cs:TryGetTaskbar`、`Services/TaskbarLayout.cs:Place`（竖向分支） | `open` | 10-10 第二十轮（收工自审） |
| **CR-42** | 贴片去底框后，**文字颜色跟的是应用主题、不是"任务栏实际看起来的明暗"**。浅色主题 + 深色壁纸时（Win11 任务栏半透明、透出壁纸），深色文字/图形在深色任务栏上对比不足。可选缓解：给文字加阴影、或按任务栏采样亮度反色、或退回"极淡的底"。**2026-10-12 迁移后仍成立**，证据改 `Services/TaskbarPillRenderer.cs:SkinFor`（读注册表 `AppsUseLightTheme`，硬编码两套 `Skin`） | `Services/TaskbarPillRenderer.cs:SkinFor` | `open` | 10-10 第二十轮（用户反馈"不需要背景"后的连带取舍） |
| **CR-43** | ~~**贴片的透明通道在 Win11 上静默失效 → 用户第三轮复看"白框还在"**……~~ **2026-10-12 随贴片改原生子窗口而作废**：贴片已不是 WinUI 3 窗口，没有"两层背景"这回事——窗口由自己 `CreateWindowExW` 建、类背景刷为空、内容整块由 `UpdateLayeredWindow` 的 ARGB 位图决定（`runtime-pitfalls.md` §16.4）。**当时的实测结论仍对小组件有效**（`WidgetWindow` 仍是 WinUI 窗口，仍走 `TransparentWindowSurface.Apply`），故本条记录保留为历史 | ~~`Services/TransparentWindowSurface.cs:Apply`~~ | `done` | 10-10 用户第三轮反馈；10-12 方案替换后消失 |
| **CR-44** | **`WS_EX_LAYERED` 这条备选在旧 WinUI 贴片上已实测排除**：`LWA_ALPHA(255)` **无变化**（255 就是不透明）、只加 `WS_EX_LAYERED` **无变化**、运行期补 `WS_EX_NOREDIRECTIONBITMAP` 被系统**直接丢弃**。唯一见效的是 `LWA_COLORKEY`，但有两条硬伤（深色主题挖掉白字、键中像素变点击穿透），故不采用。**⚠ 2026-10-12 重要更正**：**新贴片恰恰就是用 `WS_EX_LAYERED` + `UpdateLayeredWindow` 做到的透明**——旧结论"`WS_EX_LAYERED` 无效"**只对 WinUI 3 窗口成立**，对原生窗口完全有效（这是两套不同的合成路径，别把旧结论外推）。 | ~~`Services/Native.cs`~~ | `wontfix` | 10-10 第三轮实测；10-12 更正适用范围（`runtime-pitfalls.md` §16.1） |
| **CR-45** | ~~**贴片最右一格的 `%` 被窗口边缘裁掉**：窗口宽度靠"字符数 × 8.5"估算……~~ **2026-10-12 随贴片改原生子窗口而作废**：新实现（`Services/TaskbarPillRenderer.cs:ContentRight`）**直接扫描位图最右一个 alpha>1 的像素**来裁掉尾部留白，宽度是按像素实测的，不再有"估算差一两像素"这回事 | ~~`TaskbarBatteryWindow.xaml.cs:MeasureContentWidth`~~ | `done` | 10-10 用户反馈"第二个设备的电量显示不全"；10-12 方案替换后消失 |
| **CR-46** | ~~**点任务栏时贴片会闪一下**~~ **2026-10-12 随贴片改原生子窗口而作废**：闪动的成因是"贴片与任务栏同为 topmost、点任务栏把它抬到贴片之上"，子窗口天然在父窗口之上、**不再抢 Z 序**，成因整条消失（`runtime-pitfalls.md` §17.6） | ~~`TaskbarBatteryWindow.xaml.cs:Follow` / `FollowTickMs` / `GeometryEveryTicks`~~ | `done` | 10-10 用户两轮反馈；10-12 方案替换后消失 |
| **CR-47** | ~~**16ms 拍频的常驻代价未测**~~ **2026-10-12 随贴片改原生子窗口而作废**：抬 Z 序的 16ms `DispatcherQueueTimer` **整块删除**，跟随降为 **1 秒一拍**（`Services/TaskbarPillWindow.cs:FollowIntervalMs`），耗电顾虑不复存在 | ~~`TaskbarBatteryWindow.xaml.cs:FollowTickMs` / `:EnsureAbove`~~ | `done` | 10-10 第三轮（性能与手感的取舍）；10-12 方案替换后消失 |
| **CR-48** | **贴片拖动未真机手测**。**2026-10-12 迁移后仍成立、实现已换**：拖动改走原生 `SetCapture` / `ReleaseCapture` + `WM_MOUSEMOVE`（`Services/TaskbarPillWindow.cs:OnLButtonDown` / `:OnMouseMove`），不再依赖 `WM_NCHITTEST`。**新增待验点**：命中测试按 alpha 判定，整块位图铺了 alpha=1 的"不可见底"（否则留白处拖不动，见 `runtime-pitfalls.md` §16.5），实际手感仍需人手试 | `Services/TaskbarPillWindow.cs:OnLButtonDown` / `:OnMouseMove` | `open` | 10-10 第三轮（收工自审） |
| **CR-49** | ~~`TaskbarTransparency._prevProc` 是**静态单份**，隐含"同一时刻只有一个贴片窗口"的假设~~ **已修（2026-10-12）**：抽出 `Services/TransparentWindowSurface.cs`，旧窗口过程改由 `PrevProcs`（**按 HWND** 存、`WM_NCDESTROY` 清理）承载。小组件与贴片可**同时存在**（`App.xaml.cs` 的 `WidgetEnabled` / `TaskbarBatteryEnabled` 各自独立），单份字段必破。**同日贴片改原生窗口后只有小组件还用这套**（贴片自带窗口过程，无子类化） | `Services/TransparentWindowSurface.cs:PrevProcs` / `:SurfaceWndProc` | `done` | 10-10 第三轮（收工自审）；10-12 实修 |
| **CR-50** | 日志**跨天切换分支无单测覆盖**：`Log.EnsureWriter` 靠 `_currentDate != today` 判断换文件，但 `today` 取自 `DateTime.Now`，测试无法注入时钟，只能覆盖"首写落到当天文件"，覆盖不到"真实跨午夜"。逻辑简单（比对 + 关旧开新），风险低；若日后要测，需给 `Log` 加可注入的时间源（会引入静态可变状态，收益待评估） | `Utils/Log.cs:EnsureWriter` / `:Emit` | `open` | 10-10 第二十一轮（收工自审） |
| **CR-51** | 日志**一天一个文件、无大小上限**：Debug 档下单日文件可能很大（雷柏单设备被动心跳实测可达约 2.8 万行/天，见 CR-28）。用户明确选择"一天一个文件"，故不设大小滚动；总量靠 `LogRetentionDays`（默认 14 天）兜底 | `Utils/Log.cs:EnsureWriter`（无 `MaxBytes`） | `wontfix` | 10-10 第二十一轮（用户决策） |
| **CR-52** | `App.xaml.cs:ShutdownLogging` 的注释仍称 `AppServices.Dispose` 全仓无人调用；实际 `TrayService.Exit` 已调用 `App.Services.Dispose()`。行为正确，但注释会误导后续维护者对退出释放链路的判断 | `App.xaml.cs:ShutdownLogging`、`Services/TrayService.cs:Exit` | `open` | 10-11 全项目架构评估 |

### wontfix 理由（CR-30 / CR-31）

- **CR-30**：收益被高估了。句柄打开/关闭是纯 CPU 的本地系统调用（每次亚毫秒级），96 次/分 ≈ 0.1~0.2% CPU，**没有可观测收益**。而"改成只读打开"会**改变枚举面**——`GENERIC_READ` 能打开 `GENERIC_READ|GENERIC_WRITE` 打不开的受保护 collection（键盘/鼠标），即设备列表会变多，等于在**无法真机验证**的前提下改变发现行为。本项目已因写入事故立过两次安全条款，用"扩大枚举面"换 0.2% CPU 不划算。
  least-privilege 的诉求实际由插件层的严格过滤承担（`IsCandidate` 按 VID/usage/报告长度三重过滤，"多滤不会漏设备，少滤可能碰别家硬件"）。
  **若日后要修，正确做法是"按 DevicePath 缓存接口元数据"（把 96 次/分降到 24 次/分），而不是改打开权限**——那条路不改枚举面，但收益同样有限。
- **CR-31**：个人自用工具，更新源由用户自己填写，且 `CheckUpdate_Click` 在下载前有明确的确认对话框（不是静默更新）。真正的缓解手段是**给发布包做代码签名**——那属于发布流程（`scripts/build-installer.ps1`）而不是应用代码，不在本次修复范围。
  风险本身真实：任何能写 `settings.json` 的人都能让应用拉一个恶意 feed。**已知风险、接受**。

### wontfix 理由（CR-44）

- **CR-44**：`WS_EX_LAYERED` 这条路**实测无效**（四种写法逐个试过，见条目）。唯一见效的 colorkey 变体有两条硬伤：深色主题下贴片文字本身就是白色、会被键色一起挖掉；键中像素变点击穿透，贴片留白拖不动，而它又紧挨任务栏按钮区、误点会启动别的程序。而 extend-frame 路线已能做到**像素级完全透明**，没有理由为更差的方案引入这两条硬伤。

### wontfix 理由（CR-51）

- **CR-51**：用户诉求就是"按自然日分文件、不要全挤一个文件"，明确选择了"一天一个文件"。再加一层大小滚动会重新引入 `app-日期.1.log` 这类文件名，与"一天一个文件"的直觉冲突。日志总量由保留天数（默认 14 天）控制；`LogLevel` 默认 `Info` 时单日量很小，只有临时切 `Debug` 才会放大。

## Info · 记录备查（非缺陷）

| 编号 | 一句话 | 证据（`文件:符号`） | 状态 | 来源 |
|---|---|---|---|---|
| **CR-32** | `.vpk-github-token`（GitHub PAT）**存在于仓库根工作目录**，但**未入库**（`git check-ignore` 命中 `.gitignore`）。风险：若仓库目录被同步/备份/打包分发会泄露。建议改用环境变量 `VPK_GITHUB_TOKEN` | `.gitignore:.vpk-github-token`、`scripts/publish-github.ps1` | `wontfix` | 10-10 全检（安全，Info） |
| **CR-33** | 单实例互斥量用**本地命名 Mutex**（无 `Global\` 前缀），同会话进程可抢先占名 → 应用无法启动（本地 DoS）。个人工具可接受 | `App.xaml.cs:_singleInstanceMutex` | `wontfix` | 10-10 全检（安全，Info） |

### wontfix 理由（CR-32 / CR-33）

- **CR-32**：文件**未入库**（`.gitignore` 已覆盖），风险只存在于"仓库目录被同步/备份/打包分发"这一非默认场景。改环境变量的收益是把密钥从磁盘移走，但会让发布脚本多一个"环境变量没设"的失败模式，对个人工具不划算。**已知风险、接受**；若日后要分享仓库目录，先删该文件。
- **CR-33**：本地命名互斥的"抢占"需要同一用户会话里已有恶意进程——那种前提下攻击者能做的事远多于占个互斥量名。改 `Global\` 反而会引入跨会话（含服务/其他用户）可见性，收益为负。**已知风险、接受**。

## 重构 / 去重候选（非缺陷，对标 S1–S6；收益降序）

> 这些是"可抽离封装 / 去重"的具体方案，不构成 bug；做的时候按行执行。完整论证见全检报告第 3 节。

| 编号 | 抽什么 | 抽到哪 | 收益 / 风险 | 关联 |
|---|---|---|---|---|
| **D1** | 两个 watcher 的 `ReadConnected` + `HandleRemoved`（标离线/时间戳/日志/Invoke）+ `Start()` 属性探测循环 + `Snapshot()` | `Ble/WatcherProps.cs`（纯静态）或 `Ble/DeviceWatcherHost` 基类 | 消除 S3、堵"语义再分叉"（10-06 P0 根因）、`ReadConnected` 可单测（R8）；风险低 | CR-13 |
| **D2** | 设备状态文案/配色格式化（充电/离线文案、`--%`、主题色 vs 状态色） | `Models/DeviceStatusFormatter`（Core 纯逻辑 → 可单测） | S3/DRY + 可测（R8）；风险低-中（UI 回归） | CR-24 |
| **D3** | `BatteryChartControl` 几何/刻度/命中（`TimeToX` / `PctToY`、X 轴档数、`OnPointerMoved` 最近点查找） | `Views/ChartGeometry.cs`（纯函数 → 可进测试项目） | S2 + S1（405 行）；hover 命中可单测；风险低 | CR-16 |
| **D4** | `WidgetWindow` 业务逻辑（设备排序、行宽估算、显示条数封顶） | `ViewModels/WidgetLayoutModel`（纯逻辑） | S2 + S1（679 行，最大超限）；风险低-中 | CR-16 |
| **D5** | `SettingsPage.Save_Click` 的副作用编排（autostart 注册表、小组件开关生命周期、backdrop、`CleanupHistoryNow`） | `ViewModels/SettingsViewModel` | S2（355 行）；风险中（窗口生命周期需回归） | CR-16 |
| **D6** | 插件"发帧→窗口内读→解析"骨架（`RapooPlugin.ListenStatus`、`MchosePlugin.ListenCzPush` / `TryKeyboardV1` / `PollKeyboardV1` / `PollQhw`、`BetopPlugin.Query` 的 deadline+读循环） | `Plugins/HidReadLoop.cs`（`static T? ReadUntil<T>(...)`） | S3、统一读窗口语义；风险中（日志/首帧计数需参数化） | 新增 |
| **D7** | Mchose 日志去重三件套 → `Log.InfoOnChange` | — | S3/DRY；风险低 | CR-25 |
| **D8** | 充电状态映射（`MchosePlugin.MapCharging` / `ParseKeyboardV1Charge`、`BetopPlugin.MapCharging`、"100%→Full"） | `Models/ChargingStateMap.cs`（纯函数） | S3；风险低 | 新增 |
| **D9** | 删除 `LogitechPlugin.StableHash` 直通包装 | — | S6/S3；风险低 | CR-26 |
| **D10** | ~~插件写入许可读取模式统一（基类加 `ProbingFlag(Func<AppSettings,bool>)`）~~ **已作废** | — | 第十九轮取消独立写入许可，"插件开关即授权"取代了本方案 | CR-12 |

### 本轮（2026-10-10 修复轮）对重构候选的处理

| 编号 | 状态 | 说明 |
|---|---|---|
| **D2** | `done` | 抽出 `Models/DeviceStatusFormatter`（`IsCharging` / `IsChargingOrFull` / `PercentText` / `ChargeSuffix`），列表页 / 小组件 / 托盘 / `DeviceManager` 四处改用；补 `DeviceStatusFormatterTests`。**色值仍留在 App 层 `UiPalette`**（要 `Windows.UI.Color`，Core 不能碰）。 |
| **D7** | `done` | 三件套收进 `Log.Once(level, key, message)` / `Log.ResetOnce(key)`（**带级别**，因为"非 QHW 通道"那条原本是 Debug 级）。语义保持"一次 + 显式复位"，**没有**硬套 `InfoOnChange`——后者是"状态变化才记"，无法表达"恢复正常后再记一条"。 |
| **D8** | `done` | 抽出 `Models/ChargingStateMap.FromChargingFlag`；`BetopPlugin.MapCharging` 与 `MchosePlugin.ParseKeyboardV1Charge` 改为薄封装（协议位掩码 `0x10` 留在插件里）；补 `ChargingStateMapTests`。 |
| **D9** | `done` | 直通包装已在修复轮删除。 |
| **D10** | **作废** | 修复轮曾改为"插件声明写入许可"（`IBatteryPlugin.WritePermission` + 基类 `WritesEnabled => WritePermission?.Get() ?? false`）。**第十九轮整体取消该机制**——改为"插件开关即写入授权"（`WritesEnabled => IsWritesEnabled(Name)`），见变更记录第十九轮。 |
| **D1** | `open`（部分落地） | **逐字节相同的部分已抽离**：`Ble/WatcherProps.cs`（`Name` / `Connected` 常量、`ReadConnected`、`ReadBattery`），两个 watcher 共用并补 `WatcherPropsTests`。**基类化（`Start()` 探测循环 / `HandleRemoved` / `Snapshot()`）未做**——理由见下。 |
| **D3/D4/D5/D6** | `open` | 未做，理由见下。 |

### 为什么 D1（完整形态）/ D3 / D4 / D5 / D6 与 CR-16 本轮没做

这几条**都是纯结构调整**（不修 bug、不改行为），共同点是：

1. **收益是"可维护性"，成本是"回归风险"**，而本项目**没有任何 UI 自动化测试**——`WidgetWindow`（679 行）、`SettingsPage`（355 行）、`BatteryChartControl`（405 行）的拆分只能靠人工点检回归，而我这一轮**不能启动应用、不能碰真机**（`safety.md` §2：会话被占会粘住 dongle）。
2. **D1 完整形态要动两个 watcher 的 550+ 行**（`Start` 的事件接线、`HandleDevice` 的差异分支），而这两处正是 10-06 那次 P0 的现场——在无法运行时验证的情况下重排，属于"用新的不确定性换掉旧的不确定性"。
3. **CR-16 是结果不是原因**：文件超长本身不产生缺陷；它的拆法依赖 D1/D3/D4/D5/D6 落地。先拆 D1 再拆文件才对，顺序反了会白拆两次。

**建议的下一轮顺序**：D1 完整形态（含补 R8 单测）→ D3（纯函数，可单测，零 UI 风险）→ D8 已做 → D4/D5 → 最后按 D1~D5 的结果一次性处理 CR-16。

## 已关闭（保留记录，说明闭环确实有产出）

| 编号 | 一句话 | 证据 | 状态 | 关闭方式 |
|---|---|---|---|---|
| **CR-D1** | BLE 三条互相矛盾的在线判定路径（默认 `false` 覆盖现状、`Removed` 不发离线） | `Ble/BleBatteryWatcher.cs` 的 `HandleDevice` / `HandleRemoved` | `done` | 已改为 `bool? connected`（属性缺失时沿用现状）+ `Removed` 发布离线 |
| **CR-D2** | `Upsert` 合并时漏拷 `Kind` / `Protocol`，导致设备图标被后续更新保留旧值 | `Devices/DeviceManager.cs:Upsert` | `done` | 改由 `Devices/DeviceStatusMerger.Apply` 统一合并（见 `docs/project/changelog.md` 对应轮次） |
| **CR-D3** | 插件禁用后设备不下线，托盘/小组件继续显示 | `Devices/DeviceManager.cs` 的插件卸载路径 | `done` | 插件禁用时立即移除/下线设备（见 `docs/project/changelog.md` 对应轮次） |
| **CR-11** | `docs/hardware/safety.md` §3 规则 2 与实现**完全脱节**：规则原文写"一切写入受 `MchoseProbingEnabled` 开关控制（默认 false）"，实际北通走 `BetopProbingEnabled`（**默认 true**）、罗技无任何开关 | `docs/hardware/safety.md` §3 规则 2 vs `Settings/AppSettings.cs`、`Plugins/Betop/BetopPlugin.cs` | `done` | 2026-10-10 第十七轮：`safety.md` §3 规则 2 已重写为"每插件写入必须有自己的开关，判定入口唯一 `CanWrite`→`WritesEnabled`"，并新增 §3.1 逐插件对照表。**文档与实现的脱节已消除**；规则想保护的缺口本体是 CR-01，与本条分开跟踪。全检复核确认已修 |
| **CR-18** | 文档假指针：`README.md` / `handoff.md` / `experience.md` / `changelog.md` / `debugging.md` 共 6 处把 `safety.md` 的会话排他内容引成**不存在的子节 §2.2**（该文件只有 §1–§4） | 上述 5 个文档 | `done` | 2026-10-10 第十七轮：6 处全部改为 `§2`（事故二）；并脚本核验全仓 `§N` 引用，其余指针全部真实（见 `docs/project/changelog.md` 第十七轮） |

> **2026-10-10 修复轮关闭的 25 条（CR-01~CR-09/10/12/14/15/17/19~CR-29 + D2/D7/D8/D9/D10）就地标 `done`**，
> 见上方各表的"状态"列；关闭方式统一记在 `docs/project/changelog.md` 的修复轮小节，不在此重复。

## 待办：本次未核实的部分

2026-10-10 全检已把上一版遗留的四条**全部核实**，结论如下（不再列为"未核实"）：

1. ~~`SafeSample` 无去重/节流~~ → **属实**（每轮凡在线且有读数一律 `Insert`，设计意图即"无变化也写点保曲线连续"，非 bug；但每次抢 `_writeGate`，见 CR-05）。
2. ~~跨线程共享对象无锁导致 `int?` 撕裂读取~~ → **部分属实**（写写/读写确无锁保护，数据竞争属实；x64 上 `int?` 撕裂罕见，主导后果是 CR-03 的变化判定失效而非撕裂）。
3. ~~小组件刷新无合并~~ → **属实**（见 CR-29，已修）。
4. ~~BLE `PollConnectedAsync` 串行 `await` 拖长整轮~~ → **属实**（`foreach … await ReadBatteryViaGattAsync`，且 `PollOnceAsync` 持 `_passGate`；受 `_lastGattAttempt` 2 分钟节流限制影响面）。

**仍未实测（需真机，不阻塞静态结论）**：

- `history.db` 的实际日增速与 UI 卡顿程度（CR-05/06 修完后应明显缓解）；
- CR-15（低电量档位）修完后的实际提醒次数——**改的是纯逻辑，但仍建议插拔一次真机确认**；
- CR-17（罗技会话）10 分钟退避重试的真机表现——本机无 VID_046D 设备，**无法验证**；
- CR-02 的"每轮必写一帧"依赖"接口完全静默"前提；
- **第十九轮需真机确认**：罗技在"插件开关打开"后能否恢复发现设备（用户配置 `PluginEnabled.Logitech=true`，理论上应恢复）；**迈从的行为变化**——第十八轮"`MchoseProbingEnabled=false` ⇒ 只被动监听"在第十九轮变为"插件开 ⇒ 允许写帧"，用户当前 `PluginEnabled.Mchose=true`，需确认主动写帧可接受（迈从的探测写入有干扰设备的先例）。
- **UI 路径全部无自动化覆盖**：`RequestRefresh` 合并刷新、设置页插件行生成——只能靠真机点检。
  CR-34/35/36 正是"静态可见、运行时不可验"的一类，故收工自审只记录不修（理由见 `docs/reviews/archive/fix-round-2026-10-10.md` §5）。
  CR-34 里的 `_writePermissionRows` 已随第十九轮删除，但 `Load()` 不可重入本身仍在。
- **第二十轮（任务栏贴片）部分行为已实测**（2026-10-10 第三轮补测）。**已证实**：
  ① `WS_EX_NOACTIVATE` 的窗口调 `Window.Activate()` 能正常加载并显示 XAML 内容（贴片内容正常渲染）；
  ② `TrayNotifyWnd` 在 Win11 26200 上**仍存在**（`FindWindowExW` 实测命中，贴片落在托盘左侧）；
  ③ 透明化彻底（贴片内外背景像素逐字节相同，无残留底框）；④ 双设备时宽度自适应正确（89px → 186px）。
  **仍未测**：CR-38（非 100% DPI 下启动瞬间的尺寸跳变——本机 150%，但启动瞬间未逐帧观察）、
  CR-39（贴片是否压住任务栏按钮，需人手看）、CR-41（竖向任务栏 / 多显示器 / 自动隐藏）、
  CR-42（深色主题下的文字对比）、CR-47（16ms 拍频的耗电）、CR-48（拖动手感）、CR-49。
  > **2026-10-12 更新**：上段里 CR-38 / CR-47 已随"贴片改原生子窗口"作废（`done`）；
  > 新增待验 **CR-60**（右键菜单 owner 是任务栏子窗口）。**新贴片已实测的部分**：
  > `GetParent(贴片) == Shell_TrayWnd`、`GWL_STYLE` 含 `WS_CHILD` 不含 `WS_POPUP`、`IsWindowVisible`；
  > 贴片内背景与任务栏**只差 1 个色阶**（alpha=1 命中层）。**新贴片仍未测**：
  > 右键菜单（CR-58/CR-60）、拖动（CR-48）、竖向/多屏/自动隐藏（CR-41）、深色主题（CR-42）、
  > 以及"覆盖任务栏按钮区"（CR-39）。
