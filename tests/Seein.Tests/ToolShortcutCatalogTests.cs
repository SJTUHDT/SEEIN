using Seein.Core.Tools;
using Seein.Tray;

namespace Seein.Tests;

public sealed class ToolShortcutCatalogTests
{
    private static readonly string[] LegacyActions =
    ["snow.capture", "snow.copy", "snow.window", "snow.screen", "snow.delay", "snow.scrolling", "snow.record", "snow.ocr", "snow.translate", "snow.history", "snow.pdf",
     "snow.toggle-shortcuts", "pane.launcher", "pane.clipboard", "pane.files", "pane.rename", "pane.folder-jump", "pane.folder-menu", "pane.power"];

    [Fact]
    public void BothPluginsKeepEvery16SeriesGlobalAction()
    {
        var actions = ToolShortcutCatalog.ForModules(["snow", "pane"]);
        Assert.Equal(LegacyActions, actions.Select(action => action.Id));
        Assert.Equal(19, actions.Select(action => action.Id).Distinct().Count());
    }

    [Theory]
    [InlineData("snow", "截图录屏", 12)]
    [InlineData("pane", "效率工具", 7)]
    public void RuntimeModuleIdsSelectActionsWhoseGroupNamesAreLocalized(string module, string title, int count)
    {
        var actions = ToolShortcutCatalog.ForModules([module]);
        Assert.Equal(count, actions.Count);
        Assert.All(actions, action => { Assert.Equal(module, ToolCatalog.ModuleOf(action.Id)); Assert.Equal(title, action.Module); });
    }

    [Fact]
    public void MissingPluginsDoNotAddInertHotkeys()
    {
        Assert.Empty(ToolShortcutCatalog.ForModules([]));
        Assert.Empty(ToolShortcutCatalog.ForModules(["截图录屏", "效率工具"]));
    }

    [Fact]
    public void ExistingBindingIdsRemainUsableAfterMigration()
    {
        var ids = ToolShortcutCatalog.ForModules(["pane", "snow"]).Select(action => action.Id).ToHashSet();
        Assert.All(LegacyActions.Select(id => new ShortcutBinding(id, 3, 0x41)), binding => Assert.Contains(binding.ActionId, ids));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void DoubleAltHookFollowsTheEfficiencyPluginOrLegacyPayload(bool modular)
    {
        var root = Path.Combine(Path.GetTempPath(), "SeeinHotkeyPath-" + Guid.NewGuid().ToString("N"));
        var app = Path.Combine(root, "App");
        Directory.CreateDirectory(app);
        try
        {
            var owner = modular ? Path.Combine(root, "Plugins", "efficiency") : app;
            Directory.CreateDirectory(owner);
            Assert.Equal(Path.Combine(owner, "tools", "pane", "Pane.Native.dll"), ToolShortcutCatalog.DoubleAltLibrary(app));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void CaptureOnlyInstallOwnsItsPauseResumeHook()
    {
        var root = Path.Combine(Path.GetTempPath(), "SeeinCaptureHook-" + Guid.NewGuid().ToString("N"));
        var app = Path.Combine(root, "App");
        var capture = Path.Combine(root, "Plugins", "capture");
        Directory.CreateDirectory(app); Directory.CreateDirectory(capture);
        try
        {
            var path = Path.Combine(capture, "Pane.Native.dll"); File.WriteAllText(path, "fixture");
            Assert.Equal(path, ToolShortcutCatalog.DoubleAltLibrary(app));
        }
        finally { Directory.Delete(root, true); }
    }
}
