using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Seein.Contracts;
using Seein.Core;
using Seein.Modularity;
using Seein.Tray;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        var root=Path.GetFullPath(args.Length>0?args[0]:Directory.GetCurrentDirectory());
        var temp=Directory.CreateTempSubdirectory("Seein-public-release-");
        Environment.SetEnvironmentVariable("SEEIN_INSTANCE","public-release-tests");
        Environment.SetEnvironmentVariable("SEEIN_PREVIEW_USER_DATA",Path.Combine(temp.FullName,"user"));
        Environment.SetEnvironmentVariable("SEEIN_PREVIEW_SYSTEM_DATA",Path.Combine(temp.FullName,"system"));
        var checks=0;
        void Check(bool pass,string name){if(!pass)throw new InvalidOperationException(name);checks++;}
        var app=new Application();
        try
        {
            if(args.Contains("--v220"))return PluginCenterUi.Run(root,args);
            foreach(var file in new[]{"Theme.Light.xaml","Controls.xaml"})app.Resources.MergedDictionaries.Add(new(){Source=new Uri(Path.Combine(root,"src/Seein.Tray/Styles",file))});
            using(var router=new RouterControlView())
            {
                Check(((TextBox)router.FindName("RouterAddress")).Text=="","fresh router has an editable empty address");
                Check(!router.IsVisible,"router construction does not connect a browser");
            }
            var type=typeof(DafeiyuView).Assembly.GetType("Seein.Tray.CompanionConnectionDialog",true)!;
            var owner=new Window();
            _ = new System.Windows.Interop.WindowInteropHelper(owner).EnsureHandle();
            var dialog=(Window)Activator.CreateInstance(type,BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic,null,[owner],null)!;
            Check(dialog.Title=="大肥鱼连接设置","connection settings entry constructs");
            Check(((StackPanel)dialog.Content).Children.OfType<TextBox>().Count()==3,"directory and two backend addresses are editable");
            Check(!dialog.IsVisible,"settings test shows no window");dialog.Close();owner.Close();

            if(args.Contains("--packages"))
            {
                var release=Path.Combine(root,"artifacts/releases/v2.1.0");
                var full=Path.Combine(release,"Seein-win-x64-2.1.0-PublicFull");
                var core=Path.Combine(release,"Seein-win-x64-2.1.0-Core");
                var states=Path.Combine(temp.FullName,"modules.json");
                var modules=new ModuleCatalog(full,states).Read();
                Check(modules.Count==6&&modules.All(m=>m.Enabled&&m.Error is null&&!m.Manifest.Private),"six public signatures and file manifests verified");
                Check(new ModuleCatalog(core,states).Read().Count==0,"core has no optional payloads");
                var key=File.ReadAllText(Path.Combine(full,"App/module-publisher.pem"));
                foreach(var module in modules)
                {
                    var unpack=Path.Combine(temp.FullName,"unpack-"+module.Manifest.Id);
                    var verified=ModulePackage.ExtractVerified(Path.Combine(release,"Plugins-PublicFull",module.Manifest.Id+"-2.1.0.seeinplugin"),unpack,key,new Version(2,1,0));
                    Check(verified.Id==module.Manifest.Id,"offline package import: "+verified.Id);
                }
                for(var mask=0;mask<64;mask++)
                {
                    var enabled=modules.Select((m,i)=>(m.Manifest.Id,Enabled:(mask&(1<<i))!=0)).ToDictionary(m=>m.Id,m=>new ModuleState(m.Enabled));
                    File.WriteAllText(states,JsonSerializer.Serialize(enabled,ModulePackage.Json));
                    var selected=new ModuleCatalog(full,states).Read(verify:false);
                    Check(selected.All(m=>m.Error is null&&m.Enabled==enabled[m.Manifest.Id].Enabled),"module selection combination "+mask);
                }
            }
            Console.WriteLine($"PASS: {checks} public release checks; no hardware, global input, visible windows or installed-data writes.");
            return 0;
        }
        catch(Exception ex){Console.Error.WriteLine(ex);return 1;}
        finally{app.Shutdown();temp.Delete(true);}
    }
}
