using System.Diagnostics;
using System.Reflection;
using Microsoft.Extensions.Logging.Abstractions;
using Seein.Core;
using Seein.Service;

namespace Seein.Tests;

[Collection(ProcessIntegrationCollection.Name)]
public sealed class RgbEngineHostTests
{
    [Fact]
    public async Task ExitedNativeChildWithoutCachedHandleDoesNotBlockRecovery()
    {
        // No windows or keyboard input: this child only sleeps briefly and exits.
        var executable=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),"WindowsPowerShell","v1.0","powershell.exe");
        using var child=OwnedProcess.Start(executable,"-NoProfile -NonInteractive -Command \"Start-Sleep -Milliseconds 300; exit 7\"",hidden:true);
        var host=new RgbEngineHost(NullLogger<RgbEngineHost>.Instance);
        var owner=typeof(RgbEngineHost).GetField("_owner",BindingFlags.Instance|BindingFlags.NonPublic)!;
        owner.SetValue(host,child);
        try
        {
            // Observe through separate handles, leaving the adopted Process uncached.
            using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(8));
            while(true)
            {
                try { using var probe=Process.GetProcessById(child.Process.Id);if(probe.HasExited)break; }
                catch(ArgumentException){break;}
                await Task.Delay(30,deadline.Token);
            }
            Assert.True(RgbEngineHost.ReadExitCode(child.Process) is null or 7);
            Assert.False(host.EnsureReady(autoStart:false));
            Assert.Null(owner.GetValue(host));
        }
        finally{host.Stop();}
    }
}
