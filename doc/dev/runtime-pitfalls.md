# 运行时坑

## 1. HID 读取必须用"单悬空读"模式（HidDeviceInfo.ReadInput）

**症状**：写入成功，但响应"永远收不到"。

**根因**：HID 的 ReadAsync 超时后并不会取消——旧读仍挂在驱动队列上。如果每次超时后开一个新读，设备迟来的响应会被旧读吃掉，新读继续干等。

**正确姿势**：每个句柄生命周期只保留一个 pending read：

```csharp
private Task<int>? _pendingRead;
public byte[]? ReadInput(TimeSpan timeout)
{
    if (_pendingRead == null) { _readBuffer = new byte[len]; _pendingRead = _stream.ReadAsync(...); }
    var completed = Task.WhenAny(_pendingRead, Task.Delay(timeout)).GetAwaiter().GetResult();
    if (completed != _pendingRead) return null;   // 超时，旧读继续挂着
    ...
}
```

**别用阻塞式 `ReadFile` 循环 + 每次重开**，也别用 `CancelIo` 后立刻重读（驱动队列行为不定）。

## 2. HID 其余小坑

- `HidP_GetCaps` 的成功码是 **0x00110000**，不是 0。判错会把正常接口当失败。
- 键盘/鼠标等系统标准 collection（如 dongle 的 MI_00）被 Windows 保护，CreateFile 打不开是正常的，不要当故障修。
- dongle 上的 consumer/system 等小接口（in=3/out=0 之类）**没有输出通道**——插件认领候选必须排除它们（MchosePlugin.Probe 开头的 `OutputReportLength < 20` 守卫），否则发帧时数组越界崩溃（踩过：IndexOutOfRangeException）。
- WriteFile 的输出缓冲长度必须正好等于接口的 OutputReportLength；report ID 不在描述符里时报"参数错误"（ERROR_INVALID_PARAMETER）——这个报错本身就是"该接口不认这个 report ID"的证据（41E4:2001 不认 0x4D 报文即由此发现）。

## 3. BLE 电量属性名在本机 Windows 上不可用

Windows 11 26200 上 `System.Devices.AepBattery_Level` 和 `System.Devices.Aep.Battery_Level` 都无效（CreateWatcher 报 COMException 0x8002802B）。BleBatteryWatcher 用属性候选列表逐个回退（只有 `System.Devices.Aep.IsConnected` 可用），电量直接走 GATT 读 Battery Service（0x180F/0x2A19），8 秒超时。别再尝试加新属性名，GATT 直读是可靠路径。

2026-10-06 补充：**经典蓝牙 selector 下同样不可用**（BluetoothClassicWatcher 首个候选 `AepBattery_Level` 也抛"找不到元素"，自动回退）。即本机任何 AEP 电量属性都拿不到。

**但经典蓝牙耳机的电量并非拿不到——换属性体系就行（2026-10-06 打通）**。Windows 11 设置页给耳机/音箱显示的电量来自 **PnP 设备节点属性**，不是 AEP 属性：

| 项 | 值 |
|---|---|
| 属性名 | `DEVPKEY_Device_BatteryLevel` |
| KEY | `{104EA319-6EE2-4701-BD47-8DDBF425BBE5}, 2` |
| 类型 | `DEVPROP_TYPE_BYTE`（也可能 UINT16/UINT32，读的时候按类型分支） |
| 挂在哪 | HFP 免手操作音频网关节点：`BTHENUM\{0000111E-…}_LOCALMFG&005D\7&1D3D8B&0&0CAEBDC6CDA7_C00000000` |
| 怎么读 | 只能用 SetupAPI：`SetupDiGetClassDevsW` → `SetupDiEnumDeviceInfo` → `SetupDiGetDeviceInstanceIdW` → `SetupDiGetDevicePropertyW`（WinRT `DeviceInformation` 拿不到 PnP 属性） |

实测 EDIFIER W820NB 双金标版读数 60%，与设置页逐字一致。要点：

- 实例 ID 末段 `&<12位十六进制>_C…` 就是设备 MAC（`0CAEBDC6CDA7`）；AEP ID 形如 `Bluetooth#Bluetooth<适配器MAC>-<设备MAC>`，取**最后**一段 MAC 才是设备本身，别取错。
- 过滤条件用 `BTHENUM\` 前缀，把 BLE（`BTHLE\`）节点排除，免得和 GATT 通道重复上报。
- 纯读、零写入，不打开设备、不建立连接；整表枚举一次约几毫秒，**做 5 秒缓存**再挂到每分钟的轮询上。
- 属性名别再造新词（`System.Devices.BatteryLevel` 之类在 WinRT 侧同样取不到），P/Invoke SetupAPI 是可靠路径。
- 排查脚本：`scripts/probe-devnode-battery.py`（纯 ctypes，`--all` 可 dump 任意节点全属性）。

## 4. XAML 事件在解析期间就会触发

ComboBox 设 `SelectedIndex="0"` → `SelectionChanged` 在 `InitializeComponent()` 内部、x:Name 字段尚未赋值时就会触发。**事件处理器开头必须判空**（StatsPage.RenderChart 的 NRE 即此因）。

## 5. 托盘：纯 Win32，别用 H.NotifyIcon

H.NotifyIcon 在 WinUI 3 代码创建场景下：右键菜单点不动、退出失灵、图标转换内部抛异常。已彻底移除，TrayService 用纯 Win32：`Shell_NotifyIconW`（NIF_MESSAGE|ICON|TIP|INFO）+ 隐藏消息窗口 + `TrackPopupMenuEx(TPM_RETURNCMD)`（弹菜单前先 `SetForegroundWindow`）。相关注意：

- 改名的 DllImport 包装（如 `GetCursorPosRaw`）必须加 `EntryPoint = "GetCursorPos"`，漏了运行时 EntryPointNotFoundException。
- 模拟验证托盘回调可以往消息窗口 PostMessage CallbackMessage，日志能看到"收到托盘回调"即链路通。

## 6. DPI：AppWindow.Resize 是物理像素

XAML 内容按 DIP 布局，125%/150% 缩放下直接用 DIP 尺寸调用 Resize 会系统性压小窗口（小组件"看不全"的根因）。必须乘 `Root.XamlRoot.RasterizationScale`（XamlRoot 未就绪时回退 1.0）：

```csharp
double Scale => Root.XamlRoot?.RasterizationScale is > 0 ? Root.XamlRoot.RasterizationScale : 1.0;
AppWindow.Resize(new SizeInt32((int)(widthDip * Scale), (int)(WindowHeight * Scale)));
```

小组件还需：`WS_EX_TOOLWINDOW`（不进 Alt-Tab/任务栏）、`DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Nearest)` 收边。

## 7. WinAppSDK 1.7 API 名称

- 是 `DisplayAreaFallback.Nearest`（没有 GetDisplayAreaFallback、没有 NearestNeighbor）
- 无边框用方法 `OverlappedPresenter.SetBorderAndTitleBar(false, false)`（没有 IsBorderless 属性）

## 8. 外部程序/浏览器脚本调试的坑

见 debugging.md（PrintWindow 与 GetWindowDC 的区别、DPI 虚拟化坐标、HID 选择器弹窗自动化）。

## 9. 历史页（StatsPage）切换"没反应"的两类原因（2026-10-05 实修）

1. **图表轴自适应数据而不是所选时间窗**：BatteryChartControl 旧版把 X 轴缩到数据起止，历史不足 24h 时"24 小时/7 天/30 天"渲染像素级相同，看起来像档位失灵。修法：`SetData` 接受显式 domain（from/to），轴强制用所选窗口，空数据显示"暂无采样"提示。
2. **周期性重建下拉框 ItemsSource 打断用户**：`ReloadDeviceList` 挂在 `DevicesChanged`（每 60 秒必发一次）上，重建 ItemsSource 会关闭打开中的下拉框并把选择弹回。修法：设备集合没变（按存活对象引用比较）就不重建；顺带 ItemsSource 直接放 DeviceStatus + DisplayMemberPath（按引用保选择，还修了重名设备 FirstOrDefault 串台的隐患）。
3. 构造函数里订阅 `Manager.DevicesChanged` 必须配 `Unloaded` 解绑——导航每次 `Frame.Navigate` 都新建页面实例，不解绑则旧页泄漏、回调叠加。
4. **`DisplayMemberPath` 写错属性名 → 下拉项静默空白**（不报错）：换 ItemsSource 条目类型时（如 DeviceStatus → 自定义 record）路径必须同步改。

## 10. 前端美化实修的新坑（2026-10-05）

1. **x:Bind 把 null 绑到 Foreground → 文字整段不可见**（不报错）：本地 null 值会压过隐式样式的默认前景刷。DeviceRowModel.PercentBrush 一度在"正常电量"分支返回 null，右侧大百分比直接消失。修法：模型属性保证非空，正常态显式给 `TextFillColorPrimaryBrush`（从 `Application.Current.Resources` 取，自动适配明暗）。
2. **横向 StackPanel 放在 Grid 的 `*` 列里会溢出**：StackPanel 不可收缩，长设备名 + 徽章会把同行后面的 Auto 列（百分比）整个顶出可视区。行内"名称+徽章"结构要用嵌套 Grid（`*,Auto,Auto`）+ `TextTrimming="CharacterEllipsis"`。
3. **`Microsoft.UI.Win32Interop` 没有 `GetDpiForWindow`**：主窗口构造期取 DPI 直接 P/Invoke user32 的 `GetDpiForWindow(hwnd)`（配合 §6：`AppWindow.Resize` 传物理像素）。
4. **UI 自动化验证（PowerShell 驱动 WinUI 窗口）三个坑**：
   - Git Bash 写出的 .ps1 是无 BOM UTF-8，PowerShell 5.1 按 GBK 解析，脚本里的中文字面量全部变乱码 → `PropertyCondition` 按名称找元素永远 NOT FOUND 且不报错。**脚本必须带 UTF-8 BOM**（`printf '\xEF\xBB\xBF' > file` 再追加）。
   - 窗口只是 TOPMOST 但不是前台时，第一次合成点击会被"激活窗口"吞掉，导航点击看似无效。**先点标题栏/先 SetForegroundWindow 激活，或对目标连击两次**。
   - 窗口被最小化后 `IsWindowVisible` 仍为 true，但 UIA BoundingRectangle 返回 -32000 系坐标、GetWindowRect 同理——读数先判负再做点击。

## 11. DispatcherQueue 回调里抛异常 = 进程静默消失（2026-10-06 实修）

症状：进程无任何日志、无 UnhandledException 记录地消失；WER 归档（`C:\ProgramData\Microsoft\Windows\WER\ReportArchive\AppCrash_*`）里是 `combase.dll`、异常码 `0x80004005 (E_FAIL)`。事件日志 Application 里没有对应条目。

根因：**`DispatcherQueue.TryEnqueue` 回调里未捕获的异常不经过 `Application.UnhandledException`**（它只覆盖 XAML 框架的消息循环），而是直接在 combase fail-fast。本次两个触发点：
1. 对已挂树但未参与布局 pass 的元素手动调 `Measure()`（小组件测高）——直接原生崩溃。
2. `Application.Current.Resources["不存在的键"]` ——WinUI 的 ResourceDictionary 索引器对缺失键**抛异常**（不像 WPF 返回 null），在回调里抛 = fail-fast。

防御：
- 所有 `TryEnqueue` 的回调方法体整体 `try/catch` + `Log.Warn`（WidgetWindow.RefreshContent 已如此）。
- 代码里按 key 查 `Resources` 前先确认键存在；拿不准就用固定色值/常量，别赌主题资源键名。
- 排查这种死法：WER ReportArchive 按进程名 grep，能拿到故障模块和异常码；`tasklist //FI "IMAGENAME eq xxx.exe"`（过滤形式）比 `tasklist | grep` 可靠，后者在 Git Bash 管道下会间歇性漏报。

## 12. 休眠伪读数：电量突然变 1 或 0 → 误报低电量（2026-10-06 加固）

**症状**：键盘（或鼠标）休眠、或快休眠前，监控里电量突然变成 `1`（偶尔 `0`），弹出低电量告警，一会儿又恢复。

**根因**：设备进入休眠/关机的瞬间，dongle 会推一帧**占位读数**——真机 66% 时推 `pct=1`。旧代码只在解析层挡了 `pct == 0`，`1` 直接漏进 UI 和低电量判定。更深层的问题是：**整条链路对"电量突变"没有任何合理性校验**，任何解析出的数字都会被无条件采信。

**加固三层（都在代码里，改协议解析时别绕过）**：

0. **真凶已抓到（2026-10-06 12:02 实测）**：休眠帧不是"电量位=1"，而是一个**类型位不同的推送帧**——
   正常电量推送 `13 0A 01 00 04 **05** 42 01 …`（0x05=电量，[6]=66%），休眠时推
   `13 0A 01 00 04 **02** 01 …`（0x02=设备状态通知，[6]=0x01 是 payload 不是电量）。
   旧代码不看下标 5 的类型位、直接读下标 6，于是把 payload 的 0x01 显示成 1%。
   **正确做法：先认类型位**（`MchoseV1Codec.ParsePush`），并把这个 0x02 帧当"设备已休眠"信号
   立即标离线（基类 `RequestOffline`）。原始帧两边的校验和都对得上，可自洽验证。
1. **解析层**（`MchosePlugin` + `MchoseV1Codec`）：按类型位识别（见上）；另有 `InvalidPercentFloor = 1`
   兜底（≤1 一律无效）。判无效后返回 null → 计入熔断失败 → 3 次后设备标离线，符合"键盘确实睡了"。
2. **基类层**（`BatteryPluginBase.Poll` → `BatteryReadingFilter`）：每设备一个过滤器实例，两条规则——① 数值低于 2（即 0/1）或 >100 直接判无效，永不采信；② 与上次采信值相比涨跌超过 20 点则本轮不采用，需**持续 5 分钟**才采信（回弹即清零计时）。这是通用的，挡所有插件的任意异常值，涨跌双向都管。基准**不随离线清空**。
3. **告警层**（`DeviceManager.CheckLowBattery`）：低电量值比上次已知值低超过 20 点时暂缓提醒一轮。最后一道闸。

**注意**：`0` 和 `1` 都不会是真实电量——低电量阈值是 20%，正常放电到 1% 之前早就提醒过了，所以拒绝它们不会漏掉任何真实告警。

**单测**：`src/tests/VigilWatt.Core.Tests/`（xUnit，18 个用例），帧解析用例全用真机原始帧，含休眠帧回归用例。改解析或过滤器后跑 `dotnet test src/tests/VigilWatt.Core.Tests`，全绿即通过。
