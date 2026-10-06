using EasyBluetooth.App.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using EasyBluetooth.Core.Models;

namespace EasyBluetooth.App.Views;

public sealed partial class StatsPage : Page
{
    /// <summary>历史页设备条目：Id 用于查库，Name 画曲线标注，Display 是下拉框文案。
    /// 设备列表以历史库为准（所选时间范围内有过采样就在列），不在当前在线列表的标"历史 + 最后记录时间"。</summary>
    private sealed record DeviceEntry(string Id, string Name, string Display);

    public StatsPage()
    {
        InitializeComponent();
        ReloadDeviceList();
        App.Services.Manager.DevicesChanged += OnDevicesChanged;
        Unloaded += (_, _) => App.Services.Manager.DevicesChanged -= OnDevicesChanged;
    }

    // 命名方法 + Unloaded 解绑：页面每次导航都会新建实例，构造里匿名订阅会让旧页泄漏且叠加回调
    private void OnDevicesChanged() => DispatcherQueue.TryEnqueue(ReloadDeviceList);

    private int GetSelectedHours()
    {
        if (RangeCombo?.SelectedItem is ComboBoxItem item && item.Tag is string tag)
            return int.Parse(tag);
        return 24;
    }

    private void ReloadDeviceList()
    {
        if (DeviceCombo == null || RangeCombo == null || ChartHost == null) return;

        // 设备列表按历史库走：所选时间范围内有过采样的设备都在列（含已下线/换过 Id 的历史设备），
        // 不再跟随当前在线设备——否则昨天用旧协议采的曲线永远选不到。
        var liveIds = App.Services.Manager.Devices.Select(d => d.Id).ToHashSet();
        var entries = App.Services.History
            .DistinctDevices(DateTimeOffset.Now.AddHours(-GetSelectedHours()))
            .Select(d =>
            {
                string display = liveIds.Contains(d.DeviceId)
                    ? d.Name
                    : $"{d.Name}（历史 {d.LastSeen.LocalDateTime:MM-dd HH:mm}）";
                return new DeviceEntry(d.DeviceId, d.Name, display);
            })
            .ToList();

        // 列表内容没变就不重建 ItemsSource——重建会强制关闭打开中的下拉框并把选择弹回，
        // 每 60 秒一次的轮询周期正好会打断用户切换（表现为"切换没反应"）
        var selectedId = (DeviceCombo.SelectedItem as DeviceEntry)?.Id;
        bool sameList = DeviceCombo.ItemsSource is IReadOnlyList<DeviceEntry> cur &&
                        cur.Count == entries.Count &&
                        cur.Zip(entries, (a, b) => a.Id == b.Id && a.Display == b.Display).All(x => x);
        if (!sameList)
        {
            DeviceCombo.ItemsSource = entries;
            DeviceCombo.SelectedItem = entries.FirstOrDefault(e => e.Id == selectedId) ?? entries.FirstOrDefault();
        }

        RenderChart();
    }

    private void Device_SelectionChanged(object sender, SelectionChangedEventArgs e) => RenderChart();

    private void Range_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // 时间范围变了，"有数据的设备"集合也会变，列表要跟着重算
        if (DeviceCombo == null) return;
        ReloadDeviceList();
    }

    private void RenderChart()
    {
        // RangeCombo 的 SelectedIndex="0" 会在 XAML 解析期间触发本方法，此时后续控件还没创建
        if (DeviceCombo == null || RangeCombo == null || ChartHost == null) return;

        if (DeviceCombo.SelectedItem is not DeviceEntry device)
        {
            ChartHost.Children.Clear();
            ChartHost.Children.Add(new TextBlock
            {
                Text = "该时间范围内暂无采样数据，等待采集…",
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Opacity = 0.6,
            });
            return;
        }

        var from = DateTimeOffset.Now.AddHours(-GetSelectedHours());
        var to = DateTimeOffset.Now;
        var samples = App.Services.History.Query(device.Id, from, to);

        // 把所选时间窗显式交给图表：数据不足一档时轴仍然拉开，切换档位才能看出变化
        var chart = new BatteryChartControl();
        chart.SetData(new[]
        {
            new BatteryChartControl.Series
            {
                Name = device.Name,
                Color = UiPalette.SeriesColors[0],
                Points = samples.Select(s => (s.Timestamp, s.Percent)).ToList(),
            },
        }, from, to);
        ChartHost.Children.Clear();
        ChartHost.Children.Add(chart);
    }
}
