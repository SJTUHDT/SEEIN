using System.Text.Json;

namespace Seein.Core.Tools;

public static class ShellShortcutCatalog
{
    public sealed record Action(string Id, string Name, string Route);
    public static IReadOnlyList<Action> ForRoutes(IEnumerable<string> routes)
    {
        var available = routes.ToHashSet(StringComparer.Ordinal);
        var actions = new List<Action> { new("seein.show", "打开 SEEIN", "overview") };
        if (available.Any(route => route.StartsWith("tool-", StringComparison.Ordinal)))
            actions.Add(new("seein.tools", "打开小工具", "tools"));
        foreach (var (route, title) in new[] { ("pet", "桌宠设置"), ("workbench", "大肥鱼工作台"), ("astrbot", "AstrBot 后台"), ("qq", "QQ 后台") })
            if (available.Contains("tool-" + route)) actions.Add(new("seein." + route, "打开" + title, "tool-" + route));
        return actions;
    }
}

// Both the host and the optional tools edit the original file. Merge a single edit
// against the latest contents so neither owner drops the other's saved bindings.
public static class ShortcutBindingStore
{
    private static readonly object Gate = new();
    public static ShortcutBinding[] Read(string path) => File.Exists(path)
        ? JsonSerializer.Deserialize<ShortcutBinding[]>(File.ReadAllText(path)) ?? [] : [];
    public static void Save(string path, ShortcutBinding binding)
    {
        lock (Gate)
        {
            var bindings = Read(path).Where(item => item.ActionId != binding.ActionId).Append(binding).ToArray();
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try { File.WriteAllText(temporary, JsonSerializer.Serialize(bindings)); File.Move(temporary, path, true); }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
    }
}
