using Seein.Core;
using Seein.Core.Tools;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace Seein.Tray;

internal sealed class ShellShortcutController : IDisposable
{
    private const int FirstId = 0x6500;
    private readonly string _path = Path.Combine(AppPaths.UserDefault.DataRoot, "shortcuts.json");
    private readonly Dictionary<string, ShortcutBinding> _bindings = [];
    private readonly Dictionary<int, ShellShortcutCatalog.Action> _registered = [];
    private readonly IDesktopHost _host;
    private readonly IReadOnlyList<ShellShortcutCatalog.Action> _actions;
    public IEnumerable<ShortcutItem> Items => _actions.Select(action => new ShortcutItem(action.Id, action.Name, "SEEIN",
        _bindings.GetValueOrDefault(action.Id) ?? new(action.Id)));

    public ShellShortcutController(IDesktopHost host, IEnumerable<string> routes)
    {
        _host = host; _actions = ShellShortcutCatalog.ForRoutes(routes);
        try { foreach (var binding in ShortcutBindingStore.Read(_path)) _bindings[binding.ActionId] = binding; }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { host.Notify("SEEIN 快捷键", "快捷键文件读取失败，原文件已保留。", true); }
        foreach (var item in Items)
            if (item.Binding.Enabled && !item.Binding.Empty && !Register(item.Binding)) host.Notify("SEEIN 快捷键", item.Name + "的快捷键未能注册，请检查按键占用。", true);
    }
    private bool Register(ShortcutBinding binding)
    {
        var index = _actions.ToList().FindIndex(action => action.Id == binding.ActionId);
        if (index < 0 || !binding.Valid || binding.DoubleAlt) return false;
        var id = FirstId + index;
        if (!RegisterHotKey(_host.Handle, id, binding.Modifiers | 0x4000, binding.Key)) return false;
        _registered[id] = _actions[index]; return true;
    }
    public (bool Success, string Message) Save(ShortcutBinding binding)
    {
        var index = _actions.ToList().FindIndex(action => action.Id == binding.ActionId);
        if (index < 0) return (false, "未知快捷键动作。");
        var id = FirstId + index; var previous = _bindings.GetValueOrDefault(binding.ActionId);
        var wasRegistered = _registered.Remove(id); if (wasRegistered) UnregisterHotKey(_host.Handle, id);
        try
        {
            ShortcutValidation.Validate(Items.Select(item => item.Id == binding.ActionId ? binding : item.Binding));
            if (binding.Enabled && !binding.Empty && !Register(binding)) throw new InvalidOperationException("这组快捷键未能注册，请检查按键占用。原绑定已保留。");
            ShortcutBindingStore.Save(_path, binding); _bindings[binding.ActionId] = binding;
            return (true, "已保存");
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IOException or JsonException or UnauthorizedAccessException)
        {
            if (_registered.Remove(id)) UnregisterHotKey(_host.Handle, id);
            if (wasRegistered && previous is not null) Register(previous);
            return (false, ex.Message);
        }
    }
    public bool Handle(nint id)
    {
        if (!_registered.TryGetValue((int)id, out var action)) return false;
        _host.ShowWindow(); if (action.Id != "seein.show") _host.Navigate(action.Route); return true;
    }
    public void Dispose() { foreach (var id in _registered.Keys) UnregisterHotKey(_host.Handle, id); _registered.Clear(); }
    [DllImport("user32.dll", SetLastError = true)] private static extern bool RegisterHotKey(nint window, int id, uint modifiers, uint key);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(nint window, int id);
}
