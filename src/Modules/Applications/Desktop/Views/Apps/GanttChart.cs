using Seein.Core;
using Seein.Core.Apps;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;
using DrawingContext = System.Windows.Media.DrawingContext;
using FormattedText = System.Windows.Media.FormattedText;
using FontFamily = System.Windows.Media.FontFamily;
using Pen = System.Windows.Media.Pen;
using Point = System.Windows.Point;
using Rect = System.Windows.Rect;
using VisualTreeHelper = System.Windows.Media.VisualTreeHelper;

namespace Seein.Tray;

public sealed class GanttRow
{
    public required string Name { get; init; }
    public required IReadOnlyList<AppSpan> Spans { get; init; }
    public double Seconds { get; init; }
    public bool IsSystem { get; init; }
    public uint? Color { get; init; }
}

/// <summary>
/// 24 hour timeline: the first row is the machine uptime bar, every following row is one application
/// with its merged runtime intervals.  Sleep / shutdown gaps and away-from-keyboard periods are drawn
/// as shaded bands so a missing bar is never mistaken for "the app was closed".
/// </summary>
public sealed class GanttChart : AccessibleChart
{
    private static readonly Color[] Palette =
    [
        Color.FromRgb(0x00, 0x67, 0xC0), Color.FromRgb(0x2B, 0x88, 0xD8), Color.FromRgb(0x4A, 0x9E, 0xDD),
        Color.FromRgb(0x87, 0x64, 0xB8), Color.FromRgb(0xC2, 0x39, 0xB3), Color.FromRgb(0x00, 0x8C, 0x6E)
    ];

    private const double LabelWidth = 132;
    private const double RowHeight = 15;
    private const double RowGap = 4;
    private const double AxisHeight = 24;

    private IReadOnlyList<GanttRow> _rows = [];
    private long _dayStartUtc;
    private long _dayEndUtc;
    private long _systemStartUtc;
    private long _systemEndUtc;

    public void SetData(IReadOnlyList<GanttRow> rows, long dayStartUtc, long dayEndUtc,
        long systemStartUtc, long systemEndUtc)
    {
        _rows = rows;
        _dayStartUtc = dayStartUtc;
        _dayEndUtc = dayEndUtc;
        _systemStartUtc = systemStartUtc;
        _systemEndUtc = systemEndUtc;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext context)
    {
        base.OnRender(context);
        var width = ActualWidth;
        var height = ActualHeight;
        if (width < 240 || height < 80 || _dayEndUtc <= _dayStartUtc) return;

        var textBrush = Resource("MutedTextBrush", Color.FromRgb(0x5C, 0x5C, 0x5C));
        var grid = Resource("ChartGridBrush", Color.FromRgb(0xED, 0xED, 0xED));
        var body = Resource("TextBrush", Colors.Black);
        var stripeBase = ResourceColor("TextBrush", Colors.Black);
        var stripe = new SolidColorBrush(Color.FromArgb(0x0C, stripeBase.R, stripeBase.G, stripeBase.B));

        var plotLeft = LabelWidth;
        var plotWidth = Math.Max(10, width - LabelWidth - 8);
        var plotTop = 6;
        var plotBottom = height - AxisHeight;
        var maxRows = Math.Max(1, (int)((plotBottom - plotTop) / (RowHeight + RowGap)));
        var rowCount = Math.Min(_rows.Count, maxRows);
        var plotHeight = Math.Max(RowHeight, plotBottom - plotTop);
        var span = (double)(_dayEndUtc - _dayStartUtc);

        var gridPen = new Pen(grid, 1);
        const int tickCount = 4;
        for (var tick = 0; tick <= tickCount; tick++)
        {
            var fraction = tick / (double)tickCount;
            var x = plotLeft + fraction * plotWidth;
            context.DrawLine(gridPen, new Point(x, plotTop), new Point(x, plotBottom));
            var stamp = DateTimeOffset.FromUnixTimeSeconds((long)(_dayStartUtc + (_dayEndUtc - _dayStartUtc) * fraction))
                .ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture);
            var labelWidth = 40d;
            var left = tick == 0 ? x : tick == tickCount ? x - labelWidth : x - labelWidth / 2;
            DrawText(context, stamp, 12, textBrush, new Point(left, plotBottom + 6), labelWidth);
        }

        // Alternating row bands make it obvious which bar belongs to which label.
        // Baseline under the bars so the chart never looks cut off at the bottom.
        context.DrawLine(gridPen, new Point(plotLeft, plotBottom + 1), new Point(plotLeft + plotWidth, plotBottom + 1));
        context.DrawLine(gridPen, new Point(plotLeft, plotTop), new Point(plotLeft, plotBottom + 1));
        for (var index = 0; index < rowCount; index++)
        {
            if (index % 2 == 0) continue;
            var bandTop = plotTop + index * (RowHeight + RowGap) - 3;
            context.DrawRectangle(stripe, null, new Rect(0, bandTop, width, RowHeight + 6));
        }

        double y = plotTop;
        for (var index = 0; index < rowCount; index++)
        {
            var row = _rows[index];
            if (row.IsSystem)
            {
                var from = Math.Max(_systemStartUtc, _dayStartUtc);
                var to = Math.Min(_systemEndUtc, _dayEndUtc);
                DrawBar(context, plotLeft, plotWidth, span, from, to, y,
                    new SolidColorBrush(Color.FromArgb(0xB0, 0x8A, 0x8A, 0x8A)));
            }
            else
            {
                var color = row.Color is { } custom
                    ? Color.FromArgb(0xFF, (byte)(custom >> 16), (byte)(custom >> 8), (byte)custom)
                    : Palette[index % Palette.Length];
                var brush = new SolidColorBrush(color);
                foreach (var piece in row.Spans)
                {
                    var from = Math.Max(piece.StartUtc, _dayStartUtc);
                    var to = Math.Min(piece.EndUtc, _dayEndUtc);
                    DrawBar(context, plotLeft, plotWidth, span, from, to, y, brush);
                }
            }

            DrawText(context, row.IsSystem ? "系统运行" : row.Name, 12,
                row.IsSystem ? textBrush : body, new Point(0, y), LabelWidth - 8, true);
            y += RowHeight + RowGap;
            if (y > plotBottom) break;
        }

        if (_rows.Count == 0)
            DrawText(context, "今天还没有采集到数据", 12, textBrush, new Point(plotLeft, plotTop + 4), plotWidth);
    }

    private void DrawBar(DrawingContext context, double plotLeft, double plotWidth, double span,
        long from, long to, double y, Brush brush)
    {
        if (to <= from) return;
        var left = plotLeft + (from - _dayStartUtc) / span * plotWidth;
        var right = plotLeft + (to - _dayStartUtc) / span * plotWidth;
        var rect = new Rect(left, y + 2, Math.Max(1.5, right - left), RowHeight - 4);
        context.DrawRoundedRectangle(brush, null, rect, 2, 2);
    }

    private void DrawText(DrawingContext context, string value, double size, Brush brush, Point origin,
        double maxWidth, bool trim = false)
    {
        var text = new FormattedText(value, CultureInfo.CurrentUICulture, System.Windows.FlowDirection.LeftToRight,
            new Typeface(new FontFamily("Segoe UI Variable Text, Microsoft YaHei UI"), FontStyles.Normal,
                FontWeights.Normal, FontStretches.Normal), size, brush, VisualTreeHelper.GetDpi(this).PixelsPerDip)
        {
            MaxTextWidth = Math.Max(1, maxWidth),
            MaxTextHeight = size * 1.6,
            Trimming = trim ? TextTrimming.CharacterEllipsis : TextTrimming.None
        };
        context.DrawText(text, origin);
    }

    private Brush Resource(string key, Color fallback) => TryFindResource(key) as Brush ?? new SolidColorBrush(fallback);

    private Color ResourceColor(string key, Color fallback) => TryFindResource(key) is SolidColorBrush brush ? brush.Color : fallback;
}
