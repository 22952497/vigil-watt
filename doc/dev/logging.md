# 日志改造方案

> **状态：方案定稿，代码未改动（2026-10-08）。**
> 本文描述的是**目标形态**。当前 `src/VigilWatt.Core/Utils/Log.cs` 仍是硬编码三级、无级别配置。
> 实施完成后请把本文件改写成《日志设计》：删掉本提示、把 §1「现状」降级为历史背景、删掉 §6「实施步骤」中已完成的部分。

## 1. 现状（基线）

### 1.1 实现

全部日志走自研的极简类 `src/VigilWatt.Core/Utils/Log.cs`（**没有**引入 Serilog / NLog / `ILogger`）：

```csharp
public static void Info(string message)  => Write("INFO", message);
public static void Warn(string message)  => Write("WARN", message);
public static void Error(string message, Exception? ex = null) => Write("ERR ", ...);
```

- 输出位置：`%LOCALAPPDATA%\VigilWatt\logs\app.log`（`AppPaths.RootDir` + `logs`）
- 行格式：`2026-10-08 21:15:21.808 [INFO] 雷柏 状态帧: ...`
- 标签宽度都是 6 字符（`[INFO]` / `[WARN]` / `[ERR ]`，ERR 补空格对齐）
- 落盘：常驻一个 `StreamWriter`（`AutoFlush = true`），单文件超 `MaxChars` 就**整文件删除**重开
- `Log.RedirectTo(logDir)` 供单测重定向目录（不重定向会污染真实 `app.log`）

### 1.2 查出的问题

#### 稳定性

| # | 问题 | 位置 |
|---|---|---|
| S1 | **日志失败完全静默**。`catch { }` 把磁盘满、目录无权限、路径被占全部吞掉，日志停了没人知道 | `Log.cs:66-69` |
| S2 | **滚动阈值单位不一致**。`EnsureWriter` 用 `FileInfo.Length`（**字节**）初始化 `_written`，`Write` 累加 `line.Length`（**UTF-16 字符数**）。中文日志实际字节 ≈ 3× 字符数 → 单文件要涨到约 6MB 才触发；常量名 `MaxChars` 与语义也不符 | `Log.cs:19,62,80` |
| S3 | **滚动 = 整文件删除**。`Roll()` 直接 `File.Delete`，刚复现的问题日志会被一次滚动清空，无任何历史留档 | `Log.cs:88-95` |
| S4 | **第二实例日志静默丢失**。`FileShare.Read` 只允许读共享，第二个进程写不进去 → 静默吞掉 → 表现为"日志里没有新启动记录"。该坑已在 debugging.md 记录但未根治 | `Log.cs:82`；`hardware/debugging.md` §"第二实例" |
| S5 | **异常日志破坏"一行一条"**。`$"{message} :: {ex}"` 把多行堆栈塞进单行格式，产生无时间戳裸行，让所有 grep 断行统计失真 | `Log.cs:41` |
| S6 | **无退出收尾**。只有 `AutoFlush` 兜底，没有 `Flush()` / `Shutdown()`；一旦改批量 flush 就必须补退出钩子 | `Log.cs:84`；`App.xaml.cs:42-46` |
| S7 | **全局单锁串行 IO**。所有写入 + Roll 挤在一把 `Gate` 里，日志 IO 直接压在轮询线程上 | `Log.cs:14,55` |

#### 可维护性

| # | 问题 | 证据 |
|---|---|---|
| M1 | **无级别配置**，想降噪只能改代码 | 全仓 grep `LogLevel` / `MinLevel` 零命中 |
| M2 | **只有 3 级，缺 Debug**。协议 hex、接口清单、逐帧转储全走 Info，实测 INFO 占 **98%** | `Log.cs:38-41` |
| M3 | **降噪逻辑散落调用点**。每处各写一遍"变了才打"，新增代码忘了加就刷屏 | `TrayService.cs:31,224-228`、`HidDeviceScanner.cs:10,43-51`、`BetopPlugin.cs:189` |
| M4 | **无来源标记**，看不出是哪家插件打的，只能 grep 中文 | Core 里 12 个文件在打日志 |
| M5 | **日志自身零单测**。`RedirectTo` 是为测试造的 API，反而没被日志测试覆盖 | `src/tests/` 无 LogTests |
| M6 | **纯静态类，无接口** → 无法注入替身、无法静默测试 | `Log.cs:12` |
| M7 | **格式与文档耦合无契约**。debugging.md 的 grep 命令依赖 `[INFO]` / `[ERR ]` 字面格式，格式一改文档全废 | `hardware/debugging.md` §0 |

#### 顺带发现：非本应用格式的裸文本混入

`app.log` 里有 **265 行无级别标签**的裸文本，内容是 WinRT 报错：

```
Property key syntax error.  A property key must either be specified as a canonical
property name (preferred) or be of the form {fmtid} pid.
```

紧跟 `[WARN] BLE watcher 属性集不可用` 之后，来自 `Windows.Devices.Enumeration` 的属性集枚举。代码里没有 `Console.SetOut` / `SetError`，**推断是调试期从 shell 启动时 stderr 被重定向进了 app.log**，不是日志系统写进去的。

排查影响：grep 日志时这些行会被当成"无级别行"。建议排查时先用 `grep -a "\["` 滤掉，或实施阶段 2 时给日志行加统一前缀后自然消除。

## 2. 已定决策

| 决策点 | 结论 |
|---|---|
| 级别档数 | **合并 Trace 与 Debug，只保留一个 `Debug`**（理由：Debug 本来就是最低的监测档，不再往下细分） |
| 默认级别 | **`Info`** |
| 是否引第三方框架 | 不引。自研类约 150 行可控，Serilog/NLog 会撑大发布体积并给 Core 加依赖 |
| 格式兼容 | `[INFO]` / `[WARN]` / `[ERR ]` **一个都不改**，只新增 `DBG `；doc/ 里所有 grep 考古命令必须继续可用 |

> ⚠️ **不要沿用 .NET 的 `[Conditional("DEBUG")]` 语义。**
> `System.Diagnostics.Debug.WriteLine` 带 `[Conditional("DEBUG")]`，Release 编译时整个调用会被编译器删掉；
> `Trace.WriteLine` 靠 `TRACE` 常量。如果照搬，发布版里 Debug 日志会凭空消失，
> 用户在 settings.json 里配 `LogLevel=Debug` 也没用（调用点根本不存在）。
> **本项目的 Debug / Info 只是运行期过滤档位，发布版同样编译进去**，开销靠 `Log.IsEnabled()` 提前短路控制。

## 3. 目标设计

### 3.1 级别与标签

```
Debug < Info < Warn < Error          （Off = 全关哨兵）
```

| 标签 | 级别 | 语义 | 变更 |
|---|---|---|---|
| `DBG ` | Debug | 机械噪音与逐帧转储。**默认不可见** | **新增** |
| `INFO` | Info | 流程里程碑 + 协议解析结果 | 不变 |
| `WARN` | Warn | 异常但可继续 | 不变 |
| `ERR ` | Error | 功能失败或未处理异常 | 标签不变，改多行前缀 |

标签宽度统一 6 字符，`[DBG ]` 与现有三个对齐，列不错位。

### 3.2 配置

- 新增 `AppSettings.LogLevel`（`string`，默认 `"Info"`）
- 沿用项目已有的"未知取值一律按默认处理"惯例（与 `WidgetDisplayMode` / `WindowBackdrop` 写法一致）
- 启动时赋给 `Log.MinLevel`；**重启生效**（不追求热更新，避免跨线程读静态字段引入 `volatile`）
- 设置页加「详细日志」开关（等价 `LogLevel=Debug`）+「打开日志目录」按钮

### 3.3 默认 `Info` 的边界（实测依据）

对现有 `app.log`（2 天，11516 行）按类别统计：

| 类别 | 2 天行数 | 占比 | 归属 |
|---|---|---|---|
| 接口清单（`  <设备名> [VID_..] usage=..`） | 3075 | 26.7% | **Debug** |
| HID 元数据（`HID 元数据 24AE/..`） | 3065 | 26.6% | **Debug** |
| 收到托盘回调（`收到托盘回调: NxN`） | 1728 | 15.0% | **Debug** |
| 逐帧原始报文（`北通/雷柏 收到报文: <HEX>`） | 1132 | 9.8% | **Debug** |
| 其余流程 + WARN（其中 WARN 223） | 1637 | 14.2% | **Info** |
| 协议解析结果（`雷柏 状态帧` / `迈从 ... 解析`） | 879 | 7.6% | **Info** |

**噪音合计 9000 行（78%）**。默认 `Info` 后预期 2 天日志量 **~11500 → ~2500 行**。

**关键：排障能力零损失。** 两条协议解析日志的**行尾本来就带着原始 hex**：

```
[INFO] 雷柏 状态帧: 连接=2.4G 充电标志=1 电量=95% RF=0xCF(bit7连接=True RSSI=79) 原始=07 10 04 80 0C 80 0C 01 5F 00 03 01 00 12 46 01 02 02 CF
```

所以下沉掉的只是"读了但没解析出东西"的中间帧，`grep "状态帧"` 能拿到的信息一个不少。

**什么时候需要切 `Debug`**：调新协议、看探测矩阵、需要完整接口清单时 —— 即 `hardware/safety.md` 规则 6 里"先被动监听看日志"那一步。

### 3.4 格式契约

实施时**必须保持**下列不变量，否则 doc/ 里的考古命令全部失效：

| 不变量 | 说明 |
|---|---|
| 行首是 `yyyy-MM-dd HH:mm:ss.fff` | `grep -a "^2026-10"` 这类时间过滤依赖它 |
| 级别标签宽度 6 字符 | `[DBG ]` / `[INFO]` / `[WARN]` / `[ERR ]` |
| **一个逻辑事件 = 一行** | 异常堆栈的后续行统一加缩进前缀（如 `        \| `），保证 `grep "^20"` 只命中真正的日志行 |
| 协议解析结果行尾带 `原始=<HEX>` | 这是默认 `Info` 下仍能排障的前提 |

## 4. 实施步骤

### 阶段 1 —— 级别体系 + 配置

1. 引入 `LogLevel` 枚举：`Debug < Info < Warn < Error`，外加 `Off` 哨兵值。
2. `Log` 加静态 `MinLevel`（默认 `Info`），`Write()` 入口先比级别，低于阈值直接 return（零 IO、零分配）。
3. 暴露 `Log.IsEnabled(level)`，供调用点提前短路，避免 `$"..."` 插值开销。
4. 新增 `Log.Debug(string)`；`AppSettings` 加 `LogLevel`；`App` 启动时赋值。
5. `Error` 改多行友好：首行 `[ERR ]` 原样，后续堆栈行统一加缩进前缀。
6. 把 §3.3 表中四类噪音从 `Info` 改为 `Debug`（逐点确认，不批量正则替换）。

### 阶段 2 —— 稳定性加固

7. 滚动改为**带序号的保留**：`app.log` → `app.1.log` → `app.2.log`，默认留 3 个；用 `File.Move(overwrite)` 而不是 `Delete`。`app.log` 永远是"当前"，grep 习惯不变。
8. 统一**按字节**计量（写入后用 `_writer.BaseStream.Position`），与 `FileInfo.Length` 对齐；常量改名 `MaxBytes`。
9. 写入失败不再静默：连续失败达阈值时写 `logs/log-error.txt` 兜底，并保留内存失败计数供设置页显示。兜底通道自身失败就彻底放弃，不递归。
10. `FileShare.Read` → `FileShare.ReadWrite`，让并发进程至少不互相阻塞（单实例 Mutex 仍是主防线）。
11. 补 `Log.Shutdown()`（Dispose writer + 最后 flush），挂到 `AppServices.Dispose` 链路。

### 阶段 3 —— 降噪下沉 + 可测性

12. 提供 `Log.InfoOnChange(key, message)` 与 `Log.InfoThrottled(key, message, TimeSpan)`，内部用 `ConcurrentDictionary` 维护状态。
    **迁移范围要克制**：只迁 `TrayService` / `HidDeviceScanner` 这类**纯日志用途**的；`Betop` / `Mchose` 里的状态判断同时承担业务语义，**保持不动**。
13. 把 `Log` 的静态状态收敛成单个 `LogConfig` 对象（暂不引 `ILog` 抽象，改动面不匹配项目规模）。
14. 补 `LogTests`：`RedirectTo` 到临时目录，断言 ① 级别过滤生效 ② 触发滚动后生成 `app.1.log` 而非清空 ③ 超阈值不丢最新行 ④ 多行异常每行都有前缀 ⑤ 写入失败降级不抛。
15. 文档同步：`project/architecture.md` §5/§6 增补日志级别与配置项；复核 `hardware/debugging.md` 全部 grep 命令。

## 5. 验收标准

- `dotnet test` 全绿（现有用例 + 新增 LogTests）
- 现有命令行为不变：
  - `grep -a "ERR \|WARN " app.log`
  - `grep -a "启动 VigilWatt" app.log`
  - `grep -a "雷柏 状态帧" app.log`
- 默认 `Info` 跑 2 天，日志行数落在 **2500 行量级**（当前 11516）
- 切 `Debug` 后 `grep -a "usage=" app.log` 能出接口清单
- 把单文件阈值临时调到 100KB，确认产生 `app.1.log` 且 `app.log` 不丢最新行

## 6. 未纳入范围

- **不引入第三方日志框架**（见 §2）
- **不做日志按天分文件**：滚动仍按大小触发，只是改为保留多份
- **不做结构化 / JSON 日志**：grep 考古是本项目主力手段，纯文本格式的价值高于可解析性
- **不做日志上报 / 遥测**：个人自用工具，不引入任何外发通道
