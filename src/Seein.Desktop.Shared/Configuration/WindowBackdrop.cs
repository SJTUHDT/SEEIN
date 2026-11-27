using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace Seein.Tray;

/// <summary>
/// Applies the Windows 11 window treatment: rounded corners, immersive dark title bar and (when the
/// OS build supports it) a Mica backdrop.  Every call is best effort: on Windows 10 the attributes
/// simply fail and the window keeps its solid Fluent background.
/// </summary>
public static class WindowBackdrop
{
    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int DwmwaWindowCornerPreference = 33;
    private const int DwmwaSystemBackdropType = 38;
    private const int DwmcpRound = 2;
    private const int DwmsbtMainWindow = 2;

    public static bool TryApply(Window window, bool dark)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero) return false;

        TrySet(handle, DwmwaWindowCornerPreference, DwmcpRound);
        TrySet(handle, DwmwaUseImmersiveDarkMode, dark ? 1 : 0);

        if (Environment.OSVersion.Version.Build < 22621) return false;
        if (SystemParameters.HighContrast || Environment.GetEnvironmentVariable("SEEIN_ENABLE_MICA") is not { Length: > 0 })
        {
            TrySet(handle, DwmwaSystemBackdropType, 1); // DWMSBT_NONE
            return false;
        }
        return TrySet(handle, DwmwaSystemBackdropType, DwmsbtMainWindow);
    }

    public static void UseSolidBackground(Window window)
    {
        if (window.Background is SolidColorBrush brush && brush.Color.A == 0)
            window.SetResourceReference(Window.BackgroundProperty, "WindowBrush");
    }

    private static bool TrySet(IntPtr handle, int attribute, int value)
    {
        try
        {
            var data = value;
            return DwmSetWindowAttribute(handle, attribute, ref data, sizeof(int)) == 0;
        }
        catch { return false; }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
