using Seein.Core.Tools;


namespace Seein.Tray;

public partial class DisplayControlView
{
    public ShortcutItem Dp1Shortcut => new("display.dp1", "切回 DP1", "显示器", new("display.dp1", _shortcut.Settings.Modifiers, _shortcut.Settings.Key, _shortcut.Settings.Enabled));
    public (bool Success, string Message) SaveManagedShortcut(ShortcutBinding binding)
    {
        var saved = binding.Empty ? _shortcut.Save(_shortcut.Settings with { Enabled = false }) :
            _shortcut.Save(new(_shortcut.Settings.TargetId ?? Target, binding.Modifiers, binding.Key, binding.Enabled));
        return (saved, _shortcut.Status);
    }
}

