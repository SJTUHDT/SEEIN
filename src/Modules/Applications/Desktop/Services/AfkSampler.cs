using Seein.Core;
using Seein.Core.Ipc;
using System.Runtime.InteropServices;

namespace Seein.Tray;

/// <summary>
/// Reports how long the user has been idle.  Only the user session can read the input queue, which is
/// why this lives in the tray and is sent to the service through the pipe.
/// </summary>
public sealed class AfkSampler
{
    private readonly PanelPipeClient _pipe;
    private int _lastReported = -1;

    public AfkSampler(PanelPipeClient pipe) => _pipe = pipe;

    public async Task LoopAsync(CancellationToken token)
    {
        var lastSent = DateTimeOffset.MinValue;
        while (!token.IsCancellationRequested)
        {
            try
            {
                var idle = IdleSeconds();
                var now = DateTimeOffset.UtcNow;
                if (_pipe.IsConnected && (now - lastSent).TotalSeconds >= 5 && Math.Abs(idle - _lastReported) >= 2)
                {
                    await _pipe.SendAsync(PipeMessageType.AfkSample, new AfkSample(now.ToUnixTimeSeconds(), idle), token);
                    lastSent = now;
                    _lastReported = idle;
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
            catch { }
            try { await Task.Delay(2000, token); }
            catch (OperationCanceledException) { break; }
        }
    }

    private static int IdleSeconds()
    {
        var info = new LastInputInfo { Size = (uint)Marshal.SizeOf<LastInputInfo>() };
        if (!GetLastInputInfo(ref info)) return 0;
        var elapsed = (uint)Environment.TickCount64 - info.Time;
        return (int)Math.Min(int.MaxValue, elapsed / 1000);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LastInputInfo
    {
        public uint Size;
        public uint Time;
    }

    [DllImport("user32.dll")]
    private static extern bool GetLastInputInfo(ref LastInputInfo info);
}
