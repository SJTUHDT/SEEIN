namespace Seein.Devices.CapturePlayback;

/// <summary>Owns at most one waiting frame. Replacing/stopping always releases the old buffer.</summary>
public sealed class LatestFrameSlot<T> : IDisposable where T : class, IDisposable
{
    private readonly object _gate = new();
    private T? _item;
    private bool _disposed;
    public long Dropped { get; private set; }
    public bool Offer(T frame)
    {
        lock (_gate)
        {
            if (_disposed) { frame.Dispose(); return false; }
            if (_item != null) { _item.Dispose(); Dropped++; }
            _item = frame;
            return true;
        }
    }
    public T? Take() { lock (_gate) { var item = _item; _item = null; return item; } }
    public void Clear() { lock (_gate) { _item?.Dispose(); _item = null; } }
    public void Dispose() { lock (_gate) { _disposed = true; _item?.Dispose(); _item = null; } }
}
