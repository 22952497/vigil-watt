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
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        // 单实例：重复启动直接退出
        _singleInstanceMutex = new Mutex(true, "VigilWatt_SingleInstance", out bool createdNew);
        if (!createdNew)
        {
            Log.Info("检测到已有实例运行，退出");
            Environment.Exit(0);
            return;
        }

        Log.Info($"启动 VigilWatt，参数: {args.Arguments}");
        Services = new AppServices();
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
