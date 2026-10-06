using Velopack;

namespace VigilWatt.App.Services;

/// <summary>
/// 自动更新：包一层 Velopack 的 UpdateManager。更新源来自 settings.json 的 UpdateUrl
/// （本地文件夹路径、网络共享或 GitHub 仓库 URL 均可），空 = 未配置不检查。
/// 只有通过安装包装的实例才能自更新（IsInstalled），开发时从 bin/publish 直接跑返回 false。
/// </summary>
public sealed class UpdateService
{
    private readonly UpdateManager _mgr;

    public UpdateService(string url)
    {
        _mgr = new UpdateManager(url);
    }

    public bool IsInstalled => _mgr.IsInstalled;

    public string? CurrentVersion => _mgr.CurrentVersion?.ToString();

    /// <summary>检查更新。null = 已是最新；检查源不可达等异常向上抛，由调用方呈现。</summary>
    public async Task<UpdateInfo?> CheckAsync()
    {
        return await _mgr.CheckForUpdatesAsync().ConfigureAwait(false);
    }

    public Task DownloadAsync(UpdateInfo info, Action<int>? progress = null)
    {
        return _mgr.DownloadUpdatesAsync(info, progress);
    }

    /// <summary>应用更新并重启。Velopack 的流程是"Update.exe 等本进程退出 → 应用更新 → 重启"，
    /// 新实例起来时旧进程已退出，不会撞上 OnLaunched 的单实例互斥。</summary>
    public void ApplyAndRestart(UpdateInfo info)
    {
        _mgr.ApplyUpdatesAndRestart(info);
    }
}
