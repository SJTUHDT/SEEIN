using Seein.Core.Tools;
using System.Text.Json.Nodes;

namespace Seein.Tests;

public sealed class ToolSettingsSnapshotStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "Seein-SnapshotTest-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
    private ToolSettingsSnapshotStore Store => new(_root);
    private static ToolField Value(string text = "value") => new("setting", "设置", "通用", "string", JsonValue.Create(text));
    [Fact] public void RoundTripRetainsFieldMetadataAndValues()
    {
        Store.Save("snow", "engine1", [Value() with { Minimum = 1, Maximum = 20, Description = "说明" }]);
        var loaded = Assert.Single(Store.Load("snow", "engine1")!);
        Assert.Equal("value", loaded.Value!.GetValue<string>()); Assert.Equal(20, loaded.Maximum); Assert.Equal("说明", loaded.Description);
    }
    [Fact] public void EngineChangeInvalidatesOldSnapshot()
    { Store.Save("pane", "old", [Value()]); Assert.Null(Store.Load("pane", "new")); }
    [Fact] public void SecretsNeverEnterTheDiskSnapshot()
    {
        Store.Save("snow", "engine", [Value("private-secret-token") with { Kind = "secret" }]);
        Assert.DoesNotContain("private-secret-token", File.ReadAllText(Path.Combine(_root, "snow", "settings-ui-cache.json")));
        Assert.Null(Assert.Single(Store.Load("snow", "engine")!).Value);
    }
    [Fact] public void CorruptSnapshotFallsBackToLiveReading()
    {
        Directory.CreateDirectory(Path.Combine(_root, "snow")); File.WriteAllText(Path.Combine(_root, "snow", "settings-ui-cache.json"), "{");
        Assert.Null(Store.Load("snow", "engine"));
    }
    [Fact] public void UnchangedReadsDoNotRewriteTheCache()
    {
        Store.Save("snow", "engine", [Value()]); var file = Path.Combine(_root, "snow", "settings-ui-cache.json");
        var marker = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc); File.SetLastWriteTimeUtc(file, marker);
        Store.Save("snow", "engine", [Value()]); Assert.Equal(marker, File.GetLastWriteTimeUtc(file));
    }
    [Fact] public async Task ConcurrentSavesProduceACompleteSnapshot()
    {
        var store = Store;
        await Task.WhenAll(Enumerable.Range(0, 8).Select(i => Task.Run(() => store.Save("pane", "engine", [Value(i.ToString())]))));
        Assert.Single(store.Load("pane", "engine")!); Assert.False(File.Exists(Path.Combine(_root, "pane", "settings-ui-cache.json.tmp")));
    }
}
