using Seein.Core;
using Seein.Core.Apps;
using Seein.Core.Storage;

namespace Seein.Tests;

public sealed class AppIdentityResolverTests
{
    private static ProcessSample Process(int pid, int parent, string exe, string? path = null, int session = 1) =>
        new(pid, parent, exe, path ?? $@"C:\Program Files\Demo\{exe}", session, 0, 0);

    private static IReadOnlyDictionary<int, string> Resolve(params ProcessSample[] processes)
    {
        var resolver = new AppIdentityResolver();
        return resolver.Resolve(processes);
    }

    [Fact]
    public void ProcessesOfTheSameExecutableShareOneApplication()
    {
        var keys = Resolve(
            Process(10, 1, "quark.exe"),
            Process(11, 10, "quark.exe"),
            Process(12, 10, "quark.exe"));

        Assert.Equal(keys[10], keys[11]);
        Assert.Equal(keys[10], keys[12]);
    }

    [Fact]
    public void HelperProcessIsMergedIntoItsHostApplication()
    {
        var keys = Resolve(
            Process(20, 1, "steam.exe"),
            Process(21, 20, "steamwebhelper.exe"),
            Process(22, 20, "crashpad_handler.exe"));

        Assert.Equal(keys[20], keys[21]);
        Assert.Equal(keys[20], keys[22]);
    }

    [Fact]
    public void InstalledCompanionKeepsOneKeyWhenItsParentIsMissing()
    {
        const string owner = @"d:\program files\netease\gameviewer\bin\gameviewer.exe";
        const string companion = @"d:\program files\netease\gameviewer\bin\crashpad_handler.exe";
        var resolver = new AppIdentityResolver();
        var keys = resolver.Resolve(
        [
            Process(20, 1, "gameviewer.exe", owner),
            Process(21, 20, "crashpad_handler.exe", companion),
            Process(22, 99, "crashpad_handler.exe", companion),
            Process(23, 20, "crashpad_handler.exe", companion, session: 0)
        ]);

        Assert.Equal(owner, keys[20]);
        Assert.Equal(companion, keys[21]);
        Assert.Equal(companion, keys[22]);
        Assert.Equal(AppStatKeys.System, keys[23]);
        Assert.DoesNotContain(companion, resolver.PendingRemaps.Keys);
        var meta = new[] { owner, companion }.ToDictionary(key => key, key => new AppMetaRow(key, key, key, "auto"));
        Assert.Equal(owner, Assert.Single(AppCompanionRules.Match(meta)).Value);
    }

    [Fact]
    public void HelperWithAnUnexpectedParentStaysSeparate()
    {
        var keys = Resolve(
            Process(30, 1, "notepad.exe"),
            Process(31, 30, "steamwebhelper.exe"));

        Assert.NotEqual(keys[30], keys[31]);
    }

    [Fact]
    public void ShellLaunchDoesNotCollapseApplicationsIntoSingleEntry()
    {
        var keys = Resolve(
            Process(40, 1, "explorer.exe", @"C:\Windows\explorer.exe"),
            Process(41, 40, "Weixin.exe"),
            Process(42, 40, "QQ.exe"));

        Assert.Equal(AppStatKeys.System, keys[40]);
        Assert.True(keys[41] != keys[42]);
        Assert.NotEqual(AppStatKeys.System, keys[41]);
    }

    [Fact]
    public void SessionZeroProcessesAreSystem()
    {
        var keys = Resolve(Process(50, 1, "svchost.exe", @"C:\Windows\System32\svchost.exe", 0));
        Assert.Equal(AppStatKeys.System, keys[50]);
    }

    [Fact]
    public void OwnProcessesAreExcluded()
    {
        var keys = Resolve(
            Process(60, 1, "Seein.Service.exe", @"C:\Program Files\Seein\App\Seein.Service.exe"),
            Process(61, 1, "Seein.Tray.exe", @"C:\Program Files\Seein\App\Seein.Tray.exe"));

        Assert.Equal(AppStatKeys.Self, keys[60]);
        Assert.Equal(AppStatKeys.Self, keys[61]);
    }

    [Fact]
    public void WindowsUserToolsAreNotTreatedAsSystem()
    {
        var keys = Resolve(Process(70, 1, "notepad.exe", @"C:\Windows\System32\notepad.exe"));
        Assert.NotEqual(AppStatKeys.System, keys[70]);
    }

    [Fact]
    public void DisplayNamesFallBackToExecutableName()
    {
        Assert.Equal("微信", AppNameCatalog.DisplayName("Weixin.exe"));
        Assert.Equal("微信", AppNameCatalog.DisplayName(@"C:\Program Files\Tencent\Weixin\Weixin.exe"));
        Assert.Equal("betterleaks", AppNameCatalog.DisplayName(@"D:\Tools\betterleaks.exe"));
        Assert.Equal("未知应用", AppNameCatalog.DisplayName(null));
    }
}
