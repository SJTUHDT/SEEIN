using Seein.Core.Tools;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace Seein.Tests;

public sealed class ToolIntegrationTests
{
    [Fact]
    public void ToolExportsAndCachesFollowConfiguredStorageRoot()
    {
        var folder = Path.Combine(Path.GetTempPath(), "SeeinStorageTest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var fallback = ToolStoragePaths.Resolve(folder);
            Assert.StartsWith(folder, fallback.Cache); Assert.Null(fallback.Pdf);
            File.WriteAllText(Path.Combine(folder, "storage-layout.json"), JsonSerializer.Serialize(new { ExportRoot = @"E:\Seein" }));
            var configured = ToolStoragePaths.Resolve(folder);
            Assert.StartsWith(folder, configured.Cache);
            Assert.Equal(@"E:\Seein\导出文件\PDF", configured.Pdf);
            Assert.StartsWith(configured.Cache, configured.Temporary);
        }
        finally { Directory.Delete(folder, true); }
    }

    [Fact]
    public void ClipboardDoubleAltPersistsAndDoesNotConflictWithAltSpace()
    {
        var gesture = new ShortcutBinding("pane.clipboard", DoubleAlt: true);
        Assert.False(gesture.Empty); Assert.True(gesture.Valid);
        ShortcutValidation.Validate([gesture, new("pane.launcher", 1, 0x20)]);
        var restored = JsonSerializer.Deserialize<ShortcutBinding>(JsonSerializer.Serialize(gesture));
        Assert.Equal(gesture, restored);
        Assert.False(new ShortcutBinding("pane.launcher", DoubleAlt: true).Valid);
        Assert.False(new ShortcutBinding("pane.clipboard", 1, 0x20, DoubleAlt: true).Valid);
        Assert.False(JsonSerializer.Deserialize<ShortcutBinding>("{\"ActionId\":\"pane.launcher\",\"Modifiers\":1,\"Key\":32,\"Enabled\":true}")!.DoubleAlt);
    }

    [Fact]
    public void LauncherAcceptsAltSpaceAndModifiedPunctuation()
    {
        Assert.True(new ShortcutBinding("pane.launcher", 1, 0x20).Valid);
        Assert.True(new ShortcutBinding("pane.launcher", 2, 0xBA).Valid);
        Assert.False(new ShortcutBinding("pane.launcher", 0, 0x20).Valid);
        Assert.False(new ShortcutBinding("pane.launcher", 1, 0x12).Valid);
        Assert.False(new ShortcutBinding("pane.launcher", 1, 0x7B).Valid);
    }

    [Theory]
    [InlineData("pane", "Launcher.SearchEngines", "launcher")]
    [InlineData("pane", "capability.clipboard", "capabilities")]
    [InlineData("snow", "interface.text-recognition.auto-copy", "recognition")]
    [InlineData("snow", "screen-recording-output.format", "storage")]
    [InlineData("snow", "screen-recording.fps", "recording")]
    public void SettingsFollowTheirOriginalFunctionalCategory(string module, string id, string expected)
        => Assert.Equal(expected, ToolSettingsNavigation.CategoryOf(module, id));

    [Fact]
    public void DisabledBindingsDoNotCollideButActiveBindingsDo()
    {
        ShortcutValidation.Validate([new("snow.capture", 2, 0x51), new("pane.launcher", 2, 0x51, false)]);
        Assert.Throws<ArgumentException>(() => ShortcutValidation.Validate([new("snow.capture", 2, 0x51), new("pane.launcher", 2, 0x51)]));
        Assert.False(new ShortcutBinding("snow.capture", 0, 0x41).Valid);
        Assert.False(new ShortcutBinding("snow.capture", 2, 0x7B).Valid);
        Assert.True(new ShortcutBinding("lamp.power", 0, 0x70).Valid);
    }

    [Fact]
    public async Task RejectsStaleToolResponseWithoutDispatchingAnotherAction()
    {
        var name = "Seein.Tool.Test." + Guid.NewGuid().ToString("N");
        await using var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        var exchange = Task.Run(async () =>
        {
            await server.WaitForConnectionAsync();
            using var reader = new StreamReader(server, Encoding.UTF8, false, 1024, true);
            using var writer = new StreamWriter(server, new UTF8Encoding(false), 1024, true) { AutoFlush = true };
            var request = JsonSerializer.Deserialize<ToolRequest>((await reader.ReadLineAsync())!, ToolCatalog.Json)!;
            Assert.Equal("settings.get", request.Operation);
            await writer.WriteLineAsync(JsonSerializer.Serialize(new ToolResponse("stale", true), ToolCatalog.Json));
        });
        await Assert.ThrowsAsync<IOException>(() => ToolPipeClient.SendAsync(name, new("current", "settings.get")));
        await exchange;
    }

    [Fact]
    public async Task CallerCancellationDoesNotBecomeAToolSuccess()
    {
        using var cancel = new CancellationTokenSource(100);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ToolPipeClient.SendAsync("Seein.Absent." + Guid.NewGuid(), new("cancel", "execute", "snow.capture"), cancel.Token));
    }
}
