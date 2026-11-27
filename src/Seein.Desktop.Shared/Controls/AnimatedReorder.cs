using Seein.Core.Configuration;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ButtonBase = System.Windows.Controls.Primitives.ButtonBase;
using Panel = System.Windows.Controls.Panel;
using Point = System.Windows.Point;
using Size = System.Windows.Size;

namespace Seein.Tray;

/// <summary>One-second hold, lifted snapshot, animated slots, then one layout/persistence commit.</summary>
public sealed class AnimatedReorder
{
    private static readonly DependencyProperty ControllerProperty = DependencyProperty.RegisterAttached(
        "Controller", typeof(AnimatedReorder), typeof(AnimatedReorder));
    private readonly Panel _panel;
    private string _scope;
    private readonly DispatcherTimer _hold = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly DispatcherTimer _scroll = new() { Interval = TimeSpan.FromMilliseconds(40) };
    private Func<bool>? _enabled;
    private Action? _saved;
    private FrameworkElement? _candidate;
    private List<FrameworkElement> _order = [];
    private readonly Dictionary<FrameworkElement, Point> _origins = [];
    private readonly Dictionary<FrameworkElement, Transform> _transforms = [];
    private Point _press, _offset;
    private LiftAdorner? _lift;
    private AdornerLayer? _layer;
    private Window? _window;
    private bool _dragging, _settling, _releasing;
    private double _opacity;
    private static TimeSpan AnimationTime => SystemParameters.ClientAreaAnimation ? TimeSpan.FromMilliseconds(180) : TimeSpan.Zero;

    public static void Attach(Panel panel, string scope, Func<bool>? enabled = null, Action? saved = null)
    {
        if (panel.GetValue(ControllerProperty) is not AnimatedReorder controller)
        {
            controller = new(panel, scope);
            panel.SetValue(ControllerProperty, controller);
        }
        controller.Cancel();
        controller._scope = scope;
        controller._enabled = enabled;
        controller._saved = saved;
        controller.RestoreOrder();
    }

    // Installed once by the shell. Only page-selector strips qualify; action button rows do not.
    public static void RegisterPageStrips()
    {
        EventManager.RegisterClassHandler(typeof(Panel), FrameworkElement.LoadedEvent,
            new RoutedEventHandler((sender, e) =>
            {
                if (sender is not Panel panel || e.OriginalSource != panel || panel.GetValue(ControllerProperty) is not null) return;
                var items = panel.Children.OfType<FrameworkElement>().ToArray();
                if (items.Length < 2 || items.Any(i => i is not ButtonBase)) return;
                var owner = Ancestor<System.Windows.Controls.UserControl>(panel);
                if (owner is null) return;
                var namedSelector = panel.Name is "Sections" or "CategoryTabs" or "PetSettingsTabs";
                var segmented = panel.Parent is Border border && border.Style == panel.TryFindResource("SegmentedHostStyle");
                if (!namedSelector && !segmented) return;
                Attach(panel, owner.GetType().FullName + ":" + (panel.Name.Length != 0 ? panel.Name : string.Join("|", items.Select(KeyOf))));
            }));
    }

    private AnimatedReorder(Panel panel, string scope)
    {
        _panel = panel; _scope = scope;
        panel.AddHandler(UIElement.PreviewMouseLeftButtonDownEvent, new MouseButtonEventHandler(Pressed), true);
        panel.AddHandler(UIElement.PreviewMouseMoveEvent, new System.Windows.Input.MouseEventHandler(Moved), true);
        panel.AddHandler(UIElement.PreviewMouseLeftButtonUpEvent, new MouseButtonEventHandler(Released), true);
        panel.PreviewKeyDown += KeyDown;
        panel.LostMouseCapture += (_, e) => { if (e.OriginalSource == panel && !_releasing && _dragging) Cancel(); };
        panel.Unloaded += (_, _) => Cancel();
        _hold.Tick += (_, _) => Lift();
        _scroll.Tick += (_, _) => ScrollAndMove();
    }

    private static string KeyOf(FrameworkElement item) => item.Uid.Length != 0 ? item.Uid : item.Tag is string tag && tag != "selected" ? tag :
        item.Name.Length != 0 ? item.Name : (item as ContentControl)?.Content?.ToString() ?? "";
    private void RestoreOrder()
    {
        var items = _panel.Children.OfType<FrameworkElement>().ToArray();
        if (items.Any(i => KeyOf(i).Length == 0) || items.Select(KeyOf).Distinct().Count() != items.Length) return;
        var keys = NavigationOrder.Arrange(items.Select(KeyOf), DesktopThemeManager.GetTabOrder(_scope), items.Select(KeyOf));
        var ordered = keys.Select(key => items.Single(i => KeyOf(i) == key)).ToArray();
        if (items.SequenceEqual(ordered)) return;
        _panel.Children.Clear(); foreach (var item in ordered) _panel.Children.Add(item);
    }
    private void Pressed(object sender, MouseButtonEventArgs e)
    {
        if (_settling || _enabled?.Invoke() == false || e.ClickCount != 1) return;
        _hold.Stop();
        _candidate = _panel.Children.OfType<FrameworkElement>().FirstOrDefault(i => i.IsMouseOver && i.IsEnabled && i.Visibility == Visibility.Visible);
        if (_candidate is null) return;
        _press = e.GetPosition(_panel); _hold.Start();
    }
    private void Moved(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (_candidate is null || _settling) return;
        if (!_dragging)
        {
            if ((e.GetPosition(_panel) - _press).Length > 7 || e.LeftButton != MouseButtonState.Pressed) Cancel();
            return;
        }
        Move(e.GetPosition(_panel)); e.Handled = true;
    }
    private void Lift()
    {
        _hold.Stop();
        if (_candidate is null || !_candidate.IsMouseOver || Window.GetWindow(_panel)?.IsActive != true || Mouse.LeftButton != MouseButtonState.Pressed || _enabled?.Invoke() == false) { Cancel(); return; }
        _layer = AdornerLayer.GetAdornerLayer(_panel);
        if (_layer is null) { Cancel(); return; }
        _order = _panel.Children.OfType<FrameworkElement>().ToList();
        foreach (var item in _order)
        {
            _origins[item] = item.TranslatePoint(new Point(), _panel);
            _transforms[item] = item.RenderTransform;
        }
        var dpi = VisualTreeHelper.GetDpi(_candidate);
        var bitmap = new RenderTargetBitmap(Math.Max(1, (int)Math.Ceiling(_candidate.ActualWidth * dpi.DpiScaleX)),
            Math.Max(1, (int)Math.Ceiling(_candidate.ActualHeight * dpi.DpiScaleY)), dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        var visual = new DrawingVisual();
        using (var drawing = visual.RenderOpen()) drawing.DrawRectangle(new VisualBrush(_candidate), null, new Rect(0, 0, _candidate.ActualWidth, _candidate.ActualHeight));
        bitmap.Render(visual); bitmap.Freeze();
        _offset = (Point)(_press - _origins[_candidate]);
        _lift = new(_panel, bitmap, _candidate.RenderSize);
        _layer.Add(_lift); _lift.Position(_origins[_candidate], false);
        _opacity = _candidate.Opacity; _candidate.Opacity = 0;
        _dragging = true;
        if (!Mouse.Capture(_panel, CaptureMode.SubTree)) { Cancel(); return; }
        _panel.Cursor = System.Windows.Input.Cursors.SizeAll;
        _window = Window.GetWindow(_panel);
        if (_window is not null) { _window.Deactivated += Deactivated; _window.PreviewKeyDown += WindowKeyDown; }
        _scroll.Start();
    }
    private void Move(Point pointer)
    {
        if (_candidate is null || _lift is null) return;
        var position = pointer - (Vector)_offset;
        _lift.Position(position, false);
        var center = position + new Vector(_candidate.ActualWidth / 2, _candidate.ActualHeight / 2);
        var slots = Positions();
        var nearest = _order.OrderBy(i => (slots[i] + new Vector(i.ActualWidth / 2, i.ActualHeight / 2) - center).LengthSquared).First();
        var from = _order.IndexOf(_candidate); var to = _order.IndexOf(nearest);
        if (from == to) return;
        _order.RemoveAt(from); _order.Insert(to, _candidate);
        slots = Positions();
        foreach (var item in _order.Where(i => i != _candidate))
        {
            if (item.RenderTransform is not TranslateTransform transform) item.RenderTransform = transform = new TranslateTransform();
            Animate(transform, TranslateTransform.XProperty, slots[item].X - _origins[item].X);
            Animate(transform, TranslateTransform.YProperty, slots[item].Y - _origins[item].Y);
        }
    }
    private Dictionary<FrameworkElement, Point> Positions()
    {
        var result = new Dictionary<FrameworkElement, Point>();
        var vertical = _panel is StackPanel { Orientation: System.Windows.Controls.Orientation.Vertical };
        double x = 0, y = 0, line = 0;
        foreach (var item in _order)
        {
            var size = item.DesiredSize;
            if (!vertical && _panel is WrapPanel && x > 0 && x + size.Width > _panel.ActualWidth + .5) { x = 0; y += line; line = 0; }
            result[item] = new Point(vertical ? _origins[item].X : x + item.Margin.Left, y + item.Margin.Top);
            if (vertical) y += size.Height;
            else { x += size.Width; line = Math.Max(line, size.Height); }
        }
        return result;
    }
    private void Released(object sender, MouseButtonEventArgs e)
    {
        if (!_dragging) { _hold.Stop(); _candidate = null; return; }
        e.Handled = true;
        if (_settling) return;
        _settling = true; _scroll.Stop();
        var target = Positions()[_candidate!];
        _lift!.Position(target, true);
        var timer = new DispatcherTimer { Interval = AnimationTime + TimeSpan.FromMilliseconds(1) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            if (!_settling) return;
            var order = _order.ToArray(); CleanUp();
            _panel.Children.Clear(); foreach (var item in order) _panel.Children.Add(item);
            Save();
        };
        timer.Start();
    }
    private void Save()
    {
        try
        {
            DesktopThemeManager.SetTabOrder(_scope, NavigationOrder.Remember(DesktopThemeManager.GetTabOrder(_scope), _panel.Children.OfType<FrameworkElement>().Select(KeyOf)));
            _saved?.Invoke();
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException) { System.Diagnostics.Trace.WriteLine("Reorder save failed: " + ex.Message); }
    }
    private void KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key == Key.Escape && _dragging) { Cancel(); e.Handled = true; return; }
        if (_enabled?.Invoke() == false || _dragging || Keyboard.Modifiers != ModifierKeys.Alt || key is not (Key.Up or Key.Down or Key.Left or Key.Right)) return;
        var item = _panel.Children.OfType<FrameworkElement>().FirstOrDefault(i => i.IsKeyboardFocusWithin);
        if (item is null) return;
        var from = _panel.Children.IndexOf(item); var to = from + (key is Key.Up or Key.Left ? -1 : 1);
        if (to >= 0 && to < _panel.Children.Count) { _panel.Children.Remove(item); _panel.Children.Insert(to, item); item.Focus(); Save(); }
        e.Handled = true;
    }
    private void ScrollAndMove()
    {
        if (!_dragging || _settling) return;
        var scroll = Ancestor<ScrollViewer>(_panel);
        if (scroll is not null)
        {
            var point = Mouse.GetPosition(scroll);
            if (point.Y < 24) scroll.ScrollToVerticalOffset(scroll.VerticalOffset - 10);
            else if (point.Y > scroll.ActualHeight - 24) scroll.ScrollToVerticalOffset(scroll.VerticalOffset + 10);
        }
        Move(Mouse.GetPosition(_panel));
    }
    private static T? Ancestor<T>(DependencyObject item) where T : DependencyObject
    {
        while ((item = VisualTreeHelper.GetParent(item)) is not null) if (item is T match) return match;
        return null;
    }
    private void WindowKeyDown(object sender, System.Windows.Input.KeyEventArgs e) { if (e.Key == Key.Escape && _dragging) { Cancel(); e.Handled = true; } }
    private void Deactivated(object? sender, EventArgs e) => Cancel();
    public void Cancel() { _hold.Stop(); CleanUp(); }
    private void CleanUp()
    {
        _scroll.Stop(); _dragging = false; _settling = false;
        _releasing = true; if (_panel.IsMouseCaptured) _panel.ReleaseMouseCapture(); _releasing = false;
        if (_lift is not null) _layer?.Remove(_lift);
        foreach (var pair in _transforms) { if (pair.Key.RenderTransform is TranslateTransform move) { move.BeginAnimation(TranslateTransform.XProperty, null); move.BeginAnimation(TranslateTransform.YProperty, null); } pair.Key.RenderTransform = pair.Value; }
        _transforms.Clear(); _origins.Clear(); _lift = null; _layer = null;
        if (_candidate is not null && _order.Count != 0) _candidate.Opacity = _opacity;
        _candidate = null; _order.Clear(); _panel.ClearValue(FrameworkElement.CursorProperty);
        if (_window is not null) { _window.Deactivated -= Deactivated; _window.PreviewKeyDown -= WindowKeyDown; _window = null; }
    }
    private static void Animate(Animatable target, DependencyProperty property, double value) => target.BeginAnimation(property,
        new DoubleAnimation(value, new Duration(AnimationTime)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });

    private sealed class LiftAdorner : Adorner
    {
        private readonly Border _visual;
        private readonly TranslateTransform _move = new();
        public LiftAdorner(UIElement owner, BitmapSource snapshot, Size size) : base(owner)
        {
            IsHitTestVisible = false;
            _visual = new Border { Width = size.Width, Height = size.Height, CornerRadius = new(6),
                Background = (System.Windows.Media.Brush?)(owner as FrameworkElement)?.TryFindResource("PanelBrush"),
                Child = new System.Windows.Controls.Image { Source = snapshot, Stretch = Stretch.Fill }, RenderTransform = _move,
                Effect = new DropShadowEffect { BlurRadius = 18, ShadowDepth = 5, Opacity = .28 } };
            var scale = new ScaleTransform(.98, .98);
            _visual.RenderTransformOrigin = new Point(.5, .5);
            var transforms = new TransformGroup(); transforms.Children.Add(scale); transforms.Children.Add(_move); _visual.RenderTransform = transforms;
            Animate(scale, ScaleTransform.ScaleXProperty, 1.025); Animate(scale, ScaleTransform.ScaleYProperty, 1.025);
            AddVisualChild(_visual);
        }
        public void Position(Point point, bool animate)
        {
            if (animate) { Animate(_move, TranslateTransform.XProperty, point.X); Animate(_move, TranslateTransform.YProperty, point.Y); }
            else { _move.BeginAnimation(TranslateTransform.XProperty, null); _move.BeginAnimation(TranslateTransform.YProperty, null); _move.X = point.X; _move.Y = point.Y; }
        }
        protected override int VisualChildrenCount => 1;
        protected override Visual GetVisualChild(int index) => _visual;
        protected override Size MeasureOverride(Size constraint) { _visual.Measure(new Size(_visual.Width, _visual.Height)); return AdornedElement.RenderSize; }
        protected override Size ArrangeOverride(Size finalSize) { _visual.Arrange(new Rect(0, 0, _visual.Width, _visual.Height)); return finalSize; }
    }
}
