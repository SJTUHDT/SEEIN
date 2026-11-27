namespace Seein.Core.Tools;

public sealed record ToolSettingsCategory(string Id, string Name, string Description);

public static class ToolSettingsNavigation
{
    public static IReadOnlyList<ToolSettingsCategory> Categories(string module) => module == "pane" ?
    [new("general", "通用", "调整主题、字体、动画和玻璃效果。"),
     new("capabilities", "能力", "只开你要用的。停用后停止对应功能，并释放它的全局快捷键。"),
     new("launcher", "启动器", "管理搜索、常用项目和启动器的显示方式。"),
     new("clipboard", "剪贴板", "管理记录类型、容量与隐私规则。"),
     new("folders", "文件对话框", "在打开、另存为对话框中跳到常用文件夹。"),
     new("icons", "图标", "调整应用和文件图标的显示方式。"),
     new("about", "关于", "效率工具由 Pane 提供，设置由 SEEIN 统一管理。")]
    : [new("general", "通用", "调整界面与交互偏好。"),
       new("capture", "截图", "调整截图、标注和滚动截图。"),
       new("recording", "录屏", "调整录制画面、声音和编码。"),
       new("recognition", "识别与翻译", "离线文字识别，以及独立的截图翻译服务。"),
       new("storage", "导出与存储", "管理输出格式、保存路径和历史记录。"),
       new("about", "关于", "截图录屏由 Snow Shot 提供，设置由 SEEIN 统一管理。")];

    public static string CategoryOf(string module, string id)
    {
        if (module == "pane") return id.Split('.')[0] switch
        {
            "capability" => "capabilities", "Launcher" => "launcher", "Clipboard" => "clipboard",
            "FolderJump" => "folders", "Icons" => "icons", _ => "general"
        };
        if (id.StartsWith("text-recognition.") || id.StartsWith("interface.text-recognition.") ||
            id.StartsWith("translation.") || id.StartsWith("seein.translation.")) return "recognition";
        if (id.StartsWith("history.") || id.StartsWith("screenshot-output.") || id.StartsWith("screen-recording-output.") ||
            id is "screenshot.copy-image-file-to-clipboard" or "screenshot.save-as-file-dialog") return "storage";
        if (id.StartsWith("screen-recording.")) return "recording";
        return id.StartsWith("screenshot.") ? "capture" : "general";
    }
}
