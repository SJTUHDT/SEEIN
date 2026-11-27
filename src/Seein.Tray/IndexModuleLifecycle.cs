using Microsoft.Win32;
using System.Diagnostics;
using System.IO;
using System.ServiceProcess;
namespace Seein.Tray;
// The efficiency plugin owns an index service only when both registry ownership and its image path agree.
internal sealed class IndexModuleLifecycle : IDisposable
{
    private const string ServiceName="Everything";
    private const string DependencyKey=@"SOFTWARE\SEEIN\Dependencies";
    private readonly string _root;
    private readonly bool _existed,_owned,_running;
    private readonly string? _image;
    private readonly int _start;
    private readonly object? _providerBefore,_managedBefore;
    public IndexModuleLifecycle(string root)
    {
        _root=root;using var key=Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\Everything");
        _existed=key is not null;_image=key?.GetValue("ImagePath") as string;_start=key?.GetValue("Start") is int start?start:3;
        using var dependencies=Registry.LocalMachine.OpenSubKey(DependencyKey);_providerBefore=dependencies?.GetValue("EverythingExecutable");_managedBefore=dependencies?.GetValue("EverythingServiceManaged");
        _owned=_managedBefore is int managed&&managed==1&&new[]{Path.Combine(root,"Plugins","efficiency","tools","everything","seeeverything.exe"),Path.Combine(root,"App","tools","everything","seeeverything.exe")}.Any(path=>_image?.StartsWith("\""+path+"\"",StringComparison.OrdinalIgnoreCase)==true);
        if(_existed){using var service=new ServiceController(ServiceName);_running=service.Status!=ServiceControllerStatus.Stopped;}
    }
    public void StopOwned(){if(!_owned)return;using var service=new ServiceController(ServiceName);if(service.Status!=ServiceControllerStatus.Stopped){service.Stop();service.WaitForStatus(ServiceControllerStatus.Stopped,TimeSpan.FromSeconds(30));}}
    public async Task ApplyAsync(bool enabled)
    {
        if(!enabled){if(_owned)await RunAsync("sc.exe",["config",ServiceName,"start=","disabled"]);return;}
        var script=Path.Combine(_root,"Plugins","efficiency","tools","everything","Install-Dependency.ps1");
        await RunAsync("powershell.exe",["-NoProfile","-ExecutionPolicy","Bypass","-File",script,"-InstallParent",Directory.GetParent(_root)!.FullName]);
    }
    public async Task CompleteRemovalAsync(){if(!_owned)return;await RunAsync("sc.exe",["delete",ServiceName]);using var key=Registry.LocalMachine.OpenSubKey(DependencyKey,true);key?.DeleteValue("EverythingExecutable",false);key?.DeleteValue("EverythingServiceManaged",false);}
    public async Task RestoreAsync()
    {
        if(_owned)
        {
            using var existing=Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\Everything");
            await RunAsync("sc.exe",[existing is null?"create":"config",ServiceName,"binPath=",_image!,"start=",_start==2?"auto":_start==4?"disabled":"demand"]);
            if(_running){using var service=new ServiceController(ServiceName);service.Start();service.WaitForStatus(ServiceControllerStatus.Running,TimeSpan.FromSeconds(30));}
        }
        else if(!_existed&&Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\Everything") is { } created)
        {
            created.Dispose();using(var service=new ServiceController(ServiceName)){if(service.Status!=ServiceControllerStatus.Stopped){service.Stop();service.WaitForStatus(ServiceControllerStatus.Stopped,TimeSpan.FromSeconds(30));}}
            await RunAsync("sc.exe",["delete",ServiceName]);
        }
        using var key=Registry.LocalMachine.CreateSubKey(DependencyKey);
        if(_providerBefore is null)key.DeleteValue("EverythingExecutable",false);else key.SetValue("EverythingExecutable",_providerBefore);
        if(_managedBefore is null)key.DeleteValue("EverythingServiceManaged",false);else key.SetValue("EverythingServiceManaged",_managedBefore);
    }
    private static async Task RunAsync(string executable,string[] arguments)
    {
        var start=new ProcessStartInfo(executable){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true};foreach(var argument in arguments)start.ArgumentList.Add(argument);
        using var process=Process.Start(start)??throw new IOException("无法启动索引维护。");var output=process.StandardOutput.ReadToEndAsync();var errors=process.StandardError.ReadToEndAsync();await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(45));
        await output;var error=await errors;if(process.ExitCode!=0)throw new IOException("索引组件维护失败："+error);
    }
    public void Dispose(){}
}
