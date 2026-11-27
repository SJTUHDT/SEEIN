using Seein.Core.Lighting;
using Seein.Service;

namespace Seein.Tests;

public sealed class MsiMysticLightBoardTests
{
    private static readonly RgbColor[] TestColors = [new(255, 0, 0), new(0, 255, 0), new(0, 0, 255), new(255, 255, 255)];
    private static RgbEffectSettings Wave => new(RgbEffects.Wave, TestColors);

    [Fact]
    public void GroupWaveReproducesTheVendorReadbackAndPreservesUnrelatedAreas()
    {
        var initial = InitialReport();
        var original = initial.ToArray();
        var result = GroupReport(initial, Wave, null);
        Assert.Equal(original, initial);
        Assert.Equal("01FF000000FF000000FFFFFFFF03B500", Row(result, 17));
        foreach (int index in new[] { 0, 1, 2, 3 })
            Assert.Equal("0100000000000000000000000003941E", Row(result, index));
        Assert.Equal("01000000000000000000000000039400", Row(result, 9));
        Assert.Equal(Row(original, 8), Row(result, 8));
        Assert.Equal(0, result[^1]);
    }

    [Fact]
    public void CalibratedGpuFollowsTheGroupWhileCaseOutputsKeepTheRequestedColors()
    {
        var corrected = new RgbColor[] { TestColors[0], TestColors[1], TestColors[2], new(60, 60, 255) };
        var effect = Wave with { Direction = "backward", Speed = 100, Brightness = 60 };
        var result = GroupReport(InitialReport(), effect, corrected);
        Assert.Equal("0100000000000000000000000003941E", Row(result, 1));
        Assert.Equal("01FF000000FF000000FF3C3CFF03EE00", Row(result, 17));
        foreach (var index in new[] { 0, 2, 3 })
            Assert.Equal("01FF000000FF000000FFFFFFFF036E1E", Row(result, index));
        Assert.Equal("01FF000000FF000000FFFFFFFF036E00", Row(result, 9));
        var reset = GroupReport(result, Wave, null);
        foreach (var index in new[] { 0, 1, 2, 3 })
            Assert.Equal("0100000000000000000000000003941E", Row(reset, index));
    }

    [Fact]
    public void DirectModeRestoresPhysicalCountsByZoneNameAndClearsGroupFollow()
    {
        var current = GroupReport(InitialReport(), Wave, null);
        var zones = new[] { Zone(0, "JAF", 0), Zone(2, "JARGB 2", 32), Zone(1, "JARGB 1", 20), Zone(3, "JARGB 3", 20) };
        var result = MsiMysticLightBoard.BuildDirectReport(current, zones);
        Assert.Equal("09FF000000FF000000FFFFFFFF031520", Row(result, 1));
        Assert.Equal(20, result[16]);
        Assert.Equal(20, result[48]);
        Assert.Equal(0, result[64]);
        Assert.Equal("09FF000000FF000000FFFFFFFF03951E", Row(result, 17));
        Assert.Equal(Row(current, 8), Row(result, 8));
        Assert.Throws<ArgumentException>(() => MsiMysticLightBoard.BuildDirectReport(current, zones[..3]));
        Assert.Throws<ArgumentException>(() => MsiMysticLightBoard.BuildDirectReport(current, [.. zones, zones[0]]));
        Assert.Throws<ArgumentException>(() => MsiMysticLightBoard.BuildDirectReport(current, [.. zones[..3], Zone(3, "JARGB 3", 241)]));
    }

    [Fact]
    public void FirmwareRainbowDoesNotUseCalibratedPalette()
    {
        var effect = Wave with { UseRainbowPalette = true };
        var result = GroupReport(InitialReport(), effect, [new(1, 2, 3), .. TestColors.Skip(1)]);
        Assert.Equal(0, result[273 + 14] & 0x20);
        Assert.Equal(0x94, result[17 + 14]);
    }

    [Fact]
    public void SingleColorBreathingUsesOneSlotAndMusicUsesAnOutputReport()
    {
        var effect = new RgbEffectSettings(RgbEffects.Breathing, [new(255, 0, 0)]);
        var result = GroupReport(InitialReport(), effect, null);
        Assert.Equal("04FF000000000000000000000000B500", Row(result, 17));
        Assert.Throws<ArgumentException>(() => GroupReport(InitialReport(), effect with { Colors = TestColors }, null));
        var audio = MsiMysticLightBoard.BuildAudioPacket(105, 10, 95);
        Assert.Equal(64, audio.Length);
        Assert.Equal(new byte[] { 1, 0xC0, 0, 100, 10, 95 }, audio[..6]);
        Assert.All(audio[6..], value => Assert.Equal(0, value));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WaveRepairsZeroAndStaleLengthsWithoutChangingGpuCalibration(bool calibrated)
    {
        var initial = InitialReport(); initial[16] = 206; initial[32] = 32; initial[48] = 0;
        var zones = new[] { Zone(3,"JARGB 3",68), Zone(0,"JAF",0), Zone(2,"JARGB 2",32), Zone(1,"JARGB 1",152) };
        var corrected = TestColors.Select(c => c.Scale(50)).ToArray();
        var group = MsiMysticLightBoard.BuildGroupReport(initial, Wave, calibrated ? corrected : null, zones);
        AssertCounts(group); Assert.Equal(0, group[288]); Assert.Equal(Row(initial,8),Row(group,8));
        Assert.Equal(0x94,group[31]); // GPU keeps following SELECT ALL, including its corrected palette.
        Assert.Equal((calibrated ? corrected : TestColors)[0].R, group[274]);
        var direct=MsiMysticLightBoard.BuildDirectReport(group,zones); AssertCounts(direct);
        var restored=MsiMysticLightBoard.BuildGroupReport(direct,Wave,calibrated ? corrected : null,zones);AssertCounts(restored);
        Assert.Equal(Row(group,0),Row(restored,0));Assert.Equal(Row(group,2),Row(restored,2));
        Assert.Throws<ArgumentException>(()=>MsiMysticLightBoard.BuildGroupReport(initial,Wave,null,zones[..3]));
        Assert.Throws<ArgumentException>(()=>MsiMysticLightBoard.BuildGroupReport(initial,Wave,null,[..zones,zones[0]]));
        static void AssertCounts(byte[] report){Assert.Equal(152,report[16]);Assert.Equal(32,report[32]);Assert.Equal(68,report[48]);Assert.Equal(0,report[64]);}
    }

    private static byte[] GroupReport(byte[] current,RgbEffectSettings effect,IReadOnlyList<RgbColor>? corrected) =>
        MsiMysticLightBoard.BuildGroupReport(current,effect,corrected,
            [Zone(0,"JAF",30),Zone(1,"JARGB 1",30),Zone(2,"JARGB 2",30),Zone(3,"JARGB 3",30)]);

    [Fact]
    public void HybridWaveOnlyReplacesRadiatorModeAndKeepsCalibratedGpuAndOtherOutputs()
    {
        var zones=new[]{Zone(0,"JAF",0),Zone(1,"JARGB 1",152),Zone(2,"JARGB 2",32),Zone(3,"JARGB 3",68)};
        var corrected=TestColors.Select(c=>c.Scale(50)).ToArray();
        var native=MsiMysticLightBoard.BuildGroupReport(InitialReport(),Wave,corrected,zones);
        var hybrid=MsiMysticLightBoard.BuildGroupReport(native,Wave,corrected,zones,3);
        foreach(var row in new[]{0,1,3,8,9,17})Assert.Equal(Row(native,row),Row(hybrid,row));
        Assert.Equal(9,hybrid[33]);Assert.Equal(0x15,hybrid[47]);Assert.Equal(68,hybrid[48]);Assert.Equal(0,hybrid[^1]);
        var restored=MsiMysticLightBoard.BuildGroupReport(hybrid,Wave,corrected,zones);
        Assert.Equal(Row(native,2),Row(restored,2));
        var frame=Enumerable.Range(0,68).Select(i=>new RgbColor((byte)i,7,9)).ToArray();
        var packet=MsiMysticLightBoard.BuildRingPacket(zones,3,frame);
        Assert.Equal(727,packet.Length);Assert.Equal(new byte[]{0x51,9,4,2,0,0,68},packet[..7]);
        Assert.Equal(new byte[]{0,7,9,1,7,9},packet[7..13]);Assert.All(packet[(7+68*3)..],b=>Assert.Equal(0,b));
        Assert.Throws<ArgumentException>(()=>MsiMysticLightBoard.BuildRingPacket(zones,2,frame));
        Assert.Throws<ArgumentException>(()=>MsiMysticLightBoard.BuildRingPacket(zones,3,frame[..67]));
        Assert.False(MsiMysticLightBoard.IsRingZone([..zones,zones[3]],3));
        Assert.Throws<ArgumentException>(()=>MsiMysticLightBoard.BuildGroupReport(native,Wave with{EffectId=RgbEffects.Static},null,zones,3));
    }

    private static byte[] InitialReport()
    {
        var bytes = Enumerable.Repeat((byte)0xA5, 290).ToArray();
        bytes[0] = 0x50;
        for (int i = 0; i < 4; i++) bytes[16 + 16 * i] = 30;
        bytes[16 + 16 * 9] = 0;
        return bytes;
    }

    private static string Row(byte[] data, int number) => Convert.ToHexString(data, 1 + number * 16, 16);

    private static RgbZoneInfo Zone(int index, string name, int count) =>
        new(index, name, RgbZoneType.Linear, count, 0, 240, 0, 0);
}
