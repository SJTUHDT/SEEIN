using System.Text.Json;
using System.Text.Json.Nodes;

namespace Seein.Core.Tools;

public sealed record ToolAction(string Id, string Name, string Module, bool Global = true);
public sealed record ToolOption(JsonNode? Value, string Label);
public sealed record ToolColumn(string Key, string Label, string Kind = "string", JsonNode? Default = null, IReadOnlyList<ToolOption>? Options = null);
public sealed record ToolField(string Id, string Label, string Section, string Kind,
    JsonNode? Value = null, IReadOnlyList<ToolOption>? Options = null,
    double? Minimum = null, double? Maximum = null, string? Description = null, IReadOnlyList<ToolColumn>? Columns = null);
public sealed record ToolRequest(string Id, string Operation, string? Action = null, JsonObject? Values = null, int Version = 1);
public sealed record ToolResponse(string Id, bool Success, string? Error = null,
    IReadOnlyList<ToolField>? Fields = null, JsonObject? State = null, int Version = 1);

public static class ToolCatalog
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public static readonly ToolAction[] Actions =
    [
        new("snow.capture", "截图并标注", "截图录屏"),
        new("snow.copy", "截图并复制", "截图录屏"),
        new("snow.window", "截取当前窗口", "截图录屏"),
        new("snow.screen", "截取当前显示器", "截图录屏"),
        new("snow.delay", "延时截图", "截图录屏"),
        new("snow.scrolling", "滚动长截图", "截图录屏"),
        new("snow.record", "开始录屏", "截图录屏"),
        new("snow.ocr", "截图识别文字", "截图录屏"),
        new("snow.translate", "截图翻译", "截图录屏"),
        new("snow.history", "截图历史", "截图录屏"),
        new("snow.pdf", "图片转 PDF", "截图录屏"),
        new("snow.toggle-shortcuts", "开启/关闭截图快捷键", "截图录屏"),
        new("pane.launcher", "快速启动器", "效率工具"),
        new("pane.clipboard", "剪贴板历史", "效率工具"),
        new("pane.files", "文件搜索", "效率工具"),
        new("pane.rename", "批量重命名", "效率工具"),
        new("pane.folder-jump", "跳到已打开的文件夹", "效率工具"),
        new("pane.folder-menu", "选择文件夹跳转", "效率工具"),
        new("pane.power", "定时关机", "效率工具"),
    ];
    public static bool IsAction(string? id) => Actions.Any(a => a.Id == id);
    public static string ModuleOf(string id) => id.Split('.', 2)[0] switch
    { "snow" => "snow", "pane" => "pane", _ => throw new ArgumentException("未知小工具。") };
}

public sealed record ToolPreferences(bool ScreenshotEnabled = true, bool PaneEnabled = true);
public sealed record ShortcutBinding(string ActionId, uint Modifiers = 0, uint Key = 0, bool Enabled = true, bool DoubleAlt = false)
{
    public bool DoubleRightAlt { get; init; }
    public bool Empty => Key == 0 && !DoubleAlt && !DoubleRightAlt;
    public bool Valid => DoubleRightAlt ? !DoubleAlt && ActionId == "snow.toggle-shortcuts" && Modifiers == 0 && Key == 0 :
        DoubleAlt ? ActionId == "pane.clipboard" && Modifiers == 0 && Key == 0 : Empty || (Modifiers & ~15u) == 0 && Key != 0x7B &&
        (Key is 0x09 or 0x0D or 0x1B or >= 0x20 and <= 0x28 or >= 0x2D and <= 0x2E or >= 0x30 and <= 0x39 or >= 0x41 and <= 0x5A or >= 0x70 and <= 0x87 or >= 0x60 and <= 0x6F or >= 0xBA and <= 0xC0 or >= 0xDB and <= 0xDF) &&
        (Modifiers != 0 || Key is >= 0x70 and <= 0x87 or 0x21 or 0x22 or 0x23 or 0x2D);
}

public static class ShortcutValidation
{
    public static void Validate(IEnumerable<ShortcutBinding> bindings)
    {
        var active = bindings.Where(b => b.Enabled && !b.Empty).ToArray();
        if (active.Any(b => !b.Valid)) throw new ArgumentException("快捷键无效，请避开 F12 和修饰键本身。");
        if (active.GroupBy(b => (b.Modifiers, b.Key, b.DoubleAlt, b.DoubleRightAlt)).Any(g => g.Count() > 1))
            throw new ArgumentException("多个动作使用了同一快捷键，请修改冲突项。");
    }
}
