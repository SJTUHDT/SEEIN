using System.ComponentModel;
using System.Diagnostics;
using Seein.Core;

namespace Seein.Tests;

public sealed class OwnedProcessTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ClosingOwnerReapsImmediateGrandchild(bool allowBreakaway)
    {
        var folder = Directory.CreateTempSubdirectory("Seein-job-");
        var marker = Path.Combine(folder.FullName, "child.pid");
        var shell = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
        var start = new ProcessStartInfo(shell);
        start.Environment["SEEIN_TEST_MARKER"] = marker;
        const string command = "-NoProfile -NonInteractive -Command \"$child = Start-Process -WindowStyle Hidden -PassThru powershell.exe '-NoProfile -NonInteractive -Command Start-Sleep 60'; [IO.File]::WriteAllText($env:SEEIN_TEST_MARKER, $child.Id); Start-Sleep 60\"";
        try
        {
            using var owned = OwnedProcess.Start(shell, command, start.Environment, true, allowBreakaway);
            using var parent = Process.GetProcessById(owned.Process.Id);
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            while (!File.Exists(marker) || new FileInfo(marker).Length == 0)
                await Task.Delay(50, deadline.Token);
            using var child = Process.GetProcessById(int.Parse(await File.ReadAllTextAsync(marker, deadline.Token)));
            owned.TerminateTree();
            await Task.WhenAll(parent.WaitForExitAsync(deadline.Token), child.WaitForExitAsync(deadline.Token));
        }
        finally { folder.Delete(true); }
    }

    [Fact]
    public void MissingExecutableReportsLaunchFailure() =>
        Assert.Throws<Win32Exception>(() => OwnedProcess.Start(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".exe")));
}
