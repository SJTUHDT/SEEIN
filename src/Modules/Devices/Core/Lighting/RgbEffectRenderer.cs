namespace Seein.Core.Lighting;

/// <summary>
/// Turns an effect definition into one colour per LED. Pure and time-driven, so the same input always
/// produces the same frame and the output can be asserted in tests.
/// </summary>
public static class RgbEffectRenderer
{
    public static IReadOnlyList<RgbColor> Render(RgbEffectSettings effect, int ledCount,
        double timeSeconds, double? temperatureC = null, RgbColor? fallback = null,
        RgbCalibrationPoint? waveAccent = null, double? audioLevel = null)
    {
        var count = Math.Max(0, ledCount);
        if (count == 0) return [];
        var baseColor = effect.Colors is { Count: > 0 } colors ? colors[0] : fallback ?? new RgbColor(0x00, 0x67, 0xC0);
        var brightness = Math.Clamp(effect.Brightness, 0, 100);
        var speed = Math.Clamp(effect.Speed, 0, 100) / 100d;

        return effect.EffectId switch
        {
            RgbEffects.Off => Enumerable.Repeat(new RgbColor(0, 0, 0), count).ToArray(),
            RgbEffects.Static => Enumerable.Repeat(baseColor.Scale(brightness), count).ToArray(),
            RgbEffects.Breathing => RenderBreathing(effect, baseColor, brightness, speed, timeSeconds, count),
            RgbEffects.Rainbow => RenderRainbow(brightness, speed, timeSeconds, count, effect.Direction),
            RgbEffects.BoardDefault => RenderRainbow(100, 0, timeSeconds, count, "forward"),
            RgbEffects.ColorCycle => RenderColorCycle(effect, brightness, speed, timeSeconds, count, baseColor),
            RgbEffects.ColorRing or RgbEffects.Meteor =>
                RenderRingOrMeteor(effect, brightness, speed, timeSeconds, count, baseColor),
            RgbEffects.Wave => RenderWave(effect, brightness, speed, timeSeconds, count, baseColor, waveAccent),
            RgbEffects.Marquee => RenderMarquee(effect, brightness, speed, timeSeconds, count, baseColor),
            RgbEffects.Lightning => RenderLightning(effect, brightness, speed, timeSeconds, count, baseColor),
            RgbEffects.Temperature => RenderTemperature(effect, brightness, temperatureC, count, baseColor),
            RgbEffects.Fire => RenderFire(effect, brightness, speed, timeSeconds, count, baseColor),
            RgbEffects.Music => Enumerable.Repeat(baseColor.Scale(brightness *
                (audioLevel is double peak && double.IsFinite(peak) ? Math.Clamp(peak, 0, 1) : 0)), count).ToArray(),
            _ => Enumerable.Repeat(baseColor.Scale(brightness), count).ToArray()
        };
    }

    private static IReadOnlyList<RgbColor> RenderBreathing(RgbEffectSettings effect, RgbColor color, double brightness, double speed,
        double time, int count)
    {
        var rate = 0.12 + speed * 1.2;
        var phase = rate * time;
        var wave = 0.5 + 0.5 * Math.Sin(2 * Math.PI * phase);
        if (effect.Colors is { Count: > 1 } palette)
            color = SamplePalette(palette, (phase - 0.25) / palette.Count);
        var level = brightness * (0.2 + 0.8 * wave);
        return Enumerable.Repeat(color.Scale(level), count).ToArray();
    }

    private static IReadOnlyList<RgbColor> RenderRainbow(double brightness, double speed, double time, int count,
        string direction)
    {
        var frame = new RgbColor[count];
        var reverse = string.Equals(direction, "backward", StringComparison.OrdinalIgnoreCase);
        var offset = time * (0.05 + speed * 0.6);
        for (var index = 0; index < count; index++)
        {
            var position = count == 1 ? 0 : (double)index / (count - 1);
            if (reverse) position = 1 - position;
            frame[index] = FromHue((position + offset) % 1d).Scale(brightness);
        }
        return frame;
    }

    private static IReadOnlyList<RgbColor> RenderColorCycle(RgbEffectSettings effect, double brightness,
        double speed, double time, int count, RgbColor fallback)
    {
        var palette = effect.Colors is { Count: > 0 } colors ? colors : [fallback];
        var rate = 0.05 + speed * 0.5;
        var position = (time * rate) % 1d * palette.Count;
        var index = (int)Math.Floor(position) % palette.Count;
        var next = (index + 1) % palette.Count;
        var blend = position - Math.Floor(position);
        var color = Blend(palette[index], palette[next], blend);
        return Enumerable.Repeat(color.Scale(brightness), count).ToArray();
    }

    private static IReadOnlyList<RgbColor> RenderWave(RgbEffectSettings effect, double brightness, double speed,
        double time, int count, RgbColor fallback, RgbCalibrationPoint? waveAccent)
    {
        var color = effect.Colors is { Count: > 0 } colors ? colors[0] : fallback;
        var palette = effect.Colors is { Count: > 1 } colorsInWave && colorsInWave.Any(entry => entry != color)
            ? colorsInWave : null;
        var frame = new RgbColor[count];
        var rate = 0.2 + speed * 1.4;
        var direction = effect.Direction == "backward" ? -1 : 1;
        var minimum = palette is null && !effect.UseRainbowPalette ? 0.15 : 0.55;
        var hold = waveAccent is { WaveHold: > 0 } ? Math.Min(waveAccent.WaveHold, 0.8) : 0;
        for (var index = 0; index < count; index++)
        {
            var position = count == 1 ? 0 : (double)index / (count - 1);
            var phase = position * 1.5 - time * rate * direction;
            var wave = 0.5 + 0.5 * Math.Sin(2 * Math.PI * phase);
            var led = effect.UseRainbowPalette ? FromHue(phase) :
                palette is not null ? SamplePalette(palette, phase, waveAccent?.Target, hold) : color;
            frame[index] = led.Scale(brightness * (minimum + (1 - minimum) * wave));
        }
        return frame;
    }

    private static IReadOnlyList<RgbColor> RenderRingOrMeteor(RgbEffectSettings effect, double brightness,
        double speed, double time, int count, RgbColor fallback)
    {
        var palette = effect.Colors is { Count: > 0 } colors ? colors : [fallback];
        var segments = effect.UseRainbowPalette ? 6 : palette.Count;
        var travel = time * (0.06 + 0.55 * speed);
        var frame = new RgbColor[count];
        for (var index = 0; index < count; index++)
        {
            var phase = travel - (double)index / count;
            var position = (phase - Math.Floor(phase)) * segments;
            var sector = Math.Min((int)position, segments - 1);
            var fraction = position - sector;
            var color = effect.UseRainbowPalette ? FromHue(phase) : palette[sector];
            var level = brightness;
            if (effect.EffectId == RgbEffects.Meteor)
                level *= Math.Pow(Math.Max(0, 1 - fraction / 0.75), 2);
            else if (!effect.UseRainbowPalette && fraction > 0.75)
                color = Blend(color, palette[(sector + 1) % segments], (fraction - 0.75) * 4);
            var output = effect.Direction == "backward" ? count - 1 - index : index;
            frame[output] = color.Scale(level);
        }
        return frame;
    }

    private static IReadOnlyList<RgbColor> RenderMarquee(RgbEffectSettings effect, double brightness, double speed,
        double time, int count, RgbColor fallback)
    {
        var color = effect.Colors is { Count: > 0 } colors ? colors[0] : fallback;
        var frame = new RgbColor[count];
        var head = (int)(Math.Floor(time * (2 + speed * 10)) % count);
        const int tail = 4;
        for (var index = 0; index < count; index++)
        {
            var distance = (head - index + count) % count;
            var output = effect.Direction == "backward" ? count - 1 - index : index;
            frame[output] = distance < tail
                ? color.Scale(brightness * (1 - (double)distance / tail))
                : new RgbColor(0, 0, 0);
        }
        return frame;
    }

    private static IReadOnlyList<RgbColor> RenderLightning(RgbEffectSettings effect, double brightness,
        double speed, double time, int count, RgbColor fallback)
    {
        var color = effect.Colors is { Count: > 0 } colors ? colors[0] : fallback;
        var bucket = (long)Math.Floor(time * (2 + speed * 10));
        var frame = new RgbColor[count];
        for (var index = 0; index < count; index++)
        {
            var seed = Hash(bucket, index);
            var flash = seed % 11 == 0 ? 0.55 + (seed % 45) / 100d : 0.06;
            frame[index] = color.Scale(brightness * flash);
        }
        return frame;
    }

    private static IReadOnlyList<RgbColor> RenderFire(RgbEffectSettings effect, double brightness,
        double speed, double time, int count, RgbColor fallback)
    {
        var cold = effect.Colors.Count > 0 ? effect.Colors[0] : fallback;
        var hot = effect.Colors.Count > 1 ? effect.Colors[1] : new RgbColor(255, 196, 70);
        var moment = time * (2 + speed * 8);
        var bucket = (long)Math.Floor(moment);
        var fraction = moment - bucket;
        var frame = new RgbColor[count];
        for (var index = 0; index < count; index++)
        {
            var from = Hash(bucket, index / 2) % 100 / 100d;
            var to = Hash(bucket + 1, index / 2) % 100 / 100d;
            var heat = from + (to - from) * fraction;
            frame[index] = Blend(cold, hot, heat).Scale(brightness * (0.35 + heat * 0.65));
        }
        return frame;
    }

    private static IReadOnlyList<RgbColor> RenderTemperature(RgbEffectSettings effect, double brightness,
        double? temperatureC, int count, RgbColor fallback)
    {
        var palette = effect.Colors is { Count: > 0 } colors ? colors : [fallback];
        var cold = palette[0];
        var hot = palette[^1];
        var low = effect.LowTemperature;
        var high = Math.Max(low + 1d, effect.HighTemperature);
        var temperature = Math.Clamp(temperatureC ?? 45, low, high);
        var ratio = (temperature - low) / (high - low);
        var hasCustomMidpoint = palette.Count >= 3 && palette[1] != Blend(cold, hot, 0.5);
        var color = !hasCustomMidpoint ? Blend(cold, hot, ratio) :
            ratio <= 0.5 ? Blend(cold, palette[1], ratio * 2) : Blend(palette[1], hot, (ratio - 0.5) * 2);
        return Enumerable.Repeat(color.Scale(brightness), count).ToArray();
    }

    private static long Hash(long bucket, int index)
    {
        var value = (ulong)(bucket * 6364136223846793005L + index * 1442695040888963407L);
        value ^= value >> 33;
        value *= 0xff51afd7ed558ccdUL;
        value ^= value >> 33;
        return (long)(value & 0x7FFFFFFF);
    }

    internal static RgbColor Blend(RgbColor from,RgbColor to,double ratio)=>RgbColor.Blend(from,to,ratio);

    private static RgbColor SamplePalette(IReadOnlyList<RgbColor> palette, double phase,
        RgbColor? heldColor = null, double hold = 0)
    {
        var position = (phase - Math.Floor(phase)) * palette.Count;
        var index = Math.Min((int)position, palette.Count - 1);
        var from = palette[index];
        var to = palette[(index + 1) % palette.Count];
        var blend = position - index;
        // Keep the accent on both sides of its color stop and compress only the two adjacent blends.
        if (hold > 0)
        {
            if (from == heldColor) blend = (blend - hold) / (1 - hold);
            else if (to == heldColor) blend /= 1 - hold;
        }
        return Blend(from, to, blend);
    }

    /// <summary>HSV hue (0..1) with full saturation and value.</summary>
    public static RgbColor FromHue(double hue)
    {
        var h = (hue % 1d + 1d) % 1d * 6d;
        var sector = (int)Math.Floor(h);
        var fraction = h - sector;
        return (sector % 6) switch
        {
            0 => new RgbColor(255, (byte)(fraction * 255), 0),
            1 => new RgbColor((byte)((1 - fraction) * 255), 255, 0),
            2 => new RgbColor(0, 255, (byte)(fraction * 255)),
            3 => new RgbColor(0, (byte)((1 - fraction) * 255), 255),
            4 => new RgbColor((byte)(fraction * 255), 0, 255),
            _ => new RgbColor(255, 0, (byte)((1 - fraction) * 255))
        };
    }
}
