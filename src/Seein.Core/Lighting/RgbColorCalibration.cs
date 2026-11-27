using System.Numerics;
using System.Runtime.CompilerServices;

namespace Seein.Core.Lighting;

public sealed record RgbCalibrationPoint(RgbColor Target, RgbColor Output, double WaveHold = 0);

public sealed record RgbCalibrationSample(RgbColor? Target = null, RgbColor? Output = null);

/// <summary>Drive values that visually match the reference colors of an RGB cube.</summary>
public sealed record RgbColorCalibration(
    RgbColor? White = null,
    RgbColor? Yellow = null,
    RgbColor? Cyan = null,
    RgbColor? Magenta = null,
    RgbColor? Red = null,
    RgbColor? Green = null,
    RgbColor? Blue = null,
    RgbCalibrationPoint? InteriorPoint = null,
    IReadOnlyList<RgbCalibrationSample>? Samples = null)
{
    public const int MaxSamples = 80;
    private static readonly ConditionalWeakTable<RgbColorCalibration, ResidualMap> ResidualMaps = new();

    public RgbColor Correct(RgbColor color)
    {
        if (Samples is not { Count: > 0 }) return CorrectBase(color);
        return ResidualMaps.GetValue(this, static calibration => new ResidualMap(calibration)).Apply(color);
    }

    public string? ValidateSamples()
    {
        if (Samples is null) return null;
        if (Samples.Count > MaxSamples) return $"分区颜色校正最多支持 {MaxSamples} 个附加样点。";
        var seen = new HashSet<RgbColor>();
        foreach (var sample in Samples)
        {
            if (sample?.Target is not { } target || sample.Output is not { } output)
                return "校色样点须包含目标颜色和对应的输出颜色。";
            if (!seen.Add(target)) return $"校色样点中有重复的目标颜色 {target.ToHex()}。";
            if (!IsIntermediateGray(target) && FixedDistanceSquared(target, InteriorPoint) == 0 && CorrectBase(target) != output)
                return $"目标颜色 {target.ToHex()} 已由基础校色定义。";
        }
        return null;
    }

    private RgbColor CorrectBase(RgbColor color, RgbColor[]? neutralRamp = null)
    {
        // Tetrahedral interpolation. Split the input into neutral, secondary and
        // primary components so that a neutral gray uses the same ratio as white.
        var (min, mid, max) = Levels(color);
        var secondaryVertex = Vertex(color, mid);
        var primaryVertex = Vertex(color, max);
        var neutral = White ?? new RgbColor(255, 255, 255);
        var secondary = Anchor(secondaryVertex);
        var primary = Anchor(primaryVertex);
        double neutralWeight = min;
        double secondaryWeight = mid - min;
        double primaryWeight = max - mid;
        var pointWeight = 0d;
        var pointColor = default(RgbColor);
        if (InteriorPoint is { } point)
        {
            var (pointMin, pointMid, pointMax) = Levels(point.Target);
            if (pointMin > 0 && pointMid > pointMin && pointMax == 255 && pointMax > pointMid &&
                secondaryVertex == Vertex(point.Target, pointMid) && primaryVertex == Vertex(point.Target, pointMax))
            {
                // Subdivide only this tetrahedron. The additional point has zero weight on
                // the shared faces, so neighboring hues and the cube anchors stay continuous.
                var fraction = Math.Min(neutralWeight / pointMin,
                    Math.Min(secondaryWeight / (pointMid - pointMin), primaryWeight / (pointMax - pointMid)));
                neutralWeight -= fraction * pointMin;
                secondaryWeight -= fraction * (pointMid - pointMin);
                primaryWeight -= fraction * (pointMax - pointMid);
                pointWeight = fraction * 255;
                pointColor = point.Output;
            }
        }

        var neutralOutput = neutralRamp is null ? Vector3.Zero : ReadNeutralRamp(neutralRamp, neutralWeight);
        byte Interpolate(byte w, byte s, byte p, byte x, float neutralChannel)
        {
            var chromatic = s * secondaryWeight + p * primaryWeight + x * pointWeight;
            var output = neutralRamp is null ? (w * neutralWeight + chromatic) / 255 : neutralChannel + chromatic / 255;
            return (byte)Math.Clamp(Math.Round(output, MidpointRounding.AwayFromZero), 0, 255);
        }

        return new RgbColor(Interpolate(neutral.R, secondary.R, primary.R, pointColor.R, neutralOutput.X),
            Interpolate(neutral.G, secondary.G, primary.G, pointColor.G, neutralOutput.Y),
            Interpolate(neutral.B, secondary.B, primary.B, pointColor.B, neutralOutput.Z));
    }

    private static bool IsIntermediateGray(RgbColor color) =>
        color.R is > 0 and < 255 && color.R == color.G && color.G == color.B;

    private static Vector3 ReadNeutralRamp(RgbColor[] ramp, double level)
    {
        var index = Math.Clamp((int)level, 0, 254);
        var fraction = (float)Math.Clamp(level - index, 0, 1);
        var start = ramp[index];
        var end = ramp[index + 1];
        return Vector3.Lerp(new(start.R, start.G, start.B), new(end.R, end.G, end.B), fraction);
    }

    private static (int Min, int Mid, int Max) Levels(RgbColor color) =>
        (Math.Min(color.R, Math.Min(color.G, color.B)),
         Math.Max(Math.Min(color.R, color.G), Math.Max(Math.Min(color.R, color.B), Math.Min(color.G, color.B))),
         Math.Max(color.R, Math.Max(color.G, color.B)));

    private static RgbColor Vertex(RgbColor color, int level) =>
        new(color.R >= level ? (byte)255 : (byte)0, color.G >= level ? (byte)255 : (byte)0,
            color.B >= level ? (byte)255 : (byte)0);

    private RgbColor Anchor(RgbColor vertex)
    {
        var calibrated = vertex switch
        {
            (255, 255, 0) => Yellow,
            (0, 255, 255) => Cyan,
            (255, 0, 255) => Magenta,
            (255, 0, 0) => Red,
            (0, 255, 0) => Green,
            (0, 0, 255) => Blue,
            _ => null
        };
        return calibrated ?? vertex;
    }

    private static double FixedDistanceSquared(RgbColor color, RgbCalibrationPoint? interior)
    {
        // All eight cube vertices and the continuous neutral axis belong to the original calibration.
        int r = Math.Min((int)color.R, 255 - color.R), g = Math.Min((int)color.G, 255 - color.G),
            b = Math.Min((int)color.B, 255 - color.B);
        int rg = color.R - color.G, gb = color.G - color.B, br = color.B - color.R;
        var nearest = Math.Min(r * r + g * g + b * b, (rg * rg + gb * gb + br * br) / 3d);
        return interior is null ? nearest : Math.Min(nearest, DistanceSquared(color, interior.Target));
    }

    private static int DistanceSquared(RgbColor first, RgbColor second)
    {
        int r = first.R - second.R, g = first.G - second.G, b = first.B - second.B;
        return r * r + g * g + b * b;
    }

    private sealed class ResidualMap
    {
        // RGB code-value distance. Beyond this radius a visual match contributes no correction.
        private const double RadiusSquared = 144 * 144;
        private readonly record struct Entry(RgbColor Target, RgbColor Output, int Red, int Green, int Blue);
        private readonly Entry[] _entries;
        private readonly Segment[] _segments;
        private readonly RgbCalibrationPoint? _interior;
        private readonly RgbColor[]? _neutralRamp;
        private readonly RgbColorCalibration _calibration;

        public ResidualMap(RgbColorCalibration calibration)
        {
            _calibration = calibration;
            _interior = calibration.InteriorPoint;
            if (calibration.ValidateSamples() is not null)
            {
                _entries = [];
                _segments = [];
                return;
            }
            var measured = calibration.Samples!;
            var grays = measured.Where(sample => IsIntermediateGray(sample.Target!.Value))
                .OrderBy(sample => sample.Target!.Value.R).ToArray();
            _neutralRamp = grays.Length > 0 ? BuildNeutralRamp(calibration.White ?? new(255, 255, 255), grays) : null;
            _entries = measured.Where(sample => !IsIntermediateGray(sample.Target!.Value)).Select(sample =>
            {
                var target = sample.Target!.Value;
                var output = sample.Output!.Value;
                var baseline = calibration.CorrectBase(target, _neutralRamp);
                return new Entry(target, output, output.R - baseline.R, output.G - baseline.G, output.B - baseline.B);
            }).ToArray();
            _segments = FindMeasuredSegments(_entries);
        }

        public RgbColor Apply(RgbColor color)
        {
            var baseline = _calibration.CorrectBase(color, _neutralRamp);
            var fixedDistance = FixedDistanceSquared(color, _interior);
            if (fixedDistance == 0) return baseline;

            // Normalized inverse-distance interpolation of measured deviations. A small baseline weight
            // fades isolated corrections smoothly to zero at the support boundary.
            double weightSum = 0.05 + Weight(fixedDistance);
            double red = 0, green = 0, blue = 0;
            var nearestAnchor = fixedDistance;
            foreach (var entry in _entries)
            {
                var distance = DistanceSquared(color, entry.Target);
                if (distance == 0) return entry.Output;
                nearestAnchor = Math.Min(nearestAnchor, distance);
                if (distance >= RadiusSquared) continue;
                var weight = Weight(distance);
                weightSum += weight;
                red += weight * entry.Red;
                green += weight * entry.Green;
                blue += weight * entry.Blue;
            }
            byte Channel(byte value, double delta) => (byte)Math.Clamp(
                Math.Round(value + delta / weightSum, MidpointRounding.AwayFromZero), 0d, 255d);
            var corrected = new RgbColor(Channel(baseline.R, red), Channel(baseline.G, green), Channel(baseline.B, blue));
            return _segments.Length == 0 ? corrected : FollowMeasuredSegments(color, corrected, fixedDistance, nearestAnchor);
        }

        private static RgbColor[] BuildNeutralRamp(RgbColor white, RgbCalibrationSample[] grays)
        {
            var ramp = new RgbColor[256];
            var start = 0;
            var from = Vector3.Zero;
            foreach (var anchor in grays.Append(new(new RgbColor(255, 255, 255), white)))
            {
                var end = anchor.Target!.Value.R;
                var to = Vector(anchor.Output!.Value);
                for (var i = start + 1; i <= end; i++)
                {
                    var output = Vector3.Lerp(from, to, (float)(i - start) / (end - start));
                    ramp[i] = new((byte)MathF.Round(output.X), (byte)MathF.Round(output.Y), (byte)MathF.Round(output.Z));
                }
                start = end;
                from = to;
            }
            return ramp;
        }

        // Three measurements on the same RGB ramp give a 1D interpolation constraint. Using the
        // unconstrained 3D residuals between them can reverse the drive channels several times.
        private readonly record struct Segment(int First, int Last, Vector3 Start, Vector3 Direction, float LengthSquared,
            Vector3 FirstOutput, Vector3 OutputDelta);

        private static Segment[] FindMeasuredSegments(Entry[] entries)
        {
            if (entries.Length < 3) return [];
            var targets = entries.Select(e => Vector(e.Target)).ToArray();
            var segments = new Dictionary<(int, int), Segment>();
            var subdivided = new HashSet<(int, int)>();
            for (var first = 0; first < entries.Length; first++)
            for (var last = first + 1; last < entries.Length; last++)
            {
                var direction = targets[last] - targets[first];
                var lengthSquared = direction.LengthSquared();
                for (var middle = 0; middle < targets.Length; middle++)
                {
                    if (middle == first || middle == last) continue;
                    var position = Vector3.Dot(targets[middle] - targets[first], direction) / lengthSquared;
                    if (position <= 0 || position >= 1) continue;
                    var distance = Vector3.DistanceSquared(targets[middle], targets[first] + position * direction);
                    // Allow rounding of 8-bit target colors. Nearby points alone cannot establish a ramp's direction.
                    var toEnd = Math.Min(position, 1 - position);
                    if (distance > Math.Min(2.25f, 0.01f * toEnd * toEnd * lengthSquared)) continue;
                    subdivided.Add((first, last));
                    AddSegment(first, middle);
                    AddSegment(middle, last);
                }
            }
            foreach (var pair in subdivided) segments.Remove(pair);
            return segments.Values.ToArray();

            void AddSegment(int a, int b)
            {
                if (a > b) (a, b) = (b, a);
                if (segments.ContainsKey((a, b))) return;
                var delta = targets[b] - targets[a];
                var firstOutput = Vector(entries[a].Output);
                segments.Add((a, b), new(a, b, targets[a], delta, delta.LengthSquared(), firstOutput,
                    Vector(entries[b].Output) - firstOutput));
            }
        }

        private RgbColor FollowMeasuredSegments(RgbColor color, RgbColor corrected, double fixedDistance, double nearestAnchor)
        {
            // Fade into the existing 3D correction outside a narrow neighborhood. Weighting that
            // correction near its fixed points also preserves continuity at crossing ramps and the gray axis.
            const double widthSquared = 16 * 16;
            var point = Vector(color);
            var original = Vector(corrected);
            double sum = 1 + Weight(nearestAnchor, widthSquared);
            var adjustment = Vector3.Zero;
            foreach (var segment in _segments)
            {
                var t = Math.Clamp(Vector3.Dot(point - segment.Start, segment.Direction) / segment.LengthSquared, 0, 1);
                var distance = Vector3.DistanceSquared(point, segment.Start + t * segment.Direction);
                if (distance >= widthSquared) continue;
                var weight = Weight(Math.Max(distance, 0.0001f), widthSquared);
                // A ramp may pass very close to an independent anchor or the neutral axis. Retain
                // a smooth neighborhood around it, even when the query falls exactly on the ramp.
                const double guardSquared = 8 * 8;
                var influence = 1d;
                if (nearestAnchor < guardSquared)
                {
                    var guard = fixedDistance;
                    for (var i = 0; i < _entries.Length; i++)
                        if (i != segment.First && i != segment.Last)
                            guard = Math.Min(guard, DistanceSquared(color, _entries[i].Target));
                    var x = Math.Min(1, guard / guardSquared);
                    influence = x * x * (3 - 2 * x);
                }
                adjustment += (float)(weight * influence) * (segment.FirstOutput + t * segment.OutputDelta - original);
                sum += weight;
            }
            var blended = original + adjustment / (float)sum;
            static byte Channel(float value) => (byte)Math.Clamp(MathF.Round(value, MidpointRounding.AwayFromZero), 0, 255);
            return new(Channel(blended.X), Channel(blended.Y), Channel(blended.Z));
        }

        private static Vector3 Vector(RgbColor color) => new(color.R, color.G, color.B);

        private static double Weight(double distanceSquared, double radiusSquared = RadiusSquared)
        {
            if (distanceSquared >= radiusSquared) return 0;
            var ratio = distanceSquared / radiusSquared;
            var taper = 1 - ratio;
            return taper * taper / ratio;
        }
    }
}
