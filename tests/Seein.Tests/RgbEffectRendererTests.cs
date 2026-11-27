using Seein.Core.Lighting;
using Microsoft.Extensions.Logging.Abstractions;
using Seein.Service;

namespace Seein.Tests;

public sealed class RgbEffectRendererTests
{
    [Theory]
    [InlineData("#000000", "#000000")]
    [InlineData("#FF0000", "#FF0000")]
    [InlineData("#00FF00", "#00FF00")]
    [InlineData("#0000FF", "#0000FF")]
    [InlineData("#FFFF00", "#FFFF00")]
    [InlineData("#FF00FF", "#FF00FF")]
    [InlineData("#FFFFFF", "#3C3CFF")]
    [InlineData("#808080", "#1E1E80")]
    [InlineData("#00FFFF", "#0090FF")]
    [InlineData("#008080", "#004880")]
    [InlineData("#26DBFF", "#096FFF")]
    public void ColorCalibrationInterpolatesFromVisualAnchors(string input, string output)
    {
        var calibration = new RgbColorCalibration(White: RgbColor.FromHex("#3C3CFF"),
            Cyan: RgbColor.FromHex("#0090FF"));
        Assert.Equal(RgbColor.FromHex(output), calibration.Correct(RgbColor.FromHex(input)));
    }

    [Fact]
    public void EmptyColorCalibrationPreservesTheInput()
    {
        var calibration = new RgbColorCalibration();
        var color = new RgbColor(163, 40, 213);
        Assert.Equal(color, calibration.Correct(color));
    }

    [Theory]
    [InlineData("#BB72FF", "#1800FF")]
    [InlineData("#5D397F", "#0C007F")]
    [InlineData("#FFFFFF", "#3C3CFF")]
    [InlineData("#00FFFF", "#0090FF")]
    [InlineData("#FF00FF", "#FF00FF")]
    [InlineData("#26DBFF", "#096FFF")]
    public void InteriorColorPointKeepsTheCubeAnchorsAndOtherHues(string input, string output)
    {
        var calibration = new RgbColorCalibration(White: RgbColor.FromHex("#3C3CFF"),
            Cyan: RgbColor.FromHex("#0090FF"), InteriorPoint: new RgbCalibrationPoint(
                RgbColor.FromHex("#BB72FF"), RgbColor.FromHex("#1800FF")));
        Assert.Equal(RgbColor.FromHex(output), calibration.Correct(RgbColor.FromHex(input)));
    }

    [Fact]
    public void WaveHoldsTheAccentForOneThirdOfTheCycleWithBlendsOnBothSides()
    {
        var purple = RgbColor.FromHex("#FF00FF");
        var colors = new[] { RgbColor.FromHex("#FF0000"), RgbColor.FromHex("#00FF00"),
            RgbColor.FromHex("#00FFFF"), purple };
        var wave = new RgbEffectSettings(RgbEffects.Wave, colors, Speed: 0);
        var accent = new RgbCalibrationPoint(purple, purple, WaveHold: 0.66);
        RgbColor Sample(double phase, RgbCalibrationPoint? point) =>
            RgbEffectRenderer.Render(wave, 1, -phase / 0.2, waveAccent: point)[0];
        static bool HasPurpleHue(RgbColor color) => color.R == color.B && color.R > 0 && color.G == 0;

        var phases = Enumerable.Range(0, 100).Select(index => index / 100d).ToArray();
        Assert.InRange(phases.Count(phase => HasPurpleHue(Sample(phase, accent))), 32, 34);
        Assert.Single(phases, phase => HasPurpleHue(Sample(phase, null)));
        Assert.Equal(Sample(0.35, null), Sample(0.35, accent));
        Assert.True(Sample(0.54, accent).G > 0);
        Assert.True(Sample(0.96, accent).R > Sample(0.96, accent).B);
    }

    [Fact]
    public void StaticEffectRepeatsTheFirstColor()
    {
        var effect = new RgbEffectSettings(RgbEffects.Static, [new RgbColor(10, 20, 30)], Brightness: 50);
        var frame = RgbEffectRenderer.Render(effect, 6, 0);
        Assert.Equal(6, frame.Count);
        Assert.All(frame, color => Assert.Equal(new RgbColor(5, 10, 15), color));
    }

    [Fact]
    public void OffEffectIsBlack()
    {
        var frame = RgbEffectRenderer.Render(new RgbEffectSettings(RgbEffects.Off, []), 4, 12.5);
        Assert.All(frame, color => Assert.Equal(new RgbColor(0, 0, 0), color));
    }

    [Fact]
    public void SameTimeProducesSameFrame()
    {
        var effect = new RgbEffectSettings(RgbEffects.Rainbow, [], 60, 100);
        var first = RgbEffectRenderer.Render(effect, 20, 3.25);
        var second = RgbEffectRenderer.Render(effect, 20, 3.25);
        Assert.Equal(first, second);
    }

    [Fact]
    public void RainbowMovesOverTime()
    {
        var effect = new RgbEffectSettings(RgbEffects.Rainbow, [], 80, 100);
        var first = RgbEffectRenderer.Render(effect, 10, 0);
        var later = RgbEffectRenderer.Render(effect, 10, 2.5);
        Assert.NotEqual(first, later);
    }

    [Fact]
    public void BrightnessScalesColors()
    {
        var full = RgbEffectRenderer.Render(new RgbEffectSettings(RgbEffects.Static, [new RgbColor(200, 100, 50)], Brightness: 100), 2, 0);
        var half = RgbEffectRenderer.Render(new RgbEffectSettings(RgbEffects.Static, [new RgbColor(200, 100, 50)], Brightness: 50), 2, 0);
        Assert.Equal(new RgbColor(200, 100, 50), full[0]);
        Assert.Equal(new RgbColor(100, 50, 25), half[0]);
    }

    [Fact]
    public void TemperatureEffectInterpolatesBetweenColdAndHot()
    {
        var effect = new RgbEffectSettings(RgbEffects.Temperature,
            [new RgbColor(0, 0, 255), new RgbColor(255, 0, 0)], Brightness: 100);
        var cold = RgbEffectRenderer.Render(effect, 1, 0, 30)[0];
        var hot = RgbEffectRenderer.Render(effect, 1, 0, 95)[0];
        Assert.Equal(new RgbColor(0, 0, 255), cold);
        Assert.Equal(new RgbColor(255, 0, 0), hot);
    }

    [Fact]
    public void HueHelperStaysInsideByteRange()
    {
        foreach (var hue in new[] { 0d, 0.25, 0.5, 0.75, 1.5, -0.3 })
        {
            var color = RgbEffectRenderer.FromHue(hue);
            Assert.InRange(color.R, 0, 255);
            Assert.InRange(color.G, 0, 255);
            Assert.InRange(color.B, 0, 255);
        }
    }

    [Fact]
    public void ColorHexRoundTrip()
    {
        var color = RgbColor.FromHex("#12AB34");
        Assert.Equal(new RgbColor(0x12, 0xAB, 0x34), color);
        Assert.Equal("#12AB34", color.ToHex());
        Assert.Equal(new RgbColor(0x11, 0x22, 0x33), RgbColor.FromHex("#123"));
        Assert.Equal(new RgbColor(1, 2, 3), RgbColor.FromHex("not-a-color", new RgbColor(1, 2, 3)));
    }

    [Fact]
    public void NormalizeKeepsOnlyExistingProfiles()
    {
        var stored = new RgbSettings(true, "openrgb", 200, "missing", true,
            new[] { new RgbProfile("static", "静态", null, RgbEffectSettings.Default) });
        var normalized = RgbEffects.Normalize(stored);
        Assert.Equal("static", normalized.ActiveProfileId);
        Assert.Equal(40, normalized.Fps);
        Assert.Single(normalized.Profiles!);
    }

    [Fact]
    public async Task NamedGroupPresetRoundTripsAndLegacySelectionClearsGroupEffect()
    {
        using var service = new RgbService(NullLogger<RgbService>.Instance, new PanelState(),
            new RgbEngineHost(NullLogger<RgbEngineHost>.Instance));
        service.Initialize(null);
        var effect = RgbEffects.DefaultsFor(RgbEffects.Temperature) with
        {
            LowTemperature = 40, HighTemperature = 85, Brightness = 65
        };
        var saved = await service.HandleCommandAsync(new("saveGroupProfile", GroupEffect: effect, ProfileName: "夜间配置"), default);
        Assert.True(saved.Success);
        Assert.Null(service.Settings.GroupEffect);
        var preset = Assert.Single(service.Settings.Profiles!, item => item.WholeSystem);
        Assert.Equal("夜间配置", preset.Name);
        Assert.False((await service.HandleCommandAsync(new("saveGroupProfile", GroupEffect: effect, ProfileName: "夜间配置"), default)).Success);

        Assert.True((await service.HandleCommandAsync(new("loadProfile", ProfileId: preset.Id), default)).Success);
        var restored = service.Settings.GroupEffect!;
        Assert.Equal(effect.Colors, restored.Colors);
        Assert.Equal((40, 85, 65d), (restored.LowTemperature, restored.HighTemperature, restored.Brightness));
        var invalid = effect with { LowTemperature = 85, HighTemperature = 40 };
        Assert.False((await service.HandleCommandAsync(new("groupEffect", GroupEffect: invalid), default)).Success);
        Assert.Same(restored, service.Settings.GroupEffect);
        Assert.True((await service.HandleCommandAsync(new("loadProfile", ProfileId: RgbEffects.Static), default)).Success);
        Assert.Null(service.Settings.GroupEffect);
    }

    [Fact]
    public void GroupParametersAffectMarqueeDirectionAndFireColors()
    {
        var marquee = RgbEffects.DefaultsFor(RgbEffects.Marquee);
        var forward = RgbEffectRenderer.Render(marquee, 12, 1.5);
        var backward = RgbEffectRenderer.Render(marquee with { Direction = "backward" }, 12, 1.5);
        Assert.Equal(forward.Reverse().ToArray(), backward.ToArray());
        var fire = RgbEffects.DefaultsFor(RgbEffects.Fire);
        Assert.False(RgbEffectRenderer.Render(fire, 12, 2.4).SequenceEqual(RgbEffectRenderer.Render(fire, 12, 2.9)));
        Assert.All(RgbEffectRenderer.Render(fire with { Brightness = 0 }, 12, 2.9), color => Assert.Equal(default, color));
    }

    [Theory]
    [InlineData(RgbEffects.Breathing)]
    [InlineData(RgbEffects.Wave)]
    public void FourColorEffectsUseTheEntirePaletteAndPreserveLegacySingleColor(string id)
    {
        RgbColor[] colors = [new(255, 0, 0), new(0, 255, 0), new(0, 0, 255), new(200, 200, 200)];
        var effect = RgbEffects.DefaultsFor(id) with { Colors = colors, Speed = 50 };
        Assert.Equal(colors, RgbEffects.NormalizeGroupEffect(effect)!.Colors);
        var legacy = effect with { Colors = [colors[0]] };
        var normalized = RgbEffects.NormalizeGroupEffect(legacy)!;
        Assert.Equal(4, normalized.Colors.Count);
        Assert.All(normalized.Colors, color => Assert.Equal(colors[0], color));
        Assert.Equal(RgbEffectRenderer.Render(legacy, 10, 2), RgbEffectRenderer.Render(normalized, 10, 2));
        var rendered = Enumerable.Range(0, 80).SelectMany(t => RgbEffectRenderer.Render(effect, 40, t * .1)).ToArray();
        for (var index = 0; index < colors.Length; index++)
        {
            var edited = colors.ToArray();
            edited[index] = default;
            var different = Enumerable.Range(0, 80).SelectMany(t =>
                RgbEffectRenderer.Render(effect with { Colors = edited }, 40, t * .1));
            Assert.False(rendered.SequenceEqual(different), $"Palette slot {index + 1} did not affect the animation");
        }
    }

    [Fact]
    public void DefaultFourColorWaveStaysVisibleBetweenCrests()
    {
        var effect = RgbEffects.DefaultsFor(RgbEffects.Wave);
        var frames = Enumerable.Range(0, 40).SelectMany(t => RgbEffectRenderer.Render(effect, 50, t * .1));
        Assert.All(frames, color => Assert.True(Math.Max(color.R, Math.Max(color.G, color.B)) >= 70));
    }

    [Fact]
    public void GroupWaveKeepsExternalModeAndRendersTheWholePalettePerArgbZone()
    {
        var native = new RgbMode("Rainbow Wave", 1, 1, 10, 250, 0, 0, 0, 0, 60, 0, 0, 0, [], 0);
        var input = native with { Name = RgbEffects.ExternalControl, Value = 255, Flags = 0, Index = 1 };
        var gpu = new RgbDeviceInfo(0, "Sapphire Radeon RX 9070 XT Nitro+", "显卡", "Sapphire", [], [native, input],
            RgbEffects.ExternalControl, false, 1, DeviceKey: "gpu");
        var zones = new[]
        {
            new RgbZoneInfo(0, "JAF", RgbZoneType.Linear, 0, 0, 240, 0, 0),
            new RgbZoneInfo(1, "JARGB 1", RgbZoneType.Linear, 20, 0, 240, 0, 0),
            new RgbZoneInfo(2, "JARGB 2", RgbZoneType.Linear, 32, 0, 240, 0, 0)
        };
        var board = new RgbDeviceInfo(1, "MSI MYSTIC LIGHT", "主板", "MSI", zones, [], "Direct", true, 52,
            DeviceKey: "board");
        var group = RgbEffects.DefaultsFor(RgbEffects.Wave);
        var settings = RgbEffects.DefaultSettings();

        var automatic = RgbService.BuildGroupTargets([gpu, board], settings, group);
        Assert.Equal(input.Name, Assert.Single(automatic, target => target.DeviceKey == "gpu").HardwareMode);
        Assert.Equal(new int?[] { 1, 2 }, automatic.Where(target => target.DeviceKey == "board").Select(target => target.ZoneIndex));
        Assert.All(automatic.Where(target => target.DeviceKey == "board"), target => Assert.Equal(group.Colors, target.Effect.Colors));

        var restored = settings with { ExternalControlPreferences = new Dictionary<string, bool> { ["gpu"] = true } };
        var afterRestart = RgbService.BuildGroupTargets([gpu with { ActiveMode = native.Name }, board], restored, group);
        Assert.Equal(input.Name, Assert.Single(afterRestart, target => target.DeviceKey == "gpu").HardwareMode);

        var oldSettings = settings with { ExternalControlPreferences = new Dictionary<string, bool> { ["gpu"] = false } };
        foreach (var option in RgbEffects.Options)
        {
            var effect = RgbEffects.DefaultsFor(option.Id);
            var targets = RgbService.BuildGroupTargets([gpu with { ActiveMode = native.Name }, board], oldSettings, effect);
            var receiver = Assert.Single(targets, target => target.DeviceKey == "gpu");
            Assert.Equal(input.Name, receiver.HardwareMode);
            Assert.Empty(receiver.Effect.Colors);
            Assert.Equal(new int?[] { 1, 2 }, targets.Where(target => target.DeviceKey == "board").Select(target => target.ZoneIndex));
            Assert.All(targets.Where(target => target.DeviceKey == "board"), target => Assert.Equal(effect, target.Effect));
        }

        var missingInput = gpu with { ActiveMode = native.Name, Modes = [native] };
        var missingPort = RgbService.BuildGroupTargets([missingInput, board], oldSettings, group);
        Assert.DoesNotContain(missingPort, target => target.DeviceKey == "gpu");
        Assert.Equal(new int?[] { 1, 2 }, missingPort.Where(target => target.DeviceKey == "board").Select(target => target.ZoneIndex));

        var independent = missingInput with { Name = "Sapphire Radeon RX 9070 XT Pulse" };
        var separate = RgbService.BuildGroupTargets([independent, board], oldSettings, group);
        Assert.Equal(native.Name, Assert.Single(separate, target => target.DeviceKey == "gpu").HardwareMode);
        Assert.Null(Assert.Single(separate, target => target.DeviceKey == "board").ZoneIndex);
    }
}
