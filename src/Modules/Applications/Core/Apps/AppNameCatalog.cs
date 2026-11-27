namespace Seein.Core.Apps;

/// <summary>
/// Maps an executable to the Chinese product name shown in the UI.  Windows ships English file
/// descriptions for most third party software (Weixin.exe is described as "Weixin"), so the table
/// wins; anything unknown falls back to the executable name without its extension.
/// </summary>
public static class AppNameCatalog
{
    private static readonly Dictionary<string, string> Names = new(StringComparer.OrdinalIgnoreCase)
    {
        ["weixin"] = "微信",
        ["wechatappex"] = "微信",
        ["wechat"] = "微信",
        ["qq"] = "QQ",
        ["ntqq"] = "QQ",
        ["quark"] = "夸克浏览器",
        ["steam"] = "Steam",
        ["steamwebhelper"] = "Steam",
        ["steamservice"] = "Steam 服务",
        ["wegame"] = "WeGame",
        ["rail"] = "WeGame",
        ["browser"] = "WeGame",
        ["wegame_env"] = "WeGame",
        ["upc"] = "育碧连接",
        ["uplaywebcore"] = "育碧连接",
        ["gameviewer"] = "网易UU远程",
        ["gameviewerserver"] = "网易UU远程",
        ["gameviewerhealthd"] = "网易UU远程",
        ["uuyc-mux"] = "网易UU远程",
        ["qqpctray"] = "腾讯电脑管家",
        ["qqpcrtp"] = "腾讯电脑管家",
        ["minihomepagepro"] = "腾讯电脑管家",
        ["qmlauncher"] = "腾讯电脑管家",
        ["qmfilesearchproxy64"] = "腾讯电脑管家",
        ["wallpaper64"] = "Wallpaper Engine",
        ["wallpaperservice64"] = "Wallpaper Engine",
        ["translucenttb"] = "任务栏透明",
        ["quicklook"] = "QuickLook 预览",
        ["dsh next"] = "DSH NEXT",
        ["workbuddyai"] = "WorkBuddy AI",
        ["agents anywhere"] = "Agents Anywhere",
        ["onedrive"] = "OneDrive",
        ["msedge"] = "Microsoft Edge",
        ["msedgewebview2"] = "Edge WebView",
        ["chrome"] = "Chrome",
        ["firefox"] = "Firefox",
        ["code"] = "Visual Studio Code",
        ["devenv"] = "Visual Studio",
        ["pycharm64"] = "PyCharm",
        ["idea64"] = "IntelliJ IDEA",
        ["notepad"] = "记事本",
        ["taskmgr"] = "任务管理器",
        ["powershell"] = "PowerShell",
        ["pwsh"] = "PowerShell 7",
        ["cmd"] = "命令提示符",
        ["explorer"] = "资源管理器",
        ["mstsc"] = "远程桌面",
        ["snippingtool"] = "截图工具",
        ["ollama"] = "Ollama",
        ["llama-server"] = "Llama 推理服务",
        ["python"] = "Python",
        ["pythonw"] = "Python",
        ["node"] = "Node.js",
        ["dotnet"] = ".NET 运行时",
        ["xboxpcappft"] = "Xbox",
        ["m365copilot"] = "Microsoft 365 Copilot",
        ["phoneexperiencehost"] = "手机连接",
        ["dcv2"] = "MSI Center",
        ["msi_central_service"] = "MSI Center",
        ["msi.terminalserver"] = "MSI Center",
        ["ledkeeper2"] = "LED 灯效服务",
        ["lightkeeperservice"] = "LED 灯效服务",
        ["ace-tray"] = "ACE 反作弊",
        ["amdrsserv"] = "AMD Software",
        ["radeonsoftware"] = "AMD Software",
        ["crashpad_handler"] = "崩溃上报",
        ["electron"] = "Electron 应用",
        ["dsh-pet"] = "DSH 桌宠",
        ["dsh"] = "DSH 服务",
        ["snowluma"] = "SnowLuma",
        ["tinydl"] = "WeGame",
        ["tinydl64"] = "WeGame",
        [".dsh"] = "DSH 数据目录",
        ["deltaforce"] = "三角洲行动",
        ["deltaforceclient"] = "三角洲行动",
        ["acodyssey"] = "刺客信条：奥德赛",
        ["assettocorsa"] = "神力科莎",
        ["cs2"] = "反恐精英 2",
        ["valorant"] = "无畏契约",
        ["genshinimpact"] = "原神",
        ["starrail"] = "崩坏：星穹铁道",
        ["leagueclient"] = "英雄联盟",
        ["tslgame"] = "绝地求生",
        ["destiny2"] = "命运 2",
        ["gta5"] = "侠盗猎车手 5",
        ["eldenring"] = "艾尔登法环",
        ["bg3"] = "博德之门 3",
        ["dota2"] = "Dota 2",
        ["rustclient"] = "Rust",
        ["rainbowsix"] = "彩虹六号：围攻",
        ["bincore"] = ".NET 编译服务",
        ["roslyn"] = ".NET 编译服务",
        ["msbuild"] = ".NET 编译服务"
    };

    public static string DisplayName(string? exePathOrName)
    {
        if (string.IsNullOrWhiteSpace(exePathOrName)) return "未知应用";
        if (ProcessNames.DisplayNameForPath(exePathOrName) is { } branded) return branded;
        // Works for executable paths ("...\Weixin.exe") and for folder based keys ("...\dsh-pet").
        var fileName = Path.GetFileName(exePathOrName.TrimEnd('\\', '/'));
        var stem = Path.GetFileNameWithoutExtension(fileName);
        if (string.IsNullOrWhiteSpace(stem)) stem = fileName;
        if (string.IsNullOrWhiteSpace(stem)) stem = exePathOrName;

        foreach (var candidate in Candidates(stem))
            if (Names.TryGetValue(candidate, out var name)) return name;
        return stem;
    }

    /// <summary>Unreal and vendor builds append technical suffixes; try the cleaned stems too.</summary>
    private static IEnumerable<string> Candidates(string stem)
    {
        yield return stem;
        var trimmed = stem;
        foreach (var suffix in new[] { "-Win64-Shipping", "-Win64", "-Win32-Shipping", "-Shipping", "_x64", "_Win64", "Client", "Launcher" })
        {
            if (trimmed.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                trimmed = trimmed[..^suffix.Length];
                yield return trimmed;
            }
        }
        foreach (var suffix in new[] { "-Win64-Shipping", "-Win64", "-Shipping" })
            if (stem.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                yield return stem[..^suffix.Length];
    }
}
