using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Seein.PetHost;

internal sealed class PetMenuThumbnail : Border
{
    public PetMenuThumbnail(string path)
    {
        Width = Height = 28;
        CornerRadius = new CornerRadius(4);
        Background = new SolidColorBrush(Color.FromArgb(15, 0, 0, 0));
        Clip = new RectangleGeometry(new Rect(0, 0, 28, 28), 4, 4);
        Loaded += (_, _) =>
        {
            if (Child is not null) return;
            try { Child = new PetImage(path) { Stretch = Stretch.UniformToFill, Width = 28, Height = 28 }; }
            catch (Exception ex) when (ex is not OutOfMemoryException) { ToolTip = "配图预览暂不可用。"; }
        };
        // Closed submenus release their decoders/timers; reopening creates a fresh animated preview.
        Unloaded += (_, _) => { if (Child is PetImage image) image.Dispose(); Child = null; };
    }
}
