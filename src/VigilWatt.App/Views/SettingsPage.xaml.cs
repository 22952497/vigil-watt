using System.Diagnostics;
using VigilWatt.App.Services;
using VigilWatt.Core.Models;
using VigilWatt.Core.Plugins;
using VigilWatt.Core.Utils;
using Microsoft.UI.Composition.SystemBackdrops;
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
        SystemBatteryToggle.IsOn = s.MonitorSystemBattery;
        UpdateSourceBox.Text = s.UpdateUrl;
        UpdateStatusText.Text = $"当前版本 {GetType().Assembly.GetName().Version?.ToString(3)}";

        // 小组件显示方式：与"窗口背景"同一套路，下拉框存偏好、副标题写实际行为
        var mode = WidgetDisplayMode.Normalize(s.WidgetDisplayMode);
        foreach (ComboBoxItem item in WidgetModeCombo.Items)
        {
            if (item.Tag as string == mode)
            {
                WidgetModeCombo.SelectedItem = item;
                break;
            }
        }
        WidgetModeHintText.Text = WidgetDisplayMode.Describe(mode);

        // 最多显示设备数：档位按统一范围生成（1~5），保证下拉框和取值收敛永远同源
        foreach (var n in WidgetMaxDevicesOptions.Choices)
        {
            WidgetMaxCombo.Items.Add(new ComboBoxItem { Content = $"{n} 个", Tag = n });
        }
        var maxDevices = WidgetMaxDevicesOptions.Clamp(s.WidgetMaxDevices);
        foreach (ComboBoxItem item in WidgetMaxCombo.Items)
        {
            if (item.Tag is int v && v == maxDevices)
            {
                WidgetMaxCombo.SelectedItem = item;
                break;
            }
        }

        // 任务栏贴片：与小组件同一套路（下拉框存偏好、副标题写行为），取值收敛也只有一处定义
        TaskbarToggle.IsOn = s.TaskbarBatteryEnabled;
        var taskbarMode = TaskbarBatteryMode.Normalize(s.TaskbarBatteryMode);
        foreach (ComboBoxItem item in TaskbarModeCombo.Items)
        {
            if (item.Tag as string == taskbarMode)
            {
                TaskbarModeCombo.SelectedItem = item;
                break;
            }
        }
        TaskbarModeHintText.Text = TaskbarBatteryMode.Describe(taskbarMode);

        foreach (var n in TaskbarBatteryOptions.Choices)
        {
            TaskbarMaxCombo.Items.Add(new ComboBoxItem { Content = $"{n} 个", Tag = n });
        }
        var taskbarMax = TaskbarBatteryOptions.Clamp(s.TaskbarBatteryMaxDevices);
        foreach (ComboBoxItem item in TaskbarMaxCombo.Items)
        {
            if (item.Tag is int v && v == taskbarMax)
            {
                TaskbarMaxCombo.SelectedItem = item;
                break;
            }
        }

        // 窗口背景：下拉框选中"用户偏好"，副标题说"这台机器上实际会生效什么"——
        // 两者不是一回事（Win10 上选 Mica 会静默降级成 Acrylic），分开显示才不会让人困惑。
        var pref = WindowBackdrop.Normalize(s.WindowBackdrop);
        foreach (ComboBoxItem item in BackdropCombo.Items)
        {
            if (item.Tag as string == pref)
            {
                BackdropCombo.SelectedItem = item;
                break;
            }
        }
        UpdateBackdropHint(pref);

        // 日志级别：档位按 LogLevelOptions.Choices 生成（取值只有一处定义），副标题说明当前档位含义。
        // 注意与"窗口背景"不同，这里是重启生效——副标题已写明。
        foreach (var (value, label) in LogLevelOptions.Choices)
        {
            LogLevelCombo.Items.Add(new ComboBoxItem { Content = label, Tag = value });
        }
        var level = LogLevelNames.Normalize(s.LogLevel);
        foreach (ComboBoxItem item in LogLevelCombo.Items)
        {
            if (item.Tag as string == level)
            {
                LogLevelCombo.SelectedItem = item;
                break;
            }
        }
        LogLevelHintText.Text = LogLevelOptions.Describe(level);

        // 插件开关动态生成：新插件（IBatteryPlugin 实现）自动出现在设置页
        bool first = true;
        foreach (var plugin in App.Services.Manager.Plugins)
        {
            AddPluginRow(plugin, s.PluginEnabled.TryGetValue(plugin.Name, out var on) && on, first);
            first = false;
        }
    }

    private void Backdrop_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (BackdropCombo.SelectedItem is not ComboBoxItem item || item.Tag is not string tag) return;
        UpdateBackdropHint(tag);
    }

    private void WidgetMode_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (WidgetModeCombo.SelectedItem is not ComboBoxItem item || item.Tag is not string tag) return;
        WidgetModeHintText.Text = WidgetDisplayMode.Describe(tag);
    }

    private void LogLevel_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (LogLevelCombo.SelectedItem is not ComboBoxItem item || item.Tag is not string tag) return;
        LogLevelHintText.Text = LogLevelOptions.Describe(tag);
    }

    private void TaskbarMode_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (TaskbarModeCombo.SelectedItem is not ComboBoxItem item || item.Tag is not string tag) return;
        TaskbarModeHintText.Text = TaskbarBatteryMode.Describe(tag);
    }

    /// <summary>副标题按"选中的偏好在本机上实际会生效成什么"来写。</summary>
    private void UpdateBackdropHint(string preference)
    {
        var mica = MicaController.IsSupported();
        var resolved = WindowBackdrop.Resolve(preference, mica, DesktopAcrylicController.IsSupported());
        BackdropHintText.Text = WindowBackdrop.Describe(resolved, mica);
    }

    /// <summary>
    /// 按"标签左、开关右"的统一样式动态生成一行插件开关。复用 Page.Resources 里的
    /// RowTitleStyle/RowDescStyle/RowDividerStyle，让动态行与 XAML 手写行视觉完全一致。
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
        s.MonitorSystemBattery = SystemBatteryToggle.IsOn;
        s.WidgetDisplayMode = WidgetModeCombo.SelectedItem is ComboBoxItem modeItem && modeItem.Tag is string modeTag
            ? WidgetDisplayMode.Normalize(modeTag)
            : WidgetDisplayMode.Normal;
        s.WidgetMaxDevices = WidgetMaxCombo.SelectedItem is ComboBoxItem maxItem && maxItem.Tag is int maxValue
            ? WidgetMaxDevicesOptions.Clamp(maxValue)
            : WidgetMaxDevicesOptions.Default;
        s.TaskbarBatteryEnabled = TaskbarToggle.IsOn;
        s.TaskbarBatteryMode = TaskbarBatteryMode.Normalize(
            (TaskbarModeCombo.SelectedItem as ComboBoxItem)?.Tag as string);
        s.TaskbarBatteryMaxDevices = TaskbarMaxCombo.SelectedItem is ComboBoxItem taskbarMaxItem
                                     && taskbarMaxItem.Tag is int taskbarMaxValue
            ? TaskbarBatteryOptions.Clamp(taskbarMaxValue)
            : TaskbarBatteryOptions.Default;
        s.UpdateUrl = UpdateSourceBox.Text.Trim();
        s.WindowBackdrop = (BackdropCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? WindowBackdrop.Auto;
        s.LogLevel = LogLevelNames.Normalize((LogLevelCombo.SelectedItem as ComboBoxItem)?.Tag as string);
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
        else if (s.WidgetEnabled)
        {
            // 已开着的小组件：先把配置落到位再重画，让"显示方式/最多显示设备数"当场生效，
            // 不必关掉再开。RefreshFromSettings 内部会重新应用层级并重算窗口高度。
            App.Services.Widget?.RefreshFromSettings();
        }

        // 任务栏贴片开关即时生效（与小组件同一条路）
        if (s.TaskbarBatteryEnabled && App.Services.TaskbarBattery == null)
        {
            App.Services.TaskbarBattery = new TaskbarBatteryWindow();
            App.Services.TaskbarBattery.Activate();
        }
        else if (!s.TaskbarBatteryEnabled && App.Services.TaskbarBattery != null)
        {
            App.Services.TaskbarBattery.Close();
            App.Services.TaskbarBattery = null;
        }
        else if (s.TaskbarBatteryEnabled)
        {
            App.Services.TaskbarBattery?.RefreshFromSettings();
        }

        // 窗口背景即时生效：改完就想看到效果，不该逼用户重启应用
        App.Services.MainWindow?.ApplyBackdrop();

        // 历史清理立即执行一次
        App.Services.Manager.CleanupHistoryNow();

        // 日志级别不热更新（重启生效），保存后给一句提示，避免用户以为"点了没反应"
        ShowSavedFeedback(s.LogLevel == LogLevelNames.Default ? "已保存" : "已保存（日志级别重启后生效）");
    }

    private async void CheckUpdate_Click(object sender, RoutedEventArgs e)
    {
        // 直接用输入框的值（可能还没保存），避免"明明填了却提示未配置"
        var url = UpdateSourceBox.Text.Trim();
        if (url.Length == 0)
        {
            UpdateStatusText.Text = "未配置更新源，请先填写并保存设置";
            return;
        }

        CheckUpdateButton.IsEnabled = false;
        UpdateStatusText.Text = "正在检查更新…";
        try
        {
            var svc = new UpdateService(url);
            if (!svc.IsInstalled)
            {
                UpdateStatusText.Text = "当前是未安装的运行副本（bin/publish），无法自动更新";
                return;
            }

            var info = await svc.CheckAsync();
            if (info == null)
            {
                UpdateStatusText.Text = $"已是最新版本（{svc.CurrentVersion}）";
                return;
            }

            var dialog = new ContentDialog
            {
                Title = "发现新版本 " + info.TargetFullRelease.Version,
                Content = $"当前 {svc.CurrentVersion} → 最新 {info.TargetFullRelease.Version}，下载并安装？\n安装完成后应用会自动重启。",
                PrimaryButtonText = "下载并安装",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = XamlRoot,
            };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            {
                UpdateStatusText.Text = "已取消更新";
                return;
            }

            // 进度回调在线程池线程，回 UI 线程刷新
            var dispatcher = DispatcherQueue;
            UpdateStatusText.Text = "下载中…";
            await svc.DownloadAsync(info, pct =>
                dispatcher.TryEnqueue(() => UpdateStatusText.Text = $"下载中 {pct}%"));

            UpdateStatusText.Text = "下载完成，正在应用更新并重启…";
            svc.ApplyAndRestart(info);
        }
        catch (Exception ex)
        {
            Log.Warn("检查更新失败: " + ex.Message);
            UpdateStatusText.Text = "检查更新失败：" + ex.Message;
        }
        finally
        {
            CheckUpdateButton.IsEnabled = true;
        }
    }

    private void OpenLogDir_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var dir = Path.Combine(AppPaths.RootDir, "logs");
            Directory.CreateDirectory(dir);
            Process.Start(new ProcessStartInfo { FileName = dir, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Warn("打开日志目录失败: " + ex.Message);
        }
    }

    private async void ClearLogs_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new ContentDialog
        {
            Title = "清空日志？",
            Content = "将删除 logs 目录下的 app.log、滚动历史（app.N.log）与 log-error.txt，且不可恢复。",
            PrimaryButtonText = "清空",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        try
        {
            // 先释放 app.log 句柄，否则删不掉；之后的日志会重新开一个新文件
            Log.Shutdown();
            var dir = Path.Combine(AppPaths.RootDir, "logs");
            if (Directory.Exists(dir))
            {
                foreach (var file in Directory.EnumerateFiles(dir, "app*.log"))
                    File.Delete(file);
                var err = Path.Combine(dir, "log-error.txt");
                if (File.Exists(err)) File.Delete(err);
            }
            Log.Info("日志已由设置页清空");
            ShowSavedFeedback("日志已清空");
        }
        catch (Exception ex)
        {
            Log.Warn("清空日志失败: " + ex.Message);
            ShowSavedFeedback("清空日志失败：" + ex.Message);
        }
    }

    /// <summary>底部保存栏的即时反馈：显示一句话，2 秒后自动隐去。</summary>
    private void ShowSavedFeedback(string text)
    {
        SavedIcon.Visibility = Visibility.Visible;
        SavedText.Text = text;
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
