using Seein.Core;
using Seein.Core.Apps;

namespace Seein.Tests;

public sealed class RuntimeHostResolutionTests
{
    private static ProcessSample Process(int pid, int parent, string exe, string path) =>
        new(pid, parent, exe, path, 1, 0, 0);

    private static IReadOnlyDictionary<int, string> Resolve(
        (ProcessSample Process, string? CommandLine)[] entries)
    {
        var resolver = new AppIdentityResolver();
        var commandLines = entries.ToDictionary(entry => entry.Process.Pid, entry => entry.CommandLine);
        return resolver.Resolve(entries.Select(entry => entry.Process).ToArray(),
            process => commandLines.TryGetValue(process.Pid, out var value) ? value : null);
    }

    [Fact]
    public void ElectronMainProcessIsIdentifiedByItsApplicationScript()
    {
        const string exe = @"C:\Users\U\.dsh\electron\electron.exe";
        var keys = Resolve([
            (Process(10, 1, "electron.exe", exe),
                $@"""{exe}"" C:\Users\U\.dsh\profiles\web\node_modules\dsh-pet\runtime\electron-helper\main.js")
        ]);

        Assert.EndsWith(@"\dsh-pet", keys[10]);
        Assert.DoesNotContain("electron.exe", keys[10]);
    }

    [Fact]
    public void ElectronRendererAndGpuHelpersShareTheMainApplicationKey()
    {
        const string exe = @"C:\Users\U\.dsh\electron\electron.exe";
        var keys = Resolve([
            (Process(10, 1, "electron.exe", exe),
                $@"""{exe}"" C:\Users\U\.dsh\profiles\web\node_modules\dsh-pet\runtime\electron-helper\main.js"),
            (Process(11, 10, "electron.exe", exe),
                $@"""{exe}"" --type=renderer --app-path=""C:\Users\U\.dsh\profiles\web\node_modules\dsh-pet\runtime\electron-helper"""),
            (Process(12, 10, "electron.exe", exe),
                $@"""{exe}"" --type=gpu-process --user-data-dir=""C:\Users\U\AppData\Roaming\dsh-pet-electron-helper""")
        ]);

        Assert.Equal(keys[10], keys[11]);
        Assert.Equal(keys[10], keys[12]);
    }

    [Fact]
    public void VendorDownloaderMergesIntoTheLauncherItReportsAsParent()
    {
        const string wegame = @"D:\Program Files (x86)\WeGame\wegame.exe";
        var keys = Resolve([
            (Process(20, 1, "wegame.exe", wegame), null),
            (Process(21, 20, "TinyDL64.exe", @"D:\Program Files (x86)\WeGame\TinyDL64.exe"),
                $@"session=2 uid=541143874 parent=""{wegame}""")
        ]);

        Assert.Equal(keys[20], keys[21]);
        Assert.EndsWith(@"\wegame", keys[21]);
    }

    [Fact]
    public void NodeRuntimeIsIdentifiedByItsProjectFolder()
    {
        const string exe = @"C:\dafeiyu\SnowLuma\node.exe";
        var keys = Resolve([
            (Process(30, 1, "node.exe", exe), @".\node.exe  .\index.mjs")
        ]);

        Assert.Equal(@"c:\dafeiyu\snowluma", keys[30]);
    }

    [Fact]
    public void UnresolvableRuntimeHostFallsBackToItsProductFolder()
    {
        const string exe = @"C:\Apps\SomeElectron\electron.exe";
        var keys = Resolve([(Process(40, 1, "electron.exe", exe), null)]);

        Assert.Equal(@"c:\apps\someelectron", keys[40]);
    }

    [Fact]
    public void RuntimeHostRemapIsReportedOnce()
    {
        var resolver = new AppIdentityResolver();
        const string exe = @"C:\Users\U\.dsh\electron\electron.exe";
        var process = Process(10, 1, "electron.exe", exe);
        resolver.Resolve([process],
            _ => $@"""{exe}"" C:\Users\U\.dsh\profiles\web\node_modules\dsh-pet\runtime\electron-helper\main.js");

        Assert.True(resolver.PendingRemaps.TryGetValue(exe.ToLowerInvariant(), out var target));
        Assert.EndsWith(@"\dsh-pet", target);
    }
}
