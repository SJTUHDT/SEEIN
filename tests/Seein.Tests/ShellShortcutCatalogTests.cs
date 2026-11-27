using System.Text.Json;
using Seein.Core.Tools;

namespace Seein.Tests;

public sealed class ShellShortcutCatalogTests
{
    [Fact]
    public void HostShortcutSurvivesWithoutAnyPlugins() =>
        Assert.Equal(new[] { "seein.show" }, ShellShortcutCatalog.ForRoutes([]).Select(action => action.Id));

    [Fact]
    public void CompanionEntryPointsDoNotDependOnCaptureOrEfficiency()
    {
        var actions = ShellShortcutCatalog.ForRoutes(["tool-pet", "tool-workbench", "tool-astrbot", "tool-qq"]);
        Assert.Equal(new[] { "seein.show", "seein.tools", "seein.pet", "seein.workbench", "seein.astrbot", "seein.qq" }, actions.Select(action => action.Id));
        Assert.Equal("tool-pet", actions.Single(action => action.Id == "seein.pet").Route);
    }

    [Fact]
    public void UninstalledCompanionDoesNotExposeDeadEntryPoints() =>
        Assert.Equal(new[] { "seein.show", "seein.tools" }, ShellShortcutCatalog.ForRoutes(["tool-snow"]).Select(action => action.Id));

    [Fact]
    public void InterleavedOwnersPreserveEverySavedAndDisabledPluginBinding()
    {
        var root = Path.Combine(Path.GetTempPath(), "SeeinShortcutStore-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(root, "shortcuts.json");
        try
        {
            var old = new ShortcutBinding("unknown.plugin", 2, 65);
            ShortcutBindingStore.Save(path, old);
            ShortcutBindingStore.Save(path, new("seein.pet", 1, 66));
            ShortcutBindingStore.Save(path, new("snow.capture", 1, 67));
            ShortcutBindingStore.Save(path, new("seein.pet", 1, 68));
            var saved = ShortcutBindingStore.Read(path);
            Assert.Equal(3, saved.Length);
            Assert.Contains(old, saved);
            Assert.Equal(68u, saved.Single(item => item.ActionId == "seein.pet").Key);
            Assert.Equal(67u, saved.Single(item => item.ActionId == "snow.capture").Key);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public void DamagedShortcutFileIsNeverReplacedByOneOwnersPartialList()
    {
        var root = Path.Combine(Path.GetTempPath(), "SeeinShortcutStore-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root); var path = Path.Combine(root, "shortcuts.json");
        try
        {
            File.WriteAllText(path, "damaged original");
            Assert.Throws<JsonException>(() => ShortcutBindingStore.Save(path, new("seein.show", 1, 65)));
            Assert.Equal("damaged original", File.ReadAllText(path));
        }
        finally { Directory.Delete(root, true); }
    }
}
