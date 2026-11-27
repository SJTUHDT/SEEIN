using Seein.Core;
using Seein.Core.Ipc;
using Seein.Contracts;
using System.Windows;
#if FAN
using Seein.Core.Thermal;
#endif
#if RGB
using Seein.Core.Lighting;
#endif
namespace Seein.Tray;

public sealed class DeviceFeatureDesktop:DesktopModule
{
#if MONITOR
    private readonly DisplayShortcutController _shortcut=new();private DisplayControlView? _view;
    private DisplayControlView View=>_view??=new(_shortcut);
    public override IEnumerable<ModulePage> Pages=>[Page("display","显示器设置","设备控制",()=>View)];
    protected override void Initialize(){_shortcut.Initialize(Host.Handle);Host.AddHotkeyHandler(id=>{if(!_shortcut.Matches(id))return false;if(_view is not null)return _view.HandleShortcut(id);_=Restore();return true;});Host.AddShortcutProvider("monitor",new(()=>[new("display.dp1","切回 DP1","显示器",new("display.dp1",_shortcut.Settings.Modifiers,_shortcut.Settings.Key,_shortcut.Settings.Enabled))],b=>{if(_view is not null)return _view.SaveManagedShortcut(b);var saved=b.Empty?_shortcut.Save(_shortcut.Settings with{Enabled=false}):_shortcut.Save(new(_shortcut.Settings.TargetId,b.Modifiers,b.Key,b.Enabled));return(saved,_shortcut.Status);}));}
    private bool _restoring;
    private async Task Restore(){if(_restoring)return;_restoring=true;try{var result=await _shortcut.RestoreDp1Async();Host.Notify("显示器",result.Success?"已切回 DP1":result.Error??"切换失败",!result.Success);}finally{_restoring=false;}}
    public override void Dispose(){_view?.Dispose();_shortcut.Dispose();base.Dispose();}
#elif LAMP
    private readonly LampShortcutController _shortcut=new(new Seein.SmartHome.SmartHomeStore(AppPaths.UserDefault.DataRoot));private XiaomiLampView? _view;
    private XiaomiLampView View=>_view??=new(_shortcut);
    public override IEnumerable<ModulePage> Pages=>[Page("lamp","米家挂灯","设备控制",()=>View)];
    protected override void Initialize(){_shortcut.Initialize(Host.Handle);Host.AddHotkeyHandler(id=>{if(!_shortcut.Matches(id))return false;_shortcut.Execute(id);return true;});_shortcut.ActionCompleted+=(text,error)=>Host.Notify("米家挂灯",text,error);Host.AddShortcutProvider("lamp",new(()=>View.ManagedShortcuts,b=>View.SaveManagedShortcut(b)));}
    public override void Dispose(){_view?.Dispose();_shortcut.Dispose();base.Dispose();}
#elif ROUTER
    private RouterControlView? _view;
    public override IEnumerable<ModulePage> Pages=>[Page("router","中兴路由器","设备控制",()=>_view??=new())];
    public override void Dispose(){_view?.Dispose();base.Dispose();}
#elif PLAYBACK
    private CapturePlaybackView? _view;
    public override IEnumerable<ModulePage> Pages=>[Page("input-playback","采集播放","设备控制",()=>_view??=new())];
    public override Task PrepareStopAsync(CancellationToken token)=>_view?.StopPlaybackAsync()??Task.CompletedTask;
    public override Task StopAsync(CancellationToken token)=>_view?.StopPlaybackAsync()??Task.CompletedTask;
    public override void Dispose(){_view?.Dispose();base.Dispose();}
#elif FAN
    private FanControlView? _view;private readonly LatestRequestTracker _request=new(),_command=new();
    private FanControlView View {get{if(_view is not null)return _view;_view=new();_view.StateRequested+=()=>_=Request();_view.ConfigRequested+=(settings,command,gpu)=>_=Send(settings,command,gpu);_view.RestoreRequested+=()=>_=Restore();return _view;}}
    public override IEnumerable<ModulePage> Pages=>[Page("fan","风扇控制","设备控制",()=>View,()=>_=Request())];
    private async Task Request(){try{await _pipe.SendAsync(PipeMessageType.FanStateRequest,new FanStateRequest(_request.Begin()),_shutdown.Token);}catch{StatusText.Text="后台连接失败，草稿已保留。";}}
    private async Task Send(FanSettings settings,string? command,GpuFanCommand? gpu){try{_request.Begin();await _pipe.SendAsync(PipeMessageType.FanConfigRequest,new FanConfigRequest(settings,command,_command.Begin(),gpu),_shutdown.Token);}catch{View.ShowTransportError("后台连接失败，草稿已保留。");}}
    private async Task Restore(){try{await _pipe.SendAsync(PipeMessageType.FanRestoreRequest,new FanRestoreRequest(_command.Begin()),_shutdown.Token);}catch{View.ShowTransportError("交还控制失败。");}}
    protected override void HandleMessage(PipeEnvelope m){if(m.Type==PipeMessageType.FanStateResult&&m.Read<FanStateResult>() is {} s&&_request.IsCurrent(s.RequestId))View.Load(s);if(m.Type==PipeMessageType.FanConfigResult&&m.Read<FanConfigResult>() is {} c&&_command.IsCurrent(c.RequestId))View.ShowResult(c.Status,c.Settings,c.Success,c.Error);if(m.Type==PipeMessageType.FanRestoreResult&&m.Read<FanRestoreResult>() is {} r&&_command.IsCurrent(r.RequestId))View.ShowResult(r.Status,r.Settings,r.Success,r.Error);}
    protected override void RefreshCurrentPageData(){if(_page=="fan")_=Request();}
#elif RGB
    private RgbControlView? _view;private readonly LatestRequestTracker _request=new();
    private RgbControlView View {get{if(_view is not null)return _view;_view=new();_view.StateRequested+=()=>_=Request();_view.CommandRequested+=r=>_=Send(r);return _view;}}
    public override IEnumerable<ModulePage> Pages=>[Page("rgb","灯效控制","设备控制",()=>View,()=>_=Request())];
    private async Task Request(){try{await _pipe.SendAsync(PipeMessageType.RgbDevicesRequest,new RgbDevicesRequest(_request.Begin()),_shutdown.Token);}catch{StatusText.Text="灯效后台连接失败。";}}
    private async Task Send(RgbCommandRequest request){try{await _pipe.SendAsync(PipeMessageType.RgbCommandRequest,request with{RequestId=_request.Begin()},_shutdown.Token);}catch{View.ShowResult(new RgbCommandResult(false,Error:"灯效命令发送失败。"));}}
    protected override void HandleMessage(PipeEnvelope m){if(m.Type==PipeMessageType.RgbDevicesResult&&m.Read<RgbDevicesResult>() is {} s&&_request.IsCurrent(s.RequestId))View.Load(s);if(m.Type==PipeMessageType.RgbCommandResult&&m.Read<RgbCommandResult>() is {} r&&_request.IsCurrent(r.RequestId)){if(r.Status is not null&&r.Settings is not null)View.Load(new(r.Status,r.Settings,r.RequestId));View.ShowResult(r);}}
    protected override void RefreshCurrentPageData(){_view?.ResetPendingCommand();if(_page=="rgb")_=Request();}
#endif
}
