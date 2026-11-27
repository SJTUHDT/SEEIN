using System.Text.Json;
using Seein.Core.Lighting;

namespace Seein.Tests;

public class RgbCalibrationSampleTests
{
    private static RgbColor Color(string hex) => RgbColor.FromHex(hex);
    private static readonly RgbColorCalibration Legacy = new(White: Color("3C3CFF"), Cyan: Color("0090FF"),
        InteriorPoint: new(Color("BB72FF"), Color("1800FF"), 0.34));
    private static RgbCalibrationSample Sample(string target, string output) => new(Color(target), Color(output));

    [Fact]
    public void LegacyCalibrationAndMissingSampleFieldsRemainReadable()
    {
        var oldJson = """{"white":{"r":60,"g":60,"b":255},"cyan":{"r":0,"g":144,"b":255},"interiorPoint":{"target":{"r":187,"g":114,"b":255},"output":{"r":24,"g":0,"b":255},"waveHold":0.34}}""";
        var restored = JsonSerializer.Deserialize<RgbColorCalibration>(oldJson, Seein.Core.JsonDefaults.Options)!;
        Assert.Null(restored.Samples);
        foreach (var r in Enumerable.Range(0, 11))
        foreach (var g in Enumerable.Range(0, 11))
        foreach (var b in Enumerable.Range(0, 11))
        {
            var color = new RgbColor((byte)(r * 25), (byte)(g * 25), (byte)(b * 25));
            Assert.Equal(Legacy.Correct(color), restored.Correct(color));
        }

        var incomplete = JsonSerializer.Deserialize<RgbColorCalibration>(
            """{"samples":[{"target":{"r":25,"g":60,"b":90}}]}""", Seein.Core.JsonDefaults.Options)!;
        Assert.NotNull(incomplete.ValidateSamples());
        Assert.Equal(Color("193C5A"), incomplete.Correct(Color("193C5A")));
    }

    [Fact]
    public void MultipleVisualSamplesAndRecordedZeroResidualMatchesAreExactAfterReload()
    {
        // Outputs are synthetic fixtures, not hardware measurements.
        var heldColor = Color("2892FF");
        var calibration = Legacy with { Samples = [
            Sample("0067C0", "105ED1"), Sample("804D93", "5410A6"), Sample("FF3366", "D92095"),
            new(heldColor, Legacy.Correct(heldColor))] };
        var loaded = JsonSerializer.Deserialize<RgbColorCalibration>(
            JsonSerializer.Serialize(calibration, Seein.Core.JsonDefaults.Options), Seein.Core.JsonDefaults.Options)!;
        Assert.Null(loaded.ValidateSamples());
        foreach (var sample in calibration.Samples)
            Assert.Equal(sample.Output!.Value, loaded.Correct(sample.Target!.Value));
        foreach (var hex in new[] { "000000", "0000FF", "00FF00", "FF0000", "FFFF00", "00FFFF", "FF00FF", "FFFFFF", "BB72FF" })
            Assert.Equal(Legacy.Correct(Color(hex)), loaded.Correct(Color(hex)));
        for (var shade = 0; shade <= 255; shade++)
        {
            var gray = new RgbColor((byte)shade, (byte)shade, (byte)shade);
            Assert.Equal(Legacy.Correct(gray), loaded.Correct(gray));
        }
        Assert.Equal(Legacy.InteriorPoint, loaded.InteriorPoint);
    }

    [Fact]
    public void CorrectionBlendsWithNeighboursAndDisappearsOutsideItsRadius()
    {
        var calibration = new RgbColorCalibration(Samples: [Sample("506460", "648C5A")]);
        var near = calibration.Correct(Color("516460"));
        Assert.InRange((int)near.R, 99, 103);
        Assert.InRange((int)near.G, 138, 142);
        Assert.InRange((int)near.B, 88, 92);
        Assert.Equal(Color("E06460"), calibration.Correct(Color("E06460")));
        Assert.Equal(Color("E16460"), calibration.Correct(Color("E16460")));
    }

    [Fact]
    public void MeasuredGradientStaysBetweenAdjacentDriveColorsAcrossQuantizedFrames()
    {
        RgbCalibrationSample[] ramp = [
            Sample("0067C0", "000DCC"), Sample("405AAA", "0613BD"), Sample("804D93", "1719BE"),
            Sample("BF407C", "2713ED"), Sample("FF3366", "3F10FF")];
        var calibration = Legacy with { Samples = [
            Sample("2892FF", "0945FF"), ramp[3], ramp[1], Sample("26DBFF", "096FFF"),
            ramp[4], ramp[2], Sample("36F5B4", "0D95B4"), ramp[0]] };

        // Sample the same 8-bit colors generated in both directions by a two-color effect.
        for (var index = 0; index <= 255; index++)
        {
            var target = Mix(ramp[0].Target!.Value, ramp[^1].Target!.Value, index / 255d);
            var upper = Array.FindIndex(ramp, 1, s => s.Target!.Value.R >= target.R);
            var lowerTarget = ramp[upper - 1].Target!.Value.R;
            var t = (target.R - lowerTarget) / (double)(ramp[upper].Target!.Value.R - lowerTarget);
            var expected = Mix(ramp[upper - 1].Output!.Value, ramp[upper].Output!.Value, t);
            var actual = calibration.Correct(target);
            Assert.InRange(Math.Abs(expected.R - actual.R), 0, 2);
            Assert.InRange(Math.Abs(expected.G - actual.G), 0, 2);
            Assert.InRange(Math.Abs(expected.B - actual.B), 0, 2);
        }
        foreach (var sample in calibration.Samples)
            Assert.Equal(sample.Output!.Value, calibration.Correct(sample.Target!.Value));

        static RgbColor Mix(RgbColor from, RgbColor to, double t)
        {
            byte Channel(int a, int b) => (byte)Math.Round(a + (b - a) * t);
            return new(Channel(from.R, to.R), Channel(from.G, to.G), Channel(from.B, to.B));
        }
    }

    [Fact]
    public void NearbyRampCannotReplaceAnIndependentSampleOrGrayAxis()
    {
        var calibration = new RgbColorCalibration(Samples: [
            Sample("0080FF", "F00000"), Sample("4080BF", "F00000"), Sample("FF8000", "F00000"),
            Sample("607CA0", "005500")]);
        Assert.Equal(Color("005500"), calibration.Correct(Color("607CA0")));
        Assert.Equal(Color("808080"), calibration.Correct(Color("808080")));
        Assert.Equal(Color("181818"), calibration.Correct(Color("181818")));
        var nearGray = calibration.Correct(Color("80807F"));
        Assert.InRange((int)nearGray.R, 125, 131);
        Assert.InRange((int)nearGray.G, 125, 131);
        Assert.InRange((int)nearGray.B, 124, 130);
    }

    [Fact]
    public void BasicPaletteFitsAlongsideExistingMeasurementsAndResolvesEverySample()
    {
        var palette = RgbBasicPalette.Colors.Distinct().ToArray();
        Assert.Equal(48, RgbBasicPalette.Colors.Count);
        Assert.Equal(47, palette.Length);
        // Synthetic device response; production samples are measured separately.
        var measured = palette
            .Where(color => !(color.R is 0 or 255 && color.G is 0 or 255 && color.B is 0 or 255))
            .Select(color => new RgbCalibrationSample(color, color.Scale(90)))
            .Concat(Enumerable.Range(1, 32).Select(i => new RgbCalibrationSample(
                new((byte)i, 80, 112), new((byte)i, 45, 198))))
            .ToArray();
        var calibration = Legacy with { Samples = measured };
        Assert.Null(calibration.ValidateSamples());
        foreach (var point in measured)
            Assert.Equal(point.Output!.Value, calibration.Correct(point.Target!.Value));
        Assert.Equal(Color("000000"), calibration.Correct(Color("000000")));
        Assert.Equal(Legacy.Correct(Color("FFFFFF")), calibration.Correct(Color("FFFFFF")));
        Assert.Equal(Legacy.Correct(Color("BB72FF")), calibration.Correct(Color("BB72FF")));
        Assert.InRange((int)calibration.Correct(Color("818181")).B, 115, 117);
    }

    [Fact]
    public void InvalidOrExcessiveSamplesCannotChangeLegacyColors()
    {
        var sample = Sample("804D93", "5410A6");
        IReadOnlyList<RgbCalibrationSample>[] invalid = [
            [sample, sample], [new()], [null!], [Sample("FFFFFF", "FF0000")],
            [Sample("BB72FF", "FF00FF")], Enumerable.Repeat(sample, RgbColorCalibration.MaxSamples + 1).ToArray()];
        foreach (var samples in invalid)
        {
            var calibration = Legacy with { Samples = samples };
            Assert.NotNull(calibration.ValidateSamples());
            Assert.Equal(Legacy.Correct(sample.Target!.Value), calibration.Correct(sample.Target.Value));
        }
    }
}
