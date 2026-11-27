using Seein.Core.Thermal;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Pen = System.Windows.Media.Pen;
using Point = System.Windows.Point;

namespace Seein.Tray;

public sealed class FanTrendChart : FrameworkElement
{
    private IReadOnlyList<FanTrendSample> _samples = [];
    public IReadOnlyList<FanTrendSample> Samples
    {
        get => _samples;
        set { _samples = value; InvalidateVisual(); }
    }

    protected override void OnRender(DrawingContext dc)
    {
        if (ActualWidth < 150 || ActualHeight < 145) return;
        var text = TryFindResource("MutedTextBrush") as Brush ?? Brushes.Gray;
        var grid = new Pen(TryFindResource("ChartGridBrush") as Brush ?? Brushes.Gray, 1);
        var accent = new Pen(TryFindResource("AccentBrush") as Brush ?? Brushes.RoyalBlue, 2);
        if (_samples.Count < 2)
        {
            Label(dc, "等待采集趋势样本", text, new(0, 8));
            return;
        }

        var end = _samples[^1].At;
        var start = end.AddMinutes(-10);
        var height = (ActualHeight - 29) / 3;
        var rpmMax = Math.Max(500, Math.Ceiling((_samples.Max(sample => sample.Rpm) ?? 0) / 500d) * 500);
        var dual = _samples.Any(sample => sample.SecondaryTemperatureC.HasValue);
        DrawBand("温度 °C", s => s.TemperatureC, 130, 0);
        if (dual)
        {
            DrawSeries(s => s.SecondaryTemperatureC, 130, 0, new Pen(accent.Brush, 1.4) { DashStyle = DashStyles.Dash });
            Label(dc, "━━ CPU", text, new(0, 22));
            Label(dc, "┈┈ GPU", text, new(0, 39));
        }
        DrawBand("目标 %", s => s.TargetPercent, 100, 1);
        DrawBand("实测 RPM", s => s.Rpm, rpmMax, 2);
        Label(dc, "−10 分钟", text, new(66, ActualHeight - 21));
        Label(dc, "现在", text, new(ActualWidth - 29, ActualHeight - 21));

        void DrawBand(string title, Func<FanTrendSample, double?> value, double max, int band)
        {
            var top = band * height;
            Label(dc, title, text, new(0, top + 3));
            Label(dc, $"{max:0}", text, new(66, top + 1));
            Label(dc, "0", text, new(66, top + height - 20));
            dc.DrawLine(grid, new(97, top + 5), new(ActualWidth - 5, top + 5));
            dc.DrawLine(grid, new(97, top + height - 6), new(ActualWidth - 5, top + height - 6));
            if (!_samples.Any(sample => value(sample).HasValue))
                Label(dc, "无回读数据", text, new(103, top + height / 2 - 7));
            else DrawSeries(value, max, band, accent);
        }

        void DrawSeries(Func<FanTrendSample, double?> value, double max, int band, Pen pen)
        {
            var top = band * height;
            Point? prior = null;
            foreach (var sample in _samples.Where(sample => sample.At >= start))
            {
                if (value(sample) is not double actual) { prior = null; continue; }
                var position = new Point(97 + (sample.At - start).TotalMinutes / 10 * (ActualWidth - 102),
                    top + height - 6 - Math.Clamp(actual / max, 0, 1) * (height - 11));
                if (prior is Point last) dc.DrawLine(pen, last, position);
                prior = position;
            }
        }
    }

    private void Label(DrawingContext dc, string value, Brush brush, Point point) =>
        dc.DrawText(new FormattedText(value, CultureInfo.CurrentCulture, System.Windows.FlowDirection.LeftToRight,
            new Typeface("Segoe UI Variable Text, Microsoft YaHei UI"), 11, brush, VisualTreeHelper.GetDpi(this).PixelsPerDip), point);
}
