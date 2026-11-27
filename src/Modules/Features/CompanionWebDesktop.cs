using Seein.Contracts;
using Seein.Core;
namespace Seein.Tray;
public sealed class CompanionWebDesktop:DesktopModule
{
#if WORKBENCH
    private const string Route="workbench",Title="大肥鱼工作台";
#elif ASTRBOT
    private const string Route="astrbot",Title="AstrBot 后台";
#else
    private const string Route="qq",Title="QQ 后台";
#endif
    private CompanionWebView? _view;
    private CompanionWebView View
    {
        get
        {
            if(_view is not null)return _view;
            _view=new();_view.OpenToolsSection(Route);
            _view.NavigationRequested+=page=>
            {
                var target=page==DafeiyuPage.AstrBot?"astrbot":"qq";
                var root=System.IO.Path.GetFullPath(System.IO.Path.Combine(AppContext.BaseDirectory,".."));
                var installed=new Seein.Modularity.ModuleCatalog(root,System.IO.Path.Combine(AppPaths.SystemDefault.DataRoot,"modules.json")).Read();
                if(installed.Any(m=>m.Enabled&&m.Error is null&&m.Manifest.Id=="companion-"+target))Host.Navigate("tool-"+target);
                else Host.Notify(Title,"请在应用设置 → 插件管理安装对应后台页面。");
            };
            return _view;
        }
    }
    public override IEnumerable<ModulePage> Pages=>[Page("tool-"+Route,Title,"小工具",()=>View)];
    public override Task StopAsync(CancellationToken token){_view?.StopOwnedWorkbench();return Task.CompletedTask;}
    public override void Dispose(){_view?.StopOwnedWorkbench();base.Dispose();}
}
