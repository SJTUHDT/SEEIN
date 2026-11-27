namespace Seein.Core.Displays;

public sealed record DisplayShortcutSettings(string? TargetId = null, uint Modifiers = 3, uint Key = 0x44, bool Enabled = false)
{
    public bool IsValid => !string.IsNullOrWhiteSpace(TargetId) && (Modifiers & ~0x07u) == 0 &&
        (Modifiers & 3) != 0 && Key != 0x7B &&
        (Key is >= 0x30 and <= 0x39 or >= 0x41 and <= 0x5A or >= 0x70 and <= 0x87);
}
