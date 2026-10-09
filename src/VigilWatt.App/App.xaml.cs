using System.Threading;
using VigilWatt.App.Services;
using VigilWatt.Core.Devices;
using VigilWatt.Core.Settings;
using VigilWatt.Core.Storage;
using VigilWatt.Core.Utils;
using Microsoft.UI.Xaml;

namespace VigilWatt.App;

/// <summary>应用级服务容器。</summary>
public sealed class AppServices : IDisposable
{
    public AppSettings Settings { get; }
    public HistoryStore History { get; }
    public DeviceManager Manager { get; }
    public TrayService Tray { get; private set; } = null!;
    public WidgetWindow? Widget { get; set; }
    public MainWindow? MainWindow { get; set; }

    public AppServices()
    {
        Settings = AppSettings.Load();
        History = new HistoryStore();
        Manager = new DeviceManager(Settings, History);
    }

    public void AttachTray(TrayService tray)
    {
        Tray = tray;
        // 事件从后台线程来，气泡必须回 UI 线程
        Manager.LowBatteryDetected += device => MainWindow?.DispatcherQueue.TryEnqueue(
            () => Tray.ShowLowBattery(device));

        // 设备状态变化时刷新托盘图标/提示。以前只在启动时渲染一次，启动那会儿设备还没扫出来，
        // 于是托盘永远显示"暂无在线设备"——最小化到托盘后就只能看到这个过期提示。
        // DeviceUpdated 覆盖电量/在线状态变化，DevicesChanged 覆盖设备增删，两个都要订。
        Manager.DeviceUpdated += _ => tray.RequestUpdate();
        Manager.DevicesChanged += tray.RequestUpdate;
    }

    public void Dispose()
    {
        Manager.Dispose();
        History.Dispose();
    }
}

public partial class App : Application
{
    public static AppServices Services { get; private set; } = null!;
    private static Mutex? _singleInstanceMutex;

    public App()
    {
        InitializeComponent();

        // 全局异常兜底：写日志便于排错
        UnhandledException += (s, e) =>
        {
            Log.Error("XAML 未处理异常", e.Exception);
            e.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (s, e) =>
            Log.Error("未处理异常", e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (s, e) =>
        {
            Log.Error("未观察的 Task 异常", e.Exception);
            e.SetObserved();
        };

        // 退出兜底：任何退出路径（含非托盘路径、Environment.Exit）都会触发 ProcessExit，
        // 保证单实例互斥量被释放、日志被刷盘。与 TrayService.Exit 里的显式调用互为保险。
        AppDomain.CurrentDomain.ProcessExit += (s, e) => ShutdownLogging();
    }

    /// <summary>
    /// 退出收尾：先放掉单实例互斥量（让新实例能立刻启动），再刷盘并释放 app.log 句柄。
    /// **幂等**——托盘退出与 ProcessExit 兜底会各调一次，第二次是 no-op。
    /// 挂在真实退出路径上（AppServices.Dispose 全仓无人调用，钩子挂那儿永远不会触发）。
    /// </summary>
    internal static void ShutdownLogging()
    {
        var mutex = Interlocked.Exchange(ref _singleInstanceMutex, null);
        if (mutex != null)
        {
            try { mutex.ReleaseMutex(); } catch { /* 非持有线程调用会抛，忽略 */ }
            try { mutex.Dispose(); } catch { /* 忽略 */ }
        }
        Log.Shutdown();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        // 单实例：重复启动直接退出
        _singleInstanceMutex = new Mutex(true, "VigilWatt_SingleInstance", out bool createdNew);
        if (!createdNew)
        {
            Log.Info("检测到已有实例运行，退出");
            ShutdownLogging();
            Environment.Exit(0);
            return;
        }

        Services = new AppServices();
        // 日志级别：启动时应用（重启生效，不追求热更新）
        Log.MinLevel = LogLevelNames.Parse(Services.Settings.LogLevel);

        Log.Info($"启动 VigilWatt，参数: {args.Arguments}");
        Services.Manager.Start();

        Services.MainWindow = new MainWindow();

        Services.AttachTray(new TrayService(Services));
        Services.Tray.Initialize();

        Services.MainWindow.Activate();
        if (Services.Settings.StartMinimized)
        {
            Services.MainWindow.AppWindow.Hide();
        }

        if (Services.Settings.WidgetEnabled)
        {
            Services.Widget = new WidgetWindow();
            Services.Widget.Activate();
        }
    }
}
