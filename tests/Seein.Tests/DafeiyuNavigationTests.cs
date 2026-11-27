using Seein.Core;

namespace Seein.Tests;

public sealed class DafeiyuNavigationTests
{
    [Theory]
    [InlineData("http://127.0.0.1:6185/#/conversation", DafeiyuPage.AstrBot)]
    [InlineData("http://localhost:5099/", DafeiyuPage.SnowLuma)]
    public void ManagementLinksKeepDeepLinksInsideWorkbench(string url, DafeiyuPage expected)
    {
        var original = new Uri(url);
        Assert.Equal(expected, DafeiyuNavigation.ManagementPage(original));
        var canonical = DafeiyuNavigation.CanonicalManagementAddress(original);
        Assert.Equal(original.PathAndQuery + original.Fragment, canonical.PathAndQuery + canonical.Fragment);
        Assert.Equal("127.0.0.1", canonical.Host);
    }

    [Theory]
    [InlineData("https://127.0.0.1:6185/")]
    [InlineData("http://127.0.0.1:6199/")]
    [InlineData("http://127.0.0.1.example.com:6185/")]
    [InlineData("http://untrusted@127.0.0.1:6185/")]
    [InlineData("file:///C:/workbench/index.html")]
    public void UnrelatedOriginsCannotUseManagementTabs(string url)
    {
        var address = new Uri(url);
        Assert.Null(DafeiyuNavigation.ManagementPage(address));
        Assert.False(DafeiyuNavigation.SameOrigin(address, DafeiyuNavigation.AstrBot));
    }

    [Fact]
    public async Task MissingWorkbenchFailsBeforeLaunchingAnything()
    {
        var missingRoot = Path.Combine(Path.GetTempPath(), "seein-missing-" + Guid.NewGuid().ToString("N"));
        var workbench = new DafeiyuWorkbench(missingRoot);
        await Assert.ThrowsAsync<DafeiyuWorkbenchException>(() => workbench.ConnectAsync(CancellationToken.None));
    }

    [Fact]
    public async Task ClosedModuleCannotLaunchAnotherWorkbench()
    {
        var workbench = new DafeiyuWorkbench(Path.GetTempPath());
        workbench.Dispose();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => workbench.ConnectAsync(CancellationToken.None));
    }
}
