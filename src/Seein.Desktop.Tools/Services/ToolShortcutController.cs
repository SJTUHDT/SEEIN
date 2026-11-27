using Seein.Core;
using Seein.Core.Tools;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace Seein.Tray;



internal sealed class ToolShortcutController : IDisposable
{
    private const int FirstId = 0x6100;
    private readonly string _path = Path.Combine(AppPaths.UserDefault.DataRoot, "shortcuts.json");
    private readonly string _captureStatePath = Path.Combine(AppPaths.UserDefault.DataRoot, "capture-shortcuts.json");
    private CaptureShortcutGate _captureGate = new();
    private readonly Dictionary<string, ShortcutBinding> _bindings = [];
    private readonly Dictionary<int, string> _registered = [];
    private nint _window;
    private bool _disposed;
    private ToolDoubleAltListener? _doubleAlt;
    private bool _doubleAltEnabled;
    private bool _doubleRightAltEnabled;
    public string Status { get; private set; } = "新工具的快捷键默认未设置。";
    public event Action<string>? ExecuteRequested;
    public event Action<string>? ShortcutStatusChanged;
    public ToolShortcutController()
    {
        if (File.Exists(_path))
            try { foreach (var binding in JsonSerializer.Deserialize<ShortcutBinding[]>(File.ReadAllText(_path)) ?? []) _bindings[binding.ActionId] = binding; }
            catch (Exception ex) when (ex is JsonException or IOException) { Status = "快捷键文件读取失败，原文件已保留。"; }
        try { _captureGate = CaptureShortcutGate.Load(_captureStatePath); }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException) { Status = "截图快捷键启停状态读取失败，原文件已保留。"; }
    }
    public IEnumerable<ShortcutItem> Items => ToolShortcutCatalog.ForModules(ToolRuntime.ActiveModules).Select(a => new ShortcutItem(a.Id,
        a.Id == CaptureShortcutGate.ToggleAction ? a.Name + (_captureGate.Enabled ? "（当前已启用）" : "（当前已暂停）") : a.Name,
        a.Module, _bindings.GetValueOrDefault(a.Id) ?? CaptureShortcutGate.DefaultBinding(a.Id)));
    private bool Enabled(ShortcutBinding binding) => binding.Enabled && !binding.Empty && _captureGate.Allows(binding.ActionId) && ToolRuntime.Instance.ActionEnabled(binding.ActionId);
    public void Initialize(nint window)
    {
        _window = window;
        _doubleAlt = new(window, () => Dispatch("pane.clipboard"), () => Dispatch(CaptureShortcutGate.ToggleAction));
        Register(); ToolRuntime.Instance.PreferencesChanged += RegisterChanged;
    }
    public void InitializeAdditionalModule()=>Register();
    private void Clear() { foreach (var id in _registered.Keys) UnregisterHotKey(_window, id); _registered.Clear(); }
    private void Register()
    {
        if (_window == 0 || _disposed) return;
        Clear(); var index = 0;
        foreach (var binding in Items.Select(i => i.Binding))
        {
            var id = FirstId + index++;
            if (!Enabled(binding) || binding.DoubleAlt || binding.DoubleRightAlt) continue;
            if (binding.Valid && RegisterHotKey(_window, id, binding.Modifiers | 0x4000, binding.Key)) _registered[id] = binding.ActionId;
            else Status = "部分快捷键未能注册，请检查占用或修改组合键。";
        }
        try { ConfigureDoubleAlt(); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or DllNotFoundException or EntryPointNotFoundException or BadImageFormatException) { Status = ex.Message; }
        WriteRegistrationReport();
    }
    private void ConfigureDoubleAlt(ShortcutBinding? replacement = null)
    {
        var items = Items.Select(i => replacement?.ActionId == i.Id ? replacement! : i.Binding).ToArray();
        var left = items.FirstOrDefault(b => b.ActionId == "pane.clipboard");
        var right = items.FirstOrDefault(b => b.ActionId == CaptureShortcutGate.ToggleAction);
        _doubleAltEnabled = _doubleRightAltEnabled = false;
        var leftEnabled = left is not null && Enabled(left) && left.DoubleAlt;
        var rightEnabled = right is not null && Enabled(right) && right.DoubleRightAlt;
        _doubleAlt?.SetEnabled(leftEnabled, rightEnabled);
        _doubleAltEnabled = _doubleAlt is not null && leftEnabled;
        _doubleRightAltEnabled = _doubleAlt is not null && rightEnabled;
    }
    private void WriteRegistrationReport()
    {
        try
        {
            var directory = AppPaths.UserDefault.Logs; Directory.CreateDirectory(directory);
            var report = new { updated = DateTimeOffset.Now, captureShortcutsEnabled = _captureGate.Enabled, actions = Items.Select(item => new
            {
                item.Id, item.Name, item.Group,
                configured = item.Binding.Enabled && !item.Binding.Empty,
                enabled = Enabled(item.Binding),
                registered = _registered.ContainsValue(item.Id) || item.Id == "pane.clipboard" && _doubleAltEnabled || item.Id == CaptureShortcutGate.ToggleAction && _doubleRightAltEnabled
            }).ToArray() };
            File.WriteAllText(Path.Combine(directory, "tool-shortcuts.json"), JsonSerializer.Serialize(report));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { System.Diagnostics.Trace.WriteLine(ex.Message); }
    }
    private void RegisterChanged()
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is not null && !dispatcher.CheckAccess()) dispatcher.BeginInvoke(Register);
        else Register();
    }
    private void Remove(int id) { if (_registered.Remove(id)) UnregisterHotKey(_window, id); }
    public bool Save(ShortcutBinding binding)
    {
        var items = Items.ToArray(); var index = Array.FindIndex(items, i => i.Id == binding.ActionId);
        if (index < 0) throw new ArgumentException("未知快捷键动作。");
        var id = FirstId + index;
        var previous = _bindings.GetValueOrDefault(binding.ActionId);
        var wasRegistered = _registered.ContainsKey(id);
        try
        {
            ShortcutValidation.Validate(items.Select(i => i.Id == binding.ActionId ? binding : i.Binding));
            Remove(id);
            if (Enabled(binding) && !binding.DoubleAlt && !binding.DoubleRightAlt)
            {
                if (_window == 0) throw new InvalidOperationException("窗口尚未准备好，请稍后重试。");
                if (!RegisterHotKey(_window, id, binding.Modifiers | 0x4000, binding.Key))
                {
                    var error = Marshal.GetLastWin32Error();
                    throw new InvalidOperationException(error == 1409 ? "这组快捷键已被其他程序占用，请释放占用或更换组合键。原绑定已保留。" : $"全局快捷键注册失败（Windows 错误 {error}），原绑定已保留。");
                }
                _registered[id] = binding.ActionId;
            }
            if (binding.ActionId is "pane.clipboard" or CaptureShortcutGate.ToggleAction) ConfigureDoubleAlt(binding);
            _bindings[binding.ActionId] = binding;
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            ShortcutBindingStore.Save(_path, binding);
            Status = "全局快捷键已保存，在其他应用和系统托盘中均有效。"; WriteRegistrationReport(); return true;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IOException or JsonException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            // Restore only this action; unrelated failed registrations must not block a valid edit.
            if (previous is null) _bindings.Remove(binding.ActionId); else _bindings[binding.ActionId] = previous;
            if (wasRegistered && previous is not null)
            {
                Remove(id);
                if (RegisterHotKey(_window, id, previous.Modifiers | 0x4000, previous.Key)) _registered[id] = previous.ActionId;
            }
            else Remove(id);
            if (binding.ActionId is "pane.clipboard" or CaptureShortcutGate.ToggleAction)
                try { ConfigureDoubleAlt(previous ?? CaptureShortcutGate.DefaultBinding(binding.ActionId)); } catch (Exception) { }
            Status = ex.Message; return false;
        }
    }
    public bool Handle(nint id)
    {
        if (!_registered.TryGetValue((int)id, out var action)) return false;
        Dispatch(action); return true;
    }
    private void Dispatch(string action)
    {
        // Also reject a WM_HOTKEY already queued before the registrations were paused.
        var binding = Items.FirstOrDefault(i => i.Id == action)?.Binding;
        if (ShortcutSettingsView.IsRecording || binding is null || !Enabled(binding)) return;
        if (action != CaptureShortcutGate.ToggleAction) { ExecuteRequested?.Invoke(action); return; }
        try
        {
            _captureGate = _captureGate.ToggleAndSave(_captureStatePath);
            Register();
            Status = _captureGate.Enabled ? "截图快捷键已开启。" : "截图快捷键已暂停，双击右 Alt 可重新开启。";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Status = "截图快捷键切换失败，原状态已保留：" + ex.Message; }
        ShortcutStatusChanged?.Invoke(Status);
    }
    public void Dispose() { _disposed = true; Clear(); _doubleAlt?.Dispose(); ToolRuntime.Instance.PreferencesChanged -= RegisterChanged; }
    [DllImport("user32.dll", SetLastError = true)] private static extern bool RegisterHotKey(nint window, int id, uint modifiers, uint key);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(nint window, int id);
}
