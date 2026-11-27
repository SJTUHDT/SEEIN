using Seein.Core.Lighting;

namespace Seein.Tests;

public class MysticLightEffectCatalogTests
{
    [Theory]
    [InlineData(RgbEffects.Off, 0)]
    [InlineData(RgbEffects.Wave, 1)]
    [InlineData(RgbEffects.Static, 2)]
    [InlineData(RgbEffects.Fire, 3)]
    [InlineData(RgbEffects.Breathing, 4)]
    [InlineData(RgbEffects.ColorRing, 5)]
    [InlineData(RgbEffects.Lightning, 6)]
    [InlineData(RgbEffects.Music, 7)]
    [InlineData(RgbEffects.Meteor, 8)]
    [InlineData(RgbEffects.BoardDefault, 1)]
    public void MapsFirmwareModesIndependentlyOfTheUiIndex(string id, int expected)
    {
        Assert.True(MsiMysticLightEffects.TryGetMode(id, out var mode));
        Assert.Equal((byte)expected, mode);
        Assert.Contains(RgbEffects.All, item => item.Id == id);
    }

    [Theory]
    [InlineData(RgbEffects.ColorRing)]
    [InlineData(RgbEffects.Meteor)]
    public void NewSpatialEffectsRespectThePaletteDirectionAndBrightness(string id)
    {
        RgbColor red = new(230, 0, 0);
        var effect = RgbEffects.DefaultsFor(id) with { Colors = [red, red, red, red] };
        var first = RgbEffectRenderer.Render(effect, 40, 0.8);
        Assert.Contains(first, p => p.R > 100);
        Assert.All(first, p => Assert.Equal((0, 0), ((int)p.G, (int)p.B)));
        var reversed = RgbEffectRenderer.Render(effect with { Direction = "backward" }, 40, 0.8);
        Assert.Equal(first.Reverse().ToArray(), reversed.ToArray());
        var rainbow = effect with { UseRainbowPalette = true };
        Assert.True(MsiMysticLightEffects.UsesRainbowPalette(rainbow));
        Assert.Contains(RgbEffectRenderer.Render(rainbow, 40, 0.8), p => p.G > 40);
        Assert.All(RgbEffectRenderer.Render(effect with { Brightness = 0 }, 40, 0.8),
            p => Assert.Equal(default, p));
    }

    [Fact]
    public void MusicRequiresPlaybackAndGetsBrighterWithItsPeak()
    {
        var effect = new RgbEffectSettings(RgbEffects.Music, [new(200, 100, 0)], Brightness: 50);
        Assert.Equal(default, RgbEffectRenderer.Render(effect, 1, 100)[0]);
        Assert.Equal(default, RgbEffectRenderer.Render(effect, 1, 100, audioLevel: 0)[0]);
        Assert.Equal(new RgbColor(25, 12, 0), RgbEffectRenderer.Render(effect, 1, 100, audioLevel: 0.25)[0]);
        Assert.Equal(new RgbColor(100, 50, 0), RgbEffectRenderer.Render(effect, 1, 100, audioLevel: 1)[0]);
    }

    [Fact]
    public void BoardDefaultUsesTheFirmwareFixedSpeedAndBrightness()
    {
        var changed = new RgbEffectSettings(RgbEffects.BoardDefault, [], Speed: 80, Brightness: 35);
        var normalized = RgbEffects.NormalizeGroupEffect(changed)!;
        Assert.Equal(0, normalized.Speed);
        Assert.Equal(100, normalized.Brightness);
        Assert.True(MsiMysticLightEffects.UsesRainbowPalette(normalized));
    }

    [Fact]
    public void OlderPresetsRetainCalibrationAndExplicitOptOutRoundTrips()
    {
        var options = new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web);
        var old = System.Text.Json.JsonSerializer.Deserialize<RgbEffectSettings>(
            """{"effectId":"wave","colors":[]}""", options)!;
        Assert.True(RgbEffects.NormalizeGroupEffect(old)!.UseZoneCalibration);
        var saved = System.Text.Json.JsonSerializer.Serialize(old with { UseZoneCalibration = false }, options);
        var loaded = System.Text.Json.JsonSerializer.Deserialize<RgbEffectSettings>(saved, options)!;
        Assert.False(RgbEffects.NormalizeGroupEffect(loaded)!.UseZoneCalibration);
    }

    [Fact]
    public void TemperaturePreservesLegacyTwoStopPresetsAndHonorsAnEditedMiddleColor()
    {
        var old = new RgbEffectSettings(RgbEffects.Temperature, [new(0, 101, 0), new(241, 0, 0)],
            LowTemperature: 30, HighTemperature: 90);
        var migrated = RgbEffects.NormalizeGroupEffect(old)!;
        Assert.Equal(new RgbColor(120, 50, 0), migrated.Colors[1]);
        foreach (var temperature in Enumerable.Range(30, 61))
            Assert.Equal(RgbEffectRenderer.Render(old, 1, 0, temperature)[0],
                RgbEffectRenderer.Render(migrated, 1, 0, temperature)[0]);
        var edited = migrated with { Colors = [migrated.Colors[0], new(0, 0, 255), migrated.Colors[2]] };
        Assert.Equal(new RgbColor(0, 0, 255), RgbEffectRenderer.Render(edited, 1, 0, 60)[0]);
    }
}
