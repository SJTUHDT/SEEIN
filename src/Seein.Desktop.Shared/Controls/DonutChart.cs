using System.Globalization;
using System.Windows;
using System.Windows.Media;
using Brush = System.Windows.Media.Brush;
using FontFamily = System.Windows.Media.FontFamily;
using Color = System.Windows.Media.Color;
using DrawingContext = System.Windows.Media.DrawingContext;
using FormattedText = System.Windows.Media.FormattedText;
using Point = System.Windows.Point;
using Size = System.Windows.Size;
using VisualTreeHelper = System.Windows.Media.VisualTreeHelper;

namespace Seein.Tray;

public sealed record DonutSlice(string Label, double Value, Color Color);

/// <summary>Power share donut with a centred total, used by the application power page.</summary>
public sealed class DonutChart : AccessibleChart
{
    private IReadOnlyList<DonutSlice> _slices = [];
    private string _centerLabel = string.Empty;
    private string _centerValue = string.Empty;

    public void SetData(IReadOnlyList<DonutSlice> slices, string centerLabel, string centerValue)
    {
        _slices = slices;
        _centerLabel = centerLabel;
        _centerValue = centerValue;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext context)
    {
        base.OnRender(context);
        if (ActualWidth < 120 || ActualHeight < 120) return;

        var textBrush = Resource("MutedTextBrush", Color.FromRgb(0x5C, 0x5C, 0x5C));
        var body = Resource("TextBrush", Colors.Black);
        var empty = Resource("PanelAltBrush", Color.FromRgb(0xF2, 0xF2, 0xF2));

        var size = Math.Min(ActualWidth, ActualHeight) - 16;
        var radius = size / 2;
        var center = new Point(ActualWidth / 2, ActualHeight / 2 + 4);
        var total = _slices.Sum(slice => Math.Max(0, slice.Value));

        context.DrawEllipse(empty, null, center, radius, radius);
        if (total > 0)
        {
            var startAngle = -90d;
            foreach (var slice in _slices)
            {
                var value = Math.Max(0, slice.Value);
                if (value <= 0) continue;
                var sweep = value / total * 360d;
                DrawSlice(context, center, radius, startAngle, sweep, new SolidColorBrush(slice.Color));
                startAngle += sweep;
            }
        }

        var inner = radius * 0.62;
        var background = Resource("PanelBrush", Colors.White);
        context.DrawEllipse(background, null, center, inner, inner);
        DrawCentered(context, _centerValue, 20, body, center, inner * 1.6, -10);
        DrawCentered(context, _centerLabel, 11, textBrush, center, inner * 1.6, 12);
    }

    private static void DrawSlice(DrawingContext context, Point center, double radius, double startAngle, double sweep, Brush brush)
    {
        var geometry = new StreamGeometry();
        using (var figure = geometry.Open())
        {
            var start = PointOnCircle(center, radius, startAngle);
            var end = PointOnCircle(center, radius, startAngle + sweep);
            figure.BeginFigure(center, true, true);
            figure.LineTo(start, true, false);
            figure.ArcTo(end, new Size(radius, radius), 0, sweep > 180, SweepDirection.Clockwise, true, false);
        }
        geometry.Freeze();
        context.DrawGeometry(brush, null, geometry);
    }

    private static Point PointOnCircle(Point center, double radius, double angle)
    {
        var radians = angle * Math.PI / 180d;
        return new Point(center.X + radius * Math.Cos(radians), center.Y + radius * Math.Sin(radians));
    }

    private void DrawCentered(DrawingContext context, string value, double size, Brush brush, Point center, double maxWidth, double offsetY)
    {
        var text = new FormattedText(value, CultureInfo.CurrentUICulture, System.Windows.FlowDirection.LeftToRight,
            new Typeface(new FontFamily("Segoe UI Variable Text, Microsoft YaHei UI"), FontStyles.Normal,
                FontWeights.SemiBold, FontStretches.Normal), size, brush, VisualTreeHelper.GetDpi(this).PixelsPerDip)
        {
            MaxTextWidth = Math.Max(1, maxWidth),
            MaxTextHeight = size * 1.8,
            TextAlignment = TextAlignment.Center
        };
        context.DrawText(text, new Point(center.X - maxWidth / 2, center.Y + offsetY - text.Height / 2));
    }

    private Brush Resource(string key, Color fallback) => TryFindResource(key) as Brush ?? new SolidColorBrush(fallback);
}
