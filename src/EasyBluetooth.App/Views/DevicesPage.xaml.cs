using EasyBluetooth.App.ViewModels;
using EasyBluetooth.Core.Models;
using EasyBluetooth.Core.Utils;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace EasyBluetooth.App.Views;

public sealed partial class DevicesPage : Page
{
    /// <summary>手动刷新进行中：挡住重复点击，也让"刷新中"状态不被后台事件提前清掉。</summary>
    private bool _refreshing;

    public DevicesPage()
    {
        InitializeComponent();
        Refresh();
        App.Services.Manager.DevicesChanged += OnDevicesChanged;
        App.Services.Manager.DeviceUpdated += OnDeviceUpdated;
        Unloaded += (_, _) =>
        {
            App.Services.Manager.DevicesChanged -= OnDevicesChanged;
            App.Services.Manager.DeviceUpdated -= OnDeviceUpdated;
        };
    }

    // 命名方法 + Unloaded 解绑：页面每次导航都会新建实例，匿名订阅会让旧页泄漏且叠加回调
    private void OnDeviceUpdated(DeviceStatus _) => OnDevicesChanged();

    private void OnDevicesChanged() => DispatcherQueue.TryEnqueue(Refresh);

    private void Refresh()
    {
        var devices = App.Services.Manager.Devices;
        DeviceList.ItemsSource = devices.Select(DeviceRowModel.From).ToList();
        var online = devices.Count(d => d.IsOnline);
        // 带上时间戳：即使设备数没变，用户也能一眼看出这次刷新确实跑过了
        SummaryText.Text = $"{online} 台在线 / 共 {devices.Count} 台 · {DateTime.Now:HH:mm:ss} 更新";

        bool empty = devices.Count == 0;
        DeviceList.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
        EmptyState.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e)
    {
        if (_refreshing) return;

        _refreshing = true;
        SetRefreshing(true);
        var startedAt = DateTimeOffset.Now;
        try
        {
            // 等整轮跑完（含 BLE 异步补读）。若自动轮询正在进行，这里会复用那一轮，不会白跑也不会被丢弃。
            await App.Services.Manager.RequestPollAsync();
        }
        catch (Exception ex)
        {
            Log.Error("手动刷新失败", ex);
        }
        finally
        {
            // 保证"刷新中"至少可见一小会儿：否则本地扫描几十毫秒就结束，按钮闪一下反而像没反应
            var elapsed = DateTimeOffset.Now - startedAt;
            var minVisible = TimeSpan.FromMilliseconds(450);
            if (elapsed < minVisible)
                await Task.Delay(minVisible - elapsed);

            Refresh();
            SetRefreshing(false);
            _refreshing = false;
        }
    }

    private void SetRefreshing(bool on)
    {
        RefreshButton.IsEnabled = !on;
        RefreshRing.IsActive = on;
        RefreshRing.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        RefreshIcon.Visibility = on ? Visibility.Collapsed : Visibility.Visible;
        RefreshText.Text = on ? "刷新中…" : "刷新";
    }
}
