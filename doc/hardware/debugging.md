# 调试手法手册

按优先级排序。**核心原则：让官方软件去和硬件说话，我们只旁听**——比任何猜测都快、都安全。

## 0. 日志考古（永远第一步）

先翻 `%LOCALAPPDATA%\EasyBluetooth\logs\app.log`：接口扫描清单（VID/PID/usage/长度）、历次探测的原始报文、错误都在里面。过去的探测结论可能已经回答了当前问题（2026-10-05 的"邮箱扫描无响应"结论就是这样从旧日志里直接复用的）。

常用 grep：

```bash
grep -a "迈从" app.log | tail -30          # 迈从全部动作
grep -a "HID 元数据\|usage=" app.log       # 接口清单
grep -a "ERR \|WARN " app.log | tail       # 最近错误
```

设备/设置：`%LOCALAPPDATA%\EasyBluetooth\settings.json`（PluginEnabled / MchoseProbingEnabled）、history.db（SQLite，samples 表）。

## 1. 抓官方软件的包（浏览器 WebHID monkey-patch）★主力手法

不装 USBPcap、不碰硬件，让官方 M HUB WEB 自己驱动真机，我们记录每个字节。

### 1.1 启动带调试端口的 Edge

```bash
"/c/Program Files (x86)/Microsoft/Edge/Application/msedge.exe" \
  --remote-debugging-port=9333 --remote-allow-origins='*' \
  --user-data-dir=<独立目录> --no-first-run "https://www.mchose.com.cn/#/home"
```

（`pip install websocket-client` 后用 python 走 CDP；`--remote-allow-origins` 必加，否则 WS 403。）

### 1.2 注入 HID 探针（关键：要在页面脚本运行前）

用 `Page.addScriptToEvaluateOnNewDocument` 注入，包住四条通道：

- `HIDDevice.prototype.sendReport` → 记 out
- `HIDDevice.prototype.getFeatureReport` / `sendFeatureReport` → 记 featIn/featOut（**官方电量可能走 feature 读**，漏包这个会一无所获）
- `addEventListener('inputreport')` 和 `oninputreport` setter 都要包（不同代码路径用不同写法）
- `HIDDevice.prototype.open` 记录 collections（report ID 布局白送）

字节全部 push 进 `window.__frames`，事后 `Runtime.evaluate("JSON.stringify(window.__frames)")` 收走。

### 1.3 过 HID 选择器弹窗（native，CDD 点不到）

- 点页面"连接设备"按钮必须用 `Input.dispatchMouseEvent`（真实鼠标事件，满足 user activation）。
- 弹窗是独立 Win32 窗口（标题含"想要连接到 HID 设备"）：`EnumWindows` 按标题找 → `PostMessageW(WM_LBUTTONDOWN/UP)` 点设备行和"连接"按钮（坐标从截图量取，客户区坐标 = 截图坐标 × client/位图尺寸比，顺带完成 DPI 折算）。
- 弹窗失焦会自动关闭：点完连接按钮的同一脚本内立刻处理，别跨脚本调用。

### 1.4 截图弹窗内容

- **必须 `PrintWindow(hwnd, dc, 2)`（PW_RENDERFULLCONTENT）**：浏览器窗口是 GPU 合成，`GetWindowDC + BitBlt` 出来全黑；屏幕 DC 抓屏又会被上层窗口遮挡。
- python 进程先 `SetProcessDpiAwarenessContext(PER_MONITOR_AWARE_V2)`，否则多 DPI 下 GetWindowRect 坐标是虚拟化的，截出来张冠李戴。
- PIL 一行转 PNG 后用看图工具读。

### 1.5 注意事项

- **会话排他**（safety.md §2.2）：抓包时本应用必须退出；页面刷新会弄脏 dongle 会话，抓完把 Edge 整个关掉。
- CDP 驱动页面用的是独立 user-data-dir，不会污染用户浏览器配置；但 `taskkill msedge.exe` 会连用户的 Edge 一起杀——**先提醒或让用户自己关**。
- 官方抓到的帧先和公开逆向文档（arutkowski00/mchose-hid-bridge 的 K99 V1）对照，命名/校验逻辑能对上再写进插件。

## 2. HID 直测（拿到帧之后的验证）

`pip install hidapi`，比手搓 SetupAPI/CtecTypes 快十倍：

```python
import hid, time, threading
target = [d['path'] for d in hid.enumerate(vendor_id=0x41E4) if b'Col01' in d['path']][0]
h = hid.device(); h.open_path(target)
# 后台线程 h.read(64, timeout_ms=150) 收报文
h.write([0x13, 0x05, 0x01, 0, ...])   # 只发手册里记载的帧
```

坑：自己用 ctypes 调 SetupAPI 枚举路径很容易错（cbSize/对齐/DPI），直接 hidapi 枚举；PowerShell Add-Type 只支持 C# 5（dev/build.md §6）。

## 3. 排查"零响应"的顺序

1. **谁持有会话？**（safety.md §2.2）关掉本应用、浏览器里的 M HUB、官方桌面版，再看。
2. 帧校验对不对？（0x13 协议 = 前 19 字节和；QHW = 从 ver 字节起 XOR）
3. 会话开了没有？（0x13 协议必须先 `05 01`）
4. report ID 接受吗？（WriteFile 报"参数错误" = 描述符里没这个 ID，换通道）
5. 读取端是单悬空读模式吗？（dev/runtime-pitfalls.md §1）

## 4. 验证清单（改完协议代码必做）

1. `taskkill` 旧实例 → sln 构建 → **从 `bin\x64\...` 启动**（build.md §2）
2. 日志出现 `认领迈从设备 ... 电量=NN`，NN 与键盘实际指示一致（FN+B 看键盘灯）
3. 等 1 个轮询周期（1 分钟）+ 1 个采样周期（5 分钟），`history.db` 的 samples 表出现新行且百分比正确
4. 拔掉 dongle → 设备离线；插回 → 自动恢复上线
5. 涉及充电标志的改动：插充电线看 `状态字节` 日志再下结论

## 5. 修改硬件交互代码前

重读 safety.md。所有写帧逻辑必须有出处（本文档/protocols.md/官方抓包），并过 `MchoseProbingEnabled` 开关 + 3 次失败熔断。
