using System.Collections.Concurrent;
using System.Text;

namespace VigilWatt.Core.Utils;

/// <summary>
/// 极简文件日志，写到 %LOCALAPPDATA%\VigilWatt\logs\app.log，供协议探测与排障。
///
/// **格式契约**（改动会破坏 doc/ 里的 grep 考古命令，详见 doc/dev/logging.md 格式契约一节）：
/// 行首固定 <c>yyyy-MM-dd HH:mm:ss.fff</c> 本地时间；级别标签宽度 6（<c>[DBG ]</c>…）；
/// 一个逻辑事件 = 一行（换行/控制符转义、堆栈续行加缩进前缀）；编码 UTF-8 无 BOM；
/// <c>app.log</c> 是当前文件，历史滚动为 <c>app.1.log</c>…。
/// 行首用本地时间、而 <c>history.db</c> 的 <c>ts</c> 用 UTC 毫秒，两者靠每个文件首行的
/// "时间基准"声明对齐（见 <see cref="WriteTimeBasis"/>）。
///
/// 低于 <see cref="MinLevel"/> 的调用零 IO、零分配返回；热路径可用 <see cref="IsEnabled"/> 短路。
/// 注意 Debug 是**运行期过滤档位**，发布版同样编译进去——**不要**加 <c>[Conditional("DEBUG")]</c>，
/// 否则 Release 下调用点被整段删掉，用户在 settings.json 里配 Debug 也没用。
/// </summary>
public static class Log
{
    private static readonly object Gate = new();

    /// <summary>无 BOM 的 UTF-8 —— 格式契约的一部分，别改成 <c>Encoding.UTF8</c>（它会带 BOM）。</summary>
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>异常堆栈后续行的缩进前缀，保证 <c>grep "^20"</c> 只命中真正的日志行。</summary>
    private const string ContinuationPrefix = "        | ";

    private const string ErrorFileName = "log-error.txt";

    /// <summary>单文件上限，超过就滚动（按**字节**计，与 FileInfo.Length 同一口径）。
    /// 非 const：单测与验收（"临时调到 100KB"）需要临时调小。</summary>
    public static long MaxBytes { get; set; } = 2 * 1024 * 1024;

    /// <summary>滚动保留的历史份数（app.1.log ~ app.N.log），加 app.log 共 N+1 个文件。</summary>
    public const int MaxRollFiles = 2;

    /// <summary>兜底文件上限，超过就截断重来，避免兜底通道自己涨爆。</summary>
    private const long MaxErrorBytes = 64 * 1024;

    /// <summary>连续写失败达到该次数才开始写兜底文件。</summary>
    private const int FailureThreshold = 3;

    /// <summary>打开失败后的退避时间：期间不再重试，避免每行日志都重跑一遍系统调用。</summary>
    private static readonly TimeSpan RetryBackoff = TimeSpan.FromSeconds(30);

    /// <summary>兜底文件的写入节流间隔。</summary>
    private static readonly TimeSpan FallbackThrottle = TimeSpan.FromSeconds(30);

    private static string _logDir = Path.Combine(AppPaths.RootDir, "logs");
    private static string _logPath = Path.Combine(_logDir, "app.log");

    private static StreamWriter? _writer;
    private static long _bytesWritten;
    private static int _consecutiveFailures;
    private static DateTime _nextRetryUtc = DateTime.MinValue;
    private static DateTime _lastFallbackUtc = DateTime.MinValue;
    private static bool _disabled;   // 连兜底都写不进去 → 彻底放弃，不递归

    /// <summary>最低输出级别（默认 <see cref="LogLevel.Info"/>）。启动时由 AppSettings.LogLevel 赋值；**重启生效**，不追求热更新（避免跨线程读静态字段）。</summary>
    public static LogLevel MinLevel { get; set; } = LogLevel.Info;

    /// <summary>热路径短路用：低于当前级别的调用可以跳过 <c>$"..."</c> 插值开销。</summary>
    public static bool IsEnabled(LogLevel level) => level >= MinLevel;

    public static void Debug(string message) => Write(LogLevel.Debug, "DBG ", message);
    public static void Info(string message) => Write(LogLevel.Info, "INFO", message);
    public static void Warn(string message) => Write(LogLevel.Warn, "WARN", message);

    // ---------- 降噪：状态变化才记 / 节流 ----------
    // 轮询型代码（每 15 秒 / 每分钟重跑）常在同一状态上重复打日志把 app.log 冲爆。
    // 两个入口把"什么时候才值得记"从各调用点收拢到这里：
    //   - InfoOnChange：状态变了才记（托盘提示文字、HID 设备集合指纹）
    //   - InfoThrottled：同一 key 在给定间隔内最多记一条
    // 是 MchosePlugin.ShouldLogOnce/ResetLogOnce 的通用版（见 doc/dev/logging.md §4 阶段 3）。

    private static readonly ConcurrentDictionary<string, string> InfoStateByKey = new();
    private static readonly ConcurrentDictionary<string, long> InfoLastTicksByKey = new();

    /// <summary>
    /// 只有 <paramref name="state"/> 与上次不同时才打一条 Info，并返回"是否发生变化"（调用点据此决定是否顺带 dump 一批 Debug 明细，HidDeviceScanner 就这么用）。
    /// 当前级别高于 Info 时**不更新状态**（用户切回 Info 档仍能看到最新状态）；并发下最坏多打一条——去重非正确性关键，不值得为它上锁。
    /// </summary>
    public static bool InfoOnChange(string key, string state, string message)
    {
        if (!IsEnabled(LogLevel.Info)) return false;
        if (InfoStateByKey.TryGetValue(key, out var prev) && prev == state) return false;
        InfoStateByKey[key] = state;
        Info(message);
        return true;
    }

    /// <summary><paramref name="message"/> 自身即状态：变了才打，并返回是否变化。</summary>
    public static bool InfoOnChange(string key, string message) => InfoOnChange(key, message, message);

    /// <summary>同一 <paramref name="key"/> 在 <paramref name="interval"/> 内最多打一条 Info。</summary>
    public static void InfoThrottled(string key, string message, TimeSpan interval)
    {
        if (!IsEnabled(LogLevel.Info)) return;
        var now = DateTime.UtcNow.Ticks;
        if (InfoLastTicksByKey.TryGetValue(key, out var last) && now - last < interval.Ticks) return;
        InfoLastTicksByKey[key] = now;
        Info(message);
    }

    /// <summary>错误日志。带异常时把堆栈拆成多行、每行加缩进前缀，保证"一行一条"不被破坏。</summary>
    public static void Error(string message, Exception? ex = null)
    {
        if (!IsEnabled(LogLevel.Error)) return;
        if (ex is null)
        {
            Emit("ERR ", Sanitize(message));
            return;
        }

        var sb = new StringBuilder();
        sb.Append(Sanitize(message)).Append(" :: ");
        var lines = ex.ToString().Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        sb.Append(lines[0]);
        for (int i = 1; i < lines.Length; i++)
            sb.Append(Environment.NewLine).Append(ContinuationPrefix).Append(lines[i]);
        Emit("ERR ", sb.ToString());
    }

    /// <summary>把字节数组格式化成十六进制，协议探测时用。</summary>
    public static string Hex(ReadOnlySpan<byte> bytes)
    {
        var sb = new StringBuilder(bytes.Length * 3);
        foreach (var b in bytes) sb.Append(b.ToString("X2")).Append(' ');
        return sb.ToString().TrimEnd();
    }

    /// <summary>把日志重定向到指定目录，并立即释放旧文件句柄、复位失败状态与去重表。
    /// 单测用：不重定向的话测试会往真实的 %LOCALAPPDATA% app.log 里写脏数据。</summary>
    public static void RedirectTo(string logDir)
    {
        lock (Gate)
        {
            CloseWriterQuietly();
            _bytesWritten = 0;
            _consecutiveFailures = 0;
            _nextRetryUtc = DateTime.MinValue;
            _lastFallbackUtc = DateTime.MinValue;
            _disabled = false;
            _logDir = logDir;
            _logPath = Path.Combine(logDir, "app.log");
            InfoStateByKey.Clear();
            InfoLastTicksByKey.Clear();
        }
    }

    /// <summary>退出前收尾：刷盘并释放句柄。挂在**真实退出路径**上（见 TrayService.Exit）。</summary>
    public static void Shutdown()
    {
        lock (Gate)
        {
            try { _writer?.Flush(); } catch { /* 忽略 */ }
            CloseWriterQuietly();
        }
    }

    private static void Write(LogLevel level, string tag, string message)
    {
        if (level < MinLevel) return;
        Emit(tag, Sanitize(message));
    }

    /// <summary>保证"一个逻辑事件 = 一行"：把换行/回车/控制符转义成可见形式，避免设备名等外部字符串（HID 产品字符串/BLE 名称）伪造出无时间戳的假日志行。
    /// 绝大多数消息不含控制符，走快路径零分配；含控制符才逐字符转义。</summary>
    private static string Sanitize(string message)
    {
        if (!HasControlChar(message)) return message;

        var sb = new StringBuilder(message.Length + 8);
        for (int i = 0; i < message.Length; i++)
        {
            char c = message[i];
            switch (c)
            {
                case '\r':
                    // CRLF 视作一个换行；孤立的 CR 也按换行处理（同样是行分隔语义）
                    if (i + 1 < message.Length && message[i + 1] == '\n') i++;
                    sb.Append("\\n");
                    break;
                case '\n':
                    sb.Append("\\n");
                    break;
                case '\t':
                    sb.Append(c);   // 制表符不破坏"一行"，原样保留
                    break;
                default:
                    // 只有真正的控制符才转义；普通字符（含中文）必须原样保留
                    if (c < ' ' || c == '\u007F')
                        sb.Append("\\x").Append(((int)c).ToString("X2"));
                    else
                        sb.Append(c);
                    break;
            }
        }
        return sb.ToString();
    }

    private static bool HasControlChar(string s)
    {
        foreach (var c in s)
            if (c < ' ' || c == '\u007F') return true;
        return false;
    }

    private static void Emit(string tag, string message)
    {
        if (_disabled) return;

        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{tag}] {message}{Environment.NewLine}";
        lock (Gate)
        {
            if (!EnsureWriter()) { OnFailure(); return; }

            try
            {
                _writer!.Write(line);
                _bytesWritten += Utf8NoBom.GetByteCount(line);
                _consecutiveFailures = 0;
                if (_bytesWritten > MaxBytes) Roll();
            }
            catch
            {
                CloseWriterQuietly();
                _nextRetryUtc = DateTime.UtcNow + RetryBackoff;
                OnFailure();
            }
        }
    }

    /// <summary>确保 writer 可用。返回 false 表示本行丢弃（退避中，或刚失败并已记兜底）。</summary>
    private static bool EnsureWriter()
    {
        if (_writer != null) return true;
        if (_disabled) return false;
        if (DateTime.UtcNow < _nextRetryUtc) return false;   // 退避中：不要每行都重跑系统调用

        try
        {
            Directory.CreateDirectory(_logDir);
            if (File.Exists(_logPath) && new FileInfo(_logPath).Length > MaxBytes)
                RotateFiles();

            bool isNew = !File.Exists(_logPath) || new FileInfo(_logPath).Length == 0;
            _writer = new StreamWriter(
                new FileStream(_logPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite),
                Utf8NoBom)
            {
                AutoFlush = true,
            };
            _bytesWritten = isNew ? 0 : new FileInfo(_logPath).Length;
            if (isNew) WriteTimeBasis();
            _consecutiveFailures = 0;
            _nextRetryUtc = DateTime.MinValue;
            return true;
        }
        catch
        {
            CloseWriterQuietly();
            _nextRetryUtc = DateTime.UtcNow + RetryBackoff;
            return false;   // 由调用方记一次 OnFailure（退避中被丢弃的行也会计数）
        }
    }

    /// <summary>每个日志文件新建时写一行"时间基准"：声明行首是**本地时间**、附上本机时区与 UTC offset，并说明 history.db 的 ts 是 UTC 毫秒（N4）。
    /// 该行带完整时间戳前缀，不破坏 grep -a "^20" 之类的既有考古命令；仅在文件为空时出现一次。</summary>
    private static void WriteTimeBasis()
    {
        var offset = TimeZoneInfo.Local.GetUtcOffset(DateTime.Now);
        var sign = offset < TimeSpan.Zero ? "-" : "+";
        var text = $"本地时间 {TimeZoneInfo.Local.Id} (UTC{sign}{offset.Duration():hh\\:mm})；history.db 的 ts 为 UTC 毫秒";
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [INFO] 时间基准: {text}{Environment.NewLine}";
        _writer!.Write(line);
        _bytesWritten += Utf8NoBom.GetByteCount(line);
    }

    private static void Roll()
    {
        try
        {
            CloseWriterQuietly();
            RotateFiles();
            _bytesWritten = 0;
            if (!EnsureWriter()) OnFailure();
        }
        catch
        {
            CloseWriterQuietly();
            _nextRetryUtc = DateTime.UtcNow + RetryBackoff;
            OnFailure();
        }
    }

    /// <summary>app.log → app.1.log → app.2.log，最旧的被丢弃。用 Move 保留历史，不整文件删除。</summary>
    private static void RotateFiles()
    {
        var oldest = Path.Combine(_logDir, $"app.{MaxRollFiles}.log");
        if (File.Exists(oldest)) File.Delete(oldest);

        for (int i = MaxRollFiles - 1; i >= 1; i--)
        {
            var src = Path.Combine(_logDir, $"app.{i}.log");
            if (File.Exists(src))
                File.Move(src, Path.Combine(_logDir, $"app.{i + 1}.log"), overwrite: true);
        }

        if (File.Exists(_logPath))
            File.Move(_logPath, Path.Combine(_logDir, "app.1.log"), overwrite: true);
    }

    /// <summary>写失败时记一笔；连续失败达阈值就写兜底文件（带节流 + 上限）。兜底自身失败则彻底放弃。</summary>
    private static void OnFailure()
    {
        _consecutiveFailures++;
        if (_disabled || _consecutiveFailures < FailureThreshold) return;

        var now = DateTime.UtcNow;
        if (now - _lastFallbackUtc < FallbackThrottle) return;
        _lastFallbackUtc = now;

        try
        {
            Directory.CreateDirectory(_logDir);
            var path = Path.Combine(_logDir, ErrorFileName);
            if (File.Exists(path) && new FileInfo(path).Length > MaxErrorBytes)
                File.Delete(path);
            File.AppendAllText(
                path,
                $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} 连续 {_consecutiveFailures} 次写入 app.log 失败，日志暂不可用{Environment.NewLine}",
                Utf8NoBom);
        }
        catch
        {
            _disabled = true;   // 兜底也失败 → 彻底放弃，不递归、不再刷
        }
    }

    private static void CloseWriterQuietly()
    {
        try { _writer?.Dispose(); } catch { /* 忽略 */ }
        _writer = null;
    }
}
