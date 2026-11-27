using System.Text.Json;

namespace Seein.Core.Tools;

// This pauses registrations, never the engine or the user's individual bindings.
public sealed record CaptureShortcutGate(bool Enabled = true)
{
    public const string ToggleAction = "snow.toggle-shortcuts";
    public bool Allows(string action) => Enabled || !action.StartsWith("snow.", StringComparison.Ordinal) || action == ToggleAction;
    public static ShortcutBinding DefaultBinding(string action) => action == ToggleAction
        ? new(action) { DoubleRightAlt = true } : new(action);
    public static CaptureShortcutGate Load(string path) => File.Exists(path)
        ? JsonSerializer.Deserialize<CaptureShortcutGate>(File.ReadAllText(path)) ?? throw new JsonException("截图快捷键状态为空。") : new();
    public CaptureShortcutGate ToggleAndSave(string path)
    {
        var next = this with { Enabled = !Enabled };
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllText(temporary, JsonSerializer.Serialize(next)); File.Move(temporary, path, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
        return next;
    }
}
