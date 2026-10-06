# 2.4G 协议手册

所有结论都是真机验证或官方软件抓包所得，**改协议代码前先读本文，不要重新猜**。最后更新：2026-10-06。

## 1. 迈从键盘 G87（dongle VID_41E4&PID_2001）—— ✅ 已打通，应用在用

### 1.1 接口地图

| 接口 | 用途 |
|---|---|
| MI_00 | 标准键盘（系统保护，打不开，按键走这里） |
| MI_01&Col01（usagePage 0xFF02，in/out=20 字节） | **配置/电量通道**，report ID 0x13 |
| MI_01&Col02/Col03/Col05 | consumer / system / mouse，无输出通道，别碰 |
| MI_01&Col06（usagePage 0xFF04，feature=8） | "邮箱"，**不是电量**（见 §3） |

### 1.2 帧格式（20 字节 output report，report ID 0x13）

```
[0]=0x13  [1]=命令  [2]=参数  [3..18]=0x00  [19]=sum(前19字节) & 0xFF
```

校验就是前 19 字节简单求和截断。

### 1.3 会话时序（关键！）

1. **开会话**：`13 05 01 00×16 19`
   ACK：`13 05 01 00 0A 03 00 00 00 02 1C 01 … 45`，随后常跟一条自发推送。
   **不开会话，dongle 对一切查询静音。** 实测 dongle 会记住会话状态，重启后（或句柄重开）重发一次也无妨。
2. **电量查询**：`13 4A 01 00×16 5E`
   响应：`13 4A 01 00 02 <电量%> <充电标志> 00×12 84`
   - `[5]` = 电量百分比（实测 35% 时 = 0x23，与键盘指示一致）
   - `[6]` = 充电标志：`0x10` 位 = 充电中（确定）；实测 35% 放电时 = 0x01。已充满/其餘位语义**待插充电线校准**（看日志 `状态字节`）
   - **`≤1` 一律视为无效读数**（真实电量到不了 1：低电量阈值是 20%，早该提醒过了，拒绝它不会漏告警）
3. **自发推送（report 0x13，`[1]=0x0A`）**：`13 0A 01 00 04 <类型位> <载荷> … sum`
   **`[5]` 是消息类型位，必须先认它，再决定 `[6]` 怎么解释**：

   | 类型位 `[5]` | 含义 | `[6]` 是什么 |
   |---|---|---|
   | `0x05` | 电量推送 | **电量百分比**（实测 66% = `0x42`），`[7]` = 充电标志 |
   | `0x02` | 设备状态通知（实测 = 键盘休眠/关机瞬间） | **payload，不是电量**（实测 `0x01`） |
   | 其他 | 未知类型 | 忽略 |

   > **血泪教训（2026-10-06 挖到真凶）**：旧代码不看类型位、直接读 `[6]` 当电量，于是把休眠帧
   > `13 0A 01 00 04 **02** 01 …` 的 payload `0x01` 显示成 1%，触发误报低电量。识别到 `0x02`
   > 后把它当"设备已休眠"信号**立即标离线**（`BatteryPluginBase.RequestOffline`），比等 3 次
   > 通信失败（约 3 分钟）快得多。原始帧两边校验和都自洽，可自证。
   > 解析代码：`MchoseV1Codec.ParsePush`（`V1PushKind`），真机帧回归用例在 `src/tests/VigilWatt.Core.Tests/`。

轮询实现：`MchosePlugin.PollKeyboardV1`（每分钟 1 条 4A 查询 + 顺带解析 0A 推送）；探测实现：`TryKeyboardV1`（05 01 开会话 → 4A 01）。

### 1.4 已证伪的通道（别再试）

- **feat8（Col06，feature 0x06）**：`06 <命令回显> <连接模式 01=有线/02=2.4G> 64 64 64 B5 6A`。[3..5] 恒为 0x64（=100），**不随电量变化**——旧版读出 100% 的错误来源。SetFeature 只是回显写入字节。
- **经典鼠标协议（feature 0x11）/ Rapou7 V2（64 字节 output 0x4D）**：此 dongle 物理上不支持——feature 通道数量为 0（HidD 返回 err=1）、输出只有 20 字节。Rapou7/offline-mchose 项目只覆盖迈丛**鼠标**（A7/L7/M7/AX5/A5/K7），不含键盘，不要再看它。
- **QHW 0x4D 报文**：WriteFile 报"参数错误"，此 dongle 不认（见 §2，那是新一代 dongle 的协议）。

### 1.5 会话排他（运维注意）

dongle 的配置会话一次只服务一个客户端。崩溃进程不关会话 → dongle 假死，**官方软件也连不上**；kill 持有进程或重新插拔恢复（详见 safety.md §2.2）。本应用与官方 M HUB 不要同时连。

## 2. QHW 协议（新一代 dongle，M HUB WEB 前端逆向）—— 已实现备用

来源：官方 www.mchose.com.cn 的 M HUB WEB 前端 JS（QhwCmdQueueItem 类），适用于 0x3837 世代 dongle（官方通用 2.4G dongle = VID 0x3837&PID 0x3033，G87 V2/G75 V2/K87S/K99 V3 共用）。用户现款 41E4:2001 不适用，代码已备好（TryQhw），遇到新 dongle 直接用。

### 2.1 帧格式（report ID 0x4D）

```
[0]=0x4D [1]=ver=1 [2]=flags=3 [3]=payload长度 [4]=cmdLo [5]=cmdHi [6]=bizCode=0 [7]=seq [8…]=payload [8+len]=XOR(从[1]起到最后一个payload字节)
```

WebHID 侧 reportId 自动加首字节；Windows WriteFile 时 [0] 就是要写的 report ID。

### 2.2 命令表（来自官方 JS）

| 命令 | 含义 | 响应 payload |
|---|---|---|
| 0x0900 (2304) | getBaseInfo（连接后第一条） | [0..1]=VID le, [2..3]=PID le, [4]=maxProfile, [10]=连接(1有线/2无线), [11]=充电(0未充/1充电/2充满), [12]=电量%, [13]=gameSafeMode |
| 0x0901 (2305) | getFwVersion（参数 [0]=主固件 / [1]=RF 固件） | parseFwVersion |
| **0x0904 (2308)** | **getBattery** | **[0]=充电状态(0/1/2), [1]=电量%** |
| 0x0906/0x090D/0x0918/0x0921/0x0924 等 | 灯效/配置/宏/心跳 | 见 JS |

连接握手：report 0x4D，数据 `AA 55 AA 55 AA 55`（无 QHW 头、无校验），连接时发一次。

### 2.3 CZ 通道（同代键盘的批量配置协议）

帧：flag=0x55 / 响应 expect=0xAA，`[55, cmd, 0, chk, size, offsetLo, offsetHi, 0, payload…]`（chk 逻辑见 JS）。批量灯效/配置走这条。电量推送：dongle 自发 `[FA, FB, 07, 电量%, 充电标志]`。

## 3. 罗技 / 雷柏 / Elan 现状

### 罗技 Logitech (VID_046D)
HID++ 1.0/2.0 已按 Solaar 逻辑实现（Unifying/Bolt/Lightspeed/USB 直连；电量级联：Unified Battery 0x1004 → Battery Status 0x1000 → 1.0 寄存器 0x07/0x0D）。**本机未发现罗技设备**（用户自述"罗技"实为雷柏），待真机验证。

### 雷柏 Rapoo (VID_24AE) —— V2 世代 status-report7 协议 —— ✅ 插件已落地（纯被动）

**协议出处**（公开逆向，MIT，真机验证过，符合 safety.md 出处要求）：
- [pedro3z0/rapoo-software-linux](https://github.com/pedro3z0/rapoo-software-linux)：逆向自官方驱动 **A HUB 1.0.19** + 官方 Web 驱动 **hub.rapoo.cn** 抓包，VT0 Air Max（dongle 24AE:14A1）实测；
- [D3m0nZOnFire/mousectl](https://github.com/D3m0nZOnFire/mousectl)：VT3 PRO 一代（dongle 24AE:1231）实测。
- 官方设备表（pedro3z0 DEVICES.md）：VT3 PRO V2=14A5、VT0=1410、VT3 MAX=1417、VT0/VT3 Air=14A1 等。**本机 1412 不在表内**，但 PID 区段/帧特征同代 = VT3 二代 dongle（实测本机）。

**旧观测复核**：24AE:1412 的 `GetFeature(0x08) → 02 00…` 正是该协议 feature 8 应答通道的 **ACK 0x02 = Device Busy**；HID++ ping 无响应是正常的——雷柏不是 HID++ 协议。

#### 1. 状态心跳（电量的唯一来源）

dongle 每 ~3 秒自发推送 **input report 0x07（19 字节，hidraw 含 ID 视角）**，2.4G 和有线都在推：

| 字节 | 含义 |
|---|---|
| [0] | 0x07 |
| [1] | 低4位=连接方式（0=2.4G，2=有线）；高4位=sensor 类 |
| [2] | DPI 档位 0..6 |
| [3..4]/[5..6] | DPI X/Y（u16 LE） |
| [7] | **充电标志（本机实测校准 2026-10-05）：0x01=放电、0x02=充电**；充满态的值待采样（100% 时一律判充满兜底）。pedro3z0 文档此字节记为"0=有效"，与本机观测不符，以实测为准。与 mousectl 在 VT3 PRO 一代记录的 1/2 编码同族 |
| [8] | **电量 0..100**（有线=最后已知值，重插后瞬间 ~1% 属正常） |
| [9] | 背光模式 |
| [10] | 键扫率索引 |
| [11] | 轮询率码 |
| [12] | profile 索引 |
| [13..14] | USB PID（u16 LE） |
| [15] | 键/滚轮类型 |
| [16] | RF 类型 |
| [17] | sensor 模式预设 |
| [18] | bit7=RF 已连接，bit0..6=RSSI |

**电量没有查询命令**——官方驱动也是被动听心跳。充电标志已实测校准（0x01=放电/0x02=充电，见上表 [7]）；充满态的原始值待下次充满时看日志 `充电标志` 补全。

#### 2. 其余通道（仅备查，本应用不使用）

- EEPROM 读/写：output report 0x06，帧 `[conn, 0xA4读/0xA5写, len, addr u32 LE, payload…]`（conn：0xA5=无线/0xFF=有线），应答走 feature report 0x08（`[08, ACK, echo…, payload]`，ACK 0x01=OK/0x02=Busy）。**电量不在 EEPROM**。
- VT3 PRO 一代（1231/4431）电量查询：report 0xBA，`[conn, 0xAA, 0, addr=0]` → 应答 `[0xBA, 充电态(1放/2充), %]`。**本机 1412 实测 WriteFile 拒绝 0xBA（描述符无此 ID），二代不走这条**，勿再试。

#### 3. 关键行为：鼠标休眠 = dongle 全静音

鼠标睡着后 dongle 不推心跳、鼠标接口也停报文，被动监听空转是**预期行为**（不是协议错/会话占死）。移动鼠标即恢复推送。据此 RapooPlugin 表现为"用的时候在线，不用就离线"。

#### 4. 已证伪/勿再试

- HID++ ping（短/长、带/不带 ID）：雷柏无响应（非 HID++ 协议）；
- report 0xBA 电量查询（一代协议）：1412 描述符不含此 ID，WriteFile 报参数错误；
- LogitechPlugin 认领雷柏接口：已移交 RapooPlugin（`WritesEnabled=false`，纯被动零写入）。

### 弱水时砂 ROSE FIRST ANGEL（2.4G dongle = 38FF:0001）—— ❌ 2.4G 无电量通道（2026-10-05 完整勘察）

**身份澄清（修正旧记录）**：`ROSE FIRST ANGEL (VID_38FF&PID_0001)` 就是**2.4G dongle 本体**（USB 声卡 + HID 复合设备，MI_00=MEDIA 音频、MI_03=HID），不是耳机或无关设备。旧记录里的 `04F3:026F`（Elan）是另一台设备，与此耳机无关（Elan 插件观察结论仍保留，见下）。官方配套软件 RoseLink（Flutter + BLE 自定义协议）与官方升级助手 first_angel_upgrade.exe 均已逆向。

**结论：2.4G dongle 模式下主机侧不存在电量数据通道。** 逐路证伪：

| 通道 | 勘察结果 |
|---|---|
| dongle MI_03 HID（usage 0x0C/0x01，in=3B，out=0，feat=0） | 只有耳机按键透传（音量/播放）；被动监听多次零帧 |
| dongle HID 端点（物理 64B INT-IN 0x82 / INT-OUT 0x02 存在） | Windows HID 栈 preparsed 全 0 不路由数据，应用/脚本均读不到 |
| USB Audio（if0 控制、if1/2 ISO 流） | 控制接口无 interrupt 状态端点；ISO 纯音频 |
| 官方 0xAB OTA 协议 | 仅在耳机**OTA 升级模式**（重新枚举出 usagePage 0xFFAB 接口）可用；正常模式无处发送；官方工具也只做 OTA 不查电量 |

**官方升级助手协议出土（OTA 模式专用，备查勿日常使用）**：report ID 0xAB（64B），查询帧 `[0x92, seq, TLV...]`，TLV=`[type, len, data]`，type 1=版本 / 3=TWS / 4=状态 / 6=通道；单查 `[0x91, 01, type]`；OTA 握手 `CC AA 55 EE 12 19 E4`；OTA 数据头 `[0xA0, seq, filesize u32...]`。设备响应 `[0x47 'G', 0x4D 'M', cmd, len, payload...]`，cmd 0x87=原始日志 / 0x88=版本(ASCII) / 0x89=OTA 数据 / 0x8A=GUID / 0x8B=GUID 回传。芯片平台 AB123A（Actions），USB 侧电量命令**不存在**。

**RoseLink 电量通道**：BLE 自定义服务 `0000AE00`（AE01=notify 设备→App，AE02=write App→设备），设备协议族 rs_common_v2（FirstAngel 专属 `first_angle_receive_handler.dart`）。不走标准 0x180F。**耳机在 2.4G 模式下不发 BLE 广播**（系统 AEP 枚举 0 台）。
**2026-10-06 更新**：RoseLink 已完成逆向，**其协议内不含电量命令**（原"继续逆向 AE00 命令字节"的待办作废）——即官方 App 侧同样拿不到电量。

**2.4G 模式电量唯一官方途径 = 耳机语音播报**（不经主机）。主机侧与官方 App 侧均已确认无通道，**此设备在本应用中不提供电量显示，勿再投入**。

### 2026-10-06 主机侧复检（只读实测，结论不变）

用户复报"2.4G 读不到电量"，对 `38FF:0001` 做了一轮独立真机复检，全部只读、零写入：

| 检查 | 结果 |
|---|---|
| USB 接口树（CM 枚举父设备全部子节点） | 仅 `MI_00`（USB Audio Device）+ `MI_03`（HID），**无隐藏厂商接口** |
| `MI_03` HID caps（`HidP_GetCaps`） | UsagePage `0x000C` / Usage `0x0001`（Consumer Control），IN=3B，**OUT=0、FEATURE=0** |
| `MI_03` 输入报文内容（`HidP_GetButtonCaps`） | 9 个 consumer 按钮（音量/播放等），**无任何电量值字段**（NumberInputValueCaps=0） |
| PnP 属性（`probe-devnode-battery.py`） | 全机仅 EDIFIER W820NB 有 `DEVPKEY_Device_BatteryLevel`(60%)，**38FF 各节点均无** |
| 蓝牙记录 | 系统中无炽天使的 BLE/经典蓝牙配对记录 |

判据：`OUT=0 / FEATURE=0` ⇒ 主机**物理上无法向 dongle 发起任何查询**（连"问一句"的通道都没有）；输入报文无电量字段 ⇒ 即便被动监听也没有数据源。**2.4G 无解是硬件事实，与代码适配无关**（当前 App 里 `38FF` 本就不在任何插件候选范围，连设备列表都不会出现）。

### Elan (VID_04F3&PID_026F) —— ⚠️ 纯观察（与本耳机无关的另一设备）
`GetFeature(0x05)` 回 ASCII "1010"（固件版本），电量命令未知。插件 ClaimEnabled=false 只记日志。曾被误记为弱水时砂耳机接收器，2026-10-05 已澄清。

## 4. 北通手柄 BETOP（接收器 VID_20BC）—— ✅ 插件已落地

两种设备形态：**鲲鹏20（`PID_5191`）主动查询**（§4.3）、**T9（`PID_5073`）被动状态流**（§4.4）。

**协议出处**（符合 safety.md "只发有出处的帧"）：开源实现 [oitsukiii/betop-battery](https://github.com/oitsukiii/betop-battery)（MIT）——
其 `docs/protocol.md` 明示协议来自对**北通官方 Electron 客户端**的互操作性分析（不含厂商代码），
并在**鲲鹏20（BTP-KP20EB，接收器 `USB\VID_20BC&PID_5191`）**上真机验证
（Windows 11 + 2.4G 接收器，2026-09-29，电量与充电状态均与官方客户端一致）。

### 4.1 接口地图

接收器是复合设备，4 个 HID 接口靠 usage 区分：

| 接口 | usage_page / usage | 用途 |
|---|---|---|
| **厂商** | `0xFF00` / `0x0003` | ⭐ **电量通道**（本插件只碰这个） |
| 鼠标 | `0x0001` / `0x0002` | 鼠标模拟 |
| 键盘 | `0x0001` / `0x0006` | 键盘模拟 |
| 手柄 | `0x0001` / `0x0005` | 手柄本体（XInput 呈现） |

### 4.2 帧格式

收发同构：`[report_id, header, payload…]`，header 一个字节拆两个 4 位字段：

```
bit: 7 6 5 4 | 3 2 1 0
     └ subcmd ┘└── cmd ─┘     header = (subcmd << 4) | cmd
```

命令表（官方客户端内置多型号脚本 `kp20d/kp40b/kp70/t9` 等共用同一族帧结构）：

| cmd | subcmd | header | 含义 | 方向 |
|---|---|---|---|---|
| `0x5` | `0x1` | **`0x15`** | **状态报告（含电量、充电状态）** | 请求 / 上报 |
| `0x5` | `0x2` | `0x25` | 按键/摇杆报告 | 持续上报 |
| `0x5` | `0x5` | `0x55` | 固件版本信息 | 请求 / 上报 |
| `0x2` | `0x1` | `0x12` | 读配置 | 请求 / 上报 |
| `0x2` | `0x2` | `0x22` | 写配置 | 请求 |

> ⚠️ 本插件**只发读类命令 `cmd=0x5`**（即 `02 15` 状态查询），**不碰写配置（`cmd=0x2`）**、不刷固件、不改灯效。

### 4.3 电量查询时序

1. **查询**：帧 `[report_id, 0x15, 0…]`，其余补零到接口输出长度（HID 驱动要求写满声明长度）。
   - ⚠️ **report_id 必须从接口的 HID caps 读**（`HidP_GetButtonCaps` / `HidP_GetValueCaps` → `HIDP_*_CAPS.ReportID`，
     见 `Hid/NativeHid.cs` 的 `ReadReportId`），**不能硬编码**：它是设备/型号自己的描述符决定的
     （鲲鹏20 厂商集合 = `0x02`，T9 = `0x01`/`0x04`/`0x05`）。
     写一个该接口没有的 report ID，HID 驱动直接返回 `ERROR_INVALID_FUNCTION`（"函数不正确"）——这正是"设备列表里不出现北通"的根因。
   - ⚠️ **用户态拿不到原始报告描述符**：`hid.dll` **不导出** `HidP_GetReportDescriptor`（实测 `AttributeError`；
     写成 P/Invoke 会抛 `EntryPointNotFoundException`，被 catch 吞掉后表现为"读不到描述符"）。
     hidapi 的 `get_report_descriptor()` 其实是拿 preparsed data **自己重建**描述符，不是 IOCTL。
     所以这里走 caps 的 `ReportID` 字段——它是 HID 规范里 report ID 的唯一权威来源。
     真机实测 T9 三接口 = `0x01`/`0x04`/`0x05`，与 hidapi 重建结果一致。
   - **写入长度**直接用 `HIDP_CAPS.OutputReportByteLength`，它**已含 report ID 那一字节**
     （T9 Col04 实测 16 = 15 数据 + 1 ID），不需要再 +1。
2. **响应**：`02 15 <电量%> <保留> <充电标志> …`
   - 整帧 `[2]` = 电量百分比（0~100 十进制；`0` 视为未就绪/占位，不接受）
   - 整帧 `[4]` **低 4 位** = 充电标志，非 0 = 充电中（高 4 位是保留位，勿误判）
   - 实测：`02 15 64 00 51 …` = 100% 充电中；`02 15 5D 00 50 …` = 93% 用电池

解析实现：`Plugins/Betop/BetopCodec.cs`（纯函数，单测 `BetopCodecTests.cs`）；轮询/探测：`Plugins/Betop/BetopPlugin.cs`。

### 4.4 T9 变体（`USB\VID_20BC&PID_5073`）—— 不响应查询，走被动状态流

真机（2026-10-06，Windows 11）实测：T9 的厂商接口（Col01/Col04/Col05，report ID `0x01`/`0x04`/`0x05`）
**对 `02 15` 查询全程无响应**（含"写一个接口、读全部三个"的路由测试），也不出现任何 `0x15` 帧。
取而代之：**dongle 在 Col04 上持续推**一帧固定结构的状态流，电量直接就在里面。

帧特征（识别用）：

```
04 02 80 80 80 80 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 78
│  │                                                        └ byte23 = 电量
│  └ 整帧[1] = 0x02（帧头，固定；Col05 摇杆流帧头是变化的 0x46~0x6F，不会命中）
└ report ID
```

**电量编码（4-bit 等级 × 8，满值 120）**：byte23 只出现 8 的倍数，即"电量等级(0~15) × 8"。
换算：`percent = round(raw × 100 / 120)`。

| byte23 | raw | 换算 | 现场含义 |
|---|---|---|---|
| `0x78` | 120 | **100%** | 充满（北通助手显示**满格绿色**；充电后稳定在此值） |
| `0x58` | 88 | 73% | 手柄唤醒 |
| `0x50` | 80 | 67% | 手柄休眠时 dongle 报的占位值 |

- 该字节 **不是** 直接的百分比（`0x78` = 120 > 100 已排除），也**不是** `raw & 0xF7`（曾一度以为 bit3 是充电标志，被"充电/断电后数值不变"推翻）。
- T9 状态流里**未观察到充电标志字节**，故 T9 一律按"用电池"返回；`percent >= 100` 由插件判为"已充满"。
- 其它 T9 旁路设备 `PID_5075`（"T9 For Wireless"）无 report ID、64 字节全 0、无响应 —— 与本通道无关，已排除。

### 4.5 为什么标准 API 读不到

接收器在与 Windows 的标准通道上把自己报告成"有线设备"：
`XInputGetBatteryInformation() → BatteryType = WIRED / BatteryLevel = FULL`。这**不代表真的满电**，
只是不走标准通道上报 —— 所以必须像本插件这样直接与厂商接口通信。

### 4.6 已知行为

- **手柄休眠时无响应**：按一下任意按键即恢复；此时插件表现为离线，属预期（与雷柏鼠标睡着同理）。
- **充电时电量显示 100%**：手柄自己上报的原始数据就是 100%（BMS / 充电电路把测量电压抬高），非解析错误；
  据充电标志判"充电中"，`percent >= 100 && 未充电` 判"已充满"。
- **官方客户端可同时开着**（工具作者实测不冲突；若遇到厂商接口被独占打不开，关掉官方客户端再试）。
- **协议可能随固件更新变化**；适配新型号一般只需改 report_id / 偏移（同一协议家族）。

### 4.7 安全与开关

- 写入受 `AppSettings.BetopProbingEnabled` 控制（**默认开启**——查询即官方客户端自己用的只读命令，风险低）；关掉即完全静默（不认领、不写入）。
- 连续 3 次查询失败由基类自动熔断（10 分钟退避后重试一轮），最坏 1 帧/10 分钟。
- 品牌级总开关 `PluginEnabled["Betop"]`（默认开）。
- 迈从插件已按 VID 排除北通接口（`MchosePlugin.IsCandidate`），避免对北通 dongle 发 QHW / 0x13 帧。

## 5. 官方资料地图（抓包/查协议用）

- M HUB WEB（网页驱动）：https://www.mchose.com.cn/#/home → `#/connectDevice`，支持列表明确含 **G87 Series**
- CZ 键盘驱动 SPA：`https://www.mchose.com.cn/cizhou/CZ_SHARED_DATA/asset-manifest.json`（内含 layout-g87_v2.js）
- 配置中心版本清单：`https://cdn.mchose.com.cn/configCenter/config/global.json`（base64 zip，内含 keyboardConfig 哈希）
- 桌面版 MCHOSE HUB：`https://cdn.mchose.com.cn/MCHOSE_HUB_installer.zip`（Electron，未验证是否支持 41E4 老款）
- 官方设备表（打包在主 JS 里）：G87 V2 有线 = 0x3837:0x2015，官方通用 dongle = 0x3837:0x3033；0x41E4 世代 = Ace60/Ace68/Jet75/Mix87/Zero75 等有线型号 + 老款 dongle（0x41E4:0x2001 不在官方表里，但协议同代）
