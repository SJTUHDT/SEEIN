namespace Seein.Tray.CapturePlayback;

/// <summary>Some DirectShow drivers accept Run but never deliver a sample (for example when busy).</summary>
internal sealed class CaptureFirstFrameGate
{
    private readonly TaskCompletionSource _arrived = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public void FrameArrived() => _arrived.TrySetResult();
    public Task WaitAsync(TimeSpan timeout, CancellationToken token) => _arrived.Task.WaitAsync(timeout, token);
}
