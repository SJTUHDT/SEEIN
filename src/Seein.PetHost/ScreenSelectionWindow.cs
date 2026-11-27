using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using Point = System.Windows.Point;

namespace Seein.PetHost;

internal sealed class ScreenSelectionWindow : Window
{
    private readonly Canvas _canvas = new();
    private readonly System.Windows.Shapes.Rectangle _selection = new() { Stroke = System.Windows.Media.Brushes.DodgerBlue, StrokeThickness = 2, Fill = new SolidColorBrush(Color.FromArgb(20, 255, 255, 255)) };
    private readonly TaskCompletionSource<byte[]?> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Point? _start;
    private bool _captured;
    private readonly System.Drawing.Rectangle _bounds;

    private ScreenSelectionWindow(CancellationToken token)
    {
        _bounds = System.Windows.Forms.Screen.FromPoint(System.Windows.Forms.Cursor.Position).Bounds;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true;
        Background = new SolidColorBrush(Color.FromArgb(55, 0, 0, 0));
        Topmost = true;
        ShowInTaskbar = false;
        Cursor = System.Windows.Input.Cursors.Cross;
        Content = _canvas;
        _canvas.Children.Add(_selection);
        SourceInitialized += (_, _) => SetWindowPos(new WindowInteropHelper(this).Handle, 0, _bounds.Left, _bounds.Top, _bounds.Width, _bounds.Height, 0x4);
        MouseLeftButtonDown += (_, e) => { _start = e.GetPosition(_canvas); CaptureMouse(); };
        MouseMove += (_, e) =>
        {
            if (_start is not { } start) return;
            var end = e.GetPosition(_canvas);
            Canvas.SetLeft(_selection, Math.Min(start.X, end.X));
            Canvas.SetTop(_selection, Math.Min(start.Y, end.Y));
            _selection.Width = Math.Abs(end.X - start.X);
            _selection.Height = Math.Abs(end.Y - start.Y);
        };
        MouseLeftButtonUp += async (_, e) =>
        {
            if (_start is not { } start) return;
            var first = PointToScreen(start);
            var second = PointToScreen(e.GetPosition(_canvas));
            var width = (int)Math.Abs(second.X - first.X);
            var height = (int)Math.Abs(second.Y - first.Y);
            if (width < 4 || height < 4) { Close(); return; }
            ReleaseMouseCapture();
            _captured = true;
            Hide();
            try
            {
                await Task.Delay(150, token);
                using var image = new System.Drawing.Bitmap(width, height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                using (var graphics = System.Drawing.Graphics.FromImage(image)) graphics.CopyFromScreen((int)Math.Min(first.X, second.X), (int)Math.Min(first.Y, second.Y), 0, 0, image.Size);
                using var output = new MemoryStream();
                image.Save(output, System.Drawing.Imaging.ImageFormat.Png);
                _completion.TrySetResult(output.ToArray());
            }
            catch (Exception ex) when (ex is not OutOfMemoryException) { _completion.TrySetException(ex); }
            finally { Close(); }
        };
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };
        Closed += (_, _) => { if (!_captured) _completion.TrySetResult(null); };
    }

    public static async Task<byte[]?> CaptureAsync(CancellationToken token)
    {
        var window = new ScreenSelectionWindow(token);
        using var registration = token.Register(() => window.Dispatcher.BeginInvoke(window.Close));
        window.Show();
        window.Activate();
        return await window._completion.Task.WaitAsync(token);
    }

    [DllImport("user32", SetLastError = true)]
    private static extern bool SetWindowPos(nint hwnd, nint after, int x, int y, int width, int height, uint flags);
}
