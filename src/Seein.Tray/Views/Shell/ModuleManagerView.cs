using Seein.Core;
using Seein.Modularity;
using Seein.Contracts;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Button=System.Windows.Controls.Button;
using CheckBox=System.Windows.Controls.CheckBox;
using ProgressBar=System.Windows.Controls.ProgressBar;
namespace Seein.Tray;
public sealed class ModuleManagerView:System.Windows.Controls.UserControl
{
    private readonly StackPanel _body=new(),_groups=new();
    private readonly TextBlock _status=new(){TextWrapping=TextWrapping.Wrap,Margin=new(0,10,0,10)},_summary=new(){TextWrapping=TextWrapping.Wrap,Margin=new(0,12,0,12)};
    private readonly ProgressBar _progress=new(){Height=6,Minimum=0,Maximum=100,Visibility=Visibility.Collapsed};
    private readonly Button _apply=new(){Content="下载安装并重启",Margin=new(0,0,8,0)},_cancel=new(){Content="取消下载",Visibility=Visibility.Collapsed},_refresh=new(){Content="刷新插件目录",Margin=new(0,0,8,0)},_import=new(){Content="导入本地插件包"};
    private readonly string _root=Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,".."));
    private readonly Dictionary<string,bool> _toggles=[];
    private readonly Dictionary<string,(TextBlock Text,string Prefix)> _runtime=[];
    private readonly IDesktopHost? _host;
    private HashSet<string> _selected=[];private IReadOnlyList<InstalledModule> _installed=[];private PluginFeed? _feed;
    private bool _loaded,_busy;private CancellationTokenSource? _download;
    public ModuleManagerView(IDesktopHost? host=null,PluginFeed? previewFeed=null)
    {
        _host=host;if(host is not null)host.Message+=OnMessage;
        SetResourceReference(ForegroundProperty,"TextBrush");SetResourceReference(FontFamilyProperty,"UiFontFamily");
        var intro=new TextBlock{Text="勾选需要的功能，自动下载并安装。共享组件自动处理，变更统一重启一次；个人设置和历史保留。",TextWrapping=TextWrapping.Wrap};
        var actions=new StackPanel{Orientation=System.Windows.Controls.Orientation.Horizontal,Margin=new(0,12,0,0)};actions.Children.Add(_refresh);actions.Children.Add(_import);
        var bottom=new StackPanel{Orientation=System.Windows.Controls.Orientation.Horizontal};_apply.SetResourceReference(StyleProperty,"AccentButtonStyle");bottom.Children.Add(_apply);bottom.Children.Add(_cancel);
        foreach(var child in new UIElement[]{intro,actions,_status})_body.Children.Add(child);
        var layout=new Grid();layout.RowDefinitions.Add(new(){Height=GridLength.Auto});layout.RowDefinitions.Add(new(){Height=new GridLength(1,GridUnitType.Star)});layout.RowDefinitions.Add(new(){Height=GridLength.Auto});
        layout.Children.Add(_body);var list=new ScrollViewer{Content=_groups,VerticalScrollBarVisibility=ScrollBarVisibility.Auto};Grid.SetRow(list,1);layout.Children.Add(list);
        var footer=new StackPanel();foreach(var child in new UIElement[]{_summary,_progress,bottom})footer.Children.Add(child);Grid.SetRow(footer,2);layout.Children.Add(footer);Content=layout;
        _apply.Click+=async(_,_)=>await DownloadAndApplyAsync();_cancel.Click+=(_,_)=>_download?.Cancel();_refresh.Click+=async(_,_)=>await LoadAsync();
        _import.Click+=(_,_)=>{var dialog=new Microsoft.Win32.OpenFileDialog{Filter="SEEIN 插件 (*.seeinplugin)|*.seeinplugin",CheckFileExists=true};if(dialog.ShowDialog(Window.GetWindow(this))==true)Apply([new("import",Package:dialog.FileName)]);};
        if(previewFeed is not null){_feed=previewFeed;_loaded=true;Render();}else IsVisibleChanged+=async(_,_)=>{if(IsVisible&&!_loaded){_loaded=true;await LoadAsync();}};
        UpdateSummary();
    }
    private PluginFeedClient Client()=>new(Path.Combine(AppPaths.UserDefault.DataRoot,"PluginDownloads"),File.ReadAllText(Path.Combine(_root,"App","module-publisher.pem")));
    private async Task LoadAsync()
    {
        if(_busy)return;_busy=true;Controls();
        try
        {
            _installed=await Task.Run(()=>new ModuleCatalog(_root,Path.Combine(AppPaths.SystemDefault.DataRoot,"modules.json")).Read());using var client=Client();var cached=client.Cached(Path.Combine(_root,"App"));
            if(cached is not null){_feed=cached;_selected=PluginInstallPlan.InitialSelection(cached,_installed);Render();}_status.Text="正在连接官方仓库…";
            using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(25));_feed=await client.RefreshAsync(deadline.Token);_selected=PluginInstallPlan.InitialSelection(_feed,_installed);_toggles.Clear();Render();
            _status.Text=_installed.Any(m=>PluginFeed.Legacy.ContainsKey(m.Manifest.Id))?"已为旧整包选择对应子功能；点击下方按钮迁移，原启停状态和数据保留。":"目录已更新。取消勾选已安装功能表示卸载；右侧可单独停用。";
        }
        catch(Exception ex)when(ex is not OutOfMemoryException){_status.Text="在线目录暂不可用："+ex.Message+"。已安装功能继续可用，可稍后刷新或导入本地插件包。";if(_feed is null)RenderOffline();}
        finally{_busy=false;Controls();UpdateSummary();}
        if(_host is not null)try{await _host.Pipe.SendAsync("moduleHealthRequest",new ModuleHealthRequest("plugin-center"));}catch(IOException){}catch(InvalidOperationException){}
    }
    private void RenderOffline()
    {
        _groups.Children.Clear();foreach(var m in _installed)
        {
            var row=new StackPanel{Margin=new(0,8,0,12)};row.Children.Add(new TextBlock{Text=m.Manifest.Name+" · "+m.Manifest.Version+" · "+(m.Error??(m.Enabled?"已启用":"已停用"))});
            var buttons=new StackPanel{Orientation=System.Windows.Controls.Orientation.Horizontal};foreach(var op in new[]{m.Enabled?"disable":"enable","uninstall"}){var button=new Button{Content=op=="uninstall"?"卸载并重启":m.Enabled?"停用并重启":"启用并重启",Margin=new(0,8,8,0)};button.Click+=(_,_)=>Apply([new(op,m.Manifest.Id)]);buttons.Children.Add(button);}row.Children.Add(buttons);_groups.Children.Add(row);
        }
    }
    private void Render()
    {
        if(_feed is null)return;_groups.Children.Clear();_runtime.Clear();
        foreach(var group in _feed.Groups)
        {
            var packages=_feed.Packages.Where(p=>p.Selectable&&p.Group==group.Id).ToArray();if(packages.Length==0)continue;
            var panel=new StackPanel{Margin=new(16,10,12,10)};var parent=new CheckBox{Content=group.Name,IsThreeState=true,FontSize=18,FontWeight=FontWeights.SemiBold,Margin=new(0,4,0,6)};
            void State(){var count=packages.Count(p=>_selected.Contains(p.Id));parent.IsChecked=count==0?false:count==packages.Length?true:null;}State();var children=new List<CheckBox>();bool changing=false;
            foreach(var p in packages)
            {
                var local=_installed.FirstOrDefault(m=>m.Manifest.Id==p.Id);var line=new DockPanel{Margin=new(0,5,0,5)};
                if(local is not null){var toggle=new Button{Content=local.Enabled?"停用":"启用",Margin=new(14,0,0,0),MinWidth=56};DockPanel.SetDock(toggle,Dock.Right);line.Children.Add(toggle);toggle.Click+=(_,_)=>{var enabled=!(_toggles.TryGetValue(p.Id,out var desired)?desired:local.Enabled);_toggles[p.Id]=enabled;toggle.Content=enabled?"停用":"启用";UpdateSummary();};}
                var text=new StackPanel();text.Children.Add(new TextBlock{Text=p.Name+"  ·  "+p.Version+"  ·  "+(local is null?"未安装":local.Error is not null?"需要修复":local.Enabled?"已启用":"已停用"),FontWeight=FontWeights.SemiBold});text.Children.Add(new TextBlock{Text=p.Description+"\n下载 "+Size(p.DownloadBytes)+" · 解压 "+Size(p.InstalledBytes),TextWrapping=TextWrapping.Wrap,Margin=new(0,4,0,0),FontSize=12});
                var check=new CheckBox{Content=text,IsChecked=_selected.Contains(p.Id),VerticalContentAlignment=VerticalAlignment.Top};children.Add(check);line.Children.Add(check);panel.Children.Add(line);
                if(local is not null&&local.Enabled&&local.Manifest.Service is not null)_runtime[p.Id]=((TextBlock)text.Children[0],p.Name+" · "+p.Version+" · ");
                void Changed(){if(changing)return;if(check.IsChecked==true)_selected.Add(p.Id);else _selected.Remove(p.Id);State();UpdateSummary();}check.Checked+=(_,_)=>Changed();check.Unchecked+=(_,_)=>Changed();
            }
            parent.Click+=(_,_)=>{changing=true;var select=!packages.All(p=>_selected.Contains(p.Id));foreach(var p in packages)if(select)_selected.Add(p.Id);else _selected.Remove(p.Id);foreach(var check in children)check.IsChecked=select;changing=false;State();UpdateSummary();};
            var section=new Expander{Header=parent,Content=panel,IsExpanded=true,Margin=new(0,6,0,8)};section.SetResourceReference(StyleProperty,"PluginGroupExpanderStyle");_groups.Children.Add(section);
        }
        UpdateSummary();
    }
    private static string Size(long bytes)=>bytes>=1024*1024?$"{bytes/(1024d*1024):0.#} MiB":$"{bytes/1024d:0.#} KiB";
    private void OnMessage(PipeEnvelope message)
    {
        if(message.Type!="moduleHealthResult"||message.Read<ModuleHealthResult>() is not {} health)return;
        foreach(var module in health.Modules)if(_runtime.TryGetValue(module.Id,out var row))row.Text.Text=row.Prefix+(module.Ready?"后台就绪":module.Error is not null?"后台失败："+module.Error:health.Starting?"后台启动中…":"后台未就绪");
    }
    private void UpdateSummary()
    {
        if(_feed is null){_apply.IsEnabled=false;return;}var plan=PluginInstallPlan.Create(_feed,_installed,_selected,_toggles);var dependencies=plan.Required.Where(p=>!p.Selectable).Select(p=>p.Name).ToArray();
        _summary.Text=$"已选择 {_selected.Count} 项 · 待下载 {plan.Downloads.Count} 包，共 {Size(plan.Downloads.Sum(p=>p.DownloadBytes))} · 新增文件 {Size(plan.Downloads.Sum(p=>p.InstalledBytes))} · 卸载 {plan.Changes.Count(c=>c.Operation=="uninstall")} 项"+(dependencies.Length==0?"":"\n自动处理共享组件："+string.Join("、",dependencies));_apply.IsEnabled=!_busy&&plan.Changes.Count>0;
    }
    private void Controls(){_refresh.IsEnabled=_import.IsEnabled=_groups.IsEnabled=!_busy;_apply.IsEnabled=!_busy;}
    private async Task DownloadAndApplyAsync()
    {
        if(_busy||_feed is null)return;var plan=PluginInstallPlan.Create(_feed,_installed,_selected,_toggles);if(plan.Changes.Count==0)return;_busy=true;Controls();_download=new();_cancel.Visibility=_progress.Visibility=Visibility.Visible;_progress.Value=0;
        try
        {
            using var client=Client();var files=new Dictionary<string,string>();var counts=new Dictionary<string,long>();var total=plan.Downloads.Sum(p=>p.DownloadBytes);
            var progress=new Progress<PluginProgress>(p=>{counts[p.Id]=p.Received;_progress.Value=total==0?100:100d*counts.Values.Sum()/total;_status.Text=$"正在下载 {p.Id} · {Size(counts.Values.Sum())} / {Size(total)}";});using var gate=new SemaphoreSlim(2);
            await Task.WhenAll(plan.Downloads.Select(async p=>{await gate.WaitAsync(_download.Token);try{var file=await client.DownloadAsync(_feed,p,progress,_download.Token);lock(files)files[p.Id]=file;}finally{gate.Release();}}));_download.Token.ThrowIfCancellationRequested();Apply(plan.Changes.Select(c=>c.Operation=="import"?c with{Package=files[c.Id!]}:c).ToArray());
        }
        catch(OperationCanceledException){_status.Text="已取消。完整且校验通过的下载已缓存，安装未修改。";}
        catch(Exception ex)when(ex is not OutOfMemoryException){_status.Text="安装尚未提交："+ex.Message;}
        finally{_download.Dispose();_download=null;_busy=false;_cancel.Visibility=_progress.Visibility=Visibility.Collapsed;Controls();UpdateSummary();}
    }
    private async void Apply(IReadOnlyList<ModuleChange> changes)
    {
        try
        {
            var incoming=Path.Combine(AppPaths.UserDefault.DataRoot,"incoming");Directory.CreateDirectory(incoming);var request=Path.Combine(incoming,"modules-"+Guid.NewGuid().ToString("N")+".json");File.WriteAllText(request,JsonSerializer.Serialize(new ModuleChangeSet(changes),ModulePackage.Json));
            using var process=Process.Start(new ProcessStartInfo(Environment.ProcessPath!){UseShellExecute=true,Verb="runas",Arguments="--module-maintenance=\""+request+"\""});_status.Text="等待系统授权与安全重启。录制/导出时请先结束并保存；失败会恢复原状态。";
            if(process is not null){await process.WaitForExitAsync();if(File.Exists(request+".result")){using var result=JsonDocument.Parse(File.ReadAllText(request+".result"));_status.Text=result.RootElement.GetProperty("message").GetString();File.Delete(request+".result");File.Delete(request);}}
        }
        catch(Exception ex){_status.Text="安装未完成："+ex.Message;}
    }
}
