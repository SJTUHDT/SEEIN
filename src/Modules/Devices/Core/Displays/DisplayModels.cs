using System.Globalization;

namespace Seein.Core.Displays;

public enum DisplayFeature : byte
{
    Brightness = 0x10, Contrast = 0x12, ColorPreset = 0x14,
    RedGain = 0x16, GreenGain = 0x18, BlueGain = 0x1A,
    Input = 0x60, Volume = 0x62, Sharpness = 0x87, Mute = 0x8D,
    Power = 0xD6, PictureMode = 0xDC
}

public sealed record DisplayTarget(string Id, string Name, string Connection);
public sealed record DisplaySetting(DisplayFeature Feature, string Label, bool Supported,
    uint Current = 0, uint Maximum = 100, IReadOnlyDictionary<uint, string>? Options = null, string? Error = null);
public sealed record DisplayRequest(string Operation, string? TargetId = null,
    DisplayFeature? Feature = null, uint? Value = null, string? RequestId = null);
public sealed record DisplayResponse(string? RequestId, bool Success, DisplayTarget[]? Targets = null,
    DisplaySetting[]? Settings = null, string? Error = null, bool Sent = false);

public static class DisplayFeatures
{
    public static readonly IReadOnlyDictionary<DisplayFeature, string> Labels = new Dictionary<DisplayFeature, string>
    {
        [DisplayFeature.Brightness] = "亮度", [DisplayFeature.Contrast] = "对比度",
        [DisplayFeature.ColorPreset] = "色温 / 颜色预设", [DisplayFeature.RedGain] = "红色增益",
        [DisplayFeature.GreenGain] = "绿色增益", [DisplayFeature.BlueGain] = "蓝色增益",
        [DisplayFeature.Input] = "输入源", [DisplayFeature.Volume] = "显示器音量",
        [DisplayFeature.Sharpness] = "锐利度", [DisplayFeature.Mute] = "静音",
        [DisplayFeature.Power] = "电源状态", [DisplayFeature.PictureMode] = "显示模式"
    };

    public static bool IsChoice(DisplayFeature feature) => feature is DisplayFeature.Input or
        DisplayFeature.ColorPreset or DisplayFeature.PictureMode or DisplayFeature.Power or DisplayFeature.Mute;
    public static bool RequiresExplicitAction(DisplayFeature feature) => feature is DisplayFeature.Input or DisplayFeature.Power;

    public static string OptionLabel(DisplayFeature feature, uint value) => feature switch
    {
        DisplayFeature.Input => value switch { 0x0F => "DP 1", 0x10 => "DP 2", 0x11 => "HDMI 1", 0x12 => "HDMI 2", _ => $"输入 0x{value:X2}" },
        DisplayFeature.ColorPreset => value switch { 1 => "sRGB", 2 => "显示器原生", 3 => "4000 K", 4 => "5000 K", 5 => "6500 K", 6 => "7500 K", 7 => "8200 K", 8 => "9300 K", 9 => "10000 K", 10 => "11500 K", 11 => "用户预设 1", 12 => "用户预设 2", _ => $"预设 0x{value:X2}" },
        DisplayFeature.Power => value switch { 1 => "开启", 2 => "待机", 3 => "休眠", 4 => "关闭", 5 => "关闭（控制键）", _ => $"电源模式 {value}" },
        DisplayFeature.Mute => value switch { 1 => "静音", 2 => "取消静音", _ => $"状态 {value}" },
        DisplayFeature.PictureMode => value switch { 0 => "标准", 1 => "生产力", 2 => "混合", 3 => "电影", 4 => "用户", 5 => "游戏", 6 => "体育", _ => $"模式 0x{value:X2}" },
        _ => value.ToString(CultureInfo.InvariantCulture)
    };

    public static bool IsValidValue(DisplaySetting setting, uint value) => setting.Supported &&
        (IsChoice(setting.Feature) ? setting.Options?.ContainsKey(value) == true : value <= setting.Maximum);
}

/// <summary>Parse only the vcp section; command and vendor sections must never become writable features.</summary>
public static class DisplayCapabilities
{
    public static IReadOnlyDictionary<byte, uint[]> Parse(string text)
    {
        var result = new Dictionary<byte, uint[]>();
        var start = text.IndexOf("vcp(", StringComparison.OrdinalIgnoreCase);
        if (start < 0) return result;
        var i = start + 4;
        while (i < text.Length)
        {
            while (i < text.Length && char.IsWhiteSpace(text[i])) i++;
            if (i >= text.Length || text[i] == ')') break;
            var token = ReadToken(text, ref i);
            if (!byte.TryParse(token, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var code)) break;
            while (i < text.Length && char.IsWhiteSpace(text[i])) i++;
            var values = new List<uint>();
            if (i < text.Length && text[i] == '(')
            {
                i++;
                while (i < text.Length && text[i] != ')')
                {
                    while (i < text.Length && char.IsWhiteSpace(text[i])) i++;
                    if (i >= text.Length || text[i] == ')') break;
                    var value = ReadToken(text, ref i);
                    if (!uint.TryParse(value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var parsed)) return result;
                    values.Add(parsed);
                }
                if (i >= text.Length) return result; // Unterminated values are not trusted.
                i++;
            }
            result[code] = values.Distinct().ToArray();
        }
        return result;
    }

    private static string ReadToken(string text, ref int i)
    {
        var start = i;
        while (i < text.Length && Uri.IsHexDigit(text[i])) i++;
        return text[start..i];
    }
}
