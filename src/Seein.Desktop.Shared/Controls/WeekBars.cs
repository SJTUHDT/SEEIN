using System.Globalization;
using System.Windows;
using System.Windows.Media;
using Brush = System.Windows.Media.Brush;
using FontFamily = System.Windows.Media.FontFamily;
using Color = System.Windows.Media.Color;
using DrawingContext = System.Windows.Media.DrawingContext;
using FormattedText = System.Windows.Media.FormattedText;
using Pen = System.Windows.Media.Pen;
using Point = System.Windows.Point;
using Rect = System.Windows.Rect;
using VisualTreeHelper = System.Windows.Media.VisualTreeHelper;

namespace Seein.Tray;

/// <summary>Seven day bar chart used by the week mode of both application pages.</summary>
public sealed class WeekBars : AccessibleChart
{
    private IReadOnlyList<double> _values = [];
    private IReadOnlyList<string> _labels = [];
    private Func<double, string> _format = value => value.ToString("0", CultureInfo.CurrentCulture);

    public void SetData(IReadOnlyList<double> values, IReadOnlyList<string> labels, Func<double, string> format)
    {
        _values = values;
        _labels = labels;
        _format = format;
        System.Windows.Automation.AutomationProperties.SetHelpText(this, string.Join("，",
            labels.Zip(values, (label, value) => $"{label}：{format(value)}")));
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext context)
    {
        base.OnRender(context);
        var width = ActualWidth;
        var height = ActualHeight;
        if (width < 180 || height < 90 || _values.Count == 0) return;

        var textBrush = Resource("MutedTextBrush", Color.FromRgb(0x5C, 0x5C, 0x5C));
        var barBrush = Resource("AccentBrush", Color.FromRgb(0x00, 0x67, 0xC0));
        var grid = Resource("ChartGridBrush", Color.FromRgb(0xED, 0xED, 0xED));

        const double left = 8, right = 8, top = 22, bottom = 24;
        var plot = new Rect(left, top, width - left - right, Math.Max(20, height - top - bottom));
        var max = Math.Max(1, _values.Max());
        var slot = plot.Width / _values.Count;
        var barWidth = Math.Max(10, slot * 0.52);

        var gridPen = new Pen(grid, 1);
        context.DrawLine(gridPen, new Point(plot.Left, plot.Bottom), new Point(plot.Right, plot.Bottom));

        for (var index = 0; index < _values.Count; index++)
        {
            var value = Math.Max(0, _values[index]);
            var barHeight = value / max * (plot.Height - 16);
            var x = plot.Left + slot * index + (slot - barWidth) / 2;
            var rect = new Rect(x, plot.Bottom - barHeight, barWidth, Math.Max(2, barHeight));
            context.DrawRoundedRectangle(barBrush, null, rect, 3, 3);
            DrawText(context, _format(value), 12, textBrush, new Point(x - 6, rect.Top - 16), barWidth + 12);
            if (index < _labels.Count)
                DrawText(context, _labels[index], 12, textBrush, new Point(x - 6, plot.Bottom + 5), barWidth + 12);
        }
    }

    private void DrawText(DrawingContext context, string value, double size, Brush brush, Point origin, double maxWidth)
    {
        var text = new FormattedText(value, CultureInfo.CurrentUICulture, System.Windows.FlowDirection.LeftToRight,
            new Typeface(new FontFamily("Segoe UI Variable Text, Microsoft YaHei UI"), FontStyles.Normal,
                FontWeights.Normal, FontStretches.Normal), size, brush, VisualTreeHelper.GetDpi(this).PixelsPerDip)
        {
            MaxTextWidth = Math.Max(1, maxWidth),
            MaxTextHeight = size * 1.6,
            TextAlignment = TextAlignment.Center
        };
        context.DrawText(text, origin);
    }

    private Brush Resource(string key, Color fallback) => TryFindResource(key) as Brush ?? new SolidColorBrush(fallback);
}
