using Velopack;
using VigilWatt.App.Services;

namespace VigilWatt.App;

/// <summary>
/// 自定义入口（csproj 用 DISABLE_XAML_GENERATED_MAIN 关掉了 XAML 生成的 Main）。
/// VelopackApp.Run() 必须先于一切 UI 跑：安装/更新/卸载时进程会带 --veloapp-* 参数被拉起，
/// 钩子在这里被消费后直接退出；只有正常启动才会走到下面的 WinUI 启动逻辑。
/// 因此 OnLaunched 里的单实例互斥不会和钩子进程冲突，现有逻辑无需改。
/// </summary>
internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        VelopackApp.Build()
            // 卸载时清掉开机自启残留（HKCU Run 的 VigilWatt 值是应用自己在设置页写的）
            .OnBeforeUninstallFastCallback(_ => AutostartService.SetEnabled(false))
            .Run();

        WinRT.ComWrappersSupport.InitializeComWrappers();
        Microsoft.UI.Xaml.Application.Start(_ =>
        {
            var context = new Microsoft.UI.Dispatching.DispatcherQueueSynchronizationContext(
                Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread());
            System.Threading.SynchronizationContext.SetSynchronizationContext(context);
            new App();
        });
    }
}
