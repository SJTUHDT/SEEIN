namespace Seein.Core.Lighting;

/// <summary>A single periodic colour ribbon around an addressable closed perimeter.</summary>
public static class RgbRingWaveRenderer
{
    public static IReadOnlyList<RgbColor> Render(RgbEffectSettings effect, int count, double seconds)
    {
        if (count <= 0) return [];
        IReadOnlyList<RgbColor> palette = effect.UseRainbowPalette
            ? [new(255,0,0),new(255,255,0),new(0,255,0),new(0,255,255),new(0,0,255),new(255,0,255)]
            : effect.Colors.Count > 0 ? effect.Colors : [new(0,103,192)];
        var brightness=Math.Clamp(effect.Brightness,0,100);
        var rate=1/MsiWaveTiming.CycleSeconds(effect.Speed);
        var travel=seconds*rate*(effect.Direction=="backward"?-1:1);
        var frame=new RgbColor[count];
        for(var i=0;i<count;i++)
        {
            // i/count leaves one ordinary LED interval between the last and first pixels.
            // A strip's 1.5 spatial cycles and brightness envelope create artificial seams.
            var phase=(double)i/count-travel;phase-=Math.Floor(phase);
            var slot=phase*palette.Count;var index=(int)slot;
            frame[i]=RgbColor.Blend(palette[index],palette[(index+1)%palette.Count],slot-index).Scale(brightness);
        }
        return frame;
    }
}
