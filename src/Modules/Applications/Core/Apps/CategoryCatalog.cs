using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Seein.Core.Apps;

public sealed record CategoryRule(string Category, string Pattern);

/// <summary>
/// Category rules applied to the application name and key.  The rules live in a small JSON file next
/// to the settings so they can be edited without a rebuild; first run writes a sensible default set.
/// </summary>
public static class CategoryCatalog
{
    public static IReadOnlyList<CategoryRule> Default { get; } =
    [
        new("编程", @"code|devenv|pycharm|idea|rider|webstorm|sublime|vim|nvim|emacs|notepad\+\+|terminal|powershell|pwsh|cmd\.exe|node|dotnet|python|java|git|docker|dsh|visual studio|clion|goland|android studio"),
        new("游戏", @"steam|wegame|epic|battle\.net|uplay|ubisoft|origin|ea app|riot|game|shipping|unreal|unity|minecraft|valorant|deltaforce|xbox|gameviewer|网易uu"),
        new("社交", @"weixin|wechat|微信|qq|tim|dingtalk|钉钉|feishu|飞书|teams|discord|telegram|whatsapp|slack"),
        new("影音", @"vlc|potplayer|mpv|music|spotify|netease|cloudmusic|bilibili|iqiyi|youku|tencent video|爱奇艺|播放器"),
        new("浏览", @"chrome|edge|firefox|brave|opera|quark|夸克|360se|sogou|browser"),
        new("办公", @"winword|excel|powerpnt|outlook|onenote|wps|notion|obsidian|typora|acrobat|reader|office"),
        new("系统", @"explorer|taskmgr|settings|control|msi center|lightkeeper|ledkeeper|translucenttb|quicklook|wallpaper|nvidia|amd|razer|logi")
    ];

    public const string Fallback = "其他";

    public static IReadOnlyList<CategoryRule> Load(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                Save(path, Default);
                return Default;
            }
            var rules = JsonSerializer.Deserialize<List<CategoryRule>>(File.ReadAllText(path), JsonDefaults.Options);
            return rules is { Count: > 0 } ? rules : Default;
        }
        catch { return Default; }
    }

    public static void Save(string path, IReadOnlyList<CategoryRule> rules)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(rules, JsonDefaults.Options));
        File.Move(temporary, path, true);
    }

    public static string Match(IReadOnlyList<CategoryRule> rules, string key, string name)
    {
        var haystack = name + " " + key;
        foreach (var rule in rules)
        {
            if (string.IsNullOrWhiteSpace(rule.Pattern)) continue;
            try
            {
                if (Regex.IsMatch(haystack, rule.Pattern, RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(50)))
                    return rule.Category;
            }
            catch (ArgumentException) { }
            catch (RegexMatchTimeoutException) { }
        }
        return Fallback;
    }

    /// <summary>Stable colour per category, used by the gantt chart and the category donut.</summary>
    public static uint ColorOf(string category) => category switch
    {
        "编程" => 0xFF0067C0,
        "游戏" => 0xFF8B5CF6,
        "社交" => 0xFF0EA5A0,
        "影音" => 0xFFC239B3,
        "浏览" => 0xFF2B88D8,
        "办公" => 0xFFC18500,
        "系统" => 0xFF6B7280,
        _ => 0xFF9CA3AF
    };

    public static string Format(double seconds)
    {
        if (!double.IsFinite(seconds) || seconds <= 0) return "0 分钟";
        var span = TimeSpan.FromSeconds(seconds);
        return span.TotalHours >= 1
            ? string.Create(CultureInfo.InvariantCulture, $"{(int)span.TotalHours} 小时 {span.Minutes:00} 分")
            : string.Create(CultureInfo.InvariantCulture, $"{Math.Max(1, (int)span.TotalMinutes)} 分钟");
    }
}
