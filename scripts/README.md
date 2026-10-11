# scripts/ — 临时调试脚本

> **本目录只放一次性 / 临时调试脚本**（PowerShell / Python / 一次性 P/Invoke 探针），用完即删或归档。
> **长期存在的 C# 项目不放这里**：测试项目在 [`src/tests/`](../src/tests/)，工程源码在 [`src/`](../src/)。
> 临时产物用 `%TEMP%`，仓库根目录只放工程文件。新增脚本后在下面补一行索引。

## 脚本索引

| 脚本 | 用途 |
|---|---|
| `code-fingerprint.py` | **代码指纹校验器**（纯标准库）：剥掉注释与空白后对代码 token 序列算 sha256，用来**证明"只改了注释、逻辑一行没动"**。`snapshot <out.json>` 记基线 / `verify <in.json>` 比对（`-xaml` 后缀对 `.xaml` 生效，剥 `<!-- -->` 后比空白归一化文本）。做注释精简、批量重排这类"只该动注释"的改动前先 `snapshot`，改完 `verify` 必须输出 `[OK]` |
| `make-app-icon.py` | **程序图标生成器**（纯标准库，无需 Pillow）：解析式绘制绿色圆角方块 + 白色闪电，超采样抗锯齿，输出 `src/VigilWatt.App/Assets/VigilWatt.ico`（16~256 共 9 档）。`--preview x.png` 出一张放大预览图便于肉眼比对。**改图标配色/字形改这个脚本后重跑即可，不要手改 .ico** |
| `probe-devnode-battery.py` | **PnP 设备节点电量探针**（纯 ctypes + SetupAPI）：枚举全机节点读 `DEVPKEY_Device_BatteryLevel`。经典蓝牙耳机电量就是这么找到的（EDIFIER W820NB 挂在 HFP 网关节点上）；`--all --match X` 可 dump 匹配节点的全部属性，查任何"Windows 能看到但代码读不到"的设备属性都用它 |
| `probe-classic-kind.py` | **经典蓝牙设备类型信号探针**（纯 ctypes + SetupAPI）：枚举 `BTHENUM\*` 节点，打印服务 UUID（实例 ID 前缀）、`DEVPKEY_DeviceDisplay_Category`（如 `Input.Mouse`）与 ClassGuid。用来回答"这台经典蓝牙设备到底是耳机还是鼠标"，`--all` dump 全属性 |
| `test-rapou7.ps1` | 雷柏 dongle（VID_24AE）HID P/Invoke 直测。也是 build.md §7"PowerShell 5.1" 的范例：本机 Add-Type 不支持字符串插值/内联 out/可空注解，要用老式语法 |
| `test-elan-dongle-passive.py` | Elan dongle（04F3:026F）被动监听 90 秒，零写入——看 2.4G 模式下 dongle 是否推状态/电量帧 |
| `test-elan-dongle-wait.py` | 同上，但轮询等待 dongle 插入后再监听 |
| `test-rose-dongle-interactive.py` | ROSE dongle（38FF:0001）交互式被动监听 5 分钟，零写入——耳机按键操作会经 dongle 出现在这里，可当"耳机已连接"探针 |
| `test-rose-dongle-passive.py` | ROSE dongle MI_03（consumer）被动监听 60 秒，零写入 |
| `test-rose-headset-getinfo.py` | FIRST ANGEL USB 直连官方 GET_INFO 查询（report 0xAB，帧格式来自官方升级工具反编译），纯只读 |
| `test-rose-headset-passive.py` | FIRST ANGEL USB 直连 MI_03 被动监听 60 秒，零写入——看耳机是否自发推 0xAB 帧 |

## 约定

- **零写入原则**：协议未摸清前只做被动监听；主动写帧前先读 `docs/hardware/safety.md`（硬件安全红线）。
- Python 脚本依赖 `pip install hidapi`（HID 直测手法见 docs/hardware/debugging.md §2）。
- 脚本里的设备路径/接口号是本机实测值，换设备后以日志（按日 `app-日期.log`）的"HID 元数据"行为准。
- 排查完的脚本：能沉淀成知识的写进 `docs/`，脚本本身可删；值得留的再进索引。
