using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using VigilWatt.Core.Models;
using Windows.UI;

namespace VigilWatt.App.Services;

/// <summary>
/// 任务栏贴片"一格"长什么样：设备图形 + 电量数字（充电时多一个 ⚡），按电量状态着色。
/// 与窗口类分开是为了让窗口只管道具（定位/跟随/拖动），配色与排版集中在一处。
/// **贴片刻意不画底色与边框**（用户 2026-10-10 反馈"白框很突兀"）——文字直接浮在任务栏上，
/// 所以这里只有"前景色"没有"表面色"。
/// **宽度不在这里算**：字宽估算差一两像素就会把最右一格挤出去，交给窗口实测（见 TaskbarBatteryWindow.MeasureContentWidth）。
/// 颜色全部硬编码、按主题分两套：刷新走 DispatcherQueue 回调，那里查 Resources 抛异常会直接 fail-fast（runtime-pitfalls §11）。
/// </summary>
public static class TaskbarChipFactory
{
    public const double IconBox = 15;          // 设备图形槽位边长（图形本体 9~14，居中）
    public const double IconTextGap = 4;
    public const double DividerW = 1, DividerH = 14;
    public const double PercentFontSize = 13;

    /// <summary>空态文案的固定宽度（DIP）。</summary>
    private const double EmptyTextWidthDip = 104;

    /// <summary>贴片前景配色。**没有底色/边框**——贴片是"浮在任务栏上的一行读数"，不是一张卡片。</summary>
    public readonly record struct Skin(Color Divider, Color IconStroke, Color Text);

    public static Skin SkinFor(ElementTheme theme) => theme == ElementTheme.Dark
        ? new Skin(
            Divider: Color.FromArgb(0x3D, 0xFF, 0xFF, 0xFF),
            IconStroke: Color.FromArgb(0xFF, 0xD2, 0xD2, 0xD2),
            Text: Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF))
        : new Skin(
            Divider: Color.FromArgb(0x33, 0x00, 0x00, 0x00),
            IconStroke: Color.FromArgb(0xFF, 0x5F, 0x5E, 0x5A),
            Text: Color.FromArgb(0xFF, 0x1F, 0x1F, 0x1F));

    public static FrameworkElement Chip(DeviceStatus d, Skin skin)
    {
        var percent = d.BatteryPercent;
        var charging = DeviceStatusFormatter.IsCharging(d.Charging);
        // 数字与图形同用状态色，和列表页/小组件/托盘是同一套口径（UiPalette.BatteryLevel）
        var stateColor = UiPalette.BatteryLevel(percent, charging);

        var chip = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = IconTextGap,
            VerticalAlignment = VerticalAlignment.Center,
        };

        var iconHost = new Grid { Width = IconBox, Height = IconBox, VerticalAlignment = VerticalAlignment.Center };
        iconHost.Children.Add(DeviceIconFactory.Glyph(d.Kind, skin.IconStroke));
        chip.Children.Add(iconHost);

        if (charging)
        {
            chip.Children.Add(new TextBlock
            {
                Text = "⚡",
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = new SolidColorBrush(stateColor),
            });
        }

        chip.Children.Add(new TextBlock
        {
            Text = percent.HasValue ? $"{percent}%" : "--",
            FontSize = PercentFontSize,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = new SolidColorBrush(stateColor),
        });
        return chip;
    }

    public static FrameworkElement Divider(Skin skin) => new Border
    {
        Width = DividerW,
        Height = DividerH,
        VerticalAlignment = VerticalAlignment.Center,
        Background = new SolidColorBrush(skin.Divider),
    };

    /// <summary>超出台数的折叠标记 "+N"。</summary>
    public static FrameworkElement Overflow(int count, Skin skin) => new TextBlock
    {
        Text = $"+{count}",
        FontSize = PercentFontSize,
        VerticalAlignment = VerticalAlignment.Center,
        Foreground = new SolidColorBrush(skin.Text) { Opacity = 0.6 },
    };

    public static FrameworkElement EmptyText(Skin skin) => new TextBlock
    {
        Text = "暂无在线设备",
        FontSize = 12,
        Width = EmptyTextWidthDip,
        TextAlignment = TextAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
        Foreground = new SolidColorBrush(skin.Text) { Opacity = 0.6 },
    };
}
