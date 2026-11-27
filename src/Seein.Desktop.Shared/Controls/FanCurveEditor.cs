using Seein.Core.Thermal;
using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Input;
using System.Windows.Media;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using MouseButtonEventArgs = System.Windows.Input.MouseButtonEventArgs;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using Pen = System.Windows.Media.Pen;
using Point = System.Windows.Point;

namespace Seein.Tray;

public sealed class FanCurveEditor : FrameworkElement
{
    private readonly List<FanCurvePoint> _points = [];
    private int _selected;
    private int _dragging = -1;
    private bool _started;
    private double? _temperature;

    public FanCurveEditor() => Focusable = true;

    public GpuFanRange TemperatureRange { get; set; } = new(20, 100, 1);
    public GpuFanRange DutyRange { get; set; } = new(0, 100, 1);
    public bool IsReadOnly { get; set; }
    public bool FixedPointCount { get; set; }
    public IReadOnlyList<FanCurvePoint> ActivePoints { get; set; } = [];

    public double? Temperature
    {
        get => _temperature;
        set
        {
            if (_temperature == value) return;
            _temperature = value;
            InvalidateVisual();
        }
    }

    public IReadOnlyList<FanCurvePoint> Points
    {
        get => _points.ToArray();
        set
        {
            _points.Clear();
            _points.AddRange(value.OrderBy(point => point.TemperatureC));
            NotifySelection();
        }
    }

    public int SelectedIndex
    {
        get => _selected;
        set { _selected = value; NotifySelection(); }
    }

    public event EventHandler? EditStarted;
    public event EventHandler? CurveChanged;
    public event EventHandler? SelectionChanged;

    private Rect Plot => new(46, 14, Math.Max(10, ActualWidth - 82), Math.Max(10, ActualHeight - 47));

    protected override void OnRender(DrawingContext dc)
    {
        if (ActualWidth < 120 || ActualHeight < 85) return;
        var plot = Plot;
        var grid = BrushFor("ChartGridBrush", Brushes.Gray);
        var text = BrushFor("MutedTextBrush", Brushes.Gray);
        var accent = BrushFor("AccentBrush", Brushes.RoyalBlue);
        var surface = BrushFor("PanelBrush", Brushes.White);
        var passive = BrushFor("SubtleTextBrush", text);
        var gridPen = new Pen(grid, 1);

        dc.DrawRectangle(Brushes.Transparent, null, new Rect(RenderSize));
        if (DutyRange.Minimum > 0)
        {
            var bandTop = ToPoint(new(TemperatureRange.Minimum, DutyRange.Minimum), plot).Y;
            dc.DrawRectangle(BrushFor("WarningFillBrush", grid), null, new Rect(plot.Left, bandTop, plot.Width, plot.Bottom - bandTop));
        }
        for (var duty = 0; duty <= 100; duty += 25)
        {
            var y = ToPoint(new(TemperatureRange.Minimum, duty), plot).Y;
            dc.DrawLine(gridPen, new(plot.Left, y), new(plot.Right, y));
            DrawText(dc, $"{duty}%", text, new(3, y - 8));
        }
        var interval = (int)Math.Ceiling((TemperatureRange.Maximum - TemperatureRange.Minimum) / 40d) * 10;
        for (var temp = TemperatureRange.Minimum; temp <= TemperatureRange.Maximum; temp += Math.Max(10, interval))
        {
            var x = ToPoint(new(temp, 0), plot).X;
            dc.DrawLine(gridPen, new(x, plot.Top), new(x, plot.Bottom));
            DrawText(dc, $"{temp}", text, new(x - 9, plot.Bottom + 7));
        }
        DrawText(dc, "°C", text, new(plot.Right + 12, plot.Bottom + 7));
        if (Temperature is double current)
        {
            var x = ToPoint(new(current, 0), plot).X;
            dc.DrawLine(new Pen(passive, 1) { DashStyle = DashStyles.Dot }, new(x, plot.Top), new(x, plot.Bottom));
        }
        if (DutyRange.Minimum > 0)
            DrawText(dc, $"安全下限 {DutyRange.Minimum}%", text, new(plot.Left + 8, plot.Bottom - 17));

        var hasDraft = !_points.SequenceEqual(ActivePoints);
        DrawCurve(dc, ActivePoints, new Pen(hasDraft ? passive : accent, 2.3), plot);
        if (hasDraft) DrawCurve(dc, _points, new Pen(accent, 2.8) { DashStyle = DashStyles.Dash }, plot);

        if (_points.Count == 0)
            DrawText(dc, "暂无曲线数据", text, new(plot.Left + 12, plot.Top + plot.Height / 2 - 8));

        if (IsReadOnly) return;
        for (var index = 0; index < _points.Count; index++)
        {
            var center = ToPoint(_points[index], plot);
            var selected = _selected == index;
            if (selected) dc.DrawEllipse(null, new Pen(text, IsKeyboardFocused ? 2 : 1), center, 11, 11);
            dc.DrawEllipse(selected ? accent : surface, new Pen(accent, 2), center, 6, 6);
        }
    }

    private static Brush BrushFor(FrameworkElement element, string resource, Brush fallback) =>
        element.TryFindResource(resource) as Brush ?? fallback;

    private Brush BrushFor(string resource, Brush fallback) => BrushFor(this, resource, fallback);

    private Point ToPoint(FanCurvePoint point, Rect plot) => new(
        plot.Left + Math.Clamp((point.TemperatureC - TemperatureRange.Minimum) /
            Math.Max(1, TemperatureRange.Maximum - TemperatureRange.Minimum), 0, 1) * plot.Width,
        plot.Bottom - Math.Clamp(point.Percent, 0, 100) * plot.Height / 100);

    private void DrawCurve(DrawingContext dc, IReadOnlyList<FanCurvePoint> points, Pen pen, Rect plot)
    {
        for (var index = 1; index < points.Count; index++)
            dc.DrawLine(pen, ToPoint(points[index - 1], plot), ToPoint(points[index], plot));
    }

    private static double Snap(double value, GpuFanRange range)
    {
        var step = Math.Max(1, range.Step);
        var maximumStep = Math.Max(0, (range.Maximum - range.Minimum) / step);
        return range.Minimum + Math.Clamp(Math.Round((value - range.Minimum) / step), 0, maximumStep) * step;
    }

    private FanCurvePoint FromPosition(Point position)
    {
        var plot = Plot;
        var t = TemperatureRange.Minimum + (position.X - plot.Left) / plot.Width *
            (TemperatureRange.Maximum - TemperatureRange.Minimum);
        var duty = (plot.Bottom - position.Y) / plot.Height * 100;
        return new(Snap(t, TemperatureRange), Snap(duty, DutyRange));
    }

    private bool CanReplace(int index, FanCurvePoint point) =>
        TemperatureRange.Contains(point.TemperatureC) && DutyRange.Contains(point.Percent) &&
        (index == 0 || _points[index - 1].TemperatureC < point.TemperatureC) &&
        (index == _points.Count - 1 || point.TemperatureC < _points[index + 1].TemperatureC);

    public bool TryReplacePoint(double temperature, double duty)
    {
        if (IsReadOnly || _points.Count == 0) return false;
        var next = new FanCurvePoint(temperature, duty);
        if (!CanReplace(_selected, next)) return false;
        if (_points[_selected] == next) return true;
        EditStarted?.Invoke(this, EventArgs.Empty);
        _points[_selected] = next;
        NotifyChanged();
        return true;
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (IsReadOnly) return;
        var position = e.GetPosition(this);
        var index = HitPoint(position);
        if (index < 0 && (FixedPointCount || !Plot.Contains(position) || !Insert(FromPosition(position)))) return;
        if (index >= 0) SelectedIndex = index;
        Focus();
        _dragging = _selected;
        _started = index < 0;
        CaptureMouse();
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (IsReadOnly || _dragging < 0 || e.LeftButton != MouseButtonState.Pressed) return;
        var next = FromPosition(e.GetPosition(this));
        var current = _points[_dragging];
        if (!CanReplace(_dragging, next)) next = next with { TemperatureC = current.TemperatureC };
        if (next == current || !CanReplace(_dragging, next)) return;
        if (!_started)
        {
            EditStarted?.Invoke(this, EventArgs.Empty);
            _started = true;
        }
        _points[_dragging] = next;
        NotifyChanged();
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (_dragging < 0) return;
        _dragging = -1;
        ReleaseMouseCapture();
        e.Handled = true;
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        _dragging = -1;
    }

    protected override void OnMouseRightButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseRightButtonDown(e);
        if (IsReadOnly || FixedPointCount || HitPoint(e.GetPosition(this)) is not (>= 0 and var index)) return;
        e.Handled = Delete(index);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (IsReadOnly || _points.Count == 0) return;
        var current = _points[_selected];
        var direction = e.Key is Key.Up or Key.Right ? 1 : -1;
        switch (e.Key)
        {
            case Key.Left or Key.Right when Keyboard.Modifiers.HasFlag(ModifierKeys.Control):
                SelectedIndex += direction;
                break;
            case Key.Home or Key.End:
                SelectedIndex = e.Key == Key.Home ? 0 : _points.Count - 1;
                break;
            case Key.Left or Key.Right:
                TryReplacePoint(Snap(current.TemperatureC + direction * TemperatureRange.Step, TemperatureRange),
                    Snap(current.Percent, DutyRange));
                break;
            case Key.Up or Key.Down:
                TryReplacePoint(current.TemperatureC, Snap(current.Percent + direction * DutyRange.Step, DutyRange));
                break;
            case Key.Insert when !FixedPointCount:
                var temperature = Enumerable.Range(TemperatureRange.Minimum, TemperatureRange.Maximum - TemperatureRange.Minimum + 1)
                    .Where(t => _points.All(point => point.TemperatureC != t))
                    .MinBy(t => Math.Abs(current.TemperatureC - t));
                Insert(new(temperature, Snap(current.Percent, DutyRange)));
                break;
            case Key.Delete when !FixedPointCount:
                Delete(_selected);
                break;
            default:
                return;
        }
        e.Handled = true;
    }

    private int HitPoint(Point position) => _points.FindIndex(point => (ToPoint(point, Plot) - position).Length <= 12);

    private bool Insert(FanCurvePoint point)
    {
        if (_points.Count >= 24 || _points.Any(existing => existing.TemperatureC == point.TemperatureC)) return false;
        EditStarted?.Invoke(this, EventArgs.Empty);
        var index = _points.FindIndex(existing => existing.TemperatureC > point.TemperatureC);
        if (index < 0) index = _points.Count;
        _points.Insert(index, point);
        _selected = index;
        NotifyChanged();
        return true;
    }

    private bool Delete(int index)
    {
        if (_points.Count <= 2) return false;
        EditStarted?.Invoke(this, EventArgs.Empty);
        _points.RemoveAt(index);
        _selected = index;
        NotifyChanged();
        return true;
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new FrameworkElementAutomationPeer(this);
    protected override void OnGotKeyboardFocus(KeyboardFocusChangedEventArgs e) { base.OnGotKeyboardFocus(e); InvalidateVisual(); }
    protected override void OnLostKeyboardFocus(KeyboardFocusChangedEventArgs e) { base.OnLostKeyboardFocus(e); InvalidateVisual(); }

    private void NotifyChanged()
    {
        NotifySelection();
        CurveChanged?.Invoke(this, EventArgs.Empty);
    }

    private void NotifySelection()
    {
        _selected = Math.Clamp(_selected, 0, Math.Max(0, _points.Count - 1));
        if (_points.ElementAtOrDefault(_selected) is { } point)
            AutomationProperties.SetHelpText(this, $"第 {_selected + 1} 点，{point.TemperatureC:0} 摄氏度，{point.Percent:0}%。");
        else AutomationProperties.SetHelpText(this, "暂无曲线数据");
        SelectionChanged?.Invoke(this, EventArgs.Empty);
        InvalidateVisual();
    }

    private void DrawText(DrawingContext dc, string text, Brush brush, Point point) =>
        dc.DrawText(new FormattedText(text, CultureInfo.CurrentCulture, System.Windows.FlowDirection.LeftToRight,
            new Typeface("Segoe UI Variable Text, Microsoft YaHei UI"), 11, brush, VisualTreeHelper.GetDpi(this).PixelsPerDip), point);
}
