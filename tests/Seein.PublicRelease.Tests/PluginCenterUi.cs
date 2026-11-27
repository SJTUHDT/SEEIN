using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Seein.Contracts;
using Seein.Modularity;
using Seein.Tray;

internal static class PluginCenterUi
{
    public static int Run(string root,string[] args)
    {
        var output=Path.Combine(root,".build/plugin-center-ui");Directory.CreateDirectory(output);var checks=0;
        void Check(bool pass,string name){if(!pass)throw new InvalidOperationException(name);checks++;}
        foreach(var name in new[]{"Theme.Light.xaml","Controls.xaml"})Application.Current.Resources.MergedDictionaries.Add(new(){Source=new Uri(Path.Combine(root,"src/Seein.Tray/Styles",name))});
        using var definitions=JsonDocument.Parse(File.ReadAllText(Path.Combine(root,"config/plugin-definitions.json")));
        var entries=definitions.RootElement.EnumerateArray().Select(p=>new PluginListing(p.GetProperty("id").GetString()!,p.GetProperty("name").GetString()!,p.GetProperty("group").GetString()!,p.GetProperty("description").GetString()!,"2.2.0",p.GetProperty("id").GetString()+".seeinplugin",1024*1024,2*1024*1024,new('a',64),p.GetProperty("dependencies").EnumerateArray().Select(x=>x.GetString()!).ToArray(),p.GetProperty("selectable").GetBoolean())).ToArray();
        var feed=new PluginFeed(1,"v2.2.0","2.2.0",[new("display","副屏与主题"),new("applications","应用监控"),new("devices","设备控制"),new("tools","小工具")],entries);
        var release=Path.Combine(root,"artifacts/releases/v2.2.0");
        if(args.Contains("--packages"))
        {
            var full=Path.Combine(release,"Seein-win-x64-2.2.0-PublicFull");var core=Path.Combine(release,"Seein-win-x64-2.2.0-Core");
            using var source=new PluginFeedClient(output,File.ReadAllText(Path.Combine(full,"App/module-publisher.pem")));
            feed=source.Read(Path.Combine(release,"plugin-catalog.json"),Path.Combine(release,"plugin-catalog.sig"));
            var modules=new ModuleCatalog(full,Path.Combine(output,"nonexistent.json")).Read();Check(modules.Count==18&&modules.All(m=>m.Enabled&&m.Error is null),"18 signed packages verified");Check(feed.Packages.Count(p=>p.Selectable)==14,"14 independent choices");Check(new ModuleCatalog(core,Path.Combine(output,"nonexistent.json")).Read().Count==0,"core has no optional modules");
            var key=File.ReadAllText(Path.Combine(full,"App/module-publisher.pem"));var unpack=Directory.CreateTempSubdirectory("Seein-220-packages-");
            try
            {
                foreach(var m in modules)
                {
                    var directory=Path.Combine(unpack.FullName,m.Manifest.Id);
                    var imported=ModulePackage.ExtractVerified(Path.Combine(release,"Plugins-PublicFull",m.Manifest.Id+"-2.2.0.seeinplugin"),directory,key,HostVersion.Current);Check(imported.Id==m.Manifest.Id,"verified import "+m.Manifest.Id);
                    if(m.Manifest.Desktop is {} entry)
                    {
                        var context=new ModuleLoadContext(m.Directory,entry.Assembly);var module=context.Create<IDesktopModule>(entry);
                        Check(module.Pages.Any(),"desktop routes construct without startup: "+m.Manifest.Id);
                        var assembly=context.LoadFromAssemblyPath(Path.Combine(m.Directory,entry.Assembly));
                        var viewType=m.Manifest.Id switch{"devices-router"=>"Seein.Tray.RouterControlView","devices-fan"=>"Seein.Tray.FanControlView","devices-rgb"=>"Seein.Tray.RgbControlView","devices-playback"=>"Seein.Tray.CapturePlaybackView","companion-pet"=>"Seein.Tray.PetControlView",_=>null};
                        if(viewType is not null){var view=(FrameworkElement)Activator.CreateInstance(assembly.GetType(viewType,true)!)!;Check(!view.IsVisible,"invisible page constructs: "+m.Manifest.Id);if(view is IDisposable disposable)disposable.Dispose();}
                        if(m.Manifest.Id=="companion-qq")
                        {
                            var shared=System.Runtime.Loader.AssemblyLoadContext.Default.LoadFromAssemblyName(new("Seein.Companion.Web"));var view=(FrameworkElement)Activator.CreateInstance(shared.GetType("Seein.Tray.CompanionWebView",true)!)!;Check(!view.IsVisible,"shared browser constructs without starting external services");
                        }
                        // Do not initialize, start, or dispose uninitialized modules: those operations
                        // can acquire hardware, global input or application-owned processes.
                    }
                }
                foreach(var id in feed.Packages.Where(p=>p.Selectable).Select(p=>p.Id))
                {
                    var plan=PluginInstallPlan.Create(feed,[],[id]);var selected=Path.Combine(unpack.FullName,"selection-"+id);Directory.CreateDirectory(Path.Combine(selected,"App"));File.WriteAllText(Path.Combine(selected,"App/module-publisher.pem"),key);
                    foreach(var p in plan.Required)ModulePackage.ExtractVerified(Path.Combine(release,"Plugins-PublicFull",p.Asset),Path.Combine(selected,"Plugins",p.Id),key,HostVersion.Current);
                    Check(new ModuleCatalog(selected,Path.Combine(selected,"state.json")).Read().All(m=>m.Enabled&&m.Error is null),"standalone selection "+id);
                }
            }
            finally{unpack.Delete(true);}
        }
        foreach(var theme in new[]{"Light","Dark"})
        {
            Application.Current.Resources.MergedDictionaries.Clear();foreach(var name in new[]{"Theme."+theme+".xaml","Controls.xaml"})Application.Current.Resources.MergedDictionaries.Add(new(){Source=new Uri(Path.Combine(root,"src/Seein.Tray/Styles",name))});
            var manager=new ModuleManagerView(previewFeed:feed){Width=1100,Height=900};var surface=new Border{Background=(Brush)Application.Current.FindResource("WindowBrush"),Padding=new Thickness(24),Child=manager,Width=1148,Height=948};
            surface.Measure(new(1148,948));surface.Arrange(new(0,0,1148,948));surface.UpdateLayout();
            var boxes=Descendants(manager).OfType<CheckBox>().ToArray();Check(boxes.Length==18,"four groups and fourteen feature checkboxes");
            var parent=boxes.Single(b=>Equals(b.Content,"设备控制"));parent.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));Check(parent.IsChecked==true,"group selects all");parent.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));Check(parent.IsChecked==false,"group deselects all");
            var child=boxes.First(b=>b.Content is StackPanel s&&s.Children.OfType<TextBlock>().First().Text.StartsWith("米家挂灯"));child.IsChecked=true;Check(parent.IsChecked is null,"partial group state");
            // Fold the device list to show all tool choices in the documentation preview.
            foreach(var expander in Descendants(manager).OfType<Expander>().ToArray())if(expander.Header==parent)expander.IsExpanded=false;
            surface.UpdateLayout();var image=new RenderTargetBitmap(1148,948,96,96,PixelFormats.Pbgra32);image.Render(surface);var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(image));using(var file=File.Create(Path.Combine(output,"plugin-center-"+theme.ToLowerInvariant()+".png")))encoder.Save(file);
        }
        Console.WriteLine($"PASS: {checks} plugin center UI/package checks; no visible windows, hardware writes or global input.");return 0;
    }
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {for(var i=0;i<VisualTreeHelper.GetChildrenCount(root);i++){var child=VisualTreeHelper.GetChild(root,i);yield return child;foreach(var nested in Descendants(child))yield return nested;}}
}
