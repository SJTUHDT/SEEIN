using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Pane.Settings;
using Pane.Contracts;

namespace Pane;

public sealed partial class App
{
    internal static bool SeeinMode => !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("SEEIN_TOOL_PIPE"));
    internal static void SeeinOpenExternal(string target)
    {
        var result = LaunchPaneFromDesktop(target, "", "", true);
        if (result < 0) System.Runtime.InteropServices.Marshal.ThrowExceptionForHR(result);
    }
    [System.Runtime.InteropServices.DllImport("Pane.Native.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, ExactSpelling = true)]
    static extern int LaunchPaneFromDesktop(string target, string arguments, string directory,
        [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)] bool foreground);
    Task? seeinListener;
    void StartSeeinBridge()
    {
        if (!SeeinMode) return;
        Store.Save(SeeinSanitize(Store.Current));
        seeinListener = Task.Run(async () =>
        {
            while (!cancellation.IsCancellationRequested)
            {
                try
                {
                    await using var pipe = new NamedPipeServerStream(Environment.GetEnvironmentVariable("SEEIN_TOOL_PIPE")!, PipeDirection.InOut, 1,
                        PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                    await pipe.WaitForConnectionAsync(cancellation.Token);
                    using var reader = new StreamReader(pipe, Encoding.UTF8, false, 4096, true);
                    using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, true) { AutoFlush = true };
                    var line = await reader.ReadLineAsync(cancellation.Token);
                    if (line is null || line.Length > 1024 * 1024) continue;
                    var request = JsonNode.Parse(line)!.AsObject();
                    var result = new JsonObject { ["id"] = request["id"]?.DeepClone(), ["version"] = 1, ["success"] = true };
                    var quit = false;
                    try
                    {
                        if (request["version"]?.GetValue<int>() != 1) throw new ArgumentException("工具协议版本不匹配。");
                        await OnUi(async () =>
                        {
                            switch (request["operation"]?.GetValue<string>())
                            {
                                case "diagnostics.search-site":
                                    if (Environment.GetEnvironmentVariable("SEEIN_SEARCH_SITE_TEST") != "1") throw new InvalidOperationException("仅隔离测试可用。");
                                    Command("show-launcher");
                                    if (panels.TryGetValue("launcher", out var sitePanel) && sitePanel.View is Pane.Views.LauncherView siteView)
                                    {
                                        siteView.SeeinFill(request["values"]?["query"]?.GetValue<string>() ?? "");
                                        await Task.Delay(400);
                                        if (request["values"]?["choose"]?.GetValue<bool>() == true)
                                        {
                                            await siteView.SeeinChooseSite();
                                            await Task.Delay(100);
                                        }
                                        result["state"] = siteView.SeeinSiteState();
                                    }
                                    break;
                                case "diagnostics.search-position":
                                    result["state"] = SeeinSearchPositions(request["values"]?["dragX"]?.GetValue<int>() ?? 0, request["values"]?["dragY"]?.GetValue<int>() ?? 0); break;
                                case "status":
                                    result["state"] = new JsonObject { ["modules"] = ModuleDiagnostics(),
                                        ["panels"] = new JsonArray(panels.Select(p => (JsonNode)new JsonObject { ["name"] = p.Key, ["visible"] = p.Value.Shown, ["prepared"] = p.Value.Prepared }).ToArray()),
                                        ["lastCommand"] = LastModuleCommandResult }; break;
                                case "settings.get": result["fields"] = SeeinFields(); break;
                                case "files.refresh":
                                    if (FileSearchStatus is {} fileSearch) await fileSearch.RefreshAsync(CancellationToken.None);
                                    result["state"] = new JsonObject { ["ready"] = FileSearchStatus?.State == FileSearchState.Ready,
                                        ["version"] = FileSearchStatus?.Version }; break;
                                case "settings.apply":
                                    var changes = request["values"]!.AsObject();
                                    foreach (var entry in changes.Where(x => x.Key.StartsWith("capability.")))
                                    {
                                        var moduleId = entry.Key[11..];
                                        if (!SeeinCapabilities.Any(x => x.Id == moduleId)) throw new ArgumentException("不支持的能力。");
                                        await Modules.ApplyAsync(Modules.Preview(moduleId, entry.Value!.GetValue<bool>()), CancellationToken.None);
                                        var state = Modules.StateOf(moduleId);
                                        if (state.Problem != null) throw new InvalidOperationException(state.Problem);
                                    }
                                    var root = JsonSerializer.SerializeToNode(Store.Current, SettingsJson.Default.PaneSettings)!.AsObject();
                                    var allowed = SeeinFieldDefinitions.Select(x => x.Path).ToHashSet();
                                    foreach (var entry in changes.Where(x => !x.Key.StartsWith("capability.")))
                                    {
                                        if (!allowed.Contains(entry.Key)) throw new ArgumentException("不支持的设置。");
                                        var parts = entry.Key.Split('.'); JsonObject node = root;
                                        for (var i = 0; i < parts.Length - 1; i++) node = node[parts[i]]!.AsObject();
                                        node[parts[^1]] = entry.Value?.DeepClone();
                                    }
                                    Store.Save(SeeinSanitize(root.Deserialize(SettingsJson.Default.PaneSettings)!)); break;
                                case "execute":
                                    switch (request["action"]?.GetValue<string>())
                                    {
                                        case "pane.launcher": Command("show-launcher"); break;
                                        case "pane.clipboard": Command("show-clipboard"); break;
                                        case "pane.files": ShowFiles("", null); break;
                                        case "pane.rename": ShowRename([], 0); break;
                                        case "pane.power": Command("show-launcher"); if (panels.TryGetValue("launcher", out var panel) && panel.View is Pane.Views.LauncherView view) view.SeeinFill("30分钟后关机"); break;
                                        case "pane.folder-jump": (folderJump as Pane.Services.FolderJumpService)?.SeeinInvoke(false); break;
                                        case "pane.folder-menu": (folderJump as Pane.Services.FolderJumpService)?.SeeinInvoke(true); break;
                                        default: throw new ArgumentException("不支持的动作。");
                                    }
                                    break;
                                case "shutdown": quit = true; break;
                                default: throw new ArgumentException("不支持的工具请求。");
                            }
                            await Task.CompletedTask;
                        });
                    }
                    catch (Exception ex) { result["success"] = false; result["error"] = ex.Message; }
                    await writer.WriteLineAsync(result.ToJsonString());
                    if (quit) queue.TryEnqueue(Shutdown);
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException or InvalidOperationException) { }
            }
        });
    }
    static PaneSettings SeeinSanitize(PaneSettings s) => s with
    {
        General = s.General with { StartWithWindows = false, ShowTrayIcon = false },
        Update = s.Update with { AutoUpdate = false, InstallOnStartup = false, NewsShown = 3 },
        SuperPanel = s.SuperPanel with { Enabled = false },
        Modules = new(s.Modules ?? []) { [ModuleIds.Gestures] = false, [ModuleIds.SuperPanel] = false, [ModuleIds.Ai] = false, [ModuleIds.Words] = false },
        Launcher = s.Launcher with { ActionRow = s.Launcher.ActionRow.Where(x => !x.StartsWith("ai:")).ToArray(), AskKeywords = [], TranslateKeywords = [], Hotkey = new(0, 0) },
        Clipboard = s.Clipboard with { Hotkey = new(0, 0) },
        FolderJump = s.FolderJump with { JumpHotkey = new(0, 0), MenuHotkey = new(0, 0) }
    };
    internal static void OpenSeeinSettings()
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await using var pipe = new NamedPipeClientStream(".", Environment.GetEnvironmentVariable("SEEIN_SHELL_PIPE")!, PipeDirection.Out, PipeOptions.Asynchronous);
                await pipe.ConnectAsync(3000);
                using var writer = new StreamWriter(pipe, new UTF8Encoding(false)) { AutoFlush = true };
                await writer.WriteLineAsync("{\"id\":\"settings\",\"version\":1,\"operation\":\"settings\",\"action\":\"pane\"}");
            }
            catch (IOException) { }
        });
    }
    sealed record SeeinField(string Path, string Name, string Group, string Kind, string[]? Options = null);
    static readonly SeeinField[] SeeinFieldDefinitions =
    [
        new("General.Theme", "界面主题", "界面", "string", ["System", "Light", "Dark"]),
        new("General.AccentColor", "强调色", "界面", "string"),
        new("General.UiFont", "字体", "界面", "string"),
        new("General.UiLanguage", "语言", "界面", "string", ["zh-Hans", "zh-Hant"]),
        new("General.PanelRadius", "面板圆角", "界面", "number"),
        new("General.MotionSpeed", "动画速度", "界面", "number"),
        new("General.GlassLight.TintOpacity", "浅色玻璃：色调不透明度", "玻璃效果", "number"),
        new("General.GlassLight.LuminosityOpacity", "浅色玻璃：明度不透明度", "玻璃效果", "number"),
        new("General.GlassLight.Saturation", "浅色玻璃：饱和度", "玻璃效果", "number"),
        new("General.GlassLight.Highlight", "浅色玻璃：高光", "玻璃效果", "number"),
        new("General.GlassLight.Shadow", "浅色玻璃：阴影", "玻璃效果", "number"),
        new("General.GlassLight.ExtraBlur", "浅色玻璃：额外模糊", "玻璃效果", "number"),
        new("General.GlassDark.TintOpacity", "深色玻璃：色调不透明度", "玻璃效果", "number"),
        new("General.GlassDark.LuminosityOpacity", "深色玻璃：明度不透明度", "玻璃效果", "number"),
        new("General.GlassDark.Saturation", "深色玻璃：饱和度", "玻璃效果", "number"),
        new("General.GlassDark.Highlight", "深色玻璃：高光", "玻璃效果", "number"),
        new("General.GlassDark.Shadow", "深色玻璃：阴影", "玻璃效果", "number"),
        new("General.GlassDark.ExtraBlur", "深色玻璃：额外模糊", "玻璃效果", "number"),
        new("Launcher.HideOnBlur", "失去焦点后隐藏", "启动器", "bool"),
        new("Launcher.KeepInput", "保留输入", "启动器", "string", ["Clear", "OneMinute", "Always"]),
        new("Launcher.Home", "首页显示", "启动器", "string", ["RecentAndPinned", "PinnedOnly", "Nothing", "RecentOnly"]),
        new("Launcher.Width", "窗口宽度", "启动器", "string", ["Compact", "Standard", "Wide"]),
        new("Launcher.AppRows", "应用显示行数", "启动器", "number", ["1", "2", "3", "4"]),
        new("Launcher.FileRows", "文件显示行数", "启动器", "number", ["3", "5", "8", "10"]),
        new("Launcher.FileResultCount", "文件结果数", "启动器", "number", ["5", "8", "12"]),
        new("Launcher.ExpandOnOpen", "展开首页动画", "启动器", "bool"),
        new("Launcher.ArrowKeysMoveCaret", "左右键移动光标", "启动器", "bool"),
        new("Launcher.SearchSystemSettings", "搜索系统设置", "启动器", "bool"),
        new("Launcher.RememberCommands", "记住运行命令", "启动器", "bool"),
        new("Launcher.RememberWebSearches", "记住网页搜索", "启动器", "bool"),
        new("Launcher.WebSuggestions", "网页搜索建议", "启动器", "bool"),
        new("Launcher.RememberOpenedPrograms", "记住打开的便携程序", "启动器", "bool"),
        new("Launcher.AutoAttachSeconds", "自动附加剪贴板的时间（秒）", "启动器", "number", ["0", "1", "3", "5"]),
        new("Launcher.DefaultEngine", "默认搜索站点关键字", "搜索站点", "string"),
        new("Launcher.SearchEngines", "搜索站点", "搜索站点", "collection"),
        new("Launcher.Favorites", "常用文件和文件夹", "收藏与搜索", "collection"),
        new("Launcher.AppAliases", "应用别名", "收藏与搜索", "collection"),
        new("Launcher.HiddenItems", "不在搜索中显示", "收藏与搜索", "collection"),
        new("Launcher.Portable", "便携程序", "收藏与搜索", "collection"),
        new("Launcher.FilesKeywords", "文件搜索关键字", "收藏与搜索", "lines"),
        new("Launcher.PinnedApps", "固定的应用标识", "收藏与搜索", "lines"),
        new("Launcher.ActionRow", "启动器动作栏（files 或 web:站点关键字）", "收藏与搜索", "lines"),
        new("Clipboard.RecordText", "记录文字", "剪贴板", "bool"),
        new("Clipboard.RecordImages", "记录图片", "剪贴板", "bool"),
        new("Clipboard.RecordFiles", "记录文件列表", "剪贴板", "bool"),
        new("Clipboard.Limit", "历史条数上限", "剪贴板", "number", ["200", "500", "1000", "5000"]),
        new("Clipboard.ImageLimitMegabytes", "图片容量上限（MB，0 为不限）", "剪贴板", "number"),
        new("Clipboard.Dedupe", "合并重复内容", "剪贴板", "bool"),
        new("Clipboard.MoveToTopAfterPaste", "粘贴后移至顶部", "剪贴板", "bool"),
        new("Clipboard.HonorExcludeMarker", "遵守不记录标记", "剪贴板", "bool"),
        new("Clipboard.ExcludedPrograms", "排除的程序文件名", "剪贴板", "lines"),
        new("FolderJump.AutoJump", "自动跳转到最近文件夹", "文件对话框", "bool"),
        new("FolderJump.ShowRecent", "显示最近文件夹", "文件对话框", "bool"),
        new("FolderJump.MenuWhenMany", "多个文件夹时显示菜单", "文件对话框", "bool"),
        new("Icons.MaskByDefault", "统一应用图标形状", "图标", "bool"),
        new("Icons.SystemFileIcons", "使用系统文件图标", "图标", "bool")
    ];
    static readonly (string Id, string Name, string Description)[] SeeinCapabilities =
    [
        (ModuleIds.Launcher, "启动器", "热键呼出，搜应用、文件，算算式，搜网页。与剪贴板一起用：复制后呼出自动带入。"),
        (ModuleIds.Clipboard, "剪贴板", "记下复制过的文字、图片和文件，随时找回来再粘贴。可在剪贴板设置中管理记录范围与隐私。"),
        (ModuleIds.FolderJump, "文件对话框跳转", "在打开、另存为对话框里，跳到资源管理器中已打开的文件夹。支持最近文件夹和多窗口菜单。"),
        (ModuleIds.Rename, "批量重命名", "一次改多个文件名：替换文字、加编号、改扩展名，先预览再执行。从启动器或全局快捷键打开。"),
        (ModuleIds.PowerTimer, "定时关机", "到点关机、重启或睡眠；到点前提醒，可取消或推迟。可从启动器输入自然语言时间。")
    ];
    JsonArray SeeinFields()
    {
        var root = JsonSerializer.SerializeToNode(Store.Current, SettingsJson.Default.PaneSettings)!;
        var fields = new JsonArray();
        foreach (var capability in SeeinCapabilities)
            fields.Add(new JsonObject { ["id"] = "capability." + capability.Id, ["label"] = capability.Name, ["section"] = "能力",
                ["kind"] = "capability", ["value"] = Modules.StateOf(capability.Id).Enabled, ["description"] = capability.Description });
        foreach (var d in SeeinFieldDefinitions)
        {
            JsonNode? value = root; foreach (var part in d.Path.Split('.')) value = value?[part];
            var field = new JsonObject { ["id"] = d.Path, ["label"] = d.Name, ["section"] = d.Group, ["kind"] = d.Kind, ["value"] = value?.DeepClone() };
            if (d.Options != null) field["options"] = new JsonArray(d.Options.Select(x => (JsonNode)new JsonObject { ["value"] = d.Kind == "number" ? JsonValue.Create(int.Parse(x)) : JsonValue.Create(x), ["label"] = SeeinOptionLabel(x) }).ToArray());
            if (d.Path == "General.UiLanguage") field["options"]!.AsArray().Insert(0, new JsonObject { ["value"] = null, ["label"] = "跟随系统" });
            if (d.Path == "General.UiFont")
            {
                field["description"] = "字体更改在工具下次启动时生效。";
                field["options"] = new JsonArray(new JsonObject { ["value"] = null, ["label"] = "默认字体" });
                foreach (var font in new[] { "Microsoft YaHei UI", "Segoe UI", "MiSans VF", "SimSun", "Consolas" })
                    field["options"]!.AsArray().Add(new JsonObject { ["value"] = font, ["label"] = font });
            }
            if (d.Kind == "number")
            {
                field["minimum"] = d.Path == "General.MotionSpeed" ? 0.01 : 0;
                field["maximum"] = d.Path switch { "General.PanelRadius" => 100, "General.MotionSpeed" => 20, _ when d.Path.EndsWith("ExtraBlur") => 250, _ when d.Path.EndsWith("Saturation") => 2, _ when d.Path.StartsWith("General.Glass") => 1, _ => int.MaxValue };
            }
            if (d.Kind == "collection") field["columns"] = SeeinColumns(d.Path);
            fields.Add(field);
        }
        return fields;
    }
    static JsonArray SeeinColumns(string path)
    {
        static JsonObject Column(string key, string label, string kind = "string", JsonNode? value = null) =>
            new() { ["key"] = key, ["label"] = label, ["kind"] = kind, ["default"] = value ?? JsonValue.Create("") };
        return path switch
        {
            "Launcher.SearchEngines" => new(Column("Name", "名称"), Column("Keyword", "关键字"), Column("UrlTemplate", "地址（含 {query}）")),
            "Launcher.Favorites" => new(Column("Path", "路径"), Column("IsFolder", "文件夹", "bool", JsonValue.Create(false))),
            "Launcher.AppAliases" => new(Column("AppId", "应用标识"), Column("Name", "名称"), Column("Aliases", "别名（每行一个）", "lines", new JsonArray())),
            "Launcher.HiddenItems" => new(Column("Id", "应用标识"), Column("Name", "名称")),
            "Launcher.Portable" => new(Column("Path", "路径"),
                new JsonObject { ["key"] = "Kind", ["label"] = "类型", ["kind"] = "string", ["default"] = "Program", ["options"] = new JsonArray(new JsonObject { ["value"] = "Folder", ["label"] = "扫描文件夹" }, new JsonObject { ["value"] = "Program", ["label"] = "程序" }, new JsonObject { ["value"] = "Learned", ["label"] = "记住的程序" }) },
                Column("AddedAt", "添加时间", value: JsonValue.Create(DateTimeOffset.Now.ToString("O")))),
            _ => new()
        };
    }
    static string SeeinOptionLabel(string s) => s switch
    { "System" => "跟随系统", "Light" => "浅色", "Dark" => "深色", "zh-Hans" => "简体中文", "zh-Hant" => "繁体中文", "Clear" => "每次清空", "OneMinute" => "保留一分钟", "Always" => "始终保留", "RecentAndPinned" => "最近使用和固定", "PinnedOnly" => "仅固定项目", "Nothing" => "仅搜索栏", "RecentOnly" => "仅最近使用", "Compact" => "紧凑", "Standard" => "标准", "Wide" => "宽", _ => s };
}
