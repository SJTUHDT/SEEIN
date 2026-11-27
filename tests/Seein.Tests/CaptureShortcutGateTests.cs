using Seein.Core.Tools;
using System.Text.Json;

namespace Seein.Tests;

public sealed class CaptureShortcutGateTests
{
    [Fact]
    public void PauseBlocksEveryCaptureActionExceptItsOwnResumeSwitch()
    {
        var paused = new CaptureShortcutGate(false);
        Assert.All(ToolCatalog.Actions.Where(a => a.Id.StartsWith("snow.") && a.Id != CaptureShortcutGate.ToggleAction), a => Assert.False(paused.Allows(a.Id)));
        Assert.True(paused.Allows(CaptureShortcutGate.ToggleAction));
        Assert.True(paused.Allows("pane.clipboard"));
        Assert.True(paused.Allows("pane.launcher"));
        Assert.All(ToolCatalog.Actions, a => Assert.True(new CaptureShortcutGate().Allows(a.Id)));
    }

    [Fact]
    public void RightAltDefaultsAndLeftAltClipboardAreDistinctGestures()
    {
        var right = CaptureShortcutGate.DefaultBinding(CaptureShortcutGate.ToggleAction);
        var left = new ShortcutBinding("pane.clipboard", DoubleAlt: true);
        Assert.True(right.DoubleRightAlt); Assert.True(right.Valid); Assert.False(right.Empty);
        ShortcutValidation.Validate([left, right, new("snow.capture", 1, 0x41)]);
        Assert.Equal(right, JsonSerializer.Deserialize<ShortcutBinding>(JsonSerializer.Serialize(right)));
        Assert.False(new ShortcutBinding("pane.clipboard") { DoubleRightAlt = true }.Valid);
        Assert.False(new ShortcutBinding(CaptureShortcutGate.ToggleAction, DoubleAlt: true) { DoubleRightAlt = true }.Valid);
        Assert.False(new ShortcutBinding(CaptureShortcutGate.ToggleAction, 1, 65) { DoubleRightAlt = true }.Valid);
        var legacy = JsonSerializer.Deserialize<ShortcutBinding>("{\"ActionId\":\"pane.clipboard\",\"DoubleAlt\":true}")!;
        Assert.True(legacy.DoubleAlt); Assert.False(legacy.DoubleRightAlt); Assert.True(legacy.Valid);
        Assert.NotNull(typeof(ShortcutBinding).GetConstructor([typeof(string), typeof(uint), typeof(uint), typeof(bool), typeof(bool)]));
    }

    [Fact]
    public void RestartKeepsPauseWithoutRewritingOriginalBindings()
    {
        var directory = Path.Combine(Path.GetTempPath(), "SeeinCaptureGate-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "capture-shortcuts.json");
            var bindings = Path.Combine(directory, "shortcuts.json");
            const string original = "[{\"ActionId\":\"snow.capture\",\"Modifiers\":1,\"Key\":65}]";
            File.WriteAllText(bindings, original);
            Assert.True(CaptureShortcutGate.Load(path).Enabled);
            Assert.False(CaptureShortcutGate.Load(path).ToggleAndSave(path).Enabled);
            Assert.False(CaptureShortcutGate.Load(path).Enabled);
            Assert.True(CaptureShortcutGate.Load(path).ToggleAndSave(path).Enabled);
            Assert.True(CaptureShortcutGate.Load(path).Enabled);
            Assert.Equal(original, File.ReadAllText(bindings));
            Assert.Empty(Directory.GetFiles(directory, "*.tmp"));
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public void FailedSaveLeavesTheCurrentStateUsable()
    {
        var path = Path.Combine(Path.GetTempPath(), "SeeinCaptureGate-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        try
        {
            var gate = new CaptureShortcutGate();
            var error = Record.Exception(() => gate.ToggleAndSave(path));
            Assert.True(error is IOException or UnauthorizedAccessException);
            Assert.True(gate.Enabled);
        }
        finally { Directory.Delete(path, true); }
    }
}
