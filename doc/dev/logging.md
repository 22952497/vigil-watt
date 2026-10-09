# 日志改造方案

> **状态：方案定稿（补强版），代码未改动。**
>
> - 2026-10-08 初稿
> - 2026-10-09 补强评审（软件工坊团队）：修正 3 处事实错误、把 P0 稳定性提前到阶段 1、补齐"管控"半边、新增 7 项遗漏（N1–N7）、补文档同步清单与回滚方案。
>
> 本文描述的是**目标形态**。当前 `src/VigilWatt.Core/Utils/Log.cs` 仍是硬编码三级、无级别配置。  
> 实施完成后请把本文件改写成《日志设计》：删掉本提示、把 §1「现状」降级为历史背景、删掉 §4 中已完成的部分。

## 0. 本次补强摘要（2026-10-09）

| 类别   | 变更                                                                                                                                     |
| ---- | -------------------------------------------------------------------------------------------------------------------------------------- |
| 事实修正 | M4 文件数 12 → **17**；M3 行号 `BetopPlugin.cs:189` → **205-214**；§4 原「`Log.Shutdown` 挂 `AppServices.Dispose`」前提**不成立**（该 Dispose 是死代码，见 N1） |
| 顺序调整 | S1/S3/S4（静默失败 / 破坏性滚动 / 并发丢日志）**提前到阶段 1**，与分级同批                                                                                        |
| 管控补齐 | `MaxBytes` 定值 + 总量上限 + `log-error.txt` 节流；设置页「清空日志」；Debug 误设提示；`Off` 语义明示                                                              |
| 新增遗漏 | N1–N7（见 §1.3）                                                                                                                          |
| 验收修正 | §5「切 Debug 后 `grep usage=` 出接口清单」不成立，已改判据                                                                                              |
| 新增章节 | §7 文档同步清单、§8 回滚方案                                                                                                                      |

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

| #  | 问题                                                                                                                                                                        | 位置                                          |
| -- | ------------------------------------------------------------------------------------------------------------------------------------------------------------------------- | ------------------------------------------- |
| S1 | **日志失败完全静默**。`catch { }` 把磁盘满、目录无权限、路径被占全部吞掉，日志停了没人知道                                                                                                                     | `Log.cs:66-69`                              |
| S2 | **滚动阈值单位不一致**。`EnsureWriter` 用 `FileInfo.Length`（**字节**）初始化 `_written`，`Write` 累加 `line.Length`（**UTF-16 字符数**）。中文日志实际字节 ≈ 3× 字符数 → 单文件要涨到约 6MB 才触发；常量名 `MaxChars` 与语义也不符 | `Log.cs:19,62,80`                           |
| S3 | **滚动 = 整文件删除**。`Roll()` 直接 `File.Delete`，刚复现的问题日志会被一次滚动清空，无任何历史留档                                                                                                         | `Log.cs:88-95`                              |
| S4 | **第二实例日志静默丢失**。`FileShare.Read` 只允许读共享，第二个进程写不进去 → 静默吞掉 → 表现为"日志里没有新启动记录"。该坑已在 debugging.md 记录但未根治                                                                        | `Log.cs:82`；`hardware/debugging.md` §"第二实例" |
| S5 | **异常日志破坏"一行一条"**。`$"{message} :: {ex}"` 把多行堆栈塞进单行格式，产生无时间戳裸行，让所有 grep 断行统计失真                                                                                              | `Log.cs:41`                                 |
| S6 | **无退出收尾**。只有 `AutoFlush` 兜底，没有 `Flush()` / `Shutdown()`；一旦改批量 flush 就必须补退出钩子                                                                                              | `Log.cs:84`；`App.xaml.cs:42-46`             |
| S7 | **全局单锁串行 IO**。所有写入 + Roll 挤在一把 `Gate` 里，日志 IO 直接压在轮询线程上                                                                                                                   | `Log.cs:14,55`                              |

#### 可维护性

| #  | 问题                                                                        | 证据                                                                                  |
| -- | ------------------------------------------------------------------------- | ----------------------------------------------------------------------------------- |
| M1 | **无级别配置**，想降噪只能改代码                                                        | 全仓 grep `LogLevel` / `MinLevel` 零命中                                                 |
| M2 | **只有 3 级，缺 Debug**。协议 hex、接口清单、逐帧转储全走 Info，实测 INFO 占 **98%**              | `Log.cs:38-41`                                                                      |
| M3 | **降噪逻辑散落调用点**。每处各写一遍"变了才打"，新增代码忘了加就刷屏                                     | `TrayService.cs:31,224-229`、`HidDeviceScanner.cs:10,43-51`、`BetopPlugin.cs:205-214` |
| M4 | **无来源标记**，看不出是哪家插件打的，只能 grep 中文                                           | Core 里 **17** 个文件在打日志                                                               |
| M5 | **日志自身零单测**。`RedirectTo` 是为测试造的 API，反而没被日志测试覆盖                            | `src/tests/` 无 LogTests                                                             |
| M6 | **纯静态类，无接口** → 无法注入替身、无法静默测试                                              | `Log.cs:12`                                                                         |
| M7 | **格式与文档耦合无契约**。debugging.md 的 grep 命令依赖 `[INFO]` / `[ERR ]` 字面格式，格式一改文档全废 | `hardware/debugging.md` §0                                                          |

#### 1.3 评审新增发现（2026-10-09，N 系列）

| #  | 严重度 | 问题                                                                                                                                                                | 位置                                              |
| -- | --- | ----------------------------------------------------------------------------------------------------------------------------------------------------------------- | ----------------------------------------------- |
| N1 | 🔴  | **`AppServices.Dispose` 是死代码**（全仓无人调用），退出实际走 `TrayService.Exit → Environment.Exit(0)`。因此原 §4「把 `Log.Shutdown` 挂到 `AppServices.Dispose`」的钩子**永不触发**——S6 必须挂到真实退出路径 | `App.xaml.cs:42-46`；`TrayService.cs:296-300`    |
| N2 | 🟠  | **失败重试风暴**：`EnsureWriter` 失败后 `_writer` 恒为 null，之后每写一行都重跑 `Directory.CreateDirectory + FileInfo + FileStream` → 慢盘/无权限时把阻塞 IO 压到轮询线程，放大 S7                        | `Log.cs:72-86`                                  |
| N3 | 🟠  | **日志注入**：HID 产品字符串（`HidDeviceInfo.Product`，来自设备描述符）与设备路径直接进 `Log.Info`，未清洗换行/控制符 → 可伪造时间戳或 `[ERR ]` 行                                                             | `HidDeviceInfo.cs:26,31-32`；`Log.cs:60`         |
| N4 | 🟠  | **时间基准冲突**：`Log` 用 `DateTime.Now`（本地），`history.db` 用 `ToUnixTimeMilliseconds()`（UTC epoch）→ 日志与采样时间无法直接对齐                                                         | `Log.cs:60`；`HistoryStore.cs:64`                |
| N5 | 🟡  | **编码未列契约**：现为 UTF-8 无 BOM；若日后改用带 BOM 的 `Encoding.UTF8`，会在文件中部插入 BOM                                                                                               | `Log.cs:81`                                     |
| N6 | 🟡  | `RedirectTo` 改的是全局静态且 xUnit 未禁并行 → 跨测试类污染/竞态（M5 只说"未覆盖"）                                                                                                          | `Log.cs:26`；`src/tests/` 无 `CollectionBehavior` |
| N7 | 🟡  | 单实例 `Mutex` 未 `Dispose`/`Release`，二实例仍会尝试写日志                                                                                                                      | `App.xaml.cs:76-82`                             |

### 1.4 顺带发现：非本应用格式的裸文本混入（已修正结论）

`app.log` 里有 **265 行无级别标签**的裸文本，内容是 WinRT 报错：

```
Property key syntax error.  A property key must either be specified as a canonical
property name (preferred) or be of the form {fmtid} pid.
```

来自 `Windows.Devices.Enumeration` 的属性集枚举。代码里没有 `Console.SetOut` / `SetError`，**推断是调试期从 shell 启动时 stderr 被重定向进了 app.log，不是日志系统写进去的**。

> ⚠️ **修正**：原方案称"实施阶段 2 给日志行加统一前缀后自然消除"——**不成立**。这些行不由 `Log` 写出，加前缀不会让它们消失；要消除只能定位启动脚本/父进程的 stderr 重定向。排查时先用 `grep -a "\["` 滤掉。

## 2. 已定决策

| 决策点                       | 结论                                                                           |
| ------------------------- | ---------------------------------------------------------------------------- |
| 级别档数                      | **合并 Trace 与 Debug，只保留一个 `Debug`**（Debug 本就是最低监测档，不再往下细分）                    |
| 默认级别                      | **`Info`**                                                                   |
| `Off` 语义                  | **关掉全部输出（含 Error）**——它是"全关哨兵"，不是"只留 Error"                                   |
| 是否引第三方框架                  | 不引。自研类约 150 行可控，Serilog/NLog 会撑大发布体积并给 Core 加依赖                              |
| 格式兼容                      | `[INFO]` / `[WARN]` / `[ERR ]` **一个都不改**，只新增 `DBG `；doc/ 里所有 grep 考古命令必须继续可用 |
| **阶段顺序**（2026-10-09）      | **S1/S3/S4 提前到阶段 1**，与分级同批落地                                                 |
| **管控项**（2026-10-09）       | 做：`MaxBytes` 定值 + 总量上限 + `log-error.txt` 节流、设置页「清空日志」、Debug 误设提示             |
| **敏感数据**（2026-10-09）      | **不做脱敏**，但**明示**"设备名/路径进日志是有意为之"（个人本机自用，脱敏收益低）                               |
| **FileShare**（2026-10-09） | 改 `ReadWrite`；**仅保证并发不互相阻塞，不保证不交错**（单实例 Mutex 才是主防线）                         |
| **退出收尾**（2026-10-09）      | 补真正的 `Log.Shutdown()`，挂到**真实退出路径**（见 N1），不挂死代码                               |

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

| 标签     | 级别    | 语义                  | 变更         |
| ------ | ----- | ------------------- | ---------- |
| `DBG ` | Debug | 机械噪音与逐帧转储。**默认不可见** | **新增**     |
| `INFO` | Info  | 流程里程碑 + 协议解析结果      | 不变         |
| `WARN` | Warn  | 异常但可继续              | 不变         |
| `ERR ` | Error | 功能失败或未处理异常          | 标签不变，改多行前缀 |

标签宽度统一 6 字符，`[DBG ]` 与现有三个对齐，列不错位。**标签保持 ASCII、不得中文化。**

### 3.2 配置

- 新增 `AppSettings.LogLevel`（`string`，默认 `"Info"`）
- 沿用项目已有的"未知取值一律按默认处理"惯例（与 `WidgetDisplayMode` / `WindowBackdrop` 写法一致）
- 启动时赋给 `Log.MinLevel`；**重启生效**（不追求热更新，避免跨线程读静态字段引入 `volatile`）
- 设置页新增：
  - 「详细日志」开关（等价 `LogLevel=Debug`）+「打开日志目录」
  - **「清空日志」按钮**（删 `app*.log` + `log-error.txt`）
  - 开关旁**明确标注"需重启生效"**——避免重蹈"点了没反应"的老坑
- **Debug 误设提示**：`LogLevel=Debug` 时在主界面/设置页给一行提示（或运行 N 天后自动降级回 `Info`），避免忘记关掉刷爆磁盘
- `log-error.txt` 自身**带上限/节流**（否则兜底通道也会涨爆）

### 3.3 默认 `Info` 的边界（实测依据）

对现有 `app.log`（2 天，11516 行）按类别统计：

| 类别                                | 2 天行数 | 占比    | 归属        |
| --------------------------------- | ----- | ----- | --------- |
| 接口清单（`  <设备名> [VID_..] usage=..`） | 3075  | 26.7% | **Debug** |
| HID 元数据（`HID 元数据 24AE/..`）        | 3065  | 26.6% | **Debug** |
| 收到托盘回调（`收到托盘回调: 0x…`）             | 1728  | 15.0% | **Debug** |
| 逐帧原始报文（`北通/雷柏 收到报文: <HEX>`）       | 1132  | 9.8%  | **Debug** |
| 其余流程 + WARN（其中 WARN 223）          | 1637  | 14.2% | **Info**  |
| 协议解析结果（`雷柏 状态帧` / `迈从 ... 解析`）    | 879   | 7.6%  | **Info**  |

**噪音合计 9000 行（78%）**。默认 `Info` 后预期 2 天日志量 **~11500 → ~2500 行**。

> 注：上表数字来自某次真实 `app.log`，**仓库内无该文件，无法自动验证**。复现：取 `%LOCALAPPDATA%\VigilWatt\logs\app.log`，按上表各簇 `grep -c` 统计。  
> 行示例修正：实际是 `收到托盘回调: 0x{mouseMessage:X4}`（`TrayService.cs:133`），非 `NxN`。

**补充归类（原表遗漏，均归 Debug）**：`[探测]`（`HidWriteProbe.cs:78,102,103,107`）、`[Elan 探测]`（`ElanHeadsetPlugin.cs:55,61`）、Mchose 逐帧/握手簇（`MchosePlugin.cs:356,375,400,457,465,485,523,527,531,543,585,594,619`）、`雷柏 电量越界丢弃`（`RapooPlugin.cs:140`）。

**关键：排障能力零损失。** 两条协议解析日志的**行尾本来就带着原始 hex**：

```
[INFO] 雷柏 状态帧: 连接=2.4G 充电标志=1 电量=95% RF=0xCF(bit7连接=True RSSI=79) 原始=07 10 04 80 0C 80 0C 01 5F 00 03 01 00 12 46 01 02 02 CF
```

所以下沉掉的只是"读了但没解析出东西"的中间帧，`grep "状态帧"` 能拿到的信息一个不少。

**什么时候需要切 `Debug`**：调新协议、看探测矩阵、需要完整接口清单时 —— 即 `hardware/safety.md` 规则 6 里"先被动监听看日志"那一步。

### 3.4 格式契约

实施时**必须保持**下列不变量，否则 doc/ 里的考古命令全部失效：

| 不变量                           | 说明                                                                            |
| ----------------------------- | ----------------------------------------------------------------------------- |
| 行首是 `yyyy-MM-dd HH:mm:ss.fff` | `grep -a "^2026-10"` 这类时间过滤依赖它                                                |
| 级别标签宽度 6 字符                   | `[DBG ]` / `[INFO]` / `[WARN]` / `[ERR ]`，保持 ASCII                            |
| **一个逻辑事件 = 一行**               | 异常堆栈的后续行统一加缩进前缀（如 `       \|`）；**写入前把 message 里的 `\r`/`\n` 转义为可见形式**（N3 注入防护） |
| 协议解析结果行尾带 `原始=<HEX>`          | 这是默认 `Info` 下仍能排障的前提                                                          |
| **编码 = UTF-8 无 BOM**          | 写入时不得改成带 BOM，否则文件中部会插入 BOM（N5）                                                |

## 4. 实施步骤（已按 2026-10-09 决策重排）

### 阶段 1 —— 级别体系 + 配置 + P0 稳定性

1. 引入 `LogLevel` 枚举：`Debug < Info < Warn < Error`，外加 `Off` 哨兵值（Off = 全关）。
2. `Log` 加静态 `MinLevel`（默认 `Info`），`Write()` 入口先比级别，低于阈值直接 return（零 IO、零分配）。
3. 暴露 `Log.IsEnabled(level)`，供调用点提前短路，避免 `$"..."` 插值开销。
4. 新增 `Log.Debug(string)`；`AppSettings` 加 `LogLevel`；`App` 启动时赋值。
5. `Error` 改多行友好：首行 `[ERR ]` 原样，后续堆栈行统一加缩进前缀。
6. 把 §3.3 表中噪音（含补充归类）从 `Info` 改为 `Debug`（逐点确认，不批量正则替换）。
7. **[P0]** 写入失败不再静默：连续失败达阈值时写 `logs/log-error.txt` 兜底（**带上限与节流**），保留内存失败计数供设置页显示；兜底通道自身失败就彻底放弃，不递归。同时修 **N2 重试风暴**（失败后置退避标志，不要每行重试）。
8. **[P0]** 滚动改为**带序号的保留**：`app.log` → `app.1.log` → `app.2.log`，默认留 3 个；用 `File.Move(overwrite)` 而不是 `Delete`。`app.log` 永远是"当前"，grep 习惯不变。
9. **[P0]** 统一**按字节**计量（写入后用 `_writer.BaseStream.Position`），与 `FileInfo.Length` 对齐；常量改名 `MaxBytes`，**定值 2 MB**；总量上限 `3 × MaxBytes`（由滚动份数自然约束）。
10. **[P0]** `FileShare.Read` → `FileShare.ReadWrite`（**仅防阻塞、不保证不交错**）。
11. **[P0]** 补 `Log.Shutdown()`（Dispose writer + 最后 flush），挂到**真实退出路径**（N1：改 `TrayService.Exit` 或加进程退出钩子），**不要挂 `AppServices.Dispose`**。
12. **[P0] LogTests 随阶段 1 落地**：`RedirectTo` 到临时目录，断言 ① 级别过滤生效 ② 触发滚动后生成 `app.1.log` 而非清空 ③ 超阈值不丢最新行 ④ 多行异常每行都有前缀 ⑤ 写入失败降级不抛、且不重试风暴。

### 阶段 2 —— 注入 / 时间 / 测试隔离

1. **N3 注入清洗**：`Write()` 内统一转义换行与控制符，保证"一个逻辑事件 = 一行"。
2. **N4 时间基准**：统一日志与 `history.db` 的时间口径（日志也走 UTC，或在行内附 offset），使二者可对齐。
3. **N6**：给 xUnit 加 `[assembly: CollectionBehavior(DisableTestParallelization = true)]`，或让 `RedirectTo` 走实例化配置而非全局静态。
4. **N7**：`Mutex` 释放/Dispose。

### 阶段 3 —— 降噪下沉 + 可测性

1. **泛化既有实现**：把 `MchosePlugin.ShouldLogOnce / ResetLogOnce`（`MchosePlugin.cs:113-125`）抽成公共 `Log.InfoOnChange(key, message)` 与 `Log.InfoThrottled(key, message, TimeSpan)`，内部用 `ConcurrentDictionary` 维护状态。  
   **迁移范围要克制**：只迁 `TrayService` / `HidDeviceScanner` 这类**纯日志用途**的；`Betop` / `Mchose` 里的状态判断同时承担业务语义，**保持不动**。
2. 把 `Log` 的静态状态收敛成单个 `LogConfig` 对象（**可选**：个人工具改动面偏大，可砍）。
3. 文档同步（见 §7）。

## 5. 验收标准（已修正）

- `dotnet test` 全绿（现有用例 + 新增 LogTests）
- 现有命令行为不变：
  - `grep -a "ERR \|WARN " app.log`
  - `grep -a "启动 VigilWatt" app.log`
  - `grep -a "雷柏 状态帧" app.log`
- 默认 `Info` 跑 2 天，日志行数落在 **2500 行量级**（对照基线 11516，需真机复测）


- **切 `Debug` 后**：`grep -a "usage=" app.log` 能出接口清单 —— ⚠️ **接口清单只在"设备集合变化"时才 dump**（`HidDeviceScanner.cs:42-54`），验收时须**插拔一次设备触发**后再查，否则必然查不到
- 把单文件阈值临时调到 100KB，确认产生 `app.1.log` 且 `app.log` 不丢最新行
- 模拟目录无权限：确认写 `log-error.txt`、主流程不受影响、且**不产生重试风暴**（N2）

## 6. 未纳入范围

- **不引入第三方日志框架**（见 §2）
- **不做日志按天分文件**：滚动仍按大小触发，只是改为保留多份
- **不做结构化 / JSON 日志**：grep 考古是本项目主力手段，纯文本格式的价值高于可解析性
- **不做日志上报 / 遥测**：个人自用工具，不引入任何外发通道
- **不做设备名 / MAC 脱敏**：个人本机自用，已在 §2 明示"有意为之"

## 7. 文档同步清单（实施时一并改）

| 文档                            | 改什么                                                                                              |
| ----------------------------- | ------------------------------------------------------------------------------------------------ |
| `doc/project/architecture.md` | §1 目录树第 37 行 `Utils/Log` 说明；§5 数据表加 `app.N.log` / `log-error.txt` / `LogLevel`；§6 约定补日志级别与配置项    |
| `doc/hardware/debugging.md`   | §0 grep 段注明"接口清单/逐帧需先切 Debug"；**§8 "旧实例以 `FileShare.Read` 占着 app.log" 段落在改 `ReadWrite` 后失真**，需改写 |
| `doc/README.md`               | 第 63 行「数据与日志位置」补 `app.N.log` / `log-error.txt` / `LogLevel`                                      |
| `doc/project/handoff.md`      | §5/§6 及日志相关验证步骤同步                                                                                |
| 本文件                           | 实施完成后改写为《日志设计》                                                                                   |

## 8. 回滚方案

- 格式**向后兼容**：只新增 `[DBG ]`，`[INFO]` / `[WARN]` 不变 → 回滚 = `git revert` 该提交，doc/ 的 grep 命令无需回改。
- **唯一破坏性变更**：`[ERR ]` 异常由单行改多行前缀（§3.4）。若确有依赖旧单行格式的脚本，回滚需一并还原该改动。
- 新文件名 `app.N.log` / `log-error.txt` **不影响旧命令**（`app.log` 仍是"当前"文件）。
