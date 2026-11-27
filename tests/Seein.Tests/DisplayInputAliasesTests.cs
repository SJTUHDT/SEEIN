using Seein.Core.Displays;

namespace Seein.Tests;

public sealed class DisplayInputAliasesTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "Seein-input-alias-tests-" + Guid.NewGuid().ToString("N"));
    private string SettingsPath => Path.Combine(_root, "aliases.json");

    [Fact]
    public void PersistsNamesPerMonitorAndPhysicalPort()
    {
        var aliases = new DisplayInputAliases(SettingsPath);
        aliases.SetAlias("monitor-a", 0x11, "  Switch 2  ");
        aliases.SetAlias("monitor-a", 0x0F, "台式机");
        aliases.SetAlias("monitor-b", 0x11, "笔记本");
        var reloaded = new DisplayInputAliases(SettingsPath);
        Assert.Equal("Switch 2（HDMI 1）", reloaded.GetLabel("monitor-a", 0x11));
        Assert.Equal("台式机（DP 1）", reloaded.GetLabel("monitor-a", 0x0F));
        Assert.Equal("笔记本", reloaded.GetAlias("monitor-b", 0x11));
        Assert.Equal("HDMI 2", reloaded.GetLabel("monitor-a", 0x12));
    }

    [Fact]
    public void ClearingOneAliasPreservesOtherNamesAfterRestart()
    {
        var aliases = new DisplayInputAliases(SettingsPath);
        aliases.SetAlias("monitor-a", 0x11, "Switch 2");
        aliases.SetAlias("monitor-a", 0x12, "笔记本");
        aliases.SetAlias("monitor-a", 0x11, "  ");
        var reloaded = new DisplayInputAliases(SettingsPath);
        Assert.Equal("HDMI 1", reloaded.GetLabel("monitor-a", 0x11));
        Assert.Equal("笔记本", reloaded.GetAlias("monitor-a", 0x12));
    }

    [Fact]
    public void InvalidNameCannotOverwritePreviouslySavedData()
    {
        var aliases = new DisplayInputAliases(SettingsPath);
        aliases.SetAlias("monitor-a", 0x11, "Switch 2");
        var before = File.ReadAllText(SettingsPath);
        Assert.Throws<ArgumentException>(() => aliases.SetAlias("monitor-a", 0x11, new string('a', 65)));
        Assert.Throws<ArgumentException>(() => aliases.SetAlias("monitor-a", 0x11, "line\nbreak"));
        Assert.Equal(before, File.ReadAllText(SettingsPath));
        Assert.Equal("Switch 2", aliases.GetAlias("monitor-a", 0x11));
    }

    [Fact]
    public void MalformedExistingFileIsPreserved()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(SettingsPath, "{broken-json");
        var aliases = new DisplayInputAliases(SettingsPath);
        Assert.NotNull(aliases.LoadError);
        Assert.Throws<IOException>(() => aliases.SetAlias("monitor-a", 0x11, "Switch 2"));
        Assert.Equal("{broken-json", File.ReadAllText(SettingsPath));
    }

    [Fact]
    public void FailedDiskWriteDoesNotChangeInMemoryAlias()
    {
        var aliases = new DisplayInputAliases(SettingsPath);
        aliases.SetAlias("monitor-a", 0x11, "Switch 2");
        using (var locked = File.Open(SettingsPath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var error = Record.Exception(() => aliases.SetAlias("monitor-a", 0x11, "笔记本"));
            Assert.True(error is IOException or UnauthorizedAccessException);
        }
        Assert.Equal("Switch 2", aliases.GetAlias("monitor-a", 0x11));
        Assert.Equal("Switch 2", new DisplayInputAliases(SettingsPath).GetAlias("monitor-a", 0x11));
    }

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
}
