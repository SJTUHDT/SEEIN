using Seein.Core;
using System.Globalization;
using System.Windows;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;
using Colors = System.Windows.Media.Colors;
using DrawingContext = System.Windows.Media.DrawingContext;
using FormattedText = System.Windows.Media.FormattedText;
using Pen = System.Windows.Media.Pen;
using PenLineCap = System.Windows.Media.PenLineCap;
using Point = System.Windows.Point;
using RectangleGeometry = System.Windows.Media.RectangleGeometry;
using Rect = System.Windows.Rect;
using SolidColorBrush = System.Windows.Media.SolidColorBrush;
using StreamGeometry = System.Windows.Media.StreamGeometry;
using Typeface = System.Windows.Media.Typeface;
using VisualTreeHelper = System.Windows.Media.VisualTreeHelper;

namespace Seein.Tray;

public enum PowerHistoryChartDisplayMode
{
    Samples,
    Curve
}

public sealed class PowerHistoryChart : AccessibleChart
{
    private IReadOnlyList<PowerHistoryPoint> _points = [];
    private double[] _minuteAverages = [];
    private PowerHistoryChartDisplayMode _displayMode = PowerHistoryChartDisplayMode.Curve;

    public int SampleCount => _points.Count;

    public void SetDay(DateOnly? date, IReadOnlyList<PowerHistoryPoint> points)
    {
        _points = points;
        _minuteAverages = date.HasValue ? DailyPowerCurve.GetMinuteAverages(date.Value, points, DateTimeOffset.Now) : [];
        InvalidateVisual();
    }

    public PowerHistoryChartDisplayMode DisplayMode
    {
        get => _displayMode;
        set { _displayMode = value; InvalidateVisual(); }
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);
        var foreground = Brush("MutedTextBrush", Colors.Gray);
        var grid = Brush("ChartGridBrush", Colors.LightGray);
        var accent = Brush("AccentBrush", Colors.SeaGreen);
        var band = Brush("ChartBandBrush", Color.FromArgb(75, 0, 128, 96));
        var width = ActualWidth;
        var height = ActualHeight;
        if (width < 160 || height < 100) return;

        const double left = 48, right = 16, top = 14, bottom = 30;
        var plot = new Rect(left, top, width - left - right, height - top - bottom);
        var textSize = 10d;
        var maxWatts = Math.Max(100, _points.Count == 0 ? 100 : _points.Max(point => point.MaxWatts));
        var step = NiceStep(maxWatts / 4);
        var axisMax = Math.Ceiling(maxWatts / step) * step;
        var gridPen = new Pen(grid, 1);
        var averagePen = new Pen(accent, 2.25);
        var samplePen = new Pen(band, 4);
        averagePen.StartLineCap = PenLineCap.Round;
        averagePen.EndLineCap = PenLineCap.Round;
        samplePen.StartLineCap = PenLineCap.Round;
        samplePen.EndLineCap = PenLineCap.Round;

        for (var value = 0d; value <= axisMax + .001; value += step)
        {
            var y = plot.Bottom - value / axisMax * plot.Height;
            drawingContext.DrawLine(gridPen, new Point(plot.Left, y), new Point(plot.Right, y));
            DrawText(drawingContext, $"{value:0}", textSize, foreground, new Point(0, y - 8), left - 8);
        }

        foreach (var hour in new[] { 0, 6, 12, 18, 24 })
        {
            var x = plot.Left + hour / 24d * plot.Width;
            DrawText(drawingContext, hour == 24 ? "24:00" : $"{hour:00}:00", textSize, foreground,
                new Point(x - (hour == 0 ? 0 : hour == 24 ? 34 : 17), plot.Bottom + 7), 36);
        }

        if (_minuteAverages.Length == 0) return;
        drawingContext.PushClip(new RectangleGeometry(plot));
        if (DisplayMode == PowerHistoryChartDisplayMode.Samples)
        {
            foreach (var point in _points)
            {
                var local = DateTimeOffset.FromUnixTimeSeconds(point.UnixSeconds).ToLocalTime();
                var minuteOfDay = local.Hour * 60 + local.Minute;
                var x = plot.Left + (double)minuteOfDay / DailyPowerCurve.MinutesPerDay * plot.Width;
                var yMin = plot.Bottom - Math.Clamp(point.MinWatts / axisMax, 0, 1) * plot.Height;
                var yMax = plot.Bottom - Math.Clamp(point.MaxWatts / axisMax, 0, 1) * plot.Height;
                drawingContext.DrawLine(samplePen, new Point(x, yMin), new Point(x, yMax));
            }
        }
        else
        {
            var average = new StreamGeometry();
            using (var context = average.Open())
            {
                var last = _minuteAverages.Length - 1;
                Point Position(int minute, double watts) => new(
                    plot.Left + (double)minute / DailyPowerCurve.MinutesPerDay * plot.Width,
                    plot.Bottom - Math.Clamp(watts / axisMax, 0, 1) * plot.Height);
                var firstVisible = Array.FindIndex(_minuteAverages, double.IsFinite);
                var lastVisible = Array.FindLastIndex(_minuteAverages, double.IsFinite);
                if (firstVisible >= 0)
                {
                    var firstPoint = Position(firstVisible, _minuteAverages[firstVisible]);
                    context.BeginFigure(firstPoint, false, false);
                    if (firstVisible == lastVisible)
                        drawingContext.DrawEllipse(accent, null, firstPoint, 1.5, 1.5);
                    for (var minute = firstVisible + 1; minute <= lastVisible; minute++)
                    {
                        var watts = _minuteAverages[minute];
                        if (minute < lastVisible && watts == _minuteAverages[minute - 1] && watts == _minuteAverages[minute + 1]) continue;
                        context.LineTo(Position(minute, watts), true, false);
                    }
                    if (_minuteAverages.Length == DailyPowerCurve.MinutesPerDay && lastVisible == last)
                        context.LineTo(Position(DailyPowerCurve.MinutesPerDay, _minuteAverages[last]), true, false);
                }
            }
            average.Freeze();
            drawingContext.DrawGeometry(null, averagePen, average);
        }
        drawingContext.Pop();
    }

    private Brush Brush(string key, Color fallback) =>
        TryFindResource(key) as Brush ?? new SolidColorBrush(fallback);

    private void DrawText(DrawingContext context, string value, double size, Brush brush, Point origin, double maxWidth)
    {
        var text = new FormattedText(value, CultureInfo.CurrentUICulture, System.Windows.FlowDirection.LeftToRight,
            new Typeface("Segoe UI Variable Text, Microsoft YaHei UI"), size, brush, VisualTreeHelper.GetDpi(this).PixelsPerDip)
        { MaxTextWidth = Math.Max(1, maxWidth), MaxTextHeight = 18 };
        context.DrawText(text, origin);
    }

    private static double NiceStep(double raw)
    {
        var magnitude = Math.Pow(10, Math.Floor(Math.Log10(Math.Max(1, raw))));
        var normalized = raw / magnitude;
        var nice = normalized <= 1 ? 1 : normalized <= 2 ? 2 : normalized <= 5 ? 5 : 10;
        return nice * magnitude;
    }
}
