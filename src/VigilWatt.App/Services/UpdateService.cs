using System.Net.Http;
using System.Text.Json;
using Velopack;
using Velopack.Logging;
using Velopack.Sources;

namespace VigilWatt.App.Services;

/// <summary>
/// 功 能： 自动更新：包一层 Velopack 的 UpdateManager。
/// 2026/10/14 22:17:14 xliu
/// 更新源固定为公开 Gitee 仓库的最新 Release。
/// </summary>
public sealed class UpdateService
{
    private readonly UpdateManager _mgr;

    public UpdateService()
    {
        _mgr = new UpdateManager(new GiteeReleaseSource("YGWG-LX", "vigil-watt"));
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

    /// <summary>应用更新并重启。Velopack 的流程是“Update.exe 等本进程退出 → 应用更新 → 重启”。</summary>
    public void ApplyAndRestart(UpdateInfo info)
    {
        _mgr.ApplyUpdatesAndRestart(info);
    }
}

/// <summary>
/// 功 能： 把 Gitee Release 附件目录适配为 Velopack 更新源。
/// 2026/10/14 22:25:29 xliu
/// </summary>
internal sealed class GiteeReleaseSource : IUpdateSource
{
    private static readonly HttpClient Http = CreateHttpClient();
    private readonly string _owner;
    private readonly string _repository;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private SimpleWebSource? _releaseSource;

    public GiteeReleaseSource(string owner, string repository)
    {
        _owner = owner;
        _repository = repository;
    }

    public async Task<VelopackAssetFeed> GetReleaseFeed(
        IVelopackLogger logger,
        string? appId,
        string channel,
        Guid? stagingId = null,
        VelopackAsset? latestLocalRelease = null)
    {
        var source = await ResolveReleaseSourceAsync().ConfigureAwait(false);
        return await source.GetReleaseFeed(logger, appId, channel, stagingId, latestLocalRelease)
            .ConfigureAwait(false);
    }

    public async Task DownloadReleaseEntry(
        IVelopackLogger logger,
        VelopackAsset releaseEntry,
        string localFile,
        Action<int> progress,
        CancellationToken cancelToken)
    {
        var source = await ResolveReleaseSourceAsync().ConfigureAwait(false);
        await source.DownloadReleaseEntry(logger, releaseEntry, localFile, progress, cancelToken)
            .ConfigureAwait(false);
    }

    private async Task<SimpleWebSource> ResolveReleaseSourceAsync()
    {
        if (_releaseSource != null) return _releaseSource;

        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_releaseSource != null) return _releaseSource;

            var apiUrl = $"https://gitee.com/api/v5/repos/{Uri.EscapeDataString(_owner)}/{Uri.EscapeDataString(_repository)}/releases/latest";
            using var response = await Http.GetAsync(apiUrl).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
            using var release = await JsonDocument.ParseAsync(stream).ConfigureAwait(false);
            var tag = release.RootElement.TryGetProperty("tag_name", out var tagElement)
                ? tagElement.GetString()
                : null;
            if (string.IsNullOrWhiteSpace(tag))
                throw new InvalidDataException("Gitee 最新 Release 缺少 tag_name");

            var releasePath = $"https://gitee.com/{Uri.EscapeDataString(_owner)}/{Uri.EscapeDataString(_repository)}/releases/download/{Uri.EscapeDataString(tag)}";
            _releaseSource = new SimpleWebSource(releasePath);
            return _releaseSource;
        }
        finally
        {
            _gate.Release();
        }
    }

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("VigilWatt-Updater/1.0");
        return client;
    }
}
