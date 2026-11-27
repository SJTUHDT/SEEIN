namespace Seein.SmartHome;

public sealed class RouterCredential(string origin, string password)
{
    public string Origin { get; } = origin;
    public string Password { get; } = password;
    public override string ToString() => Origin;
}

public sealed record LampShortcut(uint Modifiers = 0, uint Key = 0)
{
    [System.Text.Json.Serialization.JsonIgnore] public bool IsEmpty => Modifiers == 0 && Key == 0;
    public const string KeyHint = "可单按 F1–F8、Insert、End、Page Up、Page Down，也可使用 Ctrl / Alt 组合键。";
    public static bool IsSingleKey(uint key) => key is >= 0x70 and <= 0x77 or 0x2D or 0x23 or 0x21 or 0x22;
    [System.Text.Json.Serialization.JsonIgnore] public bool IsValid => IsEmpty || Modifiers == 0 && IsSingleKey(Key)
        || (Modifiers & ~7u) == 0 && (Modifiers & 3) != 0
        && (Key is >= 0x30 and <= 0x39 or >= 0x41 and <= 0x5A or >= 0x70 and <= 0x7A || IsSingleKey(Key));
}

public sealed record LampPreset(Guid Id, string Name, int Brightness, int ColorTemperature, LampShortcut? Shortcut = null)
{
    public override string ToString() => $"{Name} · {Brightness}% · {ColorTemperature} K";
}

public sealed record LampControlProfile(LampShortcut? PowerOn = null, LampShortcut? PowerOff = null, IReadOnlyList<LampPreset>? Presets = null)
{
    [System.Text.Json.Serialization.JsonIgnore] public IReadOnlyList<LampPreset> Items => Presets ?? [];
    [System.Text.Json.Serialization.JsonIgnore] public IEnumerable<LampShortcut> Shortcuts => new[] { PowerOn, PowerOff }.Concat(Items.Select(p => p.Shortcut)).Select(s => s ?? new());

    public void Validate()
    {
        if (Items.Count > 32) throw new ArgumentException("最多可保存 32 个灯光预设。");
        if (Items.Any(p => p is null || p.Id == Guid.Empty || string.IsNullOrWhiteSpace(p.Name) || p.Name.Trim().Length > 40
            || p.Brightness is < 1 or > 100 || p.ColorTemperature is < 2700 or > 6500))
            throw new ArgumentException("请填写 1–40 字的预设名称，并检查亮度和色温范围。");
        if (Items.Select(p => p.Id).Distinct().Count() != Items.Count) throw new ArgumentException("预设标识重复。");
        var keys = Shortcuts.ToArray();
        if (keys.Any(k => !k.IsValid)) throw new ArgumentException(LampShortcut.KeyHint);
        if (keys.Where(k => !k.IsEmpty).Distinct().Count() != keys.Count(k => !k.IsEmpty))
            throw new ArgumentException("开灯、关灯和各个预设不能使用相同的快捷键。");
    }
}
