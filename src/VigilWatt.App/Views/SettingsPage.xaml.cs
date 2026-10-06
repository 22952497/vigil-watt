using VigilWatt.App.Services;
using VigilWatt.Core.Models;
using VigilWatt.Core.Plugins;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace VigilWatt.App.Views;

public sealed partial class SettingsPage : Page
{
    private readonly Dictionary<string, ToggleSwitch> _pluginToggles = new();

    public SettingsPage()
    {
        InitializeComponent();
        Load();
    }

    private void Load()
    {
        var s = App.Services.Settings;
        ThresholdBox.Value = s.LowBatteryThreshold;
        SampleBox.Value = s.SampleIntervalMinutes;
        RetentionBox.Value = s.HistoryRetentionDays;
        StartMinimizedToggle.IsOn = s.StartMinimized;
        AutostartToggle.IsOn = AutostartService.IsEnabled();
        WidgetToggle.IsOn = s.WidgetEnabled;

        // 插件开关动态生成：新插件（IBatteryPlugin 实现）自动出现在设置页
        bool first = true;
        foreach (var plugin in App.Services.Manager.Plugins)
        {
            AddPluginRow(plugin, s.PluginEnabled.TryGetValue(plugin.Name, out var on) && on, first);
            first = false;
        }
    }

    /// <summary>
    /// 按"标签左、开关右"的统一样式动态生成一行插件开关。
    /// 复用 Page.Resources 里的 RowTitleStyle/RowDescStyle/RowDividerStyle，
    /// 让动态生成的行与 XAML 里手写的行视觉完全一致（否则会出现两种排版风格）。
    /// </summary>
    private void AddPluginRow(IBatteryPlugin plugin, bool isOn, bool first)
    {
        if (!first)
            PluginPanel.Children.Add(new Border { Style = (Style)Resources["RowDividerStyle"] });

        var toggle = new ToggleSwitch
        {
            OnContent = "开",
            OffContent = "关",
            MinWidth = 0,
            VerticalAlignment = VerticalAlignment.Center,
            IsOn = isOn,
        };

        var text = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Text = plugin.DisplayName, Style = (Style)Resources["RowTitleStyle"] });
        text.Children.Add(new TextBlock { Text = KindText(plugin.Kind), Style = (Style)Resources["RowDescStyle"] });

        var row = new Grid { ColumnSpacing = 16, Padding = new Thickness(0, 12, 0, 12) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(text, 0);
        Grid.SetColumn(toggle, 1);
        row.Children.Add(text);
        row.Children.Add(toggle);

        PluginPanel.Children.Add(row);
        _pluginToggles[plugin.Name] = toggle;
    }

    /// <summary>插件声明了设备类型，用一句人话解释这个开关管的是什么设备。</summary>
    private static string KindText(DeviceKind kind) => kind switch
    {
        DeviceKind.Mouse => "鼠标 · 2.4G 私有协议",
        DeviceKind.Keyboard => "键盘 · 2.4G 私有协议",
        DeviceKind.Headset => "耳机 · 2.4G 私有协议",
        DeviceKind.Speaker => "音箱 · 2.4G 私有协议",
        DeviceKind.Gamepad => "手柄 · 2.4G 私有协议",
        _ => "2.4G 私有协议",
    };

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var s = App.Services.Settings;
        s.LowBatteryThreshold = (int)ThresholdBox.Value;
        s.SampleIntervalMinutes = (int)SampleBox.Value;
        s.HistoryRetentionDays = (int)RetentionBox.Value;
        s.StartMinimized = StartMinimizedToggle.IsOn;
        s.Autostart = AutostartToggle.IsOn;
        s.WidgetEnabled = WidgetToggle.IsOn;
        foreach (var (name, toggle) in _pluginToggles)
        {
            s.PluginEnabled[name] = toggle.IsOn;
        }
        s.Save();

        AutostartService.SetEnabled(AutostartToggle.IsOn);
        App.Services.Manager.ApplySettings();

        // 小组件开关即时生效
        if (s.WidgetEnabled && App.Services.Widget == null)
        {
            App.Services.Widget = new WidgetWindow();
            App.Services.Widget.Activate();
        }
        else if (!s.WidgetEnabled && App.Services.Widget != null)
        {
            App.Services.Widget.Close();
            App.Services.Widget = null;
        }

        // 历史清理立即执行一次
        App.Services.Manager.CleanupHistoryNow();

        SavedIcon.Visibility = Visibility.Visible;
        SavedText.Text = "已保存";
        var timer = DispatcherQueue.CreateTimer();
        timer.Interval = TimeSpan.FromSeconds(2);
        timer.Tick += (_, _) =>
        {
            SavedText.Text = "";
            SavedIcon.Visibility = Visibility.Collapsed;
            timer.Stop();
        };
        timer.Start();
    }
}
