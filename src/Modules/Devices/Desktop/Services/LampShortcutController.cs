using Seein.SmartHome;
using System.Runtime.InteropServices;

namespace Seein.Tray;

internal sealed class LampShortcutController : IDisposable
{
    private const int FirstId = 0x5400;
    private readonly SmartHomeStore _store;
    private readonly HashSet<int> _registered = [];
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Func<nint, int, uint, uint, bool> _register;
    private readonly Action<nint, int> _unregister;
    private nint _window;
    private bool _sending;
    public LampControlProfile Profile { get; private set; } = new();
    public string Status { get; private set; } = "点击快捷键框录入按键，保存后在托盘中也有效。";
    public event Action<string, bool>? ActionCompleted;

    public LampShortcutController(SmartHomeStore store, Func<nint, int, uint, uint, bool>? register = null, Action<nint, int>? unregister = null)
    {
        _store = store;
        _register = register ?? RegisterHotKey;
        _unregister = unregister ?? ((window, id) => UnregisterHotKey(window, id));
        try { Profile = store.LoadLampProfile(); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { Status = "预设配置无法读取，请重新保存。"; }
    }

    public void Initialize(nint window)
    {
        _window = window;
        if (!Register(Profile)) Status = "部分挂灯快捷键已被其他应用占用，请修改后重新保存。";
    }

    private bool Register(LampControlProfile profile)
    {
        bool success = true;
        var keys = profile.Shortcuts.ToArray();
        for (var i = 0; i < keys.Length; i++)
        {
            if (keys[i].IsEmpty) continue;
            if (_register(_window, FirstId + i, keys[i].Modifiers | 0x4000, keys[i].Key)) _registered.Add(FirstId + i);
            else success = false;
        }
        return success;
    }

    private void Unregister()
    {
        foreach (var id in _registered) _unregister(_window, id);
        _registered.Clear();
    }

    public bool Save(LampControlProfile profile)
    {
        try { profile.Validate(); }
        catch (ArgumentException ex) { Status = ex.Message; return false; }
        if (_window == 0) { Status = "快捷键尚未初始化，请重新打开窗口。"; return false; }
        Unregister();
        if (!Register(profile))
        {
            Unregister(); Register(Profile);
            Status = "快捷键已被占用，原有预设和快捷键已保留。";
            return false;
        }
        try { _store.SaveLampProfile(profile); Profile = profile; Status = "已保存，快捷键在托盘中仍有效。"; return true; }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Unregister(); Register(Profile);
            Status = "保存失败，原有设置已保留。"; return false;
        }
    }

    public bool Matches(nint id) => _registered.Contains((int)id);
    public LampShortcut? ShortcutFor(nint id) => Matches(id) ? Profile.Shortcuts.ElementAt((int)id - FirstId) : null;
    public void Execute(nint id)
    {
        if (!Matches(id) || _sending) return;
        var action = (int)id - FirstId;
        _ = RunAsync(action);
    }

    private async Task RunAsync(int action)
    {
        _sending = true;
        var profile = Profile;
        try
        {
            var auth = _store.LoadAuthorization() ?? throw new InvalidOperationException("请先在米家挂灯页扫码连接。");
            using var client = new XiaomiLampClient(_store.LoadSettings().LampAddress, auth.Token);
            await client.VerifyModelAsync(_shutdown.Token);
            string label;
            if (action < 2) { await client.SetPowerAsync(action == 0, _shutdown.Token); label = action == 0 ? "开灯" : "关灯"; }
            else
            {
                var preset = profile.Items[action - 2];
                await client.SetLightAsync(preset.Brightness, preset.ColorTemperature, _shutdown.Token);
                await client.SetPowerAsync(true, _shutdown.Token);
                label = preset.Name;
            }
            var state = await client.ReadAsync(_shutdown.Token);
            ActionCompleted?.Invoke($"挂灯 · {label} · {(state.IsOn ? "已开灯" : "已关灯")} · {state.Brightness}% · {state.ColorTemperature} K", false);
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            ActionCompleted?.Invoke(ex is InvalidOperationException or TimeoutException ? ex.Message : "挂灯快捷键执行失败，请检查设备连接。", true);
        }
        finally { _sending = false; }
    }

    public void Dispose() { Unregister(); _shutdown.Cancel(); }
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(nint window, int id, uint modifiers, uint key);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(nint window, int id);
}
