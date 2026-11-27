using Seein.Core.Lighting;
using System.Windows;
using System.Windows.Input;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using System.Windows.Media;
using Point = System.Windows.Point;
using Size = System.Windows.Size;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using Pen = System.Windows.Media.Pen;

namespace Seein.Tray;

public sealed class RgbLedCanvas : FrameworkElement
{
    private const int CellPitch = 20;
    private const int LabelWidth = 96;
    private readonly List<(int Index, Rect Bounds)> _cells = [];
    private readonly List<(string Name, double Top)> _labels = [];
    private readonly HashSet<int> _selection = [];
    private IReadOnlyList<RgbZoneInfo> _zones = [];
    private IReadOnlyList<RgbColor> _colors = [];
    private int _keyboardIndex;

    public RgbLedCanvas()
    {
        Focusable = true;
        ClipToBounds = true;
        SnapsToDevicePixels = true;
    }

    public IReadOnlyList<int> Selection => _selection.ToArray();
    public event Action<IReadOnlyList<int>>? SelectionChanged;
    public event Action<IReadOnlyList<int>>? Painted;

    public void SetDevice(IReadOnlyList<RgbZoneInfo> zones, IReadOnlyList<RgbColor> colors)
    {
        _zones = zones;
        _colors = colors;
        _selection.Clear();
        _keyboardIndex = 0;
        InvalidateMeasure();
        InvalidateVisual();
        SelectionChanged?.Invoke([]);
    }

    public void SetColors(IReadOnlyList<RgbColor> colors)
    {
        _colors = colors;
        InvalidateVisual();
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var width = double.IsInfinity(availableSize.Width) ? 640 : availableSize.Width;
        return new Size(width, Math.Max(120, Layout(width, build: false)));
    }

    private double Layout(double width, bool build)
    {
        if (build) { _cells.Clear(); _labels.Clear(); }
        var top = 4d;
        var offset = 0;
        var columns = Math.Max(1, (int)((width - LabelWidth - 4) / CellPitch));
        foreach (var zone in _zones)
        {
            if (build) _labels.Add((zone.DisplayName is { Length: > 0 } name ? name : zone.Name, top));
            var map = zone.Matrix;
            var zoneColumns = map is null ? columns : zone.MatrixWidth;
            var slots = map?.Count ?? zone.LedCount;
            var scale = Math.Min(CellPitch, Math.Max(4, (width - LabelWidth - 4) / zoneColumns));
            if (build)
            {
                for (var slot = 0; slot < slots; slot++)
                {
                    var index = map?[slot] ?? (uint)slot;
                    if (index >= zone.LedCount || offset + index >= _colors.Count) continue;
                    var side = scale - Math.Min(4, scale / 3);
                    _cells.Add((offset + (int)index,
                        new Rect(LabelWidth + slot % zoneColumns * scale, top + slot / zoneColumns * scale, side, side)));
                }
            }
            top += Math.Max(1, Math.Ceiling(slots / (double)zoneColumns)) * scale + 10;
            offset += zone.LedCount;
        }
        return top;
    }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        Layout(ActualWidth, build: true);
        var labelBrush = TryFindResource("MutedTextBrush") as Brush ?? Brushes.Gray;
        var glyph = new Typeface("Segoe UI Variable Text, Microsoft YaHei UI");
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        foreach (var (name, top) in _labels)
            dc.DrawText(new FormattedText(name, System.Globalization.CultureInfo.CurrentUICulture,
                System.Windows.FlowDirection.LeftToRight, glyph, 12, labelBrush, dpi), new Point(4, top + 2));

        foreach (var (index, bounds) in _cells)
        {
            var color = _colors[index];
            var brush = new SolidColorBrush(Color.FromRgb(color.R, color.G, color.B));
            brush.Freeze();
            dc.DrawRoundedRectangle(brush, null, bounds, 3, 3);
            if (_selection.Contains(index) || (IsKeyboardFocused && index == _keyboardIndex))
            {
                var outline = TryFindResource("TextBrush") as Brush ?? Brushes.White;
                var separation = TryFindResource("PanelBrush") as Brush ?? Brushes.Black;
                var marked = bounds;
                marked.Inflate(2, 2);
                dc.DrawRoundedRectangle(null, new Pen(separation, 4), marked, 4, 4);
                dc.DrawRoundedRectangle(null, new Pen(outline, 1.5), marked, 4, 4);
            }
        }
        if (_zones.Count == 0)
            dc.DrawText(new FormattedText("选择带灯珠的设备", System.Globalization.CultureInfo.CurrentUICulture,
                System.Windows.FlowDirection.LeftToRight, glyph, 12, labelBrush, dpi), new Point(8, 10));
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        Focus();
        var hit = HitTest(e.GetPosition(this));
        if (hit < 0) return;
        if ((Keyboard.Modifiers & ModifierKeys.Control) == 0) _selection.Clear();
        _selection.Add(hit);
        _keyboardIndex = hit;
        CaptureMouse();
        NotifySelection();
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!IsMouseCaptured || e.LeftButton != MouseButtonState.Pressed) return;
        var hit = HitTest(e.GetPosition(this));
        if (hit >= 0 && _selection.Add(hit)) NotifySelection();
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (!IsMouseCaptured) return;
        ReleaseMouseCapture();
        Painted?.Invoke(Selection);
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new FrameworkElementAutomationPeer(this);

    protected override void OnGotKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        base.OnGotKeyboardFocus(e);
        NotifySelection();
    }

    protected override void OnLostKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        base.OnLostKeyboardFocus(e);
        InvalidateVisual();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (_colors.Count == 0) return;
        switch (e.Key)
        {
            case Key.Left: _keyboardIndex = Math.Max(0, _keyboardIndex - 1); break;
            case Key.Right: _keyboardIndex = Math.Min(_colors.Count - 1, _keyboardIndex + 1); break;
            case Key.Home: _keyboardIndex = 0; break;
            case Key.End: _keyboardIndex = _colors.Count - 1; break;
            case Key.Space:
                if (!_selection.Add(_keyboardIndex)) _selection.Remove(_keyboardIndex);
                break;
            case Key.Enter:
                Painted?.Invoke(_selection.Count > 0 ? Selection : [_keyboardIndex]);
                break;
            case Key.Escape: _selection.Clear(); break;
            default: return;
        }

        e.Handled = true;
        var cell = _cells.FirstOrDefault(item => item.Index == _keyboardIndex);
        if (cell.Bounds.Width > 0) BringIntoView(cell.Bounds);
        NotifySelection();
    }

    private void NotifySelection()
    {
        AutomationProperties.SetHelpText(this,
            $"灯珠 {_keyboardIndex + 1}，已选择 {_selection.Count} 颗。左右方向键移动，空格选中，回车上色。");
        SelectionChanged?.Invoke(Selection);
        InvalidateVisual();
    }

    private int HitTest(Point point)
    {
        foreach (var (index, bounds) in _cells)
            if (bounds.Contains(point)) return index;
        return -1;
    }
}
