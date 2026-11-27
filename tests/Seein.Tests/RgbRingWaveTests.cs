using Seein.Core.Lighting;

namespace Seein.Tests;

public sealed class RgbRingWaveTests
{
    private static RgbEffectSettings Effect => new(RgbEffects.Wave,
        [new(40,146,255),new(38,219,255),new(54,245,180),new(187,114,255)],Speed:34);

    [Fact] public void SeamIsOneNormalPixelStepAtEveryPhase()
    {
        for(var t=0d;t<12;t+=.07)
        {
            var frame=RgbRingWaveRenderer.Render(Effect,68,t);
            Assert.Equal(68,frame.Count);
            for(int i=0;i<68;i++)Assert.InRange(Distance(frame[i],frame[(i+1)%68]),0,12);
        }
    }
    [Fact] public void SingleColourDoesNotFadeOrInventDarkGaps()
    {
        var colour=new RgbColor(80,160,240);
        var effect=Effect with{Colors=[colour],Brightness=50};
        foreach(var t in new[]{0d,.3,2,30})Assert.All(RgbRingWaveRenderer.Render(effect,68,t),c=>Assert.Equal(colour.Scale(50),c));
    }
    [Fact] public void OnePeriodAndDirectionPreserveTheSameLoop()
    {
        var period=MsiWaveTiming.CycleSeconds(Effect.Speed);
        var first=RgbRingWaveRenderer.Render(Effect,68,.25);
        var next=RgbRingWaveRenderer.Render(Effect,68,.25+period);
        var backwards=RgbRingWaveRenderer.Render(Effect with{Direction="backward"},68,-.25);
        for(int i=0;i<68;i++){Assert.InRange(Distance(first[i],next[i]),0,1);Assert.Equal(first[i],backwards[i]);}
        Assert.Empty(RgbRingWaveRenderer.Render(Effect,0,0));
    }
    private static int Distance(RgbColor a,RgbColor b)=>Math.Max(Math.Abs(a.R-b.R),Math.Max(Math.Abs(a.G-b.G),Math.Abs(a.B-b.B)));

    [Theory]
    [InlineData(0,0,7)]
    [InlineData(24,0,7)]
    [InlineData(25,1,3.5)]
    [InlineData(34,1,3.5)]
    [InlineData(74,1,3.5)]
    [InlineData(75,2,1.75)]
    [InlineData(100,2,1.75)]
    public void RingAndFirmwareUseTheSameSpeedBands(double speed,int level,double period)
    {
        Assert.Equal(level,MsiWaveTiming.NativeSpeedLevel(speed));Assert.Equal(period,MsiWaveTiming.CycleSeconds(speed));
    }

    [Fact] public void CurrentSpeedCompletesOneMeasuredCycleWithoutRepeatingEarly()
    {
        var start=RgbRingWaveRenderer.Render(Effect,68,0);
        var oldPeriod=RgbRingWaveRenderer.Render(Effect,68,1/.676);
        var measuredPeriod=RgbRingWaveRenderer.Render(Effect,68,3.5);
        Assert.Contains(Enumerable.Range(0,68),i=>Distance(start[i],oldPeriod[i])>30);
        for(int i=0;i<68;i++)Assert.InRange(Distance(start[i],measuredPeriod[i]),0,1);
        Assert.Equal(34,Effect.Speed);
    }
}
