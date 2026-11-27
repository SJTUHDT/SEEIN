using System.IO;
using System.Reflection;
using System.Runtime.Loader;
using Seein.Core;
using Seein.Modularity;
namespace Seein.Bootstrap;
public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        AssemblyLoadContext.Default.Resolving+=(_,name)=>
        {
            var path=Path.Combine(AppContext.BaseDirectory,name.Name+".dll");
            return File.Exists(path)?AssemblyLoadContext.Default.LoadFromAssemblyPath(path):null;
        };
        var helper=Path.GetFileNameWithoutExtension(Environment.ProcessPath);
        try
        {
            if(helper=="seepet")return RunModule("companion","seepet.dll","Seein.PetHost.Program","Main",[args]);
            if(helper=="seedisplay"||args.Contains("--display-host"))return RunModule("devices","Seein.Desktop.Devices.dll","Seein.Tray.DisplayNativeHost","Run",null);
            var tray=AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.Combine(AppContext.BaseDirectory,"Seein.Tray.dll"));
            tray.GetType("Seein.Tray.App",true)!.GetMethod("Main",BindingFlags.Public|BindingFlags.Static)!.Invoke(null,null);return 0;
        }
        catch(Exception ex)
        {
            var directory=AppPaths.UserDefault.Logs;Directory.CreateDirectory(directory);File.AppendAllText(Path.Combine(directory,"bootstrap.log"),$"{DateTimeOffset.Now:O} {ex}\n");return 1;
        }
    }
    private static int RunModule(string id,string assembly,string type,string method,object?[]? parameters)
    {
        var root=Directory.GetParent(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar))!.FullName;
        var module=new ModuleCatalog(root,Path.Combine(AppPaths.SystemDefault.DataRoot,"modules.json")).Read().SingleOrDefault(m=>m.Manifest.Id==id&&m.Enabled&&m.Error is null)
            ??throw new InvalidDataException("所需插件未安装或未启用。");
        var context=new ModuleLoadContext(module.Directory,assembly);
        var result=context.LoadFromAssemblyPath(Path.Combine(module.Directory,assembly)).GetType(type,true)!.GetMethod(method,BindingFlags.Public|BindingFlags.Static)!.Invoke(null,parameters);
        return result is int code?code:0;
    }
}
