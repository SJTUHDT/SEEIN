using Seein.Core;
using Seein.SmartHome;

namespace Seein.Tests;

public sealed class PublicDefaultsTests
{
    [Fact] public void FreshInstallDoesNotContactPresetLanAddresses()
    {
        var settings = new SmartHomeSettings();
        Assert.Empty(settings.RouterAddress); Assert.Empty(settings.LampAddress);
    }
    [Fact] public void UnconfiguredDeviceSettingsCanBePersisted()
    {
        var directory=Directory.CreateTempSubdirectory("Seein-public-settings-");
        try { var store=new SmartHomeStore(directory.FullName);store.SaveSettings(new());Assert.Empty(store.LoadSettings().RouterAddress); }
        finally { directory.Delete(true); }
    }
    [Fact] public void CompanionDefaultDirectoryBelongsToCurrentUser() =>
        Assert.StartsWith(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),CompanionConnectionSettings.Defaults.WorkbenchRoot);
    [Theory]
    [InlineData("https://127.0.0.1:6185/")]
    [InlineData("http://example.com:6185/")]
    [InlineData("http://user:password@127.0.0.1:6185/")]
    public void CompanionSettingsDoNotForwardLocalAuthenticationToRemoteHosts(string address) =>
        Assert.Throws<ArgumentException>(() => (CompanionConnectionSettings.Defaults with { AstrBotAddress=address }).Validate());
    [Fact] public void CompanionPortsMustBeDistinct() =>
        Assert.Throws<ArgumentException>(() => (CompanionConnectionSettings.Defaults with { QqAddress=CompanionConnectionSettings.Defaults.AstrBotAddress }).Validate());
}
