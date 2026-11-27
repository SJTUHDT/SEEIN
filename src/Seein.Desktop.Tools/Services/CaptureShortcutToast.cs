using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;

namespace Seein.Tray;

// A short, non-activating acknowledgement: never takes keyboard or mouse focus.
internal static class CaptureShortcutToast
{
    private static Window? _current;
    internal static Window Create(string message)
    {
        var window = new Window
        {
            Width = 380, Height = 78, WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize, AllowsTransparency = true,
            Background = Brushes.Transparent, Topmost = true, ShowInTaskbar = false,
            ShowActivated = false, Focusable = false, IsHitTestVisible = false,
            Content = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(32, 32, 32)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(80, 80, 80)),
                BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(10), Padding = new Thickness(20, 14, 20, 14),
                Child = new TextBlock { Text = message, Foreground = Brushes.White, FontSize = 14,
                    TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center }
            }
        };
        window.SourceInitialized += (_, _) =>
        {
            var handle = new WindowInteropHelper(window).Handle;
            SetWindowLongPtrW(handle, -20, GetWindowLongPtrW(handle, -20) | 0x080000A0); // NOACTIVATE | TOOLWINDOW | TRANSPARENT
            var source = HwndSource.FromHwnd(handle);
            source?.AddHook((nint h, int m, nint w, nint l, ref bool handled) =>
            { if (m == 0x21) { handled = true; return 3; } return 0; });
        };
        return window;
    }
    public static void Show(string message)
    {
        if (System.Windows.Application.Current is null) return;
        _current?.Close();
        var window = Create(message); _current = window;
        var area = SystemParameters.WorkArea;
        window.Left = area.Right - window.Width - 24; window.Top = area.Bottom - window.Height - 24;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        timer.Tick += (_, _) => window.Close();
        window.Closed += (_, _) => { timer.Stop(); if (_current == window) _current = null; };
        window.Show(); timer.Start();
    }
    [DllImport("user32.dll")] private static extern nint GetWindowLongPtrW(nint window, int index);
    [DllImport("user32.dll")] private static extern nint SetWindowLongPtrW(nint window, int index, nint value);
}
