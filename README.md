# VigilWatt 自用版

仿 [softnav.top/bluetooth](https://softnav.top/bluetooth/) 的 Windows 无线设备电量监控工具，仅保留个人需要的基础功能。

## 功能

- **蓝牙 (BLE) 电量**：自动发现已连接的 BLE 设备，直读 GATT Battery Service (0x180F/0x2A19)
- **经典蓝牙 (A2DP/HFP) 电量**：耳机/音箱走 HFP 免手操作网关节点的 `DEVPKEY_Device_BatteryLevel`（与 Windows 设置页同源），并显示连接状态
- **2.4G 设备电量**：按品牌适配私有协议（见下方"2.4G 协议适配状态"）
- **历史统计**：SQLite 存储，设备页/统计页查看 24h / 7 天 / 30 天电量曲线（指标摘要卡 + 悬停读数）
- **历史管理**：独立页面查看库大小/条数/设备数，按设备删除历史（勾选批量 + 二次确认 + 删除后 VACUUM 收缩文件）
- **托盘图标**：纯 Win32 实现（Shell_NotifyIcon），图标实时显示最低电量设备的百分比数字；左键打开主窗口，右键菜单查看全部设备/退出；新图标默认收在任务栏"^"溢出区，可拖到常驻区
- **低电量提醒**：低于阈值（默认 20%）弹托盘气泡
- **桌面小组件**：无边框悬浮电量条（普通窗口层级，不置顶），可拖动，不进 Alt-Tab/任务栏
- **开机自启**：HKCU Run 注册表键

不做：游戏内显示 (RTSS/AIDA64/Game Bar)、HTTP 数据接口、主题中心、语音包、账号体系。

## 运行

- 开发调试：`dotnet build VigilWatt.sln -c Debug`，运行 `src\VigilWatt.App\bin\x64\Debug\net8.0-windows10.0.19041.0\win-x64\VigilWatt.App.exe`
- 发布版：`publish\VigilWatt.App.exe`（self-contained，无需安装运行时），建议固定到任务栏或加入自启
- 测试：`dotnet test src/tests/VigilWatt.Core.Tests`（67 个用例，改读数过滤器 / V1 帧解析 / 北通帧解析 / 经典蓝牙名称识别 / 设备类型判定 / 历史库删除统计后必跑）

数据与日志位置：`%LOCALAPPDATA%\VigilWatt\`（settings.json / history.db / logs\app.log / tray.ico）

## 2.4G 协议适配状态

| 品牌 | 通道 | 状态 |
|---|---|---|
| 迈从键盘 MCHOSE G87 (dongle VID_41E4&PID_2001) | report 0x13 会话协议：开会话 `[13,05,01,…,sum]` → 电量查询 `[13,4A,01,…,sum]` → `[5]=电量%、[6]=充电标志`；dongle 自发推送 `[13,0A,…]` | ✅ **本机实测工作**（电量=35% 与键盘指示一致） |
| 迈从新世代 QHW 协议（M HUB WEB 逆向，report 0x4D + 命令 0x0904） | `[4D,01,03,len,cmdLo,cmdHi,biz,seq,…,XOR]`，getBattery 响应 payload `[0]=充电 [1]=电量` | 已实现，供新 dongle（3837:3033 等）使用 |
| 北通手柄 BETOP（鲲鹏20 `PID_5191` / T9 `PID_5073`） | 厂商接口 `usage_page 0xFF00`。鲲鹏20：report `0x02` + header `0x15` 查询，响应 `[2]=电量%、[4]&0x0F=充电标志`；T9：不响应查询，解析 Col04 被动流 `[23]`（4-bit 等级×8，满值 120=100%）；`[23]==0x50` = 手柄未连接占位帧 → 标离线 | ✅ 已实现；**T9 已真机验证（2026-10-06）**，含"手柄关机只剩 dongle"判离线；鲲鹏20 待真机确认 |
| 罗技 Logitech (VID_046D) | HID++ 1.0/2.0（Unifying/Bolt/Lightspeed/USB 直连），级联 0x1004 → 0x1000 → 寄存器 0x07/0x0D | ✅ 按协议实现，待真机验证 |
| 雷柏 Rapoo (VID_24AE) | vendor 接口结构接近 HID++，ping 无响应，feature 0x08 有响应 | ⚠️ 实验性，需抓包校准 |
| Elan 接收器 (VID_04F3，时砂等耳机) | feature 0x05 回固件版本号，电量命令未知 | ⚠️ 纯观察模式（只记日志不认领，避免误报） |

### 迈从协议要点与安全规则

- dongle 配置接口是 `usagePage 0xFF02` 的 20/20 vendor 集合（report ID 0x13），帧校验 = 前 19 字节之和。
- **开会话命令必须先发**（官方 M HUB WEB 连接时发送 `13 05 01`），否则 dongle 对一切查询静音。
- **dongle 会话被崩溃进程占住时所有人都会静音**（含官方软件）——重新插拔接收器恢复。
- 充电标志语义未完全校准（0x10 位=充电中是确定项）；插上充电线看日志 `状态字节` 即可补全。
- 安全规则：只发官方软件会发的帧，禁止任何命令/ID 扫描；连续 3 次查询失败自动停写。
- 本应用与官方 M HUB 同时连 dongle 可能互相抢会话（表现为 HUB 连不上），关掉一方即可。

## 结构

```
src/
  VigilWatt.Core/          # 无 UI 核心库
    Ble/BleBatteryWatcher      # DeviceWatcher + GATT 直读
    Ble/BluetoothClassicWatcher# 经典蓝牙 A2DP/HFP：连接状态 + 设备显示名（占位名识别 + 注册表配对名兜底）
    Ble/ClassicAudioBattery    # 经典蓝牙电量：HFP 网关节点 DEVPKEY_Device_BatteryLevel（SetupAPI）
    Hid/                       # Win32 HID P/Invoke（CfgMgr32 枚举、hid.dll I/O）、从 caps 读 report ID/报文长度、探测矩阵
    Plugins/
      IBatteryPlugin           # 插件契约（反射自动发现，新增品牌=加一个类文件）
      BatteryPluginBase        # 基类：冷却/熔断/生命周期/稳定Id
      BatteryReadingFilter     # 读数异常过滤：无效值 + 双向变化幅度（挡休眠伪读数）
      DeviceClaimRegistry      # 中央认领（同一接口只归一个插件）
      Logitech/                # HID++（接收器会话型，含 Rapoo 实验支持）
      Mchose/                  # 键盘 0x13 会话协议（本机实测）+ QHW 0x4D 协议（M HUB WEB 逆向）
      Betop/                   # 北通手柄：厂商接口 0x15 状态报告 + T9 被动流（含纯逻辑 BetopCodec）
      Elan/                    # Elan 接收器观察模式
    Devices/DeviceManager      # 聚合、采样、低电量判定
    Storage/HistoryStore       # SQLite（写入/查询/按设备删除/VACUUM）
  VigilWatt.App/           # WinUI 3 主程序
    MainWindow（导航）+ Views（设备/统计/历史管理/设置）+ WidgetWindow + Services（托盘/ICO/自启）
  tests/
    VigilWatt.Core.Tests/  # Core 纯逻辑 + 存储单测（xUnit，67 个用例，链接 Core 真源文件）
    VigilWatt.BleScan/     # 诊断小工具：蓝牙适配器能力 + 已配对经典/LE 设备探查
publish/                       # 发布产物（self-contained，已 gitignore）
scripts/                       # 临时调试脚本（.py/.ps1，用完即删，索引见 scripts/README.md）
doc/                           # 项目文档与经验沉淀（见 doc/README.md）
  reviews/                     # 审查报告（全仓代码/架构审查）
```

## 已知坑与经验沉淀（doc/）

完整的结构说明、代码流程、经验复盘、坑清单、协议手册、硬件安全规则、调试手法都在 **[doc/](doc/README.md)**，动代码前先看：

- [doc/project/handoff.md](doc/project/handoff.md) — **会话交接快照（新会话先读）：当前状态、待办、注意事项**
- [doc/project/architecture.md](doc/project/architecture.md) — 项目结构、模块职责、设计决策、线程模型
- [doc/project/code-flow.md](doc/project/code-flow.md) — 运行时代码流程（启动/轮询/BLE/插件/UI/托盘每条链路）
- [doc/project/experience.md](doc/project/experience.md) — 项目经验复盘（弯路、调试直觉、重开项目 checklist）
- [doc/dev/build.md](doc/dev/build.md) — 构建与环境坑（XamlCompiler 静默崩溃、双输出路径、发布）
- [doc/dev/runtime-pitfalls.md](doc/dev/runtime-pitfalls.md) — 运行时坑（HID 单悬空读、BLE 属性、托盘、DPI）
- [doc/hardware/safety.md](doc/hardware/safety.md) — **硬件安全规则 + 事故复盘（写入类改动必读）**
- [doc/hardware/protocols.md](doc/hardware/protocols.md) — 2.4G 协议手册（迈从 G87 会话协议、QHW 新协议、雷柏/Elan 现状）
- [doc/hardware/debugging.md](doc/hardware/debugging.md) — 调试手法（抓官方软件的包、HID 直测、验证清单）
- [doc/reviews/code-review-2026-10-06.md](doc/reviews/code-review-2026-10-06.md) — 全仓代码与架构审查（基线 `ddd3b39`）：硬件写入边界、状态同步、测试缺口与修复顺序
- [doc/reviews/code-review-followup-2026-10-06.md](doc/reviews/code-review-followup-2026-10-06.md) — 上条的独立复核（哪些属实/需修正/遗漏）与四阶段优化路线图

最常踩的几条速记：

1. **构建必须用 .NET 8 SDK**（SDK 8.0.425 实测通过）；机器上只有运行时没有 SDK 时用 `winget install Microsoft.DotNet.SDK.8`
2. **XAML 里写了不存在的属性（如 NumberBox 的 Min/Max，应为 Minimum/Maximum）会让 XamlCompiler 静默崩溃**（MSB3073 退出码 1，无任何报错输出）
3. 项目目录下不能有多余的 .xaml（会被自动 glob 成 Page 编译项）
4. 托盘是纯 Win32 实现（Shell_NotifyIcon + 消息窗口 + TrackPopupMenu），不要用 H.NotifyIcon
5. `dotnet build` 产物分 `bin\Debug\`（直接 csproj）与 `bin\x64\Debug\`（sln）两套，始终用 sln 构建、从 `bin\x64\...\win-x64\` 启动
6. HID 读取必须"单悬空读"模式（HidDeviceInfo.ReadInput），每次超时后重开读会吃掉设备响应
