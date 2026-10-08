using VigilWatt.App.Services;
using VigilWatt.Core.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace VigilWatt.App.ViewModels;

/// <summary>设备列表行（一次性绑定，每次刷新重建；Brush 在 UI 线程的刷新路径里创建）。</summary>
public sealed class DeviceRowModel
{
    public string IconGlyph { get; init; } = "\uE702";
    public string Name { get; init; } = "";
    public string PercentText { get; init; } = "--%";
    public double Progress { get; init; }
    public string SourceText { get; init; } = "";
    public string SubText { get; init; } = "";
    public bool IsCharging { get; init; }

    /// <summary>电量条着色：充电蓝 / 低电红 / 中等橙 / 正常绿 / 离线灰。</summary>
    public Brush ProgressBrush { get; init; } = UiPalette.Brush(UiPalette.Offline);

    /// <summary>百分比文字：仅异常状态着色（充电蓝 / 低电红 / 离线灰），正常态用主题前景色。
    /// 注意 x:Bind 绑 null 会压掉样式默认前景导致文字不可见，此属性必须非空。</summary>
    public Brush PercentBrush { get; init; } = UiPalette.Brush(UiPalette.Offline);

    public static DeviceRowModel From(DeviceStatus d)
    {
        var percent = d.BatteryPercent;
        var icon = GlyphFor(d.Kind, d.DisplayName);

        bool charging = d.Charging == ChargingState.Charging;
        string state = d.Charging switch
        {
            ChargingState.Charging => " ⚡充电中",
            ChargingState.Full => " 已充满",
            _ => "",
        };
        if (!d.IsOnline) state = " · 离线";

        var progressColor = d.IsOnline
            ? UiPalette.BatteryLevel(percent, charging)
            : UiPalette.Offline;
        Brush percentBrush = !d.IsOnline ? UiPalette.Brush(UiPalette.Offline)
            : charging ? UiPalette.Brush(UiPalette.Charging)
            : percent is <= 20 ? UiPalette.Brush(UiPalette.Low)
            : Application.Current.Resources["TextFillColorPrimaryBrush"] as Brush
              ?? UiPalette.Brush(UiPalette.Offline);

        return new DeviceRowModel
        {
            IconGlyph = icon,
            Name = d.DisplayName,
            PercentText = percent.HasValue ? $"{percent}%" : "--",
            Progress = percent ?? 0,
            SourceText = d.Source switch
            {
                DeviceSource.Ble or DeviceSource.BluetoothClassic => "蓝牙",
                DeviceSource.System => "本机",
                _ => "2.4G",
            },
            SubText = $"{d.Protocol}{state} · 更新于 {d.LastUpdated:HH:mm:ss}",
            IsCharging = charging,
            ProgressBrush = UiPalette.Brush(progressColor),
            PercentBrush = percentBrush,
        };
    }

    /// <summary>
    /// 设备类型 → 图标字形。**优先用插件声明的 <see cref="DeviceStatus.Kind"/>**，
    /// 没声明时才按显示名猜（<see cref="DeviceKindGuesser"/>）——靠名字猜是不可靠的：
    /// 雷柏鼠标叫 "Rapoo Gaming Device"、迈从键盘叫 "MCHOSE 2.4G Wireless"、耳机叫
    /// "EDIFIER W820NB 双金标版"，名字里一个关键字都没有，全都会退化成通用蓝牙图标（真机踩过）。
    /// 字形取自 Segoe MDL2 Assets / Segoe Fluent Icons。
    /// </summary>
    private static string GlyphFor(DeviceKind kind, string displayName)
    {
        if (kind == DeviceKind.Unknown) kind = DeviceKindGuesser.Guess(displayName);
        return kind switch
        {
            DeviceKind.Gamepad => "\uE7FC",   // Game
            DeviceKind.Mouse => "\uE962",     // Mouse
            DeviceKind.Keyboard => "\uE765",  // KeyboardClassic
            DeviceKind.Headset => "\uE7F6",   // Headphone
            DeviceKind.Speaker => "\uE7F5",   // Speakers
            DeviceKind.Computer => "\uE7F8",  // Devices2（显示器造型，代表本机）
            _ => "\uE702",                    // Bluetooth（兜底）
        };
    }
}
