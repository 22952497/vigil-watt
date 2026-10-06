using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.UI;

namespace EasyBluetooth.App.Views;

/// <summary>
/// 轻量电量曲线图（纯 XAML Shapes 绘制，不引图表库）。
/// 数据：若干系列，每系列一组 (时间, 百分比) 点。
/// 坐标轴：默认自适应数据起止；传入 domainStart/domainEnd 则强制使用指定时间窗
/// （历史页切"最近 7 天/30 天"时轴必须跟着变，否则数据不足 24h 时三个档位渲染完全相同，像"切换没反应"）。
/// </summary>
public sealed class BatteryChartControl : UserControl
{
    public sealed class Series
    {
        public required string Name;
        public required Color Color;
        public required IReadOnlyList<(DateTimeOffset Time, int Percent)> Points;
    }

    private readonly Grid _root = new();
    private readonly Canvas _canvas = new();
    private readonly StackPanel _legend = new()
    {
        Orientation = Orientation.Horizontal,
        Spacing = 16,
        Margin = new Thickness(44, 8, 16, 0),
    };
    private IReadOnlyList<Series> _series = Array.Empty<Series>();
    private DateTimeOffset? _domainStart, _domainEnd;

    public BatteryChartControl()
    {
        _root.Children.Add(_canvas);
        _root.Children.Add(_legend);
        Content = _root;
        SizeChanged += (_, _) => Render();
    }

    public void SetData(IReadOnlyList<Series> series,
        DateTimeOffset? domainStart = null, DateTimeOffset? domainEnd = null)
    {
        _series = series;
        _domainStart = domainStart;
        _domainEnd = domainEnd;
        Render();
    }

    private void Render()
    {
        _canvas.Children.Clear();
        double w = Math.Max(ActualWidth, 100);
        double h = Math.Max(ActualHeight, 100);
        _canvas.Width = w;
        _canvas.Height = h;
        // padT 给顶部图例留出空间
        const double padL = 44, padR = 16, padT = 36, padB = 28;
        double plotW = w - padL - padR;
        double plotH = h - padT - padB;

        DateTimeOffset tStart, tEnd;
        if (_domainStart.HasValue && _domainEnd.HasValue)
        {
            tStart = _domainStart.Value;
            tEnd = _domainEnd.Value;
        }
        else
        {
            tStart = DateTimeOffset.MaxValue;
            tEnd = DateTimeOffset.MinValue;
            foreach (var s in _series)
                foreach (var p in s.Points)
                {
                    if (p.Time < tStart) tStart = p.Time;
                    if (p.Time > tEnd) tEnd = p.Time;
                }
            if (tStart > tEnd) return;
            if ((tEnd - tStart).TotalSeconds < 60) tEnd = tStart.AddSeconds(60);
        }
        if (tEnd <= tStart) return;
        double totalSec = (tEnd - tStart).TotalSeconds;

        var gridBrush = Application.Current.Resources["DividerStrokeColorDefaultBrush"] as SolidColorBrush
                        ?? new SolidColorBrush(Color.FromArgb(60, 128, 128, 128));
        var axisBrush = Application.Current.Resources["TextFillColorTertiaryBrush"] as Brush
                        ?? new SolidColorBrush(Color.FromArgb(160, 128, 128, 128));
        var textBrush = Application.Current.Resources["TextFillColorSecondaryBrush"] as Brush
                        ?? new SolidColorBrush(Color.FromArgb(200, 128, 128, 128));

        // 图例（色点 + 设备名，只列有数据的系列）
        _legend.Children.Clear();
        foreach (var s in _series)
        {
            if (s.Points.Count == 0) continue;
            var item = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            item.Children.Add(new Ellipse
            {
                Width = 9,
                Height = 9,
                Fill = new SolidColorBrush(s.Color),
                VerticalAlignment = VerticalAlignment.Center,
            });
            var name = MakeLabel(s.Name, textBrush, 11);
            name.VerticalAlignment = VerticalAlignment.Center;
            item.Children.Add(name);
            _legend.Children.Add(item);
        }

        // 横向网格线 + Y 标签（0/25/50/75/100）
        foreach (int yPct in new[] { 0, 25, 50, 75, 100 })
        {
            double y = padT + plotH * (1 - yPct / 100.0);
            _canvas.Children.Add(new Line
            {
                X1 = padL, Y1 = y, X2 = padL + plotW, Y2 = y,
                Stroke = gridBrush, StrokeThickness = 1,
            });
            var yLabel = MakeLabel($"{yPct}%", axisBrush, 10);
            Canvas.SetLeft(yLabel, 4);
            Canvas.SetTop(yLabel, y - 8);
            _canvas.Children.Add(yLabel);
        }

        // X 时间标签（起点到终点 5 档）
        for (int i = 0; i <= 4; i++)
        {
            var t = tStart.AddSeconds(totalSec * i / 4.0);
            double x = padL + plotW * i / 4.0;
            var label = MakeLabel(t.LocalDateTime.ToString("MM-dd HH:mm"), axisBrush, 10);
            double lx = Math.Min(Math.Max(x - 45, padL - 24), w - 92);
            Canvas.SetLeft(label, lx);
            Canvas.SetTop(label, h - 20);
            _canvas.Children.Add(label);
        }

        if (!_series.Any(s => s.Points.Count > 0))
        {
            var hint = MakeLabel("该时间范围内暂无采样数据", textBrush, 12);
            Canvas.SetLeft(hint, padL + plotW / 2 - 78);
            Canvas.SetTop(hint, padT + plotH / 2 - 8);
            _canvas.Children.Add(hint);
            return;
        }

        // 各系列曲线 + 末端数值
        foreach (var s in _series)
        {
            if (s.Points.Count == 0) continue;
            var pts = new PointCollection();
            foreach (var p in s.Points)
            {
                double x = padL + plotW * (p.Time - tStart).TotalSeconds / totalSec;
                double y = padT + plotH * (1 - Math.Clamp(p.Percent, 0, 100) / 100.0);
                pts.Add(new Windows.Foundation.Point(x, y));
            }
            _canvas.Children.Add(new Polyline
            {
                Points = pts,
                Stroke = new SolidColorBrush(s.Color),
                StrokeThickness = 2,
                StrokeLineJoin = PenLineJoin.Round,
            });

            var last = s.Points[^1];
            var tip = MakeLabel($"{last.Percent}%", new SolidColorBrush(s.Color), 11);
            // 末端标签靠右缘时左移，避免被卡片裁掉（估宽：11px 字号每字符约 7px）
            double tipX = padL + plotW * (last.Time - tStart).TotalSeconds / totalSec + 4;
            double tipEstWidth = $"{last.Percent}%".Length * 7;
            if (tipX + tipEstWidth > w - padR) tipX = w - padR - tipEstWidth;
            Canvas.SetLeft(tip, tipX);
            Canvas.SetTop(tip, padT + plotH * (1 - last.Percent / 100.0) - 8);
            _canvas.Children.Add(tip);
        }
    }

    private static TextBlock MakeLabel(string text, Brush brush, double size)
    {
        return new TextBlock
        {
            Text = text,
            FontSize = size,
            Foreground = brush,
        };
    }
}
