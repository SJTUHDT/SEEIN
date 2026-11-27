namespace Seein.Core.Lighting;

/// <summary>Firmware mode byte in an MS-7E61 (MB800) 0x50 area record.</summary>
public static class MsiMysticLightEffects
{
    public static bool TryGetMode(string effectId, out byte mode)
    {
        mode = effectId switch
        {
            RgbEffects.Off => 0,
            RgbEffects.Wave or RgbEffects.Rainbow or RgbEffects.BoardDefault => 1,
            RgbEffects.Static => 2,
            RgbEffects.Fire => 3,
            RgbEffects.Breathing => 4,
            RgbEffects.ColorRing => 5,
            RgbEffects.Lightning => 6,
            RgbEffects.Music => 7,
            RgbEffects.Meteor => 8,
            _ => byte.MaxValue
        };
        return mode != byte.MaxValue;
    }

    public static bool UsesRainbowPalette(RgbEffectSettings effect) =>
        effect.EffectId is RgbEffects.Rainbow or RgbEffects.BoardDefault ||
        effect.UseRainbowPalette && effect.EffectId is (RgbEffects.Wave or RgbEffects.ColorRing or RgbEffects.Meteor);
}
