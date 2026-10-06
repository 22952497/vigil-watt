namespace VigilWatt.Core.Utils;

/// <summary>
/// 统一管理应用数据根目录（%LOCALAPPDATA%\VigilWatt），并负责从旧版
/// EasyBluetooth 数据目录一次性迁移。
///
/// 迁移时机由本类的**静态构造函数**保证：任何代码首次读取 <see cref="RootDir"/> 之前，
/// 静态构造已执行完毕——因此无论 Log / AppSettings / HistoryStore 谁先被访问，
/// 都会先完成迁移再解析各自的 static readonly 路径，不会读到旧目录。
/// </summary>
public static class AppPaths
{
    public const string AppName = "VigilWatt";

    // 迁移用：旧版数据目录名，**刻意保留旧名字面量**，勿随全局改名替换。
    private const string LegacyAppName = "EasyBluetooth";

    private static readonly string LocalAppData =
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

    /// <summary>当前应用数据根目录。</summary>
    public static readonly string RootDir = Path.Combine(LocalAppData, AppName);

    /// <summary>旧版应用数据根目录（仅迁移时使用）。</summary>
    public static readonly string LegacyRootDir = Path.Combine(LocalAppData, LegacyAppName);

    static AppPaths()
    {
        try { MigrateFromLegacy(); }
        catch { /* 迁移失败绝不阻塞进程启动 */ }
    }

    /// <summary>
    /// 幂等迁移：把 %LOCALAPPDATA%\EasyBluetooth 的内容合并进 %LOCALAPPDATA%\VigilWatt。
    /// 全部文件复制成功则删除旧目录；有任何失败则保留旧目录、下次启动重试。
    /// 规则：新目录中已存在的文件一律不覆盖。
    ///
    /// 注意：本方法内**禁止调用 Log**——Log 会在新目录下创建 logs\app.log，
    /// 随后复制旧 logs 时会因“目标已存在”被跳过，导致旧日志丢失。
    /// </summary>
    private static void MigrateFromLegacy()
    {
        if (!Directory.Exists(LegacyRootDir)) return;   // 全新用户：无旧数据

        lock (typeof(AppPaths))
        {
            if (!Directory.Exists(LegacyRootDir)) return;

            Directory.CreateDirectory(RootDir);

            bool allOk = true;
            foreach (var src in Directory.EnumerateFiles(LegacyRootDir, "*", SearchOption.AllDirectories))
            {
                var rel = Path.GetRelativePath(LegacyRootDir, src);
                var dst = Path.Combine(RootDir, rel);
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
                    if (File.Exists(dst)) continue;          // 绝不覆盖新数据
                    File.Copy(src, dst, overwrite: false);   // 旧进程占用 history.db 时会抛
                }
                catch
                {
                    allOk = false;                           // 单文件失败不致命
                }
            }

            if (allOk)
            {
                try { Directory.Delete(LegacyRootDir, recursive: true); }
                catch { /* 删不掉就留着，不影响使用 */ }
            }
            // allOk == false → 保留旧目录 → 下次启动重试（已复制的文件因存在而跳过）
        }
    }
}
