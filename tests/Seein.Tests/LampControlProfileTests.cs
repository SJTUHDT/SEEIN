using Seein.SmartHome;
using Seein.Tray;

namespace Seein.Tests;

public sealed class LampControlProfileTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "Seein-lamp-profile-" + Guid.NewGuid().ToString("N"));
    private LampPreset Preset(string name = "阅读", LampShortcut? key = null) => new(Guid.NewGuid(), name, 63, 4200, key);

    [Fact]
    public void PresetsAndPowerShortcutsSurviveRestartRenameAndDelete()
    {
        var store = new SmartHomeStore(_directory);
        var preset = Preset(key: new(3, 0x52));
        store.SaveLampProfile(new(new(3, 0x4F), new(3, 0x46), new[] { preset }));
        var loaded = store.LoadLampProfile();
        Assert.Equal(preset, Assert.Single(loaded.Items));
        Assert.Equal(new(3, 0x4F), loaded.PowerOn);
        store.SaveLampProfile(loaded with { Presets = new[] { preset with { Name = "夜间阅读", Brightness = 21 } } });
        var renamed = Assert.Single(store.LoadLampProfile().Items);
        Assert.Equal(preset.Id, renamed.Id);
        Assert.Equal("夜间阅读", renamed.Name);
        Assert.Equal(preset.Shortcut, renamed.Shortcut);
        store.SaveLampProfile(loaded with { Presets = [] });
        Assert.Empty(store.LoadLampProfile().Items);
        Assert.Equal(loaded.PowerOff, store.LoadLampProfile().PowerOff);
    }

    [Fact]
    public void DuplicateBindingsAndInvalidPresetsCannotOverwriteSavedSettings()
    {
        var store = new SmartHomeStore(_directory);
        var preset = Preset();
        store.SaveLampProfile(new(Presets: new[] { preset }));
        Assert.Throws<ArgumentException>(() => store.SaveLampProfile(new(new(3, 0x41), new(3, 0x41))));
        Assert.Throws<ArgumentException>(() => store.SaveLampProfile(new(Presets: new[] { preset with { Brightness = 0 } })));
        Assert.Throws<ArgumentException>(() => store.SaveLampProfile(new(Presets: new[] { preset with { Name = " " } })));
        Assert.Throws<ArgumentException>(() => store.SaveLampProfile(new(Presets: new[] { preset, preset })));
        Assert.Equal(preset, Assert.Single(store.LoadLampProfile().Items));
    }

    [Theory]
    [InlineData(0, 0x41)]
    [InlineData(4, 0x41)]
    [InlineData(10, 0x41)]
    [InlineData(2, 0x7B)]
    public void ShortcutsRejectBareKeysWindowsAndReservedF12(uint modifiers, uint key) => Assert.False(new LampShortcut(modifiers, key).IsValid);

    [Theory]
    [InlineData(0x70)] [InlineData(0x71)] [InlineData(0x72)] [InlineData(0x73)]
    [InlineData(0x74)] [InlineData(0x75)] [InlineData(0x76)] [InlineData(0x77)]
    [InlineData(0x2D)] [InlineData(0x23)] [InlineData(0x21)] [InlineData(0x22)]
    public void RequestedSingleKeysCanBeSavedAndRegisteredWithoutAddingModifiers(uint key)
    {
        var store = new SmartHomeStore(_directory);
        var shortcut = new LampShortcut(0, key);
        Assert.True(shortcut.IsValid);
        using var controller = new LampShortcutController(store, (_, _, modifiers, actualKey) =>
        {
            Assert.Equal(0x4000u, modifiers); // No Ctrl/Alt, only disable held-key repeat.
            Assert.Equal(key, actualKey);
            return true;
        }, (_, _) => { });
        controller.Initialize(123);
        Assert.True(controller.Save(new(Presets: new[] { Preset(key: shortcut) })));
        Assert.Equal(shortcut, Assert.Single(store.LoadLampProfile().Items).Shortcut);
        Assert.Equal(shortcut, controller.ShortcutFor(0x5402));
    }

    [Fact]
    public void ReusedSingleKeyCannotBeAssignedToBothPowerAndPreset()
    {
        var shortcut = new LampShortcut(0, 0x70);
        Assert.Throws<ArgumentException>(() => new LampControlProfile(shortcut, Presets: new[] { Preset(key: shortcut) }).Validate());
        new LampControlProfile(shortcut, Presets: new[] { Preset(key: new(2, 0x70)) }).Validate();
        Assert.False(new LampShortcut(0, 0x78).IsValid); // Unrequested bare F9.
        Assert.False(new LampShortcut(0, 0x7B).IsValid); // F12 remains reserved.
    }

    [Fact]
    public void ConflictingGlobalHotkeyRestoresExistingBindingsAndDiskProfile()
    {
        var store = new SmartHomeStore(_directory);
        var original = new LampControlProfile(new(3, 0x4F), new(3, 0x46));
        store.SaveLampProfile(original);
        var registered = new Dictionary<int, uint>();
        using (var controller = new LampShortcutController(store, (_, id, modifiers, key) =>
        {
            Assert.True((modifiers & 0x4000) != 0); // No auto-repeat lamp commands.
            if (key == 0x58) return false;
            registered.Add(id, key); return true;
        }, (_, id) => registered.Remove(id)))
        {
            controller.Initialize(123);
            Assert.Equal(2, registered.Count);
            Assert.False(controller.Save(new(new(3, 0x58), new(3, 0x46))));
            Assert.Equal(original.PowerOn, controller.Profile.PowerOn);
            Assert.Equal(original.PowerOn, store.LoadLampProfile().PowerOn);
            Assert.Equal(new uint[] { 0x4F, 0x46 }, registered.OrderBy(p => p.Key).Select(p => p.Value));
            Assert.True(controller.Save(original with { PowerOff = null }));
            Assert.Single(registered);
            Assert.Null(store.LoadLampProfile().PowerOff);
        }
        Assert.Empty(registered);
    }

    [Fact]
    public void RouterPasswordIsEncryptedAndBoundToOneOrigin()
    {
        var store = new SmartHomeStore(_directory);
        var origin = new Uri("http://192.168.5.1/");
        const string password = "test-only-router-secret-987";
        store.SaveRouterCredential(origin, password);
        Assert.DoesNotContain(password, File.ReadAllText(Path.Combine(_directory, "SmartHome", "router.authorization")));
        Assert.Equal(password, store.LoadRouterCredential(origin)!.Password);
        Assert.DoesNotContain(password, store.LoadRouterCredential(origin)!.ToString());
        Assert.Null(store.LoadRouterCredential(new("http://192.168.5.2/")));
        Assert.Null(store.LoadRouterCredential(new("https://192.168.5.1/")));
        Assert.Null(store.LoadRouterCredential(new("http://192.168.5.1:8080/")));
    }

    public void Dispose() { if (Directory.Exists(_directory)) Directory.Delete(_directory, true); }
}
