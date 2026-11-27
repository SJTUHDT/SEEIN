namespace Seein.Core.Lighting;

/// <summary>Keep software perimeter animation on the same speed bands as MS-7E61 firmware.</summary>
public static class MsiWaveTiming
{
    // User measured the native strip's same-colour recurrence at speed 34 on 2026-11-26.
    public const double NormalCycleSeconds = 3.5;

    public static int NativeSpeedLevel(double speed) =>
        (int)Math.Round(Math.Clamp(speed,0,100)/50,MidpointRounding.AwayFromZero);

    // Only the normal band is measured. Adjacent bands are explicit half/double-speed defaults.
    public static double CycleSeconds(double speed) => NativeSpeedLevel(speed) switch
    {
        0 => NormalCycleSeconds*2,
        2 => NormalCycleSeconds/2,
        _ => NormalCycleSeconds
    };
}
