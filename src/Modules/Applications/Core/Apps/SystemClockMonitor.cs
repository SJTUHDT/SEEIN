using System.Runtime.InteropServices;

namespace Seein.Core.Apps;

/// <summary>
/// Detects sleep/hibernate gaps and exposes the last boot instant.  <c>GetTickCount64</c> keeps
/// counting while the machine sleeps, <c>QueryUnbiasedInterruptTime</c> does not, so the difference
/// between the two is exactly the time the machine was suspended.
/// </summary>
public sealed class SystemClockMonitor
{
    private ulong _wallSeconds;
    private ulong _awakeSeconds;

    public SystemClockMonitor()
    {
        _wallSeconds = WallSeconds();
        _awakeSeconds = AwakeSeconds();
    }

    /// <summary>Returns the number of suspended seconds since the previous call.</summary>
    public long Advance()
    {
        var wall = WallSeconds();
        var awake = AwakeSeconds();
        var wallDelta = wall >= _wallSeconds ? wall - _wallSeconds : 0;
        var awakeDelta = awake >= _awakeSeconds ? awake - _awakeSeconds : 0;
        _wallSeconds = wall;
        _awakeSeconds = awake;
        return wallDelta > awakeDelta ? (long)(wallDelta - awakeDelta) : 0;
    }

    public static DateTimeOffset BootTimeUtc()
    {
        var wall = WallSeconds();
        return DateTimeOffset.UtcNow.AddSeconds(-(double)wall);
    }

    private static ulong WallSeconds() => GetTickCount64() / 1000;

    private static ulong AwakeSeconds()
    {
        if (!QueryUnbiasedInterruptTime(out var value)) return 0;
        return value / 10_000_000;
    }

    [DllImport("kernel32.dll")]
    private static extern ulong GetTickCount64();

    [DllImport("kernel32.dll")]
    private static extern bool QueryUnbiasedInterruptTime(out ulong unbiasedTime);
}
