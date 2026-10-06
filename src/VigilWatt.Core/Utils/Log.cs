using System.Text;

namespace VigilWatt.Core.Utils;

/// <summary>
/// 极简文件日志，写到 %LOCALAPPDATA%\VigilWatt\logs\app.log，供协议探测调试。
///
/// 实现要点：常驻一个 StreamWriter（AutoFlush），只在需要时开文件。
/// 旧版每写一行都要 Directory.CreateDirectory + File.Exists + new FileInfo + File.AppendAllText（开关一次文件），
/// 一次 HID 扫描几十行就是上百次系统调用，会把扫描拖慢；现在每行只落一次写。
/// </summary>
public static class Log
{
    private static readonly object Gate = new();
    private static string _logDir = Path.Combine(AppPaths.RootDir, "logs");
    private static string _logPath = Path.Combine(_logDir, "app.log");

    /// <summary>单文件上限，超过就整文件重开（简单防膨胀）。</summary>
    private const long MaxChars = 2 * 1024 * 1024;

    private static StreamWriter? _writer;
    private static long _written;

    /// <summary>把日志重定向到指定目录，并立即释放旧文件句柄。
    /// 单测用：不重定向的话测试会往真实的 %LOCALAPPDATA% app.log 里写脏数据。</summary>
    public static void RedirectTo(string logDir)
    {
        lock (Gate)
        {
            try { _writer?.Dispose(); } catch { /* 忽略 */ }
            _writer = null;
            _written = 0;
            _logDir = logDir;
            _logPath = Path.Combine(logDir, "app.log");
        }
    }

    public static void Info(string message) => Write("INFO", message);
    public static void Warn(string message) => Write("WARN", message);
    public static void Error(string message, Exception? ex = null) =>
        Write("ERR ", ex is null ? message : $"{message} :: {ex}");

    /// <summary>把字节数组格式化成十六进制，协议探测时用。</summary>
    public static string Hex(ReadOnlySpan<byte> bytes)
    {
        var sb = new StringBuilder(bytes.Length * 3);
        foreach (var b in bytes) sb.Append(b.ToString("X2")).Append(' ');
        return sb.ToString().TrimEnd();
    }

    private static void Write(string level, string message)
    {
        try
        {
            lock (Gate)
            {
                EnsureWriter();
                if (_writer == null) return;

                var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}{Environment.NewLine}";
                _writer.Write(line);
                _written += line.Length;
                if (_written > MaxChars) Roll();
            }
        }
        catch
        {
            // 日志失败静默，不影响主流程
        }
    }

    private static void EnsureWriter()
    {
        if (_writer != null) return;

        Directory.CreateDirectory(_logDir);
        if (File.Exists(_logPath) && new FileInfo(_logPath).Length > MaxChars)
            File.Delete(_logPath);

        _written = File.Exists(_logPath) ? new FileInfo(_logPath).Length : 0;
        _writer = new StreamWriter(
            new FileStream(_logPath, FileMode.Append, FileAccess.Write, FileShare.Read))
        {
            AutoFlush = true,
        };
    }

    private static void Roll()
    {
        try { _writer?.Dispose(); } catch { /* 忽略 */ }
        _writer = null;
        try { if (File.Exists(_logPath)) File.Delete(_logPath); } catch { /* 忽略 */ }
        _written = 0;
        EnsureWriter();
    }
}
