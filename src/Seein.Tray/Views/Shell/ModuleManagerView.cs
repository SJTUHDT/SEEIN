using Seein.Core;
using Seein.Modularity;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Button = System.Windows.Controls.Button;
namespace Seein.Tray;
public sealed class ModuleManagerView : System.Windows.Controls.UserControl
{
    private readonly StackPanel _body = new();
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Margin = new(0, 8, 0, 14) };
    private readonly IDesktopHost? _host;
    private readonly Dictionary<string,TextBlock> _runtime=[];
    public ModuleManagerView(IDesktopHost? host=null) { _host=host;if(_host is not null)_host.Message+=OnMessage;
        IsVisibleChanged+=async(_,_)=>{if(!IsVisible||_host is null)return;Refresh();try{await _host.Pipe.SendAsync("moduleHealthRequest",new ModuleHealthRequest("manager"));}catch{}};
        Content = new ScrollViewer { Content = _body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }; Refresh(); }
    private void Refresh()
    {
        _body.Children.Clear();_runtime.Clear(); _body.Children.Add(new TextBlock { Text = "插件更改将在安全重启 SEEIN 后生效。卸载保留个人设置和历史数据。", TextWrapping = TextWrapping.Wrap });
        var import = new Button { Content = "导入本地插件包", HorizontalAlignment = System.Windows.HorizontalAlignment.Left, Margin = new(0, 14, 0, 8) };
        import.Click += (_, _) => { var dialog = new Microsoft.Win32.OpenFileDialog { Filter = "SEEIN 插件 (*.seeinplugin)|*.seeinplugin", CheckFileExists = true }; if (dialog.ShowDialog(Window.GetWindow(this)) == true) Apply(new("import", Package: dialog.FileName)); };
        _body.Children.Add(import); _body.Children.Add(_status);
        var root = Directory.GetParent(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar))!.FullName;
        try
        {
            var modules = new ModuleCatalog(root, Path.Combine(AppPaths.SystemDefault.DataRoot, "modules.json")).Read();
            if (modules.Count == 0) _body.Children.Add(new TextBlock { Text = "尚未安装可选插件。本体的仪表盘和耗能日历可独立使用。", TextWrapping = TextWrapping.Wrap });
            foreach (var module in modules)
            {
                var row = new StackPanel { Margin = new(0, 0, 0, 16) }; row.Children.Add(new TextBlock { Text = module.Manifest.Name + " · " + module.Manifest.Version, FontSize = 18, FontWeight = FontWeights.SemiBold });
                var status=new TextBlock{Text=module.Error??(module.Enabled?"已启用":"已停用"),TextWrapping=TextWrapping.Wrap,Margin=new(0,4,0,8)};_runtime[module.Manifest.Id]=status;row.Children.Add(status);
                var actions = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal }; var toggle = new Button { Content = module.Enabled ? "停用并重启" : "启用并重启", Margin = new(0, 0, 10, 0) };
                toggle.Click += (_, _) => Apply(new(module.Enabled ? "disable" : "enable", module.Manifest.Id)); actions.Children.Add(toggle);
                var remove = new Button { Content = "卸载并重启" }; remove.Click += (_, _) => Apply(new("uninstall", module.Manifest.Id)); actions.Children.Add(remove); row.Children.Add(actions); _body.Children.Add(row);
            }
        }
        catch (Exception ex) { _status.Text = ex.Message; }
    }
    private void OnMessage(PipeEnvelope message)
    {
        if(message.Type!="moduleHealthResult")return;
        var health=message.Read<ModuleHealthResult>();if(health is null)return;
        foreach(var module in health.Modules)if(_runtime.TryGetValue(module.Id,out var status))status.Text=module.Ready?"已启用 · 后台就绪":module.Error is not null?"后台启动失败："+module.Error:health.Starting?"后台启动中…":"后台未就绪";
    }
    private void Apply(ModuleChange change)
    {
        try
        {
            var incoming = Path.Combine(AppPaths.UserDefault.DataRoot, "incoming"); Directory.CreateDirectory(incoming); var request = Path.Combine(incoming, "module-" + Guid.NewGuid().ToString("N") + ".json");
            File.WriteAllText(request, JsonSerializer.Serialize(change, ModulePackage.Json));
            Process.Start(new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = true, Verb = "runas", Arguments = "--module-maintenance=\"" + request + "\"" });
            _status.Text = "正在验证插件并等待安全重启；如有录屏，请先结束并保存。";
        }
        catch (Exception ex) { _status.Text = ex.Message; }
    }
}

