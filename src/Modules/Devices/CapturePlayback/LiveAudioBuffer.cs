namespace Seein.Devices.CapturePlayback;

/// <summary>Bounded PCM ring with small drift correction. All offsets stay on sample-frame boundaries.</summary>
public sealed class LiveAudioBuffer
{
    private readonly object _gate = new();
    private readonly byte[] _buffer;
    private readonly int _frameBytes, _targetBytes;
    private int _read, _count;
    public long DiscardedBytes { get; private set; }
    public int BufferedBytes { get { lock (_gate) return _count; } }
    public LiveAudioBuffer(int bytesPerSecond, int frameBytes)
    {
        _frameBytes = Math.Max(1, frameBytes);
        _targetBytes = Align(bytesPerSecond * 40 / 1000);
        _buffer = new byte[Math.Max(_frameBytes * 4, Align(bytesPerSecond * 100 / 1000))];
    }
    private int Align(int bytes) => Math.Max(0, bytes / _frameBytes * _frameBytes);
    private void Discard(int bytes) { _read = (_read + bytes) % _buffer.Length; _count -= bytes; DiscardedBytes += bytes; }
    public void Write(byte[] data, int offset, int count)
    {
        lock (_gate)
        {
            count = Align(count);
            if (count >= _buffer.Length) { offset += count - _buffer.Length; DiscardedBytes += count - _buffer.Length; count = _buffer.Length; }
            if (_count + count > _buffer.Length) Discard(_count + count - _buffer.Length);
            var write = (_read + _count) % _buffer.Length;
            var first = Math.Min(count, _buffer.Length - write);
            Array.Copy(data, offset, _buffer, write, first);
            Array.Copy(data, offset + first, _buffer, 0, count - first);
            _count += count;
        }
    }
    public int Read(byte[] output, int offset, int count)
    {
        lock (_gate)
        {
            // An accumulated device-clock mismatch must not turn into growing audio latency.
            var excess = Align(_count - _targetBytes - count);
            if (excess > _frameBytes * 2) Discard(Math.Min(excess, _frameBytes * 8));
            var copied = Math.Min(Align(count), _count);
            var first = Math.Min(copied, _buffer.Length - _read);
            Array.Copy(_buffer, _read, output, offset, first);
            Array.Copy(_buffer, 0, output, offset + first, copied - first);
            _read = (_read + copied) % _buffer.Length;
            _count -= copied;
            Array.Clear(output, offset + copied, count - copied);
            return count;
        }
    }
}
