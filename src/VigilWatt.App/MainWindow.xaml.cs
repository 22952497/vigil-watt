using System.Runtime.InteropServices;
using VigilWatt.App.Views;
using VigilWatt.Core.Utils;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace VigilWatt.App;

public sealed partial class MainWindow : Window
{
    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    public MainWindow()
    {
        InitializeComponent();

        Title = "VigilWatt";
        AppWindow.Title = "VigilWatt";
        ApplyWindowIcon();
        // AppWindow.Resize 用物理像素，按 DPI 换算成 800x600 DIP（runtime-pitfalls §6）
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        var dpiScale = GetDpiForWindow(hwnd) / 96.0;
        AppWindow.Resize(new Windows.Graphics.SizeInt32(
            (int)(800 * dpiScale), (int)(600 * dpiScale)));

        // 标题栏融入内容 + Mica 背景；不支持 Mica 的系统保持默认标题栏外观
        if (MicaController.IsSupported())
        {
            SystemBackdrop = new MicaBackdrop();
            ExtendsContentIntoTitleBar = true;
            SetTitleBar(AppTitleBar);
        }

        // 关闭按钮 = 隐藏到托盘
        AppWindow.Closing += (s, e) =>
        {
            e.Cancel = true;
            AppWindow.Hide();
        };

        // 初次进入设备页
        ContentFrame.Navigate(typeof(DevicesPage));
    }

    /// <summary>
    /// 运行时再设一次窗口图标。非打包 WinUI 3 有时不自动继承 exe 的 Win32 图标资源，
    /// 任务栏 / Alt-Tab 会显示成空白方块；显式从输出目录读 ico 设进去最稳。
    /// 注意：本方法在窗口构造期调用，异常必须自己吞掉（构造期抛出会让应用起不来）。
    /// </summary>
    private void ApplyWindowIcon()
    {
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "Assets", "VigilWatt.ico");
            if (File.Exists(path))
            {
                AppWindow.SetIcon(path);
            }
            else
            {
                Log.Warn($"窗口图标文件不存在，跳过 SetIcon: {path}");
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"设置窗口图标失败: {ex.Message}");
        }
    }

    private void Nav_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (sender.SelectedItem is not NavigationViewItem item || item.Tag is not string tag) return;
        ContentFrame.Navigate(tag switch
        {
            "devices" => typeof(DevicesPage),
            "stats" => typeof(StatsPage),
            "history" => typeof(HistoryPage),
            "settings" => typeof(SettingsPage),
            _ => typeof(DevicesPage),
        });
    }
}
