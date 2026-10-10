using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using VigilWatt.Core.Models;
using Windows.UI;

namespace VigilWatt.App.Services;

/// <summary>
/// 设备类型图标的绘制工厂：桌面小组件与任务栏贴片共用同一套图形，避免两处各画一份而慢慢分叉。
/// 全部用纯形状拼装，**不用字体图标**——Segoe 字体缺字形时会渲染成方块（见 dev/runtime-pitfalls.md §5.3）。
/// 尺寸按"图标方块 22 DIP"设计；任务栏贴片直接取 <see cref="Glyph"/> 单画图形、不套方块底。
/// </summary>
public static class DeviceIconFactory
{
    /// <summary>带圆角方块底的完整图标（小组件用）；<paramref name="size"/> 是方块边长。</summary>
    public static FrameworkElement Boxed(DeviceKind kind, Color fill, Color stroke, double size) =>
        new Grid
        {
            Width = size,
            Height = size,
            CornerRadius = new CornerRadius(6),
            Background = new SolidColorBrush(fill),
            VerticalAlignment = VerticalAlignment.Center,
            Children = { Glyph(kind, stroke) },
        };

    /// <summary>设备图形本身（无底），任务栏贴片直接用它。</summary>
    public static UIElement Glyph(DeviceKind kind, Color stroke)
    {
        var brush = new SolidColorBrush(stroke);

        switch (kind)
        {
            case DeviceKind.Keyboard:
            {
                var box = new Grid { Width = 13, Height = 9 };
                box.Children.Add(new Border
                {
                    BorderThickness = new Thickness(1.2),
                    CornerRadius = new CornerRadius(2.5),
                    BorderBrush = brush,
                });
                box.Children.Add(new Border
                {
                    Width = 7,
                    Height = 1.4,
                    Background = brush,
                    CornerRadius = new CornerRadius(0.7),
                    VerticalAlignment = VerticalAlignment.Bottom,
                    Margin = new Thickness(0, 0, 0, 1.8),
                });
                return box;
            }

            case DeviceKind.Mouse:
            {
                var box = new Grid { Width = 9, Height = 13 };
                box.Children.Add(new Border
                {
                    BorderThickness = new Thickness(1.2),
                    CornerRadius = new CornerRadius(4.5),
                    BorderBrush = brush,
                });
                box.Children.Add(new Border
                {
                    Width = 1.2,
                    Height = 3.5,
                    Background = brush,
                    VerticalAlignment = VerticalAlignment.Top,
                    Margin = new Thickness(0, 2, 0, 0),
                });
                return box;
            }

            case DeviceKind.Headset:
            {
                var box = new Grid { Width = 13, Height = 12 };
                box.Children.Add(new Border
                {
                    Width = 11,
                    Height = 7,
                    CornerRadius = new CornerRadius(5.5, 5.5, 0, 0),
                    BorderThickness = new Thickness(1.2, 1.2, 0, 0),
                    BorderBrush = brush,
                    VerticalAlignment = VerticalAlignment.Top,
                });
                foreach (var h in new[] { HorizontalAlignment.Left, HorizontalAlignment.Right })
                {
                    box.Children.Add(new Border
                    {
                        Width = 2.6,
                        Height = 4.5,
                        CornerRadius = new CornerRadius(1.3),
                        Background = brush,
                        HorizontalAlignment = h,
                        VerticalAlignment = VerticalAlignment.Bottom,
                    });
                }
                return box;
            }

            case DeviceKind.Speaker:
            {
                var box = new Grid { Width = 9.5, Height = 13 };
                box.Children.Add(new Border
                {
                    BorderThickness = new Thickness(1.2),
                    CornerRadius = new CornerRadius(2.5),
                    BorderBrush = brush,
                });
                box.Children.Add(new Ellipse
                {
                    Width = 3,
                    Height = 3,
                    Fill = brush,
                    VerticalAlignment = VerticalAlignment.Top,
                    Margin = new Thickness(0, 2.2, 0, 0),
                });
                box.Children.Add(new Ellipse
                {
                    Width = 4.4,
                    Height = 4.4,
                    Fill = brush,
                    VerticalAlignment = VerticalAlignment.Bottom,
                    Margin = new Thickness(0, 0, 0, 2),
                });
                return box;
            }

            case DeviceKind.Gamepad:
            {
                var box = new Grid { Width = 14, Height = 9 };
                box.Children.Add(new Border
                {
                    BorderThickness = new Thickness(1.2),
                    CornerRadius = new CornerRadius(4.5),
                    BorderBrush = brush,
                });
                foreach (var h in new[] { HorizontalAlignment.Left, HorizontalAlignment.Right })
                {
                    box.Children.Add(new Ellipse
                    {
                        Width = 2.6,
                        Height = 2.6,
                        Fill = brush,
                        HorizontalAlignment = h,
                        Margin = new Thickness(2, 0, 2, 0),
                        VerticalAlignment = VerticalAlignment.Center,
                    });
                }
                return box;
            }

            case DeviceKind.Computer:
            {
                // 显示器造型：屏幕 + 支架。与设置/列表里的 \uE7F8 语义一致。
                var box = new Grid { Width = 14, Height = 11 };
                box.Children.Add(new Border
                {
                    Width = 14,
                    Height = 9.5,
                    BorderThickness = new Thickness(1.2),
                    CornerRadius = new CornerRadius(1.8),
                    BorderBrush = brush,
                    VerticalAlignment = VerticalAlignment.Top,
                });
                box.Children.Add(new Border
                {
                    Width = 5,
                    Height = 1.4,
                    Background = brush,
                    CornerRadius = new CornerRadius(0.7),
                    VerticalAlignment = VerticalAlignment.Bottom,
                });
                return box;
            }

            default:
                return new Ellipse
                {
                    Width = 9,
                    Height = 9,
                    Stroke = brush,
                    StrokeThickness = 1.4,
                };
        }
    }
}
