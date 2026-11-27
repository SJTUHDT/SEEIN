using Seein.Core;
using Seein.Core.Displays;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace Seein.Tray;

internal sealed class DisplayShortcutController : IDisposable
{
    public const int HotkeyId = 0x5345;
    private readonly string _path = Path.Combine(AppPaths.UserDefault.DataRoot, "display-control.json");
    private readonly DisplayControlClient _client = new(Seein.Core.ProcessNames.Executable("display"));
    private nint _window;
    private bool _registered;
    public DisplayShortcutSettings Settings { get; private set; } = new();
    public string Status { get; private set; } = "尚未启用";

    public DisplayShortcutController()
    {
        try
        {
            if (File.Exists(_path)) Settings = JsonSerializer.Deserialize<DisplayShortcutSettings>(File.ReadAllText(_path)) ?? new();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        { Status = "快捷键配置读取失败，请重新保存。"; }
    }

    public void Initialize(nint window)
    {
        _window = window;
        if (Settings.Enabled)
            Status = Settings.IsValid && TryRegister(Settings) ? "快捷键已启用，托盘中仍有效" : "快捷键未能注册，可能已被其他应用占用。";
    }

    private bool TryRegister(DisplayShortcutSettings settings) =>
        _registered = RegisterHotKey(_window, HotkeyId, settings.Modifiers | 0x4000, settings.Key);

    public bool Save(DisplayShortcutSettings candidate)
    {
        if (!candidate.IsValid) { Status = "请选择显示器，并使用含 Ctrl 或 Alt 的字母、数字或功能键组合。"; return false; }
        if (_window == 0) { Status = "快捷键尚未初始化，请重新打开窗口。"; return false; }
        var previous = Settings;
        if (_registered) { UnregisterHotKey(_window, HotkeyId); _registered = false; }
        if (candidate.Enabled && !TryRegister(candidate))
        {
            if (previous.Enabled && previous.IsValid) TryRegister(previous);
            Status = "该快捷键已被占用或无法注册，原有设置已保留。";
            return false;
        }
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var temporary = _path + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(candidate));
            File.Move(temporary, _path, true);
            Settings = candidate;
            Status = candidate.Enabled ? "快捷键已保存，托盘中仍有效" : "快捷键已停用";
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            if (_registered) { UnregisterHotKey(_window, HotkeyId); _registered = false; }
            if (previous.Enabled && previous.IsValid) TryRegister(previous);
            Status = "保存失败，原有设置已保留：" + ex.Message;
            return false;
        }
    }

    public bool Matches(nint id) => id == HotkeyId && _registered && Settings.Enabled;
    public Task<DisplayResponse> RestoreDp1Async() => _client.SendAsync(new("restore-dp1", Settings.TargetId));

    public void Dispose()
    {
        if (_registered) UnregisterHotKey(_window, HotkeyId);
        _client.Dispose();
    }

    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(nint window, int id, uint modifiers, uint key);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(nint window, int id);
}
