using System.Buffers;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using FlashCap;
using Seein.Devices.CapturePlayback;
using Vortice.Direct2D1;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;
using Vortice.WIC;
using DxPixelFormat = Vortice.DCommon.PixelFormat;
using DxAlphaMode = Vortice.DCommon.AlphaMode;

namespace Seein.Tray.CapturePlayback;

internal sealed class EncodedCaptureFrame : IDisposable
{
    public byte[] Bytes { get; }
    public int Length { get; }
    public long ArrivedAt { get; } = Stopwatch.GetTimestamp();
    public EncodedCaptureFrame(ArraySegment<byte> source)
    {
        Length = source.Count;
        Bytes = ArrayPool<byte>.Shared.Rent(Length);
        source.AsSpan().CopyTo(Bytes);
    }
    public void Dispose() => ArrayPool<byte>.Shared.Return(Bytes);
}

/// <summary>One dedicated MTA worker owns WIC and D3D. No WPF frame objects or UI-thread decode.</summary>
internal sealed class CaptureRenderer : IDisposable
{
    internal static void ValidateGpuSurface(nint window)
    {
        using var surface = new GpuSurface(window);
        surface.Present(new byte[64 * 36 * 4], 64, 36);
    }
    private readonly LatestFrameSlot<EncodedCaptureFrame> _frames = new();
    private readonly AutoResetEvent _ready = new(false);
    private readonly Thread _worker;
    private nint _target;
    private volatile bool _visible, _disposed;
    private int _clearRequested;
    private long _presented, _decodeErrors;
    private double _latency;
    public long Presented => Interlocked.Read(ref _presented);
    public long Errors => Interlocked.Read(ref _decodeErrors);
    public long Dropped => _frames.Dropped;
    public double LatencyMs => Volatile.Read(ref _latency);
    public string? Failure { get; private set; }
    public bool HasVisibleTarget
    {
        get
        {
            var handle = Interlocked.CompareExchange(ref _target, 0, 0);
            return _visible && handle != 0 && IsWindowVisible(handle) && !IsIconic(GetAncestor(handle, 2));
        }
    }
    public CaptureRenderer()
    {
        _worker = new Thread(Run) { Name = "SEEIN Capture Render", IsBackground = true };
        _worker.SetApartmentState(ApartmentState.MTA);
        _worker.Start();
    }
    public void Attach(nint target) { Interlocked.Exchange(ref _target, target); _ready.Set(); }
    public void SetVisible(bool visible) { _visible = visible; if (!visible) _frames.Clear(); }
    public void Offer(EncodedCaptureFrame frame) { if (_frames.Offer(frame)) _ready.Set(); }
    public void Clear() { _frames.Clear(); Interlocked.Exchange(ref _clearRequested, 1); _ready.Set(); }

    private void Run()
    {
        CoInitializeEx(0, 0);
        try
        {
            using var decoder = new CaptureFrameDecoder();
            GpuSurface? surface = null;
            nint current = 0;
            long retrySurfaceAt = 0;
            try
            {
                while (!_disposed)
                {
                    _ready.WaitOne(200);
                    if (_disposed) break;
                    var target = Interlocked.CompareExchange(ref _target, 0, 0);
                    if (current != target) { surface?.Dispose(); surface = null; current = target; retrySurfaceAt = 0; }
                    if (Interlocked.Exchange(ref _clearRequested, 0) != 0)
                    {
                        // Discard GPU resources too so an old frame cannot reappear after reconnect/stop.
                        surface?.Dispose(); surface = null;
                    }
                    using var frame = _frames.Take();
                    if (frame == null || !HasVisibleTarget) continue;
                    if (surface == null && Stopwatch.GetTimestamp() < retrySurfaceAt) continue;
                    var decodedSuccessfully = false;
                    try
                    {
                        var decoded = decoder.Decode(frame.Bytes, frame.Length);
                        decodedSuccessfully = true;
                        surface ??= new GpuSurface(target);
                        if (surface.Present(decoded.Pixels, decoded.Width, decoded.Height))
                        {
                            Interlocked.Increment(ref _presented);
                            Volatile.Write(ref _latency, Stopwatch.GetElapsedTime(frame.ArrivedAt).TotalMilliseconds);
                            Failure = null;
                        }
                    }
                    catch (Exception ex)
                    {
                        Interlocked.Increment(ref _decodeErrors);
                        Failure = ex.Message;
                        if (decodedSuccessfully)
                        {
                            surface?.Dispose(); surface = null;
                            retrySurfaceAt = Stopwatch.GetTimestamp() + Stopwatch.Frequency;
                        }
                    }
                }
            }
            finally { surface?.Dispose(); }
        }
        catch (Exception ex) { Failure = "播放渲染初始化失败：" + ex.Message; }
        finally { CoUninitialize(); }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _frames.Dispose(); _ready.Set();
        if (_worker.Join(3000)) _ready.Dispose();
    }

    private sealed class GpuSurface : IDisposable
    {
        private readonly nint _window;
        private readonly ID3D11Device _device;
        private readonly ID2D1Factory1 _factory;
        private readonly ID2D1Device _d2d;
        private readonly ID2D1DeviceContext _context;
        private readonly IDXGISwapChain1 _chain;
        private ID2D1Bitmap1? _target, _bitmap;
        private int _width, _height, _imageWidth, _imageHeight;
        public GpuSurface(nint window)
        {
            _window = window;
            _device = D3D11.D3D11CreateDevice(DriverType.Hardware, DeviceCreationFlags.BgraSupport);
            try
            {
                using var dxgi = _device.QueryInterface<IDXGIDevice>();
                _factory = D2D1.D2D1CreateFactory<ID2D1Factory1>(FactoryType.SingleThreaded);
                _d2d = _factory.CreateDevice(dxgi);
                _context = _d2d.CreateDeviceContext();
                using var adapter = dxgi.GetAdapter();
                using var factory = adapter.GetParent<IDXGIFactory2>();
                _chain = factory.CreateSwapChainForHwnd(_device, window, new SwapChainDescription1
                {
                    Width = 1, Height = 1, Format = Format.B8G8R8A8_UNorm,
                    BufferCount = 2, BufferUsage = Usage.RenderTargetOutput,
                    SampleDescription = new(1, 0), SwapEffect = SwapEffect.FlipDiscard,
                    Scaling = Scaling.Stretch, AlphaMode = Vortice.DXGI.AlphaMode.Ignore,
                    Flags = SwapChainFlags.FrameLatencyWaitableObject
                });
                factory.MakeWindowAssociation(window, WindowAssociationFlags.IgnoreAltEnter);
                using var chain2 = _chain.QueryInterface<IDXGISwapChain2>();
                chain2.MaximumFrameLatency = 1;
            }
            catch { Dispose(); throw; }
        }
        public bool Present(byte[] pixels, int imageWidth, int imageHeight)
        {
            if (!GetClientRect(_window, out var rect)) return false;
            var width = rect.Right - rect.Left; var height = rect.Bottom - rect.Top;
            if (width <= 0 || height <= 0) return false;
            if (_width != width || _height != height)
            {
                _context.Target = null!;
                _target?.Dispose(); _target = null;
                _chain.ResizeBuffers(2, (uint)width, (uint)height, Format.B8G8R8A8_UNorm, SwapChainFlags.FrameLatencyWaitableObject).CheckError();
                using var back = _chain.GetBuffer<IDXGISurface>(0);
                _target = _context.CreateBitmapFromDxgiSurface(back, new BitmapProperties1(
                    new DxPixelFormat(Format.B8G8R8A8_UNorm, DxAlphaMode.Ignore), 96, 96, BitmapOptions.Target | BitmapOptions.CannotDraw));
                _context.Target = _target;
                _width = width; _height = height;
            }
            if (_imageWidth != imageWidth || _imageHeight != imageHeight)
            {
                _bitmap?.Dispose();
                _bitmap = _context.CreateBitmap(new SizeI(imageWidth, imageHeight),
                    new BitmapProperties1(new DxPixelFormat(Format.B8G8R8A8_UNorm, DxAlphaMode.Ignore)));
                _imageWidth = imageWidth; _imageHeight = imageHeight;
            }
            _bitmap!.CopyFromMemory(pixels, (uint)(imageWidth * 4)).CheckError();
            var scale = Math.Min((float)width / imageWidth, (float)height / imageHeight);
            var w = imageWidth * scale; var h = imageHeight * scale;
            _context.BeginDraw();
            _context.Clear(new Color4(0, 0, 0, 1));
            _context.DrawBitmap(_bitmap, new Vortice.RawRectF((width - w) / 2, (height - h) / 2, (width + w) / 2, (height + h) / 2),
                1, Vortice.Direct2D1.InterpolationMode.Linear, null, null);
            _context.EndDraw().CheckError();
            var result = _chain.Present(0, PresentFlags.DoNotWait);
            if (result.Code == unchecked((int)0x887A000A)) return false; // Never queue behind a busy swap chain.
            result.CheckError();
            return result.Code == 0;
        }
        public void Dispose()
        {
            if (_context != null) _context.Target = null!;
            _bitmap?.Dispose(); _target?.Dispose(); _chain?.Dispose(); _context?.Dispose();
            _d2d?.Dispose(); _factory?.Dispose(); _device?.Dispose();
        }
    }

    [DllImport("ole32.dll")] private static extern int CoInitializeEx(nint reserved, uint coInit);
    [DllImport("ole32.dll")] private static extern void CoUninitialize();
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint window);
    [DllImport("user32.dll")] private static extern bool IsIconic(nint window);
    [DllImport("user32.dll")] private static extern nint GetAncestor(nint window, uint flags);
    [DllImport("user32.dll")] private static extern bool GetClientRect(nint window, out Rect rect);
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
}

internal sealed class CaptureVideoHost(CaptureRenderer renderer) : HwndHost
{
    public event Action<int>? PlaybackKeyPressed;
    protected override HandleRef BuildWindowCore(HandleRef parent)
    {
        var handle = CreateWindowEx(0, "STATIC", "", 0x50000000 | 0x04000000, 0, 0, 1, 1, parent.Handle, 0, 0, 0);
        if (handle == 0) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        renderer.Attach(handle);
        return new HandleRef(this, handle);
    }
    protected override void DestroyWindowCore(HandleRef handle) { renderer.Attach(0); DestroyWindow(handle.Handle); }
    protected override nint WndProc(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        if (msg == 0x0201) SetFocus(hwnd); // Focus only follows a user's click inside the player.
        if (msg == 0x0100 && (wParam == 0x7A || wParam == 0x1B))
        {
            if ((lParam.ToInt64() & (1L << 30)) == 0)
            {
                var key = (int)wParam;
                Dispatcher.BeginInvoke(() => PlaybackKeyPressed?.Invoke(key));
            }
            handled = true; return 0;
        }
        return base.WndProc(hwnd, msg, wParam, lParam, ref handled);
    }
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint CreateWindowEx(uint exStyle, string className, string title, uint style, int x, int y, int width, int height, nint parent, nint menu, nint instance, nint param);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(nint handle);
    [DllImport("user32.dll")] private static extern nint SetFocus(nint handle);
}
