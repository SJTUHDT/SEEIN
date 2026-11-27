using System.IO;
using System.Text.Json;
using System.Reflection;
using System.IO.Pipes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Seein.Core;
using Seein.Core.Themes;
using Seein.Tray;

internal static class Program
{
    private static int _passed;
    [STAThread] private static int Main(string[] args)
    {
        var root=FindRoot();var data=Path.Combine(root,".build/theme-editor-work",args.Contains("--preview")?"preview-data":"ui-test-data");
        Environment.SetEnvironmentVariable("SEEIN_INSTANCE","ThemeEditorPreview");Environment.SetEnvironmentVariable("SEEIN_PREVIEW_USER_DATA",data);
        Environment.SetEnvironmentVariable("SEEIN_PREVIEW_SYSTEM_DATA",Path.Combine(data,"system"));Environment.SetEnvironmentVariable("SEEIN_DISABLE_DEVICE","1");
        Directory.CreateDirectory(data);File.WriteAllText(Path.Combine(data,"storage-layout.json"),JsonSerializer.Serialize(new{ExportRoot=Path.Combine(data,"cold")}));
        var app=new Application{ShutdownMode=ShutdownMode.OnExplicitShutdown};
        var theme=args.Contains("--dark")?"Dark":"Light";
        app.Resources.MergedDictionaries.Add(new(){Source=new Uri(Path.Combine(root,$"src/Seein.Tray/Styles/Theme.{theme}.xaml"))});
        app.Resources.MergedDictionaries.Add(new(){Source=new Uri(Path.Combine(root,"src/Seein.Tray/Styles/Controls.xaml"))});
        try
        {
            var library=new ThemeLibraryView();library.OpenEditor();var editor=library.ExistingEditor!;
            Check(editor.Document.Manifest.SchemaVersion=="1.1","editor creates editable 1.1 draft");
            var timer=(System.Windows.Threading.DispatcherTimer)typeof(ThemeEditorView).GetField("_timer",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(editor)!;
            Check(!timer.IsEnabled,"hidden editor has no periodic preview work");
            Seed(editor);
            var canvas=(ThemeEditorCanvas)editor.FindName("Canvas");canvas.Frame=editor.RenderAsync().GetAwaiter().GetResult();
            Check(canvas.Frame is not null&&Math.Abs((double)canvas.Frame.PixelHeight/canvas.Frame.PixelWidth-1920d/462)<.05,"shared renderer preserves native portrait aspect ratio");
            foreach(var width in new[]{900d,1180d,1500d})
            {
                library.Measure(new Size(width,880));library.Arrange(new Rect(0,0,width,880));library.UpdateLayout();
                var scroll=(ScrollViewer)editor.FindName("CanvasScroll");Check(scroll.ActualWidth>300,$"canvas has usable width at {width}");
                var right=(Border)editor.FindName("RightPanel");Check(right.ActualWidth>=230,$"properties fit at {width}");
            }
            canvas.Select([editor.Document.Manifest.Layers.First(l=>l.Type=="gauge").Id]);
            Check(((StackPanel)editor.FindName("Properties")).Children.OfType<ComboBox>().Any(b=>System.Windows.Automation.AutomationProperties.GetName(b)=="显示风格"),"data styles exposed in inspector");
            var count=editor.Document.Manifest.Layers.Count;Invoke(editor,"Duplicate_Click");Check(editor.Document.Manifest.Layers.Count==count+1,"duplicate retains editable element");
            Invoke(editor,"Group_Click");Check(editor.Document.Manifest.Layers.Any(l=>canvas.Selection.Contains(l.Id)&&l.GroupId is not null),"group command records selection group");
            Invoke(editor,"Lock_Click");Invoke(editor,"Delete_Click");Check(editor.Document.Manifest.Layers.Count==count+1,"delete respects locked elements");Invoke(editor,"Lock_Click");Invoke(editor,"Delete_Click");Check(editor.Document.Manifest.Layers.Count==count,"unlocked elements can be deleted");
            editor.Document.Undo();editor.Document.Redo();
            var store=new ThemeProjectStore(data);ThemeProjectStore.WriteManifest(editor.Document);var file=Path.Combine(store.ExportRoot,"ui-check.jptheme");ThemeProjectStore.Export(editor.Document,file);Check(ThemePackage.Validate(file).IsValid,"UI-created theme exports and validates");
            foreach(var dir in Directory.GetDirectories(Path.Combine(root,"themes")))
            {var loaded=ThemePackage.LoadDirectory(dir);using var renderer=new Seein.Core.Rendering.ThemeRenderer();var frame=renderer.RenderPng(loaded,ThemeMetricCatalog.Demo(DateTimeOffset.Now),null,null);Check(frame.Length>0,"legacy theme renders: "+loaded.Manifest.Id);}
            canvas.Select([]);canvas.Frame=editor.RenderAsync().GetAwaiter().GetResult();
            library.Measure(new Size(1380,980));library.Arrange(new Rect(0,0,1380,980));library.UpdateLayout();
            var bitmap=new RenderTargetBitmap(1380,980,96,96,PixelFormats.Pbgra32);bitmap.Render(library);var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using(var stream=File.Create(Path.Combine(root,$".build/theme-editor-work/editor-{theme.ToLowerInvariant()}.png")))encoder.Save(stream);
            File.WriteAllText(Path.Combine(root,".build/theme-editor-work/ui-tests.log"),$"PASS: {_passed} headless UI checks; no hardware or global input.\n");
            if(!args.Contains("--preview"))
            {
                var sample=new ThemeEditorDocument(editor.Document.Manifest,editor.Document.Root);
                sample.Update(m=>m with{Layers=Enumerable.Range(0,200).Select(i=>ThemeMetricCatalog.Create("metric") with{Id="stress-"+i,X=i%10*42,Y=i/10*80,Width=40,Height=60}).ToList()});
                var watch=System.Diagnostics.Stopwatch.StartNew();using(var renderer=new Seein.Core.Rendering.ThemeRenderer())for(int i=0;i<10;i++)renderer.RenderPreview(new(sample.Manifest,sample.Root),ThemeMetricCatalog.Demo(DateTimeOffset.Now),null,null,.4f);
                var renderMs=watch.Elapsed.TotalMilliseconds/10;watch.Restart();for(int i=0;i<20;i++)sample.Edit(["stress-0"],l=>l with{Rotation=i});
                File.WriteAllText(Path.Combine(root,".build/theme-editor-work/performance.json"),JsonSerializer.Serialize(new{elements=200,renderMs,editMs=watch.Elapsed.TotalMilliseconds/20}));
            }
            if(args.Contains("--preview"))
            {
                var window=new Window{Title="SEEIN · 主题编辑器（隔离预览）",Width=1440,Height=1000,MinWidth=960,MinHeight=720,Content=library,Background=(System.Windows.Media.Brush)app.Resources["WindowBrush"],Foreground=(System.Windows.Media.Brush)app.Resources["TextBrush"]};
                using var shutdown=new CancellationTokenSource();
                window.Closed+=(_,_)=>{shutdown.Cancel();editor.Dispose();app.Shutdown();};
                _=Task.Run(()=>ReadTelemetryAsync(editor,shutdown.Token));window.Show();app.Run();
            }
            else{editor.Dispose();app.Shutdown();}
            return 0;
        }
        catch(Exception ex){File.WriteAllText(Path.Combine(root,".build/theme-editor-work/ui-tests-error.log"),ex.ToString());return 1;}
    }
    private static void Seed(ThemeEditorView editor)
    {
        var layers=new List<ThemeLayer>();var i=0;
        void Add(string type,string name,float x,float y,float w,float h,string color,ThemeMetric? metric=null,string? text=null)
        {var l=ThemeMetricCatalog.Create(type,metric);var style=ThemeEditorDocument.Style(l,"color",color);if(type=="text")style["align"]=JsonSerializer.SerializeToElement("left");
            layers.Add(l with{Id="demo-"+i,Name=name,X=x,Y=y,Width=w,Height=h,Z=i++,Format=text??l.Format,Style=style});}
        Add("text","主题标题",24,26,414,60,"#F2F6FF",text:"SEEIN / SYSTEM");
        Add("text","副标题",26,85,410,42,"#8FABC7",text:"我的电脑 · 实时监测面板");
        Add("rect","CPU 卡片",20,150,422,360,"#182638");Add("text","CPU 标题",40,165,380,46,"#C4D6EB",text:"CPU · 处理器");
        Add("ring","CPU 温度",32,226,190,190,"#60B4FF",ThemeMetricCatalog.All[0]);Add("metric","CPU 使用率",242,240,180,80,"#FFFFFF",ThemeMetricCatalog.All[1]);
        Add("bar","CPU 负载",40,440,382,30,"#60B4FF",ThemeMetricCatalog.All[1]);
        Add("rect","GPU 卡片",20,535,422,380,"#182638");Add("text","GPU 标题",40,550,380,46,"#C4D6EB",text:"GPU · 图形处理器");
        Add("gauge","GPU 温度",38,615,190,200,"#66DFB3",ThemeMetricCatalog.All[5]);Add("metric","GPU 功率",238,630,185,90,"#66DFB3",ThemeMetricCatalog.All[9]);
        Add("bar","GPU 占用",40,842,382,34,"#66DFB3",ThemeMetricCatalog.All[6]);
        Add("rect","趋势卡片",20,940,422,280,"#182638");Add("text","趋势标题",40,952,380,46,"#C4D6EB",text:"PERFORMANCE / 趋势");Add("sparkline","占用趋势",40,1020,382,160,"#AF9BFF",ThemeMetricCatalog.All[1]);
        Add("metric","下载速度",26,1260,410,92,"#AFC6E0",ThemeMetricCatalog.All.First(m=>m.Key==SensorKeys.NetworkDown));
        Add("clock","时钟",26,1440,410,130,"#FFFFFF",text:"HH:mm:ss");
        editor.Document.Update(m=>m with{Name="自定义监测面板",BackgroundColor="#0C1522",Layers=layers});
        ((TextBox)editor.FindName("ThemeName")).Text=editor.Document.Manifest.Name;
    }
    private static void Invoke(ThemeEditorView view,string name)=>typeof(ThemeEditorView).GetMethod(name,BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(view,[view,new RoutedEventArgs()]);
    private static void Check(bool value,string name){if(!value)throw new Exception(name);_passed++;}
    private static async Task ReadTelemetryAsync(ThemeEditorView editor,CancellationToken token)
    {
        try
        {
            await using var pipe=new NamedPipeClientStream(".","Seein.Service.v1",PipeDirection.InOut,PipeOptions.Asynchronous);
            await pipe.ConnectAsync(5000,token);using var writer=new StreamWriter(pipe,new System.Text.UTF8Encoding(false),4096,true){AutoFlush=true};using var reader=new StreamReader(pipe,System.Text.Encoding.UTF8,true,4096,true);
            // The isolated preview can only request/read telemetry; no apply or settings commands.
            await writer.WriteLineAsync(JsonSerializer.Serialize(PipeEnvelope.Create("hello",new{client="theme-editor-readonly-preview"}),JsonDefaults.Options));
            while(!token.IsCancellationRequested)
            {
                var line=await reader.ReadLineAsync(token);if(line is null)return;if(line.Length>1024*1024)continue;
                var message=JsonSerializer.Deserialize<PipeEnvelope>(line,JsonDefaults.Options);if(message is null)continue;
                if(message.Type=="sensorSnapshot"){var data=message.Read<SensorSnapshot>();await editor.Dispatcher.InvokeAsync(()=>editor.UpdateTelemetry(sensors:data));}
                else if(message.Type=="powerSnapshot"){var data=message.Read<PowerSnapshot>();await editor.Dispatcher.InvokeAsync(()=>editor.UpdateTelemetry(power:data));}
            }
        }
        catch(Exception ex) when(ex is IOException or OperationCanceledException or TimeoutException or JsonException){}
    }
    private static string FindRoot(){var d=new DirectoryInfo(AppContext.BaseDirectory);while(d is not null&&!File.Exists(Path.Combine(d.FullName,"Seein.sln")))d=d.Parent;return d?.FullName??throw new Exception("Repository missing");}
}
