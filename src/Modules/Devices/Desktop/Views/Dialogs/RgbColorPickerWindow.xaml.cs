using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Seein.Core.Lighting;
using Button = System.Windows.Controls.Button;
using Color = System.Windows.Media.Color;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;

namespace Seein.Tray;

public partial class RgbColorPickerWindow : Window
{
    private readonly List<(Button Button, Border Marker, RgbColor Color)> _swatches = [];
    private readonly IReadOnlyDictionary<RgbColor, RgbColor> _caseAppearance;
    private bool _updating = true;
    private double _saturation;
    private double _value;

    public RgbColor SelectedColor { get; private set; }

    public RgbColorPickerWindow(RgbColor initial, IReadOnlyDictionary<RgbColor, RgbColor>? caseAppearance = null)
    {
        InitializeComponent();
        _caseAppearance = caseAppearance ?? new Dictionary<RgbColor, RgbColor>();
        BasicColorHint.Visibility = _caseAppearance.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        SourceInitialized += (_, _) => WindowBackdrop.TryApply(this, DesktopThemeManager.IsDark);
        foreach (var color in RgbBasicPalette.Colors)
        {
            var sampled = _caseAppearance.TryGetValue(color, out var appearance);
            var swatch = new Border { Background = Brush(sampled ? appearance : color),
                Width = 22, Height = 22, CornerRadius = new(4) };
            var hint = sampled ? $"机箱实拍近似 {appearance.ToHex()}" : "机箱颜色暂无实拍";
            var ring = new Border { Child = swatch, BorderThickness = new(2), CornerRadius = new(6), Padding = new(1) };
            var button = new Button { Content = ring, Padding = new(1), Margin = new(2), MinWidth = 0,
                MinHeight = 0, ToolTip = hint };
            AutomationProperties.SetName(button, $"色块 {BasicColors.Children.Count + 1} · {hint}");
            button.Click += (_, _) => SelectColor(color, updateHex: true);
            _swatches.Add((button, ring, color));
            BasicColors.Children.Add(button);
        }
        SelectColor(initial, updateHex: true);
        Loaded += (_, _) => { HexValue.Focus(); HexValue.SelectAll(); };
    }

    private void SelectColor(RgbColor color, bool updateHex)
    {
        _updating = true;
        var (hue, saturation, value) = RgbControlView.RgbToHsv(color);
        HueSlider.Value = hue;
        _saturation = saturation;
        _value = value;
        UpdateSelection(color, updateHex);
        _updating = false;
    }

    private void UpdateSelection(RgbColor color, bool updateHex)
    {
        SelectedColor = color;
        if (updateHex) HexValue.Text = color.ToHex();
        Confirm.IsEnabled = true;
        var sampled = _caseAppearance.TryGetValue(color, out var appearance);
        HexHint.Text = sampled ? "机箱实拍近似" : "机箱颜色暂无实拍";
        Preview.Background = Brush(sampled ? appearance : color);
        HueSurface.Background = Brush(RgbControlView.HsvToRgb(HueSlider.Value, 1, 1));
        foreach (var (button, marker, swatch) in _swatches)
        {
            var selected = swatch == color;
            if (selected) marker.SetResourceReference(Border.BorderBrushProperty, "AccentBrush");
            else marker.ClearValue(Border.BorderBrushProperty);
            AutomationProperties.SetItemStatus(button, selected ? "已选中" : "");
        }
        UpdateMarker();
    }

    private void HexChanged(object sender, TextChangedEventArgs e)
    {
        if (_updating) return;
        if (RgbGroupEffectView.TryReadColor(HexValue.Text, out var color))
            SelectColor(color, updateHex: false);
        else
        {
            Confirm.IsEnabled = false;
            HexHint.Text = "请输入 6 位 HEX 色号";
        }
    }

    private void HueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_updating) UpdateFromSpectrum();
    }

    private void SpectrumDown(object sender, MouseButtonEventArgs e)
    {
        Spectrum.CaptureMouse();
        PickSpectrum(e);
    }

    private void SpectrumMove(object sender, MouseEventArgs e)
    {
        if (Spectrum.IsMouseCaptured && e.LeftButton == MouseButtonState.Pressed) PickSpectrum(e);
    }

    private void SpectrumUp(object sender, MouseButtonEventArgs e)
    {
        if (!Spectrum.IsMouseCaptured) return;
        PickSpectrum(e);
        Spectrum.ReleaseMouseCapture();
    }

    private void PickSpectrum(MouseEventArgs e)
    {
        var position = e.GetPosition(Spectrum);
        _saturation = Math.Clamp(position.X / Spectrum.ActualWidth, 0, 1);
        _value = 1 - Math.Clamp(position.Y / Spectrum.ActualHeight, 0, 1);
        UpdateFromSpectrum();
    }

    private void UpdateFromSpectrum()
    {
        _updating = true;
        UpdateSelection(RgbControlView.HsvToRgb(HueSlider.Value, _saturation, _value), updateHex: true);
        _updating = false;
    }

    private void SpectrumSizeChanged(object sender, SizeChangedEventArgs e) => UpdateMarker();

    private void UpdateMarker()
    {
        Canvas.SetLeft(SpectrumMarker, Math.Clamp(_saturation * Spectrum.ActualWidth - 7, 0, Math.Max(0, Spectrum.ActualWidth - 14)));
        Canvas.SetTop(SpectrumMarker, Math.Clamp((1 - _value) * Spectrum.ActualHeight - 7, 0, Math.Max(0, Spectrum.ActualHeight - 14)));
    }

    private void ConfirmClick(object sender, RoutedEventArgs e) => DialogResult = true;

    private static SolidColorBrush Brush(RgbColor color) => new(Color.FromRgb(color.R, color.G, color.B));
}
