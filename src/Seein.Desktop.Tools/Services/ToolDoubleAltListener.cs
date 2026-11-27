using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace Seein.Tray;

// Reuse Pane's dedicated native hook thread and clean-tap recognition. The
// native callback forwards ordinary keyboard input and only posts a notification.
internal sealed class ToolDoubleAltListener : IDisposable
{
    private const uint Message = 0x8059;
    private readonly nint _window;
    private readonly HwndSource _source;
    private readonly Action _clipboard;
    private readonly Action _toggleCapture;
    private nint _library;
    private StartHook? _start;
    private StopHook? _stop;
    private bool _running;
    private bool _leftEnabled, _rightEnabled;
    private nuint _generation;
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int StartHook(nint window, uint message, nuint generation, uint first, uint second, uint third);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void StopHook();

    public ToolDoubleAltListener(nint window, Action clipboard, Action toggleCapture)
    {
        _window = window; _clipboard = clipboard; _toggleCapture = toggleCapture;
        _source = HwndSource.FromHwnd(window) ?? throw new InvalidOperationException("快捷键窗口尚未就绪。");
        _source.AddHook(HandleMessage);
    }
    public void SetEnabled(bool leftEnabled, bool rightEnabled)
    {
        if (_leftEnabled == leftEnabled && _rightEnabled == rightEnabled && _running == (leftEnabled || rightEnabled)) return;
        _generation++;
        _stop?.Invoke(); _running = false; _leftEnabled = _rightEnabled = false;
        if (!leftEnabled && !rightEnabled) return;
        if (_library == 0)
        {
            var path = ToolShortcutCatalog.DoubleAltLibrary();
            _library = NativeLibrary.Load(path);
            _start = Marshal.GetDelegateForFunctionPointer<StartHook>(NativeLibrary.GetExport(_library, "PaneKeyboardStart"));
            _stop = Marshal.GetDelegateForFunctionPointer<StopHook>(NativeLibrary.GetExport(_library, "PaneKeyboardStop"));
        }
        // Native slot 2 preserves clipboard's fullscreen policy. Slot 4 is allowed
        // in fullscreen so the capture switch still works while playing a game.
        var error = _start!(_window, Message, _generation, 0, leftEnabled ? 0xA4u : 0, rightEnabled ? 0xA5u : 0);
        if (error != 0) throw new Win32Exception(error, "双击 Alt 监听未能启动。");
        _running = true; _leftEnabled = leftEnabled; _rightEnabled = rightEnabled;
    }
    private nint HandleMessage(nint window, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (message != Message) return 0;
        handled = true;
        if (_running && (nuint)wParam == _generation)
        {
            if (lParam == 2 && _leftEnabled) _clipboard();
            else if (lParam == 4 && _rightEnabled) _toggleCapture();
        }
        return 0;
    }
    public void Dispose()
    {
        SetEnabled(false, false); _source.RemoveHook(HandleMessage);
        if (_library != 0) { NativeLibrary.Free(_library); _library = 0; }
    }
}
