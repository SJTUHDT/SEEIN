namespace Seein.Pet;

public readonly record struct PetDragSample(double Milliseconds, double X, double Y);
public readonly record struct PetVelocity(double X, double Y);

// Behavioral port of dsh-pet 0.4.9 shared/physics/release.ts (MIT).
// Coordinates are logical desktop pixels (DIPs), matching Electron's CSS pixel system.
public static class PetDragRelease
{
    public const double DeadZoneSpeed = 500;
    public const double StaleMilliseconds = 150;
    public const double TrailMilliseconds = 200;

    public static PetVelocity? Estimate(IReadOnlyList<PetDragSample> trail, double now, double power = 1)
    {
        if (trail.Count == 0 || now - trail[^1].Milliseconds > StaleMilliseconds) return null;
        var window = trail.Where(s => now - s.Milliseconds <= 150).ToArray();
        if (window.Length < 2) return null;
        var first = window[0];
        var last = window[^1];
        var span = last.Milliseconds - first.Milliseconds;
        if (span < 20) return null;
        var vx = (last.X - first.X) / span * 1000;
        var vy = (last.Y - first.Y) / span * 1000;
        var average = Math.Sqrt(vx * vx + vy * vy);
        if (average < 1e-6) return null;
        var segments = new List<(double Speed, double End)>();
        var previous = first;
        foreach (var sample in window.Skip(1))
        {
            var dt = sample.Milliseconds - previous.Milliseconds;
            if (dt < 8) continue;
            var dx = sample.X - previous.X;
            var dy = sample.Y - previous.Y;
            segments.Add((Math.Sqrt(dx * dx + dy * dy) / dt * 1000, sample.Milliseconds));
            previous = sample;
        }
        var peak = segments.Count == 0 ? average : segments.Max(s => s.Speed);
        var acceleration = segments.Count < 2 ? 0 : (segments[^1].Speed - segments[0].Speed)
            / Math.Max((segments[^1].End - segments[0].End) / 1000, .020);
        var raw = (.5 * average + .5 * peak) * (1 + Math.Min(Math.Max(acceleration, 0) / 8000, 1) * .6);
        var speed = 3600 * (1 - Math.Exp(-raw / 3600)) * Math.Max(0, power);
        return speed < DeadZoneSpeed ? null : new PetVelocity(vx / average * speed, vy / average * speed);
    }

    public static PetVelocity Slingshot(double pullX, double pullY, double size, double power = 1)
    {
        if (size <= 0) return new(0, 0);
        var factor = 1200 * Math.Max(0, power) / (size * .6);
        return new(-pullX * factor, -pullY * factor);
    }
}
