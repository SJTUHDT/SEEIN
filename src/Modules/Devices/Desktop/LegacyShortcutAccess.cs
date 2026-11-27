using Seein.Core.Tools;
using Seein.SmartHome;

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

public partial class XiaomiLampView
{
    public IEnumerable<ShortcutItem> ManagedShortcuts
    {
        get
        {
            static ShortcutItem Item(string id, string name, LampShortcut? key) => new(id, name, "米家挂灯", new(id, key?.Modifiers ?? 0, key?.Key ?? 0));
            yield return Item("lamp.on", "开灯", _shortcuts.Profile.PowerOn);
            yield return Item("lamp.off", "关灯", _shortcuts.Profile.PowerOff);
            foreach (var preset in _shortcuts.Profile.Items) yield return Item("lamp.preset." + preset.Id, preset.Name, preset.Shortcut);
        }
    }
    public (bool Success, string Message) SaveManagedShortcut(ShortcutBinding binding)
    {
        LampShortcut? key = binding.Empty || !binding.Enabled ? null : new(binding.Modifiers, binding.Key);
        var profile = _shortcuts.Profile;
        if (binding.ActionId == "lamp.on") profile = profile with { PowerOn = key };
        else if (binding.ActionId == "lamp.off") profile = profile with { PowerOff = key };
        else profile = profile with { Presets = profile.Items.Select(p => "lamp.preset." + p.Id == binding.ActionId ? p with { Shortcut = key } : p).ToArray() };
        var saved = SaveProfile(profile, (PresetSelection.SelectedItem as LampPreset)?.Id);
        return (saved, _shortcuts.Status);
    }
}
