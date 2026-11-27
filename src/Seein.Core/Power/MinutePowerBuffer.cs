namespace Seein.Core.Power;

public sealed record MinutePowerSample(DateTimeOffset Started, double Min, double Average, double Max, int Count);
public sealed class MinutePowerBuffer(DateTimeOffset started)
{
    private readonly object _gate = new();
    private readonly Queue<MinutePowerSample> _pending = [];
    private DateTimeOffset _started = started;
    private double _min = double.MaxValue, _max, _sum;
    private int _count;
    public void Add(DateTimeOffset now, double watts)
    {
        lock (_gate)
        {
            if (now - _started >= TimeSpan.FromMinutes(1)) CloseCore(now);
            _min = Math.Min(_min, watts); _max = Math.Max(_max, watts); _sum += watts; _count++;
        }
    }
    private MinutePowerSample? Current() => _count == 0 ? null : new(_started, _min, _sum / _count, _max, _count);
    private void CloseCore(DateTimeOffset now)
    {
        if (Current() is { } sample) _pending.Enqueue(sample);
        _started = now; _min = double.MaxValue; _max = 0; _sum = 0; _count = 0;
    }
    public void Close(DateTimeOffset now) { lock (_gate) CloseCore(now); }
    public IReadOnlyList<MinutePowerSample> Snapshot(bool includeCurrent = false)
    { lock (_gate) return includeCurrent && Current() is { } current ? _pending.Append(current).ToArray() : _pending.ToArray(); }
    public async Task FlushAsync(Func<MinutePowerSample, CancellationToken, Task> write, CancellationToken token)
    {
        foreach (var sample in Snapshot())
        {
            await write(sample, token).ConfigureAwait(false);
            lock (_gate) { if (_pending.TryPeek(out var head) && ReferenceEquals(head, sample)) _pending.Dequeue(); }
        }
    }
}
