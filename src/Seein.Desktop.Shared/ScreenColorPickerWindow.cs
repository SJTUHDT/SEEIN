using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Brushes = System.Windows.Media.Brushes;
using Cursors = System.Windows.Input.Cursors;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using VerticalAlignment = System.Windows.VerticalAlignment;
using Color = System.Windows.Media.Color;
using Point = System.Windows.Point;

namespace Seein.Tray;

/// <summary>
/// Full screen eyedropper: the cursor becomes a crosshair, a click anywhere picks that pixel.
/// The window itself is transparent so it never tints the colour being sampled.
/// </summary>
public sealed class ScreenColorPickerWindow : Window
{
    public ScreenColorPickerWindow()
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = new SolidColorBrush(Color.FromArgb(1, 0, 0, 0));
        ShowInTaskbar = false;
        Topmost = true;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Cursor = Cursors.Cross;
        Left = SystemParameters.VirtualScreenLeft;
        Top = SystemParameters.VirtualScreenTop;
        Width = SystemParameters.VirtualScreenWidth;
        Height = SystemParameters.VirtualScreenHeight;

        var hint = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0xCC, 0x1F, 0x1F, 0x1F)),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(14, 8, 14, 8),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            VerticalAlignment = System.Windows.VerticalAlignment.Top,
            Margin = new Thickness(0, 40, 0, 0),
            Child = new TextBlock
            {
                Text = "移动十字光标到目标颜色，单击取色 · Esc 取消",
                Foreground = Brushes.White,
                FontSize = 13
            },
            IsHitTestVisible = false
        };
        Content = hint;

        MouseLeftButtonDown += (_, _) => Capture();
        MouseRightButtonDown += (_, e) => { e.Handled = true; Cancel(); };
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) Cancel();
        };
        Loaded += (_, _) => Focus();
    }

    public Color? Picked { get; private set; }

    /// <summary>Shows the picker modally and returns the sampled colour, or null when cancelled.</summary>
    public static Color? Pick(Window? owner)
    {
        var window = new ScreenColorPickerWindow();
        try
        {
            if (owner is not null && owner.IsLoaded && !ReferenceEquals(owner, window)) window.Owner = owner;
        }
        catch { }
        return window.ShowDialog() == true ? window.Picked : null;
    }

    private void Capture()
    {
        if (!GetCursorPos(out var point)) { Cancel(); return; }
        try
        {
            using var bitmap = new System.Drawing.Bitmap(1, 1);
            using (var graphics = System.Drawing.Graphics.FromImage(bitmap))
                graphics.CopyFromScreen(point.X, point.Y, 0, 0, new System.Drawing.Size(1, 1));
            var pixel = bitmap.GetPixel(0, 0);
            Picked = Color.FromRgb(pixel.R, pixel.G, pixel.B);
            DialogResult = true;
        }
        catch
        {
            DialogResult = false;
        }
        Close();
    }

    private void Cancel()
    {
        DialogResult = false;
        Close();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point32
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out Point32 point);
}
