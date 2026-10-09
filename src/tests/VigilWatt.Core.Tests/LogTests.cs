using VigilWatt.Core.Utils;
using Xunit;

namespace VigilWatt.Core.Tests;

/// <summary>
/// Log 核心行为回归测试（doc/dev/logging.md §4 阶段 1 第 12 条）。
/// Log 是全局静态，本类每个用例都在独立临时目录里跑，并在 Dispose 时复位级别/阈值。
/// </summary>
public sealed class LogTests : IDisposable
{
    private readonly string _dir;
    private readonly LogLevel _savedMinLevel;
    private readonly long _savedMaxBytes;

    public LogTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "VigilWattTests", "log-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _savedMinLevel = Log.MinLevel;
        _savedMaxBytes = Log.MaxBytes;
        Log.RedirectTo(_dir);
    }

    public void Dispose()
    {
        Log.Shutdown();
        Log.MinLevel = _savedMinLevel;
        Log.MaxBytes = _savedMaxBytes;
        try { Directory.Delete(_dir, recursive: true); } catch { /* 忽略 */ }
    }

    private string AppLog => Path.Combine(_dir, "app.log");

    /// <summary>读取当前日志：Log 的写句柄以 FileShare.ReadWrite 打开，而 File.ReadAllText
    /// 只请求 Read 共享，会因共享模式冲突打不开——必须同样以 ReadWrite 共享来读。</summary>
    private string ReadLog()
    {
        if (!File.Exists(AppLog)) return "";
        using var fs = new FileStream(AppLog, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var sr = new StreamReader(fs);
        return sr.ReadToEnd();
    }

    private string[] ReadLogLines() =>
        ReadLog().Replace("\r\n", "\n").Replace('\r', '\n')
            .Split('\n', StringSplitOptions.RemoveEmptyEntries);

    [Fact]
    public void 低于级别的调用不写入()
    {
        Log.MinLevel = LogLevel.Warn;

        Log.Debug("dbg-marker");
        Log.Info("info-marker");
        Log.Warn("warn-marker");

        var text = ReadLog();
        Assert.DoesNotContain("dbg-marker", text);
        Assert.DoesNotContain("info-marker", text);
        Assert.Contains("warn-marker", text);
    }

    [Fact]
    public void Off_连错误也不写()
    {
        Log.MinLevel = LogLevel.Off;

        Log.Error("boom");

        Assert.False(File.Exists(AppLog));
    }

    [Fact]
    public void 超过阈值滚动_生成_app1_且不清空()
    {
        Log.MinLevel = LogLevel.Info;
        Log.MaxBytes = 512;

        for (int i = 0; i < 40; i++) Log.Info($"填充第 {i} 行 ----------------------------");

        Assert.True(File.Exists(Path.Combine(_dir, "app.1.log")), "应生成 app.1.log");
        Assert.True(File.Exists(AppLog), "app.log 应作为当前文件继续存在");
    }

    [Fact]
    public void 滚动后不丢最新行()
    {
        Log.MinLevel = LogLevel.Info;
        Log.MaxBytes = 512;

        for (int i = 0; i < 120; i++) Log.Info($"行{i}");

        Assert.Contains("行119", ReadLog());
    }

    [Fact]
    public void 多行异常每行都带前缀()
    {
        Log.MinLevel = LogLevel.Info;

        Exception ex;
        try { throw new InvalidOperationException("内层错误"); }
        catch (Exception e) { ex = e; }
        Log.Error("出错了", ex);

        var lines = ReadLogLines();
        Assert.True(lines.Length > 1, "异常堆栈应为多行");
        Assert.StartsWith("20", lines[0]);
        Assert.All(lines.Skip(1), l => Assert.StartsWith("| ", l.TrimStart()));
        Assert.Contains(lines, l => l.Contains("内层错误"));
    }

    [Fact]
    public void 消息里的换行被转义_不产生假行()
    {
        Log.MinLevel = LogLevel.Info;

        Log.Info("设备名\n[ERR ] 伪造行");

        var lines = ReadLogLines();
        Assert.Single(lines);
        Assert.DoesNotContain(lines, l => l.StartsWith("[ERR ]"));
    }

    [Fact]
    public void 写入失败不抛异常()
    {
        // 把 logs 目录指向一个"文件"，CreateDirectory 必然失败
        var blocked = Path.Combine(_dir, "blocked-file");
        File.WriteAllText(blocked, "x");
        Log.RedirectTo(blocked);
        Log.MinLevel = LogLevel.Info;

        var ex = Record.Exception(() =>
        {
            for (int i = 0; i < 20; i++) Log.Info("x");
        });

        Assert.Null(ex);
    }

    [Fact]
    public void 写入失败降级写兜底文件()
    {
        // app.log 位置放一个**目录**：打开必然失败，但 logs 目录本身可写 → 兜底文件应生成
        var dir = Path.Combine(_dir, "blocked-dir");
        Directory.CreateDirectory(Path.Combine(dir, "app.log"));
        Log.RedirectTo(dir);
        Log.MinLevel = LogLevel.Info;

        for (int i = 0; i < 5; i++) Log.Info("x");

        Assert.True(File.Exists(Path.Combine(dir, "log-error.txt")), "连续失败应写 log-error.txt");
    }
}
