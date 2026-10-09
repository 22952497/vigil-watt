# 项目文档存档

这一路开发踩过的坑、逆向出的协议、定下的安全规则，全部沉淀在这里。**动手改代码/排查问题前先对号入座**，别再交一遍学费。

## 📁 project/ — 项目本体

| 文档 | 内容 | 什么时候看 |
|---|---|---|
| [handoff.md](project/handoff.md) | **会话交接快照：当前状态、设备清单、待办、给下一个 AI 的注意事项** | 新会话开工第一件事 |
| [architecture.md](project/architecture.md) | 项目结构、模块职责、设计决策与理由、线程模型、数据存储 | 了解/修改整体结构时 |
| [code-flow.md](project/code-flow.md) | 运行时代码流程（启动、轮询主循环、BLE/插件链路、UI 更新、托盘） | 改任何一条链路前 |
| [experience.md](project/experience.md) | 项目经验复盘：走对的路、弯路、调试直觉、重开项目 checklist、待办 | 接手/规划下一阶段时 |

## 📁 dev/ — 开发避坑

| 文档 | 内容 | 什么时候看 |
|---|---|---|
| [build.md](dev/build.md) | 构建与环境坑（XamlCompiler 静默崩溃、双输出路径、发布流程） | 编译失败、换机器重装环境 |
| [runtime-pitfalls.md](dev/runtime-pitfalls.md) | 运行时坑（HID 读取模型、BLE 属性、托盘、DPI、XAML 事件时序） | UI/设备读写行为诡异时 |

## 📁 hardware/ — 硬件协议与安全

| 文档 | 内容 | 什么时候看 |
|---|---|---|
| [safety.md](hardware/safety.md) | **硬件安全规则 + 两次事故复盘**（必读） | 任何要向设备写数据的改动之前 |
| [protocols.md](hardware/protocols.md) | 2.4G 协议手册（迈从 G87 已打通、QHW 新协议、北通手柄、雷柏/Elan 现状） | 动 Mchose/Logitech/Betop/Elan 插件、接入新设备 |
| [debugging.md](hardware/debugging.md) | 调试手法（抓官方软件的包、HID 直测、日志考古、验证清单） | 协议不通、要校准新设备时 |

## 📁 reviews/ — 审查报告

| 文档 | 内容 | 什么时候看 |
|---|---|---|
| [code-review-2026-10-06.md](reviews/code-review-2026-10-06.md) | 全仓代码/架构静态审查（基线 `ddd3b39`）：硬件写入边界、状态同步、BLE 判定、体验与测试缺口，含文件行号与修复顺序 | 规划重构/排优先级、改写入或状态链路前 |
| [code-review-followup-2026-10-06.md](reviews/code-review-followup-2026-10-06.md) | 对上条的独立复核：哪些属实、哪 3 处需修正、报告遗漏了什么（罗技无写入闸门等），以及四阶段优化路线图 | 看完上条要动手改时、排长期重构顺序 |

## 症状速查

| 症状 | 大概率是 | 先看 |
|---|---|---|
| 编译失败 MSB3073 退出码 1、零输出 | XAML 写了不存在的属性 | dev/build.md §1 |
| 跑起来是旧版行为 | 双输出路径，启动错目录 | dev/build.md §2 |
| 写入成功但永远收不到设备响应 | 会话没开 / 悬空读被吃 / 会话被别的进程占死 | hardware/protocols.md §1.3、dev/runtime-pitfalls.md §1、hardware/safety.md §2.2 |
| 读到的电量恒为 100% | 走了 feat8 接口（[3..5] 恒 0x64，不是电量） | hardware/protocols.md §3 |
| 控件在初始化时 NRE | XAML 解析期事件触发 | dev/runtime-pitfalls.md §4 |
| 托盘菜单点不动 | 用了 H.NotifyIcon | dev/runtime-pitfalls.md §5 |
| 小组件显示不全 / 偏小 | 物理像素 vs DIP | dev/runtime-pitfalls.md §6 |
| 官方 M HUB 连不上 dongle | 和本应用互相抢会话 | hardware/protocols.md §1.5 |
| 蓝牙耳机已连接但列表里没有 | 经典蓝牙（A2DP/HFP）不进 BLE watcher，走 BluetoothClassicWatcher | project/code-flow.md §3 |
| 蓝牙耳机有连接但电量恒 `--` | AEP 属性体系拿不到，要读 HFP 网关节点 PnP 属性 `DEVPKEY_Device_BatteryLevel` | dev/runtime-pitfalls.md §3 |
| 设备休眠时电量突然变 1/0 并误报低电量 | dongle 推的休眠帧**类型位是 0x02**（不是电量），旧代码误读 [6] 当电量；现已按类型位识别 + 三层过滤 | dev/runtime-pitfalls.md §12、hardware/protocols.md §1.3 |
| Windows 能看到某属性但代码读不到 | 那是 PnP 设备节点属性，要用 SetupAPI 读，不是 WinRT AEP 属性 | scripts/probe-devnode-battery.py |
| 若水时砂炽天使（38FF:0001）2.4G 恒无电量 | 硬件无通道：dongle 仅「USB 音频 + consumer HID」，HID 的 OUT/FEATURE=0、输入报文无电量字段，官方 RoseLink 亦无电量命令 | hardware/protocols.md §3（已结案，勿再查） |
| 北通手柄（VID_20BC）列表里没有 / 电量恒 `--` | ① 手柄休眠（按一下任意按键）；② **report ID 硬编码**——T9 是 `0x01`/`0x04`/`0x05` 不是 `0x02`，必须从接口 caps 读；③ 厂商接口被官方客户端独占；④ `BetopProbingEnabled` 被关 | hardware/protocols.md §4 |
| 经典蓝牙设备名显示成一长串 `Bluetooth#Bluetooth…` ID | WinRT 的 `DeviceInformation.Name` 在友好名没解析出来时**会把 AEP 设备 ID 当名字**返回；已加占位名识别 + 注册表配对名兜底 | project/handoff.md「经典蓝牙设备显示名修成原始 ID」 |
| 配对但没连接的耳机出现在列表里（还显示着上次的电量） | `Added` 首帧的 `Aep.IsConnected` 可能缺失，旧代码拿"读到电量"当在线依据——而 HFP 节点的电量**断开后仍在**。现已改为查"已连接"集合 | project/handoff.md「第二轮真机复测」 |
| 北通图标像键盘 / 显示名带 `For Keyboard & Mouse` | 显示名是 USB product string；图标按名字匹配时 keyboard/mouse 排在手柄前面被先命中 | project/handoff.md「第二轮真机复测」 |
| 键盘/鼠标/耳机图标全是通用蓝牙图标 | 真机显示名（`Rapoo Gaming Device`/`MCHOSE 2.4G Wireless`/`EDIFIER W820NB 双金标版`）里没有任何关键字，按名字猜必然失败；已改为插件显式声明 `DeviceKind` | project/handoff.md「第三轮：改显式声明设备类型」 |
| 主界面"刷新"按钮点了像没反应 | 三个原因叠加：① `Refresh()` 自己把按钮 `IsEnabled` 设回 true，禁用状态同帧被抹掉、零视觉反馈；② `PollConnectedAsync` 是 fire-and-forget，`RequestPoll` 同步返回时 BLE 侧还没跑；③ `SafePoll` 用 `Interlocked` 标志重入即静默 `return`，撞上自动轮询就什么都不做。已改为可 await 的 `RequestPollAsync` + 转圈/文案反馈 + 复用进行中那一轮 | project/handoff.md「第四轮：刷新按钮」 |
| 本机电量不显示（开了开关也没有） | ① 台式机 / 电池被禁用（`BatteryFlag=128/255`，设计上不显示）；② 设置的「监控本机电量」没开（默认关）；③ 开关刚打开但还没到下一轮轮询 | project/code-flow.md §本机电量 |
| 本机电量显示"已充满"而不是"充电中" | 接电且读到 100% 时按 `Full` 处理（与 Windows 电池图标一致），不是 bug | project/code-flow.md §本机电量 |
| 托盘图标为什么不显示笔记本电量 | 设计如此：托盘"最低电量"只统计无线外设，本机电量不参与（否则笔记本正常放电就染红图标）。本机电量仍进列表/小组件/历史统计/低电量提醒 | project/code-flow.md §托盘 |

数据与日志位置：`%LOCALAPPDATA%\VigilWatt\`（settings.json / history.db / logs\app.log；历史日志 `app.1.log` / `app.2.log`，写入异常兜底 `logs\log-error.txt`）。日志分级 `LogLevel`（默认 `Info`，可选 关闭/详细/信息/警告/错误）在设置页下拉框调整，**改后需重启**，完整设计见 [dev/logging.md](dev/logging.md)。

## 🔧 scripts/ — 临时调试脚本

临时调试脚本（PowerShell / Python / 一次性 P/Invoke 探针）统一放仓库根的 `scripts/` 目录，索引与用途见 [scripts/README.md](../scripts/README.md)，**用完即删**。**长期存在的 C# 项目不放这里**：单测在 [`src/tests/VigilWatt.Core.Tests/`](../src/tests/VigilWatt.Core.Tests/)（xUnit），诊断小工具在 [`src/tests/VigilWatt.BleScan/`](../src/tests/VigilWatt.BleScan/)，工程源码在 `src/`；根目录只放工程文件。
